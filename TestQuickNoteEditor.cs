using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using WD = System.Windows.Documents;
using WC = System.Windows.Controls;

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
                System.Windows.Media.Imaging.BitmapSource source;
                using (var stream = new MemoryStream(originalPixels)) source = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                byte[] encoded = (byte[])typeof(QuickNoteWindow).GetMethod("EncodePng", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { source });
                encodedImages[index] = encoded;
                Check(SamePixels(originalPixels, encoded), "WPF clipboard PNG encoding preserves nonuniform pixels at " + dpi + " DPI");
            }
            for (int index = 0; index < imageDpis.Length; index++) {
                string stored = store.StoreImage(encodedImages[index]);
                Check(SamePixels(originals[index], File.ReadAllBytes(store.ResolveImage(stored))), "Image storage preserves nonuniform pixels at " + imageDpis[index] + " DPI");
            }
            string[] samples = { "", "一行", "一\n二\n", "一\r\n二\r\n", "混合\r\n换行\n不变", "# 标题\n- 项目\n\n**原样** `code` <tag>\n", "![远程](https://example.com/x.png)\n![越界](../x.png)", "![缺图](<图片/none.png>)" };
            foreach (string sample in samples) Check(QuickNoteMarkdown.Text(QuickNoteMarkdown.Create(sample, store), "\n") == sample, "Markdown must round trip exactly: " + sample);
            string imagePath = store.StoreImage(Png());
            string imageText = "前文\r\n![图片](<" + imagePath + ">)\r\n后文";
            var document = QuickNoteMarkdown.Create(imageText, store);
            Check(QuickNoteMarkdown.Text(document, "\r\n") == imageText, "Inline image preserves surrounding text and exact reference");
            bool hasImage = false;
            foreach (WD.Inline inline in ((WD.Paragraph)document.Blocks.FirstBlock).Inlines) if (inline is WD.InlineUIContainer) hasImage = true;
            Check(hasImage, "A safe local PNG renders inline");
            var paragraphs = new WD.FlowDocument(); paragraphs.Blocks.Add(new WD.Paragraph(new WD.Run("前"))); paragraphs.Blocks.Add(new WD.Paragraph(new WD.Run("后")));
            Check(QuickNoteMarkdown.Text(paragraphs, "\r\n") == "前\r\n后", "Enter-created paragraphs serialize as one line break");

            using (var window = new QuickNoteWindow(store, Path.Combine(root, "state"))) {
                window.Toggle(); Pump(100);
                Check(window.Visible && window.TopMost, "Quick note opens visibly and pinned by default");
                var editor = (WC.RichTextBox)Field(window, "editor");
                editor.CaretPosition = editor.Document.ContentEnd; editor.AppendText("自动保存中文\n- 简单列表"); Pump(800);
                var note = store.Load(DateTime.Today);
                Check(note.Text == "自动保存中文\n- 简单列表", "500ms debounce saves edited Markdown");
                Check(!note.HasRecovery, "A successful save clears transient recovery");
                var run = (WD.Run)((WD.Paragraph)editor.Document.Blocks.FirstBlock).Inlines.FirstInline;
                editor.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(2));
                var imagePaste = new System.Windows.DataObject();
                using (var stream = new MemoryStream(Png())) imagePaste.SetData(System.Windows.DataFormats.Bitmap, System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad));
                Call(window, "Paste", editor, new System.Windows.DataObjectPastingEventArgs(imagePaste, false, System.Windows.DataFormats.Bitmap));
                string replaced = QuickNoteMarkdown.Text(editor.Document, "\n");
                Check(replaced.StartsWith("![图片](<" + imagePath + ">)\n保存中文"), "Image paste replaces exactly the selected text");
                editor.Undo(); Check(QuickNoteMarkdown.Text(editor.Document, "\n") == note.Text, "Image replacement and newline are one undo group");
                editor.Redo(); Check(QuickNoteMarkdown.Text(editor.Document, "\n") == replaced, "Redo restores the whole image replacement"); editor.Undo();
                var paste = new System.Windows.DataObject(); paste.SetData(System.Windows.DataFormats.UnicodeText, "\n粘贴纯文本 **保持Markdown**");
                editor.CaretPosition = editor.Document.ContentEnd;
                Call(window, "Paste", editor, new System.Windows.DataObjectPastingEventArgs(paste, false, System.Windows.DataFormats.UnicodeText));
                Check(QuickNoteMarkdown.Text(editor.Document, "\n").EndsWith("**保持Markdown**"), "The actual paste handler accepts plain Unicode Markdown from its event snapshot");
                editor.Undo(); Pump(800); Check(store.Load(DateTime.Today).Text == note.Text, "Undo of pasted text keeps the prior note intact");
                editor.CaretPosition = editor.Document.ContentEnd; Call(window, "InsertImage", Png()); editor.AppendText("图片后文字"); Pump(800);
                string withImage = store.Load(DateTime.Today).Text;
                Check(withImage.Contains("![图片](<" + imagePath + ">)"), "Inserted image is a reusable relative Markdown reference");
                Check(withImage.StartsWith("自动保存中文\n- 简单列表") && withImage.EndsWith("图片后文字"), "Image insertion retains text on both sides");
                Check(editor.CanUndo, "Image/text editor exposes native undo");
                editor.Undo(); Pump(20); Check(editor.CanRedo, "Undo permits native redo"); editor.Redo(); Pump(800);
                Check(store.Load(DateTime.Today).Text == withImage, "Undo and redo retain Markdown and image identity");
                typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
                editor.AppendText("输入法组合"); Pump(700);
                Check(store.Load(DateTime.Today).Text == withImage, "Autosave waits while an IME composition is active");
                window.Toggle(); Check(window.Visible, "Toggle does not hide an active IME composition");
                Check(!window.FlushAndClose(), "Exit waits for IME composition instead of dropping pending text");
                typeof(QuickNoteWindow).GetField("composing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, false);
                Check((bool)Call(window, "Flush"), "Committed composition can be saved");
                withImage += "输入法组合";
                window.Toggle(); Check(!window.Visible, "The same toggle flushes and hides");
                window.Toggle(); Pump(50); Check(QuickNoteMarkdown.Text(editor.Document, "\n") == withImage, "Reopen reloads the saved document without conversion");
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(editor), 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                editor.RaiseEvent(escape); Check(escape.Handled && !window.Visible, "An ordinary routed Escape key saves and hides the note");
                window.Toggle(); Pump(50);

                editor.AppendText("我的未存修改");
                File.AppendAllText(note.Path, "\nObsidian 外部修改");
                window.Toggle();
                Check(window.Visible, "Save conflict keeps the note visible");
                Check(File.ReadAllText(note.Path).EndsWith("Obsidian 外部修改"), "Save conflict never overwrites external edits");
                Check(store.Load(DateTime.Today).HasRecovery, "Save conflict preserves recovery text outside the vault");
                Check(!window.FlushAndClose(), "Failed flush cancels application close");
                string unsaved = QuickNoteMarkdown.Text(editor.Document, "\n");
                Call(window, "ReloadDiskKeepingDraft");
                Check(QuickNoteMarkdown.Text(editor.Document, "\n").EndsWith("Obsidian 外部修改"), "Conflict recovery reloads the latest external version");
                bool archived = false;
                foreach (string path in Directory.GetFiles(recovery, "*.md")) if (File.ReadAllText(path) == unsaved) archived = true;
                Check(archived, "Conflict recovery preserves the user's exact input as a separate Markdown draft");
                Check(window.FlushAndClose(), "A clean editor can close after its recovery is preserved");
            }

            var original = store.Load(DateTime.Today); store.SaveRecovery(original, original.Text + "\n恢复输入");
            using (var recovered = new QuickNoteWindow(store, Path.Combine(root, "state"))) {
                recovered.Toggle(); Pump(800);
                Check(store.Load(DateTime.Today).Text.EndsWith("恢复输入"), "Matching-revision crash recovery is restored and saved");
                Check(recovered.FlushAndClose(), "Recovered note flushes cleanly");
            }
            var dropStore = new QuickNoteStore(vault, "拖入速记", Path.Combine(root, "drop-recovery"));
            using (var dropped = new QuickNoteWindow(dropStore, Path.Combine(root, "drop-state"))) {
                dropped.Toggle(); Pump(100);
                var editor = (WC.RichTextBox)Field(dropped, "editor"); editor.AppendText("拖入前后文字");
                byte[] droppedPixels = PatternPng(192);
                string input = Path.Combine(root, "drag-source.png"); File.WriteAllBytes(input, droppedPixels);
                var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new string[] { input });
                var drag = (System.Windows.DragEventArgs)Activator.CreateInstance(typeof(System.Windows.DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { data, System.Windows.DragDropKeyStates.None, System.Windows.DragDropEffects.Copy, editor, new System.Windows.Point(20, 20) }, System.Globalization.CultureInfo.InvariantCulture);
                drag.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
                Call(dropped, "Drop", editor, drag); Pump(800);
                string markdown = dropStore.Load(DateTime.Today).Text;
                var match = System.Text.RegularExpressions.Regex.Match(markdown, @"!\[图片\]\(<([^>]+)>\)");
                Check(drag.Handled && match.Success, "The actual file-drop handler writes a local Markdown image reference");
                Check(markdown.Contains("拖入前后文字"), "File drop preserves the existing note text");
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
