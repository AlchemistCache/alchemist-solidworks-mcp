"""The one wrapper around every tool (server._wrap) and what tools send: a slow upstream tool runs as a background
job, every answer is ONE JSON object, every call is in the usage log, and optional parameters follow session.given
(None / "" / [] / {} are not sent; 0 and False are values). A fake execution server, no SolidWorks."""
import asyncio
import json
import os
import re
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

pytest.importorskip("mcp")
import jobs  # noqa: E402
import server  # noqa: E402
import session  # noqa: E402
import tool_groups  # noqa: E402


@pytest.fixture
def fake(monkeypatch):
    """Every execution-server call answers COMPLETED; returns the list of (tool, params) sent."""
    sent = []

    def call_tool(tool, operation_id, state_version, params):
        sent.append((tool, params))
        return {"status": "COMPLETED", "stateVersion": 7, "cadState": {"activeDocument": "Part1"}}
    monkeypatch.setattr(session, "call_tool", call_tool)
    return sent


def _call(tool, args):
    res = asyncio.run(server.mcp.call_tool(tool, args))
    return json.loads(res.content[0].text)


def _params(sent, tool, args):
    _call(tool, args)
    return [p for t, p in sent if t == tool][-1]


def test_slow_upstream_tools_run_as_jobs(fake, monkeypatch):
    labels = []
    real = jobs.background
    monkeypatch.setattr(jobs, "background", lambda label, fn: labels.append(label) or real(label, fn))
    assert _call("analyze_model", {"analysis_type": "geometry"})["state_version"] == 7
    _call("open_new_part", {})
    assert labels == ["analyze_model"]          # open_new_part is quick: called directly


def test_answers_are_one_object_and_logged(fake, monkeypatch):
    log = []
    monkeypatch.setattr(tool_groups, "record", lambda tool, ms, ok: log.append((tool, ok)))
    assert _call("open_new_part", {}) == {"state_version": 7, "document": "Part1"}
    monkeypatch.setattr(session, "call_tool", lambda *a: {"status": "FAILED", "stateVersion": 7,
                                                          "error": {"code": "NO_TEMPLATE", "message": "none"}})
    with pytest.raises(Exception, match=re.escape("NO_TEMPLATE | none")):
        asyncio.run(server.mcp.call_tool("open_new_part", {}))
    assert log == [("open_new_part", True), ("open_new_part", False)]


def test_one_form_of_any_return(monkeypatch):
    monkeypatch.setattr(session, "_state_version", 3)
    assert server._one_form("t", '{"a":1}') == '{"state_version":3,"a":1}'
    assert server._one_form("t", '{"state_version":5,"a":1}') == '{"state_version":5,"a":1}'
    assert server._one_form("t", "[1,2]") == '{"state_version":3,"items":[1,2]}'
    assert server._one_form("t", "plain text") == '{"state_version":3,"text":"plain text"}'
    image = object()
    assert server._one_form("t", ['{"a":1}', image]) == ['{"state_version":3,"a":1}', image]
    assert server._one_form("tool_help", "# a document") == "# a document"


def test_optional_parameters_follow_one_rule(fake):
    assert _params(fake, "open_new_part", {}) == {}
    assert _params(fake, "open_new_assembly", {"template_path": "D:\\T\\a.asmdot"}) == {"template_path": "D:\\T\\a.asmdot"}
    assert _params(fake, "open_document", {"file_path": "D:\\W\\a.step"}) == {"file_path": "D:\\W\\a.step",
                                                                              "as_assembly": False}
    assert _params(fake, "analyze_assembly", {"analysis_type": "components"}) == {"analysis_type": "components"}
    p = _params(fake, "insert_component", {"file_path": "D:\\W\\a.SLDPRT"})
    assert p == {"file_path": "D:\\W\\a.SLDPRT", "x": 0.0, "y": 0.0, "z": 0.0, "fixed": False}   # no config / transform


def test_minus_one_is_not_given_but_zero_is_a_value(fake):
    p = _params(fake, "create_sketch", {"plane": "Front Plane"})
    assert "face_index" not in p and p["on_face"] is False
    assert _params(fake, "create_sketch", {"on_face": True, "face_index": 0})["face_index"] == 0


def test_mate_value_only_where_it_means_something(fake):
    base = {"a_component": "A-1", "a_index": 0, "b_component": "B-1", "b_index": 2}
    p = _params(fake, "add_mate", dict(base, mate_type="coincident"))
    assert "value" not in p and p["flip"] is False and p["a_index"] == 0
    assert _params(fake, "add_mate", dict(base, mate_type="distance", value=0.0))["value"] == 0.0   # touching
    assert _params(fake, "add_mate", dict(base, mate_type="distance", value=12.5))["value"] == pytest.approx(0.0125)
    assert _params(fake, "add_mate", dict(base, mate_type="angle", value=30.0, flip=True)) == dict(
        base, mate_type="angle", alignment="closest", a_kind="face", b_kind="face", flip=True, value=30.0)


