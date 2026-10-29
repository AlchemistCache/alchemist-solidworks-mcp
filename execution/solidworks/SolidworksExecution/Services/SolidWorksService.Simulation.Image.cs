using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.cosworks;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // sim_image: our own result map. The hidden own SolidWorks instance does not render Simulation plots into
    // SaveAs/SaveBMP, so the picture is drawn here from the solver data: mesh nodes (ICWMesh.GetNodes) + per-node
    // results (ICWResults.GetStress / GetTranslationalDisplacement), projected for the chosen view, z-buffered
    // colour splats (SolidWorks-like blue→red), legend, max marker, optional deformed shape.
    public partial class SolidWorksService
    {
        private static readonly Dictionary<string, double[][]> SimViews = new Dictionary<string, double[][]>
        {
            // { right, up, toward-viewer }
            ["iso"] = new[] { new[] { 1 / Math.Sqrt(2), 0, -1 / Math.Sqrt(2) }, new[] { -1 / Math.Sqrt(6), 2 / Math.Sqrt(6), -1 / Math.Sqrt(6) }, new[] { 1 / Math.Sqrt(3), 1 / Math.Sqrt(3), 1 / Math.Sqrt(3) } },
            ["front"] = new[] { new[] { 1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 } },
            ["back"] = new[] { new[] { -1.0, 0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, -1.0 } },
            ["top"] = new[] { new[] { 1.0, 0, 0 }, new[] { 0, 0, -1.0 }, new[] { 0, 1.0, 0 } },
            ["bottom"] = new[] { new[] { 1.0, 0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, -1.0, 0 } },
            ["right"] = new[] { new[] { 0, 0, -1.0 }, new[] { 0, 1.0, 0 }, new[] { 1.0, 0, 0 } },
            ["left"] = new[] { new[] { 0, 0, 1.0 }, new[] { 0, 1.0, 0 }, new[] { -1.0, 0, 0 } },
        };

        // Column of a per-node result table whose max equals the solver's reported max (robust to layout).
        private static int MatchColumn(double[] flat, int stride, double targetMax, int skip)
        {
            int best = -1; double bestErr = double.MaxValue;
            for (int c = skip; c < stride; c++)
            {
                double mx = double.MinValue;
                for (int i = c; i < flat.Length; i += stride) mx = Math.Max(mx, flat[i]);
                double err = Math.Abs(mx - targetMax) / Math.Max(1e-12, Math.Abs(targetMax));
                if (err < bestErr) { bestErr = err; best = c; }
            }
            return bestErr < 1e-3 ? best : -1;
        }

        private static Color MapColor(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            // blue → cyan → green → yellow → red
            double[][] stops = { new[] { 0.0, 0, 0, 255 }, new[] { 0.25, 0, 255, 255 }, new[] { 0.5, 0, 255, 0 }, new[] { 0.75, 255, 255, 0 }, new[] { 1.0, 255, 0, 0 } };
            for (int i = 1; i < stops.Length; i++)
                if (t <= stops[i][0])
                {
                    double u = (t - stops[i - 1][0]) / (stops[i][0] - stops[i - 1][0]);
                    return Color.FromArgb((int)(stops[i - 1][1] + u * (stops[i][1] - stops[i - 1][1])),
                                          (int)(stops[i - 1][2] + u * (stops[i][2] - stops[i - 1][2])),
                                          (int)(stops[i - 1][3] + u * (stops[i][3] - stops[i - 1][3])));
                }
            return Color.Red;
        }


        public ExecutionResponse SimImage(ToolRequest request) => Run(request, false, p =>
        {
            var study = Study(StudyMgr(), PStr(p, "study"));
            var r = study.Results as ICWResults ?? throw new ToolFail("NO_RESULTS", "No results — sim_run first.");
            var mesh = study.Mesh as ICWMesh ?? throw new ToolFail("NO_MESH", "No mesh.");
            string kind = PStr(p, "result", "stress").ToLowerInvariant();
            string view = PStr(p, "view", "iso").ToLowerInvariant();
            if (!SimViews.TryGetValue(view, out var V)) throw new ToolFail("BAD_PARAMETER", "view: " + string.Join(" | ", SimViews.Keys));
            int W = Math.Max(300, Math.Min(2000, PInt(p, "width", 640))), H = (int)(W * 0.72);

            var (idx, P) = MeshNodesMm(mesh);
            int n = P.GetLength(0);
            var f = NodeResults(r, kind, idx, n);
            var P0 = (double[,])P.Clone();   // where the peak IS: the undeformed node, not its exaggerated picture
            double scale = DeformNodes(r, P, idx, PNum(p, "deform_scale", 0));
            double clip = PNum(p, "clip_percentile", 100);

            string outPath = PStr(p, "file_path") ?? Path.Combine(Path.GetTempPath(), "SolidWorksMCP", "img",
                SafeName(study.Name) + "_" + kind + "_" + DateTime.Now.ToString("HHmmssfff") + ".png");
            EnsureFolderFor(outPath);
            string caption = $"{ActiveDocOrFail().GetTitle()} · {study.Name} · {f.Title} ({f.Unit}){(scale > 0 ? $" · deformed ×{scale:0.##}" : "")}" +
                             $"{(clip > 0 && clip < 100 ? $" · scale to P{clip:0.#}, red ≥ top" : "")} · {view}";
            var map = RenderResultMap(outPath, W, H, P, V, f, clip, caption);
            ExecLog.Write($"sim_image: {n} nodes, splat {map.Rad}px, {kind} {map.Vmin:0.###}..{map.Vmax:0.###} {f.Unit}");
            return new JObject
            {
                ["image"] = outPath, ["study"] = study.Name, ["result"] = kind, ["unit"] = f.Unit, ["view"] = view,
                ["min"] = Math.Round(map.Vmin, 5), ["max"] = Math.Round(map.Vmax, 5), ["reported_max"] = Math.Round(f.ReportedMax, 5),
                ["max_at_mm"] = map.MaxNode >= 0 ? R(new[] { P0[map.MaxNode, 0], P0[map.MaxNode, 1], P0[map.MaxNode, 2] }, 1) : null,
                ["nodes"] = n
            };
        });

        // The mesh nodes ([id, x, y, z] … meters) as node id → row, and the coordinates in mm.
        private static (Dictionary<int, int> idx, double[,] P) MeshNodesMm(ICWMesh mesh)
        {
            var nodesRaw = ToDoubles(mesh.GetNodes());
            int n = nodesRaw.Length / 4;
            var idx = new Dictionary<int, int>();
            var P = new double[n, 3];
            for (int i = 0; i < n; i++) { idx[(int)nodesRaw[i * 4]] = i; for (int k = 0; k < 3; k++) P[i, k] = nodesRaw[i * 4 + 1 + k] * MM; }
            return (idx, P);
        }

        // One value per mesh node and how it is labelled.
        private sealed class NodeField
        {
            public double[] Val;
            public bool[] Has;
            public string Unit, Title;
            public double ReportedMax;
        }

        // von Mises stress (MPa) or the resultant displacement URES (mm) per node. The per-node tables' layout is not
        // documented: the value column is the one whose maximum equals the solver's reported maximum (MatchColumn).
        private static NodeField NodeResults(ICWResults r, string kind, Dictionary<int, int> idx, int n)
        {
            var f = new NodeField { Val = new double[n], Has = new bool[n] };
            int err;
            if (kind == "stress")
            {
                var mm = ToDoubles(r.GetMinMaxStress((int)swsStressComponent_e.swsStressComponentVON, 0, 1, null, 0, out err));
                f.ReportedMax = mm.Length >= 4 ? mm[3] : 0;
                var all = ToDoubles(r.GetStress(0, 1, null, 0, out err));
                int stride = -1, col = -1;
                foreach (int s in new[] { all.Length / Math.Max(1, n), 13, 12, 11, 14, 15, 10 }.Distinct())
                {
                    if (s < 2 || all.Length % s != 0) continue;
                    col = MatchColumn(all, s, f.ReportedMax, 1);
                    if (col >= 0) { stride = s; break; }
                }
                if (col < 0 || all.Where((v, i) => i % Math.Max(stride, 1) != 0).All(v => v == 0))
                    // All-zero results = the loads did not act (e.g. a force on a sheet's thin side face, which is not
                    // carried to the shell mid-surface). NB: GetStressForEntities3 crashed SW 2024 — do not use it.
                    throw new ToolFail("ZERO_RESULTS",
                        "All stresses are zero — the loads did not act. On sheet metal load the large sheet faces, not the thickness faces; check sim_results.");
                ExecLog.Write($"sim_image: GetStress len={all.Length} nodes={n} stride={stride} vonMises col={col} first={string.Join(",", all.Take(Math.Min(30, all.Length)).Select(x => x.ToString("G4")))}");
                for (int i = 0; i + stride <= all.Length; i += stride)
                    if (idx.TryGetValue((int)all[i], out int j)) { f.Val[j] = all[i + col] * 1e-6; f.Has[j] = true; }
                f.ReportedMax *= 1e-6; f.Unit = "MPa"; f.Title = "von Mises";
            }
            else if (kind == "displacement")
            {
                var mm = ToDoubles(r.GetMinMaxDisplacement((int)swsDisplacementComponent_e.swsDisplacementComponentURES, 1, null, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out err));
                f.ReportedMax = mm.Length >= 4 ? mm[3] : 0;
                var all = ToDoubles(r.GetTranslationalDisplacement(1, null, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out err));
                int stride = 5;
                int col = MatchColumn(all, stride, f.ReportedMax, 1);
                if (col < 0) { stride = all.Length / Math.Max(1, n); col = MatchColumn(all, stride, f.ReportedMax, 1); }
                if (col < 0) throw new ToolFail("RESULT_READ_FAILED", $"Could not locate URES in GetTranslationalDisplacement ({all.Length} values, {n} nodes).");
                for (int i = 0; i + stride <= all.Length; i += stride)
                    if (idx.TryGetValue((int)all[i], out int j)) { f.Val[j] = all[i + col]; f.Has[j] = true; }
                f.Unit = "mm"; f.Title = "Displacement URES";
            }
            else throw new ToolFail("BAD_PARAMETER", "result must be stress or displacement.");
            return f;
        }

        // The deformed shape: node coordinates moved by the displacements (mm) × scale; a negative scale = auto, the
        // largest displacement shown as ~8 % of the model's diagonal. Returns the scale used (0 = undeformed).
        private static double DeformNodes(ICWResults r, double[,] P, Dictionary<int, int> idx, double scale)
        {
            if (scale == 0) return 0;
            int err, n = P.GetLength(0);
            var d = ToDoubles(r.GetTranslationalDisplacement(1, null, (int)swsLinearUnit_e.swsLinearUnitMillimeters, out err));
            if (scale < 0)
            {
                double dmax = 0; for (int i = 0; i + 4 < d.Length; i += 5) dmax = Math.Max(dmax, d[i + 4]);
                double diag = 0; for (int k = 0; k < 3; k++) { double lo = double.MaxValue, hi = double.MinValue; for (int i = 0; i < n; i++) { lo = Math.Min(lo, P[i, k]); hi = Math.Max(hi, P[i, k]); } diag += (hi - lo) * (hi - lo); }
                scale = dmax > 0 ? Math.Round(0.08 * Math.Sqrt(diag) / dmax, 3) : 0;
            }
            for (int i = 0; i + 4 < d.Length; i += 5)
                if (idx.TryGetValue((int)d[i], out int j)) for (int k = 0; k < 3; k++) P[j, k] += d[i + 1 + k] * scale;
            return scale;
        }

        // The picture: nodes projected for the view, z-buffered colour splats (far → near), pin-holes closed, then the
        // caption, the legend and a max marker. Returns the colour range shown, the max node and the splat radius.
        private static (double Vmin, double Vmax, int MaxNode, int Rad) RenderResultMap(string outPath, int W, int H, double[,] P,
                                                                                      double[][] V, NodeField f, double clip, string caption)
        {
            int n = P.GetLength(0);
            double[] val = f.Val;
            bool[] has = f.Has;
            var sx = new double[n]; var sy = new double[n]; var sz = new double[n];
            for (int i = 0; i < n; i++)
            {
                sx[i] = P[i, 0] * V[0][0] + P[i, 1] * V[0][1] + P[i, 2] * V[0][2];
                sy[i] = P[i, 0] * V[1][0] + P[i, 1] * V[1][1] + P[i, 2] * V[1][2];
                sz[i] = P[i, 0] * V[2][0] + P[i, 1] * V[2][1] + P[i, 2] * V[2][2];
            }
            double minX = sx.Min(), maxX = sx.Max(), minY = sy.Min(), maxY = sy.Max();
            int legendW = 150, pad = 30, top = 46;
            double plotW = W - legendW - 2 * pad, plotH = H - top - pad;
            double k2 = Math.Min(plotW / Math.Max(1e-9, maxX - minX), plotH / Math.Max(1e-9, maxY - minY));
            double ox = pad + (plotW - (maxX - minX) * k2) / 2, oy = top + (plotH - (maxY - minY) * k2) / 2;
            double vmin = val.Where((v, i) => has[i]).DefaultIfEmpty(0).Min(), vmax = val.Where((v, i) => has[i]).DefaultIfEmpty(1).Max();
            if (clip > 0 && clip < 100)
            {
                // singular peaks (fixture corners, point loads) wash the map out: cap the legend at a percentile
                var sorted = val.Where((v, i) => has[i]).OrderBy(v => v).ToArray();
                if (sorted.Length > 0) vmax = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * clip / 100.0))];
            }
            double area = (maxX - minX) * (maxY - minY) * k2 * k2;
            // node count includes interior (solid) nodes, so spacing on the surface is larger than sqrt(area/n)
            int rad = (int)Math.Max(2, Math.Min(8, Math.Round(Math.Sqrt(area / Math.Max(1, n)) * 1.5)));

            // z-buffered splats, far → near
            var zbuf = new double[W * H]; for (int i = 0; i < zbuf.Length; i++) zbuf[i] = double.MinValue;
            var px = new int[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFFFFFFFF);
            int maxNode = -1; double maxV = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (!has[i]) continue;
                if (val[i] > maxV) { maxV = val[i]; maxNode = i; }
                int cx = (int)(ox + (sx[i] - minX) * k2), cy = (int)(oy + (maxY - sy[i]) * k2);
                int c = MapColor((val[i] - vmin) / Math.Max(1e-12, vmax - vmin)).ToArgb();
                for (int dy = -rad; dy <= rad; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        if (dx * dx + dy * dy > rad * rad) continue;
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= W || y >= H) continue;
                        int o = y * W + x;
                        if (sz[i] > zbuf[o]) { zbuf[o] = sz[i]; px[o] = c; }
                    }
            }

            // close pin-holes: an empty pixel with ≥3 painted 8-neighbours takes their average colour (3 passes)
            int white = unchecked((int)0xFFFFFFFF);
            for (int pass = 0; pass < 3; pass++)
            {
                var copy = (int[])px.Clone();
                for (int y = 1; y < H - 1; y++)
                    for (int x = 1; x < W - 1; x++)
                    {
                        int o = y * W + x; if (copy[o] != white) continue;
                        int cnt = 0, rr = 0, gg = 0, bb = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int c = copy[o + dy * W + dx]; if (c == white) continue;
                                cnt++; rr += (c >> 16) & 255; gg += (c >> 8) & 255; bb += c & 255;
                            }
                        if (cnt >= 3) px[o] = unchecked((int)0xFF000000) | (rr / cnt << 16) | (gg / cnt << 8) | (bb / cnt);
                    }
            }

            using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
                bmp.UnlockBits(data);
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font("Segoe UI", 10f))
                using (var bold = new Font("Segoe UI", 11f, FontStyle.Bold))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    // a long document name (70 characters) pushed "von Mises (MPa)" off the right edge — shorten the name instead
                    string cap = caption;
                    for (int cut = caption.IndexOf(" · "); g.MeasureString(cap, bold).Width > W - 16 && cut > 12; cut -= 4)
                        cap = caption.Substring(0, cut - 4).TrimEnd() + "…" + caption.Substring(caption.IndexOf(" · "));
                    g.DrawString(cap, bold, Brushes.Black, 8, 10);
                    // legend
                    int lx = W - legendW + 20, ly = top + 10, lh = H - top - pad - 30;
                    for (int y = 0; y < lh; y++)
                        using (var pen = new Pen(MapColor(1 - (double)y / lh))) g.DrawLine(pen, lx, ly + y, lx + 22, ly + y);
                    g.DrawRectangle(Pens.Black, lx, ly, 22, lh);
                    for (int t = 0; t <= 6; t++)
                    {
                        double v = vmax - (vmax - vmin) * t / 6.0;
                        int y = ly + lh * t / 6;
                        g.DrawLine(Pens.Black, lx + 22, y, lx + 28, y);
                        g.DrawString(v.ToString(Math.Abs(vmax) >= 100 ? "0.#" : Math.Abs(vmax) >= 1 ? "0.###" : "0.#####"), font, Brushes.Black, lx + 30, y - 8);
                    }
                    // max marker
                    if (maxNode >= 0)
                    {
                        float mx = (float)(ox + (sx[maxNode] - minX) * k2), my = (float)(oy + (maxY - sy[maxNode]) * k2);
                        using (var pen = new Pen(Color.Black, 2f)) { g.DrawEllipse(pen, mx - 9, my - 9, 18, 18); g.DrawLine(pen, mx + 7, my - 7, mx + 22, my - 22); }
                        string lbl = $"max {(Math.Abs(maxV) >= 0.01 || maxV == 0 ? maxV.ToString("0.###") : maxV.ToString("G3"))} {f.Unit}";   // 0.00043 mm must not print as 0
                        g.FillRectangle(new SolidBrush(Color.FromArgb(220, 255, 255, 255)), mx + 22, my - 34, g.MeasureString(lbl, font).Width + 4, 18);
                        g.DrawString(lbl, font, Brushes.Black, mx + 24, my - 34);
                    }
                }
                bmp.Save(outPath, ImageFormat.Png);
            }
            return (vmin, vmax, maxNode, rad);
        }
    }
}
