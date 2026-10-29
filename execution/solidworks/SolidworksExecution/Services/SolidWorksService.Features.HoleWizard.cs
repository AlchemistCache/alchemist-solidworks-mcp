// Ported from Claw256/mcp-server-solidworks 31deceb (AGPL-3.0, © Fearghal Hayes) — verified there on SW 2025;
// re-verified on SolidWorks 2024 SP1 in this fork. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: Hole Wizard (standard counterbore / countersink / straight / tapped holes).
    public partial class SolidWorksService
    {
        // StandardIndex (swWzdHoleStandards_e) by tool name.
        private static int? HoleWizardStandardIndex(string standard)
        {
            switch ((standard ?? "").ToLowerInvariant())
            {
                case "ansi_inch":   return (int)swWzdHoleStandards_e.swStandardAnsiInch;
                case "ansi_metric": return (int)swWzdHoleStandards_e.swStandardAnsiMetric;
                case "iso":         return (int)swWzdHoleStandards_e.swStandardISO;
                case "din":         return (int)swWzdHoleStandards_e.swStandardDIN;
                case "jis":         return (int)swWzdHoleStandards_e.swStandardJIS;
                default:            return null;
            }
        }

        // Default FastenerTypeIndex (swWzdHoleStandardFastenerTypes_e VALUES, from the API docs) per
        // standard + hole type. The FastenerTypeIndex MUST belong to the chosen standard and hole type or
        // SolidWorks rejects the call (HOLE_WIZARD_FAILED) — so a wrong standard/fastener combo fails.
        //   hole        = screw-clearance hole      tap         = tapped hole
        //   counterbore = socket-head-cap-screw cbore   countersink = flat-head (CTSK) countersink
        private static int? HoleWizardDefaultFastener(int standardIndex, string holeType)
        {
            switch (standardIndex)
            {
                case 0: // ANSI inch
                    return holeType == "hole" ? 22 : holeType == "tap" ? 27 : holeType == "counterbore" ? 9 : 13;
                case 1: // ANSI metric
                    return holeType == "hole" ? 40 : holeType == "tap" ? 43 : holeType == "counterbore" ? 33 : 35;
                case 8: // ISO
                    return holeType == "hole" ? 144 : holeType == "tap" ? 147 : holeType == "counterbore" ? 139 : 140;
                case 4: // DIN
                    return holeType == "hole" ? 73 : holeType == "tap" ? 76 : holeType == "counterbore" ? 67 : 68;
                case 9: // JIS
                    return holeType == "hole" ? 161 : holeType == "tap" ? 164 : holeType == "counterbore" ? 156 : 158;
                default:
                    return null;
            }
        }

        // ISO metric coarse pitches (mm) by nominal diameter, for tapped holes given without a pitch.
        private static readonly Dictionary<string, string> HoleWizardCoarsePitch = new Dictionary<string, string>
        {
            { "1.6", "0.35" }, { "2", "0.4" }, { "2.5", "0.45" }, { "3", "0.5" }, { "3.5", "0.6" },
            { "4", "0.7" }, { "5", "0.8" }, { "6", "1" }, { "8", "1.25" }, { "10", "1.5" },
            { "12", "1.75" }, { "14", "2" }, { "16", "2" }, { "20", "2.5" }, { "24", "3" },
            { "30", "3.5" }, { "36", "4" },
        };

        private static readonly Regex HoleWizardMetricSize =
            new Regex(@"^\s*M\s*(\d+(?:\.\d+)?)\s*(?:[xX]\s*(\d+(?:\.\d+)?))?\s*$", RegexOptions.Compiled);

        // SolidWorks matches Hole Wizard size strings exactly, and the format depends on the hole type
        // (verified live on SolidWorks 2025): tapped holes need the pitch with at least one decimal
        // ('M3x0.5', 'M6x1.0', 'M12x1.75'); clearance / counterbore / countersink holes reject a pitch ('M3').
        // Inch sizes ('1/4-20', '#10-24') and anything unrecognised pass through unchanged.
        private static string HoleWizardNormalizeSize(string size, string holeType)
        {
            Match m = HoleWizardMetricSize.Match(size ?? "");
            if (!m.Success) return size;
            string dia = m.Groups[1].Value;
            if (holeType != "tap") return "M" + dia;

            string pitch = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (pitch == null && !HoleWizardCoarsePitch.TryGetValue(dia, out pitch)) return size;
            double pv;
            if (!double.TryParse(pitch, NumberStyles.Float, CultureInfo.InvariantCulture, out pv)) return size;
            return "M" + dia + "x" + pv.ToString("0.0##", CultureInfo.InvariantCulture);
        }

        private static readonly Regex HoleWizardInchTap =
            new Regex(@"^\s*(?:#\s*(\d+)|(\d+)\s*/\s*(\d+)|(\d*\.\d+|\d+))\s*-\s*(\d+)\s*(?:[A-Za-z].*)?$", RegexOptions.Compiled);

        // For a tapped hole HoleWizard5's Diameter is the TAP DRILL diameter, and 0 makes SolidWorks build a
        // giant flagged hole, so derive it from the (normalised) size: metric = major - pitch ('M6x1.0' -> 5.0 mm),
        // inch = major - 1/TPI ('1/4-20' -> 0.2 in, '#10-24' -> 0.148 in). Returns metres, or null if unknown.
        private static double? HoleWizardTapDrillDiameter(string size)
        {
            Match m = HoleWizardMetricSize.Match(size ?? "");
            if (m.Success && m.Groups[2].Success)
            {
                double d, pitch;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out pitch))
                    return (d - pitch) / 1000.0;
                return null;
            }
            m = HoleWizardInchTap.Match(size ?? "");
            if (!m.Success) return null;
            double major;
            if (m.Groups[1].Success) major = 0.060 + 0.013 * int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (m.Groups[2].Success) major = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            else major = double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            double tpi = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            return tpi > 0 ? (major - 1.0 / tpi) * 0.0254 : (double?)null;
        }

        // Places a standard Hole Wizard hole (IFeatureManager.HoleWizard5) at one or more points on a planar face.
        // Selection method (per the HoleWizard5 remarks): "To add a hole at one or more locations, call
        // IModelDocExtension.SelectByRay with Mark = 0 for each location" — so every point is picked as a
        // ray against the target face (ray starts 2 mm above the face along its normal, runs into it); the
        // hit face + point defines the hole centre. face_index / face_x/y/z is used only to read the face
        // normal (the face must be planar) so the ray direction is known.
        public ExecutionResponse HoleWizard(ToolRequest request) => Run(request, true, p =>
        {
            var h = ParseHoleSpec(p);
            var centres = HoleCentres(p);
            ActivePartOrFail(out var modelDoc);
            ExitSketch(modelDoc);   // the selections below are model selections

            // the target face → its unit normal (planar faces only): the direction of the picking ray
            IFace2 face = ResolveFace(modelDoc, p);
            double[] n = face.Normal as double[];
            if (n == null || n.Length < 3 || (n[0] == 0 && n[1] == 0 && n[2] == 0))
                throw new ToolFail("BAD_PARAMETER", "The target face is not planar (Hole Wizard holes need a planar face).");

            // HoleWizard5 places ONE hole per call (at the first picked location, however many are selected — verified
            // live), so every centre gets its own pick and call. All-or-nothing: a failure undoes the holes placed.
            var created = new List<string>();
            ToolFail Undone(ToolFail f, int alsoThisOne = 0)
            {
                if (created.Count + alsoThisOne > 0) modelDoc.EditUndo2(created.Count + alsoThisOne);
                return f;
            }
            for (int i = 0; i < centres.Count; i++)
            {
                var c = centres[i];
                if (!PickOnFace(modelDoc, c, n))
                {
                    modelDoc.ClearSelection2(true);
                    throw Undone(new ToolFail("BAD_PARAMETER", $"points[{i}] ({c[0]}, {c[1]}, {c[2]}) did not hit a face. Each point must lie ON the target planar face." +
                        (created.Count > 0 ? $" The {created.Count} hole(s) already placed were rolled back." : "")));
                }
                var v = h.V;
                var feature = modelDoc.FeatureManager.HoleWizard5(
                    h.GenericType, h.StandardIdx, h.Fastener, h.Size, h.EndType, h.Diameter, h.Depth, h.Length,
                    v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11],
                    "", h.Reverse, true, true, true, true, false) as IFeature
                    ?? throw Undone(new ToolFail("HOLE_WIZARD_FAILED",
                        $"HoleWizard5 returned null at points[{i}] for {h.Sent}. " +
                        "SolidWorks rejects a size string that is not in its list for that fastener (tapped metric sizes need a pitch, e.g. 'M6x1.0'; inch taps look like '1/4-20'), " +
                        "a blind depth deeper than the body, or points that are not on a planar face of a solid body."));

                // HoleWizard5 can hand back a feature SolidWorks flags with an error / warning (yellow triangle) whose
                // geometry is wrong (a tap built with no tap drill cuts a ~50 mm bore). Never COMPLETED — a warning
                // included (stricter than CheckCreated): this hole and the ones before it are undone.
                string flag = FeatureFlag(feature, out _);
                if (flag != null)
                    throw Undone(new ToolFail("FEATURE_ERROR", $"SolidWorks flagged '{feature.Name}' with {flag}, so it was rolled back. " +
                        $"Sent: {h.Sent}. Check the size / diameter / depth against the standard."), 1);
                created.Add(feature.Name);
            }
            modelDoc.ClearSelection2(true);
            return new ToolOutput { Features = created, Geometry = BuildBodySummary(modelDoc) };
        });

        // A Hole Wizard request, validated: the HoleWizard5 arguments, and the words an error uses for what was sent.
        private sealed class HoleSpec
        {
            public string Type, Standard, Size, RequestedSize, End;
            public int GenericType, StandardIdx, Fastener;
            public short EndType;
            public double Diameter, Depth, Length;
            public bool Reverse;
            public readonly double[] V = Enumerable.Repeat(-1.0, 12).ToArray();   // Value1..Value12, -1 = ignored

            public string Sent =>
                $"hole_type '{Type}', standard '{Standard}', fastener_type {Fastener}, size '{Size}'" +
                (Size != RequestedSize ? $" (normalised from '{RequestedSize}')" : "") +
                $", end_condition {End}, depth {Depth}, diameter {Diameter}";
        }

        private static HoleSpec ParseHoleSpec(JObject p)
        {
            var h = new HoleSpec { Type = PStr(p, "hole_type", "").ToLowerInvariant(), Standard = PStr(p, "standard") };
            switch (h.Type)
            {
                case "counterbore": h.GenericType = (int)swWzdGeneralHoleTypes_e.swWzdCounterBore; break;
                case "countersink": h.GenericType = (int)swWzdGeneralHoleTypes_e.swWzdCounterSink; break;
                case "hole":        h.GenericType = (int)swWzdGeneralHoleTypes_e.swWzdHole; break;
                case "tap":         h.GenericType = (int)swWzdGeneralHoleTypes_e.swWzdTap; break;
                default:
                    throw new ToolFail("BAD_PARAMETER", "hole_type must be one of: counterbore, countersink, hole, tap.");
            }
            h.StandardIdx = HoleWizardStandardIndex(h.Standard)
                            ?? throw new ToolFail("BAD_PARAMETER", "standard must be one of: ansi_inch, ansi_metric, iso, din, jis.");

            // fastener_type: an int (swWzdHoleStandardFastenerTypes_e value) wins; otherwise the small default table for
            // the standard + hole_type. It must match the standard and the hole type.
            int? fastener = null;
            JToken ft = p?["fastener_type"];
            if (ft != null && ft.Type == JTokenType.Integer) fastener = ft.Value<int>();
            else if (ft != null && ft.Type == JTokenType.String)
            {
                string fts = ft.Value<string>();
                if (int.TryParse(fts, out int parsedFt)) fastener = parsedFt;
                else if (!string.IsNullOrEmpty(fts) && fts != "default")
                    throw new ToolFail("BAD_PARAMETER", "fastener_type must be an integer swWzdHoleStandardFastenerTypes_e value (or omitted for the default of the standard + hole_type).");
            }
            h.Fastener = fastener ?? HoleWizardDefaultFastener(h.StandardIdx, h.Type)
                         ?? throw new ToolFail("BAD_PARAMETER", "No default fastener for this standard; pass fastener_type explicitly.");

            h.RequestedSize = PStr(p, "size")
                              ?? throw new ToolFail("MISSING_PARAMETER", "size is required (e.g. 'M6'; it must be valid for the standard + fastener).");
            h.Size = HoleWizardNormalizeSize(h.RequestedSize, h.Type);

            h.End = PStr(p, "end_condition", "blind").ToLowerInvariant();
            if (h.End != "blind" && h.End != "through_all")
                throw new ToolFail("BAD_PARAMETER", "end_condition must be 'blind' or 'through_all'.");
            h.EndType = h.End == "through_all" ? (short)swEndConditions_e.swEndCondThroughAll : (short)swEndConditions_e.swEndCondBlind;

            h.Diameter = PNum(p, "diameter", 0.0);
            if (h.Type == "tap" && h.Diameter <= 0)
                h.Diameter = HoleWizardTapDrillDiameter(h.Size) ?? h.Diameter;
            h.Depth = PNum(p, "depth", 0.0);
            if (h.End == "blind" && h.Depth <= 0)
                throw new ToolFail("MISSING_PARAMETER", "depth (> 0) is required for a blind hole. Pass end_condition='through_all' otherwise.");
            h.Reverse = PBool(p, "reverse", false);

            // Value1..Value12 depend on the hole type (see HoleWizard5 remarks)
            var v = h.V;
            switch (h.GenericType)
            {
                case (int)swWzdGeneralHoleTypes_e.swWzdCounterBore:
                    v[0] = PNum(p, "cbore_diameter", -1); v[1] = PNum(p, "cbore_depth", -1);
                    break;
                case (int)swWzdGeneralHoleTypes_e.swWzdCounterSink:
                    // near countersink diameter / angle (DEGREES at the tool boundary, radians for HoleWizard5); head
                    // clearance and its type as in the API example
                    double? csAngDeg = PNumN(p, "csink_angle");
                    v[0] = PNum(p, "csink_diameter", -1); v[1] = csAngDeg != null ? csAngDeg.Value * Math.PI / 180.0 : -1;
                    v[2] = 0; v[8] = 1;
                    break;
                case (int)swWzdGeneralHoleTypes_e.swWzdTap:
                    h.Length = -1;   // recorder quirk: -1 at the Length slot for straight taps
                    // tap thread depth; the default (2 x diameter) can exceed a blind hole, so 75 % of its depth
                    v[0] = PNumN(p, "thread_depth") ?? (h.End == "blind" ? h.Depth * 0.75 : -1);
                    break;
                default:             // plain hole: Value5/Value6 must be 0 (all -1 => null; zeroing every slot warns on blind holes)
                    v[4] = 0; v[5] = 0;
                    break;
            }
            return h;
        }

        // points: [x,y,z] model-space hole centres (a real array, or its JSON text)
        private static List<double[]> HoleCentres(JObject p)
        {
            var pts = PArr(p, "points");
            if (pts.Count == 0)
                throw new ToolFail("MISSING_PARAMETER", "points is required: [[x,y,z], …] hole centres lying ON the target planar face.");
            return pts.Select(pt => pt is JArray arr && arr.Count == 3
                ? new[] { arr[0].Value<double>(), arr[1].Value<double>(), arr[2].Value<double>() }
                : throw new ToolFail("BAD_PARAMETER", "Each entry of points must be [x,y,z].")).ToList();
        }

        // A hole location is picked as HoleWizard5 wants it (IModelDocExtension.SelectByRay, Mark = 0): a ray from 2 mm
        // above the face along its normal, into it. The sign of IFace2.Normal can depend on the face sense, so a miss is
        // retried once with the ray flipped.
        private static bool PickOnFace(IModelDoc2 d, double[] c, double[] n)
        {
            d.ClearSelection2(true);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                double s = attempt == 0 ? 1.0 : -1.0;
                if (d.Extension.SelectByRay(c[0] + s * n[0] * 0.002, c[1] + s * n[1] * 0.002, c[2] + s * n[2] * 0.002,
                                            -s * n[0], -s * n[1], -s * n[2], 0.0001, (int)swSelectType_e.swSelFACES, false, 0, 0))
                    return true;
            }
            return false;
        }
    }
}
