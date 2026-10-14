using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

[assembly: System.Reflection.AssemblyTitle("听记")]
[assembly: System.Reflection.AssemblyProduct("听记 Tingji")]
[assembly: System.Reflection.AssemblyDescription("本地中文会议录音与转写")]

internal static class MeetingBrand {
    public const string Name = "听记";
    public static readonly Icon IdleIcon = LoadIcon("Tingji.Idle.ico");
    public static readonly Icon RecordingIcon = LoadIcon("Tingji.Recording.ico");
    static Icon LoadIcon(string resource) {
        using (var stream = typeof(MeetingBrand).Assembly.GetManifestResourceStream(resource)) {
            if (stream == null) throw new IOException("听记图标资源缺失，请运行 Build.ps1 重新构建。");
            using (var icon = new Icon(stream)) return (Icon)icon.Clone();
        }
    }
}

internal static class MeetingFiles {
    public static readonly string Home = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    public static Dictionary<string, object> ReadJson(string path) {
        if (!File.Exists(path)) return new Dictionary<string, object>();
        return Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
    }
    public static string Text(Dictionary<string, object> data, string key, string fallback) {
        object value;
        return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
    }
    public static string VaultRoot(Dictionary<string, object> config) {
        string path = Text(config, "vault_root", "");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            throw new InvalidOperationException("请复制 config.example.json 为 config.json，并设置 Obsidian 笔记库的完整路径。");
        return Path.GetFullPath(path);
    }
    public static void WriteJson(string path, object value) {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Json.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
    public static void Log(Exception error) {
        try { lock (typeof(MeetingFiles)) File.AppendAllText(Path.Combine(Home, "recorder.log"), DateTimeOffset.Now.ToString("o") + " " + error + Environment.NewLine, new UTF8Encoding(false)); }
        catch { }
    }
}

// Audio callbacks are absent while WASAPI loopback is silent. Preserve that
// wall-clock gap in the WAV, including leading silence and the stop-time tail.
internal sealed class TimelineWriter : IDisposable {
    readonly object gate = new object();
    readonly WaveFileWriter writer;
    readonly int byteRate;
    readonly int blockAlign;
    readonly byte[] silence;
    readonly string path;
    readonly List<long[]> pausedRanges = new List<long[]>();
    long written;
    long endByteBudget = long.MaxValue;
    bool closed;
    double previousCallbackMs;
    bool hadCallback;
    bool paused;
    float peak;
    public float Peak { get { lock (gate) return peak; } }
    public WaveFormat Format { get { return writer.WaveFormat; } }

    public TimelineWriter(string path, WaveFormat format) {
        this.path = path;
        writer = new WaveFileWriter(path, format);
        byteRate = format.AverageBytesPerSecond;
        blockAlign = format.BlockAlign;
        silence = new byte[Math.Max(blockAlign, byteRate / blockAlign / 4 * blockAlign)];
    }
    long Bytes(double milliseconds) {
        return (long)Math.Max(0, milliseconds * byteRate / 1000.0) / blockAlign * blockAlign;
    }
    void PadTo(long target) {
        target = Math.Min(target, endByteBudget);
        while (written < target) {
            int count = (int)Math.Min(silence.Length, target - written);
            writer.Write(silence, 0, count);
            written += count;
        }
    }
    public void SetPaused(bool value, double elapsedMs) {
        lock (gate) {
            if (closed || paused == value) return;
            long boundary = Math.Min(Bytes(elapsedMs), endByteBudget);
            if (value) pausedRanges.Add(new long[] { boundary, long.MaxValue });
            else pausedRanges[pausedRanges.Count - 1][1] = boundary;
            paused = value;
            peak = 0;
        }
    }
    byte[] MaskPausedAudio(byte[] buffer, int count) {
        byte[] masked = null;
        foreach (long[] range in pausedRanges) {
            long start = Math.Max(written, range[0]);
            long end = Math.Min(written + count, range[1]);
            if (end <= start) continue;
            if (masked == null) {
                masked = new byte[count];
                Buffer.BlockCopy(buffer, 0, masked, 0, count);
            }
            Array.Clear(masked, (int)(start - written), (int)(end - start));
        }
        return masked ?? buffer;
    }
    public void Add(byte[] buffer, int count, double elapsedMs) {
        lock (gate) {
            if (closed || count <= 0) return;
            count -= count % blockAlign;
            double packetMs = count * 1000.0 / byteRate;
            // Ignore ordinary callback jitter; only extend a true missing interval.
            double gapMs = hadCallback ? elapsedMs - previousCallbackMs - packetMs : elapsedMs - packetMs;
            long targetBeforePacket = Bytes(elapsedMs - packetMs);
            if (!hadCallback || gapMs > 80 || targetBeforePacket - written > Bytes(80)) PadTo(targetBeforePacket);
            count = (int)Math.Min(count, Math.Max(0, endByteBudget - written));
            if (count == 0) return;
            byte[] audio = MaskPausedAudio(buffer, count);
            writer.Write(audio, 0, count);
            written += count;
            previousCallbackMs = elapsedMs;
            hadCallback = true;
            peak = paused ? 0 : MeasurePeak(audio, count, writer.WaveFormat);
        }
    }
    public void SetEndBoundary(double elapsedMs) {
        lock (gate) endByteBudget = Math.Min(endByteBudget, Bytes(elapsedMs));
    }
    void TrimExcessAfterClose(long limit) {
        if (written <= limit) return;
        // A callback burst can contain more data than its current wall-clock
        // position suggests. Trim only this writer's WAV after its header closes.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            var read = new BinaryReader(stream);
            var write = new BinaryWriter(stream);
            stream.Position = 12;
            while (stream.Position + 8 <= stream.Length) {
                string kind = Encoding.ASCII.GetString(read.ReadBytes(4));
                uint size = read.ReadUInt32();
                long body = stream.Position;
                if (kind == "fact" && size >= 4) { write.Write((uint)(limit / blockAlign)); }
                if (kind == "data") {
                    stream.SetLength(body + limit);
                    stream.Position = body - 4;
                    write.Write((uint)limit);
                    stream.Position = 4;
                    write.Write((uint)(stream.Length - 8));
                    write.Flush();
                    written = limit;
                    return;
                }
                stream.Position = body + size + (size % 2);
            }
            throw new IOException("无法定位录音 WAV 数据段，停止边界修整失败。");
        }
    }
    void SilencePausedRangesAfterClose() {
        if (pausedRanges.Count == 0) return;
        // A callback burst can already be written ahead of a later pause click.
        // Reapply only microphone pause ranges to the finalized WAV as well.
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            var read = new BinaryReader(stream);
            stream.Position = 12;
            while (stream.Position + 8 <= stream.Length) {
                string kind = Encoding.ASCII.GetString(read.ReadBytes(4));
                uint size = read.ReadUInt32();
                long body = stream.Position;
                if (kind == "data") {
                    foreach (long[] range in pausedRanges) {
                        long start = Math.Min(written, range[0]);
                        long end = Math.Min(written, range[1]);
                        stream.Position = body + start;
                        while (start < end) {
                            int count = (int)Math.Min(silence.Length, end - start);
                            stream.Write(silence, 0, count);
                            start += count;
                        }
                    }
                    return;
                }
                stream.Position = body + size + (size % 2);
            }
            throw new IOException("无法定位麦克风 WAV 数据段，暂停区间保存失败。");
        }
    }
    public void Finish(double elapsedMs) {
        lock (gate) {
            if (closed) return;
            endByteBudget = Math.Min(endByteBudget, Bytes(elapsedMs));
            PadTo(endByteBudget);
            closed = true;
            writer.Dispose();
            TrimExcessAfterClose(endByteBudget);
            SilencePausedRangesAfterClose();
        }
    }
    public float ReadPeak() {
        lock (gate) { float value = paused ? 0 : peak; peak *= 0.5f; return value; }
    }
    static float MeasurePeak(byte[] data, int length, WaveFormat format) {
        double maximum = 0;
        bool floatingPoint = format.Encoding == WaveFormatEncoding.IeeeFloat;
        WaveFormatExtensible extended = format as WaveFormatExtensible;
        if (extended != null) floatingPoint = extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if (floatingPoint && format.BitsPerSample == 32) {
            for (int i = 0; i + 3 < length; i += 4) maximum = Math.Max(maximum, Math.Abs((double)BitConverter.ToSingle(data, i)));
        } else if (format.BitsPerSample == 16) {
            for (int i = 0; i + 1 < length; i += 2) maximum = Math.Max(maximum, Math.Abs((double)BitConverter.ToInt16(data, i)) / 32768.0);
        } else if (format.BitsPerSample == 24) {
            for (int i = 0; i + 2 < length; i += 3) {
                int sample = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
                if ((sample & 0x800000) != 0) sample |= unchecked((int)0xff000000);
                maximum = Math.Max(maximum, Math.Abs((double)sample) / 8388608.0);
            }
        } else if (format.BitsPerSample == 32) {
            for (int i = 0; i + 3 < length; i += 4) maximum = Math.Max(maximum, Math.Abs((double)BitConverter.ToInt32(data, i)) / 2147483648.0);
        }
        return (float)Math.Min(1, maximum);
    }
    public void Dispose() { lock (gate) { if (!closed) { closed = true; writer.Dispose(); } } }
}

