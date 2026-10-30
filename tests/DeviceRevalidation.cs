using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Windows.Forms;
using NAudio.CoreAudioApi;

// Isolated acceptance harness; never included in the installed product.
internal sealed class DeviceRevalidation : Form {
    readonly Label instruction = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label time = new Label { Dock = DockStyle.Top, Height = 30 };
    readonly Button start = new Button { Text = "开始（允许本机录音及测试音）", Dock = DockStyle.Bottom, Height = 42 };
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 100 };
    readonly List<object> events = new List<object>();
    readonly List<string> failures = new List<string>();
    readonly HashSet<int> played = new HashSet<int>();
    RecordingSession session;
    SoundPlayer player;
    string deviceSnapshot = "";
    int phase = -1, countdown;
    bool finishing, allowClose;
    readonly Stopwatch countdownClock = new Stopwatch();
    readonly string[] phases = {
        "电脑声基线：无需讲话，听一下测试音是否可听。",
        "请完整朗读：这是 Verjot 麦克风测试，今天检查录音和本地转写。\n本阶段有 20 秒，不需要赶时间。",
        "麦克风已关闭，电脑声继续。\n可以说“关闭期间不应录入”，用于核对麦克风关闭是否生效。",
        "麦克风已恢复。\n请说：麦克风恢复后，这句话应该被保留。",
        "请将 Windows 默认播放设备切到可外放的扬声器，并保持。\n稍后播放测试音；麦克风能听见时才能验证真实双路相对延迟。",
        "请将 Windows 默认播放设备切回常用耳机，并保持。\n稍后再次播放测试音。",
        "如有第二支麦克风，可切换 Windows 默认麦克风。\n切换后说：这是切换麦克风后的发言。没有第二支可跳过。",
        "最后一次电脑声测试，随后自动保存。\n结束后请手动恢复你原来使用的播放和麦克风设备。"
    };
    static readonly int[] boundaries = { 15, 35, 45, 60, 85, 110, 135, 150 };
    static readonly int[] playAt = { 2, 36, 68, 95, 136 };

    DeviceRevalidation() {
        Text = "Verjot · 集中设备复验（隔离测试）";
        Size = new Size(650, 300); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10); Padding = new Padding(16); TopMost = true;
        Controls.Add(instruction); Controls.Add(time); Controls.Add(start);
        instruction.Text = "尚未录音。点击开始后倒计时 5 秒，录制约 150 秒。\n\n请先暂停其他音频。需要朗读两句中文、切换播放设备；可选切换麦克风。\n工具不修改音量、默认设备、快捷键或剪贴板，结果仅保存在此实验目录。\n关闭窗口可提前结束，已有实验资料会保留。";
        start.Click += delegate {
            if (session != null || countdownClock.IsRunning) return;
            start.Enabled = false; countdown = 5; countdownClock.Start(); tick.Start();
        };
        tick.Tick += OnTick;
        FormClosing += delegate(object sender, FormClosingEventArgs args) {
            if (allowClose) return;
            if (session != null) { args.Cancel = true; Finish("user_stopped"); }
            else { tick.Stop(); countdownClock.Stop(); }
        };
    }
    void Event(string action, object details) {
        events.Add(new { action = action, at_seconds = session == null ? 0 : session.Elapsed.TotalSeconds, details = details });
    }
    void ObserveDevices() {
        var found = new List<object>();
        using (var devices = new MMDeviceEnumerator()) {
            foreach (DataFlow flow in new DataFlow[] { DataFlow.Render, DataFlow.Capture })
                foreach (Role role in new Role[] { Role.Console, Role.Multimedia, Role.Communications }) {
                    try { using (var device = devices.GetDefaultAudioEndpoint(flow, role))
                        found.Add(new { flow = flow.ToString(), role = role.ToString(), id = device.ID, name = device.FriendlyName }); }
                    catch (Exception error) { found.Add(new { flow = flow.ToString(), role = role.ToString(), error = error.GetType().Name }); }
                }
        }
        string snapshot = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(found);
        if (snapshot != deviceSnapshot) { deviceSnapshot = snapshot; Event("default_devices", found); }
    }
    void OnTick(object sender, EventArgs args) {
        if (finishing) return;
        try {
            if (session == null) {
                int left = Math.Max(0, countdown - (int)countdownClock.Elapsed.TotalSeconds);
                time.Text = "即将开始：" + left + " 秒";
                if (left > 0) return;
                countdownClock.Stop(); ObserveDevices();
                session = new RecordingSession(MeetingConfig.Load(), delegate(Exception error) { lock (failures) failures.Add(error.ToString()); });
                player = new SoundPlayer(Path.Combine(MeetingFiles.Home, "test-input.wav")); player.Load();
                Event("recording_ready", null); deviceSnapshot = ""; ObserveDevices();
            }
            double seconds = session.Elapsed.TotalSeconds;
            int next = 0; while (next < boundaries.Length && seconds >= boundaries[next]) next++;
            if (next >= boundaries.Length) { Finish("duration_reached"); return; }
            if (next != phase) {
                phase = next; instruction.Text = phases[phase]; Event("phase", new { index = phase, text = phases[phase] });
                if (phase == 2 || phase == 3) { session.SetMicrophonePaused(phase == 2); Event(phase == 2 ? "mute_mic" : "resume_mic", null); }
            }
            time.Text = "录音中  " + TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss") + " / 02:30  ·  " + (boundaries[phase] - (int)seconds) + " 秒后下一步";
            foreach (int at in playAt) if (seconds >= at && played.Add(at)) { Event("play_requested", new { target_seconds = at }); player.Play(); }
            if ((int)(seconds * 10) % 10 == 0) ObserveDevices();
        } catch (Exception error) { lock (failures) failures.Add(error.ToString()); Finish("harness_error"); }
    }
    void Finish(string reason) {
        if (finishing) return; finishing = true; tick.Stop();
        if (player != null) { player.Stop(); player.Dispose(); player = null; }
        time.Text = "采集结束，正在保存……"; Event("stop_requested", reason);
        if (session == null) { SaveReport(reason, null); return; }
        session.StopAsync(delegate(Exception error) {
            BeginInvoke(new Action(delegate {
                string path = session.DirectoryPath;
                try { session.Dispose(); } catch (Exception disposal) { lock (failures) failures.Add(disposal.ToString()); }
                if (error != null) lock (failures) failures.Add(error.ToString());
                try { ObserveDevices(); } catch (Exception observation) { lock (failures) failures.Add(observation.ToString()); }
                SaveReport(reason, path);
            }));
        });
    }
    void SaveReport(string reason, string path) {
        MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "device-review.json"), new {
            state = "captured_pending_analysis", reason = reason, session_dir = path, events = events,
            errors = failures.ToArray(), audio_uploaded = false, clipboard_access = false, volume_changed = false,
            default_devices_changed_by_tool = false, human_heard_playback = (object)null,
            human_completed_speech = (object)null, human_completed_device_switches = (object)null,
            limitation = "Instrumented copy records packet metadata only. Raw microphone bytes are not copied around the production mute mask."
        });
        time.Text = "采集已结束，未宣称验收通过";
        instruction.Text = "测试资料已保存，开发侧将检查时间线并执行本地转写。\n请手动恢复原来使用的播放和麦克风设备。\n\n结果目录：" + MeetingFiles.Home;
        allowClose = true; start.Enabled = true; start.Text = "关闭测试窗口";
        start.Click += delegate { Close(); };
    }
    [STAThread] static int Main(string[] args) {
        try { return Run(args); }
        catch (Exception error) {
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-startup-error.txt"), error.ToString());
            return 3;
        }
    }
    static int Run(string[] args) {
        if (args.Length != 1 || args[0] != "--consent-record-and-play") return 64;
        // Never overlap a formal controller. This check does not stop it.
        if (Process.GetProcessesByName("ObsidianMeeting").Length != 0) {
            MessageBox.Show("Verjot 正在运行。请先保存速记、结束会议并等待转写完成，再从托盘退出。不会强制退出正式程序。", "无法开始隔离复验"); return 2;
        }
        var config = MeetingConfig.Load();
        string expected = Path.GetFullPath(Path.Combine(MeetingFiles.Home, "output"));
        if (Path.GetFullPath((string)config["vault_root"]) != expected || (string)config["transcription_mode"] != "local") return 2;
        if (Directory.Exists(Path.Combine(MeetingFiles.Home, "work"))) {
            MessageBox.Show("此复验目录已有采集资料。请重新准备新目录，不覆盖已有录音。", "拒绝覆盖"); return 2;
        }
        Application.EnableVisualStyles(); Application.Run(new DeviceRevalidation()); return 0;
    }
}
