"""Units at the MCP boundary: the model speaks MILLIMETRES and DEGREES everywhere; the execution server keeps SI (meters, radians), as SolidWorks does. Two conversions, both here and nowhere else:

  params_to_si(tool, params)    a tool's length parameters mm → m before the call (INPUT_LENGTHS + nested records)
  response_to_mm(tool, resp)    an SI tool's answer → mm / mm² / mm³ / degrees, by key (OUTPUT rules below)

The millimetre tools (call_mm / call_mm_json) are millimetres on both sides already and pass through untouched."""
import json
import math

M = 0.001          # one millimetre in meters

_SKETCH_ENTITY = {"x1", "y1", "x2", "y2", "xm", "ym", "cx", "cy", "radius", "vx", "vy", "distance", "points"}
# tool -> its length parameters (numbers or nested lists of numbers), mm in → m out
INPUT_LENGTHS = {
    "create_sketch": {"face_x", "face_y", "face_z"},
    "add_sketch_entity": _SKETCH_ENTITY,
    "add_sketch_constraint": {"px1", "py1", "px2", "py2"},
    "sketch_modify": {"delete_near", "move_points"},
    "add_dimension": {"px", "py", "value", "label_offset_x", "label_offset_y"},
    "extrude_feature": {"depth", "axis_x1", "axis_y1", "axis_x2", "axis_y2"},
    "create_rib": {"thickness"},
    "hole_wizard": {"points", "face_x", "face_y", "face_z", "depth", "diameter", "cbore_diameter", "cbore_depth",
                    "csink_diameter", "thread_depth"},
    "add_edge_feature": {"radius_or_distance", "ex", "ey", "ez", "distance2", "edge_points"},
    "add_reference_geometry": {"offset", "px", "py", "pz"},
    "create_pattern": {"spacing", "spacing2"},
    "sheet_metal_feature": {"thickness", "bend_radius", "ex", "ey", "ez", "flange_length", "fixed_x", "fixed_y",
                            "fixed_z", "depth"},
    "insert_component": {"x", "y", "z"},
    "analyze_model": {"near"},
    "add_drawing_view": {"pos_x", "pos_y"},
    "add_flat_pattern_view": {"pos_x", "pos_y"},
    "add_drawing_dimension": {"px", "py", "value", "label_offset_x", "label_offset_y"},
    "add_hole_callout": {"px", "py"},
    "add_section_view": {"px", "py", "edge_x", "edge_y", "x1", "y1", "x2", "y2"},
}


def _scale(v, f):
    if isinstance(v, bool) or v is None:
        return v
    if isinstance(v, (int, float)):
        return v * f
    if isinstance(v, list):
        return [_scale(x, f) for x in v]
    return v


def params_to_si(tool: str, params: dict) -> dict:
    out = dict(params)
    for k in INPUT_LENGTHS.get(tool, ()):
        if k in out:
            out[k] = _scale(out[k], M)
    if tool == "add_sketch_entities":
        out["segments"] = [params_to_si("add_sketch_entity", s) for s in out.get("segments") or []]
    if tool == "sketch_modify" and out.get("add_segments"):
        out["add_segments"] = [params_to_si("add_sketch_entity", s) for s in out["add_segments"]]
    if tool == "insert_component" and out.get("transform"):
        out["transform"] = _transform(out["transform"], M)
    return out


def _transform(t, f):
    """13 / 16 numbers: 3x3 rotation, translation (9..11), scale (12) — only the translation is a length."""
    t = list(t)
    for i in (9, 10, 11):
        if i < len(t):
            t[i] = t[i] * f
    return t


# ---------------------------------------------------------------- answers of the SI tools → mm
# keys whose numbers are lengths wherever they appear (coordinates, sizes, distances)
LENGTH_KEYS = {"x", "y", "x1", "y1", "x2", "y2", "xm", "ym", "cx", "cy", "r", "radius", "radii", "length", "len",
               "depth", "offset", "dist", "start", "end", "mid", "point", "points", "origin", "centroid", "near",
               "pos", "center", "centers", "anchors", "at", "residual", "thickness", "bend_radius", "gap", "extent"}
DIRECTION_KEYS = {"normal", "xdir", "ydir", "axis", "dir", "direction"}   # unit vectors / signs: never scaled
TOOL_LENGTH_KEYS = {"add_dimension": {"requested", "effective"}, "add_drawing_dimension": {"requested", "effective"}}
SI_TOOLS = set(INPUT_LENGTHS) | {"add_sketch_entities", "analyze_assembly", "get_selection", "check_drawing",
                                 "add_mate", "save_body_as_part"}


