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
            Check(DesktopServices.Newer("v0.1.0-beta.10", "0.1.0-beta.4"), "Numeric prerelease order");
            Check(!DesktopServices.Newer("v0.1.0-beta.2", "0.1.0-beta.4"), "No prerelease downgrade");
            Check(DesktopServices.Newer("v0.1.0", "0.1.0-rc.3"), "Stable follows RC");
            Check(!DesktopServices.Newer("v0.1.0-rc.5", "0.1.0"), "Stable never downgrades to RC");
            Check(DesktopServices.Newer("v0.2.0-alpha.1", "0.1.0"), "Higher minor version");
            Check(!DesktopServices.Newer("v0.1.0-beta.4", "0.1.0-beta.4"), "Same version");
            Reject(delegate { DesktopServices.Newer("https://untrusted.invalid", "0.1.0"); }, "Unrecognized version is not trusted");
            string release = "[{\"tag_name\":\"v0.1.0-beta.10\",\"draft\":false},{\"tag_name\":\"v99.0.0\",\"draft\":true}]";
            Check(DesktopServices.ReleaseSummary(release, "0.1.0-beta.4").Contains("beta.10"), "Draft excluded; beta checked without latest endpoint");
            Check(DesktopServices.ReleaseSummary("[]", "0.1.0-beta.4-dev").Contains("没有更高"), "No releases and dev builds supported");
            Reject(delegate { DesktopServices.ReleaseSummary("{}", "0.1.0"); }, "Malformed metadata rejected");
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
