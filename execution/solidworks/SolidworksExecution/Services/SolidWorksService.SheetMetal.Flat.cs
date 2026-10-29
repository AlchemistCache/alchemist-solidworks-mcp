using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // flat_geometry (internal, for sheet_metal_dfm) — the flat pattern as 2D outline loops and bend lines, read from the
    // model itself. ExportToDWG2 took 8-10 s a part plus ~3 s to activate the part and come back to the assembly (a real
    // machine: 8 panels 88-117 s); here the Flat-Pattern feature is unsuppressed in the part's document (no window), the outline
    // comes from ONE tessellation call and the bend lines from ONE sketch call, and the feature is suppressed again
    // (8 panels 8-15 s). A part without tessellation is reported failed — the adapter exports that one the old way.
    public partial class SolidWorksService
    {
        public ExecutionResponse FlatGeometry(ToolRequest request) => Run(request, false, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            var parts = new JArray(); var failed = new JArray();
            if (doc.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                if (!IsSheetMetalPart(doc)) throw new ToolFail("NOT_SHEET_METAL", $"'{doc.GetTitle()}' is not a sheet-metal part.");
                string cfg = (doc.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
                parts.Add(FlatPart(doc, cfg, 1));
                return new JObject { ["parts"] = parts, ["failed"] = failed };
            }
            var a = (IAssemblyDoc)doc;
            string comp = PStr(p, "component");
            var list = comp != null
                ? new List<Tuple<string, string, int>> { Tuple.Create(FindComp(a, comp).GetPathName(), FindComp(a, comp).ReferencedConfiguration, 1) }
                : SheetMetalPartsOf(a);
            foreach (var t in list)
            {
                var tm = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    parts.Add(WithPartDocFast(t.Item1, t.Item2, d =>
                    {
                        if (!IsSheetMetalPart(d)) throw new ToolFail("NOT_SHEET_METAL", "not a sheet-metal part");
                        return FlatPart(d, t.Item2, t.Item3);
                    }));
                }
                catch (Exception ex)
                {
                    // the old way for this one: its flat pattern exported to a DXF (activates the part — slower)
                    try { parts.Add(FlatByExport(t.Item1, t.Item2, t.Item3, PStr(p, "out_dir"), ex.Message)); }
                    catch (Exception ex2) { failed.Add(System.IO.Path.GetFileName(t.Item1) + " [" + t.Item2 + "]: " + ex.Message + "; export: " + ex2.Message); }
                }
                ExecLog.Write($"flat_geometry: {System.IO.Path.GetFileName(t.Item1)} in {tm.ElapsedMilliseconds} ms");
            }
            return new JObject { ["parts"] = parts, ["failed"] = failed };
        }, keepClean: true);

        private JObject FlatByExport(string path, string config, int qty, string outDir, string why)
        {
            outDir = outDir ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SolidWorksMCP", "dfm");
            using (VisibleWindow())
                return WithPartConfig(path, config, d =>
                {
                    string file = System.IO.Path.Combine(outDir, SafeName(System.IO.Path.GetFileNameWithoutExtension(path) + (string.IsNullOrEmpty(config) ? "" : "_" + config)) + ".dxf");
                    if (!ExportFlat(d, file, DxfOptions(new JObject { ["bend_lines"] = true }))) throw new ToolFail("EXPORT_FAILED", "ExportToDWG2 returned false");
                    var e = FlatEntry(d, config, file, qty);
                    e["read_by_export"] = why;
                    return e;
                });
        }

        // One part: FlatEntry's fields (thickness, radius, K, material, names) + loops + bend lines, mm.
        private JObject FlatPart(IModelDoc2 d, string config, int qty)
        {
            var e = FlatEntry(d, config, null, qty);
            e.Remove("file");
            e["path"] = d.GetPathName();
            var fp = Features(d).FirstOrDefault(f => f.GetTypeName2() == "FlatPattern")
                     ?? throw new ToolFail("NO_FLAT_PATTERN", "no Flat-Pattern feature");
            bool wasSuppressed = false;
            try { wasSuppressed = fp.IsSuppressed(); } catch { }
            try
            {
                if (wasSuppressed)
                {
                    fp.SetSuppression2((int)swFeatureSuppressionAction_e.swUnSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    d.EditRebuild3();
                }
                ReadFlat(d as IPartDoc, fp, e);
            }
            finally
            {
                if (wasSuppressed)
                {
                    fp.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature, (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    d.EditRebuild3();
                }
            }
            return e;
        }

        private void ReadFlat(IPartDoc part, IFeature fp, JObject outJ)
        {
            // the Bend-Lines sketch («Сгиб-Линии»), the other sub-sketch is the Bounding-Box («Граничная рамка»). With bends
            // it lies on the sheet and its frame is the flat-pattern view's; a part without bends has an empty one on
            // some other plane.
            var sketches = SubFeatures(fp).Where(f => f.GetTypeName2() == "ProfileFeature").ToList();
            var bendFeat = sketches.FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, "(?i)bend|сгиб")) ?? sketches.FirstOrDefault();
            var sk = bendFeat?.GetSpecificFeature2() as ISketch;
            // model → sketch as plain numbers: rotation 0..8, translation 9..11, scale 12; SolidWorks multiplies row vectors
            var T = ((sk?.ModelToSketchTransform as MathTransform)?.ArrayData as double[])?.ToArray();
            var bends = new JArray();
            // all lines of the sketch in one call: 12 numbers a line, its ends in sketch coordinates at 6..11
            var lines = sk?.GetLines2(0) as double[];
            for (int i = 0; lines != null && i + 11 < lines.Length; i += 12)
                bends.Add(new JArray(Math.Round(lines[i + 6] * MM, 4), Math.Round(lines[i + 7] * MM, 4), Math.Round(lines[i + 9] * MM, 4), Math.Round(lines[i + 10] * MM, 4)));
            var loops = TessOutline(part, bends.Count > 0 ? T : null, out var D, out var P0)
                        ?? throw new ToolFail("FLAT_FAILED", "no tessellation of the flattened body");
            Func<double[], double[]> map;
            if (T != null && bends.Count > 0 && Math.Abs(Rot(T, D)[2]) > 0.999) map = v => Apply(T, v);
            else
            {
                double[] x0 = Math.Abs(D[0]) < 0.9 ? new[] { 1.0, 0, 0 } : new[] { 0.0, 1, 0 };
                double dd = x0[0] * D[0] + x0[1] * D[1] + x0[2] * D[2];
                var x = Unit(new[] { x0[0] - dd * D[0], x0[1] - dd * D[1], x0[2] - dd * D[2] });
                var y = Cross(D, x);
                map = v => new[] { (v[0] - P0[0]) * x[0] + (v[1] - P0[1]) * x[1] + (v[2] - P0[2]) * x[2],
                                   (v[0] - P0[0]) * y[0] + (v[1] - P0[1]) * y[1] + (v[2] - P0[2]) * y[2], 0 };
                bends.Clear();   // in the sketch's frame, which is not this one
            }
            outJ["loops"] = new JArray(loops.Select(l => new JArray(l.Select(v => { var q = map(v); return new JArray(Math.Round(q[0] * MM, 4), Math.Round(q[1] * MM, 4)); }))));
            outJ["bend_lines"] = bends;
        }

        // The sheet side's outline from the part's tessellation — ONE call (GetTessTriangles) instead of ~10 per edge (a COM
        // call to SolidWorks costs 5-15 ms here; the 138 edges of a side panel took 6 s). The side = the triangles with the
        // largest-area normal direction, on its largest plane; its outline = the triangle edges used once, chained into
        // closed loops. Tessellation vertices lie ON the edges (holes come out exact, round corners as chords). null when
        // there is no tessellation.
        private static List<List<double[]>> TessOutline(IPartDoc part, double[] sketchT, out double[] sideNormal, out double[] sidePoint)
        {
            sideNormal = sidePoint = null;
            var tri = part.GetTessTriangles(true) as float[];
            if (tri == null || tri.Length < 9) return null;
            int n = tri.Length / 9;
            var nrm = new double[n][]; var area = new double[n];
            var dirArea = new Dictionary<string, double>(); var dirVec = new Dictionary<string, double[]>();
            for (int i = 0; i < n; i++)
            {
                int k = i * 9;
                double ax = tri[k + 3] - tri[k], ay = tri[k + 4] - tri[k + 1], az = tri[k + 5] - tri[k + 2];
                double bx = tri[k + 6] - tri[k], by = tri[k + 7] - tri[k + 1], bz = tri[k + 8] - tri[k + 2];
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                double len = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                area[i] = len / 2;
                if (len < 1e-14) continue;
                var u = new[] { cx / len, cy / len, cz / len };
                nrm[i] = u;
                // the axis of the normal, both senses together
                double s = Math.Abs(u[0]) > 1e-6 ? Math.Sign(u[0]) : Math.Abs(u[1]) > 1e-6 ? Math.Sign(u[1]) : Math.Sign(u[2]);
                string key = $"{Math.Round(u[0] * s, 2)}|{Math.Round(u[1] * s, 2)}|{Math.Round(u[2] * s, 2)}";
                dirArea[key] = (dirArea.TryGetValue(key, out var a0) ? a0 : 0) + area[i];
                if (!dirVec.ContainsKey(key)) dirVec[key] = new[] { u[0] * s, u[1] * s, u[2] * s };
            }
            if (dirArea.Count == 0) return null;
            var D = dirVec[dirArea.OrderByDescending(kv => kv.Value).First().Key];
            // planes along D (either sense): offsets clustered within 20 µm (rounding them split a tilted sheet into
            // pieces — float32 vertices straddled the rounding steps), the sheet's two sides are the largest clusters
            var offs = new List<Tuple<double, double>>();   // offset, area
            var offOf = new double[n];
            for (int i = 0; i < n; i++)
            {
                offOf[i] = double.NaN;
                if (nrm[i] == null || Math.Abs(nrm[i][0] * D[0] + nrm[i][1] * D[1] + nrm[i][2] * D[2]) < 0.9999) continue;
                offOf[i] = tri[i * 9] * D[0] + tri[i * 9 + 1] * D[1] + tri[i * 9 + 2] * D[2];
                offs.Add(Tuple.Create(offOf[i], area[i]));
            }
            var clusters = new List<double[]>();   // lo, hi, area
            foreach (var o in offs.OrderBy(o => o.Item1))
            {
                var last = clusters.Count > 0 ? clusters[clusters.Count - 1] : null;
                if (last != null && o.Item1 - last[1] < 2e-5) { last[1] = o.Item1; last[2] += o.Item2; }
                else clusters.Add(new[] { o.Item1, o.Item1, o.Item2 });
            }
            double maxA = clusters.Max(c => c[2]);
            var sides = clusters.Where(c => c[2] > 0.99 * maxA).ToList();
            var side = sides[0];
            if (sketchT != null && sides.Count > 1)   // the side nearer the sketch keeps its frame un-mirrored
                side = sides.OrderBy(c => Math.Abs(Apply(sketchT, new[] { D[0] * c[0], D[1] * c[0], D[2] * c[0] })[2])).First();
            double sideOff = (side[0] + side[1]) / 2;
            sideNormal = D; sidePoint = new[] { D[0] * sideOff, D[1] * sideOff, D[2] * sideOff };

            // boundary = triangle edges used once on that plane
            var pts = new Dictionary<string, double[]>();
            var uses = new Dictionary<string, int>();
            var directed = new List<Tuple<string, string>>();
            Func<int, string> keyOf = j =>
            {
                string kk = $"{Math.Round(tri[j] * 1e6)}|{Math.Round(tri[j + 1] * 1e6)}|{Math.Round(tri[j + 2] * 1e6)}";
                if (!pts.ContainsKey(kk)) pts[kk] = new double[] { tri[j], tri[j + 1], tri[j + 2] };
                return kk;
            };
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(offOf[i]) || offOf[i] < side[0] - 1e-5 || offOf[i] > side[1] + 1e-5) continue;
                string a = keyOf(i * 9), b = keyOf(i * 9 + 3), c = keyOf(i * 9 + 6);
                foreach (var e in new[] { Tuple.Create(a, b), Tuple.Create(b, c), Tuple.Create(c, a) })
                {
                    if (e.Item1 == e.Item2) continue;
                    string u = string.CompareOrdinal(e.Item1, e.Item2) < 0 ? e.Item1 + "#" + e.Item2 : e.Item2 + "#" + e.Item1;
                    uses[u] = (uses.TryGetValue(u, out var c0) ? c0 : 0) + 1;
                    directed.Add(e);
                }
            }
            var next = new Dictionary<string, List<string>>();
            foreach (var e in directed)
            {
                string u = string.CompareOrdinal(e.Item1, e.Item2) < 0 ? e.Item1 + "#" + e.Item2 : e.Item2 + "#" + e.Item1;
                if (uses[u] != 1) continue;
                if (!next.TryGetValue(e.Item1, out var l)) next[e.Item1] = l = new List<string>();
                l.Add(e.Item2);
            }
            var loops = new List<List<double[]>>();
            while (next.Count > 0)
            {
                string start = next.Keys.First(), cur = start;
                var loop = new List<double[]>();
                for (int guard = 0; guard < 1000000; guard++)
                {
                    loop.Add(pts[cur]);
                    if (!next.TryGetValue(cur, out var l) || l.Count == 0) break;
                    string nx = l[l.Count - 1];
                    l.RemoveAt(l.Count - 1);
                    if (l.Count == 0) next.Remove(cur);
                    cur = nx;
                    if (cur == start) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        // p·R·scale + t (a MathTransform's ArrayData applied to a point) and the rotation alone (a direction).
        private static double[] Apply(double[] t, double[] p)
        {
            var r = Rot(t, p);
            double s = t.Length > 12 && t[12] != 0 ? t[12] : 1;
            return new[] { r[0] * s + t[9], r[1] * s + t[10], r[2] * s + t[11] };
        }

        private static double[] Rot(double[] t, double[] p) =>
            new[] { p[0] * t[0] + p[1] * t[3] + p[2] * t[6], p[0] * t[1] + p[1] * t[4] + p[2] * t[7], p[0] * t[2] + p[1] * t[5] + p[2] * t[8] };

    }
}
