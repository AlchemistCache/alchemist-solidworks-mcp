"""Reading the model: analyze_model, comparing two parts / assemblies (compare_parts / compare_assemblies), the
driving dimension of a face. Units shown to the model: mm, mm², mm³, degrees (units.py); the reads underneath
stay SI.
Registered by registry.register_all(); call plumbing in bridge.py."""
import os
from typing import Literal


import face_dimension
import units
from adapter_log import write as _log
from analysis_reads import analysis_items, analysis_payload, kv_dict
from face_compare import diff_faces, face_desc
import session
import verify
from bridge import call, call_raw, dump
from response_mapper import fail, failure_of


def _make_active(doc: str, param: str) -> None:
    """Make `doc` the active document — one rule for compare_*: a file path is opened, or activated
    when it is already open; anything else is the title of an open document. Fails with the server's own error."""
    if os.path.isfile(doc):
        path = session.full_path(param, doc)
        opened = call_raw("open_document", {"file_path": path})
        if opened.get("status") == "COMPLETED":
            return
        if call_raw("activate_document", {"title": os.path.basename(path)}).get("status") == "COMPLETED":
            # a title names no folder: the open gear.SLDPRT may be another folder's (comparing it with itself — PASS)
            g = call_raw("list_open_documents", {}).get("result_geometry") or {}
            act = next((d for d in g.get("documents", []) if d.get("title") == g.get("document")), {})
            if os.path.normcase(os.path.abspath(act.get("path") or "")) == os.path.normcase(os.path.abspath(path)):
                return
            raise fail("SAME_TITLE_OPEN", f"{param} '{path}' cannot be opened: '{act.get('path') or g.get('document')}' "
                                          "with the same name is open — close it first (close_document).")
        raise failure_of(opened, f"opening {param} '{path}'")
    activated = call_raw("activate_document", {"title": doc})
    if activated.get("status") != "COMPLETED":
        raise failure_of(activated, f"{param} '{doc}' is neither a file nor the title of an open document")


def _mm(v, power: int = 1):
    """An SI length (power 1), area (2) or volume (3) in mm units; None stays None."""
    return None if v is None else v / units.M ** power


def _r(v, digits: int = 3):
    return None if v is None else round(v, digits)


def _read(analysis_type: str, tool: str = "analyze_model", **params):
    """One analysis read of the active document: key=value modes as numbers, JSON modes as the payload object."""
    response = call_raw(tool, {"analysis_type": analysis_type, "name": "", **params})
    if analysis_type in ("mass_properties", "geometry"):
        return kv_dict(analysis_items(response))
    return analysis_payload(response)



