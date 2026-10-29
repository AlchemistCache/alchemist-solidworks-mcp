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
    // SolidWorksService partial: sheet-metal features (base/edge flange, sketched bend, flat pattern) and their edge selection.
    public partial class SolidWorksService
    {

        // -----------------------------------------------------------------------
        // sheet_metal_feature
        // -----------------------------------------------------------------------
        public ExecutionResponse SheetMetalFeature(ToolRequest request) => Run(request, true, p =>
        {
            string featureType = (PStr(p, "feature_type", "")).ToLowerInvariant();

            const string kinds = "'base_flange', 'edge_flange', 'edge_flange_sketch', 'edge_flange_finish', 'flat_pattern' or 'sketched_bend'";
            if (featureType == "") throw new ToolFail("MISSING_PARAMETER", "feature_type is required: " + kinds + ".");

            var modelDoc = ActiveDocOrFail();
            var before = BuildBodySummary(modelDoc);

            object result = null;
            switch (featureType)
            {
                case "base_flange": result = SheetMetalBaseFlange(modelDoc, p); break;
                case "edge_flange": result = SheetMetalEdgeFlange(modelDoc, p); break;
                case "edge_flange_sketch": result = SheetMetalEdgeFlangeSketch(modelDoc, p); break;
                case "edge_flange_finish": result = SheetMetalEdgeFlangeFinish(modelDoc, p); break;
                case "sketched_bend": result = SheetMetalSketchedBend(modelDoc, p); break;
                case "flat_pattern": result = SheetMetalFlatPattern(modelDoc, p); break;
                default: throw new ToolFail("BAD_PARAMETER", $"feature_type '{featureType}': use " + kinds + ".");
            }
            if (result is ToolOutput early) return early;
            IFeature feature = result as IFeature;

            if (feature == null)
                throw new ToolFail("SHEET_METAL_FAILED",
                    $"Sheet metal feature '{featureType}' creation returned null. Verify the sketch profile (for base_flange) or edge selection (for edge_flange) is correct.");
            // flat_pattern returns the part's EXISTING Flat-Pattern: CheckCreated would delete it on an error
            string warning = featureType == "flat_pattern" ? null : CheckCreated(modelDoc, feature, featureType);

            // the body summary like extrude_feature: volume, bbox_mm and where the part grew (a flange's real side)
            var after = WithWarning(featureType == "flat_pattern" ? null : BuildBodySummary(modelDoc), warning);
            var grew = featureType == "flat_pattern" ? null : GrewToward(before, after);
            if (grew != null) after["grew_toward"] = grew;
            return new ToolOutput { Features = new List<string> { feature.Name }, Geometry = after };
        });

        private object SheetMetalBaseFlange(IModelDoc2 modelDoc, JObject p)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            double thickness = PNum(p, "thickness", 0.001);
            double bendRadius = PNum(p, "bend_radius", thickness);
            double kFactor = PNum(p, "k_factor", 0.5);

            // Base flange needs the profile sketch SELECTED. After exiting a sketch nothing is
            // selected, so InsertSheetMetalBaseFlange2 had no profile and returned null. Capture the
            // active sketch name before exiting, then re-select it by name (as revolve/sweep do).
            // A closed-profile base flange (flat tab) needs the WHOLE closed sketch contour selected,
            // not a single segment (selecting one segment makes SW treat it as an open profile, which
            // needs ExtrudeDist1 > 0 and otherwise returns null). Capture the active sketch name, exit
            // the sketch, then select the sketch FEATURE by name (the whole contour). Runs on the STA thread.
            string baseProfileName = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name;
            ExitSketch(modelDoc);
            // read before the profile is selected (reading the sketch's contours lost the selection: CreateFeature null)
            bool open = !string.IsNullOrEmpty(baseProfileName) && OpenProfile(modelDoc, baseProfileName);
            modelDoc.ClearSelection2(true);
            if (!string.IsNullOrEmpty(baseProfileName))
                SelectSketch(modelDoc, baseProfileName);

            // InsertSheetMetalBaseFlange2 signature:
            // (Thickness, ThickenDir, Radius, ExtrudeDist1, ExtrudeDist2, FlipExtruDir,
            //  EndCondition1, EndCondition2, DirToUse, CustomBendAllowance PCBA,
            //  UseDefaultRelief, ReliefType, ReliefWidth, ReliefDepth, ReliefRatio,
            //  UseReliefRatio, Merge, UseFeatScope, UseAutoSelect)
            // The legacy InsertSheetMetalBaseFlange2 returned null on SW2026 even with a correctly
            // selected closed contour on the STA thread (verified live). The modern, reliable pattern
            // is CreateDefinition (gives a pre-populated IBaseFlangeFeatureData) → set params →
            // CreateFeature, with the closed sketch contour selected.
            var bfData = featureMgr.CreateDefinition((int)swFeatureNameID_e.swFmBaseFlange) as IBaseFlangeFeatureData;
            if (bfData == null)
                throw new ToolFail("SHEET_METAL_FAILED", "CreateDefinition(swFmBaseFlange) returned null.");

            bfData.OverrideThickness = true;
            bfData.Thickness = thickness;
            // Thickness DIRECTION matters for reproduction: a wrong side puts every downstream
            // bend-sketch plane off the sheet — and the flags also define the intrinsic sheet
            // orientation bend directions fold against, so REPRODUCE THE ORIGINAL'S OWN FLAGS
            // (the base_flange reader reports them) rather than deriving from face positions
            // alone (4-1's symmetric blank was B-rep-identical to a reversed t/2 one, but bends
            // folded mirrored). Empirically (probe 2026-07-07): ReverseThickness alone had NO
            // effect on a CLOSED profile — the blank is an extrusion there and ReverseDirection
            // flips its side; set both so open profiles follow too.
            bool revThick = PBool(p, "reverse_thickness", false);
            bfData.ReverseThickness = revThick;
            bfData.ReverseDirection = revThick;
            // Symmetric = material grows BOTH ways off the sketch plane (±t/2), like a mid-plane
            // extrude. 4-1's original blank is symmetric (sheet z ∈ [−t/2, +t/2]) with NO reverse
            // flags — and the intrinsic sheet orientation those flags define is what downstream
            // bend directions are measured against, so reproduce the ORIGINAL's exact config.
            if (PBoolN(p, "symmetric_thickness") == true)
                bfData.SymmetricThickness = true;
            // An OPEN profile (an L / U section) is extruded along the sketch normal: depth = that length, mid_plane
            // centres it. Without depth SolidWorks used 1 mm; set on the created feature (setting D1EndCondition* before
            // CreateFeature made it return null).
            double? depth = PNumN(p, "depth");
            if (open && (depth == null || depth.Value <= 0))
                throw new ToolFail("MISSING_PARAMETER", "The profile is OPEN (an L / U section): give depth — its length along the sketch normal (mm; mid_plane=True centres it).");
            bfData.OverrideRadius = true;
            bfData.BendRadius = bendRadius;
            bfData.OverrideKFactor = true;
            bfData.KFactor = kFactor;

            feature = featureMgr.CreateFeature(bfData) as IFeature;
            if (feature != null && open)
            {
                var def = feature.GetDefinition() as IBaseFlangeFeatureData;
                bool done = false;
                if (def != null && def.AccessSelections(modelDoc, null))
                {
                    def.D1EndConditionType = PBool(p, "mid_plane", false) ? (int)swEndConditions_e.swEndCondMidPlane : (int)swEndConditions_e.swEndCondBlind;
                    def.D1EndConditionDistance = depth.Value;
                    done = feature.ModifyDefinition(def, modelDoc, null);
                    if (!done) def.ReleaseSelectionAccess();
                }
                ExecLog.Write($"base_flange: open profile depth {depth.Value} m -> {(done ? "set" : "NOT set")}");
                if (!done) throw new ToolFail("SHEET_METAL_FAILED", $"The base flange was made but its depth stayed at SolidWorks' default — set it with modify_dimension on '{feature.Name}'.");
            }
            return feature;
        }

        // True when no contour of the sketch is closed (an open base-flange profile).
        private bool OpenProfile(IModelDoc2 d, string sketchName)
        {
            try
            {
                var sk = FindFeatureByName(d, sketchName, null)?.GetSpecificFeature2() as ISketch;
                var contours = (sk?.GetSketchContours() as object[] ?? new object[0]).OfType<ISketchContour>().ToList();
                return contours.Count > 0 && contours.All(c => !c.IsClosed());
            }
            catch { return false; }
        }

        private object SheetMetalEdgeFlange(IModelDoc2 modelDoc, JObject p)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            // Legacy InsertSheetMetalEdgeFlange2 returns null silently on SW2026 (same fragility as
            // base flange — separate from the MTA/STA issue). Implemented via the modern pattern:
            // CreateDefinition(swFmEdgeFlange) -> IEdgeFlangeFeatureData -> AddEdges -> set params ->
            // CreateFeature. Param values reverse-engineered from a manual edge flange:
            // OffsetType/OffsetDistance is the Flange LENGTH group (OffsetDistance = flange_length);
            // PositionType=1 = swFlangePositionTypeMaterialInside.
            double flangeLength = PNum(p, "flange_length", 0.02);
            double angleDeg = PNum(p, "angle", 90.0);
            double angleRad = angleDeg * Math.PI / 180.0;

            // Edge selection shares SelectFlangeEdge with the sketch/finish phases
            // (v0.7.0): edge_index (from analyze_model(edges)) PREFERRED — the raw
            // coordinate pick can miss a real edge; ex/ey/ez
            // stay the fallback.
            var flangeEdge = SelectFlangeEdge(modelDoc, p,
                PNumN(p, "ex"), PNumN(p, "ey"), PNumN(p, "ez"));

            // Edge flange is a TWO-step API: (1) IModelDoc2.InsertSketchForEdgeFlange generates the
            // default flange profile sketch on the selected edge, then (2) InsertSheetMetalEdgeFlange2
            // consumes the edge + that sketch. The legacy/CreateDefinition one-shots all returned null
            // or 'SketchNotSpecified' precisely because no profile sketch existed (the API does NOT
            // auto-generate it). BooleanOptions requests default radius + relief ratio + default relief.
            int boolOpts = (int)(swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius
                               | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseReliefRatio
                               | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief);

            var efSketch = modelDoc.InsertSketchForEdgeFlange(flangeEdge, angleRad, false) as IFeature;
            ExecLog.Write($"edge_flange: InsertSketchForEdgeFlange -> {(efSketch == null ? "NULL" : efSketch.Name)}");
            // The profile sketch is left open/active — exit it before creating the flange feature.
            ExitSketch(modelDoc);

            if (efSketch != null)
            {
                // Re-select the edge (sketch creation/exit can clear the selection list) —
                // via the entity OBJECT so it works for edge_index and coordinate callers alike.
                modelDoc.ClearSelection2(true);
                (flangeEdge as IEntity)?.Select4(false, null);

                feature = featureMgr.InsertSheetMetalEdgeFlange2(
                    new Edge[] { flangeEdge }, new Feature[] { (Feature)efSketch },
                    boolOpts, angleRad, 0.0,
                    (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside,
                    flangeLength,
                    (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
                    0.5, 0.001, 0.001, 0, null) as IFeature;
                ExecLog.Write($"edge_flange: InsertSheetMetalEdgeFlange2 -> {(feature == null ? "NULL" : feature.Name)}");

                // The flange length comes from the auto-generated profile sketch (a small default),
                // NOT from FlangeOffsetDist when a sketch is supplied. Force the requested length by
                // editing the created feature's definition: AccessSelections (valid for an existing
                // feature, unlike a fresh CreateDefinition) -> set OffsetDistance -> ModifyDefinition.
                if (feature != null)
                {
                    var efDef = feature.GetDefinition() as IEdgeFlangeFeatureData;
                    if (efDef != null)
                    {
                        bool acc = efDef.AccessSelections(modelDoc, null);
                        ExecLog.Write($"edge_flange: pre-modify OffsetType={efDef.OffsetType} OffsetDistance={efDef.OffsetDistance} acc={acc}");
                        efDef.OffsetDistance = flangeLength;
                        bool mod = acc && feature.ModifyDefinition(efDef, modelDoc, null);
                        ExecLog.Write($"edge_flange: ModifyDefinition len={flangeLength} -> mod={mod}");
                        if (!mod)
                        {
                            // the flange stands at the auto sketch's small default length, not the one asked for: it goes
                            if (acc) efDef.ReleaseSelectionAccess();
                            string fname = feature.Name;
                            modelDoc.ClearSelection2(true);
                            bool removed = feature.Select2(false, 0) && modelDoc.Extension.DeleteSelection2(0);
                            modelDoc.ClearSelection2(true);
                            modelDoc.EditRebuild3();
                            throw new ToolFail("SHEET_METAL_FAILED", $"Edge flange '{fname}' could not be given its length of " +
                                $"{flangeLength * 1000:0.###} mm (AccessSelections={acc})" + (removed ? " and was removed." : " — delete it by hand."));
                        }
                    }
                }

                // Commit the geometry so analyze_model/verify_state read the rebuilt body.
                modelDoc.EditRebuild3();
            }
            return feature;
        }

        private object SheetMetalEdgeFlangeSketch(IModelDoc2 modelDoc, JObject p)
        {
            // STEP 1 of the CUSTOM-PROFILE edge flange (SW's documented flow: generate the
            // edge-linked profile sketch, EDIT it, create the flange from it — an arbitrary
            // user sketch is NOT accepted by the flange API). Selects the attach edge, calls
            // InsertSketchForEdgeFlange, optionally CLEARS the generated default profile, and
            // leaves the sketch ACTIVE, echoing its MEASURED frame exactly like create_sketch:
            // the generated sketch's frame is unpredictable, so the caller
            // MUST transform the original profile's 2D coordinates into it.
            double? esx = PNumN(p, "ex");
            double? esy = PNumN(p, "ey");
            double? esz = PNumN(p, "ez");
            double esAngleDeg = PNum(p, "angle", 90.0);
            bool esFlip = PBool(p, "flip", false);
            bool esClear = PBool(p, "clear_profile", true);
            var esEdge = SelectFlangeEdge(modelDoc, p, esx, esy, esz);

            var genSketch = modelDoc.InsertSketchForEdgeFlange(esEdge, esAngleDeg * Math.PI / 180.0, esFlip) as IFeature;
            var esActive = modelDoc.SketchManager.ActiveSketch;
            ExecLog.Write($"edge_flange_sketch: InsertSketchForEdgeFlange -> {(genSketch == null ? "NULL" : genSketch.Name)} flip={esFlip} active={(esActive != null)}");
            if (genSketch == null)
                throw new ToolFail("SHEET_METAL_FAILED",
                    "InsertSketchForEdgeFlange returned null. Verify the edge is a straight sheet-metal edge (a LONG boundary edge of a sheet face, not a thickness edge).");
            if (esActive == null)
            {
                // InsertSketchForEdgeFlange creates the sketch but (live-proven 2026-07-07)
                // does NOT leave it in edit mode — open it explicitly so the caller can draw.
                modelDoc.ClearSelection2(true);
                SelectSketch(modelDoc, genSketch.Name);
                modelDoc.EditSketch();
                esActive = modelDoc.SketchManager.ActiveSketch;
                ExecLog.Write($"edge_flange_sketch: EditSketch({genSketch.Name}) -> active={(esActive != null)}");
            }
            if (esActive == null)
                throw new ToolFail("SHEET_METAL_FAILED",
                    $"Generated profile sketch '{genSketch.Name}' could not be opened for edit.");

            if (esClear)
            {
                // Wipe the generated default profile (a rectangle spanning the edge) so the
                // caller can draw the ORIGINAL's custom profile via add_sketch_entity.
                var esSegs = esActive.GetSketchSegments() as object[];
                modelDoc.ClearSelection2(true);
                int marked = 0;
                if (esSegs != null)
                    foreach (var o in esSegs)
                    {
                        var ss = o as ISketchSegment;
                        if (ss != null && ss.Select4(true, null)) marked++;
                    }
                if (marked > 0) modelDoc.EditDelete();
                ExecLog.Write($"edge_flange_sketch: cleared {marked} generated segments");
            }

            object esFrameEcho = null;
            try { esFrameEcho = ReadSketchPlane(esActive); } catch { }
            return new ToolOutput { ActiveSketch = genSketch.Name, Geometry = esFrameEcho };
        }

        private object SheetMetalEdgeFlangeFinish(IModelDoc2 modelDoc, JObject p)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            // STEP 2: the ACTIVE (edited) profile sketch + the SAME edge -> InsertSheetMetalEdgeFlange2.
            // The custom profile itself defines the flange outline and length, so unlike the
            // default-profile path there is NO OffsetDistance override afterwards. Radius and
            // position replay the ORIGINAL's values (the analyze_model(features) edge_flange block).
            double? efx = PNumN(p, "ex");
            double? efy = PNumN(p, "ey");
            double? efz = PNumN(p, "ez");
            double efAngleDeg = PNum(p, "angle", 90.0);
            bool efUseDefaultRadius = PBool(p, "use_default_radius", false);
            double efRadius = PNum(p, "bend_radius", 0.001);
            int efPos = FlangePositionCode(PStr(p, "bend_position", "material_inside"), "bend_position");
            var profFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
            if (profFeat == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE",
                    "edge_flange_finish requires the generated profile sketch to be ACTIVE (run edge_flange_sketch, then draw the profile).");
            ExitSketch(modelDoc);

            var efEdge2 = SelectFlangeEdge(modelDoc, p, efx, efy, efz);

            int efOpts = (int)(swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseReliefRatio
                             | swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief);
            if (efUseDefaultRadius)
                efOpts |= (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius;

            feature = featureMgr.InsertSheetMetalEdgeFlange2(
                new Edge[] { efEdge2 }, new Feature[] { (Feature)profFeat },
                efOpts, efAngleDeg * Math.PI / 180.0, efRadius, efPos, 0.0,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular,
                0.5, 0.001, 0.001, 0, null) as IFeature;
            ExecLog.Write($"edge_flange_finish: InsertSheetMetalEdgeFlange2 sketch={profFeat.Name} pos={efPos} defRad={efUseDefaultRadius} -> {(feature == null ? "NULL" : feature.Name)}");
            if (feature != null)
                modelDoc.EditRebuild3();
            return feature;
        }

        private object SheetMetalSketchedBend(IModelDoc2 modelDoc, JObject p)
        {
            var featureMgr = modelDoc.FeatureManager;
            IFeature feature = null;
            // Sketched bend (SM3dBend): bends the sheet about the bend LINE(S) in the ACTIVE
            // sketch; the FIXED side is the pre-selected face (grown for 4-1). Signature +
            // BendPos encoding (0=centerline 1=material-inside 2=material-outside 3=bend-outside)
            // read from the local sldworksapi.chm; NOTE this encoding
            // DIFFERS from ISketchedBendFeatureData.PositionType (swFlangePositionTypes_e,
            // where 4=centerline) — the reader maps both to the same canonical string.
            double sbAngleDeg = PNum(p, "angle", 90.0);
            double sbAngleRad = sbAngleDeg * Math.PI / 180.0;
            bool useDefaultRadius = PBool(p, "use_default_radius", false);
            double sbRadius = PNum(p, "bend_radius", 0.001);
            bool sbFlip = PBool(p, "flip", false);
            // InsertSheetMetal3dBend's own order: 0 centerline, 1..3 as swFlangePositionTypes_e; bend_sharp is not offered
            int code = FlangePositionCode(PStr(p, "bend_position", "centerline"), "bend_position",
                                          "centerline", "material_inside", "material_outside", "bend_outside");
            short bendPos = (short)(code == 4 ? 0 : code);

            var bendSketchFeat = modelDoc.SketchManager.ActiveSketch as IFeature;
            if (bendSketchFeat == null)
                throw new ToolFail("SKETCH_NOT_ACTIVE",
                    "sketched_bend requires the bend-line sketch to be ACTIVE (create_sketch on the sheet face + add_sketch_entity line first).");

            int? fixedFaceIndex = PIntN(p, "fixed_face_index");
            double? fx = PNumN(p, "fixed_x");
            double? fy = PNumN(p, "fixed_y");
            double? fz = PNumN(p, "fixed_z");

            // Attempt 0: fixed face selected while the bend sketch is still ACTIVE (the UI flow —
            // the command is invoked from inside the sketch). Attempt 1: exit the sketch,
            // re-select the bend sketch by name + the face (append) — the recorded-macro flow.
            for (int attempt = 0; attempt < 2 && feature == null; attempt++)
            {
                bool append = attempt == 1;
                if (attempt == 1)
                {
                    ExitSketch(modelDoc);
                    modelDoc.ClearSelection2(true);
                    SelectSketch(modelDoc, bendSketchFeat.Name);
                }
                bool faceSel = false;
                if (fixedFaceIndex != null && fixedFaceIndex.Value >= 0)
                {
                    var allFaces = FlattenFaces(modelDoc as IPartDoc);
                    if (fixedFaceIndex.Value < allFaces.Count)
                        faceSel = (allFaces[fixedFaceIndex.Value] as IEntity).Select4(append, null);
                }
                else
                {
                    // by geometry: a screen-ray pick answers with whatever face is in front of the point (a flange)
                    var face = FaceAtPoint(modelDoc, fx ?? 0.0, fy ?? 0.0, fz ?? 0.0, PointOnFaceTol, out double nearest) as IEntity;
                    faceSel = face != null && face.Select4(append, null);
                    if (face == null && !double.IsInfinity(nearest))
                        throw new ToolFail("FACE_NOT_FOUND", $"No face at the fixed point — the nearest is {nearest * 1000:0.###} mm away; " +
                            "pass fixed_face_index (from analyze_model(faces)) or a point ON the face that must stay put.");
                }
                if (!faceSel)
                    throw new ToolFail("FACE_NOT_FOUND",
                        "sketched_bend could not select the FIXED face — pass fixed_face_index (from analyze_model(faces)) or fixed_x/y/z, a point ON the face that must stay put.");
                feature = featureMgr.InsertSheetMetal3dBend(sbAngleRad, useDefaultRadius, sbRadius, sbFlip, bendPos, null) as IFeature;
                ExecLog.Write($"sketched_bend: attempt={attempt} fixedFace={(fixedFaceIndex != null ? fixedFaceIndex.ToString() : "coord")} pos={bendPos} -> {(feature == null ? "NULL" : feature.Name)}");
            }
            if (feature != null)
            {
                // InsertSheetMetal3dBend IGNORES its Flip argument (proven live on 4-1 Bend1:
                // flip true/false produced IDENTICAL folds, z-mirrored from the original, and
                // the created feature read back ReverseDirection=false either way). Enforce the
                // requested direction post-insert through the feature data + ModifyDefinition.
                var bdef = feature.GetDefinition() as ISketchedBendFeatureData;
                if (bdef != null && bdef.ReverseDirection != sbFlip)
                {
                    bool bacc = false;
                    try { bacc = bdef.AccessSelections(modelDoc, null); } catch { }
                    bool modified = false;
                    try
                    {
                        bdef.ReverseDirection = sbFlip;
                        modified = feature.ModifyDefinition(bdef, modelDoc, null);
                        ExecLog.Write($"sketched_bend: post-insert flip enforce -> {sbFlip} (modify={modified})");
                    }
                    finally
                    {
                        // ModifyDefinition consumes the selection access; release only if it didn't run.
                        if (bacc && !modified) { try { bdef.ReleaseSelectionAccess(); } catch { } }
                    }
                    if (!modified)
                        throw new ToolFail("BEND_FLIP_FAILED",
                            "sketched_bend created the bend but could not enforce the requested flip (ModifyDefinition failed).");
                }
                modelDoc.EditRebuild3();
            }
            return feature;
        }

        private object SheetMetalFlatPattern(IModelDoc2 modelDoc, JObject p)
        {
            IFeature feature = null;
            // Flatten = UNSUPPRESS the Flat-Pattern feature (exactly what the UI "Flatten" button
            // does). `SetBendState(Flattened)` only flips a state flag — the displayed/B-rep body
            // re-folds on rebuild (verified live: after SetBendState+rebuild the CG was unchanged
            // from the bent state, i.e. still folded). Locate Flat-Pattern1 by type, unsuppress it,
            // then EditRebuild3 to commit the unfolded body so it shows and analyze_model reads flat.
            IFeature flatFeat = FindFlatPattern(modelDoc)
                ?? throw new ToolFail("FLAT_PATTERN_FAILED", "No Flat-Pattern feature found. Ensure the part has a sheet metal base flange.");

            bool unsup = flatFeat.SetSuppression2(
                (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                (int)swInConfigurationOpts_e.swThisConfiguration, null);
            modelDoc.EditRebuild3();
            int bendStateAfter = modelDoc.GetBendState();
            string flag = FeatureFlag(flatFeat, out bool onlyWarning);
            ExecLog.Write($"flat_pattern: unsuppress={unsup} bendState={bendStateAfter} flag={flag ?? "-"}");
            if (!unsup || (flag != null && !onlyWarning))
            {
                // the part does not flatten: back to the folded state, the Flat-Pattern kept
                flatFeat.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                    (int)swInConfigurationOpts_e.swThisConfiguration, null);
                modelDoc.EditRebuild3();
                throw new ToolFail("FLAT_PATTERN_FAILED", !unsup ? $"'{flatFeat.Name}' could not be unsuppressed."
                    : $"'{flatFeat.Name}' rebuilds with a SolidWorks {flag} — the part does not flatten; it is left folded.");
            }
            feature = flatFeat;
            return feature;
        }

        // Attach-edge selection for the edge-flange ops. Priority: edge_index (robust — matches
        // analyze_model(edges)'s stable `i`, the same FlattenEdges enumeration; bypasses the
        // coordinate-pick fragility — live on 4-1: EF2's slanted attach edge
        // existed EXACTLY at the pick point yet SelectByID2 missed it) → ex/ey/ez coordinate pick.
        private Edge SelectFlangeEdge(IModelDoc2 modelDoc, JObject p, double? ex, double? ey, double? ez)
        {
            int edgeIndex = PInt(p, "edge_index", -1);
            modelDoc.ClearSelection2(true);
            if (edgeIndex >= 0)
            {
                var edge = EdgeAt(modelDoc, edgeIndex);
                if (!(edge is IEntity ent) || !ent.Select4(false, null))
                    throw new ToolFail("EDGE_NOT_FOUND", $"edge_index {edgeIndex}: selection failed.");
                return edge as Edge;
            }
            if (ex == null || ey == null || ez == null)
                throw new ToolFail("MISSING_PARAMETER", "provide edge_index (from analyze_model(edges), PREFERRED) or ex/ey/ez (a point on the attach edge).");
            // by geometry (the nearest edge within 0.2 mm): a screen-ray pick on 1 mm sheet hit the edge next to it
            if (!SelectEdgeAtPoint(modelDoc, ex.Value, ey.Value, ez.Value, false))
                throw new ToolFail("EDGE_NOT_FOUND", $"No edge found at ({ex.Value}, {ey.Value}, {ez.Value}). Prefer edge_index from analyze_model(edges).");
            return (modelDoc.SelectionManager as ISelectionMgr)?.GetSelectedObject6(1, -1) as Edge
                   ?? throw new ToolFail("EDGE_NOT_FOUND", $"Entity at ({ex.Value}, {ey.Value}, {ez.Value}) is not an edge.");
        }

        // Locate the sheet-metal Flat-Pattern feature (always the last top-level feature in a sheet-metal
        // part, usually suppressed), or null for a non-sheet-metal part. Used to roll the bar before it so
        // new features insert ahead of it.
        private static IFeature FindFlatPattern(IModelDoc2 modelDoc)
        {
            try { return (modelDoc.FeatureManager.GetFeatures(true) as object[] ?? new object[0]).OfType<IFeature>().FirstOrDefault(f => f.GetTypeName2() == "FlatPattern"); }
            catch { return null; }
        }
    }
}
