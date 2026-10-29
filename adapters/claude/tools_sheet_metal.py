"""Sheet metal: features (base / edge flange, sketched bend, flat pattern; hem, miter flange, cross break, gusset,
convert) and production (info, cut list, flat DXF, DFM). Units: MILLIMETRES and DEGREES (sheet_metal_feature converts
to SI for the server in units.py). Registered by registry.register_all(); call plumbing in bridge.py."""
import os
import re
import shutil
from typing import Annotated, Literal, Optional

from pydantic import Field


import adapter_log
import session
from bridge import call, dump, flat_note, need_xlsx, scratch_dir, call_mm, call_mm_json, sheet_dxf, sheet_reports
from jobs import background


def _dfm_part(f: dict, rules: dict, details: bool, keep_dir: str = "") -> dict:
    """One exported flat pattern (an export_flat_dxf `files` entry) checked against the DFM rules: the part's entry
    with its `issues` (or an `error` when it could not be checked). mixed_bend_radii: one part bent with several
    radii."""
    t, R = f.get("thickness_mm") or 0.0, f.get("bend_radius_mm") or 0.0
    item = {k: f[k] for k in ("part", "config", "designation", "qty", "material") if f.get(k) is not None}
    item.update(thickness_mm=t, bend_radius_mm=R)
    if not t:
        return dict(item, error="thickness unknown")
    k = f.get("k_factor")
    if k is None:
        item["k_factor_assumed"] = k = 0.5
    try:
        if "loops" in f:   # read from the model (flat_geometry); else a DXF SolidWorks exported
            doc = sheet_dxf.doc_from_flat(f)
            if keep_dir:
                f["file"] = os.path.join(keep_dir, re.sub(r'[<>:"/\|?*]+', "_", f.get("part") or "part") +
                                         (f"_{f['config']}" if f.get("config") else "") + ".dxf")
                doc.saveas(f["file"])
        else:
            doc = sheet_dxf.ezdxf.readfile(f["file"])
        item.update(sheet_dxf.dfm_check_doc(doc, t, R, k, rules, details))
    except Exception as ex:  # noqa: BLE001
        adapter_log.exception("sheet_metal_dfm check")
        return dict(item, error=f"{type(ex).__name__}: {ex}")
    radii = f.get("bend_radii_mm") or []
    if len(radii) > 1:
        item["issues"].append({"rule": "mixed_bend_radii", "radii_mm": radii})
    return item


