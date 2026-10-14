using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class TestRecorderPanel {
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
    static void Snapshot(RecorderPanel panel, string filename) {
        Application.DoEvents();
        using (var bitmap = new Bitmap(panel.ClientSize.Width, panel.ClientSize.Height)) {
            panel.DrawToBitmap(bitmap, panel.ClientRectangle);
            bitmap.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            string output = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(output);
            using (var panel = new RecorderPanel()) {
                panel.ShowRecording("Test microphone", "Test speakers");
                panel.Show();
                Application.DoEvents();
                Check(panel.ClientSize == new Size(186, 68), "Panel must remain 186 x 68");
                Check(panel.TopMost && !panel.ShowInTaskbar, "Panel must be topmost without a taskbar window");
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
                string tip = panel.DevicesTip.GetToolTip(panel.MicrophoneButton);
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
                Check(panel.CollapseButton.Visible, "Processing must retain collapse");
                Check(panel.ClientRectangle.Contains(panel.Heading.Bounds) && panel.ClientRectangle.Contains(panel.Detail.Bounds), "Processing labels must fit the compact panel");
                Snapshot(panel, Path.Combine(output, "panel-processing.png"));
                string longDetail = new string('会', 100) + "识别失败，请从托盘重试对应会议";
                Call(panel, "ShowProcessing", "转写失败", longDetail);
                Check(panel.Detail.AutoEllipsis && panel.DevicesTip.GetToolTip(panel.Detail).Contains(longDetail), "Long processing detail must truncate visually and remain complete in its tooltip");
                Call(panel, "ShowProcessing", "转写中", "识别文字 · 23 / 80 段");
                panel.CollapseButton.PerformClick();
                Check(!panel.Visible, "Collapse must hide the panel");
                panel.ShowRecording("Test microphone", "Test speakers");
                panel.Show();
                Application.DoEvents();
                Check(panel.MicrophoneButton.Visible && panel.StopButton.Visible && panel.AudioLevel.Visible && !panel.Detail.Visible, "A new recording must restore its controls");
                Check(panel.DevicesTip.GetToolTip(panel.AudioLevel).IndexOf("默认设备已变化", StringComparison.Ordinal) < 0, "A new recording must clear prior device warnings");
                Check(panel.ClientSize == new Size(186, 68), "Processing must not resize the panel");
                panel.AllowClose = true;
                panel.Close();
            }
            Console.WriteLine("PASS: " + assertions + " synthetic panel assertions; no audio capture");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
