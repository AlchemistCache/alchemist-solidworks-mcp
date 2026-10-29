using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    // ===================== STEP / IGES → ONE native part =====================
    // import_step — a manufacturer's STEP (often a whole assembly) becomes ONE multibody .SLDPRT next to it (or at
    //               out_path), not an assembly with a file per component:
    //   * Import options (restored afterwards): assembly structure mapping = multibody part, 3D Interconnect off
    //     (native bodies, no link back to the STEP), no import-diagnostics prompt.
    //   * If SolidWorks still opens an assembly, it is saved as a part with all components (Save As Part).
    //   * The imported document is closed without saving — no component files are written; the translator's .err log
    //     (written next to the STEP) is read into the result and deleted unless it existed before.
    //   * "Always use the default templates" during the import — otherwise the hidden instance waits on the
    //     «Новый документ SOLIDWORKS» dialog (verified live: 11 min hang).
    public partial class SolidWorksService
    {
        // The options of every import (import_step, open_document of a foreign file) for its duration — the user's come
        // back (PrefScope): SolidWorks makes the new document from the DEFAULT template instead of asking which (with
        // «prompt user to select document template» the hidden instance waits on «New SOLIDWORKS Document» forever:
        // 11 min, then 8 h on 2026-10-28), a default that names a missing file is replaced by an existing template, and
        // no import-diagnostics prompt.
        private PrefScope ImportPrefs()
        {
            var scope = new PrefScope(_solidWorks)
                .Toggle(swUserPreferenceToggle_e.swAlwaysUseDefaultTemplates, true)
                .Toggle(swUserPreferenceToggle_e.swImportAutoRunImportDiagnostics, false);
            foreach (var (type, pref) in new[] { (swDocumentTypes_e.swDocPART, swUserPreferenceStringValue_e.swDefaultTemplatePart),
                                                 (swDocumentTypes_e.swDocASSEMBLY, swUserPreferenceStringValue_e.swDefaultTemplateAssembly) })
            {
                string current = _solidWorks.GetUserPreferenceStringValue((int)pref);
                if (!string.IsNullOrEmpty(current) && File.Exists(current)) continue;
                try { scope.Str(pref, DefaultTemplate(type)); }
                catch (ToolFail) { }   // no template of that kind at all: SolidWorks will say so
            }
            return scope;
        }

        private System.Collections.Generic.List<string> OpenDocTitles()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var o in _solidWorks.GetDocuments() as object[] ?? new object[0])
                if (o is IModelDoc2 m) list.Add(m.GetTitle());
            return list;
        }

        private static void CountBodies(IPartDoc part, JObject res)
        {
            if (part == null) return;
            int solids = (part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[])?.Length ?? 0;
            int sheets = (part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[])?.Length ?? 0;
            res["bodies"] = solids + sheets; res["solid_bodies"] = solids;
            if (sheets > 0) res["surface_bodies"] = sheets;   // pieces the translator could not knit into solids
        }

        public ExecutionResponse ImportStep(ToolRequest request) => Run(request, false, p =>
        {
            string src = PStr(p, "file_path") ?? throw new ToolFail("MISSING_PARAMETER", "file_path (the .step/.stp/.igs) is required.");
            src = Path.GetFullPath(src);
            if (!File.Exists(src)) throw new ToolFail("FILE_NOT_FOUND", $"No file at '{src}'.");
            string dst = PStr(p, "out_path") ?? Path.ChangeExtension(src, ".SLDPRT");
            dst = Path.GetFullPath(dst);
            bool inspectOnly = PBool(p, "inspect_only", false);   // reads the file; an existing part is no reason to skip
            if (!inspectOnly && File.Exists(dst) && !PBool(p, "overwrite", false))
                return new JObject { ["file"] = src, ["part"] = dst, ["skipped"] = "exists (overwrite=true to redo)" };

            var sw = Stopwatch.StartNew();
            string errLog = Path.ChangeExtension(src, ".err");
            bool errExisted = File.Exists(errLog);
            var before = new System.Collections.Generic.HashSet<string>(OpenDocTitles());
            IModelDoc2 doc = null;
            var res = new JObject { ["file"] = src, ["part"] = dst };
            // import options for this file only — the user's settings come back on every path (PrefScope): those of
            // every import (ImportPrefs), assembly structure → multibody part, no 3D Interconnect link,
            // save-assembly-as-part = all components
            using (ImportPrefs()
                .Int(swUserPreferenceIntegerValue_e.swImportNeutralAssemblyStructureMapping,
                     (int)swImportNeutralAssemblyStructureMapping_e.swImportNeutralAssemblyStructureMapping_MultibodyPart)
                .Toggle(swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect, PBool(p, "interconnect", false))
                .Int(swUserPreferenceIntegerValue_e.swSaveAssemblyAsPartOptions, (int)swSaveAsmAsPartOptions_e.swSaveAsmAsPart_AllComponents))
            try
            {
                int err = 0;
                doc = _solidWorks.LoadFile4(src, "r", null, ref err) as IModelDoc2;
                if (doc == null) throw new ToolFail("IMPORT_FAILED", $"LoadFile4 returned null (error {err}) for {Path.GetFileName(src)}.");
                res["imported_as"] = DocTypeName(doc);
                if (err != 0) res["import_errors"] = err;

                if (doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY)
                {
                    // the mapping option was not honoured — Save As Part with all components = one multibody part
                    res["via"] = "assembly saved as part (all components)";
                }
                else if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                    throw new ToolFail("IMPORT_FAILED", "The file opened as " + DocTypeName(doc) + ".");

                if (doc.GetType() == (int)swDocumentTypes_e.swDocPART) CountBodies(doc as IPartDoc, res);
                else
                {
                    // what the STEP holds — the part saved below is checked against it
                    int comps = 0, solids = 0, sheets = 0;
                    foreach (IComponent2 c in (doc as IAssemblyDoc).GetComponents(false) as object[] ?? new object[0])
                    {
                        object info;
                        int s1 = (c.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[])?.Length ?? 0;
                        int s2 = (c.GetBodies3((int)swBodyType_e.swSheetBody, out info) as object[])?.Length ?? 0;
                        if (s1 + s2 > 0) comps++;
                        solids += s1; sheets += s2;
                    }
                    res["source"] = new JObject { ["components_with_bodies"] = comps, ["solid_bodies"] = solids, ["surface_bodies"] = sheets };
                }
                if (inspectOnly) res["part"] = null;
                else
                {
                EnsureFolderFor(dst);
                int se = 0, sw2 = 0;
                bool ok = doc.Extension.SaveAs3(dst, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref se, ref sw2);
                if (!ok || !File.Exists(dst)) throw new ToolFail("SAVE_FAILED", $"SaveAs3 to {dst} failed (error {se}, warning {sw2}).");
                if (sw2 != 0) res["save_warnings"] = sw2;
                res["size_kb"] = new FileInfo(dst).Length / 1024;
                if (res["bodies"] == null && PBool(p, "verify", true))
                {
                    // the assembly route: count what landed in the part
                    int e2 = 0, w2 = 0;
                    var chk = _solidWorks.OpenDoc6(dst, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e2, ref w2) as IModelDoc2;
                    if (chk != null) { CountBodies(chk as IPartDoc, res); _solidWorks.CloseDoc(chk.GetTitle()); }
                }
                var src0 = res["source"] as JObject;
                if (src0 != null && res["bodies"] != null && (int)res["bodies"] < (int)src0["solid_bodies"] + (int)src0["surface_bodies"])
                    res["warning"] = $"the part has {res["bodies"]} bodies, the STEP {(int)src0["solid_bodies"] + (int)src0["surface_bodies"]} — check it";
                }
            }
            finally
            {
                if (doc != null) { try { _solidWorks.CloseDoc(doc.GetTitle()); } catch { } }
                // components of an imported assembly stay loaded without a path — close what this import added
                foreach (var t in OpenDocTitles()) if (!before.Contains(t)) { try { _solidWorks.CloseDoc(t); } catch { } }
            }
            if (File.Exists(errLog) && !errExisted)
            {
                try
                {
                    var lines = File.ReadAllLines(errLog, System.Text.Encoding.Default)
                        .Where(l => l.TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase) || l.TrimStart().StartsWith("Ошибка")).Distinct().Take(10).ToArray();
                    if (lines.Length > 0) res["translator_errors"] = new JArray(lines.Select(l => l.Trim()));
                    File.Delete(errLog);
                }
                catch { }
            }
            res["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
            ExecLog.Write($"import_step: {Path.GetFileName(src)} -> {res["imported_as"]}, {res["bodies"]} bodies, {res["seconds"]} s");
            return res;
        });
    }
}