internal sealed class CaptureTrack : IDisposable {
    readonly WasapiCapture capture;
    readonly TimelineWriter writer;
    readonly Stopwatch clock;
    readonly ManualResetEvent stopped = new ManualResetEvent(false);
    readonly Action<Exception> failed;
    volatile bool requestedStop;
    bool started;
    bool disposed;
    public readonly string DeviceName;
    public readonly string PathName;
    public double OffsetMs;
    public float Peak { get { return writer.ReadPeak(); } }
    public CaptureTrack(WasapiCapture source, string name, string path, Stopwatch sessionClock, Action<Exception> onFailed) {
        capture = source;
        DeviceName = name;
        PathName = path;
        clock = sessionClock;
        failed = onFailed;
        writer = new TimelineWriter(path, capture.WaveFormat);
        capture.DataAvailable += delegate(object sender, WaveInEventArgs args) {
            try { writer.Add(args.Buffer, args.BytesRecorded, clock.Elapsed.TotalMilliseconds); }
            catch (Exception error) { failed(error); }
        };
        capture.RecordingStopped += delegate(object sender, StoppedEventArgs args) {
            stopped.Set();
            if (!requestedStop) failed(args.Exception ?? new IOException("音频设备停止，已结束录音并保留音频。"));
        };
    }
    public void Start() {
        OffsetMs = clock.Elapsed.TotalMilliseconds;
        try { capture.StartRecording(); started = true; }
        catch { stopped.Set(); throw; }
    }
    public void RequestStop() {
        requestedStop = true;
        if (!started) { stopped.Set(); return; }
        try { capture.StopRecording(); }
        catch (Exception error) { MeetingFiles.Log(error); stopped.Set(); }
    }
    public void SetEndBoundary(double elapsedMs) { writer.SetEndBoundary(elapsedMs); }
    public void SetPaused(bool value, double elapsedMs) { writer.SetPaused(value, elapsedMs); }
    public void Finish(double stopMs) {
        // The writer must outlive the recording thread's last DataAvailable event.
        if (!stopped.WaitOne(5000)) MeetingFiles.Log(new TimeoutException("等待录音设备停止超时：" + DeviceName));
        capture.Dispose();
        writer.Finish(Math.Max(0, stopMs));
    }
    public void Dispose() {
        if (disposed) return;
        disposed = true;
        try { capture.Dispose(); }
        finally { try { writer.Dispose(); } finally { stopped.Dispose(); } }
    }
}

internal sealed class RecordingSession : IDisposable {
    readonly Stopwatch clock = new Stopwatch();
    readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly Dictionary<string, object> metadata;
    readonly MMDeviceEnumerator devices = new MMDeviceEnumerator();
    MMDevice microphone;
    MMDevice output;
    CaptureTrack mic;
    CaptureTrack system;
    volatile bool stopping;
    volatile bool microphonePaused;
    readonly List<Dictionary<string, object>> microphonePauses = new List<Dictionary<string, object>>();
    int failureSignalled;
    string captureError;
    public readonly string DirectoryPath;
    public bool Stopping { get { return stopping; } }
    public bool MicrophonePaused { get { return microphonePaused; } }
    public TimeSpan Elapsed { get { return clock.Elapsed; } }
    public float MicrophonePeak { get { return mic == null ? 0 : mic.Peak; } }
    public float SystemPeak { get { return system == null ? 0 : system.Peak; } }
    public string MicrophoneName { get { return mic == null ? "未启动" : mic.DeviceName; } }
    public string SystemDeviceName { get { return system == null ? "未启动" : system.DeviceName; } }
    readonly Action<Exception> onFailure;

