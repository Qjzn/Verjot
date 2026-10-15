using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;

internal static class TestMeetingActions {
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    static void Set(object target, string field, object value) { target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value); }
    [STAThread]
    static int Main() {
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var quiet = new SilenceMonitor();
            Check(!quiet.Observe(0, 0, 0, false, false, 60), "Silence must wait for the configured duration");
            Check(!quiet.Observe(59.9, 0, 0, false, false, 60), "No early silence warning");
            Check(quiet.Observe(60, 0, 0, false, false, 60), "Both quiet inputs must warn at the boundary");
            Check(!quiet.Observe(120, 0, 0, false, false, 60), "Sustained silence must not spam warnings");
            Check(!quiet.Observe(121, 0, .02f, true, false, 60), "System input keeps a muted-mic session active");
            Check(!quiet.Observe(122, 0, 0, true, false, 60), "Quiet after real input starts a fresh interval");
            Check(quiet.Observe(182, 0, 0, true, false, 60), "Muted microphone plus quiet system can warn");
            Check(!quiet.Observe(200, 0, 0, false, true, 60), "Whole pause suppresses and resets silence");
            Check(!quiet.Observe(800, 0, 0, false, true, 60), "No warning during a long deliberate pause");
            Check(!quiet.Observe(801, 0, 0, false, false, 60), "Resume gives a fresh silence interval");
            Check(!quiet.Observe(861, .02f, 0, false, false, 60), "Microphone alone counts as input");
            Check(!quiet.Observe(862, 0, 0, false, false, 0), "Zero disables silence warnings");
            quiet.Reset();
            Check(!quiet.Observe(0, .8f, 0, true, false, 5), "A disabled microphone's stale peak cannot hide system silence");
            Check(quiet.Observe(5, .8f, 0, true, false, 5), "Muted microphone is ignored by silence measurement");

            string vault = Path.Combine(MeetingFiles.Home, "notes");
            string session = Path.Combine(MeetingFiles.Home, "work", "20261015-120000");
            Directory.CreateDirectory(vault); Directory.CreateDirectory(session);
            string note = Path.Combine(vault, "original.md"), audio = Path.Combine(vault, "meeting.wav");
            byte[] noteBytes = System.Text.Encoding.UTF8.GetBytes("# 会议记录\n\n手写内容与转录都要保留。\n");
            File.WriteAllBytes(note, noteBytes); File.WriteAllBytes(audio, new byte[] { 1, 2, 3 });
            MeetingFiles.WriteJson(Path.Combine(session, "session.json"), new { state = "recorded", session_dir = session, start_time = "2026-10-15T12:00:00+08:00" });
            MeetingFiles.WriteJson(Path.Combine(session, "status.json"), new { state = "completed", note_path = note,
                merged_audio = new { published_path = audio }, managed_note = new { path = note, start = 17, length = 20, sha256 = "synthetic-hash" } });
            var catalog = new SessionCatalog(MeetingFiles.Home, vault);
            string renamed = MeetingRename.Apply(catalog, session, "产品周会");
            Check(File.Exists(renamed) && !File.Exists(note), "Rename must move the selected MD exactly once");
            Check(Convert.ToBase64String(File.ReadAllBytes(renamed)) == Convert.ToBase64String(noteBytes), "Rename must preserve all note bytes");
            Check(File.ReadAllBytes(audio).Length == 3, "Audio must remain untouched");
            var status = MeetingFiles.ReadJson(Path.Combine(session, "status.json"));
            var metadata = MeetingFiles.ReadJson(Path.Combine(session, "session.json"));
            Check((string)status["note_path"] == renamed, "Result destination must follow the renamed note");
            Check((string)((Dictionary<string, object>)status["managed_note"])["path"] == renamed, "Managed region path must follow rename");
            Check((string)metadata["note_filename"] == "产品周会.md" && (string)metadata["title"] == "产品周会", "Retry filename and meeting title must be persisted");
            Check(catalog.Get(session).State == "completed" && catalog.Get(session).Label.Contains("产品周会"), "Rename must remain completed and appear in history");
            Check(MeetingRename.Apply(catalog, session, "产品周会.md") == renamed, "Repeating a name is safe");
            string occupied = Path.Combine(vault, "已有会议.md"); File.WriteAllText(occupied, "existing user note");
            Reject(delegate { MeetingRename.Apply(catalog, session, "已有会议"); }, "Existing user files cannot be overwritten");
            Check(File.Exists(renamed) && File.ReadAllText(occupied) == "existing user note", "Rejected collision preserves both files");
            foreach (string invalid in new string[] { "", "../outside", "C:\\outside", "CON", "NUL.txt", "a.", "bad\nname", "bad\u202Ename", new string('x', 81) })
                Reject(delegate { MeetingRename.Filename(invalid); }, "Unsafe name accepted: " + invalid.Replace('\n', ' '));
            using (var held = new FileStream(Path.Combine(session, "session.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(delegate { MeetingRename.Apply(catalog, session, "锁定失败测试"); }, "Persistence failure must reject a partially applied rename");
            Check(File.Exists(renamed) && !File.Exists(Path.Combine(vault, "锁定失败测试.md")), "Failed rename must restore the original file");
            Check(catalog.Get(session).NotePath == renamed, "Failed persistence must preserve the catalog destination");
            renamed = MeetingRename.Apply(catalog, session, "Meeting");
            string caseOnly = MeetingRename.Apply(catalog, session, "meeting");
            metadata = MeetingFiles.ReadJson(Path.Combine(session, "session.json"));
            Check((string)metadata["note_filename"] == Path.GetFileName(caseOnly), "Case-only naming must retain the exact retry path spelling");

            string marks = Path.Combine(MeetingFiles.Home, "work", "20261015-120001"); Directory.CreateDirectory(marks);
            var recording = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
            var entries = new List<Dictionary<string, object>>();
            var clock = Stopwatch.StartNew();
            Set(recording, "clock", clock); Set(recording, "DirectoryPath", marks); Set(recording, "bookmarks", entries);
            Set(recording, "metadata", new Dictionary<string, object> { { "bookmarks", entries } });
            double first = recording.AddBookmark(); Thread.Sleep(20); double second = recording.AddBookmark();
            Check(first >= 0 && second > first, "Bookmark positions follow the shared monotonic session clock");
            Check(entries.Count == 2 && (string)entries[1]["label"] == "重点 2", "Bookmark numbering is per session");
            Check(MeetingFiles.ReadJson(Path.Combine(marks, "session.json")).ContainsKey("bookmarks"), "Markers must survive a controller restart");
            Set(recording, "stopping", true);
            Reject(delegate { recording.AddBookmark(); }, "Finished sessions cannot accept new marks");
            using (var panel = new RecorderPanel()) {
                panel.ShowRecording("test mic", "test system"); panel.Show(); Application.DoEvents();
                panel.FlashBookmark(12); Check(panel.Heading.Text == "已标记", "A mark needs lightweight visible feedback");
                panel.SetRecordingPaused(true); Check(panel.Heading.Text == "已暂停", "Whole pause wins over transient mark feedback");
                panel.SetRecordingPaused(false); panel.ShowProcessing("正在转写", "测试");
                Check(panel.Heading.Text == "正在转写", "Mark feedback must not overwrite processing state");
            }
            Console.WriteLine("PASS: " + assertions + " meeting-action assertions; synthetic files and signals, no audio captured");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
