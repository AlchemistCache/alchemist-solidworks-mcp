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
    // SolidWorksService partial: tidying a drawing (ideas from cyounghabs/mcp-server-solidworks, AGPL — own code):
    //   dedupe_drawing_dimensions — one model dimension shown in several views keeps the best view only;
    //   layout_drawing_views      — views in projection relation (first / third angle of the sheet), no overlaps,
    //                               clear of the title block, at one standard scale that fits;
    //   add_dimension_between     — a drawing dimension between two entities picked by sheet points.
    // All lengths in mm on the sheet.
    public partial class SolidWorksService
    {
        // Where a view looks from, as its role: the model direction toward the viewer (view Z in model space).
        private static string ViewRole(IView v)
        {
            try
            {
                if (v.IsFlatPatternView()) return "flat";
                int t = v.Type;
                if (t == (int)swDrawingViewTypes_e.swDrawingSectionView) return "section";
                if (t == (int)swDrawingViewTypes_e.swDrawingDetailView) return "detail";
                if (t == (int)swDrawingViewTypes_e.swDrawingAuxiliaryView) return "auxiliary";
                // SolidWorks transforms row vectors (p·R): view Z in model space is the THIRD ROW of view→model — the
                // third column gave the top view as "bottom" and the left one as "right"
                var d = v.ModelToViewTransform?.IInverse()?.ArrayData as double[];
                if (d == null || d.Length < 9) return "other";
                double nx = d[6], ny = d[7], nz = d[8], n = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (n < 1e-9) return "other";
                nx /= n; ny /= n; nz /= n;
                const double c = 0.999;
                if (nz > c) return "front";
                if (nz < -c) return "back";
                if (ny > c) return "top";
                if (ny < -c) return "bottom";
                if (nx > c) return "right";
                if (nx < -c) return "left";
                return "isometric";
            }
            catch { return "other"; }
        }

        // Which view keeps a dimension shown in several: the main view, then top, the side views, the rest; isometric last.
        private static readonly string[] RoleRank = { "front", "top", "left", "right", "section", "detail", "bottom", "back", "auxiliary", "flat", "other", "isometric" };

        private static IEnumerable<IDisplayDimension> ViewDimensions(IView v)
        {
            object o = null;
            try { o = v.GetFirstDisplayDimension5(); } catch { }
            for (int guard = 0; o is IDisplayDimension dd && guard < 5000; guard++)
            {
                yield return dd;
                try { o = dd.GetNext5(); } catch { o = null; }
            }
        }

        // Model dimensions come into a view where they stood in the sketch — across the geometry (the crank arm's 170,
        // 135 and Ø20 lay over the part). SolidWorks' own Auto Arrange (Align Dimensions) puts them outside: linear
        // ones stacked off the outline, the shortest nearest, radii and diameters out on leaders. It works on the
        // SELECTED dimensions; IAnnotation.Select3 selects nothing here, SelectByID2 by name does.
        // `skip`: names (GetNameForSelection) of dimensions to leave where they are — the ones placed before this call.
        private static int ArrangeDimensions(IModelDoc2 d, IEnumerable<IView> views, ISet<string> skip = null)
        {
            d.ClearSelection2(true);
            int n = 0;
            foreach (var v in views)
                foreach (var dd in ViewDimensions(v))
                    try
                    {
                        string name = dd.GetNameForSelection();
                        if (skip != null && skip.Contains(name)) continue;
                        if (d.Extension.SelectByID2(name, "DIMENSION", 0, 0, 0, true, 0, null, 0)) n++;
                    }
                    catch { }
            bool ok = false;
            try { ok = n > 0 && d.Extension.AlignDimensions((int)swAlignDimensionType_e.swAlignDimensionType_AutoArrange, 0); } catch { }
            d.ClearSelection2(true);
            return ok ? n : 0;
        }

        // What a dimension measures as the view shows it: the kind, the value and its reference points projected onto
        // the sheet (mm) — a linear one by its two points in either order, a radius / diameter by its centre. Two
        // sketches dimensioning the same 170 between the hole centres give the same key. Null for other kinds.
        private string SameSizeKey(IView v, IDisplayDimension dd)
        {
            try
            {
                int t = dd.Type2;
                bool linear = t == (int)swDimensionType_e.swLinearDimension || t == (int)swDimensionType_e.swHorLinearDimension
                              || t == (int)swDimensionType_e.swVertLinearDimension;
                bool round = t == (int)swDimensionType_e.swRadialDimension || t == (int)swDimensionType_e.swDiameterDimension;
                if (!linear && !round) return null;
                var dim = dd.GetDimension2(0) as IDimension;
                var pts = dim?.ReferencePoints as object[];
                if (pts == null || pts.Length < (linear ? 2 : 1)) return null;
                var xf = v.ModelToViewTransform;
                string P(object o)
                {
                    var q = ((o as IMathPoint)?.MultiplyTransform(xf) as IMathPoint)?.ArrayData as double[];
                    return q == null ? null : $"{Math.Round(q[0] * MM, 1)},{Math.Round(q[1] * MM, 1)}";
                }
                var a = P(pts[0]);
                var b = linear ? P(pts[1]) : "";
                if (a == null || b == null) return null;
                if (linear && string.CompareOrdinal(a, b) > 0) (a, b) = (b, a);
                return $"{t}|{Math.Round(dim.SystemValue * MM, 4)}|{a}|{b}";
            }
            catch { return null; }
        }

        public ExecutionResponse DedupeDrawingDimensions(ToolRequest request) => Run(request, true, p =>
        {
            var dr = ActiveDrawingOrFail(out var d);
            bool dry = PBool(p, "dry_run", false);
            var shown = new List<(int order, IView view, string role, IDisplayDimension dd, string full, double value)>();
            int order = 0;
            foreach (var v in DrawingViews(dr))
            {
                string role = ViewRole(v);
                foreach (var dd in ViewDimensions(v))
                {
                    var dim = dd.GetDimension2(0) as IDimension;
                    double val = 0;
                    try { val = dim?.SystemValue ?? 0; } catch { }
                    if (!string.IsNullOrEmpty(dim?.FullName)) shown.Add((order++, v, role, dd, dim.FullName, IsAngular(dim) ? val * 180 / Math.PI : val * MM));
                }
            }
            // a dimension of the MODEL keeps its name in every view it is shown in (D1@Эскиз1@Деталь.Part); one made in the
            // drawing is named after its own view, so it never groups
            var removed = new JArray(); var kept = new JArray();
            var drop = new List<IDisplayDimension>();
            var gone = new HashSet<int>();
            foreach (var g in shown.GroupBy(x => x.full).Where(g => g.Count() > 1))
            {
                var keep = g.OrderBy(x => Array.IndexOf(RoleRank, x.role) < 0 ? 99 : Array.IndexOf(RoleRank, x.role)).ThenBy(x => x.order).First();
                kept.Add(new JObject { ["name"] = keep.full, ["value"] = Math.Round(keep.value, 3), ["view"] = keep.view.Name, ["role"] = keep.role });
                foreach (var x in g.Where(x => x.order != keep.order))
                {
                    removed.Add(new JObject { ["name"] = x.full, ["view"] = x.view.Name, ["role"] = x.role });
                    drop.Add(x.dd);
                    gone.Add(x.order);
                }
            }
            // one size given twice in a view by two sketches (the profile's 170 and the holes' 170 between the same
            // centres): the first stays
            int sameSize = 0;
            foreach (var g in shown.Where(x => !gone.Contains(x.order)).GroupBy(x => x.view.Name))
            {
                var seen = new HashSet<string>();
                foreach (var x in g.OrderBy(x => x.order))
                {
                    string key = SameSizeKey(x.view, x.dd);
                    if (key == null || seen.Add(key)) continue;
                    removed.Add(new JObject { ["name"] = x.full, ["view"] = x.view.Name, ["role"] = x.role, ["why"] = "the same size twice in this view" });
                    drop.Add(x.dd);
                    sameSize++;
                }
            }
            int deleted = 0;
            if (!dry)
                foreach (var dd in drop)
                {
                    d.ClearSelection2(true);
                    // the drawing's annotation only — the model dimension stays
                    if ((dd.GetAnnotation() as IAnnotation)?.Select3(false, null) == true && (d.Extension.DeleteSelection2(0) || TryEditDelete(d))) deleted++;
                }
            d.ClearSelection2(true);
            // the stacks close up where a dimension went
            int arranged = !dry && deleted > 0 && PBool(p, "arrange", true) ? ArrangeDimensions(d, DrawingViews(dr)) : 0;
            if (!dry) d.GraphicsRedraw2();
            var res = new JObject
            {
                ["dimensions"] = shown.Count, ["duplicated"] = kept.Count, ["same_size_in_view"] = sameSize, ["removed_count"] = dry ? 0 : deleted,
                ["kept"] = new JArray(kept.Take(30)), ["removed"] = new JArray(removed.Take(40))
            };
            if (arranged > 0) res["arranged"] = arranged;
            if (dry) res["dry_run"] = true;
            else if (deleted < drop.Count) res["warning"] = $"{drop.Count - deleted} duplicate(s) could not be deleted.";
            return res;
        });

        // ---------------------------------------------------------------------------------------------- layout
        // GOST 2.302 scales
        private static readonly double[] StdScales = { 100, 50, 40, 25, 20, 10, 5, 4, 2.5, 2, 1, 1 / 2.0, 1 / 2.5, 1 / 4.0, 1 / 5.0, 1 / 10.0,
            1 / 15.0, 1 / 20.0, 1 / 25.0, 1 / 40.0, 1 / 50.0, 1 / 75.0, 1 / 100.0, 1 / 200.0, 1 / 400.0, 1 / 500.0, 1 / 800.0, 1 / 1000.0 };

        private static string ScaleText(double s) => s >= 1 ? $"{s:0.##}:1" : $"1:{1 / s:0.##}";

        // "1:2" → 0.5, "2:1" → 2, "0.5" → 0.5; 0 when it is not a scale
        private static double ParseScale(string t)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = t.Replace(',', '.').Split(':');
            if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var a)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var b) && a > 0 && b > 0) return a / b;
            return parts.Length == 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var v) && v > 0 ? v : 0;
        }

        private static bool TryEditDelete(IModelDoc2 d) { try { d.EditDelete(); return true; } catch { return false; } }

        // A view as laid out: its Position (the point SolidWorks keeps aligned between projected views) and how far its
        // outline reaches from it to each side — an outline grows with dimensions on one side, so views are lined up by
        // Position, not by outline centre (the front view stood 2.2 mm off the top one when centred by outline)
        private sealed class LaidView
        {
            public IView View; public string Role; public double[] Box, Pos;   // outline xmin, ymin, xmax, ymax; Position — mm
            public int Col, Row;
            public double L => Pos[0] - Box[0];
            public double R => Box[2] - Pos[0];
            public double Dn => Pos[1] - Box[1];
            public double Up => Box[3] - Pos[1];
            public void Read()
            {
                Box = OutlineMm(View) ?? Box;
                var q = View.Position as double[];
                if (q != null) Pos = new[] { q[0] * MM, q[1] * MM };
                if (Box != null) Box = WithAnnotations(View, Role, Box);
            }
        }

        // The outline grown by the view's dimension texts (position ± 3 mm; points farther than 60 mm from the outline are
        // not taken) and, for a view that carries a label, by the label: SolidWorks puts it centred above the view, its top
        // 26–28 mm over the outline (DXF export of box4 at 1:2.5: "Изометрия", "А–А"). The label's own extent read
        // through the API is no guide — stale (А–А read at 90…110 × 277…284 while drawn at 274, 183), and the
        // unlabelled main views report 7.7 mm wide ones. Labelled: isometric, section, detail, auxiliary views and flat
        // patterns; views in projection relation carry none (GOST 2.305).
        private static readonly string[] Labelled = { "isometric", "section", "detail", "auxiliary", "flat", "other" };

        private static double[] WithAnnotations(IView v, string role, double[] box)
        {
            var b = (double[])box.Clone();
            object[] anns = null;
            try { anns = v.GetAnnotations() as object[]; } catch { }
            foreach (var o in anns ?? new object[0])
            {
                if (!(o is IAnnotation ann)) continue;
                try
                {
                    if (ann.Visible == (int)swAnnotationVisibilityState_e.swAnnotationHidden || ann.GetSpecificAnnotation() is INote) continue;
                    if (!(ann.GetPosition() is double[] q) || q.Length < 2) continue;
                    double px = q[0] * MM, py = q[1] * MM;
                    if (px < box[0] - 60 || px > box[2] + 60 || py < box[1] - 60 || py > box[3] + 60) continue;
                    b = new[] { Math.Min(b[0], px - 3), Math.Min(b[1], py - 3), Math.Max(b[2], px + 3), Math.Max(b[3], py + 3) };
                }
                catch { }
            }
            if (Labelled.Contains(role))
            {
                double cx = (box[0] + box[2]) / 2;
                b = new[] { Math.Min(b[0], cx - 16), b[1], Math.Max(b[2], cx + 16), Math.Max(b[3], box[3] + 30) };
            }
            return b;
        }

        private static double[] OutlineMm(IView v)
        {
            var o = v.GetOutline() as double[];
            return o == null || o.Length < 4 ? null : new[] { o[0] * MM, o[1] * MM, o[2] * MM, o[3] * MM };
        }

        public ExecutionResponse LayoutDrawingViews(ToolRequest request) => Run(request, true, p =>
        {
            var dr = ActiveDrawingOrFail(out var d);
            var sheet = dr.GetCurrentSheet() as ISheet ?? throw new ToolFail("NO_SHEET", "The drawing has no active sheet.");
            var props = sheet.GetProperties2() as double[];
            double sw = props != null && props.Length > 6 ? props[5] * MM : 420, sh = props != null && props.Length > 6 ? props[6] * MM : 297;
            string proj = (PStr(p, "projection", "auto") ?? "auto").ToLowerInvariant();
            bool firstAngle = proj == "first" || (proj == "auto" && (props == null || props.Length < 5 || props[4] != 0));
            // GOST 2.301 frame: 20 mm on the left, 5 elsewhere; GOST 2.104 title block 185 × 55 in the bottom right corner
            var m = PArr(p, "margins_mm").Select(x => (double)x).ToArray();
            double ml = m.Length == 4 ? m[0] : 20, mb = m.Length == 4 ? m[1] : 5, mr = m.Length == 4 ? m[2] : 5, mt = m.Length == 4 ? m[3] : 5;
            var tb = PArr(p, "title_block_mm").Select(x => (double)x).ToArray();
            double tbw = tb.Length == 2 ? tb[0] : 185, tbh = tb.Length == 2 ? tb[1] : 55;
            double gap = PNum(p, "gap_mm", 15);
            string fit = (PStr(p, "scale", "fit") ?? "fit").Trim().ToLowerInvariant();   // fit | keep | 1:2 | 2:1 | 0.5
            bool arrange = PBool(p, "arrange", true);

            // a fresh view's label has no extent until the sheet is drawn once (the first layout of a new drawing left
            // "Изометрия" in the corner, the second placed it)
            d.EditRebuild3();
            d.GraphicsRedraw2();
            // dimensions off the geometry before the boxes are measured
            if (arrange) ArrangeDimensions(d, DrawingViews(dr));
            var views = DrawingViews(dr).Select(v => { var x = new LaidView { View = v, Role = ViewRole(v) }; x.Read(); return x; })
                                        .Where(x => x.Box != null && x.Pos != null).ToList();
            if (views.Count == 0) throw new ToolFail("NO_VIEWS", "The active sheet has no views to lay out.");

            // projection grid around the main view: first angle (GOST) — top view below, left view on the right;
            // third angle — top above, right view on the right
            var main = views.FirstOrDefault(x => x.Role == "front") ?? views.First();
            var cell = firstAngle
                ? new Dictionary<string, (int c, int r)> { ["top"] = (0, 1), ["bottom"] = (0, -1), ["left"] = (1, 0), ["right"] = (-1, 0), ["back"] = (2, 0) }
                : new Dictionary<string, (int c, int r)> { ["top"] = (0, -1), ["bottom"] = (0, 1), ["right"] = (1, 0), ["left"] = (-1, 0), ["back"] = (2, 0) };
            main.Col = 0; main.Row = 0;
            var taken = new HashSet<(int, int)> { (0, 0) };
            var rest = new List<LaidView>();
            foreach (var x in views.Where(x => x != main))
            {
                if (main.Role == "front" && cell.TryGetValue(x.Role, out var at) && taken.Add(at)) { x.Col = at.c; x.Row = at.r; }
                else rest.Add(x);
            }
            // the rest (isometric, sections, details, flat patterns): first into the empty cells of the projection block
            // (box4: the section under the left view, beside the top one — 1:4 became 1:2.5), then a column to its right.
            // A section keeps its projection link to the parent and would not move there, so the link goes (GOST 2.305
            // lets a designated section stand anywhere on the sheet)
            var grid = views.Where(x => !rest.Contains(x)).ToList();
            int c0 = grid.Min(x => x.Col), c1 = grid.Max(x => x.Col), r0 = grid.Min(x => x.Row), r1 = grid.Max(x => x.Row);
            var free = new List<(int c, int r)>();
            for (int r = r0; r <= r1; r++)
                for (int c = c1; c >= c0; c--)
                    if (!taken.Contains((c, r))) free.Add((c, r));
            var unlinked = new JArray(); var locked = new JArray();
            for (int i = 0; i < rest.Count; i++)
            {
                if (i < free.Count) { rest[i].Col = free[i].c; rest[i].Row = free[i].r; }
                else { rest[i].Col = c1 + 1; rest[i].Row = r0 + i - free.Count; }
                try { if (rest[i].View.GetAlignment() != 0) { rest[i].View.RemoveAlignment(); unlinked.Add(rest[i].View.Name); } } catch { }
            }
            foreach (var x in views)
                try { if (x.View.PositionLocked) locked.Add(x.View.Name); } catch { }

            double ux0 = ml, uy0 = mb, ux1 = sw - mr, uy1 = sh - mt;           // usable area
            double[] tbBox = { sw - mr - tbw, mb, sw - mr, mb + tbh };          // title block
            var obstacles = new List<double[]> { tbBox };
            obstacles.AddRange(SheetAnnotationBoxes(dr));                       // technical requirements, roughness sign …
            const double clear = 3;
            // the main view's scale is the sheet's; every other view keeps its ratio to it (read before anything changes —
            // a section follows its parent's scale by itself and became 2:1 when multiplied again)
            var orig = views.ToDictionary(x => x, x => x.View.ScaleDecimal);
            double cur = orig[main];

            // the block at scale factor k: each view's Position offset from the block's top-left corner (outlines grow
            // with the view scale; detail views keep theirs)
            double F(LaidView x, double k) => x.Role == "detail" ? 1 : k;
            Func<double, (Dictionary<LaidView, double[]> at, double w, double h)> size = k =>
            {
                var cols = views.GroupBy(x => x.Col).OrderBy(g => g.Key).Select(g => (g.Key, l: g.Max(x => x.L * F(x, k)), r: g.Max(x => x.R * F(x, k)))).ToList();
                var rows = views.GroupBy(x => x.Row).OrderBy(g => g.Key).Select(g => (g.Key, u: g.Max(x => x.Up * F(x, k)), dn: g.Max(x => x.Dn * F(x, k)))).ToList();
                var cx = new Dictionary<int, double>(); double xa = 0;
                foreach (var c in cols) { cx[c.Key] = xa + c.l; xa += c.l + c.r + gap; }
                var ry = new Dictionary<int, double>(); double ya = 0;
                foreach (var r in rows) { ry[r.Key] = ya + r.u; ya += r.u + r.dn + gap; }
                return (views.ToDictionary(x => x, x => new[] { cx[x.Col], ry[x.Row] }), xa - gap, ya - gap);
            };
            // the views' boxes with the block's top-left corner at (bx, by)
            double[] BoxAt(LaidView x, double[] off, double k, double bx, double by) =>
                new[] { bx + off[0] - x.L * F(x, k), by - off[1] - x.Dn * F(x, k), bx + off[0] + x.R * F(x, k), by - off[1] + x.Up * F(x, k) };
            bool Hit(double[] u, double[] v, double c) => u[0] < v[2] + c && v[0] < u[2] + c && u[1] < v[3] + c && v[1] < u[3] + c;
            // the corner nearest the middle of the space above the title block where every view is on the sheet and
            // clear of the title block and of the sheet's notes and signs (5 mm steps)
            Func<double, double[]> place = k =>
            {
                var z = size(k);
                double tx = (ux0 + ux1) / 2, ty = (tbBox[3] + uy1) / 2;
                double[] best = null; double bestD = double.MaxValue;
                for (double bx = ux0; bx + z.w <= ux1 + 1e-9; bx += 5)
                    for (double by = uy1; by - z.h >= uy0 - 1e-9; by -= 5)
                    {
                        double dd = Math.Pow(bx + z.w / 2 - tx, 2) + Math.Pow(by - z.h / 2 - ty, 2);
                        if (dd >= bestD) continue;
                        bool ok = true;
                        foreach (var x in views)
                        {
                            var bb = BoxAt(x, z.at[x], k, bx, by);
                            if (bb[0] < ux0 || bb[1] < uy0 || bb[2] > ux1 || bb[3] > uy1 || obstacles.Any(o => Hit(bb, o, clear))) { ok = false; break; }
                        }
                        if (ok) { best = new[] { bx, by }; bestD = dd; }
                    }
                return best;
            };

            // scale: the largest standard one at which the measured block is predicted to fit, then checked for real
            // after setting it (labels and dimension texts keep their size, so the prediction can miss) — else the next
            // GOST 2.302: one scale per sheet, written in the title block — the sheet gets it and the views at it use the
            // sheet's scale (a view with its own scale is labelled "0.4:1" by SolidWorks); views at another ratio to the
            // main one keep their own
            void Apply(double sc)
            {
                var pr = sheet.GetProperties2() as double[];
                if (pr != null && pr.Length >= 7)
                    sheet.SetProperties2((int)pr[0], (int)pr[1], sc >= 1 ? sc : 1, sc >= 1 ? 1 : 1 / sc, pr[4] != 0, pr[5], pr[6], pr.Length >= 8 && pr[7] != 0);
                foreach (var x in views.Where(x => x.Role != "detail"))
                {
                    if (Math.Abs(orig[x] / cur - 1) < 1e-6 && pr != null) x.View.UseSheetScale = 1;
                    else x.View.ScaleDecimal = orig[x] * sc / cur;
                }
                // a new scale carries the dimensions out or in with the geometry — their spacing is set again
                if (arrange) ArrangeDimensions(d, views.Select(x => x.View));
                foreach (var x in views) x.Read();
            }
            double target = cur;
            double[] corner = null;
            if (fit == "keep") corner = place(1);
            else
            {
                double want = ParseScale(fit);
                if (want <= 0 && fit != "fit") throw new ToolFail("BAD_PARAMETER", $"scale '{fit}': fit, keep or a ratio such as 1:2, 2:1, 0.5.");
                // the first three predicted to fit: one that fails only by its labels is followed by a smaller one
                var candidates = want > 0 ? new[] { want } : StdScales.Where(sc => place(sc / cur) != null).Take(3).ToArray();
                if (candidates.Length == 0) candidates = new[] { StdScales.Last() };
                foreach (var sc in candidates)
                {
                    Apply(sc);
                    target = sc;
                    corner = place(1);
                    if (corner != null) break;
                }
            }
            var sz = size(1);
            bool fits = corner != null;
            corner = corner ?? new[] { ux0 + Math.Max(0, (ux1 - ux0 - sz.w) / 2), uy1 - Math.Max(0, (uy1 - uy0 - sz.h) / 2) };
            // columns left to right, rows top down (row -1 in first angle is above the main view)
            foreach (var x in views.Where(x => !x.View.PositionLocked))
                x.View.Position = new[] { (corner[0] + sz.at[x][0]) / MM, (corner[1] - sz.at[x][1]) / MM };
            // SolidWorks carries each view's label along (above the view, re-placed for a new scale) — proven by DXF export:
            // the label went +100/+50 with its view. Setting the label's position by API put it far off (94, 185), and a
            // SaveBMP picture shows labels late, so a picture is no proof of where they are.
            d.EditRebuild3();
            d.GraphicsRedraw2();

            // check what came out
            foreach (var x in views) x.Read();
            var outBoxes = views.Select(x => (x, b: x.Box)).ToList();
            var overlaps = new JArray();
            for (int i = 0; i < outBoxes.Count; i++)
                for (int j2 = i + 1; j2 < outBoxes.Count; j2++)
                    if (Hit(outBoxes[i].b, outBoxes[j2].b, -0.5))
                        overlaps.Add(outBoxes[i].x.View.Name + " / " + outBoxes[j2].x.View.Name);
            var off = new JArray(outBoxes.Where(o => o.b[0] < ux0 - 0.5 || o.b[1] < uy0 - 0.5 || o.b[2] > ux1 + 0.5 || o.b[3] > uy1 + 0.5
                                                    || obstacles.Any(ob => Hit(o.b, ob, -0.5)))
                                         .Select(o => o.x.View.Name));
            var res = new JObject
            {
                ["sheet_mm"] = new JArray(Math.Round(sw, 1), Math.Round(sh, 1)), ["projection"] = firstAngle ? "first angle" : "third angle",
                ["scale"] = ScaleText(target), ["fits"] = fits && off.Count == 0 && overlaps.Count == 0,
                ["views"] = new JArray(outBoxes.Select(o => new JObject
                {
                    ["view"] = o.x.View.Name, ["role"] = o.x.Role, ["scale"] = ScaleText(o.x.View.ScaleDecimal),
                    ["box_mm"] = new JArray(o.b.Select(v => Math.Round(v, 1)))
                }))
            };
            if (overlaps.Count > 0) res["overlaps"] = overlaps;
            if (unlinked.Count > 0) res["projection_link_removed"] = unlinked;
            if (locked.Count > 0) res["position_locked"] = locked;
            if (obstacles.Count > 1) res["sheet_annotations_avoided"] = obstacles.Count - 1;
            if (off.Count > 0) res["outside_or_on_title_block_or_notes"] = off;
            if (!fits) res["warning"] = "The views do not fit the sheet clear of the title block and notes even at " + ScaleText(target) + " — a larger sheet or fewer views.";
            return res;
        });

        // Boxes (mm) of the annotations on the sheet itself — technical requirements, the roughness sign of the rest …:
        // a note by its extent, anything else as a 24 mm square around its position
        private static IEnumerable<double[]> SheetAnnotationBoxes(IDrawingDoc dr)
        {
            var sheetView = dr.GetFirstView() as IView;
            object[] anns = null;
            try { anns = sheetView?.GetAnnotations() as object[]; } catch { }
            foreach (var o in anns ?? new object[0])
            {
                if (!(o is IAnnotation ann)) continue;
                double[] box = null;
                try
                {
                    if (ann.GetSpecificAnnotation() is INote note && note.GetExtent() is double[] e && e.Length >= 6)
                        box = new[] { Math.Min(e[0], e[3]) * MM, Math.Min(e[1], e[4]) * MM, Math.Max(e[0], e[3]) * MM, Math.Max(e[1], e[4]) * MM };
                    else if (ann.GetPosition() is double[] q && q.Length >= 2)
                        box = new[] { q[0] * MM - 12, q[1] * MM - 12, q[0] * MM + 12, q[1] * MM + 12 };
                }
                catch { }
                if (box != null) yield return box;
            }
        }

        // ---------------------------------------------------------------------------------------------- dimension
        // The pick tolerance is a few PIXELS: on a whole-sheet zoom two walls 1.25 mm apart (box4 at 1:4) picked the same
        // edge — so the window is zoomed to 6 mm round the point first.
        private static bool PickDrawingEntity(IModelDoc2 d, double xm, double ym, bool append)
        {
            try { d.ViewZoomTo2(xm - 0.003, ym - 0.003, 0, xm + 0.003, ym + 0.003, 0); } catch { }
            foreach (var kind in new[] { "EDGE", "SILHOUETTE", "SKETCHSEGMENT", "VERTEX", "SKETCHPOINT" })
                if (d.Extension.SelectByID2("", kind, xm, ym, 0, append, 0, null, 0)) return true;
            return false;
        }

        public ExecutionResponse AddDimensionBetween(ToolRequest request) => Run(request, true, p =>
        {
            var dr = ActiveDrawingOrFail(out var d);
            double x1 = PNum(p, "x1_mm", double.NaN), y1 = PNum(p, "y1_mm", double.NaN), x2 = PNum(p, "x2_mm", double.NaN), y2 = PNum(p, "y2_mm", double.NaN);
            if (new[] { x1, y1, x2, y2 }.Any(double.IsNaN)) throw new ToolFail("MISSING_PARAMETER", "x1_mm, y1_mm, x2_mm, y2_mm (points on the two entities) are required.");
            string orient = (PStr(p, "orientation", "auto") ?? "auto").ToLowerInvariant();
            double tx = PNum(p, "text_x_mm", double.NaN), ty = PNum(p, "text_y_mm", double.NaN);
            string inView = PStr(p, "view");
            if (inView != null)
            {
                // points in the view's own frame, model mm — what check_drawing(include_geometry) lists — onto the sheet
                var view = FindDrawingView(dr, inView) ?? throw new ToolFail("VIEW_NOT_FOUND", $"No view '{inView}'. Views: {string.Join(", ", DrawingViewNames(dr))}");
                if (!ViewModelFrameRaw(view, out var o, out var xd, out var yd)) throw new ToolFail("VIEW_FRAME", $"The frame of view '{inView}' could not be read.");
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                var m2v = view.ModelToViewTransform;
                double[] Sheet(double u, double v)
                {
                    var mp = new[] { o[0] + (u * xd[0] + v * yd[0]) / MM, o[1] + (u * xd[1] + v * yd[1]) / MM, o[2] + (u * xd[2] + v * yd[2]) / MM };
                    var s = ((mu.CreatePoint(mp) as IMathPoint)?.MultiplyTransform(m2v) as IMathPoint)?.ArrayData as double[];
                    return s == null ? throw new ToolFail("VIEW_FRAME", "A point could not be mapped onto the sheet.") : new[] { s[0] * MM, s[1] * MM };
                }
                var a = Sheet(x1, y1); var b = Sheet(x2, y2);
                if (!double.IsNaN(tx) && !double.IsNaN(ty)) { var t = Sheet(tx, ty); tx = t[0]; ty = t[1]; }
                x1 = a[0]; y1 = a[1]; x2 = b[0]; y2 = b[1];
            }
            var v1 = DrawingViews(dr).FirstOrDefault(v => OutlineContains(v, x1 / MM, y1 / MM));
            var v2 = DrawingViews(dr).FirstOrDefault(v => OutlineContains(v, x2 / MM, y2 / MM));
            if (v1 == null || v2 == null) throw new ToolFail("VIEW_NOT_FOUND", $"Point {(v1 == null ? 1 : 2)} is outside every view.");
            if (v1.Name != v2.Name) throw new ToolFail("BAD_PARAMETER", $"The points lie in different views ('{v1.Name}', '{v2.Name}') — a dimension joins entities of one view.");
            // text: outside the pair — above for a horizontal dimension, right for a vertical one
            if (double.IsNaN(tx)) tx = orient == "vertical" ? Math.Max(x1, x2) + 12 : (x1 + x2) / 2;
            if (double.IsNaN(ty)) ty = orient == "vertical" ? (y1 + y2) / 2 : Math.Max(y1, y2) + 12;

            using var prefs = new PrefScope(_solidWorks).Toggle(swUserPreferenceToggle_e.swInputDimValOnCreate, false);
            d.ClearSelection2(true);
            if (!PickDrawingEntity(d, x1 / MM, y1 / MM, false))
            {
                try { d.ViewZoomtofit2(); } catch { }
                throw new ToolFail("ENTITY_NOT_FOUND", $"Nothing to dimension at ({x1}, {y1}) mm — put the point on an edge, circle or vertex of the view.");
            }
            bool second = PickDrawingEntity(d, x2 / MM, y2 / MM, true);
            try { d.ViewZoomtofit2(); } catch { }
            if (!second) { d.ClearSelection2(true); throw new ToolFail("ENTITY_NOT_FOUND", $"Nothing to dimension at ({x2}, {y2}) mm."); }
            if ((d.SelectionManager as ISelectionMgr)?.GetSelectedObjectCount2(-1) < 2)
            {
                d.ClearSelection2(true);
                throw new ToolFail("SAME_ENTITY", "Both points picked the same entity — use add_drawing_dimension for one entity's size.");
            }
            object dd;
            switch (orient)
            {
                case "horizontal": dd = d.AddHorizontalDimension2(tx / MM, ty / MM, 0); break;
                case "vertical": dd = d.AddVerticalDimension2(tx / MM, ty / MM, 0); break;
                case "auto": case "aligned": dd = d.AddDimension2(tx / MM, ty / MM, 0); break;
                default: throw new ToolFail("BAD_PARAMETER", "orientation: auto | horizontal | vertical.");
            }
            d.ClearSelection2(true);
            var disp = dd as IDisplayDimension ?? throw new ToolFail("DIMENSION_FAILED", "SolidWorks made no dimension for this pair (e.g. parallel lines asked for a vertical distance).");
            var dim = disp.GetDimension2(0) as IDimension;
            double val = 0; try { val = dim?.SystemValue ?? 0; } catch { }
            bool ang = dim != null && IsAngular(dim);
            d.GraphicsRedraw2();
            return new JObject
            {
                ["view"] = v1.Name, ["name"] = dim?.FullName, ["orientation"] = orient,
                ["points_mm"] = new JArray(new JArray(Math.Round(x1, 2), Math.Round(y1, 2)), new JArray(Math.Round(x2, 2), Math.Round(y2, 2))),
                [ang ? "value_deg" : "value_mm"] = Math.Round(ang ? val * 180 / Math.PI : val * MM, 4),
                ["text_mm"] = new JArray(Math.Round(tx, 1), Math.Round(ty, 1))
            };
        });
    }
}