def register(mcp, tool):
    # ------------------------------------------------------------------ sheet metal
    @tool
    def sheet_metal_cutlist(csv_path: str = "", xlsx_path: str = "", assembly_qty: int = 1, qty: int = 1,
                            max_rows: int = 100) -> str:
        """Cutting / bending sheet for production. Active part or WHOLE assembly (unique part+config, qty ×
        assembly_qty): Обозначение, Наименование, material, thickness, flat blank L×W mm, net area m², cut
        length mm, cut-outs, bends, bend radius, K, mass kg; totals per thickness+material (area, cut
        length, bends, mass). csv_path → ';'-CSV (Excel, RU decimals). xlsx_path → formatted Excel workbook:
        groups by thickness+material, positions sorted by «Обозначение» (same designation + blank merged, qty
        summed), totals as formulas, sheet «Итоги»."""
        if not xlsx_path:
            return call_mm("sheet_metal_cutlist", dict(csv_path=csv_path, assembly_qty=assembly_qty, qty=qty,
                         max_rows=max_rows))
        xlsx = session.full_path("xlsx_path", xlsx_path)   # the adapter writes it: checked here, before any work

        def run():
            need_xlsx()
            r = call_mm_json("sheet_metal_cutlist", dict(csv_path=csv_path, assembly_qty=assembly_qty, qty=qty,
                           max_rows=100000))
            x = sheet_reports.write_cutlist_xlsx(xlsx, r.get("rows") or [], r.get("totals") or [],
                                                 r.get("document") or "")
            r["xlsx"], r["xlsx_positions"] = x["xlsx"], x["positions"]
            rows = r.get("rows") or []
            r["truncated"], r["rows"] = len(rows) > max_rows, rows[:max_rows]
            return dump(r)
        return background("sheet_metal_cutlist", run)

    @tool
    def sheet_metal_hem(edge_indices: list[int], hem_type: Literal["closed", "open", "tear_drop", "rolled", "double"] = "closed",
                        position: Literal["inside", "outside"] = "inside", length_mm: float = 10.0, gap_mm: float = 0.5,
                        angle_deg: float = 270.0, radius_mm: float = 1.0, reverse: bool = False) -> str:
        """Hem (подгибка) on edges of the ACTIVE sheet-metal part. edge_indices from analyze_model(edges) —
        the outer edges of the sheet face. closed/open use length+gap, tear_drop angle+radius, rolled
        angle+radius."""
        return call_mm("sheet_metal_hem", dict(edge_indices=edge_indices, hem_type=hem_type, position=position,
                     length_mm=length_mm, gap_mm=gap_mm, angle_deg=angle_deg, radius_mm=radius_mm, reverse=reverse))

    @tool
    def sheet_metal_miter_flange(edge_indices: list[int], sketch: str = "",
                                 position: Literal["material_inside", "material_outside", "bend_outside"] = "material_inside",
                                 radius_mm: float = 0.0, gap_mm: float = 0.25, start_offset_mm: float = 0.0,
                                 end_offset_mm: float = 0.0, trim_side_bends: bool = False) -> str:
        """Miter flange (кромка под углом) along a chain of edges (corners mitred automatically). Profile =
        `sketch` or the ACTIVE sketch: an open line/arc chain starting AT the end point of the first edge, on
        a plane normal to that edge (e.g. Right Plane for an edge along X ending at x=0). edge_indices from
        analyze_model(edges). radius_mm 0 = sheet default. Verified: L-profile along 2 edges -> 4 bends."""
        return call_mm("sheet_metal_miter_flange", dict(edge_indices=edge_indices, sketch=sketch, position=position,
                     radius_mm=radius_mm, gap_mm=gap_mm, start_offset_mm=start_offset_mm, end_offset_mm=end_offset_mm,
                     trim_side_bends=trim_side_bends))

    @tool
    def sheet_metal_cross_break(face_indices: list[int], angle_deg: float = 2.0, radius_mm: float = 1.0) -> str:
        """Cross break (поперечный перегиб, stiffening X) on faces of the ACTIVE sheet-metal part.
        Tip: corner fillet/chamfer of a sheet = add_edge_feature on the thickness edges."""
        return call_mm("sheet_metal_cross_break", dict(face_indices=face_indices, angle_deg=angle_deg,
                     radius_mm=radius_mm))

    @tool
    def sheet_metal_gusset(face_indices: list[int], edge_index: int = -1, offset_mm: Optional[float] = None,
                           flip_offset: bool = False, indent_depth_mm: float = 5.0, length_mm: Optional[float] = None,
                           height_mm: float = 10.0, width_mm: float = 6.0, thickness_mm: Optional[float] = None,
                           gusset_type: Literal["rounded", "flat"] = "rounded", draft_deg: float = 0.0,
                           inner_fillet_mm: float = 1.0, outer_fillet_mm: float = 1.0, edge_fillet_mm: float = 0.0,
                           flip_sides: bool = False) -> str:
        """Sheet-metal gusset (штампованное ребро жёсткости поперёк гиба) on the ACTIVE sheet-metal part.
        face_indices = the two flat faces on both sides of one bend (analyze_model(faces)). Position: along the bend
        edge (auto: the straight edge where face 1 meets the bend; or edge_index), offset_mm from its start
        (default: middle). Size: indent_depth_mm, or length_mm + height_mm (profile dimensions); width_mm = rib
        width, thickness_mm default = sheet thickness."""
        return call_mm("sheet_metal_gusset", dict(face_indices=face_indices, edge_index=None if edge_index < 0 else edge_index,
                     offset_mm=offset_mm, flip_offset=flip_offset, indent_depth_mm=indent_depth_mm, length_mm=length_mm,
                     height_mm=height_mm, width_mm=width_mm, thickness_mm=thickness_mm, gusset_type=gusset_type,
                     draft_deg=draft_deg, inner_fillet_mm=inner_fillet_mm, outer_fillet_mm=outer_fillet_mm,
                     edge_fillet_mm=edge_fillet_mm, flip_sides=flip_sides))

    @tool
    def convert_to_sheet_metal(fixed_face_index: int = -1, bend_edge_indices: list[int] = [], thickness_mm: float = 0.0,
                               bend_radius_mm: float = 2.0, gap_mm: float = 0.2, reverse_thickness: bool = False,
                               keep_body: bool = False, auto: bool = False) -> str:
        """Convert a SOLID (e.g. a box modelled as a block) into sheet metal: fixed face + bend edges (corner
        rips are made automatically), thickness and bend radius. Indices from analyze_model(faces/edges).
        Example: box → open tray = fixed bottom face, bend edges = the 4 bottom edges.
        auto=True for a FLAT plate without bends: the largest planar face is fixed and the thickness is measured (its
        distance to the facing face); the side is checked by volume. Missing thickness_mm is measured the same way."""
        return call_mm("convert_to_sheet_metal", dict(fixed_face_index=fixed_face_index, bend_edge_indices=bend_edge_indices,
                     thickness_mm=thickness_mm if thickness_mm > 0 else None, bend_radius_mm=bend_radius_mm, gap_mm=gap_mm,
                     reverse_thickness=reverse_thickness, keep_body=keep_body, auto=auto or None))

    @tool
    def sheet_metal_info(component: str = "", details: bool = False) -> str:
        """Sheet-metal data: thickness_mm, bend_radius_mm, K-factor / bend table, bends, material and SW's
        cut-list properties (flat bounding box, cutting length…). Active part; one assembly component; or —
        for an assembly — EVERY sheet-metal part (deduped by file+config, with qty) and a by_thickness
        summary. details=True includes cut-list properties per part."""
        return call_mm("sheet_metal_info", dict(component=component, details=details))

    @tool
    def export_flat_dxf(file_path: str = "", out_dir: str = "", component: str = "",
                        name_by: Literal["file", "designation", "designation_name"] = "file", layers: bool = True,
                        note: bool = False, bend_lines: bool = True, sketches: bool = False, hidden_edges: bool = False,
                        bounding_box: bool = False, merge_coplanar_faces: bool = True, library_features: bool = False,
                        forming_tools: bool = False, name_with_thickness_qty: bool = True) -> str:
        """Flat-pattern DXF straight from the model (no drawing). Active sheet-metal part → file_path (default
        next to the part); one assembly component; or the WHOLE active assembly → one DXF per unique
        part+configuration in out_dir (default <assembly folder>\\DXF) named <name>[_<config>]_<t>mm_x<qty>.dxf,
        <name> = file name, or «Обозначение» (name_by=designation) / «Обозначение Наименование».
        layers=True sorts the geometry onto layers for laser CAM: CUT_OUTER (white), CUT_INNER (red), BEND (green,
        dashed), MARK (blue — open sketch chains); skipped with hidden_edges / bounding_box. note=True writes
        «designation | t | qty | material» under the part on layer NOTE (not a cut layer). Every file entry
        carries designation, thickness, bend radius, K, material, qty."""
        params = dict(file_path=file_path, out_dir=out_dir, component=component, name_by=name_by,
                      bend_lines=bend_lines, sketches=sketches, hidden_edges=hidden_edges, bounding_box=bounding_box,
                      merge_coplanar_faces=merge_coplanar_faces, library_features=library_features,
                      forming_tools=forming_tools, name_with_thickness_qty=name_with_thickness_qty)
        if not (layers or note) or hidden_edges or bounding_box:
            return call_mm("export_flat_dxf", params)

        def run():
            r = call_mm_json("export_flat_dxf", params)
            for f in r.get("files") or []:
                try:
                    f["layers"] = sheet_dxf.apply_layers(f["file"], flat_note(f) if note else "")
                except Exception as ex:  # noqa: BLE001 — the DXF itself is fine, only re-layering failed
                    adapter_log.exception("export_flat_dxf layers")
                    f["layers_error"] = f"{type(ex).__name__}: {ex}"
            return dump(r)
        return background("export_flat_dxf", run)

    @tool
    def sheet_metal_dfm(component: str = "", min_flange_mm: float = 0.0, hole_to_bend_mm: float = 0.0,
                        hole_to_edge_mm: float = 0.0, min_hole_mm: float = 0.0, details: bool = False,
                        keep_dxf_dir: str = "", max_parts: int = 100) -> str:
        """Manufacturability (DFM) check of sheet metal: active part, one assembly component, or EVERY sheet-metal
        part of the active assembly. Flat patterns are read from the models (~1 s a part; one that cannot be is
        exported the old way) and analysed (t = thickness, R = bend radius):
        hole_to_bend — cut-out edge closer to a bend line than 2.5·t+R in the flat (0 = cut-out crosses the bend);
        hole_to_edge — cut-out closer to the outer contour than t; small_hole — Ø (or slot width) < t;
        short_flange — outside flange length (90° estimate) < 4·t; mixed_bend_radii — one part bent with several
        radii; cross_part radius_per_thickness — same thickness+material bent with different radii (extra tooling).
        Limits 0 = these defaults, any value in mm overrides. Issues carry flat-pattern coordinates `at` (mm);
        details=True adds every bend's flange lengths. keep_dxf_dir keeps the analysed flat patterns as DXF (for laser
        files use export_flat_dxf — SolidWorks' own export)."""
        rules = dict(min_flange_mm=min_flange_mm, hole_to_bend_mm=hole_to_bend_mm, hole_to_edge_mm=hole_to_edge_mm,
                     min_hole_mm=min_hole_mm)

        def run():
            out = keep_dxf_dir or scratch_dir("dfm", unique=True)
            try:
                # the flat patterns read from the models (~1 s a part); a part that cannot be is exported the old way
                r = call_mm_json("flat_geometry", dict(component=component, out_dir=out))
                parts, by_rule, by_tm = [], {}, {}   # by_tm: (thickness, material) → the bend radii used for it
                for f in r.get("parts") or []:
                    item = _dfm_part(f, rules, details, keep_dxf_dir)
                    parts.append(item)
                    if "error" in item:
                        continue
                    for i in item["issues"]:
                        by_rule[i["rule"]] = by_rule.get(i["rule"], 0) + 1
                    radii = f.get("bend_radii_mm") or ([item["bend_radius_mm"]] if f.get("bends") else [])
                    by_tm.setdefault((item["thickness_mm"], f.get("material") or "?"), set()).update(radii)
                    if keep_dxf_dir:
                        item["dxf"] = f["file"]
                cross = [{"rule": "radius_per_thickness", "thickness_mm": t, "material": m, "radii_mm": sorted(rs)}
                         for (t, m), rs in sorted(by_tm.items()) if len(rs) > 1]
                parts.sort(key=lambda x: -len(x.get("issues") or []))
                res = {"parts_checked": len(parts), "parts_with_issues": sum(1 for x in parts if x.get("issues")),
                       "by_rule": by_rule, "cross_part": cross, "failed": r.get("failed") or [],
                       "truncated": len(parts) > max_parts, "parts": parts[:max_parts]}
                return dump(res)
            finally:
                if not keep_dxf_dir:
                    shutil.rmtree(out, ignore_errors=True)
        return background("sheet_metal_dfm", run)

    # ------------------------------------------------------------------ sheet_metal_feature (mm)
    # ---------------------------------------------------------------------------
    # Tool: sheet_metal_feature
    # ---------------------------------------------------------------------------
    @tool
    def sheet_metal_feature(
        feature_type: Literal["base_flange", "edge_flange", "edge_flange_sketch", "edge_flange_finish",
                              "flat_pattern", "sketched_bend"],
        thickness: Annotated[float, Field(
            gt=0, description="Sheet thickness in mm")] = 1.0,
        bend_radius: Annotated[float, Field(
            gt=0, description="Bend radius in mm")] = 1.0,
        k_factor: Annotated[float, Field(
            gt=0, lt=1, description="Neutral-axis K-factor (0..1)")] = 0.5,
        ex: float = 0.0,
        ey: float = 0.0,
        ez: float = 0.0,
        flange_length: Annotated[float, Field(
            gt=0, description="Edge flange length in mm; use >= 2*thickness+1")] = 20.0,
        angle: Annotated[float, Field(
            gt=0, le=180, description="Bend angle in DEGREES (edge_flange and sketched_bend; default 90)")] = 90.0,
        use_default_radius: bool = False,
        flip: bool = False,
        bend_position: Literal["centerline", "material_inside", "material_outside", "bend_outside"] = "centerline",
        fixed_face_index: int = -1,
        fixed_x: float = 0.0,
        fixed_y: float = 0.0,
        fixed_z: float = 0.0,
        reverse_thickness: bool = False,
        symmetric_thickness: bool = False,
        clear_profile: bool = True,
        edge_index: int = -1,
        depth: float = 0.0,
        mid_plane: bool = False,
    ) -> str:
        """Create sheet metal features on the active part. Lengths mm, angles DEGREES.

        'base_flange' — a sheet metal base from the ACTIVE sketch profile; exits sketch mode.
            thickness · bend_radius (default = thickness) · k_factor (0.5). An OPEN profile (an L / U section
            drawn as lines) needs depth = its length along the sketch normal (mm; mid_plane=True centres it).
            THICKENING SIDE IS A BUILD DECISION, and it is silent when wrong: `reverse_thickness`
            thickens to the OPPOSITE side of the sketch plane, `symmetric_thickness` splits ±t/2.
            Rebuilding an analyzed part, replay the original's own flags (analyze_model(features)
            reports them on the SMBaseFlange) — they also set the sheet orientation later bends fold
            against, so a wrong side fails downstream and less clearly.
        'edge_flange' — a DEFAULT-profile (full-edge-width) flange on an existing sheet metal edge.
            Select by edge_index (from analyze_model(edges), PREFERRED — a coordinate pick can miss a
            real edge) or a point ex/ey/ez on it. flange_length · angle (90).
        'edge_flange_sketch' + 'edge_flange_finish' — the CUSTOM-profile flange, in two calls. The
            sketch call takes the attach edge (edge_index preferred, else ex/ey/ez), generates the
            edge-linked profile sketch (the API accepts ONLY a sketch it generated), clears it
            (clear_profile) and leaves it ACTIVE, echoing the sketch's MEASURED frame — express your
            profile in THAT frame. Draw with add_sketch_entity, then call the finish with the SAME
            edge (+ angle, bend_radius or use_default_radius, bend_position). The profile defines the
            outline, so flange_length is ignored.
        'sketched_bend' — bends the sheet about the bend LINE(S) in the ACTIVE sketch (draw them on a
            sheet face with create_sketch + add_sketch_entity first; the sketch must still be active).
            The side that stays PUT is the fixed face: fixed_face_index (from analyze_model(faces),
            PREFERRED) or fixed_x/y/z, a 3D point ON that face. angle (90) · bend_radius, or
            use_default_radius=True to take the sheet's default and ignore it · flip reverses the fold
            direction · bend_position 'centerline' (default) | 'material_inside' | 'material_outside' |
            'bend_outside', matching the `position` analyze_model(features) reports on an SM3dBend, so a
            recipe value replays as-is. ONE sketch with SEVERAL bend lines makes one feature with one
            bend per line.
        'flat_pattern' — unfolds every bend. No extra parameters; needs an existing base_flange.

        A wrong fold is expensive: there is no sketch-entity move/delete tool, so repairing one costs
        deleting the feature and re-creating it. And a mirrored fold is invisible to volume, area AND
        topology alike — read one bend face back after building (recipe R14)."""
        params = {
            "feature_type": feature_type,
            "thickness": thickness,
            "bend_radius": bend_radius,
            "k_factor": k_factor,
            "ex": ex, "ey": ey, "ez": ez,
            "flange_length": flange_length,
            "angle": angle,
            "use_default_radius": use_default_radius,
            "flip": flip,
            "bend_position": bend_position,
            "fixed_x": fixed_x, "fixed_y": fixed_y, "fixed_z": fixed_z,
            "reverse_thickness": reverse_thickness,
            "symmetric_thickness": symmetric_thickness,
            "clear_profile": clear_profile,
            "fixed_face_index": fixed_face_index if fixed_face_index >= 0 else None,   # -1 = not given
            "edge_index": edge_index if edge_index >= 0 else None,
            "depth": depth if depth > 0 else None,
            "mid_plane": mid_plane or None,
        }
        return call("sheet_metal_feature", params)
