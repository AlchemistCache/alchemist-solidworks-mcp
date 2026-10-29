"""sheet_dxf.dfm_check pinned whole: the answer for synthetic flat patterns (100 × 60 blank, colour-8 bend lines, as
SolidWorks writes them) — issues in order, limits, flat size, per-bend flange lengths, the shared middle leg."""
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

ezdxf = pytest.importorskip("ezdxf")
import sheet_dxf  # noqa: E402

LIMITS = {"min_flange_mm": 8.0, "hole_to_bend_mm": 7.0, "hole_to_edge_mm": 2.0, "min_hole_mm": 2.0}   # t = 2, R = 2


def _flat(path, holes=(), bends=(), slots=()):
    doc = ezdxf.new(units=4)
    msp = doc.modelspace()

    def loop(pts):
        for a, b in zip(pts, pts[1:]):
            msp.add_line(a, b, dxfattribs={"color": 7})
    loop([(0, 0), (100, 0), (100, 60), (0, 60), (0, 0)])
    for (x, y, r) in holes:
        msp.add_circle((x, y), r, dxfattribs={"color": 7})
    for (x0, y0, x1, y1) in slots:
        loop([(x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0)])
    for y in bends:
        msp.add_line((0, y), (100, y), dxfattribs={"color": 8})
    doc.saveas(path)
    return str(path)


def _side(flat, outside, to_bend=False):
    return {"flat_mm": flat, "outside_mm": outside, "to_bend": to_bend}


def test_clean_part_with_flange_details(tmp_path):
    f = _flat(tmp_path / "a.dxf", holes=[(50, 45, 3)], bends=[20])
    r = sheet_dxf.dfm_check(f, 2, 2, 0.5, details=True)
    assert r == {
        "bends": 1, "cutouts": 1, "issues": [], "limits_mm": LIMITS, "flat_mm": [100.0, 60.0],
        "bend_flanges": [{"bend": 1, "length_mm": 100.0, "sides": [_side(40.0, 41.64), _side(20.0, 21.64)]}]}
    assert list(r) == ["bends", "cutouts", "issues", "limits_mm", "flat_mm", "bend_flanges"]   # the answer's order


def test_every_rule_in_order(tmp_path):
    f = _flat(tmp_path / "b.dxf", holes=[(50, 24, 2), (80, 40, 0.5), (97, 50, 2)], bends=[20, 57])
    assert sheet_dxf.dfm_check(f, 2, 2) == {
        "bends": 2, "cutouts": 3, "limits_mm": LIMITS, "flat_mm": [100.0, 60.0], "issues": [
            {"rule": "hole_to_bend", "value_mm": 2.0, "limit_mm": 7.0, "at": [50.0, 24.0], "bend": 1},
            {"rule": "small_hole", "value_mm": 1.0, "limit_mm": 2.0, "at": [80.0, 40.0]},
            {"rule": "hole_to_edge", "value_mm": 1.0, "limit_mm": 2.0, "at": [97.0, 50.0]},
            {"rule": "hole_to_bend", "value_mm": 5.0, "limit_mm": 7.0, "at": [97.0, 50.0], "bend": 2},
            {"rule": "short_flange", "value_mm": 4.64, "limit_mm": 8.0, "at": [50.0, 58.5], "bend": 2}]}


def test_the_middle_leg_is_one_issue_of_both_bends(tmp_path):
    f = _flat(tmp_path / "h.dxf", holes=[(50, 21.5, 0.5)], bends=[20, 23])
    assert sheet_dxf.dfm_check(f, 2, 2, 0.4, details=True) == {
        "bends": 2, "cutouts": 1, "limits_mm": LIMITS, "flat_mm": [100.0, 60.0], "issues": [
            {"rule": "small_hole", "value_mm": 1.0, "limit_mm": 2.0, "at": [50.0, 21.5]},
            {"rule": "hole_to_bend", "value_mm": 1.0, "limit_mm": 7.0, "at": [50.0, 21.5], "bend": 1},
            {"rule": "hole_to_bend", "value_mm": 1.0, "limit_mm": 7.0, "at": [50.0, 21.5], "bend": 2},
            {"rule": "short_flange", "value_mm": 6.6, "limit_mm": 8.0, "at": [50.0, 21.5], "bends": [1, 2]}],
        "bend_flanges": [{"bend": 1, "length_mm": 100.0, "sides": [_side(3.0, 6.6, True), _side(20.0, 21.8)]},
                         {"bend": 2, "length_mm": 100.0, "sides": [_side(37.0, 38.8), _side(3.0, 6.6, True)]}]}


def test_rules_0_or_none_keep_the_default(tmp_path):
    f = _flat(tmp_path / "e.dxf", holes=[(50, 30, 3)], bends=[20], slots=[(10, 40, 30, 41)])
    r = sheet_dxf.dfm_check(f, 2, 2, rules={"hole_to_bend_mm": 10.0, "min_hole_mm": 0, "hole_to_edge_mm": None})
    assert r == {"bends": 1, "cutouts": 2, "limits_mm": dict(LIMITS, hole_to_bend_mm=10.0), "flat_mm": [100.0, 60.0],
                 "issues": [{"rule": "hole_to_bend", "value_mm": 7.0, "limit_mm": 10.0, "at": [50.0, 30.0], "bend": 1},
                            {"rule": "small_hole", "value_mm": 1.0, "limit_mm": 2.0, "at": [20.0, 40.5]}]}


def test_no_bends(tmp_path):
    f = _flat(tmp_path / "f.dxf", holes=[(50, 30, 3)])
    assert sheet_dxf.dfm_check(f, 2, 0, details=True) == {
        "bends": 0, "cutouts": 1, "issues": [], "flat_mm": [100.0, 60.0], "bend_flanges": [],
        "limits_mm": dict(LIMITS, hole_to_bend_mm=5.0)}


def test_flat_read_from_the_model_checks_like_the_export(tmp_path):
    """flat_geometry's tessellation outline (a hole as 32 points, a rectangle split into many collinear points) gives the
    same answer as SolidWorks' exported DXF of the same blank."""
    import math
    hole = [[50 + 3 * math.cos(2 * math.pi * i / 32), 45 + 3 * math.sin(2 * math.pi * i / 32)] for i in range(32)]
    outer = [[x, 0] for x in range(0, 100, 10)] + [[100, 0], [100, 60], [0, 60]]
    part = {"loops": [outer, hole], "bend_lines": [[0, 20, 100, 20]]}
    model = sheet_dxf.dfm_check_doc(sheet_dxf.doc_from_flat(part), 2, 2, 0.5, details=True)
    export = sheet_dxf.dfm_check(_flat(tmp_path / "a.dxf", holes=[(50, 45, 3)], bends=[20]), 2, 2, 0.5, details=True)
    assert model == export
    small = {"loops": [outer, [[50 + 0.75 * math.cos(2 * math.pi * i / 16), 45 + 0.75 * math.sin(2 * math.pi * i / 16)]
                               for i in range(16)]], "bend_lines": []}
    issues = sheet_dxf.dfm_check_doc(sheet_dxf.doc_from_flat(small), 2, 2)["issues"]
    assert [i["rule"] for i in issues] == ["small_hole"] and issues[0]["value_mm"] == 1.5
