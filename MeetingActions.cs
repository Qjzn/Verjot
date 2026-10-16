using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Globalization;
using System.Diagnostics;
using NAudio.Wave;

// Links carry a meeting identifier, never a filesystem path or command.
internal sealed class MeetingPlaybackRequest {
    public readonly string SessionName;
    public readonly double Seconds;
    MeetingPlaybackRequest(string session, double seconds) { SessionName = session; Seconds = seconds; }
    public static void CheckSeconds(double seconds) {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 604800)
            throw new InvalidOperationException("回听时间必须在 0 到 604800 秒之间。");
    }
    public static MeetingPlaybackRequest Parse(string text) {
        Uri uri;
        if (!string.IsNullOrEmpty(text)) foreach (char ch in text) if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            throw new InvalidOperationException("回听链接不能包含空白或控制字符。");
        if (string.IsNullOrEmpty(text) || text.Length > 256 || !Regex.IsMatch(text, @"\Apianyan://play/?\?", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(text, UriKind.Absolute, out uri) ||
            uri.Scheme != "pianyan" || uri.Host != "play" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            (uri.AbsolutePath != "" && uri.AbsolutePath != "/") || uri.Fragment.Length != 0)
            throw new InvalidOperationException("这不是有效的verbit回听链接。");
        string session = null, seconds = null;
        foreach (string part in uri.Query.Substring(1).Split('&')) {
            if (part.StartsWith("session=", StringComparison.Ordinal) && session == null) session = part.Substring(8);
            else if (part.StartsWith("seconds=", StringComparison.Ordinal) && seconds == null) seconds = part.Substring(8);
            else throw new InvalidOperationException("回听链接包含未知或重复参数。");
        }
        double time;
        if (session == null || !Regex.IsMatch(session, @"\A[0-9]{8}-[0-9]{6}(?:-[0-9a-f]{8})?\z") ||
            seconds == null || !Regex.IsMatch(seconds, @"\A[0-9]{1,6}(?:\.[0-9]{1,3})?\z") ||
            !double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out time))
            throw new InvalidOperationException("回听链接的会议编号或时间无效。");
        CheckSeconds(time);
        return new MeetingPlaybackRequest(session, time);
    }
}

internal interface IMeetingPlayback : IDisposable {
    double Duration { get; }
    double Position { get; set; }
    bool Playing { get; }
    Exception Error { get; }
    void Play();
    void Pause();
}

// Audio is decoded in blocks. MP3 opening builds a frame seek index whose cost
// grows with duration; the independent player keeps that off the recorder UI.
// No shell, network request or system-volume mutation.
internal sealed class MeetingAudioPlayback : IMeetingPlayback {
    readonly AudioFileReader reader;
    WaveOutEvent output;
    volatile Exception playbackError;
    public MeetingAudioPlayback(string path) { reader = new AudioFileReader(path); }
    public double Duration { get { return reader.TotalTime.TotalSeconds; } }
    public double Position { get { return reader.CurrentTime.TotalSeconds; } set { reader.CurrentTime = TimeSpan.FromSeconds(value); } }
    public bool Playing { get { return output != null && output.PlaybackState == PlaybackState.Playing; } }
    public Exception Error { get { return playbackError; } }
    public void Play() {
        if (output == null) {
            var candidate = new WaveOutEvent();
            try { candidate.Init(reader); candidate.PlaybackStopped += delegate(object sender, StoppedEventArgs e) { playbackError = e.Exception; }; output = candidate; }
            catch { candidate.Dispose(); throw; }
        }
        playbackError = null;
        output.Play();
    }
    public void Pause() { if (output != null) output.Pause(); }
    public void Dispose() { if (output != null) { output.Dispose(); output = null; } reader.Dispose(); }
}

