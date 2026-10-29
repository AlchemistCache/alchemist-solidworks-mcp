"""Part features: extrude / cut / revolve / sweep / loft, rib, Hole Wizard, fillet / chamfer, reference
geometry (also a plane at an angle, an axis through two points), patterns and mirror, suppress / delete / rename,
a pipe along a 3D polyline, body visibility. Units: MILLIMETRES and DEGREES (converted to SI for the server in units.py).
Registered by registry.register_all(); call plumbing in bridge.py."""
from typing import Annotated, Literal

from pydantic import Field

from bridge import call, call_mm


def register(mcp, tool):
    # ---------------------------------------------------------------------------
    # Tool: extrude_feature
    # ---------------------------------------------------------------------------
    @tool
    def extrude_feature(
        depth: Annotated[float, Field(
            ge=0, description="Extrusion depth in mm (required > 0 for boss/cut)")] = 0.0,
        feature_type: Literal["boss", "cut", "revolve", "revolve_cut", "sweep"] = "boss",
        angle: Annotated[float, Field(
            gt=0, le=360, description="Revolve angle in DEGREES")] = 360.0,
        axis_x1: float | None = None,
        axis_y1: float | None = None,
        axis_x2: float | None = None,
        axis_y2: float | None = None,
        path_sketch: str = "",
        reverse: bool = False,
        through: bool = False,
        up_to_face_index: int = -1,
        mid_plane: bool = False,
        direction: list[float] = [],
    ) -> str:
        """Extrude/feature the active sketch profile.
        feature_type='boss' (default): solid extrusion, requires depth.
        feature_type='cut': material removal, requires depth and existing solid body.
        feature_type='revolve': solid of revolution — angle in DEGREES (default 360 = full revolve); the axis is the
            sketch's centerline, or the line through the midpoint of axis_x1/y1–axis_x2/y2 (mm, SKETCH coordinates —
            works on offset / turned planes too).
        feature_type='revolve_cut': the same, removing material (a groove / turned recess in an existing body).
        feature_type='sweep': sweeps profile along a path — requires path_sketch (name of the path sketch).
        A loft through several profile sketches is the loft tool.
        END CONDITION (boss/cut) — pick ONE; depth is required only for BLIND or mid_plane:
          through=True          through-all; depth ignored. Use it instead of guessing a depth.
          up_to_face_index >= 0 terminates exactly ON that model face (index from
                                analyze_model('faces'), same indexing as create_sketch face_index);
                                depth ignored. -1 (default) = off.
          mid_plane=True        SYMMETRIC about the sketch plane; depth is the TOTAL width.
        direction=[x,y,z] (boss/cut, model space): the side to build toward — read against the sketch's normal, it
            sets reverse for you (use it instead of guessing reverse). reverse: flip the default side. DIRECTION IS
            THE SILENT ONE (a wrong side leaves volume, area and topology identical): the answer's grew_toward says
            where the model grew ("+Z 40 mm (to 140)") and bbox_mm is the model's box after the feature.
        Sheet metal: a 'cut' on a sheet-metal body (holes after a bend included) is handled
            automatically — Normal Cut, inserted before the Flat-Pattern; no special params.
        The answer carries {volume_mm3, faces, edges, bbox_mm, grew_toward} after the feature — verify the step
            from that rather than a separate analyze_model; a cut that removes more than half of the body warns.
        Exits sketch mode automatically before executing."""
        return call(
            "extrude_feature",
            {
                "depth": depth,
                "feature_type": feature_type,
                "angle": angle,
                "axis_x1": axis_x1, "axis_y1": axis_y1,
                "axis_x2": axis_x2, "axis_y2": axis_y2,
                "path_sketch": path_sketch,
                "reverse": reverse,
                "through": through,
                "up_to_face_index": up_to_face_index,
                "mid_plane": mid_plane,
                "direction": direction,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: create_rib
    # ---------------------------------------------------------------------------
    @tool
    def create_rib(
        thickness: Annotated[float, Field(gt=0, description="Rib thickness in mm")],
        two_sided: bool = True,
        reverse_thickness_dir: bool = False,
        reverse_material_dir: bool = False,
        is_norm_to_sketch: bool = False,
    ) -> str:
        """Create a RIB from the ACTIVE sketch (consumes it, like extrude_feature). The rib profile
        is OPEN geometry — typically a single line (e.g. the diagonal between an L-bracket's legs);
        SolidWorks extends it to the surrounding walls and thickens it.
        thickness: rib thickness in mm.
        two_sided: True (default) thickens symmetrically on both sides of the sketch; False = single
            side (then reverse_thickness_dir picks which side).
        reverse_material_dir: flip which side of the profile the rib material FILLS toward. Wrong
            direction is auto-recovered: if no feature results, the tool retries flipped (your
            explicit value is tried first).
        is_norm_to_sketch: False (default) = extrusion parallel to the sketch (the classic line-rib
            on a mid/symmetry plane); True = normal to the sketch.
        Draft is deliberately not exposed (grow on demand). The answer carries
        {volume_mm3, faces, edges} — verify the step from this. Requires an existing
        solid body for the rib to attach to."""
        return call(
            "create_rib",
            {
                "thickness": thickness,
                "two_sided": two_sided,
                "reverse_thickness_dir": reverse_thickness_dir,
                "reverse_material_dir": reverse_material_dir,
                "is_norm_to_sketch": is_norm_to_sketch,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: hole_wizard
    # ---------------------------------------------------------------------------
    @tool
    def hole_wizard(
        hole_type: Literal["counterbore", "countersink", "hole", "tap"],
        standard: Literal["ansi_inch", "ansi_metric", "iso", "din", "jis"],
        size: str,
        points: list[list[float]],
        face_index: int = -1,
        face_x: float | None = None,
        face_y: float | None = None,
        face_z: float | None = None,
        fastener_type: str | int = "default",
        end_condition: Literal["blind", "through_all"] = "blind",
        depth: float = 0.0,
        diameter: float = 0.0,
        cbore_diameter: float = -1.0,
        cbore_depth: float = -1.0,
        csink_diameter: float = -1.0,
        csink_angle: float = -1.0,
        thread_depth: float = -1.0,
        reverse: bool = False,
    ) -> str:
        """Place a STANDARD SOLIDWORKS Hole Wizard hole (real counterbore / countersink / clearance / tapped
        hole with a thread callout) via IFeatureManager.HoleWizard5, at one or more points on a planar face.
        Target face: face_index (from analyze_model('faces')) OR a point face_x/face_y/face_z ON the face.
        points: [[x,y,z], …] hole centres (mm) lying ON that face, e.g. [[10,50,0]].
        hole_type: counterbore | countersink | hole (clearance) | tap (tapped).
        standard: ansi_inch | ansi_metric | iso | din | jis. fastener_type: omit for the default of that
            standard + hole_type (ISO: tap 147, clearance 144, cap-screw cbore 139, flat-head csink 140;
            ANSI metric 43/40/33/35), or pass a swWzdHoleStandardFastenerTypes_e integer. A fastener or
            size that does not belong to the standard + hole_type FAILS (HOLE_WIZARD_FAILED).
        size: e.g. 'M6' (metric) or '1/4-20' / '#10-24' (inch tap). Metric tap pitch is added automatically
            (coarse 'M6' -> 'M6x1.0'; or give 'M6x1'); for non-tap holes a pitch is stripped.
        end_condition: blind (needs depth) | through_all.
        depth/diameter in mm (diameter 0 = the standard's size); cbore_diameter/cbore_depth mm;
        csink_diameter mm and csink_angle DEGREES (included angle); thread_depth mm (tap only).
        Call it AFTER the body exists.
        The answer carries the created feature (created) and {volume_mm3, faces, edges}."""
        point = None not in (face_x, face_y, face_z)   # a face point counts only when complete
        return call("hole_wizard", {   # a negative size / index = "not given": None, which session.given drops
            "hole_type": hole_type, "standard": standard, "size": size, "points": points,
            "fastener_type": fastener_type, "end_condition": end_condition,
            "depth": depth, "diameter": diameter, "reverse": reverse,
            "face_index": face_index if face_index >= 0 else None,
            "face_x": face_x if point else None, "face_y": face_y if point else None, "face_z": face_z if point else None,
            **{k: (v if v >= 0 else None) for k, v in (
                ("cbore_diameter", cbore_diameter), ("cbore_depth", cbore_depth), ("csink_diameter", csink_diameter),
                ("csink_angle", csink_angle), ("thread_depth", thread_depth))},
        })

    # ---------------------------------------------------------------------------
    # Tool: add_edge_feature
    # ---------------------------------------------------------------------------
    @tool
    def add_edge_feature(
        feature_type: Literal["fillet", "chamfer"],
        radius_or_distance: Annotated[float, Field(gt=0, description="Fillet radius / chamfer FIRST-face setback (D1) in mm")],
        edge_indices: list[int] = [],
        ex: float = 0.0,
        ey: float = 0.0,
        ez: float = 0.0,
        edge_points: list[list[float]] = [],
        chamfer_type: Literal["distance_angle", "distance_distance"] = "distance_angle",
        angle: Annotated[float, Field(gt=0, lt=90, description="Chamfer angle in DEGREES (distance_angle mode only; default 45)")] = 45.0,
        distance2: Annotated[float, Field(ge=0, description="Chamfer SECOND-face setback (D2) in mm (distance_distance mode only; must be > 0 there)")] = 0.0,
        chamfer_flip: bool = False,
    ) -> str:
        """Apply a 3D edge modifier to solid body edges (post-extrusion; lengths and points in mm, angles in
        DEGREES). Distinct from sketch fillet/chamfer.
        feature_type='fillet': rounds selected edge(s) with given radius_or_distance.
        feature_type='chamfer': cuts a chamfer on selected edge(s). Two modes via chamfer_type:
          - 'distance_angle' (default): radius_or_distance = setback (D1), angle = the chamfer angle in DEGREES
            (default 45 → the classic equal 45° chamfer). angle is ignored in the other mode.
          - 'distance_distance': radius_or_distance = first-face setback (D1), distance2 = second-face setback
            (D2, mm, required > 0). A distance-distance chamfer is DIRECTIONAL — if D1/D2 land on the wrong
            faces (the chamfer leans the wrong way), set chamfer_flip=True to swap the sides (FlipDirection).
        PREFERRED edge selection: edge_indices — integer indices from analyze_model(analysis_type='edges'),
        e.g. [3, 5]. This selects edges directly (no coordinate pick), so it works on crowded or CONCAVE edges
        (inner-corner / small-radius step edges) that a coordinate pick can't disambiguate. Takes priority over
        edge_points / ex-ey-ez.
        Fallback — single edge: ex/ey/ez (3D point on the edge); multiple edges: edge_points [[x,y,z], …].
        All coordinates/distances in mm. Requires an active part document with an existing solid body."""
        chamfer = feature_type == "chamfer"   # the chamfer options are sent for a chamfer only
        return call("add_edge_feature", {
            "feature_type": feature_type,
            "radius_or_distance": radius_or_distance,
            "ex": ex, "ey": ey, "ez": ez,
            "edge_points": edge_points,
            "edge_indices": edge_indices,
            "chamfer_type": chamfer_type if chamfer else None,
            "angle": angle if chamfer else None,
            "distance2": distance2 if chamfer else None,
            "chamfer_flip": chamfer_flip if chamfer else None,
        })

    # ---------------------------------------------------------------------------
    # Tool: add_reference_geometry
    # ---------------------------------------------------------------------------
    @tool
    def add_reference_geometry(
        type: Literal["plane", "axis", "point"],
        ref_plane_name: str = "",
        offset: float = 0.0,
        entity1_name: str = "",
        entity1_type: str = "PLANE",
        entity2_name: str = "",
        entity2_type: str = "PLANE",
        px: float = 0.0,
        py: float = 0.0,
        pz: float = 0.0,
    ) -> str:
        """Create reference geometry (plane, axis, or point) in the active part.
        type='plane': offset plane from ref_plane_name by offset (mm). E.g. ref_plane_name='Front Plane', offset=50.
        type='axis': axis at intersection of two entities. Provide entity1_name, entity1_type, entity2_name, entity2_type (e.g. 'Top Plane'/'PLANE' and 'Right Plane'/'PLANE').
        type='point': reference point at vertex (px, py, pz, mm). Coordinates must match an existing vertex exactly.
        Returns the created feature name (e.g. 'Plane1', 'Axis1', 'Point1')."""
        return call(
            "add_reference_geometry",
            {
                "type": type,
                "ref_plane_name": ref_plane_name,
                "offset": offset,
                "entity1_name": entity1_name,
                "entity1_type": entity1_type,
                "entity2_name": entity2_name,
                "entity2_type": entity2_type,
                "px": px, "py": py, "pz": pz,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: create_pattern
    # ---------------------------------------------------------------------------
    @tool
    def create_pattern(
        pattern_type: Literal["linear", "circular", "mirror"],
        feature_name: str = "",
        spacing: Annotated[float, Field(
            gt=0, description="Linear pattern spacing in mm")] = 10.0,
        count: Annotated[int, Field(
            ge=2, description="Total instances incl. seed (>= 2)")] = 2,
        direction: Literal["X", "Y", "Z"] = "X",
        count2: Annotated[int, Field(
            ge=1, description="Second-direction instances (1 = off)")] = 1,
        spacing2: Annotated[float, Field(
            gt=0, description="Second-direction spacing in mm")] = 10.0,
        flip: bool = False,
        axis_name: str = "",
        angle: Annotated[float, Field(
            gt=0, le=360, description="Circular pattern angle in DEGREES")] = 90.0,
        equal_spacing: bool = True,
        features: list[str] = [],
        plane: str = "",
        geometry_pattern: bool = False,
    ) -> str:
        """Create a linear, circular, or mirror feature pattern. The seeds: feature_name and / or features (a list —
        all of them go into ONE pattern).
        pattern_type='linear': repeats feature_name along direction ('X', 'Y', or 'Z') with spacing (mm) and count instances.
            flip=True patterns toward the NEGATIVE axis (default False = positive).
            Direction maps to default planes: X→Right Plane normal, Y→Top Plane normal, Z→Front Plane normal.
            KNOWN LIMIT: count2/spacing2 are accepted but the second direction has no D2 entity wired —
            two-direction grids do NOT work in one call; compose a grid as a pattern OF a pattern instead.
        pattern_type='circular': repeats feature_name around axis_name (reference axis, e.g. 'Axis1') with count instances.
            Create the axis first with add_reference_geometry(type='axis', ...).
            equal_spacing=True (default): angle is the TOTAL spread (use 360 for a full ring); count instances are
                evenly divided across it and NEVER overlap (count distinct == count).
            equal_spacing=False: angle is the spacing BETWEEN adjacent instances. count*angle can exceed 360, in which
                case later instances WRAP and overlap earlier ones, so the number of DISTINCT instances < count.
            Round-trip: analyze_model reports a CirPattern's instances, equal_spacing, spacing_deg plus
            the EFFECTIVE distinct_instances and a `wraps` flag. Either replay the stored form verbatim,
            or — when it wraps — use the equivalent full ring (count=distinct_instances, angle=360,
            equal_spacing=True). Both give the same geometry.
        pattern_type='mirror': mirrors one or more FEATURES about a plane. features = feature tree names,
            e.g. ["Edge-Flange1", "Sketched Bend2"] (feature_name works for a single
            feature). plane = the mirror plane name ('Right Plane' default; canonical English default-plane
            names work on any localization, or a created reference plane's name). geometry_pattern=True
            mirrors the geometry without solving each feature (faster; default False = SW default).
            Round-trip: analyze_model(features) reports a MirrorPattern's mirror:{plane, features} — replay
            those values verbatim. feature_name/spacing/count/direction/axis params are ignored for mirror.
        The answer carries {volume_mm3, faces, edges} after the pattern — verify from
            this instead of a separate analyze_model.
        Returns the created pattern feature name."""
        return call(
            "create_pattern",
            {
                "pattern_type": pattern_type,
                "feature_name": feature_name,
                "spacing": spacing,
                "count": count,
                "direction": direction,
                "count2": count2,
                "spacing2": spacing2,
                "flip": flip,
                "axis_name": axis_name,
                "angle": angle,
                "equal_spacing": equal_spacing,
                "features": features,
                "plane": plane,
                "geometry_pattern": geometry_pattern,
            },
        )

    # ---------------------------------------------------------------------------
    # Tool: edit_feature
    # ---------------------------------------------------------------------------
    @tool
    def edit_feature(
        feature_name: str,
        action: Literal["suppress", "unsuppress", "delete", "rename"],
        new_name: str = "",
        with_children: bool = False,
    ) -> str:
        """Structurally edit an existing feature by name (suppress / unsuppress / delete / rename).
        feature_name: exact feature-tree name, e.g. 'Boss-Extrude1', 'Cut-Extrude1', 'CirPattern1'
              (get the names from analyze_model(analysis_type='features')).
        action='suppress':   removes the feature (and anything dependent on it) from the build WITHOUT deleting it.
        action='unsuppress': brings a suppressed feature back into the build.
        action='delete':     permanently removes the feature (any type, incl. sketches and reference geometry) and its
                             own sketch; features that depend on it STAY (answer: dependents_kept, warning when they
                             now fail) unless with_children=True. A feature still there afterwards is DELETE_FAILED
                             (e.g. a sketch a feature uses: delete the feature).
        action='rename':     renames the feature; requires new_name.
        new_name: required only for action='rename' (ignored otherwise).
        Bumps state_version. WARNING: suppress and delete CHANGE the model topology, so any coordinate /
        edge / face selection captured earlier may no longer resolve — re-run analyze_model
        (analysis_type='edges' or 'features') after a structural edit before selecting geometry again."""
        return call(
            "edit_feature",
            {"feature_name": feature_name, "action": action, "new_name": new_name, "with_children": with_children or None},
        )

    @tool
    def list_materials(query: str = "", limit: int = 40) -> str:
        """Materials in SolidWorks' material databases: name, library, class. query = words that must all appear
        ("6061", "nylon", "aisi 304"). set_part_material takes the exact name (and its library)."""
        return call_mm("list_materials", dict(query=query, limit=limit))

    # ------------------------------------------------------------------ reference geometry, sweep / loft, bodies (mm, degrees)
    @tool
    def add_plane_angle(base_plane: str, angle_deg: float, axis: str = "", edge_index: int = -1, name: str = "") -> str:
        """Reference plane through an axis (by name) or an edge (analyze_model(edges) index) at angle_deg to
        base_plane (e.g. 'Front Plane'). The axis/edge must lie in or parallel to the base plane."""
        return call_mm("add_plane_angle", dict(base_plane=base_plane, angle_deg=angle_deg, axis=axis,
                     edge_index=None if edge_index < 0 else edge_index, name=name))

    @tool
    def add_axis_two_points(p1_mm: list[float], p2_mm: list[float], name: str = "") -> str:
        """Reference axis through two model points [x,y,z] mm (made from a 3D-sketch line)."""
        return call_mm("add_axis_two_points", dict(p1_mm=p1_mm, p2_mm=p2_mm, name=name))

    @tool
    def sweep_pipe(points_mm: list[list[float]], diameter_mm: float, wall_mm: float = 0.0,
                   bend_radius_mm: float = 0.0, merge: bool = True) -> str:
        """Pipe / hose / cable / rod along a 3D polyline (points [[x,y,z],…] mm) with a circular profile.
        wall_mm > 0 = tube; bend_radius_mm > 0 rounds the polyline corners (pipe bends)."""
        return call_mm("sweep_pipe", dict(points_mm=points_mm, diameter_mm=diameter_mm, wall_mm=wall_mm,
                     bend_radius_mm=bend_radius_mm, merge=merge))

    @tool
    def loft(profile_sketches: list[str], closed: bool = False, merge: bool = True) -> str:
        """Lofted boss through ≥2 closed profile sketches (names, in order, on different planes)."""
        return call_mm("loft", dict(profile_sketches=profile_sketches, closed=closed, merge=merge))

    @tool
    def set_body_visibility(bodies: list[str] = [], hidden: bool = True) -> str:
        """Hide/show bodies of the active (multibody) part by name; empty list = all. Returns all body names."""
        return call_mm("set_body_visibility", dict(bodies=bodies, hidden=hidden))
