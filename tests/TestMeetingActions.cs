using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

internal static class TestMeetingActions {
    sealed class FakePlayback : IMeetingPlayback {
        public int Disposals;
        public double Duration { get { return 7200; } }
        public double Position { get; set; }
        public bool Playing { get; private set; }
        public Exception Error { get { return null; } }
        public void Play() { Playing = true; }
        public void Pause() { Playing = false; }
        public void Dispose() { Disposals++; }
    }
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

            var playbackRequest = MeetingPlaybackRequest.Parse("pianyan://play?session=20261015-120000&seconds=12.345");
            Check(playbackRequest.SessionName == "20261015-120000" && playbackRequest.Seconds == 12.345, "Playback links retain meeting identity and millisecond position");
            Check(MeetingPlaybackRequest.Parse("pianyan://play/?seconds=604800&session=20261015-120000-ab0123ef").Seconds == 604800,
                "Playback accepts reversed key order and a valid collision suffix at its finite upper boundary");
            foreach (string bad in new[] {
                "https://play?session=20261015-120000&seconds=1", "pianyan://other?session=20261015-120000&seconds=1",
                "pianyan://user@play?session=20261015-120000&seconds=1", "pianyan://play:2?session=20261015-120000&seconds=1",
                "pianyan://play/else?session=20261015-120000&seconds=1", "pianyan://play?session=20261015-120000&seconds=1#run",
                "pianyan://play?session=../other&seconds=1", "pianyan://play?session=%2E%2E&seconds=1",
                "pianyan://play?session=20261015-120000%2fother&seconds=1", "pianyan://play?session=20261015-120000&seconds=NaN",
                "pianyan://play?session=20261015-120000&seconds=Infinity", "pianyan://play?session=20261015-120000&seconds=-1",
                "pianyan://play?session=20261015-120000&seconds=604800.001", "pianyan://play?session=20261015-120000&seconds=1e3",
                "pianyan://play?session=20261015-120000&seconds=1&seconds=2", "pianyan://play?session=20261015-120000&session=20261015-120001&seconds=1",
                "pianyan://play?session=20261015-120000&seconds=1&path=C:\\secret.mp3", "pianyan://play?session=20261015-120000",
                "pianyan://play/../?session=20261015-120000&seconds=1", "pianyan://play??session=20261015-120000&seconds=1",
                "pianyan://play?seconds=1", "pianyan://play?session=20261015-120000&seconds=1\n", "pianyan://play?session=20261015-120000&seconds=1&" })
                Reject(delegate { MeetingPlaybackRequest.Parse(bad); }, "Playback accepted unsafe URI: " + bad.Replace('\n', ' '));
            Check(MeetingPlayback.ResolveAudio(catalog, session, 1) == audio, "Playback resolves only a catalog-owned audio artifact");
            // The audio fixture still has a deliberately invalid three-byte header:
            // building a launch must not create a decoder or scan a long MP3.
            var playbackStart = MeetingPlayback.StartInfo(catalog, session);
            Check(playbackStart.FileName == Application.ExecutablePath && !playbackStart.UseShellExecute && playbackStart.CreateNoWindow,
                "Recent-meeting playback launches only the current EXE directly without a shell");
            Check(playbackStart.Arguments == "--play-uri \"pianyan://play?session=20261015-120000&seconds=0\"" && playbackStart.WorkingDirectory == MeetingFiles.Home,
                "Standalone playback uses one strictly validated quoted URI and the program directory");
            Reject(delegate { MeetingPlayback.ResolveAudio(catalog, Path.Combine(vault, "outside"), 0); }, "Playback cannot address a session outside work");
            foreach (double bad in new[] { -1, double.NaN, double.PositiveInfinity, 604801 })
                Reject(delegate { MeetingPlayback.ResolveAudio(catalog, session, bad); }, "Playback rejects invalid seek positions before opening audio");
            using (var wave = new WaveFileWriter(audio, new WaveFormat(16000, 16, 1))) wave.Write(new byte[64000], 0, 64000);
            using (var reader = new MeetingAudioPlayback(audio)) {
                Check(Math.Abs(reader.Duration - 2) < .0001, "WAV playback reads real synthetic duration without an output device");
                reader.Position = .75;
                Check(Math.Abs(reader.Position - .75) < .0001 && !reader.Playing, "Decoder seeks without recording or playing any audio");
            }
            var fake = new FakePlayback();
            using (var player = new MeetingPlaybackWindow(audio, 12.345, delegate(string path) { Check(path == audio, "Player factory receives resolved audio only"); return fake; })) {
                Check(player.PositionSeconds == 12.345 && !fake.Playing, "Linked player opens paused at requested position");
                player.Show(); Application.DoEvents();
                player.SeekSeconds(10); Check(fake.Position == 10, "Native player applies seek to its bounded decoder");
                player.SeekSeconds(8000); Check(fake.Position == 7200, "Seek past audio end clamps to actual duration");
                Reject(delegate { player.SeekSeconds(double.NaN); }, "Player cannot accept nonfinite seek");
                var button = (Button)typeof(MeetingPlaybackWindow).GetField("toggle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(player);
                button.PerformClick(); Check(fake.Playing && fake.Position == 0, "Playback from end restarts from zero");
                button.PerformClick(); Check(!fake.Playing, "Playback pause is scoped to the local player");
            }
            Check(fake.Disposals == 1, "Closing the player releases its timer, reader and output exactly once");
            double seekTime;
            Check(MeetingPlaybackWindow.TryTime("90:35", out seekTime) && seekTime == 5435, "Minute entry supports a long meeting");
            Check(MeetingPlaybackWindow.TryTime("01:02:03", out seekTime) && seekTime == 3723, "Hour entry has unambiguous positions");
            foreach (string bad in new[] { "-1:00", "01:60", "01:60:00", "999:00:00", "NaN", "1", "1:2" })
                Check(!MeetingPlaybackWindow.TryTime(bad, out seekTime), "Invalid time entry must not move playback: " + bad);
            File.Move(audio, audio + ".removed");
            Reject(delegate { MeetingPlayback.ResolveAudio(catalog, session, 0); }, "Manually removed audio cannot be played through a stale transcript link");
            File.Move(audio + ".removed", audio);

            string marks = Path.Combine(MeetingFiles.Home, "work", "20261015-120001"); Directory.CreateDirectory(marks);
            var recording = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
            var entries = new List<Dictionary<string, object>>();
            var clock = Stopwatch.StartNew();
            Set(recording, "clock", clock); Set(recording, "DirectoryPath", marks); Set(recording, "bookmarks", entries);
            // The no-device fixture bypasses every instance field initializer.
            // Bookmark persistence now snapshots the capture inventory under its lock.
            Set(recording, "gate", new object());
            Set(recording, "sources", Activator.CreateInstance(typeof(RecordingSession).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic).FieldType));
            Set(recording, "captureProblems", new List<string>());
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
