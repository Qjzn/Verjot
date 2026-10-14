using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;

[assembly: System.Reflection.AssemblyTitle("听记")]
[assembly: System.Reflection.AssemblyProduct("听记 Tingji")]
[assembly: System.Reflection.AssemblyDescription("轻量中文会议录音与转写")]

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
            throw new InvalidOperationException("请复制 config.example.json 为 config.json，并设置保存目录的完整路径。");
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

internal static class MeetingConfig {
    public static Dictionary<string, object> Load() {
        CheckOrdinaryPath(MeetingFiles.Home);
        string path = Path.Combine(MeetingFiles.Home, "config.json");
        if (!File.Exists(path)) throw new IOException("缺少 config.json，请运行 Install.ps1 或复制 config.example.json 后填写保存路径。");
        return Validate(MeetingFiles.ReadJson(path));
    }
    public static Dictionary<string, object> Validate(Dictionary<string, object> config) {
        var schema = MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "config.schema.json"));
        if (!schema.ContainsKey("properties")) throw new IOException("缺少 config.schema.json，请保留完整听记源码目录。");
        var properties = (Dictionary<string, object>)schema["properties"];
        if (config == null) throw new InvalidOperationException("config.json 顶层必须是对象。");
        foreach (string key in config.Keys) if (!properties.ContainsKey(key)) throw new InvalidOperationException("未知配置字段：" + key);
        if (!config.ContainsKey("vault_root")) throw new InvalidOperationException("缺少必填配置：vault_root");
        foreach (var entry in properties) {
            var rule = (Dictionary<string, object>)entry.Value;
            if (!config.ContainsKey(entry.Key)) {
                if (rule.ContainsKey("default")) config[entry.Key] = rule["default"];
                else continue;
            }
            object value = config[entry.Key];
            string kind = (string)rule["type"];
            bool correct = kind == "string" ? value is string : kind == "boolean" ? value is bool : value is int || value is long;
            if (!correct) throw new InvalidOperationException(entry.Key + " 必须是 " + kind + "，不能为 null 或其他类型。");
            if (value is string) {
                string text = (string)value;
                int length = 0;
                for (int i = 0; i < text.Length; i++, length++)
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                if ((rule.ContainsKey("minLength") && length < Convert.ToInt32(rule["minLength"])) ||
                    (rule.ContainsKey("maxLength") && length > Convert.ToInt32(rule["maxLength"])))
                    throw new InvalidOperationException(entry.Key + " 长度不符合配置说明。");
                if (rule.ContainsKey("pattern")) {
                    Match match = Regex.Match(text, (string)rule["pattern"]);
                    if (!match.Success || match.Length != text.Length) throw new InvalidOperationException(entry.Key + " 格式不符合配置说明。");
                }
            }
            if (rule.ContainsKey("enum")) {
                bool match = false;
                foreach (object allowed in (System.Collections.IEnumerable)rule["enum"]) if (Equals(value, allowed)) match = true;
                if (!match) throw new InvalidOperationException(entry.Key + " 不在允许选项中。");
            }
            if (kind == "integer" && (Convert.ToInt64(value) < Convert.ToInt64(rule["minimum"]) || Convert.ToInt64(value) > Convert.ToInt64(rule["maximum"])))
                throw new InvalidOperationException(entry.Key + " 超出允许范围。");
        }
        string root = MeetingFiles.VaultRoot(config);
        if (!Regex.IsMatch(root, @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))") ||
            !Regex.IsMatch((string)config["vault_root"], @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))"))
            throw new InvalidOperationException("vault_root 必须是完整 Windows 绝对目录。");
        if (!Directory.Exists(root)) throw new IOException("vault_root 保存目录不存在，请先创建目录。");
        CheckOrdinaryPath(root);
        string folder = (string)config["meeting_folder"];
        if (Path.IsPathRooted(folder)) throw new InvalidOperationException("meeting_folder 必须为保存目录内的相对路径。");
        foreach (string part in folder.Split(new char[] { '\\', '/' })) {
            if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.EndsWith(".") || part.EndsWith(" ") || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("meeting_folder 包含空段、路径穿越或 Windows 非法名称。");
        }
        CheckOrdinaryPath(Path.Combine(root, folder));
        if (File.Exists(Path.Combine(root, folder))) throw new IOException("meeting_folder 已被同名文件占用，请改用目录。");
        uint firstMods, firstKey, pauseMods, pauseKey;
        ParseHotkey((string)config["hotkey"], out firstMods, out firstKey);
        ParseHotkey((string)config["pause_hotkey"], out pauseMods, out pauseKey);
        if (firstMods == pauseMods && firstKey == pauseKey) throw new InvalidOperationException("开始/结束快捷键和暂停快捷键必须不同。");
        if ((string)config["transcription_mode"] == "api") {
            Uri uri;
            if (Regex.IsMatch((string)config["api_base_url"], @"\s") ||
                !Uri.TryCreate((string)config["api_base_url"], UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("api_base_url 必须是无密钥、无查询参数的 HTTP(S) API 前缀。");
            if (string.IsNullOrWhiteSpace((string)config["api_model"])) throw new InvalidOperationException("API 模式必须填写 api_model 音频转写模型名。");
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable((string)config["api_key_env"])))
                throw new InvalidOperationException("未设置 API 密钥环境变量：" + (string)config["api_key_env"] + "；设置后重新启动听记。");
        }
        return config;
    }
    public static void CheckOrdinaryPath(string path) {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent) {
            try {
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("保存路径不能经过符号链接或 Windows 重解析目录。");
            } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public static void ParseHotkey(string text, out uint modifiers, out uint key) {
        if (!Regex.IsMatch(text, @"\A(?:(?:Ctrl|Alt|Shift|Win)\+)+(?:[A-Z]|F(?:[1-9]|1[0-9]|2[0-4])|Space)\z"))
            throw new InvalidOperationException("快捷键格式应为 Ctrl+Alt+L、Alt+Space 或 Ctrl+Shift+F9。");
        string[] parts = text.Split('+');
        modifiers = 0;
        for (int i = 0; i < parts.Length - 1; i++) {
            uint bit = parts[i] == "Alt" ? 1u : parts[i] == "Ctrl" ? 2u : parts[i] == "Shift" ? 4u : 8u;
            if ((modifiers & bit) != 0) throw new InvalidOperationException("快捷键不能重复修饰键。");
            modifiers |= bit;
        }
        string last = parts[parts.Length - 1];
        key = last == "Space" ? 32u : last.StartsWith("F") && last.Length > 1 ? (uint)(111 + int.Parse(last.Substring(1))) : last[0];
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
    bool followsDefaultMicrophone;
    bool followsDefaultOutput;
    string observedMicrophoneId;
    string observedOutputId;
    CaptureTrack mic;
    CaptureTrack system;
    volatile bool stopping;
    volatile bool microphonePaused;
    volatile bool recordingPaused;
    readonly List<Dictionary<string, object>> microphonePauses = new List<Dictionary<string, object>>();
    readonly List<Dictionary<string, object>> recordingPauses = new List<Dictionary<string, object>>();
    int failureSignalled;
    string captureError;
    public readonly string DirectoryPath;
    public bool Stopping { get { return stopping; } }
    public bool MicrophonePaused { get { return microphonePaused; } }
    public bool RecordingPaused { get { return recordingPaused; } }
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
        MeetingConfig.CheckOrdinaryPath(folder);
        Directory.CreateDirectory(folder);
        MeetingConfig.CheckOrdinaryPath(folder);
        DirectoryPath = Path.Combine(folder, started.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(DirectoryPath) || Directory.Exists(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))))
            DirectoryPath += "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Directory.CreateDirectory(DirectoryPath);
        MeetingConfig.CheckOrdinaryPath(DirectoryPath);
        metadata = new Dictionary<string, object> {
            { "schema_version", 1 }, { "start_time", started.ToString("o") }, { "state", "recording" },
            { "storage_mode", "minimal" }, { "audio_output_dir", Path.GetFullPath(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))) },
            { "note_link_style", (bool)config["open_in_obsidian"] ? "wiki" : "markdown" },
            { "transcription_mode", config["transcription_mode"] },
            { "transcription_model", (string)config["transcription_mode"] == "api" ? config["api_model"] : "Qwen3-ASR-1.7B" },
            { "transcription_api_base_url", (string)config["transcription_mode"] == "api" ? ((string)config["api_base_url"]).TrimEnd('/') : "" },
            { "session_dir", DirectoryPath }, { "capture_offsets_ms", new Dictionary<string, object> { { "mic", 0 }, { "system", 0 } } },
            { "device_start_offsets_ms", new Dictionary<string, object>() },
            { "microphone_pauses", microphonePauses }, { "microphone_paused", false },
            { "recording_pauses", recordingPauses }, { "recording_paused", false },
            { "device_names", new Dictionary<string, object>() }, { "audio_files", new Dictionary<string, object>() }
        };
        Save();
        try {
            string micId = MeetingFiles.Text(config, "mic_device_id", "");
            string outputId = MeetingFiles.Text(config, "system_device_id", "");
            followsDefaultMicrophone = string.IsNullOrWhiteSpace(micId);
            followsDefaultOutput = string.IsNullOrWhiteSpace(outputId);
            microphone = string.IsNullOrWhiteSpace(micId) ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : devices.GetDevice(micId);
            output = string.IsNullOrWhiteSpace(outputId) ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(outputId);
            observedMicrophoneId = microphone.ID;
            observedOutputId = output.ID;
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
    public string CheckDefaultDeviceChanges() {
        if (stopping) return null;
        var changes = new List<string>();
        if (followsDefaultMicrophone) {
            string change = CheckDefaultDevice(DataFlow.Capture, Role.Console, ref observedMicrophoneId, "麦克风");
            if (change != null) changes.Add(change);
        }
        if (followsDefaultOutput) {
            string change = CheckDefaultDevice(DataFlow.Render, Role.Multimedia, ref observedOutputId, "电脑声音设备");
            if (change != null) changes.Add(change);
        }
        if (changes.Count == 0) return null;
        return string.Join("；", changes.ToArray()) + "。本场仍捕获麦克风：" + MicrophoneName + "；电脑声音：" + SystemDeviceName + "。下一场将重新选择默认设备。";
    }
    string CheckDefaultDevice(DataFlow flow, Role role, ref string observedId, string label) {
        string currentId, currentName;
        try {
            using (MMDevice current = devices.GetDefaultAudioEndpoint(flow, role)) {
                currentId = current.ID;
                currentName = current.FriendlyName;
            }
        } catch (COMException) {
            currentId = "";
            currentName = "暂不可用";
        }
        if (string.Equals(observedId, currentId, StringComparison.Ordinal)) return null;
        observedId = currentId;
        return "默认" + label + "已变化：" + currentName;
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
        mic.SetPaused(value || recordingPaused, elapsedMs);
        if (value) microphonePauses.Add(new Dictionary<string, object> { { "start_seconds", elapsedMs / 1000.0 }, { "end_seconds", null } });
        else microphonePauses[microphonePauses.Count - 1]["end_seconds"] = elapsedMs / 1000.0;
        microphonePaused = value;
        metadata["microphone_paused"] = value;
        Save();
    }
    public void SetRecordingPaused(bool value) {
        if (stopping || recordingPaused == value) return;
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        mic.SetPaused(value || microphonePaused, elapsedMs);
        system.SetPaused(value, elapsedMs);
        if (value) recordingPauses.Add(new Dictionary<string, object> { { "start_seconds", elapsedMs / 1000.0 }, { "end_seconds", null } });
        else recordingPauses[recordingPauses.Count - 1]["end_seconds"] = elapsedMs / 1000.0;
        recordingPaused = value;
        metadata["recording_paused"] = value;
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
        if (recordingPaused && recordingPauses.Count > 0) recordingPauses[recordingPauses.Count - 1]["end_seconds"] = stopMs / 1000.0;
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
    public bool RecordingPaused;
    public bool InputActive;
    float inputLevel;
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
    public void SetInputLevel(float value) {
        inputLevel = !Enabled || MicrophoneDisabled || RecordingPaused ? 0 : Math.Max(0, Math.Min(1, value));
        InputActive = inputLevel > .003f;
        Invalidate();
    }
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
        Color fill = !Enabled || (RecordingPaused && !EndsRecording) ? Color.FromArgb(239, 241, 244) : EndsRecording
            ? (hovering ? Color.FromArgb(191, 42, 54) : Color.FromArgb(216, 57, 69))
            : MicrophoneDisabled ? Color.FromArgb(250, 243, 232) : (hovering || InputActive ? Color.FromArgb(221, 236, 254) : Color.FromArgb(237, 245, 255));
        Color ink = !Enabled || (RecordingPaused && !EndsRecording) ? Color.FromArgb(158, 165, 177) : EndsRecording ? Color.FromArgb(132, 25, 34) : MicrophoneDisabled ? Color.FromArgb(139, 90, 31) : InputActive ? Color.FromArgb(31, 126, 220) : Color.FromArgb(40, 104, 168);
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
        if (!EndsRecording) using (var border = new Pen(!Enabled || RecordingPaused ? Color.FromArgb(225, 230, 237) : MicrophoneDisabled ? Color.FromArgb(233, 210, 175) : InputActive ? Color.FromArgb(78, 159, 235) : Color.FromArgb(188, 215, 243), InputActive ? 1.6f : 1.1f))
            g.DrawEllipse(border, 1.6f, 1.6f, 32.8f, 32.8f);
        if (!EndsRecording && InputActive) {
            float height = 3 + 7 * (float)Math.Min(1, (Math.Log10(inputLevel) + 2.7) / 2.2);
            using (var brush = new SolidBrush(Color.FromArgb(139, 196, 248)))
                g.FillRectangle(brush, 16, 20 - height, 4, height);
        }
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
        AccessibleName = Text = "录音输入：暂无明显声音";
    }
    public void SetRecording(bool value) {
        recording = value;
        if (!value) level = 0;
        if (!value) AccessibleName = Text = "录音输入：整场已暂停";
        animation.Enabled = value && Visible;
        Invalidate();
    }
    public void SetLevels(float microphone, float system) {
        double peak = recording ? Math.Max(microphone, system) : 0;
        double next = peak <= .002 ? 0 : Math.Min(1, (Math.Log10(peak) + 2.7) / 2.2);
        level = Math.Max(level, next);
        AccessibleName = Text = !recording ? "录音输入：整场已暂停" : peak > .003 ? "录音输入：有声音" : "录音输入：暂无明显声音";
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
    readonly Font recordingHeadingFont = new Font("Microsoft YaHei UI", 7.5f);
    readonly Font processingHeadingFont = new Font("Microsoft YaHei UI", 9);
    public bool AllowClose;
    bool showingRecording;
    bool microphonePaused;
    bool recordingPaused;
    string micName = "";
    string systemName = "";
    string deviceWarning = "";
    float microphoneLevel;
    float systemLevel;
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
        Heading.SetBounds(10, 50, 74, 14);
        Heading.Font = recordingHeadingFont;
        Heading.ForeColor = Color.FromArgb(72, 103, 135);
        Heading.AutoEllipsis = true;
        Heading.TextAlign = ContentAlignment.MiddleCenter;
        Detail.Visible = false;
        Detail.Font = new Font("Microsoft YaHei UI", 8);
        Detail.ForeColor = Color.FromArgb(100, 116, 135);
        Detail.AutoEllipsis = true;
        Detail.TextAlign = ContentAlignment.MiddleCenter;
        AudioLevel.SetBounds(66, 26, 18, 18);
        MicrophoneButton.SetBounds(91, 19, 32, 32);
        StopButton.SetBounds(148, 21, 28, 28);
        CollapseButton.SetBounds(95, 3, 24, 13);
        MicrophoneButton.AccessibleName = MicrophoneButton.Text = "关闭麦克风";
        StopButton.AccessibleName = StopButton.Text = "结束整个记录";
        CollapseButton.AccessibleName = CollapseButton.Text = "收起录音浮窗";
        DevicesTip.SetToolTip(StopButton, "结束整个记录 · Ctrl+Alt+L");
        DevicesTip.SetToolTip(Time, "本场会议时长；拖动可移动浮窗");
        DevicesTip.SetToolTip(AudioLevel, "麦克风或电脑声音的实际输入音量");
        DevicesTip.SetToolTip(CollapseButton, "收起浮窗，继续录音；从托盘可恢复");
        Controls.AddRange(new Control[] { Time, AudioLevel, Heading, Detail, MicrophoneButton, StopButton, CollapseButton });
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
        recordingPaused = false;
        micName = microphone;
        systemName = system;
        deviceWarning = "";
        microphoneLevel = systemLevel = 0;
        Time.Visible = AudioLevel.Visible = MicrophoneButton.Visible = StopButton.Visible = true;
        Detail.Visible = false;
        Heading.Font = recordingHeadingFont;
        SetElapsed(TimeSpan.Zero);
        Detail.Text = systemName;
        Detail.AccessibleName = "电脑声音：" + systemName;
        DevicesTip.SetToolTip(Detail, "电脑声音：" + systemName);
        MicrophoneButton.Enabled = StopButton.Enabled = true;
        Heading.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(50 * ClientSize.Height / 68f), (int)(74 * ClientSize.Width / 186f), (int)(14 * ClientSize.Height / 68f));
        AudioLevel.SetRecording(true);
        SetMicPaused(false);
        Invalidate();
    }
    public void SetInputLevels(float microphone, float system) {
        if (!showingRecording) return;
        microphoneLevel = microphone;
        systemLevel = system;
        RefreshInputFeedback();
    }
    public void SetDeviceWarning(string warning) {
        deviceWarning = warning ?? "";
        if (showingRecording) RefreshInputFeedback();
    }
    void RefreshInputFeedback() {
        float mic = !showingRecording || recordingPaused || microphonePaused ? 0 : microphoneLevel;
        float system = !showingRecording || recordingPaused ? 0 : systemLevel;
        MicrophoneButton.RecordingPaused = recordingPaused;
        MicrophoneButton.SetInputLevel(mic);
        AudioLevel.SetLevels(mic, system);
        string micState = recordingPaused ? "整场已暂停" : microphonePaused ? "已关闭" : mic > .003f ? "有声音" : "暂无明显声音";
        string systemState = recordingPaused ? "整场已暂停" : system > .003f ? "有声音" : "暂无明显声音";
        string devices = "麦克风：" + micName + "\n麦克风输入：" + micState + "\n电脑声音：" + systemName + "\n电脑声音输入：" + systemState;
        if (!string.IsNullOrEmpty(deviceWarning)) devices += "\n" + deviceWarning;
        string action = recordingPaused ? "点击切换麦克风；恢复整场后生效" : microphonePaused ? "点击开启麦克风，电脑声音继续录制" : "点击关闭麦克风，电脑声音继续录制";
        DevicesTip.SetToolTip(MicrophoneButton, devices + "\n" + action);
        DevicesTip.SetToolTip(AudioLevel, devices);
        DevicesTip.SetToolTip(Heading, devices);
        MicrophoneButton.AccessibleName = MicrophoneButton.Text = "麦克风输入：" + micState + "；" + (microphonePaused ? "开启麦克风" : "关闭麦克风");
    }
    public void SetMicPaused(bool paused) {
        microphonePaused = paused;
        MicrophoneButton.MicrophoneDisabled = paused;
        if (showingRecording) {
            Heading.Visible = recordingPaused;
            Heading.Text = recordingPaused ? "已暂停" : "";
            Heading.AccessibleName = recordingPaused ? "整场录音已暂停" : "";
            Heading.ForeColor = Color.FromArgb(139, 90, 31);
        }
        RefreshInputFeedback();
        MicrophoneButton.Invalidate();
        Invalidate();
    }
    public void SetRecordingPaused(bool paused) {
        recordingPaused = paused;
        if (paused) microphoneLevel = systemLevel = 0;
        AudioLevel.SetRecording(showingRecording && !paused);
        SetMicPaused(microphonePaused);
    }
    public void SetElapsed(TimeSpan elapsed) {
        Time.Font = elapsed.TotalHours >= 1 ? timeLong : timeNormal;
        Time.Text = (elapsed.TotalHours >= 1 ? ((int)elapsed.TotalHours).ToString("00") + ":" : "") + elapsed.Minutes.ToString("00") + ":" + elapsed.Seconds.ToString("00");
    }
    public void ShowStatus(string title, string detail) {
        ShowProcessing(title, detail);
    }
    public void ShowProcessing(string title, string detail) {
        showingRecording = false;
        AudioLevel.SetRecording(false);
        MicrophoneButton.SetInputLevel(0);
        Time.Visible = AudioLevel.Visible = MicrophoneButton.Visible = StopButton.Visible = false;
        Heading.Visible = true;
        Detail.Visible = true;
        Heading.Font = processingHeadingFont;
        Heading.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(22 * ClientSize.Height / 68f), (int)(166 * ClientSize.Width / 186f), (int)(18 * ClientSize.Height / 68f));
        Detail.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(43 * ClientSize.Height / 68f), (int)(166 * ClientSize.Width / 186f), (int)(16 * ClientSize.Height / 68f));
        Heading.ForeColor = Color.FromArgb(38, 49, 68);
        Heading.Text = title;
        Detail.Text = detail;
        Heading.AccessibleName = title;
        Detail.AccessibleName = detail;
        MicrophoneButton.Enabled = StopButton.Enabled = false;
        DevicesTip.SetToolTip(Heading, title + "\n" + detail);
        DevicesTip.SetToolTip(Detail, title + "\n" + detail);
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
        if (disposing) { DevicesTip.Dispose(); timeNormal.Dispose(); timeLong.Dispose(); recordingHeadingFont.Dispose(); processingHeadingFont.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class MeetingHotkey : NativeWindow, IDisposable {
    const int Id = 7411;
    const int PauseId = 7412;
    readonly string pauseText;
    bool pauseRegistered;
    public event Action Pressed;
    public event Action PausePressed;
    public event Action ExitRequested;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    public MeetingHotkey(string text, string pause) {
        pauseText = pause;
        uint modifiers, key;
        MeetingConfig.ParseHotkey(text, out modifiers, out key);
        CreateHandle(new CreateParams { Caption = "ObsidianMeetingHotkey", Parent = new IntPtr(-3) });
        if (!RegisterHotKey(Handle, Id, modifiers | 0x4000, key)) {
            DestroyHandle();
            throw new InvalidOperationException(text + " 已被其他程序占用，请修改 config.json 的 hotkey 后重启。");
        }
    }
    public void EnablePause(bool enabled) {
        if (enabled == pauseRegistered) return;
        if (enabled) {
            uint modifiers, key;
            MeetingConfig.ParseHotkey(pauseText, out modifiers, out key);
            if (!RegisterHotKey(Handle, PauseId, modifiers | 0x4000, key))
                throw new InvalidOperationException(pauseText + " 已被其他程序占用，请修改 pause_hotkey 后重启。");
        } else UnregisterHotKey(Handle, PauseId);
        pauseRegistered = enabled;
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == Id && Pressed != null) Pressed();
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == PauseId && PausePressed != null) PausePressed();
        if (message.Msg == 0x0010 && ExitRequested != null) ExitRequested();
        base.WndProc(ref message);
    }
    public void Dispose() { EnablePause(false); UnregisterHotKey(Handle, Id); DestroyHandle(); }
}

