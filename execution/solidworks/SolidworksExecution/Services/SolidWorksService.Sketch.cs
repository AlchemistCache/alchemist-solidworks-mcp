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
    // SolidWorksService partial: sketch lifecycle and 2D geometry (create/edit sketch, entities, dimensions, constraints).
    public partial class SolidWorksService
    {

        public ExecutionResponse CreateSketch(ToolRequest request) => Run(request, true, p =>
        {
            string planeName = PStr(p, "plane");
            bool onFace = PBool(p, "on_face", false);

            var modelDoc = ActiveDocOrFail();

            // Auto-exit any active sketch so multi-sketch workflows (sweep needs a path + a profile,
            // loft needs multiple profiles) work without a dedicated exit-sketch tool. Previously this
            // returned SKETCH_ALREADY_ACTIVE; finishing the open sketch before starting a new one is the
            // sensible behaviour and all existing flows call create_sketch with no sketch active anyway.
            if (modelDoc.SketchManager.ActiveSketch != null)
            {
                // a wrong plane name must fail BEFORE the open sketch is closed (SolidWorks drops an empty one)
                if (!onFace && !string.IsNullOrEmpty(planeName) && FindFeatureByName(modelDoc, planeName, null) == null
                    && DefaultPlane(modelDoc.FirstFeature() as IFeature, planeName) == null)
                    throw new ToolFail("PLANE_NOT_FOUND", $"Plane '{planeName}' not found.");
                ExitSketch(modelDoc);
            }

            modelDoc.ClearSelection2(true);
            if (onFace)
            {
                // Sketch on a model FACE. Two ways to pick the face:
                //   (a) face_index — the i-th face from analyze_model(faces). ROBUST: reuses the same
                //       GetBodies2(swSolidBody,true) → GetFaces() enumeration and selects the IFace2
                //       directly, bypassing the coordinate pick that is ambiguous on a revolve end-cap
                //       (the flat circular cap's centroid sits on the revolve axis/origin → FACE_NOT_FOUND).
                //   (b) face_x/y/z — a 3D point lying ON the planar face (interior, not on an edge).
                // Plane-name selection can't reach model faces, so this is what enables features on
                // existing geometry (e.g. a recess/hub/keyway on a gear face, or a bore on a shaft end).
                if (!SelectEntity(modelDoc, ResolveFace(modelDoc, p), false))
                    throw new ToolFail("FACE_NOT_FOUND", "The face was found but could not be selected.");
            }
            else
            {
                if (string.IsNullOrEmpty(planeName))
                    throw new ToolFail("MISSING_PARAMETER", "plane is required (or set on_face=true with face_x/face_y/face_z).");
                bool selected = SelectPlaneFlexible(modelDoc, planeName, false, 0);
                if (!selected)
                    throw new ToolFail("PLANE_NOT_FOUND", $"Plane '{planeName}' not found.");
            }

            if (onFace)
            {
                try
                {
                    var selMgr = modelDoc.SelectionManager as ISelectionMgr;
                    int selCount = selMgr?.GetSelectedObjectCount2(-1) ?? -1;
                    ExecLog.Write($"create_sketch on_face: selectedCount={selCount} before InsertSketch");
                }
                catch { }
            }

            modelDoc.SketchManager.InsertSketch(true);

            var activeSketch = modelDoc.SketchManager.ActiveSketch;
            if (onFace)
                ExecLog.Write($"create_sketch on_face: activeSketch={(activeSketch != null)} after InsertSketch");
            if (activeSketch == null)
                throw new ToolFail("SKETCH_CREATION_FAILED", "Sketch was not created or is not active after InsertSketch. The face may be non-planar, or the selection was lost — try analyze_model(faces) and a different face_index.");

            string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

            // Echo the NEW sketch's MEASURED plane + frame (same reader analyze uses). The
            // caller compares this against the original sketch's recorded frame
            // and transforms 2D coordinates — the deterministic fix for the support-normal /
            // in-plane-axis mismatch class (2-1 Sketch5). Best-effort: never fails the create.
            object sketchPlaneEcho = null;
            try { sketchPlaneEcho = ReadSketchPlane(activeSketch); } catch { }

            return new ToolOutput { ActiveSketch = activeSketchName, Geometry = sketchPlaneEcho };
        });

        // The single-entity CREATE core shared by add_sketch_entity and add_sketch_entities:
        // one switch, never forked. Creates ONE entity record (entity_type + its params, incl. the
        // per-record construction flag) in the ACTIVE sketch: createdSeg / createdSegs populated, or ToolFail.
        // The caller owns the AddToDB bracket (exact coordinates, no snapping).
        private void CreateSketchEntityCore(IModelDoc2 modelDoc, JObject p, string entityType,
            string sketchName, out ISketchSegment createdSeg, out object[] createdSegs)
        {
            createdSeg = null;
            createdSegs = null;
            bool created = false;
            entityType = entityType.ToLowerInvariant();
            int segsBefore = SegCount(modelDoc);
            switch (entityType)
            {
                case "rectangle":
                {
                    var v = PNums(p, "rectangle requires x1, y1, x2, y2.", "x1", "y1", "x2", "y2");
                    createdSegs = modelDoc.SketchManager.CreateCornerRectangle(v[0], v[1], 0, v[2], v[3], 0) as object[];
                    created = createdSegs != null && createdSegs.Length > 0;
                    break;
                }
                case "circle":
                {
                    var v = PNums(p, "circle requires cx, cy, radius.", "cx", "cy", "radius");
                    createdSeg = modelDoc.SketchManager.CreateCircleByRadius(v[0], v[1], 0, v[2]) as ISketchSegment;
                    created = createdSeg != null;
                    break;
                }
                case "line":
                {
                    var v = PNums(p, "line requires x1, y1, x2, y2.", "x1", "y1", "x2", "y2");
                    createdSeg = modelDoc.SketchManager.CreateLine(v[0], v[1], 0, v[2], v[3], 0) as ISketchSegment;
                    created = createdSeg != null;
                    break;
                }
                case "arc":
                {
                    // 3-point arc: start (x1,y1), end (x2,y2), mid-arc point (xm,ym)
                    var v = PNums(p, "arc requires x1, y1 (start), x2, y2 (end), xm, ym (mid-arc point).",
                                  "x1", "y1", "x2", "y2", "xm", "ym");
                    createdSeg = modelDoc.SketchManager.Create3PointArc(v[0], v[1], 0, v[2], v[3], 0, v[4], v[5], 0) as ISketchSegment;
                    created = createdSeg != null;
                    break;
                }
                case "arc_center":
                {
                    // Center-based arc: exact center (cx,cy) + start (x1,y1), end (x2,y2), direction
                    // (+1 CCW, -1 CW). Guarantees the radius; numerically stable for shallow arcs.
                    var v = PNums(p, "arc_center requires cx, cy (center), x1, y1 (start), x2, y2 (end).",
                                  "cx", "cy", "x1", "y1", "x2", "y2");
                    double cx = v[0], cy = v[1], x1 = v[2], y1 = v[3], x2 = v[4], y2 = v[5];
                    // direction: +1 CCW / -1 CW, applied VERBATIM when given. When OMITTED (or 0),
                    // pick the MINOR (<=180°) arc deterministically — a corner round / fillet arc is
                    // virtually always the minor one, and a wrong sign silently draws the 270°
                    // complement (live benchmark failure: R2 90° corner came out as the other side).
                    // Callers that really want the major arc pass the explicit sign (the analyze/IR
                    // round-trip always does — its 'dir' is read from the original geometry).
                    double dirRaw = PNum(p, "direction", 0.0);
                    short direction;
                    if (Math.Abs(dirRaw) >= 0.5)
                        direction = (short)(dirRaw >= 0 ? 1 : -1);
                    else
                    {
                        double a1 = Math.Atan2(y1 - cy, x1 - cx);
                        double a2 = Math.Atan2(y2 - cy, x2 - cx);
                        double ccwSweep = a2 - a1;
                        while (ccwSweep < 0) ccwSweep += 2 * Math.PI;
                        while (ccwSweep >= 2 * Math.PI) ccwSweep -= 2 * Math.PI;
                        direction = (short)(ccwSweep <= Math.PI ? 1 : -1);
                    }
                    // Endpoints passed VERBATIM (shared exactly with neighbours — the closed contour is
                    // preserved). The caller's AddToDB bracket keeps SW inference off the exact radius.
                    int before = SegCount(modelDoc);
                    createdSeg = modelDoc.SketchManager.CreateArc(cx, cy, 0, x1, y1, 0, x2, y2, 0, direction);
                    if (createdSeg == null || SegCount(modelDoc) == before)
                    {
                        // CreateArc returned without adding anything when the arc was concentric with another or an end
                        // lay next to an existing point (the contour stayed open, no error): the same arc through 3 points.
                        double r = Math.Sqrt((x1 - cx) * (x1 - cx) + (y1 - cy) * (y1 - cy));
                        double s1 = Math.Atan2(y1 - cy, x1 - cx), s2 = Math.Atan2(y2 - cy, x2 - cx), sweep = s2 - s1;
                        if (direction > 0) { while (sweep <= 0) sweep += 2 * Math.PI; } else { while (sweep >= 0) sweep -= 2 * Math.PI; }
                        double am = s1 + sweep / 2;
                        createdSeg = modelDoc.SketchManager.Create3PointArc(x1, y1, 0, x2, y2, 0, cx + r * Math.Cos(am), cy + r * Math.Sin(am), 0) as ISketchSegment;
                        ExecLog.Write($"add_sketch_entity: arc_center not added by CreateArc — 3-point arc {(createdSeg != null && SegCount(modelDoc) > before ? "added" : "failed too")}");
                    }
                    created = createdSeg != null;
                    break;
                }
                case "ellipse":
                {
                    // Center-based ellipse: cx,cy (center), x1,y1 (major-axis point), x2,y2 (minor-axis point).
                    var v = PNums(p, "ellipse requires cx, cy (center), x1, y1 (major-axis point), x2, y2 (minor-axis point).",
                                  "cx", "cy", "x1", "y1", "x2", "y2");
                    createdSeg = modelDoc.SketchManager.CreateEllipse(v[0], v[1], 0, v[2], v[3], 0, v[4], v[5], 0) as ISketchSegment;
                    created = createdSeg != null;
                    break;
                }
                case "spline":
                {
                    // Through-point spline: 'points' = flat [x1,y1,x2,y2,...] (>= 2 points), z=0.
                    var ptsTok = p?["points"] as JArray;
                    if (ptsTok == null || ptsTok.Count < 4 || ptsTok.Count % 2 != 0)
                        throw new ToolFail("MISSING_PARAMETER", "spline requires 'points': a flat list [x1,y1,x2,y2,...] of >= 2 (x,y) pairs.");
                    int nPts = ptsTok.Count / 2;
                    var pointData = new double[nPts * 3];
                    for (int i = 0; i < nPts; i++)
                    {
                        pointData[i * 3] = ptsTok[i * 2].Value<double>();
                        pointData[i * 3 + 1] = ptsTok[i * 2 + 1].Value<double>();
                        pointData[i * 3 + 2] = 0.0;
                    }
                    var spl = modelDoc.SketchManager.CreateSpline(pointData);
                    createdSeg = spl as ISketchSegment;
                    created = spl != null;
                    break;
                }
                case "fillet":
                {
                    // Sketch fillet: rounds the corner between two segments meeting at vertex (vx, vy).
                    var v = PNums(p, "fillet requires vx, vy (vertex coordinates) and radius.", "vx", "vy", "radius");
                    SelectSketchVertex(modelDoc, v[0], v[1]);
                    createdSeg = modelDoc.SketchManager.CreateFillet(v[2],
                        (int)swConstrainedCornerAction_e.swConstrainedCornerDeleteGeometry) as ISketchSegment;
                    created = createdSeg != null;
                    if (created) RegenerateSketchInPlace(modelDoc, sketchName);
                    break;
                }
                case "chamfer":
                {
                    // Sketch chamfer: cuts the corner between two adjacent segments at vertex (vx, vy).
                    var v = PNums(p, "chamfer requires vx, vy (vertex coordinates) and distance.", "vx", "vy", "distance");
                    SelectSketchVertex(modelDoc, v[0], v[1]);
                    createdSeg = modelDoc.SketchManager.CreateChamfer(
                        (int)swSketchChamferType_e.swSketchChamfer_DistanceEqual, v[2], v[2]) as ISketchSegment;
                    created = createdSeg != null;
                    if (created) RegenerateSketchInPlace(modelDoc, sketchName);
                    break;
                }
                default:
                    throw new ToolFail("BAD_PARAMETER", $"entity_type '{entityType}' is not supported. Supported: rectangle, circle, line, arc, arc_center, ellipse, spline, fillet, chamfer.");
            }

            if (!created)
                throw new ToolFail("SKETCH_FAILED", $"Failed to create sketch entity of type '{entityType}'.");
            // a create call can return an object and still add nothing (a zero-length line, an arc onto existing
            // geometry): checked by the segment count, so the contour is never silently left open
            if (createdSeg != null && segsBefore >= 0 && SegCount(modelDoc) <= segsBefore && entityType != "fillet" && entityType != "chamfer")
                throw new ToolFail("SKETCH_FAILED", $"SolidWorks did not add the {entityType} (zero length, or it lies on existing geometry).");

            // construction=true converts the created segment(s) to construction/reference geometry
            // (centerlines, symmetry axes, hole-position scaffolding). Applied post-create because
            // SketchManager has no construction-aware create calls.
            bool asConstruction = PBool(p, "construction", false);
            if (asConstruction)
            {
                try
                {
                    if (createdSeg != null) createdSeg.ConstructionGeometry = true;
                    if (createdSegs != null)
                        foreach (var o in createdSegs)
                        {
                            var s = o as ISketchSegment;
                            if (s != null) s.ConstructionGeometry = true;
                        }
                }
                catch (Exception ex)
                {
                    throw new ToolFail("SKETCH_FAILED", $"Entity created but could not be converted to construction geometry: {ex.Message}");
                }
            }
        }

        private static int SegCount(IModelDoc2 d)
        {
            try { return ((d.SketchManager.ActiveSketch as ISketch)?.GetSketchSegments() as object[])?.Length ?? 0; }
            catch { return -1; }
        }

        // Sketch snapping and automatic relations off while the AI draws (exact coordinates): rectangle corners stuck to a
        // neighbour's centre 1 mm away. System options, put back afterwards.
        private PrefScope NoSnap() => new PrefScope(_solidWorks)
            .Toggle(swUserPreferenceToggle_e.swSketchInference, false)
            .Toggle(swUserPreferenceToggle_e.swSketchAutomaticRelations, false)
            .Toggle(swUserPreferenceToggle_e.swSnapToPoints, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsPoints, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsCenterPoints, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsMidPoints, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsIntersections, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsNearest, false)
            .Toggle(swUserPreferenceToggle_e.swSketchSnapsGrid, false);

        // sketch_modify — change an EXISTING sketch in one call (the AI otherwise piled bosses over a profile it could not
        // edit): delete segments (all with delete_all, or the ones passing near points), move points, add records like
        // add_sketch_entities; then exit, rebuild and name the features that now fail. SI in (the adapter converts mm).
        public ExecutionResponse SketchModify(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            string name = PStr(p, "sketch_name") ?? throw new ToolFail("MISSING_PARAMETER", "sketch_name is required (the sketch feature, e.g. 'Эскиз3').");
            var feat = FindFeatureByName(d, name, null);
            if (feat == null || feat.GetTypeName2() != "ProfileFeature") throw new ToolFail("SKETCH_NOT_FOUND", $"No sketch '{name}' (analyze_model(features) lists them).");
            bool deleteAll = PBool(p, "delete_all", false), keepCon = PBool(p, "keep_construction", true);
            var near = PArr(p, "delete_near").Select(t => (t as JArray)?.Select(v => v.ToObject<double>()).ToArray()).ToList();
            var moves = PArr(p, "move_points").Select(t => (t as JArray)?.Select(v => v.ToObject<double>()).ToArray()).ToList();
            var adds = PArr(p, "add_segments").OfType<JObject>().ToList();
            if (near.Any(v => v == null || v.Length != 2)) throw new ToolFail("BAD_PARAMETER", "delete_near is a list of [x, y] points (sketch coordinates).");
            if (moves.Any(v => v == null || v.Length != 4)) throw new ToolFail("BAD_PARAMETER", "move_points is a list of [from_x, from_y, to_x, to_y].");

            ExitSketch(d);
            d.ClearSelection2(true);
            if (!SelectSketch(d, name)) feat.Select2(false, 0);
            d.EditSketch();
            var sk = d.SketchManager.ActiveSketch as ISketch ?? throw new ToolFail("SKETCH_EDIT_FAILED", $"'{name}' could not be opened for editing.");
            var res = new JObject { ["sketch"] = name };
            try
            {
                var segs = SketchSegments(sk);
                var victims = deleteAll ? segs.Where(s => !(keepCon && s.ConstructionGeometry)).ToList() : new List<ISketchSegment>();
                foreach (var pt in near)
                {
                    var hit = segs.Select(s => new { s, dist = SegDistance(s, pt[0], pt[1]) }).OrderBy(x => x.dist).FirstOrDefault();
                    if (hit == null || hit.dist > 0.0005)
                        throw new ToolFail("ENTITY_NOT_FOUND", $"No segment of '{name}' passes within 0.5 mm of ({Math.Round(pt[0] * MM, 2)}, {Math.Round(pt[1] * MM, 2)}) — sketch coordinates (analyze_model(sketch) shows them).");
                    if (!victims.Contains(hit.s)) victims.Add(hit.s);
                }
                if (victims.Count > 0)
                {
                    d.ClearSelection2(true);
                    foreach (var v in victims) v.Select4(true, null);
                    d.EditDelete();
                    res["deleted"] = segs.Count - SketchSegments(sk).Count;
                }
                if (moves.Count > 0)
                {
                    var stuck = new JArray();
                    foreach (var m in moves)
                    {
                        var pt = SketchPoints(sk).Select(q => new { q, dist = Math.Sqrt((q.X - m[0]) * (q.X - m[0]) + (q.Y - m[1]) * (q.Y - m[1])) })
                                                 .OrderBy(x => x.dist).FirstOrDefault();
                        if (pt == null || pt.dist > 0.00005)
                            throw new ToolFail("POINT_NOT_FOUND", $"No point of '{name}' at ({Math.Round(m[0] * MM, 3)}, {Math.Round(m[1] * MM, 3)}) (within 0.05 mm).");
                        pt.q.SetCoords(m[2], m[3], 0);
                        if (Math.Abs(pt.q.X - m[2]) > 1e-6 || Math.Abs(pt.q.Y - m[3]) > 1e-6)
                            stuck.Add($"({Math.Round(m[0] * MM, 2)}, {Math.Round(m[1] * MM, 2)}) stayed at ({Math.Round(pt.q.X * MM, 2)}, {Math.Round(pt.q.Y * MM, 2)}) — held by a dimension or relation");
                    }
                    res["moved"] = moves.Count - stuck.Count;
                    if (stuck.Count > 0) res["not_moved"] = stuck;
                }
                if (adds.Count > 0)
                {
                    int done = 0;
                    using (NoSnap())
                    using (new AddToDbScope(d.SketchManager))
                        for (int i = 0; i < adds.Count; i++)
                        {
                            string et = adds[i].Value<string>("entity_type") ?? throw new ToolFail("MISSING_PARAMETER", $"add_segments[{i}] has no entity_type.");
                            try { CreateSketchEntityCore(d, adds[i], et, name, out _, out _); }
                            catch (ToolFail f) { throw new ToolFail(f.Code, $"add_segments[{i}] ({et}): {f.Message} {done} added before it."); }
                            done++;
                        }
                    res["added"] = done;
                }
                string status = SketchStatusText(sk, name);
                if (status != null) res["sketch_status"] = status;
            }
            finally { ExitSketch(d); }
            d.EditRebuild3();
            var broken = Features(d).Where(f => { try { return f.GetErrorCode2(out bool w) != 0 && !w; } catch { return false; } }).Select(f => f.Name).ToList();
            if (broken.Count > 0) res["features_with_errors"] = new JArray(broken);
            return res;
        });

        // Distance (m) from a sketch point to a segment: a line's nearest point, an arc's / circle's curve, else its ends.
        private static double SegDistance(ISketchSegment s, double x, double y)
        {
            double D(double ax, double ay) => Math.Sqrt((ax - x) * (ax - x) + (ay - y) * (ay - y));
            if (s is ISketchLine l && l.GetStartPoint2() is ISketchPoint a && l.GetEndPoint2() is ISketchPoint b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((x - a.X) * dx + (y - a.Y) * dy) / len2)) : 0;
                return D(a.X + t * dx, a.Y + t * dy);
            }
            if (s is ISketchArc arc && arc.GetCenterPoint2() is ISketchPoint c)
                return Math.Abs(D(c.X, c.Y) - arc.GetRadius());
            var pts = new List<double>();
            try
            {
                var curve = s.GetCurve() as ICurve;
                curve.GetEndParams(out double t0, out double t1, out _, out _);
                for (int i = 0; i <= 20; i++)
                {
                    var ev = curve.Evaluate2(t0 + (t1 - t0) * i / 20, 0) as double[];
                    if (ev != null) pts.Add(D(ev[0], ev[1]));
                }
            }
            catch { }
            return pts.Count > 0 ? pts.Min() : double.MaxValue;
        }

        // (vx, vy) are SKETCH coordinates (m); SelectByID2 takes model coordinates, which differ on every plane but
        // Front — so the nearest point of the active sketch is picked by distance (within 0.2 mm).
        private static void SelectSketchVertex(IModelDoc2 d, double vx, double vy)
        {
            d.ClearSelection2(true);
            var sk = d.SketchManager.ActiveSketch;
            if (sk == null) throw new ToolFail("NO_ACTIVE_SKETCH", "A sketch fillet / chamfer needs an open sketch (create_sketch or edit_sketch).");
            ISketchPoint best = null;
            double bestD = double.MaxValue;
            foreach (var pt in SketchPoints(sk))
            {
                double dist = Math.Sqrt((pt.X - vx) * (pt.X - vx) + (pt.Y - vy) * (pt.Y - vy));
                if (dist < bestD) { bestD = dist; best = pt; }
            }
            if (best == null || bestD > 0.0002 || !best.Select4(false, null))
                throw new ToolFail("VERTEX_NOT_FOUND", best == null ? "The sketch has no points."
                    : $"No sketch vertex at ({vx * 1000:0.###}, {vy * 1000:0.###}) mm — the nearest is {bestD * 1000:0.###} mm away, at ({best.X * 1000:0.###}, {best.Y * 1000:0.###}).");
        }

        // add_sketch_entities — N entity records in ONE call. The single-entity tool's calls
        // were the largest call-count item in both level-2-2 benchmark runs (62 and 16 calls); a batch
        // cuts both the round-trips and the per-call context echo. Same JSON-string idiom as
        // (the adapter parses `segments` into a JArray of records, each = add_sketch_entity's params).
        // NOT transactional: on the first failing record the run STOPS
        // and reports FAILED with the record index + how many records were already created (they remain
        // in the sketch — the caller decides whether to continue, fix, or discard). state_version bumps
        // only on COMPLETED. The echo is a compact per-type count, NOT per-segment result_geometry —
        // read the sketch back with analyze_model(sketch) when exact readback is needed.
        public ExecutionResponse AddSketchEntities(ToolRequest request) => Run(request, true, p =>
        {
            var segsTok = p?["segments"] as JArray;
            if (segsTok == null || segsTok.Count == 0)
                throw new ToolFail("MISSING_PARAMETER",
                    "segments is required: a JSON array of entity records, each with entity_type + its add_sketch_entity params.");

            var modelDoc = ActiveDocOrFail();

            var activeSketch = modelDoc.SketchManager.ActiveSketch;
            if (activeSketch == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");
            string sketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

            int done = 0;
            var counts = new Dictionary<string, int>();
            // ONE AddToDB bracket around the whole batch (frozen-exact coordinates, no
            // pixel-tolerance snapping) and ONE redraw at the end, instead of per segment.
            using (NoSnap())
            using (new AddToDbScope(modelDoc.SketchManager))
            {
                for (int i = 0; i < segsTok.Count; i++)
                {
                    var sp = segsTok[i] as JObject;
                    string et = sp?.Value<string>("entity_type");
                    if (sp == null || string.IsNullOrEmpty(et))
                        throw new ToolFail("MISSING_PARAMETER",
                            $"segments[{i}] must be an object with entity_type. {done} segment(s) before it were created and remain in the sketch.");

                    try { CreateSketchEntityCore(modelDoc, sp, et, sketchName, out _, out _); }
                    catch (ToolFail f)
                    {
                        ExecLog.Write($"add_sketch_entities: FAILED at segments[{i}] ({et}) after {done} created — {f.Code}");
                        throw new ToolFail(f.Code,
                            $"segments[{i}] ({et}): {f.Message} {done} segment(s) before it were created and remain in the sketch.");
                    }
                    done++;
                    string key = et.ToLowerInvariant();
                    counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }
            modelDoc.GraphicsRedraw2();

            ExecLog.Write($"add_sketch_entities: created {done} segment(s) in {sketchName}");

            var countsJson = new JObject();
            foreach (var kv in counts) countsJson[kv.Key] = kv.Value;

            return new ToolOutput
            {
                ActiveSketch = sketchName,
                Geometry = new JObject { ["segment_count"] = done, ["counts"] = countsJson }
            };
        });

        public ExecutionResponse AddSketchEntity(ToolRequest request) => Run(request, true, p =>
        {
            string entityType = PStr(p, "entity_type");
            if (string.IsNullOrEmpty(entityType))
                throw new ToolFail("MISSING_PARAMETER", "entity_type is required (rectangle, circle, line).");

            var modelDoc = ActiveDocOrFail();

            var activeSketch = modelDoc.SketchManager.ActiveSketch;
            if (activeSketch == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");

            // The entity switch lives in CreateSketchEntityCore — ONE switch shared with
            // add_sketch_entities, never forked. The core also applies the per-record
            // construction flag. Captured segment(s) feed the result_geometry echo below
            // (read back from SW, never the input, so snapping/inference drift is visible).
            //
            // AddToDB for the WHOLE create (not only for arc_center): all
            // coordinates arrive frozen-exact (exact coordinates from analyze or a drawing), so SW's input
            // inference/snapping must never touch them. Found live on the 1-2 flange rebuild: a
            // 1.59mm line (raised-face step) built fine in one document, then CreateLine returned
            // NULL for the same coords in the next — the pixel-based snap tolerance depends on the
            // window's zoom, so short segments nondeterministically collapse without AddToDB.
            ISketchSegment createdSeg;
            object[] createdSegs;
            using (NoSnap())
            using (new AddToDbScope(modelDoc.SketchManager))
                CreateSketchEntityCore(modelDoc, p, entityType, (activeSketch as IFeature)?.Name, out createdSeg, out createdSegs);

            string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

            // In-band echo of the REAL created geometry (Task 2). Read back from SW (never the
            // input), so AddToDB snapping / inference drift is visible to the caller without a
            // separate analyze pass. Best-effort; a read failure must not fail the create.
            object resultGeom = null;
            try
            {
                if (createdSeg != null)
                {
                    resultGeom = ReadSegment(createdSeg);
                }
                else if (createdSegs != null)
                {
                    var arr = new JArray();
                    foreach (var o in createdSegs)
                    {
                        var rs = ReadSegment(o as ISketchSegment);
                        if (rs != null) arr.Add(rs);
                    }
                    resultGeom = new JObject { ["kind"] = "rectangle", ["segments"] = arr };
                }
            }
            catch { resultGeom = null; }

            return new ToolOutput { ActiveSketch = activeSketchName, Geometry = resultGeom };
        });

        public ExecutionResponse AddDimension(ToolRequest request) => Run(request, true, p =>
        {
            double? px = PNumN(p, "px");
            double? py = PNumN(p, "py");
            double? value = PNumN(p, "value");
            double labelOffsetX = PNum(p, "label_offset_x", 0.0);
            double labelOffsetY = PNum(p, "label_offset_y", -0.015);
            // Optional (2026-10): dimension an EXISTING sketch (opened here, closed + rebuilt after),
            // pick a sketch POINT instead of a segment, and dimension point-to-ORIGIN with an
            // explicit sketch-axis orientation (AddSpecificDimension — no placement heuristics).
            string sketchName = PStr(p, "sketch_name", "");
            string entityType = (PStr(p, "entity_type", "segment")).ToLowerInvariant();
            bool toOrigin = PBool(p, "to_origin", false);
            string orientation = (PStr(p, "orientation", "auto")).ToLowerInvariant();

            if (px == null)
                throw new ToolFail("MISSING_PARAMETER", "px is required (X coordinate of a point on the target segment).");
            if (py == null)
                throw new ToolFail("MISSING_PARAMETER", "py is required (Y coordinate of a point on the target segment).");
            if (value == null)
                throw new ToolFail("MISSING_PARAMETER", "value is required.");

            var modelDoc = ActiveDocOrFail();

            if (entityType != "segment" && entityType != "point")
                throw new ToolFail("BAD_PARAMETER", "entity_type must be 'segment' or 'point'.");
            if (orientation != "auto" && orientation != "horizontal" && orientation != "vertical")
                throw new ToolFail("BAD_PARAMETER", "orientation must be 'auto', 'horizontal' or 'vertical'.");
            if (orientation != "auto" && !toOrigin)
                throw new ToolFail("BAD_PARAMETER", "orientation applies to a two-entity dimension: pass to_origin=true.");

            var activeSketch = modelDoc.SketchManager.ActiveSketch;
            bool openedHere = false;
            if (!string.IsNullOrEmpty(sketchName)
                && (activeSketch == null || !string.Equals((activeSketch as IFeature)?.Name, sketchName, StringComparison.Ordinal)))
            {
                ExitSketch(modelDoc);
                modelDoc.ClearSelection2(true);
                if (!SelectSketch(modelDoc, sketchName))
                    throw new ToolFail("SKETCH_NOT_FOUND", $"No sketch named '{sketchName}'.");
                modelDoc.EditSketch();
                activeSketch = modelDoc.SketchManager.ActiveSketch;
                openedHere = activeSketch != null;
            }
            if (activeSketch == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first (or pass sketch_name).");

            string activeSketchName = (activeSketch as IFeature)?.Name ?? "Sketch";

            string fullName, shortName;
            double effective;
            int dimType = 0;
            try   // a sketch opened here is closed again on every path
            {
                var dim = AddSketchDimension(modelDoc, activeSketch, px.Value, py.Value, entityType, toOrigin, orientation,
                                             labelOffsetX, labelOffsetY);

                // drive the dimension to the requested value
                var swDim = dim.GetDimension2(0) as IDimension;
                if (swDim != null) swDim.SystemValue = value.Value;

                // the created dimension's names, so callers never infer them: FullName is the form
                // analyze_model(features) reports and modify_dimension accepts; short = 'D1@Sketch3'
                fullName = swDim?.FullName ?? "";
                shortName = swDim != null ? $"{swDim.Name}@{activeSketchName}" : "";
                effective = value.Value;
                try
                {
                    var sv = swDim?.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null) as double[];
                    if (sv != null && sv.Length > 0) effective = sv[0];
                }
                catch { }
                try { dimType = dim.Type2; } catch { }
            }
            finally
            {
                if (openedHere) ExitSketch(modelDoc);
            }
            if (openedHere) modelDoc.EditRebuild3();
            ExecLog.Write($"add_dimension: {fullName} type={dimType} value={effective} sketch={activeSketchName} opened_here={openedHere}");

            // the short name like analyze_model(features) gives (modify_dimension takes both)
            fullName = ShortDimName(fullName);
            var existingDims = new List<string>();
            if (!string.IsNullOrEmpty(fullName)) existingDims.Add(fullName);

            return new ToolOutput
            {
                ActiveSketch = openedHere ? null : activeSketchName,
                Dimensions = existingDims,
                Geometry = new JObject
                {
                    ["dimension"] = fullName,
                    ["sketch"] = activeSketchName,
                    ["type"] = DimTypeName(dimType),
                    ["requested"] = value.Value,
                    ["effective"] = R6(effective),
                }
            };
        });

        // A driving dimension on the sketch entity nearest (px, py) — or that entity to the sketch origin — at the
        // geometry's CURRENT value (add_dimension then drives it, define_sketch keeps it). px/py and the label offsets are
        // SKETCH coordinates (m) — the frame add_sketch_entity draws in and analyze_model(sketch) reports;
        // SelectByID2/AddDimension2 take MODEL coordinates, so they go through the sketch transform.
        private IDisplayDimension AddSketchDimension(IModelDoc2 modelDoc, ISketch sketch, double px, double py, string entityType,
                                                     bool toOrigin, string orientation, double labelOffsetX, double labelOffsetY)
        {
            double[] pick = SketchToModel(sketch, px, py);
            double[] label = SketchToModel(sketch, px + labelOffsetX, py + labelOffsetY);
            if (pick == null || label == null)
                throw new ToolFail("TRANSFORM_FAILED", "Could not map sketch coordinates to model space.");

            // Commit and refresh the sketch geometry first — segments just created by add_sketch_entity may not
            // be queryable by SelectByID2 yet.
            modelDoc.ClearSelection2(true);
            modelDoc.GraphicsRedraw2();

            // No "input dimension value" dialog (SolidWorks would block waiting for the user) — for this call only
            using var dimPrefs = new PrefScope(_solidWorks).Toggle(swUserPreferenceToggle_e.swInputDimValOnCreate, false);

            // SelectByID2 with coordinates selects the nearest entity of the type — robust for any sketch geometry
            string selType = entityType == "point" ? "SKETCHPOINT" : "SKETCHSEGMENT";
            bool selected = modelDoc.Extension.SelectByID2("", selType, pick[0], pick[1], pick[2], false, 0, null, 0);
            if (selected && toOrigin)
                selected = SelectSketchOrigin(modelDoc, true);
            if (!selected)
                throw new ToolFail(entityType == "point" ? "POINT_NOT_FOUND" : "SEGMENT_NOT_FOUND",
                    $"No sketch {entityType} found at or near sketch ({px}, {py})"
                    + (toOrigin ? " (or the sketch origin could not be selected)." : "."));

            IDisplayDimension dim;
            if (orientation == "auto")
                dim = modelDoc.AddDimension2(label[0], label[1], label[2]) as IDisplayDimension;
            else
            {
                // horizontal / vertical relative to the SKETCH axes
                int dimErr = 0;
                dim = modelDoc.Extension.AddSpecificDimension(label[0], label[1], label[2],
                    orientation == "horizontal" ? (int)swDimensionType_e.swHorLinearDimension : (int)swDimensionType_e.swVertLinearDimension,
                    ref dimErr) as IDisplayDimension;
                if (dim == null) ExecLog.Write($"add_dimension: AddSpecificDimension error={dimErr}");
            }
            if (dim == null)
                throw new ToolFail("DIMENSION_CREATION_FAILED", "SolidWorks returned no dimension for the selection.");
            return dim;
        }

        // SKETCH frame → MODEL space. The sketch frame is the one add_sketch_entity draws in and
        // analyze_model(sketch) reports (ISketchPoint.X/Y): origin + xdir/ydir as echoed by create_sketch
        // and analyze_model(sketch).plane.frame. Selection/annotation APIs (SelectByID2, AddDimension2,
        // AddSpecificDimension) take MODEL coordinates. Returns {x,y,z} in meters, or null.
        private double[] SketchToModel(ISketch sketch, double u, double v)
        {
            try
            {
                var s2m = sketch?.ModelToSketchTransform?.IInverse();
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                if (s2m == null || mu == null) return null;
                var pt = mu.CreatePoint(new double[] { u, v, 0.0 }) as IMathPoint;
                pt = pt?.MultiplyTransform(s2m) as IMathPoint;
                return pt?.ArrayData as double[];
            }
            catch (Exception ex) { ExecLog.Write($"SketchToModel err: {ex.Message}"); return null; }
        }

        // swDimensionType_e as stable lowercase names.
        private static string DimTypeName(int t)
        {
            switch (t)
            {
                case 2: return "linear";
                case 3: return "angular";
                case 5: return "radial";
                case 6: return "diameter";
                case 11: return "horizontal";
                case 12: return "vertical";
                default: return "type_" + t;
            }
        }

        public ExecutionResponse AddSketchConstraint(ToolRequest request) => Run(request, true, p =>
        {
            string constraintType = PStr(p, "constraint_type");
            if (string.IsNullOrEmpty(constraintType))
                throw new ToolFail("MISSING_PARAMETER", "constraint_type is required (horizontal, vertical, coincident, parallel, perpendicular, tangent, equal, midpoint).");

            double? px1 = PNumN(p, "px1");
            double? py1 = PNumN(p, "py1");
            if (px1 == null || py1 == null)
                throw new ToolFail("MISSING_PARAMETER", "px1 and py1 are required (point on the first entity).");

            double? px2 = PNumN(p, "px2");
            double? py2 = PNumN(p, "py2");
            string entityType1 = PStr(p, "entity_type1", "SKETCHSEGMENT");
            string entityType2 = PStr(p, "entity_type2", "SKETCHSEGMENT");

            var modelDoc = ActiveDocOrFail();

            if (modelDoc.SketchManager.ActiveSketch == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch. Call create_sketch first.");

            string swConstraintId;
            bool needsTwoEntities = false;
            switch (constraintType.ToLowerInvariant())
            {
                case "horizontal":    swConstraintId = "sgHORIZONTAL";    break;
                case "vertical":      swConstraintId = "sgVERTICAL";      break;
                case "coincident":    swConstraintId = "sgCOINCIDENT";    needsTwoEntities = true; break;
                case "parallel":      swConstraintId = "sgPARALLEL";      needsTwoEntities = true; break;
                case "perpendicular": swConstraintId = "sgPERPENDICULAR"; needsTwoEntities = true; break;
                case "tangent":       swConstraintId = "sgTANGENT";       needsTwoEntities = true; break;
                case "equal":         swConstraintId = "sgSAMELENGTH";    needsTwoEntities = true; break;
                case "midpoint":      swConstraintId = "sgATMIDDLE";      needsTwoEntities = true; break;
                default:
                    throw new ToolFail("BAD_PARAMETER",
                        $"constraint_type '{constraintType}' is not supported. Supported: horizontal, vertical, coincident, parallel, perpendicular, tangent, equal, midpoint.");
            }

            if (needsTwoEntities && (px2 == null || py2 == null))
                throw new ToolFail("MISSING_PARAMETER", $"constraint_type '{constraintType}' requires px2 and py2 (point on the second entity).");

            // Sketch coordinates → model (same convention and fix as add_dimension).
            var sk = modelDoc.SketchManager.ActiveSketch;
            double[] m1 = SketchToModel(sk, px1.Value, py1.Value);
            // entity_type2='ORIGIN': the second entity is the sketch origin (px2/py2 ignored) —
            // e.g. coincident(corner point, origin) to fix a profile in place.
            bool secondIsOrigin = string.Equals(entityType2, "ORIGIN", StringComparison.OrdinalIgnoreCase);
            double[] m2 = needsTwoEntities && !secondIsOrigin ? SketchToModel(sk, px2.Value, py2.Value) : null;
            if (m1 == null || (needsTwoEntities && !secondIsOrigin && m2 == null))
                throw new ToolFail("TRANSFORM_FAILED", "Could not map sketch coordinates to model space.");

            modelDoc.ClearSelection2(true);
            bool sel1 = modelDoc.Extension.SelectByID2("", entityType1, m1[0], m1[1], m1[2], false, 0, null, 0);
            if (!sel1)
                throw new ToolFail("ENTITY_NOT_FOUND", $"No {entityType1} found at or near ({px1.Value}, {py1.Value}).");

            if (needsTwoEntities)
            {
                bool sel2 = secondIsOrigin
                    ? SelectSketchOrigin(modelDoc, true)
                    : modelDoc.Extension.SelectByID2("", entityType2, m2[0], m2[1], m2[2], true, 0, null, 0);
                if (!sel2)
                    throw new ToolFail("ENTITY_NOT_FOUND", $"No {entityType2} found at or near ({px2.Value}, {py2.Value}).");
            }

            modelDoc.SketchAddConstraints(swConstraintId);

            string activeSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name ?? "Sketch";

            return new ToolOutput { ActiveSketch = activeSketchName };
        });

        // -----------------------------------------------------------------------
        // edit_sketch
        // -----------------------------------------------------------------------
        public ExecutionResponse EditSketch(ToolRequest request) => Run(request, true, p =>
        {
            string sketchName = PStr(p, "sketch_name") ?? throw new ToolFail("MISSING_PARAMETER", "sketch_name is required (name of existing sketch feature, e.g. 'Sketch1').");
            var d = ActiveDocOrFail();
            if (d.SketchManager.ActiveSketch != null)
                throw new ToolFail("SKETCH_ALREADY_ACTIVE", "A sketch is already active. Exit the current sketch first (call extrude_feature or close the sketch).");
            if (!SelectSketch(d, sketchName))
                throw new ToolFail("SKETCH_NOT_FOUND", $"Sketch '{sketchName}' not found. Ensure the name matches exactly (e.g. 'Sketch1').");
            d.EditSketch();
            if (d.SketchManager.ActiveSketch == null)
                throw new ToolFail("SKETCH_EDIT_FAILED", $"EditSketch() was called but no sketch became active. Ensure '{sketchName}' is a valid sketch feature.");
            return new ToolOutput { ActiveSketch = sketchName };
        });

        /// <summary>
        /// CreateFillet/CreateChamfer apply the corner to the model, but the new sketch segment
        /// isn't regenerated/painted until the user clicks in the view. EditRebuild3 (Ctrl+B)
        /// regenerates it, but exits the active sketch — so we re-enter edit mode on the same
        /// sketch by name. Net effect: the fillet/chamfer appears immediately and the user stays
        /// in the sketch.
        /// </summary>
        private void RegenerateSketchInPlace(IModelDoc2 modelDoc, string sketchName)
        {
            modelDoc.EditRebuild3();
            if (!string.IsNullOrEmpty(sketchName) && modelDoc.SketchManager.ActiveSketch == null)
            {
                modelDoc.ClearSelection2(true);
                if (SelectSketch(modelDoc, sketchName))
                    modelDoc.EditSketch();
                modelDoc.ClearSelection2(true);
            }
            modelDoc.GraphicsRedraw2();
        }

        // add_sketch_text — text in the ACTIVE sketch (marking / engraving: cut-extrude it, or export it with the flat
        // pattern DXF via sketches=True → layer MARK). IModelDoc2.InsertSketchText
        public ExecutionResponse AddSketchText(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            if (d.SketchManager.ActiveSketch == null) throw new ToolFail("SKETCH_NOT_ACTIVE", "Open a sketch first (create_sketch on a plane or face).");
            string text = PStr(p, "text") ?? throw new ToolFail("MISSING_PARAMETER", "text is required.");
            // width and spacing are percentages: spacing 0 put every letter on top of the first ("ABC" one blot, and a
            // longer text an outline SolidWorks would not cut)
            var st = d.InsertSketchText(PNum(p, "x_mm", 0) / MM, PNum(p, "y_mm", 0) / MM, 0, text, 0, 0, 0, 100, 100) as ISketchText
                     ?? throw new ToolFail("TEXT_FAILED", "InsertSketchText returned null.");
            var tf = st.GetTextFormat() as ITextFormat;
            if (tf != null)
            {
                tf.CharHeight = PNum(p, "height_mm", 5) / MM;
                string font = PStr(p, "font");
                if (font != null) tf.TypeFaceName = font;
                tf.Bold = PBool(p, "bold", false);
                tf.Escapement = PNum(p, "angle_deg", 0) * Math.PI / 180;
                st.SetTextFormat(false, tf);
            }
            d.GraphicsRedraw2();
            var r = new JObject { ["text"] = st.Text, ["sketch"] = (d.SketchManager.ActiveSketch as IFeature)?.Name };
            try { var tf2 = st.GetTextFormat() as ITextFormat; r["height_mm"] = Math.Round(tf2.CharHeight * MM, 2); r["font"] = tf2.TypeFaceName; } catch { }
            return r;
        });
    }
}
