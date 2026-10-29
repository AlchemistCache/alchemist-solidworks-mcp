using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.cosworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== SOLIDWORKS Simulation, part 2: assemblies of sheet metal =====================
    // sim_connector    — bolt (circular edges / faces of the holes: head side + nut side), edge weld (two faces + the
    //                    edges where they meet; fillet / groove, single / double sided), pin (two cylindrical faces)
    // sim_contact_set  — local contact pair between entity sets (bonded / no_penetration with friction / free)
    // sim_mesh_control — local mesh refinement on faces / edges / components             ICWMesh.ApplyMeshControl
    // sim_options      — static study options: h/p-adaptive, large displacement, solver, soft springs, inertial relief
    // sim_copy_study   — DuplicateStudy2 (fixtures, loads, connectors; optionally mesh and results)
    // sim_report       — GenerateReport (SolidWorks' Word report; automates MS Word, which hung after the first report
    //                    in a session — the adapter's default report is its own HTML, see adapters/claude/sim_report.py)
    // + results: yield strength from the study material (FoS for shells), connector forces, edge-weld checks.
    // Entity: {face_index} | {edge_index} (part) · {component, face_index | edge_index} · {component} · {point_mm}
    public partial class SolidWorksService
    {
        private object SimEntity(IModelDoc2 d, JObject o)
        {
            if (o["point_mm"] is JArray) return (SimFaces(d, new JArray(o))[0] as DispatchWrapper).WrappedObject;
            string comp = (string)o["component"];
            string kind = o["edge_index"] != null ? "edge" : o["face_index"] != null ? "face" : null;
            if (comp != null)
            {
                var a = AsmOrFail(d, "a component entity");
                if (kind == null) return FindComp(a, comp);
                return GetComponentEntity(a, comp, kind, (int)o[kind + "_index"]);
            }
            if (kind == "face") return FaceAt(d, (int)o["face_index"]);
            if (kind == "edge")
            {
                var edges = FlattenEdges(d as IPartDoc);
                int i = (int)o["edge_index"];
                if (i < 0 || i >= edges.Count) throw new ToolFail("EDGE_NOT_FOUND", $"edge_index {i} out of range ({edges.Count}).");
                return edges[i];
            }
            throw new ToolFail("BAD_PARAMETER", "entity must be {face_index}, {edge_index}, {component[, face_index | edge_index]} or {point_mm}.");
        }

        private static object[] Unwrap(object[] a) => a.Select(x => x is DispatchWrapper w ? w.WrappedObject : x).ToArray();

        private object[] SimEntities(IModelDoc2 d, JArray specs, string what)
        {
            if (specs == null || specs.Count == 0) throw new ToolFail("MISSING_PARAMETER", what + " is required (list of entity specs).");
            return specs.Select(t => (object)new DispatchWrapper(SimEntity(d, t as JObject ?? throw new ToolFail("BAD_PARAMETER", what + ": each entity must be an object")))).ToArray();
        }

        // ---------------------------------------------------------------- connectors
        public ExecutionResponse SimConnector(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            string type = PStr(p, "connector_type", "bolt").ToLowerInvariant();
            int err = 0;
            var res = new JObject { ["study"] = study.Name, ["connector_type"] = type };
            int before = lr.Count;
            switch (type)
            {
                case "bolt":
                {
                    var head = SimEntities(d, PArr(p, "head"), "head");
                    var nut = SimEntities(d, PArr(p, "nut"), "nut");
                    int bt = PStr(p, "bolt_type", "nut") == "screw" ? (int)swsBoltType_e.swsBoltTypeStandardOrCounterboreScrew : (int)swsBoltType_e.swsBoltTypeStandardOrCounterboreNut;
                    var b = lr.AddBoltConnector(bt, head, nut, ref err) as CWBoltConnector;
                    if (b == null) { int e1 = err; err = 0; b = lr.AddBoltConnector(bt, Unwrap(head), Unwrap(nut), ref err) as CWBoltConnector; ExecLog.Write($"sim_connector bolt: wrapped err {e1}, plain err {err}"); }
                    if (b == null) throw new ToolFail("CONNECTOR_FAILED", $"AddBoltConnector err={err} ({(swsBoltConnectorError_e)err}).");
                    double dia = PNum(p, "diameter_mm", 8);
                    b.BoltConnectorBeginEdit();
                    b.BoltShankDiameterUnit = (int)swsLinearUnit_e.swsLinearUnitMillimeters;
                    b.BoltShankDiameterValue = dia;
                    b.HeadDiameterUnit = (int)swsLinearUnit_e.swsLinearUnitMillimeters;
                    b.HeadDiameterValue = PNum(p, "head_diameter_mm", Math.Round(dia * 1.6, 1));
                    double nd = PNum(p, "nut_diameter_mm", 0);
                    b.SameHeadAndNutDiameter = nd <= 0;
                    if (nd > 0) { b.NutDiameterUnit = (int)swsLinearUnit_e.swsLinearUnitMillimeters; b.NutDiameterValue = nd; }
                    double pre = PNum(p, "preload_n", 0);
                    if (pre > 0)
                    {
                        b.BoltUnit = (int)swsForceUnit_e.swsForceUnitNOrNm;
                        b.PreLoadForceType = (int)swsPreloadForce_e.swsPreloadForceAxial;
                        b.PreLoadForceValue = pre;
                    }
                    int ee = b.BoltConnectorEndEdit();
                    if (ee != 0) res["end_edit_error"] = ((swsBoltConnectorEndEditError_e)ee).ToString().Replace("swsBoltConnectorEndEditError", "");
                    res["diameter_mm"] = b.BoltShankDiameterValue;
                    res["head_diameter_mm"] = b.HeadDiameterValue;
                    if (pre > 0) res["preload_n"] = b.PreLoadForceValue;
                    break;
                }
                case "edge_weld":
                {
                    var f1 = SimEntity(d, p["face1"] as JObject ?? throw new ToolFail("MISSING_PARAMETER", "face1 (face of the first body) is required."));
                    var f2 = SimEntity(d, p["face2"] as JObject ?? throw new ToolFail("MISSING_PARAMETER", "face2 (face of the second body) is required."));
                    var edges = SimEntities(d, PArr(p, "edges"), "edges");
                    int style;
                    switch (PStr(p, "weld_type", "fillet_single"))
                    {
                        case "fillet_double": style = (int)swsEdgeWeldConnectorTypes_e.swsEdgeWeldConnectorFilletDoubleSided; break;
                        case "fillet_single": style = (int)swsEdgeWeldConnectorTypes_e.swsEdgeWeldConnectorFilletSingleSided; break;
                        case "groove_double": style = (int)swsEdgeWeldConnectorTypes_e.swsEdgeWeldConnectorGrooveDoubleSided; break;
                        case "groove_single": style = (int)swsEdgeWeldConnectorTypes_e.swsEdgeWeldConnectorGrooveSingleSided; break;
                        default: throw new ToolFail("BAD_PARAMETER", "weld_type must be fillet_single, fillet_double, groove_single or groove_double.");
                    }
                    // plain RCWs here — with DispatchWrapper (what AddRestraint/AddForce need) the call answers
                    // WrongEdgeSelection (verified live, SW 2024)
                    var w = lr.AddEdgeWeldConnector(f1, f2, Unwrap(edges), style, out err) as CWEdgeWeldConnector;
                    if (w == null) throw new ToolFail("CONNECTOR_FAILED", $"AddEdgeWeldConnector err={err} ({(swsEdgeWeldCreationErrorCode_e)err}) — face1/face2 = one face of each body, edges = edges of face2 lying on face1.");
                    // Eurocode 3 sizing: ultimate strength of the weaker part, correlation factor βw, partial factor γM2
                    double fu = PNum(p, "ultimate_mpa", 360);
                    int se = w.SetCodeType((int)swsEdgeWeldSolverCode_e.swsEdgeWeldSolverCodeEURO);
                    double est = PNum(p, "weld_size_mm", 0);
                    int ss = w.SetWeldSizingSettingEuro(0, fu, (int)swsStrengthUnit_e.swsStrengthUnitNewtonPerSquareMillimeter,
                        PNum(p, "correlation_factor", 0.8), PNum(p, "partial_safety_factor", 1.25), est > 0, est, (int)swsLinearUnit_e.swsLinearUnitMillimeters);
                    res["code"] = "EURO"; res["ultimate_mpa"] = fu;
                    if (est > 0) res["weld_size_mm"] = est;
                    if (se != 0 || ss != 0) res["settings_error"] = $"SetCodeType={se} SetWeldSizingSettingEuro={ss}";
                    res["edges"] = edges.Length;
                    break;
                }
                case "pin":
                {
                    var c1 = SimEntities(d, PArr(p, "faces1"), "faces1");
                    var c2 = SimEntities(d, PArr(p, "faces2"), "faces2");
                    var pin = lr.AddPinConnector(c1, c2, ref err) as CWPinConnector;
                    if (pin == null) { err = 0; pin = lr.AddPinConnector(Unwrap(c1), Unwrap(c2), ref err) as CWPinConnector; }
                    if (pin == null) throw new ToolFail("CONNECTOR_FAILED", $"AddPinConnector err={err}.");
                    break;
                }
                default: throw new ToolFail("BAD_PARAMETER", "connector_type must be bolt, edge_weld or pin.");
            }
            var names = new JArray();
            for (int i = before; i < lr.Count; i++) { int e2; var item = lr.GetLoadsAndRestraints(i, out e2) as ICWLoadsAndRestraints; if (item != null) names.Add(item.Name); }
            res["connector"] = names.Count == 1 ? names[0] : names;
            return res;
        });

        // ---------------------------------------------------------------- local contact
        public ExecutionResponse SimContactSet(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            var cm = study.ContactManager as ICWContactManager ?? throw new ToolFail("NO_CONTACT_MANAGER", "Study has no contact manager.");
            string del = PStr(p, "delete");
            if (del != null)
            {
                int n0 = cm.ContactSetCount;
                cm.DeleteContactSet(del);   // void: a wrong name deletes nothing, silently
                if (cm.ContactSetCount >= n0) throw new ToolFail("NOT_FOUND", $"No contact set '{del}' in {study.Name} ({n0} sets) — the names are in sim_status.");
                return new JObject { ["study"] = study.Name, ["deleted"] = del, ["contact_sets"] = cm.ContactSetCount };
            }
            int ct;
            string type = PStr(p, "contact_type", "no_penetration");
            // contact SETS use their own numbering (no penetration 0, bonded 1, shrink fit 2, free 3) — passing
            // swsContactType_e values made "no_penetration" a shrink fit (err 11, verified live)
            switch (type)
            {
                case "bonded": ct = (int)swsContactSetTypeStaticNonLinear_e.swsContactSetTypeStaticNonLinearBonded; break;
                case "no_penetration": ct = (int)swsContactSetTypeStaticNonLinear_e.swsContactSetTypeStaticNonLinearNoPenetration; break;
                case "free": ct = (int)swsContactSetTypeStaticNonLinear_e.swsContactSetTypeStaticNonLinearFree; break;
                default: throw new ToolFail("BAD_PARAMETER", "contact_type must be bonded, no_penetration or free.");
            }
            int err;
            var cs = cm.CreateContactSet2(ct, (int)swsSelectionType_e.swsSelectionFaceEdgeVertexPoint,
                SimEntities(d, PArr(p, "source"), "source"), SimEntities(d, PArr(p, "target"), "target"), out err) as CWContactSet;
            if (cs == null) throw new ToolFail("CONTACT_FAILED", $"CreateContactSet2 err={err} ({(swsContactSetError_e)err}).");
            var res = new JObject { ["study"] = study.Name, ["contact"] = cs.ContactName, ["contact_type"] = type };
            double fr = PNum(p, "friction", 0);
            if (fr > 0)
            {
                cs.ContactSetBeginEdit();
                cs.IncludeFriction2 = true;
                cs.FrictionValue = fr;
                int ee = cs.ContactSetEndEdit();
                res["friction"] = cs.FrictionValue;
                if (ee != 0) res["end_edit_error"] = ((swsContactSetEndEditError_e)ee).ToString();
            }
            res["contact_sets"] = cm.ContactSetCount;
            return res;
        });

        // ---------------------------------------------------------------- mesh control
        public ExecutionResponse SimMeshControl(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            var mesh = study.Mesh as ICWMesh ?? throw new ToolFail("NO_MESH", "Study mesh unavailable.");
            string del = PStr(p, "delete");
            if (del != null)
            {
                int n0 = mesh.MeshControlCount;
                mesh.DeleteMeshControl(del);   // void: a wrong name deletes nothing, silently
                if (mesh.MeshControlCount >= n0) throw new ToolFail("NOT_FOUND", $"No mesh control '{del}' in {study.Name} ({n0} controls) — the names are in sim_status.");
                return new JObject { ["study"] = study.Name, ["deleted"] = del, ["mesh_controls"] = mesh.MeshControlCount };
            }
            int err;
            var mc = mesh.ApplyMeshControl(SimEntities(d, PArr(p, "entities"), "entities"), out err) as ICWMeshControl;
            if (mc == null) throw new ToolFail("MESH_CONTROL_FAILED", $"ApplyMeshControl err={err} ({(swsMeshControlError_e)err}).");
            mc.MeshControlBeginEdit();
            mc.Units = (int)swsLinearUnit_e.swsLinearUnitMillimeters;
            mc.ElementSize = PNum(p, "element_size_mm", 2);
            mc.Ratio = PNum(p, "ratio", 1.5);
            int ee = mc.MeshControlEndEdit();
            var res = new JObject { ["study"] = study.Name, ["mesh_control"] = mc.Name, ["entities"] = mc.EntityCount,
                                    ["element_size_mm"] = mc.ElementSize, ["ratio"] = mc.Ratio, ["mesh_controls"] = mesh.MeshControlCount,
                                    ["note"] = "re-mesh (sim_mesh / sim_run) to apply" };
            if (ee != 0) res["end_edit_error"] = ((swsMeshControlError_e)ee).ToString();
            return res;
        });

        // ---------------------------------------------------------------- options / copy / report
        public ExecutionResponse SimOptions(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            if (study.AnalysisType != (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeStatic)
                throw new ToolFail("NOT_STATIC", "sim_options sets STATIC study options.");
            var o = study.StaticStudyOptions as ICWStaticStudyOptions ?? throw new ToolFail("NO_OPTIONS", "StaticStudyOptions unavailable.");
            string ad = PStr(p, "adaptive");
            if (ad != null)
            {
                o.AdaptiveMethodType = ad == "h" ? (int)swsSimAdaptiveMethodType_e.swsHAdaptive : ad == "p" ? (int)swsSimAdaptiveMethodType_e.swsPAdative : (int)swsSimAdaptiveMethodType_e.swsNoneMethod;
                if (ad == "h")
                {
                    o.HAdaptiveTargetAccuracy = PNum(p, "target_accuracy_pct", 98);
                    o.HAdaptiveMaxNoIterations = PInt(p, "max_iterations", 3);
                }
                else if (ad == "p") o.PAdaptiveMaxIterations = PInt(p, "max_iterations", 4);
            }
            if (p["large_displacement"] != null) o.LargeDisplacement2 = PBool(p, "large_displacement", false);
            if (p["soft_spring"] != null) o.UseSoftSpring2 = PBool(p, "soft_spring", false);
            if (p["inertial_relief"] != null) o.UseInertialRelief2 = PBool(p, "inertial_relief", false);
            string solver = PStr(p, "solver");
            if (solver != null) o.SolverType = solver == "direct_sparse" ? (int)swsSolverType_e.swsSolverTypeDirectSparse
                : solver == "ffeplus" ? (int)swsSolverType_e.swsSolverTypeFFEPlus
                : solver == "intel" ? (int)swsSolverType_e.swsSolverTypeINTEL : (int)swsSolverType_e.swsSolverTypeAutomatic;
            int at = o.AdaptiveMethodType;
            return new JObject
            {
                ["study"] = study.Name,
                ["adaptive"] = at == 1 ? "h" : at == 2 ? "p" : "none",
                ["target_accuracy_pct"] = at == 1 ? (JToken)o.HAdaptiveTargetAccuracy : null,
                ["max_iterations"] = at == 1 ? (JToken)o.HAdaptiveMaxNoIterations : at == 2 ? (JToken)o.PAdaptiveMaxIterations : null,
                ["large_displacement"] = o.LargeDisplacement2, ["soft_spring"] = o.UseSoftSpring2, ["inertial_relief"] = o.UseInertialRelief2,
                ["solver"] = ((swsSolverType_e)o.SolverType).ToString().Replace("swsSolverType", ""),
            };
        });

        public ExecutionResponse SimCopyStudy(ToolRequest request) => Run(request, true, p =>
        {
            var sm = StudyMgr();
            var src = Study(sm, PStr(p, "study"));
            string name = PStr(p, "new_name") ?? (src.Name + " copy");
            int err;
            var st = sm.DuplicateStudy2(src.Name, name, PStr(p, "configuration", ""), 0, PBool(p, "include_mesh", false), PBool(p, "include_results", false), out err) as CWStudy;
            if (st == null) throw new ToolFail("COPY_FAILED", $"DuplicateStudy2 err={err}.");
            Study(sm, st.Name);
            return new JObject { ["copied"] = src.Name, ["study"] = st.Name, ["studies"] = new JArray(StudyNames(sm)) };
        });

        public ExecutionResponse SimReport(ToolRequest request) => Run(request, false, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            string dir = PStr(p, "out_dir") ?? Path.Combine(DocFolder(d) ?? Path.GetTempPath(), "SimReport");
            Directory.CreateDirectory(dir);
            string name = PStr(p, "name") ?? SafeName(Path.GetFileNameWithoutExtension(d.GetTitle()) + "-" + study.Name);
            var before = new HashSet<string>(Directory.GetFiles(dir, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
            int r;
            using (VisibleWindow())   // the report captures result plots — like ExportToDWG2, it needs a visible frame
                r = study.GenerateReport(dir, name, false);
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !before.Contains(f)).ToList();
            if (files.Count == 0) throw new ToolFail("REPORT_FAILED", $"GenerateReport returned {r} and wrote nothing to {dir}.");
            return new JObject { ["study"] = study.Name, ["out_dir"] = dir, ["result"] = r,
                                 ["files"] = new JArray(files.Where(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).Take(10)),
                                 ["file_count"] = files.Count };
        });

        // ---------------------------------------------------------------- results helpers
        // Lowest yield strength (MPa) over the study's bodies and shells — basis of FoS when the automatic one is
        // unavailable (shell meshes return 1e16).
        private double? StudyYieldMpa(CWStudy study, JObject res)
        {
            double? min = null; var mats = new HashSet<string>();
            Action<object> take = m =>
            {
                var mat = m as ICWMaterial; if (mat == null) return;
                bool td;
                double y = 0;
                try { y = mat.GetPropertyByName2((int)swsUnitSystem_e.swsUnitSystemSI, "SIGYLD", out td); } catch { }
                if (y > 0) { min = Math.Min(min ?? double.MaxValue, y / 1e6); mats.Add(mat.MaterialName); }
            };
            try
            {
                var smgr = study.SolidManager as ICWSolidManager;
                for (int i = 0; smgr != null && i < smgr.ComponentCount; i++)
                {
                    int e1; var comp = smgr.GetComponentAt(i, out e1) as ICWSolidComponent; if (comp == null) continue;
                    for (int j = 0; j < comp.SolidBodyCount; j++) { int e2; take((comp.GetSolidBodyAt(j, out e2) as ICWSolidBody)?.GetSolidBodyMaterial()); }
                }
                var shm = study.ShellManager as ICWShellManager;
                for (int i = 0; shm != null && i < shm.ShellCount; i++) { int e3; take((shm.GetShellAt(i, out e3) as ICWShell)?.GetShellMaterial()); }
            }
            catch (Exception ex) { res["yield_note"] = ex.Message; }
            if (min.HasValue) res["yield_from_material"] = new JObject { ["yield_mpa"] = Math.Round(min.Value, 2), ["materials"] = new JArray(mats) };
            return min;
        }

        // What the study contains — for reports and for the model to check its setup before solving.
        private JObject StudyDetails(CWStudy study)
        {
            var o = new JObject { ["study"] = study.Name, ["type"] = ((swsAnalysisStudyType_e)study.AnalysisType).ToString().Replace("swsAnalysisStudyType", "") };
            var mats = new JArray();
            try
            {
                var smgr = study.SolidManager as ICWSolidManager;
                for (int i = 0; smgr != null && i < smgr.ComponentCount; i++)
                {
                    int e1; var comp = smgr.GetComponentAt(i, out e1) as ICWSolidComponent; if (comp == null) continue;
                    for (int j = 0; j < comp.SolidBodyCount; j++)
                    {
                        int e2; var b = comp.GetSolidBodyAt(j, out e2) as ICWSolidBody;
                        mats.Add(new JObject { ["body"] = comp.ComponentName + "/" + b?.SolidBodyName, ["material"] = (b?.GetSolidBodyMaterial() as ICWMaterial)?.MaterialName });
                    }
                }
                var shm = study.ShellManager as ICWShellManager;
                for (int i = 0; shm != null && i < shm.ShellCount; i++)
                {
                    int e3; var sh = shm.GetShellAt(i, out e3) as ICWShell; if (sh == null) continue;
                    // Sheet-metal shells of an assembly come unnamed: name them by their component; its solid body above is then
                    // only the source of the shell, not meshed as a solid
                    string name = sh.Name, comp = null;
                    try
                    {
                        // the entity is the sheet-metal body (IBody2), which has no component — its faces do
                        object e0 = sh.EntityCount > 0 ? sh.GetEntityAt(0) : null;
                        var ent = e0 as IEntity ?? (e0 as IBody2)?.GetFirstFace() as IEntity;
                        comp = (ent?.GetComponent() as IComponent2)?.Name2;
                        if (comp == null && e0 is IBody2 body) name = string.IsNullOrEmpty(name) ? body.Name : name;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(name)) name = comp ?? "";
                    if (comp != null)
                        foreach (JObject m in mats.OfType<JObject>().Where(m => m["body"] != null && ((string)m["body"]).StartsWith(comp + "/")))
                            m["meshed_as"] = "shell";
                    mats.Add(new JObject { ["shell"] = name, ["thickness_mm"] = (JToken)(sh.ShellThickness * SimUnitMm(sh.ShellUnit)), ["material"] = (sh.GetShellMaterial() as ICWMaterial)?.MaterialName });
                }
            }
            catch (Exception ex) { o["materials_error"] = ex.Message; }
            o["materials"] = mats;
            var lrs = new JArray();
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            for (int i = 0; lr != null && i < lr.Count; i++)
            {
                int e; var item = lr.GetLoadsAndRestraints(i, out e) as ICWLoadsAndRestraints; if (item == null) continue;
                string kind = NonConnectorTypes.Contains(item.Type) ? ((swsLoadsAndRestraintsType_e)item.Type).ToString().Replace("swsLoadsAndRestraintsType", "").ToLowerInvariant() : "connector";
                lrs.Add(new JObject { ["name"] = item.Name, ["kind"] = kind, ["entities"] = item.EntityCount });
            }
            o["loads_fixtures_connectors"] = lrs;
            var cm = study.ContactManager as ICWContactManager;
            if (cm != null)
            {
                try { int t2, o2; cm.GetGlobalContact(out t2, out o2); o["global_contact"] = ((swsContactType_e)t2).ToString().Replace("swsContactType", ""); } catch { }
                var sets = new JArray();
                for (int i = 0; i < cm.ContactSetCount; i++)
                {
                    var cs = cm.GetContactSetAt(i) as ICWContactSet; if (cs == null) continue;
                    sets.Add(new JObject { ["name"] = cs.ContactName, ["type"] = cs.ContactSetType, ["friction"] = cs.IncludeFriction2 ? (JToken)cs.FrictionValue : null });
                }
                if (sets.Count > 0) o["contact_sets"] = sets;
            }
            var mesh = study.Mesh as ICWMesh;
            if (mesh != null)
            {
                var m = new JObject();
                try { m["state"] = ((swsMeshState_e)mesh.MeshState).ToString().Replace("swsMeshState", ""); } catch { }
                try { m["elements"] = mesh.ElementCount; m["nodes"] = mesh.NodeCount; } catch { }
                try { m["element_size_mm"] = Math.Round(mesh.ElementSize * MM, 3); } catch { }   // SI (m) even after CreateMesh in mm
                var mcs = new JArray();
                for (int i = 0; i < mesh.MeshControlCount; i++)
                {
                    var mc = mesh.GetMeshControlAt(i) as ICWMeshControl; if (mc == null) continue;
                    mcs.Add(new JObject { ["name"] = mc.Name, ["element_size_mm"] = mc.ElementSize * SimUnitMm(mc.Units), ["entities"] = mc.EntityCount });
                }
                if (mcs.Count > 0) m["controls"] = mcs;
                o["mesh"] = m;
            }
            return o;
        }

        private static readonly HashSet<int> NonConnectorTypes = new HashSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 21, 31 };

        // Connector forces (bolts, pins …) and edge-weld checks of a solved static study.
        private void SimConnectorResults(CWStudy study, ICWResults r, JObject res)
        {
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            var conns = new JArray();
            for (int i = 0; lr != null && i < lr.Count; i++)
            {
                int e; var item = lr.GetLoadsAndRestraints(i, out e) as ICWLoadsAndRestraints;
                // connectors are everything that is not a load / fixture / mesh control — a bolt reports type 36,
                // which swsLoadsAndRestraintsType_e (2024) does not list
                if (item == null || NonConnectorTypes.Contains(item.Type)) continue;
                var o = new JObject { ["name"] = item.Name };
                try
                {
                    int ef; var f = r.GetConnectorForces2(1, item.Name, (int)swsForceUnit_e.swsForceUnitNOrNm, out ef) as Array;
                    var v = f?.Cast<object>().Select(x => Math.Round(Convert.ToDouble(x), 3)).ToArray();
                    // 16 values = 4 groups (x, y, z, resultant): shear N, axial N, bending N·m, torque N·m — verified
                    // live: a 5000 N preloaded bolt reads axial 5001, the two bolts' shear + friction = the applied load
                    if (v != null && v.Length == 16)
                    {
                        string[] g = { "shear_n", "axial_n", "bending_nm", "torque_nm" };
                        for (int k = 0; k < 4; k++) o[g[k]] = new JArray(v.Skip(k * 4).Take(4));
                    }
                    else if (v != null && v.Length > 0) o["forces"] = new JArray(v);
                    else if (ef != 0) o["forces_error"] = ef;
                }
                catch (Exception ex) { o["forces_error"] = ex.Message; }
                try
                {
                    bool pass; int ew;
                    var wr = r.GetEdgeWeldResults(item.Name, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out pass, out ew) as Array;
                    if (wr != null && wr.Length > 0)
                    {
                        var vals = wr.Cast<object>().Select(x => Math.Round(Convert.ToDouble(x), 4)).ToArray();
                        if (vals.All(x => x == 0)) o["weld_note"] = "SolidWorks returned zeros for the weld check (GetEdgeWeldResults) — seen on SW 2024 for shells and for solids even with global contact free; judge the joint by the stress at the weld root (von Mises near the weld) or the Edge Weld Check plot in SolidWorks";
                        else { o["weld_results"] = new JArray(vals); o["weld_pass"] = pass; }
                    }
                }
                catch { }
                conns.Add(o);
            }
            if (conns.Count > 0) res["connectors"] = conns;
        }
    }
}
