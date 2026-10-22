using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

internal static class TestDesktopServices {
    static int count;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    [STAThread] static int Main() {
        try {
            string setupRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model-setup-gate"); Directory.CreateDirectory(setupRoot);
            using (DesktopServices.EnterModelSetupGate(setupRoot)) {
                bool blocked = false; try { using (DesktopServices.EnterModelSetupGate(setupRoot)) { } } catch (IOException) { blocked = true; }
                Check(blocked, "A model preparation gate blocks capture/another preparation without touching audio");
            }
            using (DesktopServices.EnterModelSetupGate(setupRoot)) { Check(true, "Gate releases after setup and is reusable"); }
            string setupScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RecommendedModel.ps1");
            File.WriteAllText(setupScript, "# isolated launcher fixture; not executed");
            var setupInfo = DesktopServices.RecommendedModelStartInfo();
            Check(!setupInfo.UseShellExecute && setupInfo.CreateNoWindow && setupInfo.Arguments.Contains("\"" + setupScript + "\"") && setupInfo.Arguments.Contains("-NoProfile"), "Download opens a quoted, isolated helper without a shell console");
            Check(DesktopServices.Newer("v0.1.0-beta.10", "0.1.0-beta.4"), "Numeric prerelease order");
            Check(!DesktopServices.Newer("v0.1.0-beta.2", "0.1.0-beta.4"), "No prerelease downgrade");
            Check(DesktopServices.Newer("v0.1.0", "0.1.0-rc.3"), "Stable follows RC");
            Check(!DesktopServices.Newer("v0.1.0-rc.5", "0.1.0"), "Stable never downgrades to RC");
            Check(DesktopServices.Newer("v0.2.0-alpha.1", "0.1.0"), "Higher minor version");
            Check(!DesktopServices.Newer("v0.1.0-beta.4", "0.1.0-beta.4"), "Same version");
            Check(DesktopServices.ReleaseSummary("[{\"tag_name\":\"v0.2.0-beta.1\",\"draft\":false}]", "0.1.0").Contains("没有更高"), "Stable installs must not be offered prereleases");
            Check(DesktopServices.ReleaseSummary("[{\"tag_name\":\"v0.2.0\",\"draft\":false,\"prerelease\":true}]", "0.1.0").Contains("没有更高"), "GitHub prerelease flag is honored even with a stable-looking tag");
            Reject(delegate { DesktopServices.Newer("https://untrusted.invalid", "0.1.0"); }, "Unrecognized version is not trusted");
            string release = "[{\"tag_name\":\"v0.1.0-beta.10\",\"draft\":false},{\"tag_name\":\"v99.0.0\",\"draft\":true}]";
            Check(DesktopServices.ReleaseSummary(release, "0.1.0-beta.4").Contains("beta.10"), "Draft excluded; beta checked without latest endpoint");
            Check(DesktopServices.ReleaseSummary("[]", "0.1.0-beta.4-dev").Contains("没有更高"), "No releases and dev builds supported");
            Reject(delegate { DesktopServices.ReleaseSummary("{}", "0.1.0"); }, "Malformed metadata rejected");
            System.Windows.Forms.Application.EnableVisualStyles();
            string document = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-changelog.md");
            File.WriteAllText(document, "# 版本说明\r\n中文离线说明，无需文件关联。", new System.Text.UTF8Encoding(false));
            DesktopServices.Open(document);
            var viewer = System.Windows.Forms.Application.OpenForms[0];
            var documentText = (System.Windows.Forms.TextBox)viewer.Controls["document_text"];
            Check(documentText.ReadOnly && documentText.Text == File.ReadAllText(document), "Actual offline-document entry opens a read-only native reader without external association");
            Check(documentText.SelectionLength == 0, "Offline reader initially shows unselected readable text");
            using (var bitmap = new System.Drawing.Bitmap(viewer.Width, viewer.Height)) {
                viewer.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, viewer.Width, viewer.Height));
                bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "offline-reader.png"));
            }
            viewer.Close(); viewer.Dispose();
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-startup");
            Directory.CreateDirectory(folder); DesktopServices.StartupFolder = folder;
            Check(!DesktopServices.StartsWithWindows, "Fresh isolated startup disabled");
            DesktopServices.SetStartup(true); Check(DesktopServices.StartsWithWindows, "Real COM shortcut enables autostart in fixture");
            DesktopServices.SetStartup(true); Check(Directory.GetFiles(folder).Length == 1, "Autostart is idempotent");
            DesktopServices.SetStartup(false); Check(!DesktopServices.StartsWithWindows && Directory.GetFiles(folder).Length == 0, "Disable removes owned shortcut");
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(folder, "verbit installed.lnk") });
            link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Path.Combine(folder, "other.exe") });
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell);
            byte[] before = File.ReadAllBytes(Path.Combine(folder, "verbit installed.lnk"));
            DesktopServices.SetStartup(false); Check(File.Exists(Path.Combine(folder, "verbit installed.lnk")), "Does not remove another installation's entry");
            bool collision = false; try { DesktopServices.SetStartup(true); } catch (IOException) { collision = true; }
            Check(collision && Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, "verbit installed.lnk"))), "Foreign entry is never overwritten");
            Console.WriteLine("PASS TestDesktopServices: " + count + " assertions; no network, user startup, clipboard or audio."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
