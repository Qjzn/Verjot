using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class TestVerjotUpdates {
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) {
        bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    static string Metadata(string version, bool draft, bool prerelease, string asset = null, string digest = null) {
        return MeetingFiles.Json.Serialize(new object[] { new { tag_name = "v" + version, draft = draft, prerelease = prerelease,
            assets = new object[] { new { id = 123, name = asset ?? "Verjot-" + version + "-setup-x64.exe", size = 2048, state = "uploaded", digest = digest ?? "sha256:" + new string('a', 64) } } } });
    }
    static void PumpUntil(Func<bool> done) {
        Application.DoEvents();
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(), "Update window async state completes without blocking the UI");
    }
    static T Control<T>(Form form, string name) where T : System.Windows.Forms.Control { return (T)form.Controls.Find(name, true)[0]; }
    static void Ui(string folder) {
        var next = new VerjotRelease { Version = "0.2.0", Bytes = 2048, Notes = "synthetic release notes" };
        int checks = 0, downloads = 0, installs = 0;
        using (var window = new VerjotUpdateWindow(delegate { installs++; return "synthetic busy recording; retry later"; })) {
            window.CheckRelease = delegate { checks++; return next; };
            window.DownloadRelease = delegate(VerjotRelease release, string path, bool cli, Action<long> progress, CancellationToken token) { downloads++; progress(2048); return Path.Combine(folder, "synthetic-unexecuted.exe"); };
            window.Show(); PumpUntil(delegate { return Control<Button>(window, "update_action").Enabled; });
            Check(checks == 1 && downloads == 0 && installs == 0, "Opening checks only; downloading requires the update action");
            Check(Control<Button>(window, "update_action").Text == "下载并更新", "Installable release has a direct in-window action");
            Control<Button>(window, "update_action").PerformClick(); PumpUntil(delegate { return installs == 1 && Control<Button>(window, "update_action").Enabled; });
            Check(downloads == 1 && Control<Button>(window, "update_action").Text == "安装并重启", "Busy controller preserves the validated download for retry");
            Control<Button>(window, "update_action").PerformClick(); PumpUntil(delegate { return installs == 2; });
            Check(downloads == 1, "Retry after recording/flush never downloads the same installer twice");
            Check(Control<TextBox>(window, "update_details").ContextMenuStrip is VerjotMenu, "Updater text menus use the same renderer");
            using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(folder, "update-window.png")); }
            window.Close();
        }
        using (var window = new VerjotUpdateWindow(null)) {
            window.CheckRelease = delegate { return null; };
            window.Show(); PumpUntil(delegate { return Control<Button>(window, "update_action").Enabled; });
            Check(Control<Label>(window, "update_status").Text.Contains("没有可安装"), "No published installer is never represented as an available automatic update"); window.Close();
        }
        using (var window = new VerjotUpdateWindow(null)) {
            window.CheckRelease = delegate { throw new IOException("synthetic private/HTTP failure"); };
            window.Show(); PumpUntil(delegate { return Control<Button>(window, "update_action").Enabled; });
            Check(Control<TextBox>(window, "update_details").Text.Contains("synthetic private") && Control<CheckBox>(window, "update_github_cli").Enabled, "Authentication failure stays actionable and retryable in the window"); window.Close();
        }
        using (var window = new VerjotUpdateWindow(null)) {
            window.CheckRelease = delegate { throw new OperationCanceledException(); };
            window.Show(); PumpUntil(delegate { return Control<Button>(window, "update_action").Enabled; });
            Check(window.Visible && Control<Label>(window, "update_status").Text.Contains("超时"), "A transport timeout is retryable, not confused with a user cancellation"); window.Close();
        }
        using (var window = new VerjotUpdateWindow(null)) {
            window.CheckRelease = delegate(string version, bool cli, CancellationToken token) { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return null; };
            window.Show(); Application.DoEvents(); Control<Button>(window, "update_close").PerformClick();
            PumpUntil(delegate { return !window.Visible; });
            Check(!window.Visible, "Cancel interrupts a pending check without touching the current application");
        }
    }
    [STAThread] static int Main() {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update-fixture"); Directory.CreateDirectory(root);
            Check(VerjotUpdateClient.Parse(Metadata("0.2.0", false, false), "0.1.0").AssetId == 123, "Published stable installer is eligible");
            Check(VerjotUpdateClient.Parse(Metadata("0.2.0", true, false), "0.1.0") == null, "Drafts never update a client");
            Check(VerjotUpdateClient.Parse(Metadata("0.2.0-beta.1", false, true), "0.1.0") == null, "Stable channel excludes prereleases");
            Check(VerjotUpdateClient.Parse(Metadata("0.1.0-beta.18", false, true), "0.1.0-beta.17") != null, "Beta channel compares numeric prereleases");
            Check(VerjotUpdateClient.Parse(Metadata("0.1.0-beta.2", false, true), "0.1.0-beta.17") == null, "Existing older public artifacts cannot downgrade the app");
            foreach (string name in new[] { "../../evil.exe", "Verjot.zip", "Verjot-0.2.0-setup-x86.exe", "Verjot-9.0.0-setup-x64.exe" })
                Check(VerjotUpdateClient.Parse(Metadata("0.2.0", false, false, name), "0.1.0") == null, "Only the expected installer is eligible: " + name);
            foreach (string hash in new[] { "", "sha1:" + new string('a', 40), "sha256:short" }) Check(VerjotUpdateClient.Parse(Metadata("0.2.0", false, false, null, hash), "0.1.0") == null, "Missing/bad SHA256 prevents update");
            Check(VerjotUpdateClient.Parse("[]", "0.1.0") == null, "Empty releases never imply a new build is published");
            foreach (string version in new[] { "0.1.0-beta.2", "0.1.0-beta.17", "0.1.0-beta.38" }) {
                string oldName = "verbit-" + version + "-setup-x64.exe";
                var historic = VerjotUpdateClient.Parse(Metadata(version, false, true, oldName), "0.1.0-beta.1");
                Check(historic != null && historic.Name == oldName, "Retained historical release keeps its actual asset name: " + version);
                Check(VerjotUpdateClient.Parse(Metadata(version, false, true, oldName), "0.1.0-beta.40") == null, "Historical names never permit a downgrade: " + version);
            }
            foreach (string version in new[] { "0.1.0-beta.39", "0.2.0" })
                Check(VerjotUpdateClient.Parse(Metadata(version, false, version.Contains("-"), "verbit-" + version + "-setup-x64.exe"), "0.1.0-beta.1") == null, "Unreviewed legacy asset name rejected: " + version);
            Check(VerjotUpdateClient.Parse(Metadata("0.1.0-beta.38", false, true, "Verlog-0.1.0-beta.38-setup-x64.exe"), "0.1.0-beta.1") == null, "Unpublished interim brand asset rejected");
            Reject(delegate { VerjotUpdateClient.Parse("{}", "0.1.0"); }, "Bad metadata rejected");
            foreach (string uri in new[] { "http://release-assets.githubusercontent.com/file", "https://github.com.evil.invalid/file", "https://api.github.com/anything", "https://objects.githubusercontent.com:444/file", "https://user@objects.githubusercontent.com/file" })
                Check(!VerjotUpdateClient.AllowedRedirect(new Uri(uri)), "Untrusted or credential-bearing redirect rejected");
            Check(VerjotUpdateClient.AllowedRedirect(new Uri("https://release-assets.githubusercontent.com/file")), "Official asset storage redirect accepted without forwarding authorization");
            using (var input = new MemoryStream(new byte[10])) using (var output = new MemoryStream()) {
                Reject(delegate { VerjotUpdateClient.Copy(input, output, 9, CancellationToken.None, null); }, "Overlong download rejected before writing excess data"); Check(output.Length == 0, "Oversized block is not written");
            }
            using (var input = new MemoryStream(new byte[10])) using (var output = new MemoryStream()) { VerjotUpdateClient.Copy(input, output, 10, CancellationToken.None, null); Check(output.Length == 10, "Bounded download stream completes exactly"); }
            string binary = Assembly.GetExecutingAssembly().Location;
            var local = new VerjotRelease { Bytes = new FileInfo(binary).Length, Sha256 = VerjotUpdateClient.Hash(binary), Version = FileVersionInfo.GetVersionInfo(binary).ProductVersion };
            VerjotUpdateClient.Verify(binary, local); Check(true, "PE, exact size, SHA256 and internal version all verified");
            local.Version = "999.0.0"; Reject(delegate { VerjotUpdateClient.Verify(binary, local); }, "Mismatched internal installer version rejected");
            local.Sha256 = new string('0', 64); Reject(delegate { VerjotUpdateClient.Verify(binary, local); }, "Hash mismatch blocks installer execution");
            foreach (string name in new[] { "../outside", "C:\\outside", "config.json", "models/model.bin", "work/meeting.wav", ".venv/python.exe", "installation/receipt.json", "x/../config.json", "config.json ", "x/../../outside", "folder./file" })
                Reject(delegate { VerjotUpdateInstaller.ProgramPath(root, name); }, "Protected or escaping backup path rejected: " + name);
            string target = Path.Combine(root, "program"), backup = Path.Combine(root, "rollback"), manifestRoot = Path.Combine(root, "helper"); Directory.CreateDirectory(target); Directory.CreateDirectory(manifestRoot);
            File.WriteAllText(Path.Combine(target, "owned.dll"), "old program"); File.WriteAllText(Path.Combine(target, "config.json"), "private config");
            MeetingFiles.WriteJson(Path.Combine(manifestRoot, "update-manifest.json"), new { files = new object[] { new { path = "owned.dll" }, new { path = "new.dll" } } });
            var snapshot = VerjotUpdateInstaller.Backup(target, backup, manifestRoot);
            File.WriteAllText(Path.Combine(target, "owned.dll"), "partial new version"); File.WriteAllText(Path.Combine(target, "new.dll"), "partial new file");
            VerjotUpdateInstaller.Restore(target, backup, snapshot);
            Check(File.ReadAllText(Path.Combine(target, "owned.dll")) == "old program" && !File.Exists(Path.Combine(target, "new.dll")), "Simulated partial install rolls back old bytes and removes only newly introduced program files");
            Check(File.ReadAllText(Path.Combine(target, "config.json")) == "private config", "Rollback never includes user configuration");
            File.WriteAllText(Path.Combine(backup, "owned.dll"), "tampered backup");
            File.WriteAllText(Path.Combine(target, "update-manifest.json"), "new manifest must survive rejected rollback");
            File.WriteAllText(Path.Combine(target, "owned.dll"), "new bytes must survive rejected rollback");
            Reject(delegate { VerjotUpdateInstaller.Restore(target, backup, snapshot); }, "Tampered rollback bytes rejected");
            Check(File.Exists(Path.Combine(target, "update-manifest.json")) && File.ReadAllText(Path.Combine(target, "owned.dll")) == "new bytes must survive rejected rollback", "Every backup is validated before any rollback deletion or replacement");
            string cachedInstaller = Path.Combine(root, "unexecuted-installer.exe"); File.WriteAllText(cachedInstaller, "synthetic only");
            using (var locked = new FileStream(Path.Combine(backup, "owned.dll"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                string warning = VerjotUpdateInstaller.CleanupSuccessful(cachedInstaller, backup, snapshot);
                Check(!string.IsNullOrEmpty(warning) && File.Exists(locked.Name), "A locked cleanup file reports a warning without turning a committed install into rollback");
                Check(File.ReadAllText(Path.Combine(target, "owned.dll")) == "new bytes must survive rejected rollback", "Cleanup never alters installed program bytes");
            }
            Check(VerjotUpdateInstaller.CleanupSuccessful(cachedInstaller, backup, snapshot) == null, "Retained cleanup files can be removed after the lock is released");
            string firstAttempt, secondAttempt;
            var firstSnapshot = VerjotUpdateInstaller.BackupAttempt(target, root, manifestRoot, out firstAttempt);
            File.WriteAllText(Path.Combine(firstAttempt, "owned.dll"), "failed attempt evidence");
            var secondSnapshot = VerjotUpdateInstaller.BackupAttempt(target, root, manifestRoot, out secondAttempt);
            Check(firstAttempt != secondAttempt && VerjotUpdateClient.Hash(Path.Combine(secondAttempt, "owned.dll")) == secondSnapshot["owned.dll"], "Retry creates a fresh validated backup rather than colliding with a failed attempt");
            Check(File.ReadAllText(Path.Combine(firstAttempt, "owned.dll")) == "failed attempt evidence" && firstSnapshot.ContainsKey("owned.dll"), "Retry preserves earlier recovery evidence");
            File.WriteAllText(cachedInstaller, "synthetic only");
            using (var locked = new FileStream(cachedInstaller, FileMode.Open, FileAccess.Read, FileShare.None)) {
                Check(VerjotUpdateInstaller.CleanupSuccessful(cachedInstaller, secondAttempt, secondSnapshot) != null, "Installer deletion failure remains a cleanup warning after backups were removed");
                Check(!File.Exists(Path.Combine(secondAttempt, "owned.dll")) && File.Exists(cachedInstaller) && File.ReadAllText(Path.Combine(target, "owned.dll")) == "new bytes must survive rejected rollback", "Post-commit cleanup cannot restore deleted snapshots or modify the installed version");
            }
            Check(VerjotUpdateInstaller.CleanupSuccessful(cachedInstaller, secondAttempt, secondSnapshot) == null, "Installer cleanup is retryable without rerunning installation");
            var start = VerjotUpdateInstaller.SetupStart("C:\\synthetic\\setup.exe", "C:\\synthetic app", "C:\\synthetic\\install.log");
            Check(!start.UseShellExecute && start.Arguments.Contains("/NORESTART") && start.Arguments.Contains("/NOCLOSEAPPLICATIONS") && start.Arguments.Contains("/NORESTARTAPPLICATIONS"), "Installer cannot reboot or force close/restart recording applications");
            // Async WinForms continuations need the same message loop as the
            // installed application. DoEvents alone uninstalls its context
            // when a temporary window closes, producing false cross-thread UI.
            Exception uiFailure = null;
            using (var host = new Form { ShowInTaskbar = false }) {
                host.Shown += delegate {
                    host.Hide(); host.BeginInvoke((Action)delegate {
                        try { System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = true; Ui(root); }
                        catch (Exception error) { uiFailure = error; }
                        finally { host.Close(); }
                    });
                };
                Application.Run(host);
            }
            if (uiFailure != null) throw uiFailure;
            Console.WriteLine("PASS TestVerjotUpdates: " + assertions + " assertions; isolated UI/metadata/rollback fixtures only; no live network, installer execution, clipboard or recording."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
