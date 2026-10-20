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
    public static string Version {
        get {
            var attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            return attributes.Length == 0 ? "0.1.0-beta.4-dev" : ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
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
    public static void Open(string url) { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
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
