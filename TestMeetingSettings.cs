using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class TestMeetingSettings {
    static int assertions;
    static void Check(bool condition, string message) {
        assertions++; if (!condition) throw new InvalidOperationException(message);
    }
    static Dictionary<string, object> Config(string root) {
        return new Dictionary<string, object> {
            { "vault_root", root }, { "meeting_folder", "会议记录" }, { "quick_note_folder", "速记" },
            { "hotkey", "Ctrl+Alt+L" }, { "pause_hotkey", "Alt+Space" },
            { "bookmark_hotkey", "Ctrl+Alt+K" }, { "quick_note_hotkey", "Ctrl+Alt+J" },
            { "api_base_url", "https://example.invalid/v1" }, { "api_model", "unchanged-model" },
            { "api_key_env", "VERBIT_SETTINGS_SYNTHETIC_KEY" }, { "asr_context", "保持未展示的模型配置" },
            { "mp3_bitrate_kbps", 192 }, { "silence_warning_seconds", 90 }, { "open_in_obsidian", false }
        };
    }
    static void Write(string path, Dictionary<string, object> config) {
        File.WriteAllText(path, MeetingFiles.Json.Serialize(config), new UTF8Encoding(false));
    }
    static Dictionary<string, object> Candidate(MeetingSettingsSnapshot snapshot, string root) {
        return snapshot.CreateCandidate(root, "会议/新会议", "便笺", "Ctrl+Shift+F16", "Alt+F17", "Ctrl+F18", "Alt+Shift+F19");
    }
    static bool Reject(Action action) {
        try { action(); return false; } catch (InvalidOperationException) { return true; } catch (IOException) { return true; }
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, ref NativeRect rectangle);
    sealed class ReservedKey : IDisposable {
        readonly Thread thread;
        readonly ManualResetEvent ready = new ManualResetEvent(false), done = new ManualResetEvent(false);
        Exception failure;
        public ReservedKey(string text) {
            thread = new Thread(delegate() {
                NativeWindow window = null; bool registered = false;
                try {
                    window = new NativeWindow(); window.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
                    uint modifiers, key; MeetingConfig.ParseHotkey(text, out modifiers, out key);
                    registered = RegisterHotKey(window.Handle, 28123, modifiers | 0x4000, key);
                    if (!registered) throw new InvalidOperationException("Could not reserve synthetic key: " + Marshal.GetLastWin32Error());
                } catch (Exception error) { failure = error; }
                finally { ready.Set(); }
                try { done.WaitOne(); }
                finally { if (registered) UnregisterHotKey(window.Handle, 28123); if (window != null) window.DestroyHandle(); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.WaitOne();
            if (failure != null) { Dispose(); throw failure; }
        }
        public void Dispose() { done.Set(); thread.Join(); ready.Dispose(); done.Dispose(); }
    }
    static object Field(object target, string name) {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }
    static object Call(object target, string name, params object[] arguments) {
        try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void Receive(Func<bool> condition) {
        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
        Check(condition(), "Targeted hotkey message must dispatch through the real controller handle");
    }
    static void VerifyActive(MeetingHotkey hotkey, Dictionary<string, object> expected) {
        foreach (string field in new[] { "hotkey", "quick_note_hotkey" })
            Check(Reject(delegate { using (var blocked = new ReservedKey((string)expected[field])) { } }), "Live shortcut stays reserved: " + field);
        Check(hotkey.QuickNoteRegistered && hotkey.QuickNoteWarning.Length == 0, "Quick-note binding remains active without stale warning");
        hotkey.EnablePause(true);
        Check(hotkey.PauseRegistered && hotkey.BookmarkRegistered, "Configured recording-only shortcuts can register after save or rollback");
        hotkey.EnablePause(false);
    }
    static void ContextIntegration(string root, string nextRoot) {
        // The binary is compiled into a fresh validation folder. This guard
        // prevents accidental source-directory execution from opening config.
        Check(MeetingFiles.Home.IndexOf("\\validation\\", StringComparison.OrdinalIgnoreCase) >= 0, "Context fixture must be in the isolated validation directory");
        string path = Path.Combine(MeetingFiles.Home, "config.json");
        var config = Config(root);
        config["hotkey"] = "Ctrl+Alt+Shift+F21"; config["pause_hotkey"] = "Ctrl+Alt+Shift+F22";
        config["bookmark_hotkey"] = "Ctrl+Alt+Shift+F23"; config["quick_note_hotkey"] = "Ctrl+Alt+Shift+F24";
        config["show_processing_window"] = false;
        Write(path, config);
        using (var context = new MeetingContext(config, delegate { throw new Exception("Settings must not launch a worker"); }, delegate { throw new Exception("Settings must not open a user application"); })) {
            ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
            var hotkey = (MeetingHotkey)Field(context, "hotkey");
            var previous = new Dictionary<string, object>((Dictionary<string, object>)Field(context, "config"));
            var snapshot = MeetingSettingsSnapshot.Open(path);
            var candidate = snapshot.CreateCandidate(nextRoot, "会议", "便笺", "Ctrl+Alt+Shift+F13", "Ctrl+Alt+Shift+F14", "Ctrl+Alt+Shift+F15", "Ctrl+Alt+Shift+F16");
            byte[] original = File.ReadAllBytes(path);
            foreach (string blockedField in new[] { "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" }) {
                using (var occupied = new ReservedKey((string)candidate[blockedField])) {
                    string error = (string)Call(context, "ApplySettings", snapshot, candidate);
                    Check(error != null && error.Contains("1409"), "Actual settings apply reports occupied shortcut: " + blockedField);
                }
                Check(original.SequenceEqual(File.ReadAllBytes(path)), "Shortcut conflict does not save configuration: " + blockedField);
                Check((string)((Dictionary<string, object>)Field(context, "config"))["vault_root"] == root, "Failed rebind does not switch live storage");
                VerifyActive(hotkey, previous);
            }
            var external = Config(root); external["asr_context"] = "externally edited after settings opened"; Write(path, external);
            byte[] externalBytes = File.ReadAllBytes(path);
            string saveFailure = (string)Call(context, "ApplySettings", snapshot, candidate);
            Check(!string.IsNullOrEmpty(saveFailure) && saveFailure.Contains("其他程序"), "Persistence conflict reaches the settings callback");
            Check(externalBytes.SequenceEqual(File.ReadAllBytes(path)), "Save failure retains the external file byte-exact");
            VerifyActive(hotkey, previous);
            foreach (string field in new[] { "hotkey", "quick_note_hotkey" })
                using (var released = new ReservedKey((string)candidate[field])) { Check(true, "Failed-save candidate shortcut is released"); }
            Write(path, previous); snapshot = MeetingSettingsSnapshot.Open(path);
            byte[] beforeBusy = File.ReadAllBytes(path);
            var jobs = (List<TranscriptionJob>)Field(context, "jobs"); jobs.Add(new TranscriptionJob());
            Check(!string.IsNullOrEmpty((string)Call(context, "ApplySettings", snapshot, candidate)), "Active background job blocks apply before any rebind");
            jobs.Clear();
            Check(beforeBusy.SequenceEqual(File.ReadAllBytes(path)), "Busy apply preserves configuration");
            Call(context, "ToggleQuickNote");
            var note = (QuickNoteWindow)Field(context, "quickNote");
            var editor = (QuickNoteInputBox)Field(note, "editor");
            Receive(delegate { return !editor.ReadOnly; });
            string originalNote = (string)Field(note, "notePath");
            typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(note, true);
            Check(!string.IsNullOrEmpty((string)Call(context, "ApplySettings", snapshot, candidate)), "IME composition blocks settings before closing the note or rebinding");
            Check(!note.IsDisposed && beforeBusy.SequenceEqual(File.ReadAllBytes(path)), "Composing note remains open and settings unchanged");
            typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(note, false);
            editor.SelectedText = "synthetic note saved before changing destination";
            Check(!string.IsNullOrEmpty((string)Call(context, "ApplySettings", snapshot, candidate)), "Dirty note asks for retry while asynchronous flush is pending");
            Receive(delegate { return note.IsDisposed; });
            Check(originalNote.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.ReadAllText(originalNote).Contains("synthetic note saved before changing destination"), "Settings flush dirty text to the previous destination before switching roots");
            string applied = (string)Call(context, "ApplySettings", snapshot, candidate);
            Check(applied == null, "Valid idle settings apply succeeds without restart");
            var active = (Dictionary<string, object>)Field(context, "config");
            Check((string)active["vault_root"] == nextRoot && (string)active["quick_note_folder"] == "便笺", "Actual controller uses the new storage configuration");
            foreach (string field in new[] { "hotkey", "quick_note_hotkey" })
                using (var released = new ReservedKey((string)previous[field])) { Check(true, "Successful settings apply releases the previous shortcut"); }
            VerifyActive(hotkey, candidate);
            Call(context, "ToggleQuickNote");
            var nextNote = (QuickNoteWindow)Field(context, "quickNote");
            var nextEditor = (QuickNoteInputBox)Field(nextNote, "editor");
            Receive(delegate { return !nextEditor.ReadOnly; });
            Check(((string)Field(nextNote, "notePath")).StartsWith(nextRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Reopened note uses the new destination without moving the previous note");
            Check(File.Exists(originalNote), "Switching roots never deletes the old note");
            Check(nextNote.FlushAndClose(), "Clean replacement note closes without pending save");
            // Remove only this fixture's recording/open-note handlers, then
            // dispatch WM_HOTKEY to the private message window. No key injection.
            hotkey.Pressed -= (Action)Delegate.CreateDelegate(typeof(Action), context, "Toggle");
            hotkey.QuickNotePressed -= (Action)Delegate.CreateDelegate(typeof(Action), context, "ToggleQuickNote");
            int start = 0, quick = 0;
            hotkey.Pressed += delegate { start++; }; hotkey.QuickNotePressed += delegate { quick++; };
            Check(PostMessage(hotkey.Handle, 0x312, new IntPtr(7411), IntPtr.Zero), "Post targeted start/stop event after rebind");
            Check(PostMessage(hotkey.Handle, 0x312, new IntPtr(7414), IntPtr.Zero), "Post targeted quick-note event after rebind");
            Receive(delegate { return start == 1 && quick == 1; });
            var tray = (NotifyIcon)Field(context, "tray");
            Check(tray.ContextMenuStrip.Items["quickNote"].Text.Contains((string)candidate["quick_note_hotkey"]), "Actual tray menu displays the new shortcut");
            Check(MeetingSettingsSnapshot.Open(path).Values.Keys.All(delegate(string key) { return Equals(active[key], MeetingSettingsSnapshot.Open(path).Values[key]); }), "Live settings and persisted settings agree after success");
            Call(context, "ExitThreadCore");
        }
    }
    static void Persistence(string directory, string root, string nextRoot) {
        string path = Path.Combine(directory, "synthetic-settings.json");
        Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path);
        var before = snapshot.Values;
        var candidate = Candidate(snapshot, nextRoot);
        foreach (string key in before.Keys) {
            if (new[] { "vault_root", "meeting_folder", "quick_note_folder", "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" }.Contains(key)) continue;
            Check(Equals(before[key], candidate[key]), "Unexposed field must survive candidate creation: " + key);
        }
        var isolated = snapshot.Values; isolated["api_model"] = "modified-copy";
        Check((string)snapshot.Values["api_model"] == "unchanged-model", "A returned dictionary cannot change the edit snapshot");
        snapshot.Commit(candidate);
        byte[] bytes = File.ReadAllBytes(path);
        Check(!(bytes.Length > 2 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf), "Configuration stays UTF-8 without BOM");
        Check(File.ReadAllText(path).Contains("\r\n  \""), "Settings preserve a readable indented configuration");
        var saved = MeetingSettingsSnapshot.Open(path).Values;
        foreach (string key in candidate.Keys) Check(Equals(candidate[key], saved[key]), "Saved setting round trip: " + key);
        Check(!Directory.Exists(Path.Combine(nextRoot, "会议", "新会议")), "Validation and saving do not create or migrate user folders");
        Check(Directory.GetFiles(directory, "*.settings-*").Length == 0, "Successful commit leaves no settings temp or backup files");
        snapshot.Commit(Candidate(snapshot, root));
        Check((string)MeetingSettingsSnapshot.Open(path).Values["vault_root"] == root, "Successful snapshot may safely commit again");

        snapshot = MeetingSettingsSnapshot.Open(path);
        var external = Config(root); external["asr_context"] = "another writer's update"; Write(path, external);
        byte[] externalBytes = File.ReadAllBytes(path);
        Check(Reject(delegate { snapshot.Commit(Candidate(snapshot, nextRoot)); }), "Stale settings reject an external edit");
        Check(externalBytes.SequenceEqual(File.ReadAllBytes(path)), "An external configuration edit remains byte-exact");
        Check(Directory.GetFiles(directory, "*.settings-*").Length == 0, "A pre-write conflict creates no temp or backup");

        Write(path, Config(root)); snapshot = MeetingSettingsSnapshot.Open(path);
        var invalid = Candidate(snapshot, root); invalid["hotkey"] = invalid["quick_note_hotkey"];
        byte[] valid = File.ReadAllBytes(path);
        Check(Reject(delegate { snapshot.Commit(invalid); }), "Persistence independently rejects duplicate shortcuts");
        Check(valid.SequenceEqual(File.ReadAllBytes(path)), "Invalid settings leave the file byte-exact");
        Check(Reject(delegate { snapshot.CreateCandidate(root, "../escape", "便笺", "Ctrl+Alt+L", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J"); }), "Folder traversal rejected");
        Check(Reject(delegate { snapshot.CreateCandidate(Path.Combine(root, "missing"), "会议", "便笺", "Ctrl+Alt+L", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J"); }), "Missing root rejected without creation");
        Check(Reject(delegate { snapshot.CreateCandidate(root, "会议", "便笺", "ctrl+alt+l", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J"); }), "Malformed shortcut rejected");
        var unknown = Config(root); unknown["future_field"] = "do not silently erase"; Write(path, unknown);
        byte[] unknownBytes = File.ReadAllBytes(path);
        Check(Reject(delegate { MeetingSettingsSnapshot.Open(path); }), "Unknown schema keys fail safely");
        Check(unknownBytes.SequenceEqual(File.ReadAllBytes(path)), "Unknown original data stays byte-exact");
        var api = Config(root); api["transcription_mode"] = "api"; Write(path, api);
        string previousKey = Environment.GetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY");
        Environment.SetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY", null);
        try {
            Check(Reject(delegate { MeetingSettingsSnapshot.Open(path); }), "Existing API configuration still requires its environment key");
            Environment.SetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY", "synthetic-test-only");
            snapshot = MeetingSettingsSnapshot.Open(path); snapshot.Commit(Candidate(snapshot, nextRoot));
            Check((string)MeetingSettingsSnapshot.Open(path).Values["api_key_env"] == "VERBIT_SETTINGS_SYNTHETIC_KEY", "API secret variable reference survives settings");
            Check(!File.ReadAllText(path).Contains("synthetic-test-only"), "Saving never embeds the environment secret");
        } finally { Environment.SetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY", previousKey); }
        byte[] invalidEncoding = new byte[] { 0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d };
        File.WriteAllBytes(path, invalidEncoding); bool invalidUtf8 = false;
        try { MeetingSettingsSnapshot.Open(path); } catch (DecoderFallbackException) { invalidUtf8 = true; }
        Check(invalidUtf8 && invalidEncoding.SequenceEqual(File.ReadAllBytes(path)), "Invalid UTF-8 is rejected without silently replacing original bytes");
    }
    static TextBox Input(Form form, string name) {
        return (TextBox)form.Controls.Find(name, true)[0];
    }
    static void CheckLayout(Form form, string screenshot) {
        Application.DoEvents();
        foreach (Control input in form.Controls[0].Controls) {
            Check(form.Controls[0].ClientRectangle.Contains(input.Bounds), "Every settings row fits the client area at " + form.DeviceDpi + " DPI: " + input.Text);
            if (input is Label && !string.IsNullOrEmpty(input.Text)) {
                Size needed = TextRenderer.MeasureText(input.Text, input.Font, new Size(input.Width, int.MaxValue), TextFormatFlags.WordBreak);
                Check(needed.Height <= input.Height, "Settings label is not vertically clipped at " + form.DeviceDpi + " DPI: " + input.Text);
            }
        }
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(screenshot); }
    }
    static void Interface(string directory, string root) {
        string path = Path.Combine(directory, "synthetic-dialog.json"); Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path); int calls = 0;
        using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> value) { calls++; return "测试：快捷键被占用，请更换"; })) {
            form.Show(); Application.DoEvents();
            Check(form.Text == "片言 · 设置", "Dialog displays the new product name");
            Check(form.TopMost && !form.ShowInTaskbar && form.FormBorderStyle == FormBorderStyle.FixedDialog, "Settings remain a lightweight native tool window");
            foreach (string key in new[] { "vault_root", "meeting_folder", "quick_note_folder", "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" })
                Check(Input(form, key).Visible && Input(form, key).AccessibleName.Length > 0, "Editable and labeled setting: " + key);
            CheckLayout(form, Path.Combine(directory, "settings-dialog.png"));
            int dpi = 144;
            double scale = (double)dpi / form.DeviceDpi;
            var bounds = new NativeRect { Left = form.Left, Top = form.Top, Right = form.Left + (int)Math.Round(form.Width * scale), Bottom = form.Top + (int)Math.Round(form.Height * scale) };
            SendMessage(form.Handle, 0x02e0, new IntPtr((dpi << 16) | dpi), ref bounds);
            Application.DoEvents();
            Check(form.DeviceDpi == dpi, "Private settings window receives a real 144 DPI change message");
            CheckLayout(form, Path.Combine(directory, "settings-dialog-144.png"));
            Input(form, "hotkey").Text = "invalid";
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 0 && form.Visible, "Invalid field does not reach the parent or close the form");
            Input(form, "hotkey").Text = "Ctrl+Alt+L";
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 1 && form.Visible && ((Button)form.AcceptButton).Enabled, "Parent conflict remains editable and can be retried");
            ((Button)form.CancelButton).PerformClick();
            Check(calls == 1 && !form.Visible, "Cancel never applies the edited fields");
        }
        snapshot = MeetingSettingsSnapshot.Open(path);
        using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> value) { snapshot.Commit(value); calls++; return null; })) {
            form.Show(); Application.DoEvents(); Input(form, "quick_note_folder").Text = "便笺";
            ((Button)form.AcceptButton).PerformClick();
            Check(!form.Visible && (string)MeetingSettingsSnapshot.Open(path).Values["quick_note_folder"] == "便笺", "Successful callback persists and closes");
        }
    }
    [STAThread]
    static int Main() {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-fixture");
            string root = Path.Combine(directory, "notes"), nextRoot = Path.Combine(directory, "other-notes");
            Directory.CreateDirectory(root); Directory.CreateDirectory(nextRoot);
            Persistence(directory, root, nextRoot); Interface(directory, root); ContextIntegration(root, nextRoot);
            Console.WriteLine("PASS TestMeetingSettings: " + assertions + " assertions; synthetic configs and isolated native settings only; no user config, clipboard, audio or global input.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
