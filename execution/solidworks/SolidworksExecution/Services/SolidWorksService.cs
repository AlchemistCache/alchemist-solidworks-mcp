using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidworksExecution.Infrastructure;
using SolidworksExecution.Models;

namespace SolidworksExecution.Services
{
    public partial class SolidWorksService
    {
        private readonly IOperationGuard _guard;
        private ISldWorks _solidWorks;

        public bool IsConnected { get; private set; }

        public SolidWorksService(IOperationGuard guard)
        {
            _guard = guard;
        }

        // SolidWorks COM must be called from an STA thread.
        // Ensure the calling thread is STA (e.g. [STAThread] on Main, or a dedicated STA thread).
        public bool Connect()
        {
            if (OwnMode) return ConnectOwn();
            try
            {
                _solidWorks = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                IsConnected = true;
                EnsureVisible(); // the user must ALWAYS see what the automation does
                return true;
            }
            catch (COMException)
            {
                IsConnected = false;
                return false;
            }
        }

        // Force the attached SolidWorks instance to be on-screen and user-controllable.
        // GetActiveObject binds to whatever instance is first in the COM Running Object Table — which
        // can be a headless/COM-launched instance with NO window (Visible=false). If we silently drove
        // that one, the user would watch a different window and see nothing happen (observed live: two
        // SLDWORKS processes, the older one window-less). Policy (for now): every instance we touch is
        // made visible. Best-effort — a visibility failure must never break the attach or a tool call.
        private void EnsureVisible()
        {
            try
            {
                if (_solidWorks == null) return;
                if (OwnMode) { HideOwn(); return; }
                if (!_solidWorks.Visible) _solidWorks.Visible = true;
                _solidWorks.UserControl = true;
            }
            catch { /* visibility is best-effort; never fail over it */ }
        }

