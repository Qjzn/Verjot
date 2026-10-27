using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

internal static class DesktopServices {
    internal static System.Drawing.Rectangle FitWindowBounds(System.Drawing.Rectangle bounds, System.Drawing.Rectangle work) {
        int width = Math.Min(bounds.Width, work.Width), height = Math.Min(bounds.Height, work.Height);
        return new System.Drawing.Rectangle(Math.Max(work.Left, Math.Min(bounds.Left, work.Right - width)),
            Math.Max(work.Top, Math.Min(bounds.Top, work.Bottom - height)), width, height);
    }
    internal static void KeepWindowOnScreen(System.Windows.Forms.Form window) {
        Action fit = delegate {
            if (!window.IsDisposed) window.Bounds = FitWindowBounds(window.Bounds, System.Windows.Forms.Screen.FromControl(window).WorkingArea);
        };
        window.Shown += delegate { fit(); };
        window.DpiChanged += delegate {
            if (window.IsHandleCreated && !window.IsDisposed) window.BeginInvoke(fit);
        };
    }
    public const string Repository = "https://github.com/Qjzn/verbit";
    public const int ShowMessage = 0x802a;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    public static void ShowExisting() {
        IntPtr window = FindWindowEx(new IntPtr(-3), IntPtr.Zero, null, "ObsidianMeetingHotkey");
        if (window != IntPtr.Zero) PostMessage(window, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }
    public static string StartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
    // Held until a transcription worker publishes its identity. Native capture
    // does not use Python or model files and may start during model setup.
    public static IDisposable EnterModelSetupGate(string home) { return new ModelSetupGate(home); }
    sealed class ModelSetupGate : IDisposable {
        readonly System.Collections.Generic.List<FileStream> handles = new System.Collections.Generic.List<FileStream>();
        public ModelSetupGate(string home) {
            try {
                foreach (string name in new[] { "recommended-model.lock", "runtime-install.lock" }) {
                    string path = Path.Combine(home, name); MeetingConfig.CheckOrdinaryPath(path);
                    handles.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
            } catch { Dispose(); throw new IOException("转写环境正在准备，请完成后重试转写；录音和速记可继续使用。"); }
        }
        public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    }
    public static string Version {
        get {
            var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            return attributes.Length == 0 ? "0.1.0-beta.11-dev" : ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
        }
    }
    static object Shortcut(string path, Action<object> use) {
        object shell = null, link = null;
        try {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            use(link); return null;
        } finally {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }
    static bool Owns(string path) {
        if (!File.Exists(path)) return false;
        bool owned = false;
        Shortcut(path, delegate(object link) {
            string target = (string)link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
            owned = !string.IsNullOrEmpty(target) && string.Equals(Path.GetFullPath(target), Executable, StringComparison.OrdinalIgnoreCase);
        });
        return owned;
    }
    static string[] StartupPaths { get { return new[] { Path.Combine(StartupFolder, "verbit installed.lnk"), Path.Combine(StartupFolder, "verbit.lnk") }; } }
    public static bool StartsWithWindows { get { foreach (string path in StartupPaths) if (Owns(path)) return true; return false; } }
    public static void SetStartup(bool enabled) {
        if (enabled) {
            if (StartsWithWindows) return;
            Directory.CreateDirectory(StartupFolder);
            string path = StartupPaths[0];
            if (File.Exists(path)) throw new IOException("开机启动入口属于另一份安装，请先移除旧版的自启设置。");
            Shortcut(path, delegate(object link) {
                Type type = link.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Executable });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(Executable) });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Executable + ",0" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            });
            if (!Owns(path)) throw new IOException("无法验证开机启动快捷方式。");
        } else foreach (string path in StartupPaths) if (Owns(path)) File.Delete(path);
    }
    public static void Open(string url) {
        if (Path.IsPathRooted(url) && string.Equals(Path.GetExtension(url), ".md", StringComparison.OrdinalIgnoreCase)) {
            var window = DocumentWindow(url);
            window.Show(System.Windows.Forms.Form.ActiveForm);
            var reader = (System.Windows.Forms.TextBox)window.Controls["document_text"];
            reader.Select(0, 0); reader.ScrollToCaret();
        } else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    public static System.Windows.Forms.Form DocumentWindow(string path) {
        MeetingConfig.CheckOrdinaryPath(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("说明文件过大，请检查程序目录。");
        string text = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
        var window = new System.Windows.Forms.Form { Text = "verbit · 离线说明", Icon = MeetingBrand.IdleIcon,
            AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi, AutoScaleDimensions = new System.Drawing.SizeF(96, 96),
            ClientSize = new System.Drawing.Size(560, 420), StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            Font = System.Drawing.SystemFonts.MessageBoxFont, MinimizeBox = false, TopMost = true };
        var reader = new System.Windows.Forms.TextBox { Name = "document_text", Text = text, ReadOnly = true, Multiline = true,
            Dock = System.Windows.Forms.DockStyle.Fill, ScrollBars = System.Windows.Forms.ScrollBars.Vertical, WordWrap = true };
        window.Controls.Add(reader);
        KeepWindowOnScreen(window);
        return window;
    }
    public static bool Newer(string candidate, string current) {
        var pattern = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc)\.(\d+))?$");
        var a = pattern.Match(candidate ?? ""); var b = pattern.Match((current ?? "").Replace("-dev", ""));
        if (!a.Success || !b.Success) throw new InvalidDataException("版本格式无法比较，请在 GitHub 发布页核对。");
        for (int i = 1; i <= 3; i++) {
            int comparison = long.Parse(a.Groups[i].Value).CompareTo(long.Parse(b.Groups[i].Value));
            if (comparison != 0) return comparison > 0;
        }
        string ranks = "alpha,beta,rc,";
        int rankA = a.Groups[4].Success ? ranks.IndexOf(a.Groups[4].Value, StringComparison.Ordinal) : 99;
        int rankB = b.Groups[4].Success ? ranks.IndexOf(b.Groups[4].Value, StringComparison.Ordinal) : 99;
        if (rankA != rankB) return rankA > rankB;
        return a.Groups[5].Success && long.Parse(a.Groups[5].Value) > long.Parse(b.Groups[5].Value);
    }
    public static string ReleaseSummary(string json, string current) {
        var releases = MeetingFiles.Json.DeserializeObject(json) as object[];
        if (releases == null) throw new InvalidDataException("更新响应格式无效。");
        string latest = current.Replace("-dev", "");
        foreach (object item in releases) {
            var release = item as Dictionary<string, object>;
            if (release == null || !release.ContainsKey("tag_name") || !release.ContainsKey("draft") || !(release["draft"] is bool))
                throw new InvalidDataException("更新响应缺少版本字段。");
            if ((bool)release["draft"]) continue;
            string tag = release["tag_name"] as string;
            if (!current.Contains("-") && ((tag != null && tag.Contains("-")) ||
                (release.ContainsKey("prerelease") && Equals(release["prerelease"], true)))) continue;
            if (Newer(tag, latest)) latest = tag;
        }
        return Newer(latest, current) ? "发现新版本 " + latest + "，请打开 GitHub 发布页下载安装。升级前请结束录音、等待转写完成并退出 verbit。" : "当前没有更高版本。";
    }
    public static string CheckUpdates() {
        // No audio, paths, config, credentials or machine identifiers are sent.
        var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/Qjzn/verbit/releases?per_page=20");
        request.UserAgent = "verbit-update-check"; request.Accept = "application/vnd.github+json";
        request.Timeout = request.ReadWriteTimeout = 10000; request.AllowAutoRedirect = false;
        try {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream)) {
                char[] buffer = new char[1048577]; int count = 0, read;
                while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0) count += read;
                if (count > 1048576) throw new InvalidDataException("更新响应过大。");
                return ReleaseSummary(new string(buffer, 0, count), Version);
            }
        } catch (WebException error) {
            var response = error.Response as HttpWebResponse;
            if (response != null) {
                var status = response.StatusCode; response.Dispose();
                if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
                    throw new IOException("无法读取 GitHub 发布信息：仓库可能仍为私有或请求受限。请用有访问权限的浏览器打开发布页；程序不会读取你的 GitHub 凭据。");
            }
            throw new IOException("检查更新失败，请检查网络后重试；当前版本可继续使用。", error);
        }
    }
}

