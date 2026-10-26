using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Exercises only the unarmed countdown/close path, never audio or clipboard.
internal static class TestDeviceRevalidation {
    static int assertions;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
    static object Field(object target, string name) { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
    [STAThread] static int Main(string[] args) {
        try {
            string executable = Path.GetFullPath(args[0]), home = Path.GetDirectoryName(executable);
            Check(!Directory.Exists(Path.Combine(home, "work")), "Probe fixture must have no recordings");
            var type = Assembly.LoadFrom(executable).GetType("DeviceRevalidation", true);
            var main = type.GetMethod("Main", BindingFlags.Static | BindingFlags.NonPublic);
            Check((int)main.Invoke(null, new object[] { new string[0] }) == 64, "Missing consent refuses before opening devices");
            Check((int)main.Invoke(null, new object[] { new string[] { "--run" } }) == 64, "Unrecognized consent refuses");
            using (var form = (Form)Activator.CreateInstance(type, true)) {
                form.Show(); Application.DoEvents();
                Check(Field(form, "session") == null, "Opening test window must not record");
                Check(Field(form, "player") == null, "Opening test window must not prepare playback");
                Check(!((Timer)Field(form, "tick")).Enabled, "Timer remains disarmed until explicit start");
                ((Button)Field(form, "start")).PerformClick();
                Check(((Timer)Field(form, "tick")).Enabled, "Start enters a visible countdown");
                Check(!((Button)Field(form, "start")).Enabled, "Countdown cannot be started twice");
                type.GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
                Check(Field(form, "session") == null && Field(form, "player") == null, "Countdown has not opened audio devices");
                form.Close(); Application.DoEvents();
                Check(!((Timer)Field(form, "tick")).Enabled, "Closing countdown disarms it");
            }
            Check(!Directory.Exists(Path.Combine(home, "work")), "Smoke must not create raw recordings");
            Check(!File.Exists(Path.Combine(home, "device-review.json")), "Smoke cannot masquerade as a completed device test");
            // Exercise the actual config-validation entry, then close the unarmed window.
            using (var close = new Timer { Interval = 500 }) {
                close.Tick += delegate {
                    close.Stop();
                    foreach (Form open in Application.OpenForms) if (open.GetType() == type) { open.Close(); break; }
                };
                close.Start();
                Check((int)main.Invoke(null, new object[] { new string[] { "--consent-record-and-play" } }) == 0,
                    "Real startup validates the prepared output directory and opens the unarmed window");
            }
            Check(!Directory.Exists(Path.Combine(home, "work")), "Actual startup smoke still must not record");
            Console.WriteLine("DeviceRevalidation: " + assertions + " assertions passed; no recording/playback/clipboard."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