internal static class MeetingPlayback {
    public static string ResolveAudio(SessionCatalog catalog, string session, double seconds) {
        MeetingPlaybackRequest.CheckSeconds(seconds);
        var record = catalog.Get(session); // Catalog enforces work/direct-child and owned artifact roots.
        if (record == null || record.WorkerAlive || string.IsNullOrEmpty(record.AudioPath))
            throw new IOException("这场会议尚无可回听的成品录音。");
        string path = record.AudioPath, extension = Path.GetExtension(path);
        MeetingConfig.CheckOrdinaryPath(path);
        if ((!string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)) || !File.Exists(path))
            throw new IOException("这场会议的录音已移除或无法访问。");
        return path;
    }
    public static int RunUri(string uri) {
        var request = MeetingPlaybackRequest.Parse(uri);
        var config = MeetingConfig.Load();
        var catalog = new SessionCatalog(MeetingFiles.Home, MeetingFiles.VaultRoot(config));
        string session = Path.Combine(MeetingFiles.Home, "work", request.SessionName);
        string audio = ResolveAudio(catalog, session, request.Seconds);
        using (var window = new MeetingPlaybackWindow(audio, request.Seconds)) Application.Run(window);
        return 0;
    }
    public static void ShowForSession(SessionCatalog catalog, string session, IWin32Window owner) {
        using (var process = Process.Start(StartInfo(catalog, session))) {
            if (process == null) throw new IOException("无法打开录音回听窗口。");
        }
    }
    internal static ProcessStartInfo StartInfo(SessionCatalog catalog, string session) {
        ResolveAudio(catalog, session, 0); // Metadata/existence checks only; no decoder on the recording UI.
        string name = Path.GetFileName(Path.GetFullPath(session).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string uri = "pianyan://play?session=" + name + "&seconds=0";
        MeetingPlaybackRequest.Parse(uri); // Its strict ASCII grammar excludes quotes, escapes and additional arguments.
        return new ProcessStartInfo(Application.ExecutablePath, "--play-uri \"" + uri + "\"") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = MeetingFiles.Home
        };
    }
}

