using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WC = System.Windows.Controls;
using W = System.Windows;
using WM = System.Windows.Media;
using WMI = System.Windows.Media.Imaging;

// This deliberately uses a hidden, isolated form. Queue latency and native
// position-call timings are responsiveness evidence, not displayed frame rates.
internal static class TestQuickNoteResponsiveness {
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static void Call(object value, string name, params object[] args) {
        try { value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(value, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static double Percentile(List<double> values, double percentile) { if (values.Count == 0) return 0; values.Sort(); return values[Math.Min(values.Count - 1, (int)(values.Count * percentile))]; }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Idle(QuickNoteWindow window) {
        var watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(1); } while (!window.IsDisposed && ((bool)Field(window, "workerBusy") || Field(window, "pendingText") != null) && watch.ElapsedMilliseconds < 8000);
        Check(window.IsDisposed || !(bool)Field(window, "workerBusy"), "Storage must finish within the isolated fixture timeout.");
    }
    static WMI.BitmapSource Pixels(int width, int height) {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) { int offset = (y * width + x) * 4; data[offset] = (byte)(x / 8); data[offset + 1] = (byte)(y / 5); data[offset + 2] = (byte)((x + y) / 16); data[offset + 3] = 255; }
        var result = WMI.BitmapSource.Create(width, height, 144, 144, WM.PixelFormats.Bgra32, null, data, width * 4); result.Freeze(); return result;
    }
    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string root = Path.Combine(Path.GetTempPath(), "tingji-note-responsive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            string vault = Path.Combine(root, "vault"); Directory.CreateDirectory(vault);
            var store = new QuickNoteStore(vault, "速记", Path.Combine(root, "recovery"));
            var existing = store.Load(DateTime.Today);
            store.Save(existing, "隔离性能样本，不包含用户内容。\n" + new string('测', 1200));
            var lags = new List<double>(); var edits = new List<double>(); var moves = new List<double>();
            int posted = 0, arrived = 0;
            using (var window = new QuickNoteWindow(store, Path.Combine(root, "state"), delegate { Thread.Sleep(120); })) {
                IntPtr ignored = window.Handle;
                bool show = Array.IndexOf(args, "--show") >= 0;
                if (show) window.Toggle(); else Call(window, "LoadToday");
                Idle(window);
                var editor = (WC.RichTextBox)Field(window, "editor");
                editor.Measure(new System.Windows.Size(332, 320)); editor.Arrange(new System.Windows.Rect(0, 0, 332, 320)); editor.UpdateLayout();
                var warmup = Stopwatch.StartNew(); while (warmup.ElapsedMilliseconds < 500) { Application.DoEvents(); Thread.Sleep(1); }
                var watch = Stopwatch.StartNew();
                var renderIntervals = new List<double>(); long lastRender = 0;
                EventHandler render = delegate { long now = watch.ElapsedTicks; if (lastRender != 0) renderIntervals.Add((now - lastRender) * 1000d / Stopwatch.Frequency); lastRender = now; };
                if (show) WM.CompositionTarget.Rendering += render;
                using (var heartbeat = new System.Threading.Timer(delegate {
                    long due = watch.ElapsedTicks; Interlocked.Increment(ref posted);
                    try { window.BeginInvoke((Action)delegate { lags.Add((watch.ElapsedTicks - due) * 1000d / Stopwatch.Frequency); arrived++; }); } catch (InvalidOperationException) { }
                }, null, 0, 10)) {
                    long nextInput = 0, nextMove = 0;
                    while (watch.ElapsedMilliseconds < 3500) {
                        long now = watch.ElapsedMilliseconds;
                        if (now % 1000 < 250 && now >= nextInput) {
                            var operation = Stopwatch.StartNew(); editor.AppendText("输入"); editor.UpdateLayout(); edits.Add(operation.Elapsed.TotalMilliseconds); nextInput = now + 50;
                        }
                        if (now >= nextMove) {
                            var operation = Stopwatch.StartNew(); window.Location = new Point((int)(now / 20 % 10), 0); if (show) editor.InvalidateVisual(); moves.Add(operation.Elapsed.TotalMilliseconds); nextMove = now + 20;
                        }
                        Application.DoEvents(); Thread.Sleep(1);
                    }
                }
                if (show) WM.CompositionTarget.Rendering -= render;
                for (int drain = 0; drain < 100 && arrived < posted; drain++) { Application.DoEvents(); Thread.Sleep(1); }
                Console.WriteLine("UI queue lag ms: p50=" + Percentile(lags, .5).ToString("F2") + ", p95=" + Percentile(lags, .95).ToString("F2") + ", max=" + Percentile(lags, 1).ToString("F2"));
                Console.WriteLine("Input+layout ms: p95=" + Percentile(edits, .95).ToString("F2") + ", max=" + Percentile(edits, 1).ToString("F2") + "; native position-call max=" + Percentile(moves, 1).ToString("F2"));
                Console.WriteLine("Probe callbacks=" + arrived + "; edits=" + edits.Count + "; moves=" + moves.Count + "; visible=" + window.Visible);
                Console.WriteLine("WPF tier=" + (WM.RenderCapability.Tier >> 16) + "; render callbacks=" + renderIntervals.Count + "; callback interval p95/max ms=" + Percentile(renderIntervals, .95).ToString("F2") + "/" + Percentile(renderIntervals, 1).ToString("F2") + " (not screen FPS)");
                Check(arrived >= 50 && Percentile(lags, .95) < 100, "The UI must keep serving callbacks during controlled 120ms storage work.");
                if (Array.IndexOf(args, "--assert-responsive") >= 0 && Percentile(lags, 1) >= 100) throw new Exception("The editor must continue serving UI callbacks while durable storage is slow.");
                Idle(window);
                // An in-flight old snapshot must never clear or lose newer input.
                editor.AppendText("旧快照"); Call(window, "Flush");
                Check((bool)Field(window, "workerBusy"), "A save is asynchronous.");
                editor.AppendText("最新输入甲"); Call(window, "Flush"); editor.AppendText("最新输入乙"); Call(window, "Flush");
                string expected = QuickNoteMarkdown.Text(editor.Document, "\n"); Idle(window);
                Check(store.Load(DateTime.Today).Text == expected, "The latest coalesced snapshot is durable.");
                Check(!(bool)Field(window, "dirty"), "Only the latest completed version clears dirty state.");
                // Event data is a local read-only snapshot, never the system clipboard.
                var data = new W.DataObject(); var bitmap = Pixels(320, 96);
                data.SetData(W.DataFormats.Bitmap, bitmap); data.SetData(W.DataFormats.UnicodeText, "不可改动的剪贴板文字"); data.SetData("Tingji.Private.Test", new byte[] { 3, 1, 4 });
                string[] formats = data.GetFormats(false); object privateData = data.GetData("Tingji.Private.Test");
                var operationPaste = Stopwatch.StartNew(); Call(window, "Paste", editor, new W.DataObjectPastingEventArgs(data, false, W.DataFormats.Bitmap));
                Console.WriteLine("320x96 image Paste UI call ms=" + operationPaste.Elapsed.TotalMilliseconds.ToString("F2"));
                Check(object.ReferenceEquals(bitmap, data.GetData(W.DataFormats.Bitmap)), "Paste does not replace the event bitmap.");
                Check((string)data.GetData(W.DataFormats.UnicodeText) == "不可改动的剪贴板文字" && object.ReferenceEquals(privateData, data.GetData("Tingji.Private.Test")), "Paste preserves Unicode and private event formats.");
                Check(string.Join("|", formats) == string.Join("|", data.GetFormats(false)), "Paste preserves all event formats.");
                Idle(window);
                for (int queued = 0; queued < 17; queued++) Call(window, "Paste", editor, new W.DataObjectPastingEventArgs(data, false, W.DataFormats.Bitmap));
                Check((int)Field(window, "queuedImageCount") == 16, "Pending image work is bounded before accepting another bitmap.");
                editor.Undo(); string cancelledQueue = QuickNoteMarkdown.Text(editor.Document, "\n"); Idle(window);
                Check((int)Field(window, "queuedImageCount") == 0 && (long)Field(window, "queuedImagePixels") == 0, "Undo releases all pending bitmap budget.");
                Check(QuickNoteMarkdown.Text(editor.Document, "\n") == cancelledQueue, "Cancelled queued images never return after Undo.");
                var large = new W.DataObject(W.DataFormats.Bitmap, Pixels(1600, 5000));
                operationPaste.Restart(); Call(window, "Paste", editor, new W.DataObjectPastingEventArgs(large, false, W.DataFormats.Bitmap));
                Console.WriteLine("1600x5000 image Paste UI call ms=" + operationPaste.Elapsed.TotalMilliseconds.ToString("F2"));
                Check(operationPaste.ElapsedMilliseconds < 100, "Long-image encoding and durable storage must leave the paste UI call.");
                editor.CaretPosition = editor.Document.ContentEnd; editor.AppendText("图片处理期间继续输入");
                Idle(window);
                Check(QuickNoteMarkdown.Text(editor.Document, "\n").EndsWith("图片处理期间继续输入"), "Completing an image preserves newer text and its caret location.");
                Check(editor.Selection.IsEmpty && editor.CaretPosition.CompareTo(editor.Document.ContentEnd.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Backward)) == 0, "An asynchronous insertion does not jump a newer live caret.");
                string beforeUndoImage = QuickNoteMarkdown.Text(editor.Document, "\n");
                int beforeImages = System.Text.RegularExpressions.Regex.Matches(beforeUndoImage, @"!\[图片\]").Count;
                Call(window, "Paste", editor, new W.DataObjectPastingEventArgs(large, false, W.DataFormats.Bitmap)); editor.Undo();
                string afterUndo = QuickNoteMarkdown.Text(editor.Document, "\n"); Idle(window);
                Check(QuickNoteMarkdown.Text(editor.Document, "\n") == afterUndo, "Undo while an image is pending cancels it rather than resurrecting the insertion.");
                beforeImages = System.Text.RegularExpressions.Regex.Matches(afterUndo, @"!\[图片\]").Count;
                // Close must wait for an in-flight image as well as its Markdown.
                Call(window, "Paste", editor, new W.DataObjectPastingEventArgs(data, false, W.DataFormats.Bitmap));
                editor.AppendText("退出前最后输入"); int callbacks = 0;
                Check(!window.FlushAndClose(delegate { Check(window.IsDisposed, "The close continuation runs after disposal."); callbacks++; }), "Dirty close waits asynchronously.");
                Check(!window.FlushAndClose(delegate { callbacks += 100; }), "Repeated close clicks do not duplicate the continuation.");
                Idle(window);
                Check(window.IsDisposed && callbacks == 1, "The pending close resumes exactly once.");
                Check(store.Load(DateTime.Today).Text.EndsWith("退出前最后输入"), "Asynchronous close durably saves the final input.");
                Check(System.Text.RegularExpressions.Regex.Matches(store.Load(DateTime.Today).Text, @"!\[图片\]").Count == beforeImages + 1, "Closing waits for the pending image reference to become durable.");
                if (!window.FlushAndClose()) { for (int i = 0; i < 300 && !window.IsDisposed; i++) { Application.DoEvents(); Thread.Sleep(10); } }
            }
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