    public RecordingSession(Dictionary<string, object> config, Action<Exception> failed) {
        onFailure = failed;
        clock.Start();
        string vaultRoot = MeetingFiles.VaultRoot(config);
        string audioFolder = Path.Combine(vaultRoot, MeetingFiles.Text(config, "meeting_folder", "会议记录"), "附件");
        string folder = Path.Combine(MeetingFiles.Home, "work");
        Directory.CreateDirectory(folder);
        DirectoryPath = Path.Combine(folder, started.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(DirectoryPath) || Directory.Exists(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))))
            DirectoryPath += "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Directory.CreateDirectory(DirectoryPath);
        metadata = new Dictionary<string, object> {
            { "schema_version", 1 }, { "start_time", started.ToString("o") }, { "state", "recording" },
            { "storage_mode", "minimal" }, { "audio_output_dir", Path.GetFullPath(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))) },
            { "session_dir", DirectoryPath }, { "capture_offsets_ms", new Dictionary<string, object> { { "mic", 0 }, { "system", 0 } } },
            { "device_start_offsets_ms", new Dictionary<string, object>() },
            { "microphone_pauses", microphonePauses }, { "microphone_paused", false },
            { "device_names", new Dictionary<string, object>() }, { "audio_files", new Dictionary<string, object>() }
        };
        Save();
        try {
            string micId = MeetingFiles.Text(config, "mic_device_id", "");
            string outputId = MeetingFiles.Text(config, "system_device_id", "");
            microphone = string.IsNullOrWhiteSpace(micId) ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : devices.GetDevice(micId);
            output = string.IsNullOrWhiteSpace(outputId) ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(outputId);
            mic = new CaptureTrack(new WasapiCapture(microphone), microphone.FriendlyName, Path.Combine(DirectoryPath, "mic.wav"), clock, SignalFailure);
            system = new CaptureTrack(new WasapiLoopbackCapture(output), output.FriendlyName, Path.Combine(DirectoryPath, "system.wav"), clock, SignalFailure);
            metadata["device_names"] = new Dictionary<string, object> { { "mic", mic.DeviceName }, { "system", system.DeviceName } };
            metadata["audio_files"] = new Dictionary<string, object> { { "mic", mic.PathName }, { "system", system.PathName } };
            mic.Start();
            system.Start();
            metadata["device_start_offsets_ms"] = new Dictionary<string, object> { { "mic", mic.OffsetMs }, { "system", system.OffsetMs } };
            Save();
        } catch (Exception error) {
            captureError = error.Message;
            stopping = true;
            if (mic != null) mic.RequestStop();
            if (system != null) system.RequestStop();
            try { FinalizeAudio(); } catch (Exception cleanupError) { MeetingFiles.Log(cleanupError); }
            Dispose();
            throw;
        }
    }
    void SignalFailure(Exception error) {
        if (stopping || Interlocked.Exchange(ref failureSignalled, 1) != 0) return;
        captureError = error.Message;
        MeetingFiles.Log(error);
        onFailure(error);
    }
    void Save() { MeetingFiles.WriteJson(Path.Combine(DirectoryPath, "session.json"), metadata); }
    public void SetMicrophonePaused(bool value) {
        if (stopping || mic == null || microphonePaused == value) return;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        mic.SetPaused(value, elapsedMs);
        if (value) microphonePauses.Add(new Dictionary<string, object> { { "start_seconds", elapsedMs / 1000.0 }, { "end_seconds", null } });
        else microphonePauses[microphonePauses.Count - 1]["end_seconds"] = elapsedMs / 1000.0;
        microphonePaused = value;
        metadata["microphone_paused"] = value;
        Save();
    }
    void FinalizeAudio(double? requestedStopMs = null) {
        double stopMs = requestedStopMs.HasValue ? requestedStopMs.Value : clock.Elapsed.TotalMilliseconds;
        Exception firstError = null;
        try { if (mic != null) mic.Finish(stopMs); } catch (Exception error) { firstError = error; MeetingFiles.Log(error); }
        try { if (system != null) system.Finish(stopMs); } catch (Exception error) { if (firstError == null) firstError = error; MeetingFiles.Log(error); }
        clock.Stop();
        if (firstError != null && string.IsNullOrEmpty(captureError)) captureError = firstError.Message;
        metadata["state"] = string.IsNullOrEmpty(captureError) ? "recorded" : "recorded_with_error";
        metadata["end_time"] = started.AddMilliseconds(stopMs).ToString("o");
        metadata["duration_seconds"] = stopMs / 1000.0;
        metadata["capture_error"] = captureError;
        if (microphonePaused && microphonePauses.Count > 0) microphonePauses[microphonePauses.Count - 1]["end_seconds"] = stopMs / 1000.0;
        metadata["device_start_offsets_ms"] = new Dictionary<string, object> { { "mic", mic == null ? 0 : mic.OffsetMs }, { "system", system == null ? 0 : system.OffsetMs } };
        Save();
    }
    public void StopAsync(Action<Exception> done) {
        if (stopping) return;
        stopping = true;
        double stopMs = clock.Elapsed.TotalMilliseconds;
        if (mic != null) mic.SetEndBoundary(stopMs);
        if (system != null) system.SetEndBoundary(stopMs);
        ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null;
            try {
                // NAudio's polling stop discards the final unread packet. Keep
                // two polling intervals to drain it, but enforce the user stop
                // boundary above so no later audio enters either saved WAV.
                Thread.Sleep(120);
                if (mic != null) mic.RequestStop();
                if (system != null) system.RequestStop();
                FinalizeAudio(stopMs);
            } catch (Exception error) { failure = error; MeetingFiles.Log(error); }
            finally {
                try { Dispose(); }
                catch (Exception error) { if (failure == null) failure = error; MeetingFiles.Log(error); }
            }
            done(failure);
        });
    }
    public void Dispose() {
        try { if (mic != null) mic.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); } finally { mic = null; }
        try { if (system != null) system.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); } finally { system = null; }
        try { if (microphone != null) microphone.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); } finally { microphone = null; }
        try { if (output != null) output.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); } finally { output = null; }
        try { devices.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); }
    }
}

internal sealed class RecorderActionButton : Button {
    public bool EndsRecording;
    public bool MicrophoneDisabled;
    public bool CollapsesWindow;
    bool hovering;
    public RecorderActionButton() {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (CollapsesWindow) {
            float scale = Height / 16f;
            float x = Width / 2f, y = Height / 2f;
            using (var pen = new Pen(hovering ? Color.FromArgb(40, 104, 168) : Color.FromArgb(131, 146, 165), 1.6f * scale)) {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLines(pen, new PointF[] { new PointF(x - 4 * scale, y - 2 * scale), new PointF(x, y + 2 * scale), new PointF(x + 4 * scale, y - 2 * scale) });
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Width - 2, Height - 2));
            return;
        }
        float unit = Math.Min(Width, Height) / 36f;
        g.TranslateTransform((Width - 36 * unit) / 2, (Height - 36 * unit) / 2);
        g.ScaleTransform(unit, unit);
        Color fill = !Enabled ? Color.FromArgb(239, 241, 244) : EndsRecording
            ? (hovering ? Color.FromArgb(191, 42, 54) : Color.FromArgb(216, 57, 69))
            : MicrophoneDisabled ? Color.FromArgb(250, 243, 232) : (hovering ? Color.FromArgb(221, 236, 254) : Color.FromArgb(237, 245, 255));
        Color ink = !Enabled ? Color.FromArgb(158, 165, 177) : EndsRecording ? Color.FromArgb(132, 25, 34) : MicrophoneDisabled ? Color.FromArgb(139, 90, 31) : Color.FromArgb(40, 104, 168);
        using (var brush = new SolidBrush(fill)) {
            if (EndsRecording) using (var block = new System.Drawing.Drawing2D.GraphicsPath()) {
                block.AddArc(3, 3, 8, 8, 180, 90);
                block.AddArc(25, 3, 8, 8, 270, 90);
                block.AddArc(25, 25, 8, 8, 0, 90);
                block.AddArc(3, 25, 8, 8, 90, 90);
                block.CloseFigure();
                g.FillPath(brush, block);
            }
            else g.FillEllipse(brush, 1, 1, 34, 34);
        }
        if (!EndsRecording) using (var border = new Pen(!Enabled ? Color.FromArgb(225, 230, 237) : MicrophoneDisabled ? Color.FromArgb(233, 210, 175) : Color.FromArgb(188, 215, 243), 1.1f))
            g.DrawEllipse(border, 1.6f, 1.6f, 32.8f, 32.8f);
        if (!EndsRecording) using (var pen = new Pen(ink, 1.8f)) {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            using (var capsule = new System.Drawing.Drawing2D.GraphicsPath()) {
                capsule.AddArc(14, 7, 8, 8, 180, 180);
                capsule.AddLine(22, 11, 22, 18);
                capsule.AddArc(14, 14, 8, 8, 0, 180);
                capsule.CloseFigure();
                g.DrawPath(pen, capsule);
            }
            using (var cradle = new System.Drawing.Drawing2D.GraphicsPath()) {
                cradle.AddLine(10.5f, 16.5f, 10.5f, 18.5f);
                cradle.AddArc(10.5f, 11, 15, 15, 180, -180);
                cradle.AddLine(25.5f, 18.5f, 25.5f, 16.5f);
                g.DrawPath(pen, cradle);
            }
            g.DrawLine(pen, 18, 26, 18, 29); g.DrawLine(pen, 14.5f, 29, 21.5f, 29);
            if (MicrophoneDisabled) g.DrawLine(pen, 9, 9, 27, 27);
        }
        if (Focused && ShowFocusCues) using (var pen = new Pen(ink, 1)) {
            pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
            if (EndsRecording) g.DrawRectangle(pen, 6, 6, 24, 24);
            else g.DrawEllipse(pen, 4, 4, 28, 28);
        }
    }
}