def _round(v):
    return round(v, 6) if isinstance(v, float) else v


def _deg(rad):
    """Degrees, 4 decimals: the server rounds radians to 6 decimals, so more would only be noise (60.000026)."""
    return round(math.degrees(rad), 4)


def _to_mm(o, tool, parent=""):
    if isinstance(o, list):
        return [_to_mm(x, tool, parent) for x in o]
    if not isinstance(o, dict):
        return o
    out = {}
    extra = TOOL_LENGTH_KEYS.get(tool, set())
    for k, v in o.items():
        if k in DIRECTION_KEYS:
            out[k] = v
        elif (k == "transform" or k.endswith("_transform")) and isinstance(v, list):   # add_mate: a_/b_transform
            out[k] = [_round(x) for x in _transform(v, 1 / M)]
        elif k == "params" and "kind" in o and isinstance(v, list):   # a mate entity: point 0-2, direction 3-5, radii 6-7
            out[k] = [_round(x / M) if i in (0, 1, 2, 6, 7) and isinstance(x, (int, float)) else x
                      for i, x in enumerate(v)]
        elif k == "value_si":
            angular = o.get("angular")
            if o.get("count"):   # a pattern's instance count — no unit
                out["value"] = int(round(v))
            else:
                out["value_deg" if angular else "value_mm"] = _deg(v) if angular else _round(v / M)
        elif k == "value" and isinstance(v, (int, float)) and o.get("type") in ("distance", "angle"):   # mates
            out[k] = _deg(v) if o["type"] == "angle" else _round(v / M)
        elif k == "angle" and isinstance(v, (int, float)):        # bend / flange angles: radians in the SI answer
            out[k] = _deg(v)
        elif k == "area" and isinstance(v, (int, float)):
            out["area_mm2"] = _round(v / M ** 2)
        elif k == "volume" and isinstance(v, (int, float)):
            if "volume_mm3" not in o:
                out["volume_mm3"] = _round(v / M ** 3)
        elif k == "extent" and isinstance(v, dict):
            out[k] = {ax: _lengths(span, tool) for ax, span in v.items()}
        elif (k in LENGTH_KEYS or k in extra) and not isinstance(v, (dict, str)):
            out[k] = _lengths(v, tool)
        else:
            out[k] = _to_mm(v, tool, k)
    return out


def _lengths(v, tool):
    """Numbers (or nested lists of numbers) in mm; records inside a list (sketch points {x, y}) by their own keys."""
    if isinstance(v, list):
        return [_to_mm(x, tool) if isinstance(x, dict) else _lengths(x, tool) for x in v]
    return _round(v / M) if isinstance(v, (int, float)) and not isinstance(v, bool) else v


def to_mm(obj, tool: str = ""):
    """An SI payload the adapter built itself (dimension_for_face) in mm, by the same key rules."""
    return _to_mm(obj, tool)


def response_to_mm(tool: str, response: dict) -> dict:
    """result_geometry and the JSON payloads in cadState.features of an SI tool's answer, in mm."""
    if tool not in SI_TOOLS or not isinstance(response, dict):
        return response
    r = dict(response)
    if isinstance(r.get("result_geometry"), (dict, list)):
        r["result_geometry"] = _to_mm(r["result_geometry"], tool)
    state = r.get("cadState")
    if isinstance(state, dict) and state.get("features"):
        state = dict(state)
        state["features"] = [_feature_item(f, tool) for f in state["features"]]
        r["cadState"] = state
    return r


def _feature_item(item, tool):
    """A JSON payload, or a 'key=value' line (insert_component's transform=[…]), by the same key rules."""
    if not isinstance(item, str):
        return item
    if item[:1] in "{[":
        try:
            return json.dumps(_to_mm(json.loads(item), tool), ensure_ascii=False, separators=(",", ":"))
        except ValueError:
            return item
    key, sep, value = item.partition("=")
    if sep and key.isidentifier():
        try:
            converted = _to_mm({key: json.loads(value)}, tool)
        except ValueError:
            return item
        k, v = next(iter(converted.items()))
        return f"{k}={json.dumps(v, separators=(',', ':'))}"
    return item
