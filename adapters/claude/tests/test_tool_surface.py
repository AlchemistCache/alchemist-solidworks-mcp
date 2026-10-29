"""The MCP tool surface as a whole: every tool is described, every execution-server tool the adapter calls is
routed in ToolController.cs (regressions found by the 2026-10 code review: four tools shipped with an empty
description, dimension_for_face raised NameError)."""
import asyncio
import os
import re
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO = os.path.dirname(os.path.dirname(_ADAPTER_DIR))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

CONTROLLER = os.path.join(REPO, "execution", "solidworks", "SolidworksExecution", "Controllers", "ToolController.cs")


def _server():
    """The real server module — skipped without the MCP SDK; any other import error FAILS the test."""
    pytest.importorskip("mcp")
    import server
    return server


def test_every_tool_has_a_description():
    server = _server()
    tools = asyncio.run(server.mcp.list_tools())
    empty = [t.name for t in tools if len((t.description or "").strip()) < 20]
    assert not empty, f"tools without a description: {empty}"


def test_called_tools_are_routed():
    routed = _routed()
    called = set()
    import glob
    for name in [os.path.basename(f) for f in glob.glob(os.path.join(_ADAPTER_DIR, "*.py"))]:
        src = open(os.path.join(_ADAPTER_DIR, name), encoding="utf-8").read()
        called |= set(re.findall(r'(?<![\w.])(?:call_mm|call_mm_json|_call_mm_sync|call|call_raw|_call_checked)\(\s*"([a-z0-9_]+)"',
                                 src))
    assert len(called) > 50, f"the call pattern no longer matches the adapter's calls ({len(called)} found)"
    missing = sorted(called - routed)
    assert not missing, f"called by the adapter but not routed in ToolController.cs: {missing}"


def test_tools_fail_by_raising():
    """A failure returned as text ("FAILED | …") reads as a SUCCESSFUL call in the client; tools raise
    response_mapper.fail(code, text) instead — nothing converts such a text any more."""
    import glob
    offenders = []
    for path in glob.glob(os.path.join(_ADAPTER_DIR, "*.py")):
        for n, line in enumerate(open(path, encoding="utf-8"), 1):
            if re.search(r'return\s*\(?\s*f?["\']FAILED\b', line):
                offenders.append(f"{os.path.basename(path)}:{n}")
    assert not offenders, f"tools returning a FAILED text: {offenders}"


def _routed():
    return set(re.findall(r'case\s+"([a-z0-9_]+)"\s*:', open(CONTROLLER, encoding="utf-8").read()))


def test_scenarios_use_routed_tools():
    """The checked-in run_steps / golden scenarios call the execution server directly: every step must be routed."""
    import glob
    import json
    routed = _routed() | {"ready"}
    bad = []
    for path in glob.glob(os.path.join(REPO, "tools", "*.json")):
        for i, step in enumerate(json.load(open(path, encoding="utf-8-sig"))):
            if step and step[0] not in routed:
                bad.append(f"{os.path.basename(path)}[{i}]: {step[0]}")
    assert not bad, f"scenario steps without a route: {bad}"


def test_mcp_scenarios_use_live_tools():
    """tools/mcp/*.json (run_mcp.py) call the MCP tools: every step names a live tool and only its parameters."""
    import glob
    import json
    server = _server()
    tools = {t.name: set(((t.input_schema or {}).get("properties") or {})) for t in asyncio.run(server.mcp.list_tools())}
    bad = []
    for path in glob.glob(os.path.join(REPO, "tools", "mcp", "*.json")):
        for i, step in enumerate(json.load(open(path, encoding="utf-8-sig"))):
            where = f"{os.path.basename(path)}[{i}] {step[0]}"
            if step[0] not in tools:
                bad.append(f"{where}: no such tool")
            elif len(step) > 1 and set(step[1]) - tools[step[0]]:
                bad.append(f"{where}: unknown parameters {sorted(set(step[1]) - tools[step[0]])}")
    assert not bad, bad


def test_tools_md_lists_the_live_tools():
    """docs/TOOLS.md is generated (tools/gen_tools_doc.py) — it must name exactly the tools the server exposes."""
    server = _server()
    live = {t.name for t in asyncio.run(server.mcp.list_tools())}
    documented = set(re.findall(r"^- \*\*`([a-z0-9_]+)`\*\*\(", open(os.path.join(REPO, "docs", "TOOLS.md"),
                                                                     encoding="utf-8").read(), re.M))
    assert documented == live, (f"regenerate docs/TOOLS.md (tools/gen_tools_doc.py): missing {sorted(live - documented)}, "
                                f"stale {sorted(documented - live)}")


_LENGTH = re.compile(r"^(x|y|z|[xyz]\d|p[xyz]|c[xyz]|e[xyz]|v[xy]|[xy]m|depth|radius|distance\d?|length|height|diameter|"
                     r"thickness|bend_radius|flange_length|radius_or_distance|cbore_\w+|csink_diameter|thread_depth|"
                     r"edge_[xy]|face_[xyz]|fixed_[xyz]|pos_[xy]|label_offset_[xy])$")


def test_every_length_is_millimetres():
    """The one units rule: every length is MILLIMETRES — a length parameter without an `_mm` suffix has a tool that
    says mm; no tool speaks of meters; units.py converts exactly the parameters the tools have."""
    import units
    server = _server()
    tools = {t.name: t for t in asyncio.run(server.mcp.list_tools())}
    silent, meters = [], []
    for t in tools.values():
        if re.search(r"\bmeters?\b|\bmetres?\b|\bMETERS\b", t.description or ""):
            meters.append(t.name)
        for name, sch in ((t.input_schema or {}).get("properties") or {}).items():
            types = {sch.get("type")} | {a.get("type") for a in sch.get("anyOf", [])}
            if {"number", "integer"} & types and _LENGTH.match(name):
                text = ((t.description or "") + " " + (sch.get("description") or ""))
                if not re.search(r"\bmm\b|millimet", text, re.I):
                    silent.append(f"{t.name}.{name}")
    assert not silent, f"length parameters whose tool never says mm: {silent}"
    assert not meters, f"tools still speaking of meters: {meters}"
    for tool, keys in units.INPUT_LENGTHS.items():
        props = (tools[tool].input_schema or {}).get("properties") or {}
        assert keys <= set(props), f"units.INPUT_LENGTHS[{tool}] names no parameter: {sorted(keys - set(props))}"
