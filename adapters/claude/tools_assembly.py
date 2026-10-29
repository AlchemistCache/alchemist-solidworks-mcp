"""Assemblies: reading (components, interference, clearance, mass, BOM, GOST specification), changing (state,
appearance, move, replace, pack and go, properties, configurations), building (new assembly, insert, mates).
Units: MILLIMETRES and DEGREES everywhere (the older tools convert to SI in units.py);
mass kg. Registered by registry.register_all(); call plumbing in bridge.py."""
from typing import Literal, Optional



import pair_check
import session
import units
from bridge import call, dump, need_xlsx, call_mm, call_mm_json, gost_spec
from jobs import background


def register(mcp, tool):
    # ------------------------------------------------------------------ assemblies: read
    @tool
    def list_components(top_only: bool = False, name_filter: str = "", under: str = "", fields: list[str] = [],
                        offset: int = 0, limit: int = 50) -> str:
        """Fast component list of the ACTIVE assembly (hundreds in <1 s): name, file, config, state (resolved /
        lightweight / suppressed — a lightweight component is loaded and shown, NOT suppressed), hidden, fixed,
        origin_mm. under = only the components inside that subassembly; name_filter = substring; fields = just these
        (e.g. ["state"]; name always); pages of `limit` (next_offset when there is more). Lighter than
        analyze_assembly."""
        return call_mm("list_components", dict(top_only=top_only, name_filter=name_filter, under=under, fields=fields,
                                            offset=offset, limit=limit))

    @tool
    def delete_components(names: list[str] = [], prefix: str = "") -> str:
        """Delete components of the ACTIVE assembly in ONE operation (one selection, one rebuild — dozens in seconds;
        their mates go with them). names = exact names from list_components; prefix = every component whose full
        name starts with it ('Винт М6' at the top level, 'Sub-1/Винт' inside Sub-1). Answers deleted_count, deleted,
        not_deleted."""
        return call_mm("delete_components", dict(names=names, prefix=prefix))

    @tool
    def list_open_documents(full: bool = False) -> str:
        """Documents open in the (own) SolidWorks. Default shows only visible/modified ones; full=True lists
        background-loaded component docs too."""
        return call_mm("list_open_documents", dict(full=full))

    @tool
    def interference_check(components: list[str] = [], include_hidden: bool = False,
                           coincidence_as_interference: bool = False, subassemblies_as_components: bool = False,
                           include_multibody: bool = False, min_volume_mm3: float = 0.0, max_items: int = 25,
                           exclude_internal: bool = False, group: bool = True,
                           baseline: Literal["", "save", "compare"] = "") -> str:
        """Interference detection on the ACTIVE assembly (whole assembly, or only `components`).
        Zones are merged into component PAIRS with total volume_mm3, zone count, box_mm of the first zone and
        severity (negligible <1 mm³, minor <100, major). Sorted by volume. min_volume_mm3 drops touching faces;
        exclude_internal=True drops pairs inside one subassembly (its fasteners); group=True merges pairs that
        differ only by instance numbers ("count": 4). baseline='save' remembers every pair of this assembly as
        intended (a stud in its handle, a screw in its thread); baseline='compare' then shows only NEW or changed
        pairs (known = how many were hidden)."""
        params = dict(components=components, include_hidden=include_hidden,
                      coincidence_as_interference=coincidence_as_interference,
                      subassemblies_as_components=subassemblies_as_components, include_multibody=include_multibody,
                      min_volume_mm3=min_volume_mm3, max_items=100000)

        def run():
            info = call_mm_json("interference_check", params)
            items = info.get("interferences") or []
            out = {k: v for k, v in info.items() if k not in ("interferences", "path", "truncated")}
            if exclude_internal:
                kept = [x for x in items if not pair_check.internal(x["components"])]
                out["internal_dropped"] = len(items) - len(kept)
                items = kept
            if baseline == "save":
                out["baseline_saved"] = pair_check.save_baseline(info.get("path") or "", items, "interference")
            elif baseline == "compare":
                items, out["known"] = pair_check.new_only(info.get("path") or "", items, "interference")
            if group:
                items = pair_check.group(items, "components", "volume_mm3")
            out["pairs"] = len(items)
            out["major"] = sum(1 for x in items if x.get("severity") == "major")
            out["truncated"] = len(items) > max_items
            out["interferences"] = items[:max_items]
            return dump(out)
        return background("interference_check", run)

    @tool
    def clearance_check(min_gap_mm: float = 5.0, components: list[str] = [], subassembly: str = "",
                        max_pairs: int = 300, include_touching: bool = False, exclude_internal: bool = False,
                        group: bool = True) -> str:
        """Pairs of parts closer than min_gap_mm but NOT intersecting (bounding-box prefilter → exact
        measure). Scope: `components`, or all leaf parts under `subassembly` (e.g. 'Frame-1'), or the whole
        assembly (slow on big ones). exclude_internal / group as in interference_check."""
        params = dict(min_gap_mm=min_gap_mm, components=components, subassembly=subassembly, max_pairs=max_pairs,
                      include_touching=include_touching, max_items=5000)

        def run():
            info = call_mm_json("clearance_check", params)
            items = info.get("pairs") or []
            out = {k: v for k, v in info.items() if k not in ("pairs", "path")}
            if exclude_internal:
                kept = [x for x in items if not pair_check.internal(x["pair"])]
                out["internal_dropped"] = len(items) - len(kept)
                items = kept
            if group:
                items = pair_check.group(items, "pair", "gap_mm")
            out["pairs"] = items[:60]
            if len(items) > 60:   # the server's own `truncated` speaks of the box candidates, not of this list
                out["pair_count"], out["pairs_truncated"] = len(items), True
            return dump(out)
        return background("clearance_check", run)

    @tool
    def measure_components(component_a: str, component_b: str) -> str:
        """Minimum distance (mm) between two components of the ACTIVE assembly, plus center/normal distance,
        angle and bounding-box gaps per axis. distance 0 + contact = touching/intersecting. Subassemblies are
        measured through their parts (closest = the two parts that are nearest)."""
        return call_mm("measure_components", dict(component_a=component_a, component_b=component_b))

    @tool
    def mass_properties(components: list[str] = []) -> str:
        """Mass (kg), centre of gravity (mm, ASSEMBLY frame), volume (mm³), area (mm²) of the active part or
        assembly. No components = whole document; with components = per component + combined total."""
        return call_mm("mass_properties", dict(components=components))

    @tool
    def list_references(file_path: str = "", long_path_warn: int = 200) -> str:
        """All files referenced by an assembly/drawing (default: the active doc) AS SAVED on disk (save first to see
        unsaved changes), with missing files and over-long paths flagged; a missing file's same-named candidates under
        the assembly's folder are listed (candidates) — re-point with replace_component(assembly_path, old_file,
        new_file) on the closed assembly. SolidWorks opens an assembly whose file is missing with that component
        suppressed, so a "suppressed" part may just be a broken link."""
        return call_mm("list_references", dict(file_path=file_path, long_path_warn=long_path_warn))

    @tool
    def get_custom_properties(component: str = "", config: str = "") -> str:
        """Custom properties (file-level and configuration-specific, resolved values) of the active doc or
        of an assembly component (its referenced configuration by default)."""
        return call_mm("get_custom_properties", dict(component=component, config=config))

    @tool
    def bom_export(csv_path: str = "", properties: list[str] = [], top_level_only: bool = False,
                   max_rows: int = 200) -> str:
        """Flat BOM of the ACTIVE assembly: one row per file+configuration with quantity, type and custom
        properties (default: Обозначение, Наименование, Материал, Масса, Раздел; material falls back to the
        part material). Suppressed / excluded-from-BOM skipped. csv_path → also writes ';'-CSV (UTF-8 BOM)."""
        return call_mm("bom_export", dict(csv_path=csv_path, properties=properties, top_level_only=top_level_only,
                     max_rows=max_rows))

    @tool
    def bom_gost(xlsx_path: str = "", assembly_drawing: bool = True, max_rows: int = 200) -> str:
        """Specification (спецификация) of the ACTIVE assembly per GOST 2.106 form 1, one assembly level: sections
        Документация / Сборочные единицы / Детали / Стандартные изделия / Прочие изделия / Материалы / Комплекты from
        the «Раздел» property (else inferred: sub-assembly, part with «Обозначение», name citing ГОСТ/DIN/ISO →
        standard item, the rest → other items; «Детали» on a part without «Обозначение» that cites a standard or lies
        in a purchased-items folder is taken as a template default and inferred — moved_from_details lists them),
        positions through the sections, columns Формат / Зона / Поз. /
        Обозначение / Наименование / Кол. / Примечание (properties of the same names). assembly_drawing adds
        «<Обозначение> СБ Сборочный чертёж». xlsx_path → formatted Excel sheet."""
        xlsx = session.full_path("xlsx_path", xlsx_path)

        def run():
            need_xlsx()
            r = call_mm_json("bom_export", dict(properties=["Обозначение", "Наименование", "Раздел", "Формат", "Зона",
                                                          "Примечание"], top_level_only=True, max_rows=100000))
            doc_props = r.get("document_props") or {}
            moved = []
            spec = gost_spec.build_spec(r.get("rows") or [], doc_props, assembly_drawing, moved)
            out = {"document": r.get("document"), "assembly": doc_props, "sections": []}
            n = 0
            for s in spec:
                items = s["items"][:max(0, max_rows - n)]
                n += len(items)
                out["sections"].append({"section": s["section"], "items": items, "count": len(s["items"])})
            if xlsx:
                out.update(gost_spec.write_xlsx(xlsx, spec, doc_props, r.get("document") or ""))
            missing = [it["name"] for s in spec if s["section"] in ("Сборочные единицы", "Детали")
                       for it in s["items"] if not it["designation"]]
            if moved:
                out["moved_from_details"] = moved[:20]
                out["moved_count"] = len(moved)
            if missing:
                out["warning"] = f"{len(missing)} assembly units/parts without «Обозначение»: {missing[:10]}"
            return dump(out)
        return background("bom_gost", run)

    # ------------------------------------------------------------------ assemblies: change
    @tool
    def set_component_state(components: list[str], hidden: Optional[bool] = None,
                            suppressed: Optional[bool] = None, fixed: Optional[bool] = None) -> str:
        """Hide/show, suppress/resolve, fix/float components of the ACTIVE assembly (only the flags given)."""
        return call_mm("set_component_state", dict(components=components, hidden=hidden, suppressed=suppressed,
                     fixed=fixed))

    @tool
    def set_appearance(rgb: list[int], transparency: float = 0.0, components: list[str] = []) -> str:
        """Color (rgb 0..255) and transparency (0..1) of assembly components (this configuration), or of the
        whole active part when components is empty."""
        return call_mm("set_appearance", dict(rgb=rgb, transparency=transparency, components=components))

    @tool
    def move_component(component: str = "", dx_mm: float = 0.0, dy_mm: float = 0.0, dz_mm: float = 0.0,
                       rx_deg: float = 0.0, ry_deg: float = 0.0, rz_deg: float = 0.0, axis: list[float] = [],
                       angle_deg: float = 0.0, pivot_mm: list[float] = [], absolute: bool = False,
                       transform: list[float] = [], placements: list[dict] = []) -> str:
        """Move a component: translate by d*_mm (absolute=True → set origin to d*_mm); rotate about its own axes
        through its origin by r*_deg, or about ANY axis = [x,y,z] (assembly space) by angle_deg through pivot_mm
        (default: the component's origin) — e.g. swing a door about its hinge line. transform = its full placement,
        13 numbers as insert_component / analyze_assembly give them (rotation rows = its axes, translation mm,
        scale); placements = [{component, transform}, …] places many at once with one rebuild — a mechanism's pose,
        an animation frame. Mates may pull components back on rebuild; fixed components move too."""
        return call_mm("move_component", dict(component=component or None, dx_mm=dx_mm, dy_mm=dy_mm, dz_mm=dz_mm,
                     rx_deg=rx_deg, ry_deg=ry_deg, rz_deg=rz_deg, axis=axis, angle_deg=angle_deg, pivot_mm=pivot_mm,
                     absolute=absolute, transform=transform, placements=placements))

    @tool
    def replace_component(new_file: str, component: str = "", all_instances: bool = True, config: str = "",
                          assembly_path: str = "", old_file: str = "") -> str:
        """Replace a component file. Open assembly: component (+all_instances, config) → ReplaceComponents2
        with mates re-attached. CLOSED assembly: assembly_path + old_file → re-point the reference on disk."""
        return call_mm("replace_component", dict(new_file=new_file, component=component, all_instances=all_instances,
                     config=config, assembly_path=assembly_path, old_file=old_file))

    @tool
    def pack_and_go(target_dir: str, assembly_path: str = "", rename_map: dict[str, str] = {}, prefix: str = "",
                    suffix: str = "", flatten: bool = True, include_drawings: bool = False,
                    discard_unsaved: bool = False) -> str:
        """Copy an assembly (default: active) with all referenced files (+ same-name drawings) into
        target_dir and re-point references in the copies. rename_map {old name|stem: new name}, or
        prefix/suffix for all. CLOSES all documents first (refuses if any are unsaved unless discard_unsaved)."""
        return call_mm("pack_and_go", dict(target_dir=target_dir, assembly_path=assembly_path, rename_map=rename_map,
                     prefix=prefix, suffix=suffix, flatten=flatten, include_drawings=include_drawings,
                     discard_unsaved=discard_unsaved))

    @tool
    def set_custom_properties(properties: dict[str, Optional[str]], component: str = "", config: str = "",
                              config_specific: bool = False, save: bool = False) -> str:
        """Set custom properties (value null = delete) on the active doc or an assembly component's file.
        config_specific=True writes to the configuration tab instead of the file tab."""
        return call_mm("set_custom_properties", dict(properties=properties, component=component, config=config,
                     config_specific=config_specific, save=save))

    @tool
    def configurations(action: Literal["list", "activate", "add", "delete"] = "list", name: str = "",
                       description: str = "", component: str = "") -> str:
        """List / activate / add / delete configurations of the active doc (or a component's file).
        Note: 'add' makes the new configuration active."""
        return call_mm("configurations", dict(action=action, name=name, description=description,
                     component=component))

    @tool
    def form_subassembly(components: list[str], file_path: str = "", into: str = "") -> str:
        """Move components of the ACTIVE assembly into a subassembly (SolidWorks' Form New Subassembly): a NEW .SLDASM
        at file_path, inserted at the parent's origin and fixed (coordinates do not change), or `into` an existing
        subassembly component. Positions are kept; mates among the moved components move along, mates to the rest stay
        in the parent. The components must sit on one level; a suppressed one is resolved for the move (reported).
        The subassembly file is saved, the parent is left modified — save_document keeps it. Answers subassembly,
        moved, max_shift_mm, mates_moved."""
        return call_mm("form_subassembly", dict(components=components, file_path=file_path, into=into))

    @tool
    def export_components(components: list[str], file_path: str) -> str:
        """Export ONLY the listed components of the active assembly (assembly coordinates) to STEP/IGES/
        Parasolid/STL by file_path extension; the others are hidden during the save."""
        return call_mm("export_components", dict(components=components, file_path=file_path))

    # ------------------------------------------------------------------ mates
    @tool
    def list_mates(component: str = "", errors_only: bool = False, max_items: int = 200) -> str:
        """Mates of the ACTIVE assembly (incl. mate folders): name, type, alignment, value_mm / value_deg,
        components, suppressed, and error (over-defined / unsatisfied). Filter by component substring or
        errors_only. Lighter than analyze_assembly(mates)."""
        return call_mm("list_mates", dict(component=component, errors_only=errors_only, max_items=max_items))

    @tool
    def edit_mate(mate: str, value_mm: Optional[float] = None, value_deg: Optional[float] = None,
                  flip: bool = False, suppressed: Optional[bool] = None, new_name: str = "",
                  entities: list[dict] = [], delete: bool = False) -> str:
        """Edit an existing mate by name: value_mm (distance) / value_deg (angle), flip alignment,
        suppress / unsuppress, rename, re-point it at other geometry (entities: new entity specs, same count/kinds),
        or delete. Returns the mate's new state incl. any error.
        Entity spec: {"component","face_index"} | {"component","edge_index"} (indices from analyze_assembly(faces|edges, component)) | {"plane": "Top Plane"} (assembly plane, any language) | {"component","plane"} | {"component"}."""
        return call_mm("edit_mate", dict(mate=mate, value_mm=value_mm, value_deg=value_deg, flip=flip,
                     suppressed=suppressed, new_name=new_name, entities=entities, delete=delete))

    @tool
    def add_mate_advanced(mate_type: Literal["symmetric", "width", "profile_center", "hinge", "gear", "linear_coupler",
                                             "lock", "slot"],
                          entities: list[dict] = [], symmetry_plane: Optional[dict] = None,
                          width_entities: list[dict] = [], tab_entities: list[dict] = [],
                          concentric_entities: list[dict] = [], coincident_entities: list[dict] = [],
                          entity1: Optional[dict] = None, entity2: Optional[dict] = None,
                          reference_component1: str = "", reference_component2: str = "", components: list[str] = [],
                          constraint: str = "", value_mm: Optional[float] = None, percent: Optional[float] = None,
                          ratio_numerator: float = 1.0, ratio_denominator: float = 1.0, reverse: bool = False,
                          lock_rotation: bool = False, alignment: Literal["closest", "aligned", "anti_aligned"] = "closest") -> str:
        """Advanced / mechanical mates in the ACTIVE assembly:
        symmetric (entities ×2 + symmetry_plane) · width (width_entities ×2 + tab_entities ×2, constraint centered/
        free/distance/percent) · profile_center (entities ×2 rectangular/circular faces, value_mm offset, lock_rotation)
        · hinge (concentric_entities ×2 + coincident_entities ×2) · gear (entities ×2 cylinders, ratio) ·
        linear_coupler (entity1/entity2 + reference_component1/2, ratio) · lock (components ×2) · slot (entities).
        Entity spec: {"component","face_index"} | {"component","edge_index"} (indices from analyze_assembly(faces|edges, component)) | {"plane": "Top Plane"} (assembly plane, any language) | {"component","plane"} | {"component"}."""
        return call_mm("add_mate_advanced", dict(mate_type=mate_type, entities=entities, symmetry_plane=symmetry_plane,
                     width_entities=width_entities, tab_entities=tab_entities, concentric_entities=concentric_entities,
                     coincident_entities=coincident_entities, entity1=entity1, entity2=entity2,
                     reference_component1=reference_component1, reference_component2=reference_component2,
                     components=components, constraint=constraint, value_mm=value_mm, percent=percent,
                     ratio_numerator=ratio_numerator, ratio_denominator=ratio_denominator, reverse=reverse,
                     lock_rotation=lock_rotation, alignment=alignment))

    @tool
    def insert_and_mate(file_path: str, mates: list[dict], config: str = "") -> str:
        """Insert a part / sub-assembly into the ACTIVE assembly AND mate it in one call ("put it there").
        mates: [{"type": coincident|concentric|parallel|perpendicular|tangent|distance|angle, "own": entity of the
        NEW component (face_index / edge_index / plane — no component), "target": existing entity spec,
        "alignment": closest|aligned|anti_aligned, "value_mm" | "value_deg"}]. All-or-nothing: if a mate fails the
        component is removed again. Returns component, origin_mm, mates, constraint status.
        Entity spec: {"component","face_index"} | {"component","edge_index"} (indices from analyze_assembly(faces|edges, component)) | {"plane": "Top Plane"} (assembly plane, any language) | {"component","plane"} | {"component"}."""
        return call_mm("insert_and_mate", dict(file_path=file_path, mates=mates, config=config))

    @tool
    def pattern_components(components: list[str], pattern: Literal["linear", "circular"] = "linear", count: int = 3,
                           direction: Optional[dict] = None, spacing_mm: float = 50.0, direction2: Optional[dict] = None,
                           count2: int = 2, spacing2_mm: float = 50.0, axis: Optional[dict] = None,
                           angle_deg: float = 360.0, equal_spacing: bool = True, reverse: bool = False,
                           reverse2: bool = False) -> str:
        """Pattern components of the ACTIVE assembly (local component pattern). linear: direction = a LINEAR EDGE or
        axis entity, count, spacing_mm (+ optional direction2/count2/spacing2_mm). circular: axis = axis / cylindrical
        face / circular edge entity, count, angle_deg (total with equal_spacing, else between instances).
        Entity spec: {"component","face_index"} | {"component","edge_index"} (indices from analyze_assembly(faces|edges, component)) | {"plane": "Top Plane"} (assembly plane, any language) | {"component","plane"} | {"component"}."""
        return call_mm("pattern_components", dict(components=components, pattern=pattern, count=count, direction=direction,
                     spacing_mm=spacing_mm, direction2=direction2, count2=count2, spacing2_mm=spacing2_mm, axis=axis,
                     angle_deg=angle_deg, equal_spacing=equal_spacing, reverse=reverse, reverse2=reverse2))

    @tool
    def constraint_status(top_only: bool = True, problems_only: bool = False, max_items: int = 300) -> str:
        """Per component of the ACTIVE assembly: fixed / fully_defined / under_defined / over_defined and its
        mate count, plus a summary and the number of mates with errors. problems_only hides fixed/fully."""
        return call_mm("constraint_status", dict(top_only=top_only, problems_only=problems_only, max_items=max_items))

    # ------------------------------------------------------------------ building an assembly (mm)
    # ---------------------------------------------------------------------------
    # Tool: open_new_assembly
    # ---------------------------------------------------------------------------
    @tool
    def open_new_assembly(template_path: str = "") -> str:
        """Open a new blank SolidWorks ASSEMBLY document (the twin of open_new_part).
        Use before insert_component / add_mate. template_path: optional .asmdot template
        (default: the user-configured assembly template)."""
        return call("open_new_assembly", {"template_path": template_path})

    # ---------------------------------------------------------------------------
    # Tool: analyze_assembly  (read tool)
    # ---------------------------------------------------------------------------
    @tool
    def analyze_assembly(
        analysis_type: Literal["components", "components_flat", "mates", "faces", "edges"],
        component: str = "",
    ) -> str:
        """Analyze the active ASSEMBLY document (read-only, does NOT change state).
        analysis_type='components': top-level component occurrences in FEATURE-TREE order — per
            instance: name (instance-numbered, e.g. 'Shaft-1'), source file path, config, fixed /
            suppressed flags, children count (a subassembly — out of scope, recorded as a gap), and
            the FULL transform (13 numbers: 3x3 rotation row-major + translation in mm + scale)
            — the position ground truth for round-trip verification.
        analysis_type='components_flat': LEAF occurrences across ALL nesting levels (depth-first
            tree order, transforms in ROOT space, each with its parent's name) — flattens a
            wrapper/subassembly level for ground-truth acquisition.
        analysis_type='mates': all mates in CREATION order — type and alignment from the SolidWorks
            ENUMS (locale-proof), the value of distance (mm) / angle (deg) mates, and per mate entity: owning
            component, entity kind (plane/cylinder/circle/...), and its geometric params in ASSEMBLY
            space (location + direction + radii).
        analysis_type='faces' / 'edges': ONE component's face/edge list with stable indices — feeds
            add_mate's index-based entity selection. Requires `component` (the Name2 from
            'components'). Coordinates are COMPONENT-LOCAL (the payload carries the transform).
        component with 'components' / 'components_flat': only that component and what is inside it (a
            subassembly's name), else every name containing the text."""
        return call("analyze_assembly", {"analysis_type": analysis_type, "component": component})

    # ---------------------------------------------------------------------------
    # Tool: insert_component  (build tool)
    # ---------------------------------------------------------------------------
    @tool
    def insert_component(
        file_path: str,
        x: float = 0.0,
        y: float = 0.0,
        z: float = 0.0,
        config: str = "",
        fixed: bool = False,
        transform: list[float] = [],
    ) -> str:
        """Insert a part (.SLDPRT) or a sub-assembly (.SLDASM) as a component of the ACTIVE assembly
        (open_new_assembly / open_document first). Coordinates in mm.
        x/y/z: where the component's ORIGIN is placed (identity rotation) — used when transform is not given.
        transform: OPTIONAL full placement, 13 numbers
            (3x3 rotation row-major + translation xyz in mm + scale — exactly the layout
            analyze_assembly(components) reports). Needed for rotated placements.
        fixed: True fixes the occurrence (its transform becomes authoritative); False leaves it
            floating for mates to position. The state is applied explicitly either way.
        Returns the runtime component name (e.g. 'Shaft-2') + fixed/transform readbacks."""
        return call("insert_component", {"file_path": file_path, "x": x, "y": y, "z": z, "fixed": fixed,
                                         "config": config, "transform": transform})

    # ---------------------------------------------------------------------------
    # Tool: add_mate  (build tool)
    # ---------------------------------------------------------------------------
    @tool
    def add_mate(
        mate_type: Literal["coincident", "concentric", "perpendicular", "parallel",
                           "tangent", "distance", "angle", "lock"],
        a_component: str,
        a_index: int,
        b_component: str,
        b_index: int,
        a_kind: Literal["face", "edge"] = "face",
        b_kind: Literal["face", "edge"] = "face",
        alignment: Literal["aligned", "anti_aligned", "closest"] = "closest",
        flip: bool = False,
        value: float = 0.0,
    ) -> str:
        """Add a mate between two component entities in the ACTIVE assembly.
        Entity selection is INDEX-based (robust — coordinate picks miss real geometry): each side is
        (component name from analyze_assembly(components), kind 'face'|'edge', index from
        analyze_assembly(faces|edges, component=...)).
        value: mm for distance mates, DEGREES for angle mates (ignored otherwise).
        alignment 'closest' lets SolidWorks pick the nearer solution; the mates reader reports the
        RESOLVED alignment afterwards. DISTANCE mates (esp. plane-to-cylinder) often land on the wrong
        side with 'closest' — check the returned transforms and fix with edit_mate(mate, flip=True).
        Returns the mate name + both components' post-rebuild transforms — compare against the
        original's readback after EVERY mate (the stop-check discipline)."""
        # the server: meters for a distance, degrees for an angle; the other mates take no value
        value = {"distance": value * units.M, "angle": value}.get(mate_type)
        return call("add_mate", {"mate_type": mate_type, "alignment": alignment,
                                 "a_component": a_component, "a_kind": a_kind, "a_index": a_index,
                                 "b_component": b_component, "b_kind": b_kind, "b_index": b_index,
                                 "flip": flip, "value": value})

    # ---------------------------------------------------------------------------
    # Tool: save_body_as_part  (flattened-assembly reconstruction)
    # ---------------------------------------------------------------------------
    @tool
    def save_body_as_part(body_index: int, file_path: str) -> str:
        """Extract ONE solid body of the ACTIVE multibody part into its own .SLDPRT file.
        body_index: from analyze_model(analysis_type='bodies'). The body's geometry stays in the
        SOURCE coordinates (recover the instance transform separately); the new part is saved to
        file_path and closed, and the source document is re-activated. Use for flattened assembly
        imports whose per-part files don't exist: one file per DISTINCT body (fingerprint-deduped),
        then instances reference it with recovered transforms."""
        return call("save_body_as_part", {"body_index": body_index, "file_path": file_path})
