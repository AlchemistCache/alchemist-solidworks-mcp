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
    // SolidWorksService partial: technical drawings — create a drawing, model / flat-pattern / section views, dimensions,
    // auto-dimension, center marks, hole callouts. Reading a drawing: Drawing.Read.cs + Drawing.Geometry.cs.
    public partial class SolidWorksService
    {

        public ExecutionResponse CreateDrawing(ToolRequest request) => Run(request, true, p =>
        {
            // A3 paper (297x420mm), scale 1:1
            NewDoc(swDocumentTypes_e.swDocDRAWING, null, (int)swDwgPaperSizes_e.swDwgPapersUserDefined, 0.420, 0.297);
            return new ToolOutput();
        });

        public ExecutionResponse AddDrawingView(ToolRequest request) => Run(request, true, p =>
        {
            string viewType = (PStr(p, "view_type", "front")).ToLowerInvariant();
            double posX = PNum(p, "pos_x", 0.1);
            double posY = PNum(p, "pos_y", 0.1);
            double scale = PNum(p, "scale", 1.0);
            string modelPath = PStr(p, "model_path", "");
            string displayMode = (PStr(p, "display_mode", "")).Trim().ToLowerInvariant();

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            // Map view_type to SolidWorks named view string
            string swViewName;
            switch (viewType)
            {
                case "front":      swViewName = "*Front";     break;
                case "top":        swViewName = "*Top";       break;
                case "right":      swViewName = "*Right";     break;
                case "isometric":  swViewName = "*Isometric"; break;
                case "back":       swViewName = "*Back";      break;
                case "bottom":     swViewName = "*Bottom";    break;
                case "left":       swViewName = "*Left";      break;
                default:
                    throw new ToolFail("BAD_PARAMETER",
                        $"view_type '{viewType}' is not supported. Supported: front, top, right, isometric, back, bottom, left.");
            }

            // model_path not given: the first open (saved) part
            if (string.IsNullOrEmpty(modelPath)) modelPath = FirstOpenPartPath();

            // SolidWorks wants a Windows path: "D:/x/y.SLDASM" makes CreateDrawViewFromModelView3 return null
            try { modelPath = System.IO.Path.GetFullPath(modelPath); } catch { }
            var view = drawingDoc.CreateDrawViewFromModelView3(modelPath, swViewName, posX, posY, 0.0) as IView;
            if (view == null)
            {
                // named views are LOCALIZED: Russian SolidWorks knows "*Спереди", not "*Front" (verified live, SW 2024 RU)
                var ru = new Dictionary<string, string> { ["*Front"] = "*Спереди", ["*Top"] = "*Сверху", ["*Right"] = "*Справа",
                    ["*Isometric"] = "*Изометрия", ["*Back"] = "*Сзади", ["*Bottom"] = "*Снизу", ["*Left"] = "*Слева" };
                if (ru.TryGetValue(swViewName, out var local))
                    view = drawingDoc.CreateDrawViewFromModelView3(modelPath, local, posX, posY, 0.0) as IView;
            }
            if (view == null)
                throw new ToolFail("VIEW_CREATION_FAILED", $"CreateDrawViewFromModelView3 returned null for view '{viewType}'. Ensure model_path is saved to disk.");

            // Apply scale
            view.ScaleDecimal = scale;

            // Apply display mode (drafting convention). swDisplayMode_e:
            //   0=WIREFRAME, 1=HIDDEN_GREYED (Hidden Lines Visible/HLV), 2=HIDDEN (Hidden Lines Removed/HLR),
            //   3=SHADED, 7=SHADED_EDGES. IView.SetDisplayMode3(UseParent, Mode, Facetted, Edges) — reflection-verified.
            // If display_mode is not given, DEFAULT orthographic views to HLV (drafting rule: show hidden lines
            // for reference, never dimension them); leave isometric at the document default (shaded/HLR).
            int? modeToSet = null;
            switch (displayMode)
            {
                case "wireframe":                       modeToSet = 0; break;
                case "hlv":
                case "hidden_lines_visible":
                case "hidden_greyed":                   modeToSet = 1; break;
                case "hlr":
                case "hidden_lines_removed":
                case "hidden":                          modeToSet = 2; break;
                case "shaded":                          modeToSet = 3; break;
                case "shaded_edges":                    modeToSet = 7; break;
                case "default":                         modeToSet = null; break;
                case "":
                    // no explicit request → view-type default
                    if (viewType != "isometric") modeToSet = 1; // ortho views → HLV
                    break;
                default:
                    throw new ToolFail("BAD_PARAMETER",
                        $"display_mode '{displayMode}' is not supported. Use: hlv, hlr, wireframe, shaded, shaded_edges, default.");
            }
            if (modeToSet.HasValue)
            {
                try { view.SetDisplayMode3(false, modeToSet.Value, false, false); } catch { /* best-effort; never break the view creation */ }
            }

            modelDoc.GraphicsRedraw2();

            return new ToolOutput { Features = new List<string> { view.Name } };
        });

        // The path of the first open, saved part — the model a drawing view shows when model_path is not given.
        private string FirstOpenPartPath()
        {
            foreach (var d in _solidWorks.GetDocuments() as object[] ?? new object[0])
                if (d is IPartDoc && d is IModelDoc2 md && !string.IsNullOrEmpty(md.GetPathName()))
                    return md.GetPathName();
            throw new ToolFail("MODEL_NOT_FOUND", "model_path not provided and no saved part is open. Provide model_path or open a part first.");
        }

        public ExecutionResponse AddFlatPatternView(ToolRequest request) => Run(request, true, p =>
        {
            double posX = PNum(p, "pos_x", 0.1);
            double posY = PNum(p, "pos_y", 0.1);
            double scale = PNum(p, "scale", 1.0);
            string modelPath = PStr(p, "model_path", "");
            string configName = PStr(p, "config_name", "");
            bool hideBendLines = PBool(p, "hide_bend_lines", false);
            bool flipView = PBool(p, "flip_view", false);

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            // model_path not given: the first open (saved) part
            if (string.IsNullOrEmpty(modelPath)) modelPath = FirstOpenPartPath();

            // Resolve the configuration name (CreateFlatPatternViewFromModelView3 needs it).
            // If not supplied, read the referenced part's ACTIVE configuration; fall back to its first configuration
            // (not "Default": on a Russian SolidWorks it is «По умолчанию»).
            if (string.IsNullOrEmpty(configName))
            {
                configName = (_solidWorks.GetConfigurationNames(modelPath) as string[])?.FirstOrDefault() ?? "Default";
                object[] docs2 = _solidWorks.GetDocuments() as object[];
                if (docs2 != null)
                {
                    foreach (var d in docs2)
                    {
                        var md = d as IModelDoc2;
                        if (md != null && string.Equals(md.GetPathName(), modelPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var cfg = md.ConfigurationManager?.ActiveConfiguration;
                                if (cfg != null && !string.IsNullOrEmpty(cfg.Name))
                                    configName = cfg.Name;
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }

            // CreateFlatPatternViewFromModelView3(ModelName, ConfigName, LocX, LocY, LocZ, HideBendLines, FlipView)
            // -> IView. Internally flattens the sheet-metal body; the part must be sheet metal (have a Flat-Pattern).
            // It returns null for a part that is not OPEN (a path alone is not enough, unlike CreateDrawViewFromModelView3)
            // — loaded for the call (WithModelLoaded); the drawing keeps its own reference after the part is closed.
            var view = WithModelLoaded(modelPath, modelDoc.GetTitle(), () =>
                drawingDoc.CreateFlatPatternViewFromModelView3(modelPath, configName, posX, posY, 0.0, hideBendLines, flipView) as IView,
                configName);
            if (view == null)
                throw new ToolFail("FLAT_PATTERN_VIEW_FAILED",
                    $"CreateFlatPatternViewFromModelView3 returned null for '{modelPath}' (config '{configName}'). Ensure the part is SHEET METAL (has a Flat-Pattern feature) and is saved to disk.");

            view.ScaleDecimal = scale;
            modelDoc.GraphicsRedraw2();

            return new ToolOutput
            {
                Features = new List<string> { view.Name },
                Geometry = new JObject
                {
                    ["view_name"] = view.Name,
                    ["config"] = configName,
                    ["hide_bend_lines"] = hideBendLines
                }
            };
        });

        public ExecutionResponse AddDrawingDimension(ToolRequest request) => Run(request, true, p =>
        {
            double? px = PNumN(p, "px");
            double? py = PNumN(p, "py");
            double? value = PNumN(p, "value");
            double labelOffsetX = PNum(p, "label_offset_x", 0.0);
            double labelOffsetY = PNum(p, "label_offset_y", -0.015);

            if (px == null || py == null || value == null)
                throw new ToolFail("MISSING_PARAMETER", "px, py, and value are required.");

            ActiveDrawingOrFail(out var modelDoc, hint: "part and sketch dimensions are add_dimension");

            modelDoc.ClearSelection2(true);
            modelDoc.GraphicsRedraw2();

            // off for the rest of this call, restored on every path — exceptions included (PrefScope)
            using var dimPrefs = new PrefScope(_solidWorks).Toggle(swUserPreferenceToggle_e.swInputDimValOnCreate, false);

            // Drawing views project the model as EDGE entities (not SKETCHSEGMENT). Try EDGE first,
            // then SILHOUETTE (curved-body outlines), then SKETCHSEGMENT for sketched drawing geometry.
            bool selected = modelDoc.Extension.SelectByID2(
                "", "EDGE", px.Value, py.Value, 0, false, 0, null, 0);
            if (!selected)
                selected = modelDoc.Extension.SelectByID2(
                    "", "SILHOUETTE", px.Value, py.Value, 0, false, 0, null, 0);
            if (!selected)
                selected = modelDoc.Extension.SelectByID2(
                    "", "SKETCHSEGMENT", px.Value, py.Value, 0, false, 0, null, 0);

            if (!selected)
            {
                throw new ToolFail("SEGMENT_NOT_FOUND", $"No drawing edge/segment found at or near ({px.Value}, {py.Value}). Ensure the point lies on a projected model edge in a view.");
            }

            var dim = modelDoc.AddDimension2(px.Value + labelOffsetX, py.Value + labelOffsetY, 0) as IDisplayDimension;

            if (dim == null)
                throw new ToolFail("DIMENSION_CREATION_FAILED", "AddDimension2 returned null in drawing context.");

            var swDim = dim.GetDimension2(0) as SolidWorks.Interop.sldworks.IDimension;
            if (swDim != null)
                swDim.SystemValue = value.Value;

            return new ToolOutput { Dimensions = new List<string> { $"px={px.Value},py={py.Value},value={value.Value}" } };
        });

        // The owners whose dimensions stay: the named features and the sketches under them. Resolved BEFORE anything is
        // inserted, so a wrong name fails without leaving every model dimension on the sheet.
        private HashSet<string> FeatureKeepSet(IView[] views, List<string> features, List<string> missing)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in views)
            {
                if (!(v.ReferencedDocument is IModelDoc2 m)) continue;
                foreach (var name in features)
                {
                    var f = FindFeatureByName(m, name, null);
                    if (f == null) continue;
                    found.Add(name);
                    keep.Add(f.Name);
                    for (var sub = f.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
                        keep.Add(sub.Name);
                }
            }
            if (keep.Count == 0) throw new ToolFail("FEATURE_NOT_FOUND", $"None of the features {string.Join(", ", features)} is in the drawn model — names as analyze_model(features) gives them.");
            missing.AddRange(features.Where(f => !found.Contains(f)));
            return keep;
        }

        // Deletes the display dimensions this call inserted (not in `before`) whose owner is not in `keep` (the owner is
        // the middle of the full name, D1@Эскиз3@link_upper.Part). Returns how many went.
        private int KeepFeatureDimensions(IModelDoc2 d, IView[] views, HashSet<string> keep, ISet<string> before)
        {
            d.ClearSelection2(true);
            int n = 0;
            foreach (var v in views)
                foreach (var dd in ViewDimensions(v).ToList())
                {
                    string sel = dd.GetNameForSelection();
                    if (before.Contains(sel)) continue;   // there before this call: drawn by hand or asked for earlier
                    string full = (dd.GetDimension2(0) as IDimension)?.FullName ?? "";
                    var parts = full.Split('@');
                    if (parts.Length >= 2 && keep.Contains(parts[1])) continue;
                    try { if (d.Extension.SelectByID2(sel, "DIMENSION", 0, 0, 0, true, 0, null, 0)) n++; } catch { }
                }
            if (n > 0 && !d.Extension.DeleteSelection2(0)) TryEditDelete(d);
            d.ClearSelection2(true);
            return n;
        }

        // auto_dimension_drawing — automatically transfer the MODEL's driving dimensions into the drawing
        // views (the SolidWorks "Insert Model Items > Dimensions" automation). This is the robust
        // alternative to add_drawing_dimension's fragile coordinate pick (the drawing
        // counterpart of part index-based selection): the dimensions come straight from the model's
        // parametric dimensions, correctly placed, instead of being guessed from a sheet coordinate.
        // Uses IDrawingDoc.InsertModelAnnotations3(Option, Types, AllViews, DuplicateDims, HiddenFeatureDims,
        // UsePlacementInSketch) — signature verified against the interop (IView.InsertModelDimensions does
        // NOT exist; the insertion API lives on IDrawingDoc).
        // Bumps state_version (the drawing gains annotations); echoes inserted_count in
        // result_geometry for in-band verification so the caller can confirm dims landed
        // without a separate analyze_drawing round-trip.
        public ExecutionResponse AutoDimensionDrawing(ToolRequest request) => Run(request, true, p =>
        {
            bool allViews = PBool(p, "all_views", true);
            bool includeUnmarked = PBool(p, "include_unmarked", false);
            bool eliminateDuplicates = PBool(p, "eliminate_duplicates", true);
            bool arrange = PBool(p, "arrange", true);

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);
            string viewName = PStr(p, "view");
            IView only = null;
            if (viewName != null)
            {
                only = FindDrawingView(drawingDoc, viewName) ?? throw new ToolFail("VIEW_NOT_FOUND", $"No view '{viewName}'. Views: {string.Join(", ", DrawingViewNames(drawingDoc))}");
                allViews = false;
            }

            // InsertModelAnnotations3 args:
            //   Option = swImportModelItemsSource_e.swImportModelItemsFromEntireModel (0)
            //   Types  = swInsertAnnotation_e bitmask: swInsertDimensionsMarkedForDrawing (32768),
            //            optionally | swInsertDimensionsNotMarkedForDrawing (524288) for ALL driving dims
            //   AllViews, DuplicateDims, HiddenFeatureDims(false), UsePlacementInSketch(true)
            int option = 0;
            int types = 32768;
            if (includeUnmarked) types |= 524288;
            bool duplicateDims = !eliminateDuplicates;

            var views = only != null ? new[] { only } : DrawingViews(drawingDoc).ToArray();
            // only the sizes the drawing should give: the dimensions of the named features and of their sketches stay
            // (inserting from SELECTED features gave nothing on SW 2024 — the features selected, 0 inserted)
            var keepFeat = PArr(p, "features").Select(t => (string)t).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var missing = new List<string>();
            var keep = keepFeat.Count > 0 ? FeatureKeepSet(views, keepFeat, missing) : null;
            // the dimensions already on the sheet stay as they are: only this call's are filtered and arranged
            var before = new HashSet<string>(views.SelectMany(v => ViewDimensions(v)).Select(dd => dd.GetNameForSelection()));

            modelDoc.ClearSelection2(true);
            // all_views=false inserts into the SELECTED views: the named one
            if (only != null && !modelDoc.Extension.SelectByID2(only.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0))
                throw new ToolFail("VIEW_NOT_SELECTED", $"Could not select view '{only.Name}'.");

            object inserted = drawingDoc.InsertModelAnnotations3(
                option, types, allViews, duplicateDims, false, true);
            modelDoc.ClearSelection2(true);

            int insertedCount = 0;
            var arr = inserted as object[];
            if (arr != null) insertedCount = arr.Length;
            int left = 0;
            if (keep != null && insertedCount > 0)
                left = KeepFeatureDimensions(modelDoc, views, keep, before);
            // they come in where they stood in the sketch — over the geometry; moved out of the views
            int arranged = arrange && insertedCount > 0 ? ArrangeDimensions(modelDoc, views, before) : 0;

            modelDoc.GraphicsRedraw2();
            // InsertModelAnnotations3 places nothing, without a word, into a drawing never saved (box.sldprt, 4 views:
            // 0 in three tries; the same drawing saved — 7)
            string warning = insertedCount == 0 && string.IsNullOrEmpty(modelDoc.GetPathName())
                ? "Nothing was inserted: SolidWorks inserts no model dimensions into a drawing that was never saved — save_document(file_path) first, then call again."
                : null;

            ExecLog.Write($"auto_dimension_drawing: inserted {insertedCount} dimension(s) " +
                $"allViews={allViews} includeUnmarked={includeUnmarked} types={types}");

            var geo = new JObject { ["inserted_count"] = insertedCount, ["all_views"] = allViews, ["include_unmarked"] = includeUnmarked };
            if (arranged > 0) geo["arranged"] = arranged;
            if (keepFeat.Count > 0) { geo["kept_for_features"] = Math.Max(0, insertedCount - left); geo["left_out"] = left; }
            if (missing.Count > 0) geo["features_not_found"] = new JArray(missing);
            if (only != null) geo["view"] = only.Name;
            if (warning != null) geo["warning"] = warning;
            return new ToolOutput { Geometry = geo };
        });

        // auto_center_marks — automatically place center marks (+ extended centerlines) on every hole/slot
        // in every model view of the active drawing (the SolidWorks "Auto Insert > Center Marks"). The
        // difficulty-2 forcing-function capability: a bracket/flange with holes needs center marks the
        // model-item auto-dimension pass doesn't add. ROBUST/automatic — `IView.AutoInsertCenterMarks2`
        // operates per view with NO coordinate pick (the drawing-side analogue of
        // like index-based selection). Bumps state_version; echoes
        // the total center-mark count in result_geometry (in-band verify).
        public ExecutionResponse AutoCenterMarks(ToolRequest request) => Run(request, true, p =>
        {
            bool includeSlots = PBool(p, "include_slots", true);
            bool extendedLines = PBool(p, "extended_lines", true);

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            // swAutoInsertCenterMarkTypes_e bitmask (inlined ints): Hole=1, Slots=4.
            int insertType = 1;
            if (includeSlots) insertType |= 4;
            int insertOption = 2; // swCenterMarkStyle_e.swCenterMark_Single

            int viewsProcessed = 0;
            int viewsMarked = 0;
            var modelViews = DrawingViews(drawingDoc).ToList();   // listed first: marking a view may touch the chain
            foreach (var view in modelViews)
            {
                int vType = -1;
                try { vType = view.Type; } catch { }
                bool ok = false;
                // Many drawing annotation ops act on the ACTIVE view — activate it first.
                try { drawingDoc.ActivateView(view.Name); } catch { }
                try
                {
                    // AutoInsertCenterMarks2(InsertType, InsertOption, LinearSlotCenter,
                    //   ArcSlotCenter, UseDocumentDefaults, Size, Gap, ExtendedLines,
                    //   CenterLineFont, Angle). UseDocumentDefaults=true → SW's own center-mark
                    //   size/style (robust); the explicit args are then ignored by SW.
                    ok = view.AutoInsertCenterMarks2(insertType, insertOption,
                        includeSlots, includeSlots, true, 0.0025, 0.0,
                        extendedLines, true, 0.0);
                    viewsProcessed++;
                }
                catch (Exception exv) { ExecLog.Write($"auto_center_marks: view '{view.Name}' threw {exv.Message}"); }
                if (ok) viewsMarked++;
                ExecLog.Write($"auto_center_marks: view '{view.Name}' type={vType} ok={ok}");
            }

            // Commit so the inserted center marks/centerlines are realized (and so the per-view
            // counters below are not stale — this interop's GetCenterMarkCount can read 0 pre-rebuild).
            try { modelDoc.EditRebuild3(); } catch { }
            modelDoc.GraphicsRedraw2();

            // views_marked (the API success count) is the RELIABLE in-band signal that center marks
            // were auto-inserted; the raw mark/line counts are best-effort (this interop under-reports
            // them — verified visually that marks ARE present even when these read 0).
            int totalMarks = 0, totalLines = 0;
            foreach (var v in modelViews)
            {
                try { totalMarks += v.GetCenterMarkCount(); } catch { }
                try { totalLines += v.GetCenterLineCount(); } catch { }
            }

            ExecLog.Write($"auto_center_marks: views_marked={viewsMarked}/{viewsProcessed} marks={totalMarks} lines={totalLines} " +
                $"includeSlots={includeSlots} extendedLines={extendedLines}");

            return new ToolOutput
            {
                Geometry = new JObject
                {
                    ["views_marked"] = viewsMarked,
                    ["views_processed"] = viewsProcessed,
                    ["center_marks"] = totalMarks,   // best-effort (interop under-reports; marks ARE present)
                    ["center_lines"] = totalLines,   // best-effort
                    ["include_slots"] = includeSlots
                }
            };
        });

        // add_hole_callout — insert a hole callout (e.g. "n× ØD THRU") on the hole edge nearest the sheet
        // point (px, py) in the active drawing. Coordinate-based, mirroring add_drawing_dimension's EDGE
        // pick (the accepted #6 idiom; a drawing edge is selected by point). Uses
        // IDrawingDoc.AddHoleCallout2(X, Y, Z) — verified against the interop. Bumps state_version.
        // NOTE: coordinate selection is fragile for crowded geometry; prefer
        // auto_center_marks for centerlines and use this for explicit hole callouts where wanted.
        public ExecutionResponse AddHoleCallout(ToolRequest request) => Run(request, true, p =>
        {
            double? px = PNumN(p, "px");
            double? py = PNumN(p, "py");
            if (px == null || py == null)
                throw new ToolFail("MISSING_PARAMETER", "px and py are required (a point on the hole edge, sheet coordinates).");

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            // Select the hole's projected EDGE at the point (drawing views project the model as EDGE
            // entities; same pick order add_drawing_dimension uses). This also sets the owning view.
            modelDoc.ClearSelection2(true);
            bool selected = modelDoc.Extension.SelectByID2(
                "", "EDGE", px.Value, py.Value, 0, false, 0, null, 0);
            if (!selected)
                selected = modelDoc.Extension.SelectByID2(
                    "", "SILHOUETTE", px.Value, py.Value, 0, false, 0, null, 0);
            if (!selected)
                throw new ToolFail("EDGE_NOT_FOUND", $"No drawing hole edge found at or near ({px.Value}, {py.Value}). Point at a projected circular edge in a view.");

            object callout = drawingDoc.AddHoleCallout2(px.Value, py.Value, 0.0);
            if (callout == null)
                throw new ToolFail("CALLOUT_FAILED", "AddHoleCallout2 returned null — the selected edge may not be a hole/circular edge. Point at a hole's projected circle.");

            modelDoc.GraphicsRedraw2();

            return new ToolOutput { Dimensions = new List<string> { $"hole_callout@({px.Value},{py.Value})" } };
        });

        // add_section_view — create a SECTION VIEW by cutting along an EXISTING straight edge/line already in
        // the drawing (the robust method the user taught: "use the lines already in the technical drawing —
        // I chose a point on the cutting surface"). The difficulty-3 capability: a blind pocket's depth can't
        // be shown in a projected orthographic view (its floor is a hidden edge), but a section through it
        // exposes the depth as a real, dimensionable edge. Flow: select the existing edge at (edge_x,edge_y)
        // — a point on a projected straight edge that defines the cut location/direction — then
        // IDrawingDoc.CreateSectionViewAt5(X,Y,Z,Label,Options,Excluded,Depth) places the section at (px,py)
        // (with a CreateSectionViewAt2 fallback — At5 can return null on some states). NO new cutting line is
        // drawn (that earlier approach was fragile). Signatures verified against the
        // interop (flip = swCreateSectionView_ChangeDirection, 4). Bumps state_version.
        // NOTE: edge selection is coordinate-based — point at a real projected edge; a clean drawing
        // state matters (repeated failed attempts can corrupt section creation — restart/rebuild if so).
        public ExecutionResponse AddSectionView(ToolRequest request) => Run(request, true, p =>
        {
            double? edgeX = PNumN(p, "edge_x");
            double? edgeY = PNumN(p, "edge_y");
            double? x1 = PNumN(p, "x1");
            double? y1 = PNumN(p, "y1");
            double? x2 = PNumN(p, "x2");
            double? y2 = PNumN(p, "y2");
            double? px = PNumN(p, "px");
            double? py = PNumN(p, "py");
            string label = PStr(p, "label");
            if (string.IsNullOrEmpty(label)) label = "A";
            bool flip = PBool(p, "flip", false);
            double? scale = PNumN(p, "scale");

            // Two ways to define the cut: EDGE mode (edge_x,edge_y → cut ALONG an existing projected
            // edge — best when a real edge lies on the desired plane) or LINE mode (x1,y1,x2,y2 → draw a
            // cut line, best for cutting THROUGH a feature's interior, e.g. a pocket's middle, where no
            // edge exists). px,py = section placement (which side the section projects to).
            bool lineMode = (x1 != null && y1 != null && x2 != null && y2 != null);
            bool edgeMode = (edgeX != null && edgeY != null);
            if ((!lineMode && !edgeMode) || px == null || py == null)
                throw new ToolFail("MISSING_PARAMETER", "Provide either edge_x,edge_y (cut along an existing edge) OR x1,y1,x2,y2 (draw a cut line through a feature), plus px,py (placement). Sheet meters.");

            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            // ACTIVATE the model view the cut belongs to BEFORE drawing/selecting
            // the cut line, so the line is added to THAT VIEW's sketch — not the sheet. Without an active
            // view the section line has no owning view and CreateSectionViewAt5 returns null (and the At2
            // fallback then faulted SW with RPC_E_SERVERFAULT). The GUI activates the view implicitly when
            // the user clicks inside it (user-taught flow, 2026-07-10). view_name overrides; otherwise the
            // view is found by outline-containment of the cut point. ActivateView verified via reflection.
            double cutMidX = lineMode ? (x1.Value + x2.Value) / 2.0 : edgeX.Value;
            double cutMidY = lineMode ? (y1.Value + y2.Value) / 2.0 : edgeY.Value;
            string targetView = PStr(p, "view_name");
            if (string.IsNullOrEmpty(targetView))
                targetView = FindDrawingView(drawingDoc, null, v => OutlineContains(v, cutMidX, cutMidY))?.Name;
            if (!string.IsNullOrEmpty(targetView))
            {
                bool activated = false;
                try { activated = drawingDoc.ActivateView(targetView); } catch (Exception exa) { ExecLog.Write($"add_section_view: ActivateView threw {exa.Message}"); }
                ExecLog.Write($"add_section_view: ActivateView('{targetView}') -> {activated}");
            }
            else ExecLog.Write($"add_section_view: no model view contains cut point ({cutMidX},{cutMidY}) — proceeding without explicit activation");

            // Define the cut line and SELECT it (drawing views project the model as EDGE entities;
            // selecting the line/edge also sets the owning view). The selected line IS the section cut.
            modelDoc.ClearSelection2(true);
            bool sel = false;
            double selX = 0, selY = 0;     // a point on the selected cut line
            ISketchSegment cutLine = null; // drawn here (line mode) — removed again when no section comes of it
            string selType = "EDGE";
            if (lineMode)
            {
                // The cut line must live in the TARGET VIEW's sketch. When a view is active,
                // SketchManager.CreateLine works in VIEW-SKETCH space (model scale, view-centred) — NOT
                // sheet space. So transform the intended SHEET endpoints into view-sketch space via
                // view.GetSketch().ModelToSketchTransform (the published CodeStack recipe) before drawing;
                // otherwise the line lands far OUTSIDE the part and CreateSectionViewAt5 returns null — the
                // real ROOT CAUSE of #21 (a raw sheet-coord line drawn in an active view misses the geometry,
                // yet still object-selects, so the failure looked like a flaky API).
                IView tView = string.IsNullOrEmpty(targetView) ? null : FindDrawingView(drawingDoc, targetView);
                if (tView == null)
                    throw new ToolFail("SECTION_LINE_FAILED", "Could not resolve the model view to draw the cut line in. Point the cut inside a view, or pass view_name.");
                double[] vp1 = TransformSheetToViewSketch(tView, x1.Value, y1.Value);
                double[] vp2 = TransformSheetToViewSketch(tView, x2.Value, y2.Value);
                if (vp1 == null || vp2 == null || vp1.Length < 3 || vp2.Length < 3)
                    throw new ToolFail("SECTION_LINE_FAILED", "Could not transform the sheet coordinates into the view sketch space.");
                drawingDoc.ActivateView(tView.Name);
                var seg = cutLine = modelDoc.SketchManager.CreateLine(vp1[0], vp1[1], vp1[2], vp2[0], vp2[1], vp2[2]) as ISketchSegment;
                ExitSketch(modelDoc);
                if (seg == null)
                    throw new ToolFail("SECTION_LINE_FAILED", "Could not sketch the cut line in the view.");
                selX = (x1.Value + x2.Value) / 2.0; selY = (y1.Value + y2.Value) / 2.0; selType = "SKETCHSEGMENT";
                modelDoc.ClearSelection2(true);
                // The transformed line DISPLAYS at the intended sheet location, so a sheet-coord pick finds
                // it (also a self-check that the transform was right); object-select is the robust fallback.
                sel = modelDoc.Extension.SelectByID2("", "SKETCHSEGMENT", selX, selY, 0, false, 0, null, 0);
                if (!sel) try { sel = seg.Select4(false, null); } catch { }
                ExecLog.Write($"add_section_view: LINE sheet ({x1.Value},{y1.Value})-({x2.Value},{y2.Value}) -> viewSketch ({R6(vp1[0])},{R6(vp1[1])})-({R6(vp2[0])},{R6(vp2[1])}) selected={sel} coordPick={(sel ? "y" : "n")}");
                if (!sel)
                {
                    DeleteCutLine(modelDoc, cutLine);   // no section comes of it: the line drawn here goes again
                    throw new ToolFail("SECTION_LINE_FAILED", "Drew the cut line but could not select it.");
                }
            }
            else
            {
                selX = edgeX.Value; selY = edgeY.Value;
                sel = modelDoc.Extension.SelectByID2("", "EDGE", selX, selY, 0, false, 0, null, 0); selType = "EDGE";
                if (!sel) { sel = modelDoc.Extension.SelectByID2("", "SILHOUETTE", selX, selY, 0, false, 0, null, 0); selType = "SILHOUETTE"; }
                if (!sel) { sel = modelDoc.Extension.SelectByID2("", "SKETCHSEGMENT", selX, selY, 0, false, 0, null, 0); selType = "SKETCHSEGMENT"; }
                ExecLog.Write($"add_section_view: EDGE at ({selX},{selY}) selected={sel}");
                if (!sel)
                    throw new ToolFail("EDGE_NOT_FOUND", $"No projected straight edge/line found at or near ({selX}, {selY}). Point at an edge already shown in a view, or use x1,y1,x2,y2 to draw a cut line through the feature.");
            }

            int options = 0;
            if (flip) options |= 4; // swCreateSectionViewAtOptions_e.swCreateSectionView_ChangeDirection

            // SectionDepth = 0 → a full section. Options 0 → standard aligned.
            IView view = null;
            try { view = drawingDoc.CreateSectionViewAt5(px.Value, py.Value, 0, label, options, null, 0.0) as IView; }
            catch (Exception ex5) { ExecLog.Write($"add_section_view: At5 threw {ex5.Message}"); }
            ExecLog.Write($"add_section_view: CreateSectionViewAt5 -> {(view == null ? "null" : view.Name)}");
            // No CreateSectionViewAt2 fallback: on SW it threw RPC_E_SERVERFAULT and crashed SolidWorks; a null from
            // At5 is a clean, recoverable failure.
            if (view == null)
            {
                DeleteCutLine(modelDoc, cutLine);
                throw new ToolFail("SECTION_VIEW_FAILED",
                    "CreateSectionView returned null — the selected edge could not define a valid section, or the drawing state is corrupted (restart/rebuild the server and retry on a clean state).");
            }

            string warning = null;
            if (scale != null && scale.Value > 0)
                try { view.ScaleDecimal = scale.Value; }
                catch (Exception ex) { warning = $"The section view keeps its own scale: setting {scale.Value} failed ({ex.Message})."; }

            modelDoc.GraphicsRedraw2();

            string viewName = "";
            try { viewName = view.Name; } catch { }
            ExecLog.Write($"add_section_view: created '{viewName}' label={label} mode={(lineMode ? "line" : "edge")} cut=({selX},{selY}) at ({px.Value},{py.Value}) flip={flip}");

            return new ToolOutput
            {
                Features = new List<string> { viewName },
                Geometry = WithWarning(new JObject
                {
                    ["section_view"] = viewName,
                    ["label"] = label
                }, warning)
            };
        });

        // Transform a DRAWING SHEET point (meters) into a view's SKETCH coordinate space, so a line drawn via
        // SketchManager while that view is active appears at the intended sheet location. Published recipe:
        // view.GetSketch().ModelToSketchTransform + IMathUtility (handles the view's scale, offset and any
        // rotation). Returns {x,y,z} in view-sketch space, or null on failure.
        private double[] TransformSheetToViewSketch(IView view, double sheetX, double sheetY)
        {
            try
            {
                var sk = view.GetSketch() as ISketch;
                if (sk == null) return null;
                var xform = sk.ModelToSketchTransform;
                if (xform == null) return null;
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                if (mu == null) return null;
                var pt = mu.CreatePoint(new double[] { sheetX, sheetY, 0.0 }) as IMathPoint;
                if (pt == null) return null;
                pt = pt.MultiplyTransform(xform) as IMathPoint;
                if (pt == null) return null;
                return pt.ArrayData as double[];
            }
            catch (Exception ex) { ExecLog.Write($"TransformSheetToViewSketch err: {ex.Message}"); return null; }
        }

        private static void DeleteCutLine(IModelDoc2 d, ISketchSegment line)
        {
            if (line == null) return;
            try { d.ClearSelection2(true); if (line.Select4(false, null)) d.EditDelete(); } catch { }
            d.ClearSelection2(true);
        }

        // move_drawing_view — reposition / rescale a drawing view (mm on the sheet). IView.Position / ScaleDecimal
        public ExecutionResponse MoveDrawingView(ToolRequest request) => Run(request, true, p =>
        {
            var dr = ActiveDrawingOrFail(out var d);
            string vn = PStr(p, "view") ?? throw new ToolFail("MISSING_PARAMETER", "view (name) is required.");
            var view = FindDrawingView(dr, vn) ?? throw new ToolFail("VIEW_NOT_FOUND", $"No view '{vn}'. Views: {string.Join(", ", DrawingViewNames(dr))}");
            if (p["scale"] != null) view.ScaleDecimal = PNum(p, "scale", 1);
            if (p["x_mm"] != null || p["y_mm"] != null)
            {
                var pos = view.Position as double[] ?? new double[] { 0, 0 };
                view.Position = new[] { p["x_mm"] != null ? PNum(p, "x_mm", 0) / MM : pos[0], p["y_mm"] != null ? PNum(p, "y_mm", 0) / MM : pos[1] };
            }
            d.EditRebuild3();
            var np = view.Position as double[];
            return new JObject { ["view"] = view.Name, ["position_mm"] = np == null ? null : new JArray(Math.Round(np[0] * MM, 1), Math.Round(np[1] * MM, 1)), ["scale"] = view.ScaleDecimal };
        });
    }
}
