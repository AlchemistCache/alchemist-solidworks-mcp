"""Compact tool descriptions + tool_help (idea: Bennyco86's tool_help).

Every tool description is sent to the model with EVERY request; with 120+ tools that was ~85k characters. In compact
mode (default; SWMCP_DOCS=full turns it off) each description longer than SWMCP_DOC_LIMIT (500) characters
is cut at a sentence boundary and ends with a pointer to tool_help('<tool>'), which returns the full text, the
parameters and the topic guides below."""
import config
import json
import os
import re

import tool_groups

FULL: dict = {}          # tool name -> full description
PARAMS: dict = {}        # tool name -> input schema

GUIDES = {
    "start": """Getting started
- ensure_ready first (starts / attaches the hidden SolidWorks instance; the first start takes up to a minute).
- open_document / open_new_part / open_new_assembly; list_open_documents; close_document(close_all).
- Indices of faces / edges come from analyze_model(faces|edges) (part) or analyze_assembly(faces|edges, component);
  pass near='[x,y,z]' to get only the nearest ones. Bodies: analyze_model(bodies).
- UNITS: every tool takes and returns MILLIMETRES (areas area_mm2, volumes volume_mm3) and DEGREES; mass kg,
  force N, stress MPa.
- Every answer is ONE JSON object: state_version, document (sketch while one is open), the tool's fields, and
  created / removed — what the call added to / removed from the tree. Errors read 'CODE | message'.
- Long operations answer {"running": true, "job_id": …} after ~45 s — call job_result(job_id), never repeat
  the call.
- Sketches: draw the shape at exact coordinates (add_sketch_entities), then define_sketch with the design dimensions
  so it is fully defined (black) before the feature; answers that worked in a sketch carry sketch_status.
- execute_batch(steps) runs several tools in one call; '$last.<field>' / '$N.<field>' refer to earlier results.
- SEE the model: view_image (one view; focus = close-up of chosen components), view_images (a set), sim_image
  (FEA maps).
- Tool groups that are off (by default all but base and assemblies, unless used lately) are listed by tool_help();
  a tool of such a group runs through execute_batch(steps=[{"tool": …, "args": …}]) — that turns its group on.
  tool_help('<tool>') gives its parameters.""",
    "units": """Units and entity references
- Every tool: MILLIMETRES for lengths and coordinates (some parameters say it in the name: x_mm, thickness_mm, …),
  DEGREES for angles, mass kg, force N, stress/pressure MPa. Answers likewise: coordinates mm, area_mm2, volume_mm3,
  dimension values value_mm / value_deg (modify_dimension takes the same: mm for a length, degrees for an angle).
- Faces / edges: analyze_model(faces|edges) index `i` (part) or analyze_assembly(faces|edges, component) (assembly).
  Entity spec (where a tool takes one): {"face_index"} | {"edge_index"} | {"component","face_index"|"edge_index"} | {"component"}
  | {"point_mm":[x,y,z]} | {"plane": "Front Plane"}.
- Lists are real JSON arrays everywhere (segments, points, edge_indices, near, transform).""",
    "assemblies": """Assemblies
- Read: list_components, analyze_assembly, list_mates, constraint_status, mass_properties, measure_components,
  interference_check, clearance_check(subassembly=…), list_references, get_custom_properties, bom_export.
- Change: insert_component (origin at x/y/z, mm), insert_and_mate (insert + mates, all-or-nothing), add_mate,
  add_mate_advanced (symmetric, width, profile_center, hinge, gear, linear_coupler, lock, slot), edit_mate
  (value / flip / suppress / re-point / delete), pattern_components, move_component, replace_component,
  set_component_state, set_appearance, set_custom_properties, configurations, pack_and_go.
- A distance mate plane–cylinder often lands on the wrong side → edit_mate(flip=True).""",
    "sheet_metal": """Sheet metal for production
- sheet_metal_info (part / component / whole assembly: thickness, radius, K, bend radii).
- sheet_metal_cutlist(xlsx_path=…) — cut list: blank L×W, area, cut length, bends, mass; totals per thickness+material.
- export_flat_dxf(out_dir, name_by='designation', layers=True, note=True) — DXF per part: CUT_OUTER / CUT_INNER /
  BEND / MARK layers; sketches=True exports marking text (add_sketch_text) to MARK.
- sheet_metal_dfm — hole↔bend (2.5t+R), hole↔edge (t), small holes (<t), short flanges (<4t), mixed bend radii.
- Features: sheet_metal_feature (base / edge flange / sketched bend / flat pattern), sheet_metal_hem,
  sheet_metal_miter_flange, sheet_metal_cross_break, sheet_metal_gusset (stamped rib across a bend),
  convert_to_sheet_metal. Jog / closed corner have no API in SW 2024: jog = two sketched bends.""",
    "simulation": """Strength (SOLIDWORKS Simulation; N, MPa, mm)
1. sim_create_study → sim_set_material (parts without a SW material) → sim_add_fixture → sim_add_load.
   Sheet metal is meshed as SHELLS: put loads on the big faces, not on thin end faces (they get nothing).
2. Assemblies: sim_contact (global bonded / no_penetration / free), sim_contact_set (pairs, friction),
   sim_connector: bolt (solid bodies only — SW 2024 API refuses bolts on sheet-metal shells), edge_weld, pin.
3. sim_mesh (+ sim_mesh_control on holes / corners), sim_options (h-adaptive, large displacement), sim_run
   (meshes if needed) → σ, displacement, FoS (shells: yield of the study material / σmax), connector forces.
4. sim_image (stress / displacement map, clip_percentile 99 against singular peaks), sim_report (HTML report),
   sim_status(details=True) to check the setup, sim_copy_study for another load case.
5. Cross-check by hand: bolt_check (shear/axial from sim_results), weld_check, beam_check; by an independent
   solver: sim_crosscheck (CalculiX on the same mesh; solid meshes; needs ccx, else status blocked + the deck).""",
    "weldments": """Weldments and specifications
- weldment_profiles → weldment_member(profile='<standard>/<type>/<size>', sketch=layout) (adds the Weldment feature)
  → weldment_trim → weldment_end_cap → weldment_gusset → weldment_cut_list(xlsx_path=…).
- bom_gost (GOST 2.106 specification, XLSX), bom_export (flat BOM), insert_bom_table (on a drawing, balloons).""",
    "drawings": """Drawings
- Save the model first. create_drawing → add_drawing_view (front/top/right/isometric…; Russian SW names handled)
  → move_drawing_view, add_section_view, add_flat_pattern_view, auto_dimension_drawing, add_drawing_dimension,
  add_hole_callout, auto_center_marks, insert_bom_table → export_document (PDF / DXF / DWG).
- Tidy: save → auto_dimension_drawing (dimensions set off the views) → dedupe_drawing_dimensions →
  layout_drawing_views (projection, scale, no overlaps) → view_image to look at it.
- A part from a DXF/DWG drawing: analyze_drawing, then tool_help('reading_drawings') — projection, faces, sheet
  metal, self-checks.""",
    # the decisions behind analyze_drawing's numbers — long, so it lives in a file and is read on demand
    "reading_drawings": open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "guides", "reading_drawings.md"),
                             encoding="utf-8").read(),
    "rules": """Practical rules learned on live SW 2024
- Sheet metal = shells in Simulation: loads on thin end faces do nothing.
- Simulation bolts / pins: only between solid bodies (shells → InvalidMesh). Edge welds work on shells.
- Global contact does not separate overlapping sheet-metal shells — they bond by themselves.
- MCP clients cut a call at ~60 s: use job_result when a tool answers {"running": true, "job_id": …}.
- Named drawing views are localized; add_drawing_view handles «*Спереди»/«*Изометрия».
- Revolve: the profile must not overlap the axis line (draw the axis apart from the profile).""",
}


