"""SOLIDWORKS MCP server (Alchemist for SOLIDWORKS): Claude <-> MCP <-> this adapter <-> HTTP localhost:5000 <-> SolidworksExecution.exe <-> SolidWorks.

This file only assembles the server:
  registry.py      registers every tool family — tools_<family>.py, each with register(mcp, tool)
  bridge.py        calls to the execution server (via session.py)
  jobs.py          background jobs ({"running": true, "job_id": …}), BUSY, execute_batch's $refs
  tool_docs.py     compact tool descriptions + tool_help
  tool_groups.py   tool groups (the ones that are off are parked), enable_tools, the usage log
and applies the rules every tool shares, after registration:
  _normalize_tool_surface         leaner tools/list schemas (no titles, additionalProperties=false)
  _wrap                           every call: slow upstream tools as background jobs, a failure as a readable
                                  `CODE | message` tool error, a success as ONE JSON object, the usage log
  _unknown_parameters_are_errors  a parameter the tool does not have is BAD_PARAMETER, not silently dropped

Every tool is registered with structured_output=False: each returns text (plus an image for the picture tools),
and the SDK's structured mirror would repeat the whole payload. Changes need the MCP server reconnected in Claude.
"""
import inspect

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError

from adapter_log import write as _log
from execution_client import ExecutionLayerError
from response_mapper import DOCUMENT_TOOLS, execution_layer_error, fail, uniform

mcp = MCPServer("solidworks-execution-adapter")


# ---------------------------------------------------------------------------
# Tool-surface normalisation (prompt cost)
# ---------------------------------------------------------------------------
def _normalize_tool_surface() -> None:
    """Undo three SDK artefacts that ride in the tools/list prompt on every turn.

    1. Docstrings keep their source indentation (the SDK does not dedent them).
    2. Every parameter gets an auto-generated `title` that just re-spells its own name.
    3. `additionalProperties` is absent, so the schema does not tell the client that a typo'd
       param name is wrong — put it back (the call itself is checked by
       _unknown_parameters_are_errors, before any REST/COM round-trip).

    `Tool.description` / `Tool.parameters` are plain mutable fields that `list_tools()`
    reads at call time, so one pass after registration is enough. Without this the payload
    is ~9% larger than the FastMCP surface it replaced; with it, ~2% smaller.

    Touches `mcp._tool_manager` (private). The public alternative is the `middleware`
    hook, which the SDK marks provisional — revisit if this breaks on an SDK bump; the
    schema-contract test is what catches it.
    """
    for tool in mcp._tool_manager.list_tools():
        if tool.description:
            tool.description = inspect.cleandoc(tool.description)
        schema = tool.parameters
        if isinstance(schema, dict):
            schema.pop("title", None)
            for prop in (schema.get("properties") or {}).values():
                if isinstance(prop, dict):
                    prop.pop("title", None)
            schema.setdefault("additionalProperties", False)


# every tool family — adapters/claude/tools_*.py, registered by registry.py
import registry  # noqa: E402
registry.register_all(mcp)

# tool_help + compact descriptions (long ones cut at ~500 chars, full text on demand;
# SWMCP_DOCS=full keeps them whole) — adapters/claude/tool_docs.py
import tool_docs  # noqa: E402
tool_docs.register(mcp)

# tool groups: the ones that are off are parked at the end; execute_batch runs a parked tool (turning its group on),
# enable_tools puts groups back mid-conversation and tells the client (tools.listChanged is advertised for that)
# — adapters/claude/tool_groups.py
import tool_groups  # noqa: E402
tool_groups.register(mcp)
_init_options = mcp._lowlevel_server.create_initialization_options


def _init_with_tools_changed(notification_options=None, *args, **kwargs):
    from mcp.server.lowlevel.server import NotificationOptions
    return _init_options(notification_options or NotificationOptions(tools_changed=True), *args, **kwargs)


mcp._lowlevel_server.create_initialization_options = _init_with_tools_changed


def _as_tool_error(tool_name: str, ex: Exception) -> Exception:
    """Any failure as an error the model can READ — the SDK shows only "Error executing tool X" for exceptions that
    are not a ToolError (execution server down / timed out, a crash inside a job)."""
    if isinstance(ex, ToolError):
        return ex
    if isinstance(ex, ExecutionLayerError):
        return execution_layer_error(ex)
    import traceback
    _log(f"{tool_name}: unexpected {type(ex).__name__}: {ex}\n{traceback.format_exc()}")
    return fail("INTERNAL", f"{type(ex).__name__}: {ex} (details in adapter.log)")