        // ===================== OWN-INSTANCE MODE (2026-09-30) =====================
        // With %USERPROFILE%\SolidWorksMCP\own_instance.flag present the server NEVER attaches to the user's SolidWorks: it
        // starts its OWN SLDWORKS.exe, hides it (Visible=false) and finds it by PID (ROT moniker "SolidWorks_PID_<pid>"
        // or ISldWorks.GetProcessID); the PID is kept in %LOCALAPPDATA%\SolidWorksMCP\own_sw_pid.txt. Deleting the flag
        // file restores the original SolidPilot behaviour. The flag is looked up in %USERPROFILE%\SolidWorksMCP, then
        // next to the exe and upwards (the repository root).
        private static string OwnFlagPath
        {
            get
            {
                string user = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "SolidWorksMCP", "own_instance.flag");
                if (System.IO.File.Exists(user)) return user;
                var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
                {
                    string f = System.IO.Path.Combine(dir.FullName, "own_instance.flag");
                    if (System.IO.File.Exists(f)) return f;
                }
                return user;
            }
        }
        private static string OwnPidPath
        {
            get { return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidWorksMCP", "own_sw_pid.txt"); }
        }
        public static bool OwnMode
        {
            get { try { return System.IO.File.Exists(OwnFlagPath); } catch { return false; } }
        }

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable prot);
        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ppbc);

        private static int ReadOwnPid()
        {
            try
            {
                if (!System.IO.File.Exists(OwnPidPath)) return 0;
                int pid;
                if (!int.TryParse(System.IO.File.ReadAllText(OwnPidPath).Trim(), out pid)) return 0;
                var p = Process.GetProcessById(pid);
                if (p.HasExited || !p.ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase)) return 0;
                return pid;
            }
            catch { return 0; }
        }

        private static void WriteOwnPid(int pid)
        {
            try
            {
                EnsureFolderFor(OwnPidPath);
                System.IO.File.WriteAllText(OwnPidPath, pid.ToString());
            }
            catch { }
        }

        // Find the SolidWorks COM object that belongs to process `pid` — never any other instance.
        private static ISldWorks FindByPid(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot;
                if (GetRunningObjectTable(0, out rot) == 0 && rot != null)
                {
                    System.Runtime.InteropServices.ComTypes.IEnumMoniker en;
                    rot.EnumRunning(out en);
                    var mk = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
                    string want = "SolidWorks_PID_" + pid;
                    while (en.Next(1, mk, IntPtr.Zero) == 0)
                    {
                        string name = null;
                        try
                        {
                            System.Runtime.InteropServices.ComTypes.IBindCtx ctx;
                            CreateBindCtx(0, out ctx);
                            mk[0].GetDisplayName(ctx, null, out name);
                        }
                        catch { }
                        if (name != null && name.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                        {
                            object o;
                            rot.GetObject(mk[0], out o);
                            var sw = o as ISldWorks;
                            if (sw != null) return sw;
                        }
                    }
                }
            }
            catch { }
            // Fallback: the ProgID registration, but only if it really is our process.
            try
            {
                var sw = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                if (sw != null && sw.GetProcessID() == pid) return sw;
            }
            catch { }
            return null;
        }

        private void HideOwn()
        {
            try
            {
                if (_solidWorks == null) return;
                if (_solidWorks.Visible) _solidWorks.Visible = false;
                _solidWorks.UserControl = false;
            }
            catch { }
        }

        private bool ConnectOwn()
        {
            var sw = FindByPid(ReadOwnPid());
            if (sw != null)
            {
                try
                {
                    sw.RevisionNumber();
                    _solidWorks = sw;
                    IsConnected = true;
                    HideOwn();
                    return true;
                }
                catch { }
            }
            _solidWorks = null;
            IsConnected = false;
            return false;
        }

        private static string SwExePath()
        {
            string p = System.Environment.GetEnvironmentVariable("SWMCP_SW_EXE");
            if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) return p;
            return @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
        }

        private Dictionary<string, object> EnsureReadyOwn()
        {
            var result = new Dictionary<string, object>();
            result["ownInstanceMode"] = true;
            bool launched = false;
            bool attached = ConnectOwn();
            if (!attached)
            {
                try
                {
                    var psi = new ProcessStartInfo(SwExePath());
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Minimized;
                    var proc = Process.Start(psi);
                    launched = true;
                    WriteOwnPid(proc.Id);
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(110);
                    while (DateTime.UtcNow < deadline && !attached)
                    {
                        System.Threading.Thread.Sleep(2000);
                        if (proc.HasExited) break;
                        attached = ConnectOwn();
                    }
                }
                catch (Exception ex)
                {
                    result["launchError"] = ex.Message;
                }
            }
            result["comAttached"] = attached;
            result["swLaunched"] = launched;
            result["ownPid"] = ReadOwnPid();
            try { result["allSolidworksPids"] = SolidWorksProcesses().ToArray(); } catch { }
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                    result["swVersion"] = _solidWorks.RevisionNumber();
                    result["visible"] = _solidWorks.Visible;
                }
                catch { }
            }
            else if (!result.ContainsKey("launchError"))
            {
                result["launchError"] = "Own SolidWorks instance did not become reachable (ROT moniker SolidWorks_PID_" + ReadOwnPid() + ").";
            }
            return result;
        }
        // =================== END OWN-INSTANCE MODE ===================

        private bool EnsureConnected()
        {
            if (_solidWorks != null) return true;
            return Connect();
        }

        // The PIDs of every running SolidWorks, regardless of whether COM can see them. This is the
        // check that makes single-instance discipline possible: the ROT and the process list disagree
        // exactly in the cases that used to spawn a duplicate.
        private static List<int> SolidWorksProcesses()
        {
            try
            {
                return Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToList();
            }
            catch
            {
                return new List<int>(); // never let a diagnostic break the lifecycle path
            }
        }

        // Poll for a FRESH ROT attach until `timeout`. Used both after launching (the app needs time
        // before it is usable) and instead of launching when a process already exists but has not
        // registered yet. Gates on a real call, not mere registration, because that is what the next
        // tool call will do. Sets `_solidWorks` on success so the caller can read doc/version off it.
        private bool RetryAttach(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                try
                {
                    var probe = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                    probe.RevisionNumber(); // confirms responsive, not just registered
                    _solidWorks = probe;
                    EnsureVisible();
                    return true;
                }
                catch
                {
                    if (DateTime.UtcNow >= deadline) return false;
                    System.Threading.Thread.Sleep(1000); // still spinning up / COM server busy
                }
            }
        }

        // Health probe: attempts a fresh COM attach and reports whether SolidWorks is reachable
        // plus the active document title. Must run on the STA thread (it touches COM via Connect()).
        // Returns a dictionary so no new compiled type is needed (old-style csproj).
        public Dictionary<string, object> GetHealthInfo()
        {
            var result = new Dictionary<string, object>();
            bool attached = Connect();
            result["comAttached"] = attached;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                }
                catch { /* attached but couldn't read the active doc — leave it unset */ }
            }
            // Duplicate/hidden-instance diagnostic. A count > 1 is the
            // condition under which automation can drive an instance the user is not watching, and a
            // count > 0 while comAttached is false is the ROT-vs-process disagreement that used to
            // spawn another. windowless>0 names the specific bad case: an instance with no window.
            try
            {
                var procs = Process.GetProcessesByName("SLDWORKS");
                result["solidworksProcessCount"] = procs.Length;
                result["solidworksPids"] = procs.Select(p => p.Id).ToArray();
                result["solidworksWindowless"] = procs.Count(p => p.MainWindowHandle == IntPtr.Zero);
            }
            catch { /* diagnostic only — never fail /health over it */ }
            return result;
        }

        // Lifecycle bootstrap (ensure_ready): unlike GetHealthInfo (read-only probe), this LAUNCHES
        // SolidWorks via COM when it isn't already running, then waits until a fresh ROT attach
        // succeeds — because every real tool call re-attaches on a new service instance via
        // GetActiveObject, that is the exact readiness gate that matters. Does NOT open/create any
        // document (assembly/drawing contexts come later — opening a part here would be wrong).
        // Must run on the STA thread (touches COM). Returns a plain dictionary (old-style csproj).
        public Dictionary<string, object> EnsureReady()
        {
            if (OwnMode) return EnsureReadyOwn();
            var result = new Dictionary<string, object>();
            bool launched = false;
            bool attached = Connect();

            if (!attached)
            {
                // SINGLE-INSTANCE DISCIPLINE (2026-07-30). A failed attach does
                // NOT mean SolidWorks is absent: Marshal.GetActiveObject only sees the COM Running
                // Object Table, and a RUNNING instance can be missing from our view of it — it is still
                // starting up and has not registered yet, or it runs at a different integrity level
                // (started elevated while this server is not, or vice versa), or it is wedged in a modal
                // dialog. Launching in those cases produced a SECOND SLDWORKS.exe, which is what the
                // user actually sees. So: only CreateInstance when NO SolidWorks process exists at all.
                var running = SolidWorksProcesses();
                if (running.Count > 0)
                {
                    // One (or more) IS running but the ROT attach failed. Retry the attach — the common
                    // benign case is an instance mid-startup — and NEVER launch another.
                    attached = RetryAttach(TimeSpan.FromSeconds(30));
                    IsConnected = attached;
                    if (!attached)
                    {
                        result["comAttached"] = false;
                        result["swLaunched"] = false;
                        result["solidworksProcessCount"] = running.Count;
                        result["ensureError"] =
                            "SolidWorks IS running (PID " + string.Join(", ", running.Select(p => p.ToString()).ToArray()) +
                            ") but COM attach via the Running Object Table failed after 30s. A second instance was " +
                            "deliberately NOT launched. Most likely causes, in order: " +
                            "(1) SolidWorks is elevated/'Run as administrator' while this execution server is not " +
                            "(or the reverse) — the ROT is not shared across integrity levels, so run both the same way; " +
                            "(2) SolidWorks is still loading, or is blocked on a modal dialog / license prompt — clear it and retry; " +
                            "(3) the instance never registered in the ROT. Check: Get-Process SLDWORKS | Select Id, MainWindowHandle, MainWindowTitle";
                        return result;
                    }
                }
                else
                {
                    // Genuinely nothing running — start it out-of-process via COM and make it visible.
                    try
                    {
                        var progType = Type.GetTypeFromProgID("SldWorks.Application");
                        if (progType == null)
                            throw new Exception("SldWorks.Application ProgID is not registered. Is SolidWorks installed?");

                        _solidWorks = (ISldWorks)Activator.CreateInstance(progType);
                        EnsureVisible(); // visible + user-controllable from the moment it launches
                        launched = true;

                        // CreateInstance returns before the app is usable. Gate on a FRESH ROT attach
                        // (Marshal.GetActiveObject) succeeding + a responsive call — that's what the next
                        // tool call will do. Wait up to 90s (adapter ENSURE_TIMEOUT is 120s).
                        attached = RetryAttach(TimeSpan.FromSeconds(90));
                        IsConnected = attached;
                    }
                    catch (Exception ex)
                    {
                        result["comAttached"] = false;
                        result["swLaunched"] = false;
                        result["launchError"] = ex.Message;
                        return result;
                    }
                }
            }

            result["comAttached"] = attached;
            result["swLaunched"] = launched;
            if (attached)
            {
                try
                {
                    var d = _solidWorks.IActiveDoc2 as IModelDoc2;
                    result["activeDocument"] = d != null ? d.GetTitle() : null;
                    result["swVersion"] = _solidWorks.RevisionNumber();
                }
                catch { /* attached but couldn't read doc/version — leave unset */ }
            }
            else if (!result.ContainsKey("launchError"))
            {
                // Launched but never became responsive within the wait window.
                result["launchError"] = "SolidWorks was launched but did not become responsive within the timeout.";
            }
            return result;
        }

        public ExecutionResponse OpenNewPart(ToolRequest request) => Run(request, true, p =>
        {
            NewDoc(swDocumentTypes_e.swDocPART, PStr(p, "template_path"));
            return new ToolOutput();
        });

        // open_document — open an EXISTING file from disk (the counterpart to open_new_part, which only
        // makes a BLANK doc). Native .sldprt/.sldasm/.slddrw open directly; foreign .ipt/.CATPart/STEP/etc.
        // import as a PART via SolidWorks 3D Interconnect / translators (install/license dependent — a
        // failure returns a clear OPEN_FAILED so the caller can defer that sample, per the drawing plan).
        // Bumps state_version (a new active document is established), same effect class as open_new_part.
        public ExecutionResponse OpenDocument(ToolRequest request) => Run(request, true, p =>
        {
            string filePath = PStr(p, "file_path");
            if (string.IsNullOrEmpty(filePath))
                throw new ToolFail("MISSING_PARAMETER", "file_path is required.");
            if (!System.IO.File.Exists(filePath))
                throw new ToolFail("FILE_NOT_FOUND", $"No file at '{filePath}'.");

            // Document type from the extension.
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            int docType;
            switch (ext)
            {
                case ".sldasm": docType = (int)swDocumentTypes_e.swDocASSEMBLY; break;
                case ".slddrw": docType = (int)swDocumentTypes_e.swDocDRAWING; break;
                // .sldprt AND foreign part formats (.ipt/.CATPart/.step/.stp/.iges/.igs/.x_t/.x_b/...)
                // open as a PART (3D Interconnect imports the foreign ones).
                default: docType = (int)swDocumentTypes_e.swDocPART; break;
            }

            int errors = 0, warnings = 0;
            IModelDoc2 modelDoc;
            List<string> dialogs;
            bool foreign = ext != ".sldprt" && ext != ".sldasm" && ext != ".slddrw";
            string openPath = "OpenDoc6";
            // a foreign file is an IMPORT, every stage below: the options of every import for this open (ImportPrefs —
            // without them the hidden instance waited 8 h on «Новый документ SOLIDWORKS», 2026-10-28)
            using (foreign ? ImportPrefs() : null)
            {
                using (var watch = new DialogWatch(this))   // a repair / missing-reference prompt would block SolidWorks
                {
                    modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                    watch.Dispose();
                    dialogs = watch.Said;
                }

                // FOREIGN-FORMAT staged fallback (2026-07-09, proven live): with 3D Interconnect OFF,
                // OpenDoc6 does NOT import foreign files — it returns null with the misleading
                // errors=2097152 (swFileRequiresRepairError; observed on both .step and NX .prt).
                // Stage 2: LoadFile4 = the CLASSIC translator import (STEP/IGES; content decides
                // part vs assembly). Stage 3: NX/Creo/CATIA native formats have no classic
                // translator — they REQUIRE Interconnect, so it is enabled for that one OpenDoc6 retry (PrefScope: the
                // user's setting comes back afterwards).
                // as_assembly=true (foreign only): the file is an ASSEMBLY STEP/etc. The classic
                // translator (LoadFile4) imports an assembly STEP as a MULTIBODY PART (proven live
                // 2026-07-09 on Fixture Assembly.step) — only 3D Interconnect maps it to a real
                // assembly with components. Enable the toggle just for this open and RESTORE it
                // after, so foreign PART opens elsewhere keep their proven classic-import behavior.
                bool asAssembly = PBool(p, "as_assembly", false);
                if (modelDoc == null && foreign && asAssembly)
                {
                    // 691 = swMultiCAD_Enable3DInterconnect; 758 = swMultiCAD_ApplyOnlyToParts (ON excludes assemblies)
                    using (new PrefScope(_solidWorks).Toggle((swUserPreferenceToggle_e)691, true).Toggle((swUserPreferenceToggle_e)758, false))
                    {
                        errors = 0; warnings = 0;
                        modelDoc = _solidWorks.OpenDoc6(filePath, (int)swDocumentTypes_e.swDocASSEMBLY,
                            (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                    }
                    openPath = "OpenDoc6+3DInterconnect(as_assembly)";
                    ExecLog.Write($"open_document(as_assembly): errors={errors} warnings={warnings} null={modelDoc == null}");
                }
                if (modelDoc == null && foreign)
                {
                    int lfErrors = 0;
                    modelDoc = _solidWorks.LoadFile4(filePath, "r", null, ref lfErrors) as IModelDoc2;
                    openPath = "LoadFile4";
                    if (modelDoc == null && !_solidWorks.GetUserPreferenceToggle(691))
                    {
                        // 3D Interconnect only for this open — the user's setting comes back (PrefScope), it used to stay on
                        using (new PrefScope(_solidWorks).Toggle(swUserPreferenceToggle_e.swMultiCAD_Enable3DInterconnect, true))
                        {
                            ExecLog.Write("open_document: 3D Interconnect on for this foreign-format import");
                            errors = 0; warnings = 0;
                            modelDoc = _solidWorks.OpenDoc6(filePath, docType,
                                (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
                        }
                        openPath = "OpenDoc6+3DInterconnect";
                    }
                }
            }

            if (modelDoc == null)
                throw new ToolFail("OPEN_FAILED",
                    $"Could not open '{filePath}': {LoadErrorText(errors)} (errors={errors}" + (foreign ? ", LoadFile4 also null" : "") + "). " +
                    (foreign ? "For a foreign file this usually means no usable translator — convert to STEP. " : "") +
                    ((errors & (int)swFileLoadError_e.swFileCriticalDataRepairError) != 0
                        ? "The file needs SolidWorks' critical-data repair (old or damaged): open it once by hand in SolidWorks, let it repair, save. " : "") +
                    (dialogs.Count > 0 ? $"SolidWorks showed (closed): «{string.Join("» «", dialogs)}»." : ""));
            ExecLog.Write($"open_document: '{System.IO.Path.GetFileName(filePath)}' via {openPath}");

            // ALREADY-OPEN gotcha (the 2026-07-05 PENDING bug, fixed 2026-07-07): OpenDoc6 on an
            // already-open file returns its IModelDoc2 but does NOT activate it — the previously
            // active document silently stays active, so follow-up reads (analyze)
            // hit the WRONG doc. Explicitly activate what we just opened, then verify.
            Activate(modelDoc.GetTitle());
            var nowActive = _solidWorks.IActiveDoc2 as IModelDoc2;
            // Title normalization: an imported doc's title can gain/lose its extension between
            // reads ('Butterfly_valve' vs 'Butterfly_valve.prt' — observed live on an NX
            // import). Compare extension-stripped, case-insensitive.
            Func<string, string> baseTitle = t =>
                (System.IO.Path.GetFileNameWithoutExtension(t ?? "") ?? "").ToLowerInvariant();
            if (nowActive == null || baseTitle(nowActive.GetTitle()) != baseTitle(modelDoc.GetTitle()))
                throw new ToolFail("ACTIVATE_FAILED",
                    $"Opened '{modelDoc.GetTitle()}' but could not make it the ACTIVE document " +
                    $"(active is '{nowActive?.GetTitle()}').");

            return new ToolOutput { Features = new List<string> { System.IO.Path.GetFileName(filePath) } };
        });

        public ExecutionResponse VerifyState(ToolRequest request) => Run(request, false, p =>
        {
            var d = ActiveDocOrFail();
            var names = Features(d).Where(f => !IsFolderEnd(f)).Select(f => f.Name).ToList();
            return new ToolOutput { Features = names, ActiveSketch = (d.SketchManager.ActiveSketch as IFeature)?.Name };
        });

        // close_document — the active document (saved first if asked), every document (close_all), or a set: the given
        // titles, or hidden_only = the invisible component documents SolidWorks keeps loaded in the background.
        public ExecutionResponse CloseDocument(ToolRequest request) => Run(request, true, p =>
        {
            if (PBool(p, "close_all", false))
            {
                // Close EVERYTHING, including invisibly-loaded component docs that a per-active-doc close loop can never
                // reach. A lingering doc with the same TITLE blocks opening another folder's same-named part
                // (swFileWithSameTitleAlreadyOpen = 65536) and keeps files locked. Discards unsaved changes.
                _solidWorks.CloseAllDocuments(true);
                return new ToolOutput { NoDocument = true };
            }
            var titles = PStrList(p, "titles");
            if (titles.Count > 0 || PBool(p, "hidden_only", false))
                return CloseDocumentSet(titles, PBool(p, "include_modified", false));
            var d = ActiveDocOrFail();
            string title = d.GetTitle();   // IModelDoc2.Close() is not implemented in the interop — CloseDoc(title)
            if (PBool(p, "save", false))
            {
                if (string.IsNullOrEmpty(d.GetPathName()))
                    throw new ToolFail("NOT_SAVED", $"'{title}' was never saved — call save_document(file_path) first; the document stays open.");
                int e = 0, w = 0;
                if (!d.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w))
                    throw new ToolFail("SAVE_FAILED", $"Saving '{title}' failed (errors {e}, warnings {w}); the document stays open.");
            }
            string path = d.GetPathName();
            _solidWorks.CloseDoc(title);   // discards unsaved changes silently (hence the save above)
            // a never-saved drawing stays open after CloseDoc (CloseDocumentSet); QuitDoc closes it
            if (string.IsNullOrEmpty(path) && StillOpen(title, title, false)) try { _solidWorks.QuitDoc(title); } catch { }
            return new ToolOutput { NoDocument = true };
        });

        // The given titles, or (none given) every background document: no window of its own (component documents an
        // assembly loaded) and not the active one — in the hidden SolidWorks every document is invisible, so Visible cannot
        // tell. A modified one is skipped unless include_modified, which discards its changes.
        private JObject CloseDocumentSet(List<string> titles, bool includeModified)
        {
            var closed = new JArray(); var skipped = new JArray();
            string active = (_solidWorks.IActiveDoc2 as IModelDoc2)?.GetTitle();
            var pending = new List<(string title, string key, int type, bool saved)>();
            foreach (var d in (_solidWorks.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>().ToList())
            {
                string t;
                try { t = d.GetTitle(); }
                catch (System.Runtime.InteropServices.COMException) { continue; }   // closed with an assembly closed just before (RPC_E_DISCONNECTED)
                if (titles.Count > 0 ? !titles.Contains(t) : t == active || d.GetFirstModelView() != null) continue;
                if (d.GetSaveFlag() && !includeModified) { skipped.Add(t + " (modified)"); continue; }
                string path = d.GetPathName();
                // a never-saved drawing is titled "Чертеж61 - Лист1": CloseDoc wants "Чертеж61", and GetOpenDocumentByName
                // finds it by neither — it read as closed while it stayed open
                int type = d.GetType();
                string name = string.IsNullOrEmpty(path) && type == (int)swDocumentTypes_e.swDocDRAWING && t.LastIndexOf(" - ", StringComparison.Ordinal) > 0
                    ? t.Substring(0, t.LastIndexOf(" - ", StringComparison.Ordinal)) : t;
                pending.Add((t, string.IsNullOrEmpty(path) ? name : path, type, !string.IsNullOrEmpty(path)));
            }
            // SolidWorks keeps a document an open assembly or drawing uses: drawings and assemblies go first, and what stays
            // is tried again while a pass closes something (["L_asm.SLDASM", "L_bracket.SLDPRT"] skipped the part — the
            // documents list had it before its assembly)
            pending = pending.OrderByDescending(x => x.type).ToList();
            for (bool progress = true; progress && pending.Count > 0;)
            {
                progress = false;
                foreach (var x in pending.ToList())
                {
                    if (StillOpen(x.title, x.key, x.saved)) _solidWorks.CloseDoc(x.saved ? x.title : x.key);
                    // CloseDoc leaves a never-saved drawing open under either name; QuitDoc closes it (unsaved changes go)
                    foreach (var n in x.saved ? new string[0] : new[] { x.title, x.key })
                        if (StillOpen(x.title, x.key, x.saved)) try { _solidWorks.QuitDoc(n); } catch { }
                    if (StillOpen(x.title, x.key, x.saved)) continue;
                    closed.Add(x.title); pending.Remove(x); progress = true;
                }
            }
            foreach (var x in pending) skipped.Add(x.title + " (in use by an open assembly or drawing)");
            return new JObject { ["closed_count"] = closed.Count, ["closed"] = new JArray(closed.Take(20)), ["skipped"] = skipped };
        }

        private bool StillOpen(string title, string key, bool saved)
        {
            if (saved) return _solidWorks.GetOpenDocumentByName(key) != null;
            foreach (var o in _solidWorks.GetDocuments() as object[] ?? new object[0])
                try { if (o is IModelDoc2 m && m.GetTitle() == title) return true; } catch (System.Runtime.InteropServices.COMException) { }
            return false;
        }

        // Saving does not change CAD geometry — state_version is unchanged (same as export). save_all also saves the
        // modified documents the active one references (Save3 SaveReferenced): a top-level save left 19 subassemblies of
        // a machine modified for hours. Save As of a document other open assemblies use would point them ALL to the new file
        // (a part made "to order" replaced the stock one in another assembly too): refused unless copy_only (a copy, nothing
        // re-pointed) or repoint_assemblies. Every answer lists the documents still not saved.
        public ExecutionResponse SaveDocument(ToolRequest request) => Run(request, false, p =>
        {
            var d = ActiveDocOrFail();
            string filePath = PStr(p, "file_path");
            bool saveAll = PBool(p, "save_all", false), copyOnly = PBool(p, "copy_only", false);
            int e = 0, w = 0;
            bool ok;
            var res = new JObject();
            if (filePath == null)
            {
                if (string.IsNullOrEmpty(d.GetPathName()))
                    throw new ToolFail("MISSING_PARAMETER", "Document has never been saved; file_path is required for the first save (full path including .sldprt/.sldasm/.slddrw extension).");
                int opts = (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (saveAll ? (int)swSaveAsOptions_e.swSaveAsOptions_SaveReferenced : 0);
                ok = d.Save3(opts, ref e, ref w);
            }
            else
            {
                CheckSavePath(filePath);
                var users = copyOnly ? new List<string>() : AssembliesUsing(d);
                if (users.Count > 0 && !PBool(p, "repoint_assemblies", false))
                    throw new ToolFail("REFERENCED", $"'{d.GetTitle()}' is a component of {users.Count} open assembl{(users.Count == 1 ? "y" : "ies")} " +
                        $"({string.Join(", ", users.Take(5))}{(users.Count > 5 ? ", …" : "")}): Save As would make them ALL use the new file. " +
                        "copy_only=True writes a copy and changes nothing else; repoint_assemblies=True makes them use the new file.");
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                int opts = (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (copyOnly ? (int)swSaveAsOptions_e.swSaveAsOptions_Copy : 0);
                ok = d.Extension.SaveAs3(filePath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, opts, null, null, ref e, ref w);
                if (ok && copyOnly) res["copy"] = filePath;
                if (ok && users.Count > 0) res["repointed_assemblies"] = new JArray(users);
            }
            if (!ok) throw new ToolFail("SAVE_FAILED", $"Save failed: {SaveErrorText(e)} (errors {e}, warnings {w}).");
            res["features"] = new JArray(d.GetPathName());
            var dirty = (_solidWorks.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>()
                .Where(x => { try { return x.GetSaveFlag(); } catch { return false; } })
                .Select(x => x.GetTitle()).ToList();
            if (dirty.Count > 0)
            {
                res["still_modified_count"] = dirty.Count;
                res["still_modified"] = new JArray(dirty.Take(20));
            }
            return res;
        });

        // Windows file-name rules checked before SolidWorks answers a bare "save failed".
        private static void CheckSavePath(string path)
        {
            int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string name = path.Substring(cut + 1), dir = cut > 0 ? path.Substring(0, cut) : "";
            var badName = name.Where(ch => "<>:\"|?*".IndexOf(ch) >= 0 || ch < ' ').Distinct().ToList();
            var badDir = dir.Where((ch, i) => "<>\"|?*".IndexOf(ch) >= 0 || (ch == ':' && i != 1)).Distinct().ToList();
            if (badName.Count > 0 || badDir.Count > 0)
                throw new ToolFail("BAD_PARAMETER", $"file_path has characters Windows does not allow in a file name: {string.Join(" ", badName.Concat(badDir).Distinct().Select(ch => ch < ' ' ? $"\\x{(int)ch:x2}" : ch.ToString()))} — replace them (e.g. ':' with '-').");
            if (path.Length >= 260)
                throw new ToolFail("BAD_PARAMETER", $"file_path is {path.Length} characters; Windows allows 259 — shorten the folder or the name.");
        }

        // Open assemblies (other than d) that have d as a component.
        private List<string> AssembliesUsing(IModelDoc2 d)
        {
            string path = d.GetPathName();
            var users = new List<string>();
            if (string.IsNullOrEmpty(path)) return users;
            foreach (var x in (_solidWorks.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>())
            {
                try
                {
                    if (x.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY || ReferenceEquals(x, d) || x.GetPathName() == path) continue;
                    var comps = (x as IAssemblyDoc)?.GetComponents(false) as object[];
                    if (comps != null && comps.OfType<IComponent2>().Any(c => string.Equals(c.GetPathName(), path, StringComparison.OrdinalIgnoreCase)))
                        users.Add(x.GetTitle());
                }
                catch { }
            }
            return users;
        }

        private static string SaveErrorText(int e)
        {
            var parts = new List<string>();
            foreach (swFileSaveError_e v in Enum.GetValues(typeof(swFileSaveError_e)))
                if ((e & (int)v) != 0) parts.Add(v.ToString().Replace("swFile", "").Replace("sw", ""));
            if ((e & (int)swFileSaveError_e.swReadOnlySaveError) != 0) parts.Add("the file is read-only or open in another SolidWorks");
            return parts.Count > 0 ? string.Join(", ", parts) : "SolidWorks gave no reason";
        }

        // export_document — the active document to file_path (format from its extension) or, with formats, one
        // file per format: file_path with the extension replaced; partial failures go into Dimensions. Export does NOT
        // change the model — state_version is unchanged.
        public ExecutionResponse ExportDocument(ToolRequest request) => Run(request, false, p =>
        {
            string filePath = PStr(p, "file_path") ?? throw new ToolFail("MISSING_PARAMETER", "file_path is required (full output path, e.g. 'C:/out/part.step').");
            var formats = PStrList(p, "formats").Select(f => f.ToUpperInvariant()).Distinct().ToList();
            if (formats.Count == 0) formats.Add(ExportFormatOf(filePath));
            var d = ActiveDocOrFail();
            if (formats.Count == 1)
            {
                string fmt = formats[0];
                if (fmt == "DWG" || fmt == "DXF")
                    DrawingOrFail(d, $"format {fmt}", "create_drawing first; a sheet-metal flat pattern is export_flat_dxf");
                if (!System.IO.Path.HasExtension(filePath)) filePath += "." + ExportExtension(fmt);
                return new ToolOutput { Features = new List<string> { ExportActive(d, fmt, filePath) } };
            }
            var exported = new List<string>(); var failed = new List<string>();
            foreach (var fmt in formats)
            {
                if ((fmt == "DWG" || fmt == "DXF") && !(d is IDrawingDoc)) { failed.Add($"{fmt}: needs a drawing"); continue; }
                try { exported.Add(ExportActive(d, fmt, System.IO.Path.ChangeExtension(filePath, ExportExtension(fmt)))); }
                catch (ToolFail f) { failed.Add($"{fmt}: {f.Message}"); }
            }
            if (exported.Count == 0) throw new ToolFail("EXPORT_FAILED", $"Every format failed: {string.Join("; ", failed)}");
            return new ToolOutput { Features = exported,
                                    Dimensions = failed.Count > 0 ? new List<string> { $"partial_failures: {string.Join("; ", failed)}" } : null };
        });

        private static string ExportExtension(string format) => format == "IGES" ? "igs" : format.ToLowerInvariant();

        private static string ExportFormatOf(string filePath)
        {
            switch (System.IO.Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".step": case ".stp": return "STEP";
                case ".igs": case ".iges": return "IGES";
                case ".stl": return "STL";
                case ".pdf": return "PDF";
                case ".dwg": return "DWG";
                case ".dxf": return "DXF";
                default: throw new ToolFail("BAD_PARAMETER", $"Cannot tell the format from '{filePath}' — give formats (STEP, IGES, STL, PDF, DWG, DXF) or a file_path with one of those extensions.");
            }
        }

        // SaveAs3 to a foreign format; SolidWorks infers it from the extension (its IGES translator only knows .igs —
        // .iges gives error 256). PDF needs the export-options object. Returns the written path.
        private string ExportActive(IModelDoc2 d, string format, string filePath)
        {
            if (format == "IGES" && !filePath.ToLowerInvariant().EndsWith(".igs")) filePath = System.IO.Path.ChangeExtension(filePath, "igs");
            string dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            object data = format == "PDF" ? _solidWorks.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) : null;
            int e = 0, w = 0;
            if (!d.Extension.SaveAs3(filePath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, data, null, ref e, ref w))
                throw new ToolFail("EXPORT_FAILED", $"SaveAs3 failed for format '{format}'. Errors: {e}, Warnings: {w}. Ensure the output path is writable and the format matches the document type.");
            return filePath;
        }

        // -----------------------------------------------------------------------
        // activate_document — switch the active SolidWorks document by title.
        // Lifecycle-ish: changes which open doc subsequent ops target, not geometry. Like ensure_ready it
        // does NOT check/bump state_version (it's process-global and the activated doc's geometry is
        // unchanged). Lets a caller compare against another open part without OS window hacks.
        // -----------------------------------------------------------------------
        public ExecutionResponse ActivateDocument(ToolRequest request) => Run(request, false, p =>
        {
            string title = PStr(p, "title") ?? throw new ToolFail("MISSING_PARAMETER", "title is required (the open document's title, e.g. 'gear' or 'gear.SLDPRT').");
            var doc = Activate(title);
            if (doc == null && !new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }.Any(e => title.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                doc = Activate(title + ".SLDPRT");
            if (doc == null) throw new ToolFail("DOCUMENT_NOT_FOUND", $"Could not activate '{title}'. Pass the exact title of an OPEN document (list_open_documents).");
            ExecLog.Write($"activate_document: '{title}' -> active '{doc.GetTitle()}'");
            return new ToolOutput();
        }, checkState: false, restoreActive: false);

        // Enumerate every solid body's edges with start/end/MIDPOINT 3D coords (+ length). The midpoint
        // is the natural pick-point for coordinate-based selection (edge_flange ex/ey/ez), so the caller
        // never has to guess where an edge is or reverse-derive the extrude direction. Best-effort per edge.
        // One FeatureCut4 invocation with the full (verbose) parameter list factored out, so the cut path
        // can try a direction / normalCut combination cleanly and retry the flipped direction.
        // APPEND-select the i-th solid-body face with a selection Mark — the up-to-surface end-condition
        // reference for FeatureExtrusion3/FeatureCut4. Same GetBodies2(swSolidBody,true) → GetFaces()
        // enumeration analyze_model(faces) reports and create_sketch(on_face, face_index) walks, so the
        // caller's face index means the same thing everywhere. Mark: per the FeatureExtrusion3 remarks
        // (local sldworksapi.chm) "End condition reference entity = Mark 1" (profile 0, direction edge 16,
        // start reference 32, bodies 8) — matches CodeStack's working up-to-surface example.
        private bool SelectFaceByIndexAppend(IModelDoc2 modelDoc, int faceIndex, int mark) =>
            SelectEntity(modelDoc, FaceAt(modelDoc, faceIndex), true, mark);

        // set_document_units — length unit (mm / cm / m / in), decimals, angle unit of the active document
        public ExecutionResponse SetDocumentUnits(ToolRequest request) => Run(request, true, p =>
        {
            var d = ActiveDocOrFail();
            int lin;
            switch (PStr(p, "length", "mm"))
            {
                case "mm": lin = (int)swLengthUnit_e.swMM; break;
                case "cm": lin = (int)swLengthUnit_e.swCM; break;
                case "m": lin = (int)swLengthUnit_e.swMETER; break;
                case "in": lin = (int)swLengthUnit_e.swINCHES; break;
                default: throw new ToolFail("BAD_PARAMETER", "length must be mm, cm, m or in.");
            }
            d.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitSystem, (int)swUnitSystem_e.swUnitSystem_Custom);
            d.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear, lin);
            if (p["decimals"] != null) d.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces, PInt(p, "decimals", 2));
            if (p["angle"] != null) d.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsAngular,
                PStr(p, "angle") == "rad" ? (int)swAngleUnit_e.swRADIANS : (int)swAngleUnit_e.swDEGREES);
            int rl = d.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear);
            return new JObject
            {
                ["document"] = d.GetTitle(),
                ["length"] = ((swLengthUnit_e)rl).ToString().Replace("sw", "").ToLowerInvariant(),
                ["decimals"] = d.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinearDecimalPlaces),
                ["angle"] = d.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsAngular) == (int)swAngleUnit_e.swRADIANS ? "rad" : "deg",
            };
        });

        // Open each file silently, save as <out_dir>\<name>.<format>, close it. One bad file does not stop the batch.
        public ExecutionResponse BatchExportFiles(ToolRequest request) => Run(request, false, p =>
        {
            var files = PStrList(p, "files");
            if (files.Count == 0) throw new ToolFail("MISSING_PARAMETER", "files is required.");
            string fmt = PStr(p, "format", "step").TrimStart('.').ToLowerInvariant();
            string outDir = PStr(p, "out_dir");
            var res = new JArray(); int ok = 0;   // the previously active document comes back via Run (read-only)
            foreach (var f in files)
            {
                var r = new JObject { ["file"] = f };
                try
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    int type = ext == ".sldasm" ? (int)swDocumentTypes_e.swDocASSEMBLY : ext == ".slddrw" ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
                    bool wasOpen = _solidWorks.GetOpenDocumentByName(f) != null;
                    int e = 0, w = 0;
                    var d = _solidWorks.OpenDoc6(f, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly, "", ref e, ref w) as IModelDoc2
                            ?? throw new Exception($"open failed (err {e})");
                    try
                    {
                        string od = outDir ?? Path.GetDirectoryName(f);
                        Directory.CreateDirectory(od);
                        string o = Path.Combine(od, Path.GetFileNameWithoutExtension(f) + "." + fmt);
                        bool s = d.Extension.SaveAs(o, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                            (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (int)swSaveAsOptions_e.swSaveAsOptions_Copy, null, ref e, ref w);
                        r["out"] = o; r["ok"] = s; if (s) ok++; else r["error"] = $"SaveAs err {e}";
                    }
                    finally { if (!wasOpen) _solidWorks.CloseDoc(d.GetTitle()); }   // closed even when SaveAs throws
                }
                catch (Exception ex) { r["ok"] = false; r["error"] = ex.Message; }
                res.Add(r);
            }
            return new JObject { ["done"] = ok, ["failed"] = files.Count - ok, ["results"] = res };
        });

        // VBA macro (.swp/.swb) — ONLY from the macros folder (SWMCP_MACROS_DIR, default <repo>\macros).
        private static string LoadErrorText(int e)
        {
            var parts = new List<string>();
            foreach (swFileLoadError_e v in Enum.GetValues(typeof(swFileLoadError_e)))
                if ((e & (int)v) != 0) parts.Add(v.ToString().Replace("swFile", "").Replace("sw", ""));
            return parts.Count > 0 ? string.Join(", ", parts) : "SolidWorks gave no reason";
        }

        private static string MacrosDir()
        {
            var env = System.Environment.GetEnvironmentVariable("SWMCP_MACROS_DIR");
            if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            // the repository root: the folder with install.ps1 (a file every copy has — ROADMAP.md is not exported)
            for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "install.ps1"))) return Path.Combine(dir.FullName, "macros");
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "macros");
        }

        public ExecutionResponse RunMacro(ToolRequest request) => Run(request, true, p =>
        {
            string root = MacrosDir();
            string m = PStr(p, "macro") ?? throw new ToolFail("MISSING_PARAMETER", "macro (file name in the macros folder) is required.");
            string full = Path.GetFullPath(Path.IsPathRooted(m) ? m : Path.Combine(root, m));
            if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ToolFail("FORBIDDEN", $"Macros run only from {root}.");
            if (!File.Exists(full)) throw new ToolFail("FILE_NOT_FOUND", $"No macro file '{full}'.");
            string module = PStr(p, "module", Path.GetFileNameWithoutExtension(full) + "1");
            string proc = PStr(p, "procedure", "main");
            // args → %TEMP%\SolidWorksMCP\macro_args.txt: "key=value" lines, CRLF, ANSI (VBA's Line Input reads the system
            // code page; a LF-only UTF-8 file came in as one line and silently matched nothing)
            string argsFile = null;
            if (p["args"] is JObject args && args.Count > 0)
            {
                argsFile = Path.Combine(Path.GetTempPath(), "SolidWorksMCP", "macro_args.txt");
                EnsureFolderFor(argsFile);
                File.WriteAllText(argsFile, string.Join("\r\n", args.Properties().Select(a => a.Name + "=" + (a.Value.Type == JTokenType.String ? (string)a.Value : a.Value.ToString(Newtonsoft.Json.Formatting.None)))) + "\r\n",
                    System.Text.Encoding.Default);
            }
            int err = 0;
            bool ok;
            List<string> said;
            using (var watch = new DialogWatch(this))   // a VBA error box blocks SolidWorks (and every later macro) until closed
            {
                ok = _solidWorks.RunMacro2(full, module, proc, (int)swRunMacroOption_e.swRunMacroUnloadAfterRun, out err);
                watch.Dispose();   // the watcher's last round done: RunMacro2 returns as soon as the box is clicked
                said = watch.Said;
            }
            if (!ok && err == 0 && said.Count == 0)
            {
                // "error 0" after an earlier crashed macro: a VBA dialog still open — close it and try once more
                said = DismissDialogs();
                if (said.Count > 0)
                    using (var watch = new DialogWatch(this))
                    {
                        ok = _solidWorks.RunMacro2(full, module, proc, (int)swRunMacroOption_e.swRunMacroUnloadAfterRun, out err);
                        watch.Dispose();
                        said.AddRange(watch.Said);
                    }
                if (!ok && said.Count == 0)
                    throw new ToolFail("VBA_BLOCKED", "RunMacro2 failed with error 0 and no dialog was found — the VBA engine is stuck; ensure_ready restarts SolidWorks.");
            }
            if (!ok) throw new ToolFail("MACRO_FAILED", $"RunMacro2 error {err} ({(swRunMacroError_e)err})" +
                (said.Count > 0 ? $"; VBA said (closed): «{string.Join("» «", said)}»" : " — check module / procedure names") + ".");
            var res = new JObject { ["macro"] = full, ["module"] = module, ["procedure"] = proc };
            if (argsFile != null) res["args_file"] = argsFile;
            if (said.Count > 0) res["dialogs_closed"] = new JArray(said);   // a macro error the macro itself survived
            return res;
        });
    }
}