internal sealed class MeetingPlaybackWindow : Form {
    readonly IMeetingPlayback playback;
    readonly TrackBar timeline = new TrackBar { Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None, Dock = DockStyle.Fill };
    readonly Label position = new Label { AutoSize = true, Dock = DockStyle.Top, Height = 24 };
    readonly TextBox seek = new TextBox { Width = 75 };
    readonly Button toggle = new Button { Text = "播放", AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 200 };
    readonly Font dpiFont = new Font("Microsoft YaHei UI", 9);
    bool updating;
    bool resourcesDisposed;
    public MeetingPlaybackWindow(string audio, double seconds) : this(audio, seconds, path => new MeetingAudioPlayback(path)) { }
    internal MeetingPlaybackWindow(string audio, double seconds, Func<string, IMeetingPlayback> factory) {
        MeetingPlaybackRequest.CheckSeconds(seconds);
        playback = factory(audio);
        try {
            if (double.IsNaN(playback.Duration) || double.IsInfinity(playback.Duration) || playback.Duration <= 0)
                throw new IOException("录音没有有效的可播放时长。");
            Text = "verbit · 录音回听"; Font = dpiFont; AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96); ClientSize = new Size(360, 125); MinimumSize = new Size(340, 160);
            Padding = new Padding(12); StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true;
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false };
            var go = new Button { Text = "定位", AutoSize = true };
            actions.Controls.Add(toggle); actions.Controls.Add(seek); actions.Controls.Add(go);
            Controls.Add(timeline); Controls.Add(position); Controls.Add(actions);
            toggle.Click += delegate {
                try {
                    if (playback.Playing) playback.Pause();
                    else { if (playback.Position >= playback.Duration) playback.Position = 0; playback.Play(); }
                    RefreshPosition();
                } catch (Exception error) { position.Text = "无法播放：" + error.Message; }
            };
            timeline.Scroll += delegate { if (!updating) SeekSeconds(playback.Duration * timeline.Value / timeline.Maximum); };
            go.Click += delegate { SeekText(); };
            seek.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SeekText(); } };
            timer.Tick += delegate {
                if (playback.Error != null) { timer.Stop(); position.Text = "播放设备异常，请关闭后重试。"; }
                else RefreshPosition();
            };
            SeekSeconds(Math.Min(seconds, playback.Duration));
            seek.Text = FormatTime(playback.Position);
            timer.Start();
        } catch { playback.Dispose(); timer.Dispose(); dpiFont.Dispose(); throw; }
    }
    internal double PositionSeconds { get { return playback.Position; } }
    internal void SeekSeconds(double seconds) {
        MeetingPlaybackRequest.CheckSeconds(seconds);
        playback.Position = Math.Min(seconds, playback.Duration);
        RefreshPosition();
    }
    static string FormatTime(double seconds) {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ((int)value.TotalHours > 0 ? ((int)value.TotalHours).ToString("D2") + ":" : "") + value.Minutes.ToString("D2") + ":" + value.Seconds.ToString("D2");
    }
    internal static bool TryTime(string text, out double seconds) {
        seconds = 0;
        if (!Regex.IsMatch(text, @"\A(?:[0-9]{1,3}:)?[0-9]{1,3}:[0-5][0-9]\z")) return false;
        string[] parts = text.Split(':');
        int first = int.Parse(parts[0], CultureInfo.InvariantCulture), next = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (parts.Length == 2) seconds = first * 60 + next;
        else {
            if (next > 59) return false;
            seconds = first * 3600 + next * 60 + int.Parse(parts[2], CultureInfo.InvariantCulture);
        }
        return seconds <= 604800;
    }
    void SeekText() {
        double value;
        if (!TryTime(seek.Text.Trim(), out value)) {
            position.Text = "请输入 分:秒 或 时:分:秒，例如 02:35。"; return;
        }
        SeekSeconds(value);
    }
    void RefreshPosition() {
        updating = true;
        try {
            if (!timeline.Capture) timeline.Value = Math.Max(0, Math.Min(timeline.Maximum, (int)Math.Round(playback.Position / playback.Duration * timeline.Maximum)));
            position.Text = FormatTime(playback.Position) + " / " + FormatTime(playback.Duration);
            toggle.Text = playback.Playing ? "暂停" : "播放";
        } finally { updating = false; }
    }
    protected override void Dispose(bool disposing) {
        if (disposing && !resourcesDisposed) { timer.Stop(); timer.Dispose(); playback.Dispose(); dpiFont.Dispose(); resourcesDisposed = true; }
        base.Dispose(disposing);
    }
}

internal sealed class SilenceMonitor {
    double? quietSince;
    bool warned;
    public void Reset() { quietSince = null; warned = false; }
    public bool Observe(double seconds, float microphone, float system, bool microphoneMuted, bool paused, int limit) {
        if (limit == 0 || paused || (!microphoneMuted && microphone > .003f) || system > .003f) { Reset(); return false; }
        if (!quietSince.HasValue || seconds < quietSince.Value) quietSince = seconds;
        if (warned || seconds - quietSince.Value < limit) return false;
        warned = true;
        return true;
    }
}

