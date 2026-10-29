"""Flat-pattern DXF post-processing for sheet-metal production — plain ezdxf/numpy, no SolidWorks needed.

SolidWorks' flat-pattern export (ExportToDWG2) puts everything on layer 0 in colour 7; bend lines are LINEs in
colour 8 (verified on SW 2024: exactly one per bend). From that geometry:
  * classify()     — outer contour, inner cut-outs, open marking chains, bend lines;
  * apply_layers() — move them onto CUT_OUTER / CUT_INNER / BEND / MARK layers (colours BYLAYER) for laser CAM;
  * dfm_check()    — design-for-manufacturing checks: hole↔bend, hole↔edge, small holes, short flanges.
All lengths in mm (SW writes $INSUNITS=4)."""
import logging
import math

import ezdxf
import numpy as np
from ezdxf import path as dxfpath

logging.getLogger("ezdxf").setLevel(logging.WARNING)   # "creating ACAD_COLOR dictionary" on every save

BEND_COLOR = 8
LAYERS = {   # name: (ACI colour, linetype)
    "CUT_OUTER": (7, "CONTINUOUS"),
    "CUT_INNER": (1, "CONTINUOUS"),
    "BEND": (3, "DASHED"),
    "MARK": (5, "CONTINUOUS"),
    "NOTE": (2, "CONTINUOUS"),
}
_CURVES = {"LINE", "ARC", "CIRCLE", "ELLIPSE", "SPLINE", "LWPOLYLINE", "POLYLINE"}
_KEY = 100.0          # endpoint snapping: 0.01 mm
_FLAT = 0.05          # curve flattening sagitta, mm
_TOL = 0.01           # a value at the limit is not a violation (a Ø3 hole fitted from a tessellation is 2.99998)


def _is_bend(e) -> bool:
    if e.dxftype() != "LINE":
        return False
    if e.dxf.layer.upper().startswith("BEND"):
        return True
    lt = e.dxf.get("linetype", "").upper()
    return e.dxf.get("color", 256) == BEND_COLOR or any(w in lt for w in ("DASH", "CENTER", "PHANTOM", "HIDDEN"))


class Group:
    """Connected chain of curves (shared end points)."""

    def __init__(self, items):
        self.entities = [e for e, _, _ in items]
        self.paths = [p for _, p, _ in items]
        self.segs = np.concatenate([np.stack([pts[:-1], pts[1:]], axis=1) for _, _, pts in items if len(pts) > 1])
        allpts = np.concatenate([pts for _, _, pts in items])
        self.bbox = (*allpts.min(axis=0), *allpts.max(axis=0))
        self.closed = False
        self.circle_d = None
        if len(items) == 1 and self.entities[0].dxftype() == "CIRCLE":
            self.circle_d = 2 * self.entities[0].dxf.radius

    @property
    def size(self):
        return self.bbox[2] - self.bbox[0], self.bbox[3] - self.bbox[1]

    @property
    def center(self):
        return [round((self.bbox[0] + self.bbox[2]) / 2, 2), round((self.bbox[1] + self.bbox[3]) / 2, 2)]

    @property
    def points(self):
        return np.concatenate([self.segs[:, 0], self.segs[-1:, 1]])


