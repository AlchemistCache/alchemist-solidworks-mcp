"""Independent cross-check of a SOLIDWORKS Simulation study with CalculiX (ccx).

Approach and the evidence vocabulary (pass / warn / blocked / failed, never a fabricated result) follow
wzyn20051216/solidworks-automation-skill scripts/fea_analysis.py (MIT); the code here is our own.

Input = the JSON written by sim_export_mesh (nodes in mm, SW elements: 16 numbers each — id, 10 node ids
(corners first, -1 for unused), centroid, 2 quality values — node sets from faces, material E / ν, and for sheet
metal the shells: thickness and element ids each).
  build_deck()   — solids: C3D4 / C3D10; sheet metal (mid-surface shells): S3 / S6 with *SHELL SECTION per shell.
                   Midside nodes are matched to corner pairs GEOMETRICALLY (independent of the SW numbering), tets
                   re-oriented to positive volume, shell triangles to one consistent side (CalculiX averages the
                   normals at a node to expand a shell — opposite ones cancel). Fixed sets → *BOUNDARY (shells: the
                   rotations too, as SolidWorks' "fixed"), face loads → consistent nodal forces (uniform traction on
                   the element faces / shell triangles; on a shell's edge when the face is a sheet's side). A piece of
                   the mesh without a fixture (parts joined only by contacts, which the deck does not carry) is refused.
  find_ccx()     — SWMCP_CCX, PATH, the usual install folders (PrePoMax, bConverged, FreeCAD).
  parse_dat()    — max displacement and max von Mises (integration points) from the .dat file.
  parse_frd_stress() — max NODAL von Mises from the .frd (shells: the expanded top / bottom surface nodes — what
                   SolidWorks reports for a shell; integration points lie inside the thickness).
  compare()      — evidence: SW vs CalculiX displacement / stress ratios.
Units: mm, N, MPa."""
import config
import glob
import math
import os
import re
import shutil
import subprocess

import numpy as np

# CalculiX C3D10 edge order of the midside nodes 5..10 (corner pairs, 0-based)
C3D10_EDGES = [(0, 1), (1, 2), (2, 0), (0, 3), (1, 3), (2, 3)]
# faces of C3D4/C3D10: corner triple (0-based) and their midside nodes (0-based positions 4..9)
TET_FACES = [((0, 1, 2), (4, 5, 6)), ((0, 3, 1), (7, 8, 4)), ((1, 3, 2), (8, 9, 5)), ((2, 3, 0), (9, 7, 6))]


# S6 midside nodes 4..6 on the corner pairs (0-based)
S6_EDGES = [(0, 1), (1, 2), (2, 0)]


def _elements(model):
    """(id, node ids) of every element record: 4 / 10 nodes = tetrahedron, 3 / 6 = shell triangle."""
    stride = model.get("element_stride", 16)
    raw = model["elements_raw"]
    for i in range(0, len(raw) - stride + 1, stride):
        rec = raw[i:i + stride]
        ids = [int(x) for x in rec[1:11] if x >= 0]
        if len(ids) in (3, 4, 6, 10):
            yield int(rec[0]), ids


def _tets(model):
    return ((eid, ids) for eid, ids in _elements(model) if len(ids) in (4, 10))


def order_triangle(ids, xyz):
    """Corners as given, midside nodes in CalculiX S6 order (matched to the nearest edge midpoint)."""
    c = list(ids[:3])
    if len(ids) == 3:
        return c
    mids = list(ids[3:])
    out = []
    for a, b in S6_EDGES:
        m = (xyz[c[a]] + xyz[c[b]]) / 2
        best = min(mids, key=lambda n: float(np.sum((xyz[n] - m) ** 2)))
        out.append(best)
        mids.remove(best)
    return c + out


def _flip(tri):
    """The same triangle seen from the other side: corners 1 ↔ 2, midside nodes follow their edges."""
    return [tri[0], tri[2], tri[1]] + ([tri[5], tri[4], tri[3]] if len(tri) == 6 else [])


