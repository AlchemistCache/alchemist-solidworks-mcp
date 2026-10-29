using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SolidworksExecution.Infrastructure;

namespace SolidworksExecution.Services
{
    // Dialogs left open in the hidden SolidWorks block it: after an old file failed to open (errors 4194304, a repair
    // prompt) every later open / activate failed and close faulted; after a VBA macro error ("Microsoft Visual Basic")
    // run_macro kept answering "error 0"; an import waited 8 h on «Новый документ SOLIDWORKS» (2026-10-28). Nobody can
    // see or click them in the hidden instance, so the server answers them and says what they said. Only in own-instance
    // mode — never the user's SolidWorks.
    public partial class SolidWorksService
    {
        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private const uint WM_GETTEXT = 0x000D, WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, BM_CLICK = 0x00F5;
        private static readonly string[] ButtonOrder = { "end", "завершить", "ok", "ок", "no", "нет", "cancel", "отмена", "close", "закрыть" };
        // the template picker, and a dialog closed on request (close_dialogs): whatever it asks, the answer that does nothing
        private static readonly string[] CancelOrder = { "cancel", "отмена", "no", "нет", "close", "закрыть", "end", "завершить", "ok", "ок" };

        private static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(1024);
            SendMessageTimeout(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb, 0x0002 /* SMTO_ABORTIFHUNG */, 500, out _);
            return sb.ToString().Trim();
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // The visible dialogs (#32770) of process pid.
        private static List<IntPtr> DialogsOf(uint pid)
        {
            var found = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid == pid && IsWindowVisible(h) && ClassOf(h) == "#32770") found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // A dialog as "title: text / text", its buttons by lower-case caption (only the visible ones if visibleOnly), and
        // whether it is the template picker «New SOLIDWORKS Document»: a template list on tabs (advanced view) or that
        // title (novice view), with OK and Cancel.
        private static string Describe(IntPtr h, bool visibleOnly, out List<KeyValuePair<string, IntPtr>> buttons, out bool templatePicker)
        {
            var parts = new List<string>();
            var found = new List<KeyValuePair<string, IntPtr>>();
            bool list = false, tabs = false;
            EnumChildWindows(h, (c, l) =>
            {
                string cls = ClassOf(c);
                if (cls == "Static" && IsWindowVisible(c)) { string t = WindowText(c); if (t.Length > 0 && parts.Count < 4) parts.Add(t); }
                else if (cls == "Button" && (!visibleOnly || IsWindowVisible(c)))
                    found.Add(new KeyValuePair<string, IntPtr>(WindowText(c).Replace("&", "").Trim().ToLowerInvariant(), c));
                else if (cls == "SysListView32") list = true;
                else if (cls == "SysTabControl32") tabs = true;
                return true;
            }, IntPtr.Zero);
            string title = WindowText(h);
            bool newDocTitle = title.IndexOf("SOLIDWORKS", StringComparison.OrdinalIgnoreCase) >= 0
                               && (title.StartsWith("New ", StringComparison.OrdinalIgnoreCase) || title.StartsWith("Новый ", StringComparison.OrdinalIgnoreCase));
            templatePicker = (list && tabs || newDocTitle)
                             && found.Any(b => b.Key == "ok" || b.Key == "ок") && found.Any(b => b.Key == "cancel" || b.Key == "отмена");
            buttons = found;
            return string.Join(" ", (title + (parts.Count > 0 ? ": " + string.Join(" / ", parts) : ""))
                .Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
        }

        // Close every dialog of the SolidWorks process (or only those); returns "title: text [button]" of each.
        private static List<string> CloseDialogs(uint pid, bool cancelFirst = false, List<IntPtr> only = null)
        {
            var said = new List<string>();
            foreach (var h in only ?? DialogsOf(pid))
            {
                string text = Describe(h, false, out var buttons, out bool picker);
                // the button that ends it: a VBA run-time error box has Continue / End / Debug / Help and ignores Cancel;
                // OK in the template picker would make a document from whichever template happens to be selected
                var press = (cancelFirst || picker ? CancelOrder : ButtonOrder)
                    .Select(n => buttons.FirstOrDefault(b => b.Key == n)).FirstOrDefault(b => b.Value != IntPtr.Zero);
                if (press.Value != IntPtr.Zero) PostMessage(press.Value, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                else
                {
                    PostMessage(h, WM_COMMAND, (IntPtr)2 /* IDCANCEL */, IntPtr.Zero);
                    PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
                said.Add($"{text} [{press.Key ?? "closed"}]");
            }
            if (said.Count > 0) ExecLog.Write("closed SolidWorks dialog(s): " + string.Join(" | ", said));
            return said;
        }

        private uint SwPid()
        {
            try { return (uint)_solidWorks.GetProcessID(); } catch { return 0; }
        }

        // The dialogs open right now (own instance only).
        private List<string> DismissDialogs()
        {
            if (!OwnMode) return new List<string>();
            uint pid = SwPid();
            if (pid == 0) return new List<string>();
            Thread.Sleep(300);   // a dialog raised by the failing call may still be opening
            return CloseDialogs(pid);
        }

        // For /health and /close_dialogs, which answer OFF the STA thread (no COM) while a call waits on SolidWorks: the
        // dialogs our own hidden instance shows, and closing them (Cancel / No first). Never the user's SolidWorks, and
        // never a progress window — that is a long call working (a mesh, a solve), not one waiting for an answer.
        private static uint OwnPidOrZero() => OwnMode ? (uint)ReadOwnPid() : 0;

        private static bool HasProgressBar(IntPtr h)
        {
            bool found = false;
            EnumChildWindows(h, (c, l) => { if (ClassOf(c) == "msctls_progress32") found = true; return !found; }, IntPtr.Zero);
            return found;
        }

        private static List<IntPtr> WaitingDialogsOf(uint pid) => DialogsOf(pid).Where(h => !HasProgressBar(h)).ToList();

        public static List<string> OwnDialogs()
        {
            uint pid = OwnPidOrZero();
            return pid == 0 ? new List<string>() : WaitingDialogsOf(pid).Select(h => Describe(h, true, out _, out _)).ToList();
        }

        public static List<string> CloseOwnDialogs()
        {
            uint pid = OwnPidOrZero();
            return pid == 0 ? new List<string>() : CloseDialogs(pid, cancelFirst: true, only: WaitingDialogsOf(pid));
        }

        // What nobody will answer in the hidden instance, once it has stayed open afterSeconds:
        // - a plain message box — one button, OK — gets OK ("storage for study … not found" each time a part with an unsaved
        //   Simulation study was activated or closed: activate_document 197 s, close_document 627 s until pressed by hand);
        // - the template picker «New SOLIDWORKS Document» gets Cancel (an import with «prompt user to select document
        //   template» on waited 8 h on it, 2026-10-28) — the call then fails and says so.
        // Progress windows (Cancel), questions (Yes / No) are left alone.
        private static List<string> AnswerUnseen(uint pid, Dictionary<IntPtr, DateTime> seen, double afterSeconds)
        {
            var now = DateTime.UtcNow;
            var open = DialogsOf(pid);
            foreach (var k in seen.Keys.Where(k => !open.Contains(k)).ToList()) seen.Remove(k);
            var said = new List<string>();
            foreach (var h in open)
            {
                string text = Describe(h, true, out var buttons, out bool picker);
                var press = picker ? buttons.FirstOrDefault(b => b.Key == "cancel" || b.Key == "отмена")
                          : buttons.Count == 1 && (buttons[0].Key == "ok" || buttons[0].Key == "ок") ? buttons[0]
                          : default(KeyValuePair<string, IntPtr>);
                if (press.Value == IntPtr.Zero) continue;
                if (!seen.TryGetValue(h, out var since)) { seen[h] = now; continue; }
                if ((now - since).TotalSeconds < afterSeconds) continue;
                PostMessage(press.Value, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                seen.Remove(h);
                said.Add($"{text} [{press.Key}]");
            }
            if (said.Count > 0) ExecLog.Write("answered SolidWorks dialog(s): " + string.Join(" | ", said));
            return said;
        }

        // Runs beside every tool call in our own instance: answers what AnswerUnseen knows once it has stayed open 3 s.
        private sealed class MessageBoxWatch : IDisposable
        {
            private readonly Thread _thread;
            // a wait the end of the call cuts short: a plain Sleep(1000) held every call until the second was up
            private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
            private readonly List<string> _said = new List<string>();

            public MessageBoxWatch(SolidWorksService svc)
            {
                uint pid = OwnMode ? svc.SwPid() : 0;
                if (pid == 0) return;
                var seen = new Dictionary<IntPtr, DateTime>();
                _thread = new Thread(() =>
                {
                    while (!_stop.Wait(1000))
                    {
                        try { var c = AnswerUnseen(pid, seen, 3.0); lock (_said) _said.AddRange(c); } catch { }
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public List<string> Said { get { lock (_said) return _said.ToList(); } }

            public void Dispose()
            {
                _stop.Set();
                try { _thread?.Join(1500); } catch { }
            }
        }

        // Watches for dialogs while a call that may raise them runs (a VBA error box blocks RunMacro2 until closed).
        private sealed class DialogWatch : IDisposable
        {
            private readonly uint _pid;
            private readonly Thread _thread;
            private volatile bool _stop;
            public readonly List<string> Closed = new List<string>();

            public DialogWatch(SolidWorksService svc)
            {
                _pid = OwnMode ? svc.SwPid() : 0;
                if (_pid == 0) return;
                _thread = new Thread(() =>
                {
                    while (!_stop)
                    {
                        Thread.Sleep(400);
                        try { var c = CloseDialogs(_pid); lock (Closed) Closed.AddRange(c.Where(x => !Closed.Contains(x))); } catch { }
                    }
                }) { IsBackground = true };
                _thread.Start();
            }

            public List<string> Said { get { lock (Closed) return Closed.ToList(); } }

            public void Dispose()
            {
                _stop = true;
                try { _thread?.Join(1500); } catch { }
            }
        }
    }
}
