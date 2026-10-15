using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// Test-only launcher. Never switches the user's desktop or opens the clipboard.
internal static class TestDesktopRunner {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct StartupInfo {
        public int cb; public string reserved, desktop, title;
        public int x,y,xSize,ySize,xChars,yChars,fill,flags; public short show,reservedSize;
        public IntPtr reservedPointer, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string application, StringBuilder command, IntPtr ps, IntPtr ts, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    static string Quote(string text) {
        var value = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in text) {
            if (ch == '\\') { slashes++; continue; }
            value.Append('\\', slashes * (ch == '"' ? 2 : 1) + (ch == '"' ? 1 : 0)); slashes = 0; value.Append(ch);
        }
        value.Append('\\', slashes * 2); return value.Append('"').ToString();
    }
    static int Main(string[] args) {
        if (args.Length == 0) return 2;
        string executable = Path.GetFullPath(args[0]);
        string name = "TingjiTest_" + Guid.NewGuid().ToString("N");
        IntPtr desktop = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        ProcessInfo process = new ProcessInfo();
        try {
            var command = new StringBuilder(Quote(executable));
            for (int i = 1; i < args.Length; i++) command.Append(' ').Append(Quote(args[i]));
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = name,
                flags = 0x100, input = GetStdHandle(-10), output = GetStdHandle(-11), error = GetStdHandle(-12) };
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process)) throw new Win32Exception();
            if (WaitForSingleObject(process.process, 180000) != 0) { TerminateProcess(process.process, 124); WaitForSingleObject(process.process, 5000); return 124; }
            uint code; if (!GetExitCodeProcess(process.process, out code)) throw new Win32Exception();
            return unchecked((int)code);
        } finally {
            if (process.thread != IntPtr.Zero) CloseHandle(process.thread);
            if (process.process != IntPtr.Zero) CloseHandle(process.process);
            CloseDesktop(desktop);
        }
    }
}
