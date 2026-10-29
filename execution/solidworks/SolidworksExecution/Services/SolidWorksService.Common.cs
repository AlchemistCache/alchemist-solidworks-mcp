using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Helpers shared by every tool family (one implementation each) =====================
    // SolidBodies                 — a part's visible solid bodies, in the order every enumeration below walks them.
    // FlattenFaces / FlattenEdges — the solid-body face / edge enumeration analyze_model(faces|edges) reports: a list
    //                               index here IS the `i` every face_index / edge_index parameter means.
    // FaceAt / EdgeAt             — that enumeration by index, with a readable error.
    // ResolveFace / SelectEntity  — the face a tool works on (face_index, else a point on it); select with a mark.
    // PrefScope                   — change SolidWorks user preferences and get them back on Dispose (`using`).
    // SwInstallDir / LangFile     — the installed SolidWorks folders (from the running SW, not a hard-coded path).
    // ToDoubles / ToInts          — a COM VARIANT array → double[] / int[].
    // FindDrawingView             — a view of the active drawing sheet by name (or the first matching a predicate,
    //                               e.g. OutlineContains: the view under a sheet point).
    // MateName / MateTypeFromName / MateAlignName / MateAlignFromName — ONE mate vocabulary (snake_case, from the
    //                               enums — display names are localized) for add_mate, analyze_assembly, list_mates,
    //                               edit_mate and insert_and_mate.
    // Sketch3D                    — a 3D sketch drawn with exact coordinates, closed again on every path.
    // AddToDbScope / ExitSketch / SelectSketch / SelectSketchOrigin — exact sketch coordinates for a `using`; leave
    //                               the active sketch; select a sketch by name; the origin as a dimension/relation end.
    // Features / SubFeatures      — the feature tree as a sequence (no hand-written GetNextFeature loops).
    // RollForward                 — the rollback bar back to the end after a temporary rollback, failure reported.
    // BeforeFlatPattern           — a generic feature call on a sheet-metal part, made before its Flat-Pattern.
    // CheckCreated / FeatureFlag  — the one rule for a feature SolidWorks built but flagged (GetErrorCode2).
    // Activate / WithModelLoaded  — make a document active; load a model for one call and put everything back.
    // CleanDocs / Unmodify        — a reading tool leaves clean documents clean (Run keepClean).
    // DefaultTemplate / NewDoc    — the template for a document type; a new blank document from it.
    // LastFeature / LastFeatureName — the feature an API call just added / the last one in the tree.
    // DefaultPlane                — Front/Top/Right (or Спереди/Сверху/Справа) on any UI language.
    // VisibleWindow               — the SolidWorks frame shown for one call (APIs that fail while it is hidden).
    // Comps / FindComp            — an assembly's components; one by Name2 or a unique short name.
    // MateFeatures                — every mate in creation order, mate folders included.
    // FindFeatureByName / SelectPlaneFlexible — a feature by its tree name; a plane by name or default position.
    // CpmGet / DocProp            — a custom property (configuration value first, then the file's).
    // SafeName / LengthUnitToMm   — a file-name-safe string; the document's length unit in mm.
    // FeatureResult               — {feature, type} of a sheet-metal / weldment feature, through CheckCreated.
    public partial class SolidWorksService
    {
        private static List<IBody2> SolidBodies(IPartDoc part) =>
            (part?.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? new object[0]).OfType<IBody2>().ToList();

        private static List<IFace2> FlattenFaces(IPartDoc partDoc) =>
            SolidBodies(partDoc).SelectMany(b => (b.GetFaces() as object[] ?? new object[0]).OfType<IFace2>()).ToList();

        private static List<IEdge> FlattenEdges(IPartDoc partDoc) =>
            SolidBodies(partDoc).SelectMany(b => (b.GetEdges() as object[] ?? new object[0]).OfType<IEdge>()).ToList();

        private IFace2 FaceAt(IModelDoc2 d, int index)
        {
            var faces = FlattenFaces(d as IPartDoc);
            if (index < 0 || index >= faces.Count)
                throw new ToolFail("FACE_NOT_FOUND", $"face_index {index} out of range (part has {faces.Count} faces; see analyze_model(faces)).");
            return faces[index];
        }

        private IEdge EdgeAt(IModelDoc2 d, int index)
        {
            var edges = FlattenEdges(d as IPartDoc);
            if (index < 0 || index >= edges.Count)
                throw new ToolFail("EDGE_NOT_FOUND", $"edge_index {index} out of range (part has {edges.Count} edges; see analyze_model(edges)).");
            return edges[index];
        }

        // The face a tool works on: face_index (the `i` of analyze_model(faces) — preferred, exact) or face_x/y/z (a
        // point lying ON the face, model meters; a coordinate pick misses on edges and revolve end caps).
        private IFace2 ResolveFace(IModelDoc2 d, JObject p)
        {
            int? index = PIntN(p, "face_index");
            if (index != null) return FaceAt(d, index.Value);
            double? x = PNumN(p, "face_x"), y = PNumN(p, "face_y"), z = PNumN(p, "face_z");
            if (x == null || y == null || z == null)
                throw new ToolFail("MISSING_PARAMETER", "Provide face_index (from analyze_model(faces)) or face_x/face_y/face_z (a point ON the face).");
            // by geometry: the screen pick shoots a ray through the point — it found nothing on a face seen edge-on (a
            // servo's side face from the front view) and, for a point on no face, took whatever face the ray met (a rib
            // 12 mm away became the sketch plane). The ray only where there are no solid bodies to measure.
            if (FaceAtPoint(d, x.Value, y.Value, z.Value, 0.0005, out double nearest) is IFace2 near) return near;
            if (!double.IsInfinity(nearest))
                throw new ToolFail("FACE_NOT_FOUND", $"No face at ({x * MM:0.###}, {y * MM:0.###}, {z * MM:0.###}) mm — the nearest face is " +
                    $"{nearest * MM:0.##} mm away. Give a point ON the face (not on an edge), or face_index from analyze_model(faces).");
            d.ClearSelection2(true);
            bool picked = d.Extension.SelectByID2("", "FACE", x.Value, y.Value, z.Value, false, 0, null, 0);
            var face = picked ? (d.SelectionManager as ISelectionMgr)?.GetSelectedObject6(1, -1) as IFace2 : null;
            d.ClearSelection2(true);
            return face ?? throw new ToolFail("FACE_NOT_FOUND",
                $"No face at ({x}, {y}, {z}) — give a point ON the face (not on an edge), or face_index from analyze_model(faces).");
        }

        // Select a component, a face / edge / vertex or a feature, appending or not, with the selection mark the next
        // feature / mate API reads — one way for every kind (a component and an entity take SelectData, a feature the mark).
        private static bool SelectEntity(IModelDoc2 d, object entity, bool append, int mark = 0)
        {
            var sd = (d.SelectionManager as ISelectionMgr)?.CreateSelectData();
            if (sd != null) sd.Mark = mark;
            return entity is IComponent2 c ? c.Select4(append, sd, false)
                 : entity is IEntity e ? e.Select4(append, sd)
                 : entity is IFeature f && f.Select2(append, mark);
        }

        // using (new PrefScope(_solidWorks).Toggle(swX, false).Int(swY, 2)) { … } — the old values come back even when
        // the body throws (a preference left changed survives into the user's own SolidWorks sessions).
        private sealed class PrefScope : IDisposable
        {
            private readonly ISldWorks _sw;
            private readonly IModelDoc2 _doc;
            private readonly List<Action> _undo = new List<Action>();

            public PrefScope(ISldWorks sw, IModelDoc2 doc = null) { _sw = sw; _doc = doc; }

            public PrefScope Toggle(swUserPreferenceToggle_e pref, bool value)
            {
                int k = (int)pref;
                if (_doc != null) { bool old = _doc.GetUserPreferenceToggle(k); _doc.SetUserPreferenceToggle(k, value); _undo.Add(() => _doc.SetUserPreferenceToggle(k, old)); }
                else { bool old = _sw.GetUserPreferenceToggle(k); _sw.SetUserPreferenceToggle(k, value); _undo.Add(() => _sw.SetUserPreferenceToggle(k, old)); }
                return this;
            }

            public PrefScope Int(swUserPreferenceIntegerValue_e pref, int value)
            {
                int k = (int)pref;
                if (_doc != null) { int old = _doc.GetUserPreferenceIntegerValue(k); _doc.SetUserPreferenceIntegerValue(k, value); _undo.Add(() => _doc.SetUserPreferenceIntegerValue(k, old)); }
                else { int old = _sw.GetUserPreferenceIntegerValue(k); _sw.SetUserPreferenceIntegerValue(k, value); _undo.Add(() => _sw.SetUserPreferenceIntegerValue(k, old)); }
                return this;
            }

            public PrefScope Double(swUserPreferenceDoubleValue_e pref, double value)
            {
                int k = (int)pref;
                if (_doc != null) { double old = _doc.GetUserPreferenceDoubleValue(k); _doc.SetUserPreferenceDoubleValue(k, value); _undo.Add(() => _doc.SetUserPreferenceDoubleValue(k, old)); }
                else { double old = _sw.GetUserPreferenceDoubleValue(k); _sw.SetUserPreferenceDoubleValue(k, value); _undo.Add(() => _sw.SetUserPreferenceDoubleValue(k, old)); }
                return this;
            }

            public PrefScope Str(swUserPreferenceStringValue_e pref, string value)
            {
                int k = (int)pref;
                if (_doc != null) { string old = _doc.GetUserPreferenceStringValue(k); _doc.SetUserPreferenceStringValue(k, value); _undo.Add(() => _doc.SetUserPreferenceStringValue(k, old)); }
                else { string old = _sw.GetUserPreferenceStringValue(k); _sw.SetUserPreferenceStringValue(k, value); _undo.Add(() => _sw.SetUserPreferenceStringValue(k, old)); }
                return this;
            }

            public void Dispose()
            {
                for (int i = _undo.Count - 1; i >= 0; i--) { try { _undo[i](); } catch { } }
                _undo.Clear();
            }
        }

        // The default plane meant by `name` — "Front Plane" / "Спереди" / "front" (0), Top / Сверху (1), Right / Справа (2) —
        // as the first three RefPlane features from `first` (a part, an assembly or a component's tree): the names are
        // localized, the order is not. null when `name` is no default-plane name.
        private static IFeature DefaultPlane(IFeature first, string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            int ord = n.Contains("front") || n.Contains("спереди") ? 0 : n.Contains("top") || n.Contains("сверху") ? 1
                    : n.Contains("right") || n.Contains("справа") ? 2 : -1;
            if (ord < 0) return null;
            int seen = 0;
            for (var f = first; f != null; f = f.GetNextFeature() as IFeature)
                if (f.GetTypeName2() == "RefPlane" && seen++ == ord) return f;
            return null;
        }

        // Some APIs fault or return null while the frame of our own (invisible) SolidWorks is hidden — ExportToDWG2
        // (RPC_E_SERVERFAULT, document lost), CreateFlatPatternViewFromModelView3, GenerateReport (result plots).
        // using (VisibleWindow()) { … } shows it for that call only and hides it again on every path.
        private IDisposable VisibleWindow()
        {
            bool was = _solidWorks.Visible;
            if (!was) _solidWorks.Visible = true;
            return new OnDispose(() => { if (!was) { try { _solidWorks.Visible = false; } catch { } } });
        }

        private sealed class OnDispose : IDisposable
        {
            private Action _a;
            public OnDispose(Action a) { _a = a; }
            public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
        }

        // A 3D sketch drawn with AddToDB (exact coordinates, no snapping to nearby geometry); AddToDB is restored and the
        // sketch closed even when `draw` throws. Returns the new sketch feature.
        private IFeature Sketch3D(IModelDoc2 d, Action<ISketchManager> draw)
        {
            var skm = d.SketchManager;
            d.ClearSelection2(true);
            skm.Insert3DSketch(true);
            try
            {
                using (new AddToDbScope(skm)) draw(skm);
            }
            finally
            {
                if (skm.ActiveSketch != null) skm.Insert3DSketch(true);
            }
            return LastFeature(d);
        }

        // Exact sketch coordinates while it lives: AddToDB on — no inference / pixel snapping (a 1.59 mm line collapsed
        // nondeterministically without it: the snap tolerance depends on the window zoom); the previous value back on
        // Dispose, exceptions included.
        private sealed class AddToDbScope : IDisposable
        {
            private readonly ISketchManager _skm;
            private readonly bool _prev;
            public AddToDbScope(ISketchManager skm) { _skm = skm; _prev = skm.AddToDB; skm.AddToDB = true; }
            public void Dispose() => _skm.AddToDB = _prev;
        }

        private static void ExitSketch(IModelDoc2 d)
        {
            if (d.SketchManager.ActiveSketch != null) d.SketchManager.InsertSketch(true);
        }

        private static bool SelectSketch(IModelDoc2 d, string name, bool append = false, int mark = 0) =>
            d.Extension.SelectByID2(name, "SKETCH", 0, 0, 0, append, mark, null, 0);

        // The model origin as the second end of a sketch dimension or relation. "Point1@Origin" is its ENGLISH name only
        // (a Russian SolidWorks has «Исходная точка»), so it is selected as the origin feature's own sketch point —
        // the same external sketch point (swSelEXTSKETCHPOINTS), on any UI language (checked live on SW 2024 RU).
        private static bool SelectSketchOrigin(IModelDoc2 d, bool append)
        {
            var origin = Features(d).FirstOrDefault(f => f.GetTypeName2() == "OriginProfileFeature");
            var point = ((origin?.GetSpecificFeature2() as ISketch)?.GetSketchPoints2() as object[])?.OfType<ISketchPoint>().FirstOrDefault();
            return point != null && point.Select4(append, (d.SelectionManager as ISelectionMgr).CreateSelectData() as SelectData);
        }

        // The feature tree (top level, in order) and a feature's sub-features (a feature's sketch …). A user folder is
        // NOT a level: its features follow the FtrFolder entry in the walk, closed by a "<folder>___EndTag___" marker
        // (SW 2024, checked live) — the marker is no feature, IsFolderEnd filters it.
        private static IEnumerable<IFeature> Features(IModelDoc2 d)
        {
            for (var f = d.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature) yield return f;
        }

        private static bool IsFolderEnd(IFeature f) => f.Name?.EndsWith("___EndTag___", StringComparison.Ordinal) == true;

        private static IEnumerable<IFeature> SubFeatures(IFeature f)
        {
            for (var s = f.GetFirstSubFeature() as IFeature; s != null; s = s.GetNextSubFeature() as IFeature) yield return s;
        }

        // After a temporary rollback: the bar back to the END. Usage — the original error wins, a failed restore is loud:
        //   try { … } catch { if (rolled) RollForward(d); throw; }
        //   if (rolled && !RollForward(d)) throw new ToolFail("ROLLBACK_FAILED", …);
        private static bool RollForward(IModelDoc2 d)
        {
            try
            {
                if (d.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, "")) return true;
                ExecLog.Write("RollForward: EditRollback(end) returned false");
            }
            catch (Exception ex) { ExecLog.Write($"RollForward: {ex.Message}"); }
            return false;
        }

        // A sheet-metal part keeps its Flat-Pattern as the LAST feature with the rollback bar after it; a generic feature
        // call (FeatureCut4, InsertMirrorFeature2) would land after it — illegal, so it returns null. The bar goes just
        // before Flat-Pattern for the call (what the SolidWorks UI does) and back to the end afterwards, where
        // Flat-Pattern re-applies on top; the original error wins, a failed restore is ROLLBACK_FAILED.
        private T BeforeFlatPattern<T>(IModelDoc2 d, Func<T> call)
        {
            var flat = FindFlatPattern(d);
            bool rolled = false;
            if (flat != null)
            {
                try { rolled = d.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, flat.Name); }
                catch { rolled = false; }
            }
            T result;
            try { result = call(); }
            catch
            {
                if (rolled) RollForward(d);
                throw;
            }
            if (rolled && !RollForward(d)) throw new ToolFail("ROLLBACK_FAILED", RollbackStuck);
            return result;
        }

        private const string RollbackStuck = "The rollback bar could not be moved back to the end — the part is still rolled back; " +
                                             "drag the bar to the end of the tree (or rebuild) before going on.";

        // SolidWorks' verdict on a feature it has just built: null when clean, else "error ExtrusionNoEndFound" /
        // "warning …" (swFeatureError_e without its prefix).
        private static string FeatureFlag(IFeature f, out bool warning)
        {
            warning = false;
            int code;
            try { code = f.GetErrorCode2(out warning); } catch { return null; }
            if (code == 0 && !warning) return null;
            string name = Enum.IsDefined(typeof(swFeatureError_e), code)
                ? ((swFeatureError_e)code).ToString().Replace("swFeatureError", "") : $"code {code}";
            return (warning ? "warning " : "error ") + name;
        }

        // The one rule for a feature SolidWorks built but flagged. An ERROR (wrong or no geometry) is deleted — the
        // profile sketch stays — and the call fails FEATURE_ERROR: never COMPLETED with a broken feature in the tree.
        // A WARNING keeps the feature; the text is returned for the result. (hole_wizard is stricter: a warning fails
        // too, and it undoes all of its holes itself.)
        private static string CheckCreated(IModelDoc2 d, IFeature f, string what)
        {
            string flag = FeatureFlag(f, out bool warning);
            if (flag == null || warning) return flag;
            string name = f.Name;
            d.ClearSelection2(true);
            bool removed = f.Select2(false, 0) && d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
            throw new ToolFail("FEATURE_ERROR", $"{what} '{name}' was built with a SolidWorks {flag}" +
                (removed ? " and was removed" : " and could not be removed — delete it by hand") + "; check the inputs.");
        }

        // The ONE flange / bend position vocabulary — swFlangePositionTypes_e 1..5 ⇄ the names analyze reports and the
        // sheet-metal tools take; an unknown name is BAD_PARAMETER (never silently the default), an unknown code says so.
        private static readonly string[] FlangePositions = { null, "material_inside", "material_outside", "bend_outside", "centerline", "bend_sharp" };

        private static string FlangePositionName(int code) => code >= 1 && code < FlangePositions.Length ? FlangePositions[code] : $"unknown_{code}";

        private static int FlangePositionCode(string name, string param, params string[] allowed)
        {
            var ok = allowed.Length > 0 ? allowed : FlangePositions.Skip(1).ToArray();
            int code = Array.IndexOf(FlangePositions, (name ?? "").ToLowerInvariant());
            if (code < 1 || !ok.Contains(FlangePositions[code]))
                throw new ToolFail("BAD_PARAMETER", $"{param} must be {string.Join(" | ", ok)}, got '{name}'.");
            return code;
        }

        // An angle dimension (its value_si is radians, every other one meters) — the adapter shows degrees / mm.
        private static bool IsAngular(IDimension dim)
        {
            try { return dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular; }
            catch { return false; }
        }

        // a pattern's instance count: a plain number with no unit (42 teeth, not 42 m)
        private static bool IsCount(IDimension dim)
        {
            try { return dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeInteger; }
            catch { return false; }
        }

        // SolidWorks refuses a feature name silently (taken in this document, or invalid) — read it back.
        private static bool TryRename(IFeature f, string name)
        {
            f.Name = name;
            return f.Name == name;
        }

        private static void RenameOrFail(IFeature f, string name)
        {
            string old = f.Name;
            if (!TryRename(f, name))
                throw new ToolFail("BAD_PARAMETER", $"SolidWorks refused the name '{name}' (already used in this document, or invalid); '{old}' keeps its name.");
        }

        // Suppress / unsuppress in this configuration. SetSuppression2 answers false when it cannot — and also when
        // the feature is already in that state, so the state itself decides.
        private static void SuppressOrFail(IFeature f, bool suppress)
        {
            var action = suppress ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature;
            if (!f.SetSuppression2((int)action, (int)swInConfigurationOpts_e.swThisConfiguration, null) && f.IsSuppressed() != suppress)
                throw new ToolFail("FEATURE_ERROR", $"SolidWorks could not {(suppress ? "suppress" : "unsuppress")} '{f.Name}'.");
        }

        // The result geometry with a kept feature's warning added (when there is one).
        private static JObject WithWarning(JObject geometry, string warning)
        {
            if (warning == null) return geometry;
            geometry = geometry ?? new JObject();
            geometry["warning"] = warning;
            return geometry;
        }

        private IModelDoc2 Activate(string title)
        {
            int e = 0;
            return _solidWorks.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e) as IModelDoc2;
        }

        // A reading tool must not leave documents "modified" (a save prompt for files nobody edited). Some reads change a
        // document internally — ExportToDWG2, UpdateCutList, a configuration switch and back (measured on SW 2024) — and
        // SolidWorks has no call that clears the flag, so Run(keepClean) notes the clean documents first (CleanDocs) and
        // drops the changes of the ones the tool marked (Unmodify): what is on disk is what was in memory, nothing is lost.
        private HashSet<string> CleanDocs() =>
            new HashSet<string>(OpenDocs().Where(d => !SaveFlag(d) && !string.IsNullOrEmpty(d.GetPathName())).Select(d => d.GetTitle()),
                                StringComparer.OrdinalIgnoreCase);

        private IEnumerable<IModelDoc2> OpenDocs() => ((_solidWorks.GetDocuments() as object[]) ?? new object[0]).OfType<IModelDoc2>();

        private static bool SaveFlag(IModelDoc2 d)
        {
            try { return d.GetSaveFlag(); } catch { return false; }
        }

        // Parts only, each looked up afresh by path. A part loaded as an assembly component is opened as its own document
        // and closed again (OpenDoc6 + CloseDoc): SolidWorks drops the in-memory changes and marks nothing else —
        // ReloadOrReplace on it marked the top assembly (parts in subassemblies) and the parts derived from it (a mirror).
        // A part with a window of its own (or the active one) is reloaded, unless another open part is derived from it. Assemblies are never
        // reloaded: that reloads their components under the caller (a stale subassembly pointer crashed SolidWorks).
        // Returns the documents that stay marked, for the answer's warning.
        private List<string> Unmodify(HashSet<string> wereClean)
        {
            Func<IModelDoc2, bool> marked = d => wereClean.Contains(d.GetTitle()) && SaveFlag(d);
            var all = OpenDocs().Where(marked).ToList();
            if (all.Count > 0) ExecLog.Write($"keep-clean: the tool marked {all.Count}: " + string.Join(", ", all.Select(d => d.GetTitle())));
            var targets = all.Where(d => d.GetType() == (int)swDocumentTypes_e.swDocPART).Select(d => d.GetPathName()).ToList();
            HashSet<string> parents = null;
            string active = (_solidWorks.IActiveDoc2 as IModelDoc2)?.GetTitle();
            foreach (var path in targets)
            {
                var d = _solidWorks.GetOpenDocumentByName(path) as IModelDoc2;
                if (d == null || !SaveFlag(d)) continue;
                string title = d.GetTitle();
                try
                {
                    // a component document: no window of its own and not the active one (in the hidden SolidWorks every
                    // document is invisible, Visible cannot tell — the rule close_document uses)
                    if (title != active && d.GetFirstModelView() == null)
                    {
                        int e = 0, w = 0;
                        var own = _solidWorks.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
                        _solidWorks.CloseDoc(own?.GetTitle() ?? title);
                        ExecLog.Write($"keep-clean: '{title}' changes dropped (closed as its own document)");
                        continue;
                    }
                    if ((parents ?? (parents = DerivedFrom())).Contains(path)) { ExecLog.Write($"keep-clean: '{title}' left marked — another open part is derived from it"); continue; }
                    ExecLog.Write($"keep-clean: '{title}' reloaded from disk (ReloadOrReplace={d.ReloadOrReplace(false, path, true)})");
                }
                catch (Exception ex) { ExecLog.Write($"keep-clean: '{title}' -> {ex.Message}"); }
            }
            var left = OpenDocs().Where(marked).Select(d => d.GetTitle()).ToList();
            if (left.Count > 0) ExecLog.Write("keep-clean: still modified: " + string.Join(", ", left));
            return left;
        }

        // Files that open parts with external references (a mirror / derived part) are made from.
        private HashSet<string> DerivedFrom()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in OpenDocs().Where(d => d.GetType() == (int)swDocumentTypes_e.swDocPART))
            {
                try
                {
                    if (d.Extension.ListExternalFileReferencesCount() == 0) continue;
                    var deps = _solidWorks.GetDocumentDependencies2(d.GetPathName(), false, true, false) as string[];
                    for (int i = 1; deps != null && i < deps.Length; i += 2) set.Add(deps[i]);
                }
                catch (Exception ex) { ExecLog.Write($"keep-clean: references of '{d.GetTitle()}' -> {ex.Message}"); }
            }
            return set;
        }

        // A model the host document needs LOADED for one API call (AddComponent5, CreateFlatPatternViewFromModelView3).
        // Opened silently when it is not open yet — the document TYPE follows the extension (a part type for a .SLDASM
        // failed with 2097152); an open document is reused (OpenDoc6 on it fails with 65536) and left open. The host
        // stays active for the call (OpenDoc6 activates what it opens); afterwards a model opened here is closed (the
        // host keeps its own reference) and the host re-activated — CloseDoc leaves the active document unpredictable.
        private T WithModelLoaded<T>(string path, string hostTitle, Func<T> call, string config = "")
        {
            string openedHere = null;
            if (_solidWorks.GetOpenDocumentByName(path) == null)
            {
                int e = 0, w = 0;
                bool asm = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                var m = _solidWorks.OpenDoc6(path, asm ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART,
                                             (int)swOpenDocOptions_e.swOpenDocOptions_Silent, config ?? "", ref e, ref w) as IModelDoc2
                        ?? throw new ToolFail("OPEN_FAILED", $"Could not open '{path}' (error {e}).");
                openedHere = m.GetTitle();
            }
            Activate(hostTitle);
            try { return call(); }
            finally
            {
                if (openedHere != null && openedHere != hostTitle) { try { _solidWorks.CloseDoc(openedHere); } catch { } }
                Activate(hostTitle);
            }
        }

        // The feature the last API call added (for calls that return bool or nothing).
        private static IFeature LastFeature(IModelDoc2 d) => d.Extension.GetLastFeatureAdded() as IFeature;

        // Name of the LAST feature in the tree — a before/after pair detects whether a void-returning API (InsertRib)
        // created anything.
        private static string LastFeatureName(IModelDoc2 d)
        {
            try { return Features(d).LastOrDefault(f => !IsFolderEnd(f))?.Name; }
            catch { return null; }
        }

        // C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (from the running SolidWorks; the default path as a fallback).
        // GetExecutablePath returns the FOLDER of sldworks.exe on SW 2024 (not the exe) — both forms are accepted.
        private string SwInstallDir
        {
            get
            {
                try
                {
                    var p = _solidWorks?.GetExecutablePath();
                    if (!string.IsNullOrEmpty(p))
                    {
                        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p = Path.GetDirectoryName(p);
                        if (Directory.Exists(Path.Combine(p, "lang"))) return p;
                    }
                }
                catch { }
                return @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS";
            }
        }

        // A file or folder under <install>\lang\<UI language> (then english); null when absent.
        private string LangFile(string name)
        {
            string lang = null;
            try { lang = _solidWorks?.GetCurrentLanguage(); } catch { }
            foreach (var l in new[] { lang, "russian", "english" }.Where(x => !string.IsNullOrEmpty(x)).Distinct())
            {
                string p = Path.Combine(SwInstallDir, "lang", l, name);
                if (File.Exists(p) || Directory.Exists(p)) return p;
            }
            return null;
        }

        // The document template: the explicit path, else the user's default for that type (Tools > Options > Default
        // Templates), else the first matching template under %ProgramData%\SolidWorks.
        private string DefaultTemplate(swDocumentTypes_e type, string explicitPath = null)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            var pref = type == swDocumentTypes_e.swDocASSEMBLY ? swUserPreferenceStringValue_e.swDefaultTemplateAssembly
                     : type == swDocumentTypes_e.swDocDRAWING ? swUserPreferenceStringValue_e.swDefaultTemplateDrawing
                     : swUserPreferenceStringValue_e.swDefaultTemplatePart;
            string t = _solidWorks.GetUserPreferenceStringValue((int)pref);
            if (!string.IsNullOrEmpty(t) && File.Exists(t)) return t;
            string ext = type == swDocumentTypes_e.swDocASSEMBLY ? "*.asmdot" : type == swDocumentTypes_e.swDocDRAWING ? "*.drwdot" : "*.prtdot";
            string root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "SolidWorks");
            var found = Directory.Exists(root) ? Directory.GetFiles(root, ext, SearchOption.AllDirectories) : new string[0];
            if (found.Length == 0)
                throw new ToolFail("TEMPLATE_NOT_FOUND", $"No {ext.Substring(1)} template found. Set a default template in SolidWorks → Tools → Options → Default Templates.");
            return found[0];
        }

        // A new blank document from that template (a drawing takes its paper size and sheet width/height in m).
        private IModelDoc2 NewDoc(swDocumentTypes_e type, string explicitTemplate = null, int paper = 0, double width = 0, double height = 0)
        {
            string template = DefaultTemplate(type, explicitTemplate);
            return _solidWorks.NewDocument(template, paper, width, height) as IModelDoc2
                ?? throw new ToolFail("DOCUMENT_CREATION_FAILED", $"SolidWorks made no document from the template '{template}' — check SolidWorks → Tools → Options → Default Templates.");
        }

        private static double[] ToDoubles(object raw) => (raw as Array)?.Cast<object>().Select(Convert.ToDouble).ToArray() ?? new double[0];
        private static int[] ToInts(object raw) => (raw as Array)?.Cast<object>().Select(x => (int)Math.Round(Convert.ToDouble(x))).ToArray() ?? new int[0];

        // The views of the active sheet. The first "view" SolidWorks returns is the sheet itself — skipped unless withSheet.
        private static IEnumerable<IView> DrawingViews(IDrawingDoc dr, bool withSheet = false)
        {
            var v = dr.GetFirstView() as IView;
            if (!withSheet) v = v?.GetNextView() as IView;
            for (int guard = 0; v != null && guard < 2000; guard++, v = v.GetNextView() as IView)
                yield return v;
        }

        // A view by name, or (name null) the first view `otherwise` accepts.
        private static IView FindDrawingView(IDrawingDoc dr, string name, Func<IView, bool> otherwise = null) =>
            name != null
                ? DrawingViews(dr).FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                : DrawingViews(dr).FirstOrDefault(v => otherwise == null || otherwise(v));

        // Does a view's sheet outline (meters) contain the sheet point?
        private static bool OutlineContains(IView v, double x, double y)
        {
            try { var o = v.GetOutline() as double[]; return o != null && o.Length >= 4 && x >= o[0] && x <= o[2] && y >= o[1] && y <= o[3]; }
            catch { return false; }
        }

        private static List<string> DrawingViewNames(IDrawingDoc dr) => DrawingViews(dr).Select(v => v.Name).ToList();

        // swMateType_e — only the vocabulary slice + safe neighbors get canonical names;
        // anything else reports unknown_<n> so a reader sees it is unmapped.
        private static string MateTypeName(int t)
        {
            switch (t)
            {
                case 0: return "coincident";
                case 1: return "concentric";
                case 2: return "perpendicular";
                case 3: return "parallel";
                case 4: return "tangent";
                case 5: return "distance";
                case 6: return "angle";
                case 8: return "symmetric";
                case 11: return "width";
                case 16: return "lock";
                case 20: return "coordinate";
                case 21: return "slot";
                case 22: return "hinge";
                case 24: return "profile_center";
                default: return "unknown_" + t;
            }
        }

        private static int MateTypeFromName(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "coincident": return 0;
                case "concentric": return 1;
                case "perpendicular": return 2;
                case "parallel": return 3;
                case "tangent": return 4;
                case "distance": return 5;
                case "angle": return 6;
                case "lock": return 16;
                default: return -1;
            }
        }

        // swMateAlign_e: 0=aligned, 1=anti_aligned, 2=closest
        private static string MateAlignName(int a)
        {
            switch (a)
            {
                case 0: return "aligned";
                case 1: return "anti_aligned";
                case 2: return "closest";
                default: return "unknown_" + a;
            }
        }

        private static int MateAlignFromName(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "aligned": return 0;
                case "anti_aligned": return 1;
                default: return 2; // closest — let SolidWorks pick the nearer solution
            }
        }

        // swMateType_e → snake_case, the same words analyze_assembly uses for its vocabulary (MateTypeName).
        private static string MateName(int t)
        {
            string known = MateTypeName(t);
            if (!known.StartsWith("unknown_")) return known;
            switch (t)
            {
                case 9: return "cam_follower";
                case 10: return "gear";
                case 12: return "lock_to_tangent";
                case 13: return "rack_pinion";
                case 15: return "path";
                case 17: return "screw";
                case 18: return "linear_coupler";
                case 19: return "universal_joint";
                case 23: return "slider";
                case 25: return "magnetic";
                default: return known;
            }
        }

        // ---------------------------------------------------------------- shared by several tool files
        // components (Comps, FindComp), mate features, features and planes by name, custom properties,
        // file names, document length unit, the sheet-metal / weldment feature result
        private static List<IComponent2> Comps(IAssemblyDoc a, bool topOnly)
        {
            var arr = a.GetComponents(topOnly) as object[];
            return arr == null ? new List<IComponent2>() : arr.OfType<IComponent2>().ToList();
        }

        // Exact Name2, else the one case-insensitive exact name, else a unique match on the last path segment / prefix
        // (case-insensitive) — the same forgiving lookup the earlier Python server used, so 'Bracket-1' finds 'Sub-1/Bracket-1'.
        // Several matches are COMPONENT_AMBIGUOUS (never "not found"). The ONE component lookup of every tool.
        private static IComponent2 FindComp(IAssemblyDoc a, string name, List<IComponent2> all = null)
        {
            all = all ?? Comps(a, false);
            var exact = all.FirstOrDefault(c => c.Name2 == name);
            if (exact != null) return exact;
            var ci = all.Where(c => string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ci.Count == 1) return ci[0];
            var low = name.ToLowerInvariant();
            var m = all.Where(c =>
            {
                var n = c.Name2.ToLowerInvariant();
                return n.Split('/').Last() == low || n.StartsWith(low);
            }).ToList();
            if (m.Count == 1) return m[0];
            if (m.Count == 0) throw new ToolFail("COMPONENT_NOT_FOUND", $"No component '{name}' — list_components gives the exact names (e.g. 'Shaft-1').");
            throw new ToolFail("COMPONENT_AMBIGUOUS",
                $"'{name}' matches {m.Count}: " + string.Join(", ", m.Take(10).Select(c => c.Name2)));
        }

        // Every mate of the assembly in creation order — inside mate folders too.
        private static IEnumerable<IFeature> MateFeatures(IModelDoc2 d)
        {
            foreach (var group in Features(d).Where(f => f.GetTypeName2() == "MateGroup"))
                foreach (var m in SubFeatures(group).ToList())   // materialized: callers may delete mates
                {
                    if (m.GetSpecificFeature2() is IMate2) yield return m;
                    else // a mate folder → its mates
                        foreach (var c in SubFeatures(m))
                            if (c.GetSpecificFeature2() is IMate2) yield return c;
                }
        }

        // Locate a feature by its exact tree name, walking top-level features AND one level of
        // sub-features (folder contents) — same traversal as analyze's tree walk, so a feature
        // analyze reports can always be found here. Collects every visited name into namesOut for a
        // helpful "available features" error list. Returns null if not found.
        private IFeature FindFeatureByName(IModelDoc2 modelDoc, string name, List<string> namesOut)
        {
            IFeature found = null;
            foreach (var feat in Features(modelDoc))
            {
                if (namesOut != null) namesOut.Add(feat.Name);
                if (found == null && feat.Name == name) found = feat;

                foreach (var sub in SubFeatures(feat))
                {
                    if (namesOut != null) namesOut.Add(sub.Name);
                    if (found == null && sub.Name == name) found = sub;
                }

            }
            return found;
        }

        /// <summary>
        /// Language-independent default-plane selection. SolidWorks localizes the default
        /// plane names (e.g. "Piano frontale" on an Italian install), so SelectByID2
        /// by the English name fails on non-English installs. We first try the given name
        /// (English installs keep working), then fall back to mapping Front/Top/Right to the
        /// first three RefPlane features in tree order (the default planes), selecting the
        /// feature object directly — no name involved.
        /// </summary>
        private bool SelectPlaneFlexible(IModelDoc2 modelDoc, string planeName, bool append, int mark)
        {
            // 1) Try by name (works on English installs, and for explicitly-named planes).
            if (modelDoc.Extension.SelectByID2(planeName, "PLANE", 0, 0, 0, append, mark, null, 0))
                return true;

            // 2) A default plane by its position (Front/Top/Right — English or Russian name), on any UI language.
            var plane = DefaultPlane(modelDoc.FirstFeature() as IFeature, planeName);
            if (plane == null) return false;
            if (!append) modelDoc.ClearSelection2(true);
            return plane.Select2(append, mark);
        }

        private static string CpmGet(ICustomPropertyManager cpm, string name)
        {
            try
            {
                string val, resolved; bool wasResolved, link;
                cpm.Get6(name, false, out val, out resolved, out wasResolved, out link);
                return string.IsNullOrEmpty(resolved) ? val : resolved;
            }
            catch { return null; }
        }

        // Custom property: configuration-specific value first, then the file-level one ("" when absent), resolved
        // (ResolveProp).
        private string DocProp(IModelDoc2 d, string config, string key, int depth = 0)
        {
            string v = null;
            try { if (!string.IsNullOrEmpty(config)) v = CpmGet(d.Extension.CustomPropertyManager[config], key); } catch { }
            if (string.IsNullOrEmpty(v)) try { v = CpmGet(d.Extension.CustomPropertyManager[""], key); } catch { }
            return ResolveProp(d, config, v ?? "", key, depth);
        }

        // What Get6 leaves unresolved: "fromparent+" — a mirror / derived part linked to its parent's properties (a mirrored
        // wall panel gave it as its Обозначение and Наименование) — read from the parent part;
        // $PRP:"name" inside a value (a material named «AISI 304, h= $PRP:"толщина"») filled in from the document.
        private string ResolveProp(IModelDoc2 d, string config, string v, string key, int depth)
        {
            if (string.IsNullOrEmpty(v) || depth > 3) return v ?? "";
            if (v.StartsWith("fromparent", StringComparison.OrdinalIgnoreCase))
            {
                var parent = ParentPart(d);
                if (parent == null) return v;
                var cfgs = parent.GetConfigurationNames() as string[] ?? new string[0];
                string pcfg = cfgs.Contains(config) ? config : (parent.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
                return DocProp(parent, pcfg, key, depth + 1);
            }
            if (v.IndexOf("$PRP:", StringComparison.OrdinalIgnoreCase) < 0) return v;
            return System.Text.RegularExpressions.Regex.Replace(v, "\\$PRP:\"([^\"]+)\"",
                m => DocProp(d, config, m.Groups[1].Value, depth + 1), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // The part a mirror / derived part is made from: its first referenced part file on disk; opened read-only when
        // it is not loaded (closed again by CloseParents at the end of the call).
        private readonly Dictionary<string, IModelDoc2> _parents = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _parentsOpened = new List<string>();

        private IModelDoc2 ParentPart(IModelDoc2 d)
        {
            string self = d.GetPathName();
            if (string.IsNullOrEmpty(self)) return null;
            if (_parents.TryGetValue(self, out var known)) return known;
            IModelDoc2 parent = null;
            try
            {
                var deps = _solidWorks.GetDocumentDependencies2(self, false, true, false) as string[];
                for (int i = 1; deps != null && i < deps.Length && parent == null; i += 2)
                {
                    string path = deps[i];
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(path, self, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) continue;
                    parent = _solidWorks.GetOpenDocumentByName(path) as IModelDoc2;
                    if (parent == null)
                    {
                        int e = 0, w = 0;
                        parent = _solidWorks.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref e, ref w) as IModelDoc2;
                        if (parent != null) _parentsOpened.Add(parent.GetTitle());
                    }
                }
            }
            catch (Exception ex) { ExecLog.Write($"properties: parent of '{d.GetTitle()}' -> {ex.Message}"); }
            _parents[self] = parent;
            return parent;
        }

        private void CloseParents()
        {
            foreach (var t in _parentsOpened) try { _solidWorks.CloseDoc(t); } catch { }
            _parentsOpened.Clear();
            _parents.Clear();
        }

        private static string SafeName(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        // Creates the folder a file goes into. A bare file name ("bom.csv") has no folder: GetDirectoryName gives ""
        // and CreateDirectory("") throws, so the full path is taken first.
        private static void EnsureFolderFor(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        // The folder of a saved document; null for one never saved (GetDirectoryName("") throws on .NET Framework).
        private static string DocFolder(IModelDoc2 d)
        {
            string path = d?.GetPathName();
            return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
        }

        private static double LengthUnitToMm(IModelDoc2 d)
        {
            switch (d.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear))
            {
                case (int)swLengthUnit_e.swMM: return 1;
                case (int)swLengthUnit_e.swCM: return 10;
                case (int)swLengthUnit_e.swMETER: return 1000;
                case (int)swLengthUnit_e.swINCHES: return 25.4;
                case (int)swLengthUnit_e.swFEET: return 304.8;
                case (int)swLengthUnit_e.swFEETINCHES: return 25.4;   // values come in inches
                case (int)swLengthUnit_e.swMICRON: return 0.001;
                case (int)swLengthUnit_e.swANGSTROM: return 1e-7;
                case (int)swLengthUnit_e.swNANOMETER: return 1e-6;
                case (int)swLengthUnit_e.swMIL: return 0.0254;
                case (int)swLengthUnit_e.swUIN: return 0.0000254;
                default:   // a unit SolidWorks added later: say so instead of labelling its numbers mm
                    throw new ToolFail("BAD_PARAMETER", $"Unknown document length unit {d.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear)} — set the document to mm (set_document_units).");
            }
        }

        // (library, name, classification) of every material in the SolidWorks material databases (*.sldmat in the folders
        // of Options → File Locations → Material Databases).
        private List<Tuple<string, string, string>> MaterialsCatalog()
        {
            var res = new List<Tuple<string, string, string>>();
            string dirs = "";
            try { dirs = _solidWorks.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases) ?? ""; } catch { }
            // the standard libraries live with the installation, in the UI language: a Russian SolidWorks names
            // "6061 Alloy" «6061 Сплав» (the English name is then not found)
            try { dirs += ";" + Path.Combine(_solidWorks.GetExecutablePath(), "lang", _solidWorks.GetCurrentLanguage(), "sldmaterials"); } catch { }
            foreach (var dir in dirs.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0 && Directory.Exists(x)))
                foreach (var file in Directory.GetFiles(dir, "*.sldmat"))
                {
                    try
                    {
                        string lib = Path.GetFileNameWithoutExtension(file);
                        var xml = System.Xml.Linq.XDocument.Load(file);
                        foreach (var cls in xml.Descendants().Where(e => e.Name.LocalName == "classification"))
                            foreach (var m in cls.Elements().Where(e => e.Name.LocalName == "material"))
                                res.Add(Tuple.Create(lib, (string)m.Attribute("name") ?? "", (string)cls.Attribute("name") ?? ""));
                    }
                    catch { }
                }
            return res;
        }

        // The catalogue names closest to a wanted one ("6061 Alloy" → "6061-T6 (SS)", "6061 Alloy"…): shared words, then
        // a common prefix.
        private List<Tuple<string, string>> NearestMaterials(string wanted, int n)
        {
            var words = Words(wanted);
            return MaterialsCatalog()
                .Select(m => new { m, score = Words(m.Item2).Count(w => words.Any(x => x == w || (x.Length > 2 && w.StartsWith(x)) || (w.Length > 2 && x.StartsWith(w)))) * 10
                                                 + CommonPrefix(m.Item2.ToLowerInvariant(), wanted.ToLowerInvariant()) })
                .Where(x => x.score > 0).OrderByDescending(x => x.score).Take(n)
                .Select(x => Tuple.Create(x.m.Item1, x.m.Item2)).ToList();
        }

        private static List<string> Words(string s) =>
            System.Text.RegularExpressions.Regex.Split((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();

        private static int CommonPrefix(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            return i;
        }

        public ExecutionResponse ListMaterials(ToolRequest request) => Run(request, false, p =>
        {
            string q = PStr(p, "query", "");
            var all = MaterialsCatalog();
            var words = Words(q);
            var hit = all.Where(m => words.All(w => (m.Item2 + " " + m.Item3).ToLowerInvariant().Contains(w))).ToList();
            int limit = PInt(p, "limit", 40);
            return new JObject
            {
                ["total"] = hit.Count,
                ["materials"] = new JArray(hit.Take(limit).Select(m => new JObject { ["name"] = m.Item2, ["library"] = m.Item1, ["class"] = m.Item3 })),
                ["libraries"] = new JArray(all.Select(m => m.Item1).Distinct())
            };
        });

        // Model files under `folder` that none of the `roots` (assemblies / drawings) uses, directly or deep —
        // GetDocumentDependencies2 from disk, nothing opened. A drawing counts as used when its model is used.
        public ExecutionResponse UnreferencedFiles(ToolRequest request) => Run(request, false, p =>
        {
            string folder = PStr(p, "folder") ?? throw new ToolFail("MISSING_PARAMETER", "folder is required (where to look for unused files).");
            if (!Directory.Exists(folder)) throw new ToolFail("FILE_NOT_FOUND", $"No folder '{folder}'.");
            var roots = PStrList(p, "roots");
            if (roots.Count == 0) throw new ToolFail("MISSING_PARAMETER", "roots is required: the top assemblies (full paths) whose files are in use.");
            var excl = PStrList(p, "exclude").Select(x => x.ToLowerInvariant()).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roots)
            {
                if (!File.Exists(r)) throw new ToolFail("FILE_NOT_FOUND", $"No root '{r}'.");
                used.Add(Path.GetFullPath(r));
                var deps = _solidWorks.GetDocumentDependencies2(r, true, true, false) as string[];
                if (deps == null) continue;
                for (int i = 1; i < deps.Length; i += 2) if (!string.IsNullOrEmpty(deps[i])) used.Add(Path.GetFullPath(deps[i]));
            }
            var models = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
                .Where(f => !excl.Any(x => f.ToLowerInvariant().Contains(x))).ToList();
            var unused = models.Where(f => !used.Contains(Path.GetFullPath(f))).OrderBy(f => f).ToList();
            var drawings = Directory.GetFiles(folder, "*.slddrw", SearchOption.AllDirectories)
                .Where(f => !excl.Any(x => f.ToLowerInvariant().Contains(x)))
                .Where(f =>
                {
                    var dd = _solidWorks.GetDocumentDependencies2(f, false, true, false) as string[];
                    if (dd == null || dd.Length < 2) return false;
                    var refs = Enumerable.Range(0, dd.Length / 2).Select(i => dd[2 * i + 1]).Where(x => !string.IsNullOrEmpty(x)).ToList();
                    return refs.Count > 0 && refs.All(x => !used.Contains(Path.GetFullPath(x)));
                }).OrderBy(f => f).ToList();
            string rel(string f) => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? f.Substring(folder.TrimEnd('\\').Length + 1) : f;
            return new JObject
            {
                ["models_in_folder"] = models.Count,
                ["used_by_roots"] = used.Count,
                ["unreferenced_count"] = unused.Count,
                ["unreferenced"] = new JArray(unused.Select(rel)),
                ["drawings_of_unreferenced"] = new JArray(drawings.Select(rel))
            };
        });

        private JObject FeatureResult(IModelDoc2 d, IFeature f, string what)
        {
            d.ClearSelection2(true);
            if (f == null) throw new ToolFail("FEATURE_FAILED", what + " was not created (check the selection / parameters).");
            d.EditRebuild3();
            string warning = CheckCreated(d, f, what);
            return WithWarning(new JObject { ["feature"] = f.Name, ["type"] = f.GetTypeName2() }, warning);
        }
    }
}