def classify(msp):
    """-> dict(outer=Group|None, inner=[Group], marks=[Group], bends=[(entity, p0, p1)])."""
    bends, shapes = [], []
    for e in msp:
        if e.dxftype() not in _CURVES:
            continue
        if _is_bend(e):
            bends.append((e, np.array(e.dxf.start)[:2], np.array(e.dxf.end)[:2]))
            continue
        try:
            p = dxfpath.make_path(e)
            pts = np.array([(v.x, v.y) for v in p.flattening(_FLAT)])
        except Exception:  # noqa: BLE001 — exotic entity: ignore for classification
            continue
        if len(pts):
            shapes.append((e, p, pts))

    parent = list(range(len(shapes)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    first, degree, self_closed = {}, {}, set()
    for i, (e, p, pts) in enumerate(shapes):
        if p.is_closed or math.hypot(p.start.x - p.end.x, p.start.y - p.end.y) < 1 / _KEY:
            self_closed.add(i)
            continue
        for v in (p.start, p.end):
            k = (round(v.x * _KEY), round(v.y * _KEY))
            degree[k] = degree.get(k, 0) + 1
            if k in first:
                parent[find(i)] = find(first[k])
            else:
                first[k] = i
    comps = {}
    for i in range(len(shapes)):
        comps.setdefault(find(i), []).append(i)
    groups = []
    for idx in comps.values():
        g = Group([shapes[i] for i in idx])
        if len(idx) == 1 and idx[0] in self_closed:
            g.closed = True
        else:
            keys = [(round(v.x * _KEY), round(v.y * _KEY)) for i in idx for v in (shapes[i][1].start, shapes[i][1].end)]
            g.closed = all(degree.get(k, 0) % 2 == 0 for k in keys)
        groups.append(g)
    area = lambda g: g.size[0] * g.size[1]  # noqa: E731
    closed = [g for g in groups if g.closed]
    outer = max(closed or groups, key=area, default=None)
    inner = [g for g in closed if g is not outer]
    marks = [g for g in groups if not g.closed and g is not outer]
    return {"outer": outer, "inner": inner, "marks": marks, "bends": bends}


def _ensure_layers(doc):
    if "DASHED" not in doc.linetypes:
        doc.linetypes.add("DASHED", pattern=[8.0, 5.0, -3.0], description="Dashed __ __ __")
    for name, (color, lt) in LAYERS.items():
        if name not in doc.layers:
            doc.layers.add(name, color=color, linetype=lt)


def _put(e, layer):
    e.dxf.layer = layer
    e.dxf.color = 256          # BYLAYER
    e.dxf.linetype = "BYLAYER"


def apply_layers(file_path: str, note: str = "") -> dict:
    """Re-layer a SolidWorks flat-pattern DXF in place; `note` adds a text line under the part on layer NOTE
    (not a cut layer). Returns entity counts per layer."""
    doc = ezdxf.readfile(file_path)
    msp = doc.modelspace()
    c = classify(msp)
    _ensure_layers(doc)
    counts = {k: 0 for k in ("CUT_OUTER", "CUT_INNER", "BEND", "MARK")}
    for layer, groups in (("CUT_OUTER", [c["outer"]] if c["outer"] else []), ("CUT_INNER", c["inner"]),
                          ("MARK", c["marks"])):
        for g in groups:
            for e in g.entities:
                _put(e, layer)
                counts[layer] += 1
    for e, _, _ in c["bends"]:
        _put(e, "BEND")
        counts["BEND"] += 1
    if note and c["outer"]:
        x0, y0, x1, _ = c["outer"].bbox
        h = max(2.5, min(10.0, (x1 - x0) / 40))
        msp.add_text(note, height=h, dxfattribs={"layer": "NOTE"}).set_placement((x0, y0 - 2 * h))
    doc.saveas(file_path)
    return counts


# ---------------------------------------------------------------- geometry helpers
def _pt_seg_dist(pts, segs):
    """min distance from each point (n,2) to the segment set (m,2,2) -> (n,); chunked to bound memory."""
    a, b = segs[:, 0][None], segs[:, 1][None]
    ab = b - a
    ab2 = np.maximum((ab * ab).sum(-1), 1e-18)
    step = max(1, 2_000_000 // max(len(segs), 1))
    out = []
    for i in range(0, len(pts), step):
        p = pts[i:i + step, None]
        t = np.clip(((p - a) * ab).sum(-1) / ab2, 0, 1)
        d = p - (a + t[..., None] * ab)
        out.append(np.sqrt((d * d).sum(-1)).min(axis=1))
    return np.concatenate(out)


def _crosses(points, p0, p1):
    """True if the closed chain `points` straddles segment p0-p1 (within its length)."""
    u = p1 - p0
    L = np.hypot(*u)
    if L < 1e-9:
        return False
    u = u / L
    n = np.array([-u[1], u[0]])
    rel = points - p0
    s, along = rel @ n, rel @ u
    inside = (along > 0) & (along < L)
    return bool(inside.any() and (s[inside] > 0).any() and (s[inside] < 0).any())


def group_distance(g: Group, segs) -> float:
    return float(min(_pt_seg_dist(g.points, segs).min(), _pt_seg_dist(segs.reshape(-1, 2), g.segs).min()))


def _ray_hit(o, d, segs):
    """distance along ray o + λd (|d| = 1) to the first segment hit, inf if none."""
    a, b = segs[:, 0], segs[:, 1]
    e = b - a
    denom = d[0] * e[:, 1] - d[1] * e[:, 0]
    ao = a - o
    with np.errstate(divide="ignore", invalid="ignore"):
        lam = (ao[:, 0] * e[:, 1] - ao[:, 1] * e[:, 0]) / denom
        mu = (ao[:, 0] * d[1] - ao[:, 1] * d[0]) / denom
    ok = (np.abs(denom) > 1e-12) & (lam > 1e-6) & (mu >= -1e-9) & (mu <= 1 + 1e-9)
    return float(lam[ok].min()) if ok.any() else math.inf


# ---------------------------------------------------------------- DFM
def default_rules(t: float, r: float) -> dict:
    return {"min_flange_mm": 4.0 * t, "hole_to_bend_mm": 2.5 * t + r, "hole_to_edge_mm": 1.0 * t, "min_hole_mm": 1.0 * t}


def _issue(rule, value, limit, at, **kw) -> dict:
    return {"rule": rule, "value_mm": round(value, 2), "limit_mm": round(limit, 2), "at": at, **kw}


def _cutout_issues(inner, outer, bends, lim) -> list:
    """small_hole, hole_to_edge and hole_to_bend of every cut-out, in that order per cut-out."""
    issues = []
    for h in inner:
        w, hh = h.size
        d = h.circle_d if h.circle_d else min(w, hh)
        if d < lim["min_hole_mm"] - _TOL:
            issues.append(_issue("small_hole", d, lim["min_hole_mm"], h.center))
        if outer is not None:
            de = group_distance(h, outer.segs)
            if de < lim["hole_to_edge_mm"] - _TOL:
                issues.append(_issue("hole_to_edge", de, lim["hole_to_edge_mm"], h.center))
        for bi, (_, p0, p1) in enumerate(bends):
            seg = np.array([[p0, p1]])
            db = 0.0 if _crosses(h.points, p0, p1) else group_distance(h, seg)
            if db < lim["hole_to_bend_mm"] - _TOL:
                issues.append(_issue("hole_to_bend", db, lim["hole_to_bend_mm"], h.center, bend=bi + 1))
    return issues


def _flange_hit(p0, p1, d, outer_segs, others):
    """Rays from ¼, ½ and ¾ of the bend line p0-p1 in direction d to the outer contour or another bend: the median
    (flat length, whether it ends at a bend), or None when no ray hits anything."""
    hits = []
    for s in (0.25, 0.5, 0.75):
        o = p0 + s * (p1 - p0)
        d_out = _ray_hit(o, d, outer_segs)
        d_bend = _ray_hit(o, d, others) if len(others) else math.inf
        hits.append((min(d_out, d_bend), d_bend < d_out))
    hits = sorted(x for x in hits if math.isfinite(x[0]))
    return hits[len(hits) // 2] if hits else None


def _bend_flanges(outer, bends, t, r, k, min_flange) -> tuple:
    """Both sides of every bend: flat and estimated outside (90°) flange length → (bend_flanges, short_flange
    issues). A leg ending at another bend loses a bend allowance and gains an outside offset at each end."""
    ba_half = math.pi / 4 * (r + k * t)
    bend_segs = np.array([[p0, p1] for _, p0, p1 in bends])
    info, issues = [], []
    for bi, (_, p0, p1) in enumerate(bends):
        u = (p1 - p0) / max(np.hypot(*(p1 - p0)), 1e-12)
        n = np.array([-u[1], u[0]])
        others = np.delete(bend_segs, bi, axis=0)
        sides = []
        for sign in (1, -1):
            hit = _flange_hit(p0, p1, sign * n, outer.segs, others)
            if hit is None:
                continue
            flat, to_bend = hit
            straight = flat - ba_half * (2 if to_bend else 1)
            outside = straight + (r + t) * (2 if to_bend else 1)
            sides.append({"flat_mm": round(flat, 2), "outside_mm": round(outside, 2), "to_bend": to_bend})
            if outside < min_flange:
                mid = p0 + 0.5 * (p1 - p0) + sign * n * flat / 2
                issues.append(_issue("short_flange", outside, min_flange, [round(mid[0], 2), round(mid[1], 2)],
                                     bend=bi + 1))
        info.append({"bend": bi + 1, "length_mm": round(float(np.hypot(*(p1 - p0))), 2), "sides": sides})
    return info, issues


def _merge_shared_legs(issues) -> list:
    """The leg between two bends is seen from both of them: one issue listing both bends."""
    seen, uniq = {}, []
    for i in issues:
        key = (i["rule"], tuple(i["at"]), None if i["rule"] == "short_flange" else i.get("bend"))
        if key in seen:
            prev = seen[key]
            prev["bends"] = sorted(set(prev.pop("bends", [prev.pop("bend", None)]) + [i.get("bend")]) - {None})
            continue
        seen[key] = i
        uniq.append(i)
    return uniq


def _circle(pts: np.ndarray):
    """(center, radius) when the closed loop of points is a circle (least squares, within 0.005 mm), else None."""
    if len(pts) < 8:
        return None
    x, y = pts[:, 0], pts[:, 1]
    a = np.column_stack([x, y, np.ones_like(x)])
    sol, *_ = np.linalg.lstsq(a, -(x * x + y * y), rcond=None)
    cx, cy = -sol[0] / 2, -sol[1] / 2
    rr = math.sqrt(max(cx * cx + cy * cy - sol[2], 0.0))
    dev = np.abs(np.hypot(x - cx, y - cy) - rr).max()
    return ((cx, cy), rr) if rr > 0 and dev < max(0.005, 0.002 * rr) else None


def _simplify(pts: np.ndarray) -> list:
    """Drop the points that lie on the straight line between their neighbours (tessellation splits long edges)."""
    keep = []
    n = len(pts)
    for i in range(n):
        p0, p1, p2 = pts[i - 1], pts[i], pts[(i + 1) % n]
        d = p2 - p0
        L = math.hypot(d[0], d[1])
        if L > 0 and abs(d[0] * (p1[1] - p0[1]) - d[1] * (p1[0] - p0[0])) / L < 1e-4:
            continue
        keep.append((float(p1[0]), float(p1[1])))
    return keep if len(keep) >= 3 else [(float(p[0]), float(p[1])) for p in pts]


def doc_from_flat(part: dict):
    """An ezdxf document of one flat_geometry part (read from the model, no DXF export): the outline loops (circles
    recognised, the rest closed polylines) or exact entities, and the bend lines in colour 8 like SolidWorks' export —
    what dfm_check reads from an exported file."""
    doc = ezdxf.new("R2010")
    doc.header["$INSUNITS"] = 4
    msp = doc.modelspace()
    for loop in part.get("loops") or []:
        pts = np.array(loop, dtype=float)
        if len(pts) > 1 and np.allclose(pts[0], pts[-1]):
            pts = pts[:-1]
        c = _circle(pts)
        if c:
            msp.add_circle(c[0], c[1])
        else:
            msp.add_lwpolyline(_simplify(pts), close=True)
    for e in part.get("entities") or []:
        if e["t"] == "line":
            msp.add_line(e["a"], e["b"])
        elif e["t"] == "circle":
            msp.add_circle(e["c"], e["r"])
        elif e["t"] == "arc":
            msp.add_arc(e["c"], e["r"], e["a0"], e["a1"])
        elif len(e.get("pts") or []) > 1:
            msp.add_lwpolyline(e["pts"])
    for b in part.get("bend_lines") or []:
        msp.add_line(b[:2], b[2:], dxfattribs={"color": BEND_COLOR})
    return doc


def dfm_check(file_path: str, t: float, r: float, k: float = 0.5, rules: dict = None, details: bool = False) -> dict:
    """dfm_check_doc of an exported flat-pattern DXF."""
    return dfm_check_doc(ezdxf.readfile(file_path), t, r, k, rules, details)


def dfm_check_doc(doc, t: float, r: float, k: float = 0.5, rules: dict = None, details: bool = False) -> dict:
    """Checks on one flat pattern. t thickness, r inside bend radius, k K-factor (all mm).
    rules override default_rules(). Bend allowance is estimated for 90° bends: BA = π/2·(r + k·t).
      hole_to_bend  — flat distance from a cut-out edge to a bend line < limit (0 = the cut-out crosses the bend);
      hole_to_edge  — cut-out edge to the outer contour < limit;
      small_hole    — hole diameter (or the narrow side of a slot) < limit;
      short_flange  — estimated OUTSIDE flange length (from the bend, 90°) < limit; between two bends the
                      outside length of the middle leg counts both bends."""
    lim = default_rules(t, r)
    lim.update({k_: v for k_, v in (rules or {}).items() if v})   # a rule of 0 / None keeps the default
    c = classify(doc.modelspace())
    outer, inner, bends = c["outer"], c["inner"], c["bends"]
    issues, bend_info = _cutout_issues(inner, outer, bends, lim), []
    if outer is not None and bends:
        bend_info, short = _bend_flanges(outer, bends, t, r, k, lim["min_flange_mm"])
        issues += short
    res = {"bends": len(bends), "cutouts": len(inner), "issues": _merge_shared_legs(issues),
           "limits_mm": {k_: round(v, 2) for k_, v in lim.items()}}
    if outer is not None:
        res["flat_mm"] = [round(outer.size[0], 2), round(outer.size[1], 2)]
    if details:
        res["bend_flanges"] = bend_info
    return res