def _cut(text: str, limit: int) -> str:
    if len(text) <= limit:
        return text
    head = text[:limit]
    # end at the last sentence end or line break inside the limit (keeps the leading, most important part);
    # "e.g." / "i.e." are not sentence ends
    ends = [m.start() for m in re.finditer(r"(?<!e\.g)(?<!i\.e)[.!?](?=\s)|\n", head)]
    cut = ends[-1] if ends else -1
    if cut < limit * 0.4:
        cut = head.rfind(" ")
    return head[:cut + 1].rstrip()


def compact(mcp, limit: int = 0) -> dict:
    """Shorten descriptions of the registered tools; returns {tools, before, after} character counts."""
    mode = config.DOCS_MODE
    limit = limit or config.DOC_LIMIT
    before = after = 0
    for t in mcp._tool_manager.list_tools():
        full = t.description or ""
        FULL[t.name] = full
        PARAMS[t.name] = t.parameters
        before += len(full)
        if mode != "full" and t.name != "tool_help" and len(full) > limit:
            t.description = _cut(full, limit) + f" … [full: tool_help('{t.name}')]"
        after += len(t.description or "")
    return {"mode": mode, "tools": len(FULL), "before": before, "after": after}


def _first_sentence(text: str, n: int = 110) -> str:
    s = " ".join((text or "").split())
    m = re.match(r"(.+?[.!?])(\s|$)", s)
    s = m.group(1) if m else s
    return s if len(s) <= n else s[:n - 1] + "…"


