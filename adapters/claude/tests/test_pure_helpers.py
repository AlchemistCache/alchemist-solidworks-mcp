"""The helpers moved out of the tool modules work without MCP or SolidWorks."""
import os
import sys

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import analysis_reads  # noqa: E402
import face_compare  # noqa: E402


def test_kv_dict_reads_numbers_in_either_decimal_style():
    kv = analysis_reads.kv_dict(["volume=4,22503E-06", "surface_area=0.0046", "faces=12", "name=Плита", "junk"])
    assert kv == {"volume": 4.22503e-06, "surface_area": 0.0046, "faces": 12, "name": "Плита"}


def test_analysis_items_raise_on_a_failed_response():
    assert analysis_reads.analysis_items({"status": "COMPLETED", "cadState": {"features": ["a=1"]}}) == ["a=1"]
    try:
        analysis_reads.analysis_items({"status": "FAILED", "error": {"code": "NO_ACTIVE_DOCUMENT", "message": "x"}})
    except RuntimeError as ex:
        assert "NO_ACTIVE_DOCUMENT" in str(ex)
    else:
        raise AssertionError("FAILED must raise")


def test_face_diff_pairs_drift_and_reports_real_changes():
    top = {"i": 0, "planar": True, "normal": [0, 0, 1], "point": [0, 0, 0.01], "area": 1e-3}
    top_drift = {"i": 5, "planar": True, "normal": [0, 0, -1], "point": [0.02, 0.01, 0.01 + 1e-6], "area": 1e-3}
    hole = {"i": 1, "kind": "cylinder", "axis": [0, 0, 1], "radius": 0.003}
    missing, extra = face_compare.diff_faces([top, hole], [top_drift])
    assert missing == [hole] and extra == []
    assert face_compare.face_desc(hole) == "i=1 cyl r=0.003"


def test_face_diff_cylinders_need_the_same_axis_line():
    hole = {"i": 1, "kind": "cylinder", "axis": [0, 0, 1], "origin": [0.01, 0.02, 0], "radius": 0.003}
    same = dict(hole, axis=[0, 0, -1], origin=[0.01, 0.02, 0.05])        # same line, other direction / point on it
    moved = dict(hole, origin=[0.015, 0.02, 0])                          # parallel axis, moved 5 mm
    skew = dict(hole, axis=[0.7071, 0.7071, 0], origin=[0, 0, 0])
    assert face_compare.faces_compatible(hole, same)
    assert not face_compare.faces_compatible(hole, moved)
    assert not face_compare.faces_compatible(dict(hole, axis=[0.7071, -0.7071, 0], origin=[0, 0, 0]), skew)


def test_face_diff_planes_either_normal_sign():
    a = {"planar": True, "normal": [0, 0, 1], "point": [0, 0, 0.01]}
    assert face_compare.faces_compatible(a, {"planar": True, "normal": [0, 0, -1], "point": [0.03, 0.01, 0.01]})
    assert not face_compare.faces_compatible(a, {"planar": True, "normal": [0, 0, 1], "point": [0, 0, 0.012]})
    assert face_compare.faces_compatible({"planar": True, "normal": [0.7071, 0.7071, 0], "point": [0, 0, 0]},
                                         {"planar": True, "normal": [-0.7071, -0.7071, 0], "point": [0, 0, 1]})



def test_part_verdict_needs_exact_topology_and_one_percent():
    import verify
    geo = {"bodies": 1, "faces": 7, "edges": 15, "vertices": 10}
    mass = {"volume": 1.0e-5, "surface_area": 3.0e-3, "cx": 0.0, "cy": 0.0, "cz": 0.0}
    same = verify.part_verdict(geo, dict(geo), mass, dict(mass, volume=1.009e-5, cz=0.003))
    assert same["verified"] and same["topology_exact"] and abs(same["cg_distance"] - 0.003) < 1e-12
    assert not verify.part_verdict(geo, dict(geo, faces=8), mass, mass)["verified"]          # one face more
    assert not verify.part_verdict(geo, geo, mass, dict(mass, volume=1.02e-5))["verified"]   # 2 % volume
    assert verify.part_verdict({}, {}, mass, mass)["verified"] is False                       # unknown topology
    assert verify.delta_pct(0, 1) is None