def _edges(tri):
    c = tri[:3]
    return [(c[0], c[1]), (c[1], c[2]), (c[2], c[0])]


def orient_shells(tris: list) -> int:
    """Turn shell triangles (lists, changed in place) so that neighbours run their common edge in opposite
    directions — one consistent side per connected sheet. An edge shared by 3+ triangles (a T-joint) is not a
    neighbour link. Returns how many were flipped."""
    by_edge = {}
    for k, t in enumerate(tris):
        for a, b in _edges(t):
            by_edge.setdefault(frozenset((a, b)), []).append(k)
    seen, flips = [False] * len(tris), 0
    for start in range(len(tris)):
        if seen[start]:
            continue
        seen[start], stack = True, [start]
        while stack:
            k = stack.pop()
            for a, b in _edges(tris[k]):
                link = by_edge[frozenset((a, b))]
                if len(link) != 2:
                    continue
                j = link[0] if link[1] == k else link[1]
                if seen[j]:
                    continue
                if (a, b) in _edges(tris[j]):          # runs the edge the same way → the other side
                    tris[j][:] = _flip(tris[j])
                    flips += 1
                seen[j] = True
                stack.append(j)
    return flips


def add_midside_nodes(tris: list, xyz: dict, sets: dict) -> int:
    """Linear shell triangles → 6-node ones on the same corners (lists changed in place): a midside node per edge at
    its midpoint; a set gets the midside node when it holds both ends. CalculiX's S3 locks in bending (an L-bracket:
    0.71 of SolidWorks' displacement, 0.40 of its stress, where SolidWorks' own linear shell agreed with its
    quadratic one). Returns how many nodes were added."""
    nid = max(xyz) + 1
    mids = {}
    for t in tris:
        new = []
        for a, b in _edges(t):
            key = frozenset((a, b))
            if key not in mids:
                mids[key] = nid
                xyz[nid] = (xyz[a] + xyz[b]) / 2
                nid += 1
            new.append(mids[key])
        t += new
    for name, ids in sets.items():
        have = set(ids)
        ids += sorted(m for key, m in mids.items() if key <= have)
    return len(mids)


def unfixed_pieces(elements, fixed_nodes) -> int:
    """How many connected pieces of the mesh (elements sharing nodes) have no fixed node — each would float."""
    parent = {}

    def root(n):
        while parent.setdefault(n, n) != n:
            parent[n] = parent[parent[n]]
            n = parent[n]
        return n
    for _, nodes in elements:
        r = root(nodes[0])
        for n in nodes[1:]:
            parent[root(n)] = r
    fixed_roots = {root(n) for n in fixed_nodes if n in parent}
    return len({root(n) for n in parent} - fixed_roots)


def shell_nodal_loads(tris, xyz, load_set, force):
    """Total force (N, vector) over the shell triangles whose corners all lie in load_set (share ∝ area; S3 → 1/3
    per corner, S6 → 1/3 per midside node) or, when the set covers no triangle (a sheet's side face = the shell's
    edge), along the free edges in it (share ∝ length; 2 nodes 1/2 each, 3 nodes 1/6 · 2/3 · 1/6)."""
    load_set = set(load_set)
    parts = []
    for t in tris:
        if all(n in load_set for n in t[:3]):
            a = 0.5 * np.linalg.norm(np.cross(xyz[t[1]] - xyz[t[0]], xyz[t[2]] - xyz[t[0]]))
            parts.append((a, [(n, 1 / 3) for n in (t[3:] if len(t) == 6 else t[:3])]))
    if not parts:
        count = {}
        for t in tris:
            for k, (a, b) in enumerate(_edges(t)):
                count.setdefault(frozenset((a, b)), []).append((t, k))
        for key, users in count.items():
            if len(users) != 1 or not all(n in load_set for n in key):
                continue
            t, k = users[0]
            a, b = _edges(t)[k]
            length = float(np.linalg.norm(xyz[b] - xyz[a]))
            share = [(a, 0.5), (b, 0.5)] if len(t) == 3 else [(a, 1 / 6), (t[3 + k], 2 / 3), (b, 1 / 6)]
            parts.append((length, share))
    total = sum(w for w, _ in parts)
    if total <= 0:
        return {}, 0.0
    f = np.asarray(force, dtype=float)
    out = {}
    for w, share in parts:
        for n, s in share:
            out[n] = out.get(n, np.zeros(3)) + f * (w / total) * s
    return out, total


