using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class TestCompletionFeedback {
    static int assertions;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static object Field(object target, string name) {
        var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Check(field != null, "Missing feedback field: " + name); return field.GetValue(target);
    }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value); }
    static void Call(object target, string name, params object[] arguments) {
        var method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Check(method != null, "Missing feedback operation: " + name);
        try { method.Invoke(target, arguments); } catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void Pump(int milliseconds) {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < end);
    }
    static Bitmap Snapshot(Form panel, string file) {
        var bitmap = new Bitmap(panel.Width, panel.Height); panel.DrawToBitmap(bitmap, panel.ClientRectangle);
        if (file != null) bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png); return bitmap;
    }
    static bool Different(Bitmap a, Bitmap b) {
        int height = Math.Min(a.Height, b.Height), width = Math.Min(a.Width, b.Width);
        int margin = (int)Math.Round(10 * width / 186.0);
        for (int y = (int)Math.Round(59 * height / 68.0); y < height - (int)Math.Round(2 * height / 68.0); y++)
            for (int x = margin; x < width - margin; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return true;
        return false;
    }
    static void NativeLinkClick(LinkLabel link) {
        IntPtr position = new IntPtr(((link.Height / 2) << 16) | 10);
        SendMessage(link.Handle, 0x0200, IntPtr.Zero, position);
        SendMessage(link.Handle, 0x0201, new IntPtr(1), position);
        SendMessage(link.Handle, 0x0202, IntPtr.Zero, position);
    }
    static void AccessibleLinkClick(LinkLabel link) {
        // LinkLabel's parent is StaticText; its child is the actionable link.
        var action = link.AccessibilityObject.GetChild(0);
        Check(action != null && action.Role == AccessibleRole.Link, "A result row must expose an actual accessible link");
        action.DoDefaultAction();
    }
    static void Publish(string session, string note, string audio) {
        Directory.CreateDirectory(session); Directory.CreateDirectory(Path.GetDirectoryName(note)); Directory.CreateDirectory(Path.GetDirectoryName(audio));
        File.WriteAllText(note, "synthetic transcript"); File.WriteAllBytes(audio, new byte[] { 1 });
        MeetingFiles.WriteJson(Path.Combine(session, "session.json"), new { session_dir = session, state = "recorded", start_time = "2026-10-15T12:00:00+08:00" });
        MeetingFiles.WriteJson(Path.Combine(session, "status.json"), new { state = "completed", note_path = note, merged_audio = new { published_path = audio }, message = "完成" });
    }
    [STAThread]
    static int Main(string[] args) {
        MeetingContext context = null;
        try {
            Application.EnableVisualStyles();
            RecorderMotionPreferences.Mode = "on";
            using (var editor = new Form()) using (var notice = new RecorderNotice()) {
                editor.Show(); editor.Activate(); Pump(30);
                IntPtr foreground = GetForegroundWindow();
                notice.PresentWorking("正在合并并编码录音 · 已等待 02:13", editor.Bounds, editor.DeviceDpi);
                Pump(40);
                var activity = (System.Windows.Forms.Timer)Field(notice, "activity");
                var clock = (System.Diagnostics.Stopwatch)Field(notice, "activityClock");
                Check(notice.Visible && notice.IsWorking && !notice.PendingDismiss && activity.Enabled, "MP3 generation has a persistent visible animated notice, without a success timeout");
                Check(GetForegroundWindow() == foreground && notice.TopMost && !notice.ShowInTaskbar, "Export feedback never takes focus or adds a taskbar window");
                using (var first = Snapshot(notice, Path.Combine(MeetingFiles.Home, "export-working-first.png"))) {
                    Pump(180);
                    using (var second = Snapshot(notice, Path.Combine(MeetingFiles.Home, "export-working-second.png")))
                        Check(Different(first, second), "The export activity strip changes across real message-loop frames");
                }
                long before = clock.ElapsedMilliseconds;
                for (int i = 0; i < 6; i++) { notice.PresentWorking("正在校验 MP3 文件 · 已等待 02:14", editor.Bounds, editor.DeviceDpi); Pump(15); }
                Check(clock.ElapsedMilliseconds >= before + 60, "Status polling never resets the animation clock");
                notice.ActivateNotice(); Pump(3100);
                Check(notice.Visible && notice.IsWorking && !notice.PendingDismiss, "A long export cannot disappear on click or the normal three-second receipt timeout");
                foreach (int dpi in new[] { 96, 120, 144, 192 }) {
                    Call(notice, "LayoutReceipt", editor.Bounds, dpi);
                    Check(notice.ClientRectangle.Contains(notice.Caption.Bounds) && notice.ClientRectangle.Contains(notice.LocationText.Bounds), "Export copy fits at DPI " + dpi);
                    using (var image = Snapshot(notice, Path.Combine(MeetingFiles.Home, "export-working-" + dpi + ".png"))) { }
                }
                notice.Hide(); Check(!activity.Enabled, "Hidden export feedback stops its repaint timer");
                RecorderMotionPreferences.Mode = "off";
                notice.PresentWorking("正在校验 MP3 文件 · 已等待 02:15", editor.Bounds, editor.DeviceDpi);
                Check(notice.Visible && notice.IsWorking && !activity.Enabled, "Reduced motion retains a readable static processing state");
                RecorderMotionPreferences.Mode = "on";
                bool opened = false;
                notice.Present("录音已保存，点击查看", "synthetic.mp3", delegate { opened = true; }, editor.Bounds, editor.DeviceDpi);
                notice.EndWorking();
                Check(notice.Visible && !notice.IsWorking && notice.PendingDismiss && !activity.Enabled, "Success replaces progress on the same notice and retains its full three-second lifetime");
                notice.ActivateNotice(); Check(opened && !notice.Visible, "Completion still opens the exact recording action");
                notice.Present("录音已保存，点击查看", "synthetic.mp3", delegate { throw new IOException("测试文件已移动或无法访问"); }, editor.Bounds, editor.DeviceDpi);
                Rectangle noticeBounds = notice.Bounds;
                bool escaped = false;
                try { notice.ActivateNotice(); } catch (IOException) { escaped = true; }
                Check(!escaped && notice.Visible && notice.Caption.Text.Contains("无法打开") && notice.LocationText.Text.Contains("测试文件已移动"), "A stale destination callback never escapes to the UI thread; it shows a recoverable reason without touching the file");
                Check(notice.Bounds == noticeBounds, "Callback failure feedback stays anchored to the same panel instead of jumping above its own old window");
                notice.Present("录音已保存，点击查看", "synthetic.mp3", delegate { opened = true; }, editor.Bounds, editor.DeviceDpi);
                Call(notice, "DismissIfAway", new Point(notice.Left + 8, notice.Top + 8));
                Check(notice.Visible && notice.PendingDismiss, "Reading or approaching a result notice keeps it alive while the pointer is inside");
                Call(notice, "DismissIfAway", new Point(notice.Left - 20, notice.Top - 20));
                Check(!notice.Visible && !notice.PendingDismiss, "An expired notice away from the pointer hides and stops its timer");
                notice.PresentWorking("正在准备录音", editor.Bounds, editor.DeviceDpi);
                var workingTip = (ToolTip)Field(notice, "tip");
                notice.PresentWorking("正在合并并编码录音 · 已等待 02:15", editor.Bounds, editor.DeviceDpi);
                string stableTip = workingTip.GetToolTip(notice.LocationText);
                notice.PresentWorking("正在合并并编码录音 · 已等待 02:16", editor.Bounds, editor.DeviceDpi);
                Check(workingTip.GetToolTip(notice.LocationText) == stableTip, "Elapsed updates do not reset the native hovered tooltip every second");
                Call(notice, "DismissIfAway", new Point(notice.Left - 20, notice.Top - 20));
                Check(notice.Visible && notice.IsWorking, "A queued completion timeout cannot hide a newer in-progress notice");
                notice.Present("MP3 生成失败，点击查看", "原始录音已保留：磁盘写入失败", delegate { }, editor.Bounds, editor.DeviceDpi);
                Check(!notice.IsWorking && !activity.Enabled && notice.LocationText.Text.Contains("磁盘写入失败"), "Export failure stops animation and gives its concrete recovery reason");
            }
            using (var panel = new RecorderPanel()) {
                Call(panel, "ShowWorking", "转写 23/80 段", "10-15 12:00:00"); panel.Show(); Pump(60);
                Check(panel.Heading.Text == "转写 23/80 段", "Loading must preserve actual segment progress");
                var timer = (System.Windows.Forms.Timer)Field(panel, "processingAnimation");
                var preferences = typeof(RecorderPanel).Assembly.GetType("RecorderMotionPreferences");
                Check(preferences != null, "Motion preferences must be shared by recording and processing");
                bool enabled = (bool)preferences.GetProperty("Enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).GetValue(null, null);
                using (var first = Snapshot(panel, Path.Combine(MeetingFiles.Home, "loading-first.png"))) {
                    Pump(170);
                    using (var second = Snapshot(panel, Path.Combine(MeetingFiles.Home, "loading-second.png")))
                        Check(enabled ? timer.Enabled && Different(first, second) : !timer.Enabled, "Loading must animate over time only when system motion is enabled");
                }
                panel.Hide(); Pump(40); Check(!timer.Enabled, "Collapsed loading must not keep a repaint timer running");
                panel.Show(); Pump(40); Check(timer.Enabled == enabled, "Restoring loading must resume the same working state");
                RecorderMotionPreferences.Mode = "off"; Pump(50);
                Check(!timer.Enabled, "Disabling app motion must stop loading without hiding actual progress");
                Check(panel.Heading.Text == "转写 23/80 段", "Reduced motion must preserve real progress information");
                RecorderMotionPreferences.Mode = "on";
                panel.ShowProcessing("转写失败", "托盘选择对应会议重试"); Check(!timer.Enabled, "Failure must stop the loading animation");
                Call(panel, "ShowWorking", "分析说话人", "10-15 12:00:00");
                string root = Path.Combine(MeetingFiles.Home, "notes");
                string session = Path.Combine(MeetingFiles.Home, "work", "20261015-120000-a");
                string audio = Path.Combine(root, "会议记录", "附件", "20261015-120000-a", "meeting.wav");
                string note = Path.Combine(root, "会议记录", "2026-10-15 12-00-00-a.md");
                Call(panel, "ShowResult", session, audio, note, root);
                Check(!timer.Enabled && !panel.Heading.Visible && !panel.Detail.Visible, "Results must replace generic completion text and stop loading");
                var audioLink = (LinkLabel)Field(panel, "AudioFile"); var noteLink = (LinkLabel)Field(panel, "TranscriptFile");
                Check(audioLink.Visible && noteLink.Visible && panel.ClientSize == new Size((int)Math.Round(186 * panel.DeviceDpi / 96.0), (int)Math.Round(68 * panel.DeviceDpi / 96.0)), "Both file destinations must fit the existing panel");
                Check(audioLink.Text.Contains("录音") && audioLink.Text.Contains("meeting.wav") && noteLink.Text.Contains("转录") && noteLink.Text.Contains(".md"), "Result rows must identify both actual artifacts");
                Check(TextRenderer.MeasureText(audioLink.Text, audioLink.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= audioLink.Width, "Rendered compact paths must retain the audio basename within the available width");
                Check(audioLink.AutoEllipsis && panel.DevicesTip.GetToolTip(audioLink).Contains(audio), "Long audio paths must retain the full absolute path in a tooltip");
                Check(noteLink.AccessibleName.Contains(note), "The full transcript location must be accessible");
                Check(audioLink.ContextMenuStrip.Items[0].Text == "复制完整路径", "Long destinations must be copyable");
                int requests = 0; string requestedSession = null; bool requestedAudio = false;
                panel.ArtifactRequested += delegate(string target, bool isAudio) { requests++; requestedSession = target; requestedAudio = isAudio; };
                NativeLinkClick(audioLink);
                Check(requests == 1 && requestedSession == session && requestedAudio, "Native mouse input must activate the exact displayed audio destination");
                NativeLinkClick(noteLink);
                Check(requests == 2 && requestedSession == session && !requestedAudio, "Native mouse input must activate the exact displayed transcript destination");
                AccessibleLinkClick(audioLink);
                Check(requests == 3 && requestedSession == session && requestedAudio, "Accessible audio invocation must reach the artifact handler once");
                AccessibleLinkClick(noteLink);
                Check(requests == 4 && requestedSession == session && !requestedAudio, "Accessible transcript invocation must reach the artifact handler once");
                for (int i = 0; i < 20; i++) panel.ShowResult(session, audio, note, root);
                Check(audioLink.Links.Count == 1 && audioLink.Links[0].Enabled && noteLink.Links.Count == 1 && noteLink.Links[0].Enabled, "Repeated result polling must retain both enabled native links");
                NativeLinkClick(audioLink); AccessibleLinkClick(noteLink);
                Check(requests == 6 && requestedSession == session && !requestedAudio, "Native and accessible result entry points must remain active after polling");
                using (var result = Snapshot(panel, Path.Combine(MeetingFiles.Home, "result-paths.png"))) { }
                panel.ShowRecording("test mic", "test speakers");
                Check(!audioLink.Visible && !noteLink.Visible && !timer.Enabled, "A new recording must remove previous result actions");
                Check(!panel.Heading.Visible, "A new recording must keep the redundant recording label hidden");
                panel.SetRecordingPaused(true);
                Check(panel.Heading.Visible && panel.Heading.Text == "已暂停", "Result-to-recording transition must still restore the whole-pause label when needed");
                panel.AllowClose = true; panel.Close();
            }
            var config = new Dictionary<string, object> { { "vault_root", Path.Combine(MeetingFiles.Home, "notes") }, { "hotkey", "Ctrl+Alt+Shift+F20" },
                { "pause_hotkey", "Ctrl+Alt+Shift+F21" }, { "quick_note_hotkey", "Ctrl+Alt+Shift+F24" }, { "show_processing_window", false }, { "open_in_obsidian", false }, { "auto_open_completed_note", false } };
            string a = Path.Combine(MeetingFiles.Home, "work", "20261015-120000-a"), b = Path.Combine(MeetingFiles.Home, "work", "20261015-120000-b");
            string rootPath = (string)config["vault_root"], noteA = Path.Combine(rootPath, "会议记录", "a.md"), audioA = Path.Combine(rootPath, "会议记录", "附件", "a", "meeting.wav");
            string noteB = Path.Combine(rootPath, "会议记录", "b.md"), audioB = Path.Combine(rootPath, "会议记录", "附件", "b", "meeting.wav");
            Publish(a, noteA, audioA); Publish(b, noteB, audioB);
            context = new MeetingContext(config, null, delegate(string path, bool automatic) { throw new Exception("Must not auto-open a note"); });
            ((NotifyIcon)Field(context, "tray")).Visible = false; ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
            string located = null;
            Set(context, "artifactLocator", new Action<string>(delegate(string path) { located = path; }));
            var resultPanel = (RecorderPanel)Field(context, "panel");
            Call(context, "FocusSession", a, true);
            Pump(220);
            var catalog = (SessionCatalog)Field(context, "catalog");
            Call(context, "AudioReceipt", catalog.Get(a));
            var receipt = (RecorderNotice)Field(context, "receipt");
            Check(receipt.Visible && receipt.LocationText.Text == audioA, "The shared dock exposes the published destination in its transient notice");
            // The exact displayed session travels with the action even if the
            // controller's latest pointer changes to another same-second meeting.
            Set(context, "focusedSession", b);
            receipt.ActivateNotice();
            Check(located == audioA, "Result action must locate displayed A, never latest same-second B");
            Call(context, "FocusSession", b, true);
            Pump(220);
            Call(context, "LocateArtifactForSession", b, false);
            Check(located == noteB, "Transcript action must locate the exact note for B");
            File.Delete(noteB); located = null;
            Call(context, "LocateArtifactForSession", b, false);
            Check(located == null && !resultPanel.AudioFile.Visible && !resultPanel.TranscriptFile.Visible,
                "A deleted transcript is rejected without restoring permanent result links in the shared dock");
            Call(context, "OpenSessionFolder", b);
            Check(located == audioB, "The remaining recording is still reachable through the recent-meeting destination action");
            Call(context, "FocusSession", a, true);
            Pump(220);
            Check(resultPanel.QuickNoteButton.Visible && resultPanel.StartButton.Visible && resultPanel.SettingsButton.Visible,
                "An earlier meeting preserves the shared dock's common actions instead of permanent paths");
            Call(context, "AudioReceipt", catalog.Get(a));
            File.Delete(audioA); located = null;
            receipt.ActivateNotice();
            Check(located == null, "A recording deleted after its notice appeared must be revalidated at click time");
            Call(context, "FocusSession", a, true);
            Pump(220);
            Check(!resultPanel.AudioFile.Visible && !resultPanel.TranscriptFile.Visible && resultPanel.StartButton.Visible,
                "Transcript-only completion keeps the shared dock compact and ready to record");
            Call(context, "LocateArtifactForSession", a, true);
            Check(located == null, "An explicit removed-audio action must not invoke an artifact locator");
            Call(context, "OpenSessionFolder", a);
            Check(located == noteA, "Remaining transcript must stay reachable after audio deletion");
            var jobs = (List<TranscriptionJob>)Field(context, "jobs");
            var exporting = new TranscriptionJob { Session = a, AudioExport = true, WorkerStartedUtc = DateTime.UtcNow.AddSeconds(-42) };
            receipt.Hide(); resultPanel.Hide(); Set(context, "focusedSession", a); jobs.Add(exporting);
            try {
                Call(context, "RefreshFeedback");
                Check(receipt.Visible && receipt.IsWorking && receipt.Caption.Text == "正在生成 MP3" && receipt.LocationText.Text.Contains("00:42"), "The real controller shows export feedback immediately, before the child publishes a status, even with the dock hidden");
                Check(resultPanel.Heading.Text == "正在生成 MP3" && !resultPanel.Visible, "MP3 export is never mislabeled as preparing transcription or forced to expand");
                exporting.LastMessage = "正在校验 MP3 文件";
                Call(context, "RefreshFeedback");
                Check(receipt.LocationText.Text.Contains("正在校验 MP3 文件"), "Export stages reach the real visible feedback path");
                receipt.Present("录音已保存，点击查看", "synthetic.mp3", delegate { }, resultPanel.Bounds, resultPanel.DeviceDpi);
                Call(context, "RefreshFeedback");
                Check(!receipt.IsWorking && receipt.PendingDismiss, "A concurrent export does not overwrite another meeting's result notice");
            } finally { jobs.Remove(exporting); }
            receipt.PresentWorking("正在准备录音", resultPanel.Bounds, resultPanel.DeviceDpi);
            Call(context, "RefreshFeedback");
            Check(!receipt.IsWorking && !receipt.Visible, "Clearing the last export stops the persistent feedback");
            Console.WriteLine("Completion feedback: " + assertions + " assertions passed; synthetic files only, no audio or external file opening."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (context != null) { Call(context, "RequestExit"); context.Dispose(); } }
    }
}
