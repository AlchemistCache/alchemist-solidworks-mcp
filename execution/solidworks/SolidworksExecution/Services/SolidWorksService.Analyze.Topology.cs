using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: analyze_model(faces | edges | feature_map) — the solid's faces and edges with a stable
    // index (the `i` every face_index / edge_index means), `near` filtering, topology snapshots and the per-feature map.
    public partial class SolidWorksService
    {
        // Single-edge JSON (start/end/MIDPOINT, +length for closed edges) — the shared reader behind both
        // ReadEdges and get_selection, so an analyzed edge and a user-selected edge report identical fields
        // (the "analyze ⊆ everything else" invariant). `idx` is the edge's stable index.
        private JObject BuildEdgeJson(IEdge edge, int idx)
        {
            var ej = new JObject();
            ej["i"] = idx;
            try
            {
                // GetCurveParams2 layout: [0..2]=start xyz, [3..5]=end xyz, [6]=uStart, [7]=uEnd, [8]=sense.
                var cp = edge.GetCurveParams2() as double[];
                if (cp != null && cp.Length >= 8)
                {
                    // Round to 6 decimals (1µm — far below selection tolerance). Keeps the JSON compact
                    // (a 304-edge part otherwise blows the MCP result token cap) AND kills near-zero noise.
                    ej["start"] = new JArray { R6(cp[0]), R6(cp[1]), R6(cp[2]) };
                    ej["end"] = new JArray { R6(cp[3]), R6(cp[4]), R6(cp[5]) };
                    double dx = cp[0] - cp[3], dy = cp[1] - cp[4], dz = cp[2] - cp[5];
                    double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (chord > 1e-9)
                    {
                        // Open edge (the common case): the chord midpoint (start+end)/2 is EXACT for a line
                        // and a robust pick-point for an arc. We do NOT call IGetCurve()/GetLength3() here —
                        // on a large part those per-edge COM round-trips dominate (gear: 26s of a 58s total).
                        ej["mid"] = new JArray { R6((cp[0] + cp[3]) / 2.0), R6((cp[1] + cp[4]) / 2.0), R6((cp[2] + cp[5]) / 2.0) };
                    }
                    else
                    {
                        // Closed edge (full circle, start==end): the chord midpoint is the center, not on the
                        // edge — need the curve for an on-edge midpoint. Rare (a handful per part), so the
                        // IGetCurve cost is negligible; we also report true length since we hold the curve.
                        var curve = edge.IGetCurve();
                        if (curve != null)
                        {
                            var mid = curve.Evaluate2((cp[6] + cp[7]) / 2.0, 0) as double[];
                            if (mid != null && mid.Length >= 3)
                                ej["mid"] = new JArray { R6(mid[0]), R6(mid[1]), R6(mid[2]) };
                            try { ej["length"] = R6(curve.GetLength3(cp[6], cp[7])); } catch { }
                        }
                    }
                }
            }
            catch { }
            return ej;
        }

        // Single-face JSON (planar flag + normal/area/representative point for planar faces) — the shared
        // reader behind both ReadFaces and get_selection. `idx` is the face's stable index.
        private JObject BuildFaceJson(IFace2 face, int idx)
        {
            var fj = new JObject();
            fj["i"] = idx;
            try
            {
                var surf = face.IGetSurface();
                bool planar = surf != null && surf.IsPlane();
                fj["planar"] = planar;
                try { fj["area"] = R6(face.GetArea()); } catch { }
                if (planar)
                {
                    // Plane normal (model space) — the sketch-plane direction for on_face.
                    var n = face.Normal as double[];
                    if (n != null && n.Length >= 3)
                        fj["normal"] = new JArray { R6(n[0]), R6(n[1]), R6(n[2]) };
                    // A representative point ON the plane (UV-mid of the trimmed face). Informational —
                    // selection is by index, so this need not be strictly inside a face with holes.
                    var uv = face.GetUVBounds() as double[];
                    if (uv != null && uv.Length >= 4)
                    {
                        var pt = surf.Evaluate((uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0, 0, 0) as double[];
                        if (pt != null && pt.Length >= 3)
                            fj["point"] = new JArray { R6(pt[0]), R6(pt[1]), R6(pt[2]) };
                    }
                }
                else if (surf != null)
                {
                    // Non-planar faces: report the surface KIND + matching geometry (Phase B —
                    // a concentric mate's anchor is a CYLINDRICAL face; the resolver matches it
                    // by axis + radius). CylinderParams layout: [0..2]=origin, [3..5]=axis
                    // (unit), [6]=radius. All faces also get a representative on-surface point.
                    try
                    {
                        if (surf.IsCylinder())
                        {
                            fj["kind"] = "cylinder";
                            var cp2 = surf.CylinderParams as double[];
                            if (cp2 == null && surf.CylinderParams is object[] cpo)
                                cp2 = Array.ConvertAll(cpo, o => (double)o);
                            if (cp2 != null && cp2.Length >= 7)
                            {
                                fj["origin"] = new JArray { R6(cp2[0]), R6(cp2[1]), R6(cp2[2]) };
                                fj["axis"] = new JArray { R6(cp2[3]), R6(cp2[4]), R6(cp2[5]) };
                                fj["radius"] = R6(cp2[6]);
                            }
                        }
                        else if (surf.IsCone()) fj["kind"] = "cone";
                        else if (surf.IsSphere()) fj["kind"] = "sphere";
                        var uv2 = face.GetUVBounds() as double[];
                        if (uv2 != null && uv2.Length >= 4)
                        {
                            var pt2 = surf.Evaluate((uv2[0] + uv2[1]) / 2.0, (uv2[2] + uv2[3]) / 2.0, 0, 0) as double[];
                            if (pt2 != null && pt2.Length >= 3)
                                fj["point"] = new JArray { R6(pt2[0]), R6(pt2[1]), R6(pt2[2]) };
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return fj;
        }

        private JObject ReadEdges(IPartDoc partDoc, double[] near = null, int k = 0, double[] axis = null)
        {
            var root = new JObject();
            var edgesArr = new JArray();
            int idx = 0;
            // Edge enumeration is COM-round-trip-bound (SolidWorks is out-of-process). On a large part
            // (gear = 304 edges) the per-edge curve calls dominated, so we keep them off the open-edge
            // path (see below) and log the total for visibility on big models.
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            // BuildEdgeJson assigns the STABLE full-enumeration index `i` — kept even after a near/k filter, so a
            // filtered result still selects by the same index.
            foreach (var edge in FlattenEdges(partDoc))
                edgesArr.Add(BuildEdgeJson(edge, idx++));
            swTotal.Stop();
            ExecLog.Write($"ReadEdges: edges={edgesArr.Count} total={swTotal.ElapsedMilliseconds}ms near={(near != null)} k={k}");
            int totalCount = edgesArr.Count;

            if (near != null)
            {
                // The edge's representative point is its `mid`; the direction filter (axis) compares the
                // edge's CHORD direction (start→end) — for a curved edge that's the chord, documented.
                var filtered = NarrowByNear(edgesArr, near, k, axis, "mid",
                    (ej) => EdgeChordDir(ej));
                root["edge_count"] = filtered.Count;
                root["total_edge_count"] = totalCount;
                root["near"] = new JArray { R6(near[0]), R6(near[1]), R6(near[2]) };
                if (k > 0) root["k"] = k;
                if (axis != null) root["axis"] = new JArray { R6(axis[0]), R6(axis[1]), R6(axis[2]) };
                root["edges"] = filtered;
                return root;
            }
            root["edge_count"] = totalCount;
            root["edges"] = edgesArr;
            return root;
        }

        // Face list with a stable index — the twin of ReadEdges. The `i` matches the same
        // GetBodies2(swSolidBody,true) → GetFaces() enumeration create_sketch(on_face, face_index) walks,
        // so a caller picks a face to sketch on by index instead of a fragile coordinate pick. Per planar
        // face we report normal + area + a representative on-plane point; non-planar faces still get an
        // index (so the numbering stays aligned) but no normal.
        private JObject ReadFaces(IPartDoc partDoc, double[] near = null, int k = 0, double[] axis = null)
        {
            var root = new JObject();
            var facesArr = new JArray();
            int idx = 0;
            foreach (var face in FlattenFaces(partDoc))
                facesArr.Add(BuildFaceJson(face, idx++));
            ExecLog.Write($"ReadFaces: faces={facesArr.Count} near={(near != null)} k={k}");
            int totalCount = facesArr.Count;

            if (near != null)
            {
                // The face's representative point is `point`; the axis filter compares the (planar-face)
                // NORMAL — a non-planar face has no single normal, so it's dropped when an axis is given.
                var filtered = NarrowByNear(facesArr, near, k, axis, "point",
                    (fj) => (fj["normal"] as JArray) != null
                        ? new[] { fj["normal"][0].Value<double>(), fj["normal"][1].Value<double>(), fj["normal"][2].Value<double>() }
                        : null);
                root["face_count"] = filtered.Count;
                root["total_face_count"] = totalCount;
                root["near"] = new JArray { R6(near[0]), R6(near[1]), R6(near[2]) };
                if (k > 0) root["k"] = k;
                if (axis != null) root["axis"] = new JArray { R6(axis[0]), R6(axis[1]), R6(axis[2]) };
                root["faces"] = filtered;
                return root;
            }
            root["face_count"] = totalCount;
            root["faces"] = facesArr;
            return root;
        }

        // ------------------------------------------------------------------
        // Targeted near/k narrowing (shared by edges + faces).
        // ------------------------------------------------------------------

        // Parse a "[x,y,z]" JSON-string param (the) into a double[3]; null/blank →
        // null (= "not requested"), which the readers treat as the full-dump path.
        // An optional [x, y, z] parameter (a list or its JSON text). A malformed one is BAD_PARAMETER — it used to be
        // ignored silently, and the caller got the full, unfiltered dump.
        private static double[] Vec3(JObject p, string key)
        {
            var a = PArr(p, key);
            if (a.Count == 0) return null;
            try
            {
                if (a.Count == 3) return a.Select(t => t.Value<double>()).ToArray();
            }
            catch (FormatException) { }
            throw new ToolFail("BAD_PARAMETER", $"{key} must be [x, y, z] — three numbers.");
        }

        // Chord direction (unit) of an edge JSON built by BuildEdgeJson (start→end); null if degenerate.
        private static double[] EdgeChordDir(JObject ej)
        {
            var s = ej["start"] as JArray; var e = ej["end"] as JArray;
            if (s == null || e == null) return null;
            double dx = e[0].Value<double>() - s[0].Value<double>();
            double dy = e[1].Value<double>() - s[1].Value<double>();
            double dz = e[2].Value<double>() - s[2].Value<double>();
            double L = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (L < 1e-9) return null;
            return new[] { dx / L, dy / L, dz / L };
        }

        // Keep the k entries of `items` nearest (Euclidean) to `near` — measured from each item's
        // representative point (`ptKey` = "mid" for edges, "point" for faces). Optional `axis`: keep only
        // items whose direction (dirOf) is ~parallel to axis (|dot| ≥ 0.999), so e.g. "the nearest face on
        // a +Z-normal plane" is one call. k ≤ 0 ⇒ no count cap (all that pass the axis filter, sorted by
        // distance). Items with no representative point are dropped (can't be ranked). The returned JObjects
        // are the SAME objects (stable `i` preserved), each annotated with `dist` (meters, R6).
        private static JArray NarrowByNear(JArray items, double[] near, int k, double[] axis,
            string ptKey, Func<JObject, double[]> dirOf)
        {
            double[] axisN = null;
            if (axis != null)
            {
                double al = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
                if (al > 1e-9) axisN = new[] { axis[0] / al, axis[1] / al, axis[2] / al };
            }
            var scored = new List<KeyValuePair<double, JObject>>();
            foreach (var it in items)
            {
                var jo = it as JObject;
                if (jo == null) continue;
                var pt = jo[ptKey] as JArray;
                if (pt == null || pt.Count < 3) continue;
                if (axisN != null)
                {
                    var dir = dirOf(jo);
                    if (dir == null) continue;
                    double dot = Math.Abs(dir[0] * axisN[0] + dir[1] * axisN[1] + dir[2] * axisN[2]);
                    if (dot < 0.999) continue;
                }
                double dx = pt[0].Value<double>() - near[0];
                double dy = pt[1].Value<double>() - near[1];
                double dz = pt[2].Value<double>() - near[2];
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                jo["dist"] = R6(dist);
                scored.Add(new KeyValuePair<double, JObject>(dist, jo));
            }
            scored.Sort((a, b) => a.Key.CompareTo(b.Key));
            var outArr = new JArray();
            int limit = k > 0 ? Math.Min(k, scored.Count) : scored.Count;
            for (int i = 0; i < limit; i++) outArr.Add(scored[i].Value);
            return outArr;
        }

        // ------------------------------------------------------------------
        // feature_map — rollback-bar geometry attribution (analysis_type='feature_map')
        // ------------------------------------------------------------------

        // Feature types that can never alter SOLID geometry: they appear in the map for tree coverage
        // but get no rollback stop / snapshot (a full topology snapshot per sketch or ref plane would
        // pay real COM cost for a guaranteed-empty delta).
        private static readonly HashSet<string> NonSolidFeatureTypes = new HashSet<string>
        {
            "ProfileFeature", "3DProfileFeature", "RefPlane", "RefAxis", "RefPoint", "CoordSys",
            "OriginProfileFeature",
            // The SheetMetal DEFINITION feature (thickness/radius container): it builds no geometry
            // itself, and live on 4-1 EditRollback(ToAfterFeature, "Sheet-Metal1") JUMPED THE BAR TO
            // THE END (the stop showed the full final body and Base-Flange1 then diffed NEGATIVE).
            // Skipping it attributes the blank correctly to SMBaseFlange.
            "SheetMetal"
        };

        // 1µm — a rollback stop REPLAYS the same history, so surviving geometry matches to double
        // precision; the tolerance only needs to sit far below feature scale (same grid as R6).
        private const double MapTol = 1e-6;

        private sealed class EdgeSnap
        {
            public double[] Start, End, Mid;
            public double Len;      // chord (open) / circumference (closed) — identity + size hint
            public bool Matched;
        }

        private sealed class FaceSnap
        {
            public bool Planar;
            public double[] Normal; // planar faces only
            public double[] Point;  // representative on-surface point (UV mid)
            public double Area;
            public bool Matched;
        }

        private static bool PtEq(double[] a, double[] b)
        {
            return a != null && b != null
                && Math.Abs(a[0] - b[0]) <= MapTol
                && Math.Abs(a[1] - b[1]) <= MapTol
                && Math.Abs(a[2] - b[2]) <= MapTol;
        }

        private static JArray Pt6(double[] a) { return new JArray { R6(a[0]), R6(a[1]), R6(a[2]) }; }

        // Snapshot every solid body's edges/faces at the CURRENT rollback stop. Same enumeration the
        // shared readers walk (GetBodies2(swSolidBody,true) → GetEdges()/GetFaces()); open-edge midpoints
        // stay off the IGetCurve path. counts = {faces, edges, vertices} (authoritative, from
        // the body counters, so the delta is right even if an exotic face yields no representative point).
        private void SnapshotTopology(IPartDoc partDoc, List<EdgeSnap> edges, List<FaceSnap> faces, int[] counts)
        {
            foreach (var body in SolidBodies(partDoc))
            {
                counts[0] += body.GetFaceCount();
                counts[1] += body.GetEdgeCount();
                counts[2] += body.GetVertexCount();

                object[] es = body.GetEdges() as object[];
                if (es != null)
                    foreach (var e in es)
                    {
                        var edge = e as IEdge;
                        if (edge == null) continue;
                        var cp = edge.GetCurveParams2() as double[];
                        if (cp == null || cp.Length < 8) continue;
                        var snap = new EdgeSnap
                        {
                            Start = new[] { cp[0], cp[1], cp[2] },
                            End = new[] { cp[3], cp[4], cp[5] }
                        };
                        double dx = cp[0] - cp[3], dy = cp[1] - cp[4], dz = cp[2] - cp[5];
                        double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (chord > 1e-9)
                        {
                            snap.Mid = new[] { (cp[0] + cp[3]) / 2.0, (cp[1] + cp[4]) / 2.0, (cp[2] + cp[5]) / 2.0 };
                            snap.Len = chord;
                        }
                        else
                        {
                            // Closed edge (full circle): on-edge midpoint via the curve (rare — same
                            // trade BuildEdgeJson makes).
                            var curve = edge.IGetCurve();
                            if (curve != null)
                            {
                                var mid = curve.Evaluate2((cp[6] + cp[7]) / 2.0, 0) as double[];
                                if (mid != null && mid.Length >= 3) snap.Mid = new[] { mid[0], mid[1], mid[2] };
                                try { snap.Len = curve.GetLength3(cp[6], cp[7]); } catch { }
                            }
                            if (snap.Mid == null) snap.Mid = snap.Start;
                        }
                        edges.Add(snap);
                    }

                object[] fs = body.GetFaces() as object[];
                if (fs != null)
                    foreach (var f in fs)
                    {
                        var face = f as IFace2;
                        if (face == null) continue;
                        var fsnap = new FaceSnap();
                        try
                        {
                            var surf = face.IGetSurface();
                            fsnap.Planar = surf != null && surf.IsPlane();
                            try { fsnap.Area = face.GetArea(); } catch { }
                            if (fsnap.Planar)
                            {
                                var n = face.Normal as double[];
                                if (n != null && n.Length >= 3) fsnap.Normal = new[] { n[0], n[1], n[2] };
                            }
                            var uv = face.GetUVBounds() as double[];
                            if (surf != null && uv != null && uv.Length >= 4)
                            {
                                var pt = surf.Evaluate((uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0, 0, 0) as double[];
                                if (pt != null && pt.Length >= 3) fsnap.Point = new[] { pt[0], pt[1], pt[2] };
                            }
                        }
                        catch { }
                        if (fsnap.Point != null) faces.Add(fsnap);
                    }
            }
        }

        // Geometric diff between two rollback stops — matching is by GEOMETRY (midpoints / planes),
        // never by index: SolidWorks renumbers faces/edges at every stop. Adds consumed_edges /
        // created_faces to the entry (only when non-empty — keeps the map compact).
        private void DiffTopology(List<EdgeSnap> prevE, List<FaceSnap> prevF,
            List<EdgeSnap> curE, List<FaceSnap> curF, JObject entry)
        {
            // The prev snapshot was the CUR snapshot of the previous diff, so its Matched flags are
            // stale — an edge that survived step N would silently skip the consumed check at step N+1
            // (live-caught: both of level-2-2's chamfers reported no consumed edge). Reset them all.
            foreach (var pe in prevE) pe.Matched = false;
            foreach (var pf in prevF) pf.Matched = false;

            // Edge pass 1 — identical edges (same midpoint AND length) survive unchanged.
            foreach (var pe in prevE)
                foreach (var ce in curE)
                {
                    if (ce.Matched) continue;
                    if (PtEq(pe.Mid, ce.Mid) && Math.Abs(pe.Len - ce.Len) <= MapTol)
                    { pe.Matched = ce.Matched = true; break; }
                }

            // An unmatched previous edge whose BOTH endpoints are gone was CONSUMED (the fillet/chamfer
            // target). If an endpoint survives, the feature merely TRIMMED/extended the edge (e.g. the
            // neighbours of a filleted corner — their midpoints move by r/2, far beyond tolerance) and
            // it is NOT reported: that separation is what keeps consumed_edges a clean anchor list.
            var consumed = new JArray();
            foreach (var pe in prevE)
            {
                if (pe.Matched) continue;
                bool startSurvives = false, endSurvives = false;
                foreach (var ce in curE)
                {
                    if (!startSurvives && (PtEq(pe.Start, ce.Start) || PtEq(pe.Start, ce.End))) startSurvives = true;
                    if (!endSurvives && (PtEq(pe.End, ce.Start) || PtEq(pe.End, ce.End))) endSurvives = true;
                    if (startSurvives && endSurvives) break;
                }
                if (!startSurvives && !endSurvives)
                    consumed.Add(new JObject { ["mid"] = Pt6(pe.Mid), ["len"] = R6(pe.Len) });
            }
            if (consumed.Count > 0) entry["consumed_edges"] = consumed;

            // Face pass 1 — identical faces (same representative point AND area).
            foreach (var pf in prevF)
                foreach (var cf in curF)
                {
                    if (cf.Matched) continue;
                    if (PtEq(pf.Point, cf.Point)
                        && Math.Abs(pf.Area - cf.Area) <= 1e-9 + 1e-6 * Math.Max(pf.Area, cf.Area))
                    { pf.Matched = cf.Matched = true; break; }
                }

            // Face pass 2 — surviving PLANES: an unmatched current planar face lying on a plane that
            // already existed is a TRIMMED/extended face (its UV-mid representative point moves with the
            // trim), not a new surface. Only genuinely NEW surfaces are reported as created. (A trimmed
            // NON-planar face can still reappear here — known noise, the anchors come from consumed_edges.)
            var created = new JArray();
            foreach (var cf in curF)
            {
                if (cf.Matched) continue;
                bool onExistingPlane = false;
                if (cf.Planar && cf.Normal != null)
                {
                    foreach (var pf in prevF)
                    {
                        // Only a previous face that CHANGED (unmatched in pass 1) can have been trimmed/
                        // extended into this one. An untouched coplanar face (e.g. one pad top when a
                        // second, disconnected pad top is created on the same plane) must not swallow a
                        // genuinely new face — that hid the second pad's top from created_faces.
                        if (pf.Matched || !pf.Planar || pf.Normal == null) continue;
                        double dot = pf.Normal[0] * cf.Normal[0] + pf.Normal[1] * cf.Normal[1] + pf.Normal[2] * cf.Normal[2];
                        if (dot < 1.0 - 1e-6) continue; // must be parallel, SAME sign
                        double d = (pf.Point[0] - cf.Point[0]) * cf.Normal[0]
                                 + (pf.Point[1] - cf.Point[1]) * cf.Normal[1]
                                 + (pf.Point[2] - cf.Point[2]) * cf.Normal[2];
                        if (Math.Abs(d) <= MapTol) { onExistingPlane = true; break; }
                    }
                }
                if (onExistingPlane) continue;
                var cj = new JObject { ["point"] = Pt6(cf.Point), ["planar"] = cf.Planar };
                if (cf.Normal != null) cj["normal"] = Pt6(cf.Normal);
                cj["area"] = R6(cf.Area);
                created.Add(cj);
            }
            if (created.Count > 0) entry["created_faces"] = created;
        }

        private JObject BuildFeatureMap(IModelDoc2 modelDoc, IPartDoc partDoc, string fromFeature, string toFeature)
        {

            // Cache the walk UP FRONT, at the fully-rolled-forward state: features BEHIND a rolled-back
            // bar report IsSuppressed()==true, so name/type/suppression must be read before the bar moves.
            var names = new List<string>(); var types = new List<string>(); var supp = new List<bool>();
            foreach (var feat in Features(modelDoc))
            {
                string t = feat.GetTypeName2() ?? "";
                if (!NoiseFeatureTypes.Contains(t))
                {
                    names.Add(feat.Name); types.Add(t);
                    bool s = false; try { s = feat.IsSuppressed(); } catch { }
                    supp.Add(s);
                }
            }
            if (names.Count == 0)
                throw new ToolFail("FEATURE_MAP_FAILED", "The feature tree is empty.");

            int startIdx = 0, endIdx = names.Count - 1;
            if (!string.IsNullOrEmpty(fromFeature))
            {
                startIdx = names.IndexOf(fromFeature);
                if (startIdx < 0)
                    throw new ToolFail("FEATURE_NOT_FOUND", $"from_feature '{fromFeature}' not found. Available: {string.Join(", ", names)}");
            }
            if (!string.IsNullOrEmpty(toFeature))
            {
                endIdx = names.IndexOf(toFeature);
                if (endIdx < 0)
                    throw new ToolFail("FEATURE_NOT_FOUND", $"to_feature '{toFeature}' not found. Available: {string.Join(", ", names)}");
            }
            if (endIdx < startIdx)
                throw new ToolFail("BAD_PARAMETER", $"to_feature '{toFeature}' precedes from_feature '{fromFeature}' in the tree.");

            // First rollback stop = the first unsuppressed solid-affecting feature in range; the baseline
            // snapshot is taken just BEFORE it.
            int firstStop = -1;
            for (int i = startIdx; i <= endIdx; i++)
                if (!supp[i] && !NonSolidFeatureTypes.Contains(types[i])) { firstStop = i; break; }

            var mapArr = new JArray();
            bool rolled = false;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var prevEdges = new List<EdgeSnap>(); var prevFaces = new List<FaceSnap>(); var prevCounts = new int[3];
                if (firstStop >= 0)
                {
                    rolled = true;
                    bool okBase = modelDoc.FeatureManager.EditRollback(
                        (int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, names[firstStop]);
                    if (!okBase)
                        throw new ToolFail("ROLLBACK_FAILED", $"Could not move the rollback bar before '{names[firstStop]}' (is a sketch being edited?).");
                    SnapshotTopology(partDoc, prevEdges, prevFaces, prevCounts);
                }

                for (int i = startIdx; i <= endIdx; i++)
                {
                    var entry = new JObject { ["feature"] = names[i], ["type"] = types[i] };
                    if (supp[i]) { entry["suppressed"] = true; mapArr.Add(entry); continue; }
                    if (NonSolidFeatureTypes.Contains(types[i])) { mapArr.Add(entry); continue; }

                    bool ok = modelDoc.FeatureManager.EditRollback(
                        (int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, names[i]);
                    if (!ok)
                    {
                        // Bar did not move — this feature's changes FOLD INTO the next stop's delta.
                        entry["error"] = "ROLLBACK_FAILED (delta folds into the next feature)";
                        mapArr.Add(entry);
                        continue;
                    }

                    var curEdges = new List<EdgeSnap>(); var curFaces = new List<FaceSnap>(); var curCounts = new int[3];
                    SnapshotTopology(partDoc, curEdges, curFaces, curCounts);
                    entry["delta"] = new JObject
                    {
                        ["faces"] = curCounts[0] - prevCounts[0],
                        ["edges"] = curCounts[1] - prevCounts[1],
                        ["vertices"] = curCounts[2] - prevCounts[2]
                    };
                    DiffTopology(prevEdges, prevFaces, curEdges, curFaces, entry);
                    prevEdges = curEdges; prevFaces = curFaces; prevCounts = curCounts;
                    mapArr.Add(entry);
                }
            }
            catch
            {
                if (rolled) RollForward(modelDoc);   // non-destructive: the bar goes back to the END on every path
                throw;
            }
            if (rolled && !RollForward(modelDoc)) throw new ToolFail("ROLLBACK_FAILED", RollbackStuck);
            swTotal.Stop();
            ExecLog.Write($"FeatureMap: entries={mapArr.Count} range=[{names[startIdx]}..{names[endIdx]}] total={swTotal.ElapsedMilliseconds}ms");

            var root = new JObject();
            root["feature_count"] = mapArr.Count;
            root["map"] = mapArr;
            return root;
        }
    }
}
