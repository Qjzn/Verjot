using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// External-process journey on a private desktop. No clipboard or shared input.
internal static class TestInstalledJourney {
    delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc action, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, EnumProc action, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string caption);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, string text);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, StringBuilder text);
    struct Rect { public int Left, Top, Right, Bottom; }
    static Process child; static int assertions; static readonly List<string> steps = new List<string>();
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static string Text(IntPtr window) { var text = new StringBuilder(32768); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    static string Class(IntPtr window) { var text = new StringBuilder(256); GetClassName(window, text, text.Capacity); return text.ToString(); }
    static bool Owned(IntPtr window) { uint id; GetWindowThreadProcessId(window, out id); return child != null && id == child.Id; }
    static List<IntPtr> Children(IntPtr window) { var list = new List<IntPtr>(); EnumChildWindows(window, delegate(IntPtr item, IntPtr unused) {list.Add(item); return true;}, IntPtr.Zero); return list; }
    static IntPtr Window(string caption) { IntPtr result = IntPtr.Zero; EnumWindows(delegate(IntPtr window, IntPtr unused) { if (Owned(window) && IsWindowVisible(window) && Text(window) == caption) result = window; return true; }, IntPtr.Zero); return result; }
    static IntPtr NoteWindow() {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr unused) {
            if (Owned(window) && IsWindowVisible(window) && Children(window).Any(delegate(IntPtr item) {
                return Class(item).IndexOf("RICHEDIT", StringComparison.OrdinalIgnoreCase) >= 0;
            })) result = window;
            return true;
        }, IntPtr.Zero);
        return result;
    }
    static IntPtr ControlWindow() {
        IntPtr cursor = IntPtr.Zero;
        while ((cursor = FindWindowEx(new IntPtr(-3), cursor, null, "ObsidianMeetingHotkey")) != IntPtr.Zero) if (Owned(cursor)) return cursor;
        return IntPtr.Zero;
    }
    static void Until(Func<bool> check, string message) { var clock = Stopwatch.StartNew(); while (!check() && clock.ElapsedMilliseconds < 12000) {if (child != null && child.HasExited) break; Thread.Sleep(30);} Check(check(), message); }
    static void Click(IntPtr window, string caption) { var button = Children(window).Single(delegate(IntPtr item) {return Text(item) == caption;}); Check(PostMessage(button, 0xF5, IntPtr.Zero, IntPtr.Zero), "Native button posted: " + caption); }
    static void Launch(string app) {
        var start = new ProcessStartInfo(Path.Combine(app, "ObsidianMeeting.exe")) {UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=app};
        start.EnvironmentVariables.Remove("VERLOG_REVIEW_SYNTHETIC_MISSING_KEY");
        child = Process.Start(start);
    }
    static void Quit() { Check(PostMessage(ControlWindow(), 0x10, IntPtr.Zero, IntPtr.Zero), "Normal exit posted to owned controller"); Check(child.WaitForExit(12000) && child.ExitCode == 0, "Actual EXE exited normally"); child.Dispose(); child = null; }
    [STAThread] static int Main(string[] args) {
        string app = Path.GetFullPath(args[0]), notes = Path.GetFullPath(args[1]);
        string config = Path.Combine(app, "config.json");
        try {
            Check(!File.Exists(config) && !Directory.Exists(Path.Combine(app, ".venv")), "Fresh installed payload has no config or Python environment");
            Launch(app); Until(delegate { return Window("Verlog · 首次设置") != IntPtr.Zero; }, "Real Program.Main opens first-run settings");
            Click(Window("Verlog · 首次设置"), "取消"); Check(child.WaitForExit(8000) && child.ExitCode == 0, "First-run cancel exits normally"); child.Dispose(); child = null;
            Check(!File.Exists(config), "Cancel did not create config"); steps.Add("first-run-cancel");
            Launch(app); Until(delegate { return Window("Verlog · 首次设置") != IntPtr.Zero; }, "First-run settings reappears after cancel");
            var dialog = Window("Verlog · 首次设置");
            var edits = Children(dialog).Where(delegate(IntPtr item) {return Class(item).IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0;}).OrderBy(delegate(IntPtr item) {Rect rect; GetWindowRect(item, out rect); return rect.Top;}).ToList();
            Check(edits.Count == 8, "All eight configuration fields are present");
            SendMessage(edits[0], 0xC, IntPtr.Zero, notes);
            Click(dialog, "保存并启动");
            Until(delegate { return string.Join(" ", Children(dialog).Select(Text).ToArray()).Contains("保存目录不存在"); }, "New output directory has an actionable validation message");
            Check(!File.Exists(config) && Window("Verlog · 首次设置") != IntPtr.Zero, "Invalid save location keeps setup editable without config");
            Directory.CreateDirectory(notes); // User creates/selects their output folder before saving.
            Click(dialog, "保存并启动"); Until(delegate { return File.Exists(config) && ControlWindow() != IntPtr.Zero; }, "Real EXE saved config and registered controller");
            var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(config));
            Check((string)values["vault_root"] == notes && (string)values["quick_note_hotkey"] == "Ctrl+Alt+J", "Save location and default shortcut persisted"); steps.Add("first-run-save");
            Check((string)values["meeting_folder"] == "Verlog" && (string)values["transcription_mode"] == "api" && !(bool)values["auto_transcribe"], "Fresh EXE starts record-only, with Verlog output and unconfigured optional API");
            PostMessage(ControlWindow(), 0x312, new IntPtr(7414), IntPtr.Zero);
            Until(delegate { return NoteWindow() != IntPtr.Zero; }, "Registered quick-note handler opens actual native note");
            var note = NoteWindow(); Check((GetWindowLong(note, -20) & 8) != 0, "Quick note is topmost");
            IntPtr editor = Children(note).Single(delegate(IntPtr item) {return Class(item).IndexOf("RICHEDIT", StringComparison.OrdinalIgnoreCase) >= 0;});
            Until(delegate {return (GetWindowLong(editor, -16) & 0x800) == 0;}, "Native editor finished loading before input");
            string sample = "新用户速记验证\r\nEnglish input 123";
            SendMessage(editor, 0xC, IntPtr.Zero, sample);
            Until(delegate { return Directory.Exists(notes) && Directory.GetFiles(notes, "*.md", SearchOption.AllDirectories).Any(delegate(string path) {return File.ReadAllText(path).Contains("English input 123");}); }, "Actual EXE auto-saved Chinese and English Markdown");
            PostMessage(note, 0x10, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(250);
            PostMessage(ControlWindow(), 0x312, new IntPtr(7414), IntPtr.Zero);
            Until(delegate { return NoteWindow() != IntPtr.Zero; }, "Quick note reopened after hiding"); steps.Add("quick-note-autosave-topmost-reopen");
            var readback = new StringBuilder(1024); SendMessage(editor, 0xD, new IntPtr(readback.Capacity), readback);
            Check(readback.ToString().Contains("新用户速记验证") && readback.ToString().Contains("English input 123"), "Reopened native editor retained saved text");
            PostMessage(NoteWindow(), 0x10, IntPtr.Zero, IntPtr.Zero);
            // Model setup is deferred until transcription. This no-audio GUI
            // journey must not start hardware capture or assert the old blocker.
            Check(!File.Exists(Path.Combine(app, ".venv", "Scripts", "python.exe")), "Actual notes worked before Python installation");
            Check(!Directory.GetFiles(app, "session.json", SearchOption.AllDirectories).Any(), "No-audio journey did not start recording");
            steps.Add("missing-runtime-keeps-notes;hardware-capture-not-tested");
            Quit(); string before = File.ReadAllText(config);
            Launch(app); Until(delegate {return ControlWindow() != IntPtr.Zero;}, "Existing-config launch reaches controller directly");
            Check(Window("Verlog · 首次设置") == IntPtr.Zero && File.ReadAllText(config) == before, "Restart skips setup and preserves exact config"); Quit(); steps.Add("restart-preserves-config");
            var apiConfig = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(before);
            apiConfig["transcription_mode"] = "api"; apiConfig["api_base_url"] = "https://example.invalid/v1";
            apiConfig["api_model"] = "synthetic-never-called"; apiConfig["api_key_env"] = "VERLOG_REVIEW_SYNTHETIC_MISSING_KEY";
            File.WriteAllText(config, new JavaScriptSerializer().Serialize(apiConfig), new UTF8Encoding(false));
            Launch(app); Until(delegate { return ControlWindow() != IntPtr.Zero; }, "Missing API key still allows actual desktop startup");
            PostMessage(ControlWindow(), 0x312, new IntPtr(7414), IntPtr.Zero);
            Until(delegate { return NoteWindow() != IntPtr.Zero; }, "Missing API key still allows actual quick notes");
            PostMessage(NoteWindow(), 0x10, IntPtr.Zero, IntPtr.Zero);
            Check(!Directory.GetFiles(app, "session.json", SearchOption.AllDirectories).Any(), "Missing-key notes check did not initiate capture or upload");
            Quit(); File.WriteAllText(config, before, new UTF8Encoding(false)); steps.Add("missing-key-keeps-notes;hardware-capture-not-tested");
            File.WriteAllText(Path.Combine(app, "new-user-journey.json"), new JavaScriptSerializer().Serialize(new {passed=true, assertions=assertions, steps=steps, actual_exe_gui=true, bypassed_mutex=false, clipboard_access=false, global_input=false, audio_recorded=false}), new UTF8Encoding(false));
            Console.WriteLine("PASS real installed EXE journey: " + assertions + " assertions; no clipboard/audio/global input."); return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(app, "new-user-journey.json"), new JavaScriptSerializer().Serialize(new {
                passed=false, assertions=assertions, steps=steps, error=error.ToString(),
                actual_exe_gui=true, clipboard_access=false, global_input=false, audio_recorded=false
            }), new UTF8Encoding(false));
            Console.Error.WriteLine(error); return 1;
        }
        finally { if (child != null) {var control = ControlWindow(); if (control != IntPtr.Zero) PostMessage(control, 0x10, IntPtr.Zero, IntPtr.Zero); if (!child.WaitForExit(2000)) child.Kill(); child.Dispose();} }
    }
}
