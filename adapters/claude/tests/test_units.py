"""Units at the MCP boundary (units.py): the model's mm / degrees in, SI to the server, mm / degrees back."""
import json

import pytest

import units


def test_inputs_become_meters():
    p = units.params_to_si("extrude_feature", {"depth": 20, "axis_y2": 1, "feature_type": "boss", "reverse": True})
    assert p == {"depth": 0.02, "axis_y2": 0.001, "feature_type": "boss", "reverse": True}
    seg = units.params_to_si("add_sketch_entities", {"segments": [
        {"entity_type": "line", "x1": 0, "y1": 0, "x2": 50, "y2": 0},
        {"entity_type": "spline", "points": [0, 10, 20, 30], "construction": True}]})["segments"]
    assert seg[0]["x2"] == pytest.approx(0.05) and seg[1]["points"] == pytest.approx([0, 0.01, 0.02, 0.03])
    assert seg[1]["construction"] is True
    hw = units.params_to_si("hole_wizard", {"points": [[10, 50, 0]], "cbore_diameter": None, "size": "M6"})
    assert hw["points"][0] == pytest.approx([0.01, 0.05, 0]) and hw["cbore_diameter"] is None and hw["size"] == "M6"
    ef = units.params_to_si("add_edge_feature", {"edge_points": [[50, 0, 5]], "edge_indices": [3], "angle": 45})
    assert ef["edge_points"][0] == pytest.approx([0.05, 0, 0.005]) and ef["edge_indices"] == [3] and ef["angle"] == 45
    t = [1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 20, -5, 1]
    ic = units.params_to_si("insert_component", {"transform": t, "x": 10})
    assert ic["transform"][:9] == t[:9] and ic["transform"][9:12] == pytest.approx([0.1, 0.02, -0.005])
    assert ic["transform"][12] == 1 and ic["x"] == pytest.approx(0.01)
    # a millimetre tool is untouched
    assert units.params_to_si("sweep_pipe", {"diameter_mm": 20}) == {"diameter_mm": 20}


def test_answers_become_millimetres():
    r = {"status": "COMPLETED", "result_geometry": {"volume": 1.5e-05, "volume_mm3": 15000.0, "faces": 6, "edges": 12}}
    assert units.response_to_mm("extrude_feature", r)["result_geometry"] == {"volume_mm3": 15000.0, "faces": 6,
                                                                              "edges": 12}
    edges = {"edge_count": 1, "near": [0.05, 0.03, 0.005],
             "edges": [{"i": 0, "start": [0, 0.002, 0.01], "end": [0.002, 0, 0.01], "mid": [0.001, 0.001, 0.01],
                        "length": 0.025133, "dist": 0.0}]}
    faces = {"faces": [{"i": 0, "planar": True, "area": 0.00028, "normal": [-1.0, 0, 0], "point": [0, 0.016, 0.005]},
                       {"i": 1, "axis": [0, 0, -1.0], "origin": [0.025, 0.015, 0.01], "radius": 0.004}]}
    out = units.response_to_mm("analyze_model", {"cadState": {"features": [json.dumps(edges), json.dumps(faces)]}})
    e, f = (json.loads(x) for x in out["cadState"]["features"])
    assert e["edges"][0]["mid"] == [1.0, 1.0, 10.0] and e["edges"][0]["length"] == pytest.approx(25.133)
    assert e["near"] == [50.0, 30.0, 5.0] and e["edge_count"] == 1 and e["edges"][0]["i"] == 0
    assert f["faces"][0]["area_mm2"] == pytest.approx(280) and f["faces"][0]["normal"] == [-1.0, 0, 0]
    assert f["faces"][1]["axis"] == [0, 0, -1.0] and f["faces"][1]["radius"] == pytest.approx(4)
    sketch = {"name": "S", "sketch": {"segments": [{"kind": "arc", "cx": 0.025, "r": 0.004, "dir": -1}],
                                      "points": [{"x": 0.01, "y": 0.02, "endpoint_of": [0, 1]}]},
              "plane": {"ref": "Front Plane", "offset": 0.05,
                        "frame": {"origin": [0, 0, 0.05], "xdir": [1, 0, 0], "ydir": [0, 1, 0]}},
              "dimensions": [{"name": "D1@S", "value_si": 0.01}, {"name": "D2@S", "value_si": 0.5235988,
                                                                   "angular": True}]}
    s = units.to_mm(sketch, "analyze_model")
    seg, pt = s["sketch"]["segments"][0], s["sketch"]["points"][0]
    assert seg["cx"] == pytest.approx(25) and seg["r"] == pytest.approx(4) and seg["dir"] == -1
    assert pt == {"x": 10.0, "y": 20.0, "endpoint_of": [0, 1]}
    assert s["plane"]["offset"] == 50 and s["plane"]["frame"]["origin"] == [0, 0, 50.0]
    assert s["plane"]["frame"]["xdir"] == [1, 0, 0]
    assert s["dimensions"][0] == {"name": "D1@S", "value_mm": 10.0}
    assert s["dimensions"][1]["value_deg"] == pytest.approx(30.0) and "value_si" not in s["dimensions"][1]
    count = units.to_mm({"name": "D1@CirPattern1", "value_si": 42.0, "count": True}, "analyze_model")
    assert count == {"name": "D1@CirPattern1", "value": 42, "count": True}   # a pattern count, not 42000 mm
    flange = units.to_mm({"type": "EdgeFlange", "angle": 1.5707963, "radius": 0.002, "gap": 0.0005}, "analyze_model")
    assert flange["angle"] == pytest.approx(90) and flange["radius"] == pytest.approx(2) and flange["gap"] == 0.5


