"""
Maps execution/solidworks ExecutionResponse payloads to MCP tool answers — ONE form for every tool:

COMPLETED → one compact JSON object (answer_of): state_version, document, the tool's fields, created / removed
FAILED    → raises CadOperationError (a ToolError: the model reads `CODE | message`)
DUPLICATE → {"duplicate": true, …}

WHY ToolError: the MCP SDK (v2) shows the model only "Error executing tool <name>" for any exception
that is NOT a ToolError -- the real cause (NO_ACTIVE_DOCUMENT, EXTRUSION_FAILED, ...) was invisible, so
a failed verify_state looked like an unexplained crash. CadOperationError is also a RuntimeError, so
existing `except RuntimeError` handlers keep working.
"""
import json

try:
    from mcp.server.mcpserver.exceptions import ToolError as _ToolError
except Exception:  # noqa: BLE001 - tests / tooling without the SDK still import this module
    class _ToolError(Exception):
        pass


class CadOperationError(_ToolError, RuntimeError):
    """A failure the model should READ: the execution layer's code and message, verbatim."""


def fail(code: str, text: str) -> CadOperationError:
    """The one error a tool raises (never returns as text): the model reads `CODE | text`."""
    return CadOperationError(f"{code} | {text}")


def failure_of(response: dict, doing: str = "") -> CadOperationError:
    """A FAILED execution response as the tool's error — the server's own code; `doing` says what the tool was
    doing when it failed ("opening 'gear.SLDPRT'")."""
    err = response.get("error") or {}
    message = str(err.get("message") or "the execution server sent no message")
    return fail(err.get("code") or str(response.get("status")), f"{doing}: {message}" if doing else message)


def execution_layer_error(ex: Exception) -> CadOperationError:
    """The execution server is down / timed out / busy with a background job — one wording on every call path."""
    msg = str(ex)
    return CadOperationError(msg if msg.startswith("BUSY |") else f"EXECUTION_LAYER | {msg}")


def dump(obj) -> str:
    """Compact JSON text — every answer the model reads."""
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def answer_of(response: dict, **extra) -> dict:
    """The ONE successful answer of every tool, as an object:

        state_version              always
        document, sketch           the active document; the open sketch (only while one is open)
        <the tool's own fields>    the server's result_geometry, flattened in (a list / value goes under `result`);
                                   payload strings in cadState.features are parsed in the same way — a JSON object
                                   merged, `key=value` as a field, plain names as `features` (a created feature, a
                                   saved path, the tree)
        created / removed          what the call added to / removed from the tree (mutating calls, read by the server)
        **extra                    what the adapter adds (warning, …)"""
    state = response.get("cadState") or {}
    out = {"state_version": response.get("stateVersion")}
    if state.get("activeDocument"):
        out["document"] = state["activeDocument"]
    if state.get("activeSketch"):
        out["sketch"] = state["activeSketch"]
    names = []
    for item in state.get("features") or []:
        key, sep, value = item.partition("=") if isinstance(item, str) else ("", "", "")
        if isinstance(item, str) and item[:1] == "{":
            try:
                out.update(json.loads(item))
                continue
            except ValueError:
                pass
        elif sep and key.isidentifier():
            try:
                out[key] = json.loads(value)
            except ValueError:
                out[key] = {"True": True, "False": False}.get(value, value)
            continue
        names.append(item)
    if names:
        out["features"] = names
    if state.get("dimensions"):
        out["dimensions"] = state["dimensions"]
    geometry = response.get("result_geometry")
    if isinstance(geometry, dict):
        out.update(geometry)
    elif geometry is not None:
        out["result"] = geometry
    for k in ("created", "removed"):
        if response.get(k):
            out[k] = response[k]
    out.update({k: v for k, v in extra.items() if v is not None})
    return out


def answer(response: dict, **extra) -> str:
    return dump(answer_of(response, **extra))


DOCUMENT_TOOLS = {"tool_help"}   # they return reference documents, not operation answers


def uniform(text: str, state_version) -> str:
    """The guarantee behind the one form, applied to every tool's return (server.py): an answer is ONE JSON object that
    starts with state_version — an object without it gets it, a list goes under `items`, plain text under `text`."""
    try:
        body = json.loads(text)
    except ValueError:
        body = {"text": text}
    if not isinstance(body, dict):
        body = {"items": body}
    elif "state_version" in body:
        return text
    return dump({"state_version": state_version, **body})


def duplicate_text(response: dict) -> str:
    """The one DUPLICATE answer: a replayed operation id is answered, not executed again."""
    return dump({"duplicate": True, "note": "operation already executed — not run again",
                 "state_version": response.get("last_known_state_version")})


def map_response(response: dict) -> str:
    """An ExecutionResponse as the tool's answer text (answer_of). FAILED raises CadOperationError, so the SDK marks
    the call as an error AND the model reads why."""
    status = response.get("status")
    if status == "COMPLETED":
        return answer(response)
    if status == "DUPLICATE":
        return duplicate_text(response)
    if status == "FAILED":
        raise failure_of(response)
    raise fail("INTERNAL", f"unknown execution response status {status!r}")
