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
    // ===================== Sketch definition: blue → black without moving anything =====================
    // define_sketch    — a fully defined sketch from the geometry as it is: relations the geometry already satisfies
    //                    exactly (horizontal / vertical lines, arcs on one centre, line-arc tangency, a point on the
    //                    origin), the caller's design dimensions at their CURRENT values, the profile tied to the origin,
    //                    then SolidWorks' Fully Define Sketch for the rest. The shape is compared with a snapshot after
    //                    every step; a step that moved anything is undone and reported — the tool never changes the shape.
    //                    Checked live on SW 2024: RelationManager.AddRelation faults (relations go by selection), Fully
    //                    Define leaves a profile floating without the origin tie, an EditUndo2 after a driven dimension
    //                    made the next close fault (it is deleted instead), an arc drawn in two halves stays blue.
    // WithSketchStatus — "sketch_status": "Sketch1: under_defined, 3 free segments" in the answer of every mutating call
    //                    that worked in a sketch (still open, or just consumed by a feature).
    public partial class SolidWorksService
    {
        private const double ExactTol = 1e-8;   // m — a relation the geometry "already satisfies"

        // Relations Fully Define Sketch may add: the ones that state what the geometry is (no equal / parallel /
        // collinear / midpoint — those couple entities that only happen to match, against the design intent).
        private const int AutoRelations = (int)(swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Horizontal
                                              | swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Vertical
                                              | swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Coincident
                                              | swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Tangent
                                              | swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Concentric
                                              | swSketchFullyDefineRelationType_e.swSketchFullyDefineRelationType_Perpendicular);

        private static string SketchStatusName(int s)
        {
            switch ((swConstrainedStatus_e)s)
            {
                case swConstrainedStatus_e.swUnderConstrained: return "under_defined";
                case swConstrainedStatus_e.swFullyConstrained: return "fully_defined";
                case swConstrainedStatus_e.swOverConstrained: return "over_defined";
                case swConstrainedStatus_e.swNoSolution: return "no_solution";
                case swConstrainedStatus_e.swInvalidSolution: return "invalid_solution";
                case swConstrainedStatus_e.swAutosolveOff: return "autosolve_off";
                default: return "unknown";
            }
        }

        private static int SketchStatus(ISketch sk)
        {
            try { return sk.GetConstrainedStatus(); } catch { return (int)swConstrainedStatus_e.swUnknownConstraint; }
        }

        private static List<ISketchSegment> SketchSegments(ISketch sk) =>
            ((sk.GetSketchSegments() as object[]) ?? new object[0]).OfType<ISketchSegment>().ToList();

        private static List<ISketchPoint> SketchPoints(ISketch sk) =>
            ((sk.GetSketchPoints2() as object[]) ?? new object[0]).OfType<ISketchPoint>().ToList();

        // "Sketch1: under_defined, 3 free segments" — null for an empty sketch.
        private static string SketchStatusText(ISketch sk, string name)
        {
            var segs = SketchSegments(sk);
            if (segs.Count == 0) return null;
            int st = SketchStatus(sk);
            string text = $"{name}: {SketchStatusName(st)}";
            if (st == (int)swConstrainedStatus_e.swUnderConstrained)
            {
                int free = segs.Count(s => { try { return s.Status == (int)swConstrainedStatus_e.swUnderConstrained; } catch { return false; } });
                if (free > 0) text += $", {free} free segment{(free == 1 ? "" : "s")}";
            }
            return text;
        }

        // Under-defined segments as "line 0,80 → 120,80" / "circle 20,20 r5" (sketch frame, mm) — what is still free.
        private static JArray FreeSegments(ISketch sk, int max = 12)
        {
            Func<double, string> f = v => Math.Round(v * MM, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var list = new JArray();
            foreach (var s in SketchSegments(sk))
            {
                int st; try { st = s.Status; } catch { continue; }
                if (st != (int)swConstrainedStatus_e.swUnderConstrained) continue;
                if (list.Count == max) { list.Add("…"); break; }
                string text;
                if (s is ISketchLine ln && ln.GetStartPoint2() is ISketchPoint a && ln.GetEndPoint2() is ISketchPoint b)
                    text = $"line {f(a.X)},{f(a.Y)} → {f(b.X)},{f(b.Y)}";
                else if (s is ISketchArc arc && arc.GetCenterPoint2() is ISketchPoint c)
                    text = $"{(arc.IsCircle() == 1 ? "circle" : "arc")} {f(c.X)},{f(c.Y)} r{f(arc.GetRadius())}";
                else text = ((swSketchSegments_e)s.GetType()).ToString().Replace("swSketch", "").ToLowerInvariant();
                list.Add(s.ConstructionGeometry ? text + " (construction)" : text);
            }
            return list;
        }

        // Sketch points that end or centre nothing and carry no relation — a free point leaves the sketch blue with no free
        // segment to show (live: the centre point SolidWorks 2024 puts where a corner rectangle's construction diagonals
        // cross).
        private static List<ISketchPoint> LonePoints(ISketch sk)
        {
            var used = new HashSet<string>();
            foreach (var s in SketchSegments(sk))
            {
                object[] pts = s is ISketchLine ln ? new object[] { ln.GetStartPoint2(), ln.GetEndPoint2() }
                             : s is ISketchArc arc ? new object[] { arc.GetStartPoint2(), arc.GetEndPoint2(), arc.GetCenterPoint2() }
                             : s is ISketchEllipse el ? new object[] { el.GetStartPoint2(), el.GetEndPoint2(), el.GetCenterPoint2() }
                             : s is ISketchSpline sp ? (sp.GetPoints2() as object[]) ?? new object[0]
                             : new object[0];
                foreach (var pt in pts.OfType<ISketchPoint>()) used.Add(IdKey(pt.GetID()));
            }
            return SketchPoints(sk).Where(pt => !used.Contains(IdKey(pt.GetID())) && ((pt.GetRelations() as object[])?.Length ?? 0) == 0).ToList();
        }

        // The sketch the call worked in — still open, or open before and consumed by a feature now — and its state, so
        // a blue (under-defined) sketch is visible in the answer.
        private object WithSketchStatus(object result, string sketchBefore)
        {
            try
            {
                var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (d == null) return result;
                ISketch sk = d.SketchManager?.ActiveSketch;
                string name = (sk as IFeature)?.Name;
                if (sk == null && sketchBefore != null)
                {
                    sk = FindFeatureByName(d, sketchBefore, null)?.GetSpecificFeature2() as ISketch;
                    name = sketchBefore;
                }
                string text = sk == null ? null : SketchStatusText(sk, name);
                if (text == null) return result;
                if (result is ToolOutput o)
                {
                    if (o.Geometry == null) o.Geometry = new JObject();
                    if (o.Geometry is JObject g) g["sketch_status"] = text;
                    return o;
                }
                if (result is JObject j) { j["sketch_status"] = text; return j; }
                if (result == null) return new JObject { ["sketch_status"] = text };
            }
            catch (Exception ex) { ExecLog.Write("sketch_status: " + ex.Message); }
            return result;
        }

        // The shape of a sketch: its points (x, y) and its arc / circle radii (r, NaN), sketch space, m.
        private static List<double[]> SketchShape(ISketch sk)
        {
            var shape = SketchPoints(sk).Select(pt => new[] { pt.X, pt.Y }).ToList();
            foreach (var arc in SketchSegments(sk).OfType<ISketchArc>())
            {
                try { shape.Add(new[] { arc.GetRadius(), double.NaN }); } catch { }
            }
            return shape;
        }

        // How far the shape moved: every point / radius of one snapshot to the nearest of the other, both ways (m).
        private static double ShapeShift(List<double[]> a, List<double[]> b)
        {
            Func<double[], List<double[]>, double> nearest = (p, set) =>
            {
                double best = double.PositiveInfinity;
                foreach (var q in set)
                {
                    if (double.IsNaN(p[1]) != double.IsNaN(q[1])) continue;
                    double dd = double.IsNaN(p[1]) ? Math.Abs(p[0] - q[0]) : Math.Sqrt((p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]));
                    if (dd < best) best = dd;
                }
                return best;
            };
            double worst = 0;
            foreach (var p in a) worst = Math.Max(worst, nearest(p, b));
            foreach (var p in b) worst = Math.Max(worst, nearest(p, a));
            return worst;
        }

        private static bool HasRelation(object entity, swConstraintType_e type, Func<SketchRelation, bool> also = null)
        {
            try
            {
                var rels = (entity is ISketchPoint pt ? pt.GetRelations() : (entity as ISketchSegment)?.GetRelations()) as object[];
                return (rels ?? new object[0]).OfType<SketchRelation>().Any(r => r.GetRelationType() == (int)type && (also == null || also(r)));
            }
            catch { return false; }
        }

        private static string SegmentId(ISketchSegment s)
        {
            try { return string.Join(".", ((s.GetID() as Array) ?? new int[0]).Cast<object>()); } catch { return ""; }
        }

        // Relations the geometry already satisfies exactly — lines along a sketch axis horizontal / vertical, a point at
        // the origin coincident with it — added the way add_sketch_constraint does (selection + SketchAddConstraints;
        // RelationManager.AddRelation faulted inside SW 2024, RPC_E_SERVERFAULT). End points drawn at one place are one
        // point already (AddToDB). Nothing here can move the shape. Returns the relations added.
        private int ExactRelations(IModelDoc2 d, ISketch sk)
        {
            var sel = d.SelectionManager as ISelectionMgr;
            Func<string, Func<bool>, int> relate = (constraint, select) =>
            {
                int before = RelationCount(sk);
                d.ClearSelection2(true);
                if (select()) d.SketchAddConstraints(constraint);
                d.ClearSelection2(true);
                return RelationCount(sk) - before;
            };
            int added = 0;
            foreach (var line in SketchSegments(sk).OfType<ISketchLine>())
            {
                var a = line.GetStartPoint2() as ISketchPoint;
                var b = line.GetEndPoint2() as ISketchPoint;
                if (a == null || b == null) continue;
                bool horizontal = Math.Abs(a.Y - b.Y) <= ExactTol && Math.Abs(a.X - b.X) > ExactTol;
                bool vertical = Math.Abs(a.X - b.X) <= ExactTol && Math.Abs(a.Y - b.Y) > ExactTol;
                if (horizontal && !HasRelation(line, swConstraintType_e.swConstraintType_HORIZONTAL))
                    added += relate("sgHORIZONTAL", () => ((ISketchSegment)line).Select4(false, sel.CreateSelectData() as SelectData));
                else if (vertical && !HasRelation(line, swConstraintType_e.swConstraintType_VERTICAL))
                    added += relate("sgVERTICAL", () => ((ISketchSegment)line).Select4(false, sel.CreateSelectData() as SelectData));
            }
            // arcs on one centre: coradial (same radius — a slot end drawn as two arcs) or concentric, each to the first
            var arcs = SketchSegments(sk).OfType<ISketchArc>().Select(a => new { a, c = a.GetCenterPoint2() as ISketchPoint, r = a.GetRadius() })
                                         .Where(x => x.c != null).ToList();
            var tied = new HashSet<int>();
            for (int i = 0; i < arcs.Count; i++)
                for (int j = i + 1; j < arcs.Count; j++)
                {
                    if (tied.Contains(j) || Math.Abs(arcs[i].c.X - arcs[j].c.X) > ExactTol || Math.Abs(arcs[i].c.Y - arcs[j].c.Y) > ExactTol) continue;
                    tied.Add(j);
                    bool same = Math.Abs(arcs[i].r - arcs[j].r) <= ExactTol;
                    var type = same ? swConstraintType_e.swConstraintType_CORADIAL : swConstraintType_e.swConstraintType_CONCENTRIC;
                    if (HasRelation(arcs[j].a, type)) continue;
                    var ai = (ISketchSegment)arcs[i].a; var aj = (ISketchSegment)arcs[j].a;
                    added += relate(same ? "sgCORADIAL" : "sgCONCENTRIC",
                                    () => ai.Select4(false, sel.CreateSelectData() as SelectData) && aj.Select4(true, sel.CreateSelectData() as SelectData));
                    if (!same) continue;
                    // the joint of an arc drawn in two halves slides along the circle (the shape stays, the sketch is
                    // blue): on the centre's vertical / horizontal it is tied there — nothing else fixes that point
                    var ends = new[] { arcs[i].a.GetStartPoint2(), arcs[i].a.GetEndPoint2() }.OfType<ISketchPoint>();
                    var other = new[] { arcs[j].a.GetStartPoint2(), arcs[j].a.GetEndPoint2() }.OfType<ISketchPoint>().ToList();
                    var joint = ends.FirstOrDefault(e => other.Any(o => Math.Abs(o.X - e.X) <= ExactTol && Math.Abs(o.Y - e.Y) <= ExactTol));
                    var centre = arcs[i].c;
                    if (joint == null) continue;
                    string align = Math.Abs(joint.X - centre.X) <= ExactTol ? "sgVERTICALPOINTS2D"
                                 : Math.Abs(joint.Y - centre.Y) <= ExactTol ? "sgHORIZONTALPOINTS2D" : null;
                    if (align != null)
                        added += relate(align, () => joint.Select4(false, sel.CreateSelectData() as SelectData) && centre.Select4(true, sel.CreateSelectData() as SelectData));
                }
            // a line meeting an arc at a shared end, square to the radius there: tangent (it fixes the arc's centre — a
            // circle through two line ends otherwise slides along its axis; Fully Define does not add it)
            foreach (var line in SketchSegments(sk).OfType<ISketchLine>())
            {
                var lp = new[] { line.GetStartPoint2() as ISketchPoint, line.GetEndPoint2() as ISketchPoint };
                if (lp[0] == null || lp[1] == null) continue;
                double dx = lp[1].X - lp[0].X, dy = lp[1].Y - lp[0].Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= ExactTol) continue;
                foreach (var arc in arcs)
                {
                    var ae = new[] { arc.a.GetStartPoint2(), arc.a.GetEndPoint2() }.OfType<ISketchPoint>();
                    var shared = ae.FirstOrDefault(e => lp.Any(q => Math.Abs(q.X - e.X) <= ExactTol && Math.Abs(q.Y - e.Y) <= ExactTol));
                    if (shared == null) continue;
                    double rx = shared.X - arc.c.X, ry = shared.Y - arc.c.Y;
                    if (Math.Abs(rx * dx + ry * dy) > ExactTol * len) continue;   // not square to the radius
                    string arcId = SegmentId((ISketchSegment)arc.a);
                    if (HasRelation(line, swConstraintType_e.swConstraintType_TANGENT, r =>
                            ((r.GetEntities() as object[]) ?? new object[0]).OfType<ISketchSegment>().Any(s => SegmentId(s) == arcId))) continue;
                    var ls = (ISketchSegment)line; var asg = (ISketchSegment)arc.a;
                    added += relate("sgTANGENT", () => ls.Select4(false, sel.CreateSelectData() as SelectData) && asg.Select4(true, sel.CreateSelectData() as SelectData));
                }
            }
            // a lone point at the middle of a line (a corner rectangle's centre on its diagonals): tied there
            foreach (var pt in LonePoints(sk))
            {
                var mid = SketchSegments(sk).OfType<ISketchLine>().FirstOrDefault(l => l.GetStartPoint2() is ISketchPoint a && l.GetEndPoint2() is ISketchPoint b
                              && Math.Abs((a.X + b.X) / 2 - pt.X) <= ExactTol && Math.Abs((a.Y + b.Y) / 2 - pt.Y) <= ExactTol);
                if (mid != null)
                    added += relate("sgATMIDDLE", () => pt.Select4(false, sel.CreateSelectData() as SelectData) && ((ISketchSegment)mid).Select4(true, sel.CreateSelectData() as SelectData));
            }
            // a point on the sketch origin (the projected part origin), unless it is tied to it already
            var atOrigin = SketchPoints(sk).FirstOrDefault(pt => Math.Abs(pt.X) <= ExactTol && Math.Abs(pt.Y) <= ExactTol);
            if (atOrigin != null && !HasRelation(atOrigin, swConstraintType_e.swConstraintType_COINCIDENT))
                added += relate("sgCOINCIDENT", () => atOrigin.Select4(false, sel.CreateSelectData() as SelectData) && SelectSketchOrigin(d, true));
            return added;
        }

        private static int RelationCount(ISketch sk)
        {
            try { return sk.RelationManager.GetRelationsCount((int)swSketchRelationFilterType_e.swAll); } catch { return 0; }
        }

        private static int DimensionCount(ISketch sk)
        {
            int n = 0;
            try
            {
                var f = sk as IFeature;
                for (var dd = f?.GetFirstDisplayDimension(); dd != null; dd = f.GetNextDisplayDimension(dd)) n++;
            }
            catch { }
            return n;
        }

        private void Anchor(IModelDoc2 d, ISketch sk, List<double[]> shape, double tol)
        {
            var real = SketchSegments(sk).Where(s => !s.ConstructionGeometry).ToList();
            var anchor = real.OfType<ISketchArc>().Select(a => a.GetCenterPoint2() as ISketchPoint).FirstOrDefault(c => c != null)
                         ?? SketchPoints(sk).OrderBy(pt => Math.Round(pt.Y, 9)).ThenBy(pt => pt.X).FirstOrDefault();
            if (anchor == null) return;
            double x = anchor.X, y = anchor.Y;
            var sel = d.SelectionManager as ISelectionMgr;
            Func<bool> stays = () => ShapeShift(shape, SketchShape(sk)) <= tol && SketchStatus(sk) != (int)swConstrainedStatus_e.swOverConstrained;
            foreach (bool alongX in new[] { true, false })
            {
                if (SketchStatus(sk) != (int)swConstrainedStatus_e.swUnderConstrained) return;
                double c = alongX ? x : y;
                try
                {
                    if (Math.Abs(c) <= ExactTol)
                    {
                        // on the axis: the point and the origin vertically (same x) / horizontally (same y) aligned
                        foreach (string rel in alongX ? new[] { "sgVERTICALPOINTS2D", "sgVERTICAL" } : new[] { "sgHORIZONTALPOINTS2D", "sgHORIZONTAL" })
                        {
                            int before = RelationCount(sk);
                            d.ClearSelection2(true);
                            if (anchor.Select4(false, sel.CreateSelectData() as SelectData) && SelectSketchOrigin(d, true)) d.SketchAddConstraints(rel);
                            d.ClearSelection2(true);
                            if (RelationCount(sk) == before) continue;
                            if (!stays()) d.EditUndo2(1);
                            break;
                        }
                    }
                    else
                    {
                        var dd = AddSketchDimension(d, sk, x, y, "point", true, alongX ? "horizontal" : "vertical",
                                                    alongX ? 0 : -0.01, alongX ? -0.01 : 0);
                        var dim = dd.GetDimension2(0) as IDimension;
                        if (dim != null && dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven) DeleteDimension(d, dd);
                        else if (!stays()) d.EditUndo2(1);
                    }
                }
                catch (ToolFail f) { ExecLog.Write($"define_sketch: anchor {(alongX ? "x" : "y")} -> {f.Message}"); }
            }
        }

        // A dimension SolidWorks made driven (the size is defined already) is deleted — not undone: EditUndo2 after it
        // left the part so that the next close faulted inside SolidWorks (RPC_E_SERVERFAULT, 3 of 3).
        private static void DeleteDimension(IModelDoc2 d, IDisplayDimension dd)
        {
            d.ClearSelection2(true);
            if ((dd.GetAnnotation() as IAnnotation)?.Select3(false, null) == true) d.EditDelete();
            d.ClearSelection2(true);
        }

        // Fully Define Sketch on the given segments (mark 1) with the sketch origin as the datum (mark 8).
        private int AutoDefine(IModelDoc2 d, List<ISketchSegment> segs, bool relations, int scheme)
        {
            var sel = d.SelectionManager as ISelectionMgr;
            d.ClearSelection2(true);
            var data = sel.CreateSelectData() as SelectData;
            data.Mark = (int)swAutodimMark_e.swAutodimMarkEntities;
            foreach (var s in segs) s.Select4(true, data);
            var origin = Features(d).FirstOrDefault(f => f.GetTypeName2() == "OriginProfileFeature");
            var point = ((origin?.GetSpecificFeature2() as ISketch)?.GetSketchPoints2() as object[])?.OfType<ISketchPoint>().FirstOrDefault();
            var datum = sel.CreateSelectData() as SelectData;
            datum.Mark = (int)swAutodimMark_e.swAutodimMarkOriginDatum;
            point?.Select4(true, datum);
            int r = d.SketchManager.FullyDefineSketch(false, relations, AutoRelations, true, scheme, null, scheme, null,
                (int)swAutodimHorizontalPlacement_e.swAutodimHorizontalPlacementBelow, (int)swAutodimVerticalPlacement_e.swAutodimVerticalPlacementLeft);
            d.ClearSelection2(true);
            return r;
        }

        public ExecutionResponse DefineSketch(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            string sketchName = PStr(p, "sketch_name", "");
            bool auto = PBool(p, "auto", true);
            string scheme = PStr(p, "scheme", "baseline").ToLowerInvariant();
            int schemeCode = scheme == "baseline" ? (int)swAutodimScheme_e.swAutodimSchemeBaseline
                           : scheme == "chain" ? (int)swAutodimScheme_e.swAutodimSchemeChain
                           : scheme == "ordinate" ? (int)swAutodimScheme_e.swAutodimSchemeOrdinate
                           : throw new ToolFail("BAD_PARAMETER", $"scheme must be baseline, chain or ordinate, got '{scheme}'.");
            double tol = PNum(p, "tolerance_mm", 0.001) / MM;
            var specs = PArr(p, "dimensions");

            ISketch sk = d.SketchManager.ActiveSketch;
            bool openedHere = false;
            if (sketchName != "" && (sk == null || (sk as IFeature)?.Name != sketchName))
            {
                ExitSketch(d);
                d.ClearSelection2(true);
                if (!SelectSketch(d, sketchName)) throw new ToolFail("SKETCH_NOT_FOUND", $"No sketch named '{sketchName}'.");
                d.EditSketch();
                sk = d.SketchManager.ActiveSketch;
                openedHere = sk != null;
            }
            if (sk == null) throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch — pass sketch_name, or call create_sketch / edit_sketch first.");
            string name = (sk as IFeature)?.Name ?? sketchName;
            if (SketchSegments(sk).Count == 0) throw new ToolFail("NO_ENTITIES", $"'{name}' has no entities to define — draw the shape first.");

            var res = new JObject { ["sketch"] = name };
            var dims = new JArray(); var skipped = new JArray();
            var shape = SketchShape(sk);
            int relBefore = RelationCount(sk), dimBefore = DimensionCount(sk);
            double worst = 0;
            // one guarded step: did it move the shape (or over-define the sketch)? then undo it — true when it stayed
            Func<string, bool> keep = what =>
            {
                double shift = ShapeShift(shape, SketchShape(sk));
                bool over = SketchStatus(sk) == (int)swConstrainedStatus_e.swOverConstrained;
                if (shift <= tol && !over) { worst = Math.Max(worst, shift); return true; }
                d.EditUndo2(1);
                double after = ShapeShift(shape, SketchShape(sk));
                ExecLog.Write($"define_sketch: {what} undone (shift {shift * MM:0.####} mm, over-defined {over}; after undo {after * MM:0.####} mm)");
                skipped.Add($"{what}: {(over ? "over-defines the sketch" : $"moved the geometry by {shift * MM:0.###} mm")} — undone"
                            + (after > tol ? $" (the undo left a shift of {after * MM:0.###} mm — check the sketch)" : ""));
                return false;
            };
            try
            {
                // 1. relations the geometry already satisfies
                int exact = ExactRelations(d, sk);
                if (exact > 0 && !keep("exact relations")) { for (int i = 1; i < exact; i++) d.EditUndo2(1); }

                // 2. the caller's design dimensions, at their current values
                for (int i = 0; i < specs.Count; i++)
                {
                    var o = specs[i] as JObject;
                    double? px = (double?)o?["px"], py = (double?)o?["py"];
                    if (px == null || py == null) { skipped.Add($"dimensions[{i}]: px and py are required."); continue; }
                    try
                    {
                        var dd = AddSketchDimension(d, sk, px.Value / MM, py.Value / MM, ((string)o["entity_type"] ?? "segment").ToLowerInvariant(),
                                                    (bool?)o["to_origin"] ?? false, ((string)o["orientation"] ?? "auto").ToLowerInvariant(),
                                                    ((double?)o["label_offset_x"] ?? 0) / MM, ((double?)o["label_offset_y"] ?? -10) / MM);
                        var dim = dd.GetDimension2(0) as IDimension;
                        string full = dim?.FullName ?? $"dimensions[{i}]";
                        // a dimension of something already defined: SolidWorks makes it driven instead of over-defining
                        if (dim != null && dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven)
                        {
                            DeleteDimension(d, dd);
                            skipped.Add($"dimensions[{i}]: that size is already defined (it would be a driven dimension) — not added");
                            continue;
                        }
                        if (keep(full)) dims.Add(new JObject { ["dimension"] = ShortDimName(full), ["value"] = Math.Round((dim?.SystemValue ?? 0) * (dd.Type2 == (int)swDimensionType_e.swAngularDimension ? 180 / Math.PI : MM), 3) });
                    }
                    catch (ToolFail f) { skipped.Add($"dimensions[{i}]: {f.Message}"); }
                }

                // 2b. the profile tied to the origin — Fully Define Sketch sizes a profile but leaves it floating when nothing
                //     ties it there (live: an L-bracket off the origin, a slot centred on it). An arc's centre (a hole, a
                //     slot end) or else the lowest-left point gets a horizontal and a vertical dimension to the origin, or
                //     a relation when it lies on the axis; one that is redundant (driven), over-defines or moves anything
                //     is undone quietly.
                if (auto && SketchStatus(sk) == (int)swConstrainedStatus_e.swUnderConstrained)
                    Anchor(d, sk, shape, tol);

                // 3. SolidWorks' Fully Define Sketch for the rest, from the sketch origin (selected with the origin-datum
                //    mark — without a datum it only sizes the shape and leaves it floating). The real segments first (with a
                //    corner rectangle's construction diagonals SolidWorks dimensioned their crossing point, not the
                //    height), then everything (a construction centerline is a revolve axis). Safe relations + dimensions;
                //    dimensions only when the relations moved anything.
                if (auto)
                {
                    using var prefs = new PrefScope(_solidWorks).Toggle(swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                    foreach (bool realOnly in new[] { true, false })
                    {
                        if (SketchStatus(sk) != (int)swConstrainedStatus_e.swUnderConstrained) break;
                        var segs = SketchSegments(sk).Where(x => !realOnly || !x.ConstructionGeometry).ToList();
                        if (segs.Count == 0 || (!realOnly && segs.Count == SketchSegments(sk).Count(x => !x.ConstructionGeometry))) continue;
                        foreach (bool relations in new[] { true, false })
                        {
                            int r = AutoDefine(d, segs, relations, schemeCode);
                            string what = $"auto ({(realOnly ? "profile" : "all segments")}, {(relations ? "relations + dimensions" : "dimensions")})";
                            ExecLog.Write($"define_sketch: {what} -> {(swAutodimStatus_e)r}");
                            if (r != (int)swAutodimStatus_e.swAutodimStatusSuccess)
                            {
                                skipped.Add($"{what}: " + ((swAutodimStatus_e)r).ToString().Replace("swAutodimStatus", ""));
                                break;
                            }
                            if (keep(what)) break;
                        }
                    }
                }
            }
            finally
            {
                if (openedHere) { ExitSketch(d); d.EditRebuild3(); }
            }

            if (openedHere) sk = FindFeatureByName(d, name, null)?.GetSpecificFeature2() as ISketch ?? sk;
            res["status"] = SketchStatusName(SketchStatus(sk));
            var free = FreeSegments(sk);
            if (free.Count > 0) res["free_segments"] = free;
            if (SketchStatus(sk) == (int)swConstrainedStatus_e.swUnderConstrained)
            {
                var lone = LonePoints(sk).Select(pt => $"{Math.Round(pt.X * MM, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)},{Math.Round(pt.Y * MM, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}").ToList();
                if (lone.Count > 0) res["free_points"] = new JArray(lone.Take(12));
            }
            res["relations_added"] = RelationCount(sk) - relBefore;
            res["dimensions_added"] = DimensionCount(sk) - dimBefore;
            if (dims.Count > 0) res["design_dimensions"] = dims;
            res["max_shift_mm"] = Math.Round(worst * MM, 6);
            if (skipped.Count > 0) res["skipped"] = skipped;
            return new ToolOutput { ActiveSketch = openedHere ? null : name, Geometry = res };
        });
    }
}