def order_element(ids, xyz):
    """Corners first with positive volume, midside nodes in CalculiX order (matched to the nearest edge midpoint)."""
    c = list(ids[:4])
    p = [xyz[n] for n in c]
    if np.dot(np.cross(p[1] - p[0], p[2] - p[0]), p[3] - p[0]) < 0:
        c[1], c[2] = c[2], c[1]
    if len(ids) == 4:
        return c
    mids = list(ids[4:])
    out = []
    for a, b in C3D10_EDGES:
        m = (xyz[c[a]] + xyz[c[b]]) / 2
        best = min(mids, key=lambda n: float(np.sum((xyz[n] - m) ** 2)))
        out.append(best)
        mids.remove(best)
    return c + out


# C3D10 shape-function derivatives (d/dr, d/ds, d/dt) at the four Gauss points, CalculiX node order
def _c3d10_dn(r, s, t):
    u = 1 - r - s - t
    return np.array([[1 - 4 * u] * 3, [4 * r - 1, 0, 0], [0, 4 * s - 1, 0], [0, 0, 4 * t - 1],
                     [4 * (u - r), -4 * r, -4 * r], [4 * s, 4 * r, 0], [-4 * s, 4 * (u - s), -4 * s],
                     [-4 * t, -4 * t, 4 * (u - t)], [4 * t, 0, 4 * r], [0, 4 * t, 4 * s]], dtype=float)


_GA, _GB = 0.1381966011250105, 0.5854101966249685
_C3D10_DN = [_c3d10_dn(*g) for g in ((_GA, _GA, _GA), (_GB, _GA, _GA), (_GA, _GB, _GA), (_GA, _GA, _GB))]


def straighten_bad(tets, xyz, passes: int = 3) -> int:
    """C3D10 elements whose Jacobian is not positive at every integration point — a sliver under raised text, a
    midside node bowed by a curved face: SolidWorks solves them, CalculiX stops ("nonpositive jacobian"). Their
    midside nodes go to the edge midpoints, which leaves the corner tetrahedron's positive volume. Returns how many
    elements were straightened."""
    quad = [(eid, n) for eid, n in tets if len(n) == 10]
    if not quad:
        return 0
    done = set()
    for _ in range(passes):
        P = np.array([[xyz[k] for k in n] for _, n in quad])
        bad = np.zeros(len(quad), dtype=bool)
        for d in _C3D10_DN:
            bad |= np.linalg.det(np.einsum("enx,ny->exy", P, d)) <= 0
        if not bad.any():
            break
        for i in np.where(bad)[0]:
            eid, n = quad[i]
            done.add(eid)
            for (a, b), m in zip(C3D10_EDGES, n[4:]):
                xyz[m] = (xyz[n[a]] + xyz[n[b]]) / 2
    return len(done)


def nodal_loads(elements, xyz, load_set, force):
    """Total force (N, vector) as a uniform traction over the element faces whose nodes all lie in load_set:
    share ∝ face area; linear face → 1/3 per corner, quadratic face → 1/3 per midside node (corners 0)."""
    load_set = set(load_set)
    faces = []
    for _, nodes in elements:
        for corners, mids in TET_FACES:
            cn = [nodes[k] for k in corners]
            if all(n in load_set for n in cn):
                a = 0.5 * np.linalg.norm(np.cross(xyz[cn[1]] - xyz[cn[0]], xyz[cn[2]] - xyz[cn[0]]))
                carriers = [nodes[k] for k in mids] if len(nodes) == 10 else cn
                faces.append((a, carriers))
    total = sum(a for a, _ in faces)
    if total <= 0:
        return {}, 0.0
    f = np.asarray(force, dtype=float)
    out = {}
    for a, carriers in faces:
        for n in carriers:
            out[n] = out.get(n, np.zeros(3)) + f * (a / total) / 3
    return out, total


