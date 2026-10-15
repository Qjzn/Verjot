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
    static Bitmap Snapshot(RecorderPanel panel, string file) {
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
                { "pause_hotkey", "Ctrl+Alt+Shift+F21" }, { "show_processing_window", false }, { "open_in_obsidian", false }, { "auto_open_completed_note", false } };
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
            var aLink = (LinkLabel)Field(resultPanel, "AudioFile");
            // The exact displayed session travels with the action even if the
            // controller's latest pointer changes to another same-second meeting.
            Set(context, "focusedSession", b);
            Call(aLink, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(aLink.Links[0]));
            Check(located == audioA, "Result action must locate displayed A, never latest same-second B");
            Call(context, "FocusSession", b, true);
            var bLink = (LinkLabel)Field(resultPanel, "TranscriptFile");
            Call(bLink, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(bLink.Links[0]));
            Check(located == noteB, "Transcript action must locate the exact note for B");
            File.Delete(noteB); located = null;
            Call(bLink, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(bLink.Links[0]));
            Check(located == null && !bLink.Visible && resultPanel.Heading.Text.Contains("缺失"), "Moved artifacts must be revalidated and never opened through stale UI");
            Call(context, "FocusSession", a, true);
            Check(((LinkLabel)Field(resultPanel, "AudioFile")).Visible, "A valid earlier meeting must remain directly reachable");
            Console.WriteLine("Completion feedback: " + assertions + " assertions passed; synthetic files only, no audio or external file opening."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (context != null) { Call(context, "RequestExit"); context.Dispose(); } }
    }
}
