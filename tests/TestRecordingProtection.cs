using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

// Synthetic metadata and exact, GUID-owned fixture files only. This executable
// is built into a fresh validation directory, never the installed program home.
internal static class TestRecordingProtection {
    static int assertions, skipped;
    static readonly string FixtureId = Guid.NewGuid().ToString("N");
    static readonly string Work = Path.Combine(MeetingFiles.Home, "work");
    static int sessionNumber;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CreateSymbolicLink(string link, string target, int flags);

    [STAThread]
    static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "--fake-worker") { Thread.Sleep(1200); return 0; }
        try {
            Application.EnableVisualStyles();
            Directory.CreateDirectory(Work);
            DurationCases();
            StorageFailureCases();
            ControllerCases();
            CleanupCases();
            Console.WriteLine("PASS: " + assertions + " assertions; SKIP: " + skipped +
                "; synthetic files only, no recording, clipboard access or API call");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    static Dictionary<string, object> Metadata(object duration) {
        return new Dictionary<string, object> { { "duration_seconds", duration } };
    }
    static Dictionary<string, object> Pause(object start, object end) {
        return new Dictionary<string, object> { { "start_seconds", start }, { "end_seconds", end } };
    }
    static Dictionary<string, object> Paused(double duration, params object[] pauses) {
        // JSON deserialization is intentional: the production path reads these
        // entries as ArrayList rather than a typed List<Dictionary<...>>.
        return MeetingFiles.Json.Deserialize<Dictionary<string, object>>(
            MeetingFiles.Json.Serialize(new Dictionary<string, object> {
                { "duration_seconds", duration }, { "recording_pauses", pauses }
            }));
    }
    static void DurationCases() {
        Check(RecordingProtection.ShouldDiscard(Metadata(4.999), 5), "4.999 seconds is below the default protection boundary");
        Check(!RecordingProtection.ShouldDiscard(Metadata(5.0), 5), "Exactly 5 seconds must be retained");
        Check(!RecordingProtection.ShouldDiscard(Metadata(5.001), 5), "5.001 seconds must be retained");
        Check(RecordingProtection.ShouldDiscard(Metadata(0), 5), "An explicitly known zero-duration accidental recording can be discarded");
        Check(RecordingProtection.ShouldDiscard(Metadata(9.999), 10), "A custom threshold applies without integer duration rounding");
        Check(!RecordingProtection.ShouldDiscard(Metadata(10), 10), "A custom threshold retains its exact boundary");
        Check(!RecordingProtection.ShouldDiscard(Metadata(2), 1), "A shorter configured guard retains a longer session");
        Check(!RecordingProtection.ShouldDiscard(Metadata(0), 0), "Zero disables protection even for a zero-duration session");
        Check(!RecordingProtection.ShouldDiscard(Metadata(4), 0), "Zero disables protection for normal short sessions");
        Check(!RecordingProtection.ShouldDiscard(null, 0), "Disabled protection does not require any duration metadata");
        Reject(delegate { RecordingProtection.ShouldDiscard(Metadata(1), -1); }, "An invalid negative threshold cannot cause deletion");
        Reject(delegate { RecordingProtection.ShouldDiscard(Metadata(1), 301); }, "An excessive threshold cannot enable unintended cleanup");
        Check(RecordingProtection.ShouldDiscard(Paused(30, Pause(2, 28)), 5), "A whole-session pause is excluded from active captured duration");
        Check(!RecordingProtection.ShouldDiscard(Paused(30, Pause(2, 27)), 5), "Exactly 5 active seconds after a pause is retained");
        Check(!RecordingProtection.ShouldDiscard(Paused(8.008, Pause(0, 3.008)), 5), "Binary floating-point subtraction cannot discard the exact decimal 5-second boundary");
        Check(RecordingProtection.ShouldDiscard(Paused(20, Pause(1, 10), Pause(11, 18)), 5), "Multiple closed whole-session pauses are excluded");
        Check(!RecordingProtection.ShouldDiscard(Paused(20, Pause(1, 9), Pause(10, 17)), 5), "Multiple pauses preserve the exact 5-second active boundary");
        Check(RecordingProtection.ShouldDiscard(Paused(4.999), 5), "An empty pause array behaves like a continuous recording");
        var microphoneOff = Metadata(30);
        microphoneOff["microphone_pauses"] = new object[] { Pause(0, 30) };
        Check(!RecordingProtection.ShouldDiscard(microphoneOff, 5), "Muting only the microphone does not exclude captured computer sound");
        microphoneOff = Paused(30, Pause(2, 28));
        microphoneOff["microphone_pauses"] = "irrelevant to whole-session capture duration";
        Check(RecordingProtection.ShouldDiscard(microphoneOff, 5), "Only whole-session pauses affect the recording guard");

        Reject(delegate { RecordingProtection.ShouldDiscard(null, 5); }, "Missing metadata explicitly blocks destructive cleanup");
        Reject(delegate { RecordingProtection.ShouldDiscard(new Dictionary<string, object>(), 5); }, "Missing duration explicitly blocks destructive cleanup");
        foreach (object invalid in new object[] { null, -0.001, double.NaN, double.PositiveInfinity,
            double.NegativeInfinity, "unknown", true, new object[0], new Dictionary<string, object>() })
            Reject(delegate { RecordingProtection.ShouldDiscard(Metadata(invalid), 5); }, "Invalid duration cannot cause destructive cleanup: " + Convert.ToString(invalid, CultureInfo.InvariantCulture));
        foreach (object invalidPauses in new object[] { null, "not an array", 1, true,
            Pause(0, 1), new object[] { null }, new object[] { "bad" },
            new object[] { new Dictionary<string, object>() },
            new object[] { Pause(-1, 1) }, new object[] { Pause(1, -1) },
            new object[] { Pause(3, 2) }, new object[] { Pause(0, 5) },
            new object[] { Pause(0, null) }, new object[] { Pause("bad", 1) },
            new object[] { Pause(0, double.NaN) }, new object[] { Pause(0, double.PositiveInfinity) },
            new object[] { Pause(0, 3), Pause(2, 4) } }) {
            var metadata = Metadata(4.999);
            metadata["recording_pauses"] = invalidPauses;
            Reject(delegate { RecordingProtection.ShouldDiscard(metadata, 5); }, "Invalid pause metadata must explicitly block destructive cleanup");
        }
    }

    static string Session(string state) {
        sessionNumber++;
        string name = "20991016-" + (120000 + sessionNumber).ToString("000000", CultureInfo.InvariantCulture) + "-" + FixtureId.Substring(0, 8);
        string path = Path.Combine(Work, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fixture path already exists: " + path);
        Directory.CreateDirectory(path);
        MeetingFiles.WriteJson(Path.Combine(path, "session.json"), new {
            state = state, session_dir = path, duration_seconds = 1.0,
            start_time = "2099-10-16T12:00:00+08:00"
        });
        File.WriteAllText(Path.Combine(path, "mic.wav"), "synthetic raw track; not playable audio");
        return path;
    }
    static string State(string path) {
        return MeetingFiles.Text(MeetingFiles.ReadJson(Path.Combine(path, "session.json")), "state", "");
    }
    static object Field(object target, string name) {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    static void Set(object target, string name, object value) {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
    static void Call(object target, string name, params object[] args) {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new Exception("Required controller integration method is absent: " + name);
        try { method.Invoke(target, args); } catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void StorageFailureCases() {
        // Deliberately bypass the constructor: these objects own no endpoint,
        // capture callback, file writer, timer or device-monitor thread.
        int notifications = 0;
        var stopped = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
        Set(stopped, "stopping", true);
        Set(stopped, "failed", new Action<Exception>(delegate { Interlocked.Increment(ref notifications); }));
        Check(!stopped.HasStorageFailure, "An untouched device-free recording fixture has no storage-failure marker");
        Call(stopped, "NotifyStorageFailure", new TimelineWriter.StorageException("synthetic late storage failure", new IOException("synthetic disk full")));
        Check(stopped.HasStorageFailure, "A storage failure arriving after Stop still marks the recording for preservation");
        Check(notifications == 0, "A late storage failure must not enqueue a new stop notification after stopping has begun");
        Call(stopped, "NotifyStorageFailure", new TimelineWriter.StorageException("synthetic duplicate failure", null));
        Check(stopped.HasStorageFailure && notifications == 0, "The late failure marker is monotonic and repeated faults never notify after Stop");
        Call(stopped, "NotifyStorageFailure", new IOException("ordinary later I/O exception"));
        Check(stopped.HasStorageFailure, "An unrelated subsequent exception cannot clear a prior storage failure");

        var ordinary = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
        Set(ordinary, "stopping", true);
        Set(ordinary, "failed", new Action<Exception>(delegate { Interlocked.Increment(ref notifications); }));
        Call(ordinary, "NotifyStorageFailure", new IOException("synthetic endpoint failure, not a storage exception"));
        Check(!ordinary.HasStorageFailure && notifications == 0, "An ordinary I/O exception does not masquerade as a storage failure");
        Call(ordinary, "NotifyStorageFailure", new InvalidOperationException("synthetic wrapper", new IOException("ordinary inner error")));
        Check(!ordinary.HasStorageFailure && notifications == 0, "An unrelated wrapped error cannot set the storage-failure marker");
        Call(ordinary, "NotifyStorageFailure", new InvalidOperationException("synthetic wrapper", new TimelineWriter.StorageException("wrapped storage failure", null)));
        Check(ordinary.HasStorageFailure && notifications == 0, "Wrapped storage failures are preserved even after Stop without emitting another UI callback");
    }
    static void ControllerCases() {
        string vault = Path.Combine(MeetingFiles.Home, "controller-notes-" + FixtureId);
        Directory.CreateDirectory(vault);
        int launches = 0;
        string launchedSession = null;
        Process child = null;
        MeetingContext context = null;
        try {
            var config = new Dictionary<string, object> {
                { "vault_root", vault }, { "hotkey", "Ctrl+Alt+Shift+F22" },
                { "pause_hotkey", "Ctrl+Alt+Shift+F23" }, { "bookmark_hotkey", "Ctrl+Alt+Shift+F20" },
                { "quick_note_hotkey", "Ctrl+Alt+Shift+F24" }, { "show_processing_window", true },
                { "auto_open_completed_note", false }, { "recording_protection_seconds", 5 }, { "auto_transcribe", true }, { "transcription_mode", "local" }
            };
            context = new MeetingContext(config, delegate(string session) {
                launches++; launchedSession = session;
                child = Process.Start(new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--fake-worker",
                    UseShellExecute = false, CreateNoWindow = true
                });
                return child;
            }, delegate(string path, bool automatic) { throw new Exception("A recording-guard test must not open a note"); });
            ((NotifyIcon)Field(context, "tray")).Visible = false;
            ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
            var panel = (RecorderPanel)Field(context, "panel");
            var jobs = (List<TranscriptionJob>)Field(context, "jobs");
            var catalog = (SessionCatalog)Field(context, "catalog");

            string previous = Session("recorded"), cancelled = Session("recorded");
            Set(context, "previousRecordingSession", previous);
            Call(context, "SaveLastSession", previous, null);
            Call(context, "SaveLastSession", cancelled, null);
            RecordingProtection.Discard(cancelled);
            panel.Show(); Application.DoEvents();
            Check(panel.Visible, "The cancellation branch starts from a genuinely visible panel");
            Call(context, "CompleteStoppedRecording", cancelled, true, null);
            Check(launches == 0 && jobs.Count == 0, "A discarded short recording cannot launch or queue a transcription worker");
            Check(!panel.Visible, "Successful short-recording cancellation hides the processing panel");
            Check(!Directory.Exists(cancelled), "Controller completion does not recreate a successfully discarded session");
            Check((string)Field(context, "lastSession") == previous, "Cancellation restores the previous latest-meeting pointer");
            Check(MeetingFiles.Text(MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "last-session.json")), "session_dir", "") == previous,
                "Cancellation also restores the persisted latest-meeting pointer");
            bool catalogContainsCancelled = false;
            foreach (var item in catalog.Snapshot()) if (item.DirectoryPath == cancelled) catalogContainsCancelled = true;
            Check(!catalogContainsCancelled, "Deleted short recordings are absent from actual recent-meeting data");
            Call(context, "RefreshRecentMenu");
            bool menuContainsCancelled = false;
            foreach (ToolStripItem item in ((ToolStripMenuItem)Field(context, "recent")).DropDownItems)
                if (item.Text.Contains(Path.GetFileName(cancelled))) menuContainsCancelled = true;
            Check(!menuContainsCancelled, "The actual recent-meeting menu does not resurrect a discarded session");
            Check(Directory.GetFileSystemEntries(vault).Length == 0, "Successful cancellation publishes neither audio nor Markdown");

            string cleanupFailure = Session("recorded");
            Exception deletionError = null;
            using (var held = new FileStream(Path.Combine(cleanupFailure, "mic.wav"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                try { RecordingProtection.Discard(cleanupFailure); } catch (IOException error) { deletionError = error; }
                Check(deletionError != null && State(cleanupFailure) == "discarded", "The failed-deletion branch uses a real synthetic file lock and durable cancellation");
                panel.Show(); Application.DoEvents();
                Call(context, "CompleteStoppedRecording", cleanupFailure, true, deletionError);
                Check(launches == 0 && jobs.Count == 0, "Failed short-recording cleanup still cannot start or queue transcription");
                Check(!panel.Visible && File.Exists(Path.Combine(cleanupFailure, "mic.wav")), "Failed cleanup leaves retained raw data and hides the processing panel");
                Check(!catalog.Get(cleanupFailure).CanRetry, "Cancelled data retained after a cleanup failure cannot be offered for retry");
                Check(!File.Exists(Path.Combine(cleanupFailure, "status.json")), "Cancellation failure does not manufacture a worker status file");
            }
            Check(Directory.GetFileSystemEntries(vault).Length == 0, "Failed cancellation cleanup also publishes neither audio nor Markdown");

            string uncertain = Session("recorded");
            Call(context, "CompleteStoppedRecording", uncertain, false, new IOException("synthetic invalid duration"));
            Check(launches == 0 && jobs.Count == 0 && File.Exists(Path.Combine(uncertain, "mic.wav")), "Invalid duration never starts a worker or removes the uncertain source recording");

            string retained = Session("recorded");
            Call(context, "CompleteStoppedRecording", retained, false, null);
            Check(launches == 1 && launchedSession == retained && jobs.Count == 1, "A retained stopped recording launches exactly one worker through the same completion route");
            Check(panel.Visible, "Retained recordings continue to respect the configured processing-window option");
            Check(File.Exists(Path.Combine(retained, "mic.wav")), "Launching a retained recording leaves its lossless source available to transcription");
            Check(Directory.GetFileSystemEntries(vault).Length == 0, "The injected harmless worker cannot create actual meeting output");
            Check(child.WaitForExit(10000), "The harmless isolated worker must exit within the test bound");
        } finally {
            if (child != null) { child.WaitForExit(10000); child.Dispose(); }
            if (context != null) {
                var menu = ((NotifyIcon)Field(context, "tray")).ContextMenuStrip;
                Call(context, "RequestExit"); context.Dispose(); menu.Dispose();
            }
        }
    }
    static void CleanupCases() {
        string vault = Path.Combine(MeetingFiles.Home, "protection-notes-" + FixtureId);
        Directory.CreateDirectory(vault);
        string note = Path.Combine(vault, "user-note.md"), published = Path.Combine(vault, "meeting.mp3");
        File.WriteAllText(note, "existing user note must remain byte-for-byte");
        File.WriteAllText(published, "existing published audio must remain byte-for-byte");
        byte[] noteBefore = File.ReadAllBytes(note), audioBefore = File.ReadAllBytes(published);
        string sibling = Session("recorded");
        byte[] siblingBefore = File.ReadAllBytes(Path.Combine(sibling, "session.json"));
        string shortSession = Session("recorded");
        foreach (string file in new[] { "system.wav", "mic_02.wav", "system_02.wav", "mic.seg000000.wav", "system_02.seg000001.wav" })
            File.WriteAllText(Path.Combine(shortSession, file), "synthetic capture segment");
        MeetingFiles.WriteJson(Path.Combine(shortSession, "controller.json"), new { worker_pid = 0 });
        RecordingProtection.Discard(shortSession);
        Check(!Directory.Exists(shortSession), "A guarded short session removes only its own recognized capture files and empty directory");
        Bytes(note, noteBefore, "Published notes outside the private work session are untouched");
        Bytes(published, audioBefore, "Published audio outside the private work session is untouched");
        Bytes(Path.Combine(sibling, "session.json"), siblingBefore, "An adjacent meeting remains byte-for-byte unchanged");
        Check(File.Exists(Path.Combine(sibling, "mic.wav")), "An adjacent meeting's raw audio survives");

        string withError = Session("recorded_with_error");
        RecordingProtection.Discard(withError);
        Check(!Directory.Exists(withError), "A stopped capture with an error can still discard its accidental short recording");

        string unknown = Session("recorded");
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unknown user file");
        Reject(delegate { RecordingProtection.Discard(unknown); }, "An unknown file blocks any destructive cleanup");
        Check(State(unknown) == "recorded" && File.Exists(Path.Combine(unknown, "mic.wav")) && File.ReadAllText(Path.Combine(unknown, "keep.txt")) == "unknown user file", "Rejected unknown-file cleanup preserves metadata and all files");

        string nested = Session("recorded");
        Directory.CreateDirectory(Path.Combine(nested, "child"));
        File.WriteAllText(Path.Combine(nested, "child", "keep.txt"), "nested user file");
        Reject(delegate { RecordingProtection.Discard(nested); }, "A child directory blocks cleanup instead of inviting recursive deletion");
        Check(State(nested) == "recorded" && File.Exists(Path.Combine(nested, "child", "keep.txt")), "Nested user data remains unchanged");

        foreach (string state in new[] { "completed", "error", "queued" }) {
            string status = Session("recorded");
            MeetingFiles.WriteJson(Path.Combine(status, "status.json"), new { state = state, note_path = note });
            Reject(delegate { RecordingProtection.Discard(status); }, "Any existing worker status blocks controller-side discard: " + state);
            Check(State(status) == "recorded" && File.Exists(Path.Combine(status, "mic.wav")), "Worker-owned state preserves its raw track: " + state);
        }
        foreach (string state in new[] { "recording", "processing", "completed", "discarded", "" }) {
            string active = Session(state);
            Reject(delegate { RecordingProtection.Discard(active); }, "Only a newly stopped recording may be discarded: " + state);
            Check(File.Exists(Path.Combine(active, "mic.wav")), "Invalid source state preserves captured audio: " + state);
        }

        string redirect = Session("recorded");
        MeetingFiles.WriteJson(Path.Combine(redirect, "session.json"), new { state = "recorded", session_dir = sibling, duration_seconds = 1 });
        Reject(delegate { RecordingProtection.Discard(redirect); }, "Session metadata cannot redirect destructive cleanup to a sibling");
        Check(File.Exists(Path.Combine(redirect, "mic.wav")) && File.Exists(Path.Combine(sibling, "mic.wav")), "Redirect rejection preserves both sessions");
        string nonSession = Path.Combine(Work, "not-a-session-" + FixtureId);
        Directory.CreateDirectory(nonSession);
        MeetingFiles.WriteJson(Path.Combine(nonSession, "session.json"), new { state = "recorded", session_dir = nonSession, duration_seconds = 1 });
        File.WriteAllText(Path.Combine(nonSession, "mic.wav"), "foreign fixture");
        Reject(delegate { RecordingProtection.Discard(nonSession); }, "A matching parent alone cannot authorize a foreign directory name");
        Check(File.Exists(Path.Combine(nonSession, "mic.wav")), "Foreign work directories are preserved");
        Reject(delegate { RecordingProtection.Discard(vault); }, "Cleanup cannot target the configured notes directory");
        Reject(delegate { RecordingProtection.Discard(Work); }, "Cleanup cannot target the entire work root");

        string locked = Session("recorded");
        using (var held = new FileStream(Path.Combine(locked, "mic.wav"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            Reject(delegate { RecordingProtection.Discard(locked); }, "A locked capture file stops cleanup without forcing deletion");
            Check(State(locked) == "discarded", "Discard intent is durable before any capture deletion, even when deletion fails");
            Check(File.Exists(Path.Combine(locked, "mic.wav")), "A locked raw file remains intact");
            var record = new SessionCatalog(MeetingFiles.Home, vault).Get(locked);
            Check(!record.CanRetry, "A partially cleaned discarded session is never eligible for transcription retry");
        }

        string link = Path.Combine(Work, "20991016-125959-" + FixtureId.Substring(0, 8));
        if (CreateSymbolicLink(link, sibling, 1 | 2)) {
            try {
                Reject(delegate { RecordingProtection.Discard(link); }, "Directory reparse points are rejected before cleanup traversal");
                Check(File.Exists(Path.Combine(sibling, "mic.wav")), "A rejected reparse point cannot remove its target audio");
            } finally { Directory.Delete(link); }
        } else {
            skipped++;
            Console.WriteLine("SKIP: this account cannot create the directory-link fixture");
        }
        string linkedAudio = Session("recorded");
        File.Delete(Path.Combine(linkedAudio, "mic.wav"));
        if (CreateSymbolicLink(Path.Combine(linkedAudio, "mic.wav"), published, 2)) {
            try {
                Reject(delegate { RecordingProtection.Discard(linkedAudio); }, "A reparse capture file blocks cleanup");
                Bytes(published, audioBefore, "An external target of a rejected capture link remains intact");
            } finally { File.Delete(Path.Combine(linkedAudio, "mic.wav")); }
        } else {
            skipped++;
            Console.WriteLine("SKIP: this account cannot create the file-link fixture");
        }
        Bytes(note, noteBefore, "All rejected cleanup paths leave the user note unchanged");
        Bytes(published, audioBefore, "All rejected cleanup paths leave published audio unchanged");
    }
    static void Bytes(string path, byte[] expected, string message) {
        Check(File.Exists(path) && Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(expected), message);
    }
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) {
        try { action(); }
        catch (IOException) { assertions++; return; }
        catch (InvalidOperationException) { assertions++; return; }
        catch (ArgumentOutOfRangeException) { assertions++; return; }
        catch (UnauthorizedAccessException) { assertions++; return; }
        throw new Exception(message);
    }
}
