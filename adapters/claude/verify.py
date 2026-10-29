"""The objective verdicts of compare_parts / compare_assemblies.
A part matches when its topology is EXACT and volume and area agree within 1 %; an assembly when its component set,
the component transforms, the mates and the mass agree. Pure Python — compare_parts / compare_assemblies feed it what
analyze_model / analyze_assembly read; doc A is the reference."""
import os

TOLERANCE_PCT = 1.0          # |dV| and |dA|, percent of the reference
TRANSFORM_TOL = 1e-6         # rotation-matrix elements; translation in meters (1 µm)
TOPOLOGY = ("bodies", "faces", "edges", "vertices")


def delta_pct(a, b):
    """(b − a) / a in percent; None when either is not a number or a is 0."""
    if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
        return None
    return (b - a) / a * 100.0



def _mass_ok(dv, da) -> bool:
    return dv is not None and da is not None and abs(dv) <= TOLERANCE_PCT and abs(da) <= TOLERANCE_PCT


def part_verdict(geo_a: dict, geo_b: dict, mass_a: dict, mass_b: dict) -> dict:
    """geo_* = analyze_model(geometry) and mass_* = the internal analyze_model mass_properties read (SI) as numbers (kv_dict)."""
    topo_a = [geo_a.get(k) for k in TOPOLOGY]
    topo_b = [geo_b.get(k) for k in TOPOLOGY]
    exact = topo_a == topo_b and None not in topo_a
    dv = delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    cg = None
    if all(isinstance(m.get(k), (int, float)) for m in (mass_a, mass_b) for k in ("cx", "cy", "cz")):
        cg = sum((mass_a[k] - mass_b[k]) ** 2 for k in ("cx", "cy", "cz")) ** 0.5
    return {"topology_a": topo_a, "topology_b": topo_b, "topology_exact": exact,
            "dv": dv, "da": da, "cg_distance": cg, "verified": exact and _mass_ok(dv, da)}


def _component_keys(comps: list):
    """Components keyed by (source basename, config, instance ordinal in tree order) — instance numbers may
    legitimately differ between the original and a rebuild."""
    counters: dict = {}
    keyed = {}
    for c in comps:
        base = os.path.basename(c.get("path") or "").lower()
        cfg = c.get("config") or ""
        n = counters.get((base, cfg), 0) + 1
        counters[(base, cfg)] = n
        keyed[(base, cfg, n)] = c
    return keyed, sorted(keyed)


def assembly_verdict(comps_a: list, comps_b: list, mates_a: list, mates_b: list, mass_a: dict, mass_b: dict) -> dict:
    """comps_* / mates_* = analyze_assembly(components / mates) lists, mass_* as in part_verdict."""
    map_a, keys_a = _component_keys(comps_a)
    map_b, keys_b = _component_keys(comps_b)
    set_exact = keys_a == keys_b

    # transforms only mean something when the sets pair up: 12 numbers, rotation first, translation last
    max_rot = max_pos = 0.0
    transforms_ok = set_exact
    if set_exact:
        for k in keys_a:
            ta, tb = map_a[k].get("transform"), map_b[k].get("transform")
            if not (isinstance(ta, list) and isinstance(tb, list) and len(ta) >= 12 and len(tb) >= 12):
                transforms_ok = False
                break
            max_rot = max(max_rot, max(abs(ta[i] - tb[i]) for i in range(9)))
            max_pos = max(max_pos, max(abs(ta[i] - tb[i]) for i in range(9, 12)))
        transforms_ok = transforms_ok and max_rot <= TRANSFORM_TOL and max_pos <= TRANSFORM_TOL

    types_a = sorted(m.get("type") or "" for m in mates_a)
    types_b = sorted(m.get("type") or "" for m in mates_b)
    mates_ok = len(mates_a) == len(mates_b) and types_a == types_b
    dv = delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    return {"set_exact": set_exact, "max_rot": max_rot, "max_pos": max_pos, "transforms_ok": transforms_ok,
            "mate_types_match": types_a == types_b, "mates_ok": mates_ok, "dv": dv, "da": da,
            "verified": set_exact and transforms_ok and mates_ok and _mass_ok(dv, da)}