def _element_cards(kind, elset, elements):
    lines = [f"*ELEMENT, TYPE={kind}, ELSET={elset}"]
    for eid, nodes in elements:
        row = [str(eid)] + [str(n) for n in nodes]
        # CalculiX continues an element card after 16 entries per line
        lines.append(", ".join(row[:16]) + ("," if len(row) > 16 else ""))
        if len(row) > 16:
            lines.append(", ".join(row[16:]))
    return lines


def build_deck(model: dict, fixed_sets: list, loads: list, path: str) -> dict:
    """loads: [{"set": name, "force_n": [fx, fy, fz]}]. Returns a summary; raises ValueError on bad input."""
    mat = model.get("material") or {}
    if not mat.get("E_mpa"):
        raise ValueError("no material constants in the export (assign a material in the study)")
    xyz = {int(n[0]): np.array(n[1:4], dtype=float) for n in model["nodes"]}
    raw = list(_elements(model))
    tets = [(eid, order_element(ids, xyz)) for eid, ids in raw if len(ids) in (4, 10)]
    tris = [(eid, order_triangle(ids, xyz)) for eid, ids in raw if len(ids) in (3, 6)]
    if tets and tris:
        raise ValueError("the mesh mixes solids and sheet-metal shells — their bonded joints are not in the deck; "
                         "cross-check a solid-only or a shell-only study")
    if not tets and not tris:
        raise ValueError("no tetrahedral or shell elements in the export")
    shell = bool(tris)
    elements = tris or tets
    sets = {k: [int(n) for n in v] for k, v in (model.get("sets") or {}).items()}
    for name in fixed_sets:
        if not sets.get(name):
            raise ValueError(f"node set '{name}' is empty")
    floating = unfixed_pieces(elements, {n for name in fixed_sets for n in sets[name]})
    if floating:
        raise ValueError(f"{floating} piece(s) of the mesh have no fixture — parts joined only by contacts (the deck "
                         "carries no contacts) or a body with nothing holding it")
    info = {}
    if not shell:
        bent = straighten_bad(tets, xyz)
        if bent:
            info["straightened_elements"] = bent
    if shell and len(tris[0][1]) == 3:
        info["midside_nodes_added"] = add_midside_nodes([t for _, t in tris], xyz, sets)
    lines = ["** MCP cross-check deck (from SOLIDWORKS Simulation mesh) — units mm, N, MPa", "*NODE, NSET=NALL"]
    lines += [f"{n}, {p[0]:.6f}, {p[1]:.6f}, {p[2]:.6f}" for n, p in sorted(xyz.items())]
    if shell:
        kind = "S6"
        info["flipped_to_one_side"] = orient_shells([t for _, t in tris])
        # elements per shell (thickness); ones no shell claims go with the first
        groups = [(sh, set(sh.get("elements") or [])) for sh in model.get("shell_list") or [] if sh.get("thickness_mm")]
        if not groups:
            raise ValueError("no shell thickness in the export (shell_list) — export again with the current server")
        by = {k: [] for k in range(len(groups))}
        for eid, t in tris:
            by[next((k for k, (_, ids) in enumerate(groups) if eid in ids), 0)].append((eid, t))
        sections = []
        for k, (sh, _) in enumerate(groups):
            if by[k]:
                lines += _element_cards(kind, f"SH{k + 1}", by[k])
                sections += [f"*SHELL SECTION, ELSET=SH{k + 1}, MATERIAL=MAT, OFFSET=0", f"{sh['thickness_mm']:.6g}"]
        info["shells"] = [{"elset": f"SH{k + 1}", "thickness_mm": sh["thickness_mm"], "elements": len(by[k])}
                          for k, (sh, _) in enumerate(groups) if by[k]]
    else:
        kind = "C3D10" if len(tets[0][1]) == 10 else "C3D4"
        lines += _element_cards(kind, "EALL", tets)
        sections = ["*SOLID SECTION, ELSET=EALL, MATERIAL=MAT"]
    for name in fixed_sets:
        lines.append(f"*NSET, NSET={name}")
        lines += [", ".join(map(str, sets[name][i:i + 16])) for i in range(0, len(sets[name]), 16)]
    lines += ["*MATERIAL, NAME=MAT", "*ELASTIC", f"{mat['E_mpa']:.6g}, {mat.get('nu', 0.3):.4g}"] + sections
    # SolidWorks' "fixed" on a shell holds its rotations too
    lines += ["*STEP", "*STATIC", "*BOUNDARY"] + [f"{name}, 1, {6 if shell else 3}" for name in fixed_sets]
    load_info = []
    cl = []
    for ld in loads:
        if shell:
            nl, size = shell_nodal_loads([t for _, t in tris], xyz, sets.get(ld["set"], []), ld["force_n"])
        else:
            nl, size = nodal_loads(elements, xyz, sets.get(ld["set"], []), ld["force_n"])
        if not nl:
            raise ValueError(f"load set '{ld['set']}' covers no element face" + (" or shell edge" if shell else ""))
        load_info.append({"set": ld["set"], "faces_area_mm2": round(size, 2), "nodes": len(nl),
                          "sum_n": [round(float(x), 3) for x in sum(nl.values())]})
        for n, f in sorted(nl.items()):
            for dof in range(3):
                if abs(f[dof]) > 1e-12:
                    cl.append(f"{n}, {dof + 1}, {f[dof]:.6g}")
    lines += ["*CLOAD"] + cl
    lines += ["*NODE PRINT, NSET=NALL", "U"]
    # solids: integration-point stresses in the .dat; shells: nodal stresses of the expanded top / bottom nodes (.frd)
    lines += ["*NODE FILE, OUTPUT=3D", "U", "*EL FILE", "S"] if shell else ["*EL PRINT, ELSET=EALL", "S"]
    lines.append("*END STEP")
    with open(path, "w", encoding="ascii", errors="replace") as fh:
        fh.write("\n".join(lines) + "\n")
    return {"deck": path, "element_type": kind, "elements": len(elements), "nodes": len(xyz), "loads": load_info,
            "material": mat, **info}


