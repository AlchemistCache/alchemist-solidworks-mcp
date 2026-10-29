"""CalculiX cross-check helpers (calculix.py) on a hand-made mesh — no SolidWorks, no ccx needed."""
import os
import sys

import numpy as np
import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _ADAPTER_DIR not in sys.path:
    sys.path.insert(0, _ADAPTER_DIR)

import calculix  # noqa: E402

# unit cube split into 5 tetrahedra; corners 1..8
CUBE = {1: (0, 0, 0), 2: (1, 0, 0), 3: (1, 1, 0), 4: (0, 1, 0), 5: (0, 0, 1), 6: (1, 0, 1), 7: (1, 1, 1), 8: (0, 1, 1)}
TETS = [(1, 2, 4, 5), (2, 3, 4, 7), (2, 5, 6, 7), (4, 5, 7, 8), (2, 4, 5, 7)]


def _model(quadratic=False):
    nodes = {k: np.array(v, float) * 10 for k, v in CUBE.items()}
    raw, nid = [], 100
    mids = {}
    for e, t in enumerate(TETS, 1):
        ids = list(t)
        if quadratic:
            # midside nodes in a SCRAMBLED order — build_deck must match them geometrically
            for a, b in [(2, 3), (0, 1), (1, 3), (0, 2), (1, 2), (0, 3)]:
                key = tuple(sorted((t[a], t[b])))
                if key not in mids:
                    nid += 1
                    mids[key] = nid
                    nodes[nid] = (nodes[t[a]] + nodes[t[b]]) / 2
                ids.append(mids[key])
        ids += [-1] * (10 - len(ids))
        raw += [e] + ids + [0, 0, 0, 1, 1]
    return {"nodes": [[k, *v] for k, v in nodes.items()], "elements_raw": raw, "element_stride": 16,
            "material": {"E_mpa": 210000, "nu": 0.3},
            "sets": {"FIX": [n for n, p in nodes.items() if p[2] == 0], "TOP": [n for n, p in nodes.items() if p[2] == 10]}}


@pytest.mark.parametrize("quadratic", [False, True])
def test_deck(tmp_path, quadratic):
    m = _model(quadratic)
    info = calculix.build_deck(m, ["FIX"], [{"set": "TOP", "force_n": [0, 0, -1000]}], str(tmp_path / "job.inp"))
    assert info["element_type"] == ("C3D10" if quadratic else "C3D4") and info["elements"] == 5
    assert info["loads"][0]["sum_n"] == [0.0, 0.0, -1000.0]          # the whole force reaches the face
    assert info["loads"][0]["faces_area_mm2"] == pytest.approx(100.0)
    text = open(info["deck"]).read()
    assert "*BOUNDARY" in text and "FIX, 1, 3" in text and "*EL PRINT" in text
    xyz = {int(n[0]): np.array(n[1:4]) for n in m["nodes"]}
    for line in text.split("*ELEMENT")[1].split("*NSET")[0].splitlines()[1:]:
        ids = [int(x) for x in line.replace(",", " ").split()][1:]
        p = [xyz[i] for i in ids[:4]]
        assert np.dot(np.cross(p[1] - p[0], p[2] - p[0]), p[3] - p[0]) > 0       # positive volume
        if quadratic:                                                             # node 5 = midpoint of 1-2 …
            for k, (a, b) in enumerate(calculix.C3D10_EDGES):
                assert np.allclose(xyz[ids[4 + k]], (xyz[ids[a]] + xyz[ids[b]]) / 2)


