"""Sheet-metal production helpers that run without SolidWorks: flat-pattern DXF layering / DFM checks
(sheet_dxf.py) and the XLSX cut list (sheet_reports.py).

Run:  python -m pytest adapters/claude/tests/test_sheet_production.py
"""
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

ezdxf = pytest.importorskip("ezdxf")


def _flat(path, holes=(), bends=(), marks=()):
    """100 × 60 blank like SolidWorks writes it: layer 0, colour 7 contour, colour-8 bend lines."""
    doc = ezdxf.new(units=4)
    msp = doc.modelspace()
    pts = [(0, 0), (100, 0), (100, 60), (0, 60), (0, 0)]
    for a, b in zip(pts, pts[1:]):
        msp.add_line(a, b, dxfattribs={"color": 7})
    for (x, y, r) in holes:
        msp.add_circle((x, y), r, dxfattribs={"color": 7})
    for y in bends:
        msp.add_line((0, y), (100, y), dxfattribs={"color": 8})
    for a, b in marks:
        msp.add_line(a, b, dxfattribs={"color": 7})
    doc.saveas(path)
    return str(path)


def test_classify_and_layers(tmp_path):
    import sheet_dxf
    f = _flat(tmp_path / "p.dxf", holes=[(50, 40, 3)], bends=[20], marks=[((10, 45), (30, 45))])
    counts = sheet_dxf.apply_layers(f, note="A-01 | t=2 мм | 3 шт")
    assert counts == {"CUT_OUTER": 4, "CUT_INNER": 1, "BEND": 1, "MARK": 1}
    doc = ezdxf.readfile(f)
    layers = {e.dxf.layer for e in doc.modelspace()}
    assert layers == {"CUT_OUTER", "CUT_INNER", "BEND", "MARK", "NOTE"}
    assert doc.layers.get("BEND").dxf.linetype == "DASHED"
    # idempotent: a re-layered file classifies the same way
    assert sheet_dxf.apply_layers(f) == counts


def test_dfm_clean_part(tmp_path):
    import sheet_dxf
    f = _flat(tmp_path / "ok.dxf", holes=[(50, 45, 3)], bends=[20])
    r = sheet_dxf.dfm_check(f, t=2, r=2, k=0.5, details=True)
    assert r["bends"] == 1 and r["cutouts"] == 1
    assert r["issues"] == []
    sides = sorted(s["flat_mm"] for s in r["bend_flanges"][0]["sides"])
    assert sides == [20.0, 40.0]


def test_dfm_finds_problems(tmp_path):
    import sheet_dxf
    # t=2, R=2 → limits: hole→bend 7, hole→edge 2, hole Ø 2, flange 8
    f = _flat(tmp_path / "bad.dxf",
              holes=[(50, 24, 2),        # 2 mm from the bend line at y=20
                     (80, 40, 0.5),      # Ø1 < t
                     (97, 50, 2)],       # 1 mm from the right edge
              bends=[20, 57])            # 3 mm flange at the top
    r = sheet_dxf.dfm_check(f, t=2, r=2, k=0.5)
    rules = sorted(i["rule"] for i in r["issues"])
    assert "hole_to_bend" in rules and "small_hole" in rules and "hole_to_edge" in rules and "short_flange" in rules
    hb = next(i for i in r["issues"] if i["rule"] == "hole_to_bend" and i["at"] == [50.0, 24.0])
    assert hb["value_mm"] == pytest.approx(2.0, abs=0.05) and hb["limit_mm"] == 7.0
    # a cut-out across a bend line is distance 0
    f2 = _flat(tmp_path / "cross.dxf", holes=[(50, 20, 3)], bends=[20])
    hb2 = [i for i in sheet_dxf.dfm_check(f2, 2, 2)["issues"] if i["rule"] == "hole_to_bend"]
    assert hb2 and hb2[0]["value_mm"] == 0


def test_rule_override(tmp_path):
    import sheet_dxf
    f = _flat(tmp_path / "o.dxf", holes=[(50, 30, 3)], bends=[20])
    assert sheet_dxf.dfm_check(f, 2, 2)["issues"] == []
    r = sheet_dxf.dfm_check(f, 2, 2, rules={"hole_to_bend_mm": 10.0})
    assert [i["rule"] for i in r["issues"]] == ["hole_to_bend"]


def test_cutlist_xlsx(tmp_path):
    openpyxl = pytest.importorskip("openpyxl")
    import sheet_reports
    rows = [
        {"part": "Bracket", "config": "A", "Обозначение": "OP-10", "qty": 2, "material": "Ст3", "thickness_mm": 3.0,
         "blank_length_mm": 120.0, "blank_width_mm": 60.0, "net_area_m2": 0.0068, "cut_length_mm": 400.0,
         "bends": 2, "mass_kg": 0.16},
        {"part": "Bracket copy", "config": "A", "Обозначение": "OP-10", "qty": 1, "material": "Ст3", "thickness_mm": 3.0,
         "blank_length_mm": 120.0, "blank_width_mm": 60.0, "net_area_m2": 0.0068, "cut_length_mm": 400.0,
         "bends": 2, "mass_kg": 0.16},
        {"part": "Plate", "config": "", "Обозначение": "OP-2", "qty": 1, "material": "Ст3", "thickness_mm": 3.0,
         "blank_length_mm": 300.0, "blank_width_mm": 240.0, "net_area_m2": 0.07, "cut_length_mm": 1200.0,
         "bends": 0, "mass_kg": 1.7},
        {"part": "Cover", "config": "", "qty": 4, "material": "AISI 304", "thickness_mm": 1.0, "mass_kg": 0.3},
    ]
    totals = [{"thickness_material": "3 | Ст3", "positions": 3, "qty": 4, "mass_kg": 2.18}]
    x = sheet_reports.write_cutlist_xlsx(str(tmp_path / "c.xlsx"), rows, totals, "Test.SLDASM")
    assert x["positions"] == 3 and x["groups"] == 2
    wb = openpyxl.load_workbook(x["xlsx"])
    ws = wb["Ведомость"]
    values = [[c.value for c in r] for r in ws.iter_rows()]
    des = [v[1] for v in values if v[0] and isinstance(v[0], int)]
    assert des == [None, "OP-2", "OP-10"]          # 1 mm group first, natural order inside 3 mm
    merged = next(v for v in values if v[1] == "OP-10")
    assert merged[5] == 3 and merged[3] == "Bracket, Bracket copy"
    assert any(isinstance(v[15], str) and v[15].startswith("=SUM(") for v in values)
    assert wb["Итоги"]["A2"].value == "3 | Ст3"


def test_dfm_part_entry(tmp_path):
    """sheet_metal_dfm's per-part step: the export entry → the part's issues, an assumed K, mixed radii, errors."""
    pytest.importorskip("mcp")
    import tools_sheet_metal
    f = _flat(tmp_path / "ok.dxf", holes=[(50, 45, 3)], bends=[20])
    entry = {"file": f, "part": "A-01", "thickness_mm": 2, "bend_radius_mm": 2, "bend_radii_mm": [2, 3], "material": "St3"}
    item = tools_sheet_metal._dfm_part(entry, {}, False)
    assert item["k_factor_assumed"] == 0.5 and item["part"] == "A-01" and item["material"] == "St3"
    assert [i["rule"] for i in item["issues"]] == ["mixed_bend_radii"]
    assert tools_sheet_metal._dfm_part(dict(entry, thickness_mm=0), {}, False)["error"] == "thickness unknown"
    assert "error" in tools_sheet_metal._dfm_part(dict(entry, file=str(tmp_path / "missing.dxf")), {}, False)