def register(mcp, tool):
    # ---------------------------------------------------------------------------
    # Tool: analyze_model
    # ---------------------------------------------------------------------------
    @tool
    def analyze_model(
        analysis_type: Literal["geometry", "bodies", "bbox", "edges", "faces", "features", "sketch", "feature_map"],
        name: str = "",
        from_feature: str = "",
        to_feature: str = "",
        near: list[float] = [],
        k: int = 0,
        axis: list[float] = [],
    ) -> str:
        """Analyze the active SolidWorks part document (read-only, does NOT change state). Lengths and
        coordinates in mm, areas area_mm2, volumes volume_mm3, angles degrees. Mass, volume, area and centre of
        gravity: the mass_properties tool.
        analysis_type='geometry': returns bodies, faces, edges, vertices counts of all solid bodies.
        analysis_type='bbox': the model's box — bbox_mm [x0,y0,z0,x1,y1,z1] of the solid bodies and size_mm.
        analysis_type='bodies': PER-BODY fingerprints for multibody parts (e.g. a flattened assembly
            STEP import): volume, area, centroid, edge/vertex counts, and the body's face-index RANGE
            in the same enumeration 'faces' walks — segment the faces list per body with it.
        analysis_type='edges': returns a JSON object listing EVERY solid edge with its start/end/MIDPOINT 3D coords
            in mm (closed/circular edges also report length) and a stable index `i`. Use
            the index `i` for add_edge_feature(edge_indices=...) — robust for crowded/concave edges — or a MIDPOINT
            for coordinate-based selection (e.g. edge_flange's ex/ey/ez). On a very large part (many hundreds of edges)
            this is the slowest mode; prefer 'features' for a general understanding. TARGETED (recommended when you
            already know roughly WHERE): pass near='[x,y,z]' (+ optional k, axis) to get only the k nearest edges to
            a point instead of the full dump — see near/k/axis below.
        analysis_type='faces': returns a JSON object listing EVERY solid face with a stable index `i`; planar faces
            also carry normal, area_mm2, and a representative on-plane point (mm). Use the
            index `i` for create_sketch(on_face=True, face_index=i) — robust where a coordinate pick is ambiguous
            (e.g. a revolve end-cap / shaft end whose centre lies on the axis). TARGETED: pass near='[x,y,z]'
            (+ optional k, axis) for only the k nearest faces to a point — see near/k/axis below.
        analysis_type='features': the part's COMPACT RECIPE — this is the default "understand the part" read. Returns
            the ordered feature tree (name/type; suppressed shown only when true), each feature's driving dimensions
            (deduped: value_mm or value_deg), a per-sketch SUMMARY (segment counts + an inline
            {cx,cy,r} for single-full-circle profiles), pattern semantics (instances / spacing_deg / distinct_instances
            / wraps), and equations / globals. Each sketch reports its plane as {ref, offset} where ref is the
            canonical English default plane ('Front Plane'/'Top Plane'/'Right Plane') and offset is the signed
            distance along its normal (offset 0 = that default plane itself; offset != 0 = a parallel face/plane
            at that height — sketch on the face there). Each extrude/cut reports extrude:{end (blind/through_all/
            ...), depth (blind only), reversed (present only when the direction is flipped)} so you know HOW it
            was built; for direction use the sketch's plane.offset (offset != 0 ⇒ a face, so a cut goes into the
            part). Features are listed in tree (history) ORDER — reproduce them in that exact order (order
            matters wherever features overlap). It does NOT dump every sketch segment's coordinates — that keeps the payload small
            and is enough to understand the part and build dimension/pattern variants. Use this first.
        analysis_type='sketch' (requires name=...): ONE sketch's FULL geometry — every segment's coordinates (mm)
            plus its plane. Use this only when you actually need exact sketch geometry (e.g. to reproduce
            an irregular profile); get the sketch name from the 'features' read first. name = the sketch feature name,
            e.g. 'Sketch2'.
        analysis_type='feature_map': per-feature geometry ATTRIBUTION — deterministically answers "which edges/faces
            did each feature act on?" (e.g. WHICH edge a fillet/chamfer was applied to — never guess an anchor). Walks
            the tree base→end with the rollback bar and diffs the topology between stops INSIDE the tool; returns one
            compact JSON {feature_count, map:[{feature, type, delta:{faces,edges,vertices}, consumed_edges:[{mid,len}],
            created_faces:[{point, normal?, planar, area_mm2}]}]} (mm). consumed_edges = edges that existed
            BEFORE the feature and were consumed by it (both endpoints gone; merely trimmed neighbours excluded) — use
            a consumed edge's `mid` as the fillet/chamfer anchor `near` point (it references the PRE-feature geometry,
            exactly what an edge or face pick needs). created_faces = genuinely new surfaces (trimmed existing planes excluded).
            Sketches/planes are listed without a delta; suppressed features are skipped. Optional from_feature/to_feature
            (tree names) limit the walk to a range. NON-DESTRUCTIVE: the rollback bar is restored to the end, nothing is
            saved — but it does rebuild the model feature-by-feature, so it is the slowest mode on big trees.

        near / k / axis (edges and faces modes ONLY — a TARGETED read instead of the full dump): when you already
            know approximately where the geometry is (the common build-time case — you almost always do), pass
            near=[x,y,z] (mm) to return only the nearest entities to that point, each annotated
            with `dist` (mm) and still carrying its STABLE full-enumeration index `i` (so selection is unchanged).
            k (default 0 = no cap): keep only the k nearest. axis=[x,y,z] (optional direction): keep only entities
            whose direction is ~parallel to axis — for faces that's the planar-face NORMAL (e.g. axis=[0,0,1] → only
            faces on +Z planes), for edges the chord direction. The response adds total_edge_count/total_face_count so
            you know how many were filtered out. Omitting near preserves the exact historical full-dump behavior.
        Does NOT increment state_version."""
        return call("analyze_model", {"analysis_type": analysis_type, "name": name,
                                       "from_feature": from_feature, "to_feature": to_feature,
                                       "near": near, "k": k, "axis": axis})

    # ---------------------------------------------------------------------------
    # Tool: compare_parts  (adapter-only — are two parts the same?)
    # ---------------------------------------------------------------------------
    @tool
    def compare_parts(doc_a: str, doc_b: str, detail: str = "") -> str:
        """Objectively diff two part documents — "are these the same part?" (an original and a rebuild,
        two revisions, a part from a drawing against its model).

        doc_a / doc_b: EITHER an absolute .SLDPRT path (opened/activated from disk) OR the TITLE of
            an already-open document (e.g. 'Part4' for an unsaved rebuild). doc_a is the REFERENCE
            (deltas are relative to it — normally the original; doc_b = the rebuild).

        For each doc it runs analyze_model(geometry + mass_properties) and reports topology
        (bodies/faces/edges/vertices), volume, surface area and CG side by side with deltas, plus
        the verdict: topology EXACT AND |ΔV| <= 1% AND |ΔA| <= 1%. Read-only geometry-wise (activation may switch the active
        document; doc_b is left active). bbox comparison: pending (analyze doesn't expose it yet).

        detail='faces' (optional): ALSO run analyze_model(faces) on both and list the surfaces that
            are EXTRA (in doc_b only) or MISSING (in doc_a only), matched by supporting plane (normal
            + offset) for planar faces and by (axis, radius) for cylinders. This is the ONE diagnosis a
            point-query can't do — it turns "which face did my rebuild add/drop?" into one call instead
            of manually pairing two full face lists. Faces are reported with their key attributes + the
            stable index `i` in their own document."""
        want_faces = detail == "faces"

        def read(doc: str, param: str):
            _make_active(doc, param)
            return _read("geometry"), _read("mass_properties"), (_read("faces").get("faces", []) if want_faces else None)

        geo_a, mass_a, faces_a = read(doc_a, "doc_a")
        geo_b, mass_b, faces_b = read(doc_b, "doc_b")
        v = verify.part_verdict(geo_a, geo_b, mass_a, mass_b)
        out = {"verified_criteria": "PASS" if v["verified"] else "FAIL", "reference": doc_a,
               "topology": {"a": v["topology_a"], "b": v["topology_b"], "exact": v["topology_exact"]},
               "volume_mm3": {"a": _r(_mm(mass_a.get("volume"), 3)), "b": _r(_mm(mass_b.get("volume"), 3)),
                              "delta_pct": _r(v["dv"], 4)},
               "area_mm2": {"a": _r(_mm(mass_a.get("surface_area"), 2)), "b": _r(_mm(mass_b.get("surface_area"), 2)),
                            "delta_pct": _r(v["da"], 4)},
               "cg_distance_mm": _r(_mm(v["cg_distance"]))}
        if want_faces:
            missing, extra = diff_faces(faces_a, faces_b)
            out["faces"] = {"matched": len(faces_a) - len(missing),
                            "missing_in_b": [face_desc(f) for f in missing[:20]],
                            "extra_in_b": [face_desc(f) for f in extra[:20]]}
        return dump(out)

    # ---------------------------------------------------------------------------
    # Tool: compare_assemblies  (adapter-only — the assembly round-trip verifier)
    # ---------------------------------------------------------------------------
    @tool
    def compare_assemblies(doc_a: str, doc_b: str) -> str:
        """Objectively diff two ASSEMBLY documents against the RATIFIED verified criteria
        component set EXACT (source file + config + instance counts) AND every
        component transform within tolerance (position <= 1µm, rotation <= 1e-6) AND mate
        count + types match AND mass properties within the V1 thresholds (|dV| <= 1%, |dA| <= 1%).

        doc_a / doc_b: EITHER an absolute .SLDASM path OR the TITLE of an already-open document
            (e.g. 'Assem2' for an unsaved rebuild). doc_a is the REFERENCE (the original).

        Components are paired by (source basename, instance ordinal in tree order) — instance
        numbering may legitimately differ between original and rebuild. doc_b is left active."""
        def read(doc: str, param: str):
            _make_active(doc, param)
            return (_read("components", "analyze_assembly").get("components", []),
                    _read("mates", "analyze_assembly").get("mates", []), _read("mass_properties"))

        comps_a, mates_a, mass_a = read(doc_a, "doc_a")
        comps_b, mates_b, mass_b = read(doc_b, "doc_b")
        v = verify.assembly_verdict(comps_a, comps_b, mates_a, mates_b, mass_a, mass_b)
        return dump({"verified_criteria": "PASS" if v["verified"] else "FAIL", "reference": doc_a,
                     "components": {"a": len(comps_a), "b": len(comps_b), "set_exact": v["set_exact"]},
                     "transforms": {"max_rot": _r(v["max_rot"], 9), "max_pos_mm": _r(_mm(v["max_pos"])),
                                    "ok": v["transforms_ok"]},
                     "mates": {"a": len(mates_a), "b": len(mates_b), "types_match": v["mate_types_match"]},
                     "volume_delta_pct": _r(v["dv"], 4), "area_delta_pct": _r(v["da"], 4)})

    # ---------------------------------------------------------------------------
    # Tool: dimension_for_face  (adapter-only — face index -> its driving sketch dimension)
    # ---------------------------------------------------------------------------
    @tool
    def dimension_for_face(face_index: int, add_if_missing: bool = False) -> str:
        """Find the driving dimension behind ONE face of the active part — the step between a face that is
        off (face_index) and modify_dimension.

        Walks feature_map to the feature that CREATED the face, takes that feature's profile sketch,
        finds the sketch segment the face was swept from (a cylinder -> the circle/arc with the same
        radius on its axis; a side wall -> the line lying in its plane; a cap parallel to the sketch ->
        the extrude's depth), and returns the dimension SolidWorks reports as ATTACHED to that segment.
        face_index: from analyze_model(faces) (or compare_parts' face diff).
        add_if_missing: when the segment has no driving dimension, add one at the segment's CURRENT size
            (geometry unchanged) and return its name (status 'added').
        Returns JSON: status ('dimensioned' | 'undimensioned' | 'added' | 'not_dimension_driven' |
        'multiple_dimensions' | 'ambiguous_feature' | 'ambiguous_segment' | 'unattributed' |
        'no_segment' | 'no_profile_sketch' | 'unsupported'), feature, sketch, segment (sketch frame),
        dimension {name (pass to modify_dimension), value_mm | value_deg, type}, position_dimensions for a circle
        (lengths in mm).
        Anything that does not resolve to exactly one candidate is reported, never guessed.
        Read-only unless add_if_missing adds a dimension. feature_map rebuilds the tree step by step,
        so this takes a few seconds on large parts."""
        faces = _read("faces").get("faces", [])
        face = next((f for f in faces if f.get("i") == face_index), None)
        if face is None:
            raise fail("FACE_NOT_FOUND", f"no face {face_index} (the part has {len(faces)} faces)")
        fmap = _read("feature_map")
        recipe = _read("features")
        try:
            result = face_dimension.resolve(face, fmap, recipe, lambda name: _read("sketch", name=name), faces)
        except (ValueError, TypeError, IndexError) as ex:   # a payload shape the resolver did not expect
            raise fail("ANALYZE_FAILED", f"{type(ex).__name__}: {ex}") from ex

        if result["status"] == "undimensioned" and add_if_missing and "add_dimension" in result:
            params = dict(result["add_dimension"], sketch_name=result["sketch"])
            added = call_raw("add_dimension", params)
            if added.get("status") != "COMPLETED":
                err = added.get("error") or {}
                result["add_error"] = f"{err.get('code')} | {err.get('message')}"
            else:
                geo = added.get("result_geometry") or {}
                result["status"] = "added"
                result["dimension"] = {"name": geo.get("dimension"), "value_si": geo.get("effective"),
                                       "type": geo.get("type")}
        _log(f"<- dimension_for_face face={face_index} status={result['status']} "
             f"feature={result.get('feature')} dim={(result.get('dimension') or {}).get('name')}")
        if isinstance(result.get("add_dimension"), dict):
            # ready arguments for the add_dimension TOOL, which takes mm (to_mm knows no px / py / value keys)
            result["add_dimension"] = {k: round(v / units.M, 4) if isinstance(v, (int, float)) else v
                                       for k, v in result["add_dimension"].items()}
        return dump(units.to_mm(result, "dimension_for_face"))