def test_parse_dat_and_compare(tmp_path):
    dat = tmp_path / "job.dat"
    dat.write_text("""
 displacements (vx,vy,vz) for set NALL and time  0.1000000E+01

         1  0.000000E+00  0.000000E+00  0.000000E+00
         7  1.000000E-03 -2.000000E-03  2.000000E-03

 stresses (elem, integ.pnt.,sxx,syy,szz,sxy,sxz,syz) for set EALL and time  0.1000000E+01

         1   1  1.000000E+01  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00
         2   1  1.000000E+02  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00  0.000000E+00
""")
    r = calculix.parse_dat(str(dat))
    assert r["max_displacement_mm"] == pytest.approx(0.003) and r["at_node"] == 7
    assert r["max_von_mises_mpa"] == pytest.approx(100.0) and r["in_element"] == 2
    ev = calculix.compare({"displacement_mm": {"max": 0.0031}, "von_mises_mpa": {"max": 120}}, r)
    assert ev["status"] == "pass"
    ev = calculix.compare({"displacement_mm": {"max": 0.01}, "von_mises_mpa": {"max": 120}}, r)
    assert ev["status"] == "warn"


# a 20×10 plate at z = 0 as four shell triangles; corners 1..6
PLATE = {1: (0, 0, 0), 2: (10, 0, 0), 3: (20, 0, 0), 4: (0, 10, 0), 5: (10, 10, 0), 6: (20, 10, 0)}
TRIS = [(1, 2, 5), (1, 5, 4), (2, 3, 6), (2, 6, 5)]


def _shell_model(quadratic=True, flip_second=False, extra=None):
    nodes = {k: np.array(v, float) for k, v in PLATE.items()}
    raw, mids, nid = [], {}, 100
    for e, t in enumerate(TRIS, 1):
        t = (t[0], t[2], t[1]) if flip_second and e == 2 else t
        ids = list(t)
        if quadratic:   # midside nodes SCRAMBLED: (2,0), (0,1), (1,2)
            for a, b in [(2, 0), (0, 1), (1, 2)]:
                key = frozenset((t[a], t[b]))
                if key not in mids:
                    nid += 1
                    mids[key] = nid
                    nodes[nid] = (nodes[t[a]] + nodes[t[b]]) / 2
                ids.append(mids[key])
        raw += [e] + ids + [-1] * (10 - len(ids)) + [0, 0, 0, 1, 1]
    for e, ids in extra or []:
        raw += [e] + ids + [-1] * (10 - len(ids)) + [0, 0, 0, 1, 1]

    def at(pred):
        return [n for n, p in nodes.items() if pred(p)]
    return {"nodes": [[k, *v] for k, v in nodes.items()], "elements_raw": raw, "element_stride": 16,
            "material": {"E_mpa": 210000, "nu": 0.3}, "shells": 1,
            "shell_list": [{"name": "Лист", "thickness_mm": 2.0, "elements": [1, 2, 3, 4]}],
            "sets": {"FIX": at(lambda p: p[0] == 0), "TIP": at(lambda p: p[0] == 20), "ALL": list(nodes)}}