internal sealed class RecordingLevelIndicator : Control {
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 100 };
    double level;
    bool recording;
    public RecordingLevelIndicator() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animation.Tick += delegate { level *= .78; if (level < .01) level = 0; Invalidate(); };
        TabStop = false;
        AccessibleName = Text = "音频输入：暂无明显声音";
    }
    public void SetRecording(bool value) {
        recording = value;
        if (!value) level = 0;
        animation.Enabled = value && Visible;
        Invalidate();
    }
    public void SetLevels(float microphone, float system) {
        double peak = Math.Max(microphone, system);
        double next = peak <= .002 ? 0 : Math.Min(1, (Math.Log10(peak) + 2.7) / 2.2);
        level = Math.Max(level, next);
        AccessibleName = Text = peak > .003 ? "音频输入：有声音" : "音频输入：暂无明显声音";
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); animation.Enabled = recording && Visible; }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.ScaleTransform(Width / 18f, Height / 24f);
        float[] weights = { .3f, .7f, 1, .7f, .3f };
        using (var pen = new Pen(level > .02 ? Color.FromArgb(53, 133, 217) : Color.FromArgb(148, 165, 185), 2)) {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            for (int i = 0; i < weights.Length; i++) {
                float height = (recording ? 3 + weights[i] * 4 : 2) + (float)level * weights[i] * 14;
                g.DrawLine(pen, 1 + i * 4, 12 - height / 2, 1 + i * 4, 12 + height / 2);
            }
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}

