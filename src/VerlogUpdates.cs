using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class VerlogRelease {
    public string Version, Name, Sha256, Notes;
    public long AssetId, Bytes;
}

// Only this repository's published installer assets are eligible. No generic
// command, mirror, download URL or credentials can come from release metadata.
internal static class VerlogUpdateClient {
    const string Api = "https://api.github.com/repos/Qjzn/Verlog/releases";
    internal const long MaximumInstallerBytes = 128L * 1024 * 1024;
    internal static string Hash(string path) {
        using (var input = File.OpenRead(path)) using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }
    internal static VerlogRelease Parse(string json, string current) {
        if (json.Length > 1048576) throw new InvalidDataException("更新响应过大。");
        var releases = MeetingFiles.Json.DeserializeObject(json) as object[];
        if (releases == null) throw new InvalidDataException("更新响应格式无效。");
        VerlogRelease best = null;
        foreach (object item in releases) {
            var value = item as Dictionary<string, object>;
            if (value == null || !value.ContainsKey("draft") || !(value["draft"] is bool)) continue;
            if ((bool)value["draft"]) continue;
            string tag = MeetingFiles.Text(value, "tag_name", ""), version = tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : tag;
            if (!Regex.IsMatch(version, @"^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}(?:-(?:alpha|beta|rc)\.[0-9]{1,9})?$")) continue;
            if (!current.Contains("-") && (version.Contains("-") || (value.ContainsKey("prerelease") && Equals(value["prerelease"], true)))) continue;
            if (!DesktopServices.Newer(version, current) || (best != null && !DesktopServices.Newer(version, best.Version))) continue;
            object raw; var assets = value.TryGetValue("assets", out raw) ? raw as object[] : null;
            if (assets == null) continue;
            string expected = "Verlog-" + version + "-setup-x64.exe";
            foreach (object asset in assets) {
                var file = asset as Dictionary<string, object>;
                if (file == null || MeetingFiles.Text(file, "name", "") != expected || MeetingFiles.Text(file, "state", "") != "uploaded") continue;
                long id, size;
                if (!long.TryParse(MeetingFiles.Text(file, "id", ""), out id) || id <= 0 || !long.TryParse(MeetingFiles.Text(file, "size", ""), out size) || size < 1024 || size > MaximumInstallerBytes) continue;
                string digest = MeetingFiles.Text(file, "digest", "");
                if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) continue;
                best = new VerlogRelease { Version = version, Name = expected, AssetId = id, Bytes = size, Sha256 = digest.Substring(7).ToLowerInvariant(), Notes = MeetingFiles.Text(value, "body", "") };
                break;
            }
        }
        return best;
    }
    internal static VerlogRelease Check(string current, bool githubCli, CancellationToken cancel) {
        using (var output = new MemoryStream()) {
            if (githubCli) Cli("repos/Qjzn/Verlog/releases?per_page=20", false, output, 1048576, cancel, null);
            else Http(new Uri(Api + "?per_page=20"), false, output, 1048576, cancel, null);
            return Parse(new UTF8Encoding(false, true).GetString(output.ToArray()), current);
        }
    }
    internal static void Copy(Stream input, Stream output, long limit, CancellationToken cancel, Action<long> progress) {
        cancel.ThrowIfCancellationRequested();
        byte[] buffer = new byte[65536]; long bytes = 0; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) {
            cancel.ThrowIfCancellationRequested(); bytes += read;
            if (bytes > limit) throw new InvalidDataException("下载超出声明大小，已拒绝安装。");
            output.Write(buffer, 0, read); if (progress != null) progress(bytes);
        }
        cancel.ThrowIfCancellationRequested();
    }
    internal static bool AllowedRedirect(Uri uri) {
        return uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com");
    }
    static void Http(Uri uri, bool binary, Stream output, long limit, CancellationToken cancel, Action<long> progress) {
        for (int redirects = 0; redirects < 5; redirects++) {
            cancel.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "Verlog-updater"; request.Accept = binary ? "application/octet-stream" : "application/vnd.github+json";
            request.Timeout = request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = false;
            // Tokens are opt-in through the named local environment variable,
            // and never follow a redirect to the asset storage host.
            if (uri.Host == "api.github.com") {
                string token = MeetingConfig.ApiKeyValue("VERLOG_GITHUB_TOKEN");
                if (string.IsNullOrWhiteSpace(token)) token = MeetingConfig.ApiKeyValue("VERBIT_GITHUB_TOKEN"); // Existing private installations keep working.
                if (!string.IsNullOrWhiteSpace(token)) {
                    token = token.Trim();
                    if (!Regex.IsMatch(token, @"\A[A-Za-z0-9_.-]{20,512}\z")) throw new IOException("VERLOG_GITHUB_TOKEN 格式无效，请检查本机环境变量；不会显示或记录令牌。");
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                }
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            }
            try {
                using (cancel.Register(request.Abort)) using (var response = (HttpWebResponse)request.GetResponse()) {
                    int code = (int)response.StatusCode;
                    if (code >= 300 && code < 400) {
                        Uri next;
                        if (!binary || !Uri.TryCreate(uri, response.Headers["Location"], out next) || !AllowedRedirect(next)) throw new IOException("更新下载的跳转地址不受信任。");
                        uri = next; continue;
                    }
                    if (code != 200 || response.ContentLength > limit) throw new IOException("更新响应无效或超出大小限制。");
                    using (var input = response.GetResponseStream()) Copy(input, output, limit, cancel, progress);
                    return;
                }
            } catch (WebException failure) {
                if (failure.Response != null) failure.Response.Dispose();
                cancel.ThrowIfCancellationRequested();
                throw new IOException("无法读取 GitHub 更新。私有测试版可勾选使用已登录的 GitHub CLI，或设置 VERLOG_GITHUB_TOKEN；也请检查网络。不会读取浏览器登录信息。");
            }
        }
        throw new IOException("更新下载跳转次数过多。");
    }
    internal static string CliPath() {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        if (File.Exists(path)) return path;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            try { path = Path.Combine(folder.Trim('"'), "gh.exe"); if (Path.IsPathRooted(path) && File.Exists(path)) return path; } catch (ArgumentException) { }
        }
        throw new IOException("未找到可选的 GitHub CLI。公开仓库无需它；私有测试请安装 gh 并完成 gh auth login，或设置 VERLOG_GITHUB_TOKEN。");
    }
    static void Cli(string endpoint, bool binary, Stream output, long limit, CancellationToken cancel, Action<long> progress) {
        var start = new ProcessStartInfo(CliPath(), "api --hostname github.com " + endpoint + (binary ? " --header Accept:application/octet-stream" : " --header Accept:application/vnd.github+json")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.EnvironmentVariables.Remove("GH_DEBUG");
        using (var process = Process.Start(start)) using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel)) {
            timeout.CancelAfter(TimeSpan.FromMinutes(binary ? 10 : 1));
            using (timeout.Token.Register(delegate { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } })) {
                var errors = process.StandardError.ReadToEndAsync();
                try { Copy(process.StandardOutput.BaseStream, output, limit, timeout.Token, progress); }
                catch { if (!process.HasExited) try { process.Kill(); } catch (InvalidOperationException) { } throw; }
                process.WaitForExit(); errors.GetAwaiter().GetResult();
                cancel.ThrowIfCancellationRequested();
                if (timeout.IsCancellationRequested) throw new IOException("GitHub 下载超时，请重试。");
                if (process.ExitCode != 0) throw new IOException("GitHub CLI 无法读取发布资产，请在本机完成 gh auth login 并确认仓库访问权限。");
            }
        }
    }
    internal static string Download(VerlogRelease release, string directory, bool githubCli, Action<long> progress, CancellationToken cancel) {
        Directory.CreateDirectory(directory); MeetingConfig.CheckOrdinaryPath(directory);
        if (!Regex.IsMatch(release.Name ?? "", @"^Verlog-\d+\.\d+\.\d+(?:-(?:alpha|beta|rc)\.\d+)?-setup-x64\.exe$")) throw new InvalidDataException("安装包名称无效。");
        string destination = Path.Combine(directory, release.Name), partial = destination + ".part";
        try {
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (githubCli) Cli("repos/Qjzn/Verlog/releases/assets/" + release.AssetId, true, output, release.Bytes, cancel, progress);
                else Http(new Uri(Api + "/assets/" + release.AssetId), true, output, release.Bytes, cancel, progress);
            }
            Verify(partial, release); cancel.ThrowIfCancellationRequested();
            File.Move(partial, destination); return destination;
        } catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }
    internal static void Verify(string path, VerlogRelease release) {
        MeetingConfig.CheckOrdinaryPath(path);
        if (new FileInfo(path).Length != release.Bytes || !string.Equals(Hash(path), release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包 SHA256 或大小不一致，已拒绝更新。原版本不变。");
        using (var input = File.OpenRead(path)) if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidDataException("下载的文件不是 Windows 安装程序。");
        // Inno's version resource pads this field with spaces. Normalize only
        // resource padding, never a partial version or a failed digest.
        string version = (FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "").Trim(' ', '\0');
        if (version != release.Version) throw new InvalidDataException("安装包内部版本不一致，已拒绝更新。");
    }
}

internal static class VerlogUpdateInstaller {
    internal const string Registration = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{DE124FA5-8E3D-4B80-A62E-AF8FA54B1E43}_is1";
    internal static string ProgramPath(string root, string relative) {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(":")) throw new IOException("更新清单路径无效。");
        string[] parts = relative.Replace('\\', '/').Split('/');
        foreach (string part in parts) if (part.Length == 0 || part == "." || part == ".." || part != part.Trim() || part.EndsWith(".", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("更新清单路径无效。");
        string first = parts[0].ToLowerInvariant();
        if (first == "config.json" || first == "models" || first == ".venv" || first == "work" || first == "installation" || first == "logs") throw new IOException("更新清单不得包含个人配置或会议资料。");
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("更新路径超出程序目录。");
        MeetingConfig.CheckOrdinaryPath(path); return path;
    }
    internal static Dictionary<string, string> Backup(string root, string backup, string manifestRoot) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string manifest = Path.Combine(manifestRoot, "update-manifest.json");
        MeetingConfig.CheckOrdinaryPath(manifest);
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1048576) throw new IOException("缺少有效的程序更新清单。");
        var document = MeetingFiles.ReadJson(manifest); object raw;
        var files = document.TryGetValue("files", out raw) ? raw as System.Collections.IList : null;
        if (files == null || files.Count > 200) throw new IOException("更新清单无效。");
        var names = new List<string> { "update-manifest.json", "unins000.exe", "unins000.dat", "unins000.msg" };
        foreach (object item in files) {
            var value = item as Dictionary<string, object>;
            if (value == null) throw new IOException("更新清单文件项无效。");
            names.Add(MeetingFiles.Text(value, "path", ""));
        }
        foreach (string name in names) {
            string from = ProgramPath(root, name), to = ProgramPath(backup, name);
            if (result.ContainsKey(name)) continue;
            result[name] = File.Exists(from) ? VerlogUpdateClient.Hash(from) : null;
            if (result[name] == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(from, to, false);
            if (VerlogUpdateClient.Hash(to) != result[name]) throw new IOException("更新备份校验失败，未开始安装。");
        }
        return result;
    }
    internal static Dictionary<string, string> BackupAttempt(string root, string directory, string manifestRoot, out string backupRoot) {
        backupRoot = Path.Combine(directory, "rollback-" + Guid.NewGuid().ToString("N"));
        return Backup(root, backupRoot, manifestRoot);
    }
    internal static void Restore(string root, string backup, Dictionary<string, string> files) {
        // Reject an incomplete/tampered snapshot before deleting or replacing
        // any installed file. Validation during restoration is too late.
        foreach (var item in files) {
            ProgramPath(root, item.Key);
            string from = ProgramPath(backup, item.Key);
            if (item.Value != null && (!File.Exists(from) || VerlogUpdateClient.Hash(from) != item.Value))
                throw new IOException("回滚备份校验失败，请保留更新资料。");
        }
        foreach (var item in files) {
            string to = ProgramPath(root, item.Key), from = ProgramPath(backup, item.Key);
            if (item.Value == null) { if (File.Exists(to)) File.Delete(to); continue; }
            if (!File.Exists(from) || VerlogUpdateClient.Hash(from) != item.Value) throw new IOException("回滚备份校验失败，请保留更新资料。");
            Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Copy(from, to, true);
            if (VerlogUpdateClient.Hash(to) != item.Value) throw new IOException("程序回滚校验失败，请保留更新资料。");
        }
    }
    internal static string CleanupSuccessful(string installer, string backupRoot, Dictionary<string, string> backup) {
        bool retained = false;
        foreach (var file in backup) {
            try { string path = ProgramPath(backupRoot, file.Key); if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { retained = true; }
            catch (UnauthorizedAccessException) { retained = true; }
        }
        try { MeetingConfig.CheckOrdinaryPath(installer); File.Delete(installer); }
        catch (IOException) { retained = true; }
        catch (UnauthorizedAccessException) { retained = true; }
        return retained ? "新版已安装；少量临时资料被占用，保留在：" + Path.GetDirectoryName(backupRoot) : null;
    }
    internal static void VerifyInstalled(string root, string version) {
        string executable = ProgramPath(root, "ObsidianMeeting.exe");
        if (!File.Exists(executable) || FileVersionInfo.GetVersionInfo(executable).ProductVersion != version) throw new IOException("安装后的版本校验失败。");
        var manifest = MeetingFiles.ReadJson(ProgramPath(root, "update-manifest.json"));
        foreach (var item in (System.Collections.IList)manifest["files"]) {
            var file = (Dictionary<string, object>)item; string path = ProgramPath(root, MeetingFiles.Text(file, "path", ""));
            if (!File.Exists(path) || VerlogUpdateClient.Hash(path) != MeetingFiles.Text(file, "sha256", "")) throw new IOException("安装后程序文件不完整，准备恢复旧版本。");
        }
    }
    internal static ProcessStartInfo SetupStart(string installer, string target, string log) {
        return new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /DIR=\"" + target.TrimEnd('\\') + "\" /LOG=\"" + log + "\"") {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(installer)
        };
    }
    internal static void CheckRegistered(string target) {
        using (var key = Registry.CurrentUser.OpenSubKey(Registration)) {
            string registered = key == null ? null : key.GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(registered) || !string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("当前目录不是本机登记的安装目录。源码/便携目录请使用安装包安装后再使用窗口更新。");
        }
    }
    internal static string Prepare(VerlogRelease release, string installer, string directory) {
        string target = Path.GetDirectoryName(DesktopServices.Executable);
        CheckRegistered(target); VerlogUpdateClient.Verify(installer, release);
        Directory.CreateDirectory(directory); MeetingConfig.CheckOrdinaryPath(directory);
        string helper = Path.Combine(directory, "Verlog-update-helper.exe");
        MeetingConfig.CheckOrdinaryPath(helper); File.Copy(DesktopServices.Executable, helper, true);
        foreach (string name in new[] { "NAudio.dll", "update-manifest.json" }) {
            string destination = Path.Combine(directory, name); MeetingConfig.CheckOrdinaryPath(destination); File.Copy(Path.Combine(target, name), destination, true);
        }
        MeetingConfig.CheckOrdinaryPath(helper + ".config"); File.Copy(DesktopServices.Executable + ".config", helper + ".config", true);
        using (var parent = Process.GetCurrentProcess()) {
            string plan = Path.Combine(directory, "update-plan.json");
            MeetingFiles.WriteJson(plan, new { target = target, parent = parent.Id, parent_started = parent.StartTime.ToUniversalTime().Ticks,
                from_version = DesktopServices.Version, version = release.Version, installer = installer, bytes = release.Bytes, sha256 = release.Sha256 });
            return plan;
        }
    }
    internal static void Launch(string plan) {
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(plan), "Verlog-update-helper.exe"), "--apply-update \"" + plan + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan) };
        using (var process = Process.Start(start)) {
            if (!process.WaitForInputIdle(10000) || process.HasExited) throw new IOException("无法打开更新安装窗口，当前程序继续运行。");
        }
    }
    internal static void Apply(string planPath, Action<string> progress) {
        string directory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        if (!string.Equals(Path.GetFullPath(planPath), Path.Combine(directory, "update-plan.json"), StringComparison.OrdinalIgnoreCase)) throw new IOException("更新计划必须位于更新工具旁。");
        var plan = MeetingFiles.ReadJson(planPath);
        string target = Path.GetFullPath(MeetingFiles.Text(plan, "target", "")), executable = ProgramPath(target, "ObsidianMeeting.exe");
        CheckRegistered(target);
        var release = new VerlogRelease { Version = MeetingFiles.Text(plan, "version", ""), Bytes = Convert.ToInt64(plan["bytes"]), Sha256 = MeetingFiles.Text(plan, "sha256", "") };
        string previousVersion = MeetingFiles.Text(plan, "from_version", ""), installer = Path.GetFullPath(MeetingFiles.Text(plan, "installer", ""));
        if (!DesktopServices.Newer(release.Version, previousVersion) || !installer.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("更新版本或安装包位置无效。");
        VerlogUpdateClient.Verify(installer, release);
        progress("正在等待 Verlog 保存并退出…");
        try {
            using (var parent = Process.GetProcessById(Convert.ToInt32(plan["parent"]))) {
                if (parent.StartTime.ToUniversalTime().Ticks != Convert.ToInt64(plan["parent_started"]) || !string.Equals(parent.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)) throw new IOException("原程序身份不匹配，未开始安装。");
                if (!parent.WaitForExit(30000)) throw new IOException("原程序仍在运行，未强制结束，也未替换文件。请完成保存后重试。");
            }
        } catch (ArgumentException) { /* The verified parent may already have exited. */ }
        using (var gate = new Mutex(false, "Local\\ObsidianMeetingHotkey")) {
            bool acquired;
            try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Verlog 已被重新打开，更新已停止，未替换程序。");
            bool startedInstaller = false; Dictionary<string, string> backup = null;
            // Never reuse an earlier attempt's snapshot after a failure.
            string backupRoot = null, log = Path.Combine(directory, "install.log");
            try {
                if (FileVersionInfo.GetVersionInfo(executable).ProductVersion != previousVersion) throw new IOException("原程序版本已变化，请重新检查更新。");
                VerlogUpdateClient.Verify(installer, release);
                progress("正在校验并备份程序文件…");
                backup = BackupAttempt(target, directory, directory, out backupRoot);
                MeetingFiles.WriteJson(Path.Combine(directory, "rollback-files.json"), backup);
                progress("正在安装 " + release.Version + "，请稍候…");
                // Keep the verified installer immutable until Windows maps it.
                using (var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var setup = Process.Start(SetupStart(installer, target, log))) {
                    startedInstaller = true; setup.WaitForExit();
                    if (setup.ExitCode != 0) throw new IOException("安装未完成（代码 " + setup.ExitCode + "）。");
                }
                VerifyInstalled(target, release.Version);
                MeetingFiles.WriteJson(Path.Combine(directory, "result.json"), new { passed = true, version = release.Version });
            } catch (Exception failure) {
                if (startedInstaller && backup != null) {
                    progress("安装未完成，正在恢复旧版本…");
                    try {
                        Restore(target, backupRoot, backup);
                        using (var key = Registry.CurrentUser.OpenSubKey(Registration, true)) if (key != null) key.SetValue("DisplayVersion", previousVersion);
                    } catch (Exception restore) { throw new IOException("安装及回滚未完成。请保留资料：" + directory + "。" + restore.Message, failure); }
                }
                throw new IOException(failure.Message + "\n旧版本及个人资料保留。日志与恢复资料：" + directory, failure);
            } finally { gate.ReleaseMutex(); }
            // Installation is committed. Cleanup must never trigger rollback:
            // it may already have removed part of the successful snapshot.
            string warning = CleanupSuccessful(installer, backupRoot, backup);
            if (warning != null) {
                try { MeetingFiles.WriteJson(Path.Combine(directory, "result.json"), new { passed = true, version = release.Version, cleanup_warning = warning }); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                progress(warning);
            }
        }
        Process.Start(new ProcessStartInfo(executable, "--show") { UseShellExecute = false, WorkingDirectory = target });
    }
}

internal sealed class VerlogUpdateWindow : Form {
    readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 8) };
    readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 8, Style = ProgressBarStyle.Marquee };
    readonly TextBox details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    readonly CheckBox cli = new CheckBox { Text = "使用已登录的 GitHub CLI（仅私有测试需要）", Dock = DockStyle.Top, Height = 28 };
    readonly Button action = new Button { Text = "检查更新", AutoSize = true, MinimumSize = new Size(100, 30) };
    readonly Button close = new Button { Text = "关闭", AutoSize = true, MinimumSize = new Size(80, 30) };
    readonly CancellationTokenSource cancel = new CancellationTokenSource();
    readonly Func<VerlogRelease, string, string, string> install;
    internal Func<string, bool, CancellationToken, VerlogRelease> CheckRelease = VerlogUpdateClient.Check;
    internal Func<VerlogRelease, string, bool, Action<long>, CancellationToken, string> DownloadRelease = VerlogUpdateClient.Download;
    readonly string plan;
    VerlogRelease release;
    string downloaded, directory;
    Font displayFont;
    bool working, installing, handedOff;
    internal VerlogUpdateWindow(Func<VerlogRelease, string, string, string> install, string plan = null) {
        this.install = install; this.plan = plan;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        displayFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel); Font = displayFont;
        Text = "Verlog · 更新"; Icon = MeetingBrand.IdleIcon; ClientSize = new Size(460, 300); Padding = new Padding(16);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; BackColor = details.BackColor = VerlogMenuRenderer.Surface; ForeColor = details.ForeColor = VerlogMenuRenderer.Ink;
        status.Text = "当前版本 " + DesktopServices.Version;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(action); buttons.Controls.Add(close);
        VerlogUi.StyleButton(action, true); VerlogUi.StyleButton(close, false);
        Controls.Add(details); Controls.Add(cli); Controls.Add(progress); Controls.Add(status); Controls.Add(buttons);
        VerlogMenu.AttachTextMenu(details); CancelButton = close;
        action.Name = "update_action"; close.Name = "update_close"; cli.Name = "update_github_cli"; status.Name = "update_status"; details.Name = "update_details";
        action.Click += async delegate {
            if (working) return;
            if (plan != null) {
                try {
                    string target = MeetingFiles.Text(MeetingFiles.ReadJson(plan), "target", ""); VerlogUpdateInstaller.CheckRegistered(target);
                    Process.Start(new ProcessStartInfo(VerlogUpdateInstaller.ProgramPath(target, "ObsidianMeeting.exe"), "--show") { UseShellExecute = false, WorkingDirectory = target }); Close();
                } catch (Exception error) { details.Text = error.Message; }
            } else await Run();
        };
        cli.CheckedChanged += delegate { if (!working) { release = null; downloaded = null; action.Text = "重新检查"; } };
        close.Click += delegate { if (working) { cancel.Cancel(); close.Enabled = false; } else Close(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (installing) { e.Cancel = true; return; }
            if (!handedOff) cancel.Cancel();
        };
        Shown += async delegate { ScaleFont(); await Run(); };
    }
    void ScaleFont() {
        var old = displayFont; displayFont = new Font("Microsoft YaHei UI", 12 * DeviceDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel); Font = displayFont; old.Dispose();
        Bounds = DesktopServices.FitWindowBounds(Bounds, Screen.FromControl(this).WorkingArea);
        status.MaximumSize = new Size(ClientSize.Width - Padding.Horizontal, 0);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); ScaleFont(); }
    void Busy(bool value) {
        working = value; action.Enabled = cli.Enabled = !value; close.Text = value ? "取消" : "关闭";
        close.Enabled = !installing; progress.Visible = value;
    }
    void Report(string text) {
        if (!IsDisposed && IsHandleCreated) try { BeginInvoke((Action)delegate { if (!IsDisposed) status.Text = text; }); } catch (InvalidOperationException) { }
    }
    async Task Run() {
        if (cancel.IsCancellationRequested) { Close(); return; }
        if (plan != null) {
            installing = true; cli.Visible = action.Visible = false; Busy(true);
            details.Text = "更新仅替换程序文件，个人配置、模型、笔记及录音保留。安装期间请不要关闭电脑。";
            try {
                await Task.Run(delegate { VerlogUpdateInstaller.Apply(plan, Report); });
                status.Text = "更新完成，Verlog 已重新打开。";
                details.Text = MeetingFiles.Text(MeetingFiles.ReadJson(Path.Combine(Path.GetDirectoryName(plan), "result.json")), "cleanup_warning", "配置和个人资料已保留。");
            } catch (Exception error) {
                status.Text = "更新未完成"; details.Text = error.Message;
                action.Text = "打开 Verlog"; action.Visible = true;
                // The original program may already have exited. The retained
                // log/rollback paths are shown rather than silently retrying.
            } finally { installing = false; Busy(false); }
            return;
        }
        bool useCli = cli.Checked; Busy(true);
        try {
            if (release == null) {
                status.Text = "正在检查可安装的新版本…";
                release = await Task.Run(delegate { return CheckRelease(DesktopServices.Version, useCli, cancel.Token); });
                if (IsDisposed) return;
                if (release == null) { status.Text = "当前没有可安装的新版本。"; details.Text = "已安装 " + DesktopServices.Version + "。只显示已发布、带 x64 安装包和 SHA256 摘要的新版本；代码提交不等于可安装发布。"; action.Text = "重新检查"; }
                else { status.Text = "可更新至 " + release.Version; details.Text = "下载约 " + (release.Bytes / 1048576.0).ToString("F1") + " MB，校验后将在此窗口完成安装并重新打开 Verlog。\r\n\r\n" + release.Notes; action.Text = "下载并更新"; }
            } else {
                if (downloaded == null) {
                    directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Verlog", "updates", "update-" + Guid.NewGuid().ToString("N"));
                    status.Text = "正在下载 " + release.Version + "…";
                    long last = 0;
                    downloaded = await Task.Run(delegate {
                        return DownloadRelease(release, directory, useCli, delegate(long bytes) {
                            long now = Environment.TickCount;
                            if (now - last < 150 && bytes != release.Bytes) return; last = now;
                            Report("正在下载 " + (bytes / 1048576.0).ToString("F1") + " / " + (release.Bytes / 1048576.0).ToString("F1") + " MB…");
                        }, cancel.Token);
                    });
                }
                if (IsDisposed) return;
                status.Text = "安装包已校验，正在准备安全更新…";
                string failure = install == null ? "请从托盘的设置中开始更新。" : install(release, downloaded, directory);
                if (!string.IsNullOrEmpty(failure)) { details.Text = failure; status.Text = "安装包已保留，稍后可重试。"; action.Text = "安装并重启"; }
                else handedOff = true;
            }
        } catch (OperationCanceledException) {
            if (!IsDisposed) {
                if (cancel.IsCancellationRequested) { status.Text = "更新已取消，当前版本未变。"; Close(); }
                else { status.Text = "检查或下载超时"; details.Text = "当前版本未变，请检查网络后重试。"; action.Text = release == null ? "重新检查" : "重试下载"; }
            }
        }
        catch (Exception error) { if (!IsDisposed) { status.Text = "暂时无法更新"; details.Text = error.Message; action.Text = release == null ? "重新检查" : "重试下载"; } }
        finally { if (!IsDisposed && !handedOff) Busy(false); }
    }
    internal static int ApplyMode(string plan) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        using (var window = new VerlogUpdateWindow(null, plan)) Application.Run(window);
        return 0;
    }
    protected override void Dispose(bool disposing) {
        if (disposing) cancel.Cancel();
        base.Dispose(disposing);
        if (disposing && displayFont != null) { displayFont.Dispose(); displayFont = null; }
        // Do not dispose CancellationTokenSource while a canceled background
        // request can still be completing its cancellation callback.
    }
}
