"""Face-by-face comparison of two analyze_model(faces) lists — the geometry diff behind compare_parts.
Pure Python (no MCP, no SolidWorks)."""


FACE_POS_TOL = 5e-5   # 50µm — supporting plane offset / point match
FACE_RAD_TOL = 5e-5   # 50µm — cylinder radius match


def faces_compatible(fa: dict, fb: dict) -> bool:
    """True if two faces (from analyze_model(faces) JSON) are the SAME SURFACE within tolerance —
    matched by supporting geometry, not by trim/area. Planar → parallel normal (either sign) AND the same
    plane offset (±50µm). Cylinder → parallel axis on the same LINE (±50µm) AND same radius (±50µm).
    Other → same kind AND representative point within 50µm. This is deliberately looser than the
    <=1% verified gate: a face-DIFF looks for surfaces one part has and the other doesn't, so
    sub-µm/µm parameter drift on a shared surface must PAIR, leaving only true add/drops unmatched."""
    ka, kb = face_cat(fa), face_cat(fb)
    if ka != kb:
        return False
    if ka == "plane":
        na, oa = _unit(fa["normal"]), _dot(_unit(fa["normal"]), fa["point"])
        nb, ob = _unit(fb["normal"]), _dot(_unit(fb["normal"]), fb["point"])
        dot = _dot(na, nb)
        # the same plane with the normal either way round (a face's normal points out of ITS body)
        return abs(dot) > 0.999 and abs(oa - (ob if dot > 0 else -ob)) <= FACE_POS_TOL
    if ka == "cyl":
        aa, ab = _unit(fa["axis"]), _unit(fb["axis"])
        if abs(_dot(aa, ab)) <= 0.999 or abs(float(fa.get("radius", 0)) - float(fb.get("radius", 0))) > FACE_RAD_TOL:
            return False
        oa, ob = fa.get("origin"), fb.get("origin")
        # the same axis LINE, not just a parallel one — a hole that moved is a change
        return not (isinstance(oa, list) and isinstance(ob, list)) or _line_dist(ob, oa, aa) <= FACE_POS_TOL
    pa, pb = fa.get("point"), fb.get("point")
    if isinstance(pa, list) and isinstance(pb, list):
        return all(abs(float(x) - float(y)) <= FACE_POS_TOL for x, y in zip(pa, pb))
    return False


def _dot(a, b) -> float:
    return sum(float(x) * float(y) for x, y in zip(a, b))


def _unit(v) -> list:
    n = _dot(v, v) ** 0.5 or 1.0
    return [float(x) / n for x in v]


def _line_dist(p, origin, axis_unit) -> float:
    """Distance from point p to the line through origin along axis_unit."""
    d = [float(a) - float(b) for a, b in zip(p, origin)]
    t = _dot(d, axis_unit)
    return sum((x - t * a) ** 2 for x, a in zip(d, axis_unit)) ** 0.5


def face_cat(f: dict) -> str:
    if f.get("planar") and isinstance(f.get("normal"), list) and isinstance(f.get("point"), list):
        return "plane"
    if f.get("kind") == "cylinder" and isinstance(f.get("axis"), list):
        return "cyl"
    return f.get("kind", "surf")


def diff_faces(faces_a: list, faces_b: list):
    """Greedy tolerance set-difference of two face lists. Returns (missing, extra): missing = faces
    in A that pair with no face in B; extra = faces in B that pair with none in A. Same-surface faces
    pair through sub-µm/µm parameter drift (faces_compatible), so only genuine add/drops remain."""
    used_b = [False] * len(faces_b)
    missing = []
    for fa in faces_a:
        hit = -1
        for j, fb in enumerate(faces_b):
            if not used_b[j] and faces_compatible(fa, fb):
                hit = j
                break
        if hit >= 0:
            used_b[hit] = True
        else:
            missing.append(fa)
    extra = [fb for j, fb in enumerate(faces_b) if not used_b[j]]
    return missing, extra


def face_desc(f: dict) -> str:
    """Compact human description of a face for the diff report."""
    i = f.get("i")
    if f.get("planar"):
        n = f.get("normal")
        area = f.get("area")
        nstr = ("[" + ",".join(format(float(x), ".3g") for x in n) + "]") if isinstance(n, list) else "?"
        return f"i={i} plane n={nstr} area={format(float(area), '.4g') if isinstance(area, (int, float)) else '?'}"
    if f.get("kind") == "cylinder":
        return f"i={i} cyl r={format(float(f.get('radius', 0)), '.4g')}"
    return f"i={i} {f.get('kind', 'surf')}"
