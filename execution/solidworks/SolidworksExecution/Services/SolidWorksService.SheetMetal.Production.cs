using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== Sheet metal for production =====================
    // sheet_metal_info  — thickness / bend radius / K-factor / cut-list props (flat size, bends) for the active
    //                     part, one component, or EVERY sheet-metal part of the active assembly (deduped by
    //                     file+configuration, with quantities).
    // export_flat_dxf   — flat-pattern DXF straight from the part (IPartDoc.ExportToDWG2, no drawing needed), for
    //                     the active part, one component, or all sheet-metal parts of the assembly.
    public partial class SolidWorksService
    {
        // Sheet thickness (mm) from the SheetMetal definition feature — cheap, no cut-list update.
        private static double? ThicknessMm(IModelDoc2 d)
        {
            foreach (var f in Features(d))
            {
                if (f.GetTypeName2() == "SheetMetal")
                    try { return Math.Round(((ISheetMetalFeatureData)f.GetDefinition()).Thickness * MM, 3); } catch { return null; }
            }
            return null;
        }

        private static bool IsSheetMetalPart(IModelDoc2 d)
        {
            if (d == null || d.GetType() != (int)swDocumentTypes_e.swDocPART) return false;
            foreach (var f in Features(d))
            {
                if (f.GetTypeName2() == "SheetMetal") return true;
            }
            return false;
        }

        // Run `action` on a part in configuration `config`. The part is opened (or the already-loaded
        // component doc reused) and ACTIVATED — ExportToDWG2 silently fails on a background doc — then the
        // previous active document is re-activated and the part's configuration restored; a part that was
        // not open as its own window before is closed again (it stays loaded as a component).
        private T WithPartConfig<T>(string path, string config, Func<IModelDoc2, T> action)
        {
            var prevActive = _solidWorks.IActiveDoc2 as IModelDoc2;
            var existing = _solidWorks.GetOpenDocumentByName(path) as IModelDoc2;
            // a part with its own window, or with unsaved edits, stays open afterwards (IModelDoc2.Visible is false for
            // every document of the hidden instance; closing an edited component's part re-reads it from disk)
            bool wasVisible = existing != null && (existing.GetFirstModelView() != null || existing.GetSaveFlag());
            int e = 0, w = 0;
            var d = _solidWorks.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART,
                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e, ref w) as IModelDoc2;
            if (d == null) throw new ToolFail("OPEN_FAILED", $"Cannot open {path} (err {e}).");
            Activate(d.GetTitle());
            string prev = (d.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
            bool switched = !string.IsNullOrEmpty(config) && config != prev && d.ShowConfiguration2(config);
            try { return action(d); }
            finally
            {
                if (switched) d.ShowConfiguration2(prev);
                if (prevActive == null)
                {
                    if (existing == null) _solidWorks.CloseDoc(d.GetTitle());   // opened only for this call: close it again
                }
                else if (prevActive.GetTitle() != d.GetTitle())
                {
                    if (!wasVisible) _solidWorks.CloseDoc(d.GetTitle());
                    Activate(prevActive.GetTitle());
                }
            }
        }

        // withBends: per-bend list (name, radius, angle); cutList: SW cut-list properties (a manual cut list is updated first).
        private JObject SheetMetalInfoOf(IModelDoc2 d, bool withBends = false, bool cutList = true)
        {
            var res = new JObject { ["document"] = d.GetTitle(), ["config"] = (d.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name };
            IFeature smFeat = null; IFeature bodyFolder = null; int bends = 0;
            var radii = new List<double>(); var bendList = new JArray(); var subTypes = new HashSet<string>();
            foreach (var f in Features(d))
            {
                string t = f.GetTypeName2();
                if (t == "SheetMetal" && smFeat == null) smFeat = f;
                else if (t == "SolidBodyFolder") bodyFolder = f;
                else if (t == "FlatPattern")
                {
                    bends += SubFeatures(f).Count(sf => sf.GetTypeName2() == "UiBend");
                }
                else if (smFeat != null && t != "ProfileFeature" && t != "RefPlane")
                {
                    // the bends of base/edge/miter flanges, hems, sketched bends … are "OneBend"/"SketchBend" sub-features;
                    // a bend may override the sheet's default radius — needed for the "one bend radius" DFM check
                    foreach (var sf in SubFeatures(f))
                    {
                        string st = sf.GetTypeName2();
                        subTypes.Add(st);
                        if (st == "OneBend" || st == "SketchBend")
                        {
                            try
                            {
                                var def = sf.GetDefinition();
                                double? rad = null, ang = null;
                                if (def is IOneBendFeatureData ob) { rad = ob.BendRadius; ang = ob.BendAngle; }
                                else if (def is ISketchedBendFeatureData sb) { rad = sb.BendRadius; ang = sb.BendAngle; }
                                if (rad.HasValue)
                                {
                                    double r = Math.Round(rad.Value * MM, 3);
                                    var b = new JObject { ["name"] = sf.Name, ["feature"] = f.Name, ["radius_mm"] = r, ["angle_deg"] = Math.Round((ang ?? 0) * 180 / Math.PI, 2) };
                                    // a hem is flattened with its own tooling — its (near-zero) radius is not a press-brake radius
                                    if (t == "Hem") b["hem"] = true;
                                    else if (!radii.Contains(r)) radii.Add(r);
                                    bendList.Add(b);
                                }
                            }
                            catch (Exception ex) { ExecLog.Write($"sheet_metal: bend {sf.Name} -> {ex.Message}"); }
                        }
                    }
                }
            }
            if (bends > 0 && radii.Count == 0) ExecLog.Write($"sheet_metal: {d.GetTitle()} no bend radii; sub-feature types: {string.Join(",", subTypes)}");
            if (smFeat == null) { res["sheet_metal"] = false; return res; }
            res["sheet_metal"] = true;
            try
            {
                var sm = smFeat.GetDefinition() as ISheetMetalFeatureData;
                res["thickness_mm"] = Math.Round(sm.Thickness * MM, 3);
                res["bend_radius_mm"] = Math.Round(sm.BendRadius * MM, 3);
                var ba = sm.GetCustomBendAllowance() as ICustomBendAllowance;
                if (ba != null)
                {
                    res["bend_allowance_type"] = ((swBendAllowanceTypes_e)ba.Type).ToString().Replace("swBendAllowance", "");
                    if (ba.Type == (int)swBendAllowanceTypes_e.swBendAllowanceKFactor) res["k_factor"] = Math.Round(ba.KFactor, 4);
                    else if (ba.Type == (int)swBendAllowanceTypes_e.swBendAllowanceBendTable) res["bend_table"] = ba.BendTableFile;
                }
            }
            catch (Exception ex) { res["definition_error"] = ex.Message; }
            res["bends"] = bends;
            if (radii.Count > 0) res["bend_radii_mm"] = new JArray(radii.OrderBy(x => x));
            if (withBends && bendList.Count > 0) res["bend_list"] = bendList;
            try { string db; var mat = (d as IPartDoc)?.GetMaterialPropertyName2("", out db); if (!string.IsNullOrEmpty(mat)) res["material"] = mat; } catch { }

            // Cut-list folders carry SW's own sheet-metal properties (flat bounding box, cutting length, bends …),
            // names localized by the SW UI language — returned verbatim.
            if (bodyFolder != null && cutList)
            {
                // an automatic cut list is current already — updating it only marks the document modified
                try { var bf = bodyFolder.GetSpecificFeature2() as IBodyFolder; if (bf != null && !bf.GetAutomaticUpdate()) bf.UpdateCutList(); } catch { }
                var lists = new JArray();
                foreach (var sf in SubFeatures(bodyFolder))
                {
                    if (sf.GetTypeName2() == "CutListFolder")
                    {
                        var cpm = sf.CustomPropertyManager;
                        var props = new JObject();
                        foreach (var name in cpm?.GetNames() as string[] ?? new string[0]) props[name] = CpmGet(cpm, name);
                        int count = 0; try { count = (sf.GetSpecificFeature2() as IBodyFolder)?.GetBodyCount() ?? 0; } catch { }
                        lists.Add(new JObject { ["name"] = sf.Name, ["bodies"] = count, ["properties"] = props });
                    }
                }
                if (lists.Count > 0) res["cut_list"] = lists;
            }
            return res;
        }

        // Read-only access to a part in a configuration: when the already-loaded component document is in that
        // configuration, use it directly — no OpenDoc6 / activation / config switch (slow, and the full path
        // destabilised SW on a real 28-component assembly). Otherwise fall back to WithPartConfig.
        private T WithPartDocFast<T>(string path, string config, Func<IModelDoc2, T> action)
        {
            var d = _solidWorks.GetOpenDocumentByName(path) as IModelDoc2;
            string active = (d?.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
            if (d != null && (string.IsNullOrEmpty(config) || config == active)) return action(d);
            return WithPartConfig(path, config, action);
        }

        // Unique sheet-metal parts of an assembly: (path, config) → (sample component, qty).
        private List<Tuple<string, string, int>> SheetMetalPartsOf(IAssemblyDoc a)
        {
            ResolveLightweight(a);
            var map = new Dictionary<string, Tuple<string, string, int>>(StringComparer.OrdinalIgnoreCase);
            var notSm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Comps(a, false))
            {
                if (IsSuppressedComp(c)) continue;
                var md = c.GetModelDoc2() as IModelDoc2;
                if (md == null || md.GetType() != (int)swDocumentTypes_e.swDocPART) continue;
                string path = c.GetPathName(), cfg = c.ReferencedConfiguration, key = path + "|" + cfg;
                if (notSm.Contains(path)) continue;
                if (map.TryGetValue(key, out var t)) { map[key] = Tuple.Create(t.Item1, t.Item2, t.Item3 + 1); continue; }
                if (!IsSheetMetalPart(md)) { notSm.Add(path); continue; }
                map[key] = Tuple.Create(path, cfg, 1);
            }
            return map.Values.OrderBy(x => Path.GetFileName(x.Item1), StringComparer.OrdinalIgnoreCase).ToList();
        }

        public ExecutionResponse SheetMetalInfo(ToolRequest request) => Run(request, false, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            string comp = PStr(p, "component");
            if (doc.GetType() == (int)swDocumentTypes_e.swDocPART) return SheetMetalInfoOf(doc, true);
            var a = (IAssemblyDoc)doc;
            if (comp != null)
            {
                var c = FindComp(a, comp);
                return WithPartDocFast(c.GetPathName(), c.ReferencedConfiguration, d => SheetMetalInfoOf(d, true));
            }
            bool details = PBool(p, "details", false);
            var parts = new JArray();
            var tw = System.Diagnostics.Stopwatch.StartNew();
            var smParts = SheetMetalPartsOf(a);
            ExecLog.Write($"sheet_metal_info: scan {Comps(a, false).Count} comps -> {smParts.Count} sheet-metal in {tw.ElapsedMilliseconds} ms");
            foreach (var t in smParts)
            {
                tw.Restart();
                var info = WithPartDocFast(t.Item1, t.Item2, d => SheetMetalInfoOf(d, details));
                ExecLog.Write($"sheet_metal_info: {Path.GetFileName(t.Item1)} [{t.Item2}] {tw.ElapsedMilliseconds} ms");
                info["qty"] = t.Item3; info["path"] = t.Item1;
                if (!details) info.Remove("cut_list");
                parts.Add(info);
            }
            var byThk = parts.GroupBy(x => (double?)x["thickness_mm"] ?? 0)
                .OrderBy(g => g.Key)
                .Select(g => new JObject { ["thickness_mm"] = g.Key, ["parts"] = g.Count(), ["qty"] = g.Sum(x => (int)x["qty"]) });
            return new JObject { ["assembly"] = doc.GetTitle(), ["sheet_metal_parts"] = parts.Count, ["by_thickness"] = new JArray(byThk), ["parts"] = parts };
        }, keepClean: true);

        private static int DxfOptions(JObject p)
        {
            int o = 1;                                         // flat-pattern geometry
            if (PBool(p, "hidden_edges", false)) o |= 2;
            if (PBool(p, "bend_lines", true)) o |= 4;
            if (PBool(p, "sketches", false)) o |= 8;
            if (PBool(p, "merge_coplanar_faces", true)) o |= 16;
            if (PBool(p, "library_features", false)) o |= 32;
            if (PBool(p, "forming_tools", false)) o |= 64;
            if (PBool(p, "bounding_box", false)) o |= 2048;
            return o;
        }

        private bool ExportFlat(IModelDoc2 d, string outPath, int options)
        {
            var part = PartOrFail(d, "a flat-pattern DXF");
            EnsureFolderFor(outPath);
            string model = d.GetPathName();
            if (string.IsNullOrEmpty(model)) throw new ToolFail("NOT_SAVED", $"'{d.GetTitle()}' must be saved before flat export.");
            using (VisibleWindow())   // ExportToDWG2 faults while the frame is hidden
                return part.ExportToDWG2(outPath, model, (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal,
                    true, null, false, false, options, null);
        }

        // DXF base name: file name, or the part's «Обозначение» [+ «Наименование»] when name_by asks and it is filled.
        private string FlatBaseName(IModelDoc2 d, string config, string nameBy)
        {
            string n = Path.GetFileNameWithoutExtension(d.GetPathName());
            if (nameBy == "designation" || nameBy == "designation_name")
            {
                string des = DocProp(d, config, "Обозначение").Trim();
                if (nameBy == "designation_name") des = (des + " " + DocProp(d, config, "Наименование").Trim()).Trim();
                if (des.Length > 0) n = des;
            }
            return n;
        }

        // What the adapter needs per exported file: naming, DXF layering and the DFM checks (thickness, radius, K).
        private JObject FlatEntry(IModelDoc2 d, string config, string file, int qty)
        {
            var info = SheetMetalInfoOf(d, false, false);
            var e = new JObject { ["file"] = file, ["qty"] = qty, ["part"] = Path.GetFileNameWithoutExtension(d.GetPathName()), ["config"] = config ?? (string)info["config"] };
            string des = DocProp(d, config, "Обозначение"), name = DocProp(d, config, "Наименование");
            if (des.Length > 0) e["designation"] = des;
            if (name.Length > 0) e["name"] = name;
            foreach (var k in new[] { "material", "thickness_mm", "bend_radius_mm", "k_factor", "bend_radii_mm", "bends" })
                if (info[k] != null) e[k] = info[k];
            return e;
        }

        public ExecutionResponse ExportFlatDxf(ToolRequest request) => Run(request, false, p =>
        {
            var doc = ActivePartOrAsmOrFail();
            int opts = DxfOptions(p);
            string filePath = PStr(p, "file_path"), outDir = PStr(p, "out_dir");
            string nameBy = (PStr(p, "name_by") ?? "file").ToLowerInvariant();
            if (doc.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                if (!IsSheetMetalPart(doc)) throw new ToolFail("NOT_SHEET_METAL", $"'{doc.GetTitle()}' is not a sheet-metal part (sheet_metal_feature base_flange, or convert_to_sheet_metal).");
                string cfg = (doc.ConfigurationManager.ActiveConfiguration as IConfiguration)?.Name;
                filePath = filePath ?? Path.Combine(outDir ?? DocFolder(doc) ?? throw new ToolFail("NOT_SAVED", "The part was never saved: pass file_path or out_dir."),
                    SafeName(FlatBaseName(doc, cfg, nameBy)) + ".dxf");
                if (!ExportFlat(doc, filePath, opts)) throw new ToolFail("EXPORT_FAILED", "ExportToDWG2 returned false.");
                return new JObject { ["files"] = new JArray(FlatEntry(doc, cfg, filePath, PInt(p, "qty", 1))) };
            }
            var a = (IAssemblyDoc)doc;
            string comp = PStr(p, "component");
            if (comp != null)
            {
                var c = FindComp(a, comp);
                string cfg = c.ReferencedConfiguration;
                var entry = WithPartConfig(c.GetPathName(), cfg, d =>
                {
                    if (!IsSheetMetalPart(d)) throw new ToolFail("NOT_SHEET_METAL", $"Component '{c.Name2}' is not a sheet-metal part.");
                    string fp = filePath ?? Path.Combine(outDir ?? DocFolder(doc) ?? Path.GetDirectoryName(c.GetPathName()), SafeName(FlatBaseName(d, cfg, nameBy)) + ".dxf");
                    if (!ExportFlat(d, fp, opts)) throw new ToolFail("EXPORT_FAILED", "ExportToDWG2 returned false.");
                    return FlatEntry(d, cfg, fp, PInt(p, "qty", 1));
                });
                return new JObject { ["files"] = new JArray(entry) };
            }
            // Whole assembly: one DXF per unique part+config; name = <part>[_<config>]_<t>mm_x<qty>.dxf
            outDir = outDir ?? Path.Combine(DocFolder(doc) ?? throw new ToolFail("NOT_SAVED", "The assembly was never saved: pass out_dir."), "DXF");
            bool decorate = PBool(p, "name_with_thickness_qty", true);
            var done = new JArray(); var failed = new JArray();
            var parts = SheetMetalPartsOf(a);
            var multiCfg = new HashSet<string>(parts.GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Show the frame ONCE for the whole batch (ExportToDWG2 needs it; toggling per part is slow).
            using (VisibleWindow())
            foreach (var t in parts)
            {
                try
                {
                    WithPartConfig(t.Item1, t.Item2, d =>
                    {
                        string name = FlatBaseName(d, t.Item2, nameBy);
                        bool fromFile = name == Path.GetFileNameWithoutExtension(t.Item1);
                        if ((fromFile && multiCfg.Contains(t.Item1)) || used.Contains(name)) name += "_" + t.Item2;
                        used.Add(name);
                        if (decorate)
                        {
                            double? thk = ThicknessMm(d);
                            if (thk.HasValue) name += "_" + thk.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "mm";
                            name += "_x" + t.Item3;
                        }
                        string fp = Path.Combine(outDir, SafeName(name) + ".dxf");
                        if (ExportFlat(d, fp, opts)) done.Add(FlatEntry(d, t.Item2, fp, t.Item3));
                        else failed.Add(Path.GetFileName(t.Item1) + " [" + t.Item2 + "]: ExportToDWG2=false");
                        return 0;
                    });
                }
                catch (Exception ex) { failed.Add(Path.GetFileName(t.Item1) + " [" + t.Item2 + "]: " + ex.Message); }
            }
            ExecLog.Write($"export_flat_dxf: {done.Count} ok, {failed.Count} failed → {outDir}");
            return new JObject { ["out_dir"] = outDir, ["exported"] = done.Count, ["failed"] = failed, ["files"] = done };
        }, keepClean: true);
    }
}
