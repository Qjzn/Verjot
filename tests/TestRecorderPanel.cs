using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class TestRecorderPanel {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
    static int assertions;
    static void Check(bool condition, string message) {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    static void Call(RecorderPanel panel, string method, params object[] arguments) {
        MethodInfo target = typeof(RecorderPanel).GetMethod(method);
        Check(target != null, "Missing panel interface: " + method);
        target.Invoke(panel, arguments);
    }
    static bool InputActive(RecorderActionButton button) {
        FieldInfo field = typeof(RecorderActionButton).GetField("InputActive");
        Check(field != null, "Microphone input feedback is unavailable");
        return (bool)field.GetValue(button);
    }
    static void Snapshot(RecorderPanel panel, string filename, bool native = false) {
        panel.Refresh(); Pump(250);
        using (var bitmap = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height)) {
            if (native) using (Graphics graphics = Graphics.FromImage(bitmap)) {
                IntPtr dc = graphics.GetHdc();
                try { Check(PrintWindow(panel.Handle, dc, 0), "Native shared-window capture succeeds"); }
                finally { graphics.ReleaseHdc(dc); }
            } else panel.DrawToBitmap(bitmap, panel.ClientRectangle);
            bitmap.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
            if (native && panel.BrandTitle.Visible) {
                int ink=0;
                for(int y=panel.BrandTitle.Top;y<panel.BrandTitle.Bottom;y++)
                    for(int x=panel.BrandTitle.Left;x<panel.BrandTitle.Right;x++) {
                        Color c=bitmap.GetPixel(x,y); if(c.R<120 && c.G<150 && c.B<180) ink++;
                    }
                Console.WriteLine("Header ink="+ink+" text="+panel.BrandTitle.Text+" bounds="+panel.BrandTitle.Bounds+" font="+panel.BrandTitle.Font+" color="+panel.BrandTitle.ForeColor);
                Check(ink>10,"Native header must visibly paint verbit in every state: "+Path.GetFileName(filename));
            }
            bitmap.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    static void CheckScreenLayout(RecorderPanel panel, string state, int dpi) {
        Console.WriteLine("State=" + state + "; dpi=" + dpi + "; size=" + panel.ClientSize + "; brand=" + panel.BrandTitle.Bounds + "; heading=" + panel.Heading.Bounds + "; font=" + panel.Heading.Font.Height);
        Check(panel.ClientSize == new Size((int)Math.Round(186 * dpi / 96.0), (int)Math.Round(68 * dpi / 96.0)), "Cross-monitor panel retains logical size: " + state);
        foreach (Control control in panel.Controls) {
            if (!control.Visible) continue;
            Check(panel.ClientRectangle.Contains(control.Bounds), "Visible control fits at " + dpi + " DPI: " + control.GetType().Name + " " + state);
            var label = control as Label;
            if (label != null && label.Text.Length != 0) {
                Size line = TextRenderer.MeasureText("国Ag", label.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Check(line.Height <= label.Height, "Label line fits at " + dpi + " DPI: " + label.Text + " required=" + line.Height + " actual=" + label.Height);
            }
        }
        foreach (Control content in new Control[] { panel.Heading, panel.Detail, panel.AudioFile, panel.TranscriptFile, panel.QuickNoteButton, panel.StartButton, panel.SettingsButton })
            if (content.Visible) {
                Check(!content.Bounds.IntersectsWith(panel.BrandMark.Bounds) && !content.Bounds.IntersectsWith(panel.BrandTitle.Bounds), "Content never overlaps brand header: " + state);
                foreach (Control action in new Control[] { panel.QuickNoteButton, panel.StartButton, panel.SettingsButton })
                    if (action.Visible && content is Label) Check(!content.Bounds.IntersectsWith(action.Bounds), "Labels do not overlap quick actions: " + state);
            }
    }
    static void CheckMonitorMatrix(string output) {
        Directory.CreateDirectory(output);
        var failures = new System.Collections.Generic.List<string>();
        using (var panel = new RecorderPanel()) {
            panel.Show(); Application.DoEvents();
            foreach (string state in new[] { "attention", "recording", "processing", "result", "ready" }) {
                typeof(RecorderPanel).GetMethod("RestoreDpiFonts", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, null);
                if (state == "attention") panel.ShowAttention("转写完成 · 录音需检查", "默认设备采集告警，请核对原始录音");
                else if (state == "recording") panel.ShowRecording("Synthetic microphone", "Synthetic system audio");
                else if (state == "processing") panel.ShowWorking("正在转写", "中文识别 · 23 / 80 段");
                else if (state == "result") panel.ShowResult("synthetic", "C:\\Demo\\meeting.mp3", "C:\\Demo\\meeting.md", "C:\\Demo");
                else panel.ShowStatus("verbit 已就绪", "");
                foreach (Screen screen in Screen.AllScreens) {
                    panel.Location = new Point(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80);
                    for (int tick = 0; tick < 12; tick++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    Snapshot(panel, Path.Combine(output, "matrix-" + state + "-" + panel.DeviceDpi + ".png"));
                    try { CheckScreenLayout(panel, state + " actual-monitor", panel.DeviceDpi); }
                    catch (Exception error) { failures.Add(error.Message); Console.WriteLine("FAIL: " + error.Message); }
                }
                foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240 }) {
                    // Exercise production logical geometry/font metrics without
                    // changing Windows scale or pretending these are real monitors.
                    typeof(RecorderPanel).GetMethod("ApplyDpiLayout", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(panel, new object[] { dpi });
                    try { CheckScreenLayout(panel, state + " simulated-metrics", dpi); }
                    catch (Exception error) { failures.Add(error.Message); Console.WriteLine("FAIL: " + error.Message); }
                    Snapshot(panel, Path.Combine(output, "simulated-" + state + "-" + dpi + ".png"));
                }
            }
            panel.AllowClose = true;
        }
        Check(failures.Count == 0, String.Join("\n", failures.ToArray()));
    }
    static void Pump(int milliseconds) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
    }
    static void SettleDock(RecorderPanel panel) {
        var timer = (System.Windows.Forms.Timer)typeof(RecorderPanel).GetField("dockAnimation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do { Pump(5); } while (timer.Enabled && clock.ElapsedMilliseconds < 450);
        Check(!timer.Enabled, "Shared dock settles without a lingering timer (native scheduling allowance: 450 ms)");
    }
    static void CheckSharedDock(string output) {
        string motion = RecorderMotionPreferences.Mode; RecorderMotionPreferences.Mode = "on";
        try {
            foreach (var screen in Screen.AllScreens) using (var panel = new RecorderPanel()) {
                Console.WriteLine("SHARED enable " + screen.DeviceName);
                panel.EnableSharedDock(); panel.ShowStatus("verbit已就绪", "ready"); panel.Show(); Pump(80);
                Console.WriteLine("SHARED shown " + panel.Bounds);
                panel.Location = new Point(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80);
                Console.WriteLine("SHARED move=" + panel.Location + "; anchor=" + typeof(RecorderPanel).GetField("dockAnchor", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel));
                Pump(80);
                Console.WriteLine("SHARED settled=" + panel.Location + "; actual=" + Screen.FromControl(panel).DeviceName + "; dpi=" + panel.DeviceDpi);
                Check(Screen.FromControl(panel).DeviceName == screen.DeviceName, "Shared dock must remain on the monitor it was moved to");
                int unit = (int)Math.Round(40 * panel.DeviceDpi / 96.0); IntPtr handle = panel.Handle;
                Check(panel.ClientSize == new Size(unit, unit), "Shared idle window defaults to logo only at actual monitor DPI");
                Check(!panel.QuickNoteButton.Visible && !panel.BrandTitle.Visible, "Collapsed shared window hides all duplicate branding and tools");
                Check(SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x13), "Isolated fixture can simulate lost native topmost state");
                Check((GetWindowLong(handle,-20)&8)!=0,"The shared dock restores a lost native topmost band immediately on the position event");
                panel.SetDockExpanded(true); SettleDock(panel);
                Check((GetWindowLong(handle,-20)&8)!=0,"Explicit expansion restores native topmost even when the managed TopMost property stayed true");
                Console.WriteLine("SHARED expanded " + panel.Bounds + "; target=" + typeof(RecorderPanel).GetField("dockExpanded", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel) + "; progress=" + typeof(RecorderPanel).GetField("dockProgress", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel) + "; tools=" + panel.QuickNoteButton.Visible + "/" + panel.StartButton.Visible + "/" + panel.SettingsButton.Visible);
                Check(panel.Width == (int)Math.Round(186*panel.DeviceDpi/96.0) && panel.Height == (int)Math.Round(68*panel.DeviceDpi/96.0) && panel.QuickNoteButton.Visible && panel.StartButton.Visible && panel.SettingsButton.Visible, "The shared window expands to a brand header and common actions");
                Snapshot(panel, Path.Combine(output, "shared-idle-" + panel.DeviceDpi + ".png"), true);
                int logoResizes = 0, actionMoves = 0;
                panel.BrandMark.SizeChanged += delegate { logoResizes++; };
                panel.QuickNoteButton.LocationChanged += delegate { actionMoves++; };
                for (int i = 0; i < 5; i++) panel.ShowStatus("verbit已就绪", "ready");
                Check(logoResizes == 0 && actionMoves == 0, "Repeated idle polling must not resize the logo or move hovered actions");
                Check(panel.Handle == handle && typeof(RecorderPanel).Assembly.GetType("QuickNoteCompact") == null, "No independent minimized-note form remains in the application");
                panel.ShowRecording("Synthetic microphone", "Synthetic system"); SettleDock(panel);
                Check(panel.Handle == handle && panel.MicrophoneButton.Visible && panel.StopButton.Visible && panel.Time.Visible, "Recording reuses the same window and restores the original capture controls");
                Snapshot(panel, Path.Combine(output, "shared-recording-" + panel.DeviceDpi + ".png"), true);
                panel.ShowWorking("正在本地转写", "识别文字"); SettleDock(panel);
                var animation = (System.Windows.Forms.Timer)typeof(RecorderPanel).GetField("dockAnimation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);
                Check(panel.Handle == handle && !panel.Detail.Visible && !panel.Heading.Visible && panel.BrandTitle.Visible && panel.SettingsButton.Visible && !animation.Enabled, "Processing keeps the shared content and settles its expansion timer");
                using (var bitmap = new Bitmap(panel.Width, panel.Height)) {
                    panel.DrawToBitmap(bitmap, panel.ClientRectangle); int blue = 0, y = (int)Math.Round(67 * panel.DeviceDpi / 96.0);
                    for (int x = 0; x < bitmap.Width; x++) { Color pixel = bitmap.GetPixel(x, y); if (pixel.B > pixel.R + 12) blue++; }
                    Check(blue > 5, "The shared content parent must not cover the transcription activity strip");
                    bitmap.Save(Path.Combine(output, "shared-working-" + panel.DeviceDpi + ".png"));
                }
                panel.ToggleDockFromLogo(); SettleDock(panel);
                Check(panel.Visible && panel.Width == unit && panel.Height == unit, "Collapse returns an active shared window to the Logo rather than creating or hiding another window");
                for (int i = 0; i < 5; i++) panel.ShowWorking("正在本地转写", "识别文字");
                Check(panel.Width == unit && !animation.Enabled, "Background status polling never reopens a manually collapsed working window");
                panel.ShowAttention("转写失败 · 可重试", "原录音和恢复资料保留"); panel.SetDockExpanded(true); SettleDock(panel);
                Check(panel.Handle == handle && panel.Heading.Text.Contains("失败") && panel.QuickNoteButton.Visible, "Failure feedback and actions reuse the same shared window");
                Snapshot(panel, Path.Combine(output, "shared-attention-" + panel.DeviceDpi + ".png"), true);
                panel.ShowResult("synthetic", "C:\\Demo\\verbit.mp3", "C:\\Demo\\verbit.md", "C:\\Demo"); panel.SetDockExpanded(true); SettleDock(panel);
                Check(panel.AudioFile.Visible && panel.TranscriptFile.Visible && panel.Handle == handle, "Result paths remain available on the single shared window");
                panel.SetDockExpanded(false); SettleDock(panel);
                panel.Location = new Point(screen.WorkingArea.Right - unit - 4, screen.WorkingArea.Bottom - unit - 4);
                panel.SetDockExpanded(true); SettleDock(panel);
                Check(screen.WorkingArea.Contains(panel.Bounds), "Right/bottom edge expansion stays entirely on the actual screen");
                Point anchor = new Point(panel.Left + panel.BrandMark.Left - unit/5, panel.Top + panel.BrandMark.Top - unit/5);
                panel.SetDockExpanded(false); SettleDock(panel);
                Console.WriteLine("Fitted Logo capsule expected="+anchor+"; actual="+panel.Location);
                Check(Math.Abs(panel.Left-anchor.X)<=1 && Math.Abs(panel.Top-anchor.Y)<=1, "Collapse keeps the expanded Logo within one physical pixel of native DPI rounding");
                anchor=panel.Location;
                panel.SetDockExpanded(true); SettleDock(panel); panel.SetDockExpanded(false); SettleDock(panel);
                Check(panel.Location == anchor,"Subsequent edge open/close cycles do not creep");
                panel.AllowClose = true; panel.Close();
            }
        } finally { RecorderMotionPreferences.Mode = motion; }
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            string output = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(output);
            if (Array.IndexOf(args, "--shared-only") >= 0) { CheckSharedDock(output); Console.WriteLine("PASS shared dock assertions=" + assertions); return 0; }
            if (Array.IndexOf(args, "--screen-matrix") >= 0) { CheckMonitorMatrix(output); Console.WriteLine("PASS monitor matrix: " + assertions); return 0; }
            CheckMonitorMatrix(Path.Combine(output, "dpi-matrix"));
            using (var panel = new RecorderPanel()) {
                panel.ShowRecording("Test microphone", "Test speakers");
                panel.Show();
                Application.DoEvents();
                Check(panel.ClientSize == new Size((int)Math.Round(186 * panel.DeviceDpi / 96.0), (int)Math.Round(68 * panel.DeviceDpi / 96.0)), "Panel must remain 186 x 68");
                Check(panel.TopMost && !panel.ShowInTaskbar, "Panel must be topmost without a taskbar window");
                Check(panel.BrandTitle.Text == "verbit" && panel.BrandMark.Image != null, "The header must show the product logo and new name");
                Size nameSize = TextRenderer.MeasureText(panel.BrandTitle.Text, panel.BrandTitle.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Check(nameSize.Width <= panel.BrandTitle.ClientSize.Width && nameSize.Height <= panel.BrandTitle.ClientSize.Height, "The separate verbit name must be fully readable without clipping");
                Check(panel.BrandMark.Right < panel.BrandTitle.Left, "The logo and name must remain independent, non-overlapping elements");
                Check(Math.Abs((panel.BrandMark.Left + panel.BrandTitle.Right) - panel.ClientSize.Width) <= 2, "The logo and name group must remain centered");
                Check(panel.BrandMark.Image.Width >= panel.BrandMark.Width - 1 && panel.BrandMark.Image.Height >= panel.BrandMark.Height - 1, "The header must not upscale a smaller icon frame");
                Check(typeof(RecorderPanel).GetField("BackButton") == null, "The unused return action must be removed");
                Check(typeof(RecorderPanel).GetField("CollapseButton") == null, "Logo replaces the separate collapse action");
                foreach (Control control in new Control[] { panel.BrandMark, panel.BrandTitle }) {
                    Check(panel.ClientRectangle.Contains(control.Bounds), "Header control must fit the 186 x 68 panel");
                    Check(control.Bottom <= panel.MicrophoneButton.Top, "Header must not overlap recording actions");
                }
                Check(!panel.Heading.Visible, "Recording must not put a whole-meeting label under the microphone");
                using (var stop = new Bitmap(panel.StopButton.Width, panel.StopButton.Height)) {
                    panel.StopButton.DrawToBitmap(stop, panel.StopButton.ClientRectangle);
                    Color center = stop.GetPixel(stop.Width / 2, stop.Height / 2);
                    Check(center.R > 180 && center.G < 100 && center.B < 100, "End button must remain a solid red block without a white symbol");
                }
                Call(panel, "SetInputLevels", .6f, 0f);
                Check(InputActive(panel.MicrophoneButton), "Microphone must respond to its own signal");
                Check(panel.AudioLevel.AccessibleName.Contains("录音输入：有声音"), "Mic-only input must activate the meeting wave");
                Snapshot(panel, Path.Combine(output, "panel-mic-only.png"));
                Call(panel, "SetInputLevels", 0f, .6f);
                Check(!InputActive(panel.MicrophoneButton), "System sound must not activate the microphone");
                Check(panel.AudioLevel.AccessibleName.Contains("录音输入：有声音"), "Meeting wave must also show system signal");
                string tip = panel.DevicesTip.GetToolTip(panel.AudioLevel);
                Check(tip.Contains("Test microphone") && tip.Contains("Test speakers"), "Tooltip must name actual captured devices");
                Check(tip.Contains("麦克风输入：暂无") && tip.Contains("电脑声音输入：有声音"), "Tooltip must distinguish both inputs");
                Snapshot(panel, Path.Combine(output, "panel-system-only.png"));
                panel.SetMicPaused(true);
                Call(panel, "SetInputLevels", .6f, .6f);
                Check(panel.MicrophoneButton.MicrophoneDisabled && !InputActive(panel.MicrophoneButton), "Disabled mic must not show input");
                Check(!panel.Heading.Visible && panel.Heading.Text.Length == 0, "Disabling the mic must not suggest the whole recording is closed");
                Check(panel.MicrophoneButton.AccessibleName.Contains("开启麦克风") && panel.DevicesTip.GetToolTip(panel.MicrophoneButton).Contains("电脑声音继续录制"), "Disabled mic must offer enabling it and explain the continuing system recording");
                Check(panel.AudioLevel.AccessibleName.Contains("有声音"), "Disabling mic must retain computer feedback");
                Snapshot(panel, Path.Combine(output, "panel-mic-disabled.png"));
                Call(panel, "SetInputLevels", .6f, 0f);
                Check(panel.AudioLevel.AccessibleName.Contains("暂无明显声音"), "A disabled microphone must not contribute to the meeting wave");
                panel.SetRecordingPaused(true);
                Call(panel, "SetInputLevels", .6f, .6f);
                Check(panel.Heading.Visible && panel.Heading.Text == "已暂停" && !InputActive(panel.MicrophoneButton), "Whole pause must show its own state and stop both input indicators");
                Check(panel.Heading.Right <= panel.MicrophoneButton.Left, "Whole pause belongs to the left recording area, not under the microphone");
                Check(panel.AudioLevel.AccessibleName.Contains("已暂停"), "System indicator must say whole pause");
                Snapshot(panel, Path.Combine(output, "panel-paused.png"));
                panel.SetRecordingPaused(false);
                Check(panel.MicrophoneButton.MicrophoneDisabled, "Resume must preserve disabled mic preference");
                Check(!panel.Heading.Visible, "Resuming a whole recording must clear its pause label");
                panel.SetMicPaused(false);
                Call(panel, "SetInputLevels", 0f, 0f);
                Check(!panel.Heading.Visible && panel.AudioLevel.AccessibleName.Contains("暂无明显声音"), "Quiet input must not display a redundant recording label or suggest a fault");
                Call(panel, "SetDeviceWarning", "默认设备已变化，当前仍捕获 Test microphone / Test speakers");
                Check(panel.DevicesTip.GetToolTip(panel.AudioLevel).Contains("默认设备已变化"), "Device warning must be reachable on the wave tooltip");
                Check(!panel.Heading.Visible, "A device change warning must not restore the microphone's ambiguous label");
                Call(panel, "ShowProcessing", "转写中", "识别文字 · 23 / 80 段");
                Check(panel.Heading.Visible && panel.Detail.Visible, "Processing title and detail must both be visible");
                Check(!panel.MicrophoneButton.Visible && !panel.StopButton.Visible && !panel.AudioLevel.Visible, "Processing must hide obsolete recording controls");
                Check(panel.BrandMark.Visible, "Processing retains the Logo toggle");
                Check(!panel.QuickNoteButton.Visible && !panel.StartButton.Visible && !panel.SettingsButton.Visible, "Progress must not be obscured by home actions");
                Check(panel.ClientRectangle.Contains(panel.Heading.Bounds) && panel.ClientRectangle.Contains(panel.Detail.Bounds), "Processing labels must fit the compact panel");
                Snapshot(panel, Path.Combine(output, "panel-processing.png"));
                panel.SetQuickActionHotkeys("Ctrl+Shift+J", "Ctrl+Shift+L");
                panel.ShowStatus("verbit已就绪", "ready");
                Check(panel.QuickNoteButton.Visible && panel.StartButton.Visible && panel.SettingsButton.Visible, "Idle must expose the three requested actions");
                Check(!panel.Heading.Visible && !panel.Detail.Visible && !panel.StopButton.Visible, "Idle must show icons without redundant ready text");
                Check(panel.DevicesTip.GetToolTip(panel.QuickNoteButton).Contains("Ctrl+Shift+J") && panel.DevicesTip.GetToolTip(panel.StartButton).Contains("Ctrl+Shift+L"), "Tooltips must follow configured hotkeys");
                Check(panel.DevicesTip.GetToolTip(panel.QuickNoteButton)=="速记 · Ctrl+Shift+J" && panel.DevicesTip.GetToolTip(panel.StartButton)=="录音 · Ctrl+Shift+L" && panel.DevicesTip.GetToolTip(panel.SettingsButton)=="设置", "Action hints contain only action and configured shortcut");
                Check(!panel.DevicesTip.GetToolTip(panel.SettingsButton).Contains("Ctrl") && panel.DevicesTip.GetToolTip(panel.SettingsButton).Contains("设置"), "Settings must have an explanation without an invented hotkey");
                int noteClicks = 0, startClicks = 0, settingsClicks = 0;
                panel.QuickNoteButton.Click += delegate { noteClicks++; };
                panel.StartButton.Click += delegate { startClicks++; };
                panel.SettingsButton.Click += delegate { settingsClicks++; };
                panel.QuickNoteButton.PerformClick(); panel.StartButton.PerformClick(); panel.SettingsButton.PerformClick();
                Check(noteClicks == 1 && startClicks == 1 && settingsClicks == 1, "All home actions must dispatch independently");
                Check(Math.Abs((panel.StartButton.Left - panel.QuickNoteButton.Left) - (panel.SettingsButton.Left - panel.StartButton.Left)) <= 1, "Home icons must be evenly spaced within monitor DPI rounding");
                foreach (Control control in new Control[] { panel.QuickNoteButton, panel.StartButton, panel.SettingsButton })
                    Check(panel.ClientRectangle.Contains(control.Bounds) && control.Top > panel.BrandTitle.Bottom, "Home actions must fit below the header");
                Snapshot(panel, Path.Combine(output, "panel-home.png"));
                int homeVisibilityChanges = 0;
                panel.QuickNoteButton.VisibleChanged += delegate { homeVisibilityChanges++; };
                panel.SettingsButton.VisibleChanged += delegate { homeVisibilityChanges++; };
                for (int i = 0; i < 4; i++) panel.ShowStatus("verbit已就绪", "ready");
                Check(homeVisibilityChanges == 0, "Repeated idle polling must preserve native hover tracking without hiding icons");
                panel.ShowAttention("录音已保存 · 待配置转写", "请在设置中配置本地模型；原始录音已保留");
                Check(panel.SettingsButton.Visible && panel.StartButton.Visible && panel.QuickNoteButton.Visible && panel.Heading.Visible && !panel.Detail.Visible, "Untranscribed results must retain the quick actions and an explicit status");
                Check(panel.Heading.Bottom <= panel.StartButton.Top && panel.DevicesTip.GetToolTip(panel.Heading).Contains("原始录音已保留"), "Attention state must not cover its actions or hide the full explanation");
                Snapshot(panel, Path.Combine(output, "panel-attention.png"));
                int previousChanges = homeVisibilityChanges;
                for (int i = 0; i < 4; i++) panel.ShowAttention("录音已保存 · 待配置转写", "请在设置中配置本地模型；原始录音已保留");
                Check(previousChanges == homeVisibilityChanges, "Attention polling must preserve icon tooltips too");
                string longDetail = new string('会', 100) + "识别失败，请从托盘重试对应会议";
                Call(panel, "ShowProcessing", "转写失败", longDetail);
                Check(panel.Detail.AutoEllipsis && panel.DevicesTip.GetToolTip(panel.Detail).Contains(longDetail), "Long processing detail must truncate visually and remain complete in its tooltip");
                Call(panel, "ShowProcessing", "转写中", "识别文字 · 23 / 80 段");
                panel.ToggleDockFromLogo();
                Check(!panel.Visible, "Collapse must hide the panel");
                panel.ShowRecording("Test microphone", "Test speakers");
                panel.Show();
                Application.DoEvents();
                Check(panel.MicrophoneButton.Visible && panel.StopButton.Visible && panel.AudioLevel.Visible && !panel.Detail.Visible, "A new recording must restore its controls");
                Check(!panel.StartButton.Visible && !panel.SettingsButton.Visible && !panel.QuickNoteButton.Visible, "Recording must hide home actions");
                Check(panel.DevicesTip.GetToolTip(panel.AudioLevel).IndexOf("默认设备已变化", StringComparison.Ordinal) < 0, "A new recording must clear prior device warnings");
                Check(panel.ClientSize == new Size((int)Math.Round(186 * panel.DeviceDpi / 96.0), (int)Math.Round(68 * panel.DeviceDpi / 96.0)), "Processing must not resize the panel");
                panel.AllowClose = true;
                panel.Close();
            }
            CheckSharedDock(output);
            Console.WriteLine("PASS: " + assertions + " synthetic panel assertions; no audio capture");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
