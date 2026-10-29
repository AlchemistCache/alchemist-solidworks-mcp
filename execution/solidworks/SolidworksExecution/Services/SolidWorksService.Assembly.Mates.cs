using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Mates: list / edit / suppress / delete + constraint status =====================
    // list_mates          — every mate (incl. ones in sub-folders) with type, alignment, value (mm/deg), components,
    //                       suppressed and ERROR state (over-defined / unsatisfied), filter by component / errors only.
    // edit_mate           — set value, flip alignment, suppress/unsuppress, rename, delete.
    // constraint_status   — per component: fixed / fully / under / over defined + mate count (what is still free?).
    public partial class SolidWorksService
    {

        private JObject MateInfo(IFeature f)
        {
            var m = f.GetSpecificFeature2() as IMate2;
            var o = new JObject { ["name"] = f.Name, ["type"] = MateName(m.Type), ["alignment"] = MateAlignName(m.Alignment) };
            var comps = new JArray();
            for (int i = 0; i < m.GetMateEntityCount(); i++)
            {
                try { var c = (m.MateEntity(i) as IMateEntity2)?.ReferenceComponent as IComponent2; comps.Add(c?.Name2 ?? "(assembly)"); } catch { }
            }
            o["components"] = comps;
            // only mate types that carry a value have a dimension (the lookup costs ~40 ms per mate)
            int mtype = m.Type;
            bool hasValue = mtype == (int)swMateType_e.swMateDISTANCE || mtype == (int)swMateType_e.swMateANGLE || mtype == (int)swMateType_e.swMateGEAR
                         || mtype == (int)swMateType_e.swMatePROFILECENTER || mtype == (int)swMateType_e.swMateLINEARCOUPLER || mtype == (int)swMateType_e.swMateSLOT
                         || mtype == (int)swMateType_e.swMateWIDTH || mtype == (int)swMateType_e.swMateSCREW || mtype == (int)swMateType_e.swMateRACKPINION;
            if (hasValue) try
            {
                var dd = f.GetFirstDisplayDimension() as IDisplayDimension;
                var dim = dd?.GetDimension2(0) as IDimension;
                if (dim != null)
                {
                    double v = dim.SystemValue;
                    if (m.Type == (int)swMateType_e.swMateANGLE) o["value_deg"] = Math.Round(v * 180 / Math.PI, 4);
                    else o["value_mm"] = Math.Round(v * MM, 4);
                    o["dimension"] = dim.FullName;
                }
            }
            catch { }
            bool[] sup = f.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
            o["suppressed"] = sup != null && sup.Length > 0 && sup[0];
            bool warn; int code = f.GetErrorCode2(out warn);
            if (code != 0) { o["error_code"] = code; o["error"] = ((swFeatureError_e)code).ToString(); o["is_warning"] = warn; }
            return o;
        }

        private IFeature FindMate(IModelDoc2 d, string name)
        {
            var all = MateFeatures(d).ToList();
            return all.FirstOrDefault(f => f.Name == name)
                ?? all.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolFail("MATE_NOT_FOUND", $"No mate '{name}'. Mates: " + string.Join(", ", all.Take(30).Select(f => f.Name)));
        }

        public ExecutionResponse ListMates(ToolRequest request) => Run(request, false, p =>
        {
            ActiveAsmOrFail(out var d);
            string comp = PStr(p, "component");
            bool errorsOnly = PBool(p, "errors_only", false);
            var tw = System.Diagnostics.Stopwatch.StartNew();
            var feats = MateFeatures(d).ToList();
            long tEnum = tw.ElapsedMilliseconds;
            var list = feats.Select(MateInfo).ToList();
            ExecLog.Write($"list_mates: enumerate {feats.Count} in {tEnum} ms, details {tw.ElapsedMilliseconds - tEnum} ms");
            int total = list.Count, errors = list.Count(x => x["error_code"] != null);
            if (comp != null) list = list.Where(x => x["components"].Any(c => ((string)c).IndexOf(comp, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            if (errorsOnly) list = list.Where(x => x["error_code"] != null).ToList();
            int max = PInt(p, "max_items", 200);
            return new JObject { ["total"] = total, ["with_errors"] = errors, ["returned"] = Math.Min(max, list.Count), ["mates"] = new JArray(list.Take(max)) };
        });

        // ---------------------------------------------------------------- mate entities
        // Entity spec: {"component","face_index"} | {"component","edge_index"} | {"plane"} (assembly plane) |
        // {"component","plane"} (a plane of that component) | {"component"} (the component itself — lock / coupler).
        private object MateEntityOf(IModelDoc2 d, IAssemblyDoc a, JToken t)
        {
            var o = t as JObject ?? throw new ToolFail("BAD_PARAMETER", "Each mate entity must be an object.");
            string comp = (string)o["component"];
            if (o["face_index"] != null || o["edge_index"] != null)
            {
                bool face = o["face_index"] != null;
                if (comp == null) throw new ToolFail("BAD_PARAMETER", "face_index/edge_index need 'component'.");
                return ViaSelection(d, GetComponentEntity(a, comp, face ? "face" : "edge", (int)(face ? o["face_index"] : o["edge_index"])));
            }
            if (o["plane"] != null)
            {
                string pn = (string)o["plane"];
                if (comp != null)
                {
                    var c = FindComp(a, comp);
                    return ViaSelection(d, (c.FeatureByName(pn) as IFeature) ?? DefaultPlane(c.FirstFeature() as IFeature, pn)
                           ?? throw new ToolFail("PLANE_NOT_FOUND", $"No plane '{pn}' in {c.Name2}."));
                }
                return ViaSelection(d, FindFeatureByName(d, pn, null) ?? DefaultPlane(d.FirstFeature() as IFeature, pn)
                       ?? throw new ToolFail("PLANE_NOT_FOUND", $"No plane '{pn}' (Front / Top / Right, or a plane in the feature tree)."));
            }
            if (comp != null) return FindComp(a, comp);
            throw new ToolFail("BAD_PARAMETER", "mate entity needs face_index / edge_index / plane / component.");
        }

        // The assembly-context object SolidWorks itself hands out for a selected entity (what CreateMate expects).
        private static object ViaSelection(IModelDoc2 d, object entity)
        {
            d.ClearSelection2(true);
            bool ok = entity is IEntity en ? en.Select4(false, null) : entity is IFeature f && f.Select2(false, 0);
            object sel = ok ? (d.SelectionManager as ISelectionMgr).GetSelectedObject6(1, -1) : null;
            d.ClearSelection2(true);
            return sel ?? entity;
        }

        // Entity arrays for mate / pattern definitions must hold VT_DISPATCH elements (plain RCWs: CreateMate
        // silently creates nothing, CreateFeature faults) — observed live, same as in Simulation.
        private static object[] W(IEnumerable<object> ents) =>
            ents.Select(e => e is System.Runtime.InteropServices.DispatchWrapper ? e : new System.Runtime.InteropServices.DispatchWrapper(e)).ToArray();

        private static bool SetMateEntities(object def, object[] ents)
        {
            ents = W(ents);
            switch (def)
            {
                case ICoincidentMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IConcentricMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IParallelMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IPerpendicularMateFeatureData x: x.EntitiesToMate = ents; return true;
                case ITangentMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IDistanceMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IAngleMateFeatureData x: x.EntitiesToMate = ents; return true;
                case ILockMateFeatureData x: x.EntitiesToMate = ents; return true;
                case ISymmetricMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IProfileCenterMateFeatureData x: x.EntitiesToMate = ents; return true;
                case IGearMateFeatureData x: x.EntitiesToMate = ents; return true;
                case ISlotMateFeatureData x: x.EntitiesToMate = ents; return true;
                default: return false;
            }
        }

        // Create a mate the way the UI does: select the entities with selection MARKS, then AddMate5 (proven path,
        // same as add_mate). CreateMateData/CreateMate silently created nothing for these types on SW 2024.
        private IFeature AddMateBySelection(IModelDoc2 d, IAssemblyDoc a, int mateType, IEnumerable<Tuple<object, int>> sel,
            int align, bool flip = false, double dist = 0, double angle = 0, double gNum = 1, double gDen = 1,
            bool lockRot = false, int widthOpt = 0)
        {
            d.ClearSelection2(true);
            foreach (var s in sel)
                if (!SelectEntity(d, s.Item1, true, s.Item2))
                {
                    d.ClearSelection2(true);
                    throw new ToolFail("SELECTION_FAILED", "Could not select a mate entity — check that each one exists in the assembly.");
                }
            int before = MateFeatures(d).Count();
            int err;
            var mate = a.AddMate5(mateType, align, flip, dist, dist, dist, gNum, gDen, angle, angle, angle, false, lockRot, widthOpt, out err);
            d.ClearSelection2(true);
            d.EditRebuild3();
            var all = MateFeatures(d).ToList();
            if (mate == null || all.Count <= before)
                throw new ToolFail("MATE_FAILED", $"The {MateTypeName(mateType)} mate was not created (AddMate5, swAddMateError_e {err}) — check that the entity kinds fit the mate type (concentric wants cylindrical faces), their count and the alignment.");
            return all.Last();
        }

        private static IEnumerable<Tuple<object, int>> Marked(IEnumerable<object> ents, int mark) => ents.Select(e => Tuple.Create(e, mark));

        // Advanced / mechanical mates. symmetric, width, profile_center, gear, lock, slot: selection + AddMate5;
        // linear_coupler: CreateMate (works); hinge: CreateMate, else concentric + coincident (same motion).
        public ExecutionResponse AddMateAdvanced(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var d);
            Func<string, List<object>> E = key => PArr(p, key).Select(t => MateEntityOf(d, a, t)).ToList();
            string type = PStr(p, "mate_type") ?? throw new ToolFail("MISSING_PARAMETER", "mate_type is required.");
            int align = MateAlignFromName(PStr(p, "alignment"));
            IFeature made;
            switch (type)
            {
                case "symmetric":
                {
                    var plane = MateEntityOf(d, a, p["symmetry_plane"] ?? throw new ToolFail("MISSING_PARAMETER", "symmetry_plane is required."));
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMateSYMMETRIC, Marked(E("entities"), 1).Concat(Marked(new[] { plane }, 2)), align);
                    break;
                }
                case "width":
                {
                    string c = PStr(p, "constraint", "centered");
                    int opt = c == "free" ? (int)swMateWidthOptions_e.swMateWidth_Free : c == "distance" ? (int)swMateWidthOptions_e.swMateWidth_Dimension
                            : c == "percent" ? (int)swMateWidthOptions_e.swMateWidth_Percent : (int)swMateWidthOptions_e.swMateWidth_Centered;
                    double dist = c == "distance" ? PNum(p, "value_mm", 0) / MM : c == "percent" ? PNum(p, "percent", 50) / 100.0 : 0;
                    var wEnts = E("width_entities"); var tEnts = E("tab_entities");
                    made = null;
                    foreach (var mk in new[] { new[] { 1, 2 }, new[] { 1, 1 }, new[] { 2, 1 }, new[] { 4, 8 }, new[] { 1, 4 }, new[] { 4, 1 }, new[] { 0, 0 } })
                    {
                        try
                        {
                            made = AddMateBySelection(d, a, (int)swMateType_e.swMateWIDTH,
                                Marked(wEnts, mk[0]).Concat(Marked(tEnts, mk[1])), align, false, dist, 0, 1, 1, false, opt);
                            ExecLog.Write($"width mate: marks {mk[0]}/{mk[1]} ok");
                            break;
                        }
                        catch (ToolFail tf) { ExecLog.Write($"width mate: marks {mk[0]}/{mk[1]} -> {tf.Message}"); }
                    }
                    if (made == null && c == "centered" && wEnts.Count == 2 && tEnts.Count == 2)
                    {
                        // Equivalent of a centred width mate: a mid-plane between the width faces + the tab faces
                        // symmetric about it (AddMate5 returned swAddMateError 0 for width on SW 2024, every mark scheme).
                        d.ClearSelection2(true);
                        SelectEntity(d, wEnts[0], true, 0);
                        SelectEntity(d, wEnts[1], true, 1);
                        var mid = d.FeatureManager.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_MidPlane, 0,
                            (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_MidPlane, 0, 0, 0) as IFeature;
                        d.ClearSelection2(true);
                        if (mid == null) throw new ToolFail("MATE_FAILED", "width mate: could not build the mid-plane between the width faces.");
                        mid.Name = "Ширина_середина_" + Guid.NewGuid().ToString("N").Substring(0, 4);
                        made = AddMateBySelection(d, a, (int)swMateType_e.swMateSYMMETRIC, Marked(tEnts, 1).Concat(Marked(new object[] { mid }, 2)), align);
                        try { mid.Select2(false, 0); d.BlankRefGeom(); d.ClearSelection2(true); } catch { }
                        var winfo = MateInfo(made); winfo["note"] = $"centred width emulated: mid-plane '{mid.Name}' + symmetric mate (same kinematics)";
                        return winfo;
                    }
                    if (made == null) throw new ToolFail("MATE_FAILED", "width mate failed (SW 2024 AddMate5 width). Use constraint='centered' (emulated) or two distance mates.");
                    break;
                }
                case "profile_center":
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMatePROFILECENTER, Marked(E("entities"), 1), align,
                        PBool(p, "flip", false), PNum(p, "value_mm", 0) / MM, 0, 1, 1, PBool(p, "lock_rotation", false));
                    break;
                case "gear":
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMateGEAR, Marked(E("entities"), 1), align,
                        PBool(p, "reverse", false), 0, 0, PNum(p, "ratio_numerator", 1), PNum(p, "ratio_denominator", 1));
                    break;
                case "lock":
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMateLOCK,
                        Marked(PStrList(p, "components").Select(n => (object)FindComp(a, n)), 1), align);
                    break;
                case "slot":
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMateSLOT, Marked(E("entities"), 1), align);
                    break;
                case "linear_coupler":
                {
                    var m = (ILinearCouplerMateFeatureData)a.CreateMateData((int)swMateType_e.swMateLINEARCOUPLER);
                    m.MateEntity1 = MateEntityOf(d, a, p["entity1"]); m.MateEntity2 = MateEntityOf(d, a, p["entity2"]);
                    m.ReferenceComponent1 = FindComp(a, PStr(p, "reference_component1") ?? throw new ToolFail("MISSING_PARAMETER", "reference_component1 is required."));
                    m.ReferenceComponent2 = FindComp(a, PStr(p, "reference_component2") ?? throw new ToolFail("MISSING_PARAMETER", "reference_component2 is required."));
                    m.CouplerRatioNumerator = PNum(p, "ratio_numerator", 1); m.CouplerRatioDenominator = PNum(p, "ratio_denominator", 1);
                    m.Reverse = PBool(p, "reverse", false);
                    int before = MateFeatures(d).Count();
                    a.CreateMate(m); d.EditRebuild3();
                    var all = MateFeatures(d).ToList();
                    if (all.Count <= before) throw new ToolFail("MATE_FAILED", "linear coupler was not created — check the entities.");
                    made = all.Last();
                    break;
                }
                case "hinge":
                {
                    var conc = E("concentric_entities"); var coin = E("coincident_entities");
                    var m = (IHingeMateFeatureData)a.CreateMateData((int)swMateType_e.swMateHINGE);
                    m.set_EntitiesToMate((int)swHingeMateEntityType_e.swHingeMateEntityType_Concentric, W(conc));
                    m.set_EntitiesToMate((int)swHingeMateEntityType_e.swHingeMateEntityType_Coincident, W(coin));
                    m.MateAlignment = align;
                    int before = MateFeatures(d).Count();
                    a.CreateMate(m); d.EditRebuild3();
                    var all = MateFeatures(d).ToList();
                    if (all.Count > before) { made = all.Last(); break; }
                    // same kinematics as two standard mates
                    AddMateBySelection(d, a, (int)swMateType_e.swMateCONCENTRIC, Marked(conc, 1), align);
                    made = AddMateBySelection(d, a, (int)swMateType_e.swMateCOINCIDENT, Marked(coin, 1), align);
                    var info = MateInfo(made); info["note"] = "hinge emulated as concentric + coincident (identical motion)";
                    return info;
                }
                default: throw new ToolFail("BAD_PARAMETER", "mate_type must be symmetric, width, profile_center, hinge, gear, linear_coupler, lock or slot.");
            }
            return MateInfo(made);
        });

        // Standard mate (coincident / concentric / parallel / perpendicular / tangent / distance / angle) via AddMate5.
        private IFeature CreateStdMate(IModelDoc2 d, IAssemblyDoc a, string type, object[] ents, int align, double? value)
        {
            int t = MateTypeFromName(type);
            if (t < 0 || t == (int)swMateType_e.swMateLOCK)
                throw new ToolFail("BAD_PARAMETER", "mate type must be coincident, concentric, parallel, perpendicular, tangent, distance or angle.");
            double dist = t == (int)swMateType_e.swMateDISTANCE ? (value ?? 0) / MM : 0;
            double ang = t == (int)swMateType_e.swMateANGLE ? (value ?? 0) * Math.PI / 180 : 0;
            return AddMateBySelection(d, a, t, Marked(ents, 1), align, false, dist, ang);
        }

        // Insert a part/sub-assembly and mate it in one call ("place it here"): each mate pairs an entity of the NEW
        // component ("own": face_index / edge_index / plane) with an existing one ("target": entity spec).
        public ExecutionResponse InsertAndMate(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var d);
            string file = PStr(p, "file_path") ?? throw new ToolFail("MISSING_PARAMETER", "file_path is required.");
            if (!File.Exists(file)) throw new ToolFail("FILE_NOT_FOUND", $"No file at '{file}'.");
            file = Path.GetFullPath(file);
            var comp = WithModelLoaded(file, d.GetTitle(), () =>
                a.AddComponent5(file, 0, "", false, PStr(p, "config", ""), 0, 0, 0) as IComponent2
                ?? throw new ToolFail("INSERT_FAILED", "AddComponent5 returned null."));
            var made = new JArray();
            try
            {
                foreach (var mt in PArr(p, "mates").OfType<JObject>())
                {
                    var own = (JObject)(mt["own"] ?? throw new ToolFail("BAD_PARAMETER", "Each mate needs 'own' and 'target'.")).DeepClone();
                    own["component"] = comp.Name2;
                    var ents = new[] { MateEntityOf(d, a, own), MateEntityOf(d, a, mt["target"]) };
                    var f = CreateStdMate(d, a, (string)mt["type"] ?? "coincident", ents, MateAlignFromName((string)mt["alignment"]),
                                          mt["value_mm"] != null ? (double?)mt["value_mm"] : mt["value_deg"] != null ? (double?)mt["value_deg"] : null);
                    made.Add(f.Name);
                }
            }
            catch (Exception ex)
            {
                // all-or-nothing: take the new component (and the mates made so far) back out — on ANY failure (a
                // malformed mate record or a COM error too, not only a ToolFail)
                d.ClearSelection2(true);
                comp.Select4(false, null, false);
                d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
                d.EditRebuild3();
                if (ex is ToolFail tf) throw new ToolFail(tf.Code, tf.Message + " (the inserted component was removed again)");
                throw new ToolFail("MATE_FAILED", $"Mate failed: {ex.Message} (the inserted component was removed again)");
            }
            d.EditRebuild3();
            var o = OriginMm(comp);
            return new JObject { ["component"] = comp.Name2, ["origin_mm"] = o == null ? null : R(o), ["mates"] = made,
                                 ["status"] = ConstraintName(comp) };
        });

        private static string ConstraintName(IComponent2 c)
        {
            if (c.IsFixed()) return "fixed";
            int cs = c.GetConstrainedStatus();
            return cs == (int)swConstrainedStatus_e.swFullyConstrained ? "fully_defined"
                 : cs == (int)swConstrainedStatus_e.swOverConstrained ? "over_defined" : "under_defined";
        }

        // Linear / circular pattern of components (assembly local pattern) — selection + FeatureLinearPattern5 /
        // FeatureCircularPattern5 (the CreateDefinition(swFmLocal*Pattern) path faulted inside SW 2024). The
        // selection-mark scheme for assemblies is tried both ways; the first that yields a feature wins.
        public ExecutionResponse PatternComponents(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var d);
            var all = Comps(a, false);
            var seeds = PStrList(p, "components").Select(n => (object)FindComp(a, n, all)).ToList();
            if (seeds.Count == 0) throw new ToolFail("MISSING_PARAMETER", "components (seed) is required.");
            string kind = PStr(p, "pattern", "linear");
            int count = PInt(p, "count", 2);
            var fm = d.FeatureManager;
            object dir1 = kind == "linear" ? MateEntityOf(d, a, p["direction"] ?? throw new ToolFail("MISSING_PARAMETER", "direction (edge / axis / face entity)"))
                                           : MateEntityOf(d, a, p["axis"] ?? throw new ToolFail("MISSING_PARAMETER", "axis (axis / cylindrical face / edge entity)"));
            object dir2 = kind == "linear" && p["direction2"] != null ? MateEntityOf(d, a, p["direction2"]) : null;
            var schemes = new[] { new[] { 1, 2, 4 }, new[] { 2, 4, 1 } };   // {dir1, dir2, seeds}
            int before = d.FeatureManager.GetFeatureCount(true);
            IFeature f = null;
            foreach (var m in schemes)
            {
                var sel = new List<Tuple<object, int>> { Tuple.Create(dir1, m[0]) };
                if (dir2 != null) sel.Add(Tuple.Create(dir2, m[1]));
                sel.AddRange(Marked(seeds, m[2]));
                d.ClearSelection2(true);
                bool ok = true;
                foreach (var s in sel) ok &= SelectEntity(d, s.Item1, true, s.Item2);
                if (!ok) continue;
                try
                {
                    if (kind == "linear")
                        f = fm.FeatureLinearPattern5(count, PNum(p, "spacing_mm", 50) / MM, dir2 != null ? PInt(p, "count2", 2) : 1,
                            PNum(p, "spacing2_mm", 50) / MM, PBool(p, "reverse", false), PBool(p, "reverse2", false), "", "",
                            false, false, false, false, true, true, false, false, false, false, 0, 0, false, false) as IFeature;
                    else if (kind == "circular")
                        f = fm.FeatureCircularPattern5(count, PNum(p, "angle_deg", 360) * Math.PI / 180, PBool(p, "reverse", false), "",
                            false, PBool(p, "equal_spacing", true), false, false, false, false, 1, 0, "", false) as IFeature;
                    else throw new ToolFail("BAD_PARAMETER", "pattern must be linear or circular.");
                }
                catch (System.Runtime.InteropServices.COMException ex) { ExecLog.Write($"pattern_components: scheme {string.Join(",", m)} -> {ex.Message}"); f = null; }
                d.ClearSelection2(true);
                if (f != null) break;
            }
            if (f == null) throw new ToolFail("PATTERN_FAILED", "SolidWorks did not create the pattern (direction must be a linear edge / axis; circular axis an axis or cylindrical face).");
            d.EditRebuild3();
            return new JObject { ["feature"] = f.Name, ["type"] = f.GetTypeName2(), ["components_total"] = Comps(a, true).Count };
        });
        public ExecutionResponse EditMate(ToolRequest request) => Run(request, true, p =>
        {
            ActiveAsmOrFail(out var d);
            var f = FindMate(d, PStr(p, "mate") ?? throw new ToolFail("MISSING_PARAMETER", "mate (name) is required."));
            var done = new JArray();
            if (PBool(p, "delete", false))
            {
                string n = f.Name;
                d.ClearSelection2(true);
                f.Select2(false, 0);
                if (!d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed)) throw new ToolFail("DELETE_FAILED", $"SolidWorks did not delete the mate '{n}'.");
                d.EditRebuild3();
                return new JObject { ["deleted"] = n };
            }
            // Order matters: a suppressed mate can be neither flipped nor re-valued.
            bool? sup = PBoolN(p, "suppressed");
            if (sup == false)
            {
                SuppressOrFail(f, false);
                d.EditRebuild3(); done.Add("unsuppressed");
            }
            var m = f.GetSpecificFeature2() as IMate2;
            if (p["entities"] != null)
            {
                // Re-point the mate at other geometry (replace mate references).
                var ents = PArr(p, "entities").Select(t => MateEntityOf(d, d as IAssemblyDoc, t)).ToArray();
                object def = f.GetDefinition() ?? throw new ToolFail("EDIT_FAILED", $"'{f.Name}' has no editable definition.");
                if (!SetMateEntities(def, ents)) throw new ToolFail("EDIT_FAILED", $"Replacing entities is not supported for '{MateName(m.Type)}' mates.");
                if (!f.ModifyDefinition(def, d, null)) throw new ToolFail("EDIT_FAILED", "ModifyDefinition returned false (entities incompatible with the mate type?).");
                done.Add("entities");
                m = f.GetSpecificFeature2() as IMate2;
            }
            double? v = p["value_mm"] != null ? PNum(p, "value_mm", 0) / MM : p["value_deg"] != null ? PNum(p, "value_deg", 0) * Math.PI / 180 : (double?)null;
            if (v.HasValue)
            {
                var dim = (f.GetFirstDisplayDimension() as IDisplayDimension)?.GetDimension2(0) as IDimension
                          ?? throw new ToolFail("NO_VALUE", $"'{f.Name}' ({MateName(m.Type)}) has no value to set.");
                int r = dim.SetSystemValue3(v.Value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                done.Add("value");
                if (r != (int)swSetValueReturnStatus_e.swSetValue_Successful) throw new ToolFail("SET_VALUE_FAILED", $"SetSystemValue3 status {r}.");
            }
            if (PBool(p, "flip", false))
            {
                // IMate2.Flipped when allowed; otherwise toggle MateAlignment on the type-specific mate feature
                // data (ICoincidentMateFeatureData etc. — late-bound, they share the property name).
                int before = m.Alignment;
                if (m.CanBeFlipped) m.Flipped = !m.Flipped;
                else
                {
                    object def = f.GetDefinition() ?? throw new ToolFail("FLIP_FAILED", $"'{f.Name}' has no editable definition.");
                    int na = before == (int)swMateAlign_e.swMateAlignALIGNED ? (int)swMateAlign_e.swMateAlignANTI_ALIGNED : (int)swMateAlign_e.swMateAlignALIGNED;
                    // MateAlignment lives on the type-specific interfaces only (not reachable via IDispatch name lookup).
                    if (def is ICoincidentMateFeatureData c1) c1.MateAlignment = na;
                    else if (def is IParallelMateFeatureData c2) c2.MateAlignment = na;
                    else if (def is IDistanceMateFeatureData c4) c4.MateAlignment = na;
                    else if (def is IAngleMateFeatureData c5) c5.MateAlignment = na;
                    else if (def is IConcentricMateFeatureData c6) c6.MateAlignment = na;
                    else if (def is ITangentMateFeatureData c7) c7.MateAlignment = na;
                    else throw new ToolFail("FLIP_FAILED", $"Flip is not supported for '{MateName(m.Type)}' mates.");
                    if (!f.ModifyDefinition(def, d, null)) throw new ToolFail("FLIP_FAILED", "ModifyDefinition returned false.");
                }
                done.Add("flip");
            }
            if (sup == true)
            {
                SuppressOrFail(f, true);
                done.Add("suppressed");
            }
            string rename = PStr(p, "new_name");
            if (rename != null) { RenameOrFail(f, rename); done.Add("renamed"); }
            d.EditRebuild3();
            var info = MateInfo(f); info["changed"] = done;
            return info;
        });

        public ExecutionResponse ConstraintStatus(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var d);
            var res = new JArray(); var counts = new Dictionary<string, int>();
            // Mate count per component from the mate tree (IComponent2.GetMates() faulted inside SW on a real
            // assembly with imported STEP parts — access violation in combase, instance lost).
            var mateCount = new Dictionary<string, int>();
            foreach (var mf in MateFeatures(d))
            {
                var m = mf.GetSpecificFeature2() as IMate2;
                for (int i = 0; m != null && i < m.GetMateEntityCount(); i++)
                {
                    string cn = null;
                    try { cn = ((m.MateEntity(i) as IMateEntity2)?.ReferenceComponent as IComponent2)?.Name2; } catch { }
                    if (cn == null) continue;
                    string top = cn.Split('/')[0];
                    mateCount[cn] = (mateCount.TryGetValue(cn, out var k1) ? k1 : 0) + 1;
                    if (top != cn) mateCount[top] = (mateCount.TryGetValue(top, out var k2) ? k2 : 0) + 1;
                }
            }
            foreach (var c in Comps(a, PBool(p, "top_only", true)))
            {
                if (IsSuppressedComp(c)) continue;
                string st;
                if (c.IsFixed()) st = "fixed";
                else
                {
                    int cs = c.GetConstrainedStatus();
                    st = cs == (int)swConstrainedStatus_e.swFullyConstrained ? "fully_defined"
                       : cs == (int)swConstrainedStatus_e.swOverConstrained ? "over_defined"
                       : cs == (int)swConstrainedStatus_e.swUnderConstrained ? "under_defined" : "unknown";
                }
                int mates = mateCount.TryGetValue(c.Name2, out var mc) ? mc : 0;
                counts[st] = (counts.TryGetValue(st, out var n) ? n : 0) + 1;
                res.Add(new JObject { ["component"] = c.Name2, ["status"] = st, ["mates"] = mates });
            }
            bool onlyProblems = PBool(p, "problems_only", false);
            var list = res.Where(x => !onlyProblems || ((string)x["status"] != "fixed" && (string)x["status"] != "fully_defined")).ToList();
            return new JObject { ["summary"] = JObject.FromObject(counts), ["mate_errors"] = MateFeatures(d).Count(f => { bool w; return f.GetErrorCode2(out w) != 0; }),
                                 ["components"] = new JArray(list.Take(PInt(p, "max_items", 300))) };
        });
    }
}
