using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    // ===================== SOLIDWORKS Simulation =====================
    // Static / frequency / buckling studies on the active PART or ASSEMBLY (sheet metal is meshed as shells by
    // Simulation itself). Entry: GetAddInObject("SldWorks.Simulation") → ICwAddincallback.CosmosWorks.
    // Approach and API pitfalls learned from the cyounghabs and Bennyco86 forks of SolidPilot (AGPL):
    //  * CreateMesh's FIRST argument is the length unit (not quality) — wrong unit = micron mesh that never ends.
    //  * entity arrays must be object[] of DispatchWrapper (plain RCWs are rejected by AddRestraint/AddForce).
    //  * AddForce3 does not store directional components — set them afterwards via SetForceComponentValues2.
    // Faces: {"face_index": i} (part, analyze_model(faces) numbering), {"component": "X-1", "face_index": i}
    // (assembly, analyze_assembly(faces, component) numbering) or {"point_mm": [x,y,z]} (a point ON the face).
    // Units at this boundary: N, MPa, mm.
    public partial class SolidWorksService
    {
        private string SimAddinDll => System.IO.Path.Combine(SwInstallDir, "Simulation", "cosworks.dll");

        private ICosmosWorks Cosmos()
        {
            object addin = _solidWorks.GetAddInObject("SldWorks.Simulation");
            if (addin == null)
            {
                try { _solidWorks.LoadAddIn(SimAddinDll); } catch { }
                addin = _solidWorks.GetAddInObject("SldWorks.Simulation");
            }
            var cw = (addin as ICwAddincallback)?.CosmosWorks as ICosmosWorks;
            if (cw == null) throw new ToolFail("SIMULATION_UNAVAILABLE",
                "SOLIDWORKS Simulation add-in could not be loaded (installed? licensed? Tools > Add-Ins).");
            return cw;
        }

        private ICWStudyManager StudyMgr()
        {
            ActiveDocOrFail();
            var cdoc = Cosmos().ActiveDoc as ICWModelDoc ?? throw new ToolFail("NO_ACTIVE_DOCUMENT", "Simulation sees no active part/assembly.");
            return cdoc.StudyManager as ICWStudyManager ?? throw new ToolFail("NO_ACTIVE_DOCUMENT", "StudyManager is null.");
        }

        // Study by name (case-insensitive) or the active one; makes it active.
        private CWStudy Study(ICWStudyManager sm, string name)
        {
            if (sm.StudyCount == 0) throw new ToolFail("STUDY_NOT_FOUND", "No studies — sim_create_study first.");
            if (string.IsNullOrEmpty(name)) return sm.GetStudy(sm.ActiveStudy) as CWStudy;
            for (int i = 0; i < sm.StudyCount; i++)
            {
                var s = sm.GetStudy(i) as CWStudy;
                if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) { sm.ActiveStudy = i; return s; }
            }
            throw new ToolFail("STUDY_NOT_FOUND", $"No study '{name}'. Existing: " + string.Join(", ", StudyNames(sm)));
        }

        private static List<string> StudyNames(ICWStudyManager sm) =>
            Enumerable.Range(0, sm.StudyCount).Select(i => (sm.GetStudy(i) as CWStudy)?.Name).ToList();


        private object[] SimFaces(IModelDoc2 d, JArray specs)
        {
            if (specs == null || specs.Count == 0) throw new ToolFail("MISSING_PARAMETER", "faces is required: [{face_index}|{component,face_index}|{point_mm:[x,y,z]}].");
            var res = new List<object>();
            foreach (var t in specs)
            {
                var o = t as JObject ?? throw new ToolFail("BAD_PARAMETER", "Each face must be an object.");
                object face;
                if (o["point_mm"] is JArray pt)
                {
                    double x = (double)pt[0] / MM, y = (double)pt[1] / MM, z = (double)pt[2] / MM;
                    face = FaceAtPoint(d, x, y, z, PointOnFaceTol, out double nearest);
                    if (face == null && double.IsPositiveInfinity(nearest))   // no solid bodies to measure: the screen pick
                    {
                        d.ClearSelection2(true);
                        if (d.Extension.SelectByID2("", "FACE", x, y, z, false, 0, null, 0))
                            face = (d.SelectionManager as ISelectionMgr).GetSelectedObject6(1, -1);
                        d.ClearSelection2(true);
                    }
                    if (face == null)
                        throw new ToolFail("FACE_NOT_FOUND", $"No face at {pt.ToString(Newtonsoft.Json.Formatting.None)} mm"
                            + (double.IsPositiveInfinity(nearest) ? "." : $" — the nearest face is {nearest * MM:0.###} mm away; give a point ON the face."));
                }
                else if (o["component"] != null)
                {
                    var a = AsmOrFail(d, "a component face");
                    face = GetComponentEntity(a, (string)o["component"], "face", (int)o["face_index"]);
                }
                else face = FaceAt(d, (int)o["face_index"]);
                res.Add(new DispatchWrapper(face));
            }
            return res.ToArray();
        }

        private const double PointOnFaceTol = 0.0002;   // m — how far a point_mm may sit off its face

        // The face a point lies on, by geometry: the nearest face of the part's solid bodies, or of the shown components'
        // bodies in an assembly (the point taken into each component's space). A screen pick (SelectByID2) shoots a ray
        // through the point and misses a face hidden from the view — a hole wall — or answers with the face in front of
        // it. null when nothing is within tol (m); nearest = the closest distance found (infinity: no bodies at all).
        private object FaceAtPoint(IModelDoc2 d, double x, double y, double z, double tol, out double nearest)
        {
            var sources = new List<(IBody2 body, double[] q)>();
            if (d is IAssemblyDoc asm)
            {
                var mu = _solidWorks.IGetMathUtility();
                foreach (var c in Comps(asm, false))
                {
                    if (IsSuppressedComp(c) || c.GetModelDoc2() is IAssemblyDoc) continue;
                    try { if (c.IsHidden(true)) continue; } catch { }
                    var q = new[] { x, y, z };
                    if (c.Transform2 is MathTransform xf && mu.CreatePoint(q) is MathPoint mp)
                        q = (mp.MultiplyTransform(xf.Inverse()) as MathPoint)?.ArrayData as double[] ?? q;
                    foreach (var b in c.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[] ?? new object[0])
                        if (b is IBody2 body) sources.Add((body, q));
                }
            }
            else foreach (var body in SolidBodies(d as IPartDoc)) sources.Add((body, new[] { x, y, z }));

            nearest = double.PositiveInfinity;
            object best = null;
            foreach (var (body, q) in sources)
            {
                if (body.GetBodyBox() is double[] box && box.Length >= 6)   // a body whose box is farther than the best face: skip
                {
                    double bx = Math.Max(0, Math.Max(box[0] - q[0], q[0] - box[3])), by = Math.Max(0, Math.Max(box[1] - q[1], q[1] - box[4])),
                           bz = Math.Max(0, Math.Max(box[2] - q[2], q[2] - box[5]));
                    if (Math.Sqrt(bx * bx + by * by + bz * bz) > nearest) continue;
                }
                foreach (var f in (body.GetFaces() as object[] ?? new object[0]).OfType<IFace2>())
                {
                    if (!(f.GetClosestPointOn(q[0], q[1], q[2]) is double[] c) || c.Length < 3) continue;
                    double dist = Math.Sqrt((c[0] - q[0]) * (c[0] - q[0]) + (c[1] - q[1]) * (c[1] - q[1]) + (c[2] - q[2]) * (c[2] - q[2]));
                    if (dist < nearest) { nearest = dist; best = f; }
                }
            }
            return nearest <= tol ? best : null;
        }

        private static object MinMax(object raw, double scale, bool withNodes)
        {
            var a = raw as Array;
            if (a == null) return null;
            var v = a.Cast<object>().Select(x => Convert.ToDouble(x)).ToArray();
            if (withNodes && v.Length >= 4)
                return new JObject { ["min"] = Math.Round(v[1] * scale, 4), ["min_node"] = (int)v[0], ["max"] = Math.Round(v[3] * scale, 4), ["max_node"] = (int)v[2] };
            if (v.Length >= 2) return new JObject { ["min"] = Math.Round(v[0] * scale, 4), ["max"] = Math.Round(v[1] * scale, 4) };
            return new JArray(v);
        }

        // ---------------------------------------------------------------- tools
        public ExecutionResponse SimStatus(ToolRequest request) => Run(request, false, p =>
        {
            var cw = Cosmos();
            var res = new JObject { ["addin"] = true, ["version"] = cw.VersionNumber };
            var d = _solidWorks.IActiveDoc2 as IModelDoc2;
            if (d == null) return res;
            var sm = (cw.ActiveDoc as ICWModelDoc)?.StudyManager as ICWStudyManager;
            var studies = new JArray();
            if (sm != null)
                for (int i = 0; i < sm.StudyCount; i++)
                {
                    var s = sm.GetStudy(i) as CWStudy; if (s == null) continue;
                    var o = new JObject { ["index"] = i, ["name"] = s.Name, ["type"] = ((swsAnalysisStudyType_e)s.AnalysisType).ToString().Replace("swsAnalysisStudyType", "") };
                    try { var m = s.Mesh as ICWMesh; o["mesh_elements"] = m?.ElementCount ?? 0; } catch { }
                    try { o["loads_and_fixtures"] = (s.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager)?.Count ?? 0; } catch { }
                    try { o["has_results"] = s.Results != null; } catch { o["has_results"] = false; }
                    o["active"] = i == sm.ActiveStudy;
                    studies.Add(o);
                }
            res["document"] = d.GetTitle();
            res["path"] = d.GetPathName();
            res["studies"] = studies;
            if (sm != null && sm.StudyCount > 0 && (PStr(p, "study") != null || PBool(p, "details", false)))
                res["details"] = StudyDetails(Study(sm, PStr(p, "study")));
            return res;
        });

        public ExecutionResponse SimCreateStudy(ToolRequest request) => Run(request, true, p =>
        {
            var sm = StudyMgr();
            string name = PStr(p, "name", "Static 1");
            string type = PStr(p, "study_type", "static").ToLowerInvariant();
            int t;
            switch (type)
            {
                case "static": t = (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeStatic; break;
                case "frequency": t = (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeFrequency; break;
                case "buckling": t = (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeBuckling; break;
                default: throw new ToolFail("BAD_PARAMETER", "study_type must be static, frequency or buckling.");
            }
            // SW 2024 rejects '.' and ',' in a study name (err 2, as for a taken name): "Опора 6,2 кН" fails, "Опора 6200 Н" works
            int bad = name.IndexOfAny(new[] { '.', ',' });
            if (bad >= 0) throw new ToolFail("BAD_PARAMETER", $"Study name '{name}' has '{name[bad]}' — SolidWorks refuses '.' and ',' in study names (e.g. write 6200 N, not 6.2 kN).");
            if (StudyNames(sm).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (!PBool(p, "replace", false)) throw new ToolFail("STUDY_EXISTS", $"'{name}' exists (replace=true to recreate).");
                sm.DeleteStudy(name);
            }
            int err = 0;
            var st = sm.CreateNewStudy3(name, t, 0, out err) as CWStudy;
            if (st == null)
                throw new ToolFail("STUDY_CREATE_FAILED", $"CreateNewStudy3 err={err}: " + (err == 1 ? "no solid body in the document." :
                    err == 2 ? "SolidWorks refused the name (taken, or a character it does not accept)." : "(swsStudyError_e); Simulation license?"));
            Study(sm, name);
            return new JObject { ["study"] = st.Name, ["type"] = type, ["studies"] = new JArray(StudyNames(sm)) };
        });

        public ExecutionResponse SimDeleteStudy(ToolRequest request) => Run(request, true, p =>
        {
            var sm = StudyMgr();
            string name = PStr(p, "study") ?? throw new ToolFail("MISSING_PARAMETER", "study is required.");
            Study(sm, name);
            sm.DeleteStudy(name);
            return new JObject { ["deleted"] = name, ["studies"] = new JArray(StudyNames(sm)) };
        });

        // Library material for every solid body and shell of the study (optionally only `components`).
        // Parts that already carry a SOLIDWORKS material get it automatically — this is for the rest.
        public ExecutionResponse SimSetMaterial(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            string mat = PStr(p, "material") ?? throw new ToolFail("MISSING_PARAMETER", "material is required (e.g. 'AISI 304', 'Plain Carbon Steel').");
            string lib = PStr(p, "library");
            if (lib == null)
            {
                lib = LangFile(System.IO.Path.Combine("sldmaterials", "solidworks materials.sldmat"))
                      ?? throw new ToolFail("LIBRARY_NOT_FOUND", "solidworks materials.sldmat not found; pass library.");
            }
            var only = PStrList(p, "components");
            var done = new JArray(); var failed = new JArray();
            var smgr = study.SolidManager as ICWSolidManager;
            for (int i = 0; smgr != null && i < smgr.ComponentCount; i++)
            {
                int e1; var comp = smgr.GetComponentAt(i, out e1) as ICWSolidComponent; if (comp == null) continue;
                string cname = comp.ComponentName;
                if (only.Count > 0 && !only.Any(o => cname.StartsWith(o, StringComparison.OrdinalIgnoreCase))) continue;
                for (int j = 0; j < comp.SolidBodyCount; j++)
                {
                    int e2; var body = comp.GetSolidBodyAt(j, out e2) as ICWSolidBody; if (body == null) continue;
                    if (body.SetLibraryMaterial2(lib, mat)) done.Add(cname + "/" + body.SolidBodyName); else failed.Add(cname + "/" + body.SolidBodyName);
                }
            }
            var shm = study.ShellManager as ICWShellManager;
            for (int i = 0; shm != null && i < shm.ShellCount; i++)
            {
                int e3; var sh = shm.GetShellAt(i, out e3) as ICWShell; if (sh == null) continue;
                if (sh.SetLibraryMaterial2(lib, mat)) done.Add("shell:" + sh.Name); else failed.Add("shell:" + sh.Name);
            }
            if (done.Count == 0) throw new ToolFail("MATERIAL_FAILED", $"'{mat}' not applied to anything (name in {System.IO.Path.GetFileName(lib)}?). Failed: {failed}");
            return new JObject { ["study"] = study.Name, ["material"] = mat, ["applied"] = done, ["failed"] = failed };
        });

        public ExecutionResponse SimAddFixture(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            string type = PStr(p, "fixture_type", "fixed").ToLowerInvariant();
            int rt;
            switch (type)
            {
                case "fixed": rt = (int)swsRestraintType_e.swsRestraintTypeFixed; break;
                case "immovable": rt = (int)swsRestraintType_e.swsRestraintTypeImmovable; break;
                case "roller": case "slider": rt = (int)swsRestraintType_e.swsRestraintTypeRoller; break;
                case "hinge": rt = (int)swsRestraintType_e.swsRestraintTypeHinge; break;
                default: throw new ToolFail("BAD_PARAMETER", "fixture_type must be fixed, immovable, roller or hinge.");
            }
            var faces = SimFaces(d, PArr(p, "faces"));
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            int err = 0;
            var r = lr.AddRestraint(rt, faces, null, out err) as CWRestraint;
            if (r == null) throw new ToolFail("FIXTURE_FAILED", $"AddRestraint err={err} (swsRestraintError_e).");
            return new JObject { ["study"] = study.Name, ["fixture"] = type, ["faces"] = faces.Length, ["loads_and_fixtures"] = lr.Count };
        });

        public ExecutionResponse SimAddLoad(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var study = Study(StudyMgr(), PStr(p, "study"));
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            string kind = PStr(p, "load_type", "force").ToLowerInvariant();
            double value = PNum(p, "value", 0);
            var dir = PArr(p, "direction").Select(x => (double)x).ToArray();   // [x,y,z] global; empty = normal to face
            int err = 0;
            var res = new JObject { ["study"] = study.Name, ["load_type"] = kind };
            if (kind == "gravity")
            {
                // Self-weight: gravity relative to the Top plane, default -Y (9.81 m/s²).
                string refPlane = PStr(p, "reference_plane", "");
                var plane = FindFeatureByName(d, refPlane, null)
                            ?? DefaultPlane(d.FirstFeature() as IFeature, refPlane == "" ? "top" : refPlane);
                var g = lr.AddGravity(plane, out err) as CWGravity;
                if (g == null) throw new ToolFail("LOAD_FAILED", $"AddGravity err={err}.");
                double gv = value == 0 ? 9.81 : value;
                var gd = dir.Length == 3 ? dir : new[] { 0.0, -1.0, 0.0 };
                double n = Math.Sqrt(gd.Sum(x => x * x));
                try
                {
                    g.GravityBeginEdit();
                    // Values are (plane dir1, plane dir2, plane normal); for the Top plane: dir1=+X, dir2=-Z, normal=+Y.
                    g.SetGravitationalAcclerationValues(gd[0] / n * gv, -gd[2] / n * gv, gd[1] / n * gv);
                    g.GravityEndEdit();
                }
                catch (Exception ex) { res["warning"] = "gravity direction not set: " + ex.Message; }
                res["g_m_s2"] = gv; res["direction"] = new JArray(gd);
                return res;
            }
            var faces = SimFaces(d, PArr(p, "faces"));
            res["faces"] = faces.Length;
            if (kind == "pressure")
            {
                var pr = lr.AddPressure((int)swsPressureType_e.swsPressureTypeNormal, faces, null, out err) as CWPressure;
                if (pr == null) throw new ToolFail("LOAD_FAILED", $"AddPressure err={err}.");
                pr.PressureBeginEdit();
                // Setting Unit does not stick (read back 0 = N/m² live), so the value goes in Pa.
                pr.Unit = (int)swsPressureUnit_e.swsPressureUnit_NewtonsPerMeterSquared;
                pr.Value = value * 1e6;
                if (PBool(p, "reverse", false)) pr.ReverseDirection2 = true;
                int pe = pr.PressureEndEdit();
                res["pressure_mpa"] = value;
                res["readback"] = $"unit={pr.Unit} value={pr.Value} endEdit={pe}";
                return res;
            }
            if (kind != "force") throw new ToolFail("BAD_PARAMETER", "load_type must be force, pressure or gravity.");
            CWForce f;
            if (dir.Length == 3)
            {
                // A directional force needs a reference (err 2 without one). The Front plane's in-plane
                // directions 1/2 and its normal ARE global X/Y/Z, so components map 1:1.
                d.ClearSelection2(true);
                object front = SelectPlaneFlexible(d, "Front Plane", false, 0) ? (d.SelectionManager as ISelectionMgr).GetSelectedObject6(1, -1) : null;
                d.ClearSelection2(true);
                if (front == null) throw new ToolFail("REFERENCE_NOT_FOUND", "Front plane not found (needed as the direction reference).");
                f = lr.AddForce2((int)swsForceType_e.swsForceTypeForceOrMoment, (int)swsSelectionType_e.swsSelectionFaceEdgeVertexPoint,
                    faces, front, out err) as CWForce;
                if (f == null) throw new ToolFail("LOAD_FAILED", $"AddForce2(directional) err={err} (swsForceError_e).");
                double n = Math.Sqrt(dir.Sum(x => x * x));
                double fx = dir[0] / n * value, fy = dir[1] / n * value, fz = dir[2] / n * value;
                f.ForceBeginEdit();
                f.Unit = (int)swsForceUnit_e.swsForceUnitNOrNm;
                f.SetForceComponentValues2(Math.Abs(fx) > 1e-12, Math.Abs(fy) > 1e-12, Math.Abs(fz) > 1e-12, fx, fy, fz);
                f.ForceEndEdit();
                bool b1, b2, b3; double r1, r2, r3;
                f.GetForceComponentValues2(out b1, out b2, out b3, out r1, out r2, out r3);
                res["force_n"] = new JArray(Math.Round(r1, 3), Math.Round(r2, 3), Math.Round(r3, 3));
            }
            else
            {
                f = lr.AddForce2((int)swsForceType_e.swsForceTypeNormal, (int)swsSelectionType_e.swsSelectionFaceEdgeVertexPoint, faces, null, out err) as CWForce;
                if (f == null) throw new ToolFail("LOAD_FAILED", $"AddForce2 err={err}.");
                f.ForceBeginEdit();
                f.Unit = (int)swsForceUnit_e.swsForceUnitNOrNm;
                f.NormalForceOrTorqueValue = value;
                f.ForceEndEdit();
                res["normal_force_n"] = f.NormalForceOrTorqueValue;
            }
            return res;
        });

        // Global contact for assemblies: bonded (SW default) | no_penetration | free.
        public ExecutionResponse SimContact(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            var cm = study.ContactManager as ICWContactManager ?? throw new ToolFail("NO_CONTACT_MANAGER", "Study has no contact manager (part study?).");
            string type = PStr(p, "global_contact", "bonded").ToLowerInvariant();
            int ct;
            switch (type)
            {
                case "bonded": ct = (int)swsContactType_e.swsContactTypeBonded; break;
                case "no_penetration": ct = (int)swsContactType_e.swsContactTypeStaticNoPenetration; break;
                case "free": ct = (int)swsContactType_e.swsContactTypeFreeOrInsulated; break;
                default: throw new ToolFail("BAD_PARAMETER", "global_contact must be bonded, no_penetration or free.");
            }
            cm.SetGlobalContact(ct, 0);
            int t2, o2; cm.GetGlobalContact(out t2, out o2);
            return new JObject { ["study"] = study.Name, ["global_contact"] = ((swsContactType_e)t2).ToString().Replace("swsContactType", "") };
        });

        public ExecutionResponse SimMesh(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            var mesh = study.Mesh as ICWMesh ?? throw new ToolFail("NO_MESH", "Study mesh unavailable.");
            mesh.Quality = PStr(p, "quality", "high") == "draft" ? (int)swsMeshQuality_e.swsMeshQualityDraft : (int)swsMeshQuality_e.swsMeshQualityHigh;
            string mesher = PStr(p, "mesher", "");
            if (mesher == "standard") mesh.MesherType = (int)swsMesherTypeNew_e.swsMesherType_Standard;
            else if (mesher == "curvature") mesh.MesherType = (int)swsMesherTypeNew_e.swsMesherType_CB;
            else if (mesher == "blended") mesh.MesherType = (int)swsMesherTypeNew_e.swsMesherType_BCB;
            double size = PNum(p, "element_size_mm", 0), tol = PNum(p, "tolerance_mm", 0);
            if (size <= 0)
            {
                double ds = 0, dt = 0;
                try { mesh.GetDefaultElementSizeAndTolerance((int)swsLinearUnit_e.swsLinearUnitMillimeters, out ds, out dt); } catch { }
                size = ds > 0 ? ds : 5.0; if (tol <= 0) tol = dt;
            }
            if (tol <= 0) tol = size * 0.05;
            var sw = Stopwatch.StartNew();
            int err = study.CreateMesh((int)swsLinearUnit_e.swsLinearUnitMillimeters, size, tol);   // 1st arg = UNIT
            int elems = 0, nodes = 0; try { elems = mesh.ElementCount; nodes = mesh.NodeCount; } catch { }
            int state = -1; try { state = mesh.MeshState; } catch { }
            // ElementCount can be the OLD mesh's (or another study's) after a failed re-mesh: MeshState tells
            if (err != 0 && (elems == 0 || state != (int)swsMeshState_e.swsMeshStateExistsAndCurrent))
                throw new ToolFail("MESH_FAILED", $"CreateMesh err={err} (swsStudyMeshError_e) at {size} mm.");
            return new JObject { ["study"] = study.Name, ["element_size_mm"] = Math.Round(size, 3), ["tolerance_mm"] = Math.Round(tol, 4),
                                 ["elements"] = elems, ["nodes"] = nodes, ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1), ["err"] = err };
        });

        // Millimetres in one swsLinearUnit_e unit (mm 0, cm 1, m 2, in 3, ft 4, Å 6, nm 7, µm 8, mil 9, µin 10).
        private static double SimUnitMm(int unit)
        {
            switch (unit)
            {
                case 0: return 1;
                case 1: return 10;
                case 2: return 1000;
                case 3: case 5: return 25.4;
                case 4: return 304.8;
                case 6: return 1e-7;
                case 7: return 1e-6;
                case 8: return 1e-3;
                case 9: return 0.0254;
                case 10: return 2.54e-5;
                default: return 1000;
            }
        }

        // FoS of a shell: the lower of its top and bottom faces (swsFOS_ShellFaceOption_MinAmongTopAndBottomFace) — the
        // bottom face of a bent sheet carries membrane + bending too; ignored for solids
        private const int ShellFosBothFaces = 3;

        private JObject SimResultsOf(CWStudy study, double yieldMpa)
        {
            var r = study.Results as ICWResults ?? throw new ToolFail("NO_RESULTS", "No results — sim_run first.");
            var res = new JObject { ["study"] = study.Name };
            int err = 0;
            int type = study.AnalysisType;
            if (type == (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeFrequency)
            {
                var fr = r.GetResonantFrequencies(out err) as Array;
                res["frequencies"] = fr == null ? null : new JArray(fr.Cast<object>().Select(x => Math.Round(Convert.ToDouble(x), 3)));
                return res;
            }
            if (type == (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeBuckling)
            {
                var bf = r.GetBucklingLoadFactors(out err) as Array;
                res["buckling_load_factors"] = bf == null ? null : new JArray(bf.Cast<object>().Select(x => Math.Round(Convert.ToDouble(x), 4)));
                return res;
            }
            try { res["von_mises_mpa"] = (JToken)MinMax(r.GetMinMaxStress((int)swsStressComponent_e.swsStressComponentVON, 0, 1, null, 0, out err), 1e-6, true); }
            catch (Exception ex) { res["von_mises_error"] = ex.Message; }
            try { res["displacement_mm"] = (JToken)MinMax(r.GetMinMaxDisplacement((int)swsDisplacementComponent_e.swsDisplacementComponentURES, 1, null, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out err), 1, true); }
            catch (Exception ex) { res["displacement_error"] = ex.Message; }
            try
            {
                object fos = yieldMpa > 0
                    ? r.GetMinMaxFactorOfSafetyWithDetailSettings2(true, null, 0, false, 0.0, 0, 2, yieldMpa * 1e6, 2, yieldMpa * 1e6, 1.0, 1.0, false, ShellFosBothFaces, 0, out err)
                    : r.GetMinMaxFactorOfSafety2(true, null, 4, ShellFosBothFaces, 0, out err);
                var f = MinMax(fos, 1, false) as JObject;
                if (f != null && (double)f["min"] > 1e12)
                {
                    f = null;   // shell (sheet-metal) studies: the automatic FoS comes back as 1e16 — meaningless
                    res["fos_note"] = "automatic FoS not available for this mesh (shells) — pass yield_mpa";
                }
                if (f != null) res["factor_of_safety"] = f;
                else if (res["fos_note"] == null) res["fos_note"] = $"FoS API returned no data (err={err}, {fos?.GetType().Name ?? "null"})";
                res["fos_basis"] = yieldMpa > 0 ? $"yield {yieldMpa} MPa (von Mises)" : "material (automatic)";
            }
            catch (Exception ex) { res["fos_error"] = ex.Message; }
            // Where are the extremes? Node coordinates from the mesh (GetNodes = [node, x, y, z]… in meters).
            try
            {
                var nodes = (study.Mesh as ICWMesh)?.GetNodes() as Array;
                if (nodes != null)
                {
                    var map = new Dictionary<int, double[]>();
                    for (int i = 0; i + 3 < nodes.Length; i += 4)
                        map[Convert.ToInt32(nodes.GetValue(i))] = new[] { Convert.ToDouble(nodes.GetValue(i + 1)) * MM, Convert.ToDouble(nodes.GetValue(i + 2)) * MM, Convert.ToDouble(nodes.GetValue(i + 3)) * MM };
                    foreach (var key in new[] { "von_mises_mpa", "displacement_mm" })
                        if (res[key] is JObject o && o["max_node"] != null && map.TryGetValue((int)o["max_node"], out var xyz))
                            o["max_at_mm"] = R(xyz, 1);
                    if (res["von_mises_mpa"] is JObject v && v["max_node"] != null)
                        try { PeakOnBoundary(study, r, map, v, res); } catch (Exception ex) { res["peak_check_error"] = ex.Message; }
                }
            }
            catch (Exception ex) { res["location_note"] = ex.Message; }
            var vm = res["von_mises_mpa"] as JObject;
            var y = StudyYieldMpa(study, res);   // reported always: the basis for hand checks and shell FoS
            if (yieldMpa <= 0 && res["factor_of_safety"] == null)
            {
                // shells: no automatic FoS — take the lowest yield strength of the study's materials
                if (y.HasValue)
                {
                    yieldMpa = y.Value;
                    res["fos_basis"] = $"yield {Math.Round(y.Value, 1)} MPa of the study material (lowest) / max von Mises";
                    res.Remove("fos_note");
                }
            }
            if (yieldMpa > 0 && vm?["max"] != null && res["factor_of_safety"] == null)
                res["factor_of_safety_min"] = Math.Round(yieldMpa / (double)vm["max"], 3);   // = yield / max von Mises
            SimConnectorResults(study, r, res);
            return res;
        }

        // A von Mises peak ON a fixture or a loaded face (within 1.5 elements) is made by the idealised boundary condition
        // (a welded support bracket: 240 MPa on the edge of a rigidly fixed bolt hole, 264 MPa with a 1 mm mesh there, while the
        // part away from it carries ~90 MPa); a FoS from it says nothing about the part. Then the max farther than two
        // elements from every fixture and load is reported too.
        private void PeakOnBoundary(CWStudy study, ICWResults r, Dictionary<int, double[]> map, JObject vm, JObject res)
        {
            var mesh = study.Mesh as ICWMesh;
            var lr = study.LoadsAndRestraintsManager as ICWLoadsAndRestraintsManager;
            if (mesh == null || lr == null || !map.TryGetValue((int)vm["max_node"], out var at)) return;
            double h = Math.Max(mesh.ElementSize * MM, 0.5);
            var bounds = new List<(ICWLoadsAndRestraints item, List<double[]> pts, List<double[][]> tris)>();
            for (int i = 0; i < lr.Count; i++)
            {
                int e; var item = lr.GetLoadsAndRestraints(i, out e) as ICWLoadsAndRestraints;
                if (item == null || !(item.Type == (int)swsLoadsAndRestraintsType_e.swsLoadsAndRestraintsTypeRestraint
                                      || item.Type == (int)swsLoadsAndRestraintsType_e.swsLoadsAndRestraintsTypeForce
                                      || item.Type == (int)swsLoadsAndRestraintsType_e.swsLoadsAndRestraintsTypePressure)) continue;
                var b = (item, new List<double[]>(), new List<double[][]>());
                BoundaryOf(mesh, item, map, b.Item2, b.Item3);
                if (b.Item2.Count + b.Item3.Count > 0) bounds.Add(b);
            }
            var hit = bounds.Select(b => (b.item, d: BoundaryDistance(at, b.pts, b.tris, 0))).Where(x => x.d <= 1.5 * h)
                            .OrderBy(x => x.d).FirstOrDefault();
            if (hit.item == null) return;
            bool fixture = hit.item.Type == (int)swsLoadsAndRestraintsType_e.swsLoadsAndRestraintsTypeRestraint;
            vm["max_on"] = new JObject { ["name"] = hit.item.Name, ["kind"] = fixture ? "fixture" : "load", ["distance_mm"] = Math.Round(hit.d, 2) };
            // the highest stress farther than two elements from every fixture and load, and the next hot spots (10 mm apart):
            // a sharp corner of a joint (a bonded edge = a weld, a slot corner) is a singularity too — on that bracket the
            // max away went 101 → 189 MPa with the mesh 3.6 → 1.8 mm, moving to the corner of the welded nut
            double keep = 2 * h;
            var (idx, P) = MeshNodesMm(mesh);
            var f = NodeResults(r, "stress", idx, P.GetLength(0));
            var spots = new List<(double v, double[] at)>();
            foreach (int i in Enumerable.Range(0, f.Val.Length).Where(i => f.Has[i]).OrderByDescending(i => f.Val[i]))
            {
                var q = new[] { P[i, 0], P[i, 1], P[i, 2] };
                if (spots.Any(s => Dist(s.at, q) < Math.Max(10, 3 * h))) continue;
                if (bounds.Any(b => BoundaryDistance(q, b.pts, b.tris, keep) <= keep)) continue;
                spots.Add((f.Val[i], q));
                if (spots.Count == 3) break;
            }
            if (spots.Count > 0)
                vm["max_away_from_boundary"] = new JObject
                {
                    ["max"] = Math.Round(spots[0].v, 3), ["at_mm"] = R(spots[0].at, 1), ["excluded_within_mm"] = Math.Round(keep, 2),
                    ["hot_spots"] = new JArray(spots.Select(s => new JObject { ["max"] = Math.Round(s.v, 3), ["at_mm"] = R(s.at, 1) }))
                };
            res["peak_note"] = $"The von Mises max sits on the {(fixture ? "fixture" : "load")} '{hit.item.Name}': there the stress is made by the idealised "
                + "boundary condition (a rigid fixture, a load on a small face — at a sharp edge it grows without limit as the mesh is refined), so the max "
                + "and the FoS from it do not describe the part. Look at max_away_from_boundary (farther than two elements from every fixture and load; "
                + "hot_spots = the next highest places) — but a hot spot at a sharp corner of a joint between parts (a bonded edge = a weld, a slot "
                + "corner) is a singularity too: solve a copy on a finer mesh (sim_copy_study + sim_mesh) and compare; what keeps growing is "
                + "judged by hand (weld_check, bolt_check), what settles is the part's stress.";
        }

        // A fixture's / load's faces as mesh nodes (mm) or, when a face has none (a fixture on the solid face of a sheet-metal
        // part meshed as a shell), as the face's tessellation triangles (mm, assembly space).
        private void BoundaryOf(ICWMesh mesh, ICWLoadsAndRestraints item, Dictionary<int, double[]> map, List<double[]> pts, List<double[][]> tris)
        {
            for (int k = 0; k < item.EntityCount; k++)
            {
                object ent = null; int e, n;
                try { ent = item.GetEntityAt(k, out e); } catch { }
                if (ent == null) continue;
                double[] data = null;
                try { data = ToDoubles(mesh.GetNodeDataFromEntity(ent, out n)); } catch { }
                int before = pts.Count;
                for (int j = 0; data != null && j + 3 < data.Length; j += 4)
                    if (map.TryGetValue((int)data[j], out var q)) pts.Add(q);
                if (pts.Count == before && ent is IFace2 face) FaceTrianglesMm(face, tris);
            }
        }

        // A face's tessellation as triangles in mm, assembly space: the tessellation of a component's face is in the
        // component's space (seen: the nut's face y 0..13 mm, its mesh nodes 10..23).
        private static void FaceTrianglesMm(IFace2 face, List<double[][]> tris)
        {
            float[] tess = null;
            try { tess = face.GetTessTriangles(true) as float[]; } catch { }
            if (tess == null) return;
            var xf = (((face as IEntity)?.GetComponent() as IComponent2)?.Transform2 as MathTransform)?.ArrayData as double[];
            for (int j = 0; j + 8 < tess.Length; j += 9)
            {
                var tri = new double[3][];
                for (int v = 0; v < 3; v++)
                {
                    var p = new double[] { tess[j + 3 * v], tess[j + 3 * v + 1], tess[j + 3 * v + 2] };
                    if (xf != null) p = Apply(xf, p);
                    tri[v] = new[] { p[0] * MM, p[1] * MM, p[2] * MM };
                }
                tris.Add(tri);
            }
        }

        // Distance (mm) from p to the nearest point / triangle; stops early once at or below `enough` (0 = exact).
        private static double BoundaryDistance(double[] p, List<double[]> pts, List<double[][]> tris, double enough)
        {
            double best = double.MaxValue;
            foreach (var q in pts) { best = Math.Min(best, Dist(p, q)); if (best <= enough) return best; }
            foreach (var t in tris) { best = Math.Min(best, PointTriangleDistance(p, t[0], t[1], t[2])); if (best <= enough) return best; }
            return best;
        }

        // Closest point on triangle abc (Ericson, Real-Time Collision Detection 5.1.5) → its distance from p.
        private static double PointTriangleDistance(double[] p, double[] a, double[] b, double[] c)
        {
            double[] Sub(double[] u, double[] v) => new[] { u[0] - v[0], u[1] - v[1], u[2] - v[2] };
            double Dot(double[] u, double[] v) => u[0] * v[0] + u[1] * v[1] + u[2] * v[2];
            double[] At(double[] o, double[] u, double s, double[] w = null, double t = 0) =>
                new[] { o[0] + u[0] * s + (w == null ? 0 : w[0] * t), o[1] + u[1] * s + (w == null ? 0 : w[1] * t), o[2] + u[2] * s + (w == null ? 0 : w[2] * t) };
            var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(p, a);
            double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return Dist(p, a);
            var bp = Sub(p, b); double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return Dist(p, b);
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return Dist(p, At(a, ab, d1 / (d1 - d3)));
            var cp = Sub(p, c); double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return Dist(p, c);
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return Dist(p, At(a, ac, d2 / (d2 - d6)));
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Dist(p, At(b, Sub(c, b), (d4 - d3) / ((d4 - d3) + (d5 - d6))));
            double den = 1 / (va + vb + vc);
            return Dist(p, At(a, ab, vb * den, ac, vc * den));
        }

        // Mesh (if needed) + solve + results summary. BLOCKS the execution server for the solve.
        public ExecutionResponse SimRun(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            var sw = Stopwatch.StartNew();
            JObject meshed = null;
            var mesh = study.Mesh as ICWMesh;
            int state = -1; try { state = mesh?.MeshState ?? -1; } catch { }
            // ElementCount of a fresh study can be stale (another study's mesh) — MeshState is reliable
            if (mesh != null && state != (int)swsMeshState_e.swsMeshStateExistsAndCurrent)
            {
                // not meshed yet (RunAnalysis would fail with MeshNotFound = 23): SolidWorks' default size; a mesh made
                // before and out of date now (a mesh control added, the model changed) keeps the size it was made with
                double ds = 0, dt = 0;
                if (state == (int)swsMeshState_e.swsMeshStateExistsAndNotCurrent)
                    try { ds = mesh.ElementSize * SimUnitMm(mesh.Unit); dt = mesh.Tolerance * SimUnitMm(mesh.Unit); } catch { ds = 0; }
                if (ds <= 0)
                    try { mesh.GetDefaultElementSizeAndTolerance((int)swsLinearUnit_e.swsLinearUnitMillimeters, out ds, out dt); } catch { }
                if (ds <= 0) ds = 5;
                int me = study.CreateMesh((int)swsLinearUnit_e.swsLinearUnitMillimeters, ds, dt > 0 ? dt : ds * 0.05);
                meshed = new JObject { ["element_size_mm"] = Math.Round(ds, 3), ["elements"] = mesh.ElementCount, ["err"] = me };
            }
            int err = study.RunAnalysis();
            ExecLog.Write($"sim_run: '{study.Name}' err={err} in {sw.ElapsedMilliseconds} ms");
            if (err != 0) throw new ToolFail("RUN_FAILED", $"RunAnalysis err={err} ({((swsRunAnalysisError_e)err).ToString().Replace("swsRunAnalysisError", "")}) — check material on every body, fixtures, loads, mesh.");
            var res = SimResultsOf(study, PNum(p, "yield_mpa", 0));
            if (meshed != null) res["auto_mesh"] = meshed;
            res["solve_seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            return res;
        });

        public ExecutionResponse SimResults(ToolRequest request) => Run(request, false, p =>
            SimResultsOf(Study(StudyMgr(), PStr(p, "study")), PNum(p, "yield_mpa", 0)));

        // Result plot shown in the graphics area (the adapter then captures it via view_image).
        public ExecutionResponse SimPlot(ToolRequest request) => Run(request, true, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            var r = study.Results as ICWResults ?? throw new ToolFail("NO_RESULTS", "No results — sim_run first.");
            string kind = PStr(p, "result", "stress").ToLowerInvariant();
            int rt, comp, units;
            switch (kind)
            {
                case "stress": rt = (int)swsPlotResultTypes_e.swsResultStress; comp = (int)swsStressComponent_e.swsStressComponentVON; units = (int)swsStrengthUnit_e.swsStrengthUnitNewtonPerSquareMillimeter; break;
                case "displacement": rt = (int)swsPlotResultTypes_e.swsResultDisplacementOrAmplitude; comp = (int)swsDisplacementComponent_e.swsDisplacementComponentURES; units = (int)swsLinearUnit_e.swsLinearUnitMillimeters; break;
                case "fos": rt = (int)swsPlotResultTypes_e.swsResultFactorOfSafety; comp = 0; units = 0; break;
                default: throw new ToolFail("BAD_PARAMETER", "result must be stress, displacement or fos.");
            }
            int err = 0;
            var plot = r.CreatePlot(rt, comp, units, false, out err) as CWPlot;
            if (plot == null) throw new ToolFail("PLOT_FAILED", $"CreatePlot err={err}.");
            double scale = PNum(p, "deform_scale", 0);
            string warning = null;
            try
            {
                int de = plot.ShowDeformedPlot(PBool(p, "deformed", kind != "fos"), scale > 0 ? 2 : 0, scale, true);
                if (de != 0) warning = $"The deformed shape was not applied (ShowDeformedPlot err={de}); the plot is undeformed.";
            }
            catch (Exception ex) { warning = $"The deformed shape was not applied ({ex.Message}); the plot is undeformed."; }
            string name; plot.GetPlotName(out name);
            var mm = plot.GetMinMaxResultValues(out err) as Array;
            var res = new JObject { ["study"] = study.Name, ["plot"] = name, ["result"] = kind };
            if (mm != null) res["min_max"] = new JArray(mm.Cast<object>().Select(x => Math.Round(Convert.ToDouble(x), 4)));
            // NOTE: no image here — in the hidden own instance neither SaveAs nor SaveBMP renders the
            // Simulation overlay (tested live, SW 2024). The plot lives in the study for the user's GUI.
            plot.ActivatePlot();
            return WithWarning(res, warning);
        });
    }
}