internal sealed class RecorderPanel : Form {
    public readonly Label Heading = new Label();
    public readonly Label Time = new Label();
    public readonly Label Detail = new Label();
    public readonly RecorderActionButton MicrophoneButton = new RecorderActionButton();
    public readonly RecorderActionButton StopButton = new RecorderActionButton { EndsRecording = true };
    public readonly RecorderActionButton CollapseButton = new RecorderActionButton { CollapsesWindow = true };
    public readonly RecordingLevelIndicator AudioLevel = new RecordingLevelIndicator();
    public readonly ToolTip DevicesTip = new ToolTip();
    readonly Font timeNormal = new Font("Segoe UI Semibold", 11);
    readonly Font timeLong = new Font("Segoe UI Semibold", 8);
    public bool AllowClose;
    bool showingRecording;
    bool microphonePaused;
    string micName = "";
    string systemName = "";
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public RecorderPanel() {
        Text = MeetingBrand.Name + " · 会议录音";
        Icon = MeetingBrand.IdleIcon;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.White;
        DoubleBuffered = true;
        ClientSize = new Size(186, 68);
        MinimumSize = MaximumSize = new Size(186, 68);
        StartPosition = FormStartPosition.Manual;
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(work.Right - Width - 24, work.Bottom - Height - 24);
        Time.SetBounds(10, 24, 55, 22);
        Time.Font = timeNormal;
        Time.ForeColor = Color.FromArgb(36, 59, 83);
        Time.TextAlign = ContentAlignment.MiddleLeft;
        Heading.SetBounds(82, 53, 50, 13);
        Heading.Font = new Font("Microsoft YaHei UI", 7.5f);
        Heading.ForeColor = Color.FromArgb(72, 103, 135);
        Heading.AutoEllipsis = true;
        Heading.TextAlign = ContentAlignment.MiddleCenter;
        Detail.Visible = false;
        AudioLevel.SetBounds(66, 26, 18, 18);
        MicrophoneButton.SetBounds(91, 19, 32, 32);
        StopButton.SetBounds(148, 21, 28, 28);
        CollapseButton.SetBounds(95, 3, 24, 13);
        MicrophoneButton.AccessibleName = MicrophoneButton.Text = "关闭麦克风";
        StopButton.AccessibleName = StopButton.Text = "结束整个记录";
        CollapseButton.AccessibleName = CollapseButton.Text = "收起录音浮窗";
        DevicesTip.SetToolTip(StopButton, "结束整个记录 · Ctrl+Alt+L");
        DevicesTip.SetToolTip(Time, "本场会议时长；拖动可移动浮窗");
        DevicesTip.SetToolTip(AudioLevel, "麦克风与电脑声的实际输入音量");
        DevicesTip.SetToolTip(CollapseButton, "收起浮窗，继续录音；从托盘可恢复");
        Controls.AddRange(new Control[] { Time, AudioLevel, Heading, MicrophoneButton, StopButton, CollapseButton });
        CollapseButton.Click += delegate { Hide(); };
        MouseDown += Drag;
        Time.MouseDown += Drag;
        Heading.MouseDown += Drag;
        AudioLevel.MouseDown += Drag;
        Heading.TextChanged += delegate { if (!showingRecording) DevicesTip.SetToolTip(Heading, Heading.Text); };
        Detail.TextChanged += delegate { if (!showingRecording) DevicesTip.SetToolTip(Heading, Heading.Text + "\n" + Detail.Text); };
        SetMicPaused(false);
    }
    void Drag(object sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }
    public void ShowRecording(string microphone, string system) {
        showingRecording = true;
        micName = microphone;
        systemName = system;
        SetElapsed(TimeSpan.Zero);
        Detail.Text = systemName;
        Detail.AccessibleName = "电脑声音：" + systemName;
        DevicesTip.SetToolTip(Detail, "电脑声音：" + systemName);
        MicrophoneButton.Enabled = StopButton.Enabled = true;
        Heading.SetBounds((int)(82 * ClientSize.Width / 186f), (int)(53 * ClientSize.Height / 68f), (int)(50 * ClientSize.Width / 186f), (int)(13 * ClientSize.Height / 68f));
        AudioLevel.SetRecording(true);
        SetMicPaused(false);
        Invalidate();
    }
    public void SetMicPaused(bool paused) {
        microphonePaused = paused;
        MicrophoneButton.MicrophoneDisabled = paused;
        MicrophoneButton.AccessibleName = MicrophoneButton.Text = paused ? "开启麦克风" : "关闭麦克风";
        DevicesTip.SetToolTip(MicrophoneButton, "麦克风：" + micName + "\n电脑声音：" + systemName + "\n" + (paused ? "点击开启麦克风" : "点击关闭麦克风，电脑声音继续录制"));
        if (showingRecording) {
            Heading.Text = paused ? "已关闭" : "录音中";
            Heading.AccessibleName = "麦克风：" + micName + (paused ? "，已暂停" : "，录制中");
            Heading.ForeColor = paused ? Color.FromArgb(139, 90, 31) : Color.FromArgb(72, 103, 135);
            DevicesTip.SetToolTip(Heading, "麦克风：" + micName + (paused ? "（已关闭，电脑声音仍在录制）" : "（录制中）"));
        }
        MicrophoneButton.Invalidate();
        Invalidate();
    }
    public void SetElapsed(TimeSpan elapsed) {
        Time.Font = elapsed.TotalHours >= 1 ? timeLong : timeNormal;
        Time.Text = (elapsed.TotalHours >= 1 ? ((int)elapsed.TotalHours).ToString("00") + ":" : "") + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00");
    }
    public void ShowStatus(string title, string detail) {
        showingRecording = false;
        AudioLevel.SetRecording(false);
        Heading.SetBounds((int)(4 * ClientSize.Width / 186f), (int)(53 * ClientSize.Height / 68f), (int)(178 * ClientSize.Width / 186f), (int)(13 * ClientSize.Height / 68f));
        Heading.ForeColor = Color.FromArgb(38, 49, 68);
        Heading.Text = title;
        Detail.Text = detail;
        Heading.AccessibleName = title;
        Detail.AccessibleName = detail;
        MicrophoneButton.Enabled = StopButton.Enabled = false;
        DevicesTip.SetToolTip(Heading, title + "\n" + detail);
        Invalidate();
    }
    static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF bounds, float radius) {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    protected override void OnSizeChanged(EventArgs e) {
        base.OnSizeChanged(e);
        if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
        using (var path = Rounded(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), ClientSize.Height / 6f)) {
            var previous = Region;
            Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), ClientSize.Height / 6f))
        using (var pen = new Pen(Color.FromArgb(224, 231, 240), 1)) g.DrawPath(pen, path);
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(work.Left, work.Right - Width - 24), Math.Max(work.Top, work.Bottom - Height - 24));
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void Dispose(bool disposing) {
        if (disposing) { DevicesTip.Dispose(); timeNormal.Dispose(); timeLong.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class MeetingHotkey : NativeWindow, IDisposable {
    const int Id = 7411;
    public event Action Pressed;
    public event Action ExitRequested;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    public MeetingHotkey() {
        CreateHandle(new CreateParams { Caption = "ObsidianMeetingHotkey", Parent = new IntPtr(-3) });
        if (!RegisterHotKey(Handle, Id, 0x4003, 0x4c)) {
            DestroyHandle();
            throw new InvalidOperationException("Ctrl+Alt+L 已被其他程序占用，会议快捷键未启动。");
        }
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == Id && Pressed != null) Pressed();
        if (message.Msg == 0x0010 && ExitRequested != null) ExitRequested();
        base.WndProc(ref message);
    }
    public void Dispose() { UnregisterHotKey(Handle, Id); DestroyHandle(); }
}

internal sealed class TranscriptionJob {
    public string Session;
    public Process Process;
    public string LastMessage;
    public bool Reported;
    public DateTime PreviousStatusWriteUtc = DateTime.MinValue;
}

internal sealed class MeetingContext : ApplicationContext {
    readonly Dictionary<string, object> config;
    readonly Control dispatch = new Control();
    readonly MeetingHotkey hotkey;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly RecorderPanel panel = new RecorderPanel();
    readonly List<TranscriptionJob> jobs = new List<TranscriptionJob>();
    readonly ToolStripMenuItem toggle = new ToolStripMenuItem("开始录音 · Ctrl+Alt+L");
    readonly ToolStripMenuItem pauseMicrophone = new ToolStripMenuItem("关闭麦克风");
    RecordingSession recording;
    string lastSession;
    bool exiting;

    public MeetingContext() {
        config = MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "config.json"));
        MeetingFiles.VaultRoot(config);
        IntPtr dispatchHandle = dispatch.Handle;
        hotkey = new MeetingHotkey();
        hotkey.Pressed += Toggle;
        hotkey.ExitRequested += RequestExit;
        var menu = new ContextMenuStrip();
        toggle.Click += delegate { Toggle(); };
        menu.Items.Add(toggle);
        pauseMicrophone.Click += delegate { ToggleMicrophone(); };
        pauseMicrophone.Enabled = false;
        menu.Items.Add(pauseMicrophone);
        menu.Items.Add("显示录音浮窗", null, delegate { panel.Show(); panel.Activate(); });
        menu.Items.Add("重试最近一场转写", null, delegate { RetryLastSession(); });
        menu.Items.Add("打开会议记录", null, delegate {
            string path = Path.Combine(MeetingFiles.VaultRoot(config), MeetingFiles.Text(config, "meeting_folder", "会议记录"));
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { RequestExit(); });
        tray = new NotifyIcon { Icon = MeetingBrand.IdleIcon, Text = "听记 · Ctrl+Alt+L", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { panel.Show(); panel.Activate(); };
        panel.StopButton.Click += delegate { Stop(); };
        panel.MicrophoneButton.Click += delegate { ToggleMicrophone(); };
        panel.Time.Text = "00:00";
        panel.ShowStatus("听记已就绪", "Ctrl+Alt+L 开始录制");
        timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += delegate { Tick(); };
        RestoreLastSession();
        timer.Start();
    }
    void Notice(string title, string message, ToolTipIcon icon) { tray.ShowBalloonTip(5000, "听记 · " + title, message, icon); }
    void OnUi(Action action) { if (!dispatch.IsDisposed) try { dispatch.BeginInvoke(action); } catch (InvalidOperationException) { } }
    void Toggle() {
        if (exiting) return;
        if (recording == null) Start();
        else Stop();
    }
    void Start() {
        try {
            recording = new RecordingSession(config, delegate(Exception error) {
                OnUi(delegate { Notice("音频设备异常", error.Message + " 已录内容会保留。", ToolTipIcon.Warning); Stop(); });
            });
            SaveLastSession(recording.DirectoryPath, null);
            toggle.Text = "结束录音 · Ctrl+Alt+L";
            pauseMicrophone.Text = "关闭麦克风";
            pauseMicrophone.Enabled = true;
            panel.ShowRecording(recording.MicrophoneName, recording.SystemDeviceName);
            panel.Show();
            tray.Icon = MeetingBrand.RecordingIcon;
            tray.Text = "听记录音中 · Ctrl+Alt+L 结束";
            Notice("会议录音已开始", "正在录制麦克风和电脑声音；Ctrl+Alt+L 结束。", ToolTipIcon.Info);
        } catch (Exception error) {
            MeetingFiles.Log(error);
            recording = null;
            Notice("无法开始录音", error.Message, ToolTipIcon.Error);
            MessageBox.Show(error.Message, MeetingBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    void ToggleMicrophone() {
        if (recording == null || recording.Stopping) return;
        try {
            recording.SetMicrophonePaused(!recording.MicrophonePaused);
        } catch (Exception error) { MeetingFiles.Log(error); Notice("麦克风状态保存异常", error.Message, ToolTipIcon.Error); }
        finally {
            panel.SetMicPaused(recording.MicrophonePaused);
            pauseMicrophone.Text = recording.MicrophonePaused ? "开启麦克风" : "关闭麦克风";
            tray.Text = recording.MicrophonePaused ? "听记 · 麦克风已关闭，电脑声音录制中" : "听记录音中 · Ctrl+Alt+L 结束";
        }
    }
    void Stop() {
        if (recording == null || recording.Stopping) return;
        RecordingSession ended = recording;
        pauseMicrophone.Enabled = false;
        panel.ShowStatus("正在保存录音…", "");
        ended.StopAsync(delegate(Exception error) {
            OnUi(delegate {
                recording = null;
                toggle.Text = "开始录音 · Ctrl+Alt+L";
                tray.Icon = MeetingBrand.IdleIcon;
                tray.Text = "听记 · Ctrl+Alt+L";
                if (error != null) Notice("录音保存异常", error.Message + " 原始文件已保留。", ToolTipIcon.Error);
                LaunchWorker(ended.DirectoryPath);
                panel.Hide();
                if (exiting) ExitThread();
            });
        });
    }
    static string Quote(string text) { return "\"" + text.Replace("\"", "\\\"") + "\""; }
    void SaveLastSession(string session, Process process) {
        lastSession = session;
        try {
            MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new {
                session_dir = session,
                worker_pid = process == null ? 0 : process.Id,
                worker_start_time_utc = process == null ? "" : process.StartTime.ToUniversalTime().ToString("o")
            });
        } catch (Exception error) { MeetingFiles.Log(error); }
    }
    void RestoreLastSession() {
        try {
            var saved = MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "last-session.json"));
            lastSession = MeetingFiles.Text(saved, "session_dir", "");
            if (string.IsNullOrEmpty(lastSession) || !Directory.Exists(lastSession)) return;
            int pid;
            DateTime started;
            if (!int.TryParse(MeetingFiles.Text(saved, "worker_pid", "0"), out pid) || pid <= 0 ||
                !DateTime.TryParse(MeetingFiles.Text(saved, "worker_start_time_utc", ""), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)) return;
            var process = Process.GetProcessById(pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime() == started.ToUniversalTime())
                jobs.Add(new TranscriptionJob { Session = lastSession, Process = process });
            else process.Dispose();
        } catch (ArgumentException) { } catch (Exception error) { MeetingFiles.Log(error); }
    }
    void RetryLastSession() {
        if (exiting) return;
        if (string.IsNullOrEmpty(lastSession) || !File.Exists(Path.Combine(lastSession, "session.json"))) {
            Notice("暂无可重试录音", "先按 Ctrl+Alt+L 完成一场录音。", ToolTipIcon.Info);
            return;
        }
        if (recording != null && string.Equals(recording.DirectoryPath, lastSession, StringComparison.OrdinalIgnoreCase)) {
            panel.Show();
            Notice("会议正在录音", "请先结束录音，再进行本地转写。", ToolTipIcon.Info);
            return;
        }
        var savedStatus = MeetingFiles.ReadJson(Path.Combine(lastSession, "status.json"));
        if (string.Equals(MeetingFiles.Text(savedStatus, "artifacts_compacted", "false"), "true", StringComparison.OrdinalIgnoreCase)) {
            string note = MeetingFiles.Text(savedStatus, "note_path", "");
            if (File.Exists(note)) {
                OpenNote(note);
                Notice("会议笔记已完成", "已打开保存的转录笔记。", ToolTipIcon.Info);
            } else Notice("会议笔记未找到", "转录笔记已被移动或删除，请检查会议记录文件夹。", ToolTipIcon.Warning);
            return;
        }
        LaunchWorker(lastSession);
        panel.Show();
    }
    void LaunchWorker(string session) {
        try {
            foreach (var existing in jobs) {
                if (string.Equals(existing.Session, session, StringComparison.OrdinalIgnoreCase) && !existing.Process.HasExited) {
                    if (recording == null) {
                        panel.ShowStatus("这场会议正在本地转写", string.IsNullOrEmpty(existing.LastMessage) ? "后台处理尚未结束" : existing.LastMessage);
                    }
                    Notice("转写仍在进行", "已显示当前状态，请等待完成。", ToolTipIcon.Info);
                    return;
                }
            }
            string python = Path.Combine(MeetingFiles.Home, ".venv", "Scripts", "python.exe");
            string worker = Path.Combine(MeetingFiles.Home, "meeting_worker.py");
            if (!File.Exists(python) || !File.Exists(worker)) throw new FileNotFoundException("本地转写程序未安装完整；录音已保留：" + session);
            DateTime previousStatus = File.GetLastWriteTimeUtc(Path.Combine(session, "status.json"));
            var process = Process.Start(new ProcessStartInfo {
                FileName = python, Arguments = Quote(worker) + " --session " + Quote(session),
                WorkingDirectory = MeetingFiles.Home, UseShellExecute = false, CreateNoWindow = true
            });
            jobs.Add(new TranscriptionJob { Session = session, Process = process, PreviousStatusWriteUtc = previousStatus });
            SaveLastSession(session, process);
            if (recording == null) {
                panel.ShowStatus("录音已保存 · 本地转写中", "Ctrl+Alt+L 可开始下一场");
            }
            Notice("录音已保存", "后台本地转写中，完成后打开 Obsidian 笔记。", ToolTipIcon.Info);
        } catch (Exception error) {
            MeetingFiles.Log(error);
            if (recording == null) {
                panel.ShowStatus("录音已保存，转写未启动", "原始音频已保留，可从托盘重试");
            }
            Notice("转写启动失败", error.Message, ToolTipIcon.Error);
        }
    }
    void ReportJobFailure(TranscriptionJob job, string detail) {
        job.Reported = true;
        string message = (string.IsNullOrEmpty(detail) ? "本地转写进程退出，未生成完成状态。" : detail) + " 原始音频已保留，可从托盘重试最近一场转写。";
        if (recording == null) {
            panel.ShowStatus("转写失败，原始录音已保留", message);
        }
        Notice("本地转写失败", message, ToolTipIcon.Error);
    }
    void Tick() {
        if (recording != null && !recording.Stopping) {
            TimeSpan elapsed = recording.Elapsed;
            panel.SetElapsed(elapsed);
            panel.AudioLevel.SetLevels(recording.MicrophonePeak, recording.SystemPeak);
        }
        foreach (TranscriptionJob job in jobs) {
            if (job.Reported) continue;
            try {
                string statusPath = Path.Combine(job.Session, "status.json");
                var status = MeetingFiles.ReadJson(statusPath);
                bool workerExited = job.Process.HasExited;
                // A worker may publish completed and exit between the first
                // read and this check. Re-read its final state before failing it.
                if (workerExited) status = MeetingFiles.ReadJson(statusPath);
                bool freshStatus = job.PreviousStatusWriteUtc == DateTime.MinValue || File.GetLastWriteTimeUtc(statusPath) > job.PreviousStatusWriteUtc;
                string state = MeetingFiles.Text(status, "state", MeetingFiles.Text(status, "status", ""));
                string message = MeetingFiles.Text(status, "message", state);
                if (freshStatus && message != job.LastMessage && !string.IsNullOrEmpty(message)) {
                    job.LastMessage = message;
                    if (recording == null) panel.Detail.Text = message;
                }
                string note = MeetingFiles.Text(status, "note_path", "");
                string artifactError = MeetingFiles.Text(status, "artifact_error", "");
                if (freshStatus && !string.IsNullOrEmpty(artifactError)) {
                    ReportJobFailure(job, artifactError);
                } else if (freshStatus && workerExited && job.Process.ExitCode == 0 && (state == "completed" || state == "done" || state == "success")) {
                    job.Reported = true;
                    if (recording == null) panel.Heading.Text = "会议笔记已生成";
                    Notice("会议笔记已生成", "中文转写和说话人标注已保存。", ToolTipIcon.Info);
                    if (!string.IsNullOrEmpty(note)) OpenNote(note);
                } else if ((freshStatus && (state == "error" || state == "failed")) || workerExited) {
                    string detail = MeetingFiles.Text(status, "error", "");
                    if (string.IsNullOrEmpty(detail)) detail = MeetingFiles.Text(status, "artifact_error", "");
                    if (string.IsNullOrEmpty(detail)) detail = workerExited ? "本地转写进程退出，未生成完成状态。" : message;
                    ReportJobFailure(job, detail);
                }
            } catch (Exception error) {
                MeetingFiles.Log(error);
                if (job.Process.HasExited) ReportJobFailure(job, "转写进程已退出，无法读取有效状态：" + error.Message);
            }
        }
    }
    void OpenNote(string path) {
        string root = MeetingFiles.VaultRoot(config).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("笔记路径不在配置的 Obsidian 库中。");
        string relative = full.Substring(root.Length).Replace('\\', '/');
        string vault = MeetingFiles.Text(config, "vault_id", "");
        if (string.IsNullOrWhiteSpace(vault)) vault = MeetingFiles.Text(config, "vault_name", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)));
        string uri = "obsidian://open?vault=" + Uri.EscapeDataString(vault) + "&file=" + Uri.EscapeDataString(relative);
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
    void RequestExit() {
        exiting = true;
        toggle.Enabled = false;
        if (recording != null) Stop();
        else ExitThread();
    }
    protected override void ExitThreadCore() {
        timer.Stop(); timer.Dispose();
        hotkey.Dispose();
        tray.Visible = false; tray.Dispose();
        panel.AllowClose = true; panel.Close(); panel.Dispose();
        dispatch.Dispose();
        foreach (var job in jobs) job.Process.Dispose();
        base.ExitThreadCore();
    }
}

