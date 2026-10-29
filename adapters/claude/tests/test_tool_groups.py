"""Tool groups: which tools a group holds, what 'auto' turns on from the usage log, parking and enable_tools."""
import asyncio
import json
import time

import pytest

pytest.importorskip("mcp")
import server  # noqa: E402
import tool_docs  # noqa: E402
import tool_groups  # noqa: E402


def _names():
    return {t.name for t in server.mcp._tool_manager.list_tools()}


def _text(res):
    items = res if isinstance(res, (list, tuple)) else getattr(res, "content", [res])
    return " ".join(getattr(c, "text", "") for c in items)


@pytest.fixture
def all_back_on():
    yield
    tool_groups.enable(server.mcp, set(tool_groups.GROUPS))


def test_every_tool_has_a_group():
    assert _names() <= set(tool_groups.GROUP_OF) | {"tool_help", "enable_tools"}
    assert set(tool_groups.GROUP_OF.values()) <= set(tool_groups.GROUPS)
    g = tool_groups.GROUP_OF
    assert g["create_drawing"] == g["analyze_drawing"] == "drawings"
    assert g["weldment_member"] == "weldments"
    assert g["compare_parts"] == g["dimension_for_face"] == "base" and g["compare_assemblies"] == "assemblies"
    assert g["analyze_model"] == g["extrude_feature"] == g["view_image"] == g["execute_batch"] == "base"
    assert g["insert_component"] == "assemblies" and g["sim_run"] == "simulation"


def test_wanted_settings(monkeypatch, tmp_path):
    log = tmp_path / "usage.jsonl"
    now = time.time()
    log.write_text("\n".join(json.dumps(r) for r in [
        {"t": now - 3 * 86400, "tool": "weldment_member", "group": "weldments", "ms": 5, "ok": True},
        {"t": now - 90 * 86400, "tool": "create_drawing", "group": "drawings", "ms": 5, "ok": True},
        {"t": now - 90 * 86400, "tool": "sim_run", "group": "simulation", "ms": 5, "ok": True},
        {"t": now - 86400, "enable": "drawings"},
    ]) + "\nnot json\n", encoding="utf-8")
    monkeypatch.setattr(tool_groups, "USAGE_PATH", str(log))
    assert tool_groups.wanted("all") == set(tool_groups.GROUPS)
    assert tool_groups.wanted("drawings, nonsense") == {"base", "drawings"}
    # auto: the default groups + what was used in the last 30 days (a 90-day-old call does not count)
    assert tool_groups.wanted("auto", now) == tool_groups.DEFAULT_ON | {"weldments", "drawings"}
    monkeypatch.setattr(tool_groups, "USAGE_PATH", str(tmp_path / "none.jsonl"))
    assert tool_groups.wanted("auto") == tool_groups.DEFAULT_ON


def test_park_help_and_enable(all_back_on, monkeypatch, tmp_path):
    monkeypatch.setattr(tool_groups, "USAGE_PATH", str(tmp_path / "u.jsonl"))
    assert tool_groups.DEFAULT_ON == {"base", "assemblies"}
    off = tool_groups.park(server.mcp, tool_groups.DEFAULT_ON)
    assert off == ["drawings", "sheet_metal", "simulation", "weldments"]
    names = _names()
    assert not {"create_drawing", "weldment_member", "sim_run", "sheet_metal_info"} & names
    assert {"insert_component", "execute_batch", "enable_tools", "tool_help"} <= names
    # help still finds a parked tool and says how to run it
    info = json.loads(tool_docs.help_text("create_drawing"))
    assert info["group"] == "drawings" and "execute_batch" in info["off"] and "enable_tools(['drawings'])" in info["off"]
    listing = json.loads(tool_docs.help_text())
    assert set(listing["groups_off"]) == {"drawings", "sheet_metal", "simulation", "weldments"}
    assert "[off" in listing["tools"]["weldment_member"] and "[off" not in listing["tools"]["insert_component"]
    # a batch with an unknown tool is refused before anything runs — the parked tool's group stays off
    with pytest.raises(Exception, match=r"BATCH_STEP.*'paint_it'") as ex:
        asyncio.run(server.mcp.call_tool("execute_batch", {"steps": [{"tool": "weldment_member", "args": {}},
                                                                     {"tool": "paint_it", "args": {}}]}))
    assert "weldment_member" not in str(ex.value) and "weldment_member" not in _names()
    # execute_batch runs a parked tool (no client has to re-read its list): the group is turned on and logged;
    # the step itself fails on the missing profile before it reaches SolidWorks
    out = json.loads(_text(asyncio.run(server.mcp.call_tool(
        "execute_batch", {"steps": [{"tool": "weldment_member", "args": {}}], "report": "full"}))))
    assert out["groups_turned_on"] == ["weldments"] and out["ok"] == 0
    assert out["results"][0]["tool"] == "weldment_member" and "profile" in out["results"][0]["error"]
    assert "weldment_member" in _names() and [r["enable"] for r in tool_groups.read_usage() if "enable" in r] == [
        "weldments"]
    # turn one group on mid-conversation (no client here: the tools are back, the notification is skipped)
    out = json.loads(_text(asyncio.run(server.mcp.call_tool("enable_tools", {"groups": ["drawings"]}))))
    assert "create_drawing" in out["tools_added"] and out["still_off"] == ["sheet_metal", "simulation"]
    assert "execute_batch" in out["note"]
    assert "create_drawing" in _names() and "sim_run" not in _names()
    with pytest.raises(Exception, match="BAD_PARAMETER"):
        asyncio.run(server.mcp.call_tool("enable_tools", {"groups": ["paint"]}))


def test_usage_log_records_calls_without_arguments(monkeypatch, tmp_path):
    log = tmp_path / "u.jsonl"
    monkeypatch.setattr(tool_groups, "USAGE_PATH", str(log))
    asyncio.run(server.mcp.call_tool("tool_help", {"topic": "secret-word"}))
    rows = tool_groups.read_usage(str(log))
    assert rows[-1]["tool"] == "tool_help" and rows[-1]["ok"] is True and rows[-1]["group"] == "base"
    assert "secret-word" not in log.read_text(encoding="utf-8")
    monkeypatch.setattr(tool_groups, "USAGE_PATH", "")       # SWMCP_USAGE_LOG=off
    tool_groups.record("view_image", 1, True)
    assert len(tool_groups.read_usage(str(log))) == len(rows)


def test_an_unknown_parameter_is_an_error():
    """A guessed parameter name is not dropped silently (the SDK ignores extra keys) — it is BAD_PARAMETER with the
    parameters the tool has."""
    with pytest.raises(Exception, match=r"BAD_PARAMETER \| list_components has no parameter max_items.*limit"):
        asyncio.run(server.mcp.call_tool("list_components", {"max_items": 400}))
    out = asyncio.run(server.mcp.call_tool("tool_help", {"topic": "units"}))   # known parameters still pass
    assert "MILLIMETRES" in _text(out)


def test_tools_list_changed_is_advertised():
    opts = server.mcp._lowlevel_server.create_initialization_options()
    assert opts.capabilities.tools.list_changed is True
