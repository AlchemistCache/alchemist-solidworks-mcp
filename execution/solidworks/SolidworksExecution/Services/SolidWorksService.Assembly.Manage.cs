using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Assemblies (ported from the earlier Python server) =====================
    // Large-assembly work: light component lists, interference/clearance, measure, mass, visibility, appearance,
    // move/replace, references, pack-and-go, properties, configurations, BOM, model snapshot.
    // Units at this boundary: MILLIMETERS, DEGREES, KILOGRAMS (every length param/field is suffixed _mm).
    public partial class SolidWorksService
    {

        // Resolve lightweight components only when there are any (ResolveAllLightWeightComponents walks and
        // touches the whole assembly even when everything is already resolved).
        private static void ResolveLightweight(IAssemblyDoc a)
        {
            bool any = Comps(a, false).Any(c => { try { return c.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentLightweight; } catch { return false; } });
            if (any) a.ResolveAllLightWeightComponents(false);
        }

        // Suppressed means SUPPRESSED (GetSuppression2 = 0): IsSuppressed() is also true for a LIGHTWEIGHT component, which
        // is loaded and shown — the subassemblies of a big machine, freshly opened, read as "suppressed" (a false alarm).
        private static bool IsSuppressedComp(IComponent2 c)
        {
            try { return c.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed; } catch { return false; }
        }

        // suppressed / lightweight / resolved, by GetSuppression2.
        private static string CompState(IComponent2 c)
        {
            int s;
            try { s = c.GetSuppression2(); } catch { return "unknown"; }
            switch (s)
            {
                case (int)swComponentSuppressionState_e.swComponentSuppressed: return "suppressed";
                case (int)swComponentSuppressionState_e.swComponentLightweight:
                case (int)swComponentSuppressionState_e.swComponentFullyLightweight: return "lightweight";
                default: return "resolved";
            }
        }

        private static double[] CompBoxMm(IComponent2 c)
        {
            var b = c.GetBox(false, false) as double[];
            return b?.Select(v => v * MM).ToArray();
        }

        private static JArray R(IEnumerable<double> v, int dp = 2) => new JArray(v.Select(x => Math.Round(x, dp)));

        private static double[] OriginMm(IComponent2 c)
        {
            var t = c.Transform2 as IMathTransform;
            var a = t?.ArrayData as double[];
            return a == null ? null : new[] { a[9] * MM, a[10] * MM, a[11] * MM };
        }

        // ================================================================ read-only tools

        // Tree names (components, features, mates) of the active doc — the adapter diffs two snapshots
        // around every mutating call (п.9 'created=[...]'). Internal: not in tool-schemas.json.
        public ExecutionResponse ModelSnapshot(ToolRequest request) => Run(request, false, p =>
        {
            var d = _solidWorks.IActiveDoc2 as IModelDoc2;
            return new JObject { ["doc"] = d?.GetTitle(), ["items"] = new JArray(ModelItems()) };
        });

        public ExecutionResponse ListComponents(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            bool topOnly = PBool(p, "top_only", false);
            string filter = PStr(p, "name_filter", "");
            string under = PStr(p, "under");
            int offset = PInt(p, "offset", 0), limit = PInt(p, "limit", 50);
            var fields = PStrList(p, "fields");
            var known = new[] { "file", "config", "state", "hidden", "fixed", "origin_mm" };
            var unknown = fields.Where(f => f != "name" && !known.Contains(f)).ToList();
            if (unknown.Count > 0) throw new ToolFail("BAD_PARAMETER", $"fields: unknown {string.Join(", ", unknown)}; known: name, {string.Join(", ", known)}.");
            bool F(string f) => fields.Count == 0 || fields.Contains(f);
            var cs = Comps(a, topOnly);
            if (under != null)
            {
                var root = FindComp(a, under, cs);
                cs = cs.Where(c => c.Name2.StartsWith(root.Name2 + "/", StringComparison.Ordinal)).ToList();
            }
            if (filter.Length > 0) cs = cs.Where(c => c.Name2.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var outArr = new JArray();
            foreach (var c in cs.Skip(offset).Take(limit))
            {
                var j = new JObject { ["name"] = c.Name2 };
                if (F("file")) j["file"] = c.GetPathName();
                if (F("config")) j["config"] = c.ReferencedConfiguration;
                if (F("state")) j["state"] = CompState(c);
                if (F("hidden")) j["hidden"] = c.Visible == (int)swComponentVisibilityState_e.swComponentHidden;
                if (F("fixed")) j["fixed"] = c.IsFixed();
                if (F("origin_mm")) { var o = OriginMm(c); j["origin_mm"] = o == null ? null : R(o); }
                outArr.Add(j);
            }
            var res = new JObject { ["total"] = cs.Count, ["offset"] = offset, ["returned"] = outArr.Count, ["components"] = outArr };
            if (offset + outArr.Count < cs.Count) res["next_offset"] = offset + outArr.Count;
            return res;
        });

        // delete_components — many components in ONE selection and ONE delete (one rebuild): one by one through
        // edit_feature it was ~6 s each in a 190-component assembly. names = exact names (list_components); prefix = every
        // component whose full name starts with it ('Винт юстировки' at the top level, 'Sub-1/Винт' inside Sub-1). Their
        // mates go with them.
        public ExecutionResponse DeleteComponents(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var d);
            var names = PStrList(p, "names");
            string prefix = PStr(p, "prefix");
            if (names.Count == 0 && prefix == null) throw new ToolFail("MISSING_PARAMETER", "names (exact component names) or prefix is required.");
            var all = Comps(a, false);
            var targets = names.Select(n => FindComp(a, n, all)).ToList();
            if (prefix != null)
            {
                var byPrefix = all.Where(c => c.Name2.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byPrefix.Count == 0) throw new ToolFail("COMPONENT_NOT_FOUND", $"No component name starts with '{prefix}'.");
                targets.AddRange(byPrefix);
            }
            // a component inside another one being deleted goes with its parent
            var set = targets.Select(c => c.Name2).Distinct().ToList();
            set = set.Where(n => !set.Any(o => n.StartsWith(o + "/", StringComparison.Ordinal))).ToList();
            d.ClearSelection2(true);
            int selected = 0;
            foreach (var n in set)
                if (all.First(c => c.Name2 == n).Select4(true, null, false)) selected++;
            if (selected == 0) throw new ToolFail("DELETE_FAILED", "None of the components could be selected.");
            d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
            d.ClearSelection2(true);
            var now = new HashSet<string>(Comps(a, false).Select(c => c.Name2));
            var kept = set.Where(now.Contains).ToList();
            if (kept.Count == set.Count) throw new ToolFail("DELETE_FAILED", $"SolidWorks deleted none of {set.Count} component(s).");
            var res = new JObject { ["deleted_count"] = set.Count - kept.Count, ["deleted"] = new JArray(set.Where(n => !now.Contains(n)).Take(50)) };
            if (kept.Count > 0) res["not_deleted"] = new JArray(kept);
            return res;
        });

        public ExecutionResponse ListOpenDocuments(ToolRequest request) => Run(request, false, p =>
        {
            bool full = PBool(p, "full", false);
            var docs = _solidWorks.GetDocuments() as object[] ?? new object[0];
            var res = new JArray(); int hidden = 0;
            foreach (var o in docs)
            {
                var d = o as IModelDoc2; if (d == null) continue;
                bool vis = d.Visible, mod = d.GetSaveFlag();
                if (!(full || vis || mod)) { hidden++; continue; }
                res.Add(new JObject { ["title"] = d.GetTitle(), ["path"] = d.GetPathName(), ["type"] = DocTypeName(d), ["visible"] = vis, ["modified"] = mod });
            }
            return new JObject { ["count"] = docs.Length, ["hidden_unmodified"] = hidden, ["documents"] = res };
        });

        public ExecutionResponse InterferenceCheck(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            var names = PStrList(p, "components");
            double minVol = PNum(p, "min_volume_mm3", 0);
            int maxItems = PInt(p, "max_items", 25);
            var all = Comps(a, false);
            doc.ClearSelection2(true);
            foreach (var n in names) FindComp(a, n, all).Select4(true, null, false);

            var idm = a.InterferenceDetectionManager;
            var opts = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("TreatCoincidenceAsInterference", () => idm.TreatCoincidenceAsInterference = PBool(p, "coincidence_as_interference", false)),
                new KeyValuePair<string, Action>("TreatSubAssembliesAsComponents", () => idm.TreatSubAssembliesAsComponents = PBool(p, "subassemblies_as_components", false)),
                new KeyValuePair<string, Action>("IncludeMultibodyPartInterferences", () => idm.IncludeMultibodyPartInterferences = PBool(p, "include_multibody", false)),
                new KeyValuePair<string, Action>("IgnoreHiddenBodies", () => idm.IgnoreHiddenBodies = !PBool(p, "include_hidden", false)),
                new KeyValuePair<string, Action>("CreateFastenersFolder", () => idm.CreateFastenersFolder = false),
                new KeyValuePair<string, Action>("ShowIgnoredInterferences", () => idm.ShowIgnoredInterferences = false),
            };
            foreach (var o in opts)
                try { o.Value(); } catch (Exception ex) { ExecLog.Write($"interference_check: {o.Key} -> {ex.Message}"); }
            var sw = Stopwatch.StartNew();
            // Read EVERYTHING before idm.Done(): Done() frees the results, and touching an IInterference
            // afterwards faults inside SolidWorks (RPC_E_SERVERFAULT — observed live).
            var pairs = new Dictionary<string, JObject>();
            int zones = 0;
            try
            {
                var ints = idm.GetInterferences() as object[] ?? new object[0];
                zones = ints.Length;
                foreach (var o in ints)
                {
                    var it = o as IInterference; if (it == null) continue;
                    var cs = (it.Components as object[] ?? new object[0]).OfType<IComponent2>().Select(c => c.Name2).OrderBy(x => x).ToList();
                    double vol = it.Volume * 1e9;
                    string key = string.Join("|", cs);
                    if (!pairs.TryGetValue(key, out var pr))
                    {
                        pr = new JObject { ["components"] = new JArray(cs), ["volume_mm3"] = 0.0, ["zones"] = 0 };
                        try { var bx = (it.GetInterferenceBody() as IBody2)?.GetBodyBox() as double[]; if (bx != null) pr["box_mm"] = R(bx.Select(v => v * MM), 1); } catch { }
                        pairs[key] = pr;
                    }
                    pr["volume_mm3"] = Math.Round(pr.Value<double>("volume_mm3") + vol, 2);
                    pr["zones"] = pr.Value<int>("zones") + 1;
                }
            }
            finally { try { idm.Done(); } catch { } doc.ClearSelection2(true); }
            var list = pairs.Values.Where(x => x.Value<double>("volume_mm3") >= minVol)
                .OrderByDescending(x => x.Value<double>("volume_mm3")).ToList();
            foreach (var x in list)
            {
                double v = x.Value<double>("volume_mm3");
                x["severity"] = v < 1 ? "negligible" : v < 100 ? "minor" : "major";
            }
            return new JObject
            {
                ["path"] = doc.GetPathName(),
                ["zones"] = zones,
                ["pairs"] = list.Count,
                ["major"] = list.Count(x => (string)x["severity"] == "major"),
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                ["truncated"] = list.Count > maxItems,
                ["interferences"] = new JArray(list.Take(maxItems))
            };
        });

        // Minimum distance between two selected things (IMeasure) — null when not measurable.
        private double? MeasureDistanceMm(IModelDoc2 doc, IComponent2 c1, IComponent2 c2, out bool intersect)
        {
            intersect = false;
            doc.ClearSelection2(true);
            c1.Select4(false, null, false); c2.Select4(true, null, false);
            var m = doc.Extension.CreateMeasure() as IMeasure;
            if (m == null || !m.Calculate(null)) { doc.ClearSelection2(true); return null; }
            try { intersect = m.IsIntersect; } catch { }
            double dist = m.Distance;
            doc.ClearSelection2(true);
            return dist < 0 ? (double?)null : dist * MM;
        }

        public ExecutionResponse ClearanceCheck(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            double minGap = PNum(p, "min_gap_mm", 5.0);
            int maxPairs = PInt(p, "max_pairs", 300);
            bool includeTouching = PBool(p, "include_touching", false);
            var all = Comps(a, false);
            var names = PStrList(p, "components");
            // Leaf parts only: a sub-assembly's box covers its children and would pair with them.
            string sub = PStr(p, "subassembly");
            var cs = names.Count > 0 ? names.Select(n => FindComp(a, n, all)).ToList()
                : all.Where(c => c.Visible != (int)swComponentVisibilityState_e.swComponentHidden && !IsSuppressedComp(c)
                              && (sub == null || c.Name2.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase))
                              && ((c.GetChildren() as object[])?.Length ?? 0) == 0).ToList();
            var boxes = new List<Tuple<IComponent2, double[]>>();
            foreach (var c in cs) { try { var b = CompBoxMm(c); if (b != null) boxes.Add(Tuple.Create(c, b)); } catch { } }
            var cand = new List<Tuple<double, int, int>>();
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var b1 = boxes[i].Item2; var b2 = boxes[j].Item2;
                    double gap = Enumerable.Range(0, 3).Max(k => Math.Max(b2[k] - b1[k + 3], b1[k] - b2[k + 3]));
                    if (gap < minGap) cand.Add(Tuple.Create(gap, i, j));
                }
            cand.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            var res = new List<JObject>(); int measured = 0;
            var sw = Stopwatch.StartNew();
            foreach (var c in cand.Take(maxPairs))
            {
                var c1 = boxes[c.Item2].Item1; var c2 = boxes[c.Item3].Item1;
                var dist = MeasureDistanceMm(doc, c1, c2, out bool inter); measured++;
                if (inter || dist == null || dist.Value <= 1e-6)
                {
                    if (includeTouching) res.Add(new JObject { ["pair"] = new JArray(c1.Name2, c2.Name2), ["gap_mm"] = 0.0, ["contact"] = true });
                    continue;
                }
                if (dist.Value < minGap) res.Add(new JObject { ["pair"] = new JArray(c1.Name2, c2.Name2), ["gap_mm"] = Math.Round(dist.Value, 2) });
            }
            return new JObject
            {
                ["min_gap_mm"] = minGap,
                ["components"] = boxes.Count,
                ["bbox_candidates"] = cand.Count,
                ["measured"] = measured,
                ["truncated"] = cand.Count > maxPairs,
                ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
                ["path"] = doc.GetPathName(),
                ["pairs"] = new JArray(res.OrderBy(r => r.Value<double>("gap_mm")).Take(PInt(p, "max_items", 60)))
            };
        });

        public ExecutionResponse MeasureComponents(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            var all = Comps(a, false);
            var c1 = FindComp(a, PStr(p, "component_a") ?? throw new ToolFail("MISSING_PARAMETER", "component_a is required."), all);
            var c2 = FindComp(a, PStr(p, "component_b") ?? throw new ToolFail("MISSING_PARAMETER", "component_b is required."), all);
            var res = new JObject { ["a"] = c1.Name2, ["b"] = c2.Name2 };
            doc.ClearSelection2(true);
            try
            {
                c1.Select4(false, null, false); c2.Select4(true, null, false);
                var m = doc.Extension.CreateMeasure() as IMeasure;
                bool ok = m != null && m.Calculate(null);
                res["ok"] = ok;
                if (ok)
                {
                    if (m.Distance > -1) res["distance_mm"] = Math.Round(m.Distance * MM, 3);
                    if (m.CenterDistance > -1) res["center_distance_mm"] = Math.Round(m.CenterDistance * MM, 3);
                    if (m.NormalDistance > -1) res["normal_distance_mm"] = Math.Round(m.NormalDistance * MM, 3);
                    if (m.Angle > -1) res["angle_deg"] = Math.Round(m.Angle * 180 / Math.PI, 3);
                    try { if (m.IsIntersect) { res["distance_mm"] = 0.0; res["contact"] = "touching/intersecting"; } } catch { }
                }
            }
            finally { doc.ClearSelection2(true); }   // never leave the two components selected
            // a subassembly is not measurable as a whole (ok:false): the closest of its parts, prefiltered by boxes
            if (res["distance_mm"] == null && (Leaves(c1).Count > 1 || Leaves(c2).Count > 1))
            {
                var pairs = Leaves(c1).SelectMany(x => Leaves(c2).Select(y => new { x, y, gap = BoxGap(CompBoxMm(x), CompBoxMm(y)) }))
                                      .OrderBy(q => q.gap).Take(25).ToList();
                double best = double.MaxValue; string bx = null, by = null; bool touch = false;
                foreach (var q in pairs)
                {
                    if (q.gap > best) break;
                    var dmm = MeasureDistanceMm(doc, q.x, q.y, out bool inter);
                    if (dmm == null) continue;
                    if (inter) { best = 0; bx = q.x.Name2; by = q.y.Name2; touch = true; break; }
                    if (dmm.Value < best) { best = dmm.Value; bx = q.x.Name2; by = q.y.Name2; }
                }
                if (bx != null)
                {
                    res["ok"] = true;
                    res["distance_mm"] = Math.Round(best, 3);
                    res["closest"] = new JArray(bx, by);
                    if (touch) res["contact"] = "touching/intersecting";
                }
            }
            var b1 = CompBoxMm(c1); var b2 = CompBoxMm(c2);
            if (b1 != null && b2 != null)
            {
                res["bbox_gap_mm"] = R(Enumerable.Range(0, 3).Select(i => Math.Max(b2[i] - b1[i + 3], b1[i] - b2[i + 3])));
                res["bbox_a_mm"] = R(b1, 1); res["bbox_b_mm"] = R(b2, 1);
            }
            return res;
        });

        // The parts under a component (itself when it has none), visible and not suppressed.
        private static List<IComponent2> Leaves(IComponent2 c)
        {
            var kids = (c.GetChildren() as object[] ?? new object[0]).OfType<IComponent2>().ToList();
            if (kids.Count == 0) return new List<IComponent2> { c };
            return kids.Where(k => !IsSuppressedComp(k)).SelectMany(Leaves).ToList();
        }

        private static double BoxGap(double[] a, double[] b)
        {
            if (a == null || b == null) return double.MaxValue;
            double s = 0;
            for (int i = 0; i < 3; i++) { double g = Math.Max(0, Math.Max(b[i] - a[i + 3], a[i] - b[i + 3])); s += g * g; }
            return Math.Sqrt(s);
        }

        // Mass/CoG in the ASSEMBLY frame. components=[] → the whole active doc (part or assembly).
        // Per component: its solid bodies in assembly context are fed to one IMassProperty (AddBodies), so
        // the CoG comes out in assembly coordinates with each part's own material density.
        public ExecutionResponse MassProperties(ToolRequest request) => Run(request, false, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            var names = PStrList(p, "components");
            Func<IMassProperty, JObject> pack = mp => new JObject
            {
                ["mass_kg"] = Math.Round(mp.Mass, 6),
                ["cog_mm"] = R(((double[])mp.CenterOfMass).Select(v => v * MM), 3),
                ["volume_mm3"] = Math.Round(mp.Volume * 1e9, 3),
                ["area_mm2"] = Math.Round(mp.SurfaceArea * 1e6, 3)
            };
            if (names.Count == 0)
            {
                var mp = doc.Extension.CreateMassProperty() as IMassProperty;
                mp.UseSystemUnits = true;
                return pack(mp);
            }
            var a = AsmOrFail(doc, "components");
            ResolveLightweight(a);
            var all = Comps(a, false);
            // Per component: mass of its own document (active config = the referenced one for the usual
            // single-config case), CoG mapped into the assembly frame through the component transform.
            var mu = _solidWorks.GetMathUtility() as IMathUtility;
            var items = new JArray(); double tm = 0, sx = 0, sy = 0, sz = 0;
            foreach (var n in names)
            {
                var c = FindComp(a, n, all);
                var md = c.GetModelDoc2() as IModelDoc2;
                if (md == null) { items.Add(new JObject { ["name"] = c.Name2, ["error"] = "not loaded (suppressed/lightweight)" }); continue; }
                var mp = md.Extension.CreateMassProperty() as IMassProperty;
                mp.UseSystemUnits = true;
                var o = pack(mp); o["name"] = c.Name2; o["config"] = c.ReferencedConfiguration;
                var pt = mu.CreatePoint((double[])mp.CenterOfMass) as IMathPoint;
                var cg = (double[])(pt.MultiplyTransform(c.Transform2) as IMathPoint).ArrayData;
                o["cog_mm"] = R(cg.Select(v => v * MM), 1);
                items.Add(o);
                tm += mp.Mass; sx += mp.Mass * cg[0]; sy += mp.Mass * cg[1]; sz += mp.Mass * cg[2];
            }
            return new JObject
            {
                ["total"] = new JObject { ["mass_kg"] = Math.Round(tm, 6), ["cog_mm"] = tm > 0 ? R(new[] { sx / tm * MM, sy / tm * MM, sz / tm * MM }, 3) : null },
                ["items"] = items
            };
        });

        public ExecutionResponse ListReferences(ToolRequest request) => Run(request, false, p =>
        {
            string path = PStr(p, "file_path") ?? ActiveDocOrFail().GetPathName();
            if (string.IsNullOrEmpty(path)) throw new ToolFail("NOT_SAVED", "Active document has no file; pass file_path.");
            int longWarn = PInt(p, "long_path_warn", 200);
            var deps = _solidWorks.GetDocumentDependencies2(path, true, false, false) as string[] ?? new string[0];
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < deps.Length; i += 2) files.Add(deps[i]);
            var refs = new JArray(files.Select(f => new JObject { ["path"] = f, ["exists"] = File.Exists(f), ["long_path"] = f.Length > longWarn }));
            var missing = files.Where(f => !File.Exists(f)).ToList();
            var res = new JObject
            {
                ["file"] = path,
                ["count"] = files.Count,
                ["missing"] = new JArray(missing),
                ["long_paths"] = new JArray(files.Where(f => f.Length > longWarn)),
                ["references"] = refs
            };
            // a moved file is usually still under the assembly's folder: same-named candidates for replace_component
            if (missing.Count > 0)
            {
                var found = new JObject();
                try
                {
                    var byName = Directory.EnumerateFiles(Path.GetDirectoryName(path), "*.sld*", SearchOption.AllDirectories)
                                          .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                    foreach (var m in missing)
                        if (byName.TryGetValue(Path.GetFileName(m), out var hits)) found[m] = new JArray(hits.Take(5));
                }
                catch (Exception ex) { ExecLog.Write($"list_references candidates: {ex.Message}"); }
                if (found.Count > 0) res["candidates"] = found;
            }
            return res;
        });

        private IModelDoc2 DocOrComponentDoc(JObject p, out string config)
        {
            var doc = ActiveDocOrFail();
            config = PStr(p, "config", "");
            var comp = PStr(p, "component");
            if (comp == null) return doc;
            var a = AsmOrFail(doc, "component");
            var c = FindComp(a, comp);
            if (c.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentLightweight) c.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
            var md = c.GetModelDoc2() as IModelDoc2 ?? throw new ToolFail("COMPONENT_NOT_LOADED", $"'{c.Name2}' is suppressed or not loaded.");
            if (PStr(p, "config") == null) config = c.ReferencedConfiguration;
            return md;
        }

        public ExecutionResponse GetCustomProperties(ToolRequest request) => Run(request, false, p =>
        {
            var d = DocOrComponentDoc(p, out var cfg);
            var res = new JObject { ["document"] = d.GetTitle(), ["config"] = cfg };
            try
            {
                foreach (var scope in new[] { "", cfg }.Distinct())
                {
                    var cpm = d.Extension.CustomPropertyManager[scope];
                    var props = new JObject();
                    foreach (var n in cpm.GetNames() as string[] ?? new string[0]) props[n] = ResolveProp(d, cfg, CpmGet(cpm, n), n, 0);
                    res[scope == "" ? "file_properties" : "config_properties"] = props;
                }
            }
            finally { CloseParents(); }
            return res;
        });

        public ExecutionResponse SetCustomProperties(ToolRequest request) => Run(request, true, p =>
        {
            var d = DocOrComponentDoc(p, out var cfg);
            var props = p["properties"] as JObject ?? (PStr(p, "properties") is string s ? JObject.Parse(s) : null)
                        ?? throw new ToolFail("MISSING_PARAMETER", "properties (object name→value) is required.");
            string scope = PBool(p, "config_specific", false) ? cfg : "";
            var cpm = d.Extension.CustomPropertyManager[scope];
            foreach (var kv in props)
            {
                if (kv.Value == null || kv.Value.Type == JTokenType.Null) cpm.Delete2(kv.Key);
                else cpm.Add3(kv.Key, (int)swCustomInfoType_e.swCustomInfoText, kv.Value.ToString(),
                              (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
            if (PBool(p, "save", false))
            {
                int e = 0, w = 0;
                if (string.IsNullOrEmpty(d.GetPathName()))
                    throw new ToolFail("NOT_SAVED", $"'{d.GetTitle()}' was never saved — the properties are set in memory; save_document(file_path) keeps them.");
                if (!d.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w))
                    throw new ToolFail("SAVE_FAILED", $"The properties are set, but saving '{d.GetTitle()}' failed (errors {e}, warnings {w}; read-only file?).");
            }
            var outP = new JObject();
            foreach (var n in cpm.GetNames() as string[] ?? new string[0]) outP[n] = CpmGet(cpm, n);
            return new JObject { ["document"] = d.GetTitle(), ["scope"] = scope == "" ? "file" : scope, ["properties"] = outP };
        });

        public ExecutionResponse Configurations(ToolRequest request)
        {
            // list is read-only, every other action changes the model — decided before Run; malformed params are
            // reported by Run itself (ParamsOf inside its try), not as an HTTP 500 here
            bool changes;
            try { changes = PStr(ParamsOf(request), "action", "list") != "list"; } catch { changes = false; }
            return Run(request, changes, p =>
            {
                string action = PStr(p, "action", "list");
                var d = DocOrComponentDoc(p, out _);
                string name = PStr(p, "name");
                if (action != "list" && name == null) throw new ToolFail("MISSING_PARAMETER", "name is required for " + action);
                if (action == "activate")
                {
                    if (!d.ShowConfiguration2(name)) throw new ToolFail("CONFIG_NOT_FOUND", $"Cannot activate '{name}'.");
                    d.ForceRebuild3(false);
                }
                else if (action == "add")
                {
                    if (d.AddConfiguration3(name, PStr(p, "description", ""), "", 0) == null) throw new ToolFail("CONFIG_ADD_FAILED", $"Cannot add '{name}'.");
                }
                else if (action == "delete")
                {
                    if (!d.DeleteConfiguration2(name)) throw new ToolFail("CONFIG_DELETE_FAILED", $"Cannot delete '{name}' (active configs cannot be deleted).");
                }
                else if (action != "list") throw new ToolFail("BAD_PARAMETER", "action must be list, activate, add or delete.");
                return new JObject
                {
                    ["document"] = d.GetTitle(),
                    ["configs"] = new JArray(d.GetConfigurationNames() as string[] ?? new string[0]),
                    ["active"] = (d.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name
                };
            });
        }

        // Flat BOM by file+configuration (suppressed / ExcludeFromBOM skipped), quantity = occurrences.
        public ExecutionResponse BomExport(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            var props = PStrList(p, "properties");
            if (props.Count == 0) props = new List<string> { "Обозначение", "Наименование", "Материал", "Масса", "Раздел" };
            bool topOnly = PBool(p, "top_level_only", false);
            ResolveLightweight(a);
            var rows = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Comps(a, topOnly))
            {
                if (IsSuppressedComp(c)) continue;
                try { if (c.ExcludeFromBOM) continue; } catch { }
                string path = c.GetPathName(), cfg = c.ReferencedConfiguration;
                string key = path + "|" + cfg;
                if (rows.TryGetValue(key, out var r)) { r["qty"] = r.Value<int>("qty") + 1; continue; }
                r = new JObject { ["file"] = Path.GetFileName(path), ["config"] = cfg, ["qty"] = 1 };
                var md = c.GetModelDoc2() as IModelDoc2;
                if (md != null)
                {
                    foreach (var pr in props)
                    {
                        var v = DocProp(md, cfg, pr);
                        if (!string.IsNullOrEmpty(v)) r[pr] = v;
                    }
                    if (md.GetType() == (int)swDocumentTypes_e.swDocPART && r["Материал"] == null)
                    {
                        string db; var mat = (md as IPartDoc)?.GetMaterialPropertyName2(cfg, out db);
                        if (!string.IsNullOrEmpty(mat)) r["Материал"] = mat;
                    }
                    r["type"] = DocTypeName(md);
                }
                r["path"] = path;
                rows[key] = r;
            }
            var list = rows.Values.OrderBy(x => (string)x["file"], StringComparer.OrdinalIgnoreCase).ToList();
            string csv = PStr(p, "csv_path");
            if (csv != null)
            {
                EnsureFolderFor(csv);
                var cols = new List<string> { "file", "config", "qty", "type" }; cols.AddRange(props); cols.Add("path");
                var lines = new List<string> { string.Join(";", cols) };
                lines.AddRange(list.Select(x => string.Join(";", cols.Select(k => (x[k]?.ToString() ?? "").Replace(";", ",")))));
                File.WriteAllLines(csv, lines, new System.Text.UTF8Encoding(true));
            }
            int maxRows = PInt(p, "max_rows", 200);
            string acfg = (doc.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
            var docProps = new JObject();
            foreach (var pr in new[] { "Обозначение", "Наименование" })
            {
                string v = DocProp(doc, acfg, pr);
                if (v.Length > 0) docProps[pr] = v;
            }
            return new JObject
            {
                ["document"] = doc.GetTitle(),
                ["document_props"] = docProps,
                ["positions"] = list.Count,
                ["total_qty"] = list.Sum(x => x.Value<int>("qty")),
                ["csv"] = csv,
                ["truncated"] = list.Count > maxRows,
                ["rows"] = new JArray(list.Take(maxRows))
            };
        });

        // ================================================================ mutating tools

        public ExecutionResponse SetComponentState(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            var all = Comps(a, false);
            bool? hidden = PBoolN(p, "hidden"), suppressed = PBoolN(p, "suppressed"), fixedN = PBoolN(p, "fixed");
            var changed = new JArray();
            foreach (var n in PStrList(p, "components"))
            {
                var c = FindComp(a, n, all);
                if (suppressed.HasValue)
                    c.SetSuppression2(suppressed.Value ? (int)swComponentSuppressionState_e.swComponentSuppressed
                                                       : (int)swComponentSuppressionState_e.swComponentFullyResolved);
                if (hidden.HasValue)
                    c.Visible = hidden.Value ? (int)swComponentVisibilityState_e.swComponentHidden : (int)swComponentVisibilityState_e.swComponentVisible;
                if (fixedN.HasValue)
                {
                    doc.ClearSelection2(true); c.Select4(false, null, false);
                    if (fixedN.Value) a.FixComponent(); else a.UnfixComponent();
                    doc.ClearSelection2(true);
                }
                changed.Add(c.Name2);
            }
            doc.EditRebuild3(); doc.GraphicsRedraw2();
            return new JObject { ["changed"] = changed };
        });

        public ExecutionResponse SetAppearance(ToolRequest request) => Run(request, true, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            var rgb = PArr(p, "rgb").Select(x => (double)x / 255.0).ToArray();
            if (rgb.Length != 3) throw new ToolFail("BAD_PARAMETER", "rgb must be [r, g, b] with each value 0..255.");
            double tr = PNum(p, "transparency", 0);
            var vals = new[] { rgb[0], rgb[1], rgb[2], 1.0, 1.0, 0.5, 0.4, tr, 0.0 };
            var names = PStrList(p, "components");
            var done = new JArray();
            if (names.Count > 0)
            {
                var a = AsmOrFail(doc, "components");
                var all = Comps(a, false);
                foreach (var n in names)
                {
                    var c = FindComp(a, n, all);
                    c.SetMaterialPropertyValues2(vals, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    done.Add(c.Name2);
                }
            }
            else { doc.MaterialPropertyValues = vals; done.Add(doc.GetTitle()); }
            doc.GraphicsRedraw2();
            return new JObject { ["colored"] = done };
        });

        public ExecutionResponse MoveComponent(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            // full placements — 13 numbers as insert_component and analyze_assembly give them (rotation rows = the
            // component's axes in the assembly, translation mm, scale): `transform` for this component, `placements`
            // for many at once with one rebuild (a mechanism's pose; one rebuild per component made a 60-part arm's
            // pose take half a minute)
            var list = new List<(string name, JArray xf)>();
            if (PArr(p, "transform").Count > 0)
                list.Add((PStr(p, "component") ?? throw new ToolFail("MISSING_PARAMETER", "component is required with transform."), PArr(p, "transform")));
            foreach (var it in PArr(p, "placements"))
                list.Add(((string)it["component"] ?? throw new ToolFail("BAD_PARAMETER", "placements[]: {component, transform}."),
                          it["transform"] as JArray ?? throw new ToolFail("BAD_PARAMETER", "placements[]: {component, transform}.")));
            if (list.Count > 0)
            {
                var mu0 = _solidWorks.GetMathUtility() as IMathUtility;
                var tm = Stopwatch.StartNew();
                var all = Comps(a, false);
                long tFind = tm.ElapsedMilliseconds;
                // every entry checked and every component found BEFORE the first one moves: a bad entry at item k used to
                // leave k components moved, not rebuilt, behind a plain failure
                var todo = new List<(IComponent2 comp, double[] m)>();
                foreach (var (name, xf) in list)
                {
                    if (xf.Count < 12) throw new ToolFail("BAD_PARAMETER", $"transform of {name}: 13 numbers — 9 rotation (rows: the component's axes), 3 translation mm, scale.");
                    var m = new double[16];
                    for (int i = 0; i < 9; i++) m[i] = (double)xf[i];
                    for (int i = 9; i < 12; i++) m[i] = (double)xf[i] / MM;
                    m[12] = xf.Count > 12 ? (double)xf[12] : 1;
                    todo.Add((FindComp(a, name, all), m));
                }
                var moved = new JArray();
                foreach (var (cc, m) in todo)
                {
                    cc.Transform2 = mu0.CreateTransform(m) as MathTransform;
                    moved.Add(cc.Name2);
                }
                long tSet = tm.ElapsedMilliseconds;
                doc.EditRebuild3(); doc.GraphicsRedraw2();
                ExecLog.Write($"move_component: {moved.Count} placed — list {tFind} ms, set {tSet - tFind} ms, rebuild {tm.ElapsedMilliseconds - tSet} ms");
                return new JObject { ["moved"] = moved.Count, ["components"] = new JArray(moved.Take(20)),
                                     ["note"] = "Mates may pull components back on rebuild." };
            }
            var c = FindComp(a, PStr(p, "component") ?? throw new ToolFail("MISSING_PARAMETER", "component is required."));
            var t = (double[])(c.Transform2 as IMathTransform).ArrayData;
            var Rm = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Rm[i, j] = t[i * 3 + j];
            foreach (var ax in new[] { "x", "y", "z" })
            {
                double ang = PNum(p, "r" + ax + "_deg", 0) * Math.PI / 180;
                if (ang == 0) continue;
                double cs = Math.Cos(ang), sn = Math.Sin(ang);
                double[,] Q = ax == "x" ? new double[,] { { 1, 0, 0 }, { 0, cs, sn }, { 0, -sn, cs } }
                            : ax == "y" ? new double[,] { { cs, 0, -sn }, { 0, 1, 0 }, { sn, 0, cs } }
                                        : new double[,] { { cs, sn, 0 }, { -sn, cs, 0 }, { 0, 0, 1 } };
                // row vectors (p' = p·R + t): Q·Rm turns about the component's OWN axis; Rm·Q would turn about the
                // assembly's axis, which differs for any component already rotated
                var N = new double[3, 3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) N[i, j] += Q[i, k] * Rm[k, j];
                Rm = N;
            }
            bool absolute = PBool(p, "absolute", false);
            double dx = PNum(p, "dx_mm", 0) / MM, dy = PNum(p, "dy_mm", 0) / MM, dz = PNum(p, "dz_mm", 0) / MM;
            var arr = new double[16];
            double tx = t[9], ty = t[10], tz = t[11];
            // axis + angle_deg (+ pivot_mm): a turn about ANY axis in assembly space through a point (default the
            // component's origin) — rx/ry/rz turn about the component's own axes through its origin only
            var axis = PArr(p, "axis").Select(v => v.ToObject<double>()).ToArray();
            double angAxis = PNum(p, "angle_deg", 0) * Math.PI / 180;
            if (axis.Length != 0 && axis.Length != 3) throw new ToolFail("BAD_PARAMETER", "axis is 3 numbers [x, y, z] (assembly space).");
            if (axis.Length == 3 && angAxis != 0)
            {
                double n = Math.Sqrt(axis.Sum(v => v * v));
                if (n < 1e-12) throw new ToolFail("BAD_PARAMETER", "axis must not be zero.");
                double ux = axis[0] / n, uy = axis[1] / n, uz = axis[2] / n, c0 = Math.Cos(angAxis), s0 = Math.Sin(angAxis), k = 1 - c0;
                // Rodrigues for column vectors, transposed: SolidWorks multiplies row vectors (p' = p·R + t)
                double[,] M = { { c0 + ux * ux * k, ux * uy * k - uz * s0, ux * uz * k + uy * s0 },
                                { uy * ux * k + uz * s0, c0 + uy * uy * k, uy * uz * k - ux * s0 },
                                { uz * ux * k - uy * s0, uz * uy * k + ux * s0, c0 + uz * uz * k } };
                var Q = new double[3, 3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Q[i, j] = M[j, i];
                var piv = PArr(p, "pivot_mm").Select(v => v.ToObject<double>() / MM).ToArray();
                if (piv.Length != 0 && piv.Length != 3) throw new ToolFail("BAD_PARAMETER", "pivot_mm is 3 numbers [x, y, z] (mm, assembly space).");
                if (piv.Length == 0) piv = new[] { tx, ty, tz };
                var N = new double[3, 3];
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int kk = 0; kk < 3; kk++) N[i, j] += Rm[i, kk] * Q[kk, j];
                Rm = N;
                double[] rel = { tx - piv[0], ty - piv[1], tz - piv[2] };
                double[] rot = new double[3];
                for (int j = 0; j < 3; j++) rot[j] = rel[0] * Q[0, j] + rel[1] * Q[1, j] + rel[2] * Q[2, j];
                tx = rot[0] + piv[0]; ty = rot[1] + piv[1]; tz = rot[2] + piv[2];
            }
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) arr[i * 3 + j] = Rm[i, j];
            arr[9] = absolute ? dx : tx + dx; arr[10] = absolute ? dy : ty + dy; arr[11] = absolute ? dz : tz + dz; arr[12] = t[12];
            var mu = _solidWorks.GetMathUtility() as IMathUtility;
            c.Transform2 = mu.CreateTransform(arr) as MathTransform;
            doc.EditRebuild3(); doc.GraphicsRedraw2();
            var o = OriginMm(c);
            return new JObject { ["component"] = c.Name2, ["origin_mm"] = R(o), ["fixed"] = c.IsFixed(),
                                 ["note"] = "Mates may pull the component back on rebuild." };
        }, treeDiff: false);

        public ExecutionResponse ReplaceComponent(ToolRequest request) => Run(request, true, p =>
        {
            string newFile = PStr(p, "new_file") ?? throw new ToolFail("MISSING_PARAMETER", "new_file is required.");
            if (!File.Exists(newFile)) throw new ToolFail("FILE_NOT_FOUND", $"No replacement file at '{newFile}'.");
            string comp = PStr(p, "component");
            if (comp != null)
            {
                var a = ActiveAsmOrFail(out var doc);
                var c = FindComp(a, comp);
                doc.ClearSelection2(true); c.Select4(false, null, false);
                bool ok = a.ReplaceComponents2(newFile, PStr(p, "config", ""), PBool(p, "all_instances", true), 0, true);
                doc.EditRebuild3();
                if (!ok) throw new ToolFail("REPLACE_FAILED", "ReplaceComponents2 returned false.");
                return new JObject { ["ok"] = true, ["mode"] = "ReplaceComponents2 (open assembly, mates re-attached)" };
            }
            string asm = PStr(p, "assembly_path") ?? throw new ToolFail("MISSING_PARAMETER", "component (open asm) or assembly_path+old_file (closed asm)");
            string oldFile = PStr(p, "old_file") ?? throw new ToolFail("MISSING_PARAMETER", "old_file is required with assembly_path.");
            bool r = _solidWorks.ReplaceReferencedDocument(asm, oldFile, newFile);
            if (!r) throw new ToolFail("REPLACE_FAILED", "ReplaceReferencedDocument returned false (the assembly must be CLOSED).");
            return new JObject { ["ok"] = true, ["mode"] = "ReplaceReferencedDocument (closed assembly)" };
        });

        // Pack and Go: copy the assembly + all references (+ optional drawings) into target_dir with optional
        // renames, then re-point references in the copies. IModelDocExtension.GetPackAndGo is unreliable out of
        // process, so this is copy + ReplaceReferencedDocument (proven by the earlier Python server: 118 files / 11 s).
        public ExecutionResponse PackAndGo(ToolRequest request) => Run(request, true, p =>
        {
            string asm = PStr(p, "assembly_path") ?? ActiveDocOrFail().GetPathName();
            string target = PStr(p, "target_dir") ?? throw new ToolFail("MISSING_PARAMETER", "target_dir is required.");
            if (string.IsNullOrEmpty(asm) || !File.Exists(asm)) throw new ToolFail("FILE_NOT_FOUND", "assembly_path: " + asm);
            var rename = p["rename_map"] as JObject ?? (PStr(p, "rename_map") is string rs ? JObject.Parse(rs) : new JObject());
            bool flatten = PBool(p, "flatten", true), drawings = PBool(p, "include_drawings", false);
            string prefix = PStr(p, "prefix", ""), suffix = PStr(p, "suffix", "");

            // References are rewritten in CLOSED files, so everything gets closed — never silently
            // dropping the user's unsaved work.
            var dirty = (_solidWorks.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>()
                .Where(x => x.GetSaveFlag()).Select(x => x.GetTitle()).ToList();
            if (dirty.Count > 0 && !PBool(p, "discard_unsaved", false))
                throw new ToolFail("UNSAVED_CHANGES", "Save or close these first (or discard_unsaved=true): " + string.Join(", ", dirty.Take(10)));
            _solidWorks.CloseAllDocuments(true);
            var deps = _solidWorks.GetDocumentDependencies2(asm, true, false, false) as string[] ?? new string[0];
            var files = new List<string> { asm };
            for (int i = 1; i < deps.Length; i += 2) if (!files.Contains(deps[i], StringComparer.OrdinalIgnoreCase)) files.Add(deps[i]);
            if (drawings)
                foreach (var f in files.ToList())
                {
                    var dw = Path.ChangeExtension(f, ".SLDDRW");
                    if (File.Exists(dw) && !files.Contains(dw, StringComparer.OrdinalIgnoreCase)) files.Add(dw);
                }
            string root = Path.GetDirectoryName(asm);
            Func<string, string> newName = f =>
            {
                string b = Path.GetFileName(f), stem = Path.GetFileNameWithoutExtension(f), ext = Path.GetExtension(f);
                string nb = (string)rename[b] ?? (string)rename[stem];
                if (nb == null) return prefix + stem + suffix + ext;
                return Path.HasExtension(nb) ? nb : nb + ext;
            };
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                string dst;
                if (flatten) dst = Path.Combine(target, newName(f));
                else
                {
                    string rel = MakeRelative(root, Path.GetDirectoryName(f));
                    if (rel.StartsWith("..")) rel = "_external";
                    dst = Path.Combine(target, rel, newName(f));
                }
                if (seen.TryGetValue(dst, out var prev) && !prev.Equals(f, StringComparison.OrdinalIgnoreCase))
                    throw new ToolFail("BAD_PARAMETER", $"Flattening collides: {prev} and {f} → {dst}. Use flatten=false or rename_map.");
                seen[dst] = f; map[f] = dst;
            }
            foreach (var kv in map)
            {
                if (!File.Exists(kv.Key)) continue;
                EnsureFolderFor(kv.Value);
                File.Copy(kv.Key, kv.Value, true);
                File.SetAttributes(kv.Value, File.GetAttributes(kv.Value) & ~FileAttributes.ReadOnly);
            }
            int fixedRefs = 0; var failed = new JArray();
            foreach (var kv in map)
            {
                var ext = Path.GetExtension(kv.Value).ToLowerInvariant();
                if (ext != ".sldasm" && ext != ".slddrw" && ext != ".sldprt") continue;
                var d2 = _solidWorks.GetDocumentDependencies2(kv.Value, false, false, false) as string[] ?? new string[0];
                for (int i = 1; i < d2.Length; i += 2)
                {
                    if (!map.TryGetValue(d2[i], out var nw) || nw.Equals(d2[i], StringComparison.OrdinalIgnoreCase)) continue;
                    if (_solidWorks.ReplaceReferencedDocument(kv.Value, d2[i], nw)) fixedRefs++;
                    else failed.Add(Path.GetFileName(kv.Value) + " → " + Path.GetFileName(d2[i]));
                }
            }
            return new JObject
            {
                ["files"] = map.Count,
                ["missing_sources"] = new JArray(map.Keys.Where(f => !File.Exists(f))),
                ["renamed"] = map.Count(kv => !Path.GetFileName(kv.Key).Equals(Path.GetFileName(kv.Value), StringComparison.OrdinalIgnoreCase)),
                ["refs_fixed"] = fixedRefs,
                ["refs_failed"] = failed,
                ["assembly"] = map[asm]
            };
        });

        private static string MakeRelative(string fromDir, string toDir)
        {
            var u1 = new Uri(fromDir.TrimEnd('\\') + "\\"); var u2 = new Uri(toDir.TrimEnd('\\') + "\\");
            var rel = Uri.UnescapeDataString(u1.MakeRelativeUri(u2).ToString()).Replace('/', '\\').TrimEnd('\\');
            return rel.Length == 0 ? "." : rel;
        }

        // STEP/IGES/STL/Parasolid of ONLY the listed components, in assembly coordinates: every other
        // top-level occurrence is hidden for the save and restored afterwards.
        public ExecutionResponse ExportComponents(ToolRequest request) => Run(request, false, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            string outPath = PStr(p, "file_path") ?? throw new ToolFail("MISSING_PARAMETER", "file_path is required.");
            var all = Comps(a, false);
            var keep = PStrList(p, "components").Select(n => FindComp(a, n, all).Name2).ToList();
            if (keep.Count == 0) throw new ToolFail("MISSING_PARAMETER", "components is required.");
            var hidden = new List<IComponent2>();
            foreach (var c in Comps(a, true))
            {
                bool wanted = keep.Any(k => k == c.Name2 || k.StartsWith(c.Name2 + "/"));
                if (!wanted && c.Visible != (int)swComponentVisibilityState_e.swComponentHidden)
                { c.Visible = (int)swComponentVisibilityState_e.swComponentHidden; hidden.Add(c); }
            }
            int err = 0, warn = 0; bool ok;
            try
            {
                EnsureFolderFor(outPath);
                ok = doc.Extension.SaveAs(outPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, ref err, ref warn);
            }
            finally { foreach (var c in hidden) c.Visible = (int)swComponentVisibilityState_e.swComponentVisible; }
            if (!ok) throw new ToolFail("EXPORT_FAILED", $"SaveAs failed (err {err}, warn {warn}).");
            return new JObject { ["file"] = outPath, ["components"] = new JArray(keep), ["hidden_for_export"] = hidden.Count };
        });

        // form_subassembly — SolidWorks' "Form New Subassembly" for components of the ACTIVE assembly that sit on one
        // level: an empty assembly is saved to file_path and inserted at the parent's origin, fixed (its frame IS the
        // parent's, so coordinates do not change), or `into` names an existing subassembly; ReorganizeComponents moves
        // the components in, keeping their positions — mates among them move along, mates to the rest stay in the
        // parent. A suppressed component is resolved for the move (it cannot be reorganized otherwise) and reported.
        // The subassembly file is saved; the parent is left modified (save_document keeps it).
        public ExecutionResponse FormSubassembly(ToolRequest request) => Run(request, true, p =>
        {
            var a = ActiveAsmOrFail(out var doc);
            string docTitle = doc.GetTitle();
            var all = Comps(a, false);
            var names = PStrList(p, "components");
            if (names.Count == 0) throw new ToolFail("MISSING_PARAMETER", "components is required (names from list_components).");
            var comps = names.Select(n => FindComp(a, n, all)).Distinct().ToList();
            var parents = comps.Select(c => (c.GetParent() as IComponent2)?.Name2 ?? "").Distinct().ToList();
            if (parents.Count > 1)
                throw new ToolFail("BAD_PARAMETER", $"The components sit on different levels ({string.Join(", ", parents.Select(x => x == "" ? "top" : x))}); form one subassembly per level.");
            var before = comps.Select(c => (file: c.GetPathName(), xyz: Translation(c))).ToList();

            IComponent2 target;
            string into = PStr(p, "into"), file = null;
            if (into != null)
                target = FindComp(a, into, all);
            else
            {
                file = PStr(p, "file_path") ?? throw new ToolFail("MISSING_PARAMETER", "file_path (the new .SLDASM) or into (an existing subassembly) is required.");
                file = Path.GetFullPath(file);
                if (!file.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
                    throw new ToolFail("BAD_PARAMETER", $"file_path must be a .SLDASM file, got '{file}'.");
                if (File.Exists(file))
                    throw new ToolFail("FILE_EXISTS", $"'{file}' already exists — pass into=<its component> to move components into it, or choose another name.");
                EnsureFolderFor(file);
                var blank = NewDoc(swDocumentTypes_e.swDocASSEMBLY);
                int e = 0, w = 0;
                if (!blank.Extension.SaveAs3(file, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w))
                {
                    _solidWorks.CloseDoc(blank.GetTitle());
                    Activate(docTitle);
                    throw new ToolFail("SAVE_FAILED", $"Saving the new subassembly to '{file}' failed (errors {e}, warnings {w}).");
                }
                string blankTitle = blank.GetTitle();
                Activate(docTitle);
                target = a.AddComponent5(file, 0, "", false, "", 0, 0, 0) as IComponent2;
                _solidWorks.CloseDoc(blankTitle);   // the parent keeps its own reference
                if (target == null)
                {
                    Activate(docTitle);
                    try { File.Delete(file); } catch { }   // a retry would hit FILE_EXISTS otherwise
                    throw new ToolFail("INSERT_FAILED", $"AddComponent5 returned null for '{file}'.");
                }
                Activate(docTitle);
                var mu = _solidWorks.IGetMathUtility();
                target.Transform2 = mu.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }) as MathTransform;
                doc.ClearSelection2(true);
                if (target.Select4(false, null, false)) a.FixComponent();
                doc.ClearSelection2(true);
            }

            // the empty subassembly this call inserted goes again on a failure: the model is as it was
            void TakeBack()
            {
                if (file == null) return;
                doc.ClearSelection2(true);
                if (target.Select4(false, null, false)) doc.EditDelete();
                doc.ClearSelection2(true);
                try { File.Delete(file); } catch { }
            }
            // ReorganizeComponents needs resolved components
            var resolved = new JArray();
            foreach (var c in comps.Where(c => c.IsSuppressed()))
            {
                if (c.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved) != (int)swSuppressionError_e.swSuppressionChangeOk)
                {
                    TakeBack();
                    throw new ToolFail("SUPPRESSED", $"'{c.Name2}' is suppressed and could not be resolved (its file is missing?) — re-point it with replace_component first.");
                }
                resolved.Add(c.Name2);
            }
            int matesBefore = MateFeatures(doc).Count();
            // a TYPED Component2[] — with object[] ReorganizeComponents just returns false (like the weldment arrays)
            if (!a.ReorganizeComponents(comps.Cast<Component2>().ToArray(), target))
            {
                TakeBack();
                throw new ToolFail("REORGANIZE_FAILED", "ReorganizeComponents returned false (components on another level than the target's parent, or a pattern / mirror instance whose seed stays behind).");
            }
            doc.EditRebuild3();

            // every moved component where it was: the nearest child of the same file
            var children = ((target.GetChildren() as object[]) ?? new object[0]).OfType<IComponent2>().ToList();
            double shift = 0;
            foreach (var b in before)
            {
                var same = children.Where(c => string.Equals(c.GetPathName(), b.file, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count == 0) { shift = double.PositiveInfinity; continue; }
                shift = Math.Max(shift, same.Min(c => Dist(Translation(c), b.xyz)));
            }
            var subDoc = target.GetModelDoc2() as IModelDoc2;
            int se = 0, sw = 0;
            bool saved = subDoc != null && subDoc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref se, ref sw);
            var res = new JObject
            {
                ["subassembly"] = target.Name2, ["file"] = target.GetPathName(), ["moved"] = comps.Count,
                ["children"] = children.Count, ["max_shift_mm"] = double.IsInfinity(shift) ? null : new JValue(Math.Round(shift * MM, 4)),
                ["mates_left_in_parent"] = MateFeatures(doc).Count(), ["mates_moved"] = matesBefore - MateFeatures(doc).Count(),
                ["subassembly_saved"] = saved
            };
            if (resolved.Count > 0) res["resolved_for_the_move"] = resolved;
            if (double.IsInfinity(shift) || shift * MM > 0.01)
                res["warning"] = "A moved component is not where it was (or is missing from the subassembly) — check before saving the parent.";
            if (!saved) res["warning"] = (res["warning"] + " ").TrimStart() + $"The subassembly file was not saved (errors {se}, warnings {sw}).";
            return res;
        });

        private static double[] Translation(IComponent2 c)
        {
            var t = (c.Transform2 as MathTransform)?.ArrayData as double[];
            return t == null ? new double[3] : new[] { t[9], t[10], t[11] };
        }

        private static double Dist(double[] u, double[] v) =>
            Math.Sqrt((u[0] - v[0]) * (u[0] - v[0]) + (u[1] - v[1]) * (u[1] - v[1]) + (u[2] - v[2]) * (u[2] - v[2]));
    }
}
