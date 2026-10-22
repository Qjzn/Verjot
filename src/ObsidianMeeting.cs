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

[assembly: System.Reflection.AssemblyTitle("verbit")]
[assembly: System.Reflection.AssemblyProduct("verbit")]
[assembly: System.Reflection.AssemblyDescription("轻量中文会议录音与转写")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

internal static class MeetingBrand {
    public const string Name = "verbit";
    public static readonly Icon IdleIcon = LoadIcon("Tingji.Idle.ico", SystemInformation.SmallIconSize.Width);
    public static readonly Icon RecordingIcon = LoadIcon("Tingji.Recording.ico", SystemInformation.SmallIconSize.Width);
    public static Bitmap LogoBitmap(int pixels) {
        // Icon(stream, size) may choose a smaller nearest frame (36 -> 32).
        // Request the next embedded frame so fractional DPI only downsamples.
        int frame = 256;
        foreach (int size in new int[] { 16, 20, 24, 32, 48, 64, 128, 256 }) {
            if (size >= pixels) { frame = size; break; }
        }
        using (var icon = LoadIcon("Tingji.Idle.ico", frame)) return icon.ToBitmap();
    }
    static Icon LoadIcon(string resource, int pixels) {
        using (var stream = typeof(MeetingBrand).Assembly.GetManifestResourceStream(resource)) {
            if (stream == null) throw new IOException("verbit图标资源缺失，请运行 Build.ps1 重新构建。");
            using (var icon = new Icon(stream, new Size(pixels, pixels))) return (Icon)icon.Clone();
        }
    }
}

internal static class MeetingFiles {
    public static readonly string Home = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    public static Dictionary<string, object> ReadJson(string path) {
        bool observed = File.Exists(path);
        var retryClock = Stopwatch.StartNew();
        string snapshot;
        for (int attempt = 0; ; attempt++) {
            try {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8)) snapshot = reader.ReadToEnd();
                break;
            } catch (IOException error) {
                int code = error.HResult & 0xffff;
                bool sharing = code == 32 || code == 33;
                if (sharing) observed = true;
                bool replacing = observed && (error is FileNotFoundException || error is DirectoryNotFoundException);
                // Atomic replacement/short external locks can outlast three
                // scheduler ticks. A known path can briefly disappear too.
                // Unknown optional files keep their short missing-file policy;
                // permanent conflicts/disappearance remain explicit errors.
                if (attempt >= 3 && ((!sharing && !replacing) || retryClock.ElapsedMilliseconds >= 200)) {
                    if (!observed && (error is FileNotFoundException || error is DirectoryNotFoundException))
                        return new Dictionary<string, object>();
                    throw;
                }
                Thread.Sleep(10);
            } catch (UnauthorizedAccessException error) {
                // Windows can report ERROR_ACCESS_DENIED while an existing
                // file is delete-pending. Retry that brief window, but never
                // turn denied/uncertain metadata into an empty optional file.
                if ((error.HResult & 0xffff) != 5 || attempt >= 3) throw;
                observed = true;
                Thread.Sleep(10);
            }
        }
        return Json.Deserialize<Dictionary<string, object>>(snapshot);
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
    static int TextLength(string text) {
        int length = 0;
        for (int i = 0; i < text.Length; i++, length++)
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        return length;
    }
    public static Dictionary<string, object> Load(bool requireApiKey = true) {
        CheckOrdinaryPath(MeetingFiles.Home);
        string path = Path.Combine(MeetingFiles.Home, "config.json");
        if (!File.Exists(path)) throw new IOException("缺少 config.json，请运行 Install.ps1 或复制 config.example.json 后填写保存路径。");
        return Validate(MeetingFiles.ReadJson(path), requireApiKey);
    }
    public static Dictionary<string, object> Validate(Dictionary<string, object> config, bool requireApiKey = true) {
        var schema = MeetingFiles.ReadJson(Path.Combine(MeetingFiles.Home, "config.schema.json"));
        if (!schema.ContainsKey("properties")) throw new IOException("缺少 config.schema.json，请保留完整verbit源码目录。");
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
                int length = TextLength(text);
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
        var terms = new List<string>();
        foreach (string item in Regex.Split(MeetingFiles.Text(config, "asr_terms", ""), "[,，;；\\r\\n]+")) {
            string term = item.Trim();
            if (term.Length == 0 || terms.Contains(term)) continue;
            if (TextLength(term) > 64) throw new InvalidOperationException("asr_terms 每项最多64字符。");
            terms.Add(term);
        }
        if (terms.Count > 64) throw new InvalidOperationException("asr_terms 最多64项。");
        string context = MeetingFiles.Text(config, "asr_context", "");
        if (terms.Count > 0) context += (context.Length > 0 ? "\n" : "") + "术语：" + string.Join("、", terms.ToArray()) + "。";
        if (TextLength(context) > 2000) throw new InvalidOperationException("术语和上下文合计最多2000字符。");
        string root = MeetingFiles.VaultRoot(config);
        if (!Regex.IsMatch(root, @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))") ||
            !Regex.IsMatch((string)config["vault_root"], @"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))"))
            throw new InvalidOperationException("vault_root 必须是完整 Windows 绝对目录。");
        if (!Directory.Exists(root)) throw new IOException("vault_root 保存目录不存在，请先创建目录。");
        CheckOrdinaryPath(root);
        foreach (string field in new string[] { "meeting_folder", "quick_note_folder" }) {
            string folder = (string)config[field];
            if (Path.IsPathRooted(folder)) throw new InvalidOperationException(field + " 必须为保存目录内的相对路径。");
            foreach (string part in folder.Split(new char[] { '\\', '/' })) {
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    part.EndsWith(".") || part.EndsWith(" ") || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException(field + " 包含空段、路径穿越或 Windows 非法名称。");
            }
            CheckOrdinaryPath(Path.Combine(root, folder));
            if (File.Exists(Path.Combine(root, folder))) throw new IOException(field + " 已被同名文件占用，请改用目录。");
        }
        var shortcuts = new HashSet<string>();
        foreach (string field in new string[] { "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" }) {
            uint modifiers, key;
            ParseHotkey((string)config[field], out modifiers, out key);
            if (!shortcuts.Add(modifiers + ":" + key)) throw new InvalidOperationException("开始/结束、暂停、标记重点和速记快捷键必须各不相同。");
        }
        if ((string)config["transcription_mode"] == "api") {
            Uri uri;
            if (Regex.IsMatch((string)config["api_base_url"], @"\s") ||
                !Uri.TryCreate((string)config["api_base_url"], UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("api_base_url 必须是无密钥、无查询参数的 HTTP(S) API 前缀。");
            if (string.IsNullOrWhiteSpace((string)config["api_model"])) throw new InvalidOperationException("API 模式必须填写 api_model 音频转写模型名。");
            if (requireApiKey && string.IsNullOrWhiteSpace(ApiKeyValue((string)config["api_key_env"])))
                throw new InvalidOperationException("未设置 API 密钥环境变量：" + (string)config["api_key_env"] + "；请配置 Windows 用户环境变量后重试转写。");
        }
        return config;
    }
    public static string ApiKeyValue(string name) {
        // Read newly configured user variables without requiring a tray restart.
        // Never persist this value in config/session/status/log files.
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name);
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
    public static void CheckRecordingRuntime(string home) {
        if (!File.Exists(Path.Combine(home, ".venv", "Scripts", "python.exe")) || !File.Exists(Path.Combine(home, "meeting_worker.py")))
            throw new IOException("缺少独立 Python 转写环境。请打开「设置 → 转写 → 下载推荐模型…」准备环境，完成后从「最近会议」重试。原始录音已保留。");
    }
    public static string LocalRuntimeStatus(string home) {
        try { CheckTranscriptionRuntime(home, new Dictionary<string, object> { { "transcription_mode", "local" } }); }
        catch (IOException) { return "本地转写环境未就绪，请点击「下载推荐模型…」。仅选择本地不会下载；不影响录音和速记。"; }
        return "本地模型文件已就绪（Qwen3-ASR-1.7B）。实际转写时检查依赖与模型加载；音频留在本机。";
    }
    public static void CheckTranscriptionRuntime(string home, Dictionary<string, object> settings) {
        CheckRecordingRuntime(home);
        string models = Path.Combine(home, "models");
        bool segmentation = false;
        foreach (string file in new[] { Path.Combine(models, "segmentation", "model.onnx"),
            Path.Combine(models, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"),
            Path.Combine(models, "sherpa-onnx-pyannote-segmentation-3-0", "model.int8.onnx") })
            if (File.Exists(file) && new FileInfo(file).Length > 0) segmentation = true;
        if (!segmentation) throw new IOException("缺少本地说话人分段模型。请在「设置 → 转写 → 下载推荐模型…」准备，完成后重试；原始录音已保留。");
        var required = new List<string> {
            Path.Combine(models, "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx")
        };
        if (MeetingFiles.Text(settings, "transcription_mode", "local") == "local") {
            string qwen = Path.Combine(models, "Qwen3-ASR-1.7B-hf");
            foreach (string file in new[] { "config.json", "model.safetensors", "processor_config.json", "tokenizer_config.json", "tokenizer.json" }) required.Add(Path.Combine(qwen, file));
        }
        foreach (string file in required) if (!File.Exists(file) || new FileInfo(file).Length == 0)
            throw new IOException("本地转写模型尚未准备完整，请打开「设置 → 转写 → 下载推荐模型…」；完成后从「最近会议」重试。原始录音已保留。缺少：" + Path.GetFileName(file));
    }
}

// Audio callbacks are absent while WASAPI loopback is silent. Preserve that
// wall-clock gap in the WAV, including leading silence and the stop-time tail.
internal sealed class TimelineWriter : IDisposable {
    internal sealed class StorageException : IOException {
        public StorageException(string message, Exception cause) : base(message, cause) { }
    }
    readonly object gate = new object();
    WaveFileWriter writer;
    readonly WaveFormat format;
    readonly int byteRate;
    readonly int blockAlign;
    readonly int sampleRate, channels, bits;
    readonly bool floatSamples;
    readonly byte[] silence;
    readonly string path;
    readonly long segmentByteLimit;
    sealed class Segment {
        public string Path;
        public long Start, Bytes;
    }
    readonly List<Segment> segments = new List<Segment>();
    Segment currentSegment;
    readonly List<long[]> pausedRanges = new List<long[]>();
    long written;
    long endByteBudget = long.MaxValue;
    bool closed;
    double previousCallbackMs;
    bool hadCallback;
    bool paused;
    float peak;
    byte[] pendingPacket;
    int pendingCount;
    long pendingPosition;
    double pendingStartMs;
    double pendingQpcMs;
    double millisecondsPerFrame;
    sealed class ClockPoint {
        public double Position;
        public double TimeMs;
        public ClockPoint(double position, double timeMs) { Position = position; TimeMs = timeMs; }
    }
    readonly Queue<ClockPoint> clockPoints = new Queue<ClockPoint>();
    long lastClockPosition;
    double lastClockTimeMs;
    int clockWarmupPackets;
    double previousFitSlope, previousFitTarget;
    long previousFitPosition;
    bool hadClockFit;
    bool clockFitRejected;
    const double ClockWindowMs = 2000, ClockFitMinimumMs = 500, ClockPhaseCorrectionMs = 1000;
    // Below this bound a continuous sample counter cannot distinguish timestamp
    // jitter from a tiny unreported counter suspension. Never stretch a larger
    // interval, a counter jump, or a driver-reported discontinuity over speech.
    const double ContinuousTimestampUncertaintyMs = 2;
    public float Peak { get { lock (gate) return peak; } }
    public bool ClockFitRejected { get { lock (gate) return clockFitRejected; } }
    public WaveFormat Format { get { return format; } }
    public object[] Segments { get {
        lock (gate) {
            var result = new List<object>();
            foreach (Segment segment in segments) result.Add(new Dictionary<string, object> {
                { "path", Path.GetFileName(segment.Path) }, { "start_frame", segment.Start / blockAlign }, { "frames", segment.Bytes / blockAlign }
            });
            return result.ToArray();
        }
    } }

    // The two-argument path remains available to historical timeline self-tests.
    // Production CaptureTrack always supplies the bounded segment duration.
    public TimelineWriter(string path, WaveFormat format) : this(path, format, 0, false) { }
    public TimelineWriter(string path, WaveFormat format, int segmentSeconds) : this(path, format, segmentSeconds, true) { }
    TimelineWriter(string path, WaveFormat format, int segmentSeconds, bool segmented) {
        if (segmented && (segmentSeconds < 10 || segmentSeconds > 600))
            throw new ArgumentOutOfRangeException("segmentSeconds", "无损录音分段时长须为 10 到 600 秒。");
        this.path = path; this.format = format;
        byteRate = format.AverageBytesPerSecond;
        blockAlign = format.BlockAlign;
        sampleRate = format.SampleRate; channels = format.Channels; bits = format.BitsPerSample; floatSamples = IsFloat(format);
        // Reserve ample RIFF/header headroom, including unusual high-channel
        // mix formats. A frame is never split, and only one WAV remains open.
        segmentByteLimit = !segmented ? long.MaxValue :
            Math.Min((long)segmentSeconds * sampleRate * blockAlign, 0xf0000000L / blockAlign * blockAlign);
        millisecondsPerFrame = 1000.0 / format.SampleRate;
        silence = new byte[Math.Max(blockAlign, byteRate / blockAlign / 4 * blockAlign)];
        OpenSegment(0);
    }
    void OpenSegment(long firstByte) {
        string filename = segmentByteLimit == long.MaxValue ? path : Path.Combine(Path.GetDirectoryName(path),
            Path.GetFileNameWithoutExtension(path) + ".seg" + (segments.Count + 1).ToString("D6", CultureInfo.InvariantCulture) + ".wav");
        try { writer = new WaveFileWriter(filename, format); }
        catch (IOException error) { throw StorageFailure(error); }
        catch (UnauthorizedAccessException error) { throw StorageFailure(error); }
        currentSegment = new Segment { Path = filename, Start = firstByte };
        segments.Add(currentSegment);
    }
    long Bytes(double milliseconds) {
        return (long)Math.Max(0, milliseconds * byteRate / 1000.0) / blockAlign * blockAlign;
    }
    static StorageException StorageFailure(Exception error) {
        return new StorageException("录音文件无法继续写入，请检查磁盘空间和文件权限；正在保存已录内容。", error);
    }
    void WriteAudio(byte[] data, int count) {
        try {
            int offset = 0;
            while (offset < count) {
                if (currentSegment.Bytes == segmentByteLimit) {
                    CloseWriter(); OpenSegment(written);
                }
                int part = (int)Math.Min(count - offset, segmentByteLimit - currentSegment.Bytes);
                writer.Write(data, offset, part);
                currentSegment.Bytes += part; offset += part; written += part;
            }
        }
        catch (IOException error) { throw StorageFailure(error); }
        catch (UnauthorizedAccessException error) { throw StorageFailure(error); }
        catch (ArgumentException error) {
            // This is the bundled NAudio writer's explicit RIFF/UInt32 guard,
            // not an arbitrary bad buffer/format argument from the caller.
            if (!error.Message.StartsWith("WAV file too large", StringComparison.Ordinal)) throw;
            throw new StorageException("录音文件达到 WAV 4 GB 上限，正在停止并保存已录内容。", error);
        }
    }
    void CloseWriter() {
        try { writer.Dispose(); }
        catch (IOException error) { throw StorageFailure(error); }
        catch (UnauthorizedAccessException error) { throw StorageFailure(error); }
    }
    void PadTo(long target) {
        target = Math.Min(target, endByteBudget);
        while (written < target) {
            int count = (int)Math.Min(silence.Length, target - written);
            WriteAudio(silence, count);
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
            WriteAudio(audio, count);
            previousCallbackMs = elapsedMs;
            hadCallback = true;
            peak = paused ? 0 : MeasurePeak(audio, count, format);
        }
    }
    void ResetClock(long position, double timeMs) {
        clockPoints.Clear(); lastClockPosition = position; lastClockTimeMs = timeMs; clockWarmupPackets = 0;
        millisecondsPerFrame = 1000.0 / sampleRate;
        hadClockFit = false;
    }
    double ClockTarget(long position, double timeMs) {
        // Adjacent-point averaging suppresses packet-period timestamp jitter.
        // The very first interval is excluded from the fit because startup
        // timestamps can carry a different bias from an established stream.
        double pointPosition = position / 2.0 + lastClockPosition / 2.0;
        double pointTimeMs = timeMs / 2.0 + lastClockTimeMs / 2.0;
        lastClockPosition = position; lastClockTimeMs = timeMs;
        if (clockWarmupPackets++ == 0) return double.NaN;
        clockPoints.Enqueue(new ClockPoint(pointPosition, pointTimeMs));
        while (clockPoints.Count > 2 && (clockPoints.Count > 512 || (position - clockPoints.Peek().Position) * 1000.0 / sampleRate > ClockWindowMs))
            clockPoints.Dequeue();
        if ((position - clockPoints.Peek().Position) * 1000.0 / sampleRate < ClockFitMinimumMs)
            return double.NaN;
        // Fit in a local coordinate system so multi-hour recordings do not lose
        // precision. QPC jitter affects the regression, never a packet boundary.
        double x = 0, y = 0, xx = 0, xy = 0;
        foreach (ClockPoint point in clockPoints) {
            double px = point.Position - position, py = point.TimeMs - timeMs;
            x += px; y += py; xx += px * px; xy += px * py;
        }
        double count = clockPoints.Count, denominator = xx - x * x / count;
        if (denominator <= 0) return double.NaN;
        double slope = (xy - x * y / count) / denominator;
        // A sustained percent-scale change is not normal sample-clock drift.
        // Keep the prior estimate rather than treating a suspect fit as audio.
        if (slope <= 0 || Math.Abs(slope * sampleRate / 1000.0 - 1) > .005) {
            clockFitRejected = true;
            return double.NaN;
        }
        double target = timeMs + (y - slope * x) / count;
        // Adjacent overlapping fits have opposite endpoint bias for alternating
        // timestamp jitter. Average their predictions at the same device frame.
        double mappedTarget = hadClockFit ? (target + previousFitTarget + (position - previousFitPosition) * previousFitSlope) / 2 : target;
        millisecondsPerFrame = hadClockFit ? (slope + previousFitSlope) / 2 : slope;
        previousFitSlope = slope; previousFitTarget = target; previousFitPosition = position; hadClockFit = true;
        return mappedTarget;
    }
    // Acquisition QPC determines the initial/global position. Continuous device
    // frames retain continuous resampling phase; a rolling fit estimates slow
    // clock drift without turning short QPC jitter into zero holes or cuts.
    // True reports a segment boundary, including an unflagged device/time gap.
    public bool AddTimestamped(byte[] buffer, int count, double startMs, long devicePosition, bool discontinuity) {
        lock (gate) {
            if (closed || count <= 0) return false;
            if (double.IsNaN(startMs) || double.IsInfinity(startMs)) throw new InvalidDataException("音频包时间戳无效。");
            count -= count % blockAlign;
            if (count == 0) return false;
            clockFitRejected = false;
            double mappedStartMs = startMs;
            bool segmented = false;
            if (pendingPacket != null) {
                long distance = devicePosition - pendingPosition;
                if (distance == 0 && Math.Abs(startMs - pendingQpcMs) < .001) return false; // duplicate packet
                if (distance < 0 || startMs < pendingQpcMs) throw new InvalidDataException("音频设备时间戳倒退，已保留此前录音。");
                int pendingFrames = pendingCount / blockAlign;
                double intervalErrorMs = startMs - pendingQpcMs - distance * millisecondsPerFrame;
                bool adjacent = distance == pendingFrames && !discontinuity && Math.Abs(intervalErrorMs) <= ContinuousTimestampUncertaintyMs;
                double endMs;
                if (adjacent) {
                    double target = ClockTarget(devicePosition, startMs);
                    mappedStartMs = pendingStartMs + pendingFrames * millisecondsPerFrame;
                    if (!double.IsNaN(target)) {
                        double portion = Math.Min(1, pendingFrames * millisecondsPerFrame / ClockPhaseCorrectionMs);
                        mappedStartMs += (target - mappedStartMs) * portion;
                    }
                    endMs = mappedStartMs;
                } else {
                    segmented = true;
                    endMs = Math.Min(startMs, pendingStartMs + pendingFrames * millisecondsPerFrame);
                    ResetClock(devicePosition, startMs);
                }
                WriteTimestampedPacket(pendingPacket, pendingCount, pendingStartMs, endMs,
                    adjacent ? buffer : null);
            } else ResetClock(devicePosition, startMs);
            pendingPacket = new byte[count];
            Buffer.BlockCopy(buffer, 0, pendingPacket, 0, count);
            pendingCount = count; pendingStartMs = mappedStartMs; pendingQpcMs = startMs; pendingPosition = devicePosition;
            return segmented;
        }
    }
    static bool IsFloat(WaveFormat format) {
        WaveFormatExtensible extended = format as WaveFormatExtensible;
        return format.Encoding == WaveFormatEncoding.IeeeFloat || (extended != null && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
    }
    double ReadSample(byte[] data, int frame, int channel) {
        int index = frame * blockAlign + channel * bits / 8;
        if (floatSamples) return BitConverter.ToSingle(data, index);
        if (bits == 16) return BitConverter.ToInt16(data, index) / 32768.0;
        if (bits == 24) {
            int value = data[index] | data[index + 1] << 8 | data[index + 2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
            return value / 8388608.0;
        }
        if (bits == 32) return BitConverter.ToInt32(data, index) / 2147483648.0;
        throw new InvalidDataException("不支持的录音采样格式。");
    }
    [StructLayout(LayoutKind.Explicit)]
    struct FloatBits {
        [FieldOffset(0)] public float Value;
        [FieldOffset(0)] public int Bits;
    }
    void WriteSample(byte[] data, int frame, int channel, double value) {
        int index = frame * blockAlign + channel * bits / 8;
        if (floatSamples) {
            // BitConverter.GetBytes allocated an array for every resampled
            // sample/channel. Reinterpret locally and write WAV little endian.
            int raw = new FloatBits { Value = (float)value }.Bits;
            for (int part = 0; part < 4; part++) data[index + part] = (byte)(raw >> (part * 8));
            return;
        }
        long maximum = (1L << (bits - 1)) - 1;
        long sample = Math.Max(-maximum - 1, Math.Min(maximum, (long)Math.Round(value * (maximum + 1))));
        for (int part = 0; part < bits / 8; part++) data[index + part] = (byte)(sample >> (part * 8));
    }
    void WriteTimestampedPacket(byte[] data, int count, double startMs, double endMs, byte[] following) {
        if (endMs <= startMs) return;
        double startFrame = startMs * sampleRate / 1000.0;
        double endFrame = endMs * sampleRate / 1000.0;
        long first = Math.Max(written / blockAlign, Math.Max(0, (long)Math.Round(startFrame)));
        long last = Math.Min(endByteBudget / blockAlign, Math.Max(0, (long)Math.Round(endFrame)));
        if (last <= first) return;
        PadTo(first * blockAlign);
        int frames = count / blockAlign;
        byte[] output = new byte[checked((int)(last - first) * blockAlign)];
        if (Math.Abs(endFrame - startFrame - frames) < .000001 && Math.Abs(startFrame - Math.Round(startFrame)) < .000001) {
            Buffer.BlockCopy(data, checked((int)(first - (long)Math.Round(startFrame)) * blockAlign), output, 0, output.Length);
        } else {
            // Continuous fractional phase avoids periodic dropped/duplicated
            // whole samples when clocks differ. No extra resampling dependency.
            for (int frame = 0; frame < output.Length / blockAlign; frame++) {
                double location = Math.Max(0, (first + frame - startFrame) * frames / (endFrame - startFrame));
                int left = Math.Min(frames - 1, (int)location);
                double fraction = Math.Min(1, location - left);
                for (int channel = 0; channel < channels; channel++) {
                    double a = ReadSample(data, left, channel);
                    double b = left + 1 < frames ? ReadSample(data, left + 1, channel) : following == null ? a : ReadSample(following, 0, channel);
                    WriteSample(output, frame, channel, a + (b - a) * fraction);
                }
            }
        }
        byte[] masked = MaskPausedAudio(output, output.Length);
        WriteAudio(masked, masked.Length);
        peak = paused ? 0 : MeasurePeak(masked, masked.Length, Format);
    }
    public void SetEndBoundary(double elapsedMs) {
        lock (gate) endByteBudget = Math.Min(endByteBudget, Bytes(elapsedMs));
    }
    void TrimExcessAfterClose(long limit) {
        if (written <= limit) return;
        // A callback burst can contain more data than its current wall-clock
        // position suggests. Repair each owned segment in absolute byte space.
        for (int index = segments.Count - 1; index >= 0; index--) {
            Segment segment = segments[index];
            if (segment.Start >= limit && index > 0) {
                File.Delete(segment.Path); segments.RemoveAt(index); continue;
            }
            long remaining = Math.Min(segment.Bytes, Math.Max(0, limit - segment.Start));
            if (remaining < segment.Bytes) { TrimWave(segment.Path, remaining); segment.Bytes = remaining; }
        }
        written = limit;
    }
    void TrimWave(string filename, long limit) {
        using (var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
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
        // A late click can affect closed segments too. Reopen each one alone.
        foreach (Segment segment in segments) {
            bool overlaps = false;
            foreach (long[] range in pausedRanges) overlaps |= range[0] < segment.Start + segment.Bytes && range[1] > segment.Start;
            if (overlaps) SilenceSegment(segment);
        }
    }
    void SilenceSegment(Segment segment) {
        using (var stream = new FileStream(segment.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) {
            var read = new BinaryReader(stream);
            stream.Position = 12;
            while (stream.Position + 8 <= stream.Length) {
                string kind = Encoding.ASCII.GetString(read.ReadBytes(4));
                uint size = read.ReadUInt32();
                long body = stream.Position;
                if (kind == "data") {
                    foreach (long[] range in pausedRanges) {
                        long start = Math.Max(0, Math.Min(segment.Bytes, range[0] - segment.Start));
                        long end = Math.Max(0, Math.Min(segment.Bytes, range[1] - segment.Start));
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
            try {
                if (pendingPacket != null) {
                    WriteTimestampedPacket(pendingPacket, pendingCount, pendingStartMs,
                        pendingStartMs + pendingCount / blockAlign * millisecondsPerFrame, null);
                    pendingPacket = null;
                }
                PadTo(endByteBudget);
            } catch {
                // Close even when the final packet/tail cannot be written. The
                // bundled WAV writer finalizes the prefix and closes its stream
                // in a finally; retain the original write failure if that fails too.
                closed = true;
                try { CloseWriter(); } catch (Exception error) { MeetingFiles.Log(error); }
                throw;
            }
            closed = true;
            CloseWriter();
            try { TrimExcessAfterClose(endByteBudget); SilencePausedRangesAfterClose(); }
            catch (IOException error) { throw StorageFailure(error); }
            catch (UnauthorizedAccessException error) { throw StorageFailure(error); }
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
    public void Dispose() { lock (gate) { if (!closed) { closed = true; CloseWriter(); } } }
}

internal sealed class CaptureTrack : IDisposable {
    readonly object lifecycleGate = new object();
    readonly WasapiCapture capture;
    readonly TimelineWriter writer;
    readonly Stopwatch clock;
    readonly string deviceId;
    readonly bool loopback;
    readonly double qpcOrigin100ns;
    readonly List<Dictionary<string, object>> diagnostics = new List<Dictionary<string, object>>();
    Thread captureThread;
    double drainUntilMs;
    Exception startupError;
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    readonly ManualResetEvent stopped = new ManualResetEvent(false);
    readonly Action<Exception> failed;
    volatile bool requestedStop;
    bool started;
    bool disposed;
    volatile bool ended;
    public readonly string DeviceName;
    public readonly string PathName;
    public double OffsetMs;
    public string DeviceId { get { return deviceId; } }
    public bool Running { get { return started && !ended; } }
    public object[] Diagnostics { get {
        lock (diagnostics) {
            var snapshot = new List<object>();
            foreach (Dictionary<string, object> entry in diagnostics) snapshot.Add(new Dictionary<string, object>(entry));
            return snapshot.ToArray();
        }
    } }
    public float Peak { get { return writer.ReadPeak(); } }
    public WaveFormat Format { get { return writer.Format; } }
    public object[] Segments { get { return writer.Segments; } }
    public CaptureTrack(WasapiCapture source, string name, string path, Stopwatch sessionClock, Action<Exception> onFailed)
        : this(source, name, path, sessionClock, onFailed, 300) { }
    public CaptureTrack(WasapiCapture source, string name, string path, Stopwatch sessionClock, Action<Exception> onFailed, int segmentSeconds) {
        capture = source;
        DeviceName = name;
        PathName = path;
        clock = sessionClock;
        failed = onFailed;
        writer = new TimelineWriter(path, capture.WaveFormat, segmentSeconds);
        capture.DataAvailable += delegate(object sender, WaveInEventArgs args) {
            try { writer.Add(args.Buffer, args.BytesRecorded, clock.Elapsed.TotalMilliseconds); }
            catch (Exception error) { failed(error); }
        };
        capture.RecordingStopped += delegate(object sender, StoppedEventArgs args) {
            ended = true;
            stopped.Set();
            if (!requestedStop) failed(args.Exception ?? new IOException("音频设备停止，已结束录音并保留音频。"));
        };
    }
    public CaptureTrack(MMDevice source, bool isLoopback, string path, Stopwatch sessionClock, Action<Exception> onFailed)
        : this(source, isLoopback, path, sessionClock, onFailed, 300) { }
    public CaptureTrack(MMDevice source, bool isLoopback, string path, Stopwatch sessionClock, Action<Exception> onFailed, int segmentSeconds) {
        deviceId = source.ID; loopback = isLoopback;
        DeviceName = source.FriendlyName; PathName = path; clock = sessionClock; failed = onFailed;
        // Both clocks use QueryPerformanceCounter. WASAPI returns its value in
        // 100 ns units; calibrate the shared session zero without DateTime.
        long before = Stopwatch.GetTimestamp();
        double elapsed = sessionClock.Elapsed.TotalMilliseconds;
        long after = Stopwatch.GetTimestamp();
        qpcOrigin100ns = (before / 2.0 + after / 2.0) * 10000000.0 / Stopwatch.Frequency - elapsed * 10000.0;
        writer = new TimelineWriter(path, source.AudioClient.MixFormat, segmentSeconds);
    }
    void Diagnostic(string message, double atMs, bool uncertain) {
        lock (diagnostics) {
            foreach (Dictionary<string, object> entry in diagnostics) if ((string)entry["message"] == message) {
                entry["last_at_seconds"] = atMs / 1000.0; entry["count"] = (int)entry["count"] + 1; return;
            }
            diagnostics.Add(new Dictionary<string, object> {
                { "at_seconds", atMs / 1000.0 }, { "last_at_seconds", atMs / 1000.0 }, { "count", 1 },
                { "message", message }, { "timing_uncertain", uncertain }
            });
        }
    }
    internal static bool SameCaptureFormat(WaveFormat expected, WaveFormat actual) {
        // WAVEFORMATEX also carries encoding, alignment and extensible channel
        // mask/valid precision. Equal rate/channels/width alone can accept PCM32
        // bytes under a float32 WAV header and turn valid speech into NaNs.
        using (var first = new MemoryStream())
        using (var second = new MemoryStream())
        using (var firstWriter = new BinaryWriter(first))
        using (var secondWriter = new BinaryWriter(second)) {
            expected.Serialize(firstWriter); actual.Serialize(secondWriter);
            byte[] a = first.ToArray(), b = second.ToArray();
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false;
            return true;
        }
    }
    internal static double ResolvePacketStart(double startMs, double arrivalMs, int frames, int rate,
        long position, long previousPosition, double previousStartMs, int previousFrames, bool invalid, out bool estimated) {
        // Driver QPC is the packet's capture time, never minutes after arrival.
        // Keep valid delayed packets; callback scheduling is not capture timing.
        estimated = invalid || double.IsNaN(startMs) || double.IsInfinity(startMs) || startMs > arrivalMs + 20 ||
            (!double.IsNaN(previousStartMs) && startMs < previousStartMs);
        if (!estimated) return startMs;
        double fallback = arrivalMs - frames * 1000.0 / rate;
        if (!double.IsNaN(previousStartMs)) {
            double fromPosition = previousStartMs + (position - previousPosition) * 1000.0 / rate;
            if (position >= previousPosition + previousFrames && fromPosition <= arrivalMs + 20)
                fallback = fromPosition;
            fallback = Math.Max(previousStartMs + previousFrames * 1000.0 / rate, fallback);
        }
        return Math.Max(0, Math.Min(arrivalMs, fallback));
    }
    void CaptureTimestamped() {
        try {
            using (var enumerator = new MMDeviceEnumerator())
            using (var device = enumerator.GetDevice(deviceId))
            using (var client = device.AudioClient) {
                WaveFormat format = client.MixFormat;
                if (!SameCaptureFormat(writer.Format, format))
                    throw new IOException("音频设备格式在启动时变化，请重新连接设备。");
                client.Initialize(AudioClientShareMode.Shared, loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
                    1000000, 0, format, Guid.Empty);
                using (var packets = client.AudioCaptureClient) {
                    double lastStartMs = double.NaN;
                    long lastPosition = 0;
                    int lastFrames = 0;
                    OffsetMs = clock.Elapsed.TotalMilliseconds;
                    client.Start(); ready.Set();
                    while (!requestedStop || clock.Elapsed.TotalMilliseconds < drainUntilMs) {
                        while (packets.GetNextPacketSize() > 0) {
                            int frames; AudioClientBufferFlags flags; long position, qpc;
                            IntPtr address = packets.GetBuffer(out frames, out flags, out position, out qpc);
                            if (frames <= 0) break;
                            byte[] buffer = new byte[checked(frames * format.BlockAlign)];
                            try { if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(address, buffer, 0, buffer.Length); }
                            finally { packets.ReleaseBuffer(frames); }
                            double arrivalMs = clock.Elapsed.TotalMilliseconds;
                            bool timestampError;
                            double startMs = ResolvePacketStart((qpc - qpcOrigin100ns) / 10000.0, arrivalMs,
                                frames, format.SampleRate, position, lastPosition, lastStartMs, lastFrames,
                                (flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0, out timestampError);
                            bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                            if (timestampError) {
                                Diagnostic("设备包时间戳无效或超前，已按采样位置及到达边界估计；本段时间精度需核对。", arrivalMs, true);
                                if (position < lastPosition + lastFrames && !double.IsNaN(lastStartMs)) position = lastPosition + lastFrames;
                            }
                            if (discontinuity && !double.IsNaN(lastStartMs)) Diagnostic("设备报告数据间断（静音或采集缺口），已保留硬件时间线。", startMs, true);
                            bool segmented = writer.AddTimestamped(buffer, buffer.Length, startMs, position, discontinuity || timestampError);
                            if (segmented && !discontinuity && !timestampError)
                                Diagnostic("设备采样位置或时间戳出现未标记的缺口，已分段保留；本段时间精度需核对。", startMs, true);
                            if (writer.ClockFitRejected)
                                Diagnostic("设备采样时钟拟合超出可校正范围，未按可疑时钟拉伸音频；本段时间精度需核对。", startMs, true);
                            lastStartMs = startMs; lastPosition = position; lastFrames = frames;
                        }
                        Thread.Sleep(10);
                    }
                    try { client.Stop(); }
                    catch (COMException error) {
                        // Route removal may make Stop unsupported after all packets
                        // have drained. It does not invalidate the preceding audio.
                        Diagnostic("设备停止时报告异常，停止前音频已保留（错误码 0x" + error.ErrorCode.ToString("X8") + "）。", clock.Elapsed.TotalMilliseconds, false);
                        MeetingFiles.Log(error);
                    }
                }
            }
        } catch (Exception error) {
            startupError = error;
            Diagnostic("音频设备采集异常（错误码 0x" + error.HResult.ToString("X8") + "），此前音频已保留，请检查设备连接。", clock.Elapsed.TotalMilliseconds, true);
            if (!requestedStop) failed(error);
        } finally { ended = true; ready.Set(); stopped.Set(); }
    }
    public void Start() {
        OffsetMs = clock.Elapsed.TotalMilliseconds;
        if (deviceId != null) {
            captureThread = new Thread(CaptureTimestamped) { IsBackground = true, Name = "Tingji audio " + DeviceName };
            captureThread.SetApartmentState(ApartmentState.STA);
            started = true; captureThread.Start();
            if (!ready.WaitOne(5000)) throw new TimeoutException("启动录音设备超时：" + DeviceName);
            if (startupError != null) throw new IOException("无法启动录音设备：" + DeviceName, startupError);
            return;
        }
        try { capture.StartRecording(); started = true; }
        catch { stopped.Set(); throw; }
    }
    public void RequestStop() {
        if (requestedStop) return;
        drainUntilMs = clock.Elapsed.TotalMilliseconds + 250;
        requestedStop = true;
        if (!started) { stopped.Set(); return; }
        if (deviceId != null) return;
        try { capture.StopRecording(); }
        catch (Exception error) { MeetingFiles.Log(error); stopped.Set(); }
    }
    public void SetEndBoundary(double elapsedMs) { writer.SetEndBoundary(elapsedMs); }
    public void SetPaused(bool value, double elapsedMs) { writer.SetPaused(value, elapsedMs); }
    public void Finish(double stopMs) {
        lock (lifecycleGate) {
            if (disposed) return;
            // Never close a writer before its recording thread's last packet.
            if (!requestedStop) RequestStop();
            if (!stopped.WaitOne(5000)) throw new TimeoutException("等待录音设备停止超时，原始轨道保留：" + DeviceName);
            try { writer.Finish(Math.Max(0, stopMs)); }
            catch {
                try { Dispose(); } catch (Exception error) { MeetingFiles.Log(error); }
                throw;
            }
            Dispose();
        }
    }
    public void Dispose() {
        lock (lifecycleGate) {
            if (disposed) return;
            if (deviceId != null) {
                if (!requestedStop) RequestStop();
                if (!stopped.WaitOne(5000)) throw new TimeoutException("录音线程未停止，已保留轨道而不关闭正在写入的文件。");
            }
            // A timeout leaves a live writer intact and remains retryable.
            disposed = true;
            try { if (capture != null) capture.Dispose(); }
            finally { try { writer.Dispose(); } finally { stopped.Dispose(); ready.Dispose(); } }
        }
    }
}

internal sealed class CaptureRetryState {
    readonly Dictionary<string, int> failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, double> deadlines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    public bool CanRetry(string key, double nowMs) { double until; return !deadlines.TryGetValue(key, out until) || nowMs >= until; }
    public bool Fail(string key, double nowMs) {
        int count; failures.TryGetValue(key, out count); count = Math.Min(count + 1, 6); failures[key] = count;
        deadlines[key] = nowMs + Math.Min(60000, 3000 * (1 << (count - 1)));
        return count == 1;
    }
    public void Succeeded(string key) { failures.Remove(key); deadlines.Remove(key); }
    public void Clear() { failures.Clear(); deadlines.Clear(); }
}

internal static class CaptureEndpointSelection {
    public static string[] Outputs(string mode, string configured, string defaultId, IEnumerable<string> active) {
        if (!string.IsNullOrWhiteSpace(configured)) return new string[] { configured };
        if (mode == "default") return string.IsNullOrEmpty(defaultId) ? new string[0] : new string[] { defaultId };
        return new List<string>(active).ToArray();
    }
}

internal sealed class RecordingSession : IDisposable {
    sealed class Source {
        public string Key, Kind, DeviceId, Name, EndReason = "";
        public CaptureTrack Track;
        public double RequestedMs, EndMs;
        public bool Ended, Finished;
        public string Error = "";
    }
    sealed class EndpointListener : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient {
        readonly Action<string> changed;
        public EndpointListener(Action<string> callback) { changed = callback; }
        public void OnDeviceStateChanged(string id, DeviceState state) { changed(id); }
        public void OnDeviceAdded(string id) { changed(id); }
        public void OnDeviceRemoved(string id) { changed(id); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { changed(id); }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
    readonly object gate = new object();
    readonly Stopwatch clock = new Stopwatch();
    readonly DateTimeOffset started = DateTimeOffset.Now;
    readonly Dictionary<string, object> metadata;
    readonly Dictionary<string, object> config;
    readonly Action<Exception> failed;
    readonly MMDeviceEnumerator devices = new MMDeviceEnumerator();
    readonly List<Source> sources = new List<Source>();
    readonly Dictionary<string, Source> active = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
    readonly CaptureRetryState failedDevices = new CaptureRetryState();
    readonly Dictionary<string, double> notificationsAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    readonly Queue<string> notices = new Queue<string>();
    readonly List<string> captureProblems = new List<string>();
    readonly AutoResetEvent devicesChanged = new AutoResetEvent(false);
    readonly List<Dictionary<string, object>> microphonePauses = new List<Dictionary<string, object>>();
    readonly List<Dictionary<string, object>> recordingPauses = new List<Dictionary<string, object>>();
    readonly List<Dictionary<string, object>> bookmarks = new List<Dictionary<string, object>>();
    EndpointListener listener;
    Thread deviceThread;
    volatile bool stopping, microphonePaused, recordingPaused;
    int microphoneCount, outputCount, inventoryVersion, observedInventoryVersion;
    int storageFailureNotified;
    double stopBoundaryMs;
    bool disposed;
    bool initializing = true;
    string captureError;
    public readonly string DirectoryPath;
    public bool Stopping { get { return stopping; } }
    public bool MicrophonePaused { get { return microphonePaused; } }
    public bool RecordingPaused { get { return recordingPaused; } }
    public bool HasStorageFailure { get { return Volatile.Read(ref storageFailureNotified) != 0; } }
    public TimeSpan Elapsed { get { return clock.Elapsed; } }
    float Peak(string kind) {
        float peak = 0;
        lock (gate) foreach (Source source in sources) if (!source.Ended && source.Kind == kind && source.Track != null) peak = Math.Max(peak, source.Track.Peak);
        return peak;
    }
    public float MicrophonePeak { get { return Peak("microphone"); } }
    public float SystemPeak { get { return Peak("loopback"); } }
    string Names(string kind) {
        var names = new List<string>();
        lock (gate) foreach (Source source in sources) if (!source.Ended && source.Kind == kind && source.Track != null) names.Add(source.Name);
        return names.Count == 0 ? "等待可用设备" : string.Join("；", names.ToArray());
    }
    public string MicrophoneName { get { return Names("microphone"); } }
    public string SystemDeviceName { get { return Names("loopback"); } }

    public RecordingSession(Dictionary<string, object> settings, Action<Exception> failed) {
        config = settings; this.failed = failed;
        clock.Start();
        string audioFolder = Path.Combine(MeetingFiles.VaultRoot(config), MeetingFiles.Text(config, "meeting_folder", "会议记录"), "附件");
        string folder = Path.Combine(MeetingFiles.Home, "work");
        MeetingConfig.CheckOrdinaryPath(folder); Directory.CreateDirectory(folder); MeetingConfig.CheckOrdinaryPath(folder);
        DirectoryPath = Path.Combine(folder, started.ToString("yyyyMMdd-HHmmss"));
        if (Directory.Exists(DirectoryPath) || Directory.Exists(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))))
            DirectoryPath += "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Directory.CreateDirectory(DirectoryPath); MeetingConfig.CheckOrdinaryPath(DirectoryPath);
        metadata = new Dictionary<string, object> {
            { "schema_version", 2 }, { "start_time", started.ToString("o") }, { "state", "recording" },
            { "storage_mode", "minimal" }, { "audio_output_dir", Path.GetFullPath(Path.Combine(audioFolder, Path.GetFileName(DirectoryPath))) },
            { "note_link_style", (bool)config["open_in_obsidian"] ? "wiki" : "markdown" },
            { "transcription_deferred", true },
            { "transcription_mode", config["transcription_mode"] }, { "asr_denoise", config["asr_denoise"] },
            { "asr_context", MeetingFiles.Text(config, "asr_context", "") },
            { "asr_terms", MeetingFiles.Text(config, "asr_terms", "") },
            { "speaker_anchor_mode", MeetingFiles.Text(config, "speaker_anchor_mode", "legacy") },
            { "asr_segmentation", MeetingFiles.Text(config, "asr_segmentation", "speaker") },
            { "asr_echo_filter", config.ContainsKey("asr_echo_filter") ? config["asr_echo_filter"] : true },
            { "transcription_model", (string)config["transcription_mode"] == "api" ? config["api_model"] : "Qwen3-ASR-1.7B" },
            { "transcription_api_base_url", (string)config["transcription_mode"] == "api" ? ((string)config["api_base_url"]).TrimEnd('/') : "" },
            { "system_capture_mode", MeetingFiles.Text(config, "system_capture_mode", "all_active") },
            { "audio_format", "mp3" }, { "mp3_bitrate_kbps", config.ContainsKey("mp3_bitrate_kbps") ? config["mp3_bitrate_kbps"] : 192 },
            { "recording_segment_seconds", config.ContainsKey("recording_segment_seconds") ? config["recording_segment_seconds"] : 300 },
            { "timeline", "wasapi_device_qpc_100ns" }, { "session_dir", DirectoryPath },
            { "microphone_pauses", microphonePauses }, { "microphone_paused", false },
            { "recording_pauses", recordingPauses }, { "recording_paused", false }, { "bookmarks", bookmarks }
        };
        Save();
        try {
            listener = new EndpointListener(delegate(string id) {
                if (stopping) return;
                lock (gate) if (!string.IsNullOrEmpty(id)) notificationsAt[id] = clock.Elapsed.TotalMilliseconds;
                Interlocked.Increment(ref inventoryVersion);
                try { devicesChanged.Set(); } catch (ObjectDisposedException) { }
            });
            int registered = devices.RegisterEndpointNotificationCallback(listener);
            if (registered != 0) Marshal.ThrowExceptionForHR(registered);
            RefreshDevices(devices);
            if (active.Count == 0) throw new IOException("没有可捕获的音频设备，请检查 Windows 声音设置。");
            initializing = false;
            deviceThread = new Thread(DeviceLoop) { IsBackground = true, Name = "Tingji audio devices" };
            deviceThread.SetApartmentState(ApartmentState.STA); deviceThread.Start();
        } catch {
            stopping = true;
            try { FinalizeAudio(clock.Elapsed.TotalMilliseconds); } finally { Dispose(); }
            throw;
        }
    }
    bool Problem(string message) {
        lock (gate) {
            if (captureProblems.Contains(message)) return false;
            captureProblems.Add(message); return true;
        }
    }
    void MissingDevice(string message) { if (Problem(message)) lock (gate) notices.Enqueue(message); }
    Source[] Snapshot() { lock (gate) return sources.ToArray(); }
    static string EndpointKey(string kind, string id) { return kind + ":" + id; }
    static bool IsStorageFailure(Exception error) {
        Exception cause = error;
        while (cause != null && !(cause is TimelineWriter.StorageException)) cause = cause.InnerException;
        return cause != null;
    }
    void NotifyStorageFailure(Exception error) {
        if (!IsStorageFailure(error) || Interlocked.CompareExchange(ref storageFailureNotified, 1, 0) != 0) return;
        if (stopping) return;
        // Do not invoke the stop/save UI callback from a capture thread or while
        // holding gate: saving waits for those same threads to drain. Multiple
        // outputs can fail together when a disk fills; notify exactly once.
        ThreadPool.QueueUserWorkItem(delegate {
            try { if (!stopping && failed != null) failed(error); }
            catch (Exception callbackError) { MeetingFiles.Log(callbackError); }
        });
    }
    void CaptureFailed(Source source, string endpointKey, Exception error) {
        lock (gate) {
            source.Error = error.Message;
            if (!source.Ended) {
                source.Ended = true; source.EndMs = clock.Elapsed.TotalMilliseconds;
                source.EndReason = "capture_error";
            }
            Source current;
            if (active.TryGetValue(endpointKey, out current) && ReferenceEquals(current, source)) {
                active.Remove(endpointKey);
                bool firstFailure = failedDevices.Fail(endpointKey, clock.Elapsed.TotalMilliseconds);
                if (firstFailure || IsStorageFailure(error)) notices.Enqueue(IsStorageFailure(error) ? "录音文件无法继续保存，正在停止并保留已录内容。" : "音频设备采集中断：" + source.Name + "；其他设备继续录制。");
            }
        }
        MeetingFiles.Log(error);
        Problem(source.Name + "：音频设备采集异常（错误码 0x" + error.HResult.ToString("X8") + "），此前音频已保留，请检查设备连接。");
        NotifyStorageFailure(error);
        try { devicesChanged.Set(); } catch (ObjectDisposedException) { }
    }
    void OpenSource(MMDevice device, string kind) {
        string endpointKey = EndpointKey(kind, device.ID);
        Source source;
        lock (gate) {
            if (stopping || HasStorageFailure || active.ContainsKey(endpointKey) || !failedDevices.CanRetry(endpointKey, clock.Elapsed.TotalMilliseconds)) return;
            int number = kind == "microphone" ? ++microphoneCount : ++outputCount;
            string key = (kind == "microphone" ? "mic" : "system") + (number == 1 ? "" : "_" + number.ToString("00", CultureInfo.InvariantCulture));
            double at;
            source = new Source { Key = key, Kind = kind, DeviceId = device.ID, Name = device.FriendlyName,
                RequestedMs = notificationsAt.TryGetValue(device.ID, out at) ? at : initializing ? 0 : clock.Elapsed.TotalMilliseconds };
            sources.Add(source); active[endpointKey] = source;
        }
        try {
            var track = new CaptureTrack(device, kind == "loopback", Path.Combine(DirectoryPath, source.Key + ".wav"), clock, delegate(Exception error) {
                CaptureFailed(source, endpointKey, error);
            }, config.ContainsKey("recording_segment_seconds") ? Convert.ToInt32(config["recording_segment_seconds"]) : 300);
            lock (gate) {
                source.Track = track;
                track.SetPaused(recordingPaused || (kind == "microphone" && microphonePaused), clock.Elapsed.TotalMilliseconds);
            }
            source.Track.Start();
            lock (gate) if (!source.Ended) failedDevices.Succeeded(endpointKey);
            if (stopping) source.Track.SetEndBoundary(stopBoundaryMs);
            lock (gate) if (!initializing) notices.Enqueue((kind == "microphone" ? "麦克风已接入：" : "已覆盖电脑输出：") + source.Name);
        } catch (Exception error) {
            lock (gate) {
                source.Error = error.Message; source.Ended = true; source.EndMs = clock.Elapsed.TotalMilliseconds;
                source.EndReason = "open_failed"; active.Remove(endpointKey);
                bool firstFailure = failedDevices.Fail(endpointKey, clock.Elapsed.TotalMilliseconds);
                if (firstFailure || IsStorageFailure(error)) notices.Enqueue(IsStorageFailure(error) ? "录音文件无法创建或继续保存，正在停止并保留已录内容。" : "暂时无法捕获设备：" + source.Name + "；其他设备继续录制。");
            }
            Problem(source.Name + "：" + error.Message);
            NotifyStorageFailure(error);
        }
    }
    void EndSource(Source source, double atMs, string reason) {
        lock (gate) {
            if (source.Ended) return;
            source.Ended = true; source.EndMs = atMs; source.EndReason = reason;
            active.Remove(EndpointKey(source.Kind, source.DeviceId));
            notices.Enqueue("音频设备已断开或切换：" + source.Name + "；其他设备继续录制。");
        }
        if (source.Track != null) { source.Track.SetEndBoundary(atMs); source.Track.RequestStop(); }
    }
    void FinishEndedSources() {
        foreach (Source source in Snapshot()) if (source.Ended && !source.Finished && source.Track != null) {
            try { source.Track.SetEndBoundary(source.EndMs); source.Track.RequestStop(); source.Track.Finish(source.EndMs); }
            catch (Exception error) { Problem(source.Name + "：" + error.Message); NotifyStorageFailure(error); }
            source.Finished = true;
        }
    }
    void RefreshDevices(MMDeviceEnumerator enumerator) {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string micId = MeetingFiles.Text(config, "mic_device_id", "");
        try {
            using (var microphone = string.IsNullOrWhiteSpace(micId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : enumerator.GetDevice(micId)) {
                if (microphone.State == DeviceState.Active) { desired.Add(EndpointKey("microphone", microphone.ID)); OpenSource(microphone, "microphone"); }
                else MissingDevice("麦克风不可用，正在等待设备；电脑声音继续录制。");
            }
        } catch (COMException) { MissingDevice("麦克风不可用，正在等待设备；电脑声音继续录制。"); }
        var available = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (MMDevice output in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) available[output.ID] = output;
            string defaultId = "";
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) using (var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) defaultId = current.ID;
            string[] targetOutputs = CaptureEndpointSelection.Outputs(MeetingFiles.Text(config, "system_capture_mode", "all_active"), MeetingFiles.Text(config, "system_device_id", ""), defaultId, available.Keys);
            if (targetOutputs.Length == 0) MissingDevice("没有可用的电脑声音输出设备，正在等待设备；麦克风继续录制。");
            foreach (string id in targetOutputs) {
                MMDevice output;
                if (available.TryGetValue(id, out output)) { desired.Add(EndpointKey("loopback", id)); OpenSource(output, "loopback"); }
                else MissingDevice("指定的电脑声音输出设备不可用，正在等待设备；麦克风继续录制。");
            }
        } finally { foreach (MMDevice device in available.Values) device.Dispose(); }
        // Open replacements first. Shared QPC timestamps keep already-running
        // endpoints undisturbed when the default route changes.
        foreach (Source source in Snapshot()) if (!source.Ended && !desired.Contains(EndpointKey(source.Kind, source.DeviceId))) EndSource(source, clock.Elapsed.TotalMilliseconds, "device_removed_or_route_changed");
        FinishEndedSources(); Save();
    }
    void DeviceLoop() {
        try {
            using (var enumerator = new MMDeviceEnumerator()) while (!stopping) {
                devicesChanged.WaitOne(2000); if (stopping) break;
                int version = inventoryVersion;
                if (version != observedInventoryVersion) { lock (gate) failedDevices.Clear(); observedInventoryVersion = version; }
                try { RefreshDevices(enumerator); }
                catch (Exception error) { Problem("重新检测音频设备失败：" + error.Message); }
            }
        } catch (Exception error) { Problem("音频设备监测停止：" + error.Message); }
    }
    public string CheckDefaultDeviceChanges() {
        lock (gate) {
            if (notices.Count == 0) return null;
            string result = string.Join("；", notices.ToArray()); notices.Clear(); return result;
        }
    }
    void Save() {
        lock (gate) {
            var entries = new List<object>(); var offsets = new Dictionary<string, object>();
            var names = new Dictionary<string, object>(); var files = new Dictionary<string, object>();
            var starts = new Dictionary<string, object>();
            foreach (Source source in sources) {
                if (source.Track == null) continue;
                offsets[source.Key] = 0; names[source.Key] = source.Name; files[source.Key] = source.Track.PathName; starts[source.Key] = source.Track.OffsetMs;
                entries.Add(new Dictionary<string, object> {
                    { "source", source.Key }, { "kind", source.Kind }, { "path", Path.GetFileName(source.Track.PathName) },
                    { "segments", source.Track.Segments }, { "sample_rate", source.Track.Format.SampleRate },
                    { "channels", source.Track.Format.Channels }, { "bits_per_sample", source.Track.Format.BitsPerSample },
                    { "device_id", source.DeviceId }, { "name", source.Name }, { "requested_at_seconds", source.RequestedMs / 1000.0 },
                    { "opened_at_seconds", source.Track.OffsetMs / 1000.0 }, { "coverage_gap_seconds", Math.Max(0, source.Track.OffsetMs - source.RequestedMs) / 1000.0 },
                    { "closed_at_seconds", source.Ended ? (object)(source.EndMs / 1000.0) : null },
                    { "end_reason", source.EndReason }, { "error", source.Error }, { "diagnostics", source.Track.Diagnostics }
                });
            }
            metadata["sources"] = entries; metadata["capture_offsets_ms"] = offsets;
            metadata["device_names"] = names; metadata["audio_files"] = files; metadata["device_start_offsets_ms"] = starts;
            metadata["capture_problems"] = captureProblems.ToArray();
            MeetingFiles.WriteJson(Path.Combine(DirectoryPath, "session.json"), metadata);
        }
    }
    public double AddBookmark() {
        if (stopping) throw new InvalidOperationException("录音已结束，不能继续标记。");
        lock (gate) {
            double seconds = clock.Elapsed.TotalSeconds;
            bookmarks.Add(new Dictionary<string, object> { { "time_seconds", seconds }, { "label", "重点 " + (bookmarks.Count + 1) } });
            try { Save(); } catch { bookmarks.RemoveAt(bookmarks.Count - 1); throw; }
            return seconds;
        }
    }
    public void SetMicrophonePaused(bool value) {
        if (stopping || microphonePaused == value) return;
        lock (gate) {
            double at = clock.Elapsed.TotalMilliseconds; microphonePaused = value;
            foreach (Source source in sources) if (!source.Ended && source.Track != null && source.Kind == "microphone") source.Track.SetPaused(value || recordingPaused, at);
            if (value) microphonePauses.Add(new Dictionary<string, object> { { "start_seconds", at / 1000.0 }, { "end_seconds", null } });
            else microphonePauses[microphonePauses.Count - 1]["end_seconds"] = at / 1000.0;
            metadata["microphone_paused"] = value; Save();
        }
    }
    public void SetRecordingPaused(bool value) {
        if (stopping || recordingPaused == value) return;
        lock (gate) {
            double at = clock.Elapsed.TotalMilliseconds; recordingPaused = value;
            foreach (Source source in sources) if (!source.Ended && source.Track != null) source.Track.SetPaused(value || (source.Kind == "microphone" && microphonePaused), at);
            if (value) recordingPauses.Add(new Dictionary<string, object> { { "start_seconds", at / 1000.0 }, { "end_seconds", null } });
            else recordingPauses[recordingPauses.Count - 1]["end_seconds"] = at / 1000.0;
            metadata["recording_paused"] = value; Save();
        }
    }
    void FinalizeAudio(double stopMs) {
        foreach (Source source in Snapshot()) if (!source.Ended) EndSource(source, stopMs, "session_stopped");
        FinishEndedSources(); clock.Stop();
        foreach (Source source in Snapshot()) if (source.Track != null) foreach (Dictionary<string, object> diagnostic in source.Track.Diagnostics)
            if (diagnostic.ContainsKey("timing_uncertain") && (bool)diagnostic["timing_uncertain"]) Problem(source.Name + "：" + (string)diagnostic["message"]);
        captureError = string.Join("；", captureProblems.ToArray());
        metadata["state"] = string.IsNullOrEmpty(captureError) ? "recorded" : "recorded_with_error";
        metadata["end_time"] = started.AddMilliseconds(stopMs).ToString("o"); metadata["duration_seconds"] = stopMs / 1000.0;
        metadata["capture_error"] = captureError;
        if (microphonePaused && microphonePauses.Count > 0) microphonePauses[microphonePauses.Count - 1]["end_seconds"] = stopMs / 1000.0;
        if (recordingPaused && recordingPauses.Count > 0) recordingPauses[recordingPauses.Count - 1]["end_seconds"] = stopMs / 1000.0;
        Save();
    }
    public void StopAsync(Action<Exception> done) {
        if (stopping) return;
        stopping = true; stopBoundaryMs = clock.Elapsed.TotalMilliseconds; devicesChanged.Set();
        foreach (Source source in Snapshot()) if (source.Track != null && !source.Ended) source.Track.SetEndBoundary(stopBoundaryMs);
        ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null;
            try {
                if (deviceThread != null && !deviceThread.Join(6000)) throw new TimeoutException("等待设备监测线程停止超时。");
                FinalizeAudio(stopBoundaryMs);
            } catch (Exception error) { failure = error; MeetingFiles.Log(error); }
            finally { try { Dispose(); } catch (Exception error) { if (failure == null) failure = error; MeetingFiles.Log(error); } }
            done(failure);
        });
    }
    public void Dispose() {
        if (disposed) return; disposed = true; stopping = true; devicesChanged.Set();
        if (listener != null) { try { devices.UnregisterEndpointNotificationCallback(listener); } catch (Exception error) { MeetingFiles.Log(error); } }
        if (deviceThread != null && deviceThread != Thread.CurrentThread) deviceThread.Join(6000);
        foreach (Source source in Snapshot()) if (source.Track != null) try { source.Track.Dispose(); } catch (Exception error) { MeetingFiles.Log(error); }
        devices.Dispose(); devicesChanged.Dispose();
    }
}

internal static class RecordingProtection {
    static double Seconds(object value) {
        if (!(value is double || value is int || value is long || value is decimal)) throw new IOException("录音保护时长数据无效，原始资料已保留。");
        double seconds = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) throw new IOException("录音保护时长数据无效，原始资料已保留。");
        return seconds;
    }
    public static bool ShouldDiscard(Dictionary<string, object> metadata, int protectionSeconds) {
        if (protectionSeconds < 0 || protectionSeconds > 300) throw new ArgumentOutOfRangeException("protectionSeconds");
        if (protectionSeconds == 0) return false;
        if (metadata == null) throw new IOException("缺少录音时长资料，原始资料已保留。");
        object duration;
        if (!metadata.TryGetValue("duration_seconds", out duration)) throw new IOException("缺少录音时长，原始资料已保留。");
        double total = Seconds(duration), paused = 0, previousEnd = 0;
        object pauses;
        if (metadata.TryGetValue("recording_pauses", out pauses)) {
            var ranges = pauses as System.Collections.IList;
            if (ranges == null) throw new IOException("整场暂停数据无效，原始资料已保留。");
            foreach (object item in ranges) {
                var range = item as Dictionary<string, object>;
                object first, last;
                if (range == null || !range.TryGetValue("start_seconds", out first) || !range.TryGetValue("end_seconds", out last))
                    throw new IOException("整场暂停区间无效，原始资料已保留。");
                double start = Seconds(first), end = Seconds(last);
                if (start < previousEnd || end < start || end > total) throw new IOException("整场暂停时间线无效，原始资料已保留。");
                paused += end - start; previousEnd = end;
            }
        }
        // Subtracting decimal pause boundaries can put exactly five seconds a
        // fraction of a nanosecond below five. Keep that boundary conservatively.
        return total - paused + 1e-9 < protectionSeconds;
    }
    public static void Discard(string session) {
        string work = Path.GetFullPath(Path.Combine(MeetingFiles.Home, "work"));
        session = Path.GetFullPath(session).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(session), work, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(session), @"\A[0-9]{8}-[0-9]{6}(?:-[0-9a-f]{8})?\z"))
            throw new IOException("短录音清理路径不在本程序的会议临时目录内。");
        MeetingConfig.CheckOrdinaryPath(session);
        string metadataPath = Path.Combine(session, "session.json");
        MeetingConfig.CheckOrdinaryPath(metadataPath);
        var metadata = MeetingFiles.ReadJson(metadataPath);
        string state = MeetingFiles.Text(metadata, "state", "");
        if ((state != "recorded" && state != "recorded_with_error") ||
            !string.Equals(MeetingFiles.Text(metadata, "session_dir", ""), session, StringComparison.OrdinalIgnoreCase))
            throw new IOException("短录音会话身份或状态不匹配，资料未清理。");
        if (Directory.GetDirectories(session).Length != 0) throw new IOException("短录音目录存在其他资料，未自动清理。");
        string[] files = Directory.GetFiles(session);
        foreach (string file in files) {
            MeetingConfig.CheckOrdinaryPath(file);
            string name = Path.GetFileName(file);
            if (name != "session.json" && name != "controller.json" &&
                !Regex.IsMatch(name, @"\A(?:mic|system)(?:_[0-9]{2,})?(?:\.seg[0-9]{6})?\.wav\z"))
                throw new IOException("短录音目录存在非录音临时文件，未自动清理。");
        }
        // Persist cancellation before removing files: interrupted cleanup must
        // never turn the remaining short capture into a retryable ASR job.
        metadata["state"] = "discarded";
        MeetingFiles.WriteJson(metadataPath, metadata);
        foreach (string file in files) if (!string.Equals(file, metadataPath, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        File.Delete(metadataPath);
        Directory.Delete(session, false);
    }
}

internal static class RecorderMotionPreferences {
    public static string Mode = "auto";
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);
    public static bool Enabled {
        get {
            if (Mode == "on") return true;
            if (Mode == "off") return false;
            int enabled;
            return !SystemInformation.HighContrast && (!SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled != 0);
        }
    }
}

internal sealed class RecorderActionButton : Button {
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch transitionClock = new Stopwatch();
    bool endsRecording, microphoneDisabled, recordingPaused, collapsesWindow;
    public bool ReturnsHome;
    public bool EndsRecording { get { return endsRecording; } set { if (endsRecording == value) return; endsRecording = value; UpdateVisualState(); } }
    public bool MicrophoneDisabled { get { return microphoneDisabled; } set { if (microphoneDisabled == value) return; microphoneDisabled = value; if (value) { inputLevel = 0; InputActive = false; } UpdateVisualState(); } }
    public bool RecordingPaused { get { return recordingPaused; } set { if (recordingPaused == value) return; recordingPaused = value; if (value) { inputLevel = 0; InputActive = false; } UpdateVisualState(); } }
    public bool CollapsesWindow { get { return collapsesWindow; } set { if (collapsesWindow == value) return; collapsesWindow = value; UpdateVisualState(); } }
    public bool InputActive;
    float inputLevel;
    bool hovering, pressed;
    Color visualFill, visualInk, visualBorder, startFill, startInk, startBorder, targetFill, targetInk, targetBorder;
    float visualInputLevel, startInputLevel, targetInputLevel;
    float visualScale = 1, startScale = 1, targetScale = 1;
    public RecorderActionButton() {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animation.Tick += delegate { AdvanceAnimation(); };
        UpdateVisualState();
    }
    protected override void OnMouseEnter(EventArgs e) { hovering = true; pressed = Capture && (MouseButtons & MouseButtons.Left) != 0; UpdateVisualState(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovering = pressed = false; UpdateVisualState(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; UpdateVisualState(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = false; UpdateVisualState(); } base.OnMouseUp(e); }
    protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture && pressed) { pressed = false; UpdateVisualState(); } base.OnMouseCaptureChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; UpdateVisualState(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = false; UpdateVisualState(); } base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; UpdateVisualState(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        if (!Enabled) { pressed = false; inputLevel = 0; InputActive = false; }
        UpdateVisualState();
        base.OnEnabledChanged(e);
    }
    public void SetInputLevel(float value) {
        inputLevel = !Enabled || MicrophoneDisabled || RecordingPaused ? 0 : Math.Max(0, Math.Min(1, value));
        InputActive = inputLevel > .003f;
        UpdateVisualState();
    }
    static Color Blend(Color from, Color to, double progress) {
        return Color.FromArgb((int)Math.Round(from.R + (to.R - from.R) * progress), (int)Math.Round(from.G + (to.G - from.G) * progress), (int)Math.Round(from.B + (to.B - from.B) * progress));
    }
    void UpdateVisualState() {
        if (animation == null || IsDisposed) return;
        if (animation.Enabled) AdvanceAnimation();
        bool unavailable = !Enabled || (RecordingPaused && !EndsRecording);
        Color fill = unavailable ? Color.FromArgb(239, 241, 244) : EndsRecording
            ? (pressed ? Color.FromArgb(171, 34, 45) : hovering ? Color.FromArgb(191, 42, 54) : Color.FromArgb(216, 57, 69))
            : MicrophoneDisabled ? (pressed ? Color.FromArgb(240, 224, 200) : hovering ? Color.FromArgb(247, 235, 215) : Color.FromArgb(250, 243, 232))
            : pressed ? Color.FromArgb(205, 226, 253) : (hovering || InputActive ? Color.FromArgb(221, 236, 254) : Color.FromArgb(237, 245, 255));
        Color ink = unavailable ? Color.FromArgb(158, 165, 177) : EndsRecording ? Color.FromArgb(132, 25, 34) : MicrophoneDisabled ? Color.FromArgb(139, 90, 31) : InputActive ? Color.FromArgb(31, 126, 220) : Color.FromArgb(40, 104, 168);
        if (CollapsesWindow || ReturnsHome) ink = !Enabled ? SystemColors.GrayText : pressed ? Color.FromArgb(31, 78, 126) : hovering ? Color.FromArgb(40, 104, 168) : Color.FromArgb(100, 116, 135);
        Color border = unavailable ? Color.FromArgb(225, 230, 237) : MicrophoneDisabled ? Color.FromArgb(233, 210, 175) : InputActive ? Color.FromArgb(78, 159, 235) : Color.FromArgb(188, 215, 243);
        float nextInput = InputActive && !unavailable && !MicrophoneDisabled ? (float)Math.Max(0, Math.Min(1, (Math.Log10(inputLevel) + 2.7) / 2.2)) : 0;
        float nextScale = pressed && Enabled ? .95f : 1;
        if (fill == targetFill && ink == targetInk && border == targetBorder && Math.Abs(nextInput - targetInputLevel) < .001f && nextScale == targetScale) return;
        startFill = visualFill; startInk = visualInk; startBorder = visualBorder;
        startInputLevel = visualInputLevel; startScale = visualScale;
        targetFill = fill; targetInk = ink; targetBorder = border;
        targetInputLevel = nextInput; targetScale = nextScale;
        if (!IsHandleCreated || !Visible || !RecorderMotionPreferences.Enabled) { CompleteAnimation(); return; }
        transitionClock.Restart();
        animation.Start();
    }
    void AdvanceAnimation() {
        if (!Visible || !IsHandleCreated || !RecorderMotionPreferences.Enabled || transitionClock.ElapsedMilliseconds >= 150) { CompleteAnimation(); return; }
        double remaining = 1 - transitionClock.Elapsed.TotalMilliseconds / 150.0;
        double progress = 1 - remaining * remaining * remaining;
        visualFill = Blend(startFill, targetFill, progress);
        visualInk = Blend(startInk, targetInk, progress);
        visualBorder = Blend(startBorder, targetBorder, progress);
        visualInputLevel = startInputLevel + (targetInputLevel - startInputLevel) * (float)progress;
        visualScale = startScale + (targetScale - startScale) * (float)progress;
        Invalidate(ClientRectangle);
    }
    void CompleteAnimation() {
        animation.Stop();
        transitionClock.Stop();
        visualFill = targetFill; visualInk = targetInk; visualBorder = targetBorder;
        visualInputLevel = targetInputLevel; visualScale = targetScale;
        Invalidate(ClientRectangle);
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); CompleteAnimation(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CompleteAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e) { animation.Stop(); transitionClock.Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing) {
        if (disposing) { animation.Stop(); animation.Dispose(); transitionClock.Stop(); }
        base.Dispose(disposing);
    }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        if (CollapsesWindow || ReturnsHome) {
            float scale = Height / 20f * visualScale;
            float x = Width / 2f, y = Height / 2f;
            using (var pen = new Pen(visualInk, 1.6f * scale)) {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (ReturnsHome) {
                    g.DrawLines(pen, new PointF[] { new PointF(x, y - 4 * scale), new PointF(x - 4 * scale, y), new PointF(x, y + 4 * scale) });
                    g.DrawLine(pen, x - 4 * scale, y, x + 4 * scale, y);
                } else {
                    g.DrawLine(pen, x - 3 * scale, y - 3 * scale, x + 3 * scale, y + 3 * scale);
                    g.DrawLine(pen, x - 3 * scale, y + 3 * scale, x + 3 * scale, y - 3 * scale);
                }
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(1, 1, Width - 2, Height - 2));
            return;
        }
        float unit = Math.Min(Width, Height) / 36f * visualScale;
        g.TranslateTransform((Width - 36 * unit) / 2, (Height - 36 * unit) / 2);
        g.ScaleTransform(unit, unit);
        Color fill = visualFill;
        Color ink = visualInk;
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
        if (!EndsRecording) using (var border = new Pen(visualBorder, 1.1f + .5f * visualInputLevel))
            g.DrawEllipse(border, 1.6f, 1.6f, 32.8f, 32.8f);
        if (!EndsRecording && visualInputLevel > .001f) {
            float height = 3 + 7 * visualInputLevel;
            using (var brush = new SolidBrush(Color.FromArgb((int)(180 * visualInputLevel), 139, 196, 248)))
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
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch frameClock = new Stopwatch();
    double level, targetLevel;
    bool recording;
    public RecordingLevelIndicator() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animation.Tick += delegate { AdvanceAnimation(); };
        TabStop = false;
        AccessibleName = Text = "录音输入：暂无明显声音";
    }
    public void SetRecording(bool value) {
        recording = value;
        if (!value) { level = targetLevel = 0; animation.Stop(); frameClock.Stop(); }
        AccessibleName = Text = value ? "录音输入：暂无明显声音" : "录音输入：整场已暂停";
        Invalidate(ClientRectangle);
    }
    public void SetLevels(float microphone, float system) {
        double peak = recording ? Math.Max(microphone, system) : 0;
        double next = peak <= .002 ? 0 : Math.Min(1, (Math.Log10(peak) + 2.7) / 2.2);
        targetLevel = next;
        AccessibleName = Text = !recording ? "录音输入：整场已暂停" : peak > .003 ? "录音输入：有声音" : "录音输入：暂无明显声音";
        if (!recording || !Visible || !IsHandleCreated || !RecorderMotionPreferences.Enabled || Math.Abs(targetLevel - level) < .002) { CompleteAnimation(); return; }
        if (!animation.Enabled) { frameClock.Restart(); animation.Start(); }
    }
    void AdvanceAnimation() {
        if (!recording || !Visible || !IsHandleCreated || !RecorderMotionPreferences.Enabled) { CompleteAnimation(); return; }
        double elapsed = Math.Min(.25, frameClock.Elapsed.TotalSeconds);
        frameClock.Restart();
        // Only interpolate measured samples. Silence has an exact zero target;
        // attack is quicker than release, without a synthetic ticking waveform.
        double seconds = targetLevel > level ? .065 : .15;
        level += (targetLevel - level) * (1 - Math.Exp(-elapsed / seconds));
        if (Math.Abs(targetLevel - level) < .002) { CompleteAnimation(); return; }
        Invalidate(ClientRectangle);
    }
    void CompleteAnimation() {
        animation.Stop(); frameClock.Stop();
        level = targetLevel;
        Invalidate(ClientRectangle);
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); CompleteAnimation(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CompleteAnimation(); }
    protected override void OnHandleDestroyed(EventArgs e) { animation.Stop(); frameClock.Stop(); base.OnHandleDestroyed(e); }
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
    protected override void Dispose(bool disposing) { if (disposing) { animation.Stop(); animation.Dispose(); frameClock.Stop(); } base.Dispose(disposing); }
}

internal sealed class RecorderPanel : Form {
    public readonly Label Heading = new Label();
    public readonly Label Time = new Label();
    public readonly Label Detail = new Label();
    public readonly LinkLabel AudioFile = new LinkLabel();
    public readonly LinkLabel TranscriptFile = new LinkLabel();
    public event Action<string, bool> ArtifactRequested;
    public readonly RecorderActionButton MicrophoneButton = new RecorderActionButton();
    public readonly RecorderActionButton StopButton = new RecorderActionButton { EndsRecording = true };
    public readonly RecorderActionButton CollapseButton = new RecorderActionButton { CollapsesWindow = true };
    public readonly RecorderActionButton BackButton = new RecorderActionButton { ReturnsHome = true };
    public readonly PictureBox BrandMark = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom };
    public readonly Label BrandTitle = new Label();
    public event Action BackRequested;
    public readonly RecordingLevelIndicator AudioLevel = new RecordingLevelIndicator();
    public readonly ToolTip DevicesTip = new ToolTip();
    Font timeNormal = DpiFont("Segoe UI Semibold", 11, 96);
    Font timeLong = DpiFont("Segoe UI Semibold", 8, 96);
    Font recordingHeadingFont = DpiFont("Microsoft YaHei UI", 7.5f, 96);
    Font processingHeadingFont = DpiFont("Microsoft YaHei UI", 9, 96);
    Font artifactFont = DpiFont("Microsoft YaHei UI", 8, 96);
    Font detailFont = DpiFont("Microsoft YaHei UI", 8, 96);
    Font brandFont = DpiFont("Segoe UI Semibold", 8, 96);
    int fontDpi = 96;
    readonly System.Windows.Forms.Timer processingAnimation = new System.Windows.Forms.Timer { Interval = 33 };
    readonly Stopwatch processingClock = new Stopwatch();
    bool processingBusy;
    string resultSession;
    public bool AllowClose;
    bool showingRecording;
    bool microphonePaused;
    bool recordingPaused;
    DateTime bookmarkFeedbackUntil;
    string bookmarkFeedback = "";
    string micName = "";
    string systemName = "";
    string deviceWarning = "";
    float microphoneLevel;
    float systemLevel;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public RecorderPanel() {
        SuspendLayout();
        Text = MeetingBrand.Name + " · 会议录音";
        Icon = MeetingBrand.IdleIcon;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.White;
        DoubleBuffered = true;
        ClientSize = new Size(186, 68);
        MinimumSize = MaximumSize = new Size(186, 68);
        StartPosition = FormStartPosition.Manual;
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(work.Right - Width - 24, work.Bottom - Height - 24);
        Time.SetBounds(10, 30, 55, 22);
        Time.Font = timeNormal;
        Time.ForeColor = Color.FromArgb(36, 59, 83);
        Time.TextAlign = ContentAlignment.MiddleLeft;
        Heading.SetBounds(10, 50, 74, 14);
        Heading.Font = recordingHeadingFont;
        Heading.ForeColor = Color.FromArgb(72, 103, 135);
        Heading.AutoEllipsis = true;
        Heading.TextAlign = ContentAlignment.MiddleCenter;
        Detail.Visible = false;
        Detail.Font = detailFont;
        Detail.ForeColor = Color.FromArgb(100, 116, 135);
        Detail.AutoEllipsis = true;
        Detail.TextAlign = ContentAlignment.MiddleCenter;
        foreach (var link in new LinkLabel[] { AudioFile, TranscriptFile }) {
            link.Font = artifactFont;
            link.Visible = false;
            link.AutoEllipsis = true;
            link.TextAlign = ContentAlignment.MiddleLeft;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.LinkColor = Color.FromArgb(42, 94, 150);
            link.ActiveLinkColor = Color.FromArgb(25, 63, 110);
            var copyMenu = new ContextMenuStrip();
            LinkLabel captured = link;
            copyMenu.Items.Add("复制完整路径", null, delegate {
                string path = captured.Tag as string;
                if (!string.IsNullOrEmpty(path)) try { Clipboard.SetText(path); }
                catch (Exception error) { MeetingFiles.Log(error); }
            });
            link.ContextMenuStrip = copyMenu;
        }
        AudioFile.LinkClicked += delegate { if (AudioFile.Enabled && ArtifactRequested != null && resultSession != null) ArtifactRequested(resultSession, true); };
        TranscriptFile.LinkClicked += delegate { if (TranscriptFile.Enabled && ArtifactRequested != null && resultSession != null) ArtifactRequested(resultSession, false); };
        processingAnimation.Tick += delegate {
            if (!processingBusy || !Visible || !RecorderMotionPreferences.Enabled) { UpdateProcessingAnimation(); return; }
            Invalidate();
        };
        AudioLevel.SetBounds(66, 32, 18, 18);
        MicrophoneButton.SetBounds(91, 27, 32, 32);
        StopButton.SetBounds(148, 29, 28, 28);
        BackButton.SetBounds(4, 2, 22, 20);
        CollapseButton.SetBounds(160, 2, 22, 20);
        BrandMark.SetBounds(63, 4, 16, 16);
        RefreshBrandBitmap();
        BrandMark.SizeChanged += delegate { RefreshBrandBitmap(); };
        BrandTitle.SetBounds(83, 3, 40, 18);
        BrandTitle.Font = brandFont;
        BrandTitle.ForeColor = Color.FromArgb(36, 59, 83);
        BrandTitle.TextAlign = ContentAlignment.MiddleLeft;
        BrandTitle.Text = MeetingBrand.Name;
        BrandTitle.AccessibleName = MeetingBrand.Name;
        BrandMark.AccessibleName = MeetingBrand.Name + " Logo";
        MicrophoneButton.AccessibleName = MicrophoneButton.Text = "关闭麦克风";
        StopButton.AccessibleName = StopButton.Text = "结束整个记录";
        CollapseButton.AccessibleName = CollapseButton.Text = "关闭浮窗";
        BackButton.AccessibleName = BackButton.Text = "返回首页";
        DevicesTip.SetToolTip(StopButton, "结束整个记录 · Ctrl+Alt+L");
        DevicesTip.SetToolTip(Time, "本场会议时长；拖动可移动浮窗");
        DevicesTip.SetToolTip(AudioLevel, "麦克风或电脑声音的实际输入音量");
        DevicesTip.SetToolTip(CollapseButton, "关闭浮窗，录音和转写继续；从托盘可恢复");
        DevicesTip.SetToolTip(BackButton, "返回首页，后台转写继续");
        Controls.AddRange(new Control[] { Time, AudioLevel, Heading, Detail, AudioFile, TranscriptFile, MicrophoneButton, StopButton, CollapseButton, BackButton, BrandMark, BrandTitle });
        CollapseButton.Click += delegate { Hide(); };
        BackButton.Click += delegate { if (BackRequested != null) BackRequested(); };
        MouseDown += Drag;
        Time.MouseDown += Drag;
        Heading.MouseDown += Drag;
        AudioLevel.MouseDown += Drag;
        BrandTitle.MouseDown += Drag;
        BrandMark.MouseDown += Drag;
        Heading.TextChanged += delegate { if (!showingRecording) DevicesTip.SetToolTip(Heading, Heading.Text); };
        Detail.TextChanged += delegate { if (!showingRecording) DevicesTip.SetToolTip(Heading, Heading.Text + "\n" + Detail.Text); };
        SetMicPaused(false);
        foreach (Control label in new Control[] { Time, Heading, Detail, AudioFile, TranscriptFile })
            label.DpiChangedAfterParent += delegate { RestoreDpiFonts(); };
        // Keep all design coordinates at 96 DPI until initialization is complete.
        // Setting the font earlier can otherwise reset this baseline to the desktop DPI.
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(false);
    }
    void RefreshBrandBitmap() {
        int pixels = Math.Max(1, Math.Min(BrandMark.Width, BrandMark.Height));
        Image previous = BrandMark.Image;
        BrandMark.Image = MeetingBrand.LogoBitmap(pixels);
        if (previous != null) previous.Dispose();
    }
    void LayoutBrandHeader() {
        float scale = DeviceDpi / 96f;
        int iconSize = (int)Math.Round(16 * scale);
        int titleWidth = (int)Math.Round(40 * scale);
        int gap = (int)Math.Round(4 * scale);
        int left = (ClientSize.Width - iconSize - gap - titleWidth) / 2;
        BrandMark.SetBounds(left, (int)Math.Round(4 * scale), iconSize, iconSize);
        BrandTitle.SetBounds(left + iconSize + gap, (int)Math.Round(3 * scale), titleWidth, (int)Math.Round(18 * scale));
    }
    static Font DpiFont(string family, float points, int dpi) {
        return new Font(family, points * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
    }
    void RestoreDpiFonts() {
        Font[] previous = null;
        if (fontDpi != DeviceDpi) {
            previous = new Font[] { timeNormal, timeLong, recordingHeadingFont, processingHeadingFont, artifactFont, detailFont, brandFont };
            fontDpi = DeviceDpi;
            timeNormal = DpiFont("Segoe UI Semibold", 11, fontDpi);
            timeLong = DpiFont("Segoe UI Semibold", 8, fontDpi);
            recordingHeadingFont = DpiFont("Microsoft YaHei UI", 7.5f, fontDpi);
            processingHeadingFont = DpiFont("Microsoft YaHei UI", 9, fontDpi);
            artifactFont = DpiFont("Microsoft YaHei UI", 8, fontDpi);
            detailFont = DpiFont("Microsoft YaHei UI", 8, fontDpi);
            brandFont = DpiFont("Segoe UI Semibold", 8, fontDpi);
        }
        // Pixel fonts avoid reusing a system-DPI GDI font at another monitor DPI.
        Time.Font = Time.Text.Length > 5 ? timeLong : timeNormal;
        Heading.Font = showingRecording ? recordingHeadingFont : processingHeadingFont;
        Detail.Font = detailFont;
        AudioFile.Font = TranscriptFile.Font = artifactFont;
        BrandTitle.Font = brandFont;
        LayoutBrandHeader();
        if (previous != null) foreach (Font font in previous) font.Dispose();
    }
    void Drag(object sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }
    public void ShowRecording(string microphone, string system) {
        SetProcessingBusy(false);
        HideArtifacts();
        showingRecording = true;
        BackButton.Enabled = false;
        bookmarkFeedbackUntil = DateTime.MinValue;
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
        Heading.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(52 * ClientSize.Height / 68f), (int)(74 * ClientSize.Width / 186f), (int)(13 * ClientSize.Height / 68f));
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
            UpdateRecordingHeading();
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
        if (showingRecording) UpdateRecordingHeading();
    }
    public void FlashBookmark(double seconds) {
        bookmarkFeedback = "已标记 " + TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        bookmarkFeedbackUntil = DateTime.UtcNow.AddSeconds(2);
        DevicesTip.SetToolTip(Time, "已标记重点：" + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") + "；结束后写入转录文件");
        UpdateRecordingHeading();
    }
    void UpdateRecordingHeading() {
        bool marked = DateTime.UtcNow < bookmarkFeedbackUntil;
        Heading.Visible = recordingPaused || marked;
        Heading.Text = recordingPaused ? "已暂停" : marked ? "已标记" : "";
        Heading.AccessibleName = recordingPaused ? "整场录音已暂停" : marked ? bookmarkFeedback : "";
        Heading.ForeColor = recordingPaused ? Color.FromArgb(139, 90, 31) : Color.FromArgb(42, 94, 150);
    }
    public void ShowStatus(string title, string detail) {
        ShowProcessing(title, detail);
    }
    public void ShowProcessing(string title, string detail) {
        SetProcessingBusy(false);
        HideArtifacts();
        showingRecording = false;
        BackButton.Enabled = true;
        AudioLevel.SetRecording(false);
        MicrophoneButton.SetInputLevel(0);
        Time.Visible = AudioLevel.Visible = MicrophoneButton.Visible = StopButton.Visible = false;
        Heading.Visible = true;
        Detail.Visible = true;
        Heading.Font = processingHeadingFont;
        Heading.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(25 * ClientSize.Height / 68f), (int)(166 * ClientSize.Width / 186f), (int)(18 * ClientSize.Height / 68f));
        Detail.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(45 * ClientSize.Height / 68f), (int)(166 * ClientSize.Width / 186f), (int)(16 * ClientSize.Height / 68f));
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
    public void ShowWorking(string title, string detail) {
        bool continuing = processingBusy;
        // Repeated status polling must not restart the animation every 500 ms.
        ShowProcessing(title, detail);
        if (!continuing) processingClock.Reset();
        SetProcessingBusy(true);
    }
    void HideArtifacts() {
        AudioFile.Visible = TranscriptFile.Visible = false;
        AudioFile.Tag = TranscriptFile.Tag = null;
        resultSession = null;
    }
    public void ShowResult(string session, string audio, string note, string root) {
        if (!showingRecording && !processingBusy && resultSession == session && (AudioFile.Tag as string) == audio && (TranscriptFile.Tag as string) == note) return;
        ShowProcessing("", "");
        Heading.Visible = Detail.Visible = false;
        resultSession = session;
        SetArtifact(AudioFile, "录音", audio, root, 25);
        SetArtifact(TranscriptFile, "转录", note, root, 45);
    }
    void SetArtifact(LinkLabel link, string kind, string path, string root, int top) {
        bool available = !string.IsNullOrEmpty(path);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string display = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
        link.SetBounds((int)(10 * ClientSize.Width / 186f), (int)(top * ClientSize.Height / 68f), (int)(166 * ClientSize.Width / 186f), (int)(18 * ClientSize.Height / 68f));
        link.Text = available ? CompactArtifactPath(kind + "：", display, link.Font, link.Width - 2) : kind + "：已移除";
        link.Tag = path;
        link.Enabled = available;
        link.AccessibleName = available ? kind + "文件：" + path + "；点击在文件夹中定位" : kind + "文件已移除";
        DevicesTip.SetToolTip(link, available ? kind + "：" + path + "\n点击在文件夹中定位；右键复制完整路径" : kind + "文件已移除");
        link.Visible = true;
    }
    static string CompactArtifactPath(string caption, string path, Font font, int width) {
        TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (TextRenderer.MeasureText(caption + path, font, new Size(int.MaxValue, int.MaxValue), flags).Width <= width) return caption + path;
        string filename = Path.GetFileName(path);
        string compact = caption + "…\\" + filename;
        if (TextRenderer.MeasureText(compact, font, new Size(int.MaxValue, int.MaxValue), flags).Width <= width) return compact;
        string extension = Path.GetExtension(filename);
        string stem = Path.GetFileNameWithoutExtension(filename);
        for (int keep = stem.Length - 1; keep > 0; keep--) {
            int head = (keep + 1) / 2, tail = keep / 2;
            compact = caption + stem.Substring(0, head) + "…" + stem.Substring(stem.Length - tail) + extension;
            if (TextRenderer.MeasureText(compact, font, new Size(int.MaxValue, int.MaxValue), flags).Width <= width) return compact;
        }
        return caption + "…" + extension;
    }
    void SetProcessingBusy(bool busy) {
        processingBusy = busy;
        UpdateProcessingAnimation();
    }
    void UpdateProcessingAnimation() {
        bool animate = processingBusy && Visible && RecorderMotionPreferences.Enabled;
        processingAnimation.Enabled = animate;
        if (animate) processingClock.Start(); else processingClock.Stop();
        Invalidate();
    }
    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        if (processingAnimation != null) UpdateProcessingAnimation();
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
        if (processingBusy) {
            float scaleX = ClientSize.Width / 186f, scaleY = ClientSize.Height / 68f;
            float left = 14 * scaleX, width = 158 * scaleX, y = 62 * scaleY;
            using (var track = new Pen(Color.FromArgb(232, 239, 247), 2 * scaleY)) g.DrawLine(track, left, y, left + width, y);
            float travel = width - 30 * scaleX;
            float phase = RecorderMotionPreferences.Enabled ? (float)(processingClock.Elapsed.TotalMilliseconds % 1500 / 1500) : .5f;
            // Indeterminate activity, never a fabricated percentage.
            float offset = (float)((1 - Math.Cos(phase * Math.PI * 2)) * .5) * travel;
            using (var ink = new Pen(Color.FromArgb(67, 128, 192), 2 * scaleY)) {
                ink.StartCap = ink.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(ink, left + offset, y, left + offset + 30 * scaleX, y);
            }
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(Math.Max(work.Left, work.Right - Width - 24), Math.Max(work.Top, work.Bottom - Height - 24));
        RestoreDpiFonts();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        // Re-align after Windows/WinForms finish rounding the scaled child bounds.
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { if (!IsDisposed) RestoreDpiFonts(); }));
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            processingAnimation.Dispose(); processingClock.Stop();
            foreach (var link in new LinkLabel[] { AudioFile, TranscriptFile }) if (link.ContextMenuStrip != null) link.ContextMenuStrip.Dispose();
            if (BrandMark.Image != null) BrandMark.Image.Dispose();
            DevicesTip.Dispose(); timeNormal.Dispose(); timeLong.Dispose(); recordingHeadingFont.Dispose(); processingHeadingFont.Dispose(); artifactFont.Dispose(); detailFont.Dispose(); brandFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class MeetingHotkey : NativeWindow, IDisposable {
    const int Id = 7411;
    const int PauseId = 7412;
    const int BookmarkId = 7413;
    const int QuickNoteId = 7414;
    string startText;
    string pauseText;
    string bookmarkText;
    string quickNoteText;
    bool startRegistered;
    bool pauseRegistered;
    bool bookmarkRegistered;
    bool quickNoteRegistered;
    string quickNoteWarning = "";
    string auxiliaryWarning = "";
    public bool PauseRegistered { get { return pauseRegistered; } }
    public bool BookmarkRegistered { get { return bookmarkRegistered; } }
    public string AuxiliaryWarning { get { return auxiliaryWarning; } }
    public bool QuickNoteRegistered { get { return quickNoteRegistered; } }
    public string QuickNoteWarning { get { return quickNoteWarning; } }
    public event Action Pressed;
    public event Action PausePressed;
    public event Action BookmarkPressed;
    public event Action QuickNotePressed;
    public event Action ExitRequested;
    public event Action ShowRequested;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    public MeetingHotkey(string text, string pause) : this(text, pause, "Ctrl+Alt+K") { }
    public MeetingHotkey(string text, string pause, string bookmark) : this(text, pause, bookmark, null) { }
    public MeetingHotkey(string text, string pause, string bookmark, string quickNote) {
        startText = text;
        pauseText = pause;
        bookmarkText = bookmark;
        quickNoteText = quickNote;
        uint modifiers, key;
        MeetingConfig.ParseHotkey(text, out modifiers, out key);
        CreateHandle(new CreateParams { Caption = "ObsidianMeetingHotkey", Parent = new IntPtr(-3) });
        if (!RegisterHotKey(Handle, Id, modifiers | 0x4000, key)) {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new InvalidOperationException(RegistrationError(text, "hotkey", error));
        }
        startRegistered = true;
        try {
            if (!string.IsNullOrEmpty(quickNote))
                quickNoteRegistered = TryRegisterAuxiliary(quickNote, QuickNoteId, "quick_note_hotkey", out quickNoteWarning);
        } catch {
            UnregisterHotKey(Handle, Id); DestroyHandle(); throw;
        }
    }
    static string RegistrationError(string text, string setting, int error) {
        string reason = error == 1409 ? "已被其他程序占用" : "注册失败";
        return text + " " + reason + "（Win32 " + error + "），请检查 config.json 的 " + setting + "。";
    }
    bool TryRegisterAuxiliary(string text, int id, string setting, out string warning) {
        uint modifiers, key;
        MeetingConfig.ParseHotkey(text, out modifiers, out key);
        if (RegisterHotKey(Handle, id, modifiers | 0x4000, key)) { warning = ""; return true; }
        warning = RegistrationError(text, setting, Marshal.GetLastWin32Error());
        return false;
    }
    void ReleaseBindings() {
        EnablePause(false);
        if (quickNoteRegistered) UnregisterHotKey(Handle, QuickNoteId);
        if (startRegistered) UnregisterHotKey(Handle, Id);
        quickNoteRegistered = startRegistered = false;
    }
    void BindIdle(string start, string pause, string bookmark, string quick, bool requireQuick, bool probe) {
        string warning;
        startRegistered = TryRegisterAuxiliary(start, Id, "hotkey", out warning);
        if (!startRegistered) throw new InvalidOperationException(warning);
        quickNoteWarning = "";
        if (!string.IsNullOrEmpty(quick)) {
            quickNoteRegistered = TryRegisterAuxiliary(quick, QuickNoteId, "quick_note_hotkey", out quickNoteWarning);
            if (!quickNoteRegistered && requireQuick) throw new InvalidOperationException(quickNoteWarning);
        }
        if (probe) {
            pauseRegistered = TryRegisterAuxiliary(pause, PauseId, "pause_hotkey", out warning);
            if (!pauseRegistered) throw new InvalidOperationException(warning);
            bookmarkRegistered = TryRegisterAuxiliary(bookmark, BookmarkId, "bookmark_hotkey", out warning);
            if (!bookmarkRegistered) throw new InvalidOperationException(warning);
            EnablePause(false);
        }
    }
    public void Reconfigure(string start, string pause, string bookmark, string quick) {
        if (pauseRegistered || bookmarkRegistered) throw new InvalidOperationException("请结束录音后再修改快捷键。");
        var unique = new HashSet<string>();
        foreach (string text in new string[] { start, pause, bookmark, quick }) {
            uint modifiers, key; MeetingConfig.ParseHotkey(text, out modifiers, out key);
            if (!unique.Add(modifiers + ":" + key)) throw new InvalidOperationException("四个快捷键必须各不相同。");
        }
        string previousStart = startText, previousPause = pauseText, previousBookmark = bookmarkText, previousQuick = quickNoteText;
        bool previousQuickRequired = quickNoteRegistered;
        ReleaseBindings();
        try { BindIdle(start, pause, bookmark, quick, true, true); }
        catch (Exception changeError) {
            ReleaseBindings();
            try { BindIdle(previousStart, previousPause, previousBookmark, previousQuick, previousQuickRequired, false); }
            catch (Exception restoreError) { throw new InvalidOperationException(changeError.Message + " 原快捷键恢复失败，请从托盘正常退出后重启：" + restoreError.Message); }
            throw;
        }
        startText = start; pauseText = pause; bookmarkText = bookmark; quickNoteText = quick;
    }
    internal void RestoreIdleConfiguration(Dictionary<string, object> previous) {
        ReleaseBindings();
        startText = (string)previous["hotkey"]; pauseText = (string)previous["pause_hotkey"];
        bookmarkText = (string)previous["bookmark_hotkey"]; quickNoteText = (string)previous["quick_note_hotkey"];
        BindIdle(startText, pauseText, bookmarkText, quickNoteText, false, false);
    }
    public void EnablePause(bool enabled) {
        if (!enabled) {
            if (pauseRegistered) UnregisterHotKey(Handle, PauseId);
            if (bookmarkRegistered) UnregisterHotKey(Handle, BookmarkId);
            pauseRegistered = bookmarkRegistered = false;
            auxiliaryWarning = "";
            return;
        }
        var warnings = new List<string>();
        string warning;
        if (!pauseRegistered) {
            pauseRegistered = TryRegisterAuxiliary(pauseText, PauseId, "pause_hotkey", out warning);
            if (!pauseRegistered) warnings.Add("麦克风暂停快捷键 " + warning);
        }
        if (!bookmarkRegistered) {
            bookmarkRegistered = TryRegisterAuxiliary(bookmarkText, BookmarkId, "bookmark_hotkey", out warning);
            if (!bookmarkRegistered) warnings.Add("重点快捷键 " + warning);
        }
        auxiliaryWarning = string.Join("；", warnings);
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == Id && Pressed != null) Pressed();
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == PauseId && PausePressed != null) PausePressed();
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == BookmarkId && BookmarkPressed != null) BookmarkPressed();
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == QuickNoteId && QuickNotePressed != null) QuickNotePressed();
        if (message.Msg == 0x0010 && ExitRequested != null) ExitRequested();
        if (message.Msg == DesktopServices.ShowMessage && ShowRequested != null) ShowRequested();
        base.WndProc(ref message);
    }
    public void Dispose() {
        ReleaseBindings(); DestroyHandle();
    }
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
    readonly ToolStripMenuItem pauseMicrophone = new ToolStripMenuItem("暂停麦克风");
    readonly ToolStripMenuItem summary = new ToolStripMenuItem("verbit已就绪") { Enabled = false };
    readonly ToolStripMenuItem recent = new ToolStripMenuItem("最近会议");
    readonly ToolStripMenuItem bookmark = new ToolStripMenuItem("标记重点") { Name = "bookmark", Enabled = false };
    readonly ToolStripMenuItem rename = new ToolStripMenuItem("命名这场会议…") { Name = "rename", Enabled = false };
    readonly SilenceMonitor silence = new SilenceMonitor();
    SessionCatalog catalog;
    MeetingSettingsDialog settingsDialog;
    DateTime lastUpdateCheck = DateTime.MinValue;
    bool checkingUpdate;
    readonly Func<string, Process> workerLauncher;
    readonly Action<string, bool> noteOpener;
    Action<string> artifactLocator = null;
    RecordingSession recording;
    QuickNoteWindow quickNote;
    string lastSession;
    string previousRecordingSession;
    string focusedSession;
    SessionRecord focusedRecord;
    bool exiting;
    bool preserveShortRecording;
    int deviceCheckTicks;
    string StartHotkey { get { return (string)config["hotkey"]; } }
    string PauseHotkey { get { return (string)config["pause_hotkey"]; } }
    string BookmarkHotkey { get { return (string)config["bookmark_hotkey"]; } }
    string QuickNoteHotkey { get { return (string)config["quick_note_hotkey"]; } }

    public MeetingContext() : this(MeetingConfig.Load(false), null, null) { }
    // The two optional operations also allow isolated native regressions without
    // capturing audio, loading a model, or opening the user's applications.
    internal MeetingContext(Dictionary<string, object> settings, Func<string, Process> launcher, Action<string, bool> opener) {
        config = MeetingConfig.Validate(settings, false);
        RecorderMotionPreferences.Mode = (string)config["animation_mode"];
        workerLauncher = launcher;
        noteOpener = opener;
        catalog = new SessionCatalog(MeetingFiles.Home, MeetingFiles.VaultRoot(config));
        IntPtr dispatchHandle = dispatch.Handle;
        hotkey = new MeetingHotkey(StartHotkey, PauseHotkey, BookmarkHotkey, QuickNoteHotkey);
        hotkey.Pressed += Toggle;
        hotkey.PausePressed += ToggleMicrophone;
        hotkey.BookmarkPressed += MarkBookmark;
        hotkey.QuickNotePressed += ToggleQuickNote;
        hotkey.ExitRequested += RequestExit;
        hotkey.ShowRequested += ShowPanel;
        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9), RenderMode = ToolStripRenderMode.System };
        panel.ContextMenuStrip = menu;
        menu.Items.Add(summary);
        menu.Items.Add(new ToolStripSeparator());
        var quickNoteItem = new ToolStripMenuItem("速记便签 · " + QuickNoteHotkey) { Name = "quickNote" };
        quickNoteItem.Click += delegate { ToggleQuickNote(); };
        menu.Items.Add(quickNoteItem);
        menu.Items.Add(new ToolStripSeparator());
        toggle.Click += delegate { Toggle(); };
        toggle.Text = "开始录音 · " + StartHotkey;
        menu.Items.Add(toggle);
        pauseMicrophone.Text = "暂停麦克风 · " + PauseHotkey;
        pauseMicrophone.Click += delegate { ToggleMicrophone(); };
        pauseMicrophone.Enabled = false;
        menu.Items.Add(pauseMicrophone);
        bookmark.Text = "标记重点 · " + BookmarkHotkey;
        bookmark.Click += delegate { MarkBookmark(); };
        menu.Items.Add(bookmark);
        rename.Click += delegate { PromptRename(focusedSession); };
        menu.Items.Add(rename);
        menu.Items.Add("显示浮窗", null, delegate { RefreshFeedback(); panel.Show(); panel.Activate(); });
        menu.Items.Add("收起浮窗", null, delegate { panel.Hide(); });
        // WinForms does not expose an expansion affordance for an empty child
        // menu. Populate it before the parent opens, then refresh on expansion.
        menu.Opening += delegate { RefreshFeedback(); RefreshRecentMenu(); };
        recent.DropDownOpening += delegate { RefreshRecentMenu(); };
        menu.Items.Add(recent);
        menu.Items.Add("打开会议记录文件夹", null, delegate {
            string path = Path.Combine(MeetingFiles.VaultRoot(config), MeetingFiles.Text(config, "meeting_folder", "会议记录"));
            MeetingConfig.CheckOrdinaryPath(path);
            Directory.CreateDirectory(path);
            OpenFolder(path);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, delegate { OpenSettings(); });
        menu.Items.Add("首次使用检查…", null, delegate { OpenFirstUseGuide(); });
        menu.Items.Add("关于 verbit · " + DesktopServices.Version, null, delegate { OpenSettings(true); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { RequestExit(); });
        tray = new NotifyIcon { Icon = MeetingBrand.IdleIcon, Text = "verbit · " + StartHotkey, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { RefreshFeedback(); panel.Show(); panel.Activate(); };
        panel.StopButton.Click += delegate { Stop(); };
        panel.MicrophoneButton.Click += delegate { ToggleMicrophone(); };
        panel.ArtifactRequested += LocateArtifactForSession;
        panel.BackRequested += delegate { focusedSession = null; focusedRecord = null; RefreshFeedback(); };
        panel.Time.Text = "00:00";
        panel.DevicesTip.SetToolTip(panel.StopButton, "结束整个记录 · " + StartHotkey);
        panel.ShowStatus("verbit已就绪", StartHotkey + " 开始录制");
        timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += delegate { Tick(); MaybeCheckUpdates(); };
        RestoreSessions();
        timer.Start();
        if (hotkey.QuickNoteWarning.Length != 0)
            dispatch.BeginInvoke(new Action(delegate { Notice("速记快捷键不可用", hotkey.QuickNoteWarning + " 可从托盘打开速记便签。", ToolTipIcon.Warning); }));
    }
    public void ShowPanel() { RefreshFeedback(); panel.Show(); panel.Activate(); }
    async void MaybeCheckUpdates() {
        if (exiting || checkingUpdate || !Convert.ToBoolean(config["check_updates"]) || DateTime.UtcNow - lastUpdateCheck < TimeSpan.FromHours(24)) return;
        lastUpdateCheck = DateTime.UtcNow; checkingUpdate = true;
        try {
            string message = await System.Threading.Tasks.Task.Run(delegate { return DesktopServices.CheckUpdates(); });
            if (!exiting && message.StartsWith("发现新版本", StringComparison.Ordinal)) Notice("verbit 有更新", message, ToolTipIcon.Info);
        } catch { /* Optional background checks never interrupt a meeting. Manual checks show errors. */ }
        finally { checkingUpdate = false; }
    }
    void OpenFirstUseGuide() {
        try {
            string guide = Path.Combine(MeetingFiles.Home, "documentation", "first-use.md");
            if (!File.Exists(guide)) throw new IOException("缺少首次使用说明，请保留完整程序目录。");
            DesktopServices.Open(guide);
        } catch (Exception error) { Notice("无法打开检查说明", error.Message, ToolTipIcon.Warning); }
    }
    void OpenSettings(bool about = false) {
        if (settingsDialog != null && !settingsDialog.IsDisposed) { if (about) settingsDialog.ShowAbout(); settingsDialog.Activate(); return; }
        if (recording != null || jobs.Count > 0) { Notice("请稍后修改设置", "请先结束录音并等待后台转写完成。", ToolTipIcon.Info); return; }
        try {
            var snapshot = MeetingSettingsSnapshot.Open(Path.Combine(MeetingFiles.Home, "config.json"));
            settingsDialog = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> next) { return ApplySettings(snapshot, next); });
            settingsDialog.FormClosed += delegate { settingsDialog = null; };
            if (about) settingsDialog.ShowAbout();
            settingsDialog.Show();
        } catch (Exception error) { Notice("无法打开设置", error.Message, ToolTipIcon.Warning); }
    }
    string ApplySettings(MeetingSettingsSnapshot snapshot, Dictionary<string, object> next) {
        if (recording != null || jobs.Count > 0 || exiting) return "请结束录音并等待后台转写完成后再保存。";
        if (quickNote != null && !quickNote.IsDisposed && !quickNote.FlushAndClose())
            return "速记正在保存，或输入法尚未完成。请确认文字已保存后再次点击保存。";
        var previous = new Dictionary<string, object>(config);
        bool rebound = false;
        try {
            hotkey.Reconfigure((string)next["hotkey"], (string)next["pause_hotkey"], (string)next["bookmark_hotkey"], (string)next["quick_note_hotkey"]);
            rebound = true;
            snapshot.Commit(next);
        } catch (Exception error) {
            if (rebound) try { hotkey.RestoreIdleConfiguration(previous); }
            catch (Exception restoreError) { return error.Message + " 原快捷键未能恢复，请从托盘退出后重启：" + restoreError.Message; }
            return error.Message;
        }
        config.Clear(); foreach (var item in next) config[item.Key] = item.Value;
        catalog = new SessionCatalog(MeetingFiles.Home, MeetingFiles.VaultRoot(config));
        focusedSession = null; focusedRecord = null;
        toggle.Text = "开始录音 · " + StartHotkey;
        pauseMicrophone.Text = "暂停麦克风 · " + PauseHotkey;
        bookmark.Text = "标记重点 · " + BookmarkHotkey;
        var quickItem = tray.ContextMenuStrip.Items["quickNote"];
        if (quickItem != null) quickItem.Text = "速记便签 · " + QuickNoteHotkey;
        panel.DevicesTip.SetToolTip(panel.StopButton, "结束整个记录 · " + StartHotkey);
        RefreshFeedback();
        Notice("设置已生效", "尚未开始转写的新录音重试时使用当前模型设置。旧文件保留在原位置。", ToolTipIcon.Info);
        return null;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    void ToggleQuickNote() {
        if (settingsDialog != null && !settingsDialog.IsDisposed) { settingsDialog.Activate(); return; }
        try {
            if (quickNote == null || quickNote.IsDisposed) {
                string stateRoot = Path.Combine(MeetingFiles.Home, "work", "quick-note");
                var store = new QuickNoteStore(MeetingFiles.VaultRoot(config), (string)config["quick_note_folder"], stateRoot);
                quickNote = new QuickNoteWindow(store, stateRoot);
            }
            quickNote.Toggle();
        } catch (Exception error) { MeetingFiles.Log(error); Notice("无法打开速记便签", error.Message, ToolTipIcon.Warning); }
    }
    void Notice(string title, string message, ToolTipIcon icon) { tray.ShowBalloonTip(5000, "verbit · " + title, message, icon); }
    void OnUi(Action action) { if (!dispatch.IsDisposed) try { dispatch.BeginInvoke(action); } catch (InvalidOperationException) { } }
    void Toggle() {
        if (exiting) return;
        if (settingsDialog != null && !settingsDialog.IsDisposed) { settingsDialog.Activate(); return; }
        if (recording == null) Start(); else Stop();
    }
    void Start() {
        try {
            // Native capture has no Python/model dependency. Validate paths and
            // shortcuts here; credentials and models belong to transcription.
            MeetingConfig.Validate(new Dictionary<string, object>(config), false);
            preserveShortRecording = false;
            hotkey.EnablePause(true);
            RecordingSession startedSession = null;
            startedSession = new RecordingSession(config, delegate(Exception error) {
                OnUi(delegate {
                    if (recording == null || !object.ReferenceEquals(recording, startedSession)) return;
                    preserveShortRecording = true;
                    panel.SetDeviceWarning("录音异常，正在保存已录内容");
                    Notice("录音异常", error.Message + " 已录内容会保留。", ToolTipIcon.Warning); Stop();
                });
            });
            recording = startedSession;
            catalog.SaveWorker(recording.DirectoryPath, null);
            previousRecordingSession = lastSession;
            SaveLastSession(recording.DirectoryPath, null);
            focusedSession = recording.DirectoryPath;
            focusedRecord = null;
            toggle.Text = "结束录音 · " + StartHotkey;
            pauseMicrophone.Text = "暂停麦克风 · " + PauseHotkey;
            pauseMicrophone.Enabled = true;
            deviceCheckTicks = 0;
            silence.Reset();
            bookmark.Enabled = true;
            panel.ShowRecording(recording.MicrophoneName, recording.SystemDeviceName);
            panel.Show();
            tray.Icon = MeetingBrand.RecordingIcon;
            RefreshFeedback();
            string startMessage = "正在录制麦克风和电脑声音；" + StartHotkey + " 结束。";
            if (hotkey.AuxiliaryWarning.Length == 0) startMessage += PauseHotkey + " 暂停/继续麦克风，电脑声音持续录制。";
            else startMessage += hotkey.AuxiliaryWarning + " 仍可从浮窗或托盘右键菜单暂停麦克风、标记重点；电脑声音持续录制。";
            Notice("会议录音已开始", startMessage, hotkey.AuxiliaryWarning.Length == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning);
        } catch (Exception error) {
            hotkey.EnablePause(false);
            MeetingFiles.Log(error);
            // If persistence failed after capture started, close the capture
            // through the ordinary save path rather than orphaning a recorder.
            if (recording != null) { preserveShortRecording = true; Stop(); return; }
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
    void MarkBookmark() {
        if (recording == null || recording.Stopping) return;
        try { panel.FlashBookmark(recording.AddBookmark()); }
        catch (Exception error) { MeetingFiles.Log(error); Notice("重点标记未保存", error.Message, ToolTipIcon.Warning); }
    }
    void PromptRename(string session) {
        if (recording != null || string.IsNullOrEmpty(session)) return;
        try {
            var record = catalog.Get(session);
            if (record == null || record.State != "completed" || record.WorkerAlive) return;
            var data = MeetingFiles.ReadJson(Path.Combine(session, "session.json"));
            using (var dialog = new MeetingNameDialog(MeetingFiles.Text(data, "title", ""))) {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                MeetingRename.Apply(catalog, session, dialog.MeetingTitle);
            }
            focusedSession = session;
            RefreshFeedback();
        } catch (Exception error) { MeetingFiles.Log(error); Notice("无法命名这场会议", error.Message, ToolTipIcon.Warning); }
    }
    void UpdateRecordingState() {
        panel.SetMicPaused(recording.MicrophonePaused);
        panel.SetRecordingPaused(recording.RecordingPaused);
        pauseMicrophone.Text = (recording.MicrophonePaused ? "继续麦克风 · " : "暂停麦克风 · ") + PauseHotkey;
        RefreshFeedback();
    }
    void Stop() {
        if (recording == null || recording.Stopping) return;
        RecordingSession ended = recording;
        hotkey.EnablePause(false);
        pauseMicrophone.Enabled = false;
        bookmark.Enabled = false;
        silence.Reset();
        panel.ShowStatus("正在结束录音", "正在关闭两路录音文件");
        int protectionSeconds = config.ContainsKey("recording_protection_seconds") ? Convert.ToInt32(config["recording_protection_seconds"]) : 5;
        bool preserveShort = preserveShortRecording;
        ended.StopAsync(delegate(Exception error) {
            bool discarded = false;
            if (error == null) try {
                bool shortRecording = RecordingProtection.ShouldDiscard(MeetingFiles.ReadJson(Path.Combine(ended.DirectoryPath, "session.json")), protectionSeconds);
                if (shortRecording && (preserveShort || ended.HasStorageFailure)) throw new IOException("保护时间内因录音异常停止，原始资料已保留供检查，未自动转录。");
                discarded = shortRecording;
                if (discarded) RecordingProtection.Discard(ended.DirectoryPath);
            } catch (Exception failure) { error = failure; MeetingFiles.Log(failure); }
            OnUi(delegate { CompleteStoppedRecording(ended.DirectoryPath, discarded, error); });
        });
    }
    void CompleteStoppedRecording(string session, bool discarded, Exception error) {
        recording = null;
        toggle.Text = "开始录音 · " + StartHotkey;
        tray.Icon = MeetingBrand.IdleIcon;
        focusedSession = session;
        if (discarded) {
            if (error == null) {
                focusedSession = null; focusedRecord = null;
                try {
                    if (string.Equals(lastSession, session, StringComparison.OrdinalIgnoreCase)) {
                        lastSession = null;
                        if (!string.IsNullOrEmpty(previousRecordingSession) && Directory.Exists(previousRecordingSession)) SaveLastSession(previousRecordingSession, null);
                        else {
                            string pointer = Path.Combine(MeetingFiles.Home, "last-session.json");
                            MeetingConfig.CheckOrdinaryPath(pointer);
                            if (File.Exists(pointer) && string.Equals(MeetingFiles.Text(MeetingFiles.ReadJson(pointer), "session_dir", ""), session, StringComparison.OrdinalIgnoreCase)) File.Delete(pointer);
                        }
                    }
                } catch (Exception failure) { MeetingFiles.Log(failure); }
                Notice("短录音已取消", "未达到保护时间，未保留录音或转录文件。", ToolTipIcon.Info);
            } else Notice("短录音未转录", "临时资料清理未完成：" + session + "；" + error.Message, ToolTipIcon.Warning);
        } else if (error != null) {
            RecordLaunchFailure(session, "录音保存异常，请检查原始资料：" + error.Message);
            Notice("录音保存异常", "已保留原始文件，请从最近会议检查这场资料。", ToolTipIcon.Error);
        } else LaunchWorker(session);
        RefreshFeedback();
        if (!discarded && (bool)config["show_processing_window"] && !exiting) panel.Show();
        else panel.Hide();
        if (exiting) ExitThread();
    }
    static string Quote(string text) { return "\"" + text.Replace("\"", "\\\"") + "\""; }
    static void OpenFolder(string path) {
        MeetingConfig.CheckOrdinaryPath(path);
        Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
    }
    void SaveLastSession(string session, Process process) {
        SaveLastSessionWithAttempt(session, process, DateTime.MinValue, DateTime.MinValue);
    }
    void SaveLastSessionWithAttempt(string session, Process process, DateTime launchUtc, DateTime previousStatusUtc) {
        // Retrying an older meeting must not move the latest-meeting pointer.
        if (!string.IsNullOrEmpty(lastSession) && string.Compare(Path.GetFileName(session), Path.GetFileName(lastSession), StringComparison.OrdinalIgnoreCase) < 0) return;
        lastSession = session;
        try {
            MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new {
                session_dir = session, worker_pid = process == null ? 0 : process.Id,
                worker_start_time_utc = process == null ? "" : process.StartTime.ToUniversalTime().ToString("o"),
                launch_time_utc = process == null ? "" : launchUtc == DateTime.MinValue ? process.StartTime.ToUniversalTime().ToString("o") : launchUtc.ToUniversalTime().ToString("o"),
                previous_status_write_time_utc = previousStatusUtc == DateTime.MinValue ? "" : previousStatusUtc.ToUniversalTime().ToString("o")
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
                    if (process != null) jobs.Add(new TranscriptionJob { Session = record.DirectoryPath, Process = process, LastMessage = record.Message,
                        WorkerStartedUtc = record.WorkerLaunchUtc, PreviousStatusWriteUtc = record.PreviousStatusWriteUtc });
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
    void RecordLaunchFailure(string session, string detail, bool configuration = false) {
        try {
            catalog.SaveWorker(session, null);
            var status = MeetingFiles.ReadJson(Path.Combine(session, "status.json"));
            status["state"] = "error"; status["stage"] = configuration ? "configuration" : "launch";
            status["message"] = configuration ? "录音已保存 · 待配置转写。" + detail : "转写未启动，原始资料已保留"; status["error"] = detail;
            status["updated_at"] = DateTimeOffset.Now.ToString("o");
            MeetingFiles.WriteJson(Path.Combine(session, "status.json"), status);
            if (recording == null) { focusedSession = session; focusedRecord = catalog.Get(session); }
        } catch (Exception error) { MeetingFiles.Log(error); }
    }
    void LaunchWorker(string session) {
        Process startedProcess = null;
        IDisposable setupGate = null;
        bool checkingConfiguration = true;
        try {
            var record = catalog.Get(session);
            if (record == null) throw new IOException("会议资料不在当前程序的 work 中。");
            if (ActiveJob(session)) return;
            if (!record.CanRetry) throw new IOException(record.Message);
            if (workerLauncher == null) {
                setupGate = DesktopServices.EnterModelSetupGate(MeetingFiles.Home);
                MeetingConfig.Validate(new Dictionary<string, object>(config));
                MeetingConfig.CheckTranscriptionRuntime(MeetingFiles.Home, config);
            }
            var metadata = MeetingFiles.ReadJson(Path.Combine(session, "session.json"));
            if (MeetingFiles.Text(metadata, "transcription_deferred", "").Equals("True", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(session, "result.json"))) {
                bool api = (string)config["transcription_mode"] == "api";
                if (api && MessageBox.Show("这场录音将发送到：" + config["api_base_url"] + "\n模型：" + config["api_model"] + "\n是否允许这次云端转写？取消后录音保留在本机。", MeetingBrand.Name,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) throw new IOException("已取消云端提交，录音留在本机。可在设置中选择本地模式后重试。");
                MeetingFiles.WriteJson(Path.Combine(session, "transcription-settings.json"), new {
                    transcription_mode = config["transcription_mode"],
                    transcription_model = api ? config["api_model"] : "Qwen3-ASR-1.7B",
                    transcription_api_base_url = api ? ((string)config["api_base_url"]).TrimEnd('/') : "",
                    api_upload_confirmed = api, asr_context = config["asr_context"], asr_terms = config["asr_terms"]
                });
            }
            checkingConfiguration = false;
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
                var start = new ProcessStartInfo {
                    FileName = python, Arguments = Quote(worker) + " --session " + Quote(session),
                    WorkingDirectory = MeetingFiles.Home, UseShellExecute = false, CreateNoWindow = true
                };
                if ((string)config["transcription_mode"] == "api") start.EnvironmentVariables[(string)config["api_key_env"]] = MeetingConfig.ApiKeyValue((string)config["api_key_env"]);
                process = Process.Start(start);
            }
            if (process == null) throw new IOException("未能启动转写进程。");
            startedProcess = process;
            jobs.Add(new TranscriptionJob { Session = session, Process = process, PreviousStatusWriteUtc = previousStatus, WorkerStartedUtc = attemptStarted });
            try { catalog.SaveWorker(session, process, attemptStarted, previousStatus); }
            catch (Exception persistenceError) {
                MeetingFiles.Log(persistenceError);
                // Legacy identity is a fallback if the per-meeting file suddenly
                // becomes unwritable. Do not overwrite the worker's status.
                try {
                    MeetingFiles.WriteJson(Path.Combine(MeetingFiles.Home, "last-session.json"), new {
                        session_dir = session, worker_pid = process.Id,
                        worker_start_time_utc = process.StartTime.ToUniversalTime().ToString("o"),
                        launch_time_utc = attemptStarted.ToString("o"), previous_status_write_time_utc = previousStatus.ToString("o")
                    });
                } catch (Exception fallbackError) { MeetingFiles.Log(fallbackError); }
                Notice("进度信息保存异常", "转写仍在进行，请等待这场完成后退出verbit；原始资料已保留。", ToolTipIcon.Warning);
            }
            SaveLastSessionWithAttempt(session, process, attemptStarted, previousStatus);
            if (recording == null) { focusedSession = session; focusedRecord = catalog.Get(session); }
            RefreshFeedback();
            Notice("录音已保存 · " + record.Label, ((string)config["transcription_mode"] == "api" ? "讲话片段将提交配置的 API。" : "后台本地转写中。") + "托盘 → 最近会议可查看这场进度。", ToolTipIcon.Info);
        } catch (Exception error) {
            MeetingFiles.Log(error);
            if (startedProcess != null) {
                Notice("转写状态需要检查", "后台处理已启动，原始资料已保留；请从最近会议查看对应场次。", ToolTipIcon.Warning);
                return;
            }
            RecordLaunchFailure(session, error.Message, checkingConfiguration);
            RefreshFeedback();
            Notice(checkingConfiguration ? "录音已保存 · 待配置转写" : "转写未启动", error.Message + " 请在最近会议中选择对应场次检查或重试。", ToolTipIcon.Warning);
        } finally { if (setupGate != null) setupGate.Dispose(); }
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
            float microphonePeak = recording.MicrophonePeak, systemPeak = recording.SystemPeak;
            panel.SetInputLevels(microphonePeak, systemPeak);
            if (silence.Observe(recording.Elapsed.TotalSeconds, microphonePeak, systemPeak, recording.MicrophonePaused,
                recording.RecordingPaused, Convert.ToInt32(config["silence_warning_seconds"])))
                Notice("持续未检测到声音", recording.MicrophonePaused ? "麦克风已关闭，电脑声音也持续无明显输入；如有需要，请检查播放设备。录音继续。" : "麦克风和电脑声音持续无明显输入；请检查静音、音量及设备选择。录音继续。", ToolTipIcon.Warning);
            if (++deviceCheckTicks >= 4) {
                deviceCheckTicks = 0;
                if ((bool)config["notify_device_changes"]) {
                    string warning = recording.CheckDefaultDeviceChanges();
                    if (!string.IsNullOrEmpty(warning)) {
                        panel.SetDeviceWarning(warning);
                        Notice("音频设备变化", warning, ToolTipIcon.Warning);
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
                if (fresh && job.Process.ExitCode == 0 && record != null &&
                    (record.State == "completed" || record.State == "capture_warning" || record.State == "transcript_only" || record.State == "audio_only" || record.State == "removed")) {
                    job.Reported = true;
                    MarkNotifiedSafely(record);
                    if (recording == null) { focusedSession = job.Session; focusedRecord = record; }
                    if (record.State == "completed")
                        Notice("会议文件 · " + record.Label, "录音：" + record.AudioPath + "\n转录：" + record.NotePath + "\n浮窗点击路径可定位文件。", ToolTipIcon.Info);
                    else if (record.State == "capture_warning")
                        Notice("采集异常 · " + record.Label, record.Message + "\n录音：" + record.AudioPath + "\n转录：" + record.NotePath, ToolTipIcon.Warning);
                    // Completing A must never take focus away from meeting B.
                    if (record.State != "capture_warning" && recording == null && File.Exists(record.NotePath) && (bool)config["auto_open_completed_note"] && (bool)config["open_in_obsidian"]) {
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
        bookmark.Enabled = recording != null && !recording.Stopping;
        rename.Enabled = recording == null && focusedRecord != null && focusedRecord.State == "completed" && !focusedRecord.WorkerAlive;
        if (recording != null) {
            string state = recording.Stopping ? "正在保存录音" : recording.RecordingPaused ? "整场已暂停" : recording.MicrophonePaused ? "麦克风已关闭 · 电脑声录制中" : "录音中";
            string text = "verbit · " + state + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
            summary.Text = text; SetTrayText(text);
            return;
        }
        if (focusedSession != null) {
            foreach (var job in jobs) {
                if (string.Equals(job.Session, focusedSession, StringComparison.OrdinalIgnoreCase) && !job.Reported) {
                    string title = string.IsNullOrEmpty(job.LastMessage) ? "准备转写" : job.LastMessage == "转写完成" ? "正在整理文件" : job.LastMessage;
                    string label = focusedRecord == null ? Path.GetFileName(focusedSession) : focusedRecord.Label;
                    panel.ShowWorking(title, label);
                    summary.Text = label + " · " + title + (jobs.Count > 1 ? " · 后台 " + jobs.Count + " 场" : "");
                    SetTrayText("verbit · " + summary.Text); return;
                }
            }
            {
                try { focusedRecord = catalog.Get(focusedSession); }
                catch (Exception error) {
                    MeetingFiles.Log(error);
                    panel.ShowProcessing("会议资料需要检查", "资料未修改，请检查 work 中对应场次");
                    summary.Text = "会议资料需要检查" + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
                    SetTrayText("verbit · " + summary.Text); return;
                }
            }
            if (focusedRecord != null && focusedRecord.State == "removed") {
                focusedRecord = null; focusedSession = null; rename.Enabled = false;
            }
            if (focusedRecord != null) {
                rename.Enabled = focusedRecord.State == "completed" && !focusedRecord.WorkerAlive;
                string title = StateLabel(focusedRecord);
                if (focusedRecord.State == "completed" || focusedRecord.State == "transcript_only" || focusedRecord.State == "audio_only")
                    panel.ShowResult(focusedRecord.DirectoryPath, focusedRecord.State == "transcript_only" ? "" : focusedRecord.AudioPath,
                        focusedRecord.State == "audio_only" ? "" : focusedRecord.NotePath, MeetingFiles.VaultRoot(config));
                else if (focusedRecord.WorkerAlive) panel.ShowWorking(title, focusedRecord.Label + " · " + focusedRecord.Message);
                else panel.ShowProcessing(title, focusedRecord.Label + " · " + focusedRecord.Message);
                summary.Text = focusedRecord.Label + " · " + title + (jobs.Count > 0 ? " · 后台 " + jobs.Count + " 场" : "");
                SetTrayText("verbit · " + summary.Text); return;
            }
        }
        panel.ShowStatus(jobs.Count > 0 ? "后台转写中" : "verbit已就绪", jobs.Count > 0 ? jobs.Count + " 场 · 可开始新会议" : StartHotkey + " 开始录制");
        panel.BackButton.Enabled = false;
        summary.Text = "verbit已就绪"; SetTrayText("verbit · " + StartHotkey);
    }
    static string StateLabel(string state) {
        switch (state) {
            case "completed": return "录音与转录已完成";
            case "transcript_only": return "仅保留转录";
            case "audio_only": return "仅保留录音";
            case "capture_warning": return "采集异常 · 检查录音";
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
        if (record.CanRetry && record.Message.StartsWith("录音已保存 · 待配置转写")) return "录音已保存 · 待配置转写";
        if (!record.CanRetry && (record.State == "failed" || record.State == "retry")) return "转写未完成 · 检查资料";
        return StateLabel(record.State);
    }
    void RefreshRecentMenu() {
        try {
            recent.DropDownItems.Clear();
            var records = catalog.Snapshot();
            // Keep the tray compact while retaining a route to every local folder.
            var displayed = new List<SessionRecord>();
            foreach (var record in records) if (record.State != "completed" && record.State != "transcript_only" && record.State != "audio_only" && !record.WorkerAlive && displayed.Count < 10) displayed.Add(record);
            foreach (var record in records) if (displayed.Count < 20 && !displayed.Contains(record)) displayed.Add(record);
            foreach (var record in displayed) {
                string session = record.DirectoryPath;
                bool current = recording != null && string.Equals(recording.DirectoryPath, session, StringComparison.OrdinalIgnoreCase);
                var item = new ToolStripMenuItem(record.Label + " · " + (current ? "正在录音" : StateLabel(record)));
                item.DropDownItems.Add("查看状态", null, delegate { FocusSession(session, true); });
                if (!string.IsNullOrEmpty(record.NotePath) && File.Exists(record.NotePath))
                    item.DropDownItems.Add("打开转录笔记", null, delegate { OpenNoteForSession(session); });
                if (!string.IsNullOrEmpty(record.AudioPath) && File.Exists(record.AudioPath))
                    item.DropDownItems.Add("定位录音文件", null, delegate { LocateArtifactForSession(session, true); });
                if (!string.IsNullOrEmpty(record.AudioPath) && File.Exists(record.AudioPath))
                    item.DropDownItems.Add("回听录音…", null, delegate {
                        try { MeetingPlayback.ShowForSession(catalog, session, panel); }
                        catch (Exception error) { Notice("无法回听", error.Message, ToolTipIcon.Warning); }
                    });
                if (!string.IsNullOrEmpty(record.NotePath) && File.Exists(record.NotePath))
                    item.DropDownItems.Add("定位转录文件", null, delegate { LocateArtifactForSession(session, false); });
                if (record.State == "completed" && !record.WorkerAlive && recording == null)
                    item.DropDownItems.Add("命名这场会议…", null, delegate { PromptRename(session); });
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
    void LocateArtifactForSession(string session, bool audio) {
        try {
            // Re-read this exact session at click time: never infer a file from
            // the latest-meeting pointer or the ellipsized display label.
            var record = catalog.Get(session);
            string path = record == null ? "" : audio ? record.AudioPath : record.NotePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) throw new IOException((audio ? "录音" : "转录") + "文件已移动或删除，请检查原保存位置。");
            MeetingConfig.CheckOrdinaryPath(path);
            if (artifactLocator != null) artifactLocator(path);
            else Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = true });
        } catch (Exception error) {
            MeetingFiles.Log(error);
            Notice("无法定位这场文件", error.Message, ToolTipIcon.Warning);
            if (recording == null && string.Equals(focusedSession, session, StringComparison.OrdinalIgnoreCase)) FocusSession(session, false);
        }
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
        if (quickNote != null && !quickNote.IsDisposed && !quickNote.FlushAndClose(RequestExit)) return;
        exiting = true;
        toggle.Enabled = false;
        if (recording != null) Stop(); else ExitThread();
    }
    protected override void ExitThreadCore() {
        if (quickNote != null && !quickNote.IsDisposed && !quickNote.FlushAndClose(ExitThread)) return;
        if (settingsDialog != null && !settingsDialog.IsDisposed) { settingsDialog.Close(); settingsDialog = null; }
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
            if (args.Length == 2 && args[0] == "--play-uri") {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return MeetingPlayback.RunUri(args[1]);
            }
            if (args.Length == 2 && args[0] == "--check-config") return CheckConfig(Path.GetFullPath(args[1]));
            if (args.Length == 2 && args[0] == "--diagnose") return Diagnose(Path.GetFullPath(args[1]));
            if (args.Length > 0 && args[0] == "--list-devices") return ListDevices();
            if (args.Length > 0 && args[0] == "--self-test-timeline") return SelfTestTimeline();
            bool show = args.Length == 1 && args[0] == "--show";
            if (args.Length > 0 && !show) return 2;
            bool first;
            using (var mutex = new Mutex(true, "Local\\ObsidianMeetingHotkey", out first)) {
                if (!first) { if (show) DesktopServices.ShowExisting(); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string suggestedRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!Directory.Exists(suggestedRoot)) suggestedRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!MeetingFirstRunSetup.Run(Path.Combine(MeetingFiles.Home, "config.json"), suggestedRoot)) return 0;
                using (var context = new MeetingContext()) { if (show) context.ShowPanel(); Application.Run(context); }
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
            MessageBox.Show(error.Message, "verbit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