def test_assembly_verdict_pairs_instances_by_file_and_order():
    import verify
    t = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0.0, 0.0, 0.0]
    a = [{"path": r"C:\x\Pin.SLDPRT", "transform": t}, {"path": r"C:\x\Pin.SLDPRT", "transform": t[:9] + [0.01, 0, 0]}]
    b = [{"path": r"D:\y\pin.sldprt", "transform": t}, {"path": r"D:\y\pin.sldprt", "transform": t[:9] + [0.01, 0, 0]}]
    mates = [{"type": "coincident"}]
    mass = {"volume": 1.0, "surface_area": 1.0}
    assert verify.assembly_verdict(a, b, mates, mates, mass, mass)["verified"]
    moved = [b[0], {"path": b[1]["path"], "transform": t[:9] + [0.011, 0, 0]}]
    v = verify.assembly_verdict(a, moved, mates, mates, mass, mass)
    assert not v["verified"] and not v["transforms_ok"] and abs(v["max_pos"] - 0.001) < 1e-12
    assert not verify.assembly_verdict(a, b[:1], mates, mates, mass, mass)["set_exact"]


def test_analysis_payload_unparseable():
    from response_mapper import CadOperationError
    try:
        analysis_reads.analysis_payload({"status": "COMPLETED", "cadState": {"features": ["{oops"]}})
    except CadOperationError as ex:
        assert str(ex).startswith("PAYLOAD_UNPARSEABLE | ")
    else:
        raise AssertionError("an unreadable payload must raise")


def test_sim_verdict_bands():
    import sim_report
    assert sim_report.verdict({"factor_of_safety": {"min": 2.0}}, 1.5)[0] == "ok"
    assert sim_report.verdict({"factor_of_safety_min": 1.2}, 1.5)[0] == "warn"     # no yield, but below the target
    assert sim_report.verdict({"factor_of_safety": {"min": 0.8}}, 1.5)[0] == "bad"
    assert sim_report.verdict({}, 1.5)[0] == "warn"                                  # unknown is never "ok"


def test_given_drops_only_not_given():
    import session
    assert session.given({"a": None, "b": "", "c": [], "d": {}, "e": 0, "f": False, "g": "x"}) == {"e": 0, "f": False, "g": "x"}


def test_a_non_json_server_answer_is_an_execution_error():
    """HTTP.sys answers some failures with an HTML page — that is the execution layer's error with the status and the
    start of the body, not a JSONDecodeError the adapter would blame on the tool's arguments."""
    import execution_client

    class Html:
        status_code, text = 400, "<html><body>Bad Request - Invalid Hostname</body></html>"

        def json(self):
            raise ValueError("Expecting value")
    try:
        execution_client._json_body(Html(), "extrude_feature")
    except execution_client.ExecutionLayerError as ex:
        assert "HTTP 400 from extrude_feature is not JSON" in str(ex) and "Bad Request" in str(ex)
    else:
        raise AssertionError("must raise")


def test_model_diff_names_what_changed():
    import bridge
    a = {"doc": "Part1", "items": ["pin-1 (component)", "Boss1"]}
    assert bridge._model_diff(a, {"doc": "Part1", "items": ["pin-1 (component)", "Boss1"]}) is None
    assert bridge._model_diff(a, {"doc": "Part2", "items": []}) == {"document_changed": "Part1 → Part2"}
    d = bridge._model_diff(a, {"doc": "Part1", "items": ["Boss1", "Coincident1 (mate)"]})
    assert d == {"created": ["Coincident1 (mate)"], "removed": ["pin-1 (component)"]}
    assert bridge._model_diff(None, a) is None


def test_wire_json_is_compact_and_unicode():
    import drawing_summary
    assert drawing_summary.wire_json({"a": [1, 2], "т": "Ø"}) == '{"a":[1,2],"т":"Ø"}'