def find_ccx():
    cands = [config.CCX_PATH]
    cands += [shutil.which(n) or "" for n in ("ccx", "ccx.exe", "ccx_static.exe")]
    for pat in (r"C:\Program Files\PrePoMax*\Solver\ccx*.exe", r"C:\PrePoMax*\Solver\ccx*.exe",
                r"C:\Program Files\bConverged\CalculiX\ccx\ccx*.exe", r"C:\Program Files\FreeCAD*\bin\ccx*.exe",
                r"C:\Tools\CalculiX\*\ccx_static.exe"):
        cands += sorted(glob.glob(pat))
    return next((c for c in cands if c and os.path.isfile(c)), None)


_NUM = re.compile(r"[-+]?\d*\.?\d+(?:[EeDd][-+]?\d+)?")


def parse_dat(path: str) -> dict:
    """Max |U| (node) and max von Mises over integration points from a CalculiX .dat (NODE PRINT U, EL PRINT S)."""
    mode, umax, unode, vmax, velem = None, 0.0, None, 0.0, None
    with open(path, encoding="ascii", errors="replace") as fh:
        for line in fh:
            low = line.strip().lower()
            if not low:
                continue
            if low.startswith("displacements"):
                mode = "u"
                continue
            if low.startswith("stresses"):
                mode = "s"
                continue
            if not low[0].isdigit():          # any other block header ends the current block
                mode = None
                continue
            v = [float(x.replace("D", "E").replace("d", "e")) for x in _NUM.findall(line)]
            if mode == "u" and len(v) >= 4:
                m = math.sqrt(v[1] ** 2 + v[2] ** 2 + v[3] ** 2)
                if m > umax:
                    umax, unode = m, int(v[0])
            elif mode == "s" and len(v) >= 8:
                sxx, syy, szz, sxy, sxz, syz = v[2:8]
                vm = math.sqrt(0.5 * ((sxx - syy) ** 2 + (syy - szz) ** 2 + (szz - sxx) ** 2) + 3 * (sxy ** 2 + sxz ** 2 + syz ** 2))
                if vm > vmax:
                    vmax, velem = vm, int(v[0])
    if unode is None and velem is None:
        raise ValueError("no displacement / stress blocks in the .dat file")
    return {"max_displacement_mm": round(umax, 6), "at_node": unode, "max_von_mises_mpa": round(vmax, 4), "in_element": velem}


