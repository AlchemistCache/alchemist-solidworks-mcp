using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Weldment profiles the SolidWorks library lacks =====================
    // create_weldment_profiles — GOST equal angles (8509-93), parallel-flange channels (8240-97, series П) and seamless
    //   pipes (8732-78) as weldment profile files: one .SLDLFP per size (a closed profile sketch centred on the origin,
    //   Description = "<name> ГОСТ …") in <out_dir>\GOST\<type>\<size>.SLDLFP. Default out_dir =
    //   %USERPROFILE%\SolidWorksMCP\weldment profiles, which weldment_profiles / weldment_member also search.
    //   Section data: reference values of the standards (b, t, R / h, b, s, t, R / D, s) — check before production.
    public partial class SolidWorksService
    {
        internal static string UserProfilesRoot =>
            System.Environment.GetEnvironmentVariable("SWMCP_PROFILES")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "SolidWorksMCP", "weldment profiles");

        private static readonly Dictionary<string, Tuple<string, string, double[][]>> GostProfiles = new Dictionary<string, Tuple<string, string, double[][]>>
        {
            // kind → (type folder, standard, rows)
            ["angle"] = Tuple.Create("Уголок равнополочный ГОСТ 8509-93", "ГОСТ 8509-93", new[]
            {   // b, t, R
                new[] { 20, 3, 3.5 }, new[] { 25, 3, 3.5 }, new[] { 25, 4, 3.5 }, new[] { 32, 3, 4.5 }, new[] { 32, 4, 4.5 },
                new[] { 40, 3, 5.0 }, new[] { 40, 4, 5.0 }, new[] { 45, 4, 5.0 }, new[] { 50, 4, 5.5 }, new[] { 50, 5, 5.5 },
                new[] { 63, 5, 7.0 }, new[] { 63, 6, 7.0 }, new[] { 70, 6, 8.0 }, new[] { 75, 6, 9.0 }, new[] { 80, 6, 9.0 },
                new[] { 90, 7, 10.0 }, new[] { 100, 8, 12.0 },
            }),
            ["channel"] = Tuple.Create("Швеллер ГОСТ 8240-97 (П)", "ГОСТ 8240-97", new[]
            {   // h, b, s, t, R
                new[] { 50, 32, 4.4, 7.0, 6.0 }, new[] { 65, 36, 4.4, 7.2, 6.0 }, new[] { 80, 40, 4.5, 7.4, 6.5 },
                new[] { 100, 46, 4.5, 7.6, 7.0 }, new[] { 120, 52, 4.8, 7.8, 7.5 }, new[] { 140, 58, 4.9, 8.1, 8.0 },
                new[] { 160, 64, 5.0, 8.4, 8.5 }, new[] { 180, 70, 5.1, 8.7, 9.0 }, new[] { 200, 76, 5.2, 9.0, 9.5 },
            }),
            ["pipe"] = Tuple.Create("Труба бесшовная ГОСТ 8732-78", "ГОСТ 8732-78", new[]
            {   // D, s
                new[] { 25, 3.0 }, new[] { 32, 3.5 }, new[] { 38, 4.0 }, new[] { 42, 4.0 }, new[] { 45, 4.0 }, new[] { 57, 4.0 },
                new[] { 76, 5.0 }, new[] { 89, 5.0 }, new[] { 108, 5.0 }, new[] { 133, 6.0 }, new[] { 159, 6.0 },
            }),
        };

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static string ProfileSize(string kind, double[] r) =>
            kind == "angle" ? $"{Fmt(r[0])}x{Fmt(r[0])}x{Fmt(r[1])}" :
            kind == "channel" ? Fmt(r[0] / 10) + "П" : $"{Fmt(r[0])}x{Fmt(r[1])}";

        // Closed profile in the active sketch, centred on the origin (sketch units: metres).
        private static void DrawProfile(ISketchManager sm, string kind, double[] r)
        {
            Func<double, double> m = v => v / 1000.0;
            Action<double, double, double, double> line = (x1, y1, x2, y2) => sm.CreateLine(m(x1), m(y1), 0, m(x2), m(y2), 0);
            Action<double, double, double, double, double, double> arcCw = (cx, cy, x1, y1, x2, y2) => sm.CreateArc(m(cx), m(cy), 0, m(x1), m(y1), 0, m(x2), m(y2), 0, -1);
            if (kind == "pipe")
            {
                sm.CreateCircleByRadius(0, 0, 0, m(r[0] / 2));
                sm.CreateCircleByRadius(0, 0, 0, m(r[0] / 2 - r[1]));
                return;
            }
            if (kind == "angle")
            {
                double b = r[0], t = r[1], R = r[2], o = b / 2;     // heel at (-o,-o)
                line(-o, -o, b - o, -o); line(b - o, -o, b - o, t - o); line(b - o, t - o, t + R - o, t - o);
                arcCw(t + R - o, t + R - o, t + R - o, t - o, t - o, t + R - o);
                line(t - o, t + R - o, t - o, b - o); line(t - o, b - o, -o, b - o); line(-o, b - o, -o, -o);
                return;
            }
            double h = r[0], bb = r[1], s = r[2], tf = r[3], Rr = r[4], ox = bb / 2, oy = h / 2;   // web on the left
            line(-ox, -oy, bb - ox, -oy); line(bb - ox, -oy, bb - ox, tf - oy); line(bb - ox, tf - oy, s + Rr - ox, tf - oy);
            arcCw(s + Rr - ox, tf + Rr - oy, s + Rr - ox, tf - oy, s - ox, tf + Rr - oy);
            line(s - ox, tf + Rr - oy, s - ox, h - tf - Rr - oy);
            arcCw(s + Rr - ox, h - tf - Rr - oy, s - ox, h - tf - Rr - oy, s + Rr - ox, h - tf - oy);
            line(s + Rr - ox, h - tf - oy, bb - ox, h - tf - oy); line(bb - ox, h - tf - oy, bb - ox, h - oy);
            line(bb - ox, h - oy, -ox, h - oy); line(-ox, h - oy, -ox, -oy);
        }

        public ExecutionResponse CreateWeldmentProfiles(ToolRequest request) => Run(request, false, p =>
        {
            string kind = PStr(p, "kind") ?? throw new ToolFail("MISSING_PARAMETER", "kind is required: angle, channel or pipe.");
            if (!GostProfiles.TryGetValue(kind, out var def)) throw new ToolFail("BAD_PARAMETER", "kind must be angle, channel or pipe.");
            var only = new HashSet<string>(PStrList(p, "sizes").Select(SizeKey));
            var known = def.Item3.Select(r => ProfileSize(kind, r)).ToList();
            var unknown = PStrList(p, "sizes").Where(s => !known.Any(k => SizeKey(k) == SizeKey(s))).ToList();
            if (unknown.Count > 0)
                throw new ToolFail("BAD_PARAMETER", $"Not in the {def.Item2} table: {string.Join(", ", unknown)}. Sizes: {string.Join(", ", known)}.");
            string dir = Path.Combine(PStr(p, "out_dir") ?? UserProfilesRoot, "GOST", def.Item1);
            Directory.CreateDirectory(dir);
            string tpl = DefaultTemplate(swDocumentTypes_e.swDocPART);
            var made = new JArray(); var skipped = new JArray();
            foreach (var row in def.Item3)
            {
                string size = ProfileSize(kind, row);
                if (only.Count > 0 && !only.Contains(SizeKey(size))) continue;
                string fp = Path.Combine(dir, size + ".SLDLFP");
                if (File.Exists(fp) && !PBool(p, "overwrite", false)) { skipped.Add(size); continue; }
                var d = NewDoc(swDocumentTypes_e.swDocPART, tpl);
                try
                {
                    d.ClearSelection2(true);
                    if (!SelectPlaneFlexible(d, "Front Plane", false, 0)) throw new ToolFail("REFERENCE_NOT_FOUND", "The new part has no Front plane — check the default part template.");
                    d.SketchManager.InsertSketch(true);
                    using (new AddToDbScope(d.SketchManager)) DrawProfile(d.SketchManager, kind, row);
                    ExitSketch(d);
                    string name = (kind == "angle" ? "Уголок " : kind == "channel" ? "Швеллер " : "Труба ") + size.Replace('x', 'х') + " " + def.Item2;
                    var cpm = d.Extension.CustomPropertyManager[""];
                    cpm.Add3("Description", (int)swCustomInfoType_e.swCustomInfoText, name, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    cpm.Add3("Обозначение", (int)swCustomInfoType_e.swCustomInfoText, name, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    int se = 0, sw2 = 0;
                    if (!d.Extension.SaveAs3(fp, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref se, ref sw2) || !File.Exists(fp))
                        throw new ToolFail("SAVE_FAILED", $"SaveAs3 {fp} failed (error {se}, warning {sw2}).");
                    made.Add(size);
                }
                finally { _solidWorks.CloseDoc(d.GetTitle()); }
            }
            return new JObject { ["kind"] = kind, ["folder"] = dir, ["created"] = made, ["skipped_existing"] = skipped,
                                 ["usage"] = $"weldment_member(profile='GOST/{def.Item1}/<size>')",
                                 ["note"] = "section data = reference values of " + def.Item2 + " — check before production" };
        });
    }
}
