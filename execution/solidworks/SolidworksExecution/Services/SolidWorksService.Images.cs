using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // view_image: PNG of the active model (named view, display mode, hidden components, optional section, zoom to chosen
    // components or a box) — SaveAs to an image works in the hidden own instance (proven by the earlier Python server). Auto-crop to
    // content, scale so the LONG side is `size`, draw the view's axes in the emptiest corner; the adapter returns the PNG
    // to the model as an image.
    public partial class SolidWorksService
    {
        private static readonly Dictionary<string, int> NamedViews = new Dictionary<string, int>
        {
            ["front"] = (int)swStandardViews_e.swFrontView, ["back"] = (int)swStandardViews_e.swBackView,
            ["left"] = (int)swStandardViews_e.swLeftView, ["right"] = (int)swStandardViews_e.swRightView,
            ["top"] = (int)swStandardViews_e.swTopView, ["bottom"] = (int)swStandardViews_e.swBottomView,
            ["iso"] = (int)swStandardViews_e.swIsometricView, ["trimetric"] = (int)swStandardViews_e.swTrimetricView,
            ["dimetric"] = (int)swStandardViews_e.swDimetricView,
        };

        private static readonly swUserPreferenceToggle_e[] HideRefToggles =
        {
            swUserPreferenceToggle_e.swDisplayPlanes, swUserPreferenceToggle_e.swDisplayAxes,
            swUserPreferenceToggle_e.swDisplayOrigins, swUserPreferenceToggle_e.swDisplayCoordSystems,
            swUserPreferenceToggle_e.swDisplaySketches,
            swUserPreferenceToggle_e.swDisplayReferencePoints,
        };

        private const double MaxShotPixels = 16e6;   // the re-shot of a long narrow model stops here (SaveAs time grows with it)

        public ExecutionResponse ViewImage(ToolRequest request) => Run(request, false, p => CaptureView(ActiveDocOrFail(), p));

        // view_image's capture: params view/view_dir/up/display/size/focus/focus_box_mm/isolate/hide_components/section_*/
        // crop/triad/file_path. Read-only for the user: the view (orientation, zoom, display mode), the section, hidden
        // components and reference toggles are all put back afterwards.
        private JObject CaptureView(IModelDoc2 d, JObject p)
        {
            string view = PStr(p, "view", "iso").ToLowerInvariant();
            var viewDir = PVec(p, "view_dir");
            var upHint = PVec(p, "up");
            string viewTag = viewDir != null ? "dir" + string.Concat(viewDir.Select(v => v > 1e-9 ? "+" : v < -1e-9 ? "-" : "0")) : view;
            string display = PStr(p, "display", "shaded_edges").ToLowerInvariant();
            // a presentation picture: perspective, shadows, ambient occlusion, full-scene anti-aliasing and no edge lines
            bool render = PBool(p, "render", false);
            if (render && display == "shaded_edges") display = "shaded";
            int size = Math.Max(200, Math.Min(2000, PInt(p, "size", 640)));
            // Short names: a 160-character document title took the path past 260 characters (GDI+ "generic error",
            // SaveAs err 1).
            string stem = SafeName(Path.GetFileNameWithoutExtension(d.GetTitle()));
            if (stem.Length > 40) stem = stem.Substring(0, 40).TrimEnd();
            string imgDir = Path.Combine(Path.GetTempPath(), "SolidWorksMCP", "img");
            Directory.CreateDirectory(imgDir);
            string outPath = PStr(p, "file_path") ?? Path.Combine(imgDir, stem + "_" + SafeName(viewTag.TrimStart('*')) + "_" + DateTime.Now.ToString("HHmmssfff") + ".png");
            EnsureFolderFor(outPath);

            // Every name is resolved BEFORE anything is hidden.
            var hideNames = PStrList(p, "hide_components");
            var focusNames = PStrList(p, "focus");
            bool isolate = PBool(p, "isolate", false);
            var boxArg = PArr(p, "focus_box_mm").Select(t => t.ToObject<double>() / MM).ToArray();
            if (boxArg.Length != 0 && boxArg.Length != 6)
                throw new ToolFail("BAD_PARAMETER", "focus_box_mm is 6 numbers: x1, y1, z1, x2, y2, z2 (two opposite corners, mm).");
            if (isolate && focusNames.Count == 0) throw new ToolFail("MISSING_PARAMETER", "isolate shows only the focus components — give focus.");
            IAssemblyDoc a = null;
            List<IComponent2> all = null, focus = new List<IComponent2>(), toHide = new List<IComponent2>();
            if (hideNames.Count > 0 || focusNames.Count > 0)
            {
                a = AsmOrFail(d, hideNames.Count > 0 ? "hide_components" : "focus");
                all = Comps(a, false);
                toHide = hideNames.Select(n => FindComp(a, n, all)).ToList();
                focus = focusNames.Select(n => FindComp(a, n, all)).ToList();
            }
            double[] box = boxArg.Length == 6 ? boxArg : null;

            var hidden = new List<IComponent2>();
            foreach (var c in toHide) Hide(c, hidden);
            bool isolated = false;
            var hideRefs = new PrefScope(_solidWorks, d);   // planes / axes / sketches off in this document for the shot
            foreach (var t in HideRefToggles)
                try { hideRefs.Toggle(t, false); } catch { }
            // a revolve's profile sketch stayed in the picture with sketches off — View > Hide All Types as well
            try { hideRefs.Toggle(swUserPreferenceToggle_e.swViewDisplayHideAllTypes, true); } catch { }
            bool section = false;
            var mv = d.ActiveView as IModelView;
            var before = ViewState.Of(mv);
            int mode = -1;
            try { mode = mv?.DisplayMode ?? -1; } catch { }
            // SYSTEM options, shared with the user's SolidWorks through the registry — put back in finally
            var look = new PrefScope(_solidWorks);
            bool perspective = false;
            try
            {
                if (render)
                {
                    // a presentation picture shows no selection: the last sketch of a fresh part stayed blue (points
                    // and edges) in it
                    try { d.ClearSelection2(true); } catch { }
                    try { look.Int(swUserPreferenceIntegerValue_e.swSystemOptionDisplayAntiAliasing, (int)swSystemOptionDisplayAntiAliasing_e.swSystemOptionDisplayAntiAliasing_FullScene); } catch { }
                    try { look.Toggle(swUserPreferenceToggle_e.swDisplayShadowsInShadedMode, true); } catch { }
                    try { look.Toggle(swUserPreferenceToggle_e.swDisplayAmbientOcclusionShadows, true); } catch { }
                    try { if (mv != null && !mv.HasPerspective()) { mv.AddPerspective(); perspective = true; } } catch { }
                }
                if (viewDir != null) SetViewDir(mv, viewDir, upHint);
                else if (view.StartsWith("*")) d.ShowNamedView2(view.Substring(1), -1);
                else d.ShowNamedView2("", NamedViews.TryGetValue(view, out var vid) ? vid : (int)swStandardViews_e.swIsometricView);
                if (display == "wireframe") d.ViewDisplayWireframe();
                else if (display == "hlr") d.ViewDisplayHiddenremoved();
                else if (display == "hlv") d.ViewDisplayHiddengreyed();
                else
                {
                    d.ViewDisplayShaded();
                    // Light parts on the white background read as an empty picture without their edges.
                    if (display != "shaded" && mv != null) mv.DisplayMode = (int)swViewDisplayMode_e.swViewDisplayMode_ShadedWithEdges;
                }

                string plane = PStr(p, "section_plane");
                if (plane != null)
                {
                    d.ClearSelection2(true);
                    if (!SelectPlaneFlexible(d, plane, false, 0)) throw new ToolFail("PLANE_NOT_FOUND", $"No section plane '{plane}' (Front / Top / Right, or a plane in the feature tree).");
                    var mvm = d.ModelViewManager;
                    var sd = mvm.CreateSectionViewData() as SectionViewData;
                    sd.FirstPlane = (d.SelectionManager as ISelectionMgr).GetSelectedObject6(1, -1);
                    sd.FirstOffset = PNum(p, "section_offset_mm", 0) / MM;
                    sd.FirstReverseDirection = PBool(p, "section_reverse", false);
                    section = mvm.CreateSectionView(sd);
                    d.ClearSelection2(true);
                }
                var axes = ViewAxes(mv);
                // Focus: the components' common box (or focus_box_mm) is fitted to the graphics area. isolate adds SolidWorks'
                // Isolate (display only, the document stays unmodified) so nothing else is drawn. Zoom to fit in Isolate
                // fitted the whole machine on a big assembly (the unit came out 288 px), and its view kept after ExitIsolate came out
                // shifted (ExitIsolate moves the view itself) — hence the box.
                bool fitted = false;
                if (isolate) isolated = IsolateFocus(d, a, focus);
                d.ViewZoomtofit2();
                if (focus.Count > 0 || box != null) fitted = FitBox(mv, box ?? UnionBox(focus));

                var tm = System.Diagnostics.Stopwatch.StartNew();
                int err = 0, warn = 0;
                // SaveBMP renders the graphics area at the size asked, keeps its orientation and shows what the window
                // shows (Isolate included). SaveAs to an image came back portrait whatever the paper (3640×2189 asked,
                // 2189×3640 given, the model in its top third), drew the isolated-away components and opened the image
                // viewer — it is only the fallback when SaveBMP fails.
                bool bmp = PStr(p, "method", "bmp") != "saveas";
                string raw = Path.Combine(imgDir, "raw_" + Guid.NewGuid().ToString("N").Substring(0, 8) + (bmp ? ".bmp" : QuietImageExt()));
                double aspect = ScreenAspect(mv);
                // Zoom to fit leaves about half of the window empty (content 39-49 % of its width); a fitted box fills 90 %.
                double paper = fitted || isolated ? 1.3 : 2.6;
                int w = (int)(size * paper), h = (int)Math.Round(w / aspect);
                double fit = Math.Sqrt(MaxShotPixels / ((double)w * h));
                if (fit < 1) { w = (int)(w * fit); h = (int)(h * fit); }
                bool crop = PBool(p, "crop", true);
                bool ok = Shoot(d, ref raw, w, h, ref bmp, ref err, ref warn);
                if (!ok || !File.Exists(raw)) throw new ToolFail("IMAGE_FAILED", $"Neither SaveBMP nor SaveAs made the picture (err {err}, warn {warn}).");
                var content = ContentRect(raw, crop);
                long tFirst = tm.ElapsedMilliseconds;
                // A long narrow model fills the graphics area only along one side: shoot it once more bigger so its long
                // side still gets `size` pixels.
                int longSide = Math.Max(content.Width, content.Height);
                double grow = Math.Min(size * 1.15 / Math.Max(1, longSide), Math.Sqrt(MaxShotPixels / ((double)w * h)));
                if (crop && longSide < size * 0.9 && grow > 1.15)
                {
                    w = (int)(w * grow); h = (int)(h * grow);
                    try { File.Delete(raw); } catch { }
                    ok = Shoot(d, ref raw, w, h, ref bmp, ref err, ref warn);
                    if (!ok || !File.Exists(raw)) throw new ToolFail("IMAGE_FAILED", $"Neither SaveBMP nor SaveAs made the picture (err {err}, warn {warn}).");
                    content = ContentRect(raw, crop);
                }
                int[] px = CropAndScale(raw, outPath, content, size, PBool(p, "triad", true) ? axes : null);
                SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: {(bmp ? "SaveBMP" : "SaveAs")}, aspect {aspect:0.00}, shot {w}x{h}, content {content.Width}x{content.Height}, " +
                    $"first shot {tFirst} ms, total {tm.ElapsedMilliseconds} ms");
                try { File.Delete(raw); } catch { }
                var res = new JObject { ["image"] = outPath, ["size"] = new JArray(px), ["view"] = viewDir != null ? "view_dir" : view, ["section"] = section };
                if (render) res["render"] = true;
                if (axes != null)
                    res["view_axes"] = new JObject { ["right"] = AxisName(axes[0]), ["up"] = AxisName(axes[1]), ["toward_viewer"] = AxisName(axes[2]) };
                if (focus.Count > 0) res["focus"] = new JArray(focus.Select(c => c.Name2));
                if (box != null) res["zoomed_to_mm"] = R(box.Select(v => v * MM), 1);
                if ((focus.Count > 0 || box != null) && !fitted && !isolated) res["warning"] = "Could not zoom to the focus — the picture shows the whole model.";
                if (isolated) res["isolated"] = true;
                return res;
            }
            finally
            {
                if (perspective) try { mv.RemovePerspective(); } catch { }
                look.Dispose();
                if (section) try { d.ModelViewManager.RemoveSectionView(); } catch { }
                if (isolated) try { a.ExitIsolate(); } catch { }
                foreach (var c in hidden) try { c.Visible = (int)swComponentVisibilityState_e.swComponentVisible; } catch { }
                hideRefs.Dispose();
                try
                {
                    if (mv != null)
                    {
                        if (mode >= 0) mv.DisplayMode = mode;
                        before.Apply(_solidWorks, mv);
                        d.GraphicsRedraw2();
                    }
                }
                catch (Exception ex) { SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: view not restored: {ex.Message}"); }
            }
        }

        // A 3-number list parameter (a direction), or null.
        private static double[] PVec(JObject p, string key)
        {
            var v = PArr(p, key);
            if (v.Count == 0) return null;
            double[] r = null;
            try { r = v.Select(t => t.ToObject<double>()).ToArray(); } catch (Exception) { }
            if (r == null || r.Length != 3 || Math.Sqrt(r.Sum(x => x * x)) < 1e-9)
                throw new ToolFail("BAD_PARAMETER", $"{key} is 3 numbers, a direction [x, y, z] that is not zero.");
            return r;
        }

        // The view's orientation, zoom and pan as numbers (the API's MathTransform / MathVector are live objects).
        private sealed class ViewState
        {
            private double[] _orient, _shift;
            private double _scale;

            public static ViewState Of(IModelView mv)
            {
                var s = new ViewState();
                try
                {
                    s._orient = ((mv?.Orientation3 as MathTransform)?.ArrayData as double[])?.ToArray();
                    s._shift = ((mv?.Translation3 as MathVector)?.ArrayData as double[])?.ToArray();
                    s._scale = mv?.Scale2 ?? 0;
                }
                catch (Exception ex) { SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: view state not read: {ex.Message}"); }
                return s;
            }

            public void Apply(ISldWorks sw, IModelView mv)
            {
                if (mv == null) return;
                var mu = sw.GetMathUtility() as IMathUtility;
                if (_orient != null) mv.Orientation3 = mu.CreateTransform(_orient.ToArray()) as MathTransform;
                if (_scale > 0) mv.Scale2 = _scale;
                if (_shift != null) mv.Translation3 = mu.CreateVector(_shift.ToArray()) as MathVector;
            }
        }

        // Look along -view_dir (view_dir points from the model toward the viewer: [1,1,1] = the usual iso, [-1,1,-1] =
        // iso from behind on the left). `up` is a hint (default +Y; looking along ±Y it is ∓Z, like SolidWorks' top /
        // bottom views).
        private void SetViewDir(IModelView mv, double[] dir, double[] upHint)
        {
            if (mv == null) return;
            double[] o = Unit(dir);
            double[] u0 = upHint != null ? Unit(upHint) : Math.Abs(o[1]) > 0.98 ? new[] { 0.0, 0, o[1] > 0 ? -1.0 : 1.0 } : new[] { 0.0, 1, 0 };
            double[] right = Cross(u0, o);
            if (Math.Sqrt(right.Sum(x => x * x)) < 1e-6) throw new ToolFail("BAD_PARAMETER", "up must not be parallel to view_dir.");
            right = Unit(right);
            double[] up = Cross(o, right);
            var cur = ((mv.Orientation3 as MathTransform)?.ArrayData as double[])?.ToArray() ?? new double[16];
            if (cur.Length < 16) Array.Resize(ref cur, 16);
            double[] m = { right[0], up[0], o[0], right[1], up[1], o[1], right[2], up[2], o[2] };   // columns: right, up, toward
            Array.Copy(m, cur, 9);
            if (cur[12] == 0) cur[12] = 1;
            mv.Orientation3 = (_solidWorks.GetMathUtility() as IMathUtility).CreateTransform(cur) as MathTransform;
        }

        private static double[] Unit(double[] v) { double n = Math.Sqrt(v.Sum(x => x * x)); return v.Select(x => x / n).ToArray(); }
        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out ClientRect rect);
        private struct ClientRect { public int Left, Top, Right, Bottom; }

        // The graphics area in pixels (the view window's client rectangle), or null.
        private static Size? ScreenSize(IModelView mv)
        {
            try
            {
                var h = new IntPtr(mv.GetViewHWndx64());
                if (h != IntPtr.Zero && GetClientRect(h, out var r) && r.Right > 10 && r.Bottom > 10) return new Size(r.Right, r.Bottom);
            }
            catch { }
            return null;
        }

        private static double ScreenAspect(IModelView mv)
        {
            var s = ScreenSize(mv);
            double a = s != null ? s.Value.Width / (double)s.Value.Height : 4.0 / 3;
            try { if (s == null && mv != null && mv.FrameWidth > 0 && mv.FrameHeight > 0) a = mv.FrameWidth / (double)mv.FrameHeight; } catch { }
            return Math.Max(0.3, Math.Min(4.0, a));
        }

        // Screen rectangle (px) of a model box's 8 corners.
        private static RectangleF ProjectBox(IModelView mv, double[] b)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                double X = (i & 1) == 0 ? b[0] : b[3], Y = (i & 2) == 0 ? b[1] : b[4], Z = (i & 4) == 0 ? b[2] : b[5];
                mv.ProjectModelPoint(X, Y, Z, out double sx, out double sy, out double sz);
                x0 = Math.Min(x0, (float)sx); x1 = Math.Max(x1, (float)sx); y0 = Math.Min(y0, (float)sy); y1 = Math.Max(y1, (float)sy);
            }
            return RectangleF.FromLTRB(x0, y0, x1, y1);
        }

        // Pan and zoom so the model box fills ~90 % of the graphics area. TranslateBy's units are calibrated on the spot
        // (one small step, measured through ProjectModelPoint), so nothing depends on how SolidWorks scales them. false
        // when the view did not follow (then the picture is of the whole model).
        private static bool FitBox(IModelView mv, double[] box, double fill = 0.9)
        {
            var scr = ScreenSize(mv);
            if (mv == null || scr == null) return false;
            double W = scr.Value.Width, H = scr.Value.Height;
            try
            {
                for (int round = 0; round < 3; round++)
                {
                    var r = ProjectBox(mv, box);
                    double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                    const double step = 0.01;
                    mv.TranslateBy(step, 0); var rx = ProjectBox(mv, box); mv.TranslateBy(-step, 0);
                    mv.TranslateBy(0, step); var ry = ProjectBox(mv, box); mv.TranslateBy(0, -step);
                    double kx = (rx.X + rx.Width / 2 - cx) / step, ky = (ry.Y + ry.Height / 2 - cy) / step;
                    SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: box round {round}: {r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0} of {W}x{H}, px per unit {kx:0.#} / {ky:0.#}");
                    if (Math.Abs(kx) < 1e-6 || Math.Abs(ky) < 1e-6) return false;
                    mv.TranslateBy((W / 2 - cx) / kx, (H / 2 - cy) / ky);
                    r = ProjectBox(mv, box);
                    double f = fill * Math.Min(W / Math.Max(1, r.Width), H / Math.Max(1, r.Height));
                    bool centred = Math.Abs(r.X + r.Width / 2 - W / 2) < W * 0.02 && Math.Abs(r.Y + r.Height / 2 - H / 2) < H * 0.02;
                    if (Math.Abs(f - 1) < 0.03 && centred) break;
                    mv.ZoomByFactor(f);
                }
                var done = ProjectBox(mv, box);
                SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: box on screen {done.X:0},{done.Y:0} {done.Width:0}x{done.Height:0} of {W}x{H}");
                return done.Width > W * 0.5 || done.Height > H * 0.5;
            }
            catch (Exception ex)
            {
                SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: box zoom failed: {ex.Message}");
                return false;
            }
        }

        // One shot of the current view to `raw`, w × h px. Plain white background (gradients defeat auto-crop and waste
        // tokens). These are SYSTEM options (shared registry with the user's SW) — restored right after. Image size:
        // "print capture" at 100 dpi on a user-defined paper of w × h px, instead of a multi-megapixel default (measured:
        // 29 s SaveAs + 15 s crop before this).
        private bool Shoot(IModelDoc2 d, ref string raw, int w, int h, ref bool bmp, ref int err, ref int warn)
        {
            using (new PrefScope(_solidWorks)
                .Int(swUserPreferenceIntegerValue_e.swColorsBackgroundAppearance, (int)swColorsBackgroundAppearance_e.swColorsBackgroundAppearance_Plain)
                .Int(swUserPreferenceIntegerValue_e.swSystemColorsViewportBackground, 0xFFFFFF)
                .Int(swUserPreferenceIntegerValue_e.swTiffScreenOrPrintCapture, 1)
                .Int(swUserPreferenceIntegerValue_e.swTiffPrintDPI, 100)
                .Int(swUserPreferenceIntegerValue_e.swTiffPrintPaperSize, (int)swDwgPaperSizes_e.swDwgPapersUserDefined)
                .Double(swUserPreferenceDoubleValue_e.swTiffPrintPaperWidth, w / 100.0 * 0.0254)
                .Double(swUserPreferenceDoubleValue_e.swTiffPrintPaperHeight, h / 100.0 * 0.0254)
                .Toggle(swUserPreferenceToggle_e.swTiffPrintScaleToFit, true)
                .Int(swUserPreferenceIntegerValue_e.swTiffImageType, (int)swTiffImageType_e.swTiffImageRGB)   // a .tif shot (QuietImageExt)
                .Int(swUserPreferenceIntegerValue_e.swTiffCompressionScheme, (int)swTiffCompressionScheme_e.swTiffUncompressed))
            {
                d.GraphicsRedraw2();
                if (bmp)
                {
                    try { if (d.SaveBMP(raw, w, h) && File.Exists(raw)) return true; }
                    catch (Exception ex) { SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: SaveBMP failed: {ex.Message}"); }
                    bmp = false;
                    raw = Path.ChangeExtension(raw, QuietImageExt());
                }
                return d.Extension.SaveAs(raw, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, ref err, ref warn);
            }
        }

        // SolidWorks opens every PNG / JPEG / TIF it saves in the default image viewer when that format's "View … After
        // Saving" export setting is on. The setting is not in the API and a running SolidWorks does not re-read it from
        // the registry, and the shot is deleted right after the crop, so each view_image left an empty viewer window
        // ("file moved or renamed"). The shot is saved in a format whose flag is off: PNG, else TIF, else JPEG.
        private string QuietImageExt()
        {
            string key = @"HKEY_CURRENT_USER\Software\SolidWorks\SOLIDWORKS ";
            try { key += (1992 + int.Parse(_solidWorks.RevisionNumber().Split('.')[0])) + @"\Export Settings"; }
            catch { return ".png"; }
            foreach (var f in new[] { ".png PNG", ".tif TIF", ".jpg JPEG" })
            {
                var ext = f.Split(' ');
                if (!(Microsoft.Win32.Registry.GetValue(key, $"View {ext[1]} After Saving", 0) is int on) || on == 0) return ext[0];
            }
            SolidworksExecution.Infrastructure.ExecLog.Write("view_image: View PNG / TIF / JPEG After Saving are all on — the viewer opens the shot");
            return ".png";
        }

        private static void Hide(IComponent2 c, List<IComponent2> hidden)
        {
            try
            {
                if (c.Visible == (int)swComponentVisibilityState_e.swComponentHidden) return;
                c.Visible = (int)swComponentVisibilityState_e.swComponentHidden;
                hidden.Add(c);
            }
            catch { }   // suppressed / lightweight: nothing to hide
        }

        // SolidWorks' Isolate of the focus components (the rest hidden); false when it does not take (then the box zoom).
        private static bool IsolateFocus(IModelDoc2 d, IAssemblyDoc a, List<IComponent2> focus)
        {
            try
            {
                d.ClearSelection2(true);
                foreach (var c in focus) c.Select4(true, null, false);
                bool ok = a.Isolate();
                if (ok) a.SetIsolateVisibility((int)swIsolateVisibility_e.swIsolateVisibility_HIDDEN);
                d.ClearSelection2(true);   // no selection highlight in the shot
                return ok;
            }
            catch (Exception ex)
            {
                SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: isolate failed: {ex.Message}");
                return false;
            }
        }

        // The components' common bounding box in assembly space (m), a little larger so the edges are not cut.
        private static double[] UnionBox(List<IComponent2> comps)
        {
            var boxes = comps.Select(c => { try { return c.GetBox(false, false) as double[]; } catch { return null; } })
                             .Where(b => b != null && b.Length == 6).ToList();
            if (boxes.Count == 0) throw new ToolFail("BAD_PARAMETER", "The focus components have no geometry to zoom to (suppressed or empty).");
            var lo = new double[3]; var hi = new double[3];
            for (int i = 0; i < 3; i++)
            {
                lo[i] = boxes.Min(b => Math.Min(b[i], b[i + 3]));
                hi[i] = boxes.Max(b => Math.Max(b[i], b[i + 3]));
            }
            double pad = 0.05 * Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (hi[i] - lo[i]) * (hi[i] - lo[i])));
            return new[] { lo[0] - pad, lo[1] - pad, lo[2] - pad, hi[0] + pad, hi[1] + pad, hi[2] + pad };
        }

        // The screen's right, up and toward-the-viewer directions in model space: the columns of the view rotation (SolidWorks
        // multiplies row vectors: view = model · R).
        private static double[][] ViewAxes(IModelView mv)
        {
            try
            {
                var r = (mv?.Orientation3 as MathTransform)?.ArrayData as double[];
                if (r == null || r.Length < 9) return null;
                return Enumerable.Range(0, 3).Select(j => new[] { r[j], r[3 + j], r[6 + j] }).ToArray();
            }
            catch { return null; }
        }

        // "+X" when the direction lies along an axis, else its components ("+0.71X -0.71Z").
        private static string AxisName(double[] v)
        {
            string[] n = { "X", "Y", "Z" };
            int i = Enumerable.Range(0, 3).OrderByDescending(k => Math.Abs(v[k])).First();
            if (Math.Abs(v[i]) > 0.999) return (v[i] > 0 ? "+" : "-") + n[i];
            return string.Join(" ", Enumerable.Range(0, 3).Where(k => Math.Abs(v[k]) > 0.005).Select(k => v[k].ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture) + n[k]));
        }

        // Everything that is not the white background (set for the shot), padded; the whole shot without crop. Not the
        // corner's colour: a close-up often has the model in the corner. Light faces render 255,255,249 — content, unless
        // the shot is a JPEG (its noise needs the old tolerance).
        private static Rectangle ContentRect(string src, bool crop)
        {
            using (var bmp = new Bitmap(src))
            {
                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                SolidworksExecution.Infrastructure.ExecLog.Write($"view_image: raw {bmp.Width}x{bmp.Height}");
                if (!crop) return rect;
                var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var px = new int[bmp.Width * bmp.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                bmp.UnlockBits(data);
                int W = bmp.Width, m = 4, tol = src.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? 24 : 3;
                const int br = 255, bgr = 255, bb = 255;
                int x0 = W, y0 = bmp.Height, x1 = -1, y1 = -1;
                for (int y = m; y < bmp.Height - m; y++)
                    for (int x = m, i = y * W + m; x < W - m; x++, i++)
                    {
                        int c = px[i];
                        int d = Math.Abs(((c >> 16) & 255) - br) + Math.Abs(((c >> 8) & 255) - bgr) + Math.Abs((c & 255) - bb);
                        if (d > tol) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                    }
                if (x1 <= x0 || y1 <= y0) return rect;
                int pad = 12;
                x0 = Math.Max(0, x0 - pad); y0 = Math.Max(0, y0 - pad);
                x1 = Math.Min(bmp.Width - 1, x1 + pad); y1 = Math.Min(bmp.Height - 1, y1 + pad);
                return new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            }
        }

        // Crop to `rect`, scale so the long side is at most `size`, draw the axes (right / up / toward-viewer directions
        // from ViewAxes).
        private static int[] CropAndScale(string src, string dst, Rectangle rect, int size, double[][] axes)
        {
            using (var bmp = new Bitmap(src))
            {
                double k = Math.Min(1.0, size / (double)Math.Max(rect.Width, rect.Height));
                int w = Math.Max(1, (int)Math.Round(rect.Width * k)), h = Math.Max(1, (int)Math.Round(rect.Height * k));
                var outBmp = new Bitmap(w, h);
                try
                {
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bmp, new Rectangle(0, 0, w, h), rect, GraphicsUnit.Pixel);
                    }
                    if (axes != null) outBmp = WithTriad(outBmp, axes);
                    outBmp.Save(dst, ImageFormat.Png);
                    return new[] { outBmp.Width, outBmp.Height };
                }
                finally { outBmp.Dispose(); }
            }
        }

        private const int TriadBox = 64;

        // The triad in the emptiest corner; when every corner holds the model (a close-up, a narrow column) — in a white
        // strip added along the short side.
        private static Bitmap WithTriad(Bitmap img, double[][] axes)
        {
            const int T = TriadBox;
            var spot = img.Width >= 2 * T && img.Height >= 2 * T ? EmptyCorner(img) : null;
            if (spot != null)
            {
                using (var g = Graphics.FromImage(img)) DrawTriad(g, spot.Value, axes);
                return img;
            }
            bool below = img.Width >= img.Height;
            var big = new Bitmap(below ? img.Width : img.Width + T, below ? img.Height + T : img.Height);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.White);
                g.DrawImage(img, below ? 0 : T, 0, img.Width, img.Height);
                DrawTriad(g, below ? new Point(0, img.Height) : new Point(0, 0), axes);
            }
            img.Dispose();
            return big;
        }

        // The corner box with the least model in it, or null when even that one is more than 5 % model.
        private static Point? EmptyCorner(Bitmap img)
        {
            const int T = TriadBox;
            var corners = new[] { new Point(0, img.Height - T), new Point(0, 0), new Point(img.Width - T, img.Height - T), new Point(img.Width - T, 0) };
            Point best = corners[0]; int bestInk = int.MaxValue;
            foreach (var c in corners)
            {
                int ink = 0;
                for (int y = c.Y; y < c.Y + T; y += 2)
                    for (int x = c.X; x < c.X + T; x += 2)
                    {
                        var col = img.GetPixel(x, y);
                        if (col.R + col.G + col.B < 3 * 255 - 3) ink++;
                    }
                if (ink < bestInk) { bestInk = ink; best = c; }
            }
            return bestInk <= T * T / 4 / 20 ? best : (Point?)null;
        }

        // X red, Y green, Z blue, as seen in this view, in the T × T box at `at`.
        private static void DrawTriad(Graphics g, Point at, double[][] axes)
        {
            const int T = TriadBox, L = 20;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var o = new PointF(at.X + T / 2f, at.Y + T / 2f);
            string[] names = { "X", "Y", "Z" };
            Color[] colors = { Color.FromArgb(210, 30, 30), Color.FromArgb(20, 140, 20), Color.FromArgb(30, 70, 220) };
            using (var font = new Font("Arial", 8f, FontStyle.Bold))
            {
                // An axis pointing away from the viewer is drawn first, so the nearer ones stay on top.
                foreach (int i in Enumerable.Range(0, 3).OrderBy(i => axes[2][i]))
                {
                    float dx = (float)axes[0][i] * L, dy = -(float)axes[1][i] * L;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy);
                    using (var pen = new Pen(colors[i], 2f) { EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor })
                    {
                        if (len > 3) g.DrawLine(pen, o, new PointF(o.X + dx, o.Y + dy));
                        else { pen.EndCap = System.Drawing.Drawing2D.LineCap.Flat; g.DrawEllipse(pen, o.X - 4, o.Y - 4, 8, 8); }   // along the view direction
                    }
                    float lx = len > 3 ? o.X + dx * (L + 7) / len : o.X + 10, ly = len > 3 ? o.Y + dy * (L + 7) / len : o.Y + 8;
                    lx = Math.Max(at.X + 1, Math.Min(at.X + T - 10, lx - 4));   // the letters stay inside the box
                    ly = Math.Max(at.Y + 1, Math.Min(at.Y + T - 13, ly - 6));
                    using (var brush = new SolidBrush(colors[i]))
                        g.DrawString(names[i], font, brush, lx, ly);
                }
            }
        }
    }
}