internal static class Program {
    static void VerifyPauseWave(string path, double durationMs, double[,] silentRanges) {
        using (var reader = new WaveFileReader(path)) {
            if (Math.Abs(reader.TotalTime.TotalMilliseconds - durationMs) > 0.1)
                throw new Exception("Microphone pause regression: wall-clock duration changed.");
            byte[] sample = new byte[2];
            for (int index = 0; reader.Read(sample, 0, sample.Length) == sample.Length; index++) {
                double milliseconds = index * 1000.0 / 16000;
                bool silent = false;
                for (int range = 0; range < silentRanges.GetLength(0); range++)
                    silent |= milliseconds >= silentRanges[range, 0] && milliseconds < silentRanges[range, 1];
                short expected = (short)(silent ? 0 : 1000);
                if (BitConverter.ToInt16(sample, 0) != expected)
                    throw new Exception("Microphone pause regression at " + milliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms: expected " + expected + ".");
            }
        }
    }
    static int SelfTestTimeline() {
        string directory = Path.Combine(Path.GetTempPath(), "ObsidianMeetingTimeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "test.wav");
        var format = new WaveFormat(16000, 16, 1);
        byte[] packet = new byte[3200]; // 100 ms of synthetic PCM.
        using (var writer = new TimelineWriter(file, format)) {
            writer.Add(packet, packet.Length, 100);
            writer.Add(packet, packet.Length, 200);
            writer.Add(packet, packet.Length, 2100); // loopback silent for 1.8 s.
            writer.Finish(3000);
        }
        using (var reader = new WaveFileReader(file)) {
            if (Math.Abs(reader.TotalTime.TotalMilliseconds - 3000) > 1) throw new Exception("Timeline padding self-test failed.");
        }
        string boundaryFile = Path.Combine(directory, "stop-boundary.wav");
        byte[] beginning = new byte[3200];
        byte[] ending = new byte[3200];
        for (int i = 0; i < 1600; i++) {
            Buffer.BlockCopy(BitConverter.GetBytes((short)1000), 0, beginning, i * 2, 2);
            Buffer.BlockCopy(BitConverter.GetBytes((short)(i < 800 ? 2000 : 3000)), 0, ending, i * 2, 2);
        }
        using (var writer = new TimelineWriter(boundaryFile, format)) {
            writer.Add(beginning, beginning.Length, 100);
            writer.SetEndBoundary(150);
            writer.Add(ending, ending.Length, 200); // arrived while draining.
            writer.Finish(150);
        }
        using (var reader = new WaveFileReader(boundaryFile)) {
            if (Math.Abs(reader.TotalTime.TotalMilliseconds - 150) > 0.1)
                throw new Exception("Stop boundary regression: audio recorded after the stop point.");
            reader.Position = 4000; // 125 ms: the delayed last syllable must survive.
            byte[] sample = new byte[2];
            if (reader.Read(sample, 0, 2) != 2 || BitConverter.ToInt16(sample, 0) != 2000)
                throw new Exception("Stop boundary regression: delayed audio before stop was lost.");
        }
        string gapsFile = Path.Combine(directory, "short-gaps.wav");
        using (var writer = new TimelineWriter(gapsFile, format)) {
            for (int i = 1; i <= 10; i++) writer.Add(beginning, beginning.Length, i * 140);
            writer.Finish(1400);
        }
        using (var reader = new WaveFileReader(gapsFile)) {
            reader.Position = 1310 * 32; // 1310 ms, within the final real packet.
            byte[] sample = new byte[2];
            if (reader.Read(sample, 0, 2) != 2 || BitConverter.ToInt16(sample, 0) != 1000)
                throw new Exception("Short-gap regression: repeated short silence accumulated timeline drift.");
        }
        string burstFile = Path.Combine(directory, "callback-burst.wav");
        using (var writer = new TimelineWriter(burstFile, format)) {
            writer.Add(beginning, beginning.Length, 100);
            byte[] burst = new byte[6400];
            Buffer.BlockCopy(ending, 0, burst, 0, ending.Length);
            Buffer.BlockCopy(ending, 0, burst, ending.Length, ending.Length);
            writer.Add(burst, burst.Length, 200);
            writer.SetEndBoundary(200);
            writer.Finish(200);
        }
        using (var reader = new WaveFileReader(burstFile)) {
            if (Math.Abs(reader.TotalTime.TotalMilliseconds - 200) > 0.1)
                throw new Exception("Callback burst regression: saved audio exceeded the stop boundary.");
        }
        string pausedFile = Path.Combine(directory, "microphone-pause.wav");
        string continuousSystemFile = Path.Combine(directory, "system-during-microphone-pause.wav");
        using (var mic = new TimelineWriter(pausedFile, format))
        using (var system = new TimelineWriter(continuousSystemFile, format)) {
            mic.Add(beginning, beginning.Length, 100);
            mic.SetPaused(true, 150);
            mic.Add(beginning, beginning.Length, 200); // delayed callback crosses pause.
            mic.SetPaused(false, 250);
            mic.Add(beginning, beginning.Length, 300); // delayed callback crosses resume.
            mic.Add(beginning, beginning.Length, 400);
            mic.SetPaused(true, 450);
            mic.Add(beginning, beginning.Length, 500);
            for (int i = 1; i <= 6; i++) system.Add(beginning, beginning.Length, i * 100);
            mic.SetEndBoundary(600);
            mic.Finish(600); // stop while still paused, preserving the silent tail.
            system.Finish(600);
        }
        VerifyPauseWave(pausedFile, 600, new double[,] { { 150, 250 }, { 450, 600 } });
        VerifyPauseWave(continuousSystemFile, 600, new double[0, 2]);
        string latePausedFile = Path.Combine(directory, "pause-and-resume-before-callback.wav");
        using (var mic = new TimelineWriter(latePausedFile, format)) {
            byte[] latePacket = new byte[6400];
            Buffer.BlockCopy(beginning, 0, latePacket, 0, beginning.Length);
            Buffer.BlockCopy(beginning, 0, latePacket, beginning.Length, beginning.Length);
            mic.Add(beginning, beginning.Length, 100);
            mic.SetPaused(true, 150);
            mic.SetPaused(true, 170); // repeated pause requests are idempotent.
            mic.SetPaused(false, 250);
            mic.SetPaused(false, 270);
            mic.Add(latePacket, latePacket.Length, 300); // both toggles preceded this callback.
            mic.Finish(300);
        }
        VerifyPauseWave(latePausedFile, 300, new double[,] { { 150, 250 } });
        string pausedBurstFile = Path.Combine(directory, "pause-buffered-burst.wav");
        using (var mic = new TimelineWriter(pausedBurstFile, format)) {
            byte[] burst = new byte[6400];
            Buffer.BlockCopy(beginning, 0, burst, 0, beginning.Length);
            Buffer.BlockCopy(beginning, 0, burst, beginning.Length, beginning.Length);
            mic.Add(beginning, beginning.Length, 100);
            mic.Add(burst, burst.Length, 200); // writer is ahead of the current clock.
            mic.SetPaused(true, 250);
            mic.SetPaused(false, 350);
            mic.Add(beginning, beginning.Length, 400);
            mic.Finish(400);
        }
        VerifyPauseWave(pausedBurstFile, 400, new double[,] { { 250, 350 } });
        string floatPausedFile = Path.Combine(directory, "stereo-float-microphone-pause.wav");
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        byte[] floatPacket = new byte[floatFormat.AverageBytesPerSecond / 10];
        for (int i = 0; i < floatPacket.Length; i += 4)
            Buffer.BlockCopy(BitConverter.GetBytes(0.25f), 0, floatPacket, i, 4);
        using (var mic = new TimelineWriter(floatPausedFile, floatFormat)) {
            mic.Add(floatPacket, floatPacket.Length, 100);
            mic.SetPaused(true, 150);
            mic.SetPaused(false, 250);
            mic.Add(floatPacket, floatPacket.Length, 200);
            mic.Add(floatPacket, floatPacket.Length, 300);
            mic.Finish(300);
        }
        using (var reader = new WaveFileReader(floatPausedFile)) {
            if (Math.Abs(reader.TotalTime.TotalMilliseconds - 300) > 0.1)
                throw new Exception("Stereo float pause regression: wall-clock duration changed.");
            byte[] sample = new byte[8];
            for (int index = 0; reader.Read(sample, 0, sample.Length) == sample.Length; index++) {
                double milliseconds = index * 1000.0 / 48000;
                float expected = milliseconds >= 150 && milliseconds < 250 ? 0 : 0.25f;
                if (BitConverter.ToSingle(sample, 0) != expected || BitConverter.ToSingle(sample, 4) != expected)
                    throw new Exception("Stereo float pause regression at " + milliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms.");
            }
        }
        File.WriteAllText(Path.Combine(MeetingFiles.Home, "timeline-self-test.json"), MeetingFiles.Json.Serialize(new { passed = true, expected_ms = 3000, actual_ms = 3000, stop_boundary_ms = 150, delayed_tail_preserved = true, post_stop_audio_excluded = true, short_gaps_aligned = true, callback_burst_trimmed = true, microphone_pause_silent = true, delayed_pause_callbacks_masked = true, pause_and_resume_before_callback_masked = true, microphone_resume_preserved = true, system_uninterrupted = true, paused_stop_tail_silent = true, pause_buffered_burst_masked = true, stereo_float_pause_masked = true, pause_artifact = pausedFile, artifact = file }), new UTF8Encoding(false));
        return 0;
    }
    static int ListDevices() {
        var result = new List<object>();
        using (var enumerator = new MMDeviceEnumerator()) {
            foreach (DataFlow flow in new DataFlow[] { DataFlow.Capture, DataFlow.Render }) {
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)) {
                    using (device) result.Add(new { id = device.ID, name = device.FriendlyName, flow = flow.ToString() });
                }
            }
        }
        MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "audio-devices.json"), result);
        return 0;
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            if (args.Length > 0 && args[0] == "--list-devices") return ListDevices();
            if (args.Length > 0 && args[0] == "--self-test-timeline") return SelfTestTimeline();
            bool first;
            using (var mutex = new Mutex(true, "Local\\ObsidianMeetingHotkey", out first)) {
                if (!first) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new MeetingContext()) Application.Run(context);
            }
            return 0;
        } catch (Exception error) {
            MeetingFiles.Log(error);
            if (args.Length > 0 && args[0] == "--self-test-timeline") {
                MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "timeline-self-test.json"), new { passed = false, error = error.Message });
                return 1;
            }
            MessageBox.Show(error.Message, "听记", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
