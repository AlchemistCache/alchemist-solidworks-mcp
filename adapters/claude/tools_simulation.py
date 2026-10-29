"""Strength: SOLIDWORKS Simulation studies and hand checks (bolt, weld, beam — EN 1993-1-8). Units: mm, N, MPa,
degrees. Registered by registry.register_all(); call plumbing in bridge.py."""
import config
import json
import os
import re
from typing import Literal, Optional


import session
import adapter_log
from bridge import (CadOperationError, calc, doc_append, dump, fail, call_mm, call_mm_json,
                    with_image, calculix, hand_calcs, scratch_dir, sim_report_html)
from jobs import background, solidworks_free


def register(mcp, tool):
    # ------------------------------------------------------------------ simulation (N, MPa, mm)
    @tool
    def sim_status(study: str = "", details: bool = False) -> str:
        """SOLIDWORKS Simulation: add-in version and the active document's studies (type, mesh elements,
        loads+fixtures count, has_results, active). study / details=True add that study's setup: materials per
        body/shell, fixtures / loads / connectors, global contact and contact sets, mesh and mesh controls —
        check it before sim_run."""
        return call_mm("sim_status", dict(study=study, details=details or None))

    @tool
    def sim_create_study(name: str = "Static 1", study_type: Literal["static", "frequency", "buckling"] = "static",
                         replace: bool = False) -> str:
        """Create a Simulation study on the active part/assembly and make it active (sheet metal is meshed
        as shells automatically). Workflow: create → sim_set_material (if parts lack a material) →
        sim_add_fixture → sim_add_load → [sim_contact] → sim_mesh → sim_run → sim_results."""
        return call_mm("sim_create_study", dict(name=name, study_type=study_type, replace=replace))

    @tool
    def sim_delete_study(study: str) -> str:
        """Delete a Simulation study by name."""
        return call_mm("sim_delete_study", dict(study=study))

    @tool
    def sim_set_material(material: str, study: str = "", library: str = "", components: list[str] = []) -> str:
        """Assign a library material (e.g. 'AISI 304', 'AISI 1020', 'Plain Carbon Steel', '1060 Alloy') to all
        solid bodies and shells of the study (or only `components`). Needed when RunAnalysis reports a
        missing material; parts with a SOLIDWORKS material already have it."""
        return call_mm("sim_set_material", dict(material=material, study=study, library=library,
                     components=components))

    @tool
    def sim_add_fixture(faces: list[dict], fixture_type: Literal["fixed", "immovable", "roller", "hinge"] = "fixed",
                        study: str = "") -> str:
        """Add a fixture (restraint) on faces of the active/named study.
        faces: list of {"face_index": i} (part, analyze_model(faces) numbering) | {"component": "X-1", "face_index": i} (assembly, analyze_assembly(faces, component)) | {"point_mm": [x,y,z]} (a point ON the face, found by geometry — a hidden hole wall too)."""
        return call_mm("sim_add_fixture", dict(faces=faces, fixture_type=fixture_type, study=study))

    @tool
    def sim_add_load(load_type: Literal["force", "pressure", "gravity"] = "force", value: float = 0.0,
                     faces: list[dict] = [], direction: list[float] = [], reverse: bool = False,
                     study: str = "") -> str:
        """Add a load. force: value N total over the faces, direction [x,y,z] GLOBAL (empty = normal to the
        face). pressure: value MPa normal to the faces (reverse flips). gravity: value m/s² (default 9.81),
        direction default [0,-1,0], no faces. SHEET METAL (shells): load the large sheet faces — loads on the
        thin thickness/side faces are not carried to the shell mid-surface (observed: zero result).
        faces: list of {"face_index": i} (part, analyze_model(faces) numbering) | {"component": "X-1", "face_index": i} (assembly, analyze_assembly(faces, component)) | {"point_mm": [x,y,z]} (a point ON the face, found by geometry — a hidden hole wall too)."""
        return call_mm("sim_add_load", dict(load_type=load_type, value=value, faces=faces, direction=direction,
                     reverse=reverse, study=study))

    @tool
    def sim_contact(global_contact: Literal["bonded", "no_penetration", "free"] = "bonded", study: str = "") -> str:
        """Global contact between touching assembly components (bonded = welded/glued, SW default;
        no_penetration = parts may separate/slide)."""
        return call_mm("sim_contact", dict(global_contact=global_contact, study=study))

    @tool
    def sim_mesh(element_size_mm: float = 0.0, quality: Literal["high", "draft"] = "high",
                 mesher: Literal["", "standard", "curvature", "blended"] = "", tolerance_mm: float = 0.0,
                 study: str = "") -> str:
        """Mesh the study. element_size_mm 0 = SolidWorks default size. Returns elements/nodes/time."""
        return call_mm("sim_mesh", dict(element_size_mm=element_size_mm, quality=quality, mesher=mesher,
                     tolerance_mm=tolerance_mm, study=study))

    @tool
    def sim_run(study: str = "", yield_mpa: float = 0.0) -> str:
        """Solve the study (blocks until done; mesh first) and return the results summary like sim_results."""
        return call_mm("sim_run", dict(study=study, yield_mpa=yield_mpa))

    @tool
    def sim_results(study: str = "", yield_mpa: float = 0.0) -> str:
        """Results of a solved study. Static: von Mises min/max MPa and displacement min/max mm, each with
        the node and max_at_mm location, factor of safety (automatic; for sheet-metal shells = lowest yield of the
        study materials / max σ; yield_mpa overrides), connector forces and edge-weld pass/fail.
        When the von Mises max sits on a fixture or a loaded face: von_mises_mpa.max_on names it and
        max_away_from_boundary is the highest stress farther than two elements from every fixture and load — judge
        the part by that, not by the max (peak_note says why).
        Frequency: resonant frequencies (Hz). Buckling: load factors."""
        return call_mm("sim_results", dict(study=study, yield_mpa=yield_mpa))

    @tool
    def sim_plot(result: Literal["stress", "displacement", "fos"] = "stress", deformed: bool = True,
                 deform_scale: float = 0.0, study: str = "") -> str:
        """Create and activate a result plot in the study (for viewing in SolidWorks) and return its
        min/max. To SEE the result use sim_image."""
        return call_mm("sim_plot", dict(result=result, deformed=deformed, deform_scale=deform_scale, study=study))

    @tool
    def sim_image(result: Literal["stress", "displacement"] = "stress",
                  view: Literal["iso", "front", "back", "top", "bottom", "right", "left"] = "iso",
                  deform_scale: float = 0.0, clip_percentile: float = 100.0, width: int = 640, study: str = "",
                  file_path: str = ""):
        """SEE a solved study: colour map (blue→red) of von Mises stress (MPa) or displacement (mm) drawn from the
        solver's node data (solids and sheet-metal shells), with legend and the max marked. deform_scale:
        0 = undeformed, -1 = auto, N = ×N. clip_percentile <100 (e.g. 99) caps the colour scale so a singular
        peak (fixture corner, point load) does not wash out the map; values above are red."""
        res = call_mm("sim_image", dict(result=result, view=view, deform_scale=deform_scale,
                    clip_percentile=clip_percentile, width=width,
                    study=study, file_path=file_path))
        return with_image(res)

    @tool
    def sim_crosscheck(fixed: list[dict], loads: list[dict], study: str = "", out_dir: str = "",
                       disp_tol: float = 0.15, stress_tol: float = 0.35) -> str:
        """Independent check of a SOLVED static study with CalculiX on the SAME mesh: exports the mesh, builds a
        CalculiX deck (fixed = the study's fixed faces, loads = [{"faces": [...], "force_n": [fx, fy, fz]}] — the
        same as in sim_add_load), solves it with ccx and compares max displacement / von Mises with SolidWorks →
        evidence status pass / warn / blocked / failed (never a made-up number). Solid meshes (C3D4/C3D10) or
        sheet-metal shells (S6 with each shell's thickness — a draft mesh gets midside nodes, CalculiX's S3 locks in
        bending; nodal surface stress); a mesh mixing both, or pieces joined only by contacts, is blocked. ccx is found via SWMCP_CCX, PATH or PrePoMax / bConverged / FreeCAD folders;
        without it the deck is still written (status blocked). Entity specs as in sim_add_fixture."""
        def run():
            folder = session.full_path("out_dir", out_dir) or scratch_dir("ccx", unique=True)
            os.makedirs(folder, exist_ok=True)
            sets = {"FIX": fixed}
            for i, ld in enumerate(loads, 1):
                sets[f"LOAD{i}"] = ld.get("faces") or []
            exp = call_mm_json("sim_export_mesh", dict(study=study, sets=sets, file_path=os.path.join(folder, "mesh.json")))
            sw = call_mm_json("sim_results", dict(study=study))
            out = {"study": exp.get("study"), "solidworks": {k: sw.get(k) for k in ("von_mises_mpa", "displacement_mm")}}
            try:
                with open(exp["file"], encoding="utf-8") as fh:
                    model = json.load(fh)
                deck = calculix.build_deck(model, ["FIX"], [{"set": f"LOAD{i}", "force_n": ld.get("force_n") or [0, 0, 0]}
                                                            for i, ld in enumerate(loads, 1)],
                                           os.path.join(folder, "crosscheck.inp"))
            except ValueError as ex:
                out.update(status="blocked", reason=str(ex))
                return dump(out)
            out["deck"] = deck
            ccx = calculix.find_ccx()
            if not ccx:
                out.update(status="blocked", reason="CalculiX (ccx) not found — install it (e.g. PrePoMax or bConverged "
                           "CalculiX) and set SWMCP_CCX to ccx.exe; the deck above can also be solved by hand.")
                return dump(out)
            with solidworks_free():   # minutes of solving: SolidWorks is free for other calls meanwhile
                r = calculix.run_ccx(ccx, deck["deck"])
            out["ccx"] = {"executable": ccx, "exit_code": r["exit_code"]}
            if r["exit_code"] != 0 or not r["dat"]:
                out.update(status="failed", stdout_tail=r["stdout_tail"], stderr_tail=r["stderr_tail"])
                return dump(out)
            try:
                res = calculix.parse_dat(r["dat"])
                if deck["element_type"] in ("S3", "S6"):   # shells: the surface (nodal) stress, as SolidWorks reports it
                    if not r.get("frd"):
                        raise ValueError("ccx wrote no .frd file (the shells' nodal stresses)")
                    res.pop("in_element", None)
                    res.update(calculix.parse_frd_stress(r["frd"]))
            except ValueError as ex:
                out.update(status="failed", reason=str(ex))
                return dump(out)
            out["calculix"] = res
            out.update(calculix.compare(sw, res, disp_tol, stress_tol))
            return dump(out)
        return background("sim_crosscheck", run)

    SIM_ENT = ('Entity: {"face_index"} | {"edge_index"} (part) | {"component","face_index"|"edge_index"} (assembly, '
               'indices from analyze_assembly) | {"component"} | {"point_mm":[x,y,z]} (a point on a face).')

    @tool
    @doc_append(SIM_ENT)
    def sim_connector(connector_type: Literal["bolt", "edge_weld", "pin"], head: list[dict] = [], nut: list[dict] = [],
                      bolt_type: Literal["nut", "screw"] = "nut", diameter_mm: float = 8.0,
                      head_diameter_mm: float = 0.0, nut_diameter_mm: float = 0.0, preload_n: float = 0.0,
                      face1: Optional[dict] = None, face2: Optional[dict] = None, edges: list[dict] = [],
                      weld_type: Literal["fillet_single", "fillet_double", "groove_single", "groove_double"] = "fillet_single",
                      weld_size_mm: float = 0.0, ultimate_mpa: float = 360.0, correlation_factor: float = 0.8,
                      partial_safety_factor: float = 1.25, faces1: list[dict] = [], faces2: list[dict] = [],
                      study: str = "") -> str:
        """Connector in the study (assemblies of sheet metal: bolted / welded joints instead of global bonding).
        bolt: head = circular edge(s) (shells) or face(s) of the hole on the head side, nut = the same on the nut side
        (bolt_type nut) or the threaded hole (screw); diameter_mm shank, head_diameter_mm (default 1.6·d),
        preload_n axial. edge_weld: face1 + face2 (one face of each body) + edges where they meet; Eurocode 3 sizing
        with ultimate_mpa of the weaker part, βw, γM2; weld_size_mm 0 = SolidWorks estimates the size.
        pin: faces1/faces2 cylindrical faces of the two parts. After sim_run, sim_results lists connector
        forces and edge-weld pass/fail."""
        return call_mm("sim_connector", dict(connector_type=connector_type, head=head, nut=nut, bolt_type=bolt_type,
                     diameter_mm=diameter_mm, head_diameter_mm=head_diameter_mm or None,
                     nut_diameter_mm=nut_diameter_mm or None, preload_n=preload_n or None, face1=face1, face2=face2,
                     edges=edges, weld_type=weld_type, weld_size_mm=weld_size_mm or None, ultimate_mpa=ultimate_mpa,
                     correlation_factor=correlation_factor, partial_safety_factor=partial_safety_factor,
                     faces1=faces1, faces2=faces2, study=study))

    @tool
    @doc_append(SIM_ENT)
    def sim_contact_set(source: list[dict] = [], target: list[dict] = [],
                        contact_type: Literal["no_penetration", "bonded", "free"] = "no_penetration",
                        friction: float = 0.0, delete: str = "", study: str = "") -> str:
        """Local contact pair between two entity sets (overrides the global contact for these faces):
        no_penetration (parts may separate/slide; friction = coefficient), bonded, free. delete = contact set name.
        """
        return call_mm("sim_contact_set", dict(source=source, target=target, contact_type=contact_type,
                     friction=friction or None, delete=delete, study=study))

    @tool
    @doc_append(SIM_ENT)
    def sim_mesh_control(entities: list[dict] = [], element_size_mm: float = 2.0, ratio: float = 1.5,
                         delete: str = "", study: str = "") -> str:
        """Local mesh refinement (сгущение сетки) on faces / edges / whole components: element_size_mm there,
        growth ratio away from them. Re-mesh afterwards (sim_mesh or sim_run). delete = mesh control name.
        """
        return call_mm("sim_mesh_control", dict(entities=entities, element_size_mm=element_size_mm, ratio=ratio,
                     delete=delete, study=study))

    @tool
    def sim_options(adaptive: Optional[Literal["none", "h", "p"]] = None, target_accuracy_pct: float = 98.0,
                    max_iterations: int = 3, large_displacement: Optional[bool] = None,
                    soft_spring: Optional[bool] = None, inertial_relief: Optional[bool] = None,
                    solver: Optional[Literal["auto", "direct_sparse", "ffeplus", "intel"]] = None, study: str = "") -> str:
        """Static study options: adaptive h (refines the mesh until target_accuracy_pct, max_iterations loops) or p;
        large_displacement (big deflections of thin sheet); soft_spring / inertial_relief (stabilise a model with
        no full fixture); solver. Unset = unchanged; returns the current options."""
        return call_mm("sim_options", dict(adaptive=adaptive, target_accuracy_pct=target_accuracy_pct,
                     max_iterations=max_iterations, large_displacement=large_displacement, soft_spring=soft_spring,
                     inertial_relief=inertial_relief, solver=solver, study=study))

    @tool
    def sim_copy_study(new_name: str = "", study: str = "", include_mesh: bool = False,
                       include_results: bool = False, configuration: str = "") -> str:
        """Duplicate a study with its material, fixtures, loads, connectors and contacts (e.g. to try another load
        case or mesh) — the copy becomes active."""
        return call_mm("sim_copy_study", dict(new_name=new_name, study=study, include_mesh=include_mesh,
                     include_results=include_results, configuration=configuration))

    HAND_CHECKS = {"weld_check": hand_calcs.weld_check, "bolt_check": hand_calcs.bolt_check,
                   "beam_check": hand_calcs.beam_check}

    @tool
    def sim_report(out_dir: str = "", name: str = "", study: str = "", required_fos: float = 1.5,
                   yield_mpa: float = 0.0, title: str = "", engine: Literal["html", "solidworks"] = "html",
                   hand_checks: list[dict] = [], notes: list[str] = [], compare_studies: list[str] = []) -> str:
        """Report of a SOLVED study into out_dir (default <model folder>\\SimReport). engine=html (default): own
        HTML page — verdict against required_fos (from the stress away from fixtures and loads when the peak sits on
        one), results table (σ, displacement, FoS with locations), connector forces / weld checks, stress and
        displacement maps, materials, fixtures, loads, contacts, mesh.
        hand_checks: [{"title": "Шов плита–стенка", "check": "weld_check" | "bolt_check" | "beam_check", "args": {the
        tool's arguments}}] — computed here and listed with their inputs; a failed one fails the verdict. notes: the
        assumptions and where the loads come from, one line each. compare_studies: solved copies of the same case on
        other meshes (sim_copy_study + sim_mesh) → a mesh-sensitivity table; a governing stress that moves > 15 % is
        named a singularity and an "ok" then needs hand_checks.
        engine=solidworks: SOLIDWORKS' Word report — automates MS Word, which hung after the first report of a
        session on SW 2024 (then ~2 min and RPC fault); use only if the Word document is required."""
        if engine == "solidworks":
            if not config.DEV_TOOLS:
                raise fail("DISABLED", "engine=solidworks automates MS Word, which hung after the first report of a session "
                                       "(SW 2024); use the HTML report (set SWMCP_DEV_TOOLS=1 to allow it)")
            return call_mm("sim_report", dict(out_dir=out_dir, name=name, study=study))

        out = session.full_path("out_dir", out_dir)   # the adapter writes the report: checked before any work
        checks = []
        for hc in hand_checks:   # no SolidWorks needed: computed (and a bad one refused) before any work
            fn = HAND_CHECKS.get(hc.get("check"))
            if fn is None:
                raise fail("BAD_PARAMETER", f"hand_checks: check must be one of {', '.join(HAND_CHECKS)} (got {hc.get('check')!r})")
            item = {"title": hc.get("title") or "", "check": hc["check"], "args": hc.get("args") or {}}
            try:
                item["result"] = fn(**item["args"])
            except (TypeError, ValueError) as ex:
                raise fail("BAD_PARAMETER", f"hand_checks «{item['title'] or item['check']}»: {ex}")
            checks.append(item)

        def run():
            status = call_mm_json("sim_status", dict(study=study, details=True))
            results = call_mm_json("sim_results", dict(study=study, yield_mpa=yield_mpa or None))
            st = results.get("study") or study
            model_dir = os.path.dirname(status.get("path") or "")
            # beside the model; an unsaved model has no folder — then the adapter's scratch (cleaned after two days)
            folder = out or (os.path.join(model_dir, "SimReport") if model_dir else scratch_dir("simreport", unique=True))
            os.makedirs(folder, exist_ok=True)
            base = name or re.sub(r'[\\/:*?"<>|]+', "_", f"{os.path.splitext(status.get('document') or 'model')[0]}-{st}")
            images = []
            if results.get("von_mises_mpa") or results.get("displacement_mm"):
                # a peak on a fixture / load paints the rest blue: then the stress scale stops at the 99th percentile
                clip = 99.0 if (results.get("von_mises_mpa") or {}).get("max_on") else 100.0
                for kind, cap in (("stress", "Напряжения по Мизесу, МПа" + (" (шкала до 99-го процентиля, выше — красное)"
                                                                            if clip < 100 else "")),
                                  ("displacement", "Перемещения, мм")):
                    try:
                        im = call_mm_json("sim_image", dict(result=kind, view="iso", study=st, width=900,
                                                          clip_percentile=clip if kind == "stress" else 100.0,
                                                          file_path=os.path.join(folder, f"{base}-{kind}.png")))
                        if im.get("image"):
                            images.append({"image": im["image"], "caption": cap})
                    except CadOperationError as ex:
                        adapter_log.write(f"sim_report: no {kind} image ({ex})")
            compared = [{"study": s, "details": (call_mm_json("sim_status", dict(study=s, details=True)).get("details") or {}),
                         "results": call_mm_json("sim_results", dict(study=s, yield_mpa=yield_mpa or None))}
                        for s in compare_studies if s != st]
            path = os.path.join(folder, base + ".html")
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(sim_report_html.build_html(status, results, images, required_fos, title, checks, notes, compared))
            cls, text = sim_report_html.verdict(results, required_fos, checks, compared)
            out_ = {"report": path, "images": [i["image"] for i in images], "verdict": text, "status": cls}
            if checks:
                out_["hand_checks"] = [{"title": c["title"] or c["check"], "utilization": c["result"].get("utilization",
                                        c["result"].get("stress_util")), "ok": c["result"].get("ok")} for c in checks]
            return dump(out_)
        return background("sim_report", run)

    # ------------------------------------------------------------------ hand checks (no SolidWorks; EN 1993-1-8)
    @tool
    def bolt_check(diameter_mm: float, shear_n: float = 0.0, axial_n: float = 0.0,
                   grade: Literal["4.6", "4.8", "5.6", "5.8", "6.8", "8.8", "10.9", "A2-70", "A4-70", "A4-80"] = "8.8",
                   shear_planes: int = 1, threads_in_shear: bool = True, plate_t_mm: float = 0.0,
                   plate_fu_mpa: float = 360.0, e1_mm: float = 0.0) -> str:
        """Hand check of one bolt (EN 1993-1-8 table 3.4): shear, tension, their interaction and — with plate_t_mm —
        bearing of the thinnest sheet (e1_mm = edge distance in the load direction). Feed it the shear_n / axial_n
        resultants of a bolt connector from sim_results. Returns resistances, utilizations, ok."""
        return calc(hand_calcs.bolt_check, diameter_mm, shear_n, axial_n, grade, shear_planes, threads_in_shear,
                     plate_t_mm, plate_fu_mpa, e1_mm)

    @tool
    def weld_check(force_n: float, length_mm: float, throat_mm: float = 0.0, leg_mm: float = 0.0,
                   fu_mpa: float = 360.0, beta_w: float = 0.8, sides: int = 1) -> str:
        """Hand check of a fillet weld (EN 1993-1-8 simplified method): f_vw,d = f_u/√3/(β_w·γ_M2), utilization =
        F/(f_vw,d·a·L·sides); a = throat_mm or 0.7·leg_mm. β_w 0.8 S235, 0.85 S275, 0.9 S355. Also returns the
        minimum throat for this force."""
        return calc(hand_calcs.weld_check, force_n, length_mm, throat_mm, leg_mm, fu_mpa, beta_w, sides=sides)

    @tool
    def beam_check(section: Literal["rect", "rhs", "pipe", "round", "channel", "angle"], span_mm: float,
                   load_n: float = 0.0, udl_n_per_mm: float = 0.0,
                   support: Literal["cantilever", "simply_supported"] = "cantilever", b_mm: float = 0.0,
                   h_mm: float = 0.0, t_mm: float = 0.0, d_mm: float = 0.0, E_mpa: float = 210000.0,
                   fy_mpa: float = 235.0, deflection_limit: float = 200.0) -> str:
        """Hand check of a beam / bracket arm — an independent cross-check of an FEA result: section properties
        (rect b×h, rhs b×h×t, pipe d×t, round d, channel/U b×h×t, angle h×b×t; load along h), max moment, bending
        stress M/W, deflection (cantilever: point load at the end / UDL; simply_supported: mid-span / UDL),
        utilization vs f_y and span/deflection_limit."""
        return calc(hand_calcs.beam_check, section, span_mm, load_n, udl_n_per_mm, support, b_mm, h_mm, t_mm, d_mm,
                     E_mpa, fy_mpa, deflection_limit)
