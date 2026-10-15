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
            Console.WriteLine("PASS TestMeetingHotkeys: " + assertions + " assertions; isolated F21/F22/F23 registration and WM_HOTKEY only; no audio or user windows.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
