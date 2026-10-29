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
    // SolidWorksService partial: part-modeling features (extrude/revolve/sweep/loft, rib, edge fillet/chamfer, reference geometry, patterns, material, dimension/feature edits).
    public partial class SolidWorksService
    {

        public ExecutionResponse ExtrudeFeature(ToolRequest request) => Run(request, true, p =>
        {
            double? depth = PNumN(p, "depth");

            string featureType = (PStr(p, "feature_type", "boss")).ToLowerInvariant();
            if (featureType != "boss" && featureType != "cut" && featureType != "revolve" && featureType != "revolve_cut" && featureType != "sweep")
                throw new ToolFail("BAD_PARAMETER",
                    $"feature_type '{featureType}' is not supported. Supported: boss, cut, revolve, revolve_cut, sweep (a loft is the loft tool).");

            // depth (> 0) is required ONLY for a BLIND boss/cut. A through-all (through=true) ignores
            // depth, and revolve/sweep don't use it — so don't demand it there.
            bool throughAll = PBool(p, "through", false);
            // up_to_face_index >= 0 ⇒ up-to-surface end condition: the extrude/cut terminates ON that
            // face (index from analyze_model(faces), same enumeration create_sketch(on_face) walks).
            // Depth is ignored, like through-all.
            int upToFaceIndex = PInt(p, "up_to_face_index", -1);
            bool upToSurface = upToFaceIndex >= 0;
            // mid_plane=true ⇒ swEndCondMidPlane: the feature extrudes SYMMETRICALLY about the
            // sketch plane; depth = the TOTAL width (SolidWorks midplane semantics).
            bool midPlane = PBool(p, "mid_plane", false);
            if (upToSurface && featureType != "boss" && featureType != "cut")
                throw new ToolFail("BAD_PARAMETER", "up_to_face_index is only valid for feature_type 'boss' or 'cut'.");
            if (midPlane && featureType != "boss" && featureType != "cut")
                throw new ToolFail("BAD_PARAMETER", "mid_plane is only valid for feature_type 'boss' or 'cut'.");
            if (midPlane && (throughAll || upToSurface))
                throw new ToolFail("BAD_PARAMETER", "mid_plane conflicts with through/up_to_face_index — pick ONE end condition.");
            if ((featureType == "boss" || featureType == "cut") && !throughAll && !upToSurface && (depth == null || depth.Value <= 0))
                throw new ToolFail(depth == null ? "MISSING_PARAMETER" : "BAD_PARAMETER", "depth (> 0) is required for a blind or mid-plane boss/cut. Pass through=true for a through-all instead.");

            var modelDoc = ActiveDocOrFail();
            var before = BuildBodySummary(modelDoc);
            double volBefore = SolidsVolume(modelDoc);

            // Direction + end-condition controls (boss/cut). reverse flips the feature direction
            // (needed e.g. for a cut sketched on a part FACE, where "into the body" is the opposite
            // side from a cut sketched on a boundary plane). through = through-all (depth ignored).
            // direction = [x,y,z] in model space decides it instead: the sketch's normal is read and reverse set to
            // point the feature that way (the same reverse flag gave opposite sides on two offset-plane sketches).
            bool reverse = PBool(p, "reverse", false);
            var wantDir = PArr(p, "direction").Select(t => t.ToObject<double>()).ToArray();
            if (wantDir.Length != 0 && wantDir.Length != 3) throw new ToolFail("BAD_PARAMETER", "direction is 3 numbers [x, y, z] (model space).");
            if (wantDir.Length == 3 && (featureType == "boss" || featureType == "cut"))
            {
                var n = SketchNormal(modelDoc) ?? throw new ToolFail("SKETCH_NOT_ACTIVE", "direction needs the profile sketch active (its normal decides the side).");
                double dot = n[0] * wantDir[0] + n[1] * wantDir[1] + n[2] * wantDir[2];
                if (Math.Abs(dot) < 1e-9) throw new ToolFail("BAD_PARAMETER", $"direction is parallel to the sketch plane (its normal is [{string.Join(", ", n.Select(v => Math.Round(v, 3)))}]).");
                reverse = dot < 0;
            }
            int endCond = upToSurface
                ? (int)swEndConditions_e.swEndCondUpToSurface
                : midPlane
                    ? (int)swEndConditions_e.swEndCondMidPlane
                    : throughAll
                        ? (int)swEndConditions_e.swEndCondThroughAll
                        : (int)swEndConditions_e.swEndCondBlind;
            // Through-all ignores depth; a blind boss/cut already validated depth > 0 above.
            // Default a missing depth to 0 so the API call is safe (e.g. a REST through-cut with no depth).
            double depthVal = depth ?? 0.0;

            // Capture the active sketch name before exiting — it is the PROFILE for sweep (and is
            // useful context generally). Re-selected by name after exit (names resolve post-exit).
            string activeProfileSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;

            // Exit sketch mode if a sketch is still active. EXCEPTION: revolve selects its axis
            // line by coordinate, which only works while the sketch is still active (a closed
            // sketch's segments are not coordinate-pickable). Revolve handles its own selection
            // below with the sketch open; FeatureRevolve2 consumes the sketch as the profile.
            bool isRevolve = featureType == "revolve" || featureType == "revolve_cut";   // revolve_cut: idea from Bennyco86
            if (!isRevolve) ExitSketch(modelDoc);

            IFeature feature = null;
            switch (featureType)
            {
                case "revolve":
                case "revolve_cut": feature = ExtrudeRevolve(modelDoc, p, featureType); break;
                case "sweep": feature = ExtrudeSweep(modelDoc, p, activeProfileSketchName); break;
                case "cut": feature = ExtrudeCut(modelDoc, p, activeProfileSketchName, reverse, endCond, depthVal, upToSurface, upToFaceIndex); break;
                case "boss": feature = ExtrudeBoss(modelDoc, p, activeProfileSketchName, reverse, endCond, depthVal, upToSurface, upToFaceIndex); break;
            }

            if (feature == null)
                throw new ToolFail("EXTRUSION_FAILED", $"Feature creation returned null for feature_type '{featureType}'. Check the profile is a closed/valid sketch, the cut direction hits existing material, and (cut) a solid body exists.");
            string warning = CheckCreated(modelDoc, feature, featureType);
            var after = WithWarning(BuildBodySummary(modelDoc), warning) ?? new JObject();
            var grew = GrewToward(before, after);
            if (grew != null && featureType != "cut" && featureType != "revolve_cut") after["grew_toward"] = grew;
            // A cut that takes most of the body (a mid-plane through-cut wrecked a sheet-metal manifold: 8.8 → 1.3 kg,
            // no error) is almost never meant.
            double vb = volBefore * 1e9, va = SolidsVolume(modelDoc) * 1e9;
            if ((featureType == "cut" || featureType == "revolve_cut") && vb > 0 && va < vb * 0.5)
                after["warning"] = $"the cut removed {Math.Round(100 * (1 - va / vb))} % of the body ({Math.Round(vb)} → {Math.Round(va)} mm³) — check its side, mid_plane / through and the profile";
            return new ToolOutput { Features = new List<string> { feature.Name }, Geometry = after };
        });

        // The active sketch's normal in model space (its +Z), or null without an open sketch.
        private double[] SketchNormal(IModelDoc2 d)
        {
            var sk = d.SketchManager.ActiveSketch as ISketch;
            var t = sk?.ModelToSketchTransform as MathTransform;
            if (t == null) return null;
            var mu = _solidWorks.GetMathUtility() as IMathUtility;
            var v = (mu.CreateVector(new double[] { 0, 0, 1 }) as MathVector).MultiplyTransform(t.Inverse() as MathTransform) as MathVector;
            var n = v?.ArrayData as double[];
            if (n == null) return null;
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            return new[] { n[0] / len, n[1] / len, n[2] / len };
        }

        private IFeature ExtrudeRevolve(IModelDoc2 modelDoc, JObject p, string featureType)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            // Requires depth param ignored; needs axis defined by two coordinate points in the sketch
            double ax1 = PNum(p, "axis_x1", 0.0);
            double ay1 = PNum(p, "axis_y1", 0.0);
            double ax2 = PNum(p, "axis_x2", 0.0);
            double ay2 = PNum(p, "axis_y2", 0.001);
            // angle arrives in DEGREES (model-facing convention, like create_pattern circular /
            // sheet_metal); convert to radians for FeatureRevolve2. Default 360 = full revolve.
            double angleDeg = PNum(p, "angle", 360.0);
            double angle = angleDeg * Math.PI / 180.0;
            // Snap anything within ~0.06° of a full turn to an exact 2π so "full revolve" is clean
            // (guards float rounding). Partial revolves (e.g. 180°) are unaffected.
            if (Math.Abs(angle - 2.0 * Math.PI) < 1e-3) angle = 2.0 * Math.PI;

            // The sketch is still active here (the generic exit above skips revolve), so the
            // axis line is coordinate-pickable. Select it at the midpoint of the two endpoints.
            if (modelDoc.SketchManager.ActiveSketch == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "revolve needs the profile+axis sketch active. Call create_sketch and draw the profile + axis line first.");

            modelDoc.ClearSelection2(true);
            // The axis is looked up IN the sketch (sketch coordinates): SelectByID2 picks in MODEL space, so on an
            // offset / turned plane the point missed and every axis_* gave AXIS_NOT_FOUND. Without axis_* the sketch's
            // centerline (construction line) is the axis.
            bool axisGiven = p["axis_x1"] != null || p["axis_y1"] != null || p["axis_x2"] != null || p["axis_y2"] != null;
            var axisSeg = RevolveAxis(modelDoc.SketchManager.ActiveSketch as ISketch, axisGiven, (ax1 + ax2) / 2.0, (ay1 + ay2) / 2.0);
            bool axSel = axisSeg != null;
            var revSelMgr = modelDoc.SelectionManager as ISelectionMgr;
            // a plain line becomes a centerline so the rest of the sketch is the profile (the manual flow)
            if (!axisSeg.ConstructionGeometry) axisSeg.ConstructionGeometry = true;

            // Remember the profile sketch, then exit it (post-exit, re-select it by name — names
            // resolve after exit even though coordinates do not).
            string profileSketchName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;
            ExitSketch(modelDoc);
            // SW 2024: ISketch does not QueryInterface to IFeature (name stayed null, nothing got selected and
            // FeatureRevolve2 returned null) — the sketch just closed is the last feature of the tree.
            if (string.IsNullOrEmpty(profileSketchName))
            {
                var last = modelDoc.FeatureByPositionReverse(0) as IFeature;
                if (last != null && last.GetTypeName2() == "ProfileFeature") profileSketchName = last.Name;
            }
            modelDoc.ClearSelection2(true);
            if (!string.IsNullOrEmpty(profileSketchName))
                SelectSketch(modelDoc, profileSketchName);
            // the axis of revolution goes in with selection Mark 16 (as the macro recorder does) — with only
            // the sketch selected SW 2024 returns null even when the sketch has one centerline (verified live)
            if (axisSeg != null && revSelMgr != null)
            {
                var axSd = revSelMgr.CreateSelectData(); axSd.Mark = 16;
                axisSeg.Select4(true, axSd);
            }

            feature = featureMgr.FeatureRevolve2(
                true, true, false, featureType == "revolve_cut", false, false,
                0, 0, angle, 0, false, false,
                0.0, 0.0, 0, 0.0, 0.0, true, true, true) as IFeature;
            if (feature == null && revSelMgr != null && !string.IsNullOrEmpty(profileSketchName))
            {
                // the axis with Mark 4 (what worked in a revolve macro on an offset-plane sketch)
                modelDoc.ClearSelection2(true);
                SelectSketch(modelDoc, profileSketchName);
                var sd4 = revSelMgr.CreateSelectData(); sd4.Mark = 4;
                axisSeg.Select4(true, sd4);
                feature = featureMgr.FeatureRevolve2(true, true, false, featureType == "revolve_cut", false, false,
                    0, 0, angle, 0, false, false, 0.0, 0.0, 0, 0.0, 0.0, true, true, true) as IFeature;
            }
            ExecLog.Write($"revolve: axisSel={axSel} axisSeg={(axisSeg != null)} sketch='{profileSketchName}' selCount={(modelDoc.SelectionManager as ISelectionMgr)?.GetSelectedObjectCount2(-1)} -> {(feature == null ? "null" : feature.Name)}");
            return feature;
        }

        // The axis of a revolve in the active sketch: the line through the given midpoint (sketch coordinates, m), else
        // the one construction line.
        private static ISketchSegment RevolveAxis(ISketch sk, bool given, double mx, double my)
        {
            if (sk == null) throw new ToolFail("SKETCH_NOT_ACTIVE", "revolve needs the profile+axis sketch active.");
            var lines = SketchSegments(sk).Where(s => s is ISketchLine).ToList();
            if (given)
            {
                ISketchSegment best = null; double bestD = double.MaxValue;
                foreach (var s in lines)
                {
                    var l = (ISketchLine)s;
                    var a = l.GetStartPoint2() as ISketchPoint; var b = l.GetEndPoint2() as ISketchPoint;
                    if (a == null || b == null) continue;
                    double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                    double t = len2 > 0 ? Math.Max(0, Math.Min(1, ((mx - a.X) * dx + (my - a.Y) * dy) / len2)) : 0;
                    double ex = a.X + t * dx - mx, ey = a.Y + t * dy - my, dist = Math.Sqrt(ex * ex + ey * ey);
                    if (dist < bestD) { bestD = dist; best = s; }
                }
                if (best == null || bestD > 0.0005)
                    throw new ToolFail("AXIS_NOT_FOUND", $"No line of the sketch passes the axis midpoint ({Math.Round(mx * MM, 2)}, {Math.Round(my * MM, 2)}) mm in SKETCH coordinates — " +
                        "axis_x1/y1/x2/y2 are the centerline's ends as drawn, or leave them out to use the sketch's centerline.");
                return best;
            }
            // construction lines, without a corner rectangle's diagonals (both ends on corners of solid segments)
            var solidEnds = new List<double[]>();
            foreach (var s in lines.Where(s => !s.ConstructionGeometry).OfType<ISketchLine>())
                foreach (var q in new[] { s.GetStartPoint2() as ISketchPoint, s.GetEndPoint2() as ISketchPoint })
                    if (q != null) solidEnds.Add(new[] { q.X, q.Y });
            bool OnCorner(ISketchPoint q) => q != null && solidEnds.Any(e => Math.Abs(e[0] - q.X) < 1e-9 && Math.Abs(e[1] - q.Y) < 1e-9);
            var center = lines.Where(s => s.ConstructionGeometry).Where(s =>
            {
                var l = (ISketchLine)s;
                return !(OnCorner(l.GetStartPoint2() as ISketchPoint) && OnCorner(l.GetEndPoint2() as ISketchPoint));
            }).ToList();
            if (center.Count == 1) return center[0];
            if (center.Count == 0)
                try { return RevolveAxis(sk, true, 0, 0.0005); }   // the old default: a line along sketch Y through the origin
                catch (ToolFail) { }
            throw new ToolFail("AXIS_NOT_FOUND", center.Count == 0
                ? "The sketch has no centerline — draw one (add_sketch_entity centerline) or give axis_x1/y1/x2/y2."
                : $"The sketch has {center.Count} centerlines — give axis_x1/y1/x2/y2 of the one to revolve about.");
        }

        private IFeature ExtrudeSweep(IModelDoc2 modelDoc, JObject p, string activeProfileSketchName)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            string pathSketch = PStr(p, "path_sketch");
            if (string.IsNullOrEmpty(pathSketch))
                throw new ToolFail("MISSING_PARAMETER", "sweep requires path_sketch (name of the sketch containing the sweep path).");

            // The profile sketch was already exited by the generic exit above. A sweep needs BOTH
            // selected: the profile (selection mark 1) and the path (mark 4). Selecting only the
            // path left InsertProtrusionSwept4 with no profile → null.
            if (string.IsNullOrEmpty(activeProfileSketchName))
                throw new ToolFail("PROFILE_NOT_FOUND", "sweep needs an active profile sketch when called. Create the profile sketch last (it stays active) and pass the path sketch name.");

            modelDoc.ClearSelection2(true);
            bool profSel = SelectSketch(modelDoc, activeProfileSketchName, false, 1);
            if (!profSel)
                throw new ToolFail("PROFILE_NOT_FOUND", $"Profile sketch '{activeProfileSketchName}' not found.");
            bool pathSel = SelectSketch(modelDoc, pathSketch, true, 4);
            if (!pathSel)
                throw new ToolFail("SKETCH_NOT_FOUND", $"Path sketch '{pathSketch}' not found. Ensure the sketch is named exactly as provided.");

            feature = featureMgr.InsertProtrusionSwept4(
                false, false, 0, false, false,
                0, 0,
                false, 0.0, 0.0, 0, 0,
                true, true, true,
                0.0, false, false, 0.0, 0) as IFeature;
            return feature;
        }

        private IFeature ExtrudeCut(IModelDoc2 modelDoc, JObject p, string activeProfileSketchName, bool reverse, int endCond, double depthVal, bool upToSurface, int upToFaceIndex)
        {
            var featureMgr = modelDoc.FeatureManager;
            // A cut on sheet metal is made before its Flat-Pattern (BeforeFlatPattern); the profile sketch already sits
            // before it, so it stays available.
            bool isSheetMetal = FindFlatPattern(modelDoc) != null;
            return BeforeFlatPattern(modelDoc, () =>
            {
                IFeature feature;
                // Explicitly re-select the profile sketch by name. On a PLANE sketch the just-exited
                // sketch stays implicitly selected as the profile, but on a FACE sketch the selected
                // FACE remains the selection after exit, so FeatureCut4 found no profile → null.
                // (Same post-exit re-select trick revolve uses.) Also required after a rollback, which
                // clears the selection.
                modelDoc.ClearSelection2(true);
                if (!string.IsNullOrEmpty(activeProfileSketchName))
                    SelectSketch(modelDoc, activeProfileSketchName);
                if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                    throw new ToolFail("FACE_NOT_FOUND",
                        $"up_to_face_index {upToFaceIndex} could not be selected. Call analyze_model(faces) for valid indices.");

                // Dir (3rd arg) controls cut direction. Default true = INTO the body (opposite the
                // sketch plane's outward normal) — correct for a cut on a boundary plane. For a cut
                // sketched on a part FACE the material is on the other side, so pass reverse=true to
                // flip it (Dir=false). through ⇒ swEndCondThroughAll (depth ignored).
                // SHEET METAL: a plain cut returns null — it needs the "Normal Cut"
                // option (normalCut=true, 18th arg, cut perpendicular to the sheet faces) AND the
                // OPPOSITE default direction (the sketch sits on a sheet FACE, like a cut sketched on a part face),
                // i.e. dir = reverse instead of !reverse. We then add a flipped-direction retry so the
                // through-all works regardless of which face the profile was sketched on.
                bool normalCut = isSheetMetal;
                bool dir = isSheetMetal ? reverse : !reverse;
                feature = FeatureCutOnce(featureMgr, dir, endCond, depthVal, normalCut);
                if (feature == null)
                {
                    // A null cut is most often the WRONG direction: the default Dir sent the cut AWAY
                    // from material so nothing was removed. This happens whenever the sketch sits on a
                    // surface whose outward normal points into free space — an offset reference plane
                    // tangent to the body (e.g. a keyway plane) or a part FACE both behave like
                    // face cut, inverting the boundary-plane default. Retry the flipped direction before
                    // failing, for ANY body type (this generalizes the former sheet-metal-only retry;
                    // for sheet metal the flipped through-all also matters per the sketched face). The
                    // caller's explicit `reverse` is still tried FIRST — we only flip to recover a null,
                    // never to override intent.
                    modelDoc.ClearSelection2(true);
                    if (!string.IsNullOrEmpty(activeProfileSketchName))
                        SelectSketch(modelDoc, activeProfileSketchName);
                    if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                        throw new ToolFail("FACE_NOT_FOUND",
                            $"up_to_face_index {upToFaceIndex} could not be re-selected for the flipped-direction retry.");
                    feature = FeatureCutOnce(featureMgr, !dir, endCond, depthVal, normalCut);
                    ExecLog.Write($"cut: flipped-direction retry (sheetMetal={isSheetMetal}) → feature={(feature != null)}");
                }
                return feature;
            });
        }

        private IFeature ExtrudeBoss(IModelDoc2 modelDoc, JObject p, string activeProfileSketchName, bool reverse, int endCond, double depthVal, bool upToSurface, int upToFaceIndex)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            // Re-select the profile sketch by name (see cut branch — needed for FACE sketches,
            // harmless for plane sketches).
            modelDoc.ClearSelection2(true);
            if (!string.IsNullOrEmpty(activeProfileSketchName))
                SelectSketch(modelDoc, activeProfileSketchName);
            if (upToSurface && !SelectFaceByIndexAppend(modelDoc, upToFaceIndex, 1))
                throw new ToolFail("FACE_NOT_FOUND",
                    $"up_to_face_index {upToFaceIndex} could not be selected. Call analyze_model(faces) for valid indices.");

            feature = featureMgr.FeatureExtrusion3(
                true, false, reverse,
                endCond, 0,
                depthVal, 0,
                false, false, false, false,
                0, 0,
                false, false, false, false,
                true, true, true,
                (int)swStartConditions_e.swStartSketchPlane, 0,
                false) as IFeature;
            return feature;
        }

        public ExecutionResponse CreateRib(ToolRequest request) => Run(request, true, p =>
        {
            double? thickness = PNumN(p, "thickness");
            if (thickness == null || thickness.Value <= 0)
                throw new ToolFail(thickness == null ? "MISSING_PARAMETER" : "BAD_PARAMETER", "thickness (> 0) is required.");
            bool twoSided = PBool(p, "two_sided", true);
            bool reverseThicknessDir = PBool(p, "reverse_thickness_dir", false);
            bool reverseMaterialDir = PBool(p, "reverse_material_dir", false);
            bool isNormToSketch = PBool(p, "is_norm_to_sketch", false);

            var modelDoc = ActiveDocOrFail();

            // The ACTIVE sketch is the rib profile (an OPEN sketch — typically a single line;
            // SolidWorks extends it to the surrounding walls). Same consume-the-active-sketch
            // contract as extrude_feature.
            var activeSketchFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
            if (activeSketchFeat == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE", "No active sketch — the rib profile sketch must be active. Call create_sketch + add_sketch_entity first.");
            string profileName = activeSketchFeat.Name;

            // Snapshot the last feature so the void-returning InsertRib can be verified from
            // the tree afterwards (no return value to check — unlike FeatureExtrusion3).
            string lastBefore = LastFeatureName(modelDoc);

            ExitSketch(modelDoc);
            modelDoc.ClearSelection2(true);
            SelectSketch(modelDoc, profileName);

            // InsertRib(Is2Sided, ReverseThicknessDir, Thickness, ReferenceEdgeIndex,
            //           ReverseMaterialDir, IsDrafted, DraftOutward, DraftAngle,
            //           IsNormToSketch, IsDraftedFromWall) — signature + parameter semantics
            // verified against the LOCAL sldworksapi.chm + interop reflection.
            // Draft deliberately not exposed in v1 (2-1's rib is undrafted; grow on demand).
            modelDoc.FeatureManager.InsertRib(
                twoSided, reverseThicknessDir, thickness.Value, 0,
                reverseMaterialDir, false, false, 0.0,
                isNormToSketch, false);
            modelDoc.EditRebuild3();

            string lastAfter = LastFeatureName(modelDoc);
            if (lastAfter == lastBefore)
            {
                // Most likely the WRONG material direction (nothing to fill on that side) —
                // retry flipped, mirroring the cut path's flipped-direction recovery. The
                // caller's explicit reverse_material_dir is still tried FIRST.
                modelDoc.ClearSelection2(true);
                SelectSketch(modelDoc, profileName);
                modelDoc.FeatureManager.InsertRib(
                    twoSided, reverseThicknessDir, thickness.Value, 0,
                    !reverseMaterialDir, false, false, 0.0,
                    isNormToSketch, false);
                modelDoc.EditRebuild3();
                lastAfter = LastFeatureName(modelDoc);
                ExecLog.Write($"create_rib: flipped-material retry → created={(lastAfter != lastBefore)}");
            }
            if (lastAfter == lastBefore)
                throw new ToolFail("RIB_FAILED",
                    "InsertRib created no feature (tried both material directions). Check the profile sketch is open geometry whose extensions hit existing walls, and thickness is sensible.");

            var rib = LastFeature(modelDoc);
            string warning = rib != null && rib.Name == lastAfter ? CheckCreated(modelDoc, rib, "rib") : null;
            return new ToolOutput { Features = new List<string> { lastAfter }, Geometry = WithWarning(BuildBodySummary(modelDoc), warning) };
        });

        // The edge a point lies on, found by geometry: the part's nearest solid-body edge (IEdge.GetClosestPointOn) within
        // 0.2 mm. SelectByID2 with the point is a pick along the screen ray: it found nothing at the hidden back edges of
        // a box in the iso view (a servo case's four vertical edges — two "not found"). It stays the fallback for a
        // point given near, not on, an edge.
        private static bool SelectEdgeAtPoint(IModelDoc2 d, double x, double y, double z, bool append)
        {
            if (d is IPartDoc part)
            {
                IEdge best = null;
                double bestD = double.MaxValue;
                foreach (var e in FlattenEdges(part))
                {
                    try
                    {
                        if (!(e.GetClosestPointOn(x, y, z) is double[] q) || q.Length < 3) continue;
                        double dd = Math.Sqrt((q[0] - x) * (q[0] - x) + (q[1] - y) * (q[1] - y) + (q[2] - z) * (q[2] - z));
                        if (dd < bestD) { bestD = dd; best = e; }
                    }
                    catch { }
                }
                if (best != null && bestD <= 0.0002 && (best as IEntity)?.Select4(append, null) == true) return true;
            }
            return d.Extension.SelectByID2("", "EDGE", x, y, z, append, 0, null, 0);
        }

        public ExecutionResponse AddEdgeFeature(ToolRequest request) => Run(request, true, p =>
        {
            string featureType = PStr(p, "feature_type")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(featureType) || (featureType != "fillet" && featureType != "chamfer"))
                throw new ToolFail(string.IsNullOrEmpty(featureType) ? "MISSING_PARAMETER" : "BAD_PARAMETER", $"feature_type must be 'fillet' or 'chamfer', got '{featureType}'.");

            double? radiusOrDist = PNumN(p, "radius_or_distance");
            if (radiusOrDist == null)
                throw new ToolFail("MISSING_PARAMETER", "radius_or_distance is required.");

            var modelDoc = ActiveDocOrFail();

            // Ensure we are NOT in sketch mode (edge features work on solid body)
            ExitSketch(modelDoc);

            modelDoc.ClearSelection2(true);

            // Selection priority: edge_indices (robust) → edge_points (coords) → ex/ey/ez (single coord).
            // edge_indices is an array of integers matching the `i` field from analyze_model(edges).
            // It reuses the EXACT same enumeration (GetBodies2(swSolidBody,true) → GetEdges()), so the
            // index is stable and selection bypasses the coordinate pick tolerance entirely — the fix for
            // crowded / concave (inner-corner, small-radius) edges that SelectByID2 can't disambiguate
            var indexArray = PArr(p, "edge_indices");

            // Support single edge via ex/ey/ez or multiple via edge_points [[x,y,z], …]
            var edgeArray = PArr(p, "edge_points");

            if (indexArray != null && indexArray.Count > 0)
            {
                // The i-th IEdge of analyze_model(edges)'s enumeration, selected directly (no coordinate pick).
                for (int i = 0; i < indexArray.Count; i++)
                {
                    int ei = indexArray[i].Value<int>();
                    if (!(EdgeAt(modelDoc, ei) is IEntity edgeEnt) || !edgeEnt.Select4(i > 0, null))
                        throw new ToolFail("EDGE_NOT_FOUND", $"Failed to select edge index {ei}.");
                }
            }
            else if (edgeArray != null && edgeArray.Count > 0)
            {
                for (int i = 0; i < edgeArray.Count; i++)
                {
                    var pt = edgeArray[i] as JArray;
                    if (pt == null || pt.Count != 3) throw new ToolFail("BAD_PARAMETER", $"edge_points[{i}] must be [x, y, z].");
                    double ex = (double)pt[0], ey = (double)pt[1], ez = (double)pt[2];
                    bool sel = SelectEdgeAtPoint(modelDoc, ex, ey, ez, i > 0);
                    if (!sel)
                        throw new ToolFail("EDGE_NOT_FOUND", $"No edge found at or near ({ex}, {ey}, {ez}) (edge index {i}).");
                }
            }
            else
            {
                double? ex = PNumN(p, "ex");
                double? ey = PNumN(p, "ey");
                double? ez = PNumN(p, "ez");
                if (ex == null || ey == null || ez == null)
                    throw new ToolFail("MISSING_PARAMETER", "ex, ey, ez are required (3D coordinates of a point on the target edge), or provide edge_points for multiple edges.");
                bool sel = SelectEdgeAtPoint(modelDoc, ex.Value, ey.Value, ez.Value, false);
                if (!sel)
                    throw new ToolFail("EDGE_NOT_FOUND", $"No edge found at or near ({ex.Value}, {ey.Value}, {ez.Value}).");
            }

            IFeature feature;
            if (featureType == "fillet")
            {
                // Options must include swFeatureFilletUniformRadius (2) for a constant-radius fillet;
                // Propagate (1) alone left R1 unapplied and FeatureFillet3 returned null on SW2026.
                int filletOpts = (int)(swFeatureFilletOptions_e.swFeatureFilletPropagate
                                     | swFeatureFilletOptions_e.swFeatureFilletUniformRadius);
                feature = modelDoc.FeatureManager.FeatureFillet3(
                    filletOpts,
                    radiusOrDist.Value,   // R1
                    0.0,                  // R2
                    0.0,                  // Rho
                    (int)swFeatureFilletType_e.swFeatureFilletType_Simple,
                    0,                    // OverflowType
                    0,                    // ConicRhoType
                    null, null, null,     // Radii, Dist2Arr, RhoArr
                    null, null, null, null // SetBackDistances, PointRadiusArray, PointDist2Array, PointRhoArray
                ) as IFeature;
                ExecLog.Write($"add_edge_feature fillet: opts={filletOpts} r={radiusOrDist.Value} -> {(feature == null ? "NULL" : feature.Name)}");
            }
            else // chamfer
            {
                // Two chamfer modes (grown for level-2-2: Chamfer1 dist-angle @45°, Chamfer2 dist-dist 3×5).
                // InsertFeatureChamfer(Options, ChamferType, Width, Angle, OtherDist, VC1, VC2, VC3):
                //   Width = D1 (first-face setback), Angle = radians (AngleDistance), OtherDist = D2
                //   (second-face setback, DistanceDistance):
                //   swChamferAngleDistance=1, swChamferDistanceDistance=2, swFeatureChamferFlipDirection=1.
                // A DistanceDistance chamfer is directional (which face gets D1 vs D2) — a wrong side is
                // corrected by chamfer_flip (the FlipDirection option), same idea as rib/extrude reverse.
                string chamferType = (PStr(p, "chamfer_type", "distance_angle")).ToLowerInvariant();
                bool chamferFlip = PBool(p, "chamfer_flip", false);
                int chamferOpts = chamferFlip ? (int)swFeatureChamferOption_e.swFeatureChamferFlipDirection : 0;
                if (chamferType == "distance_distance")
                {
                    double? dist2 = PNumN(p, "distance2");
                    if (dist2 == null || dist2.Value <= 0)
                        throw new ToolFail("MISSING_PARAMETER", "chamfer_type 'distance_distance' requires distance2 > 0 (the second-face setback).");
                    feature = modelDoc.FeatureManager.InsertFeatureChamfer(
                        chamferOpts,
                        (int)swChamferType_e.swChamferDistanceDistance,
                        radiusOrDist.Value,                          // D1 (Width — first-face setback)
                        0.0,                                         // Angle unused
                        dist2.Value,                                 // D2 (OtherDist — second-face setback)
                        0.0, 0.0, 0.0) as IFeature;
                    ExecLog.Write($"add_edge_feature chamfer(dist-dist): d1={radiusOrDist.Value} d2={dist2.Value} flip={chamferFlip} -> {(feature == null ? "NULL" : feature.Name)}");
                }
                else // distance_angle (default; angle in DEGREES at the tool boundary, default 45)
                {
                    double angleDeg = PNum(p, "angle", 45.0);
                    double chamferAngle = angleDeg * Math.PI / 180.0;
                    feature = modelDoc.FeatureManager.InsertFeatureChamfer(
                        chamferOpts,
                        (int)swChamferType_e.swChamferAngleDistance,
                        radiusOrDist.Value,                          // Width (setback distance, D1)
                        chamferAngle,                                // angle in radians
                        0.0, 0.0, 0.0, 0.0) as IFeature;
                    ExecLog.Write($"add_edge_feature chamfer(dist-angle): dist={radiusOrDist.Value} angle={angleDeg} flip={chamferFlip} -> {(feature == null ? "NULL" : feature.Name)}");
                }
            }

            if (feature == null)
                throw new ToolFail("FEATURE_FAILED", $"add_edge_feature '{featureType}' returned null. Ensure the selected edge(s) belong to an existing solid body.");
            string warning = CheckCreated(modelDoc, feature, featureType);

            return new ToolOutput { Features = new List<string> { feature.Name }, Geometry = WithWarning(null, warning) };
        });

        // -----------------------------------------------------------------------
        // add_reference_geometry
        // -----------------------------------------------------------------------
        public ExecutionResponse AddReferenceGeometry(ToolRequest request) => Run(request, true, p =>
        {
            string geoType = PStr(p, "type", "").ToLowerInvariant();
            var d = ActiveDocOrFail();
            IFeature feature;
            switch (geoType)
            {
                case "plane": feature = RefPlane(d, p); break;
                case "axis": feature = RefAxis(d, p); break;
                case "point": feature = RefPoint(d, p); break;
                default: throw new ToolFail("BAD_PARAMETER", $"type '{geoType}' is not supported. Supported: plane, axis, point.");
            }
            if (feature == null)
                throw new ToolFail("REFERENCE_GEOMETRY_FAILED",
                    $"Failed to create reference {geoType}. Verify the referenced entities are valid and the document has no active sketch.");

            // A new plane / axis is hidden at once (user rule 2026-07-05): it is scaffolding for later features and
            // must not clutter the view; selection by name still works on hidden reference geometry. Best effort.
            if (geoType != "point")
            {
                try
                {
                    d.ClearSelection2(true);
                    if (feature.Select2(false, 0)) d.BlankRefGeom();
                    d.ClearSelection2(true);
                }
                catch { }
            }
            return new ToolOutput { Features = new List<string> { feature.Name } };
        });

        private IFeature RefPlane(IModelDoc2 d, JObject p)
        {
            string refPlaneName = PStr(p, "ref_plane_name") ?? throw new ToolFail("MISSING_PARAMETER",
                "plane requires ref_plane_name (e.g. 'Front Plane') and offset (distance in meters).");
            double offset = PNum(p, "offset", 0.0);
            d.ClearSelection2(true);
            if (!SelectPlaneFlexible(d, refPlaneName, false, 0))
                throw new ToolFail("PLANE_NOT_FOUND", $"Plane '{refPlaneName}' not found.");
            // InsertRefPlane's distance is UNSIGNED — a negative value is silently not applied (an offset of -0.007
            // landed ON its parent); the minus side is the OptionFlip constraint bit + the absolute distance.
            int constraint = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
            if (offset < 0) constraint |= (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
            return d.FeatureManager.InsertRefPlane(constraint, Math.Abs(offset), 0, 0, 0, 0) as IFeature;
        }

        private IFeature RefAxis(IModelDoc2 d, JObject p)
        {
            string name1 = PStr(p, "entity1_name"), name2 = PStr(p, "entity2_name");
            if (name1 == null || name2 == null)
                throw new ToolFail("MISSING_PARAMETER", "axis requires entity1_name and entity2_name (e.g. 'Top Plane' and 'Right Plane').");
            d.ClearSelection2(true);
            SelectRefEntity(d, name1, PStr(p, "entity1_type", "PLANE").ToUpperInvariant(), false, 1);
            SelectRefEntity(d, name2, PStr(p, "entity2_type", "PLANE").ToUpperInvariant(), true, 2);
            if (!d.InsertAxis2(false))
                throw new ToolFail("REFERENCE_GEOMETRY_FAILED", "InsertAxis2 failed. Verify the two selected planes/edges are valid.");
            return LastFeature(d);   // InsertAxis2 returns bool
        }

        // A plane by the language-independent resolver; any other entity type (EDGE, …) by its exact name.
        private void SelectRefEntity(IModelDoc2 d, string name, string type, bool append, int mark)
        {
            bool ok = type == "PLANE" ? SelectPlaneFlexible(d, name, append, mark)
                                      : d.Extension.SelectByID2(name, type, 0, 0, 0, append, mark, null, 0);
            if (!ok) throw new ToolFail("ENTITY_NOT_FOUND", $"Entity '{name}' (type '{type}') not found.");
        }

        private IFeature RefPoint(IModelDoc2 d, JObject p)
        {
            double px = PNum(p, "px", 0.0), py = PNum(p, "py", 0.0), pz = PNum(p, "pz", 0.0);
            d.ClearSelection2(true);
            if (!d.Extension.SelectByID2("", "VERTEX", px, py, pz, false, 0, null, 0))
                throw new ToolFail("VERTEX_NOT_FOUND", $"No vertex found at ({px}, {py}, {pz}). Coordinates must match an existing vertex exactly.");
            return d.FeatureManager.InsertReferencePoint((int)swRefPointType_e.swRefPointIntersection, 0, 0.0, 1) as IFeature;
        }

        // -----------------------------------------------------------------------
        // create_pattern
        // -----------------------------------------------------------------------
        public ExecutionResponse CreatePattern(ToolRequest request) => Run(request, true, p =>
        {
            string patternType = (PStr(p, "pattern_type", "")).ToLowerInvariant();
            string featureName = PStr(p, "feature_name");

            if (patternType == "")
                throw new ToolFail("MISSING_PARAMETER", "pattern_type is required: 'linear', 'circular', or 'mirror'.");
            // feature_name and / or features: every seed goes into ONE pattern (features used to be read by mirror only —
            // a linear pattern of two features silently repeated the first)
            if (string.IsNullOrEmpty(featureName) && PArr(p, "features").Count == 0)
                throw new ToolFail("MISSING_PARAMETER",
                    "feature_name or features is required (the feature(s) to pattern, e.g. 'Boss-Extrude1').");

            var modelDoc = ActiveDocOrFail();

            ExitSketch(modelDoc);

            var before = BuildBodySummary(modelDoc);
            IFeature feature;
            switch (patternType)
            {
                case "linear": feature = PatternLinear(modelDoc, p, featureName); break;
                case "mirror": feature = PatternMirror(modelDoc, p, featureName); break;
                case "circular": feature = PatternCircular(modelDoc, p, featureName); break;
                default: throw new ToolFail("BAD_PARAMETER", $"pattern_type '{patternType}': use 'linear', 'circular' or 'mirror'.");
            }

            if (feature == null)
                throw new ToolFail("PATTERN_FAILED",
                    $"Pattern feature creation returned null. Verify seed feature and direction/axis are valid.");
            string warning = CheckCreated(modelDoc, feature, patternType + " pattern");

            var after = WithWarning(BuildBodySummary(modelDoc), warning);
            // SolidWorks creates the pattern feature but SKIPS instances that do not touch the body (a boss copied
            // into empty space) — say so instead of a silent success (seen live: mirror / circular of a lone boss).
            if (after != null && before != null && (double?)after["volume"] == (double?)before["volume"] && (int?)after["faces"] == (int?)before["faces"])
                after["warning"] = "the pattern added no geometry: instances that do not touch the body are skipped — pattern a cut/hole, " +
                                   "or a boss that overlaps the body (or use bodies: geometry_pattern / a body pattern in SolidWorks)";
            return new ToolOutput { Features = new List<string> { feature.Name }, Geometry = after };
        });

        // The seed features of a pattern / mirror, selected with `mark`: by tree name as a BODYFEATURE, else through the
        // tree walk (feature types BODYFEATURE does not match).
        private void SelectSeeds(IModelDoc2 d, IEnumerable<string> names, int mark, string forWhat)
        {
            d.ClearSelection2(true);
            foreach (var nm in names)
            {
                bool sel = d.Extension.SelectByID2(nm, "BODYFEATURE", 0, 0, 0, true, mark, null, 0);
                if (!sel)
                {
                    var f = FindFeatureByName(d, nm, null);
                    sel = f != null && f.Select2(true, mark);
                }
                if (!sel)
                    throw new ToolFail("FEATURE_NOT_FOUND", $"Feature '{nm}' not found for the {forWhat} — names must match the feature tree exactly.");
            }
        }

        // Linear, mirror and circular patterns are generic feature calls: on sheet metal they are made before the
        // Flat-Pattern (BeforeFlatPattern), like the mirror always was.
        // feature_name + features, without repeats, in that order
        private static List<string> PatternSeeds(JObject p, string featureName)
        {
            var seeds = new List<string>();
            if (!string.IsNullOrEmpty(featureName)) seeds.Add(featureName);
            foreach (var t in PArr(p, "features")) if (!seeds.Contains(t.ToString())) seeds.Add(t.ToString());
            return seeds;
        }

        private IFeature PatternLinear(IModelDoc2 modelDoc, JObject p, string featureName)
        {
            var featureMgr = modelDoc.FeatureManager;
            string direction = (PStr(p, "direction", "X")).ToUpperInvariant();
            double spacing = PNum(p, "spacing", 0.01);
            int count = PInt(p, "count", 2);
            int count2 = PInt(p, "count2", 1);
            double spacing2 = PNum(p, "spacing2", 0.01);
            bool flip = PBool(p, "flip", false);

            // Direction entity: X→Right Plane (normal=X), Y→Top Plane (normal=Y), Z→Front Plane (normal=Z)
            string dirPlane;
            switch (direction)
            {
                case "Y": dirPlane = "Top Plane";   break;
                case "Z": dirPlane = "Front Plane"; break;
                default:  dirPlane = "Right Plane"; break; // X
            }

            return BeforeFlatPattern(modelDoc, () =>
            {
                SelectSeeds(modelDoc, PatternSeeds(p, featureName), 4, "linear pattern");
                // append=true keeps the seed feature (mark=4) selected; language-independent
                if (!SelectPlaneFlexible(modelDoc, dirPlane, true, 1))
                    throw new ToolFail("DIRECTION_NOT_FOUND",
                        $"Direction plane '{dirPlane}' not found. Ensure default planes exist in the document.");

                // FeatureLinearPattern3(Num1, Spacing1, Num2, Spacing2, FlipDir1, FlipDir2,
                // DName1, DName2, GeometryPattern, VaryInstance) — reflection-verified
                // FlipDir1 exposed as 'flip': pattern toward the NEGATIVE
                // axis. FlipDir2 stays false — the D2 direction entity is not wired (count2
                // is accepted but has no direction-2 selection; see the contract note).
                return featureMgr.FeatureLinearPattern3(
                    count, spacing,
                    count2, spacing2,
                    flip, false,
                    null, null,
                    false, false) as IFeature; // geometryPattern=false (SW default). NOTE: disjoint instances warn & are skipped; geometryPattern=true returns null here, not a safe blanket default.
            });
        }

        private IFeature PatternMirror(IModelDoc2 modelDoc, JObject p, string featureName)
        {
            var featureMgr = modelDoc.FeatureManager;
            // Mirror pattern (grown for 4-1's Mirror1): mirrors FEATURES about a plane.
            // InsertMirrorFeature2 selection marks read from the local CHM:
            // features to mirror = mark 1, mirror plane / planar face = mark 2.
            string planeName = PStr(p, "plane");
            if (string.IsNullOrEmpty(planeName)) planeName = "Right Plane";
            bool geometryPattern = PBool(p, "geometry_pattern", false);

            var mirrorNames = PatternSeeds(p, featureName);
            if (mirrorNames.Count == 0)
                throw new ToolFail("MISSING_PARAMETER",
                    "mirror requires features (feature tree names, e.g. [\"Edge-Flange1\", \"Sketched Bend2\"]) or feature_name.");

            return BeforeFlatPattern(modelDoc, () =>
            {
                SelectSeeds(modelDoc, mirrorNames, 1, "mirror");
                if (!SelectPlaneFlexible(modelDoc, planeName, true, 2))
                    throw new ToolFail("PLANE_NOT_FOUND",
                        $"Mirror plane '{planeName}' not found. Use a default plane name or a created reference plane's name.");

                var feature = featureMgr.InsertMirrorFeature2(false, geometryPattern, false, false, 0) as IFeature;
                ExecLog.Write($"mirror: features=[{string.Join(",", mirrorNames)}] plane={planeName} -> {(feature == null ? "NULL" : feature.Name)}");
                return feature;
            });
        }

        private IFeature PatternCircular(IModelDoc2 modelDoc, JObject p, string featureName)
        {
            var featureMgr = modelDoc.FeatureManager;
            string axisName = PStr(p, "axis_name");
            int count = PInt(p, "count", 4);
            double angleDeg = PNum(p, "angle", 90.0);
            double angleRad = angleDeg * Math.PI / 180.0;
            // equal_spacing=true (default): angle = TOTAL angle spread across all instances (pass
            // 360 for a full ring). equal_spacing=false: angle = the angle BETWEEN adjacent
            // instances (matches a model authored that way; can exceed 360 and wrap/overlap).
            bool equalSpacing = PBool(p, "equal_spacing", true);

            if (string.IsNullOrEmpty(axisName))
                throw new ToolFail("MISSING_PARAMETER",
                    "circular pattern requires axis_name (name of the rotation axis, e.g. 'Axis1'). Create one first with add_reference_geometry.");

            return BeforeFlatPattern(modelDoc, () =>
            {
                SelectSeeds(modelDoc, PatternSeeds(p, featureName), 4, "circular pattern");
                // axis with mark=1 — AXIS type first, then REFERENCECURVES
                bool axisSel = modelDoc.Extension.SelectByID2(axisName, "AXIS", 0, 0, 0, true, 1, null, 0);
                if (!axisSel)
                    axisSel = modelDoc.Extension.SelectByID2(axisName, "REFERENCECURVES", 0, 0, 0, true, 1, null, 0);
                if (!axisSel)
                    throw new ToolFail("AXIS_NOT_FOUND",
                        $"Axis '{axisName}' not found. Create a reference axis with add_reference_geometry first.");

                return featureMgr.FeatureCircularPattern4(
                    count, angleRad,
                    false,
                    null,
                    false, equalSpacing, false) as IFeature; // geometryPattern=false (SW default): disjoint instances are skipped
            });
        }

        // -----------------------------------------------------------------------
        // set_part_material
        // -----------------------------------------------------------------------
        public ExecutionResponse SetPartMaterial(ToolRequest request) => Run(request, true, p =>
        {
            string material = PStr(p, "material_name") ?? throw new ToolFail("MISSING_PARAMETER", "material_name is required (e.g. 'AISI 1020 Steel (SS)', 'Aluminum 1060 Alloy', 'ABS').");
            string library = PStr(p, "library", "SolidWorks Materials");
            var part = ActivePartOrFail(out _);
            part.SetMaterialPropertyName2("", library, material);    // "" = all configurations
            // an unknown name leaves the part's previous material in place — compare, not just "something is set"
            string applied = part.GetMaterialPropertyName2("", out string db);
            if (!string.Equals(applied, material, StringComparison.OrdinalIgnoreCase))
            {
                var near = NearestMaterials(material, 6);
                throw new ToolFail("MATERIAL_NOT_FOUND", $"Material '{material}' in library '{library}' was not found or could not be applied" +
                    (string.IsNullOrEmpty(applied) ? "" : $" — the part keeps '{applied}'") + ". " +
                    (near.Count > 0 ? "Closest names: " + string.Join("; ", near.Select(m => $"'{m.Item2}' ({m.Item1})")) + "." : "list_materials(query) shows the names."));
            }
            return new ToolOutput { Features = new List<string> { $"material={applied}", $"library={db}" } };
        });

        // -----------------------------------------------------------------------
        // modify_dimension — UNIVERSAL parametric value edit (the variants keystone).
        // Sets a named display dimension's value (e.g. "D1@Boss-Extrude1@Part.Part") in SI units
        // (meters for length, radians for angle), rebuilds, then echoes the EFFECTIVE value back in
        // result_geometry for in-band verification. Bumps state_version (geometry
        // changes — unlike analyze). Flow: analyze_model(features) reports every feature's dimension
        // full-names + SI values → pick a name → modify_dimension(name, value).
        // -----------------------------------------------------------------------
        public ExecutionResponse ModifyDimension(ToolRequest request) => Run(request, true, p =>
        {
            string name = PStr(p, "name") ?? throw new ToolFail("MISSING_PARAMETER", "name is required (the dimension full-name, e.g. 'D1@Boss-Extrude1@Part.Part' — get it from analyze_model(features)).");
            if (p["value"] == null || p["value"].Type == JTokenType.Null) throw new ToolFail("MISSING_PARAMETER", "value is required (mm for a length, degrees for an angle).");
            double value = PNum(p, "value", 0);
            // units=mm (the MCP tool): mm for a length, degrees for an angle — converted here, where the dimension's
            // kind is known; without it SI (meters / radians)
            bool user = PStr(p, "units") == "mm";
            var d = ActiveDocOrFail();
            ExitSketch(d);
            // the short name analyze_model gives ('D1@Boss-Extrude1') or the full one ('D1@Boss-Extrude1@Part1.Part')
            var dim = d.Parameter(name) as IDimension
                      ?? (name.Count(c => c == '@') == 1 ? d.Parameter(name + "@" + System.IO.Path.GetFileNameWithoutExtension(d.GetTitle()) +
                          (d.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? ".Assembly" : ".Part")) as IDimension : null)
                      ?? throw new ToolFail("DIMENSION_NOT_FOUND", $"Dimension '{name}' not found. Use the exact full-name reported by analyze_model(features) (e.g. 'D1@Boss-Extrude1@Part.Part').");
            bool angular = IsAngular(dim), count = IsCount(dim);
            double toSi = !user || count ? 1 : angular ? Math.PI / 180 : 1 / MM;
            var boxBefore = ModelBoxMm(d);
            var sketch = SketchOfDimension(d, name);
            var ptsBefore = sketch == null ? null : SketchPoints(sketch).Select(pt => new[] { pt.X, pt.Y }).ToList();
            dim.SystemValue = value * toSi;      // SI setter (meters / radians)
            // ForceRebuild: EditRebuild3 left the geometry as it was until the next save (seen twice on real parts)
            if (!d.ForceRebuild3(false)) d.EditRebuild3();
            // the effective value (a rebuild may clamp it, an equation may drive it) — the reader analyze uses
            double effective = value;
            try { var sv = dim.GetSystemValue3(1, null) as double[]; effective = sv != null && sv.Length > 0 ? sv[0] : dim.SystemValue; }
            catch { try { effective = dim.SystemValue; } catch { } }
            ExecLog.Write($"modify_dimension: '{name}' requested={value} effective={effective} (SI)");
            var g = new JObject { ["dimension"] = name, ["requested"] = value, ["effective"] = R6(effective / toSi) };
            if (user) g["unit"] = count ? "count" : angular ? "deg" : "mm";
            // what the change moved: the sketch points (which end of a driving dimension went) and the model's box
            if (ptsBefore != null)
            {
                var after = SketchPoints(sketch).Select(pt => new[] { pt.X, pt.Y }).ToList();
                var moved = new JArray();
                for (int i = 0; i < Math.Min(ptsBefore.Count, after.Count); i++)
                    if (Math.Abs(after[i][0] - ptsBefore[i][0]) > 1e-7 || Math.Abs(after[i][1] - ptsBefore[i][1]) > 1e-7)
                        moved.Add(new JObject { ["from"] = new JArray(Math.Round(ptsBefore[i][0] * MM, 3), Math.Round(ptsBefore[i][1] * MM, 3)),
                                                ["to"] = new JArray(Math.Round(after[i][0] * MM, 3), Math.Round(after[i][1] * MM, 3)) });
                g["sketch"] = (sketch as IFeature)?.Name;
                g["moved_points"] = new JArray(moved.Take(12));
                if (moved.Count > 12) g["moved_points_count"] = moved.Count;
            }
            var boxAfter = ModelBoxMm(d);
            if (boxBefore != null && boxAfter != null && boxBefore.Zip(boxAfter, (x, y) => Math.Abs(x - y)).Max() > 0.001)
            {
                g["bbox_mm_before"] = new JArray(boxBefore.Select(v => Math.Round(v, 2)));
                g["bbox_mm"] = new JArray(boxAfter.Select(v => Math.Round(v, 2)));
            }
            return new ToolOutput { Dimensions = new List<string> { name }, Geometry = g };
        });

        // Delete ONE feature (its absorbed sketch goes with it). Its dependents stay unless with_children — EditDelete took a
        // following sweep along with an unrelated boss, silently. A feature that is still there afterwards is an error,
        // not "ok" (a sketch absorbed by a feature cannot be deleted alone — EditDelete just did nothing).
        private object DeleteFeature(IModelDoc2 d, IFeature feature, string name, bool withChildren)
        {
            var kids = ((feature.GetChildren() as object[]) ?? new object[0]).OfType<IFeature>().Select(f => f.Name).ToList();
            var owners = ((feature.GetParents() as object[]) ?? new object[0]).OfType<IFeature>().Select(f => f.Name).ToList();
            d.ClearSelection2(true);
            if (!feature.Select2(false, 0)) throw new ToolFail("FEATURE_NOT_FOUND", $"Feature '{name}' could not be selected for deletion.");
            int opts = (int)swDeleteSelectionOptions_e.swDelete_Absorbed | (withChildren ? (int)swDeleteSelectionOptions_e.swDelete_Children : 0);
            d.Extension.DeleteSelection2(opts);
            d.EditRebuild3();
            if (FindFeatureByName(d, name, null) != null)
            {
                string why = feature.GetTypeName2() == "ProfileFeature" && kids.Count > 0
                    ? $" — it is the sketch of {string.Join(", ", kids)}: delete that feature (its sketch goes with it)"
                    : kids.Count > 0 ? $" — {string.Join(", ", kids)} depend on it (with_children=True deletes them too)" : "";
                throw new ToolFail("DELETE_FAILED", $"SolidWorks did not delete '{name}'{why}.");
            }
            var left = kids.Where(k => FindFeatureByName(d, k, null) != null).ToList();
            var g = new JObject { ["deleted"] = name };
            if (left.Count > 0)
            {
                g["dependents_kept"] = new JArray(left);
                var broken = left.Where(k => { var f = FindFeatureByName(d, k, null); try { return f != null && f.GetErrorCode2(out _) != 0; } catch { return false; } }).ToList();
                if (broken.Count > 0) g["warning"] = $"{string.Join(", ", broken)} depended on '{name}' and now have rebuild errors — fix or delete them.";
            }
            ExecLog.Write($"edit_feature: delete '{name}' (children {(withChildren ? "deleted" : "kept")}: {string.Join(", ", kids)})");
            return new ToolOutput { Features = new List<string>(), Geometry = g };
        }

        // The sketch a dimension belongs to ("D1@Эскиз3…" → Эскиз3), or null for a feature's own dimension.
        private ISketch SketchOfDimension(IModelDoc2 d, string name)
        {
            var parts = name.Split('@');
            if (parts.Length < 2) return null;
            var f = FindFeatureByName(d, parts[1], null);
            return f != null && f.GetTypeName2() == "ProfileFeature" ? f.GetSpecificFeature2() as ISketch : null;
        }

        // -----------------------------------------------------------------------
        // edit_feature — discriminated STRUCTURAL edit of a named feature.
        // action: suppress | unsuppress | delete | rename (rename needs new_name). Each rebuilds and
        // bumps state_version. Suppress/delete CHANGE topology, so selectors or
        // coordinates captured earlier may no longer resolve — re-query with analyze_model afterwards.
        // -----------------------------------------------------------------------
        public ExecutionResponse EditFeature(ToolRequest request) => Run(request, true, p =>
        {
            string featureName = PStr(p, "feature_name") ?? throw new ToolFail("MISSING_PARAMETER", "feature_name is required (exact feature-tree name, e.g. 'Boss-Extrude1').");
            string action = (PStr(p, "action") ?? "").ToLowerInvariant();
            if (action != "suppress" && action != "unsuppress" && action != "delete" && action != "rename")
                throw new ToolFail(action == "" ? "MISSING_PARAMETER" : "BAD_PARAMETER", $"action must be 'suppress', 'unsuppress', 'delete' or 'rename', got '{action}'.");
            string newName = PStr(p, "new_name");
            var d = ActiveDocOrFail();
            ExitSketch(d);
            var all = new List<string>();
            var feature = FindFeatureByName(d, featureName, all)
                          ?? throw new ToolFail("FEATURE_NOT_FOUND", $"Feature '{featureName}' not found. Available features: [{string.Join(", ", all)}].");
            string resultName = featureName;
            switch (action)
            {
                case "suppress":
                case "unsuppress":
                    SuppressOrFail(feature, action == "suppress");
                    break;
                case "rename":
                    if (string.IsNullOrEmpty(newName)) throw new ToolFail("MISSING_PARAMETER", "new_name is required for action='rename'.");
                    RenameOrFail(feature, newName);
                    resultName = newName;
                    break;
                case "delete":
                    return DeleteFeature(d, feature, featureName, PBool(p, "with_children", false));
            }
            d.EditRebuild3();   // commit so analyze_model / verify_state reflect the edit
            ExecLog.Write($"edit_feature: {action} '{featureName}'" + (action == "rename" ? $" -> '{newName}'" : ""));
            return new ToolOutput { Features = new List<string> { resultName } };
        });

        private IFeature FeatureCutOnce(IFeatureManager featureMgr, bool dir, int endCond, double depthVal, bool normalCut)
        {
            return featureMgr.FeatureCut4(
                true, false, dir,
                endCond, 0,
                depthVal, 0,
                false, false, false, false,
                0, 0,
                false, false, false, false,
                normalCut, true, true,
                false, false, false,
                (int)swStartConditions_e.swStartSketchPlane, 0,
                false, false) as IFeature;
        }
    }
}
