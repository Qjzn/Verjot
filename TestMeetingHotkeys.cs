using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class TestMeetingHotkeys {
    const string StartKey = "Ctrl+Alt+Shift+F21";
    const string PauseKey = "Ctrl+Alt+Shift+F22";
    const string BookmarkKey = "Ctrl+Alt+Shift+F23";
    const string QuickNoteKey = "Ctrl+Alt+Shift+F24";
    static int assertions;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int x, y; public uint data, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KeyInput { public ushort key, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MouseInput mouse; [FieldOffset(0)] public KeyInput key; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public InputUnion data; }
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
    static Input Key(ushort code, bool up) { return new Input { type = 1, data = new InputUnion { key = new KeyInput { key = code, flags = up ? 2u : 0u } } }; }
    static void Press(ushort functionKey) {
        var keys = new ushort[] { 0x11, 0x12, 0x10, functionKey };
        var input = new List<Input>();
        foreach (ushort key in keys) input.Add(Key(key, false));
        for (int i = keys.Length - 1; i >= 0; i--) input.Add(Key(keys[i], true));
        Check(SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf(typeof(Input))) == input.Count, "Isolated F-key injection failed: Win32 " + Marshal.GetLastWin32Error());
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
            Press(blockPause ? (ushort)0x86 : (ushort)0x85);
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
            Press(0x87);
            Receive(delegate { return quickNotes == 1; });
            Press(0x84);
            Receive(delegate { return starts == 1; });
            hotkey.EnablePause(true);
            Check(hotkey.QuickNoteRegistered && hotkey.PauseRegistered && hotkey.BookmarkRegistered, "Quick note must remain registered alongside both recording-only keys");
            hotkey.EnablePause(false);
            Check(hotkey.QuickNoteRegistered, "Disabling recording-only keys must keep quick note registered");
            Check(!hotkey.PauseRegistered && !hotkey.BookmarkRegistered, "Recording-only keys must still be released");
            Check(hotkey.QuickNoteWarning.Length == 0, "Recording lifecycle must not invent a quick-note warning");
            Press(0x87);
            Receive(delegate { return quickNotes == 2; });
            Press(0x84);
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
            Press(0x84);
            Receive(delegate { return starts == 1; });
            hotkey.EnablePause(true);
            Check(hotkey.PauseRegistered && hotkey.BookmarkRegistered, "Quick-note conflict must not block either recording-only key");
            Check(hotkey.AuxiliaryWarning.Length == 0, "Available recording keys must not inherit the quick-note warning");
            Press(0x85);
            Receive(delegate { return pauses == 1; });
            Press(0x86);
            Receive(delegate { return bookmarks == 1; });
            hotkey.EnablePause(false);
            Check(hotkey.QuickNoteWarning == warning && !hotkey.QuickNoteRegistered, "Disabling recording keys must preserve the actionable quick-note conflict warning");
            Press(0x84);
            Receive(delegate { return starts == 2; });
            Check(quickNotes == 0, "Other registered keys must not invoke quick note");
        }
        using (var released = new OccupiedKey(StartKey)) { Check(true, "Controller disposal after quick-note conflict must release the start key"); }
        using (var released = new OccupiedKey(QuickNoteKey)) { Check(true, "Conflict fixture must release its isolated quick-note key"); }
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
                hotkey.EnablePause(true);
                hotkey.EnablePause(true);
                Check(Registered(hotkey, "PauseRegistered") && Registered(hotkey, "BookmarkRegistered"), "Both available auxiliaries register and repeated enable stays idempotent");
                Check(Warning(hotkey).Length == 0, "Successful auxiliary registration must have no conflict warning");
            }
            using (var released = new OccupiedKey(PauseKey)) { Check(true, "Disposal releases pause independently"); }
            using (var released = new OccupiedKey(BookmarkKey)) { Check(true, "Disposal releases bookmark independently"); }
            QuickNoteLifetime();
            QuickNoteConflict();
            Console.WriteLine("PASS TestMeetingHotkeys: " + assertions + " assertions; isolated F21/F22/F23/F24 registration and WM_HOTKEY only; no audio or user windows.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
