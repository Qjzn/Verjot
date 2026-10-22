using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

internal static class TestMeetingHotkeys {
    const string StartKey = "Ctrl+Alt+Shift+F21";
    const string PauseKey = "Ctrl+Alt+Shift+F22";
    const string BookmarkKey = "Ctrl+Alt+Shift+F23";
    const string QuickNoteKey = "Ctrl+Alt+Shift+F24";
    static int assertions;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    static bool Registered(MeetingHotkey hotkey, string name) {
        var property = typeof(MeetingHotkey).GetProperty(name);
        Check(property != null, "Missing independent registration state: " + name);
        return (bool)property.GetValue(hotkey, null);
    }
    static string Warning(MeetingHotkey hotkey) {
        var property = typeof(MeetingHotkey).GetProperty("AuxiliaryWarning");
        Check(property != null, "Auxiliary failure must expose an actionable startup warning");
        return (string)property.GetValue(hotkey, null);
    }
    sealed class OccupiedKey : IDisposable {
        readonly Thread owner;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly ManualResetEvent done = new ManualResetEvent(false);
        Exception failure;
        public OccupiedKey(string text) {
            owner = new Thread(delegate() {
                NativeWindow window = null;
                bool registered = false;
                try {
                    window = new NativeWindow();
                    window.CreateHandle(new CreateParams { Caption = "Isolated hotkey conflict fixture", Parent = new IntPtr(-3) });
                    uint modifiers, key; MeetingConfig.ParseHotkey(text, out modifiers, out key);
                    registered = RegisterHotKey(window.Handle, 17231, modifiers | 0x4000, key);
                    if (!registered) throw new Exception("Fixture could not reserve " + text + ": Win32 " + Marshal.GetLastWin32Error());
                } catch (Exception error) { failure = error; }
                finally { ready.Set(); }
                try { done.WaitOne(); }
                finally { if (registered) UnregisterHotKey(window.Handle, 17231); if (window != null) window.DestroyHandle(); }
            });
            owner.SetApartmentState(ApartmentState.STA);
            owner.Start(); ready.WaitOne();
            if (failure != null) { Dispose(); throw failure; }
        }
        public void Dispose() { done.Set(); owner.Join(); }
    }
    static void Press(MeetingHotkey hotkey, ushort functionKey) {
        // Registration/conflict/release is tested against Windows separately.
        // Deliver only to this fixture's message window; never alter the user's
        // held modifiers, keyboard focus, clipboard or current input stream.
        int id = 7411 + functionKey - 0x84;
        Check(PostMessage(hotkey.Handle, 0x0312, new IntPtr(id), new IntPtr((functionKey << 16) | 7)), "Fixture WM_HOTKEY delivery failed: Win32 " + Marshal.GetLastWin32Error());
    }
    static void Receive(Func<bool> received) {
        DateTime until = DateTime.UtcNow.AddSeconds(2);
        while (!received() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
        Check(received(), "The unoccupied auxiliary hotkey must still deliver its actual WM_HOTKEY event");
    }
    static void AuxiliaryConflict(bool blockPause) {
        string blocked = blockPause ? PauseKey : BookmarkKey;
        string available = blockPause ? BookmarkKey : PauseKey;
        using (var occupied = new OccupiedKey(blocked))
        using (var hotkey = new MeetingHotkey(StartKey, PauseKey, BookmarkKey)) {
            int callbacks = 0;
            if (blockPause) hotkey.BookmarkPressed += delegate { callbacks++; };
            else hotkey.PausePressed += delegate { callbacks++; };
            // This call is the actual RED trigger: the previous all-or-nothing
            // implementation throws here and prevents a meeting from starting.
            hotkey.EnablePause(true);
            Check(Registered(hotkey, "PauseRegistered") == !blockPause, "Pause registration must be independent of bookmark registration");
            Check(Registered(hotkey, "BookmarkRegistered") == blockPause, "Bookmark registration must be independent of pause registration");
            string warning = Warning(hotkey);
            Check(warning.Contains(blocked) && warning.Contains("1409"), "Conflict warning must identify the failed key and real Win32 1409");
            Check(!warning.Contains(available), "The working auxiliary key must not be reported as failed");
            Press(hotkey, blockPause ? (ushort)0x86 : (ushort)0x85);
            Receive(delegate { return callbacks == 1; });
            hotkey.EnablePause(false);
            Check(!Registered(hotkey, "PauseRegistered") && !Registered(hotkey, "BookmarkRegistered"), "Disable must clear both independent registration states");
            using (var released = new OccupiedKey(available)) { Check(true, "Disable released the working auxiliary key"); }
            Check(Warning(hotkey).Length == 0, "Disable must clear obsolete startup warnings");
        }
        using (var released = new OccupiedKey(blocked)) { Check(true, "Fixture and controller must release the conflicted key"); }
        using (var released = new OccupiedKey(StartKey)) { Check(true, "Controller disposal must release the required main key"); }
    }
    static void QuickNoteLifetime() {
        MeetingHotkey disposed;
        using (var hotkey = new MeetingHotkey(StartKey, PauseKey, BookmarkKey, QuickNoteKey)) {
            disposed = hotkey;
            int quickNotes = 0, starts = 0;
            hotkey.QuickNotePressed += delegate { quickNotes++; };
            hotkey.Pressed += delegate { starts++; };
            Check(hotkey.QuickNoteRegistered, "The quick-note key must register before any recording starts");
            Check(hotkey.QuickNoteWarning.Length == 0, "An available quick-note key must have no warning");
            Check(!hotkey.PauseRegistered && !hotkey.BookmarkRegistered, "Quick note must not register recording-only keys while idle");
            Press(hotkey, 0x87);
            Receive(delegate { return quickNotes == 1; });
            Press(hotkey, 0x84);
            Receive(delegate { return starts == 1; });
            hotkey.EnablePause(true);
            Check(hotkey.QuickNoteRegistered && hotkey.PauseRegistered && hotkey.BookmarkRegistered, "Quick note must remain registered alongside both recording-only keys");
            hotkey.EnablePause(false);
            Check(hotkey.QuickNoteRegistered, "Disabling recording-only keys must keep quick note registered");
            Check(!hotkey.PauseRegistered && !hotkey.BookmarkRegistered, "Recording-only keys must still be released");
            Check(hotkey.QuickNoteWarning.Length == 0, "Recording lifecycle must not invent a quick-note warning");
            Press(hotkey, 0x87);
            Receive(delegate { return quickNotes == 2; });
            Press(hotkey, 0x84);
            Receive(delegate { return starts == 2; });
        }
        Check(!disposed.QuickNoteRegistered, "Disposal must clear quick-note registration state");
        using (var released = new OccupiedKey(QuickNoteKey)) { Check(true, "Disposal must release the actual quick-note key"); }
    }
    static void QuickNoteConflict() {
        using (var occupied = new OccupiedKey(QuickNoteKey))
        using (var hotkey = new MeetingHotkey(StartKey, PauseKey, BookmarkKey, QuickNoteKey)) {
            int starts = 0, pauses = 0, bookmarks = 0, quickNotes = 0;
            hotkey.Pressed += delegate { starts++; };
            hotkey.PausePressed += delegate { pauses++; };
            hotkey.BookmarkPressed += delegate { bookmarks++; };
            hotkey.QuickNotePressed += delegate { quickNotes++; };
            Check(!hotkey.QuickNoteRegistered, "A conflicted quick-note key must not appear registered");
            string warning = hotkey.QuickNoteWarning;
            Check(warning.Contains(QuickNoteKey) && warning.Contains("quick_note_hotkey") && warning.Contains("1409"), "Quick-note conflict warning must identify its setting, key and real Win32 1409");
            Check(!warning.Contains(StartKey) && !warning.Contains(PauseKey) && !warning.Contains(BookmarkKey), "Quick-note conflict must not blame unrelated working keys");
            Check(hotkey.AuxiliaryWarning.Length == 0, "Quick-note conflict must remain separate from recording-key warnings");
            Press(hotkey, 0x84);
            Receive(delegate { return starts == 1; });
            hotkey.EnablePause(true);
            Check(hotkey.PauseRegistered && hotkey.BookmarkRegistered, "Quick-note conflict must not block either recording-only key");
            Check(hotkey.AuxiliaryWarning.Length == 0, "Available recording keys must not inherit the quick-note warning");
            Press(hotkey, 0x85);
            Receive(delegate { return pauses == 1; });
            Press(hotkey, 0x86);
            Receive(delegate { return bookmarks == 1; });
            hotkey.EnablePause(false);
            Check(hotkey.QuickNoteWarning == warning && !hotkey.QuickNoteRegistered, "Disabling recording keys must preserve the actionable quick-note conflict warning");
            Press(hotkey, 0x84);
            Receive(delegate { return starts == 2; });
            Check(quickNotes == 0, "Other registered keys must not invoke quick note");
        }
        using (var released = new OccupiedKey(StartKey)) { Check(true, "Controller disposal after quick-note conflict must release the start key"); }
        using (var released = new OccupiedKey(QuickNoteKey)) { Check(true, "Conflict fixture must release its isolated quick-note key"); }
    }
    static object Field(object target, string name) {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(target);
    }
    static void Set(object target, string name, object value) {
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value);
    }
    static void MicrophoneOnlyController() {
        // Invoke the real hotkey/controller/menu path with in-memory endpoint
        // fixtures and actual WAV writers. Never enumerate or capture a device.
        string root = Path.Combine(MeetingFiles.Home, "microphone-hotkey");
        Directory.CreateDirectory(root);
        var config = new Dictionary<string, object> {
            { "vault_root", root }, { "hotkey", StartKey }, { "pause_hotkey", PauseKey },
            { "bookmark_hotkey", BookmarkKey }, { "quick_note_hotkey", QuickNoteKey },
            { "check_updates", false }, { "show_processing_window", false }, { "open_in_obsidian", false }
        };
        var session = (RecordingSession)FormatterServices.GetUninitializedObject(typeof(RecordingSession));
        Set(session, "gate", new object()); Set(session, "clock", Stopwatch.StartNew());
        Set(session, "DirectoryPath", root); Set(session, "captureProblems", new List<string>());
        var micRanges = new List<Dictionary<string, object>>();
        var wholeRanges = new List<Dictionary<string, object>>();
        Set(session, "microphonePauses", micRanges); Set(session, "recordingPauses", wholeRanges);
        var metadata = new Dictionary<string, object> {
            { "microphone_pauses", micRanges }, { "recording_pauses", wholeRanges },
            { "recording_paused", false }, { "microphone_paused", false }
        };
        Set(session, "metadata", metadata);
        Type sourceType = typeof(RecordingSession).GetNestedType("Source", BindingFlags.NonPublic);
        var sources = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sourceType));
        Set(session, "sources", sources);
        var writers = new List<TimelineWriter>();
        MeetingContext context = null;
        try {
            foreach (string key in new[] { "mic", "output-one", "output-two" }) {
                string path = Path.Combine(root, key + ".wav");
                var writer = new TimelineWriter(path, new WaveFormat(16000, 16, 1)); writers.Add(writer);
                var track = (CaptureTrack)FormatterServices.GetUninitializedObject(typeof(CaptureTrack));
                Set(track, "writer", writer); Set(track, "PathName", path);
                Set(track, "diagnostics", new List<Dictionary<string, object>>());
                var source = Activator.CreateInstance(sourceType, true);
                Set(source, "Key", key); Set(source, "Kind", key == "mic" ? "microphone" : "loopback");
                Set(source, "DeviceId", key); Set(source, "Name", key); Set(source, "Track", track);
                sources.Add(source);
            }
            context = new MeetingContext(config, null, null);
            ((NotifyIcon)Field(context, "tray")).Visible = false;
            ((System.Windows.Forms.Timer)Field(context, "timer")).Stop();
            var hotkey = (MeetingHotkey)Field(context, "hotkey");
            Press(hotkey, 0x85); Application.DoEvents();
            Check(!session.MicrophonePaused, "Idle pause key must not affect a recording fixture");
            Set(context, "recording", session);
            var panel = (RecorderPanel)Field(context, "panel");
            panel.ShowRecording("mic", "output-one; output-two"); panel.Show();
            var micMenu = (ToolStripMenuItem)Field(context, "pauseMicrophone");
            micMenu.Enabled = true;
            hotkey.EnablePause(true);
            Thread.Sleep(20); Press(hotkey, 0x85);
            Receive(delegate { return session.MicrophonePaused || session.RecordingPaused; });
            Check(session.MicrophonePaused && !session.RecordingPaused, "Pause hotkey must mute only microphone, never system audio");
            Check(panel.MicrophoneButton.MicrophoneDisabled && !panel.Heading.Visible, "Mic mute must update the icon without a whole-recording pause label");
            Check(micMenu.Text.Contains("继续麦克风") && micMenu.Text.Contains(PauseKey), "One menu action must show resume and the configured microphone hotkey");
            foreach (ToolStripItem item in panel.ContextMenuStrip.Items)
                Check(!item.Text.Contains("整场录音"), "The shared menu must not expose whole-session pause");
            Thread.Sleep(20); panel.MicrophoneButton.PerformClick();
            Check(!session.MicrophonePaused && !session.RecordingPaused, "Clicking the microphone after keyboard pause must resume it");
            Check(micMenu.Text.Contains("暂停麦克风"), "Resume must restore the menu action to pause microphone");
            Thread.Sleep(20); micMenu.PerformClick();
            Check(session.MicrophonePaused && !session.RecordingPaused, "Menu pause must share the same mic-only state");
            Thread.Sleep(20); Press(hotkey, 0x85); Receive(delegate { return !session.MicrophonePaused; });
            Check(!session.RecordingPaused && wholeRanges.Count == 0, "Mixed keyboard, icon and menu actions must never create whole-session pauses");
            Check(micRanges.Count == 2, "The two microphone pause intervals must be persisted");
            var saved = MeetingFiles.ReadJson(Path.Combine(root, "session.json"));
            Check(!(bool)saved["recording_paused"] && !(bool)saved["microphone_paused"], "Persisted session state must reflect mic-only resume");
            int frames = (int)Math.Ceiling(((Stopwatch)Field(session, "clock")).Elapsed.TotalSeconds * 16000) + 1600;
            byte[] pcm = new byte[frames * 2];
            for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0xe8; pcm[i + 1] = 3; }
            foreach (var writer in writers) { writer.Add(pcm, pcm.Length, frames / 16.0); writer.Finish(frames / 16.0); }
            foreach (string key in new[] { "mic", "output-one", "output-two" }) {
                using (var reader = new WaveFileReader(Path.Combine(root, key + ".wav"))) {
                    Check(reader.Length == pcm.Length, "Microphone pauses must preserve each track's wall-clock length");
                    byte[] actual = new byte[pcm.Length]; int count = reader.Read(actual, 0, actual.Length);
                    Check(count == actual.Length, "Every track must remain fully readable");
                    bool samplesCorrect = true;
                    for (int i = 0; i < count; i += 2) {
                        double seconds = i / 32000.0; bool muted = false, boundary = false;
                        if (key == "mic") foreach (var range in micRanges) {
                            double start = (double)range["start_seconds"], end = (double)range["end_seconds"];
                            muted |= seconds > start && seconds < end;
                            boundary |= Math.Abs(seconds - start) < .002 || Math.Abs(seconds - end) < .002;
                        }
                        if (!boundary && BitConverter.ToInt16(actual, i) != (muted ? 0 : 1000)) samplesCorrect = false;
                    }
                    Check(samplesCorrect, "Only mic pause intervals may be silent; both computer sources must remain sample-identical: " + key);
                }
            }
        } finally {
            if (context != null) { Set(context, "recording", null); context.ExitThread(); context.Dispose(); }
            foreach (var writer in writers) writer.Dispose();
        }
    }
    [STAThread]
    static int Main() {
        try {
            AuxiliaryConflict(true);
            AuxiliaryConflict(false);
            using (var occupied = new OccupiedKey(StartKey)) {
                bool rejected = false;
                try { using (var hotkey = new MeetingHotkey(StartKey, PauseKey, BookmarkKey)) { } }
                catch (InvalidOperationException error) { rejected = error.Message.Contains(StartKey) && error.Message.Contains("1409"); }
                Check(rejected, "A conflicting main start/stop key must remain a fatal, accurately reported startup error");
            }
            using (var hotkey = new MeetingHotkey(StartKey, PauseKey, BookmarkKey)) {
                int shown = 0;
                hotkey.ShowRequested += delegate { shown++; };
                DesktopServices.ShowExisting();
                Receive(delegate { return shown == 1; });
                Check(shown == 1, "Start menu activation reaches the existing instance on the isolated desktop");
                hotkey.EnablePause(true);
                hotkey.EnablePause(true);
                Check(Registered(hotkey, "PauseRegistered") && Registered(hotkey, "BookmarkRegistered"), "Both available auxiliaries register and repeated enable stays idempotent");
                Check(Warning(hotkey).Length == 0, "Successful auxiliary registration must have no conflict warning");
            }
            using (var released = new OccupiedKey(PauseKey)) { Check(true, "Disposal releases pause independently"); }
            using (var released = new OccupiedKey(BookmarkKey)) { Check(true, "Disposal releases bookmark independently"); }
            QuickNoteLifetime();
            QuickNoteConflict();
            MicrophoneOnlyController();
            Console.WriteLine("PASS TestMeetingHotkeys: " + assertions + " assertions; actual isolated key registration and targeted WM_HOTKEY dispatch; no global input, clipboard, audio or user windows.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
