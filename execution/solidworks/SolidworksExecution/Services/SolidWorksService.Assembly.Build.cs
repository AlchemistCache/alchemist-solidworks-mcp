using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: assembly tools (open/insert/mate/save-body) and the assembly readers (components, mates, entities).
    public partial class SolidWorksService
    {

        // ==================================================================
        // ASSEMBLY SURFACE — readers first, then builders.
        // Signatures reflection-verified 2026-07-09 (scratch/b1_reflection_notes.md).
        // Mate types/alignments come from the ENUMS — display names are localized
        // and never trusted (enum values reflected from swconst 2026-07-09).
        // ==================================================================

        // swMateEntity2ReferenceType_e — the KIND of geometry a mate side references.
        private static string MateEntityKind(int rt)
        {
            switch (rt)
            {
                case 0: return "point";
                case 1: return "line";
                case 2: return "circle";
                case 3: return "plane";
                case 4: return "cylinder";
                case 5: return "sphere";
                case 6: return "set";
                case 7: return "cone";
                case 11: return "ellipse";
                case 12: return "general_curve";
                default: return "unknown_" + rt;
            }
        }

        // Component transform readback — Transform2.ArrayData: [0..8] = 3x3 rotation (row major),
        // [9..11] = translation (meters), [12] = scale. Rounded to 9dp: comfortably beyond the
        // round-trip verification tolerances (pos <= 1µm, rot <= 1e-6) without rounding AT them.
        private static JArray TransformJson(IComponent2 comp)
        {
            try
            {
                var xf = comp.Transform2;
                if (xf == null) return null;
                var d = xf.ArrayData as double[];
                if (d == null && xf.ArrayData is object[] oa)
                    d = Array.ConvertAll(oa, o => (double)o);
                if (d == null || d.Length < 13) return null;
                var arr = new JArray();
                for (int i = 0; i < 13; i++) arr.Add(Math.Round(d[i], 9));
                return arr;
            }
            catch { return null; }
        }

        // Component occurrences in FEATURE-TREE order — GetComponents(true) returns an
        // ARBITRARY order (observed live: second insert listed first), but a rebuild needs
        // them IN TREE ORDER. Component occurrences are features of type
        // "Reference" whose specific feature is the Component2.
        private List<IComponent2> ComponentsInTreeOrder(IModelDoc2 modelDoc, IAssemblyDoc asm)
        {
            var list = new List<IComponent2>();
            var seen = new HashSet<string>();
            foreach (var feat in Features(modelDoc))
            {
                if ((feat.GetTypeName2() ?? "") == "Reference")
                {
                    var comp = feat.GetSpecificFeature2() as IComponent2;
                    if (comp != null && seen.Add(comp.Name2)) list.Add(comp);
                }
            }
            if (list.Count == 0)
            {
                // Fallback — never return nothing just because the tree walk found no Reference
                // features (unexpected): fall back to GetComponents' arbitrary order.
                object[] comps = asm.GetComponents(true) as object[];
                if (comps != null)
                    foreach (var c in comps)
                    {
                        var comp = c as IComponent2;
                        if (comp != null) list.Add(comp);
                    }
            }
            return list;
        }

        // One component occurrence -> its JSON entry (shared by the flat and top-level reads).
        private JObject ComponentJson(IComponent2 comp, int idx)
        {
            var cj = new JObject();
            cj["i"] = idx;
            cj["name"] = comp.Name2;
            try { cj["path"] = comp.GetPathName(); } catch { }
            try
            {
                var cfg = comp.ReferencedConfiguration;
                if (!string.IsNullOrEmpty(cfg)) cj["config"] = cfg;
            }
            catch { }
            cj["fixed"] = comp.IsFixed();
            string state = CompState(comp);
            if (state != "resolved") cj["state"] = state;
            var t = TransformJson(comp);
            if (t != null) cj["transform"] = t;
            return cj;
        }

        // Recursive LEAF collection for components_flat: a WRAPPER/subassembly level is
        // descended into (the user-provided samples wrap everything one level down); leaves
        // report their Transform2, which SolidWorks gives in ROOT-assembly space. The IR stays
        // FLAT — this read is ground-truth acquisition, and the artifact notes the wrapper.
        private void CollectLeafComponents(IComponent2 comp, JArray arr, ref int idx, string parent)
        {
            object[] children = null;
            try { children = comp.GetChildren() as object[]; } catch { }
            if (children != null && children.Length > 0)
            {
                foreach (var ch in children)
                {
                    var c = ch as IComponent2;
                    if (c != null) CollectLeafComponents(c, arr, ref idx, comp.Name2);
                }
                return;
            }
            var cj = ComponentJson(comp, idx++);
            if (parent != null) cj["parent"] = parent;
            arr.Add(cj);
        }

        // Component tree read. Top-level components in TREE order; per instance: name,
        // source path, config, fixed/suppressed flags, FULL Transform2 (the verification ground
        // truth). A component with children is a SUBASSEMBLY — out of scope first round
        // still reported (with child count) so a caller sees the gap.
        private JObject ReadComponents(IModelDoc2 modelDoc, IAssemblyDoc asm)
        {
            var root = new JObject();
            var arr = new JArray();
            int idx = 0;
            var comps = ComponentsInTreeOrder(modelDoc, asm);
            {
                foreach (var comp in comps)
                {
                    if (comp == null) continue;
                    var cj = new JObject();
                    cj["i"] = idx++;
                    cj["name"] = comp.Name2;
                    try { cj["path"] = comp.GetPathName(); } catch { }
                    try
                    {
                        var cfg = comp.ReferencedConfiguration;
                        if (!string.IsNullOrEmpty(cfg)) cj["config"] = cfg;
                    }
                    catch { }
                    cj["fixed"] = comp.IsFixed();
                    string state = CompState(comp);
                    if (state != "resolved") cj["state"] = state;
                    try
                    {
                        int children = comp.IGetChildrenCount();
                        if (children > 0) cj["children"] = children;
                    }
                    catch { }
                    var t = TransformJson(comp);
                    if (t != null) cj["transform"] = t;
                    arr.Add(cj);
                }
            }
            root["component_count"] = arr.Count;
            root["components"] = arr;
            return root;
        }

        // Mate read — the MateGroup's mates (folders included), which IS creation order (a rebuild
        // must replay mates in that order). Type/alignment from the ENUMS (never the localized
        // display name); distance/angle value in SI from the mate's own dimension (verbatim);
        // per entity: owning component (null = the assembly's own datum), geometry KIND, and
        // EntityParams (location + direction + radii, assembly space) as the geometric anchor.
        // Read-only: plain FeatureData reads, no AccessSelections needed.
        private JObject ReadMates(IModelDoc2 modelDoc)
        {
            var root = new JObject();
            var arr = new JArray();
            int idx = 0;
            // MateFeatures = the same walk list_mates uses, INCLUDING mates inside mate folders (missed before)
            foreach (var sub in MateFeatures(modelDoc))
            {
                var mate = (IMate2)sub.GetSpecificFeature2();
                var mj = new JObject();
                mj["i"] = idx++;
                mj["name"] = sub.Name;
                int mtype = -1;
                try { mtype = mate.Type; } catch { }
                mj["type"] = MateTypeName(mtype);
                try { mj["alignment"] = MateAlignName(mate.Alignment); } catch { }
                if (mtype == 5 || mtype == 6) // distance / angle carry a driving dimension
                {
                    try
                    {
                        var dd = mate.get_DisplayDimension2(0);
                        var dim = dd?.GetDimension2(0);
                        if (dim != null) mj["value"] = Math.Round(dim.SystemValue, 9); // SI: m / rad
                    }
                    catch { }
                }
                var ents = new JArray();
                int ec = 0;
                try { ec = mate.GetMateEntityCount(); } catch { }
                for (int i = 0; i < ec; i++)
                {
                    try
                    {
                        var me = mate.MateEntity(i);
                        if (me == null) continue;
                        var ej = new JObject();
                        var rc = me.ReferenceComponent;
                        ej["component"] = rc != null ? rc.Name2 : null;
                        ej["kind"] = MateEntityKind(me.ReferenceType2);
                        var raw = me.EntityParams;
                        double[] prms = raw as double[];
                        if (prms == null && raw is object[] oa)
                            prms = Array.ConvertAll(oa, o => (double)o);
                        if (prms != null)
                        {
                            var pa = new JArray();
                            foreach (var v in prms) pa.Add(Math.Round(v, 9));
                            ej["params"] = pa;
                        }
                        ents.Add(ej);
                    }
                    catch { }
                }
                mj["entities"] = ents;
                arr.Add(mj);
            }
            root["mate_count"] = arr.Count;
            root["mates"] = arr;
            return root;
        }

        // A component's solid-body faces (or edges) in ONE order: the `i` analyze_assembly(faces|edges, component)
        // reports is the index add_mate selects by (index-first selection — coordinate picks miss real geometry).
        private static List<object> ComponentEntities(IComponent2 comp, bool faces)
        {
            var list = new List<object>();
            foreach (var b in comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[] ?? new object[0])
                if (b is IBody2 body)
                    foreach (var it in (faces ? body.GetFaces() : body.GetEdges()) as object[] ?? new object[0])
                        if (it != null) list.Add(it);
            return list;
        }

        private static IComponent2 ComponentOrFail(IAssemblyDoc asm, string name) => FindComp(asm, name);

        // The component's faces/edges with their geometry, plus its transform (to map part <-> assembly space).
        private JObject ReadComponentEntities(IAssemblyDoc asm, string componentName, bool faces)
        {
            var comp = ComponentOrFail(asm, componentName);
            var root = new JObject { ["component"] = comp.Name2 };
            var t = TransformJson(comp);
            if (t != null) root["transform"] = t;
            var arr = new JArray();
            int idx = 0;
            foreach (var it in ComponentEntities(comp, faces))
                arr.Add(faces ? BuildFaceJson((IFace2)it, idx++) : BuildEdgeJson((IEdge)it, idx++));
            root[faces ? "face_count" : "edge_count"] = arr.Count;
            root[faces ? "faces" : "edges"] = arr;
            return root;
        }

        // (component, kind, index) → the selectable entity, from the same enumeration.
        private object GetComponentEntity(IAssemblyDoc asm, string componentName, string kind, int index)
        {
            var comp = ComponentOrFail(asm, componentName);
            if (kind != "face" && kind != "edge")
                throw new ToolFail("BAD_PARAMETER", $"Entity kind '{kind}' is not supported (face|edge); datum planes: use add_mate_advanced.");
            var items = ComponentEntities(comp, kind == "face");
            if (index < 0 || index >= items.Count)
                throw new ToolFail("ENTITY_NOT_FOUND", $"{kind} index {index} not found on '{componentName}' (component has {items.Count} {kind}s). Re-read analyze_assembly({kind}s, component=...) — indices shift when geometry changes.");
            return items[index];
        }

        // analyze_assembly — the assembly READ tool. analysis_type: 'components' (tree + transforms),
        // 'mates' (creation order, enum types, values, entity anchors), 'faces'/'edges'
        // (component-scoped, index-first; requires 'component'). Read-only, no state bump.
        // mass of the whole assembly or of chosen components: the mass_properties tool.
        public ExecutionResponse AnalyzeAssembly(ToolRequest request) => Run(request, false, p =>
        {
            string mode = PStr(p, "analysis_type", "components").ToLowerInvariant();
            var asm = ActiveAsmOrFail(out var d);
            switch (mode)
            {
                case "components": return Payload(Filtered(ReadComponents(d, asm), PStr(p, "component")));
                case "components_flat": return Payload(Filtered(ReadLeafComponents(d, asm), PStr(p, "component")));
                case "mates": return Payload(ReadMates(d));
                case "faces":
                case "edges":
                    string component = PStr(p, "component") ?? throw new ToolFail("MISSING_PARAMETER",
                        $"analysis_type='{mode}' requires 'component' (Name2 from analyze_assembly(components), e.g. 'Shaft-1').");
                    return Payload(ReadComponentEntities(asm, component, mode == "faces"));
                default:
                    throw new ToolFail("BAD_PARAMETER",
                        $"analysis_type '{mode}' is not supported. Supported: components, components_flat, mates, faces, edges.");
            }
        });

        // LEAF occurrences across all nesting levels (tree order, depth-first), transforms in ROOT space — flattens
        // wrapper / sub-assembly levels for ground-truth acquisition (nesting is noted).
        // components / components_flat narrowed by `component`: that component, the ones inside it ('Sub-1' → 'Sub-1/…',
        // also a leaf's `parent`), or every name containing the text.
        private static JObject Filtered(JObject read, string component)
        {
            if (string.IsNullOrEmpty(component)) return read;
            var all = (read["components"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            bool Under(JObject c)
            {
                string n = (string)c["name"] ?? "", par = (string)c["parent"] ?? "";
                return n == component || n.StartsWith(component + "/") || par == component || par.StartsWith(component + "/");
            }
            var hit = all.Where(Under).ToList();
            if (hit.Count == 0) hit = all.Where(c => ((string)c["name"] ?? "").IndexOf(component, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (hit.Count == 0)
                throw new ToolFail("COMPONENT_NOT_FOUND", $"No component is '{component}', inside it, or contains that text — list_components gives the names.");
            var res = new JObject(read);
            res["components"] = new JArray(hit);
            res["component_count"] = hit.Count;
            res["filter"] = component;
            return res;
        }

        private JObject ReadLeafComponents(IModelDoc2 d, IAssemblyDoc asm)
        {
            var arr = new JArray();
            int idx = 0;
            foreach (var comp in ComponentsInTreeOrder(d, asm))
                CollectLeafComponents(comp, arr, ref idx, null);
            return new JObject { ["component_count"] = arr.Count, ["components"] = arr };
        }

        // open_new_assembly — blank ASSEMBLY document (the twin of open_new_part).
        public ExecutionResponse OpenNewAssembly(ToolRequest request) => Run(request, true, p =>
        {
            NewDoc(swDocumentTypes_e.swDocASSEMBLY, PStr(p, "template_path"));
            return new ToolOutput();
        });

        // insert_component — AddComponent5. The component document must be LOADED first
        // (AddComponent5 contract), so we preload it silently, re-activate the assembly, insert,
        // then close the preloaded doc (the assembly holds its own reference). fixed=true fixes
        // the inserted instance; fixed=false explicitly unfixes (SolidWorks auto-fixes a first
        // component in some flows — we make the state deterministic either way).
        public ExecutionResponse InsertComponent(ToolRequest request) => Run(request, true, p =>
        {
            string filePath = PStr(p, "file_path");
            if (string.IsNullOrEmpty(filePath))
                throw new ToolFail("MISSING_PARAMETER", "file_path is required (.sldprt or .sldasm to insert).");
            if (!System.IO.File.Exists(filePath))
                throw new ToolFail("FILE_NOT_FOUND", $"No file at '{filePath}'.");
            // AddComponent5 returns null for forward-slash paths (observed live) — normalize.
            filePath = System.IO.Path.GetFullPath(filePath);
            double x = PNum(p, "x", 0.0);
            double y = PNum(p, "y", 0.0);
            double z = PNum(p, "z", 0.0);
            string config = PStr(p, "config", "");
            bool? fixedFlag = PBoolN(p, "fixed");

            var asm = ActiveAsmOrFail(out var modelDoc);
            string asmTitle = modelDoc.GetTitle();

            // AddComponent5 needs the component document loaded (WithModelLoaded); the assembly stays active.
            var comp = WithModelLoaded(filePath, asmTitle, () =>
            {
                var c = asm.AddComponent5(filePath,
                    0 /* swAddComponentConfigOptions_CurrentSelectedConfig */, "",
                    false, config, x, y, z);
                if (c == null)
                    throw new ToolFail("INSERT_FAILED",
                        $"AddComponent5 returned null for '{filePath}' (config='{config}'). The file must be a loadable part/assembly document; an assembly cannot be inserted into itself.");

                // Optional FULL transform placement (13 doubles: 3x3 rotation row-major, translation
                // xyz, scale — the exact layout analyze_assembly reports). Needed for mate-less
                // imported assemblies (1-3 class) whose fixed components sit at arbitrary rotations
                // x/y/z alone cannot express. JSON-string param.
                var tvals = PArr(p, "transform");
                if (tvals.Count > 0)
                {
                    if (tvals.Count < 13)
                        throw new ToolFail("BAD_PARAMETER", "transform needs 13 numbers: 9 rotation (row-major) + 3 translation + scale.");
                    var arr = new double[16];
                    for (int i = 0; i < 13; i++) arr[i] = (double)tvals[i];
                    arr[13] = arr[14] = arr[15] = 0.0;
                    var mu = _solidWorks.IGetMathUtility();
                    var xf = mu.CreateTransform(arr) as MathTransform;
                    if (xf == null)
                        throw new ToolFail("TRANSFORM_FAILED", "CreateTransform rejected the array.");
                    c.Transform2 = xf;
                }
                else
                {
                    // x/y/z = where the component's ORIGIN goes (AddComponent5 itself centres the bounding
                    // box on the point, which surprised users: a 0..10 plate inserted at z=0 had its origin
                    // at -5). Identity rotation; mates may move it later.
                    var mu = _solidWorks.IGetMathUtility();
                    c.Transform2 = mu.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, x, y, z, 1, 0, 0, 0 }) as MathTransform;
                }

                // Deterministic fixed state when the caller asked for one.
                if (fixedFlag.HasValue && c.IsFixed() != fixedFlag.Value)
                {
                    modelDoc.ClearSelection2(true);
                    if (c.Select4(false, null, false))
                    {
                        if (fixedFlag.Value) asm.FixComponent(); else asm.UnfixComponent();
                    }
                    modelDoc.ClearSelection2(true);
                }
                return c;
            });

            modelDoc.EditRebuild3();

            var features = new List<string>();
            // features[0] = the PLAIN runtime component name (instance-numbered Name2) — the
            // caller's handle, exactly like created feature names elsewhere; mates reference components through it.
            features.Add(comp.Name2);
            features.Add($"fixed={comp.IsFixed()}");
            var tj = TransformJson(comp);
            if (tj != null) features.Add("transform=" + tj.ToString(Newtonsoft.Json.Formatting.None));

            return new ToolOutput { Features = features };
        });

        // add_mate — AddMate5. Entity selection is INDEX-FIRST: each side is
        // (component Name2, kind face|edge, index from analyze_assembly(faces|edges, component)).
        // value: METERS for distance, DEGREES for angle (degrees at tool boundaries).
        // After the mate: rebuild + report BOTH sides' component transforms (the per-mate stop
        // check — compare against the original's readback before the next mate).
        public ExecutionResponse AddMate(ToolRequest request) => Run(request, true, p =>
        {
            string mateType = PStr(p, "mate_type");
            int mateTypeInt = MateTypeFromName(mateType);
            if (mateTypeInt < 0)
                throw new ToolFail("BAD_PARAMETER",
                    $"mate_type '{mateType}' is not supported. Supported: coincident, concentric, perpendicular, parallel, tangent, distance, angle, lock.");
            int alignInt = MateAlignFromName(PStr(p, "alignment"));
            bool flip = PBool(p, "flip", false);
            double value = PNum(p, "value", 0.0);

            string aComp = PStr(p, "a_component");
            string aKind = (PStr(p, "a_kind", "face")).ToLowerInvariant();
            int aIndex = PInt(p, "a_index", -1);
            string bComp = PStr(p, "b_component");
            string bKind = (PStr(p, "b_kind", "face")).ToLowerInvariant();
            int bIndex = PInt(p, "b_index", -1);
            if (string.IsNullOrEmpty(aComp) || string.IsNullOrEmpty(bComp) || aIndex < 0 || bIndex < 0)
                throw new ToolFail("MISSING_PARAMETER",
                    "a_component/a_index and b_component/b_index are required (indices from analyze_assembly(faces|edges, component=...)).");

            var asm = ActiveAsmOrFail(out var modelDoc);

            object entA, entB;
            try { entA = GetComponentEntity(asm, aComp, aKind, aIndex); }
            catch (ToolFail f) { throw new ToolFail(f.Code, "side A: " + f.Message); }
            try { entB = GetComponentEntity(asm, bComp, bKind, bIndex); }
            catch (ToolFail f) { throw new ToolFail(f.Code, "side B: " + f.Message); }

            // the shared selection + AddMate5 path (mate entities carry mark 1); it reads the new mate back from the tree
            var mate = AddMateBySelection(modelDoc, asm, mateTypeInt, new[] { Tuple.Create(entA, 1), Tuple.Create(entB, 1) }, alignInt, flip,
                mateTypeInt == (int)swMateType_e.swMateDISTANCE ? value : 0,
                mateTypeInt == (int)swMateType_e.swMateANGLE ? value * Math.PI / 180.0 : 0);

            var features = new List<string> { $"mate={mate.Name}", $"type={MateTypeName(mateTypeInt)}" };
            var ta = TransformJson(FindComp(asm, aComp));
            var tb = TransformJson(FindComp(asm, bComp));
            if (ta != null) features.Add("a_transform=" + ta.ToString(Newtonsoft.Json.Formatting.None));
            if (tb != null) features.Add("b_transform=" + tb.ToString(Newtonsoft.Json.Formatting.None));

            return new ToolOutput { Features = features };
        });

        // save_body_as_part — extract ONE body of a multibody part into its own SLDPRT
        // (CreateFeatureFromBody3 on a fresh part; geometry stays in the SOURCE coordinates).
        // The missing-parts half of flattened-assembly reconstruction: a structured
        // assembly STEP with no per-part files flattens to a multibody import; each distinct
        // body becomes a referenced part file, instances get transforms via fingerprint match.
        public ExecutionResponse SaveBodyAsPart(ToolRequest request) => Run(request, true, p =>
        {
            int bodyIndex = PInt(p, "body_index", -1);
            string filePath = PStr(p, "file_path");
            if (bodyIndex < 0 || string.IsNullOrEmpty(filePath))
                throw new ToolFail("MISSING_PARAMETER", "body_index (>= 0) and file_path (.sldprt) are required.");

            var partDoc = ActivePartOrFail(out var modelDoc);
            string srcTitle = modelDoc.GetTitle();

            var bodies = SolidBodies(partDoc);
            if (bodyIndex >= bodies.Count)
                throw new ToolFail("BODY_NOT_FOUND", $"body_index {bodyIndex} out of range (document has {bodies.Count} solid bodies).");
            var body = bodies[bodyIndex];
            object copy = body.Copy2(false);
            if (copy == null) copy = body.Copy();
            if (copy == null)
                throw new ToolFail("BODY_COPY_FAILED", "IBody2.Copy2/Copy returned null.");

            var newDoc = NewDoc(swDocumentTypes_e.swDocPART);
            var newPart = newDoc as IPartDoc;
            string newTitle = newDoc.GetTitle();

            bool ok;
            int se = 0, sw = 0;
            try
            {
                if (!(newPart.CreateFeatureFromBody3(copy, false, 0) is IFeature))
                    throw new ToolFail("BODY_FEATURE_FAILED", "CreateFeatureFromBody3 returned null for the copied body.");
                newDoc.EditRebuild3();
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                ok = newDoc.Extension.SaveAs3(filePath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null, null, ref se, ref sw);
            }
            finally
            {
                // the scratch document is closed and the source re-activated on every path
                try { _solidWorks.CloseDoc(newTitle); } catch { }
                Activate(srcTitle);
            }
            if (!ok)
                throw new ToolFail("SAVE_FAILED", $"SaveAs3('{filePath}') failed (errors={se}, warnings={sw}).");

            var features = new List<string>();
            features.Add(System.IO.Path.GetFileName(filePath));
            try { if (!string.IsNullOrEmpty(body.Name)) features.Add("body_name=" + body.Name); } catch { }

            return new ToolOutput { Features = features };
        });
    }
}
