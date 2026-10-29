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
    // ===================== Weldments =====================
    // weldment_profiles  — library profiles: <profiles folder>\<standard>\<type>\<size>.SLDLFP, or one .SLDLFP per type whose
    //                      configurations are the sizes (the GOST folder: «Труба профильная», «Полоса стальная» …)
    // weldment_member    — structural members along sketch segments (groups: angle, corner treatment)  InsertStructuralWeldment5
    // weldment_cut_list  — cut list of the active weldment part: per item qty, description, length, end angles, mass
    // weldment_trim      — trim / extend bodies against bodies or faces                     InsertWeldmentTrimFeature2
    // weldment_end_cap   — end caps on open member ends                                       InsertEndCapFeature2
    // weldment_gusset    — gusset between two planar faces of members                         InsertGussetFeature2
    // Lengths mm, angles degrees.
    public partial class SolidWorksService
    {
        private List<string> ProfileRoots()
        {
            var roots = new List<string>();
            try
            {
                string pref = _solidWorks.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsWeldmentProfiles);
                roots.AddRange((pref ?? "").Split(';').Select(x => x.Trim()).Where(Directory.Exists));
            }
            catch { }
            string def = LangFile("weldment profiles");
            if (def != null && !roots.Contains(def, StringComparer.OrdinalIgnoreCase)) roots.Add(def);
            // profiles made by create_weldment_profiles (GOST angles, channels, pipes)
            if (Directory.Exists(UserProfilesRoot) && !roots.Contains(UserProfilesRoot, StringComparer.OrdinalIgnoreCase)) roots.Add(UserProfilesRoot);
            return roots;
        }

        // sizes are written "40х40х3" (Cyrillic х) and "40 x 40 x 3" alike — compare without spaces, х = x, comma = point
        private static string SizeKey(string s) => (s ?? "").Replace(" ", "").Replace('х', 'x').Replace('Х', 'x').Replace(',', '.').ToLowerInvariant();

        // "40 x 40 x 4" = "40 x 40 x 4.0": the numbers of a size
        private static string SizeNums(string s) => string.Join("x", System.Text.RegularExpressions.Regex.Matches((s ?? "").Replace(',', '.'), @"\d+(\.\d+)?")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)));

        private static string Child(string dir, string name, bool file)
        {
            if (dir == null || !Directory.Exists(dir)) return null;
            var items = file ? Directory.GetFiles(dir, "*.sldlfp") : Directory.GetDirectories(dir);
            Func<string, string> nm = x => file ? Path.GetFileNameWithoutExtension(x) : Path.GetFileName(x);
            return items.FirstOrDefault(x => string.Equals(nm(x), name, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(x => SizeKey(nm(x)) == SizeKey(name))
                ?? (file && SizeNums(name).Length > 0 ? items.FirstOrDefault(x => SizeNums(nm(x)) == SizeNums(name)) : null);
        }

        private string[] ProfileConfigs(string path)
        {
            try { return _solidWorks.GetConfigurationNames(path) as string[] ?? new string[0]; } catch { return new string[0]; }
        }

        // "iso/Tube (square)/40 x 40 x 4" | "GOST/Труба профильная" + size "40x40x3" | a .SLDLFP path (+ size = configuration)
        private Tuple<string, string> ResolveProfile(string profile, string size)
        {
            if (string.IsNullOrEmpty(profile)) throw new ToolFail("MISSING_PARAMETER", "profile is required (see weldment_profiles).");
            if (File.Exists(profile)) return Tuple.Create(profile, size ?? "");
            var parts = profile.Split('/', '\\').Where(x => x.Length > 0).ToArray();
            foreach (var root in ProfileRoots())
            {
                string std = Child(root, parts[0], false);
                if (std == null || parts.Length < 2) continue;
                string typeDir = Child(std, parts[1], false);
                string sz = parts.Length > 2 ? parts[2] : size;
                if (typeDir != null && sz != null)
                {
                    string f = Child(typeDir, sz, true);
                    if (f != null) return Tuple.Create(f, "");
                    throw new ToolFail("PROFILE_NOT_FOUND", $"No size '{sz}' in {typeDir}. Sizes: " + string.Join(", ", Directory.GetFiles(typeDir, "*.sldlfp").Select(Path.GetFileNameWithoutExtension)));
                }
                string lib = Child(std, parts[1], true);
                if (lib != null)
                {
                    var cfgs = ProfileConfigs(lib);
                    if (cfgs.Length > 1 || sz != null)
                    {
                        string c = cfgs.FirstOrDefault(x => string.Equals(x, sz, StringComparison.OrdinalIgnoreCase))
                                   ?? cfgs.FirstOrDefault(x => SizeKey(x) == SizeKey(sz))
                                   ?? cfgs.FirstOrDefault(x => SizeKey(x).StartsWith(SizeKey(sz) + "."))   // "4х20" → "4х20, ГОСТ 103-2006"
                                   ?? cfgs.FirstOrDefault(x => SizeNums(x.Split(',')[0]) == SizeNums(sz));
                        if (c == null) throw new ToolFail("PROFILE_NOT_FOUND", $"'{Path.GetFileName(lib)}' has no configuration '{sz}'. Sizes: " + string.Join(", ", cfgs.Take(80)));
                        return Tuple.Create(lib, c);
                    }
                    return Tuple.Create(lib, cfgs.FirstOrDefault() ?? "");
                }
            }
            throw new ToolFail("PROFILE_NOT_FOUND", $"Profile '{profile}' not found under {string.Join("; ", ProfileRoots())} (weldment_profiles lists them).");
        }

        public ExecutionResponse WeldmentProfiles(ToolRequest request) => Run(request, false, p =>
        {
            string fStd = PStr(p, "standard"), fType = PStr(p, "type");
            var stds = new JArray();
            foreach (var root in ProfileRoots())
                foreach (var stdDir in Directory.GetDirectories(root))
                {
                    string std = Path.GetFileName(stdDir);
                    if (fStd != null && !string.Equals(std, fStd, StringComparison.OrdinalIgnoreCase)) continue;
                    var types = new JArray();
                    foreach (var lib in Directory.GetFiles(stdDir, "*.sldlfp"))
                    {
                        string t = Path.GetFileNameWithoutExtension(lib);
                        if (fType != null && !string.Equals(t, fType, StringComparison.OrdinalIgnoreCase)) continue;
                        types.Add(new JObject { ["type"] = t, ["sizes"] = new JArray(ProfileConfigs(lib)), ["file"] = lib });
                    }
                    foreach (var typeDir in Directory.GetDirectories(stdDir))
                    {
                        string t = Path.GetFileName(typeDir);
                        if (fType != null && !string.Equals(t, fType, StringComparison.OrdinalIgnoreCase)) continue;
                        types.Add(new JObject { ["type"] = t, ["sizes"] = new JArray(Directory.GetFiles(typeDir, "*.sldlfp").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) });
                    }
                    stds.Add(new JObject { ["standard"] = std, ["root"] = root, ["types"] = types });
                }
            return new JObject { ["standards"] = stds, ["usage"] = "profile = '<standard>/<type>/<size>' (size may also go in `size`)" };
        });

        // The Weldment feature makes the part a weldment (cut list folders, weldment properties). Structural members
        // inserted through the API do NOT add it themselves (verified live: no cut list without it).
        private static bool HasWeldmentFeature(IModelDoc2 d)
        {
            foreach (var f in Features(d))
            {
                string t = f.GetTypeName2();
                if (t == "WeldmentFeature" || t == "Weldment") return true;
            }
            return false;
        }

        private static IBody2 BodyByName(IPartDoc part, string name) =>
            SolidBodies(part).FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolFail("BODY_NOT_FOUND", $"No body '{name}'. Bodies: " + string.Join(", ", SolidBodies(part).Select(b => b.Name)));

        private static int CornerType(string s)
        {
            switch ((s ?? "miter").ToLowerInvariant())
            {
                case "none": return (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionNone;
                case "butt1": return (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionButt1;
                case "butt2": return (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionButt2;
                case "trim": return (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionTrim;
                default: return (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionMiter;
            }
        }

        public ExecutionResponse WeldmentMember(ToolRequest request) => Run(request, true, p =>
        {
            var part = ActivePartOrFail(out var d);
            var prof = ResolveProfile(PStr(p, "profile"), PStr(p, "size"));
            ExitSketch(d);
            var specs = PArr(p, "groups");
            if (specs.Count == 0) specs = new JArray(new JObject { ["sketch"] = PStr(p, "sketch"), ["segments"] = PArr(p, "segments"), ["angle_deg"] = PNum(p, "angle_deg", 0), ["corner"] = PStr(p, "corner", "miter") });
            var fm = d.FeatureManager;
            var groups = new List<object>();
            int segTotal = 0;
            foreach (JObject g in specs)
            {
                string skName = (string)g["sketch"] ?? throw new ToolFail("MISSING_PARAMETER", "sketch (name of the layout sketch) is required.");
                var skf = FindFeatureByName(d, skName, null) ?? throw new ToolFail("SKETCH_NOT_FOUND", $"No sketch '{skName}' in the feature tree.");
                var sk = skf.GetSpecificFeature2() as ISketch ?? throw new ToolFail("SKETCH_NOT_FOUND", skName + " is not a sketch.");
                var segs = (sk.GetSketchSegments() as object[] ?? new object[0]).OfType<ISketchSegment>().Where(s => !s.ConstructionGeometry).ToList();
                var idx = (g["segments"] as JArray)?.Select(x => (int)x).ToList() ?? new List<int>();
                if (idx.Count > 0)
                {
                    var bad = idx.Where(i => i < 0 || i >= segs.Count).ToList();
                    if (bad.Count > 0) throw new ToolFail("SEGMENT_NOT_FOUND", $"segments {string.Join(",", bad)} out of range ({segs.Count} non-construction segments in {skName}).");
                    segs = idx.Select(i => segs[i]).ToList();
                }
                var grp = fm.CreateStructuralMemberGroup() as IStructuralMemberGroup;
                // a TYPED array — object[] (plain or DispatchWrapper) leaves the group empty (verified live)
                grp.Segments = segs.Cast<SketchSegment>().ToArray();
                grp.Angle = ((double?)g["angle_deg"] ?? 0) * Math.PI / 180;
                int ct = CornerType((string)g["corner"]);
                grp.ApplyCornerTreatment = ct != (int)swSolidworksWeldmentEndCondOptions_e.swEndConditionNone;
                if (grp.ApplyCornerTreatment) grp.CornerTreatmentType = ct;
                if (g["mirror"] != null && (bool)g["mirror"]) grp.MirrorProfile = true;
                groups.Add(grp);
                segTotal += segs.Count;
            }
            if (!HasWeldmentFeature(d))
            {
                var wf = fm.InsertWeldmentFeature() as IFeature;
                ExecLog.Write("weldment_member: weldment feature -> " + (wf == null ? "null" : wf.Name + ":" + wf.GetTypeName2()));
            }
            int before = SolidBodies(part).Count;
            // TYPED arrays (StructuralMemberGroup[]): with object[] InsertStructuralWeldment4/5 and the
            // CreateDefinition route all return null on SW 2024 (verified live)
            var typed = groups.Cast<StructuralMemberGroup>().ToArray();
            var tries = new List<string>();
            IFeature f = fm.InsertStructuralWeldment5(prof.Item1, (int)swConnectedSegmentsOption_e.swConnectedSegments_SimpleCut, false, typed, prof.Item2) as IFeature;
            tries.Add("v5: " + (f == null ? "null" : "ok"));
            if (f == null && string.IsNullOrEmpty(prof.Item2))
            {
                f = fm.InsertStructuralWeldment4(prof.Item1, (int)swConnectedSegmentsOption_e.swConnectedSegments_SimpleCut, false, typed) as IFeature;
                tries.Add("v4: " + (f == null ? "null" : "ok"));
            }
            ExecLog.Write($"weldment_member: {prof.Item1} [{prof.Item2}] {string.Join("; ", tries)}");
            var res = FeatureResult(d, f, "Structural member");
            res["profile"] = prof.Item1; res["size"] = prof.Item2; res["segments"] = segTotal;
            res["bodies_added"] = SolidBodies(part).Count - before;
            return res;
        });

        public ExecutionResponse WeldmentCutList(ToolRequest request) => Run(request, false, p =>
        {
            var part = ActivePartOrFail(out var d);
            double k = LengthUnitToMm(d);
            // the cut list's mass is in the document's mass unit (swUnitsMassPropMass_e: mg 1, g 2, kg 3, lb 4)
            int massUnit = d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass, 0);
            double kgPerMassUnit = massUnit == 1 ? 1e-6 : massUnit == 2 ? 1e-3 : massUnit == 4 ? 0.45359237 : 1.0;
            var folder = Features(d).FirstOrDefault(f => f.GetTypeName2() == "SolidBodyFolder")
                         ?? throw new ToolFail("NO_CUT_LIST", "No solid-body folder.");
            try { (folder.GetSpecificFeature2() as IBodyFolder)?.UpdateCutList(); } catch { }
            var rows = new List<JObject>();
            foreach (var sf in SubFeatures(folder))
            {
                if (sf.GetTypeName2() == "CutListFolder")
                {
                    var bf = sf.GetSpecificFeature2() as IBodyFolder;
                    var cpm = sf.CustomPropertyManager;
                    var props = new JObject();
                    foreach (var name in cpm?.GetNames() as string[] ?? new string[0]) props[name] = CpmGet(cpm, name);
                    var r = new JObject { ["item"] = sf.Name, ["qty"] = bf?.GetBodyCount() ?? 0 };
                    try { r["kind"] = ((swCutListType_e)bf.GetCutListType()).ToString().Replace("swCutListType_", ""); } catch { }
                    double? len = CutProp(props, new[] { "длина" }, new[] { "length" });
                    if (len.HasValue) r["length_mm"] = Math.Round(len.Value * k, 1);
                    double? a1 = CutProp(props, new[] { "угол1" }, new[] { "angle1" }), a2 = CutProp(props, new[] { "угол2" }, new[] { "angle2" });
                    if (a1.HasValue) r["angle1_deg"] = a1; if (a2.HasValue) r["angle2_deg"] = a2;
                    string desc = props.Properties().FirstOrDefault(x => x.Name.ToLowerInvariant().Contains("описание") || x.Name.ToLowerInvariant() == "description")?.Value?.ToString();
                    if (!string.IsNullOrEmpty(desc)) r["description"] = desc;
                    double? mass = CutProp(props, new[] { "масса" }, new[] { "mass" });
                    if (mass.HasValue) r["mass_kg"] = Math.Round(mass.Value * kgPerMassUnit, 4);
                    string mat = props.Properties().FirstOrDefault(x => x.Name.ToLowerInvariant().Contains("материал") || x.Name.ToLowerInvariant() == "material")?.Value?.ToString();
                    if (!string.IsNullOrEmpty(mat)) r["material"] = mat;
                    if (PBool(p, "properties", false)) r["properties"] = props;
                    rows.Add(r);
                }
            }
            if (rows.Count == 0 && !HasWeldmentFeature(d))
                throw new ToolFail("NOT_A_WELDMENT", "The part has no Weldment feature, so SolidWorks keeps no cut list — create members with weldment_member (it adds the feature).");
            if (rows.Count == 0)
            {
                var types = SubFeatures(folder).Select(x => x.Name + ":" + x.GetTypeName2());
                var top = Features(d).Take(40).Select(tf => tf.GetTypeName2());
                ExecLog.Write($"weldment_cut_list: no CutListFolder under {folder.Name}; subs: {string.Join(", ", types)}; top: {string.Join(",", top)}");
            }
            var totals = rows.Where(r => r["length_mm"] != null).GroupBy(r => (string)r["description"] ?? (string)r["item"])
                .Select(g => new JObject { ["description"] = g.Key, ["pieces"] = g.Sum(r => (int)r["qty"]),
                                           ["total_length_m"] = Math.Round(g.Sum(r => (double)r["length_mm"] * (int)r["qty"]) / 1000, 3) });
            string csv = PStr(p, "csv_path");
            if (csv != null)
            {
                EnsureFolderFor(csv);
                var cols = new[] { "item", "qty", "description", "length_mm", "angle1_deg", "angle2_deg", "material", "mass_kg", "kind" };
                Func<JToken, string> fmt = tk => tk == null ? "" : tk.Type == JTokenType.Float ? ((double)tk).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) : tk.ToString().Replace(";", ",");
                File.WriteAllLines(csv, new[] { string.Join(";", cols) }.Concat(rows.Select(r => string.Join(";", cols.Select(c => fmt(r[c]))))), new System.Text.UTF8Encoding(true));
            }
            return new JObject { ["document"] = d.GetTitle(), ["items"] = rows.Count, ["rows"] = new JArray(rows), ["totals"] = new JArray(totals), ["csv"] = csv };
        }, keepClean: true);   // UpdateCutList marks the part modified — a read leaves it as it was

        public ExecutionResponse WeldmentTrim(ToolRequest request) => Run(request, true, p =>
        {
            var part = ActivePartOrFail(out var d);
            // typed arrays (Body2[] / Face2[]) — like structural members, object[] is not accepted
            var trim = PStrList(p, "bodies").Select(b => (Body2)BodyByName(part, b)).ToArray();
            if (trim.Length == 0) throw new ToolFail("MISSING_PARAMETER", "bodies (members to trim/extend) are required.");
            var byBodies = PStrList(p, "by_bodies").Select(b => (Body2)BodyByName(part, b)).ToArray();
            var byFaces = PArr(p, "by_faces").Select(t => (Face2)FaceAt(d, (int)t)).ToArray();
            if (byBodies.Length == 0 && byFaces.Length == 0) throw new ToolFail("MISSING_PARAMETER", "by_bodies or by_faces (trimming boundary) is required.");
            if (byBodies.Length > 0 && byFaces.Length > 0) throw new ToolFail("BAD_PARAMETER", "by_bodies OR by_faces, not both.");
            object bounds = byBodies.Length > 0 ? (object)byBodies : byFaces;
            int opts = 0;
            if (PBool(p, "allow_extension", true)) opts |= (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_AllowTrimmedExtensionTrim;
            if (PBool(p, "coped", false)) opts |= (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_CopedCut;
            double gap = PNum(p, "gap_mm", 0);
            if (gap > 0) opts |= (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_WeldGap;
            var f = d.FeatureManager.InsertWeldmentTrimFeature2(CornerType(PStr(p, "corner", "trim")), opts, gap / MM, trim, bounds) as IFeature;
            return FeatureResult(d, f, "Trim/extend");
        });

        public ExecutionResponse WeldmentEndCap(ToolRequest request) => Run(request, true, p =>
        {
            var part = ActivePartOrFail(out var d);
            var faces = PArr(p, "face_indices").Select(t => (Face2)FaceAt(d, (int)t)).ToArray();
            if (faces.Length == 0) throw new ToolFail("MISSING_PARAMETER", "face_indices (end faces of members) are required.");
            double off = PNum(p, "offset_mm", 0), ch = PNum(p, "chamfer_mm", 0);
            var f = d.FeatureManager.InsertEndCapFeature2(PNum(p, "thickness_mm", 5) / MM, off > 0, ch > 0, off / MM, 0.5, ch / MM, faces) as IFeature;
            return FeatureResult(d, f, "End cap");
        });

        public ExecutionResponse WeldmentGusset(ToolRequest request) => Run(request, true, p =>
        {
            var part = ActivePartOrFail(out var d);
            var fi = PArr(p, "face_indices");
            if (fi.Count != 2) throw new ToolFail("MISSING_PARAMETER", "face_indices: two planar faces of the members meeting at the corner.");
            var faces = fi.Select(t => (Face2)FaceAt(d, (int)t)).ToArray();
            int loc = PStr(p, "location", "center") == "start" ? 0 : PStr(p, "location", "center") == "end" ? 2 : 1;
            bool polygon = PStr(p, "shape", "triangle") == "polygon";
            var f = d.FeatureManager.InsertGussetFeature2(PNum(p, "thickness_mm", 5) / MM, 1, (short)loc, polygon,
                PNum(p, "d1_mm", 50) / MM, PNum(p, "d2_mm", 50) / MM, PNum(p, "d3_mm", 10) / MM, Math.PI / 4, PNum(p, "d4_mm", 10) / MM,
                false, 0, 0, false, false, true, faces) as IFeature;
            return FeatureResult(d, f, "Gusset");
        });
    }
}
