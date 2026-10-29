using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: reading a NATIVE drawing (check_drawing) — views, dimensions with their
    // anchors and attached primitives, section metadata, the view's model frame, center marks and centerlines.
    public partial class SolidWorksService
    {
        // check_drawing (was analyze_slddrw_test until 2026-10-21, analyze_drawing until 2026-07-27) — read the ACTIVE NATIVE .SLDDRW
        // structurally. TEST/REFERENCE ONLY: the shipping reverse path is moving to a DXF/DWG reader;
        // the fields below (measures/section/normal_axis/relations/extent) are COM constructs a DXF lacks.:
        // every view's {name, type, scale, pos} + its dimensions {name, value_si}. Read-only, does NOT bump
        // state_version (same pattern as AnalyzeModel/VerifyState). Feeds the objective Stage-1 check (do the
        // drawing's dims match the model?) and the Stage-2 re-modeling input. Packs one JSON string into
        // Features (the field the adapter's response_mapper surfaces). NOTE: GetFirstView() returns the SHEET
        // as the first view; it is reported too (type int distinguishes it) — interpret view[0] as the sheet.
        public ExecutionResponse CheckDrawing(ToolRequest request) => Run(request, false, p =>
        {
            var drawingDoc = ActiveDrawingOrFail(out var modelDoc);

            var pAd = p;
            bool includeGeometry = pAd?.Value<bool?>("include_geometry") ?? false;
            // Deterministic relations + cross-view station join keys. Relations are computed
            // FROM the geometry read, so the flag forces geometry on. All these blocks are ADDITIVE —
            // the raw geometry arrays are never replaced or altered by them.
            bool includeRelations = pAd?.Value<bool?>("include_relations") ?? false;
            if (includeRelations) includeGeometry = true;

            // A freshly-opened drawing has not computed its view DISPLAY geometry yet, so GetPolylines7
            // returns empty (per-view UpdateViewDisplayGeometry alone is insufficient on a cold doc that
            // has never been displayed this session). Force a rebuild AND a redraw/zoom so the views
            // actually render and the projected edges exist before we read them.
            if (includeGeometry)
            {
                try { modelDoc.ForceRebuild3(false); } catch { }
                try { modelDoc.EditRebuild3(); } catch { }
                try { modelDoc.GraphicsRedraw2(); } catch { }
                try { modelDoc.ViewZoomtofit2(); } catch { }
            }

            var root = new JObject();
            var viewsArr = new JArray();
            int totalDims = 0;
            var relSkippedVids = new JArray();                // views excluded from relations (reported)

            foreach (var view in DrawingViews(drawingDoc, withSheet: true))   // views[0] = the sheet container
            {
                var vj = new JObject();
                string vid = "v" + viewsArr.Count;   // views[] index — the contract's view alias
                vj["name"] = view.Name;
                int vType = -1;
                try { vType = view.Type; vj["type"] = vType; } catch { }   // swDrawingViewTypes_e (raw int)
                try { vj["scale"] = R6(view.ScaleDecimal); } catch { }
                try
                {
                    var pos = view.Position as double[];
                    if (pos != null && pos.Length >= 2)
                        vj["pos"] = new JArray { R6(pos[0]), R6(pos[1]) };
                }
                catch { }
                if (includeRelations) vj["vid"] = vid;

                // Geometry first: the relation/measure blocks and the dimension
                // attachment both consume the SAME extraction — one read, no second GetPolylines7 pass.
                ViewGeomData geomData = null;
                double[] frO = null, frX = null, frY = null;
                if (includeGeometry)
                {
                    geomData = ExtractViewGeometry(view);
                    vj["geometry"] = BuildGeometryJson(geomData, view);
                }
                bool relationsHere = includeRelations && vType != 1
                    && geomData != null && geomData.Err == null;
                if (relationsHere)
                {
                    if (ViewModelFrameRaw(view, out frO, out frX, out frY))
                    {
                        var rel = ComputeViewRelations(geomData);
                        if (rel != null) vj["relations"] = rel;
                        var cm = ReadCenterMarks(view, frO, frX, frY, geomData);
                        if (cm != null && cm.Count > 0) vj["center_marks"] = cm;
                        var cl = ReadCenterlines(view, geomData);
                        if (cl != null && cl.Count > 0) vj["centerlines"] = cl;
                    }
                    else
                    {
                        // No usable view→model transform ⇒ this view cannot join relations/stations.
                        // Recorded loudly instead of silently thinning the output.
                        relSkippedVids.Add(vid);
                        relationsHere = false;
                    }
                }
                var dimsArr = ReadViewDimensions(view, relationsHere ? geomData : null, frO, frX, frY);
                totalDims += dimsArr.Count;
                vj["dimensions"] = dimsArr;
                // Section views also carry cutting-plane metadata (parent view +
                // model-space normal/frame) so section-coordinate → 3D mapping is arithmetic.
                var sect = ReadSectionMetadata(view);
                if (sect != null) vj["section"] = sect;
                viewsArr.Add(vj);
            }

            root["view_count"] = viewsArr.Count;
            root["dimension_count"] = totalDims;
            root["views"] = viewsArr;
            if (includeRelations)
            {
                // stations table DROPPED (2026-07-19 trim) — the benchmark data-consumption report
                // showed it unconsumed (the model cross-references views by dimension, not this table).
                if (relSkippedVids.Count > 0) root["relations_skipped_views"] = relSkippedVids;
            }

            // Read-only — does NOT bump state_version (mirrors AnalyzeModel).
            return new ToolOutput { Features = new List<string> { root.ToString(Newtonsoft.Json.Formatting.None) } };
        });

        // Per-view display dimensions, reusing the same IDisplayDimension→IDimension reader shape as the
        // feature-side ReadDisplayDimensions (FullName + SI value). View-level iteration uses
        // GetFirstDisplayDimension5()/GetNext5() (the IView/IDisplayDimension API). Best-effort + guarded.
        //
        // DIMENSION→GEOMETRY MAPPING: each dim also reports WHAT it measures, as DATA
        // (never inference — the "what is this 17mm the dimension of?" question). Added fields:
        //   diametric  — true when the display shows it as a Ø (IDisplayDimension.Diametric) — kills "is 17 the
        //                diameter?" (this IS reliable; IDimension.GetType() is NOT — it comes back Unknown(0)
        //                for most model-inserted drawing dims and mis-labels a chamfer angle, so it is not used).
        //   attached   — the KIND(s) of geometry the dimension is attached to (swSelectType_e: edge/vertex/
        //                sketch_seg/...), read from the underlying IAnnotation's attached-entity types.
        //   anchors    — the dimension's REFERENCE POINTS in MODEL space (meters) for a model dimension — the
        //                concrete 3D geometry it snaps to (live-proven: the Ø17 bore's anchor is [0,0,0.0651],
        //                on the axis at the top plane), so a reader maps the value to a 3D location arithmetically.
        // All reflection-verified against SolidWorks.Interop.sldworks (IAnnotation.GetAttachedEntityTypes,
        // IDimension.GetReferencePointsCount/IGetReferencePoints→IMathPoint.ArrayData).
        //
        // DIMENSION → PRIMITIVE ATTACHMENT: when the view's extracted geometry + model frame are
        // handed in (include_relations mode), each dimension additionally names the primitive id(s) it
        // measures — `measures` — resolved ARITHMETICALLY from its anchors: anchor (model 3D) → view 2D via
        // the frame (u=(p−o)·xdir, v=(p−o)·ydir), then point-vs-primitive distance ≤ RelTol. Sources:
        // anchor_at_center (anchor sits on a circle/arc CENTER — the live-proven Ø-dim shape) or
        // anchor_on_primitive (anchor lies on the primitive's geometry). A dimension whose anchors resolve
        // to NO primitive is flagged `unattached:true` — loud, never dropped, never guessed.
        private JArray ReadViewDimensions(IView view, ViewGeomData geom = null,
            double[] frO = null, double[] frX = null, double[] frY = null)
        {
            var arr = new JArray();
            bool attach = geom != null && frO != null && frX != null && frY != null;
            object dispObj = null;
            try { dispObj = view.GetFirstDisplayDimension5(); } catch { return arr; }
            int guard = 0;
            while (dispObj != null && guard++ < 1000)
            {
                var disp = dispObj as IDisplayDimension;
                if (disp != null)
                {
                    var dim = disp.GetDimension2(0) as IDimension;
                    if (dim != null)
                    {
                        var dj = new JObject();
                        dj["name"] = dim.FullName;
                        // value_si must be SI (meters/radians) for EVERY dim. GetSystemValue3(swThisConfiguration)
                        // is correct for MODEL dims shown in the drawing, but for reference dims CREATED in the
                        // drawing (RD*@Drawing View…) it resolves no configuration and the value falls through in
                        // DOCUMENT units (live on level-2-2: RD4 read 125.864691 "meters" = the 125.86 mm block
                        // width). SystemValue is the configuration-independent SI accessor (the same one
                        // modify_dimension writes through) — prefer it here; drawings have no configurations, so
                        // nothing is lost. GetSystemValue3 stays as the fallback, raw Value as the last resort.
                        double val = dim.Value;
                        bool gotSi = false;
                        try { val = dim.SystemValue; gotSi = true; } catch { }
                        if (!gotSi)
                        {
                            try
                            {
                                var sv = dim.GetSystemValue3(1, null) as double[]; // 1 = swThisConfiguration (inlined)
                                if (sv != null && sv.Length > 0) val = sv[0];
                            }
                            catch { }
                        }
                        dj["value_si"] = R6(val);
                        if (IsAngular(dim)) dj["angular"] = true;
                        else if (IsCount(dim)) dj["count"] = true;

                        // --- Ø flag (the reliable dimension-kind signal) ---
                        try { if (disp.Diametric) dj["diametric"] = true; } catch { }

                        // --- attached-entity KIND(s): what geometry this dim hangs off ---
                        try
                        {
                            var ann = disp.GetAnnotation() as IAnnotation;
                            if (ann != null)
                            {
                                object rawTypes = null;
                                try { rawTypes = ann.GetAttachedEntityTypes(); } catch { }
                                var kinds = new JArray();
                                var seenKinds = new HashSet<string>();
                                foreach (int et in ToInts(rawTypes))
                                {
                                    string kn = AttachEntityKindName(et);
                                    if (seenKinds.Add(kn)) kinds.Add(kn);
                                }
                                if (kinds.Count > 0) dj["attached"] = kinds;
                            }
                        }
                        catch { }

                        // --- ANCHOR points (sheet space, meters): the concrete geometry the dim references ---
                        JArray anchorsArr = null;
                        try
                        {
                            anchorsArr = ReadDimensionAnchors(dim);
                            if (anchorsArr != null && anchorsArr.Count > 0) dj["anchors"] = anchorsArr;
                        }
                        catch { }

                        // --- Name the primitive(s) this dimension measures (or flag it loudly) ---
                        if (attach)
                        {
                            try { AttachDimensionToPrimitives(dj, anchorsArr, geom, frO, frX, frY); }
                            catch { }
                        }

                        arr.Add(dj);
                    }
                }
                object next = null;
                try { next = disp != null ? disp.GetNext5() : null; } catch { next = null; }
                dispObj = next;
            }
            return arr;
        }

        // The dimension's reference points (the points it snaps to) in MODEL space (meters) for a model
        // dimension. IDimension exposes GetReferencePointsCount() + IGetReferencePoints(i)→IMathPoint
        // (ArrayData = [x,y,z]); the aggregate get_ReferencePoints() returns an array of the same MathPoints.
        // We prefer the indexed accessor and fall back to the aggregate. Rounded to 6 decimals (1µm). Identical
        // points are de-duped: model-inserted drawing dims frequently report both reference points at the same
        // spot (the attach location), so a lone point carries the same information without the duplicate noise.
        private JArray ReadDimensionAnchors(IDimension dim)
        {
            var raw = new JArray();
            int n = 0;
            try { n = dim.GetReferencePointsCount(); } catch { n = 0; }
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    IMathPoint mp = null;
                    try { mp = dim.IGetReferencePoints(i) as IMathPoint; } catch { mp = null; }
                    var pj = MathPointJson(mp);
                    if (pj != null) raw.Add(pj);
                }
            }
            if (raw.Count == 0)
            {
                // Fallback: the aggregate accessor (array of MathPoint COM objects).
                try
                {
                    var agg = dim.ReferencePoints as object[];
                    if (agg != null)
                        foreach (var o in agg)
                        {
                            var pj = MathPointJson(o as IMathPoint);
                            if (pj != null) raw.Add(pj);
                        }
                }
                catch { }
            }
            // De-dup identical consecutive points.
            var pts = new JArray();
            foreach (var t in raw)
            {
                bool dup = false;
                foreach (var u in pts)
                    if (JToken.DeepEquals(t, u)) { dup = true; break; }
                if (!dup) pts.Add(t);
            }
            return pts;
        }

        private static JArray MathPointJson(IMathPoint mp)
        {
            if (mp == null) return null;
            try
            {
                var d = mp.ArrayData as double[];
                if (d != null && d.Length >= 3)
                    return new JArray { R6(d[0]), R6(d[1]), R6(d[2]) };
            }
            catch { }
            return null;
        }

        // swSelectType_e (subset that a drawing dimension attaches to) → label. Inlined ints.
        private static string AttachEntityKindName(int t)
        {
            switch (t)
            {
                case 1: return "edge";
                case 2: return "face";
                case 3: return "vertex";
                case 9: return "sketch";
                case 10: return "sketch_seg";
                case 11: return "sketch_point";
                default: return "sel_" + t;
            }
        }

        // SECTION VIEW cutting-plane metadata. For a section view (Type==2 =
        // swDrawingSectionView, or one that owns an IDrSection) report, as DATA:
        //   parent_view  — the view the section was cut from (IView.GetBaseView, fallback IDrSection.GetView)
        //   cut_normal   — the cutting-plane NORMAL in MODEL space (= the section view's viewing direction):
        //                  the section view's view→model transform maps view-Z to this. Read exactly as
        //                  ReadSketchPlane reads a sketch normal (inverse transform, columns d[2],d[5],d[8]),
        //                  so the sign convention matches the part-side readers. Normalized.
        //   axis         — cut_normal snapped to a principal axis ("X"/"Y"/"Z") when axis-aligned — the direct
        //                  answer to "which axis is A-A perpendicular to?" (else null → read cut_normal).
        //   frame        — the section view's model-space frame {origin, xdir, ydir} (view→model), so a caller
        //                  maps a 2D section-view coordinate to 3D: p_model = origin + u*xdir + v*ydir.
        //   label        — the section label (A, B, ...) when available from the IDrSection.
        // All reflection-verified: IView.Type / GetBaseView / ModelToViewTransform / GetSection→IDrSection.
        // Best-effort — any failure just omits the affected field; a non-section view returns null.
        private JObject ReadSectionMetadata(IView view)
        {
            IDrSection section = null;
            try { section = view.GetSection() as IDrSection; } catch { section = null; }
            int vtype = -1;
            try { vtype = view.Type; } catch { }
            // Gate: swDrawingSectionView (2) OR an owning IDrSection is present.
            if (vtype != 2 && section == null) return null;

            var sj = new JObject();

            // Parent view — the view the cut was taken in.
            try
            {
                var baseView = view.GetBaseView() as IView;
                if (baseView != null) sj["parent_view"] = baseView.Name;
            }
            catch { }
            if (sj["parent_view"] == null && section != null)
            {
                try { var pv = section.GetView() as IView; if (pv != null) sj["parent_view"] = pv.Name; }
                catch { }
            }

            // Section label (A / B / ...).
            if (section != null)
            {
                try { var lbl = section.GetLabel(); if (!string.IsNullOrEmpty(lbl)) sj["label"] = lbl; }
                catch { }
                try { if (section.IsAligned()) sj["aligned"] = true; } catch { }
            }

            // Cutting-plane normal + model-space frame, from the section view's view→model transform.
            try
            {
                var m2v = view.ModelToViewTransform;   // model → view
                var v2m = m2v != null ? m2v.IInverse() : null;   // view → model
                double[] d = v2m != null ? v2m.ArrayData as double[] : null;
                if (d != null && d.Length >= 12)
                {
                    // View Z in model space = the THIRD ROW (SolidWorks transforms row vectors; xdir / ydir are rows 1–2,
                    // as in the frame). The third column used before equals it only for the front view: a horizontal
                    // cut gave cut_normal −Y beside frame.normal_axis Y in the same answer (box.SLDDRW, 2026-10-26).
                    double nx = d[6], ny = d[7], nz = d[8];
                    double nlen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nlen > 1e-9) { nx /= nlen; ny /= nlen; nz /= nlen; }
                    sj["cut_normal"] = new JArray { R6(nx), R6(ny), R6(nz) };
                    string ax = PrincipalAxis(nx, ny, nz);
                    if (ax != null) sj["axis"] = ax;
                }
                // Frame origin: the raw v2m translation (view 0,0,0 → model) is NOT the reference the
                // view's projected geometry uses (live on level-2-2 Section C-C it landed half a meter
                // off the part), so the frame is computed by ViewModelFrame — origin = the model-space
                // point of the view geometry's (0,0) — and shared with the geometry read.
                var fr = ViewModelFrame(view);
                if (fr != null) sj["frame"] = fr;
            }
            catch { }

            return sj.Count > 0 ? sj : null;
        }

        // The model-space frame of a drawing view's PROJECTED 2D geometry: p_model = origin + u*xdir +
        // v*ydir for a (u,v) from ReadViewGeometry (model-scale meters, centered on the view anchor).
        // xdir/ydir = the view→model rotation columns (same column convention as ReadSketchPlane, proven
        // on the section axis classification). origin = the view's sheet-space anchor (IView.Position)
        // mapped through the view→model transform via IMathUtility (MultiplyTransform applies the
        // transform's scale component, so the 1:viewscale sheet→model scaling is handled) — the raw v2m
        // translation is the model point of SHEET (0,0), not of the view anchor, which is why it read
        // half a meter off the part. Verified live on level-2-2 (block corners / bore heights map exactly).
        private JObject ViewModelFrame(IView view)
        {
            double[] o, x, y;
            if (!ViewModelFrameRaw(view, out o, out x, out y)) return null;
            var fj = new JObject
            {
                ["origin"] = new JArray { R6(o[0]), R6(o[1]), R6(o[2]) },
                ["xdir"] = new JArray { R6(x[0]), R6(x[1]), R6(x[2]) },
                ["ydir"] = new JArray { R6(y[0]), R6(y[1]), R6(y[2]) },
            };
            // The view's NORMAL snapped to a signed principal axis
            // ("Y" / "-Z" ...) — the deterministic restatement of "which way does this view look".
            // Drafting semantics the recipe builds on it: a CIRCLE in this view is the cross-section
            // of a feature whose axis runs along this normal, so circles in different-normal views
            // are DIFFERENT features unless a section proves otherwise (the 2026-07-17 benchmark
            // model conflated a top-view Y-axis stepped hole with the front-view Z-axis loft chain).
            // Computed on the UNROUNDED frame vectors; omitted (loudly absent) when not axis-aligned.
            double nx = x[1] * y[2] - x[2] * y[1];
            double ny = x[2] * y[0] - x[0] * y[2];
            double nz = x[0] * y[1] - x[1] * y[0];
            string ax = PrincipalAxis(nx, ny, nz);
            if (ax != null)
            {
                double comp = ax == "X" ? nx : (ax == "Y" ? ny : nz);
                fj["normal_axis"] = (comp < 0 ? "-" : "") + ax;
            }
            return fj;
        }

        // Raw (unrounded) frame vectors — the single transform-reading path ViewModelFrame wraps. The
        // The relation/station/attachment arithmetic runs on these raw doubles and rounds only at
        // JSON-emit time, so residuals report the true measured deviation.
        private bool ViewModelFrameRaw(IView view, out double[] origin, out double[] xdir, out double[] ydir)
        {
            origin = null; xdir = null; ydir = null;
            try
            {
                var m2v = view.ModelToViewTransform;
                var v2m = m2v != null ? m2v.IInverse() : null;
                double[] d = v2m != null ? v2m.ArrayData as double[] : null;
                if (d == null || d.Length < 12) return false;
                var pos = view.Position as double[];
                if (pos == null || pos.Length < 2) return false;
                var mu = _solidWorks.GetMathUtility() as IMathUtility;
                var pt = mu != null ? mu.CreatePoint(new double[] { pos[0], pos[1], 0.0 }) as IMathPoint : null;
                var mapped = pt != null ? pt.MultiplyTransform(v2m) as IMathPoint : null;
                double[] o = mapped != null ? mapped.ArrayData as double[] : null;
                if (o == null || o.Length < 3) return false;
                origin = new double[] { o[0], o[1], o[2] };
                xdir = new double[] { d[0], d[1], d[2] };
                ydir = new double[] { d[3], d[4], d[5] };
                return true;
            }
            catch { return false; }
        }


        // ---- Center marks ----
        // ICenterMark walk (GetFirstCenterMark/GetNext — reflection-verified). Position source PINNED by
        // the 2026-07-17 live A/B on level-2-2: `ICenterMark.GetPosition(index)`
        // returns the mark center directly in the VIEW-2D geometry space (its x,y matched the circle
        // centers exactly — "matched via view-space" for every mark), while `IAnnotation.GetPosition()`
        // is the sheet-space LABEL anchor (y ≈ 0.25 on the A3 sheet — never a mark center) and is
        // therefore NOT read. A mark whose position matches no circle center within RelTol is emitted
        // `unmatched: true` — loud, never guessed.
        private JArray ReadCenterMarks(IView view, double[] O, double[] X, double[] Y, ViewGeomData g)
        {
            var arrOut = new JArray();
            object cmObj = null;
            try { cmObj = view.GetFirstCenterMark(); } catch { return arrOut; }
            int guard = 0;
            while (cmObj != null && guard++ < 500)
            {
                var cm = cmObj as ICenterMark;
                if (cm == null) break;

                var rawPts = new List<double[]>();
                try
                {
                    bool grouped = false; int gc = 1;
                    try { grouped = cm.IsGrouped; } catch { }
                    try { if (grouped) gc = cm.GroupCount; } catch { gc = 1; }
                    for (int i = 0; i < Math.Max(1, gc) && i < 100; i++)
                    {
                        var p = ToDoubles(cm.GetPosition(i));
                        if (p.Length >= 2) rawPts.Add(new double[] { p[0], p[1] });
                    }
                }
                catch { }

                foreach (var raw in rawPts)
                {
                    double u = raw[0], v = raw[1];
                    var on = new List<string>();
                    double res = 0;
                    for (int i = 0; i < g.Circles.Count; i++)
                    {
                        double dc = Dist2D(u, v, g.Circles[i].CX, g.Circles[i].CY);
                        if (dc <= RelTol) { on.Add("c" + i); res = Math.Max(res, dc); }
                    }
                    if (on.Count > 0)
                    {
                        SortPrimitiveIds(on);
                        arrOut.Add(new JObject
                        {
                            ["at"] = new JArray { R6(u), R6(v) },
                            ["on"] = new JArray(on.ToArray()),
                            ["source"] = "center_coincide",
                            ["residual"] = R6(res)
                        });
                    }
                    else
                    {
                        ExecLog.Write($"center_mark UNMATCHED at=({R6(u)},{R6(v)}) view='{view.Name}'");
                        arrOut.Add(new JObject
                        {
                            ["at"] = new JArray { R6(u), R6(v) },
                            ["unmatched"] = true
                        });
                    }
                }

                object next = null;
                try { next = cm.GetNext(); } catch { next = null; }
                cmObj = next;
            }
            return arrOut;
        }

        // ---- Centerlines (best-effort per the API's shape) ----
        // ICenterLine itself exposes NO endpoints (only GetAnnotation — a single point), so the
        // segment geometry is read from IView.GetCenterLineSketch()'s sketch segments, presumed to be
        // in the view's sketch space (= the geometry space; live A/B decides — raw values ExecLog'd).
        // When the view reports centerlines but the sketch route yields nothing readable, the caller
        // emits `centerlines_unreadable` — the gap stays loud instead of heuristically inferred.
        private JArray ReadCenterlines(IView view, ViewGeomData g)
        {
            var arrOut = new JArray();
            int reported = 0;
            try { reported = view.GetCenterLineCount(); } catch { }
            try
            {
                var sk = view.GetCenterLineSketch() as ISketch;
                if (sk != null)
                {
                    var segs = sk.GetSketchSegments() as object[];
                    if (segs != null)
                    {
                        foreach (var so in segs)
                        {
                            var seg = so as ISketchSegment;
                            if (seg == null) continue;
                            int st = -1;
                            try { st = seg.GetType(); } catch { }
                            if (st != 0) continue;   // swSketchLINE = 0 (inlined)
                            var ln = seg as ISketchLine;
                            if (ln == null) continue;
                            ISketchPoint sp = null, ep = null;
                            try { sp = ln.IGetStartPoint2(); ep = ln.IGetEndPoint2(); } catch { }
                            if (sp == null || ep == null) continue;
                            double x1 = sp.X, y1 = sp.Y, x2 = ep.X, y2 = ep.Y;
                            ExecLog.Write($"centerline raw=({x1:R},{y1:R})-({x2:R},{y2:R}) view='{view.Name}'");
                            var cj = new JObject
                            {
                                ["x1"] = R6(x1), ["y1"] = R6(y1),
                                ["x2"] = R6(x2), ["y2"] = R6(y2)
                            };
                            // Circles whose center lies ON this centerline (arithmetic, RelTol).
                            var through = new List<string>();
                            double worst = 0;
                            for (int i = 0; i < g.Circles.Count; i++)
                            {
                                double tRaw;
                                double d = SegDist(g.Circles[i].CX, g.Circles[i].CY, x1, y1, x2, y2, out tRaw);
                                if (d <= RelTol) { through.Add("c" + i); worst = Math.Max(worst, d); }
                            }
                            if (through.Count > 0)
                            {
                                SortPrimitiveIds(through);
                                cj["through"] = new JArray(through.ToArray());
                                cj["source"] = "center_coincide";
                                cj["residual"] = R6(worst);
                            }
                            arrOut.Add(cj);
                        }
                    }
                }
            }
            catch (Exception ex) { ExecLog.Write($"ReadCenterlines err: {ex.Message}"); }

            if (arrOut.Count == 0 && reported > 0)
            {
                // Loud gap: the view SAYS it has centerlines but the sketch route exposed none.
                var gap = new JObject { ["centerlines_reported"] = reported, ["unreadable"] = true };
                arrOut.Add(gap);
            }
            return arrOut;
        }
    }
}