def _params(schema: dict) -> list:
    out = []
    req = set((schema or {}).get("required", []))
    for k, v in ((schema or {}).get("properties") or {}).items():
        typ = v.get("type") or "/".join(x.get("type", "?") for x in v.get("anyOf", [])) or "any"
        if "enum" in v:
            typ += " " + "|".join(map(str, v["enum"]))
        item = {"name": k, "type": typ}
        if k in req:
            item["required"] = True
        elif "default" in v:
            item["default"] = v["default"]
        if v.get("description"):
            item["description"] = v["description"]
        out.append(item)
    return out


def _line(name: str) -> str:
    """One line per tool; a tool of a group that is off says how to turn it on."""
    s = _first_sentence(FULL[name])
    if name in tool_groups.PARKED:
        s += " [off — runs through execute_batch]"
    return s


def help_text(topic: str = "") -> str:
    topic = (topic or "").strip()
    if not topic:
        return json.dumps({"guides": {k: _first_sentence(v.split("\n")[0], 60) for k, v in GUIDES.items()},
                           "usage": "tool_help('<guide>') or tool_help('<tool name>') or a search word",
                           "groups_off": {g: tool_groups.GROUPS[g] for g in tool_groups.off_groups()},
                           "tools": {n: _line(n) for n in sorted(FULL)}}, ensure_ascii=False)
    if topic in GUIDES:
        return GUIDES[topic]
    if topic in FULL:
        info = {"tool": topic, "group": tool_groups.GROUP_OF.get(topic, "base"), "description": FULL[topic],
                "parameters": _params(PARAMS.get(topic))}
        if topic in tool_groups.PARKED:
            info["off"] = (f"its group is off — call it through execute_batch(steps=[{{\"tool\": \"{topic}\", "
                           f"\"args\": {{…}}}}]), which turns the group on (or enable_tools(['{info['group']}']))")
        return json.dumps(info, ensure_ascii=False)
    words = [w.lower() for w in re.split(r"\W+", topic) if w]
    hits = {n: _line(n) for n in sorted(FULL)
            if all(w in (n + " " + FULL[n]).lower() for w in words)}
    guides = [g for g, txt in GUIDES.items() if all(w in txt.lower() for w in words)]
    return json.dumps({"search": topic, "guides": guides, "tools": hits}, ensure_ascii=False)


def register(mcp):
    @mcp.tool(structured_output=False)
    def tool_help(topic: str = "") -> str:
        """Reference for this server: full description + parameters of any tool (topic = tool name), topic guides
        (start, units, assemblies, sheet_metal, simulation, weldments, drawings, reading_drawings, rules) or a
        search word. Empty topic =
        the guide list and every tool with one line, the tools of groups that are off too (they run through
        execute_batch). Descriptions ending with '[full: …]' were shortened."""
        return help_text(topic)
