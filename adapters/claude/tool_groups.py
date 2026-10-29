"""Tool groups: the model sees the groups this user works with, not all of the tools.

    base         always on: documents, sketches, features, analyze_model, pictures, batches, help
    assemblies   on by default
    sheet_metal, simulation, drawings, weldments     on when used in the last SWMCP_USAGE_DAYS days

SWMCP_TOOLS: auto (default) = the default groups + every group used in the last SWMCP_USAGE_DAYS days (read
from the usage log — the personal part), all, or a comma list of groups. A tool of a group that is off is still
found by tool_help and RUNS through execute_batch, which turns its group on (turn_on_for) — that path needs nothing
from the client. enable_tools turns groups on too and tells the client its tool list changed, but a client may not
re-read the list mid-conversation (the Claude desktop app did not, 2026-10): the tools then appear next session.

The usage log (usage.jsonl next to adapter.log): one JSON line per tool call — time, tool, group, ms, ok — and per
enabled group; never the arguments. tools/usage_report.py summarises it."""
import json
import os
import threading
import time

import config

GROUPS = {
    "base": "documents, import/export, sketches, features, analyze_model, compare_parts, dimension_for_face, mass, "
            "pictures, batches, help (always on)",
    "assemblies": "components, mates, interference / clearance, BOM, custom properties, configurations, pack and go",
    "sheet_metal": "sheet-metal features, flat DXF, cut list, DFM check",
    "simulation": "SOLIDWORKS Simulation studies, results, maps, report, CalculiX cross-check, bolt / weld / beam checks",
    "drawings": "drawings: views, sections, dimensions, hole callouts, BOM table; reading a drawing (analyze_drawing)",
    "weldments": "weldment profiles, members, trims, end caps, gussets, weldment cut list",
}
DEFAULT_ON = {"base", "assemblies"}
MODULE_GROUP = {"tools_assembly": "assemblies", "tools_sheet_metal": "sheet_metal", "tools_simulation": "simulation",
                "tools_drawing": "drawings", "tools_weldments": "weldments"}
TOOL_GROUP = {"compare_assemblies": "assemblies"}

GROUP_OF: dict = {}      # tool name -> group (filled by registry.register_all)
PARKED: dict = {}        # tool name -> Tool object of a group that is off (put back by enable)
USAGE_PATH = config.USAGE_LOG
_lock = threading.Lock()


def assign(names, module_name: str) -> None:
    for n in names:
        GROUP_OF[n] = TOOL_GROUP.get(n) or MODULE_GROUP.get(module_name, "base")


def wanted(setting: str = None, now: float = None) -> set:
    """The groups to start with: 'all', 'auto' (default groups + recently used ones) or a comma list."""
    setting = (setting if setting is not None else config.TOOL_GROUPS).strip().lower()
    if setting == "all":
        return set(GROUPS)
    if setting in ("", "auto"):
        return DEFAULT_ON | recent_groups(config.USAGE_DAYS, now)
    named = {g.strip() for g in setting.split(",") if g.strip()}
    return {"base"} | (named & set(GROUPS))


def park(mcp, on: set) -> list:
    """Take the tools of the groups that are off out of the tool list (kept, with every rule already applied)."""
    for t in list(mcp._tool_manager.list_tools()):
        if GROUP_OF.get(t.name, "base") not in on:
            PARKED[t.name] = t
            mcp._tool_manager.remove_tool(t.name)
    return sorted({GROUP_OF[n] for n in PARKED})


def enable(mcp, groups) -> list:
    """Put the parked tools of these groups back; returns their names."""
    added = [n for n in PARKED if GROUP_OF[n] in groups]
    for n in added:
        mcp._tool_manager._tools[n] = PARKED.pop(n)    # the SDK has no public "add an existing Tool"
    return sorted(added)


def turn_on_for(mcp, names) -> list:
    """Turn on the groups of the parked tools among `names` (execute_batch runs a tool of a group that is off) and
    log it, so the group starts on in the next sessions too; returns those groups."""
    groups = sorted({GROUP_OF[n] for n in names if n in PARKED})
    if groups:
        enable(mcp, set(groups))
        record_enabled(groups)
    return groups


def off_groups() -> list:
    return sorted({GROUP_OF[n] for n in PARKED})


# ---------------------------------------------------------------- usage log
def _write(entry: dict) -> None:
    if not USAGE_PATH:
        return
    try:
        with _lock:
            if os.path.exists(USAGE_PATH) and os.path.getsize(USAGE_PATH) > 5 * 1024 * 1024:
                os.replace(USAGE_PATH, USAGE_PATH + ".1")
            with open(USAGE_PATH, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except OSError:
        pass   # the log must never break a tool call


def record(tool: str, ms: float, ok: bool) -> None:
    _write({"t": round(time.time()), "tool": tool, "group": GROUP_OF.get(tool, "base"), "ms": round(ms), "ok": ok})


def record_enabled(groups) -> None:
    for g in groups:
        _write({"t": round(time.time()), "enable": g})


def read_usage(path: str = None) -> list:
    path = path or USAGE_PATH
    rows = []
    for p in (path + ".1", path) if path else ():
        try:
            with open(p, encoding="utf-8") as fh:
                for line in fh:
                    try:
                        rows.append(json.loads(line))
                    except ValueError:
                        pass
        except OSError:
            pass
    return rows


def recent_groups(days: int, now: float = None) -> set:
    """Groups used (a tool call or enable_tools) within the last `days` days."""
    since = (now or time.time()) - days * 86400
    return {r.get("group") or r.get("enable") for r in read_usage() if r.get("t", 0) >= since} & set(GROUPS)


# ---------------------------------------------------------------- the tool
def register(mcp):
    from mcp.server.mcpserver import Context
    from response_mapper import fail

    @mcp.tool(structured_output=False)
    async def enable_tools(groups: list[str], ctx: Context) -> str:
        """Turn on tool groups that are off (they stay on in the next sessions while you use them). Their tools
        appear in your tool list once your client re-reads it — some clients only in the next session; a tool of a
        group that is off always runs through execute_batch(steps=[{"tool": …, "args": …}]), which turns its group
        on by itself. Groups: sheet_metal, simulation, drawings (views, sections, dimensions, BOM table, reading a
        drawing), weldments (profiles, members, cut list), assemblies. tool_help('<tool>') says which group a tool is
        in and gives its parameters."""
        unknown = [g for g in groups if g not in GROUPS]
        if unknown:
            raise fail("BAD_PARAMETER", f"unknown group(s) {unknown}; groups: {sorted(GROUPS)}")
        added = enable(mcp, set(groups))
        record_enabled(groups)
        notified = False
        try:      # handshake-era clients (Claude Code today) listen for notifications/tools/list_changed
            await ctx.session.send_tool_list_changed()
            notified = True
        except Exception:   # noqa: BLE001 — a client without it: the tools are still on after a reconnect
            pass
        try:      # 2026-07-28+ clients subscribe through subscriptions/listen
            await ctx.notify_tools_changed()
            notified = True
        except Exception:   # noqa: BLE001
            pass
        return json.dumps({"enabled": groups, "tools_added": added, "still_off": off_groups(),
                           "client_notified": notified,
                           "note": ("the tools are on; if your tool list does not show them yet, call them "
                                    "through execute_batch — tool_help('<tool>') gives the parameters")
                                   if added else "already on"},
                          ensure_ascii=False, separators=(",", ":"))
