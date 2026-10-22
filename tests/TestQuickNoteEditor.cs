using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class TestQuickNoteEditor {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int WindowStyle(IntPtr window, int index);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct NativeRectangle { internal int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr FormatRectangle(IntPtr window, int message, IntPtr wparam, ref NativeRectangle rectangle);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr NativeMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static object Call(object value, string name, params object[] arguments) {
        try { return value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static void Pump(int milliseconds) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    static void Settled(QuickNoteWindow window) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do { Pump(10); } while (!window.IsDisposed && ((bool)Field(window, "workerBusy") || (string)Field(window, "pendingText") != null || (int)Field(window, "queuedImageCount") != 0) && watch.ElapsedMilliseconds < 5000);
        Check(window.IsDisposed || (!(bool)Field(window, "workerBusy") && Field(window, "pendingText") == null && (int)Field(window, "queuedImageCount") == 0), "Background storage and image work complete without blocking the UI pump");
    }
    static void Autosaved(QuickNoteWindow window) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do { Pump(10); } while (!window.IsDisposed && ((bool)Field(window, "dirty") || (bool)Field(window, "workerBusy") || Field(window, "pendingText") != null || (int)Field(window, "queuedImageCount") != 0) && watch.ElapsedMilliseconds < 5000);
        Check(!window.IsDisposed && !(bool)Field(window, "dirty") && !(bool)Field(window, "workerBusy") && Field(window, "pendingText") == null && (int)Field(window, "queuedImageCount") == 0,
            "The production autosave timer and background persistence finish within the existing five-second bound");
    }
    static NativeNoteDocument Document(QuickNoteWindow window) { return (NativeNoteDocument)Field(window, "nativeDocument"); }
    static void CheckMenuClose(string vault, string root) {
        foreach (string method in new string[] { "ShowSaveMenu", "ShowEditorMenu" }) {
            for (int shutdown = 0; shutdown < 3; shutdown++) {
                var store = new QuickNoteStore(vault, "menu-fixture", Path.Combine(root, "menu-recovery"));
                using (var window = new QuickNoteWindow(store, Path.Combine(root, "menu-state"))) {
                    // Show without Toggle: no note is loaded and no clipboard action is invoked.
                    window.Show(); Pump(20);
                    var menus = (System.ComponentModel.Container)Field(window, "transientMenus");
                    int rounds = shutdown == 0 ? 3 : 1;
                    for (int round = 0; round < rounds; round++) {
                        Call(window, method);
                        Check(menus.Components.Count == 1, method + " owns one temporary menu");
                        var menu = (ContextMenuStrip)menus.Components[0];
                        Check(menu.Visible && !menu.IsDisposed, method + " opens normally");
                        if (shutdown != 2) {
                            var message = Message.Create(menu.Handle, 0x100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
                            menu.PreProcessMessage(ref message);
                            Check(!menu.Visible, method + " Escape closes without a disposal exception");
                        }
                        if (shutdown != 0) window.Dispose();
                        Pump(30);
                        Check(menu.IsDisposed && menus.Components.Count == 0, method + " releases menus after close or host shutdown");
                    }
                }
            }
        }
        Console.WriteLine("Quick-note menu Escape, repeated reopen and host shutdown passed; clipboard actions were never invoked.");
    }
    static void Append(QuickNoteWindow window, string text) {
        var editor = (RichTextBox)Field(window, "editor"); editor.Select(editor.TextLength, 0); Document(window).InsertText(text);
    }
    static byte[] Preview(QuickNoteStore store, string relative) {
        string path = store.ResolveImage(relative); return path == null ? null : File.ReadAllBytes(path);
    }
    static string Escaped(string text) { return text.Replace("\\", "\\\\").Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); }
    static void CheckShortcutRouting() {
        // These are callback spies, never the window's default clipboard
        // providers. HandleKey consumes every unmatched chord before base.
        using (var input = new QuickNoteInputBox()) {
            int paste = 0, copy = 0, cut = 0, unmatched = 0;
            input.UserPaste = delegate { paste++; };
            input.UserCopyOrCut = delegate(bool isCut) { if (isCut) cut++; else copy++; };
            input.HandleKey = delegate { unmatched++; return true; };
            Keys[] chords = {
                Keys.Control | Keys.C, Keys.Control | Keys.X, Keys.Control | Keys.V,
                Keys.Control | Keys.Insert, Keys.Shift | Keys.Insert, Keys.Shift | Keys.Delete,
                Keys.C, Keys.Shift | Keys.C, Keys.Alt | Keys.C, Keys.Insert, Keys.Delete,
                Keys.Control | Keys.Alt | Keys.C, Keys.Control | Keys.Alt | Keys.X, Keys.Control | Keys.Alt | Keys.V, Keys.Control | Keys.Alt | Keys.Insert,
                Keys.Control | Keys.Shift | Keys.C, Keys.Control | Keys.Shift | Keys.X, Keys.Control | Keys.Shift | Keys.V, Keys.Control | Keys.Shift | Keys.Insert, Keys.Control | Keys.Shift | Keys.Delete,
                Keys.Shift | Keys.Alt | Keys.Insert, Keys.Shift | Keys.Alt | Keys.Delete,
                Keys.Control | Keys.Shift | Keys.Alt | Keys.C, Keys.Control | Keys.Shift | Keys.Alt | Keys.X, Keys.Control | Keys.Shift | Keys.Alt | Keys.V,
                Keys.Control | Keys.Shift | Keys.Alt | Keys.Insert, Keys.Control | Keys.Shift | Keys.Alt | Keys.Delete
            };
            var failures = new List<string>();
            foreach (Keys chord in chords) {
                paste = copy = cut = unmatched = 0;
                var message = Message.Create(input.Handle, 0x100, new IntPtr((int)(chord & Keys.KeyCode)), new IntPtr(1));
                bool handled = (bool)Call(input, "ProcessCmdKey", message, chord);
                int expectedPaste = chord == (Keys.Control | Keys.V) || chord == (Keys.Shift | Keys.Insert) ? 1 : 0;
                int expectedCopy = chord == (Keys.Control | Keys.C) || chord == (Keys.Control | Keys.Insert) ? 1 : 0;
                int expectedCut = chord == (Keys.Control | Keys.X) || chord == (Keys.Shift | Keys.Delete) ? 1 : 0;
                if (!handled || paste != expectedPaste || copy != expectedCopy || cut != expectedCut)
                    failures.Add(chord + " => paste=" + paste + ", copy=" + copy + ", cut=" + cut + ", unmatched=" + unmatched + ", handled=" + handled);
            }
            Check(failures.Count == 0, "Only exact Ctrl+C/X/V/Insert and Shift+Insert/Delete may route clipboard commands; unexpected callback routes: " + string.Join("; ", failures.ToArray()));
            Console.WriteLine("Exact shortcut routing passed: " + chords.Length + " synthetic CmdKey cases; clipboard providers were replaced by counting spies.");
        }
    }
    static void CheckSyntheticNewlines(string vault, string root) {
        // This deliberately synthesizes the command route and its WM_CHAR.
        // It changes no global keyboard state and is not physical-key or IME evidence.
        foreach (string newline in new string[] { "\n", "\r\n" }) {
            string label = newline == "\n" ? "LF" : "CRLF";
            var store = new QuickNoteStore(vault, "合成换行-" + label, Path.Combine(root, "newline-recovery-" + label));
            var initial = store.Load(DateTime.Today); string expected = "首" + newline + "尾"; store.Save(initial, expected);
            using (var window = new QuickNoteWindow(store, Path.Combine(root, "newline-state-" + label))) {
                window.Toggle(); Settled(window);
                var input = (QuickNoteInputBox)Field(window, "editor");
                Check(window.ReadMarkdown() == expected, "The synthetic newline fixture loads exact " + label + " Markdown");
                foreach (Keys chord in new Keys[] { Keys.Enter, Keys.Shift | Keys.Enter }) {
                    input.Select(input.TextLength, 0);
                    var key = Message.Create(input.Handle, 0x100, new IntPtr((int)Keys.Return), new IntPtr(0x001c0001));
                    Check(!(bool)Call(input, "ProcessCmdKey", key, chord), "Synthetic " + chord + " must proceed to character handling");
                    string before = expected;
                    var character = Message.Create(input.Handle, 0x102, new IntPtr(13), new IntPtr(0x001c0001));
                    Call(input, "WndProc", character); expected += newline;
                    Check(window.ReadMarkdown() == expected, "Synthetic " + chord + " inserts exactly one " + label + " newline");
                    Check(input.CanUndo, "A synthetic newline remains undoable"); input.Undo();
                    Check(window.ReadMarkdown() == before, "Undo removes exactly the synthetic newline");
                    Check(input.CanRedo, "A synthetic newline remains redoable"); input.Redo();
                    Check(window.ReadMarkdown() == expected, "Redo restores the exact synthetic newline");
                    window.Toggle(); Settled(window);
                    Check(!window.Visible && store.Load(DateTime.Today).Text == expected, "Hiding durably saves the synthetic newline");
                    window.Toggle(); Settled(window);
                    Check(window.ReadMarkdown() == expected, "Reopening preserves the exact synthetic newline");
                }
                Check(window.FlushAndClose(), "The synthetic newline fixture closes cleanly");
            }
            Console.WriteLine("Synthetic Enter/ShiftEnter route, Undo/Redo and reopen passed for " + label + "; not a real IME or physical keyboard test.");
        }
    }
    static bool SameBytes(byte[] first, byte[] second) {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
        return true;
    }
    static void AwaitImagePreparationCallback(QuickNoteWindow window) {
        // The RED implementation releases workerBusy immediately after inserting.
        // A corrected implementation may keep it busy with a deferred completion.
        // Either observation proves the UI callback ran; no new field is required
        // before the actual no-insertion regression can fail meaningfully.
        FieldInfo completed = typeof(QuickNoteWindow).GetField("completedImage", BindingFlags.Instance | BindingFlags.NonPublic);
        var watch = System.Diagnostics.Stopwatch.StartNew(); bool completedCallback = false;
        do {
            Pump(10);
            completedCallback = !(bool)Field(window, "workerBusy") || (completed != null && completed.GetValue(window) != null);
        } while (!completedCallback && watch.ElapsedMilliseconds < 5000);
        Check(completedCallback, "Image preparation reaches its UI completion callback within five seconds");
    }
    static void CheckDeferredImageComposition(string vault, string root) {
        // Synthetic composition state only: this protects the asynchronous image
        // completion boundary, without claiming real IME or keyboard acceptance.
        foreach (string scenario in new string[] { "new-text", "no-text-change", "undo-deferred" }) {
            bool addText = scenario != "no-text-change", cancel = scenario == "undo-deferred";
            var store = new QuickNoteStore(vault, "组合延迟图片-" + scenario, Path.Combine(root, "composition-image-recovery-" + scenario));
            var initial = store.Load(DateTime.Today); const string original = "前后"; store.Save(initial, original);
            byte[] diskBefore = File.ReadAllBytes(initial.Path);
            using (var window = new QuickNoteWindow(store, Path.Combine(root, "composition-image-state-" + scenario)))
            using (var bitmap = new Bitmap(8, 4)) {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.RoyalBlue);
                window.Toggle(); Settled(window);
                var input = (QuickNoteInputBox)Field(window, "editor");
                input.Select(1, 0);
                var data = new DataObject(); data.SetData(DataFormats.Bitmap, bitmap);
                window.PasteData(data);
                Check((int)Field(window, "queuedImageCount") == 1 && (bool)Field(window, "workerBusy"), "The local image is queued before synthetic composition begins: " + scenario);
                typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
                if (addText) Append(window, "组合中的新字");
                input.Select(input.TextLength, 0);
                string partial = window.ReadMarkdown(), nativeText = input.Text, selectedText = input.SelectedText;
                int selectionStart = input.SelectionStart, selectionLength = input.SelectionLength;
                using (NativeNoteDocument.LiveRange liveCaret = Document(window).CaptureRange(selectionStart, selectionStart + selectionLength)) {
                    AwaitImagePreparationCallback(window);
                    Pump(650); // Include the normal 500ms autosave interval while composing.
                    Check(Document(window).ObjectPositions().Length == 0, "A prepared image must not enter the document during composition: " + scenario + "; actual=[" + Escaped(window.ReadMarkdown()) + "]");
                    Check(window.ReadMarkdown() == partial && input.Text == nativeText, "Image completion leaves all composing text unchanged: " + scenario);
                    Check(input.SelectionStart == selectionStart && input.SelectionLength == selectionLength && input.SelectedText == selectedText, "Image completion leaves the active composition selection unchanged: " + scenario);
                    Check((int)Field(window, "queuedImageCount") == 1, "A deferred prepared image retains its queue and resource reservation: " + scenario);
                    Check(SameBytes(diskBefore, File.ReadAllBytes(initial.Path)) && !File.Exists(initial.RecoveryPath) && Field(window, "pendingText") == null, "Neither the note nor its recovery snapshot receives partial composition text: " + scenario);
                    if (cancel) {
                        Check(input.CanUndo, "The deferred-cancellation fixture has a real native text operation to undo");
                        input.Undo();
                        Check(window.ReadMarkdown() == original && Document(window).ObjectPositions().Length == 0, "Native Undo removes the newer text while the prepared image remains uninserted");
                    }
                    // Exercise the real production composition-end callback. In
                    // no-text-change this must restart progress despite dirty=false.
                    input.CompositionChanged(false);
                    Check(!(bool)Field(window, "composing"), "The production composition-end callback clears synthetic composition: " + scenario);
                    Settled(window); Pump(650); Settled(window);
                    if (cancel) {
                        Check(window.ReadMarkdown() == original && Document(window).ObjectPositions().Length == 0, "An image cancelled by native Undo while deferred never reappears after composition ends");
                        Check(store.Load(DateTime.Today).Text == original, "Cancelling a deferred image preserves the exact durable original note");
                    } else {
                        string committed = window.ReadMarkdown();
                        var image = System.Text.RegularExpressions.Regex.Match(committed, @"!\[图片\]\(<([^>]+)>\)");
                        Check(image.Success && Document(window).ObjectPositions().Length == 1 && System.Text.RegularExpressions.Regex.Matches(committed, @"!\[图片\]\(<([^>]+)>\)").Count == 1, "Composition end inserts the prepared image exactly once: " + scenario);
                        string expected = partial.Insert(1, image.Value + "\n");
                        Check(committed == expected, "Deferred insertion preserves exact newer text and the original live image anchor: " + scenario + "; actual=[" + Escaped(committed) + "]");
                        Check(input.SelectionStart == liveCaret.Start && input.SelectionLength == liveCaret.End - liveCaret.Start && input.SelectedText == selectedText, "Deferred insertion preserves the user's live caret: " + scenario);
                        Check(store.Load(DateTime.Today).Text == expected && !store.Load(DateTime.Today).HasRecovery && !(bool)Field(window, "dirty"), "The production timer durably saves the one committed image and complete text: " + scenario);
                        Check(input.CanUndo, "The committed deferred image remains natively undoable: " + scenario);
                        input.Undo();
                        Check(window.ReadMarkdown() == partial && Document(window).ObjectPositions().Length == 0, "One Undo removes only the deferred image and its newline, retaining all newer text: " + scenario);
                        Pump(650); Settled(window);
                        Check(store.Load(DateTime.Today).Text == partial, "Undo of the deferred image is durably saved without losing newer text: " + scenario);
                    }
                }
                Check((int)Field(window, "queuedImageCount") == 0 && (long)Field(window, "queuedImagePixels") == 0, "Deferred completion or cancellation releases the image budget exactly once: " + scenario);
                Check(window.FlushAndClose(), "The deferred image composition fixture closes cleanly: " + scenario);
            }
            Console.WriteLine("Synthetic deferred-image composition passed: " + scenario + "; no real IME, clipboard or global input was used.");
        }
    }
    static string FailureRecoveryPath(QuickNoteWindow window, string recoveryRoot, string vault) {
        FieldInfo field = typeof(QuickNoteWindow).GetField("loadFailureRecoveryPath", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(field != null, "A failed import must cache loadFailureRecoveryPath for safe recovery access");
        string path = (string)field.GetValue(window);
        Check(!string.IsNullOrEmpty(path) && Path.IsPathRooted(path), "A failed import must expose its absolute preserved-draft path");
        string full = Path.GetFullPath(path);
        string recoveryPrefix = Path.GetFullPath(recoveryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string vaultPrefix = Path.GetFullPath(vault).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Check(full.StartsWith(recoveryPrefix, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(vaultPrefix, StringComparison.OrdinalIgnoreCase), "A failed import recovery path stays inside the isolated recovery directory outside the vault");
        Check(File.Exists(full), "The cached failed-import recovery path must identify an existing preserved file");
        return full;
    }
    static void CheckFailedImportRecovery(string vault, string root) {
        string recoveryRoot = Path.Combine(root, "unsupported-recovery");
        var store = new QuickNoteStore(vault, "失败恢复路径", recoveryRoot);
        var original = store.Load(DateTime.Today); store.Save(original, "磁盘原文保持不变");
        string draft = "需要保全的恢复草稿\0末尾仍在"; store.SaveRecovery(original, draft);
        var matched = store.Load(DateTime.Today);
        Check(matched.HasRecovery && matched.RecoveryRevision == matched.Revision && matched.RecoveryText == draft, "The unsupported draft fixture starts with a matching recovery revision");
        byte[] originalBytes = File.ReadAllBytes(original.Path), recoveryBytes = File.ReadAllBytes(original.RecoveryPath);
        using (var window = new QuickNoteWindow(store, Path.Combine(root, "unsupported-recovery-state"))) {
            window.Toggle(); Settled(window);
            Check(((RichTextBox)Field(window, "editor")).ReadOnly, "Unsupported matching-revision recovery remains read-only after import fails");
            Check(SameBytes(originalBytes, File.ReadAllBytes(original.Path)), "Failed recovery import never rewrites the original disk bytes");
            Check(SameBytes(recoveryBytes, File.ReadAllBytes(original.RecoveryPath)), "Failed recovery import preserves every recovery-file byte including NUL");
            string path = FailureRecoveryPath(window, recoveryRoot, vault);
            Check(string.Equals(path, Path.GetFullPath(original.RecoveryPath), StringComparison.OrdinalIgnoreCase), "A failed matching-revision import exposes the original complete recovery file");
            Check(store.Load(DateTime.Today).RecoveryText == draft, "The cached failure path still retains the exact unsupported draft");
            Check(window.FlushAndClose(), "A clean failed recovery import can close without discarding its preserved draft");
        }
        Check(SameBytes(recoveryBytes, File.ReadAllBytes(original.RecoveryPath)), "Closing a failed recovery import keeps the original recovery bytes");

        string reloadRecoveryRoot = Path.Combine(root, "unsupported-reload-recovery");
        var reloadStore = new QuickNoteStore(vault, "失败重载路径", reloadRecoveryRoot);
        var prior = reloadStore.Load(DateTime.Today); reloadStore.Save(prior, "原有磁盘正文\n");
        string archived = null, unsaved = null;
        using (var window = new QuickNoteWindow(reloadStore, Path.Combine(root, "unsupported-reload-state"))) {
            window.Toggle(); Settled(window);
            Append(window, "应保全的本地未存输入"); unsaved = window.ReadMarkdown();
            File.WriteAllText(prior.Path, "Obsidian 外部版本\0保持原样", new System.Text.UTF8Encoding(false));
            byte[] externalBytes = File.ReadAllBytes(prior.Path);
            Call(window, "ReloadDiskKeepingDraft"); Settled(window);
            Check(((RichTextBox)Field(window, "editor")).ReadOnly, "A failed import after archive keeps the editor read-only");
            Check(SameBytes(externalBytes, File.ReadAllBytes(prior.Path)), "Failed reload never rewrites unsupported external disk content");
            string[] archives = Directory.GetFiles(reloadRecoveryRoot, "*.md");
            Check(archives.Length == 1 && File.ReadAllText(archives[0]) == unsaved, "Reload archives the complete former editor content before the unsupported import fails");
            archived = FailureRecoveryPath(window, reloadRecoveryRoot, vault);
            Check(string.Equals(archived, Path.GetFullPath(archives[0]), StringComparison.OrdinalIgnoreCase) && File.ReadAllText(archived) == unsaved, "The failed reload cache exposes the newly archived exact draft");
        }
        Check(File.Exists(archived) && File.ReadAllText(archived) == unsaved, "Disposing a failed reload keeps the cached archive and complete draft available");
    }
    static byte[] Png() {
        using (var image = new Bitmap(8, 4)) using (var graphics = Graphics.FromImage(image)) using (var stream = new MemoryStream()) {
            graphics.Clear(Color.RoyalBlue); image.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
    static byte[] PatternPng(float dpi) {
        using (var image = new Bitmap(80, 32, PixelFormat.Format32bppArgb)) using (var stream = new MemoryStream()) {
            image.SetResolution(dpi, dpi);
            for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                image.SetPixel(x, y, Color.FromArgb(255, x * 3 % 256, y * 7 % 256, (x + y) * 5 % 256));
            image.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
    static bool SamePixels(byte[] expected, byte[] actual) {
        using (var first = new Bitmap(new MemoryStream(expected))) using (var second = new Bitmap(new MemoryStream(actual))) {
            if (first.Size != second.Size) return false;
            for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
    }
    static void CheckWindowGeometry(QuickNoteStore store, string root) {
        var minimum = new Size(280, 240);
        Rectangle[,] samples = {
            { new Rectangle(0, 0, 1024, 700), new Rectangle(444, 60, 556, 669), new Rectangle(444, 31, 556, 669) },
            { new Rectangle(-1024, 40, 1024, 680), new Rectangle(-580, 100, 556, 669), new Rectangle(-580, 51, 556, 669) },
            { new Rectangle(0, 0, 480, 360), new Rectangle(-100, 60, 556, 669), new Rectangle(0, 0, 480, 360) },
            { new Rectangle(1024, 0, 800, 600), new Rectangle(1800, 580, 300, 260), new Rectangle(1524, 340, 300, 260) },
            { new Rectangle(0, 0, 640, 480), new Rectangle(30, 40, 100, 100), new Rectangle(30, 40, 280, 240) }
        };
        for (int index = 0; index < samples.GetLength(0); index++) {
            Rectangle fitted = QuickNoteWindow.FitWindowBounds(samples[index, 1], samples[index, 0], minimum);
            Check(fitted == samples[index, 2], "Default/restored bounds match the expected small-screen or offset-monitor rectangle: " + index);
            Check(QuickNoteWindow.FitWindowBounds(fitted, samples[index, 0], minimum) == fitted, "Screen constraints are idempotent across reopen: " + index);
        }
        foreach (bool initialScale in new bool[] { false, true }) foreach (bool nearScreenHeight in new bool[] { false, true }) {
            Rectangle? previousBounds = null;
            for (int cycle = 0; cycle < 3; cycle++) {
                using (var geometry = new QuickNoteWindow(store, Path.Combine(root, "geometry-state-" + initialScale + "-" + nearScreenHeight))) {
                    // A persisted rectangle is already in screen pixels. Force
                    // an initial 150% WinForms scale transition independently
                    // of the machine's current display DPI.
                    if (initialScale) geometry.AutoScaleDimensions = new SizeF(64, 64);
                    if (nearScreenHeight) {
                        geometry.PerformAutoScale();
                        // Reproduce a short screen without changing the user's
                        // display settings: the initial 60px inset cannot fit.
                        geometry.Size = new Size(geometry.Width, Screen.PrimaryScreen.WorkingArea.Height - 20);
                    }
                    geometry.Toggle(); Pump(100);
                    Rectangle bounds = geometry.Bounds, area = Screen.FromRectangle(bounds).WorkingArea;
                    Console.WriteLine("Quick note geometry scale=" + initialScale + " nearScreenHeight=" + nearScreenHeight + " cycle " + cycle + ": " + bounds + "; DPI=" + geometry.DeviceDpi + "; workingArea=" + area);
                    Check(area.Contains(bounds), "The first and restored windows fit the working area, including a short screen");
                    if (previousBounds.HasValue) Check(bounds == previousBounds.Value, "Persisted bounds must not grow across recreated windows: previous=" + previousBounds.Value + "; next=" + bounds + "; workingArea=" + area);
                    previousBounds = bounds;
                    geometry.Toggle(); Check(!geometry.Visible, "Geometry probe hides after flushing");
                    Check(geometry.FlushAndClose(), "Geometry probe closes without pending text");
                }
            }
        }
    }
    static void CheckFormatting(string vault, string root) {
        var store = new QuickNoteStore(vault, "格式测试", Path.Combine(root, "format-recovery"));
        using (var window = new QuickNoteWindow(store, Path.Combine(root, "format-state"))) {
            window.Toggle(); Settled(window);
            var input = (RichTextBox)Field(window, "editor");
            var toolbar = (Control)Field(window, "formatBar");
            Check(toolbar.Controls.Count == 7, "Six formatting/image actions plus a save-location icon are available");
            var tips = (ToolTip)Field(window, "tips");
            foreach (Control button in toolbar.Controls) Check(!string.IsNullOrWhiteSpace(tips.GetToolTip(button)), "Every toolbar button has a hover explanation: " + button.Name);
            Check(tips.ShowAlways, "Hover explanations are enabled for the topmost tool window");
            var status = (LinkLabel)Field(window, "status");
            Check(!status.Visible, "Normal save status does not take a second footer row");
            foreach (Control button in toolbar.Controls) {
                int popups = 0;
                PopupEventHandler hover = delegate(object sender, PopupEventArgs e) { if (e.AssociatedControl == button) popups++; };
                tips.Popup += hover;
                typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
                Pump(60); Check(popups > 0, "Hover actually opens this button's tooltip: " + button.Name);
                IntPtr tipWindow = (IntPtr)typeof(ToolTip).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tips, null);
                Check(IsWindowVisible(tipWindow) && (WindowStyle(tipWindow, -20) & 8) != 0, "Tooltip is visible above the topmost note: " + button.Name);
                NativeRectangle tipBounds; Check(GetWindowRect(tipWindow, out tipBounds), "The native tooltip has measurable bounds");
                Rectangle tipRectangle = Rectangle.FromLTRB(tipBounds.Left, tipBounds.Top, tipBounds.Right, tipBounds.Bottom);
                Check(!tipRectangle.IntersectsWith(button.RectangleToScreen(button.ClientRectangle)), "Tooltip must not cover any hover point on its button: " + button.Name + "; tip=" + tipRectangle + "; button=" + button.RectangleToScreen(button.ClientRectangle));
                if (button.Name == "format-location") {
                    Pump(750);
                    NativeRectangle held; Check(GetWindowRect(tipWindow, out held), "Held location tooltip has bounds");
                    Check(IsWindowVisible(tipWindow) && popups == 1 && Rectangle.FromLTRB(held.Left, held.Top, held.Right, held.Bottom) == tipRectangle, "A held multiline location tooltip stays visible without moving or repeatedly reopening");
                }
                typeof(Control).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
                Pump(20); Check(!IsWindowVisible(tipWindow), "Leaving hides the tooltip: " + button.Name); tips.Popup -= hover;
            }
            int statusShows = 0; status.VisibleChanged += delegate { if (status.Visible) statusShows++; };
            string[] formats = { "bold", "italic", "underline", "strike" };
            string[] expected = { "<b>中文重点</b>", "<i>中文重点</i>", "<u>中文重点</u>", "<s>中文重点</s>" };
            FontStyle[] styles = { FontStyle.Bold, FontStyle.Italic, FontStyle.Underline, FontStyle.Strikeout };
            for (int i = 0; i < formats.Length; i++) {
                input.SelectAll(); Document(window).InsertText("中文重点"); input.SelectAll();
                ((Button)toolbar.Controls["format-" + formats[i]]).PerformClick();
                Check(input.Text == "中文重点" && input.SelectionFont != null && (input.SelectionFont.Style & styles[i]) != 0, "Toolbar renders a real native style without visible markup: " + formats[i]);
                Check(window.ReadMarkdown() == expected[i], "Formatting writes portable Markdown: " + formats[i]);
                Check(input.SelectedText == "中文重点", "Formatting preserves the selected words");
                input.Undo(); Check(window.ReadMarkdown() == "中文重点", "One undo removes the complete formatting action");
                input.Redo(); Check(window.ReadMarkdown() == expected[i], "Redo restores the format");
                input.Select(0, 4);
                Call(window, "ApplyFormat", formats[i]); Check(window.ReadMarkdown() == "中文重点", "Clicking again removes the surrounding markers");
            }
            input.SelectAll(); Document(window).InsertText("重点"); input.SelectAll();
            Call(window, "ApplyFormat", "bold"); Call(window, "ApplyFormat", "italic");
            Check(window.ReadMarkdown() == "<b><i>重点</i></b>" && input.SelectionFont.Bold && input.SelectionFont.Italic, "Italic combines visually with bold");
            Call(window, "ApplyFormat", "italic"); Check(window.ReadMarkdown() == "<b>重点</b>", "Removing italic keeps bold intact");
            input.SelectAll(); Call(window, "ApplyFormat", "bold");
            input.SelectAll(); Document(window).InsertText("中文重点"); input.Select(0, 2); Call(window, "ApplyFormat", "bold");
            input.SelectAll(); Call(window, "ApplyFormat", "italic");
            input.Select(0, 2); Check(input.SelectionFont.Bold && input.SelectionFont.Italic, "Applying italic to a mixed selection preserves existing bold");
            input.Select(2, 2); Check(!input.SelectionFont.Bold && input.SelectionFont.Italic, "Applying italic does not extend bold into previously plain text");
            Autosaved(window); string mixed = window.ReadMarkdown(); input.Undo(); Autosaved(window);
            Check(store.Load(DateTime.Today).Text == "<b>中文</b>重点", "Font-only undo is durably autosaved");
            input.Redo(); Autosaved(window); Check(store.Load(DateTime.Today).Text == mixed, "Font-only redo is durably autosaved");
            input.SelectAll(); Call(window, "ApplyFormat", "italic"); input.Select(0, 2); Call(window, "ApplyFormat", "bold");
            input.SelectAll(); Document(window).InsertText("第一项\r\n第二项\r\n第三项");
            input.Select(1, 7); Call(window, "ApplyFormat", "bullet");
            Check(window.ReadMarkdown() == "- 第一项\r\n- 第二项\r\n第三项", "Bullets affect only the selected lines and retain CRLF");
            Call(window, "ApplyFormat", "bullet");
            Check(window.ReadMarkdown() == "第一项\r\n第二项\r\n第三项", "Bullets toggle off for the same selected lines");
            input.SelectAll(); Document(window).InsertText("第一项"); input.Select(input.TextLength, 0);
            Call(window, "ApplyFormat", "bullet");
            Check(input.SelectionLength == 0 && input.SelectionStart == input.TextLength, "Toggling a list with a caret preserves the insertion point");
            ((QuickNoteInputBox)input).InsertNewline(); Document(window).InsertText("第二项");
            string listNewline = (string)Field(window, "newline");
            Check(window.ReadMarkdown() == "- 第一项" + listNewline + "- 第二项", "Enter continues a populated bullet list: " + window.ReadMarkdown());
            ((QuickNoteInputBox)input).InsertNewline(); ((QuickNoteInputBox)input).InsertNewline(); Document(window).InsertText("普通段落");
            Check(window.ReadMarkdown() == "- 第一项" + listNewline + "- 第二项" + listNewline + "普通段落", "Enter on an empty bullet exits the list without an extra blank line: " + window.ReadMarkdown());
            Check(!status.Visible && !status.Text.Contains("正在输入"), "Typing and autosaving never display a flashing input status");
            Autosaved(window); Check(!status.Visible, "Successful autosave keeps the footer quiet");
            Check(statusShows == 0, "Normal edit/save cycles never briefly show and hide a status row");
            input.SelectAll(); Document(window).InsertText("首项"); input.Select(input.TextLength, 0); Call(window, "ApplyFormat", "bullet");
            using (var bitmap = new Bitmap(24, 24)) { var data = new DataObject(); data.SetData(DataFormats.Bitmap, bitmap); window.PasteData(data); Settled(window); }
            input.SelectAll(); Call(window, "ApplyFormat", "bold"); Autosaved(window); string styledList = window.ReadMarkdown();
            window.Toggle(); Settled(window); window.Toggle(); Settled(window);
            Check(Document(window).RawText().StartsWith("\u2022 首项") && Document(window).ObjectPositions().Length == 1 && window.ReadMarkdown() == styledList, "A styled bullet with a pasted image autosaves and reopens intact");
            input.Select(Document(window).ObjectPositions()[0] + 1, 0); Call(window, "ApplyFormat", "bold"); ((QuickNoteInputBox)input).InsertNewline(); Document(window).InsertText("续项");
            Check(Document(window).RawText().Contains("\u2022 续项"), "Enter at the end of a styled image list item continues it after saving and reopening");
            input.SelectAll(); Document(window).InsertText(""); Call(window, "ApplyFormat", "bold");
            Check(input.Text == "" && input.SelectionStart == 0 && input.SelectionFont.Bold, "Empty-note formatting changes the typing style without inserting symbols");
            Document(window).InsertText("接着输入"); Check(input.Text == "接着输入" && window.ReadMarkdown() == "<b>接着输入</b>", "Inserted text inherits the chosen typing style");
            input.SelectAll(); Call(window, "ApplyFormat", "bold");
            input.SelectAll(); Document(window).InsertText("中文选词"); input.SelectAll();
            typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
            Call(window, "ApplyFormat", "bold"); Check(window.ReadMarkdown() == "中文选词", "Composition cannot be replaced by toolbar actions");
            typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, false);
            var key = new KeyEventArgs(Keys.Control | Keys.B); Call(window, "EditorKeyDown", input, key);
            Check(key.Handled && input.Text == "中文选词" && window.ReadMarkdown() == "<b>中文选词</b>", "Ctrl+B uses the same visual and persisted formatting action");
            input.Select(input.TextLength, 0);
            using (var bitmap = new Bitmap(24, 24)) {
                var data = new DataObject(); data.SetData(DataFormats.Bitmap, bitmap); window.PasteData(data); Settled(window);
            }
            string imageMarkdown = window.ReadMarkdown(); int objects = Document(window).ObjectPositions().Length;
            input.SelectAll(); Call(window, "ApplyFormat", "strike");
            Check(input.SelectionFont.Strikeout && window.ReadMarkdown().Contains("<s>") && Document(window).ObjectPositions().Length == objects, "Formatting preserves inline image identities and references");
            input.Undo(); Check(window.ReadMarkdown() == imageMarkdown, "Undoing format retains the image");
            input.Select(0, 4); Call(window, "ApplyFormat", "bold"); Autosaved(window);
            string saved = window.ReadMarkdown(); Check(store.Load(DateTime.Today).Text == saved, "Formatted content is autosaved exactly");
            window.Toggle(); Settled(window); window.Toggle(); Settled(window);
            Check(window.ReadMarkdown() == saved && Document(window).ObjectPositions().Length == objects && !input.Text.Contains("<b>") && !input.Text.Contains("**"), "Reopen renders formatting and retains the image without markup");
            window.ClientSize = new Size(280, 270); window.PerformLayout();
            foreach (Control button in toolbar.Controls) Check(toolbar.ClientRectangle.Contains(button.Bounds), "Toolbar fits the minimum width: " + button.Name + "; bar=" + toolbar.ClientRectangle + "; button=" + button.Bounds);
            for (int i = 1; i < toolbar.Controls.Count; i++) Check(Math.Abs((toolbar.Controls[i].Left - toolbar.Controls[i - 1].Left) - (toolbar.Controls[1].Left - toolbar.Controls[0].Left)) <= 1, "Footer icons are evenly spaced: " + i);
            Check(toolbar.Controls["format-location"].Right >= toolbar.ClientSize.Width - 14, "Save location is the bottom-right action");
            input.SelectAll(); Document(window).InsertText("会议速记\r\n确认下周排期\r\n整理客户反馈\r\n\r\n重点：周五前完成"); input.Select(0, 4); Call(window, "ApplyFormat", "bold");
            input.Select(5, 13); Call(window, "ApplyFormat", "bullet"); Autosaved(window);
            input.Select(input.TextLength, 0);
            window.ClientSize = new Size(360, 420); window.PerformLayout();
            using (var screenshot = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(screenshot, new Rectangle(Point.Empty, window.Size)); screenshot.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quick-note-formatting.png")); }
            Check(window.FlushAndClose(), "The formatting note closes after saving");
        }
    }
    static void CheckNavigation(string vault, string root) {
        var store = new QuickNoteStore(vault, "navigation", Path.Combine(root, "navigation-recovery"));
        var seed = store.Load(DateTime.Today); store.Save(seed, "旧版日期便签");
        var first = store.LoadFile("产品讨论.md");
        string image = store.StoreImage(Png());
        var body = new System.Text.StringBuilder();
        for (int i = 0; i < 140; i++) body.Append("第" + i + "行，测试滚动和正文位置。\n");
        store.Save(first, body + "![图片](<" + image + ">)\n");
        var second = store.LoadFile("会议摘录与很长的完整名称.md"); store.Save(second, "另一篇便签\n独立正文");
        var empty = store.NewNote();
        Check(empty.Revision == "missing" && !File.Exists(empty.Path), "New note allocation does not create an empty file");
        Check(empty.Path != store.NewNote().Path, "New note identities cannot collide in the same second");
        foreach (string unsafeName in new[] { "../outside.md", "图片/x.md", "C:\\outside.md", "x.md:stream", "x.txt", "CON.md" }) {
            bool rejected = false; try { store.LoadFile(unsafeName); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Named-note loads reject paths, streams, reserved names and non-Markdown files: " + unsafeName);
        }
        Check(Array.IndexOf(store.RecentFiles(), "产品讨论.md") >= 0, "Existing named Markdown files are discoverable without moving or renaming them");
        string state = Path.Combine(root, "navigation-state"), originalFirst = File.ReadAllText(first.Path), originalSecond = File.ReadAllText(second.Path);
        string newPath;
        using (var window = new QuickNoteWindow(store, state, delegate { Thread.Sleep(80); })) {
            window.Toggle(); Settled(window); window.Location = new Point(240, 100);
            var input = (QuickNoteInputBox)Field(window, "editor");
            var header = (Panel)Field(window, "top");
            Call(window, "HideTabs"); Pump(250); Call(header, "OnMouseEnter", EventArgs.Empty); Pump(40);
            Check(Field(window, "tabs") == null || !((Form)Field(window, "tabs")).Visible, "Header hover alone does not reveal the document tabs");
            Check(window.FormBorderStyle == FormBorderStyle.None && header.Controls.Count == 6, "One header contains new, brand, filename, pin, minimize and close");
            Point edgePoint = new Point(window.Right - 2, window.Bottom - 2);
            Check(NativeMessage(window.Handle, 0x84, IntPtr.Zero, new IntPtr((edgePoint.Y << 16) | (edgePoint.X & 0xffff))).ToInt32() == 17, "The single-header window exposes the native bottom-right resize hit target");
            var pin = (Control)Field(window, "pin"); var minimize = (Control)Field(window, "hideButton"); var close = (Control)Field(window, "closeButton");
            Check(pin.Left < minimize.Left && minimize.Left < close.Left, "Header actions follow pin, minimize, close order");
            ((Button)Field(window, "newButton")).PerformClick(); Settled(window);
            newPath = (string)Field(window, "notePath");
            Check(newPath != seed.Path && !File.Exists(newPath) && window.ReadMarkdown() == "", "Plus opens a separate blank note without creating a file");
            Document(window).InsertText("未保存的当前输入"); input.Select(3, 2);
            Call(window, "RequestNote", "产品讨论.md", false); Settled(window);
            Check(File.ReadAllText(newPath) == "未保存的当前输入", "Switch waits for unsaved input to become durable in its own file");
            Check(window.ReadMarkdown() == originalFirst && Document(window).ObjectPositions().Length == 1, "Click loads full note and its local inline image");
            var viewport = (Control)Field(window, "viewport"); var formatting = new NativeRectangle(); FormatRectangle(input.Handle, 0xb2, IntPtr.Zero, ref formatting);
            Check(formatting.Right <= viewport.Width && formatting.Right >= viewport.Width - 8, "Native wrapping ends inside the visible viewport at the current DPI");
            input.Select(35, 3); QuickNoteScrollBar.SetPosition(input, new Point(0, 170));
            Point beforeScroll = QuickNoteScrollBar.Position(input);
            Call(window, "ShowTabs"); Pump(250);
            var rail = (QuickNoteTabs)Field(window, "tabs");
            Rectangle editorBounds = input.Bounds;
            Check(rail.Visible && !((System.Windows.Forms.Timer)Field(rail, "animation")).Enabled, "Tab animation finishes and stops its timer");
            rail.HoverFile(Path.GetFileName(second.Path)); Pump(550);
            Check((bool)Field(window, "previewVisible"), "Dwell opens a separate readonly preview");
            Check(window.ReadMarkdown() == originalFirst && (string)Field(window, "notePath") == first.Path, "Preview never rebinds the editable document or save destination");
            Check(input.SelectionStart == 35 && input.SelectionLength == 3 && QuickNoteScrollBar.Position(input) == beforeScroll, "Preview preserves the original selection and scroll position");
            Check(((RichTextBox)Field(window, "previewEditor")).ReadOnly && !((Panel)Field(window, "formatBar")).Enabled, "Preview disables editing and formatting");
            Check(input.Bounds == editorBounds, "Showing tabs and preview never reflows the underlying editor");
            Check(File.ReadAllText(first.Path) == originalFirst && File.ReadAllText(second.Path) == originalSecond, "Hover does not write either note");
            Call(window, "CancelPreview");
            Check(input.Visible && !(bool)Field(window, "previewVisible") && window.ReadMarkdown() == originalFirst, "Leaving preview restores the untouched editor");
            int targetRow = Array.IndexOf((string[])Field(rail, "files"), Path.GetFileName(second.Path));
            Call(rail, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, rail.Width - 15, targetRow * (36 * rail.DeviceDpi / 96) + 10, 0)); Pump(80);
            var railTip = (ToolTip)Field(rail, "tip");
            IntPtr railTipHandle = (IntPtr)typeof(ToolTip).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(railTip, null);
            NativeRectangle tipBounds; GetWindowRect(railTipHandle, out tipBounds);
            Check(IsWindowVisible(railTipHandle) && !Rectangle.FromLTRB(tipBounds.Left, tipBounds.Top, tipBounds.Right, tipBounds.Bottom).IntersectsWith(rail.Bounds), "Real tab tooltip displays the full filename outside the entire hover rail");
            Check(!Rectangle.FromLTRB(tipBounds.Left, tipBounds.Top, tipBounds.Right, tipBounds.Bottom).IntersectsWith(window.Bounds), "Tab filename tooltip never covers the note content or header");
            Rectangle screen = Screen.FromControl(window).WorkingArea;
            foreach (Point edge in new[] { new Point(screen.Left, screen.Top + 100), new Point(screen.Right - window.Width, screen.Top + 100) }) {
                window.Location = edge; Call(window, "PositionTabs"); Call(rail, "ShowFileTip", "完整文件名.md\n悬停预览 · 点击切换"); Pump(30);
                GetWindowRect(railTipHandle, out tipBounds);
                Check(!IsWindowVisible(railTipHandle) || !Rectangle.FromLTRB(tipBounds.Left, tipBounds.Top, tipBounds.Right, tipBounds.Bottom).IntersectsWith(window.Bounds), "Monitor edge placement never clamps a filename tip over content: " + edge);
            }
            window.Location = new Point(240, 100); Call(window, "PositionTabs");
            Call(window, "CancelPreview");
            rail.HoverFile(Path.GetFileName(second.Path)); Call(window, "StartPreview"); Call(window, "CancelPreview"); Pump(250);
            Check(!(bool)Field(window, "previewVisible"), "Cancelled asynchronous preview cannot reappear from a late result");
            string[] positions = (string[])((string[])Field(rail, "files")).Clone();
            rail.HoverFile(Path.GetFileName(second.Path)); Pump(550); rail.SelectFile(Path.GetFileName(second.Path)); Settled(window);
            Check((string)Field(window, "notePath") == second.Path && !input.ReadOnly && !(bool)Field(window, "previewVisible"), "Click commits the requested file and restores editing");
            Check(String.Join("|", positions) == String.Join("|", (string[])Field(rail, "files")), "Click highlights the selected paper tab without moving the other tab targets");
            Call(window, "RequestNote", "产品讨论.md", false); Settled(window);
            Check(input.SelectionStart == 35 && input.SelectionLength == 3 && QuickNoteScrollBar.Position(input) == beforeScroll, "Switching back restores each file's own view");
            var scrollbar = (QuickNoteScrollBar)Field(window, "scroll");
            Call(scrollbar, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, scrollbar.Width / 2, scrollbar.Height - 10, 0));
            Call(scrollbar, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, scrollbar.Width / 2, scrollbar.Height - 10, 0));
            Check(QuickNoteScrollBar.Position(input).Y > beforeScroll.Y, "The slim scrollbar's full hit area really scrolls the native editor");
            Call(window, "HideTabs"); Pump(250);
            Check(!rail.Visible && !((System.Windows.Forms.Timer)Field(rail, "animation")).Enabled, "Conceal stops the animation and removes the rail");
            Call(window, "ShowTabs"); Pump(250);
            window.ClientSize = new Size(280, 240); window.PerformLayout(); Pump(80);
            Check(header.ClientRectangle.Contains(close.Bounds) && header.ClientRectangle.Contains(pin.Bounds) && !close.Bounds.IntersectsWith(pin.Bounds), "Header actions fit the minimum width");
            Check(((string[])Field(rail, "files")).Length < 5, "Short windows reduce visible tab count");
            window.ClientSize = new Size(360, 420); window.PerformLayout(); Call(window, "ShowTabs"); Pump(250);
            var label = (Label)Field(window, "dateLabel");
            Check(label.Text == "产品讨论.md" && !label.Text.Contains("verbit"), "Header shows logo and current filename without old branding");
            input.Select(0, 0); QuickNoteScrollBar.SetPosition(input, Point.Empty);
            using (var screenshot = new Bitmap(window.Width + rail.Width, window.Height)) {
                using (Graphics g = Graphics.FromImage(screenshot)) g.Clear(Color.FromArgb(240, 240, 238));
                using (var noteShot = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(noteShot, new Rectangle(Point.Empty, window.Size)); using (Graphics g = Graphics.FromImage(screenshot)) g.DrawImageUnscaled(noteShot, rail.Width, 0); }
                using (var tabShot = new Bitmap(rail.Width, rail.Height)) { rail.DrawToBitmap(tabShot, new Rectangle(Point.Empty, rail.Size)); tabShot.MakeTransparent(Color.Fuchsia); using (Graphics g = Graphics.FromImage(screenshot)) g.DrawImageUnscaled(tabShot, 0, header.Bottom); }
                screenshot.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quick-note-tabs.png"));
            }
            Call(window, "RequestNote", Path.GetFileName(newPath), false); Settled(window);
            formatting = new NativeRectangle(); FormatRectangle(input.Handle, 0xb2, IntPtr.Zero, ref formatting);
            Check(formatting.Right <= viewport.Width, "Short notes keep the same safe wrapping width when no thumb is needed");
            Call(window, "NewNote"); Settled(window); string abandoned = (string)Field(window, "notePath");
            Call(window, "RequestNote", Path.GetFileName(newPath), false); Settled(window);
            Check(!File.Exists(abandoned) && !((List<string>)Field(window, "recentFiles")).Contains(Path.GetFileName(abandoned)), "Abandoned blank notes leave neither files nor ghost recent tabs");
            input.Select(2, 1); window.Toggle(); Settled(window);
            Check(window.FlushAndClose(), "Clean note closes normally");
        }
        using (var restored = new QuickNoteWindow(store, state)) {
            restored.Toggle(); Settled(restored);
            var input = (RichTextBox)Field(restored, "editor");
            Check((string)Field(restored, "notePath") == newPath && restored.ReadMarkdown() == "未保存的当前输入", "Restart restores last selected named note instead of today's default");
            Check(input.SelectionStart == 2 && input.SelectionLength == 1, "Restart restores the persisted selection");
            Call(restored, "RequestNote", Path.GetFileName(second.Path), false); Settled(restored);
            File.WriteAllText(second.Path, "外部编辑"); Append(restored, "本地输入");
            Call(restored, "RequestNote", "产品讨论.md", false); Settled(restored);
            Check((string)Field(restored, "notePath") == second.Path && File.ReadAllText(second.Path) == "外部编辑" && store.LoadFile(Path.GetFileName(second.Path)).HasRecovery, "Conflict blocks switching, preserves external text and the named recovery draft");
        }
        string minimizedFile, minimizedState = Path.Combine(root, "minimized-state");
        using (var minimized = new QuickNoteWindow(store, minimizedState)) {
            minimized.Toggle(); Settled(minimized); Call(minimized, "NewNote"); Settled(minimized);
            minimizedFile = (string)Field(minimized, "notePath"); Append(minimized, "最小化前的新输入");
            Call((Control)Field(minimized, "editor"), "OnMouseEnter", EventArgs.Empty);
            ((Button)Field(minimized, "hideButton")).PerformClick(); Autosaved(minimized);
            Check(minimized.WindowState == FormWindowState.Minimized && File.ReadAllText(minimizedFile) == "最小化前的新输入", "Minimizing retains and asynchronously saves new input");
            Check(minimized.FlushAndClose(), "Exit from minimized state saves last-document navigation");
        }
        using (var reopened = new QuickNoteWindow(store, minimizedState)) {
            reopened.Toggle(); Settled(reopened);
            Check((string)Field(reopened, "notePath") == minimizedFile && reopened.ReadMarkdown() == "最小化前的新输入", "Restart after minimizing restores the newly saved file");
            typeof(QuickNoteWindow).GetField("previewOnHover", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(reopened, false);
            Call(reopened, "ShowTabs"); Call(reopened, "HoverNote", "产品讨论.md"); Pump(350);
            Check(!(bool)Field(reopened, "previewVisible"), "Turning off hover preview leaves the selected editor unchanged");
            reopened.FlushAndClose();
        }
        Console.WriteLine("Navigation, isolated hover, file persistence, scroll and conflict tests passed: " + assertions + " assertions; clipboard_access=0; global_input=0");
    }
    static void CheckRefinements(string vault, string root) {
        var store = new QuickNoteStore(vault, "refine", Path.Combine(root, "refine-recovery"));
        string reference = store.StoreImage(Png());
        var seed = store.Load(DateTime.Today); store.Save(seed, "正文开头\n![图片](<" + reference + ">)\n图片后中文");
        byte[] original = File.ReadAllBytes(store.ResolveImage(reference));
        string saved;
        using (var window = new QuickNoteWindow(store, Path.Combine(root, "refine-state"))) {
            window.Toggle(); Settled(window); window.Location = new Point(360, 140);
            var editor = (QuickNoteInputBox)Field(window, "editor");
            var top = (Panel)Field(window, "top"); var bottom = (Panel)Field(window, "formatBar");
            var title = (Label)Field(window, "dateLabel");
            Call(window, "SetChromeVisible", true);
            int center = title.Top + title.Height / 2;
            foreach (Control c in top.Controls) Check(Math.Abs(c.Top + c.Height / 2 - center) <= 1, "Header title/logo/action centers align at actual DPI: " + c.Name);
            Rectangle editorBounds = editor.RectangleToScreen(editor.ClientRectangle);
            editor.Select(1, 2); Point scroll = QuickNoteScrollBar.Position(editor);
            Call(window, "UpdateChromeForPointer", new Point(-10000, -10000));
            Check(!top.Visible && !bottom.Visible && window.Region != null, "Leaving the window hides both navigation bars and clips empty chrome bands");
            Check(editor.RectangleToScreen(editor.ClientRectangle) == editorBounds && editor.SelectionStart == 1 && editor.SelectionLength == 2 && QuickNoteScrollBar.Position(editor) == scroll, "Chrome conceal never reflows content or moves selection/scroll");
            Call(editor, "OnMouseEnter", EventArgs.Empty); Pump(30);
            Check(top.Visible && bottom.Visible && window.Region == null, "Entering text reveals controls without requiring a click");
            Check(editor.RectangleToScreen(editor.ClientRectangle) == editorBounds, "Chrome reveal preserves exact editor geometry");
            int cp = Document(window).ObjectPositions()[0]; editor.Select(cp, 1);
            Rectangle before = Document(window).SelectedImageBounds();
            Check(before.Width > 0 && before.Height > 0, "Native selected picture exposes its actual bounds");
            Console.WriteLine("image_before=" + before + "; cp_position=" + editor.GetPositionFromCharIndex(cp));
            Call(window, "ResizeImageTo", 160);
            Rectangle enlarged = Document(window).SelectedImageBounds();
            Check(Math.Abs(enlarged.Width * 96.0 / editor.DeviceDpi - 160) <= 1, "Resize changes the native image display width");
            Check(Math.Abs(enlarged.Width / (double)enlarged.Height - 2) < .03, "Image enlargement preserves original 2:1 ratio");
            Check(window.ReadMarkdown().Contains("![图片|160](<" + reference + ">)"), "Display width is stored as an Obsidian-compatible Markdown alt width");
            editor.Undo(); Check(window.ReadMarkdown() == seed.Text, "Image resize is one native undo with original image reference intact");
            editor.Redo(); Check(window.ReadMarkdown().Contains("图片|160"), "Redo restores the size and image identity");
            editor.Select(cp, 1); Rectangle drag = Document(window).SelectedImageBounds();
            Point corner = new Point(drag.Right, drag.Bottom);
            Check((bool)Call(window, "HandleImagePointer", 0x201, corner), "Native corner resize is intercepted before unrestricted stretching");
            Point moved = new Point(corner.X + 80, corner.Y + 9);
            Check((bool)Call(window, "HandleImagePointer", 0x200, moved), "Drag uses a lightweight proportional outline");
            Rectangle outline = (Rectangle)Field(window, "imageDragOutline");
            Check(Math.Abs(outline.Width / (double)outline.Height - 2) < .03, "Unequal horizontal/vertical pointer motion cannot distort the outline");
            Check(Document(window).SelectedImageBounds().Size == drag.Size, "Dragging does not repeatedly recreate OLE images or decode pixels");
            var dragTimes = new List<double>();
            for (int frame = 0; frame < 40; frame++) {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Call(window, "HandleImagePointer", 0x200, new Point(corner.X + 40 + frame, corner.Y + 9)); editor.Update();
                watch.Stop(); dragTimes.Add(watch.Elapsed.TotalMilliseconds);
            }
            dragTimes.Sort();
            Console.WriteLine("Proportional drag outline+native paint: frames=40; p95_ms=" + dragTimes[37].ToString("F2") + "; max_ms=" + dragTimes[39].ToString("F2") + "; not_screen_FPS=true");
            Check(Document(window).ObjectPositions().Length == 1 && Document(window).SelectedImageBounds().Size == drag.Size, "Repeated drag frames keep the same image object and original dimensions until mouse-up");
            Call(window, "HandleImagePointer", 0x100, Point.Empty);
            Check((int)Field(window, "imageDragCp") == -1 && !editor.Capture && Document(window).SelectedImageBounds().Size == drag.Size, "Escape cancels sizing without changing the picture");
            Call(window, "HandleImagePointer", 0x201, corner); editor.Select(0, 0);
            Check((int)Field(window, "imageDragCp") == -1 && !editor.Capture, "Changing the selection cancels a pending image drag");
            editor.Select(cp, 1);
            // Send the exact own-HWND message route; no global input or cursor.
            NativeMessage(editor.Handle, 0x201, new IntPtr(1), new IntPtr((corner.Y << 16) | (corner.X & 0xffff)));
            NativeMessage(editor.Handle, 0x200, new IntPtr(1), new IntPtr((moved.Y << 16) | (moved.X & 0xffff)));
            NativeMessage(editor.Handle, 0x202, IntPtr.Zero, new IntPtr((moved.Y << 16) | (moved.X & 0xffff)));
            Check((int)Field(window, "imageDragCp") == -1 && !editor.Capture, "Mouse-up commits and releases capture via production WndProc");
            Rectangle resized = Document(window).SelectedImageBounds();
            Check(resized.Width > drag.Width && Math.Abs(resized.Width / (double)resized.Height - 2) < .03, "Completed corner drag enlarges proportionally");
            Call(window, "ShowEditorMenu");
            var menus = (System.ComponentModel.Container)Field(window, "transientMenus");
            var menu = (ContextMenuStrip)menus.Components[0];
            Check(menu.Items[0].Text == "图片适合窗口" && menu.Items[1].Text == "等比例放大" && menu.Items[2].Text == "等比例缩小", "Selected image exposes proportional fit/enlarge/shrink commands");
            Call(window, "UpdateChromeForPointer", new Point(-10000, -10000)); Check(top.Visible, "An open context menu keeps its controls available");
            menu.Close(); Pump(50);
            Call(window, "ScaleImage", .8); Rectangle smaller = Document(window).SelectedImageBounds();
            Check(smaller.Width < resized.Width && Math.Abs(smaller.Width / (double)smaller.Height - 2) < .03, "Shrink command also keeps aspect ratio");
            Call(window, "SetStatus", "保存失败测试，草稿仍在", true, true);
            Call(window, "UpdateChromeForPointer", new Point(-10000, -10000));
            Check(top.Visible && ((Control)Field(window, "status")).Visible, "Important save errors remain visible when the pointer leaves");
            Call(window, "SetSavedStatus"); Autosaved(window); saved = window.ReadMarkdown();
            Check(saved.StartsWith("正文开头\n") && saved.EndsWith("\n图片后中文"), "Resizing preserves surrounding Chinese text and line breaks");
            Check(Convert.ToBase64String(File.ReadAllBytes(store.ResolveImage(reference))) == Convert.ToBase64String(original), "Original image bytes never change");
            Call(window, "SetChromeVisible", true); editor.Select(cp, 1);
            using (var capture = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(capture, new Rectangle(Point.Empty, window.Size)); capture.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "quick-note-refined.png")); }
        }
        using (var reopened = new QuickNoteWindow(store, Path.Combine(root, "refine-reopen"))) {
            reopened.Toggle(); Settled(reopened); var editor = (RichTextBox)Field(reopened, "editor");
            editor.Select(Document(reopened).ObjectPositions()[0], 1);
            Rectangle bounds = Document(reopened).SelectedImageBounds();
            Check(reopened.ReadMarkdown() == saved && Math.Abs(bounds.Width / (double)bounds.Height - 2) < .03, "Saved image width and ratio survive complete close/reopen");
        }
        Console.WriteLine("Refinement checks passed: " + assertions + "; clipboard_access=0; global_input=0");
    }
    static void CheckRefinementAudit(string vault, string root) {
        int failures = 0;
        foreach (string scenario in new[] { "escape", "scroll", "zoom", "height", "inline" }) {
            try {
                var store = new QuickNoteStore(vault, "audit-" + scenario, Path.Combine(root, "audit-recovery-" + scenario));
                byte[] picture = Png();
                if (scenario == "height") using (var image = new Bitmap(40, 800)) using (var stream = new MemoryStream()) { image.Save(stream, ImageFormat.Png); picture = stream.ToArray(); }
                string reference = store.StoreImage(picture);
                var seed = store.Load(DateTime.Today);
                store.Save(seed, (scenario == "inline" ? "前文" : "") + "![测试](<" + reference + ">)\n" + new string('后', 4000));
                using (var window = new QuickNoteWindow(store, Path.Combine(root, "audit-state-" + scenario))) {
                    window.Toggle(); Settled(window);
                    var editor = (QuickNoteInputBox)Field(window, "editor");
                    editor.Select(Document(window).ObjectPositions()[0], 1); editor.Focus();
                    string before = window.ReadMarkdown();
                    Rectangle bounds = Document(window).SelectedImageBounds();
                    if (scenario == "inline") {
                        Call(window, "SetChromeVisible", true);
                        // Tiny selected pictures can be completely covered by
                        // native selection handles. Print without that overlay.
                        editor.Select(editor.SelectionStart, 0);
                        using (var rendered = new Bitmap(window.Width, window.Height)) {
                            window.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                            rendered.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inline-image.png"));
                            Point offset = window.PointToClient(editor.PointToScreen(Point.Empty));
                            int left = rendered.Width, right = -1, up = rendered.Height, down = -1;
                            for (int y = offset.Y; y < Math.Min(rendered.Height, offset.Y + editor.Height); y++) for (int x = offset.X; x < Math.Min(rendered.Width, offset.X + editor.Width); x++) if (rendered.GetPixel(x, y).ToArgb() == Color.RoyalBlue.ToArgb()) { left = Math.Min(left, x - offset.X); right = Math.Max(right, x - offset.X); up = Math.Min(up, y - offset.Y); down = Math.Max(down, y - offset.Y); }
                            Console.WriteLine("inline_handle_bounds=" + bounds + "; rendered_pixels=" + Rectangle.FromLTRB(left, up, right + 1, down + 1));
                            Check(right >= left && Math.Abs(bounds.Left - left) <= 2 && Math.Abs(bounds.Top - up) <= 2,
                                "Inline image handles match its native rendered origin beside text");
                        }
                        continue;
                    }
                    Point corner = new Point(bounds.Right, bounds.Bottom);
                    Call(window, "HandleImagePointer", 0x201, corner);
                    Check((int)Field(window, "imageDragCp") >= 0, "Audit starts an actual selected-image drag: " + scenario);
                    if (scenario == "escape") {
                        Message key = Message.Create(editor.Handle, 0x100, new IntPtr((int)Keys.Escape), IntPtr.Zero);
                        Call(editor, "ProcessCmdKey", key, Keys.Escape);
                        Check(window.Visible && (int)Field(window, "imageDragCp") == -1 && !editor.Capture && window.ReadMarkdown() == before,
                            "Preprocessed Escape cancels image sizing without hiding the note or changing content");
                        Call(editor, "ProcessCmdKey", key, Keys.Escape); Settled(window);
                        Check(!window.Visible, "A second Escape keeps the existing save-and-hide behavior");
                    } else if (scenario == "scroll") {
                        QuickNoteScrollBar.SetPosition(editor, new Point(0, 90));
                        Call(editor, "OnVScroll", EventArgs.Empty);
                        Check((int)Field(window, "imageDragCp") == -1 && !editor.Capture && window.ReadMarkdown() == before,
                            "Scrolling cancels stale image drag geometry before it can resize the wrong location");
                    } else if (scenario == "zoom") {
                        editor.ZoomFactor = 2;
                        Call(window, "HandleImagePointer", 0x200, new Point(corner.X + 20, corner.Y + 20));
                        Check((int)Field(window, "imageDragCp") == -1 && !editor.Capture && window.ReadMarkdown() == before,
                            "Zooming cancels a captured image handle without modifying its stored dimensions");
                    } else {
                        Call(window, "HandleImagePointer", 0x200, new Point(corner.X + 400, corner.Y + 10));
                        Rectangle outline = (Rectangle)Field(window, "imageDragOutline");
                        Check(outline.Height * 96.0 / editor.DeviceDpi / editor.ZoomFactor <= NativeNoteDocument.MaxImageLogicalSize + 1,
                            "Long-image drag outline obeys the same height safety limit as commit");
                        int expected = outline.Width;
                        Call(window, "HandleImagePointer", 0x202, new Point(corner.X + 400, corner.Y + 10));
                        Console.WriteLine("long_image_before=" + bounds + "; outline=" + outline + "; actual=" + Document(window).SelectedImageBounds() + "; status=" + ((Control)Field(window, "status")).Text);
                        Check(Math.Abs(Document(window).SelectedImageBounds().Width - expected) <= 2,
                            "Committed image matches the proportional drag outline within pixel rounding");
                    }
                }
                Console.WriteLine("PASS refinement audit " + scenario);
            } catch (Exception error) { failures++; Console.WriteLine("FAIL refinement audit " + scenario + ": " + error.Message); }
        }
        Check(failures == 0, "Refinement audit failures=" + failures);
        Console.WriteLine("Refinement audit passed: " + assertions + "; clipboard_access=0; global_input=0");
    }
    static void CheckInteractionUpgrade(string vault, string root) {
        int failures = 0;
        foreach (string scenario in new[] { "edge", "hover", "focus" }) {
            try {
                var store = new QuickNoteStore(vault, "interaction-" + scenario, Path.Combine(root, "interaction-recovery-" + scenario));
                var seed = store.Load(DateTime.Today); store.Save(seed, "当前便签内容");
                var other = store.NewNote(); store.Save(other, "另一份预览内容");
                using (var window = new QuickNoteWindow(store, Path.Combine(root, "interaction-state-" + scenario))) {
                    window.Toggle(); Settled(window); window.Location = new Point(Screen.FromControl(window).WorkingArea.Left, 100);
                    var input = (QuickNoteInputBox)Field(window, "editor");
                    Call(window, "ShowTabs"); Pump(180);
                    var rail = (QuickNoteTabs)Field(window, "tabs");
                    if (scenario == "edge") {
                        Check(!rail.Bounds.IntersectsWith(window.Bounds), "Left-edge tab rail stays outside the note and never covers text");
                        Check(rail.OnRight, "The left screen edge uses a mirrored right-side rail");
                        Rectangle working = Screen.FromControl(window).WorkingArea;
                        window.Left = working.Right - window.Width; Call(window, "ShowTabs"); Pump(180);
                        Check(!rail.OnRight && !rail.Bounds.IntersectsWith(window.Bounds) && working.Contains(rail.Bounds), "Right-edge rail stays on the left and entirely within the screen");
                        window.Location = new Point(working.Left, working.Bottom - window.Height); Call(window, "ShowTabs");
                        Check(!rail.Bounds.IntersectsWith(window.Bounds) && working.Contains(rail.Bounds), "Bottom-left placement also keeps the entire rail outside content and inside the screen");
                        window.Location = new Point(working.Left, 100); Call(window, "ShowTabs");
                        using (var capture = new Bitmap(window.Width + rail.Width, window.Height)) {
                            using (Graphics graphics = Graphics.FromImage(capture)) graphics.Clear(Color.FromArgb(240, 240, 238));
                            using (var noteShot = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(noteShot, new Rectangle(Point.Empty, window.Size)); using (Graphics graphics = Graphics.FromImage(capture)) graphics.DrawImageUnscaled(noteShot, 0, 0); }
                            using (var railShot = new Bitmap(rail.Width, rail.Height)) { rail.DrawToBitmap(railShot, new Rectangle(Point.Empty, rail.Size)); railShot.MakeTransparent(Color.Fuchsia); using (Graphics graphics = Graphics.FromImage(capture)) graphics.DrawImageUnscaled(railShot, window.Width, 40); }
                            capture.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outside-tabs-right.png"));
                        }
                        Rectangle area = Screen.FromControl(window).WorkingArea;
                        window.Bounds = area; Call(window, "ShowTabs");
                        Check(!rail.Visible, "No external space hides the rail instead of covering the note");
                        Call((Label)Field(window, "dateLabel"), "OnClick", EventArgs.Empty); Pump(30);
                        var menus = (System.ComponentModel.Container)Field(window, "transientMenus");
                        var menu = (ContextMenuStrip)menus.Components[0];
                        var picker = (QuickNoteRecentPicker)((ToolStripControlHost)menu.Items[0]).Control;
                        Check(menu.Visible && area.Contains(menu.Bounds) && picker.Results.Items.Count == 2, "Filename opens the bounded recent picker even with no rail space");
                        picker.Search.Text = "不存在"; Check(picker.Results.Items.Count == 0, "The recent picker filters and presents an empty result");
                        picker.Search.Text = Path.GetFileNameWithoutExtension(other.Path);
                        Check(picker.Results.Items.Count == 1 && (string)picker.Results.Items[0] == Path.GetFileName(other.Path), "Filename filtering keeps the actual destination separate from display text");
                        Call(picker.Search, "OnKeyDown", new KeyEventArgs(Keys.Enter)); Settled(window); Pump(30);
                        Check((string)Field(window, "notePath") == other.Path && !menu.Visible, "Enter opens the filtered destination and closes the popup");
                    } else if (scenario == "hover") {
                        Call(window, "SetChromeVisible", false); Call(window, "HideTabs"); Pump(220);
                        NativeMessage(input.Handle, 0x200, IntPtr.Zero, new IntPtr((20 << 16) | 20));
                        Check(((Panel)Field(window, "top")).Visible && rail.Visible, "Native pointer movement restores hidden chrome and tabs without relying on MouseEnter");
                        Call(window, "HideTabs"); NativeMessage(input.Handle, 0x200, IntPtr.Zero, new IntPtr((20 << 16) | 20)); Pump(220);
                        Check(rail.Visible && rail.IsRevealed, "Pointer re-entry interrupts a pending tab conceal instead of disappearing under the pointer");
                        int cursorChanges = 0; input.CursorChanged += delegate { cursorChanges++; };
                        for (int i = 0; i < 30; i++) NativeMessage(input.Handle, 0x200, IntPtr.Zero, new IntPtr((20 << 16) | (20 + i)));
                        Check(cursorChanges <= 1, "Ordinary text hover does not repeatedly switch the pointer shape");
                    } else {
                        input.Focus(); int lost = 0, gained = 0;
                        input.LostFocus += delegate { lost++; }; input.GotFocus += delegate { gained++; };
                        rail.HoverFile(Path.GetFileName(other.Path)); Call(window, "StartPreview"); Pump(300);
                        Check((bool)Field(window, "previewVisible"), "The focus audit actually enters a hover preview");
                        Call(window, "CancelPreview");
                        Check(lost == 0 && gained == 0 && input.Focused, "Hover preview never churns editor focus or restarts its caret");
                        for (int i = 0; i < 5; i++) { rail.HoverFile(Path.GetFileName(other.Path)); Call(window, "StartPreview"); Pump(120); Call(window, "CancelPreview"); }
                        Check(lost == 0 && gained == 0 && !(bool)Field(window, "previewCaretHidden"), "Repeated hover preview cycles balance caret suppression without moving focus");
                        input.Select(input.TextLength, 0);
                        rail.HoverFile(Path.GetFileName(other.Path)); Call(window, "StartPreview"); Pump(200);
                        Check((bool)Field(window, "previewVisible"), "Typing protection is tested against an active preview");
                        NativeMessage(input.Handle, 0x102, new IntPtr('字'), IntPtr.Zero);
                        Check(!(bool)Field(window, "previewVisible") && window.ReadMarkdown().EndsWith("字") && File.ReadAllText(other.Path).Contains("另一份"), "Typing dismisses the overlay before editing only the original note");
                    }
                }
                Console.WriteLine("PASS interaction " + scenario);
            } catch (Exception error) { failures++; Console.WriteLine("FAIL interaction " + scenario + ": " + error.Message); }
        }
        Check(failures == 0, "Interaction failures=" + failures);
        CheckImageInteraction(vault, root);
    }
    static void CheckImageInteraction(string vault, string root) {
        var store = new QuickNoteStore(vault, "interaction-images", Path.Combine(root, "interaction-images-recovery"));
        string reference = store.StoreImage(Png()); byte[] original = File.ReadAllBytes(store.ResolveImage(reference));
        var seed = store.Load(DateTime.Today); store.Save(seed, "图前\n![图片](<" + reference + ">)\n图后");
        string saved;
        using (var window = new QuickNoteWindow(store, Path.Combine(root, "interaction-images-state"))) {
            window.Toggle(); Settled(window); var input = (QuickNoteInputBox)Field(window, "editor");
            input.Select(Document(window).ObjectPositions()[0], 1); Call(window, "ResizeImageTo", 160);
            Rectangle before = Document(window).SelectedImageBounds(); Point edge = new Point(before.Right, before.Top + before.Height / 2);
            Call(window, "HandleImagePointer", 0x201, edge);
            Point horizontal = new Point(edge.X + 60, edge.Y + 15);
            Call(window, "HandleImagePointer", 0x200, horizontal); Call(window, "HandleImagePointer", 0x202, horizontal);
            Rectangle stretched = Document(window).SelectedImageBounds();
            Check(stretched.Width > before.Width && stretched.Height == before.Height, "Horizontal side drag changes width freely without changing height");
            Check(window.ReadMarkdown().Contains("|200x80]"), "Free dimensions persist as one Markdown width-by-height reference");
            input.Undo(); Check(Document(window).SelectedImageBounds().Size == before.Size, "Side sizing is one undo"); input.Redo(); input.Select(Document(window).ObjectPositions()[0], 1);
            Rectangle start = Document(window).SelectedImageBounds(); Point corner = new Point(start.Right, start.Bottom);
            Check((bool)Call(window, "HandleImagePointer", 0x201, corner), "The diagonal case starts on the selected picture's corner");
            Point diagonal = new Point(corner.X + 35, corner.Y + 15);
            Call(window, "HandleImagePointer", 0x200, diagonal); Rectangle outline = (Rectangle)Field(window, "imageDragOutline");
            Check(Math.Abs(outline.Width / (double)outline.Height - start.Width / (double)start.Height) < .03, "Diagonal corner drag locks the current display ratio after free adjustment: start=" + start + "; outline=" + outline);
            Call(window, "HandleImagePointer", 0x202, diagonal);
            string opened = null; int opens = 0; window.OpenImageFile = delegate(string path) { opened = path; opens++; };
            Rectangle picture = Document(window).SelectedImageBounds(); Point middle = new Point(picture.Left + picture.Width / 2, picture.Top + picture.Height / 2);
            NativeMessage(input.Handle, 0x203, new IntPtr(1), new IntPtr((middle.Y << 16) | (middle.X & 0xffff)));
            Check(opens == 1 && opened == store.ResolveImage(reference), "Double-click dispatches the original local file to the system viewer");
            input.Select(0, 0);
            NativeMessage(input.Handle, 0x204, new IntPtr(2), new IntPtr((middle.Y << 16) | (middle.X & 0xffff)));
            Call(window, "ShowEditorMenu");
            var menus = (System.ComponentModel.Container)Field(window, "transientMenus"); var menu = (ContextMenuStrip)menus.Components[0];
            ToolStripMenuItem open = null; foreach (ToolStripItem item in menu.Items) if (item.Text == "查看原图") open = (ToolStripMenuItem)item;
            Check(open != null && input.SelectionLength == 1, "Right-click selects the pointed picture and offers original viewing");
            open.PerformClick(); menu.Close(); Pump(30); Check(opens == 2 && opened == store.ResolveImage(reference), "The menu opens the same original file without rewriting it");
            string originalPath = store.ResolveImage(reference), temporary = originalPath + ".missing-test";
            File.Move(originalPath, temporary);
            try { Call(window, "OpenSelectedImage"); Check(opens == 2 && (bool)Field(window, "statusError"), "A missing original produces an actionable error without launching another path"); }
            finally { File.Move(temporary, originalPath); }
            Call(window, "SetSavedStatus");
            Call(window, "ShowRecentMenu"); menu = (ContextMenuStrip)menus.Components[0];
            using (var capture = new Bitmap(menu.Width, menu.Height)) { menu.DrawToBitmap(capture, new Rectangle(Point.Empty, menu.Size)); capture.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recent-picker.png")); }
            menu.Close(); Pump(30);
            Call(window, "ShowTabs"); var rail = (QuickNoteTabs)Field(window, "tabs"); Pump(150);
            Call(window, "SetChromeVisible", true);
            using (var capture = new Bitmap(window.Width + rail.Width, window.Height)) {
                using (Graphics graphics = Graphics.FromImage(capture)) graphics.Clear(Color.FromArgb(240, 240, 238));
                using (var noteShot = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(noteShot, new Rectangle(Point.Empty, window.Size)); using (Graphics graphics = Graphics.FromImage(capture)) graphics.DrawImageUnscaled(noteShot, rail.OnRight ? 0 : rail.Width, 0); }
                using (var railShot = new Bitmap(rail.Width, rail.Height)) { rail.DrawToBitmap(railShot, new Rectangle(Point.Empty, rail.Size)); railShot.MakeTransparent(Color.Fuchsia); using (Graphics graphics = Graphics.FromImage(capture)) graphics.DrawImageUnscaled(railShot, rail.OnRight ? window.Width : 0, 40); }
                capture.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "outside-tabs.png"));
            }
            Check(Convert.ToBase64String(File.ReadAllBytes(store.ResolveImage(reference))) == Convert.ToBase64String(original), "All image interactions preserve original PNG bytes");
            Autosaved(window); saved = window.ReadMarkdown();
        }
        using (var reopened = new QuickNoteWindow(store, Path.Combine(root, "interaction-images-reopen"))) {
            reopened.Toggle(); Settled(reopened); Check(reopened.ReadMarkdown() == saved, "Freely resized image dimensions survive durable save and a complete reopen");
        }
        Console.WriteLine("PASS image gestures, original viewing and recent picker; assertions=" + assertions + "; clipboard_access=0; global_input=0");
    }
    static void CheckTabDeletion(string vault, string root) {
        var store = new QuickNoteStore(vault, "tab-delete", Path.Combine(root, "tab-delete-recovery"));
        var seed = store.Load(DateTime.Today); store.Save(seed, "删除当前前的原文");
        var other = store.NewNote(); store.Save(other, "保留另一篇");
        string picture = store.StoreImage(Png()), imagePath = store.ResolveImage(picture);
        byte[] imageBytes = File.ReadAllBytes(imagePath);
        string recycled = null, contents = null, bin = Path.Combine(root, "synthetic-bin"); Directory.CreateDirectory(bin);
        store.RecycleFile = delegate(string path) { recycled = path; contents = File.ReadAllText(path); File.Move(path, Path.Combine(bin, Guid.NewGuid().ToString("N") + ".md")); };
        string state = Path.Combine(root, "tab-delete-state");
        using (var window = new QuickNoteWindow(store, state)) {
            window.Toggle(); Settled(window); Call(window, "ShowTabs"); Pump(180);
            var rail = (QuickNoteTabs)Field(window, "tabs");
            int row = rail.Height / (((string[])Field(rail, "files")).Length + 1);
            var files = (string[])Field(rail, "files"); string chosen = files[0];
            Point point = new Point(rail.Width / 2, row / 2);
            Call(rail, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0)); Pump(10);
            var menus = (System.ComponentModel.Container)Field(window, "transientMenus");
            var menu = (ContextMenuStrip)menus.Components[0];
            Check(menu.Visible && menu.Items[0].Text == "删除便签…", "Right-clicking a paper tab opens its deletion menu");
            using (var capture = new Bitmap(menu.Width, menu.Height)) { menu.DrawToBitmap(capture, new Rectangle(Point.Empty, menu.Size)); capture.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tab-delete-menu.png")); }
            window.ConfirmDelete = delegate(string name) { Check(name == chosen, "The context menu retains the clicked tab identity"); return false; };
            menu.Items[0].PerformClick(); menu.Close(); Pump(30);
            Check(recycled == null && File.Exists(seed.Path) && File.Exists(other.Path), "Canceling deletion preserves every note");
            window.ConfirmDelete = delegate { return true; };
            Append(window, "，包含还未自动保存的输入");
            Call(window, "DeleteNote", Path.GetFileName(seed.Path)); Settled(window);
            Check(recycled == seed.Path && contents.EndsWith("包含还未自动保存的输入"), "Current dirty text is durably saved before recycling");
            Check(!File.Exists(seed.Path) && (string)Field(window, "notePath") == other.Path, "Deleting the active note switches to a remaining note without recreating it");
            Check(!File.Exists(seed.RecoveryPath) && !((List<string>)Field(window, "recentFiles")).Contains(Path.GetFileName(seed.Path)), "Deleted identity and successful-save draft leave the recent rail");
            Check(Convert.ToBase64String(File.ReadAllBytes(imagePath)) == Convert.ToBase64String(imageBytes), "Deleting a note does not remove or mutate shared image attachments");
            Call(window, "ShowRecentMenu"); menu = (ContextMenuStrip)menus.Components[0];
            var picker = (QuickNoteRecentPicker)((ToolStripControlHost)menu.Items[0]).Control;
            Call(picker.Results, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 20, 20, 0)); Pump(30);
            ContextMenuStrip deleteMenu = null;
            foreach (System.ComponentModel.Component component in menus.Components) { var item = component as ContextMenuStrip; if (item != null && item.Visible && item.Items.Count > 0 && item.Items[0].Text == "删除便签…") deleteMenu = item; }
            Check(deleteMenu != null, "More-notes rows also expose deletion via right click");
            deleteMenu.Close(); Pump(30);
            var draft = store.NewNote(); store.Save(draft, "磁盘正文"); store.SaveRecovery(draft, "尚未处理的恢复正文");
            recycled = null;
            Call(window, "DeleteNote", Path.GetFileName(draft.Path)); Settled(window);
            Check(recycled == null && File.Exists(draft.Path) && File.Exists(draft.RecoveryPath) && (bool)Field(window, "statusError"), "An inactive note with an unresolved draft is preserved and explains why deletion failed");
            Check(!((QuickNoteInputBox)Field(window, "editor")).ReadOnly, "Failed deletion restores editability of the current note");
            var input = (QuickNoteInputBox)Field(window, "editor");
            string unchanged = window.ReadMarkdown();
            window.ConfirmDelete = delegate { typeof(QuickNoteWindow).GetField("workerBusy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true); return true; };
            Call(window, "DeleteNote", Path.GetFileName(other.Path));
            typeof(QuickNoteWindow).GetField("workerBusy", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, false);
            Check(recycled == null && !input.ReadOnly && window.ReadMarkdown() == unchanged, "A save started during modal confirmation cancels deletion safely");
            window.ConfirmDelete = delegate { return true; };
            store.RecycleFile = delegate { throw new IOException("合成回收站失败"); };
            Call(window, "DeleteNote", Path.GetFileName(other.Path)); Settled(window);
            Check(File.Exists(other.Path) && window.ReadMarkdown() == unchanged && !input.ReadOnly, "Recycle failure keeps the original note, its current text and editing available");
            store.RecycleFile = delegate(string path) { File.Move(path, Path.Combine(bin, "last.md")); };
            // Remove the synthetic draft explicitly within its own isolated fixture.
            File.Delete(draft.RecoveryPath); File.Delete(draft.Path);
            Call(window, "DeleteNote", Path.GetFileName(other.Path)); Settled(window);
            Check(!File.Exists(other.Path) && (string)Field(window, "notePath") != other.Path && window.ReadMarkdown() == "" && !input.ReadOnly, "Deleting the final persisted note creates an editable, unsaved new note");
            string blank = (string)Field(window, "notePath");
            store.RecycleFile = delegate { throw new Exception("An empty tab must not invoke the shell"); };
            Call(window, "DeleteNote", Path.GetFileName(blank)); Settled(window);
            Check(!File.Exists(blank) && (string)Field(window, "notePath") != blank && !input.ReadOnly, "Deleting a new empty tab simply discards it without a file or a shell operation");
            Call(window, "SaveWindowState");
        }
        using (var reopened = new QuickNoteWindow(store, state)) {
            reopened.Toggle(); Settled(reopened);
            Check(!File.Exists(seed.Path) && !File.Exists(other.Path) && windowNotePath(reopened) != other.Path, "Reopening never restores or recreates a deleted note from old navigation state");
        }
        Console.WriteLine("PASS tab deletion; recycle spy is isolated; clipboard_access=0; global_input=0");
        if (Environment.GetEnvironmentVariable("VERBIT_TEST_NATIVE_RECYCLE") != "1") {
            Console.WriteLine("SKIP native shell recycle: opt in with VERBIT_TEST_NATIVE_RECYCLE=1 on a local Windows desktop"); return;
        }
        var nativeStore = new QuickNoteStore(vault, "native-recycle", Path.Combine(root, "native-recycle-recovery"));
        var nativeSeed = nativeStore.Load(DateTime.Today); nativeStore.Save(nativeSeed, "仅用于回收的合成测试便签");
        using (var window = new QuickNoteWindow(nativeStore, Path.Combine(root, "native-recycle-state"))) {
            window.Toggle(); Settled(window); window.ConfirmDelete = delegate { return true; };
            Call(window, "DeleteNote", Path.GetFileName(nativeSeed.Path)); Settled(window);
            Check(!File.Exists(nativeSeed.Path) && !(bool)Field(window, "statusError"), "The real Windows shell recycles an isolated synthetic note through the production background path");
        }
        Console.WriteLine("PASS native shell recycle of one synthetic file; no user note touched");
    }
    static string windowNotePath(QuickNoteWindow window) { return (string)Field(window, "notePath"); }
    [STAThread] static int Main(string[] arguments) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "tingji-quick-note-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string vault = Path.Combine(root, "vault"), recovery = Path.Combine(root, "recovery");
            Directory.CreateDirectory(vault);
            if (Array.IndexOf(arguments, "--interaction-only") >= 0) { CheckInteractionUpgrade(vault, root); return 0; }
            if (Array.IndexOf(arguments, "--delete-only") >= 0) { CheckTabDeletion(vault, root); return 0; }
            if (Array.IndexOf(arguments, "--refine-audit-only") >= 0) { CheckRefinementAudit(vault, root); return 0; }
            if (Array.IndexOf(arguments, "--refine-only") >= 0) { CheckRefinements(vault, root); return 0; }
            if (Array.IndexOf(arguments, "--navigation-only") >= 0) { CheckNavigation(vault, root); return 0; }
            CheckFormatting(vault, root);
            if (Array.IndexOf(arguments, "--format-only") >= 0) { Console.WriteLine("Formatting tests passed: " + assertions + " assertions; clipboard_access=0; global_input=0"); return 0; }
            CheckNavigation(vault, root);
            CheckRefinements(vault, root);
            CheckRefinementAudit(vault, root);
            CheckInteractionUpgrade(vault, root);
            CheckTabDeletion(vault, root);
            var store = new QuickNoteStore(vault, "速记", recovery);
            CheckMenuClose(vault, root);
            CheckShortcutRouting();
            CheckDeferredImageComposition(vault, root);
            CheckFailedImportRecovery(vault, root);
            CheckSyntheticNewlines(vault, root);
            CheckWindowGeometry(store, root);
            float[] imageDpis = new float[] { 96, 144, 192 };
            var originals = new byte[imageDpis.Length][];
            var encodedImages = new byte[imageDpis.Length][];
            for (int index = 0; index < imageDpis.Length; index++) {
                float dpi = imageDpis[index];
                byte[] originalPixels = PatternPng(dpi); originals[index] = originalPixels;
                byte[] encoded;
                using (var stream = new MemoryStream(originalPixels)) using (var source = new Bitmap(stream))
                    encoded = (byte[])typeof(QuickNoteWindow).GetMethod("EncodePng", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { source });
                encodedImages[index] = encoded;
                Check(SamePixels(originalPixels, encoded), "Native PNG encoding preserves nonuniform pixels at " + dpi + " DPI");
            }
            for (int index = 0; index < imageDpis.Length; index++) {
                string stored = store.StoreImage(encodedImages[index]);
                Check(SamePixels(originals[index], File.ReadAllBytes(store.ResolveImage(stored))), "Image storage preserves nonuniform pixels at " + imageDpis[index] + " DPI");
            }
            string[] samples = { "", "一行", "一\n二\n", "一\r\n二\r\n", "混合\r\n换行\n不变", "# 标题\n- 项目\n\n**原样** `code` <tag>\n", "![远程](https://example.com/x.png)\n![越界](../x.png)", "![缺图](<图片/none.png>)" };
            string imagePath = store.StoreImage(Png());
            string imageText = "前文\r\n![图片](<" + imagePath + ">)\r\n后文";
            using (var richText = new NativeNoteInputBox()) using (var document = new NativeNoteDocument(richText, delegate(string relative) { return Preview(store, relative); })) {
                foreach (string sample in samples) {
                    document.Load(sample); Check(document.Markdown() == sample, "Native Markdown must round trip exactly: " + sample);
                }
                document.Load(imageText);
                Check(document.Markdown() == imageText, "Inline image preserves surrounding text and exact reference");
                Check(document.ObjectPositions().Length == 1, "A safe local PNG renders as a real inline RichEdit object");
                document.Load("前"); richText.Select(richText.TextLength, 0); richText.SelectedText = "\r\n后";
                Check(document.Markdown() == "前\r\n后", "Native paragraph insertion serializes as one CRLF line break");
            }

            bool delayAutosave = false;
            using (var window = new QuickNoteWindow(store, Path.Combine(root, "state"), delegate { if (delayAutosave) Thread.Sleep(650); })) {
                window.Toggle(); Pump(100);
                Check(window.Visible && window.TopMost, "Quick note opens visibly and pinned by default");
                Check(window.FormBorderStyle == FormBorderStyle.None, "The native editor uses the requested single header with native resize hit testing");
                Rectangle resized = new Rectangle(window.Left + 12, window.Top + 16, window.Width + 24, window.Height + 20);
                window.Bounds = resized; Call(window, "OnResizeEnd", EventArgs.Empty);
                Check(window.Bounds == resized && !window.Capture, "Completing a system resize retains bounds without custom mouse capture");
                var editor = (RichTextBox)Field(window, "editor");
                Append(window, "自动保存中文\n- 简单列表"); Autosaved(window);
                var note = store.Load(DateTime.Today);
                Check(note.Text == "自动保存中文\n- 简单列表", "500ms debounce saves edited Markdown");
                Check(!note.HasRecovery, "A successful save clears transient recovery");
                editor.Select(0, 2);
                var imagePaste = new DataObject();
                var pasteBitmap = new Bitmap(8, 4);
                using (var graphics = Graphics.FromImage(pasteBitmap)) graphics.Clear(Color.RoyalBlue);
                imagePaste.SetData(DataFormats.Bitmap, pasteBitmap);
                window.PasteData(imagePaste);
                Settled(window);
                string replaced = window.ReadMarkdown();
                Check(replaced.StartsWith("![图片](<" + imagePath + ">)\n保存中文"), "Image paste replaces exactly the selected text: " + replaced + "; status=" + ((LinkLabel)Field(window, "status")).Text);
                editor.Undo(); Check(window.ReadMarkdown() == note.Text, "Image replacement and newline are one undo group");
                editor.Redo(); Check(window.ReadMarkdown() == replaced, "Redo restores the whole image replacement"); editor.Undo();
                var paste = new DataObject(); paste.SetData(DataFormats.UnicodeText, "\n粘贴纯文本 **保持Markdown**");
                editor.Select(editor.TextLength, 0);
                window.PasteData(paste);
                Check(window.ReadMarkdown().EndsWith("**保持Markdown**"), "The actual paste handler accepts plain Unicode Markdown from its local snapshot");
                editor.Undo(); Autosaved(window); Check(store.Load(DateTime.Today).Text == note.Text, "Undo of pasted text keeps the prior note intact");
                editor.Select(editor.TextLength, 0);
                window.PasteData(imagePaste); Settled(window);
                // Saving may legitimately outlast a fixed 800ms UI pump. Exercise
                // that case without bypassing the real debounce or storage path.
                delayAutosave = true;
                Append(window, "图片后文字"); Autosaved(window);
                delayAutosave = false;
                string withImage = store.Load(DateTime.Today).Text;
                Check(withImage.Contains("![图片](<" + imagePath + ">)"), "Inserted image is a reusable relative Markdown reference");
                Check(withImage.StartsWith("自动保存中文\n- 简单列表") && withImage.EndsWith("图片后文字"), "Image insertion retains text on both sides");
                Check(editor.CanUndo, "Image/text editor exposes native undo");
                editor.Undo(); Pump(20); Check(editor.CanRedo, "Undo permits native redo"); editor.Redo(); Autosaved(window);
                Check(store.Load(DateTime.Today).Text == withImage, "Undo and redo retain Markdown and image identity");
                editor.Select(editor.TextLength, 0); window.PasteData(imagePaste); Settled(window);
                string duplicateImage = window.ReadMarkdown();
                Check(Document(window).ObjectPositions().Length == 2 && System.Text.RegularExpressions.Regex.Matches(duplicateImage, System.Text.RegularExpressions.Regex.Escape("![图片](<" + imagePath + ">)")).Count == 2,
                    "Repeated identical pixels retain two independent inline objects and exact reusable references");
                editor.Undo(); Check(window.ReadMarkdown() == withImage, "Undo removes only the new duplicate image");
                editor.Redo(); Check(window.ReadMarkdown() == duplicateImage && Document(window).ObjectPositions().Length == 2, "Redo restores the duplicate object's exact identity and reference");
                editor.Undo(); Autosaved(window);
                typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
                Append(window, "输入法组合"); Pump(700);
                Check(store.Load(DateTime.Today).Text == withImage, "Autosave waits while an IME composition is active");
                window.Toggle(); Check(window.Visible, "Toggle does not hide an active IME composition");
                Check(!window.FlushAndClose(), "Exit waits for IME composition instead of dropping pending text");
                typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, false);
                Call(window, "Flush"); Settled(window);
                Check(!(bool)Field(window, "dirty"), "Committed composition can be saved");
                withImage += "输入法组合";
                window.Toggle(); Check(!window.Visible, "The same toggle flushes and hides");
                window.Toggle(); Pump(50); Check(window.ReadMarkdown() == withImage, "Reopen reloads the saved document without conversion");
                var escape = new KeyEventArgs(Keys.Escape);
                Call(window, "EditorKeyDown", editor, escape); Check(escape.Handled && !window.Visible, "The native Escape key handler saves and hides the note");
                window.Toggle(); Pump(50);

                Append(window, "我的未存修改");
                File.AppendAllText(note.Path, "\nObsidian 外部修改");
                window.Toggle();
                Settled(window);
                Check(window.Visible, "Save conflict keeps the note visible");
                Check(File.ReadAllText(note.Path).EndsWith("Obsidian 外部修改"), "Save conflict never overwrites external edits");
                Check(store.Load(DateTime.Today).HasRecovery, "Save conflict preserves recovery text outside the vault");
                Check(!window.FlushAndClose(), "Failed flush cancels application close");
                string unsaved = window.ReadMarkdown();
                Call(window, "ReloadDiskKeepingDraft");
                Settled(window);
                Check(window.ReadMarkdown().EndsWith("Obsidian 外部修改"), "Conflict recovery reloads the latest external version");
                bool archived = false;
                foreach (string path in Directory.GetFiles(recovery, "*.md")) if (File.ReadAllText(path) == unsaved) archived = true;
                Check(archived, "Conflict recovery preserves the user's exact input as a separate Markdown draft");
                Check(window.FlushAndClose(), "A clean editor can close after its recovery is preserved");
                pasteBitmap.Dispose();
            }

            var original = store.Load(DateTime.Today); store.SaveRecovery(original, original.Text + "\n恢复输入");
            using (var loadFailure = new QuickNoteWindow(store, Path.Combine(root, "load-failure-state"), delegate { throw new IOException("模拟首次载入失败"); })) {
                loadFailure.Toggle(); Settled(loadFailure);
                Check(((RichTextBox)Field(loadFailure, "editor")).ReadOnly, "An initial load failure keeps the empty editor read-only");
                Check(loadFailure.FlushAndClose() && loadFailure.IsDisposed, "A clean initial load failure does not trap application exit");
            }
            var reloadStore = new QuickNoteStore(vault, "慢重载", Path.Combine(root, "slow-reload-recovery"));
            var reloadOriginal = reloadStore.Load(DateTime.Today); reloadStore.Save(reloadOriginal, "原有磁盘内容");
            bool delayReload = false;
            using (var reloadWindow = new QuickNoteWindow(reloadStore, Path.Combine(root, "slow-reload-state"), delegate { if (delayReload) Thread.Sleep(800); })) {
                reloadWindow.Toggle(); Settled(reloadWindow);
                Append(reloadWindow, "待恢复的输入");
                File.WriteAllText(reloadOriginal.Path, "Obsidian 外部版本保持不变");
                Call(reloadWindow, "Flush"); Settled(reloadWindow);
                delayReload = true;
                Call(reloadWindow, "ReloadDiskKeepingDraft");
                Pump(650); // The real 500ms debounce fires while archive/load is in flight.
                Call(reloadWindow, "LoadToday"); // Also model the cross-day timer's direct entry.
                bool capturedOldText = Field(reloadWindow, "pendingText") != null;
                Settled(reloadWindow);
                Check(reloadStore.Load(DateTime.Today).Text == "Obsidian 外部版本保持不变", "A debounce during slow reload must never save the old editor snapshot with the new disk revision");
                Check(!capturedOldText, "A read-only reload does not capture an obsolete pending save");
                bool retained = false;
                foreach (string path in Directory.GetFiles(Path.Combine(root, "slow-reload-recovery"), "*.md")) if (File.ReadAllText(path).Contains("待恢复的输入")) retained = true;
                Check(retained, "Slow reload retains the exact former input in an archived draft");
                delayReload = false; Check(reloadWindow.FlushAndClose(), "The reloaded editor closes cleanly");
            }
            using (var recovered = new QuickNoteWindow(store, Path.Combine(root, "state"))) {
                recovered.Toggle(); Autosaved(recovered);
                Check(store.Load(DateTime.Today).Text.EndsWith("恢复输入"), "Matching-revision crash recovery is restored and saved");
                Check(recovered.FlushAndClose(), "Recovered note flushes cleanly");
            }
            var dropStore = new QuickNoteStore(vault, "拖入速记", Path.Combine(root, "drop-recovery"));
            using (var dropped = new QuickNoteWindow(dropStore, Path.Combine(root, "drop-state"))) {
                dropped.Toggle(); Pump(100);
                var editor = (RichTextBox)Field(dropped, "editor"); Append(dropped, "拖入前后文字");
                string beforeDrop = dropped.ReadMarkdown();
                byte[] droppedPixels = PatternPng(192);
                string input = Path.Combine(root, "drag-source.png"); File.WriteAllBytes(input, droppedPixels);
                var data = new DataObject(DataFormats.FileDrop, new string[] { input });
                Point position = editor.PointToScreen(new Point(20, 20));
                var drag = new DragEventArgs(data, 0, position.X, position.Y, DragDropEffects.Copy, DragDropEffects.None);
                Call(dropped, "Drop", editor, drag); Autosaved(dropped);
                string markdown = dropStore.Load(DateTime.Today).Text;
                var match = System.Text.RegularExpressions.Regex.Match(markdown, @"!\[图片\]\(<([^>]+)>\)");
                Check(drag.Effect == DragDropEffects.Copy && match.Success, "The actual native file-drop handler writes a local Markdown image reference");
                string insertedFragment = match.Value + "\n";
                string dropDiagnostic = " Image index=" + match.Index + "; before=[" + Escaped(beforeDrop) + "]; actual=[" + Escaped(markdown) + "]";
                Check(markdown.IndexOf(insertedFragment, StringComparison.Ordinal) == match.Index, "File drop inserts the exact image reference followed by one LF." + dropDiagnostic);
                Check(markdown.IndexOf(insertedFragment, match.Index + insertedFragment.Length, StringComparison.Ordinal) < 0, "One dropped file inserts its exact image fragment only once." + dropDiagnostic);
                Check(string.Equals(markdown.Remove(match.Index, insertedFragment.Length), beforeDrop, StringComparison.Ordinal), "Removing only the inserted image fragment preserves every original character in order." + dropDiagnostic);
                Check(SamePixels(droppedPixels, File.ReadAllBytes(dropStore.ResolveImage(match.Groups[1].Value))), "File drop preserves all nonuniform source pixels at 192 DPI");
                var status = (LinkLabel)Field(dropped, "status");
                Check(status.Text.StartsWith("拖入速记 / "), "Saved status names the configured note folder");
                Check(((ToolTip)Field(dropped, "tips")).GetToolTip(status) == dropStore.Load(DateTime.Today).Path, "Saved status tooltip exposes the actual note path");
                Check(dropped.FlushAndClose(), "A dropped image and its note flush cleanly");
            }
            Console.WriteLine("Quick note editor tests passed: " + assertions + " assertions."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