# Upstream tools that can run for minutes on real assemblies / large files: like the millimetre tools they answer
# {"running": true, "job_id": …} after SYNC_WAIT (the MCP client cuts a call at ~60 s) and make other calls
# answer BUSY meanwhile.
_LONG_UPSTREAM = {"open_document", "save_document", "export_document", "analyze_model",
                  "analyze_assembly", "compare_parts", "compare_assemblies", "analyze_drawing"}


def _one_form(name: str, res):
    """Every operation answers ONE JSON object that starts with state_version (response_mapper.uniform); a picture
    tool's images ride after it."""
    if name in DOCUMENT_TOOLS:
        return res
    import session
    if isinstance(res, str):
        return uniform(res, session.state_version())
    if isinstance(res, list) and res and isinstance(res[0], str):
        return [uniform(res[0], session.state_version()), *res[1:]]
    return res


def _wrap():
    """One wrapper around every tool, for every call:
    - a slow upstream tool (_LONG_UPSTREAM) runs as a background job (jobs.background);
    - a tool fails by RAISING (response_mapper.fail — never a 'FAILED | …' text, which the client would show as a
      successful call), and every exception reaches the model readable (see _as_tool_error);
    - a success is ONE JSON object (_one_form);
    - the call goes into the usage log (tool, time, ok — no arguments)."""
    import functools
    import time
    import jobs

    for t in mcp._tool_manager.list_tools():
        def wrap(fn, name=t.name, is_async=t.is_async):
            if is_async:
                @functools.wraps(fn)
                async def wrapped_async(*args, **kwargs):
                    start, ok = time.perf_counter(), False
                    try:
                        res = _one_form(name, await fn(*args, **kwargs))
                        ok = True
                        return res
                    except Exception as ex:   # noqa: BLE001 — mapped, never swallowed
                        raise _as_tool_error(name, ex) from ex
                    finally:
                        tool_groups.record(name, (time.perf_counter() - start) * 1000, ok)
                return wrapped_async

            long = name in _LONG_UPSTREAM

            @functools.wraps(fn)
            def wrapped(*args, **kwargs):
                start, ok = time.perf_counter(), False
                try:
                    res = jobs.background(name, lambda: fn(*args, **kwargs)) if long else fn(*args, **kwargs)
                    res = _one_form(name, res)
                    ok = True
                    return res
                except Exception as ex:   # noqa: BLE001 — mapped, never swallowed
                    raise _as_tool_error(name, ex) from ex
                finally:
                    tool_groups.record(name, (time.perf_counter() - start) * 1000, ok)
            return wrapped
        t.fn = wrap(t.fn)


def _unknown_parameters_are_errors():
    """Every schema promises additionalProperties=false, but the SDK's argument model IGNORES extra keys: a guessed
    parameter name (max_items for limit) was dropped silently and the call ran with the default. The one entry point
    of every call — the client's and execute_batch's — rejects it, naming the parameters the tool has."""
    call_tool = mcp.call_tool

    async def strict(name, arguments, context=None):
        tool = mcp._tool_manager.get_tool(name)
        if tool is not None and isinstance(arguments, dict):
            known = set(((tool.parameters or {}).get("properties") or {}))
            extra = sorted(set(arguments) - known)
            if extra:
                raise ToolError(f"BAD_PARAMETER | {name} has no parameter {', '.join(extra)}; "
                                f"its parameters: {', '.join(sorted(known)) or 'none'}")
        return await call_tool(name, arguments, context)
    mcp.call_tool = strict


_normalize_tool_surface()
_wrap()
_unknown_parameters_are_errors()
TOOL_DOCS = tool_docs.compact(mcp)     # every tool, the parked ones too — tool_help finds them
TOOL_DOCS["groups_off"] = tool_groups.park(mcp, tool_groups.wanted())
TOOL_DOCS["tools_on"] = len(mcp._tool_manager.list_tools())
_log(f"adapter start: {TOOL_DOCS}")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    import bridge
    try:
        bridge.cleanup_temp()
    except OSError:
        pass
    mcp.run()