internal static class MeetingRename {
    public static string Filename(string title) {
        title = title.Trim().Normalize();
        if (title.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 3);
        if (title.Length == 0 || title.Length > 80 || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            title.EndsWith(".") || title.EndsWith(" ") || Regex.IsMatch(title, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("名称需为 1–80 个字符，不能包含路径、换行或 Windows 保留名称。");
        foreach (char value in title) if (char.IsControl(value) || char.GetUnicodeCategory(value) == System.Globalization.UnicodeCategory.Format)
            throw new InvalidOperationException("会议名称不能包含不可见控制字符。");
        return title + ".md";
    }
    public static string Apply(SessionCatalog catalog, string session, string title) {
        SessionRecord record = catalog.Get(session);
        if (record == null || record.State != "completed" || record.WorkerAlive)
            throw new InvalidOperationException("请等待这场会议转写完成后再命名。");
        string filename = Filename(title);
        string original = record.NotePath;
        string renamed = Path.Combine(Path.GetDirectoryName(original), filename);
        MeetingConfig.CheckOrdinaryPath(original);
        MeetingConfig.CheckOrdinaryPath(renamed);
        bool same = string.Equals(original, renamed, StringComparison.OrdinalIgnoreCase);
        if (!same && (File.Exists(renamed) || Directory.Exists(renamed))) throw new IOException("已有同名文件，请换一个会议名称；原笔记未修改。");
        string metadataPath = Path.Combine(session, "session.json"), statusPath = Path.Combine(session, "status.json");
        var metadata = MeetingFiles.ReadJson(metadataPath);
        var status = MeetingFiles.ReadJson(statusPath);
        string previousMetadata = MeetingFiles.Json.Serialize(metadata), previousStatus = MeetingFiles.Json.Serialize(status);
        metadata["title"] = Path.GetFileNameWithoutExtension(filename);
        metadata["note_filename"] = same ? Path.GetFileName(original) : filename;
        status["note_path"] = same ? original : renamed;
        object managed;
        if (status.TryGetValue("managed_note", out managed) && managed is Dictionary<string, object>) {
            var region = (Dictionary<string, object>)managed;
            if (string.Equals(MeetingFiles.Text(region, "path", ""), original, StringComparison.OrdinalIgnoreCase)) region["path"] = same ? original : renamed;
        }
        bool moved = false;
        try {
            if (!same) { File.Move(original, renamed); moved = true; }
            MeetingFiles.WriteJson(metadataPath, metadata);
            MeetingFiles.WriteJson(statusPath, status);
        } catch {
            // Never overwrite or delete a destination. Restore the exact old
            // metadata if a local persistence step fails after the move.
            try {
                if (moved) File.Move(renamed, original);
                MeetingFiles.WriteJson(metadataPath, MeetingFiles.Json.Deserialize<Dictionary<string, object>>(previousMetadata));
                MeetingFiles.WriteJson(statusPath, MeetingFiles.Json.Deserialize<Dictionary<string, object>>(previousStatus));
            } catch (Exception rollback) { MeetingFiles.Log(rollback); }
            throw;
        }
        return same ? original : renamed;
    }
}

internal sealed class MeetingNameDialog : Form {
    readonly TextBox name = new TextBox { Dock = DockStyle.Top, MaxLength = 83 };
    Font dpiFont;
    public string MeetingTitle { get { return name.Text; } }
    public MeetingNameDialog(string current) {
        SuspendLayout();
        Text = "命名这场会议";
        dpiFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(330, 112);
        Padding = new Padding(12);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Text = "会议名称（转录文件名）", Dock = DockStyle.Top, Height = 25 };
        name.Text = current;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        Controls.Add(name); Controls.Add(label); Controls.Add(actions);
        AcceptButton = save; CancelButton = cancel;
        Shown += delegate { name.Focus(); name.SelectAll(); };
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(false);
    }
    void RestoreDpiFont() {
        var previous = dpiFont;
        dpiFont = new Font("Microsoft YaHei UI", 9 * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont;
        previous.Dispose();
        Padding = new Padding((int)Math.Round(12 * DeviceDpi / 96f));
        foreach (Control child in Controls) {
            if (child is Label) child.Height = (int)Math.Round(25 * DeviceDpi / 96f);
            if (child is FlowLayoutPanel) child.Height = (int)Math.Round(32 * DeviceDpi / 96f);
        }
        ClientSize = new Size((int)Math.Round(330 * DeviceDpi / 96f), (int)Math.Round(112 * DeviceDpi / 96f));
    }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        RestoreDpiFont();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        RestoreDpiFont();
    }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && dpiFont != null) { dpiFont.Dispose(); dpiFont = null; }
    }
}
