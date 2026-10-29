using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Reference geometry, sweep / loft, bodies =====================
    // add_plane_angle, add_axis_two_points, sweep_pipe, loft, set_body_visibility (ported from the earlier Python server).
    // Units: mm / degrees.
    public partial class SolidWorksService
    {
        private static double[] Pt3(JToken t, string what)
        {
            var a = t as JArray;
            if (a == null || a.Count != 3) throw new ToolFail("BAD_PARAMETER", what + " must be [x,y,z] (mm)");
            return a.Select(v => (double)v / MM).ToArray();
        }

        // Plane through an axis/edge at an angle to a base plane (swRefPlaneReferenceConstraint_Angle).
        public ExecutionResponse AddPlaneAngle(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            d.ClearSelection2(true);
            string basePlane = PStr(p, "base_plane") ?? throw new ToolFail("MISSING_PARAMETER", "base_plane is required.");
            if (!SelectPlaneFlexible(d, basePlane, false, 0)) throw new ToolFail("PLANE_NOT_FOUND", $"No plane '{basePlane}' (Front / Top / Right, or a plane in the feature tree).");
            if (p["edge_index"] != null)
                SelectEntity(d, EdgeAt(d, PInt(p, "edge_index", -1)), true, 1);
            else
            {
                string axis = PStr(p, "axis") ?? throw new ToolFail("MISSING_PARAMETER", "axis (name) or edge_index is required.");
                SelectEntity(d, FindFeatureByName(d, axis, null) ?? throw new ToolFail("AXIS_NOT_FOUND", $"No axis '{axis}' in the feature tree."), true, 1);
            }
            var f = d.FeatureManager.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle,
                PNum(p, "angle_deg", 45) * Math.PI / 180, (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0, 0, 0) as IFeature;
            d.ClearSelection2(true);
            if (f == null) throw new ToolFail("PLANE_FAILED", "InsertRefPlane returned null (axis must lie in/parallel to the base plane).");
            string refused = NameRefused(f, PStr(p, "name"));   // before the result reads the name
            return WithWarning(new JObject { ["plane"] = f.Name }, refused);
        });

        // The created feature keeps its automatic name when SolidWorks refuses the requested one — a warning, not a failure.
        private static string NameRefused(IFeature f, string name) =>
            name == null || TryRename(f, name) ? null : $"SolidWorks refused the name '{name}' (already used, or invalid); the feature is '{f.Name}'.";

        // Axis through two points: a 3D-sketch line + InsertAxis2.
        public ExecutionResponse AddAxisTwoPoints(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var a = Pt3(p["p1_mm"], "p1_mm"); var b = Pt3(p["p2_mm"], "p2_mm");
            ISketchSegment seg = null;
            var sk = Sketch3D(d, skm => seg = skm.CreateLine(a[0], a[1], a[2], b[0], b[1], b[2]) as ISketchSegment);
            if (seg == null) throw new ToolFail("SKETCH_FAILED", "3D line not created.");
            d.ClearSelection2(true);
            seg.Select4(false, null);
            if (!d.InsertAxis2(true)) throw new ToolFail("AXIS_FAILED", "InsertAxis2 returned false.");
            var ax = LastFeature(d);
            string refused = NameRefused(ax, PStr(p, "name"));
            return WithWarning(new JObject { ["axis"] = ax.Name, ["sketch"] = sk?.Name }, refused);
        });

        // Pipe / hose / cable along a 3D polyline with a circular profile (optionally thin-walled).
        public ExecutionResponse SweepPipe(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var pts = PArr(p, "points_mm").Select(t => Pt3(t, "points_mm item")).ToList();
            if (pts.Count < 2) throw new ToolFail("BAD_PARAMETER", "points_mm needs at least 2 points.");
            double dia = PNum(p, "diameter_mm", 0) / MM;
            if (dia <= 0) throw new ToolFail("BAD_PARAMETER", "diameter_mm > 0 is required.");
            double wall = PNum(p, "wall_mm", 0) / MM;
            double bend = PNum(p, "bend_radius_mm", 0) / MM;
            var path = Sketch3D(d, skm =>
            {
                for (int i = 1; i < pts.Count; i++)
                    skm.CreateLine(pts[i - 1][0], pts[i - 1][1], pts[i - 1][2], pts[i][0], pts[i][1], pts[i][2]);
            }) ?? throw new ToolFail("SKETCH_FAILED", "Path sketch not created.");
            if (bend > 0)
            {
                // round the polyline corners: sketch fillets in the 3D path sketch (closed again on every path)
                var skm = d.SketchManager;
                path.Select2(false, 0); d.EditSketch();
                try
                {
                    foreach (var pt in pts.Skip(1).Take(pts.Count - 2))
                    {
                        d.ClearSelection2(true);
                        d.Extension.SelectByID2("", "SKETCHPOINT", pt[0], pt[1], pt[2], false, 0, null, 0);
                        skm.CreateFillet(bend, (int)swConstrainedCornerAction_e.swConstrainedCornerDeleteGeometry);
                    }
                }
                finally { if (skm.ActiveSketch != null) skm.Insert3DSketch(true); }
            }
            d.ClearSelection2(true);
            path.Select2(false, 4);
            bool thin = wall > 0;
            var f = d.FeatureManager.InsertProtrusionSwept4(false, false, (int)swTwistControlType_e.swTwistControlFollowPath, false, false,
                0, 0, thin, wall, 0, 0, 0, PBool(p, "merge", true), true, true, 0, true, true, dia, 0) as IFeature;
            d.ClearSelection2(true);
            if (f == null) throw new ToolFail("SWEEP_FAILED", "InsertProtrusionSwept4 returned null (self-intersecting path? use bend_radius_mm).");
            return new JObject { ["feature"] = f.Name, ["path_sketch"] = path.Name, ["diameter_mm"] = dia * MM, ["wall_mm"] = thin ? new JValue(wall * MM) : null };
        });

        public ExecutionResponse Loft(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            var names = PStrList(p, "profile_sketches");
            if (names.Count < 2) throw new ToolFail("BAD_PARAMETER", "profile_sketches needs ≥ 2 sketch names, in order.");
            ExitSketch(d);   // the last profile is often still open
            d.ClearSelection2(true);
            foreach (var n in names)
            {
                var sk = FindFeatureByName(d, n, null) ?? throw new ToolFail("SKETCH_NOT_FOUND", $"No sketch '{n}' in the feature tree.");
                sk.Select2(true, 1);
            }
            var f = d.FeatureManager.InsertProtrusionBlend2(PBool(p, "closed", false), true, false, 1.0, 0, 0, 1.0, 1.0,
                true, true, false, 0.0, 0.0, 0, PBool(p, "merge", true), true, true, 0) as IFeature;
            d.ClearSelection2(true);
            if (f == null) throw new ToolFail("LOFT_FAILED", "InsertProtrusionBlend2 returned null (profiles must be closed, non-coplanar).");
            // the created name in cadState.features, as extrude_feature reports it
            var g = WithWarning(BuildBodySummary(d), CheckCreated(d, f, "loft")) ?? new JObject();
            g["feature"] = f.Name;
            return new ToolOutput { Features = new System.Collections.Generic.List<string> { f.Name }, Geometry = g };
        });

        public ExecutionResponse SetBodyVisibility(ToolRequest request) => Run(request, true, p =>
        {
            var part = ActivePartOrFail(out var d);
            var bodies = (part.GetBodies2((int)swBodyType_e.swAllBodies, false) as object[] ?? new object[0]).OfType<IBody2>().ToList();
            var want = PStrList(p, "bodies");
            bool hide = PBool(p, "hidden", true);
            var changed = new JArray();
            foreach (var b in bodies.Where(b => want.Count == 0 || want.Contains(b.Name)))
            { b.HideBody(hide); changed.Add(b.Name); }
            d.GraphicsRedraw2();
            return new JObject { ["changed"] = changed, ["all_bodies"] = new JArray(bodies.Select(b => b.Name)) };
        });

    }
}
