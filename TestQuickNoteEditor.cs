using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class TestQuickNoteEditor {
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
    [STAThread] static int Main() {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "tingji-quick-note-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string vault = Path.Combine(root, "vault"), recovery = Path.Combine(root, "recovery");
            Directory.CreateDirectory(vault);
            var store = new QuickNoteStore(vault, "速记", recovery);
            CheckMenuClose(vault, root);
            CheckShortcutRouting();
            CheckDeferredImageComposition(vault, root);
            CheckFailedImportRecovery(vault, root);
            CheckSyntheticNewlines(vault, root);
            foreach (bool initialScale in new bool[] { false, true }) {
                Rectangle? previousBounds = null;
                for (int cycle = 0; cycle < 3; cycle++) {
                    using (var geometry = new QuickNoteWindow(store, Path.Combine(root, "geometry-state-" + initialScale))) {
                        // A persisted rectangle is already in screen pixels. Force
                        // an initial 150% WinForms scale transition independently
                        // of the machine's current display DPI.
                        if (initialScale) geometry.AutoScaleDimensions = new SizeF(64, 64);
                        geometry.Toggle(); Pump(100);
                        Rectangle bounds = geometry.Bounds;
                        Console.WriteLine("Quick note geometry scale=" + initialScale + " cycle " + cycle + ": " + bounds.Width + "x" + bounds.Height + "; DPI=" + geometry.DeviceDpi);
                        if (previousBounds.HasValue) Check(bounds == previousBounds.Value, "Persisted bounds must not grow across recreated windows");
                        previousBounds = bounds;
                        geometry.Toggle(); Check(!geometry.Visible, "Geometry probe hides after flushing");
                        Check(geometry.FlushAndClose(), "Geometry probe closes without pending text");
                    }
                }
            }
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

            using (var window = new QuickNoteWindow(store, Path.Combine(root, "state"))) {
                window.Toggle(); Pump(100);
                Check(window.Visible && window.TopMost, "Quick note opens visibly and pinned by default");
                Check(window.FormBorderStyle == FormBorderStyle.Sizable, "The native editor uses the verified system title bar and resize border");
                Rectangle resized = new Rectangle(window.Left + 12, window.Top + 16, window.Width + 24, window.Height + 20);
                window.Bounds = resized; Call(window, "OnResizeEnd", EventArgs.Empty);
                Check(window.Bounds == resized && !window.Capture, "Completing a system resize retains bounds without custom mouse capture");
                var editor = (RichTextBox)Field(window, "editor");
                Append(window, "自动保存中文\n- 简单列表"); Pump(800);
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
                editor.Undo(); Pump(800); Check(store.Load(DateTime.Today).Text == note.Text, "Undo of pasted text keeps the prior note intact");
                editor.Select(editor.TextLength, 0);
                window.PasteData(imagePaste); Settled(window);
                Append(window, "图片后文字"); Pump(800);
                string withImage = store.Load(DateTime.Today).Text;
                Check(withImage.Contains("![图片](<" + imagePath + ">)"), "Inserted image is a reusable relative Markdown reference");
                Check(withImage.StartsWith("自动保存中文\n- 简单列表") && withImage.EndsWith("图片后文字"), "Image insertion retains text on both sides");
                Check(editor.CanUndo, "Image/text editor exposes native undo");
                editor.Undo(); Pump(20); Check(editor.CanRedo, "Undo permits native redo"); editor.Redo(); Pump(800);
                Check(store.Load(DateTime.Today).Text == withImage, "Undo and redo retain Markdown and image identity");
                editor.Select(editor.TextLength, 0); window.PasteData(imagePaste); Settled(window);
                string duplicateImage = window.ReadMarkdown();
                Check(Document(window).ObjectPositions().Length == 2 && System.Text.RegularExpressions.Regex.Matches(duplicateImage, System.Text.RegularExpressions.Regex.Escape("![图片](<" + imagePath + ">)")).Count == 2,
                    "Repeated identical pixels retain two independent inline objects and exact reusable references");
                editor.Undo(); Check(window.ReadMarkdown() == withImage, "Undo removes only the new duplicate image");
                editor.Redo(); Check(window.ReadMarkdown() == duplicateImage && Document(window).ObjectPositions().Length == 2, "Redo restores the duplicate object's exact identity and reference");
                editor.Undo(); Pump(800);
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
                recovered.Toggle(); Pump(800);
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
                Call(dropped, "Drop", editor, drag); Pump(800);
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
