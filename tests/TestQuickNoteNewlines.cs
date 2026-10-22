using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Run on TestDesktopRunner's private desktop. Return is translated by the
// actual WinForms message loop; no WM_CHAR, clipboard or global input is sent.
internal static class TestQuickNoteNewlines {
    sealed class MessageProbe : IMessageFilter {
        internal int ReturnCharacters;
        internal int CurrentMessage;
        internal readonly List<int> Changes = new List<int>();
        public bool PreFilterMessage(ref Message message) {
            if (message.HWnd == input.Handle) {
                CurrentMessage = message.Msg;
                if (message.Msg == 0x102 && message.WParam.ToInt64() == 13) ReturnCharacters++;
            }
            return false;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    static ApplicationContext context;
    static QuickNoteWindow window;
    static QuickNoteStore store;
    static RichTextBox input;
    static NativeNoteDocument document;
    static MessageProbe probe;
    static string root;
    static int assertions, lineStyle, caseIndex, insertions;
    static string newline;
    static bool failed;
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static string Escaped(string value) { return value.Replace("\r", "\\r").Replace("\n", "\\n"); }
    static void Fail(Exception error) { failed = true; Console.Error.WriteLine(error); context.ExitThread(); }
    static void Later(Action action) {
        var timer = new Timer { Interval = 100 };
        timer.Tick += delegate {
            timer.Stop(); timer.Dispose();
            try { action(); } catch (Exception error) { Fail(error); }
        };
        timer.Start();
    }
    static void BeginStyle() {
        newline = lineStyle == 0 ? "\n" : "\r\n";
        string vault = Path.Combine(root, "vault"); Directory.CreateDirectory(vault);
        store = new QuickNoteStore(vault, "sample-" + lineStyle, Path.Combine(root, "recovery"));
        var note = store.Load(DateTime.Today); store.Save(note, "seed" + newline + "tail");
        window = new QuickNoteWindow(store, Path.Combine(root, "state-" + lineStyle));
        input = (RichTextBox)Field(window, "editor"); document = (NativeNoteDocument)Field(window, "nativeDocument");
        var actualInsert = ((QuickNoteInputBox)input).InsertNewline;
        ((QuickNoteInputBox)input).InsertNewline = delegate { insertions++; actualInsert(); };
        probe = new MessageProbe(); Application.AddMessageFilter(probe);
        input.TextChanged += delegate { probe.Changes.Add(probe.CurrentMessage); };
        caseIndex = 0; window.Toggle(); Later(Ready);
    }
    static void Ready() {
        if (input.ReadOnly || (bool)Field(window, "workerBusy")) { Later(Ready); return; }
        Check(window.ReadMarkdown() == "seed" + newline + "tail", "The fixture loads the original newline style.");
        NextCase();
    }
    static void NextCase() {
        string seed = caseIndex == 9 ? "- 第一项" : caseIndex == 10 ? "- " : caseIndex >= 11 ? "- 甲乙丙" : caseIndex == 0 ? "" : caseIndex == 2 ? "AB" : caseIndex == 3 ? "ABCDE" : caseIndex == 4 ? "中文" : "A";
        int start = caseIndex == 2 || caseIndex == 3 || caseIndex == 4 ? 1 : seed.Length;
        int length = caseIndex == 3 ? 3 : 0, repetitions = caseIndex == 5 ? 3 : 1;
        string inserted = ""; for (int i = 0; i < repetitions; i++) inserted += newline;
        if (caseIndex >= 11) { start = 3; length = caseIndex == 12 ? 1 : 0; }
        bool cancelled = caseIndex >= 6 && caseIndex <= 8;
        string expected = cancelled ? seed : seed.Remove(start, length).Insert(start, inserted);
        if (caseIndex == 9) expected = seed + newline + "- ";
        if (caseIndex == 10) expected = "";
        if (caseIndex == 11) expected = "- 甲" + newline + "- 乙丙";
        if (caseIndex == 12) expected = "- 甲" + newline + "- 丙";
        input.ReadOnly = false; document.Load(seed); input.Select(start, length);
        insertions = 0; probe.ReturnCharacters = 0; probe.CurrentMessage = 0; probe.Changes.Clear();
        KeyEventHandler keyDown = delegate(object sender, KeyEventArgs args) { if (caseIndex == 6) args.SuppressKeyPress = true; };
        KeyPressEventHandler keyPress = delegate(object sender, KeyPressEventArgs args) { if (caseIndex == 7) args.Handled = true; };
        input.KeyDown += keyDown; input.KeyPress += keyPress;
        if (caseIndex == 8) input.ReadOnly = true;
        input.Focus(); Check(input.Focused, "The private fixture editor has keyboard focus.");
        for (int i = 0; i < repetitions; i++)
            Check(PostMessage(input.Handle, 0x100, new IntPtr(13), new IntPtr(i == 0 ? 0x001c0001 : 0x401c0001)), "Return KeyDown is posted only to the fixture editor.");
        Check(PostMessage(input.Handle, 0x101, new IntPtr(13), new IntPtr(unchecked((int)0xc01c0001))), "Return KeyUp is posted only to the fixture editor.");
        Later(delegate {
            input.KeyDown -= keyDown; input.KeyPress -= keyPress; input.ReadOnly = false;
            string actual = window.ReadMarkdown();
            string label = (lineStyle == 0 ? "LF" : "CRLF") + " case=" + caseIndex;
            Check(actual == expected, label + " expected=[" + Escaped(expected) + "], actual=[" + Escaped(actual) + "].");
            Check(insertions == (cancelled ? 0 : repetitions), label + " custom insertion count must match uncancelled Return presses.");
            if (!cancelled) {
                Check(probe.ReturnCharacters == repetitions, label + " each KeyDown naturally translates one Return character; the fixture never posts WM_CHAR.");
                Check(!probe.Changes.Contains(0x100), label + " native KeyDown must not edit before the translated character.");
                Check(input.CanUndo, label + " Return is undoable."); input.Undo();
                string undone = caseIndex == 5 ? expected.Substring(0, expected.Length - newline.Length) : seed;
                Check(window.ReadMarkdown() == undone, label + " one Undo removes one Return and restores replaced text.");
                Check(input.CanRedo, label + " Return is redoable."); input.Redo();
                Check(window.ReadMarkdown() == expected, label + " Redo restores the exact newline.");
            }
            Console.WriteLine("PASS " + label + "; translated=" + probe.ReturnCharacters + "; inserted=" + insertions);
            caseIndex++;
            if (caseIndex < 13) Later(NextCase); else SaveAndReopen();
        });
    }
    static void SaveAndReopen() {
        document.Load("saved" + newline + "tail"); input.Select(input.TextLength, 0);
        Check(PostMessage(input.Handle, 0x100, new IntPtr(13), new IntPtr(0x001c0001)), "The saved Return is posted to the fixture.");
        Check(PostMessage(input.Handle, 0x101, new IntPtr(13), new IntPtr(unchecked((int)0xc01c0001))), "The saved Return is released in the fixture.");
        Later(delegate { window.Toggle(); Later(CheckHidden); });
    }
    static void CheckHidden() {
        if (window.Visible || (bool)Field(window, "workerBusy")) { Later(CheckHidden); return; }
        Check(store.Load(DateTime.Today).Text == "saved" + newline + "tail" + newline, "Save preserves the original newline style without extra blank lines.");
        window.Toggle(); Later(CheckReopened);
    }
    static void CheckReopened() {
        if (input.ReadOnly || (bool)Field(window, "workerBusy")) { Later(CheckReopened); return; }
        Check(window.ReadMarkdown() == "saved" + newline + "tail" + newline, "Hide/reopen preserves the exact translated Return.");
        Check(window.FlushAndClose(), "The fixture closes after durable save.");
        Application.RemoveMessageFilter(probe); probe = null; window.Dispose(); window = null;
        if (++lineStyle < 2) BeginStyle();
        else { Console.WriteLine("PASS translated Return regression; assertions=" + assertions + "; 26 content/cancellation/list cases plus durable save/reopen for LF and CRLF."); context.ExitThread(); }
    }
    [STAThread] static int Main() {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        root = Path.Combine(Path.GetTempPath(), "tingji-newlines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        context = new ApplicationContext();
        using (var dispatch = new Control()) {
            IntPtr ignored = dispatch.Handle;
            dispatch.BeginInvoke((Action)delegate {
                try {
                    BeginStyle();
                } catch (Exception error) { Fail(error); }
            });
            var deadline = new Timer { Interval = 30000 };
            deadline.Tick += delegate { deadline.Stop(); Fail(new TimeoutException("The translated Return fixture must finish within thirty seconds.")); };
            deadline.Start(); Application.Run(context); deadline.Stop(); deadline.Dispose();
        }
        if (probe != null) Application.RemoveMessageFilter(probe);
        if (window != null) window.Dispose(); context.Dispose();
        // The only files touched are this fixture's freshly created directory.
        try { Directory.Delete(root, true); } catch { }
        return failed ? 1 : 0;
    }
}