internal sealed class TranscriptionJob {
    public string Session;
    public Process Process;
    public string LastMessage;
    public bool Reported;
    public DateTime PreviousStatusWriteUtc = DateTime.MinValue;
    public DateTime WorkerStartedUtc = DateTime.MinValue;
}

internal sealed class MeetingContext : ApplicationContext {
    readonly Dictionary<string, object> config;
    readonly Control dispatch = new Control();
    readonly MeetingHotkey hotkey;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly RecorderPanel panel = new RecorderPanel();
    readonly List<TranscriptionJob> jobs = new List<TranscriptionJob>();
    readonly ToolStripMenuItem toggle = new ToolStripMenuItem("开始录音");
    readonly ToolStripMenuItem pauseRecording = new ToolStripMenuItem("暂停整场录音");
    readonly ToolStripMenuItem pauseMicrophone = new ToolStripMenuItem("关闭麦克风");
    readonly ToolStripMenuItem summary = new ToolStripMenuItem("听记已就绪") { Enabled = false };
    readonly ToolStripMenuItem recent = new ToolStripMenuItem("最近会议");
    readonly SessionCatalog catalog;
    readonly Func<string, Process> workerLauncher;
    readonly Action<string, bool> noteOpener;
    RecordingSession recording;
    string lastSession;
    string focusedSession;
    SessionRecord focusedRecord;
    bool exiting;
    int deviceCheckTicks;
    string StartHotkey { get { return (string)config["hotkey"]; } }
    string PauseHotkey { get { return (string)config["pause_hotkey"]; } }