@pytest.mark.parametrize("quadratic", [True, False])
def test_shell_deck(tmp_path, quadratic):
    m = _shell_model(quadratic, flip_second=True)
    info = calculix.build_deck(m, ["FIX"], [{"set": "ALL", "force_n": [0, 0, -100]}, {"set": "TIP", "force_n": [0, 0, -10]}],
                               str(tmp_path / "job.inp"))
    assert info["element_type"] == "S6" and info["elements"] == 4 and info["flipped_to_one_side"] == 1
    assert info.get("midside_nodes_added") == (None if quadratic else 9)      # 9 edges: S3 is upgraded to S6
    assert info["shells"] == [{"elset": "SH1", "thickness_mm": 2.0, "elements": 4}]
    face, tip = info["loads"]
    assert face["sum_n"] == [0.0, 0.0, -100.0] and face["faces_area_mm2"] == pytest.approx(200.0)
    assert tip["sum_n"] == [0.0, 0.0, -10.0] and tip["faces_area_mm2"] == pytest.approx(10.0)   # the free edge, length 10
    text = open(info["deck"]).read()
    assert "*SHELL SECTION, ELSET=SH1, MATERIAL=MAT, OFFSET=0\n2\n" in text
    assert "FIX, 1, 6" in text and "*NODE FILE, OUTPUT=3D" in text and "*EL PRINT" not in text
    xyz = {}
    for line in text.split("*NODE, NSET=NALL\n")[1].split("*ELEMENT")[0].splitlines():
        n, *p = [float(x) for x in line.split(",")]
        xyz[int(n)] = np.array(p)
    normals = []
    for line in text.split("*ELEMENT, TYPE=S6, ELSET=SH1\n")[1].split("*")[0].splitlines():
        ids = [int(x) for x in line.split(",")][1:]
        c = [xyz[i] for i in ids[:3]]
        normals.append(np.cross(c[1] - c[0], c[2] - c[0])[2])
        for k, (a, b) in enumerate(calculix.S6_EDGES):                       # node 4 = midpoint of 1-2 …
            assert np.allclose(xyz[ids[3 + k]], (xyz[ids[a]] + xyz[ids[b]]) / 2)
    assert all(z > 0 for z in normals) or all(z < 0 for z in normals)          # one side
    fix_line = text.split("*NSET, NSET=FIX\n")[1].splitlines()[0]
    assert len(fix_line.split(",")) == 3                                        # corners 1, 4 + the midside node between


def test_shell_edge_load_shares():
    """Uniform line load on a 3-node edge: 1/6 · 2/3 · 1/6."""
    xyz = {1: np.array([0., 0, 0]), 2: np.array([10., 0, 0]), 3: np.array([0., 10, 0]),
           4: np.array([5., 0, 0]), 5: np.array([5., 5, 0]), 6: np.array([0., 5, 0])}
    loads, length = calculix.shell_nodal_loads([[1, 2, 3, 4, 5, 6]], xyz, {1, 2, 4}, [0, 0, -60])
    assert length == pytest.approx(10.0)
    assert {n: round(float(f[2]), 6) for n, f in loads.items()} == {1: -10.0, 4: -40.0, 2: -10.0}


def test_shell_deck_refusals(tmp_path):
    with pytest.raises(ValueError, match="mixes solids and sheet-metal shells"):
        calculix.build_deck(_shell_model(extra=[(9, [1, 2, 4, 3])]), ["FIX"], [], str(tmp_path / "a.inp"))
    loose = _shell_model()
    loose["nodes"] += [[201, 50, 0, 0], [202, 60, 0, 0], [203, 50, 10, 0]]
    loose["elements_raw"] += [9, 201, 202, 203] + [-1] * 7 + [0, 0, 0, 1, 1]
    with pytest.raises(ValueError, match=r"1 piece\(s\) of the mesh have no fixture"):
        calculix.build_deck(loose, ["FIX"], [], str(tmp_path / "b.inp"))
    no_t = _shell_model()
    no_t["shell_list"] = []
    with pytest.raises(ValueError, match="no shell thickness"):
        calculix.build_deck(no_t, ["FIX"], [], str(tmp_path / "c.inp"))


def test_parse_frd_stress(tmp_path):
    """Fixed columns: a negative value runs into the one before it."""
    frd = tmp_path / "job.frd"
    frd.write_text("    1C\n"
                   " -4  DISP        4    1\n"
                   " -1         1 9.00000E+02 0.00000E+00 0.00000E+00\n"
                   " -3\n"
                   " -4  STRESS      6    1\n"
                   " -5  SXX         1    4    1    1\n"
                   " -1         1 1.00000E+02-5.00000E+01 0.00000E+00 0.00000E+00 0.00000E+00 0.00000E+00\n"
                   " -1         2 1.00000E+01 0.00000E+00 0.00000E+00 0.00000E+00 0.00000E+00 0.00000E+00\n"
                   " -3\n")
    r = calculix.parse_frd_stress(str(frd))
    assert r == {"max_von_mises_mpa": pytest.approx(132.2876, abs=1e-4), "stress_node": 1, "stress_at": "nodes"}