def parse_frd_stress(path: str) -> dict:
    """Max nodal von Mises over the STRESS blocks of a CalculiX .frd (fixed columns: ' -1', node I10, values E12)."""
    vmax, vnode, mode = 0.0, None, False
    with open(path, encoding="ascii", errors="replace") as fh:
        for line in fh:
            head = line[:3]
            if head == " -4":
                mode = line[3:].split()[0].upper() == "STRESS"
            elif head == " -3":
                mode = False
            elif mode and head == " -1":
                v = [float(line[13 + 12 * i:25 + 12 * i]) for i in range(6)]
                sxx, syy, szz, sxy, syz, szx = v
                vm = math.sqrt(0.5 * ((sxx - syy) ** 2 + (syy - szz) ** 2 + (szz - sxx) ** 2) + 3 * (sxy ** 2 + syz ** 2 + szx ** 2))
                if vm > vmax:
                    vmax, vnode = vm, int(line[3:13])
    if vnode is None:
        raise ValueError("no STRESS block in the .frd file")
    return {"max_von_mises_mpa": round(vmax, 4), "stress_node": vnode, "stress_at": "nodes"}


def run_ccx(ccx: str, deck: str, timeout_s: int = 600) -> dict:
    job = os.path.splitext(os.path.basename(deck))[0]
    try:
        cp = subprocess.run([ccx, "-i", job], cwd=os.path.dirname(deck), capture_output=True, encoding="utf-8",
                            errors="replace", timeout=timeout_s, shell=False, check=False)
    except subprocess.TimeoutExpired:   # a solve that does not finish is a failed cross-check, not a crash
        return {"exit_code": None, "dat": None, "frd": None, "stdout_tail": f"ccx did not finish in {timeout_s} s",
                "stderr_tail": ""}
    dat = os.path.join(os.path.dirname(deck), job + ".dat")
    frd = os.path.join(os.path.dirname(deck), job + ".frd")
    return {"exit_code": cp.returncode, "dat": dat if os.path.isfile(dat) else None,
            "frd": frd if os.path.isfile(frd) else None, "stdout_tail": cp.stdout[-1500:], "stderr_tail": cp.stderr[-800:]}


def compare(sw: dict, ccx: dict, disp_tol: float = 0.15, stress_tol: float = 0.35) -> dict:
    """Evidence from the two solutions. SolidWorks reports NODAL von Mises (extrapolated); CalculiX here the
    integration-point maximum for solids — stress agrees less tightly than displacement by nature — and the nodal
    surface maximum for shells."""
    swd = (sw.get("displacement_mm") or {}).get("max")
    sws = (sw.get("von_mises_mpa") or {}).get("max")
    checks = []
    for name, a, b, tol in (("max_displacement", swd, ccx.get("max_displacement_mm"), disp_tol),
                            ("max_von_mises", sws, ccx.get("max_von_mises_mpa"), stress_tol)):
        if not a or not b:
            checks.append({"check": name, "status": "blocked", "reason": "value missing"})
            continue
        r = b / a
        checks.append({"check": name, "solidworks": a, "calculix": b, "ratio": round(r, 3),
                       "status": "pass" if abs(r - 1) <= tol else "warn"})
    status = "pass" if all(c["status"] == "pass" for c in checks) else (
        "blocked" if all(c["status"] == "blocked" for c in checks) else "warn")
    return {"status": status, "checks": checks,
            "note": "independent solver on the SAME mesh, supports and loads — agreement checks the setup and the "
                    "solution, not the model assumptions; engineering review still required"}