    public MeetingContext() : this(MeetingConfig.Load(), null, null) { }
    // The two optional operations also allow isolated native regressions without
    // capturing audio, loading a model, or opening the user's applications.
    internal MeetingContext(Dictionary<string, object> settings, Func<string, Process> launcher, Action<string, bool> opener) {
        config = MeetingConfig.Validate(settings);
        workerLauncher = launcher;
        noteOpener = opener;
        catalog = new SessionCatalog(MeetingFiles.Home, MeetingFiles.VaultRoot(config));
        IntPtr dispatchHandle = dispatch.Handle;
        hotkey = new MeetingHotkey(StartHotkey, PauseHotkey);
        hotkey.Pressed += Toggle;
        hotkey.PausePressed += ToggleRecordingPause;
        hotkey.ExitRequested += RequestExit;
        var menu = new ContextMenuStrip();
        menu.Items.Add(summary);
        menu.Items.Add(new ToolStripSeparator());
        toggle.Click += delegate { Toggle(); };
        toggle.Text = "开始录音 · " + StartHotkey;
        menu.Items.Add(toggle);
        pauseRecording.Text = "暂停整场录音 · " + PauseHotkey;
        pauseRecording.Enabled = false;
        pauseRecording.Click += delegate { ToggleRecordingPause(); };
        menu.Items.Add(pauseRecording);
        pauseMicrophone.Click += delegate { ToggleMicrophone(); };
        pauseMicrophone.Enabled = false;
        menu.Items.Add(pauseMicrophone);
        menu.Items.Add("显示浮窗", null, delegate { RefreshFeedback(); panel.Show(); panel.Activate(); });
        // WinForms does not expose an expansion affordance for an empty child
        // menu. Populate it before the parent opens, then refresh on expansion.
        menu.Opening += delegate { RefreshRecentMenu(); };
        recent.DropDownOpening += delegate { RefreshRecentMenu(); };
        menu.Items.Add(recent);
        menu.Items.Add("打开会议记录文件夹", null, delegate {
            string path = Path.Combine(MeetingFiles.VaultRoot(config), MeetingFiles.Text(config, "meeting_folder", "会议记录"));
            MeetingConfig.CheckOrdinaryPath(path);
            Directory.CreateDirectory(path);
            OpenFolder(path);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { RequestExit(); });
        tray = new NotifyIcon { Icon = MeetingBrand.IdleIcon, Text = "听记 · " + StartHotkey, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { RefreshFeedback(); panel.Show(); panel.Activate(); };
        panel.StopButton.Click += delegate { Stop(); };
        panel.MicrophoneButton.Click += delegate { ToggleMicrophone(); };
        panel.Time.Text = "00:00";
        panel.DevicesTip.SetToolTip(panel.StopButton, "结束整个记录 · " + StartHotkey);
        panel.ShowStatus("听记已就绪", StartHotkey + " 开始录制");
        timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += delegate { Tick(); };
        RestoreSessions();
        timer.Start();
    }
    void Notice(string title, string message, ToolTipIcon icon) { tray.ShowBalloonTip(5000, "听记 · " + title, message, icon); }
    void OnUi(Action action) { if (!dispatch.IsDisposed) try { dispatch.BeginInvoke(action); } catch (InvalidOperationException) { } }
    void Toggle() {
        if (exiting) return;
        if (recording == null) Start(); else Stop();
    }
    void Start() {
        try {
            hotkey.EnablePause(true);
            recording = new RecordingSession(config, delegate(Exception error) {
                OnUi(delegate { panel.SetDeviceWarning("音频设备异常，正在保存已录内容"); Notice("音频设备异常", error.Message + " 已录内容会保留。", ToolTipIcon.Warning); Stop(); });
            });
            catalog.SaveWorker(recording.DirectoryPath, null);
            SaveLastSession(recording.DirectoryPath, null);
            focusedSession = recording.DirectoryPath;
            focusedRecord = null;
            toggle.Text = "结束录音 · " + StartHotkey;
            pauseRecording.Text = "暂停整场录音 · " + PauseHotkey;
            pauseRecording.Enabled = true;
            pauseMicrophone.Text = "关闭麦克风";
            pauseMicrophone.Enabled = true;
            deviceCheckTicks = 0;
            panel.ShowRecording(recording.MicrophoneName, recording.SystemDeviceName);
            panel.Show();
            tray.Icon = MeetingBrand.RecordingIcon;
            RefreshFeedback();
            Notice("会议录音已开始", "正在录制麦克风和电脑声音；" + StartHotkey + " 结束，" + PauseHotkey + " 暂停。", ToolTipIcon.Info);
        } catch (Exception error) {
            hotkey.EnablePause(false);
            MeetingFiles.Log(error);
            // If persistence failed after capture started, close the capture
            // through the ordinary save path rather than orphaning a recorder.
            if (recording != null) { Stop(); return; }
            Notice("无法开始录音", error.Message, ToolTipIcon.Error);
            MessageBox.Show(error.Message, MeetingBrand.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    void ToggleMicrophone() {
        if (recording == null || recording.Stopping) return;
        try { recording.SetMicrophonePaused(!recording.MicrophonePaused); }
        catch (Exception error) { MeetingFiles.Log(error); Notice("麦克风状态保存异常", error.Message, ToolTipIcon.Error); }
        finally { UpdateRecordingState(); }
    }
    void ToggleRecordingPause() {
        if (recording == null || recording.Stopping) return;
        try { recording.SetRecordingPaused(!recording.RecordingPaused); }
        catch (Exception error) { MeetingFiles.Log(error); Notice("暂停状态保存异常", error.Message, ToolTipIcon.Error); }
        finally { UpdateRecordingState(); }
    }
    void UpdateRecordingState() {
        panel.SetMicPaused(recording.MicrophonePaused);
        panel.SetRecordingPaused(recording.RecordingPaused);
        pauseMicrophone.Text = recording.MicrophonePaused ? "开启麦克风" : "关闭麦克风";
        pauseRecording.Text = (recording.RecordingPaused ? "继续整场录音 · " : "暂停整场录音 · ") + PauseHotkey;
        RefreshFeedback();
    }
    void Stop() {
        if (recording == null || recording.Stopping) return;
        RecordingSession ended = recording;
        hotkey.EnablePause(false);
        pauseRecording.Enabled = false;
        pauseMicrophone.Enabled = false;
        panel.ShowStatus("正在保存录音", "正在关闭两路录音文件");
        ended.StopAsync(delegate(Exception error) {
            OnUi(delegate {
                recording = null;
                toggle.Text = "开始录音 · " + StartHotkey;
                tray.Icon = MeetingBrand.IdleIcon;
                focusedSession = ended.DirectoryPath;
                if (error != null) {
                    RecordLaunchFailure(ended.DirectoryPath, "录音保存异常，请检查原始资料：" + error.Message);
                    Notice("录音保存异常", "已保留原始文件，请从最近会议检查这场资料。", ToolTipIcon.Error);
                } else LaunchWorker(ended.DirectoryPath);
                RefreshFeedback();
                if ((bool)config["show_processing_window"] && !exiting) panel.Show();
                else panel.Hide();
                if (exiting) ExitThread();
            });
        });
    }
    static string Quote(string text) { return "\"" + text.Replace("\"", "\\\"") + "\""; }
    static void OpenFolder(string path) {
        MeetingConfig.CheckOrdinaryPath(path);
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
    }
    void SaveLastSession(string session, Process process) {
        // Retrying an older meeting must not move the latest-meeting pointer.
        if (!string.IsNullOrEmpty(lastSession) && string.Compare(Path.GetFileName(session), Path.GetFileName(lastSession), StringComparison.OrdinalIgnoreCase) < 0) return;
        lastSession = session;
        try {
            MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new {
                session_dir = session, worker_pid = process == null ? 0 : process.Id,
                worker_start_time_utc = process == null ? "" : process.StartTime.ToUniversalTime().ToString("o")
            });
        } catch (Exception error) { MeetingFiles.Log(error); }
    }
    void RestoreSessions() {
        try {
            var records = catalog.Snapshot();
            SessionRecord attention = null;
            int attentionCount = 0;
            foreach (var record in records) {
                if (lastSession == null) lastSession = record.DirectoryPath;
                if (record.WorkerAlive) {
                    Process process = record.TryOpenWorker();
                    if (process != null) jobs.Add(new TranscriptionJob { Session = record.DirectoryPath, Process = process, LastMessage = record.Message, WorkerStartedUtc = record.WorkerStartUtc });
                }
                if (record.NeedsAttention) {
                    attentionCount++;
                    if (attention == null) attention = record;
                    MarkNotifiedSafely(record);
                }
            }
            focusedRecord = attention ?? (records.Count == 0 ? null : records[0]);
            focusedSession = focusedRecord == null ? null : focusedRecord.DirectoryPath;
            RefreshFeedback();
            if (attention != null) {
                Notice("上次会议 · " + attention.Label, attention.Message + "；托盘 → 最近会议可查看" + (attentionCount > 1 ? "，另有 " + (attentionCount - 1) + " 场待查看。" : "。"),
                    attention.State == "completed" ? ToolTipIcon.Info : ToolTipIcon.Warning);
                if ((bool)config["show_processing_window"]) panel.Show();
            } else if (jobs.Count > 0 && (bool)config["show_processing_window"]) panel.Show();
        } catch (Exception error) { MeetingFiles.Log(error); Notice("恢复状态需要检查", "请查看程序 work 中的会议资料；原始内容未修改。", ToolTipIcon.Warning); }
    }
    bool ActiveJob(string session) {
        foreach (var job in jobs) if (string.Equals(job.Session, session, StringComparison.OrdinalIgnoreCase) && !job.Reported && !job.Process.HasExited) return true;
        return false;
    }
    void MarkNotifiedSafely(SessionRecord record) {
        try { if (record != null) catalog.MarkNotified(record); }
        catch (Exception error) { MeetingFiles.Log(error); }
    }
    void RetrySession(string session) {
        if (exiting) return;
        try {
            if (recording != null && string.Equals(recording.DirectoryPath, session, StringComparison.OrdinalIgnoreCase)) {
                Notice("会议正在录音", "请先结束这场录音。", ToolTipIcon.Info); return;
            }
            var record = catalog.Get(session);
            if (record == null) throw new IOException("会议资料不存在。");
            if (ActiveJob(session)) { FocusSession(session, true); return; }
            if (!record.CanRetry) {
                FocusSession(session, true);
                Notice("请检查这场资料 · " + record.Label, record.Message, ToolTipIcon.Info); return;
            }
            LaunchWorker(session);
            if (recording == null) FocusSession(session, true);
        } catch (Exception error) { MeetingFiles.Log(error); Notice("无法重试这场会议", error.Message, ToolTipIcon.Error); }
    }
    void FocusSession(string session, bool show) {
        focusedSession = session;
        try { focusedRecord = catalog.Get(session); }
        catch (Exception error) { MeetingFiles.Log(error); focusedRecord = null; }
        RefreshFeedback();
        if (show) { panel.Show(); panel.Activate(); }
    }
    void RecordLaunchFailure(string session, string detail) {
        try {
            catalog.SaveWorker(session, null);
            var status = MeetingFiles.ReadJson(Path.Combine(session, "status.json"));
            status["state"] = "error"; status["stage"] = "launch";
            status["message"] = "转写未启动，原始资料已保留"; status["error"] = detail;
            status["updated_at"] = DateTimeOffset.Now.ToString("o");
            MeetingFiles.WriteJson(Path.Combine(session, "status.json"), status);
            if (recording == null) { focusedSession = session; focusedRecord = catalog.Get(session); }
        } catch (Exception error) { MeetingFiles.Log(error); }
    }
    void LaunchWorker(string session) {
        Process startedProcess = null;
        try {
            var record = catalog.Get(session);
            if (record == null) throw new IOException("会议资料不在当前程序的 work 中。");
            if (ActiveJob(session)) return;
            if (!record.CanRetry) throw new IOException(record.Message);
            DateTime previousStatus = File.GetLastWriteTimeUtc(Path.Combine(session, "status.json"));
            // Check metadata persistence before any potentially uploading worker
            // starts. A running worker must never be overwritten as launch-failed.
            catalog.SaveWorker(session, null);
            DateTime attemptStarted = DateTime.UtcNow;
            Process process;
            if (workerLauncher != null) process = workerLauncher(session);
            else {
                string python = Path.Combine(MeetingFiles.Home, ".venv", "Scripts", "python.exe");
                string worker = Path.Combine(MeetingFiles.Home, "meeting_worker.py");
                if (!File.Exists(python) || !File.Exists(worker)) throw new FileNotFoundException("转写程序未安装完整；录音已保留。");
                process = Process.Start(new ProcessStartInfo {
                    FileName = python, Arguments = Quote(worker) + " --session " + Quote(session),
                    WorkingDirectory = MeetingFiles.Home, UseShellExecute = false, CreateNoWindow = true
                });
            }
            if (process == null) throw new IOException("未能启动转写进程。");
            startedProcess = process;
            jobs.Add(new TranscriptionJob { Session = session, Process = process, PreviousStatusWriteUtc = previousStatus, WorkerStartedUtc = attemptStarted });
            try { catalog.SaveWorker(session, process); }
            catch (Exception persistenceError) {
                MeetingFiles.Log(persistenceError);
                // Legacy identity is a fallback if the per-meeting file suddenly
                // becomes unwritable. Do not overwrite the worker's status.
                try {
                    MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new {
                        session_dir = session, worker_pid = process.Id,
                        worker_start_time_utc = process.StartTime.ToUniversalTime().ToString("o")
                    });
                } catch (Exception fallbackError) { MeetingFiles.Log(fallbackError); }
                Notice("进度信息保存异常", "转写仍在进行，请等待这场完成后退出听记；原始资料已保留。", ToolTipIcon.Warning);
            }
            SaveLastSession(session, process);
            if (recording == null) { focusedSession = session; focusedRecord = catalog.Get(session); }
            RefreshFeedback();
            Notice("录音已保存 · " + record.Label, ((string)config["transcription_mode"] == "api" ? "讲话片段将提交配置的 API。" : "后台本地转写中。") + "托盘 → 最近会议可查看这场进度。", ToolTipIcon.Info);
        } catch (Exception error) {
            MeetingFiles.Log(error);
            if (startedProcess != null) {
                Notice("转写状态需要检查", "后台处理已启动，原始资料已保留；请从最近会议查看对应场次。", ToolTipIcon.Warning);
                return;
            }
            RecordLaunchFailure(session, error.Message);
            RefreshFeedback();
            Notice("转写未启动", error.Message + " 请在最近会议中选择对应场次检查或重试。", ToolTipIcon.Error);
        }
    }
    void ReportJobFailure(TranscriptionJob job, string detail) {
        job.Reported = true;
        SessionRecord record = null;
        try { record = catalog.Get(job.Session); } catch (Exception error) { MeetingFiles.Log(error); }
        MarkNotifiedSafely(record);
        if (recording == null) { focusedSession = job.Session; focusedRecord = record; }
        string label = record == null ? Path.GetFileName(job.Session) : record.Label;
        Notice("转写失败 · " + label, (string.IsNullOrEmpty(detail) ? "转写进程退出，未生成完成状态。" : detail) + " 原始资料已保留；托盘 → 最近会议 → " + label + " 检查或重试。", ToolTipIcon.Error);
    }
    void Tick() {
        if (recording != null && !recording.Stopping) {
            panel.SetElapsed(recording.Elapsed);
            panel.SetInputLevels(recording.MicrophonePeak, recording.SystemPeak);
            if (++deviceCheckTicks >= 4) {
                deviceCheckTicks = 0;
                if ((bool)config["notify_device_changes"]) {
                    string warning = recording.CheckDefaultDeviceChanges();
                    if (!string.IsNullOrEmpty(warning)) {
                        panel.SetDeviceWarning(warning);
                        Notice("默认音频设备已变化", warning, ToolTipIcon.Warning);
                    }
                }
            }
        }
        foreach (TranscriptionJob job in jobs.ToArray()) {
            if (job.Reported) continue;
            try {
                string statusPath = Path.Combine(job.Session, "status.json");
                var status = MeetingFiles.ReadJson(statusPath);
                bool workerExited = job.Process.HasExited;
                if (workerExited) status = MeetingFiles.ReadJson(statusPath);
                DateTime statusWrite = File.GetLastWriteTimeUtc(statusPath);
                bool fresh = File.Exists(statusPath) && statusWrite >= job.WorkerStartedUtc &&
                    (job.PreviousStatusWriteUtc == DateTime.MinValue || statusWrite > job.PreviousStatusWriteUtc);
                if (fresh) job.LastMessage = SessionCatalog.DisplayTitle(status);
                if (!workerExited) continue;
                var record = catalog.Get(job.Session);
                if (fresh && job.Process.ExitCode == 0 && record != null && record.State == "completed") {
                    job.Reported = true;
                    MarkNotifiedSafely(record);
                    if (recording == null) { focusedSession = job.Session; focusedRecord = record; }
                    Notice("会议笔记已生成 · " + record.Label, "录音与转录已保存；托盘 → 最近会议可打开这场笔记。", ToolTipIcon.Info);
                    // Completing A must never take focus away from meeting B.
                    if (recording == null && (bool)config["auto_open_completed_note"] && (bool)config["open_in_obsidian"]) {
                        try { OpenNote(record.NotePath, true); }
                        catch (Exception openError) { MeetingFiles.Log(openError); Notice("笔记已保存，未能自动打开", "请从最近会议手动打开对应笔记。", ToolTipIcon.Warning); }
                    }
                } else {
                    string detail = MeetingFiles.Text(status, "error", MeetingFiles.Text(status, "artifact_error", ""));
                    if (string.IsNullOrEmpty(detail)) detail = record == null ? "无法读取这场会议的有效状态。" : record.Message;
                    ReportJobFailure(job, detail);
                }
            } catch (Exception error) {
                MeetingFiles.Log(error);
                if (job.Process.HasExited) ReportJobFailure(job, "无法读取有效状态：" + error.Message);
            }
            if (job.Reported) { jobs.Remove(job); job.Process.Dispose(); }
        }
        RefreshFeedback();
    }
    void SetTrayText(string value) { tray.Text = value.Length > 63 ? value.Substring(0, 62) + "…" : value; }
    void RefreshFeedback() {
        if (recording != null) {
            string state = recording.Stopping ? "正在保存录音" : recording.RecordingPaused ? "整场已暂停" : recording.MicrophonePaused ? "麦克风已关闭 · 电脑声录制中" : "录音中";
            string text = "听记 · " + state + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
            summary.Text = text; SetTrayText(text);
            return;
        }
        if (focusedSession != null) {
            foreach (var job in jobs) {
                if (string.Equals(job.Session, focusedSession, StringComparison.OrdinalIgnoreCase) && !job.Reported) {
                    string title = string.IsNullOrEmpty(job.LastMessage) ? "录音已保存 · 转写中" : job.LastMessage;
                    string label = focusedRecord == null ? Path.GetFileName(focusedSession) : focusedRecord.Label;
                    panel.ShowProcessing(title, label + " · 托盘查看对应会议");
                    summary.Text = label + " · " + title + (jobs.Count > 1 ? " · 后台 " + jobs.Count + " 场" : "");
                    SetTrayText("听记 · " + summary.Text); return;
                }
            }
            if (focusedRecord == null || !string.Equals(focusedRecord.DirectoryPath, focusedSession, StringComparison.OrdinalIgnoreCase)) {
                try { focusedRecord = catalog.Get(focusedSession); }
                catch (Exception error) {
                    MeetingFiles.Log(error);
                    panel.ShowProcessing("会议资料需要检查", "资料未修改，请检查 work 中对应场次");
                    summary.Text = "会议资料需要检查" + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
                    SetTrayText("听记 · " + summary.Text); return;
                }
            }
            if (focusedRecord != null) {
                string title = StateLabel(focusedRecord);
                panel.ShowProcessing(title, focusedRecord.Label + " · " + focusedRecord.Message);
                summary.Text = focusedRecord.Label + " · " + title + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
                SetTrayText("听记 · " + summary.Text); return;
            }
        }
        panel.ShowStatus("听记已就绪", StartHotkey + " 开始录制");
        summary.Text = "听记已就绪"; SetTrayText("听记 · " + StartHotkey);
    }
    static string StateLabel(string state) {
        switch (state) {
            case "completed": return "录音与转录已完成";
            case "interrupted": return "录音中断 · 检查资料";
            case "failed": return "转写失败 · 可重试";
            case "retry": return "录音已保留 · 可重试";
            case "missing": return "成品缺失 · 检查资料";
            case "processing": return "转写中";
            case "queued": return "等待上一场转写";
            default: return "会议资料需要检查";
        }
    }
    static string StateLabel(SessionRecord record) {
        if (!record.CanRetry && (record.State == "failed" || record.State == "retry")) return "转写未完成 · 检查资料";
        return StateLabel(record.State);
    }
    void RefreshRecentMenu() {
        try {
            recent.DropDownItems.Clear();
            var records = catalog.Snapshot();
            // Keep the tray compact while retaining a route to every local folder.
            var displayed = new List<SessionRecord>();
            foreach (var record in records) if (record.State != "completed" && !record.WorkerAlive && displayed.Count < 10) displayed.Add(record);
            foreach (var record in records) if (displayed.Count < 20 && !displayed.Contains(record)) displayed.Add(record);
            foreach (var record in displayed) {
                string session = record.DirectoryPath;
                bool current = recording != null && string.Equals(recording.DirectoryPath, session, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(record.Label + " · " + (current ? "正在录音" : StateLabel(record)));
                item.DropDownItems.Add("查看状态", null, delegate { FocusSession(session, true); });
                if (!string.IsNullOrEmpty(record.NotePath) && File.Exists(record.NotePath))
                    item.DropDownItems.Add("打开转录笔记", null, delegate { OpenNoteForSession(session); });
                if (!current && record.CanRetry) item.DropDownItems.Add("重试这场转写", null, delegate { RetrySession(session); });
                item.DropDownItems.Add("查看录音资料", null, delegate { OpenSessionFolder(session); });
                recent.DropDownItems.Add(item);
            }
            if (displayed.Count == 0) recent.DropDownItems.Add(new ToolStripMenuItem("尚无会议") { Enabled = false });
            if (records.Count > displayed.Count) recent.DropDownItems.Add("其余会话资料", null, delegate { OpenFolder(Path.Combine(MeetingFiles.Home, "work")); });
        } catch (Exception error) { MeetingFiles.Log(error); recent.DropDownItems.Add(new ToolStripMenuItem("资料读取异常，请检查 work") { Enabled = false }); }
    }
    void OpenSessionFolder(string session) {
        try {
            var record = catalog.Get(session);
            if (record == null) throw new IOException("会议资料不存在。");
            string folder = !string.IsNullOrEmpty(record.AudioPath) && File.Exists(record.AudioPath) ? Path.GetDirectoryName(record.AudioPath) : record.DirectoryPath;
            OpenFolder(folder);
        } catch (Exception error) { Notice("无法打开这场资料", error.Message, ToolTipIcon.Warning); }
    }
    void OpenNoteForSession(string session) {
        try {
            var record = catalog.Get(session);
            if (record == null || !File.Exists(record.NotePath)) throw new IOException("转录笔记已被移动或删除，请检查会议记录文件夹。");
            OpenNote(record.NotePath, false);
        } catch (Exception error) { Notice("无法打开这场笔记", error.Message, ToolTipIcon.Warning); }
    }
    void OpenNote(string path, bool automatic) {
        if (automatic && (recording != null || !(bool)config["auto_open_completed_note"] || !(bool)config["open_in_obsidian"])) return;
        string root = MeetingFiles.VaultRoot(config).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("笔记路径不在配置的保存目录中。");
        MeetingConfig.CheckOrdinaryPath(full);
        if (!File.Exists(full)) throw new IOException("转录笔记已被移动或删除。");
        if (noteOpener != null) { noteOpener(full, automatic); return; }
        if (!(bool)config["open_in_obsidian"]) {
            try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(full)) { UseShellExecute = true }); }
            return;
        }
        string relative = full.Substring(root.Length).Replace('\\', '/');
        string vault = MeetingFiles.Text(config, "vault_id", "");
        if (string.IsNullOrWhiteSpace(vault)) vault = MeetingFiles.Text(config, "vault_name", "");
        if (string.IsNullOrWhiteSpace(vault)) vault = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        string uri = "obsidian://open?vault=" + Uri.EscapeDataString(vault) + "&file=" + Uri.EscapeDataString(relative);
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }
    void RequestExit() {
        exiting = true;
        toggle.Enabled = false;
        if (recording != null) Stop(); else ExitThread();
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
    static int CheckConfig(string output) {
        try {
            var config = MeetingConfig.Load();
            MeetingFiles.WriteJson(output, new { ok = true, hotkey = config["hotkey"], pause_hotkey = config["pause_hotkey"], mode = config["transcription_mode"] });
            return 0;
        } catch (Exception error) {
            MeetingFiles.WriteJson(output, new { ok = false, error = error.Message });
            return 2;
        }
    }
    static int Diagnose(string output) {
        var checks = new List<object>();
        var endpoints = new List<object>();
        bool healthy = true;
        Dictionary<string, object> config = null;
        try { config = MeetingConfig.Load(); checks.Add(new { id = "config", ok = true, message = "配置有效" }); }
        catch (Exception error) { healthy = false; checks.Add(new { id = "config", ok = false, message = error.Message }); }
        using (var enumerator = new MMDeviceEnumerator()) {
            foreach (DataFlow flow in new DataFlow[] { DataFlow.Capture, DataFlow.Render }) {
                string id = flow == DataFlow.Capture ? "microphone" : "system_audio";
                try {
                    Role role = flow == DataFlow.Capture ? Role.Console : Role.Multimedia;
                    using (var currentDefault = enumerator.GetDefaultAudioEndpoint(flow, role))
                        endpoints.Add(new { id = currentDefault.ID, name = currentDefault.FriendlyName, flow = flow.ToString(), role = role.ToString(), selection = "system_default" });
                    string configured = config == null ? "" : (string)config[flow == DataFlow.Capture ? "mic_device_id" : "system_device_id"];
                    using (var device = string.IsNullOrWhiteSpace(configured) ? enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Console : Role.Multimedia) : enumerator.GetDevice(configured)) {
                        bool valid = device.State == DeviceState.Active && device.DataFlow == flow;
                        if (!valid) healthy = false;
                        checks.Add(new { id = id, ok = valid, message = valid ? device.FriendlyName : "所选设备类型错误或不可用" });
                        endpoints.Add(new { id = device.ID, name = device.FriendlyName, flow = flow.ToString(), selection = string.IsNullOrWhiteSpace(configured) ? "system_default" : "configured" });
                    }
                    foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                        using (device) endpoints.Add(new { id = device.ID, name = device.FriendlyName, flow = flow.ToString(), selection = "available" });
                } catch (Exception) {
                    healthy = false;
                    checks.Add(new { id = id, ok = false, message = "无法获取所选/默认音频设备，请检查 Windows 声音设置后重新自检" });
                }
            }
        }
        MeetingFiles.WriteJson(output, new { ok = healthy, checks = checks, devices = endpoints, audio_recorded = false });
        return healthy ? 0 : 2;
    }
    [STAThread]
    static int Main(string[] args) {
        try {
            if (args.Length == 2 && args[0] == "--check-config") return CheckConfig(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--diagnose") return Diagnose(Path.GetFullPath(args[1]));
            if (args.Length > 0 && args[0] == "--list-devices") return ListDevices();
            if (args.Length > 0 && args[0] == "--self-test-timeline") return SelfTestTimeline();
            if (args.Length > 0) return 2;
            bool first;
            using (var mutex = new Mutex(true, "Local\\ObsidianMeetingHotkey", out first)) {
                if (!first) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var context = new MeetingContext()) Application.Run(context);
            }
            return 0;
        } catch (Exception error) {
            if (args.Length > 0 && (args[0] == "--diagnose" || args[0] == "--check-config")) {
                if (args.Length == 2) try { MeetingFiles.WriteJson(Path.GetFullPath(args[1]), new { ok = false, error = "原生检查失败，请检查 JSON 格式、文件权限或音频设备" }); } catch { }
                return 2;
            }
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
