using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

internal static class TestMeetingExperience {
    static int assertions;
    static readonly List<Process> children = new List<Process>();
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value); }
    static void Call(object target, string name, params object[] args) {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Check(method != null, "Missing meeting operation: " + name);
        try { method.Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static string Fixture(string name, string state) {
        string path = Path.Combine(MeetingFiles.Home, "work", name);
        Directory.CreateDirectory(path);
        MeetingFiles.WriteJson(Path.Combine(path, "session.json"), new { session_dir = path, state = state, start_time = "2026-10-14T12:00:00+08:00" });
        File.WriteAllBytes(Path.Combine(path, "mic.wav"), new byte[] { 1, 2, 3 });
        return path;
    }
    static void Publish(string session, string state, string stage, int done, int total) {
        MeetingFiles.WriteJson(Path.Combine(session, "status.json"), new { state = state, stage = stage, completed = done, total = total, message = stage, updated_at = DateTimeOffset.Now.ToString("o") });
    }
    static void Complete(string session, string note, string audio) {
        Directory.CreateDirectory(Path.GetDirectoryName(note));
        File.WriteAllText(note, "synthetic native test note");
        File.WriteAllBytes(audio, new byte[] { 1, 2 });
        MeetingFiles.WriteJson(Path.Combine(session, "status.json"), new { state = "completed", note_path = note, merged_audio = new { published_path = audio }, message = "完成", updated_at = DateTimeOffset.Now.ToString("o") });
    }
    static Process Child(bool success) {
        Process process = Process.Start(new ProcessStartInfo {
            FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--child " + (success ? "0" : "1"), UseShellExecute = false, CreateNoWindow = true
        });
        children.Add(process); return process;
    }
    static RecordingSession FakeActiveRecording(string session) {
        // No constructor, endpoint or writer is invoked. Stopping skips level reads
        // while retaining the controller's real "another meeting is active" guard.
        var value = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
        Set(value, "DirectoryPath", session); Set(value, "stopping", true);
        return value;
    }
    [STAThread]
    static int Main(string[] args) {
        if (args.Length == 2 && args[0] == "--child") { Thread.Sleep(1800); return int.Parse(args[1]); }
        if (args.Length == 3 && args[1] == "--session") { Thread.Sleep(1800); return 1; }
        MeetingContext context = null;
        try {
            Application.EnableVisualStyles();
            var ctor = typeof(MeetingContext).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(Dictionary<string, object>), typeof(Func<string, Process>), typeof(Action<string, bool>) }, null);
            var config = new Dictionary<string, object> { { "vault_root", Path.Combine(MeetingFiles.Home, "notes") }, { "hotkey", "Ctrl+Alt+Shift+F22" },
                { "pause_hotkey", "Ctrl+Alt+Shift+F23" }, { "show_processing_window", false }, { "open_in_obsidian", true }, { "auto_open_completed_note", true } };
            Directory.CreateDirectory((string)config["vault_root"]);
            if (ctor == null) {
                // Exercise the original controller's actual retry route as RED.
                string failedA = Fixture("20261014-120000", "recorded");
                Publish(failedA, "error", "asr", 23, 80);
                string latestB = Fixture("20261014-120001", "recorded");
                MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new { session_dir = latestB, worker_pid = 0 });
                MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "config.json"), config);
                string pythonFolder = Path.Combine(MeetingFiles.Home, ".venv", "Scripts");
                Directory.CreateDirectory(pythonFolder);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(pythonFolder, "python.exe"), true);
                File.WriteAllText(Path.Combine(MeetingFiles.Home, "meeting_worker.py"), "synthetic native worker fixture");
                context = new MeetingContext();
                ((NotifyIcon)Field(context, "tray")).Visible = false;
                ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
                Call(context, "RetryLastSession");
                var baselineJobs = (List<TranscriptionJob>)Field(context, "jobs");
                Check(baselineJobs.Count == 1, "Baseline synthetic worker must launch");
                baselineJobs[0].Process.WaitForExit(10000);
                Check(baselineJobs[0].Session == failedA, "Failed A retry incorrectly launched latest B: " + Path.GetFileName(baselineJobs[0].Session));
                return 1;
            }
            string launched = null, opened = null;
            int autoOpens = 0;
            Func<string, Process> launcher = delegate(string session) { launched = session; return Child(false); };
            Action<string, bool> opener = delegate(string path, bool automatic) { opened = path; if (automatic) autoOpens++; };
            context = (MeetingContext)ctor.Invoke(new object[] { config, launcher, opener });
            var tray = (NotifyIcon)Field(context, "tray");
            tray.Visible = false;
            ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
            var panel = (RecorderPanel)Field(context, "panel");
            var recent = (ToolStripMenuItem)Field(context, "recent");
            var menu = tray.ContextMenuStrip;
            // Exercise the actual first menu opening. WinForms will not offer
            // an expansion affordance for an initially empty child menu, so
            // calling RefreshRecentMenu directly hides this entry-point bug.
            menu.Show(new Point(20, 20));
            Application.DoEvents();
            Check(menu.Visible, "The actual tray context menu must open");
            Check(recent.HasDropDownItems, "Recent meetings must have an expandable submenu on the first context-menu opening");
            recent.ShowDropDown();
            Application.DoEvents();
            Check(recent.DropDown.Visible, "The initial recent submenu must expand through the native menu");
            Check(recent.DropDownItems.Count == 1 && !recent.DropDownItems[0].Enabled && recent.DropDownItems[0].Text == "尚无会议", "An empty catalog must have a reachable empty-state item");
            menu.Close();
            string a = Fixture("20261014-120000", "recorded");
            Call(context, "LaunchWorker", a);
            Check(launched == a, "Initial job must target A");
            Publish(a, "processing", "asr", 23, 80);
            Call(context, "Tick");
            Check(panel.Heading.Text == "转写 23/80 段", "Real segment progress must be visible");
            Check(!panel.Visible, "Disabled auto-processing window must remain hidden");
            Check(((NotifyIcon)Field(context, "tray")).Text.Contains("23/80"), "Tray must expose progress without relying on balloons");
            panel.Show(); panel.CollapseButton.PerformClick(); Call(context, "Tick");
            Check(!panel.Visible, "Progress updates must respect a collapsed panel");
            Check(panel.ClientSize == new Size(186, 68), "Processing must remain 186 x 68");
            string b = Fixture("20261014-120001", "recorded");
            Call(context, "SaveLastSession", b, null);
            Set(context, "recording", FakeActiveRecording(b));
            Publish(a, "error", "asr", 23, 80);
            children[0].WaitForExit(10000);
            Call(context, "Tick");
            Check(((NotifyIcon)Field(context, "tray")).Text.Contains("正在保存录音"), "Recording B must retain tray priority over A's failure");
            menu.Show(new Point(20, 20));
            Application.DoEvents();
            Check(recent.HasDropDownItems, "Opening the tray menu must refresh newly created meetings");
            recent.ShowDropDown();
            Application.DoEvents();
            Check(recent.DropDown.Visible, "Failed meetings must be reachable through the actual recent submenu");
            ToolStripMenuItem failed = null;
            foreach (ToolStripItem item in recent.DropDownItems) if (item.Text.Contains("转写失败")) failed = item as ToolStripMenuItem;
            Check(failed != null, "Failed A must be reachable in recent meetings");
            ToolStripItem retry = null;
            foreach (ToolStripItem action in failed.DropDownItems) if (action.Text == "重试这场转写") retry = action;
            Check(retry != null, "Failed A must provide a retry action");
            launched = null; retry.PerformClick();
            menu.Close();
            Check(launched == a, "Retrying A's menu action must never launch B");
            var pointer = MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "last-session.json"));
            Check(MeetingFiles.Text(pointer, "session_dir", "") == b, "Retrying A must preserve B as latest meeting");
            Call(context, "RetrySession", a);
            Check(children.Count == 2, "A running retry must not start a duplicate worker");
            Publish(a, "error", "asr", 23, 80);
            children[1].WaitForExit(10000); Call(context, "Tick");
            Set(context, "recording", null);
            string c = Fixture("20261014-120002", "recorded");
            Call(context, "LaunchWorker", c);
            string note = Path.Combine((string)config["vault_root"], "meeting-c.md"), audio = Path.Combine((string)config["vault_root"], "meeting-c.wav");
            Complete(c, note, audio);
            // Replace its synthetic exit-1 process with a successful child while
            // preserving exactly the production completion/persistence path.
            var jobs = (List<TranscriptionJob>)Field(context, "jobs");
            var job = jobs[0]; job.Process.WaitForExit(10000);
            Process successful = Child(true); job.Process = successful;
            ((SessionCatalog)Field(context, "catalog")).SaveWorker(c, successful);
            Complete(c, note, audio);
            Set(context, "recording", FakeActiveRecording(b));
            successful.WaitForExit(10000); Call(context, "Tick");
            Check(autoOpens == 0, "A completion during B must not open Obsidian or steal focus");
            Call(context, "OpenNoteForSession", c);
            Check(opened == note, "Manual open must target the exact completed meeting");
            Set(context, "recording", null);
            Call(context, "OpenNote", note, true);
            Check(autoOpens == 1, "Automatic open may operate when no recording is active");
            config["auto_open_completed_note"] = false;
            Call(context, "OpenNote", note, true);
            Check(autoOpens == 1, "Auto-open setting must suppress automatic focus changes");
            menu.Show(new Point(20, 20));
            Application.DoEvents();
            Check(recent.DropDownItems.Count >= 3, "Each of the three meetings must remain reachable");
            menu.Close();
            // A controller file can become locked after the worker is created.
            // Preserve the live process and use legacy identity, never write error
            // over the worker's output just because persistence failed.
            string d = Fixture("20261014-120003", "recorded");
            Publish(d, "error", "old-attempt", 0, 1);
            byte[] originalStatus = File.ReadAllBytes(Path.Combine(d, "status.json"));
            FileStream controllerLock = null;
            Func<string, Process> lockedLauncher = delegate(string session) {
                controllerLock = new FileStream(Path.Combine(session, "controller.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                launched = session; return Child(false);
            };
            Set(context, "workerLauncher", lockedLauncher);
            Call(context, "LaunchWorker", d);
            Check(((List<TranscriptionJob>)Field(context, "jobs")).Count == 1, "Persistence failure must retain the running worker");
            Check(Convert.ToBase64String(originalStatus) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(d, "status.json"))), "Persistence failure must not overwrite worker status");
            controllerLock.Dispose();
            var restored = ((SessionCatalog)Field(context, "catalog")).Get(d);
            Check(restored.WorkerAlive, "Legacy fallback must preserve identity after controller write failure");
            Call(context, "Tick");
            Check(!panel.Heading.Text.Contains("失败") && !panel.Heading.Text.Contains("完成"), "Stale status from an old attempt must not report final outcome");
            Publish(d, "error", "asr", 0, 1);
            children[children.Count - 1].WaitForExit(10000); Call(context, "Tick");
            Set(context, "workerLauncher", launcher);
            // Reopening the program must surface interruption and offline results,
            // while it only attaches a worker already in progress.
            string interrupted = Fixture("20261014-120004", "recording");
            string offline = Fixture("20261014-120005", "recorded");
            Complete(offline, Path.Combine((string)config["vault_root"], "offline.md"), Path.Combine((string)config["vault_root"], "offline.wav"));
            string running = Fixture("20261014-120006", "recorded");
            Process background = Child(false);
            ((SessionCatalog)Field(context, "catalog")).SaveWorker(running, background);
            int childrenBeforeRestore = children.Count, opensBeforeRestore = autoOpens;
            Call(context, "RestoreSessions");
            Check(children.Count == childrenBeforeRestore, "Startup restore must not launch or upload any old meeting");
            Check(((List<TranscriptionJob>)Field(context, "jobs")).Count == 1, "Startup must attach the existing worker");
            Check(autoOpens == opensBeforeRestore, "Startup results must not automatically open an editor");
            Check(!((SessionCatalog)Field(context, "catalog")).Get(interrupted).CanRetry, "Interrupted recording only offers inspection");
            Check(!((SessionCatalog)Field(context, "catalog")).Get(offline).NeedsAttention, "Offline completion notice must persist as acknowledged");
            Publish(running, "error", "asr", 0, 1); background.WaitForExit(10000); Call(context, "Tick");
            string removed = Fixture("20261014-120007", "recorded");
            Set(context, "focusedSession", removed); Set(context, "focusedRecord", null);
            File.Delete(Path.Combine(removed, "session.json")); File.Delete(Path.Combine(removed, "mic.wav")); Directory.Delete(removed);
            Call(context, "Tick");
            Check(panel.Heading.Text.Contains("检查"), "Removed session must give a safe status instead of crashing the timer");
            Console.WriteLine("Meeting experience: " + assertions + " assertions passed; no audio, model, cloud or external editor.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            if (context != null) {
                var menu = ((NotifyIcon)Field(context, "tray")).ContextMenuStrip;
                menu.Close();
                Application.DoEvents();
                Set(context, "recording", null); Call(context, "RequestExit"); context.Dispose();
                menu.Dispose();
            }
            foreach (var process in children) { try { if (!process.HasExited) process.Kill(); } catch { } process.Dispose(); }
        }
    }
}
