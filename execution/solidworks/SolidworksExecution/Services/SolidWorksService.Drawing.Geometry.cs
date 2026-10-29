using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: a drawing view's projected geometry (lines, arcs, circles from GetPolylines7),
    // its extent and the relations between primitives (concentric, equal diameter, tangent) — no COM writes.
    public partial class SolidWorksService
    {
        // Read a drawing view's PROJECTED 2D geometry as clean primitives — the "clean shape" needed to
        // reverse-engineer a part from its drawing WITHOUT depending on a cluttered raster (dimension lines
        // overlapping the geometry). Source: IView.GetPolylines7 (the only view-geometry getter that returns
        // data here — GetLines3/GetArcs4/GetSplines3 come back empty).
        // UpdateViewDisplayGeometry() is called first to force the display geometry to compute.
        //
        // GetPolylines7 returns a flat double[] of records; each record = [header][N][N*(x,y,z)], coords in
        // MODEL-scale meters CENTERED on the view centroid (z==0, planar). Decoded empirically:
        //  - straight edges: an 8-double header [0,0,0,0, nx,ny,nz, 0] then N=2 then 2 points -> a LINE.
        //    Extracted by a desync-proof SIGNATURE SCAN (cylindrical-wall silhouettes come out here — this is
        //    where a revolve profile's UP/DOWN steps live, the info a dimension value can't carry).
        //  - curved edges (fillets/chamfers, and the circular boundaries of annular faces): tessellated
        //    multi-point runs, captured best-effort as {start, mid, end} via a tolerant walk.
        // Raw primitive holders for one view's extracted geometry. Values are UNROUNDED —
        // relations/stations/attachment compute on them and round only at emit time. Ids are positional
        // and documented in the contract: l<i> = lines[i], a<i> = curves[i], c<i> = circles[i].
        private class LineRec { public double X1, Y1, X2, Y2; }
        private class CurveRec
        {
            public int N;
            public double SX, SY, MX, MY, EX, EY;   // start / mid / end tessellation points
            public bool HasCenter;
            public double CX, CY, R;
        }
        private class CircleRec { public double CX, CY, R; }
        private class ViewGeomData
        {
            public readonly List<LineRec> Lines = new List<LineRec>();
            public readonly List<CurveRec> Curves = new List<CurveRec>();
            public readonly List<CircleRec> Circles = new List<CircleRec>();
            public string Err;
        }

        private ViewGeomData ExtractViewGeometry(IView view)
        {
            var g = new ViewGeomData();
            try { view.UpdateViewDisplayGeometry(); } catch { }
            double[] arr;
            try { object polys; view.GetPolylines7((short)0, out polys); arr = polys as double[]; }
            catch (Exception ex) { g.Err = ex.Message; return g; }
            int nlen = (arr == null) ? 0 : arr.Length;

            // Hidden-edge filtering (ORTHO views only). GetPolylines7 returns visible AND obscured
            // (hidden) edges; each record's 6-double "display block" immediately before its point-count
            // distinguishes them — VISIBLE carries [.,.,-1,1,-1,0], HIDDEN (obscured) carries
            // [2,1,1,0,-1,0]. Pinned empirically on the 'test' cube (Ø25 blind hole from the top: its
            // hidden walls + bottom circle) and confirmed 100% clean across level-2-2's lines+curves via
            // an A/B against a hand-cleaned copy (2026-07-19): every record classified, zero ambiguous.
            // Ortho views must not leak hidden edges — a hidden bore's circle otherwise reads as a phantom
            // feature section and the model mis-reconstructs (run #4/#5 "hidden-line confusion"). Section
            // views (Type==2 / owning an IDrSection) are EXEMPT: the cut face is visible geometry and, as
            // the A/B showed, carries no hidden records anyway. Hidden straight LINES were already dropped
            // (the line-scan below requires the visible display block d0==d1==0); this adds the same
            // discipline to the curve walk, which previously kept obscured arcs/circles.
            bool _isSection = false;
            try { _isSection = (view.Type == 2); } catch { }
            if (!_isSection) { try { _isSection = (view.GetSection() != null); } catch { } }
            bool filterHidden = !_isSection;

            // Line-record signature: arr[i..i+3]==0, arr[i+7]==0, arr[i+8]==2.0, then two points. Each point
            // is (x, y, depth); the 3rd coord is the view DEPTH — CONSTANT per record (0 for some views,
            // non-zero for others), not necessarily 0. Require the two points to share the same depth.
            for (int i = 0; i + 15 <= nlen;)
            {
                if (Z(arr[i]) && Z(arr[i + 1]) && Z(arr[i + 2]) && Z(arr[i + 3]) && Z(arr[i + 7])
                    && arr[i + 8] == 2.0 && Math.Abs(arr[i + 11] - arr[i + 14]) < 1e-6 && InR(arr[i + 11])
                    && InR(arr[i + 9]) && InR(arr[i + 10]) && InR(arr[i + 12]) && InR(arr[i + 13]))
                {
                    g.Lines.Add(new LineRec { X1 = arr[i + 9], Y1 = arr[i + 10], X2 = arr[i + 12], Y2 = arr[i + 13] });
                    i += 15;
                }
                else i++;
            }

            // Curves: tolerant walk for N>=3 point-runs (tessellated arcs/circles). Every run gets a
            // fitted CENTER + RADIUS (circumcenter of three spread tessellation points — exact for any
            // circular arc, independent of tessellation uniformity). A CLOSED run (start == end) is a
            // FULL CIRCLE and goes to Circles as {cx, cy, r} — explicit, so a reader never has
            // to decode the "x1=+r, xm=-r" start/mid/end pattern (the Sonnet benchmark read that pattern
            // as "the tool doesn't report centers" and mis-placed every bore). Open runs stay in Curves
            // as {n, start, mid, end} + fitted center.
            // Points are (x, y, depth); require all points in a run to share one depth (planar) — this is
            // the validity filter (a header's stray values are not consistently planar).
            for (int j = 0; j < nlen - 1;)
            {
                int N = -1, basep = -1;
                for (int h = 0; h <= 13 && j + h < nlen; h++)
                {
                    double Nd = arr[j + h];
                    if (Nd != Math.Floor(Nd)) continue;
                    int Ni = (int)Nd;
                    if (Ni < 3 || Ni > 500) continue;
                    int b = j + h + 1;
                    if (b + 3 * Ni > nlen) continue;
                    double depth = arr[b + 2];
                    if (!InR(depth)) continue;
                    bool ok = true;
                    for (int k = 0; k < Ni; k++)
                        if (Math.Abs(arr[b + 3 * k + 2] - depth) > 1e-6 || !InR(arr[b + 3 * k]) || !InR(arr[b + 3 * k + 1])) { ok = false; break; }
                    if (ok) { N = Ni; basep = b; break; }
                }
                if (basep < 0) { j++; continue; }
                // Drop hidden (obscured) curves/circles from ortho views. The 6-double display block sits
                // immediately before the point-count (which is at basep-1), i.e. arr[basep-7 .. basep-2];
                // HIDDEN == [2,1,1,0,-1,0]. Matching the FULL signature is fail-safe — the VISIBLE block
                // [.,.,-1,1,-1,0] differs in the first field, so a visible edge is never dropped.
                if (filterHidden && basep >= 7
                    && Math.Abs(arr[basep - 7] - 2) < 0.5 && Math.Abs(arr[basep - 6] - 1) < 0.5
                    && Math.Abs(arr[basep - 5] - 1) < 0.5 && Math.Abs(arr[basep - 4]) < 0.5
                    && Math.Abs(arr[basep - 3] + 1) < 0.5 && Math.Abs(arr[basep - 2]) < 0.5)
                {
                    j = basep + 3 * N;   // hidden run — skip entirely
                    continue;
                }
                int mid = N / 2;
                double sx = arr[basep], sy = arr[basep + 1];
                double ex = arr[basep + 3 * (N - 1)], ey = arr[basep + 3 * (N - 1) + 1];
                // Center fit from three spread points (0, N/3, 2N/3) — for a closed run the endpoints
                // coincide, so start/mid/end would be only two distinct points; the spread triple is
                // always three distinct points on the arc.
                int i1 = N / 3, i2 = (2 * N) / 3;
                double[] cc = Circumcenter(
                    sx, sy,
                    arr[basep + 3 * i1], arr[basep + 3 * i1 + 1],
                    arr[basep + 3 * i2], arr[basep + 3 * i2 + 1]);
                bool closed = Math.Abs(sx - ex) < 1e-6 && Math.Abs(sy - ey) < 1e-6;
                if (closed && cc != null)
                {
                    g.Circles.Add(new CircleRec { CX = cc[0], CY = cc[1], R = cc[2] });
                }
                else
                {
                    var cr = new CurveRec
                    {
                        N = N,
                        SX = sx, SY = sy,
                        MX = arr[basep + 3 * mid], MY = arr[basep + 3 * mid + 1],
                        EX = ex, EY = ey
                    };
                    if (cc != null) { cr.HasCenter = true; cr.CX = cc[0]; cr.CY = cc[1]; cr.R = cc[2]; }
                    g.Curves.Add(cr);
                }
                j = basep + 3 * N;
            }
            return g;
        }

        // Emit the extracted geometry in the contract shape: lines / curves /
        // circles / frame, all R6-rounded.
        private JObject BuildGeometryJson(ViewGeomData g, IView view)
        {
            var gj = new JObject();
            if (g.Err != null) { gj["err"] = g.Err; return gj; }

            var lines = new JArray();
            foreach (var l in g.Lines)
                lines.Add(new JObject { ["x1"] = R6(l.X1), ["y1"] = R6(l.Y1), ["x2"] = R6(l.X2), ["y2"] = R6(l.Y2) });

            var curves = new JArray();
            foreach (var c in g.Curves)
            {
                var cj = new JObject();
                cj["n"] = c.N;
                cj["x1"] = R6(c.SX); cj["y1"] = R6(c.SY);
                cj["xm"] = R6(c.MX); cj["ym"] = R6(c.MY);
                cj["x2"] = R6(c.EX); cj["y2"] = R6(c.EY);
                if (c.HasCenter) { cj["cx"] = R6(c.CX); cj["cy"] = R6(c.CY); cj["r"] = R6(c.R); }
                curves.Add(cj);
            }

            var circles = new JArray();
            foreach (var c in g.Circles)
                circles.Add(new JObject { ["cx"] = R6(c.CX), ["cy"] = R6(c.CY), ["r"] = R6(c.R) });

            gj["lines"] = lines;
            gj["curves"] = curves;
            gj["circles"] = circles;
            // Model-space frame: p_model = origin + x*xdir + y*ydir for every coordinate above (and for
            // the circle centers) — the deterministic view-2D → model-3D mapping. Falls back to the
            // legacy prose note when the view exposes no transform (e.g. the sheet container).
            var vfr = ViewModelFrame(view);
            if (vfr != null)
            {
                gj["frame"] = vfr;
                var ext = ComputeViewExtent(g, view);
                if (ext != null) gj["extent"] = ext;
            }
            else gj["frame"] = "model-scale meters (x,y in the view plane), centered on view centroid";
            return gj;
        }

        // the view's MODEL-SPACE span along its two resolved axes, computed SERVER-SIDE over
        // ALL extracted primitives with the frame's component SIGNS applied — so "is there material
        // beyond z=0.045 in this view?" is a field read, not client-side frame arithmetic (benchmark
        // run #2 dropped ydir's sign and read the z∈[0.045,0.065] loft silhouette as absent). Exact:
        // circles contribute center±r, arcs contribute endpoints + any axis-extreme point that lies
        // within their angular span. Omitted (loudly absent) when the frame is not axis-aligned or the
        // view has no primitives.
        private JObject ComputeViewExtent(ViewGeomData g, IView view)
        {
            double[] o, x, y;
            if (!ViewModelFrameRaw(view, out o, out x, out y)) return null;
            string axX = PrincipalAxis(x[0], x[1], x[2]);
            string axY = PrincipalAxis(y[0], y[1], y[2]);
            if (axX == null || axY == null) return null;
            int kx = AxisIndex(axX), ky = AxisIndex(axY);

            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] mx = { double.MinValue, double.MinValue, double.MinValue };
            bool any = false;
            Action<double, double> add = (u, v) =>
            {
                foreach (int k in new int[] { kx, ky })
                {
                    double val = o[k] + u * x[k] + v * y[k];
                    if (val < mn[k]) mn[k] = val;
                    if (val > mx[k]) mx[k] = val;
                }
                any = true;
            };

            foreach (var l in g.Lines) { add(l.X1, l.Y1); add(l.X2, l.Y2); }
            foreach (var c in g.Circles)
            {
                add(c.CX - c.R, c.CY); add(c.CX + c.R, c.CY);
                add(c.CX, c.CY - c.R); add(c.CX, c.CY + c.R);
            }
            foreach (var a in g.Curves)
            {
                add(a.SX, a.SY); add(a.MX, a.MY); add(a.EX, a.EY);
                if (a.HasCenter)
                {
                    // Axis-extreme points of the arc's circle, included only when ON the arc.
                    double[][] cand = {
                        new double[] { a.CX - a.R, a.CY }, new double[] { a.CX + a.R, a.CY },
                        new double[] { a.CX, a.CY - a.R }, new double[] { a.CX, a.CY + a.R } };
                    foreach (var p in cand)
                        if (AngOnArc(a, p[0], p[1])) add(p[0], p[1]);
                }
            }
            if (!any) return null;

            var ext = new JObject();
            // Canonical X→Y→Z key order.
            foreach (int k in kx < ky ? new int[] { kx, ky } : new int[] { ky, kx })
            {
                string ax = k == 0 ? "X" : (k == 1 ? "Y" : "Z");
                ext[ax] = new JArray { R6(mn[k]), R6(mx[k]) };
            }
            return ext;
        }

        // Circumcenter + radius of the circle through three 2D points; null when degenerate (collinear).
        private static double[] Circumcenter(double ax, double ay, double bx, double by, double cx, double cy)
        {
            double dd = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(dd) < 1e-12) return null;
            double a2 = ax * ax + ay * ay, b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / dd;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / dd;
            double r = Math.Sqrt((ux - ax) * (ux - ax) + (uy - ay) * (uy - ay));
            return new double[] { ux, uy, r };
        }

        private static bool Z(double v) { return Math.Abs(v) < 1e-6; }
        private static bool InR(double v) { return Math.Abs(v) < 2.0; }

        // ====================================================================================
        // Deterministic RELATIONS with PROVENANCE + cross-view STATION join keys.
        //
        // Design rules (Observation-Graph review 2026-07-16, approved subset):
        //  - GROUP form only, never pairwise dumps.
        //  - Every relation/group carries `source` (a slug from the CLOSED enum documented in
        //    tool-schemas.json) + `residual` (max measured deviation, meters, 6dp). A relation that
        //    cannot name a deterministic source is structurally impossible to emit — the detector
        //    itself assigns the slug. No confidence scores, no inference fields.
        //  - Cross-view output = join keys + candidate sets. NEVER "same physical entity".
        //  - All math runs on RAW (unrounded) extracted values; rounding happens at emit time only,
        //    so `residual` reports the true deviation.
        //  - touches = junction contacts only (shared_endpoint + endpoint_on_curve). X-crossings of
        //    projected edges are DELIBERATELY not emitted: a 2D projection crossing carries no
        //    same-depth guarantee, so emitting it would invite a false "contact" reading.
        // ====================================================================================

        // Cluster / match tolerance: 10 µm at model scale. Payload resolution is 1 µm (6dp); the
        // circumcenter fit is exact for true circular arcs (sub-µm noise); genuinely distinct design
        // geometry sits ≥ 100 µm apart — two orders of margin on both sides.
        private const double RelTol = 1e-5;

        // Minimal union-find for the clustering passes.
        private class UF
        {
            private readonly int[] _p;
            public UF(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
            public int Find(int i) { while (_p[i] != i) { _p[i] = _p[_p[i]]; i = _p[i]; } return i; }
            public void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) _p[ra] = rb; }
        }

        private static double Dist2D(double ax, double ay, double bx, double by)
        {
            double dx = ax - bx, dy = ay - by;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // Sort primitive ids deterministically: kind rank c < a < l, then numeric index.
        private static void SortPrimitiveIds(List<string> ids)
        {
            ids.Sort((x, y) =>
            {
                int rx = x[0] == 'c' ? 0 : (x[0] == 'a' ? 1 : 2);
                int ry = y[0] == 'c' ? 0 : (y[0] == 'a' ? 1 : 2);
                if (rx != ry) return rx.CompareTo(ry);
                int ix, iy;
                int.TryParse(x.Substring(1), out ix);
                int.TryParse(y.Substring(1), out iy);
                return ix.CompareTo(iy);
            });
        }

        // Is the point (px,py) — assumed to lie ON the arc's circle — within the arc's swept span?
        // The arc runs start → mid → end; mid disambiguates the direction. Angular tolerance is the
        // linear RelTol mapped through the radius.
        private static bool AngOnArc(CurveRec a, double px, double py)
        {
            if (!a.HasCenter || a.R < 1e-9) return false;
            double TwoPi = 2.0 * Math.PI;
            double aS = Math.Atan2(a.SY - a.CY, a.SX - a.CX);
            double aM = Math.Atan2(a.MY - a.CY, a.MX - a.CX);
            double aE = Math.Atan2(a.EY - a.CY, a.EX - a.CX);
            double aP = Math.Atan2(py - a.CY, px - a.CX);
            double ccw = ((aE - aS) % TwoPi + TwoPi) % TwoPi;
            double posM = ((aM - aS) % TwoPi + TwoPi) % TwoPi;
            double posP = ((aP - aS) % TwoPi + TwoPi) % TwoPi;
            double tolA = Math.Min(0.5, RelTol / a.R);
            if (posM <= ccw)   // CCW arc from S to E
                return posP <= ccw + tolA || posP >= TwoPi - tolA;
            // CW arc: swept region is [ccw, 2π] (in CCW-offset terms)
            return posP >= ccw - tolA || posP <= tolA;
        }

        // Perpendicular foot of (px,py) on the INFINITE line p1→p2. Returns false when degenerate.
        private static bool LinePerpFoot(double px, double py, double x1, double y1, double x2, double y2,
            out double t, out double fx, out double fy, out double perpDist)
        {
            t = 0; fx = 0; fy = 0; perpDist = 0;
            double dx = x2 - x1, dy = y2 - y1;
            double l2 = dx * dx + dy * dy;
            if (l2 < 1e-18) return false;
            t = ((px - x1) * dx + (py - y1) * dy) / l2;
            fx = x1 + t * dx; fy = y1 + t * dy;
            perpDist = Dist2D(px, py, fx, fy);
            return true;
        }

        // Distance from (px,py) to the SEGMENT p1→p2 (clamped), with the unclamped param out.
        private static double SegDist(double px, double py, double x1, double y1, double x2, double y2, out double tRaw)
        {
            double t, fx, fy, pd;
            if (!LinePerpFoot(px, py, x1, y1, x2, y2, out t, out fx, out fy, out pd))
            { tRaw = 0; return Dist2D(px, py, x1, y1); }
            tRaw = t;
            double tc = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Dist2D(px, py, x1 + tc * (x2 - x1), y1 + tc * (y2 - y1));
        }

        // ---- Per-view relation groups: concentric, equal_diameter, tangent ----
        // (touches was dropped 2026-07-19: fully redundant with `tangent` for corner/junction identification.)
        private JObject ComputeViewRelations(ViewGeomData g)
        {
            // centered items: circles first, then arcs with a fitted center — ids c<i> / a<i>
            var items = g.Circles.Select((c, i) => new CenteredItem { CX = c.CX, CY = c.CY, R = c.R, Id = "c" + i })
                .Concat(g.Curves.Select((c, i) => new { c, i }).Where(t => t.c.HasCenter)
                    .Select(t => new CenteredItem { CX = t.c.CX, CY = t.c.CY, R = t.c.R, Id = "a" + t.i, Arc = t.c }))
                .ToList();
            var rel = new JObject();
            var concentric = ConcentricRelations(items);
            if (concentric.Count > 0) rel["concentric"] = concentric;
            var equalDia = EqualDiameterRelations(g.Circles);
            if (equalDia.Count > 0) rel["equal_diameter"] = equalDia;
            var tangent = TangentRelations(items, g.Lines);
            if (tangent.Count > 0) rel["tangent"] = tangent;
            return rel.Count > 0 ? rel : null;   // empty buckets dropped
        }

        // A full circle (Arc null) or an arc with a fitted center, as the relation families see it.
        private sealed class CenteredItem
        {
            public double CX, CY, R;
            public string Id;
            public CurveRec Arc;
        }

        // Groups of ≥2 indices joined pairwise by `joined` (union-find), each in index order, ordered by first member.
        private static List<List<int>> JoinedGroups(int n, Func<int, int, bool> joined)
        {
            var uf = new UF(Math.Max(n, 1));
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (joined(i, j)) uf.Union(i, j);
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = uf.Find(i);
                if (!groups.ContainsKey(r)) groups[r] = new List<int>();
                groups[r].Add(i);
            }
            var ordered = groups.Values.Where(grp => grp.Count >= 2).ToList();
            ordered.Sort((a, b) => a[0].CompareTo(b[0]));
            return ordered;
        }

        // concentric: a shared center within RelTol.
        private static JArray ConcentricRelations(List<CenteredItem> items)
        {
            var concentric = new JArray();
            foreach (var grp in JoinedGroups(items.Count, (i, j) => Dist2D(items[i].CX, items[i].CY, items[j].CX, items[j].CY) <= RelTol))
            {
                double mx = grp.Average(i => items[i].CX), my = grp.Average(i => items[i].CY);
                double resid = grp.Max(i => Dist2D(items[i].CX, items[i].CY, mx, my));
                var radii = grp.Select(i => items[i].R).OrderBy(r => r).ToList();
                var mids = grp.Select(i => items[i].Id).ToList();
                SortPrimitiveIds(mids);
                concentric.Add(new JObject
                {
                    ["members"] = new JArray(mids.ToArray()),
                    ["center"] = new JArray { R6(mx), R6(my) },
                    ["radii"] = new JArray(radii.Select(r => (object)R6(r)).ToArray()),
                    ["source"] = "center_coincide",
                    ["residual"] = R6(resid)
                });
            }
            return concentric;
        }

        // equal_diameter: full CIRCLES of the same r, with at least one pair of genuinely distinct centers.
        private static JArray EqualDiameterRelations(List<CircleRec> circles)
        {
            var equalDia = new JArray();
            foreach (var grp in JoinedGroups(circles.Count, (i, j) => Math.Abs(circles[i].R - circles[j].R) <= RelTol))
            {
                bool distinct = grp.Any(a => grp.Any(b => Dist2D(circles[a].CX, circles[a].CY, circles[b].CX, circles[b].CY) > RelTol));
                if (!distinct) continue;
                double mr = grp.Average(i => circles[i].R);
                equalDia.Add(new JObject
                {
                    ["members"] = new JArray(grp.Select(i => (object)("c" + i)).ToArray()),
                    ["r"] = R6(mr),
                    ["centers"] = new JArray(grp.Select(i => (object)new JArray { R6(circles[i].CX), R6(circles[i].CY) }).ToArray()),
                    ["source"] = "radius_equal",
                    ["residual"] = R6(grp.Max(i => Math.Abs(circles[i].R - mr)))
                });
            }
            return equalDia;
        }

        // tangent: circle/arc ↔ circle/arc (center distance = r1+r2 external / |r1−r2| internal) and line ↔ circle/arc
        // (perpendicular distance = r, foot inside the segment span); an arc only where the touch point lies on it.
        private static JArray TangentRelations(List<CenteredItem> items, List<LineRec> lines)
        {
            var tangent = new JArray();
            void Add(string a, string b, double px, double py, string source, double residual)
            {
                var mids = new List<string> { a, b };
                SortPrimitiveIds(mids);
                tangent.Add(new JObject
                {
                    ["members"] = new JArray(mids.ToArray()),
                    ["at"] = new JArray { R6(px), R6(py) },
                    ["source"] = source,
                    ["residual"] = R6(residual)
                });
            }
            bool OnArc(CenteredItem it, double px, double py) => it.Arc == null || AngOnArc(it.Arc, px, py);

            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    var a = items[i]; var b = items[j];
                    double d = Dist2D(a.CX, a.CY, b.CX, b.CY);
                    if (d <= RelTol) continue;   // concentric — its own family
                    double devExt = Math.Abs(d - (a.R + b.R));
                    double devInt = Math.Abs(d - Math.Abs(a.R - b.R));
                    bool ext = devExt <= RelTol && devExt <= devInt;
                    bool intr = !ext && devInt <= RelTol && Math.Abs(a.R - b.R) > RelTol;
                    if (!ext && !intr) continue;
                    double px = a.CX + (b.CX - a.CX) * (a.R / d), py = a.CY + (b.CY - a.CY) * (a.R / d);
                    if (!OnArc(a, px, py) || !OnArc(b, px, py)) continue;
                    Add(a.Id, b.Id, px, py, ext ? "dist_eq_radius_sum" : "dist_eq_radius_diff", ext ? devExt : devInt);
                }
            for (int li = 0; li < lines.Count; li++)
            {
                var l = lines[li];
                double segLen = Dist2D(l.X1, l.Y1, l.X2, l.Y2);
                if (segLen < 1e-12) continue;
                double tolT = RelTol / segLen;
                foreach (var it in items)
                {
                    if (!LinePerpFoot(it.CX, it.CY, l.X1, l.Y1, l.X2, l.Y2, out double t, out double fx, out double fy, out double pd))
                        continue;
                    if (pd <= RelTol) continue;   // line through the center — not tangency
                    double dev = Math.Abs(pd - it.R);
                    if (dev > RelTol || t < -tolT || t > 1 + tolT) continue;
                    double px = it.CX + (fx - it.CX) * (it.R / pd), py = it.CY + (fy - it.CY) * (it.R / pd);
                    if (!OnArc(it, px, py)) continue;
                    Add(it.Id, "l" + li, px, py, "line_dist_eq_radius", dev);
                }
            }
            return tangent;
        }

        private static int AxisIndex(string ax) { return ax == "X" ? 0 : (ax == "Y" ? 1 : 2); }

        // ---- Dimension → primitive attachment ----
        // Each MODEL-space anchor is mapped into the view's 2D frame (u=(p−o)·xdir, v=(p−o)·ydir) and
        // matched against the extracted primitives arithmetically. `measures` lists ALL primitives any
        // anchor resolves to (a corner anchor legitimately touches two lines — candidates stay visible);
        // `measure_src` = anchor_at_center when any anchor sits on a circle/arc center (the Ø/R shape),
        // else anchor_on_primitive; `measure_residual` = the worst contributing deviation. No resolvable
        // primitive at all ⇒ `unattached: true` — loud, never dropped, never guessed.
        private void AttachDimensionToPrimitives(JObject dj, JArray anchors, ViewGeomData g,
            double[] O, double[] X, double[] Y)
        {
            if (anchors == null || anchors.Count == 0) { dj["unattached"] = true; return; }
            var matched = new List<string>();
            bool anyCenter = false;
            double maxResid = 0;
            int unmatchedAnchors = 0;

            foreach (var aTok in anchors)
            {
                var pa = aTok as JArray;
                if (pa == null || pa.Count < 3) continue;
                double px = (double)pa[0], py = (double)pa[1], pz = (double)pa[2];
                double dx = px - O[0], dy = py - O[1], dz = pz - O[2];
                double u = dx * X[0] + dy * X[1] + dz * X[2];
                double v = dx * Y[0] + dy * Y[1] + dz * Y[2];
                bool any = false;

                for (int i = 0; i < g.Circles.Count; i++)
                {
                    double dc = Dist2D(u, v, g.Circles[i].CX, g.Circles[i].CY);
                    if (dc <= RelTol)
                    { AddMatch(matched, "c" + i, dc, ref maxResid); anyCenter = true; any = true; }
                    else if (Math.Abs(dc - g.Circles[i].R) <= RelTol)
                    { AddMatch(matched, "c" + i, Math.Abs(dc - g.Circles[i].R), ref maxResid); any = true; }
                }
                for (int i = 0; i < g.Curves.Count; i++)
                {
                    var a = g.Curves[i];
                    if (a.HasCenter)
                    {
                        double dc = Dist2D(u, v, a.CX, a.CY);
                        if (dc <= RelTol)
                        { AddMatch(matched, "a" + i, dc, ref maxResid); anyCenter = true; any = true; continue; }
                        double dev = Math.Abs(dc - a.R);
                        if (dev <= RelTol && AngOnArc(a, u, v))
                        { AddMatch(matched, "a" + i, dev, ref maxResid); any = true; continue; }
                    }
                    if (Dist2D(u, v, a.SX, a.SY) <= RelTol || Dist2D(u, v, a.EX, a.EY) <= RelTol)
                    { AddMatch(matched, "a" + i, 0, ref maxResid); any = true; }
                }
                for (int i = 0; i < g.Lines.Count; i++)
                {
                    var l = g.Lines[i];
                    double tRaw;
                    double d = SegDist(u, v, l.X1, l.Y1, l.X2, l.Y2, out tRaw);
                    if (d <= RelTol) { AddMatch(matched, "l" + i, d, ref maxResid); any = true; }
                }
                if (!any) unmatchedAnchors++;
            }

            if (matched.Count > 0)
            {
                SortPrimitiveIds(matched);
                dj["measures"] = new JArray(matched.ToArray());
                dj["measure_src"] = anyCenter ? "anchor_at_center" : "anchor_on_primitive";
                dj["measure_residual"] = R6(maxResid);
                if (unmatchedAnchors > 0) dj["unmatched_anchors"] = unmatchedAnchors;
            }
            else dj["unattached"] = true;
        }

        private static void AddMatch(List<string> matched, string id, double residual, ref double maxResid)
        {
            if (!matched.Contains(id)) matched.Add(id);
            maxResid = Math.Max(maxResid, residual);
        }
    }
}
