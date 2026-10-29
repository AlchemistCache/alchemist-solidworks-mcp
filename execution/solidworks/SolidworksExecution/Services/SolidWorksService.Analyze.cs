using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: analyze_model (the dispatcher over its modes; mass properties, geometry, bodies, sketch),
    // get_selection, the post-build body summary and equations. Feature readers: Analyze.Features.cs; faces, edges
    // and feature_map: Analyze.Topology.cs.
    public partial class SolidWorksService
    {

        // -----------------------------------------------------------------------
        // analyze_model — one reader per mode; read-only (no state_version bump). The JSON modes return ONE payload
        // string in cadState.features, mass_properties / geometry key=value strings.
        // -----------------------------------------------------------------------
        public ExecutionResponse AnalyzeModel(ToolRequest request) => Run(request, false, p =>
        {
            string mode = PStr(p, "analysis_type", "mass_properties").ToLowerInvariant();
            var d = ActiveDocOrFail();
            string need = $"analysis_type='{mode}'";
            switch (mode)
            {
                case "mass_properties": return new ToolOutput { Features = MassPropertiesKv(d) };
                case "geometry": return new ToolOutput { Features = GeometryKv(PartOrFail(d, need)) };
                case "bodies": return Payload(ReadBodies(PartOrFail(d, need)));
                case "edges": return Payload(ReadEdges(PartOrFail(d, need), Vec3(p, "near"), PInt(p, "k", 0), Vec3(p, "axis")));
                case "faces": return Payload(ReadFaces(PartOrFail(d, need), Vec3(p, "near"), PInt(p, "k", 0), Vec3(p, "axis")));
                case "bbox":
                {
                    var box = ModelBoxMm(d) ?? throw new ToolFail("NO_GEOMETRY", "The document has no solid geometry to measure.");
                    return new JObject { ["bbox_mm"] = new JArray(box.Select(v => Math.Round(v, 3))),
                                         ["size_mm"] = new JArray(Math.Round(box[3] - box[0], 3), Math.Round(box[4] - box[1], 3), Math.Round(box[5] - box[2], 3)) };
                }
                case "features": return Payload(FeatureRange(BuildFeatureTreeAnalysis(d), PStr(p, "from_feature"), PStr(p, "to_feature")));
                case "sketch": return Payload(ReadOneSketch(d, PStr(p, "name")));
                case "feature_map": return Payload(BuildFeatureMap(d, PartOrFail(d, need), PStr(p, "from_feature"), PStr(p, "to_feature")));
                default:
                    throw new ToolFail("BAD_PARAMETER",
                        $"analysis_type '{mode}' is not supported. Supported: mass_properties, geometry, bodies, bbox, edges, faces, features, sketch, feature_map.");
            }
        });

        private static ToolOutput Payload(JObject o) =>
            new ToolOutput { Features = new List<string> { o.ToString(Newtonsoft.Json.Formatting.None) } };

        // IModelDoc2.GetMassProperties2: [0..2] = CG, [3] = volume, [4] = surface area, [5] = mass, [6+] moments
        // (checked on a 50 mm cube: [0.025, 0.025, 0.025, 0.000125, 0.015, …]); null when there is no solid.
        private static double[] MassProps(IModelDoc2 d)
        {
            int status = 0;
            object raw = d.GetMassProperties2(ref status);
            return raw as double[] ?? (raw is object[] oa ? Array.ConvertAll(oa, o => (double)o) : null);
        }

        // Solid bodies of a part and their face / edge / vertex totals.
        private static (int bodies, int faces, int edges, int vertices) SolidCounts(IPartDoc part)
        {
            var bodies = SolidBodies(part);
            return (bodies.Count, bodies.Sum(b => b.GetFaceCount()), bodies.Sum(b => b.GetEdgeCount()), bodies.Sum(b => b.GetVertexCount()));
        }

        private static List<string> MassPropertiesKv(IModelDoc2 d)
        {
            var m = MassProps(d);
            if (m == null || m.Length < 5)
                throw new ToolFail("ANALYSIS_FAILED", "GetMassProperties2 returned no data — the document needs at least one solid body.");
            return new List<string> { $"volume={m[3]:G6}", $"surface_area={m[4]:G6}", $"cx={m[0]:G6}", $"cy={m[1]:G6}", $"cz={m[2]:G6}" };
        }

        private static List<string> GeometryKv(IPartDoc part)
        {
            var c = SolidCounts(part);
            return new List<string> { $"bodies={c.bodies}", $"faces={c.faces}", $"edges={c.edges}", $"vertices={c.vertices}" };
        }

        // Per-BODY fingerprint of a multibody part (e.g. a flattened assembly STEP): volume / area / centroid and the
        // body's FACE INDEX RANGE in the enumeration analyze_model(faces) walks, so a caller can segment the faces per
        // body and match bodies to known parts.
        private static JObject ReadBodies(IPartDoc part)
        {
            var arr = new JArray();
            int faceCursor = 0;
            foreach (var body in SolidBodies(part))
            {
                var bj = new JObject { ["i"] = arr.Count };
                try
                {
                    // imported breps often carry the source product name (Fusion/NX exports) — free body naming
                    var bn = body.Name;
                    if (!string.IsNullOrEmpty(bn)) bj["name"] = bn;
                }
                catch { }
                int fc = body.GetFaceCount();
                bj["face_start"] = faceCursor;
                bj["face_count"] = fc;
                faceCursor += fc;
                bj["edges"] = body.GetEdgeCount();
                bj["vertices"] = body.GetVertexCount();
                try
                {
                    // IBody2.GetMassProperties(density=1): [0..2] centroid, [3] volume, [4] area (checked live against
                    // the document totals)
                    object raw = body.GetMassProperties(1.0);
                    var mp = raw as double[] ?? (raw is object[] oa ? Array.ConvertAll(oa, o => (double)o) : null);
                    if (mp != null && mp.Length >= 5)
                    {
                        bj["centroid"] = new JArray { Math.Round(mp[0], 9), Math.Round(mp[1], 9), Math.Round(mp[2], 9) };
                        bj["volume"] = mp[3];
                        bj["area"] = mp[4];
                    }
                }
                catch { }
                arr.Add(bj);
            }
            return new JObject { ["body_count"] = arr.Count, ["bodies"] = arr };
        }

        // ONE sketch's full geometry (every segment, rounded), its dimensions and plane — on demand, so the common
        // 'features' recipe stays small and coordinates are paid for only where they are needed.
        private JObject ReadOneSketch(IModelDoc2 d, string sketchName)
        {
            if (string.IsNullOrEmpty(sketchName))
                throw new ToolFail("MISSING_PARAMETER",
                    "analysis_type='sketch' requires 'name' (sketch feature name, e.g. 'Sketch2'). " +
                    "Call analysis_type='features' first to list sketch names.");
            var allNames = new List<string>();
            var feat = FindFeatureByName(d, sketchName, allNames)
                       ?? throw new ToolFail("FEATURE_NOT_FOUND", $"No feature named '{sketchName}'. Available: {string.Join(", ", allNames)}");
            var sketch = feat.GetSpecificFeature2() as ISketch
                         ?? throw new ToolFail("BAD_PARAMETER",
                             $"Feature '{sketchName}' is not a sketch (type '{feat.GetTypeName2()}'). Pass a sketch/ProfileFeature name.");
            var o = new JObject
            {
                ["name"] = feat.Name,
                ["sketch"] = ReadSketch(sketch, true),
                ["dimensions"] = ReadSketchDimensions(feat, sketch)
            };
            var pl = ReadSketchPlane(sketch);
            if (pl != null) o["plane"] = pl;
            return o;
        }

        // get_selection — report what the USER currently has selected in the SolidWorks GUI, mapped to the
        // SAME stable indices analyze_model(faces/edges) reports. The INVERSE of index-based selection
        // instead of the AI telling SW "select face i", the human picks in the GUI and the AI
        // reads back which face/edge index it was — so a user can point at geometry mid-conversation and the
        // AI acts on it (face → create_sketch(on_face, face_index); edge → add_edge_feature(edge_indices)).
        // Read-only: does NOT clear the selection (would wipe the user's pick) and does NOT bump
        // state_version (same pattern as AnalyzeModel/VerifyState).
        public ExecutionResponse GetSelection(ToolRequest request) => Run(request, false, p =>
        {
            var modelDoc = ActiveDocOrFail();
            var selMgr = modelDoc.SelectionManager as ISelectionMgr;
            int count = selMgr?.GetSelectedObjectCount2(-1) ?? 0;

            // Enumerate part faces/edges LAZILY — only when a face/edge is actually in the selection — so
            // a vertex/plane/empty selection costs no body-walk. Same enumeration analyze_model uses, so
            // the matched index is the one create_sketch(face_index)/add_edge_feature(edge_indices) expect.
            var partDoc = modelDoc as IPartDoc;
            List<IFace2> allFaces = null;
            List<IEdge> allEdges = null;

            var selArr = new JArray();
            for (int s = 1; s <= count; s++)
            {
                // GetSelectedObjectType3 → swSelectType_e (inlined ints): 1=EDGES, 2=FACES,
                // 3=VERTICES, 4=DATUMPLANES.
                int t = selMgr.GetSelectedObjectType3(s, -1);
                object obj = selMgr.GetSelectedObject6(s, -1);
                JObject so;
                if (t == 2 && obj is IFace2 selFace)
                {
                    if (allFaces == null) allFaces = FlattenFaces(partDoc);
                    int i = IndexByJson(allFaces, BuildFaceJson(selFace, -1), BuildFaceJson);
                    so = BuildFaceJson(selFace, i);
                    so["type"] = "face";
                }
                else if (t == 1 && obj is IEdge selEdge)
                {
                    if (allEdges == null) allEdges = FlattenEdges(partDoc);
                    int i = IndexByJson(allEdges, BuildEdgeJson(selEdge, -1), BuildEdgeJson);
                    so = BuildEdgeJson(selEdge, i);
                    so["type"] = "edge";
                }
                else if (t == 3 && obj is IVertex selVert)
                {
                    so = new JObject { ["type"] = "vertex" };
                    var pt = selVert.GetPoint() as double[];
                    if (pt != null && pt.Length >= 3)
                        so["point"] = new JArray { R6(pt[0]), R6(pt[1]), R6(pt[2]) };
                }
                else if (t == 4)
                {
                    so = new JObject { ["type"] = "plane", ["name"] = (obj as IFeature)?.Name };
                }
                else
                {
                    so = new JObject { ["type"] = "other", ["sw_type"] = t };
                }
                selArr.Add(so);
            }

            var root = new JObject();
            root["selected_count"] = count;
            root["selection"] = selArr;
            // read-only, same shape as analyze_model: the payload is the single cadState.features entry
            return new ToolOutput { Features = new List<string> { root.ToString(Newtonsoft.Json.Formatting.None) },
                                    ActiveSketch = (modelDoc.SketchManager.ActiveSketch as IFeature)?.Name };
        });


        // Match a selected entity to its index in the enumerated list by GEOMETRY: build each candidate's
        // JSON (idx -1, so the `i` field doesn't interfere) and DeepEquals it against the selected entity's
        // JSON. Reuses the SAME BuildFaceJson/BuildEdgeJson readers, so the match key is exactly the reported
        // geometry. (We can't use IEntity.IsSameAs — not exposed by this interop version — and RCW reference
        // equality is unreliable across separate COM calls.) Collisions need two geometrically identical
        // entities, which a valid solid doesn't have. -1 if no match.
        private static int IndexByJson<T>(List<T> items, JObject target, Func<T, int, JObject> build)
        {
            for (int i = 0; i < items.Count; i++)
                if (JToken.DeepEquals(build(items[i], -1), target)) return i;
            return -1;
        }

        // -----------------------------------------------------------------------
        // analyze_model "features" helpers — read-only feature-tree extraction.
        // -----------------------------------------------------------------------

        // Tree folders / lights carry no design intent; skip them so the output is just
        // the meaningful feature tree (sketches, solid features, patterns, planes, equations).
        private static readonly HashSet<string> NoiseFeatureTypes = new HashSet<string>
        {
            "HistoryFolder", "SensorFolder", "CommentsFolder", "FavoriteFolder", "DetailCabinet",
            "MaterialFolder", "EnvironmentFolder", "DocsFolder", "BlocksFolder", "SunFolder",
            "AmbientLight", "DirectionLight", "PointLight", "SpotLight",
            // UI / annotation / body-container folders that carry no design intent — they leaked into
            // the feature list (8 of the gear's 25 "features" were these). Equations have their own
            // section (ReadEquations), so the EqnFolder entry is redundant too.
            "SelectionSetFolder", "NotesAreaFtrFolder", "SurfaceBodyFolder", "SolidBodyFolder",
            "EnvFolder", "InkMarkupFolder", "EqnFolder",
            // Sheet-metal cut-list folder ("Sheet<1>" under Solid Bodies) — a UI container, no design
            // intent (it also broke feature_map's baseline: rolling before it no-ops, 4-1 live).
            "CutListFolder"
        };

        // Small post-build body summary for in-band verification: volume + face/edge
        // counts, so a caller can confirm an extrude/cut/pattern step from the response itself instead of
        // a separate analyze_model + manual volume arithmetic. Read-only; not part of state/idempotency.
        private JObject BuildBodySummary(IModelDoc2 modelDoc)
        {
            try
            {
                var s = new JObject();
                var mp = MassProps(modelDoc);
                if (mp != null && mp.Length >= 4)
                {
                    s["volume"] = double.Parse(mp[3].ToString("G6"), System.Globalization.CultureInfo.InvariantCulture);
                    s["volume_mm3"] = Math.Round(mp[3] * 1e9, 3);
                }
                if (modelDoc is IPartDoc part)
                {
                    var c = SolidCounts(part);
                    s["faces"] = c.faces;
                    s["edges"] = c.edges;
                }
                var box = ModelBoxMm(modelDoc);
                if (box != null) s["bbox_mm"] = new JArray(box.Select(v => Math.Round(v, 2)));
                return s;
            }
            catch { return null; }
        }

        // The model's box in mm [x0, y0, z0, x1, y1, z1]: a part's solid bodies, an assembly's components; null when there is
        // nothing solid. Not GetPartBox: it took in reference planes and sketches (a plane at x=200 made a 100 mm part 200).
        private static double[] ModelBoxMm(IModelDoc2 d)
        {
            try
            {
                double[] b = d is IPartDoc part ? SolidsBox(part)
                           : d is IAssemblyDoc asm ? asm.GetBox(0) as double[] : null;
                if (b == null || b.Length < 6 || b.All(v => Math.Abs(v) < 1e-12)) return null;
                return new[] { Math.Min(b[0], b[3]), Math.Min(b[1], b[4]), Math.Min(b[2], b[5]), Math.Max(b[0], b[3]), Math.Max(b[1], b[4]), Math.Max(b[2], b[5]) }
                    .Select(v => v * MM).ToArray();
            }
            catch { return null; }
        }

        private static double[] SolidsBox(IPartDoc part)
        {
            double[] u = null;
            foreach (var body in (part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? new object[0]).OfType<IBody2>())
            {
                var b = body.GetBodyBox() as double[];
                if (b == null || b.Length < 6) continue;
                u = u == null ? b.ToArray() : new[] { Math.Min(u[0], b[0]), Math.Min(u[1], b[1]), Math.Min(u[2], b[2]), Math.Max(u[3], b[3]), Math.Max(u[4], b[4]), Math.Max(u[5], b[5]) };
            }
            return u;
        }

        // Volume of a part's solid bodies (m³) — readable with a sketch open, where the document's mass properties are not.
        private static double SolidsVolume(IModelDoc2 d)
        {
            double v = 0;
            if (!(d is IPartDoc part)) return 0;
            foreach (var body in (part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? new object[0]).OfType<IBody2>())
                try { var mp = body.GetMassProperties(1.0) as double[]; if (mp != null && mp.Length > 3) v += mp[3]; } catch { }
            return v;
        }

        // Which way the model grew between two body summaries: "+Z 40 mm" — the extrude's real direction, readable
        // without a picture (the same flags put a boss on opposite sides in two parts).
        private static JArray GrewToward(JObject before, JObject after)
        {
            var b = (before?["bbox_mm"] as JArray)?.Select(t => (double)t).ToArray();
            var a = (after?["bbox_mm"] as JArray)?.Select(t => (double)t).ToArray();
            if (a == null || b == null) return null;
            var res = new JArray();
            string[] ax = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                if (a[i + 3] > b[i + 3] + 0.01) res.Add($"+{ax[i]} {Math.Round(a[i + 3] - b[i + 3], 2)} mm (to {Math.Round(a[i + 3], 2)})");
                if (a[i] < b[i] - 0.01) res.Add($"-{ax[i]} {Math.Round(b[i] - a[i], 2)} mm (to {Math.Round(a[i], 2)})");
            }
            return res.Count > 0 ? res : null;
        }

        // Round a coordinate/length (meters) to 6 decimals = 1µm. Below SW's selection tolerance,
        // so pick-points stay valid, while the edges JSON stays small enough for the MCP result cap.
        private static double R6(double v) { return Math.Round(v, 6); }

        private JArray ReadEquations(IModelDoc2 modelDoc)
        {
            var arr = new JArray();
            try
            {
                var em = modelDoc.GetEquationMgr();
                if (em != null)
                {
                    int n = em.GetCount();
                    for (int i = 0; i < n; i++)
                    {
                        var ej = new JObject();
                        try { ej["equation"] = em.get_Equation(i); } catch { }
                        try { ej["value"] = em.get_Value(i); } catch { }
                        try { ej["global"] = em.get_GlobalVariable(i); } catch { }
                        arr.Add(ej);
                    }
                }
            }
            catch { }
            return arr;
        }
    }
}