def test_assembly_answers():
    comp = {"components": [{"name": "A-1", "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.02, 0, 1]}]}
    c = units.to_mm(comp, "analyze_assembly")["components"][0]["transform"]
    assert c[9:12] == [100.0, 20.0, 0] and c[12] == 1 and c[:9] == [1, 0, 0, 0, 1, 0, 0, 0, 1]
    mates = {"mates": [{"type": "distance", "value": 0.015}, {"type": "angle", "value": 1.5707963},
                       {"type": "coincident"}]}
    m = units.to_mm(mates, "analyze_assembly")["mates"]
    assert m[0]["value"] == pytest.approx(15) and m[1]["value"] == pytest.approx(90) and "value" not in m[2]
    ent = units.to_mm({"component": "B-2", "kind": "circle", "params": [0.1, 0, 0.027, 0, 0, -1.0, 0.004, 0]},
                      "analyze_assembly")
    assert ent["params"] == [100.0, 0, 27.0, 0, 0, -1.0, 4.0, 0]
    added = units.response_to_mm("add_mate", {"cadState": {"features": [
        "mate=Distance1", "b_transform=[1,0,0,0,1,0,0,0,1,0.1,0,0.027,1]"]}})["cadState"]["features"]
    assert added[0] == "mate=Distance1" and added[1] == "b_transform=[1,0,0,0,1,0,0,0,1,100.0,0.0,27.0,1]"


def test_drawing_read():
    view = {"views": [{"pos": [0.1, 0.05], "scale": 2.0, "type": 7,
                       "dimensions": [{"value_si": 0.03, "anchors": [[0, 0.0001, 0.03]]}],
                       "extent": {"X": [0, 0.1]}, "circles": [{"cx": 0.03, "cy": 0, "r": 0.004}]}]}
    v = units.to_mm(view, "check_drawing")["views"][0]
    assert v["pos"] == [100.0, 50.0] and v["scale"] == 2.0 and v["type"] == 7
    assert v["dimensions"][0]["value_mm"] == 30.0 and v["dimensions"][0]["anchors"] == [[0, 0.1, 30.0]]
    assert v["extent"] == {"X": [0, 100.0]} and v["circles"][0]["r"] == 4.0


def test_dimension_tools_echo_mm():
    r = units.response_to_mm("add_dimension", {"result_geometry": {"dimension": "D1@S", "requested": 0.06,
                                                                    "effective": 0.06, "type": "linear"}})
    assert r["result_geometry"]["requested"] == 60.0 and r["result_geometry"]["type"] == "linear"
    # modify_dimension converts on the server (units=mm) — its answer is not converted twice
    g = {"requested": 12, "effective": 12, "unit": "mm"}
    assert units.response_to_mm("modify_dimension", {"result_geometry": g})["result_geometry"] == g