internal sealed class LocalEngineState {
    public bool Ready, Busy;
    public string Title, Detail, Action;
    public static LocalEngineState Read(string root) {
        var value = new LocalEngineState { Title = "尚未启用", Detail = "仅首次准备，约占 5 GB；音频留在电脑。", Action = "启用本地转写" };
        try {
            string statusPath = Path.Combine(root, "installation", "recommended-model.json");
            MeetingConfig.CheckOrdinaryPath(statusPath);
            var status = File.Exists(statusPath) ? MeetingFiles.ReadJson(statusPath) : new Dictionary<string, object>();
            string state = MeetingFiles.Text(status, "state", "");
            string lockPath = Path.Combine(root, "recommended-model.lock"); MeetingConfig.CheckOrdinaryPath(lockPath);
            if (File.Exists(lockPath)) {
                try { using (var handle = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException) { value.Busy = true; }
            }
            if (value.Busy) {
                value.Title = state == "verifying" ? "正在验证中文转写" : "正在准备本地转写";
                value.Detail = MeetingFiles.Text(status, "message", "已下载内容会保留；可以关闭设置，后台继续。");
                string progressPath = Path.Combine(root, "models", "download-status.json");
                if (state == "installing" && File.Exists(progressPath) && File.GetLastWriteTimeUtc(progressPath) >= File.GetLastWriteTimeUtc(statusPath)) {
                    MeetingConfig.CheckOrdinaryPath(progressPath);
                    var progress = MeetingFiles.ReadJson(progressPath);
                    if (MeetingFiles.Text(progress, "state", "") == "downloading") {
                        double received, total, speed;
                        if (double.TryParse(MeetingFiles.Text(progress, "received", "0"), out received) && double.TryParse(MeetingFiles.Text(progress, "total", "0"), out total) && total > 0) {
                            double.TryParse(MeetingFiles.Text(progress, "bytes_per_second", "0"), out speed);
                            value.Detail = string.Format("正在下载当前文件：{0:N0}% · {1:N0}/{2:N0} MB · {3:N1} MB/s。关闭设置后继续。", 100 * received / total, received / 1048576, total / 1048576, speed / 1048576);
                        }
                    }
                }
                value.Action = "准备中…"; return value;
            }
            if (state == "error" || state == "preparing" || state == "installing" || state == "verifying") {
                value.Title = "准备未完成"; value.Action = "继续准备";
                value.Detail = state == "error" ? MeetingFiles.Text(status, "message", "请重试，已下载文件会复用。") : "上次准备已中断，继续时复用已下载文件。";
                return value;
            }
            if (Verified(root)) {
                value.Ready = true; value.Title = "本地转写已可用"; value.Action = "已就绪";
                value.Detail = "内置中文样本试运行通过 · 千问 1.7B · 音频不上传"; return value;
            }
            string python = Path.Combine(root, ".venv", "Scripts", "python.exe");
            if (File.Exists(python)) {
                value.Title = "需要完成验证"; value.Action = "验证并启用";
                value.Detail = "复用已有组件，补齐缺失文件并测试一次中文转写。";
            }
        } catch (Exception error) {
            value.Title = "状态需要检查"; value.Action = "重新检查";
            value.Detail = "无法读取本地状态：" + error.Message;
        }
        return value;
    }
    public static bool Verified(string root) {
        try {
            string path = Path.Combine(root, "installation", "local-ready.json"); MeetingConfig.CheckOrdinaryPath(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) return false;
            var receipt = MeetingFiles.ReadJson(path);
            if (MeetingFiles.Text(receipt, "version", "") != "1" || MeetingFiles.Text(receipt, "state", "") != "ready") return false;
            object files;
            if (!receipt.TryGetValue("files", out files) || !(files is IEnumerable)) return false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object entry in (IEnumerable)files) {
                var row = entry as Dictionary<string, object>; if (row == null) return false;
                string relative = MeetingFiles.Text(row, "path", "");
                string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (relative.Length == 0 || Path.IsPathRooted(relative) || !full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                MeetingConfig.CheckOrdinaryPath(full);
                var info = new FileInfo(full);
                if (!info.Exists || info.Length == 0 || info.Length != Convert.ToInt64(row["size"]) ||
                    info.LastWriteTimeUtc.Ticks != Convert.ToInt64(row["mtime_ns"]) / 100 + 621355968000000000L || !names.Add(relative)) return false;
            }
            foreach (string required in new[] { ".venv/Scripts/python.exe", "check_local_engine.py", "asr_engine.py", "prepare_audio.py", "assets/engine-check.wav",
                "models/Qwen3-ASR-1.7B-hf/config.json", "models/Qwen3-ASR-1.7B-hf/model.safetensors", "models/Qwen3-ASR-1.7B-hf/processor_config.json",
                "models/Qwen3-ASR-1.7B-hf/tokenizer_config.json", "models/Qwen3-ASR-1.7B-hf/tokenizer.json", "models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
                ".venv/Lib/site-packages/torch/__init__.py", ".venv/Lib/site-packages/transformers/__init__.py", ".venv/Lib/site-packages/sherpa_onnx/__init__.py" }) if (!names.Contains(required)) return false;
            return names.Count == 15 && (names.Contains("models/segmentation/model.onnx") || names.Contains("models/sherpa-onnx-pyannote-segmentation-3-0/model.onnx") || names.Contains("models/sherpa-onnx-pyannote-segmentation-3-0/model.int8.onnx"));
        } catch { return false; }
    }
}
