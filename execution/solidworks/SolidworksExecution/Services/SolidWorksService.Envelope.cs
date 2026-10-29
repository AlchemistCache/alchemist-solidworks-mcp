using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== The one handler envelope =====================
    // Every tool handler is   public ExecutionResponse X(ToolRequest request) => Run(request, mutating, p => { … });
    // Run does idempotency (a replayed operation id answers DUPLICATE), the state_version check and bump, the COM
    // attach, the active-document restore for read-only tools and the error mapping: a handler throws
    // ToolFail(code, message) and returns a payload (→ result_geometry) or a ToolOutput (+ cadState fields).
    // Params: PStr / PNum / PInt / PBool / PArr / PStrList / PNums. Documents: ActiveDocOrFail / ActivePartOrFail /
    // ActiveAsmOrFail / ActiveDrawingOrFail / ActivePartOrAsmOrFail, and PartOrFail / AsmOrFail / DrawingOrFail for a
    // document already in hand (one mode of a tool, a component's model) — no hand-written GetType checks.
    // Error codes — one vocabulary: MISSING_PARAMETER (a required param is absent), BAD_PARAMETER (a value out of range
    // or an unknown option), NO_ACTIVE_DOCUMENT, NOT_A_PART / NOT_AN_ASSEMBLY / NOT_A_DRAWING / NOT_SHEET_METAL /
    // NOT_A_WELDMENT (the one document kind a tool needs; WRONG_DOCUMENT_TYPE when several kinds are allowed),
    // <THING>_NOT_FOUND / <THING>_AMBIGUOUS for a lookup, <ACTION>_FAILED when SolidWorks refused, FEATURE_ERROR when it
    // built a feature with an error (CheckCreated). A state the tool needs: NO_<THING> (NO_MESH, NO_RESULTS),
    // NOT_<STATE> (NOT_SAVED, NOT_STATIC), SKETCH_NOT_ACTIVE / SKETCH_ALREADY_ACTIVE, <THING>_EXISTS, UNSAVED_CHANGES,
    // ZERO_RESULTS, SIMULATION_UNAVAILABLE; FORBIDDEN (a macro outside the macros folder). COM_ERROR /
    // UNEXPECTED_ERROR / INVALID_STATE_VERSION / COM_ATTACH_FAILED come from Run itself, EXPIRED (the client stopped
    // waiting while the request queued behind a long call; not executed) from ToolController. A message is a sentence for the
    // model — what is wrong and what to do — never just a name.
    public partial class SolidWorksService
    {
        private const double MM = 1000.0;

        private sealed class ToolFail : Exception
        {
            public readonly string Code;
            public ToolFail(string code, string message) : base(message) { Code = code; }
        }

        // What a handler can return besides a plain payload: the CadState fields the modelling tools report
        // (cadState.features / activeSketch). A plain object = result_geometry only.
        private sealed class ToolOutput
        {
            public object Geometry;                 // → result_geometry
            public List<string> Features;           // → cadState.features
            public string ActiveSketch;             // → cadState.activeSketch
            public bool NoDocument;                 // report no active document (e.g. after close_document)
            public List<string> Dimensions;         // → cadState.dimensions (a multi-format export_document puts partial failures here)
        }

        // Shared envelope: idempotency + state_version + COM attach + error mapping. `body` returns the
        // payload (any JSON-serializable object, or a ToolOutput) delivered in result_geometry. mutating=true bumps
        // state_version and registers the op (DUPLICATE on replay); read-only tools leave the version alone.
        // checkState=false: lifecycle tools (activate_document) that are not state-versioned CAD operations.
        // restoreActive=false: a read-only tool whose JOB is to change the active document.
        // keepClean=true: a reading tool whose SolidWorks calls mark documents modified — the changes of those that were
        // clean are dropped afterwards (CleanDocs / Unmodify); what cannot be dropped is named in the answer's warning.
        // treeDiff: false skips reading the tree before and after (created / removed) — for a call that only moves
        // components: on a 64-component arm the two reads were 3 of a pose's 6 s
        private ExecutionResponse Run(ToolRequest request, bool mutating, Func<JObject, object> body,
                                      bool checkState = true, bool restoreActive = true, bool keepClean = false,
                                      bool treeDiff = true)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);
            if (checkState && !_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");
            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks is not running (or not attached) — call ensure_ready, it starts / attaches it.");
            var sw = Stopwatch.StartNew();
            string startTitle = null, before = null;
            List<string> beforeItems = null;
            // An out-of-process caller pays a UI round trip on EVERY COM call unless SolidWorks knows a command is in
            // progress — on a 400-component assembly that was ~12 ms a call (a tree read 30-70 s). One flag per tool.
            bool inCommand = false;
            try { _solidWorks.CommandInProgress = true; inCommand = true; } catch { }
            var pressed = new List<string>();   // what the watch answered — reported on success and on failure
            try
            {
                startTitle = (_solidWorks.IActiveDoc2 as IModelDoc2)?.GetTitle();
                string sketchBefore = mutating ? ((_solidWorks.IActiveDoc2 as IModelDoc2)?.SketchManager?.ActiveSketch as IFeature)?.Name : null;
                if (mutating && treeDiff) { before = ModelFingerprint(); beforeItems = ModelItems(); }
                var p = ParamsOf(request);
                var clean = keepClean ? CleanDocs() : null;
                object result;
                List<string> marked = null;
                using (var boxes = new MessageBoxWatch(this))
                {
                    try { result = body(p); }
                    finally { if (clean != null) marked = Unmodify(clean); pressed = boxes.Said; }
                }
                if (pressed.Count > 0 && ((result as ToolOutput)?.Geometry ?? result) is JObject pg)
                    pg["messages_closed"] = new JArray(pressed);
                if (marked?.Count > 0)
                {
                    var g = ((result as ToolOutput)?.Geometry ?? result) as JObject;
                    string note = $"SolidWorks marked {string.Join(", ", marked)} modified while reading — nothing was changed " +
                                  "in them, close without saving.";
                    if (g != null) g["warning"] = g["warning"] == null ? note : g["warning"] + " " + note;
                }
                if (mutating) result = WithSketchStatus(result, sketchBefore);
                if (!mutating && restoreActive) RestoreActive(startTitle);
                var output = result as ToolOutput;
                var doc = output != null && output.NoDocument ? null : _solidWorks.IActiveDoc2 as IModelDoc2;
                int sv = _guard.GetCurrentStateVersion() + (mutating ? 1 : 0);
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = sv,
                    CadState = new CadState
                    {
                        StateVersion = sv,
                        ActiveDocument = doc?.GetTitle(),
                        DocumentType = doc == null ? null : DocTypeName(doc),
                        ActiveSketch = output?.ActiveSketch,
                        Features = output?.Features ?? new List<string>(),
                        Dimensions = output?.Dimensions ?? new List<string>()
                    },
                    ResultGeometry = output != null ? output.Geometry : result
                };
                if (mutating)
                {
                    TreeDiff(response, startTitle, beforeItems);
                    _guard.RegisterCompleted(request.OperationId, response);
                }
                ExecLog.Write($"{request.Tool}: ok in {sw.ElapsedMilliseconds} ms");
                return response;
            }
            catch (Exception ex)
            {
                if (!mutating && restoreActive) RestoreActive(startTitle);   // on the error path too
                // a failure that a dialog left open in the hidden SolidWorks causes (or will cause next): close it, say it
                if (ex is COMException || (ex is ToolFail tf && BlockingCodes.Contains(tf.Code)))
                {
                    var said = DismissDialogs();
                    if (said.Count > 0)
                    {
                        string note = $" — SolidWorks had a dialog open, now closed: «{string.Join("» «", said)}». Repeat the call.";
                        ex = ex is ToolFail tf2 ? new ToolFail(tf2.Code, tf2.Message + note) : new ToolFail("COM_ERROR", ex.Message + note);
                    }
                }
                // the call failed after the watch answered a dialog (the template picker → Cancel): the reason, said
                if (pressed.Count > 0 && (ex is ToolFail || ex is COMException))
                {
                    string asked = $" — SolidWorks asked «{string.Join("» «", pressed)}», answered by the server (nobody can in the hidden instance).";
                    ex = ex is ToolFail tf3 ? new ToolFail(tf3.Code, tf3.Message + asked) : new ToolFail("COM_ERROR", ex.Message + asked);
                }
                if (ex is ToolFail f) return Failed(request, mutating, before, f.Code, f.Message, startTitle, beforeItems);
                if (ex is COMException) return Failed(request, mutating, before, "COM_ERROR", ex.Message, startTitle, beforeItems);
                ExecLog.Write($"{request.Tool}: {ex}");
                return Failed(request, mutating, before, "UNEXPECTED_ERROR", ex.GetType().Name + ": " + ex.Message,
                              startTitle, beforeItems);
            }
            finally
            {
                if (inCommand) { try { _solidWorks.CommandInProgress = false; } catch { } }
                CloseParents();   // parents of mirror parts opened to read their properties
            }
        }

        private static readonly HashSet<string> BlockingCodes = new HashSet<string>
            { "ACTIVATE_FAILED", "DOCUMENT_CREATION_FAILED", "OPEN_FAILED", "MACRO_FAILED", "DOCUMENT_NOT_FOUND" };

        // A read-only tool must leave the user's active document as it found it (it may have opened or activated others).
        private void RestoreActive(string startTitle)
        {
            try
            {
                if (startTitle == null || (_solidWorks.IActiveDoc2 as IModelDoc2)?.GetTitle() == startTitle) return;
                Activate(startTitle);
            }
            catch (Exception ex) { ExecLog.Write($"RestoreActive '{startTitle}': {ex.Message}"); }   // best effort, logged
        }

        // A mutating tool that fails AFTER it already changed the model must not look like "nothing happened": the
        // state version advances (the adapter resyncs) and the message says so. Selections it left are cleared.
        private ExecutionResponse Failed(ToolRequest request, bool mutating, string before, string code, string message,
                                         string startTitle = null, List<string> beforeItems = null)
        {
            if (mutating)
            {
                try { (_solidWorks.IActiveDoc2 as IModelDoc2)?.ClearSelection2(true); } catch { }
                string after = ModelFingerprint();
                if (before != null && after != before)
                {
                    var changed = BuildFailed(request.OperationId, _guard.GetCurrentStateVersion() + 1, code,
                        message + $" — NOTE: the model WAS changed before the error ({before} → {after}); check it (verify_state / analyze_model).");
                    TreeDiff(changed, startTitle, beforeItems);
                    _guard.RegisterCompleted(request.OperationId, changed);
                    ExecLog.Write($"{request.Tool}: failed after changing the model ({before} -> {after})");
                    return changed;
                }
            }
            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), code, message);
        }

        // The active document's tree as names — features (folder end markers left out), "<name> (mate)",
        // "<name> (component)": what a mutating call created / removed is the difference of two reads. model_snapshot
        // answers the same list.
        private List<string> ModelItems()
        {
            var items = new List<string>();
            var t = Stopwatch.StartNew();
            try
            {
                var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (d == null) return items;
                var comps = new HashSet<string>();
                if (d is IAssemblyDoc a && d.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                    foreach (var c in Comps(a, true)) { comps.Add(c.Name2); items.Add(c.Name2 + " (component)"); }
                // a top-level component is also a tree feature of the same name — listed once, as the component
                items.AddRange(Features(d).Where(f => !IsFolderEnd(f) && !comps.Contains(f.Name)).Select(f => f.Name));
                items.AddRange(MateFeatures(d).Select(m => m.Name + " (mate)"));
            }
            catch (Exception ex) { ExecLog.Write($"ModelItems: {ex.Message}"); }
            if (t.ElapsedMilliseconds > 1000) ExecLog.Write($"ModelItems: {items.Count} items in {t.ElapsedMilliseconds} ms");
            return items;
        }

        // created / removed on the response — only when the call stayed in the document it started in (a new document
        // is reported by its title, not as a tree of "created" items).
        private void TreeDiff(ExecutionResponse r, string startTitle, List<string> before)
        {
            if (before == null || (_solidWorks.IActiveDoc2 as IModelDoc2)?.GetTitle() != startTitle) return;
            var after = ModelItems();
            var b = new HashSet<string>(before);
            var a = new HashSet<string>(after);
            var created = after.Where(x => !b.Contains(x)).Take(30).ToList();
            var removed = before.Where(x => !a.Contains(x)).Take(30).ToList();
            if (created.Count > 0) r.Created = created;
            if (removed.Count > 0) r.Removed = removed;
        }

        // Cheap "did the model change" signature: active document, feature count, modified flag.
        private string ModelFingerprint()
        {
            try
            {
                var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (d == null) return "no document";
                return $"{d.GetTitle()}: {d.FeatureManager?.GetFeatureCount(false) ?? 0} features{(d.GetSaveFlag() ? ", modified" : "")}";
            }
            catch { return "?"; }
        }

        // ---------------------------------------------------------------- params
        private static JObject ParamsOf(ToolRequest r)
        {
            if (r.Params is JObject jo) return jo;
            if (r.Params is string s && !string.IsNullOrWhiteSpace(s)) return JObject.Parse(s);
            return new JObject();
        }

        private static string PStr(JObject p, string key, string def = null)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null) return def;
            var s = t.ToString();
            return string.IsNullOrEmpty(s) ? def : s;
        }

        private static double PNum(JObject p, string key, double def) => PNumN(p, key) ?? def;
        private static int PInt(JObject p, string key, int def) => PIntN(p, key) ?? def;
        private static bool PBool(JObject p, string key, bool def) => PBoolN(p, key) ?? def;
        private static bool? PBoolN(JObject p, string key) => PValue<bool>(p, key, "true or false");
        private static double? PNumN(JObject p, string key) => PValue<double>(p, key, "a number");
        private static int? PIntN(JObject p, string key) => PValue<int>(p, key, "a whole number");

        // A typed parameter; absent / null = not given. A value of the wrong kind ("10 мм" for a number) is BAD_PARAMETER
        // naming the parameter — not a FormatException surfacing as UNEXPECTED_ERROR without a name.
        private static T? PValue<T>(JObject p, string key, string kind) where T : struct
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            try { return t.ToObject<T>(); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException
                                       || ex is Newtonsoft.Json.JsonException || ex is ArgumentException)
            {
                throw new ToolFail("BAD_PARAMETER", $"{key} must be {kind}, got '{t}'.");
            }
        }

        // A list param may arrive as a real JSON array or as a JSON-encoded string ('["a","b"]').
        private static JArray PArr(JObject p, string key)
        {
            var t = p[key];
            if (t == null || t.Type == JTokenType.Null) return new JArray();
            if (t is JArray a) return a;
            var s = t.ToString().Trim();
            if (s.Length == 0) return new JArray();
            if (!s.StartsWith("[")) return new JArray(s);
            try { return JArray.Parse(s); }
            catch (Newtonsoft.Json.JsonException ex) { throw new ToolFail("BAD_PARAMETER", $"{key} must be a JSON array (a list or its JSON text): {ex.Message}"); }
        }

        // Several REQUIRED numbers in order (an entity's coordinates); `missing` is the one MISSING_PARAMETER text.
        private static double[] PNums(JObject p, string missing, params string[] keys)
        {
            var v = keys.Select(k => PNumN(p, k)).ToArray();
            if (v.Any(x => x == null)) throw new ToolFail("MISSING_PARAMETER", missing);
            return v.Select(x => x.Value).ToArray();
        }

        private static List<string> PStrList(JObject p, string key) =>
            PArr(p, key).Select(x => x.ToString()).Where(x => x.Length > 0).ToList();

        // ---------------------------------------------------------------- documents / components
        private static string DocTypeName(IModelDoc2 d)
        {
            if (d == null) return null;
            switch (d.GetType())
            {
                case (int)swDocumentTypes_e.swDocPART: return "PART";
                case (int)swDocumentTypes_e.swDocASSEMBLY: return "ASSEMBLY";
                case (int)swDocumentTypes_e.swDocDRAWING: return "DRAWING";
                default: return "OTHER";
            }
        }

        private IModelDoc2 ActiveDocOrFail()
        {
            var d = _solidWorks.IActiveDoc2 as IModelDoc2;
            if (d == null) throw new ToolFail("NO_ACTIVE_DOCUMENT", "No active document (open_document first).");
            return d;
        }

        private IPartDoc ActivePartOrFail(out IModelDoc2 doc, string need = null) => PartOrFail(doc = ActiveDocOrFail(), need);
        private IAssemblyDoc ActiveAsmOrFail(out IModelDoc2 doc, string need = null) => AsmOrFail(doc = ActiveDocOrFail(), need);
        private IDrawingDoc ActiveDrawingOrFail(out IModelDoc2 doc, string need = null, string hint = null) =>
            DrawingOrFail(doc = ActiveDocOrFail(), need, hint);

        // A tool that works on a part or an assembly: a drawing (or anything else) is WRONG_DOCUMENT_TYPE.
        private IModelDoc2 ActivePartOrAsmOrFail(string need = null)
        {
            var d = ActiveDocOrFail();
            int t = d.GetType();
            if (t != (int)swDocumentTypes_e.swDocPART && t != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new ToolFail("WRONG_DOCUMENT_TYPE", $"{need ?? "This tool"} needs a part or an assembly; the document '{d.GetTitle()}' is {KindOf(d)}.");
            return d;
        }

        // The one document kind a tool — or one mode of it (`need`: "analysis_type='faces'", "components") — needs.
        private static IPartDoc PartOrFail(IModelDoc2 d, string need = null) =>
            d is IPartDoc part && d.GetType() == (int)swDocumentTypes_e.swDocPART ? part
                : throw new ToolFail("NOT_A_PART", WrongKind(d, "a part", need, "open_new_part, or open_document an .SLDPRT"));

        private static IAssemblyDoc AsmOrFail(IModelDoc2 d, string need = null) =>
            d is IAssemblyDoc a && d.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY ? a
                : throw new ToolFail("NOT_AN_ASSEMBLY", WrongKind(d, "an assembly", need, "open_new_assembly, or open_document an .SLDASM"));

        private static IDrawingDoc DrawingOrFail(IModelDoc2 d, string need = null, string hint = null) =>
            d is IDrawingDoc dr && d.GetType() == (int)swDocumentTypes_e.swDocDRAWING ? dr
                : throw new ToolFail("NOT_A_DRAWING", WrongKind(d, "a drawing", need, hint ?? "create_drawing first"));

        private static string WrongKind(IModelDoc2 d, string kind, string need, string hint) =>
            $"{need ?? "This tool"} needs {kind}; the document '{d.GetTitle()}' is {KindOf(d)} ({hint}).";

        private static string KindOf(IModelDoc2 d)
        {
            switch (d.GetType())
            {
                case (int)swDocumentTypes_e.swDocPART: return "a part";
                case (int)swDocumentTypes_e.swDocASSEMBLY: return "an assembly";
                case (int)swDocumentTypes_e.swDocDRAWING: return "a drawing";
                default: return "not a model";
            }
        }

        private ExecutionResponse BuildFailed(string operationId, int stateVersion, string code, string message)
        {
            return new ExecutionResponse
            {
                OperationId = operationId,
                Status = "FAILED",
                Verified = false,
                StateVersion = stateVersion,
                CadState = null,
                Error = new ExecutionError
                {
                    Code = code,
                    Message = message
                }
            };
        }
    }
}
