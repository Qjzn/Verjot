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
    static void RecognitionConfig(string root) {
        var config = Config(root); config["transcription_mode"] = "local"; // Legacy vocabulary validation, no engine load.
        config["asr_terms"] = "挚达，BOM\n挚达;OA";
        config["speaker_anchor_mode"] = "clean"; config["asr_segmentation"] = "speech";
        Check((string)MeetingConfig.Validate(config)["asr_terms"] == (string)config["asr_terms"], "Explicit terms and candidate modes are accepted without rewriting vocabulary");
        config["asr_terms"] = new string('字', 65);
        Check(Reject(delegate { MeetingConfig.Validate(config); }), "Overlong term is rejected natively");
        config["asr_terms"] = "BOM"; config["asr_context"] = new string('字', 2000);
        Check(Reject(delegate { MeetingConfig.Validate(config); }), "Combined context bound is checked before recording");
        config["asr_context"] = string.Concat(Enumerable.Repeat("\U0001F600", 1000));
        Check(MeetingConfig.Validate(config) != null, "Native context limits count Unicode characters like Python");
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
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
            string guideFolder = Path.Combine(MeetingFiles.Home, "documentation"); Directory.CreateDirectory(guideFolder);
            File.WriteAllText(Path.Combine(guideFolder, "first-use.md"), "# 首次使用\n隔离说明夹具", new UTF8Encoding(false));
            Call(context, "OpenFirstUseGuide");
            using (var reader = Application.OpenForms.OfType<Form>().Single(delegate(Form form) { return form.Text == "verbit · 离线说明"; })) {
                Check(((TextBox)reader.Controls["document_text"]).ReadOnly, "Tray first-use guide uses the internal reader without a Markdown association");
                reader.Close();
            }
            var hotkey = (MeetingHotkey)Field(context, "hotkey");
            Call(context, "OpenSettings", false); Application.DoEvents();
            var keyDialog = (MeetingSettingsDialog)Field(context, "settingsDialog");
            var liveKey = (HotkeyInput)Input(keyDialog, "hotkey"); liveKey.Focus(); liveKey.BeginCapture();
            Check(!hotkey.QuickNoteRegistered && Field(context, "recording") == null, "Real settings capture suspends owned shortcuts without starting audio");
            using (var released = new ReservedKey((string)config["hotkey"])) Check(true, "The actual settings callback releases the currently assigned combination");
            liveKey.CaptureKey(Keys.Control | Keys.Alt | Keys.Shift | Keys.F21); Call(liveKey, "OnKeyUp", new KeyEventArgs(Keys.F21));
            Check(hotkey.QuickNoteRegistered && liveKey.Text == (string)config["hotkey"], "Real key release restores the active configuration before settings is saved");
            ((Button)keyDialog.CancelButton).PerformClick();
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
            Check(((string)Call(context, "BeginUpdate", null, "unused.exe", "unused")).Contains("转写"), "Active transcription defers an update before accessing or executing an installer");
            Check(!string.IsNullOrEmpty((string)Call(context, "ApplySettings", snapshot, candidate)), "Active background job blocks apply before any rebind");
            jobs.Clear();
            Check(beforeBusy.SequenceEqual(File.ReadAllBytes(path)), "Busy apply preserves configuration");
            Call(context, "ToggleQuickNote");
            var note = (QuickNoteWindow)Field(context, "quickNote");
            var editor = (QuickNoteInputBox)Field(note, "editor");
            Receive(delegate { return !editor.ReadOnly; });
            string originalNote = (string)Field(note, "notePath");
            typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(note, true);
            Check(((string)Call(context, "BeginUpdate", null, "unused.exe", "unused")).Contains("输入法"), "An update cannot discard an unfinished IME composition");
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
            Check(MeetingSettingsSnapshot.Open(path) != null, "Missing API credentials must not prevent opening repair settings");
            Check(Reject(delegate { MeetingConfig.Validate(ConfigWithApi(root)); }), "Transcription validation requires an API key");
            snapshot = MeetingSettingsSnapshot.Open(path); snapshot.Commit(Candidate(snapshot, nextRoot));
            Check((string)MeetingSettingsSnapshot.Open(path).Values["transcription_mode"] == "api", "Settings can be saved before an API key is installed");
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
    static void RecordingProtection(string directory, string root) {
        string path = Path.Combine(directory, "synthetic-protection.json"); Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path);
        Check(Convert.ToInt32(snapshot.Values["recording_protection_seconds"]) == 5, "An existing config without protection defaults to five seconds");
        var candidate = snapshot.CreateCandidate(root, "会议记录", "速记", "Ctrl+Alt+L", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J", " 12 ");
        Check(Convert.ToInt32(candidate["recording_protection_seconds"]) == 12, "Protection text becomes an integer and permits surrounding whitespace");
        foreach (string key in snapshot.Values.Keys)
            if (key != "recording_protection_seconds") Check(Equals(snapshot.Values[key], candidate[key]), "Editing protection preserves every other model and storage setting: " + key);
        snapshot.Commit(candidate);
        snapshot = MeetingSettingsSnapshot.Open(path);
        Check(Convert.ToInt32(Candidate(snapshot, root)["recording_protection_seconds"]) == 12, "Existing seven-argument callers retain an explicitly configured protection value");
        foreach (int value in new[] { 0, 300 }) {
            candidate = snapshot.CreateCandidate(root, "会议记录", "速记", "Ctrl+Alt+L", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J", value.ToString());
            snapshot.Commit(candidate);
            Check(Convert.ToInt32(MeetingSettingsSnapshot.Open(path).Values["recording_protection_seconds"]) == value, "Protection boundary saves and reloads: " + value);
        }
        byte[] before = File.ReadAllBytes(path);
        foreach (string invalid in new[] { "", "-1", "301", "1.5", "five", "99999999999999999999" }) {
            string message = null;
            try { snapshot.CreateCandidate(root, "会议记录", "速记", "Ctrl+Alt+L", "Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J", invalid); }
            catch (InvalidOperationException error) { message = error.Message; }
            Check(message != null && message.Contains("0 到 300") && message.Contains("整数"), "Invalid protection has an actionable validation message: " + invalid);
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "Invalid protection does not touch the config: " + invalid);
        }
        candidate = Candidate(snapshot, root); candidate["recording_protection_seconds"] = -1;
        Check(Reject(delegate { snapshot.Commit(candidate); }), "Persistence independently validates the protection range");
        Check(before.SequenceEqual(File.ReadAllBytes(path)), "Rejected protection commit leaves all original bytes intact");
    }
    static TextBox Input(Form form, string name) {
        return (TextBox)form.Controls.Find(name, true)[0];
    }
    static void FirstRun(string directory, string root) {
        string path = Path.Combine(directory, "first-run-config.json");
        var snapshot = MeetingSettingsSnapshot.CreateNew(path, root);
        Check(!File.Exists(path), "Preparing first-run settings does not create a config");
        Check(!(bool)snapshot.Values["open_in_obsidian"] && (string)snapshot.Values["transcription_mode"] == "api" && !(bool)snapshot.Values["auto_transcribe"], "New installs use ordinary notes and record only without connecting to a service");
        Check((string)snapshot.Values["meeting_folder"] == "verbit", "New installs save meetings under verbit; old configurations retain their folders");
        using(var form = new MeetingSettingsDialog(snapshot, delegate { return null; }, true)) {
            form.Show();form.ShowTranscription();Application.DoEvents();
            Check(form.Controls.Find("download_recommended_model",true).Length==0,"First-run settings contain no model download entry");
            ((CheckBox)form.Controls.Find("auto_transcribe",true)[0]).Checked=true;Application.DoEvents();
            Check(((ComboBox)form.Controls.Find("transcription_mode",true)[0]).Items.Count==1 && Input(form,"api_model").Visible,"New user can directly configure their third-party model without a dead local option");
            using(var image=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(Path.Combine(directory,"settings-api-first-use.png"));}
            form.Close();
        }
        Check((string)snapshot.Values["quick_note_hotkey"] == "Ctrl+Alt+J" && Convert.ToInt32(snapshot.Values["recording_protection_seconds"]) == 5, "First-run defaults include current quick note and protection settings");
        Check((string)MeetingSettingsSnapshot.CreateNew(path, "").Values["vault_root"] == "", "An unavailable suggested folder does not prevent the first-run chooser from opening");
        var invalid = snapshot.Values; invalid["vault_root"] = Path.Combine(root, "missing");
        Check(Reject(delegate { snapshot.Commit(invalid); }) && !File.Exists(path), "Invalid first-run settings do not create a config or directory");
        for (int round = 0; round < 2; round++) {
            int currentRound = round; bool appeared = false; Exception failure = null;
            EventHandler idle = null;
            idle = delegate {
                var form = Application.OpenForms.OfType<MeetingSettingsDialog>().FirstOrDefault();
                if (form == null) return;
                Application.Idle -= idle;
                try {
                    appeared = true;
                    Check(form.Text == "verbit · 首次设置", "Missing config opens the real first-run settings dialog");
                    Check(!File.Exists(path), "First-run config remains absent until Save succeeds");
                    Check(((Button)form.AcceptButton).Text == "保存并启动", "First-run action explains startup after saving without starting a recording");
                    CheckLayout(form, Path.Combine(directory, "first-run-dialog-" + currentRound + ".png"));
                    if (currentRound == 0) ((Button)form.CancelButton).PerformClick();
                    else {
                        Input(form, "quick_note_folder").Text = "首次便笺";
                        string[] fields = { "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" };
                        for (int index = 0; index < fields.Length; index++) Input(form, fields[index]).Text = "Ctrl+Alt+Shift+F" + (13 + index);
                        foreach (string field in fields) {
                            using (var occupied = new ReservedKey(Input(form, field).Text)) {
                                ((Button)form.AcceptButton).PerformClick();
                                Check(form.Visible && !File.Exists(path) && ((Label)Field(form, "error")).Text.Contains("1409"), "First-run shortcut conflict stays editable without saving: " + field);
                            }
                            foreach (string key in fields) using (var released = new ReservedKey(Input(form, key).Text))
                                Check(true, "First-run failed probe releases all its temporary shortcuts: " + key);
                        }
                        ((Button)form.AcceptButton).PerformClick();
                        foreach (string key in fields) using (var released = new ReservedKey(Input(form, key).Text))
                            Check(true, "Successful first-run probe releases its temporary shortcuts before controller startup: " + key);
                    }
                } catch (Exception error) { failure = error; form.Close(); }
            };
            bool configured;
            Application.Idle += idle;
            try { configured = MeetingFirstRunSetup.Run(path, root); }
            finally { Application.Idle -= idle; }
            if (failure != null) throw failure;
            Check(appeared && configured == (round == 1), "Cancel exits setup and next startup presents it again; Save succeeds");
            Check(File.Exists(path) == (round == 1), "Only a successful first-run save creates config.json");
        }
        Check((string)MeetingSettingsSnapshot.Open(path).Values["quick_note_folder"] == "首次便笺", "First-run save persists the actual selected fields");
        byte[] saved = File.ReadAllBytes(path);
        Check(MeetingFirstRunSetup.Run(path, "invalid-unused-root") && saved.SequenceEqual(File.ReadAllBytes(path)), "Existing config skips setup without rewriting or even validating new defaults");
        Check(Reject(delegate { MeetingSettingsSnapshot.CreateNew(path, root); }), "New-config factory cannot replace an existing config");
        string raced = Path.Combine(directory, "first-run-race.json");
        var pending = MeetingSettingsSnapshot.CreateNew(raced, root);
        Write(raced, Config(root)); byte[] concurrent = File.ReadAllBytes(raced);
        Check(Reject(delegate { pending.Commit(pending.Values); }) && concurrent.SequenceEqual(File.ReadAllBytes(raced)), "Concurrent config creation is preserved byte-exact and rejects first-run save");
        Check(Directory.GetFiles(directory, "*.settings-*").Length == 0, "First-run save and conflict leave no temporary files");
        string runtime = Path.Combine(directory, "synthetic-runtime"); Directory.CreateDirectory(runtime);
        Check(Reject(delegate { MeetingConfig.CheckRecordingRuntime(runtime); }), "Missing runtime is rejected before transcription");
        Check(MeetingConfig.Validate(Config(root), false) != null, "Recording configuration needs neither Python nor model files");
        Check(MeetingConfig.LocalRuntimeStatus(runtime).Contains("未就绪"), "Settings explicitly shows missing local runtime");
        File.WriteAllText(Path.Combine(runtime, "meeting_worker.py"), "synthetic; never executed");
        Check(Reject(delegate { MeetingConfig.CheckRecordingRuntime(runtime); }), "Worker alone cannot pass runtime preflight");
        Directory.CreateDirectory(Path.Combine(runtime, ".venv", "Scripts"));
        File.WriteAllText(Path.Combine(runtime, ".venv", "Scripts", "python.exe"), "synthetic; never executed");
        MeetingConfig.CheckRecordingRuntime(runtime); Check(true, "Runtime preflight checks file presence without executing Python or loading models");
        Check(Reject(delegate { MeetingConfig.CheckTranscriptionRuntime(runtime, new Dictionary<string,object> { {"transcription_mode","local"} }); }), "Python alone does not make local models ready");
        foreach (string name in new[] { "segmentation/model.onnx", "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            "Qwen3-ASR-1.7B-hf/config.json", "Qwen3-ASR-1.7B-hf/model.safetensors", "Qwen3-ASR-1.7B-hf/processor_config.json",
            "Qwen3-ASR-1.7B-hf/tokenizer_config.json", "Qwen3-ASR-1.7B-hf/tokenizer.json" }) {
            string file = Path.Combine(runtime, "models", name); Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "synthetic; never loaded");
        }
        MeetingConfig.CheckTranscriptionRuntime(runtime, new Dictionary<string,object> { {"transcription_mode","local"} });
        Check(MeetingConfig.LocalRuntimeStatus(runtime).Contains("文件已就绪"), "Ready file layout is detected without importing ML libraries");
        File.WriteAllText(Path.Combine(runtime, "models", "Qwen3-ASR-1.7B-hf", "model.safetensors"), "");
        Check(Reject(delegate { MeetingConfig.CheckTranscriptionRuntime(runtime, new Dictionary<string,object> { {"transcription_mode","local"} }); }), "An incomplete empty ASR weight is not treated as ready");
        MeetingConfig.CheckTranscriptionRuntime(runtime, ConfigWithApi(root));
        Check(true, "API mode requires speaker files but does not require Qwen weights");
    }
    static void CheckChildren(Control parent, int dpi) {
        foreach (Control input in parent.Controls) {
            if (!input.Visible) continue;
            var scroll = parent as ScrollableControl;
            if (!(parent is TabControl) && (scroll == null || !scroll.AutoScroll))
                Check(parent.ClientRectangle.Contains(input.Bounds), "Visible control fits its container at " + dpi + " DPI: " + input.GetType().Name + " " + input.Text + "; " + input.Bounds + " inside " + parent.ClientRectangle);
            if (input is Label && !string.IsNullOrEmpty(input.Text)) {
                Size needed = TextRenderer.MeasureText(input.Text, input.Font, new Size(input.Width - input.Padding.Horizontal, int.MaxValue), TextFormatFlags.WordBreak);
                Check(needed.Height + input.Padding.Vertical <= input.Height, "Settings label is not vertically clipped at " + dpi + " DPI: " + input.Text + "; needed=" + (needed.Height + input.Padding.Vertical) + "; actual=" + input.Height);
            }
            CheckChildren(input, dpi);
        }
    }
    static void CheckLayout(Form form, string screenshot) {
        form.Refresh(); Application.DoEvents(); CheckChildren(form, form.DeviceDpi);
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(screenshot); }
    }
    static void Interface(string directory, string root) {
        string path = Path.Combine(directory, "synthetic-dialog.json"); Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path); int calls = 0;
        using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> value) { calls++; return "测试：快捷键被占用，请更换"; })) {
            form.Show(); Application.DoEvents();
            Check(form.Text == "verbit · 设置", "Dialog displays the new product name");
            Check(!form.HasPendingChanges, "Fresh settings do not block the updater with a false unsaved-change warning");
            Check(form.TopMost && !form.ShowInTaskbar && form.FormBorderStyle == FormBorderStyle.FixedDialog, "Settings remain a lightweight native tool window");
            foreach (string key in new[] { "vault_root", "meeting_folder", "quick_note_folder", "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey", "recording_protection_seconds" })
                Check(Input(form, key).Visible && Input(form, key).AccessibleName.Length > 0, "Editable and labeled setting: " + key);
            Check(Input(form, "recording_protection_seconds").Text == "5", "Protection input displays the legacy-config default");
            CheckLayout(form, Path.Combine(directory, "settings-dialog.png"));
            Input(form, "hotkey").Text = "invalid";
            Check(form.HasPendingChanges, "Unsaved settings are detectable before starting an installation");
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 0 && form.Visible, "Invalid field does not reach the parent or close the form");
            Input(form, "hotkey").Text = "Ctrl+Alt+L";
            Input(form, "recording_protection_seconds").Text = "301";
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 0 && form.Visible && ((Label)Field(form, "error")).Text.Contains("0 到 300"), "Invalid protection remains editable without reaching apply");
            Input(form, "recording_protection_seconds").Text = "5";
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 1 && form.Visible && ((Button)form.AcceptButton).Enabled, "Parent conflict remains editable and can be retried");
            ((Button)form.CancelButton).PerformClick();
            Check(calls == 1 && !form.Visible, "Cancel never applies the edited fields");
        }
        snapshot = MeetingSettingsSnapshot.Open(path);
        using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> value) { snapshot.Commit(value); calls++; return null; })) {
            form.Show(); Application.DoEvents(); Input(form, "quick_note_folder").Text = "便笺";
            Input(form, "recording_protection_seconds").Text = "9";
            ((Button)form.AcceptButton).PerformClick();
            Check(!form.Visible && (string)MeetingSettingsSnapshot.Open(path).Values["quick_note_folder"] == "便笺", "Successful callback persists and closes");
            Check(Convert.ToInt32(MeetingSettingsSnapshot.Open(path).Values["recording_protection_seconds"]) == 9, "Protection edited through the native dialog is persisted");
        }
    }
    static void MonitorLayouts(string directory, string root) {
        string path = Path.Combine(directory, "synthetic-monitor-layout.json"); Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path); int index = 0;
        foreach (Screen screen in Screen.AllScreens) foreach (bool firstRun in new[] { false, true }) {
            using (var form = new MeetingSettingsDialog(snapshot, delegate { throw new InvalidOperationException("Layout fixture must not apply settings"); }, firstRun)) {
                // Real monitor placement lets Windows supply both client and
                // non-client DPI; synthetic WM_DPICHANGED cannot do the latter.
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(screen.WorkingArea.Left + 20, screen.WorkingArea.Top + 20);
                form.Show(); Application.DoEvents();
                Check(Screen.FromControl(form).DeviceName == screen.DeviceName, "Settings layout fixture opens on the intended monitor");
                Check(screen.WorkingArea.Contains(form.Bounds), "Settings, including save/cancel, fit the actual monitor work area");
                Check(form.ClientSize.Width <= 501 * form.DeviceDpi / 96 && form.ClientSize.Height <= 481 * form.DeviceDpi / 96, "Settings default stays within the compact 500 x 480 logical footprint");
                var defaultTabs = (TabControl)form.Controls.Find("settingsTabs", true)[0];
                int rightGap = defaultTabs.ClientSize.Width - defaultTabs.GetTabRect(defaultTabs.TabCount - 1).Right;
                Check(rightGap >= 0 && rightGap <= 4, "Settings tabs fill the available header width, leaving only the native border: " + form.DeviceDpi + " DPI, gap=" + rightGap);
                if (form.ClientSize.Height >= 476 * form.DeviceDpi / 96)
                    Check(!defaultTabs.TabPages[0].VerticalScroll.Visible, "General settings fit without scrolling at the default compact size: " + form.DeviceDpi + " DPI, page=" + defaultTabs.TabPages[0].ClientSize + ", content=" + defaultTabs.TabPages[0].Controls[0].Bounds);
                CheckLayout(form, Path.Combine(directory, "settings-monitor-" + index++ + "-dpi-" + form.DeviceDpi + (firstRun ? "-first-run" : "") + ".png"));
                Console.WriteLine("Settings layout: " + screen.DeviceName + ", " + form.DeviceDpi + " DPI, first-run " + firstRun + ", client " + form.ClientSize);
                var smallWork = new Rectangle(screen.WorkingArea.Left, screen.WorkingArea.Top, 800, 540);
                typeof(MeetingSettingsDialog).GetMethod("FitToWorkArea", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { smallWork });
                Application.DoEvents();
                Check(smallWork.Contains(form.Bounds), "A short work area keeps the complete settings window visible");
                var tabs = (TabControl)form.Controls.Find("settingsTabs", true)[0];
                foreach (TabPage page in tabs.TabPages) {
                    tabs.SelectedTab = page; Application.DoEvents();
                    Check(page.AutoScroll && tabs.ItemSize.Width * tabs.TabCount <= tabs.ClientSize.Width, "All settings pages remain scrollable and headers stay reachable on a short screen");
                    rightGap = tabs.ClientSize.Width - tabs.GetTabRect(tabs.TabCount - 1).Right;
                    Check(rightGap >= 0 && rightGap <= 4, "Tab header keeps its right edge aligned after resizing and switching pages: gap=" + rightGap);
                }
                var save = (Button)Field(form, "save");
                Check(form.RectangleToScreen(form.ClientRectangle).Contains(save.RectangleToScreen(save.ClientRectangle)), "Save remains reachable after reducing available height");
                Check(form.RectangleToScreen(form.ClientRectangle).Contains(((Control)form.CancelButton).RectangleToScreen(((Control)form.CancelButton).ClientRectangle)), "Cancel remains reachable after reducing available height");
                ((Button)form.CancelButton).PerformClick();
            }
        }
    }
    static Dictionary<string, object> ConfigWithApi(string root) { var config = Config(root); config["transcription_mode"] = "api"; return config; }
    static void ApiCredentialRecovery(string directory, string root) {
        string path = Path.Combine(directory, "missing-key-recovery.json"); Write(path, ConfigWithApi(root));
        string previous = Environment.GetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY");
        Environment.SetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY", null);
        try {
            var snapshot = MeetingSettingsSnapshot.Open(path);
            using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> next) { snapshot.Commit(next); return null; })) {
                form.Show(); Application.DoEvents();
                Check(((ComboBox)form.Controls.Find("transcription_mode", true)[0]).Items.Count == 1, "A machine without a local environment offers API only, without a dead model selector");
                ((CheckBox)form.Controls.Find("auto_transcribe", true)[0]).Checked = false;
                ((Button)form.AcceptButton).PerformClick();
                Check(!form.Visible && (string)MeetingSettingsSnapshot.Open(path).Values["transcription_mode"] == "api" && !(bool)MeetingSettingsSnapshot.Open(path).Values["auto_transcribe"], "Missing API credentials do not prevent saving record-only settings, without inventing a local model");
            }
            var api = ConfigWithApi(root);
            // Never contend with the user's running verbit default shortcuts.
            api["hotkey"] = "Ctrl+Alt+Shift+F21"; api["pause_hotkey"] = "Ctrl+Alt+Shift+F22";
            api["bookmark_hotkey"] = "Ctrl+Alt+Shift+F23"; api["quick_note_hotkey"] = "Ctrl+Alt+Shift+F24";
            using (var context = new MeetingContext(api, delegate { throw new Exception("No worker may launch"); }, delegate { throw new Exception("No external app may open"); })) {
                ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
                Check(context != null, "Missing API key no longer prevents desktop context from starting");
                Check(Reject(delegate { MeetingConfig.Validate(new Dictionary<string, object>(api)); }), "Transcription preflight remains strict without the API key");
                Check(MeetingConfig.Validate(new Dictionary<string, object>(api), false) != null, "Native recording accepts a configured API mode without credentials");
                context.ExitThread();
            }
        } finally { Environment.SetEnvironmentVariable("VERBIT_SETTINGS_SYNTHETIC_KEY", previous); }
    }
    static void ExtendedInterface(string directory, string root) {
        string path = Path.Combine(directory, "extended-settings.json"); Write(path, Config(root));
        var snapshot = MeetingSettingsSnapshot.Open(path);
        int calls = 0;
        string legacyPath=Path.Combine(directory,"legacy-model-settings.json");
        var legacy=Config(root); legacy["transcription_mode"]="local"; legacy["auto_transcribe"]=true; Write(legacyPath,legacy);
        byte[] beforeLegacy=File.ReadAllBytes(legacyPath);
        using(var legacyForm=new MeetingSettingsDialog(MeetingSettingsSnapshot.Open(legacyPath),delegate { throw new Exception("Cancel must not apply"); })) {
            legacyForm.Show(); legacyForm.ShowTranscription(); Application.DoEvents();
            Check(!((CheckBox)legacyForm.Controls.Find("auto_transcribe",true)[0]).Checked,"A legacy auto-local choice cannot silently enable API transcription");
            ((CheckBox)legacyForm.Controls.Find("auto_transcribe",true)[0]).Checked=true; Application.DoEvents();
            Check(((ComboBox)legacyForm.Controls.Find("transcription_mode",true)[0]).Items.Count==1,"Even an old local config only offers explicit API setup");
            ((Button)legacyForm.CancelButton).PerformClick();
        }
        Check(beforeLegacy.SequenceEqual(File.ReadAllBytes(legacyPath)),"Opening and canceling API setup preserves old configuration byte-exact");
        using (var form = new MeetingSettingsDialog(snapshot, delegate { return null; })) {
            form.Show(); form.ShowTranscription(); Application.DoEvents();
            Check(form.Controls.Find("download_recommended_model", true).Length == 0, "No recommended model download control is constructed, even for existing local users");
            ((Button)form.CancelButton).PerformClick();
        }
        using (var form = new MeetingSettingsDialog(snapshot, delegate { calls++; return "synthetic apply failure"; })) {
            form.Show(); Application.DoEvents();
            var startup = (CheckBox)form.Controls.Find("start_with_windows", true)[0]; startup.Checked = true;
            ((Button)form.AcceptButton).PerformClick();
            Check(calls == 1 && !DesktopServices.StartsWithWindows, "Failed settings apply rolls autostart back");
            ((Button)form.CancelButton).PerformClick();
        }
        using (var form = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> next) { snapshot.Commit(next); return null; })) {
            form.Show(); Application.DoEvents();
            var tabs = (TabControl)form.Controls.Find("settingsTabs", true)[0];
            Check(tabs.TabPages.Count == 3, "General, transcription and about pages exist");
            tabs.SelectedIndex = 1; Application.DoEvents();
            var automaticTranscription = (CheckBox)form.Controls.Find("auto_transcribe", true)[0];
            Check(!automaticTranscription.Checked && form.Controls.Find("download_recommended_model", true).Length == 0, "Default record-only settings have no model download entry");
            automaticTranscription.Checked = true; Application.DoEvents();
            Check(form.Controls.Find("download_recommended_model", true).Length == 0, "Enabling transcription never exposes a model download entry");
            Check(!Input(form, "asr_terms").Visible && Input(form, "api_model").Visible, "API configuration is visible while advanced fields remain collapsed");
            Check(form.Controls.Find("transcription_readiness", true).Length==0,"Settings never probes or exposes the old local engine");
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "settings-local.png")); }
            ((Button)form.Controls.Find("transcription_advanced", true)[0]).PerformClick(); Application.DoEvents();
            Check(Input(form, "asr_terms").Visible && Input(form, "api_model").Visible, "Advanced terms expand alongside explicit API fields");
            var mode = (ComboBox)form.Controls.Find("transcription_mode", true)[0]; mode.SelectedIndex = 0; Application.DoEvents();
            Check(Input(form, "api_model").Enabled, "Choosing API exposes API model fields");
            Input(form, "api_key_env").Text = "VERBIT_SYNTHETIC_UNSET_KEY";
            Input(form, "asr_terms").Text = "产品名,术语";
            CheckChildren(tabs.SelectedTab, form.DeviceDpi);
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "settings-models.png")); }
            form.ShowAbout(); Application.DoEvents();
            Check(tabs.SelectedIndex == 2 && form.Controls.Find("check_update_now", true)[0].Visible, "About entry opens version and update controls");
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "settings-about.png")); }
            ((CheckBox)form.Controls.Find("check_updates", true)[0]).Checked = true;
            ((Button)form.AcceptButton).PerformClick();
            var saved = MeetingSettingsSnapshot.Open(path).Values;
            Check(!form.Visible && (string)saved["transcription_mode"] == "api" && (bool)saved["auto_transcribe"], "Automatic transcription selection persists; missing API key is checked at transcription");
            Check(!form.Visible && (string)saved["asr_terms"] == "产品名,术语" && (bool)saved["check_updates"], "Transcription terms and opt-in update preference persist");
        }
    }
    static void WatchInvalidations(Control parent, Action changed) {
        parent.Invalidated += delegate { changed(); };
        foreach (Control child in parent.Controls) WatchInvalidations(child, changed);
    }
    static void RenderingResponsiveness(string directory, string root) {
        string path = Path.Combine(directory, "render-settings.json"); Write(path, Config(root));
        using (var form = new MeetingSettingsDialog(MeetingSettingsSnapshot.Open(path), delegate { return null; })) {
            form.Show(); form.ShowTranscription(); Application.DoEvents();
            ((CheckBox)form.Controls.Find("auto_transcribe", true)[0]).Checked = true; Application.DoEvents();
            ((Button)form.Controls.Find("transcription_advanced", true)[0]).PerformClick(); Application.DoEvents();
            Application.DoEvents();
            Check(typeof(MeetingSettingsDialog).GetField("engineTimer",BindingFlags.Instance|BindingFlags.NonPublic)==null,"No local model polling timer is constructed");
            int invalidations = 0;
            WatchInvalidations(form, delegate { invalidations++; });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) Application.DoEvents();
            watch.Stop();
            Console.WriteLine("Settings unchanged status: 50 polls, " + invalidations + " invalidations, " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
            int unchangedInvalidations = invalidations;
            var combo = (ComboBox)form.Controls.Find("transcription_mode", true)[0];
            combo.Focus(); combo.DroppedDown = true; Application.DoEvents();
            int before = invalidations;
            for (int i = 0; i < 5; i++) { Application.DoEvents(); Application.DoEvents(); }
            bool dropdownPreserved = combo.DroppedDown && combo.Focused;
            int dropdownInvalidations = invalidations - before;
            combo.DroppedDown = false;
            var tabs = (TabControl)form.Controls.Find("settingsTabs", true)[0];
            watch.Restart();
            for (int i = 0; i < 30; i++) { tabs.SelectedIndex = i % 3; Application.DoEvents(); }
            watch.Stop();
            Console.WriteLine("Settings page switches: 30 in " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
            Check(unchangedInvalidations == 0, "Unchanged readiness must not invalidate controls or redraw the page every timer tick");
            Check(dropdownPreserved, "Status polling preserves an open native dropdown and its keyboard focus");
            Check(dropdownInvalidations == 0, "An open dropdown must not trigger background status repaint churn");
            Check(tabs.SelectedIndex == 2 && tabs.TabPages.Count == 3, "Repeated native page switches leave the intended page active");
            tabs.SelectedIndex = 1; Application.DoEvents();
            var mode = (ComboBox)form.Controls.Find("transcription_mode", true)[0];
            watch.Restart();
            for (int i = 0; i < 12; i++) {
                mode.DroppedDown = true; Application.DoEvents();
                mode.SelectedIndex = 0; mode.DroppedDown = false; Application.DoEvents();
                Check(Input(form, "api_model").Visible && mode.Items.Count==1, "Native mode selection updates API visibility without stale controls");
            }
            watch.Stop();
            Console.WriteLine("Settings mode changes: 12 in " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
            Input(form, "api_model").Focus(); Input(form, "api_model").Text = "render-test-only";
            Check(Input(form, "api_model").Focused && Input(form, "api_model").Text == "render-test-only", "Text fields remain immediately editable after repeated mode changes");
            form.Close();
        }
    }
    static void ShortcutCapture(string directory, string root) {
        string path = Path.Combine(directory, "key-capture.json"); Write(path, Config(root));
        byte[] original = File.ReadAllBytes(path); int applyCount = 0, begin = 0, end = 0;
        using (var form = new MeetingSettingsDialog(MeetingSettingsSnapshot.Open(path), delegate(Dictionary<string, object> next) { applyCount++; MeetingSettingsSnapshot.Open(path).Commit(next); return null; })) {
            form.HotkeyCaptureChanged += delegate(bool capture) { if (capture) begin++; else end++; };
            form.Show(); Application.DoEvents();
            Check(!form.Controls.OfType<Panel>().Any(p => p.Controls.OfType<PictureBox>().Any()), "Settings has no second logo/header beneath the native title bar");
            var field = Input(form, "hotkey") as HotkeyInput;
            Check(field != null && field.ReadOnly && !field.ShortcutsEnabled && field.ContextMenuStrip == null, "Shortcuts record presses instead of text or clipboard operations");
            field.Focus(); Call(field, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
            Check(field.Capturing && !((Button)form.AcceptButton).Enabled, "Save cannot persist an incomplete capture prompt");
            field.CaptureKey(Keys.Enter); field.CaptureKey(Keys.A);
            Check(field.Capturing && field.Text == "请按快捷键…" && applyCount == 0, "Enter and bare letters cannot save or become malformed shortcuts");
            field.CaptureKey(Keys.Control | Keys.Shift | Keys.F18);
            Check(field.Capturing && field.Text == "Ctrl+Shift+F18", "Modifier combination appears immediately");
            Call(field, "OnKeyUp", new KeyEventArgs(Keys.F18));
            Check(!field.Capturing && field.Text == "Ctrl+Shift+F18" && ((Button)form.AcceptButton).Enabled, "Release of the captured key finishes capture");
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Recording a shortcut changes no file until Save");
            field.BeginCapture(); field.CaptureKey(Keys.Alt | Keys.F19); field.CaptureKey(Keys.Escape);
            Check(!field.Capturing && field.Text == "Ctrl+Shift+F18" && form.Visible, "Escape rolls back capture rather than closing settings");
            field.BeginCapture(); field.CaptureKey(Keys.Alt | Keys.F19); Input(form, "meeting_folder").Focus(); Application.DoEvents();
            Check(!field.Capturing && field.Text == "Ctrl+Shift+F18", "Focus loss restores the previous value, without a half-recorded shortcut");
            field.Focus(); field.BeginCapture(); field.CaptureKey(Keys.Alt | Keys.Space); Call(field, "OnKeyUp", new KeyEventArgs(Keys.Space));
            Check(((Label)Field(form, "shortcutHint")).Text.Contains("不能使用同一"), "Duplicate identities are explained immediately, without lowering validation");
            ((Button)form.AcceptButton).PerformClick();
            Check(form.Visible && applyCount == 0 && original.SequenceEqual(File.ReadAllBytes(path)), "Duplicate shortcuts cannot reach apply or change the file");
            field.Focus(); field.BeginCapture(); field.CaptureKey(Keys.Control | Keys.Shift | Keys.F18); Call(field, "OnKeyUp", new KeyEventArgs(Keys.F18));
            ((Button)form.AcceptButton).PerformClick();
            Check(!form.Visible && applyCount == 1 && (string)MeetingSettingsSnapshot.Open(path).Values["hotkey"] == "Ctrl+Shift+F18", "Captured shortcut persists through the existing validated Save flow");
            Check(begin == end, "Every capture releases its temporary state");
        }
        using (var form = new MeetingSettingsDialog(MeetingSettingsSnapshot.Open(path), delegate { throw new Exception("Closing must not apply"); })) {
            form.Show(); Application.DoEvents();
            var field = (HotkeyInput)Input(form, "quick_note_hotkey"); int restored = 0;
            form.HotkeyCaptureChanged += delegate(bool capturing) { if (!capturing) restored++; };
            field.Focus(); field.BeginCapture(); form.Close();
            Check(restored == 1 && !field.Capturing, "Closing mid-capture restores bindings once");
        }
        using (var hotkey = new MeetingHotkey("Ctrl+Alt+Shift+F21", "Ctrl+Alt+Shift+F22", "Ctrl+Alt+Shift+F23", "Ctrl+Alt+Shift+F24")) {
            int invoked = 0; hotkey.Pressed += delegate { invoked++; };
            hotkey.SetCaptureMode(true);
            using (var released = new ReservedKey("Ctrl+Alt+Shift+F21")) Check(true, "The currently assigned key is released so it can itself be captured");
            PostMessage(hotkey.Handle, 0x312, new IntPtr(7411), IntPtr.Zero); Application.DoEvents();
            Check(invoked == 0, "A queued old WM_HOTKEY cannot start recording during capture");
            hotkey.SetCaptureMode(false);
            Check(Reject(delegate { using (var reserved = new ReservedKey("Ctrl+Alt+Shift+F21")) { } }), "Active shortcut is restored after capture, without using unsaved fields");
            hotkey.EnablePause(true);
            Check(Reject(delegate { hotkey.SetCaptureMode(true); }) && hotkey.PauseRegistered, "Capturing cannot interrupt recording-time bindings");
            hotkey.EnablePause(false);
            hotkey.SetCaptureMode(true);
            using (var occupied = new ReservedKey("Ctrl+Alt+Shift+F21")) {
                bool explained = false;
                try { hotkey.SetCaptureMode(false); } catch (InvalidOperationException failure) { explained = failure.Message.Contains("恢复失败"); }
                Check(explained && !hotkey.QuickNoteRegistered, "A restore race is actionable and releases partial bindings rather than pretending to succeed");
            }
            hotkey.Reconfigure("Ctrl+Alt+Shift+F21", "Ctrl+Alt+Shift+F22", "Ctrl+Alt+Shift+F23", "Ctrl+Alt+Shift+F24");
            Check(hotkey.QuickNoteRegistered, "An explicit later reconfiguration can recover after the competing program releases the old shortcut");
        }
    }
    [STAThread]
    static int Main() {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            DesktopServices.StartupFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "isolated-startup");
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-fixture");
            string root = Path.Combine(directory, "notes"), nextRoot = Path.Combine(directory, "other-notes");
            Directory.CreateDirectory(root); Directory.CreateDirectory(nextRoot);
            ShortcutCapture(directory, root); RenderingResponsiveness(directory, root);
            RecognitionConfig(root); Persistence(directory, root, nextRoot); RecordingProtection(directory, root); Interface(directory, root); ExtendedInterface(directory, root); ApiCredentialRecovery(directory, root); FirstRun(directory, root); MonitorLayouts(directory, root); ContextIntegration(root, nextRoot);
            Console.WriteLine("PASS TestMeetingSettings: " + assertions + " assertions; synthetic configs and isolated native settings only; no user config, clipboard, audio or global input.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