def test_dimension_for_face_answers_one_compact_object(monkeypatch):
    """The adapter-built answer goes the same way as every other: compact JSON, state_version first, mm."""
    if os.path.dirname(os.path.abspath(__file__)) not in sys.path:
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from test_face_dimension import FACES, FEATURE_MAP, RECIPE, SKETCHES
    payloads = {"faces": {"faces": FACES}, "feature_map": FEATURE_MAP, "features": RECIPE}

    def call_tool(tool, operation_id, state_version, params):
        kind = params.get("analysis_type")
        body = SKETCHES[params["name"]] if kind == "sketch" else payloads[kind]
        return {"status": "COMPLETED", "stateVersion": 9, "cadState": {"features": [json.dumps(body)]}}
    monkeypatch.setattr(session, "call_tool", call_tool)
    text = asyncio.run(server.mcp.call_tool("dimension_for_face", {"face_index": 2})).content[0].text
    assert text.startswith('{"state_version":9,"face_index":2,') and "\n" not in text and ", " not in text
    answer = json.loads(text)
    assert answer["status"] == "dimensioned" and answer["dimension"]["name"].startswith("D1@Sketch1")
    assert answer["segment"]["x2"] == 50.0                     # sketch metres → mm


def test_define_sketch_sends_mm_and_sketch_status_passes_through(fake, monkeypatch):
    """define_sketch is a millimetre tool: the design dimensions reach the server unconverted. The server's
    sketch_status of a sketch call is a plain field of the answer (no unit rule touches it)."""
    dims = [{"px": 40, "py": 10}, {"px": 10, "py": 35, "to_origin": True, "orientation": "vertical"}]
    p = _params(fake, "define_sketch", {"dimensions": dims})
    assert p["dimensions"] == dims and p["auto"] is True and p["scheme"] == "baseline" and "sketch_name" not in p
    monkeypatch.setattr(session, "call_tool", lambda *a: {
        "status": "COMPLETED", "stateVersion": 8, "cadState": {"activeDocument": "Part1", "activeSketch": "Sketch1"},
        "result_geometry": {"segment_count": 1, "sketch_status": "Sketch1: under_defined, 1 free segment"}})
    answer = _call("add_sketch_entities", {"segments": [{"entity_type": "line", "x1": 0, "y1": 0, "x2": 50, "y2": 0}]})
    assert answer["sketch"] == "Sketch1" and answer["sketch_status"] == "Sketch1: under_defined, 1 free segment"


def test_view_image_close_up_and_long_side(fake):
    """view_image takes the long side as size, a close-up as focus components or a box in mm (unconverted); the old
    width is gone, not silently ignored."""
    p = _params(fake, "view_image", {"focus": ["Motor-1"], "isolate": True, "size": 1200})
    assert p["size"] == 1200 and p["focus"] == ["Motor-1"] and p["isolate"] is True and p["display"] == "shaded_edges"
    assert _params(fake, "view_image", {"focus_box_mm": [0, 0, 0, 100, 50, 20]})["focus_box_mm"] == [0, 0, 0, 100, 50, 20]
    with pytest.raises(Exception, match="view_image has no parameter width"):
        _call("view_image", {"width": 640})


def test_execute_batch_from_file_answers_a_summary(fake, tmp_path):
    """A long batch comes from a JSON file and answers a summary — not every step's answer again."""
    steps = [{"tool": "list_open_documents", "args": {}} for _ in range(3)]
    f = tmp_path / "steps.json"
    f.write_text(json.dumps({"steps": steps}), encoding="utf-8")
    out = _call("execute_batch", {"steps_file": str(f)})
    assert out["ok"] == 3 and out["failed"] == [] and "results" not in out and out["last"]["tool"] == "list_open_documents"
    full = _call("execute_batch", {"steps": steps, "report": "full"})
    assert len(full["results"]) == 3
    with pytest.raises(Exception, match="steps OR steps_file"):
        _call("execute_batch", {"steps": steps, "steps_file": str(f)})


def test_save_document_options_reach_the_server(fake):
    p = _params(fake, "save_document", {"save_all": True})
    assert p == {"save_all": True}
    p = _params(fake, "save_document", {"file_path": r"D:\Work\copy.SLDPRT", "copy_only": True})
    assert p["copy_only"] is True and "repoint_assemblies" not in p


def test_view_images_takes_objects_and_rejects_unknown_keys(fake):
    out = _call("view_images", {"views": ["top", {"view_dir": [-1, 1, -1], "focus": ["Motor-1"]}]})
    assert [r["view"] for r in out["views"]] == ["top", "view_dir"]
    sent = [p for t, p in fake if t == "view_image"]
    assert sent[-1]["view_dir"] == [-1, 1, -1] and sent[-1]["focus"] == ["Motor-1"]
    with pytest.raises(Exception, match=r"views\[0\] has no parameter width"):
        _call("view_images", {"views": [{"view": "top", "width": 300}]})
