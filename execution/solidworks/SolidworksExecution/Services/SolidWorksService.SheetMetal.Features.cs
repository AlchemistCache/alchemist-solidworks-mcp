using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Sheet metal: cut list report + more features =====================
    // sheet_metal_cutlist     — production table for the active part/assembly: per unique part+config: qty, material,
    //                           thickness, flat blank L×W, net area (= volume / t), cutting length, bends, mass; totals
    //                           by thickness+material; CSV for the laser/press-brake shop.
    // sheet_metal_hem          — hem on edges (closed / open / tear drop / rolled)          InsertSheetMetalHem2
    // sheet_metal_miter_flange — miter flange from the ACTIVE profile sketch along edges   InsertSheetMetalMiterFlange
    // sheet_metal_cross_break  — cross break on a face                                     InsertCrossBreak
    // convert_to_sheet_metal   — solid → sheet metal (fixed face + bend edges)              InsertConvertToSheetMetal2
    // sheet_metal_gusset       — stamped gusset across a bend (faces + edge + point selected) InsertSheetMetalGussetFeature3
    // Corner fillet/chamfer: use add_edge_feature on the thickness edges (works on sheet metal; the break-corner
    // definition is not creatable in SW 2024). Jog / closed corner have no creation API in SW 2024 (only their
    // *FeatureData for editing) — a jog = two sketched bends (sheet_metal_feature 'sketched_bend').  Lengths in mm, angles in degrees.
    public partial class SolidWorksService
    {
        // ---------------------------------------------------------------- cut list
        // Cut-list property value by keywords (SW localizes the names: RU and EN both handled).
        private static double? CutProp(JObject props, params string[][] keySets)
        {
            if (props == null) return null;
            foreach (var kv in props)
            {
                string k = kv.Key.ToLowerInvariant().Trim();
                // a one-word set means an EXACT name ("вырезы"), otherwise all words must occur
                if (!keySets.Any(set => set.Length == 1 ? k == set[0] : set.All(w => k.Contains(w)))) continue;
                var s = ((string)kv.Value ?? "").Trim().Replace(',', '.');
                var num = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.' || ch == '-' || ch == 'E' || ch == 'e').ToArray());
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            }
            return null;
        }

        private JObject CutRow(IModelDoc2 d, int qty)
        {
            var info = SheetMetalInfoOf(d);
            if (info["sheet_metal"]?.Value<bool>() != true) return null;
            double k = LengthUnitToMm(d);
            var props = (info["cut_list"] as JArray)?.FirstOrDefault()?["properties"] as JObject;
            double t = (double?)info["thickness_mm"] ?? 0;
            double vol = 0, mass = 0;
            try
            {
                var mp2 = d.Extension.CreateMassProperty2() as IMassProperty2;
                vol = mp2.Volume; mass = mp2.Mass;     // SI
            }
            catch (Exception ex)
            {
                ExecLog.Write("cutlist: CreateMassProperty2 -> " + ex.Message);
                var mp = d.Extension.CreateMassProperty() as IMassProperty; mp.UseSystemUnits = true;
                vol = mp.Volume; mass = mp.Mass;
            }
            var row = new JObject
            {
                ["part"] = Path.GetFileNameWithoutExtension(d.GetPathName()),
                ["config"] = info["config"],
                ["qty"] = qty,
                ["material"] = info["material"],
                ["thickness_mm"] = t,
                ["bend_radius_mm"] = info["bend_radius_mm"],
                ["k_factor"] = info["k_factor"],
            };
            double? L = CutProp(props, new[] { "длина граничной рамки" }, new[] { "bounding box length" });
            double? W = CutProp(props, new[] { "ширина граничной рамки" }, new[] { "bounding box width" });
            if (L.HasValue) row["blank_length_mm"] = Math.Round(L.Value * k, 1);
            if (W.HasValue) row["blank_width_mm"] = Math.Round(W.Value * k, 1);
            if (t > 0) row["net_area_m2"] = Math.Round(vol / (t / MM), 4);
            double? co = CutProp(props, new[] { "периметр", "внешн" }, new[] { "длина резки", "внешн" }, new[] { "cutting length-outer" });
            double? ci = CutProp(props, new[] { "периметр", "внутрен" }, new[] { "длина резки", "внутр" }, new[] { "cutting length-inner" });
            if (co.HasValue || ci.HasValue) row["cut_length_mm"] = Math.Round(((co ?? 0) + (ci ?? 0)) * k, 1);
            double? cuts = CutProp(props, new[] { "вырезы" }, new[] { "cut outs" });
            if (cuts.HasValue) row["cut_outs"] = (int)cuts.Value;
            double? bends = CutProp(props, new[] { "сгибы" }, new[] { "bends" });
            row["bends"] = bends.HasValue ? (int)bends.Value : info["bends"];
            row["mass_kg"] = Math.Round(mass, 4);
            foreach (var key in new[] { "Обозначение", "Наименование" })
            {
                var v = DocProp(d, (string)info["config"] ?? "", key);
                if (!string.IsNullOrEmpty(v)) row[key] = v;
            }
            row["path"] = d.GetPathName();
            return row;
        }

        public ExecutionResponse SheetMetalCutlist(ToolRequest request) => Run(request, false, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            var rows = new List<JObject>();
            if (doc.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                var r = CutRow(doc, PInt(p, "qty", 1)) ?? throw new ToolFail("NOT_SHEET_METAL", $"'{doc.GetTitle()}' is not a sheet-metal part (sheet_metal_feature base_flange, or convert_to_sheet_metal).");
                rows.Add(r);
            }
            else
            {
                var a = (IAssemblyDoc)doc;
                int mult = PInt(p, "assembly_qty", 1);
                foreach (var t in SheetMetalPartsOf(a))
                {
                    var r = WithPartDocFast(t.Item1, t.Item2, d => CutRow(d, t.Item3 * mult));
                    if (r != null) rows.Add(r);
                }
            }
            var totals = rows.GroupBy(r => ((double?)r["thickness_mm"] ?? 0).ToString("0.###", CultureInfo.InvariantCulture) + " | " + ((string)r["material"] ?? "?"))
                .OrderBy(g => g.Key).Select(g => new JObject
                {
                    ["thickness_material"] = g.Key,
                    ["positions"] = g.Count(),
                    ["qty"] = g.Sum(r => (int)r["qty"]),
                    ["net_area_m2"] = Math.Round(g.Sum(r => ((double?)r["net_area_m2"] ?? 0) * (int)r["qty"]), 3),
                    ["blank_area_m2"] = Math.Round(g.Sum(r => ((double?)r["blank_length_mm"] ?? 0) * ((double?)r["blank_width_mm"] ?? 0) / 1e6 * (int)r["qty"]), 3),
                    ["cut_length_m"] = Math.Round(g.Sum(r => ((double?)r["cut_length_mm"] ?? 0) * (int)r["qty"]) / 1000, 2),
                    ["bends"] = g.Sum(r => ((int?)r["bends"] ?? 0) * (int)r["qty"]),
                    ["mass_kg"] = Math.Round(g.Sum(r => ((double?)r["mass_kg"] ?? 0) * (int)r["qty"]), 3),
                });
            string csv = PStr(p, "csv_path");
            if (csv != null)
            {
                EnsureFolderFor(csv);
                var cols = new[] { "part", "config", "Обозначение", "Наименование", "qty", "material", "thickness_mm", "blank_length_mm", "blank_width_mm",
                                   "net_area_m2", "cut_length_mm", "cut_outs", "bends", "bend_radius_mm", "k_factor", "mass_kg", "path" };
                var lines = new List<string> { string.Join(";", cols) };
                Func<JToken, string> fmt = tk => tk == null ? "" : tk.Type == JTokenType.Float
                    ? ((double)tk).ToString("0.####", CultureInfo.GetCultureInfo("ru-RU")) : tk.ToString().Replace(";", ",");
                lines.AddRange(rows.Select(r => string.Join(";", cols.Select(c => fmt(r[c])))));
                File.WriteAllLines(csv, lines, new System.Text.UTF8Encoding(true));
            }
            int max = PInt(p, "max_rows", 100);
            return new JObject { ["document"] = doc.GetTitle(), ["positions"] = rows.Count, ["csv"] = csv, ["totals"] = new JArray(totals), ["truncated"] = rows.Count > max,
                                 ["rows"] = new JArray(rows.Take(max).Select(r => { var c = (JObject)r.DeepClone(); c.Remove("path"); return c; })) };
        }, keepClean: true);

        // ---------------------------------------------------------------- features
        private IModelDoc2 SheetMetalPartOrFail()
        {
            var d = ActiveDocOrFail();
            if (!IsSheetMetalPart(d)) throw new ToolFail("NOT_SHEET_METAL", $"'{d.GetTitle()}' is not a sheet-metal part (activate the part).");
            return d;
        }

        // Select edges/faces by analyze_model index (append, mark).
        private List<object> SelectByIndices(IModelDoc2 d, JArray idx, bool faces, int mark, bool clear = true)
        {
            if (clear) d.ClearSelection2(true);
            var list = new List<object>();
            if (idx.Count == 0) return list;
            var all = faces ? AllPartFaces(d) : FlattenEdges(d as IPartDoc).Cast<object>().ToList();
            foreach (var t in idx)
            {
                int i = (int)t;
                string err = i < 0 || i >= all.Count ? $"index {i} out of range ({all.Count}); see analyze_model({(faces ? "faces" : "edges")})."
                           : !SelectEntity(d, all[i], true, mark) ? $"{(faces ? "face" : "edge")} {i} could not be selected" : null;
                if (err != null)
                {
                    d.ClearSelection2(true);   // no half-made selection left behind
                    throw new ToolFail(err.Contains("out of range") ? (faces ? "FACE_NOT_FOUND" : "EDGE_NOT_FOUND") : "SELECTION_FAILED", err);
                }
                list.Add(all[i]);
            }
            return list;
        }

        private List<object> AllPartFaces(IModelDoc2 d) => FlattenFaces(d as IPartDoc).Cast<object>().ToList();

        // (index of the largest planar face, distance to the nearest planar face facing it) — a flat plate's sheet.
        private static Tuple<int, double> AutoPlate(IPartDoc part)
        {
            var faces = FlattenFaces(part);
            var planes = new List<Tuple<int, double[], double[], double>>();   // index, normal (outward), point, area
            for (int i = 0; i < faces.Count; i++)
            {
                try
                {
                    var s = faces[i].GetSurface() as ISurface;
                    if (s == null || !s.IsPlane()) continue;
                    var pp = s.PlaneParams as double[];
                    double sign = faces[i].FaceInSurfaceSense() ? 1 : -1;
                    planes.Add(Tuple.Create(i, new[] { pp[0] * sign, pp[1] * sign, pp[2] * sign }, new[] { pp[3], pp[4], pp[5] }, faces[i].GetArea()));
                }
                catch { }
            }
            if (planes.Count < 2) return null;
            var big = planes.OrderByDescending(x => x.Item4).First();
            double best = double.MaxValue;
            foreach (var q in planes)
            {
                double dot = big.Item2[0] * q.Item2[0] + big.Item2[1] * q.Item2[1] + big.Item2[2] * q.Item2[2];
                if (dot > -0.999) continue;
                double dist = Math.Abs((q.Item3[0] - big.Item3[0]) * big.Item2[0] + (q.Item3[1] - big.Item3[1]) * big.Item2[1] + (q.Item3[2] - big.Item3[2]) * big.Item2[2]);
                if (dist > 1e-6 && dist < best) best = dist;
            }
            return best < double.MaxValue ? Tuple.Create(big.Item1, best) : null;
        }

        public ExecutionResponse SheetMetalHem(ToolRequest request) => Run(request, true, p =>
        {
            var d = SheetMetalPartOrFail();
            SelectByIndices(d, PArr(p, "edge_indices"), false, 0);
            int type;
            switch (PStr(p, "hem_type", "closed"))
            {
                case "closed": type = (int)swHemTypes_e.swHemTypeClosed; break;
                case "open": type = (int)swHemTypes_e.swHemTypeOpen; break;
                case "tear_drop": type = (int)swHemTypes_e.swHemTypeTearDrop; break;
                case "rolled": type = (int)swHemTypes_e.swHemTypeRolled; break;
                case "double": type = (int)swHemTypes_e.swHemTypeDouble; break;
                default: throw new ToolFail("BAD_PARAMETER", "hem_type must be closed, open, tear_drop or rolled.");
            }
            int pos = PStr(p, "position", "inside") == "outside" ? (int)swHemPositionTypes_e.swHemPositionTypeOutside : (int)swHemPositionTypes_e.swHemPositionTypeInside;
            var f = d.FeatureManager.InsertSheetMetalHem2(type, pos, PBool(p, "reverse", false),
                PNum(p, "length_mm", 10) / MM, PNum(p, "gap_mm", 0.5) / MM, PNum(p, "angle_deg", 270) * Math.PI / 180,
                PNum(p, "radius_mm", 1) / MM, PNum(p, "miter_gap_mm", 0.5) / MM, null, true, 0, 0, true, 0.5, 0, 0) as IFeature;
            return FeatureResult(d, f, "Hem");
        });

        public ExecutionResponse SheetMetalMiterFlange(ToolRequest request) => Run(request, true, p =>
        {
            var d = SheetMetalPartOrFail();
            // Profile = the given sketch (or the last sketch), drawn on a plane normal to the first edge at its end.
            string sk = PStr(p, "sketch");
            sk = sk ?? (d.SketchManager.ActiveSketch as IFeature)?.Name;
            ExitSketch(d);
            var skf = (sk != null ? FindFeatureByName(d, sk, null) : null) ?? throw new ToolFail("MISSING_PARAMETER", "sketch (the miter profile, an open line/arc chain) is required.");
            d.ClearSelection2(true);
            skf.Select2(false, 4);
            SelectByIndices(d, PArr(p, "edge_indices"), false, 1, false);
            int pos = FlangePositionCode(PStr(p, "position", "material_inside"), "position",
                                         "material_inside", "material_outside", "bend_outside");
            double r = PNum(p, "radius_mm", 0);
            var f = d.FeatureManager.InsertSheetMetalMiterFlange(r <= 0, r / MM, PNum(p, "gap_mm", 0.25) / MM, true, true, 0.5, 0, 0,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, PBool(p, "trim_side_bends", false), pos,
                PNum(p, "start_offset_mm", 0) / MM, PNum(p, "end_offset_mm", 0) / MM, null) as IFeature;
            return FeatureResult(d, f, "Miter flange");
        });

        public ExecutionResponse SheetMetalCrossBreak(ToolRequest request) => Run(request, true, p =>
        {
            var d = SheetMetalPartOrFail();
            SelectByIndices(d, PArr(p, "face_indices"), true, 0);
            var f = d.FeatureManager.InsertCrossBreak(PNum(p, "angle_deg", 2) * Math.PI / 180, PNum(p, "radius_mm", 1) / MM) as IFeature;
            return FeatureResult(d, f, "Cross break");
        });

        public ExecutionResponse ConvertToSheetMetal(ToolRequest request) => Run(request, true, p =>
        {
            ActivePartOrFail(out var d);
            // auto: a FLAT plate modelled as a solid → sheet metal without bends: the largest planar face is the fixed one
            // and the thickness is its distance to the facing parallel face (50+ parts by hand-picked face indices before)
            int fixedFace = PInt(p, "fixed_face_index", -1);
            double thickness = PNum(p, "thickness_mm", 0) / MM;
            var auto = new JObject();
            if (fixedFace < 0 || thickness <= 0)
            {
                if (!PBool(p, "auto", false) && fixedFace < 0)
                    throw new ToolFail("MISSING_PARAMETER", "fixed_face_index is required — or auto=True for a flat plate (largest planar face, thickness measured).");
                var plate = AutoPlate(d as IPartDoc) ?? throw new ToolFail("NOT_A_PLATE", "auto found no pair of parallel facing planar faces — give fixed_face_index and thickness_mm.");
                if (fixedFace < 0) { fixedFace = plate.Item1; auto["fixed_face_index"] = fixedFace; }
                if (thickness <= 0) { thickness = plate.Item2; auto["thickness_mm"] = Math.Round(thickness * MM, 3); }
            }
            double volBefore = SolidsVolume(d);
            bool reverse = PBool(p, "reverse_thickness", false);
            IFeature converted = null;
            for (int attempt = 0; attempt < 2 && converted == null; attempt++)
            {
                // Fixed face = mark 0, bend edges = mark 1 (the SolidWorks "Convert to Sheet Metal" selection marks).
                SelectByIndices(d, new JArray(fixedFace), true, 0);
                SelectByIndices(d, PArr(p, "bend_edge_indices"), false, 1, false);
                bool ok = d.FeatureManager.InsertConvertToSheetMetal2(thickness, reverse,
                    PArr(p, "bend_edge_indices").Count == 0, PNum(p, "bend_radius_mm", 2) / MM, PNum(p, "gap_mm", 0.2) / MM,
                    (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 0.5, 0, 0.5, PBool(p, "keep_body", false));
                // GetLastFeatureAdded names the last bend INSIDE the conversion; the feature itself is the SolidToSheetMetal one
                converted = ok ? Features(d).LastOrDefault(f => f.GetTypeName2() == "SolidToSheetMetal") ?? LastFeature(d) : null;
                // auto on a plate: the sheet must BE the plate — thickened outward it doubles the volume; then the other side
                if (converted != null && auto.Count > 0 && volBefore > 0 && Math.Abs(SolidsVolume(d) - volBefore) > 0.02 * volBefore && attempt == 0)
                {
                    d.ClearSelection2(true);
                    converted.Select2(false, 0);
                    d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
                    converted = null;
                    reverse = !reverse;
                }
            }
            var res = FeatureResult(d, converted, "Convert to sheet metal");
            if (auto.Count > 0) { res["auto"] = auto; res["reverse_thickness"] = reverse; }
            return res;
        });
        // ---------------------------------------------------------------- gusset (stiffening rib pressed across a bend)
        // The two flat faces on both sides of a bend, a linear edge along the bend (position line) and a point on it;
        // offset_mm from that point (default: the middle of the edge).
        public ExecutionResponse SheetMetalGusset(ToolRequest request) => Run(request, true, p =>
        {
            var d = SheetMetalPartOrFail();
            var fi = PArr(p, "face_indices");
            if (fi.Count != 2) throw new ToolFail("MISSING_PARAMETER", "face_indices: the two flat faces on both sides of the bend (analyze_model(faces)).");
            var faces = SelectByIndices(d, fi, true, 0);
            d.ClearSelection2(true);
            IEdge line = null;
            int ei = PInt(p, "edge_index", -1);
            if (ei >= 0)
            {
                var edges = FlattenEdges(d as IPartDoc);
                if (ei >= edges.Count) throw new ToolFail("EDGE_NOT_FOUND", $"edge_index {ei} out of range ({edges.Count}).");
                line = edges[ei] as IEdge;
            }
            else
            {
                // the tangent edge where face 1 meets the bend's cylindrical face — runs along the bend axis
                foreach (IEdge e in (faces[0] as IFace2).GetEdges() as object[] ?? new object[0])
                {
                    var c = e.GetCurve() as ICurve;
                    if (c == null || !c.IsLine()) continue;
                    var adj = (e.GetTwoAdjacentFaces2() as object[] ?? new object[0]).OfType<IFace2>();
                    if (adj.Any(f => (f.GetSurface() as ISurface)?.IsCylinder() == true)) { line = e; break; }
                }
                if (line == null) throw new ToolFail("BEND_NOT_FOUND", "No straight bend edge on face 1 — pass edge_index (a linear edge along the bend).");
            }
            var sv = line.GetStartVertex() as IVertex;
            var cp = line.GetCurveParams3() as ICurveParamData;
            double len = 0;
            try { len = (line.GetCurve() as ICurve).GetLength3(cp.UMinValue, cp.UMaxValue); } catch { }
            double offset = p["offset_mm"] != null && p["offset_mm"].Type != JTokenType.Null ? PNum(p, "offset_mm", 0) / MM : len / 2;
            double t = (ThicknessMm(d) ?? 1) / MM;
            bool dims = p["length_mm"] != null && p["length_mm"].Type != JTokenType.Null;
            int type = PStr(p, "gusset_type", "rounded") == "flat" ? (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Flat : (int)swSheetMetalRibGussetType_e.swSheetMetalRibGussetType_Rounded;
            double inner = PNum(p, "inner_fillet_mm", 1) / MM, outer = PNum(p, "outer_fillet_mm", 1) / MM, edgeF = PNum(p, "edge_fillet_mm", 0) / MM;
            // SW 2024: InsertSheetMetalGussetFeature2 (explicit arrays) and CreateDefinition(swFmSMGusset) both return null
            // (verified live); the working route is faces + line + point pre-selected (mark 0) and the v3 call.
            d.ClearSelection2(true);
            if (!faces.All(fc => SelectEntity(d, fc, true)) || !SelectEntity(d, line, true) || (sv != null && !SelectEntity(d, sv, true)))
                throw new ToolFail("SELECTION_FAILED", "Could not select the gusset faces / bend edge.");
            double draft = PNum(p, "draft_deg", 0);
            var feat = d.FeatureManager.InsertSheetMetalGussetFeature3(sv != null, offset, PBool(p, "flip_offset", false),
                dims ? (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_ProfileDimensions : (int)swSheetMetalGussetProfileDimType_e.swSheetMetalGussetProfileDimType_IndentDepth,
                PNum(p, "indent_depth_mm", 5) / MM, PNum(p, "length_mm", 10) / MM, false, PNum(p, "height_mm", 10) / MM, Math.PI / 4,
                PBool(p, "flip_sides", false), PNum(p, "width_mm", 6) / MM, PNum(p, "thickness_mm", t * MM) / MM,
                draft > 0, draft * Math.PI / 180, inner > 0, inner, outer > 0, outer, type, edgeF > 0, edgeF,
                false, false, false) as IFeature;
            var res = FeatureResult(d, feat, "Sheet-metal gusset");
            res["offset_mm"] = Math.Round(offset * MM, 2);
            return res;
        });
    }
}
