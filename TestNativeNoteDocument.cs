// Synthetic own-window/private-desktop fixture. No shared clipboard or global
// input, real user notes, performance sampling, or incoming RTF are used.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class TestNativeNoteDocument {
    static int assertions, failures;
    static byte[] png;
    static void Check(bool condition, string label) { assertions++; if (!condition) throw new Exception(label); }
    static void Case(string name, Action action) {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
    }
    static byte[] Png(int width, int height, float dpi) {
        using (var image = new Bitmap(width, height)) using (var stream = new MemoryStream()) {
            image.SetResolution(dpi, dpi);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) image.SetPixel(x, y, Color.FromArgb(255, x * 3 % 256, y * 7 % 256, (x + y) * 5 % 256));
            image.Save(stream, ImageFormat.Png); return stream.ToArray();
        }
    }
    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); png = Png(80, 32, 192);
        using (var uiFont = new Font("Microsoft YaHei UI", 9))
        using (var editorFont = new Font("Microsoft YaHei UI", 10.5f))
        using (var form = new Form { AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96), Font = uiFont, Text = "Synthetic native document", ShowInTaskbar = false, Width = 480, Height = 360 })
        using (var box = new NativeNoteInputBox { Dock = DockStyle.Fill, Font = editorFont }) {
            form.Controls.Add(box); form.Shown += delegate { form.BeginInvoke(new Action(delegate {
                try { Run(box); } catch (Exception error) { failures++; Console.WriteLine("FATAL " + error.GetType().Name + " " + error.Message); }
                finally { form.Close(); }
            })); }; Application.Run(form);
        }
        Console.WriteLine("assertions=" + assertions + "; failures=" + failures + "; clipboard_access=0; global_input=0"); return failures == 0 ? 0 : 1;
    }
    static void Run(NativeNoteInputBox box) {
        Func<string, byte[]> resolver = delegate(string target) { return target == "图片/image.png" ? png : null; };
        string first = "![第一张](图片/image.png \"title A\")", second = "![[图片/image.png|second]]";
        string document = "前😀\r\n" + first + "\n中间\r\n" + second + "\n后";
        using (var note = new NativeNoteDocument(box, resolver)) {
            Console.WriteLine("CONTEXT native_document=true; form_DPI=" + box.FindForm().DeviceDpi + "; editor_DPI=" + box.DeviceDpi + "; autoscale=" + box.FindForm().AutoScaleMode);
            Check(box.DeviceDpi == box.FindForm().DeviceDpi && box.DeviceDpi >= 96, "Editor DPI differs from its production-style DPI-scaled parent");
            string[] text = { "", "一行", "一\n二\n", "一\r\n二\r\n", "混合\r\n换行\n不变", "A😀B\t{}\\", "# 标题\n- 列表\n\n**原样** `code` <tag>\n", "![远程](https://example.com/image.png)\n![越界](../image.png)\n![缺图](图片/missing.png)" };
            foreach (string sample in text) {
                string source = sample;
                Case("exact plain Markdown/newline round trip", delegate { note.Load(source); Check(note.Markdown() == source, "Source text changed"); Check(note.ObjectPositions().Length == 0, "Literal syntax became an object"); });
            }
            Case("background preparation then exact image mapping", delegate {
                int owner = Thread.CurrentThread.ManagedThreadId, preparedThread = owner; NativeNotePrepared prepared = null; Exception failure = null;
                var thread = new Thread(delegate() { try { preparedThread = Thread.CurrentThread.ManagedThreadId; prepared = NativeNoteDocument.PrepareLoad(document, resolver); } catch (Exception error) { failure = error; } });
                thread.Start(); Check(thread.Join(5000), "Preparation did not finish"); if (failure != null) throw failure;
                Check(preparedThread != owner, "Preparation ran on editor thread"); note.ApplyPrepared(prepared);
                Check(note.Markdown() == document, "Prepared import changed alt/title/wiki/newlines"); Check(note.RetainedObjectCount == 2 && note.ObjectPositions().Length == 2, "Distinct image occurrence mappings missing");
                int[] positions = note.ObjectPositions(); Check(note.MarkdownSelection(positions[0], positions[1] + 1) == first + "\n中间\r\n" + second, "Selection copy lost exact image tokens or breaks");
            });
            foreach (string unsupported in new[] { "A\ufffcB", "\ufffc", "A\vB", "A\rB", "A\0B" }) {
                string source = unsupported;
                Case("unsupported controls refuse before visible mutation", delegate {
                    note.Load(document); box.Select(0, 1);
                    using (NativeNoteDocument.LiveRange range = note.CaptureRange(0, 1)) {
                        bool refused = false; try { note.Load(source); } catch (NativeNoteUnsupportedTextException) { refused = true; }
                        Check(refused && note.Markdown() == document, "Rejected load changed the old document"); Check(note.RetainedObjectCount == 2, "Rejected load released image mappings");
                        Check(box.SelectionStart == 0 && box.SelectionLength == 1 && range.Text == "前", "Rejected load invalidated selection or live anchor");
                        refused = false; try { note.InsertText(source); } catch (NativeNoteUnsupportedTextException) { refused = true; }
                        Check(refused && note.Markdown() == document && range.Text == "前", "Rejected input mutated source/anchor");
                    }
                });
            }
            Case("length preflight preserves current source", delegate {
                note.Load(document); bool refused = false;
                try { note.Load(new string('x', NativeNoteDocument.MaxTextLength + 1)); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && note.Markdown() == document && note.RetainedObjectCount == 2, "Oversized source replaced the old document");
            });
            Case("129 image references fall back to editable exact Markdown and save", delegate {
                var many = new StringBuilder(); for (int i = 0; i < 129; i++) many.Append(first).Append('\n'); string source = many.ToString();
                NativeNotePrepared prepared = NativeNoteDocument.PrepareLoad(source, resolver);
                Check(prepared.TextImageFallback && prepared.References.Length == 0, "Oversized preview did not use the explicit text fallback"); note.ApplyPrepared(prepared);
                Check(note.TextImageFallback && note.Markdown() == source && note.ObjectPositions().Length == 0 && note.RetainedObjectCount == 0, "Fallback changed source or retained pictures");
                box.Select(box.TextLength, 0); note.InsertText("继续编辑"); string edited = source + "继续编辑"; Check(note.Markdown() == edited, "Fallback document is not editable");
                string fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tingji-native-document-" + Guid.NewGuid().ToString("N")));
                try {
                    var store = new QuickNoteStore(Path.Combine(fixture, "vault"), "速记", Path.Combine(fixture, "recovery"));
                    QuickNoteDocument stored = store.Load(new DateTime(2001, 2, 3)); store.Save(stored, note.Markdown());
                    Check(File.ReadAllText(stored.Path, Encoding.UTF8) == edited && store.Load(new DateTime(2001, 2, 3)).Text == edited, "Text fallback did not survive real atomic Markdown storage");
                } finally {
                    // Only the unique synthetic fixture created above is removed.
                    string parent = Path.GetFullPath(Path.GetDirectoryName(fixture)).TrimEnd(Path.DirectorySeparatorChar);
                    if (!String.Equals(parent, Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fixture).StartsWith("tingji-native-document-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup target");
                    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
                }
                box.Undo(); Check(note.Markdown() == source, "Fallback edit undo changed references"); box.Redo(); Check(note.Markdown() == edited, "Fallback edit redo changed references");
            });
            Case("retained image budget inserts literal reference with exact undo redo", delegate {
                var many = new StringBuilder(); for (int i = 0; i < 128; i++) many.Append(first).Append('\n'); string source = many.ToString();
                note.Load(source); Check(!note.TextImageFallback && note.RetainedObjectCount == 128, "At-budget fixture unexpectedly fell back");
                box.Select(box.TextLength, 0); note.ReplaceSelection(first + "\n"); string expected = source + first + "\n";
                Check(note.TextImageFallback && note.Markdown() == expected && note.ObjectPositions().Length == 128 && note.RetainedObjectCount == 128, "Budget insertion lost a reference or old undo identity");
                box.Undo(); Check(note.Markdown() == source, "Budget fallback undo changed old pictures"); box.Redo(); Check(note.Markdown() == expected, "Budget fallback redo changed the literal reference"); box.Undo();
                box.Select(note.ObjectPositions()[0], 1); string knownRtf = box.SelectedRtf; box.Select(box.TextLength, 0); box.SelectedRtf = knownRtf; string before = note.RawText();
                bool refused = false; try { note.ReplaceSelection(first); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && note.RawText() == before && note.RetainedObjectCount == 128, "Unknown object bypassed the budget fallback or changed current source");
                box.Undo(); Check(note.Markdown() == source, "Unknown at-budget clone undo did not recover references");
            });
            Case("cumulative preview pixels fall back without importing large objects", delegate {
                byte[] large; using (var image = new Bitmap(1024, 1024)) using (var stream = new MemoryStream()) { using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.Blue); image.Save(stream, ImageFormat.Png); large = stream.ToArray(); }
                var many = new StringBuilder(); for (int i = 0; i < 9; i++) many.Append(first).Append('\n'); string source = many.ToString();
                NativeNotePrepared prepared = NativeNoteDocument.PrepareLoad(source, delegate(string target) { return large; });
                Check(prepared.TextImageFallback && prepared.References.Length == 0 && prepared.TotalPreviewPixels == 0, "Pixel budget did not yield bounded text preparation"); note.ApplyPrepared(prepared);
                Check(note.Markdown() == source && note.ObjectPositions().Length == 0, "Pixel fallback changed original Markdown");
            });
            Case("invalid preview at count limit cannot trigger text fallback", delegate {
                note.Load(document); var many = new StringBuilder(); for (int i = 0; i < 129; i++) many.Append(first).Append('\n'); int count = 0; bool refused = false;
                try { note.ApplyPrepared(NativeNoteDocument.PrepareLoad(many.ToString(), delegate(string target) { count++; return count == 129 ? new byte[40] : png; })); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && count == 129 && note.Markdown() == document && !note.TextImageFallback, "Invalid PNG was mistaken for a capacity fallback");
            });
            Case("corrupt preview rejected before replacing existing source", delegate {
                note.Load(document); bool refused = false;
                try { note.ApplyPrepared(NativeNoteDocument.PrepareLoad("![坏图](bad)", delegate(string target) { return new byte[40]; })); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && note.Markdown() == document, "Invalid PNG replaced the old document");
            });
            Case("ordinary text does not resolve image-looking Markdown", delegate {
                note.Load("前后"); box.Select(1, 0); note.InsertText(first);
                Check(note.Markdown() == "前" + first + "后" && note.ObjectPositions().Length == 0, "Plain paste unexpectedly rendered a picture");
                Check(box.SelectionStart == 1 + first.Length && box.SelectionLength == 0, "Plain insertion caret does not follow native CP");
                box.Undo(); Check(note.Markdown() == "前后", "Plain insertion undo lost source"); box.Redo(); Check(note.Markdown() == "前" + first + "后", "Plain insertion redo changed literal Markdown");
            });
            foreach (string newline in new[] { "\n", "\r\n" }) {
                string value = newline;
                Case("Enter and ShiftEnter use explicit note newline", delegate {
                    note.Load(document); int firstCp = note.ObjectPositions()[0]; box.Select(firstCp, 0); note.InsertText(value);
                    string expected = "前😀\r\n" + value + first + "\n中间\r\n" + second + "\n后";
                    Check(note.Markdown() == expected, "Entered newline changed original mixed breaks"); Check(box.SelectionStart == firstCp + 1 && box.SelectionLength == 0, "Newline caret used Markdown length instead of native CP");
                    box.Undo(); Check(note.Markdown() == document, "Enter undo altered image references"); box.Redo(); Check(note.Markdown() == expected, "Enter redo altered image references");
                    note.InsertText(value); Check(note.Markdown() == "前😀\r\n" + value + value + first + "\n中间\r\n" + second + "\n后", "ShiftEnter-equivalent newline was normalized differently");
                });
            }
            Case("picture insertion and following newline undo together", delegate {
                note.Load("前选中后"); box.Select(1, 2); note.ReplaceSelectionPrepared(NativeNoteDocument.PrepareSelection(first + "\n", resolver));
                Check(note.Markdown() == "前" + first + "\n后", "Selected source replaced incorrectly"); Check(box.SelectionStart == 3 && box.SelectionLength == 0, "Image and break insertion caret CP wrong");
                box.Undo(); Check(note.Markdown() == "前选中后", "Picture and break did not undo as one action"); box.Redo(); Check(note.Markdown() == "前" + first + "\n后", "Picture redo lost token");
            });
            Case("repeated image delete undo redo keeps distinct tokens", delegate {
                note.Load(document);
                for (int i = 0; i < 5; i++) {
                    box.Select(note.ObjectPositions()[0], 1); box.SelectedText = "";
                    Check(note.Markdown() == document.Replace(first, ""), "Image deletion removed wrong occurrence"); Check(note.RetainedObjectCount == 2, "Undo identity released too soon");
                    box.Undo(); Check(note.Markdown() == document, "Image undo changed canonical mapping"); box.Redo(); Check(note.Markdown() == document.Replace(first, ""), "Image redo changed mapping"); box.Undo(); Check(note.Markdown() == document, "Second undo changed mapping");
                }
            });
            Case("unknown object refuses serialization until undo", delegate {
                note.Load(document); box.Select(note.ObjectPositions()[0], 1); string safeInternalRtf = box.SelectedRtf;
                box.Select(box.TextLength, 0); box.SelectedRtf = safeInternalRtf;
                bool refused = false; try { string invalid = note.Markdown(); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && note.ObjectPositions().Length == 3, "Unmapped native clone silently serialized or disappeared");
                refused = false; try { note.ReplaceSelection(first); } catch (NativeNoteSerializationException) { refused = true; }
                Check(refused && note.ObjectPositions().Length == 3, "Image insertion guessed an unknown existing token");
                box.Undo(); Check(note.Markdown() == document, "Undo of blocked unknown object did not recover safe state");
            });
            Case("preview dimensions and resize preserve undo and identity", delegate {
                byte[] portrait = Png(300, 900, 216);
                NativeNotePrepared prepared = NativeNoteDocument.PrepareLoad("前![纵图](portrait)\n后", delegate(string target) { return portrait; });
                Check(prepared.Rtf.Contains(@"\picw300\pich900") && prepared.Rtf.Contains(@"\pichgoal3900"), "Preview did not retain pixels and bound logical height");
                note.ApplyPrepared(prepared); string before = note.Markdown(); box.Select(0, 0); note.InsertText("X");
                int retained = note.RetainedObjectCount; box.Width += 50; box.Width -= 50;
                Check(note.Markdown() == "X" + before && note.RetainedObjectCount == retained && box.CanUndo, "Resize rebuilt RTF or mappings");
                box.Undo(); Check(note.Markdown() == before, "Resize destroyed the typing undo action");
            });
            Case("live selection follows edits and unload invalidates it", delegate {
                note.Load("AA选中ZZ"); NativeNoteDocument.LiveRange range = note.CaptureRange(2, 4);
                try {
                    Check(range.Start == 2 && range.End == 4 && range.Text == "选中", "Initial native range wrong");
                    box.Select(0, 0); note.InsertText("X"); Check(range.Start == 3 && range.End == 5 && range.Text == "选中", "Anchor did not follow earlier insertion");
                    box.Select(0, 1); box.SelectedText = ""; Check(range.Start == 2 && range.End == 4, "Anchor did not follow earlier deletion");
                    box.Select(range.Start, range.End - range.Start); box.SelectedText = ""; Check(range.Start == 2 && range.End == 2 && range.Text == "", "Deleted captured text did not collapse anchor");
                    note.Load("新文档"); bool refused = false; try { int start = range.Start; } catch (NativeNoteSerializationException) { refused = true; } Check(refused, "Stale range crossed document generation");
                } finally { range.Dispose(); }
                note.Load(document); Check(note.RetainedObjectCount == 2, "Fixture mappings missing"); note.Load("新的正文"); Check(note.RetainedObjectCount == 0, "Unload retained old COM identities");
            });
        }
    }
}
