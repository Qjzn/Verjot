using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Only the Windows system RichEdit implementation is loaded. The window owns
// paste/drop policy; this control never enables automatic RTF/OLE import.
internal class NativeNoteInputBox : RichTextBox {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    static readonly IntPtr module = LoadLibraryEx("msftedit.dll", IntPtr.Zero, 0x800);
    protected override CreateParams CreateParams {
        get {
            if (module == IntPtr.Zero) throw new InvalidOperationException("无法载入 Windows 原生文本组件。");
            CreateParams value = base.CreateParams; value.ClassName = "RICHEDIT50W"; return value;
        }
    }
    internal NativeNoteInputBox() {
        DetectUrls = false; EnableAutoDragDrop = false; AllowDrop = false; MaxLength = NativeNoteDocument.MaxTextLength;
    }
}

internal class NativeNoteSerializationException : InvalidOperationException {
    internal NativeNoteSerializationException(string message) : base(message) { }
}
internal sealed class NativeNoteUnsupportedTextException : NativeNoteSerializationException {
    internal NativeNoteUnsupportedTextException(string message) : base(message) { }
}
internal sealed class NativeNotePreviewBudgetException : NativeNoteSerializationException {
    internal NativeNotePreviewBudgetException(string message) : base(message) { }
}

// Immutable, worker-prepared native import. RTF is generated exclusively from
// escaped Markdown and bounded local PNG previews, never from incoming RTF.
internal sealed class NativeNotePrepared {
    internal readonly string Source, Rtf;
    internal readonly string[] References;
    internal readonly int[] PreviewPixels;
    internal readonly bool FullDocument, TextImageFallback;
    internal readonly int NativeLength, TotalPreviewPixels;
    internal NativeNotePrepared(string source, string rtf, List<string> references, List<int> pixels, bool fullDocument, int nativeLength, int totalPixels, bool textImageFallback) {
        Source = source; Rtf = rtf; References = references.ToArray(); PreviewPixels = pixels.ToArray();
        FullDocument = fullDocument; NativeLength = nativeLength; TotalPreviewPixels = totalPixels; TextImageFallback = textImageFallback;
    }
}

internal sealed class NativeNoteDocument : IDisposable {
    internal const int MaxTextLength = 4 * 1024 * 1024;
    const int MaxRetained = 128, MaxPreviewPixels = 2 * 1024 * 1024, MaxRetainedPreviewPixels = 8 * 1024 * 1024;
    const int GetOleInterface = 0x43c, GetTextEx = 0x45e, GetTextLengthEx = 0x45f, StopGroupTyping = 0x458;
    static readonly Guid documentIid = new Guid("8CC497C0-A1DF-11CE-8098-00AA0047BE5D"), unknownIid = new Guid("00000000-0000-0000-C000-000000000046");
    static readonly Regex images = new Regex(@"!\[[^\]\r\n]*\]\(\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^)\r\n]+?))\s*\)|!\[\[(?<wiki>[^\]\r\n]+)\]\]", RegexOptions.Compiled);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct GetText { public uint cb, flags, codepage; public IntPtr defaultChar, usedDefault; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct GetLength { public uint flags, codepage; }
    // Public Windows SDK TOM.h vtable offsets: ITextDocument.Range=24;
    // ITextRange.GetText=7, GetStart=14, GetEnd=16, GetEmbeddedObject=57.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateRange(IntPtr self, int start, int end, out IntPtr range);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EmbeddedObject(IntPtr self, out IntPtr embedded);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RangePosition(IntPtr self, out int position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RangeText(IntPtr self, out IntPtr bstr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr Send(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendOle(IntPtr hwnd, int message, IntPtr wparam, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendText(IntPtr hwnd, int message, ref GetText options, IntPtr output);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendLength(IntPtr hwnd, int message, ref GetLength options, IntPtr unused);

    sealed class Reference { internal string Markdown; internal int Pixels; }
    sealed class ObjectInfo { internal int Cp; internal IntPtr Identity; }
    sealed class Snapshot : IDisposable {
        internal readonly List<ObjectInfo> Items = new List<ObjectInfo>();
        public void Dispose() { foreach (ObjectInfo item in Items) if (item.Identity != IntPtr.Zero) Marshal.Release(item.Identity); Items.Clear(); }
    }
    readonly RichTextBox box;
    readonly Func<string, byte[]> resolvePreview;
    readonly Dictionary<IntPtr, Reference> retained = new Dictionary<IntPtr, Reference>();
    readonly List<LiveRange> anchors = new List<LiveRange>();
    readonly int ownerThread;
    IntPtr ole, hwnd;
    bool disposed, invalidated, loadedTextValid = true, textImageFallback;
    int generation, retainedPixels;
    internal int RetainedObjectCount { get { Check(); return retained.Count; } }
    internal bool TextImageFallback { get { Check(); return textImageFallback; } }

    internal NativeNoteDocument(RichTextBox control, Func<string, byte[]> safeLocalPngPreview) {
        if (control == null) throw new ArgumentNullException("control");
        box = control; resolvePreview = safeLocalPngPreview; ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        hwnd = box.Handle;
        if (SendOle(hwnd, GetOleInterface, IntPtr.Zero, out ole) == IntPtr.Zero || ole == IntPtr.Zero) throw new NativeNoteSerializationException("原生文本组件尚未就绪。");
        box.HandleDestroyed += HandleDestroyed;
    }
    static Delegate Slot(IntPtr pointer, int slot, Type type) {
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size), type);
    }
    void Check() {
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Native note requires its owning STA thread");
        if (disposed || invalidated || !box.IsHandleCreated || box.Handle != hwnd) throw new NativeNoteSerializationException("当前编辑器已卸载，请重新打开速记。");
    }
    void HandleDestroyed(object sender, EventArgs e) { invalidated = true; ReleaseAll(); }
    void ReleaseAnchors() { while (anchors.Count != 0) anchors[anchors.Count - 1].Dispose(); generation++; }
    void ReleaseReferences() { foreach (IntPtr identity in retained.Keys) Marshal.Release(identity); retained.Clear(); retainedPixels = 0; }
    void ReleaseAll() {
        ReleaseAnchors(); ReleaseReferences();
        if (ole != IntPtr.Zero) { Marshal.Release(ole); ole = IntPtr.Zero; }
    }
    public void Dispose() {
        if (disposed) return;
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Dispose requires owning STA");
        box.HandleDestroyed -= HandleDestroyed; ReleaseAll(); disposed = true;
    }

    internal sealed class LiveRange : IDisposable {
        readonly NativeNoteDocument owner; readonly int generation;
        IntPtr range; readonly RangePosition start, end; readonly RangeText text;
        internal LiveRange(NativeNoteDocument owner, IntPtr pointer) {
            this.owner = owner; generation = owner.generation; range = pointer;
            start = (RangePosition)Slot(range, 14, typeof(RangePosition)); end = (RangePosition)Slot(range, 16, typeof(RangePosition)); text = (RangeText)Slot(range, 7, typeof(RangeText));
        }
        void Check() { owner.Check(); if (range == IntPtr.Zero || generation != owner.generation) throw new NativeNoteSerializationException("图片的原插入位置已失效。"); }
        internal int Start { get { Check(); int value; Marshal.ThrowExceptionForHR(start(range, out value)); return value; } }
        internal int End { get { Check(); int value; Marshal.ThrowExceptionForHR(end(range, out value)); return value; } }
        internal string Text {
            get {
                Check(); IntPtr value = IntPtr.Zero;
                try { Marshal.ThrowExceptionForHR(text(range, out value)); return value == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(value); }
                finally { if (value != IntPtr.Zero) Marshal.FreeBSTR(value); }
            }
        }
        public void Dispose() {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != owner.ownerThread) throw new InvalidOperationException("Range Dispose requires owning STA");
            if (range != IntPtr.Zero) { Marshal.Release(range); range = IntPtr.Zero; } owner.anchors.Remove(this);
        }
    }
    internal LiveRange CaptureRange(int start, int end) {
        Check(); if (start < 0 || end < start) throw new ArgumentOutOfRangeException("start");
        IntPtr document = IntPtr.Zero, range = IntPtr.Zero;
        try {
            Guid iid = documentIid; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ole, ref iid, out document));
            var create = (CreateRange)Slot(document, 24, typeof(CreateRange)); Marshal.ThrowExceptionForHR(create(document, start, end, out range));
            if (range == IntPtr.Zero) throw new NativeNoteSerializationException("无法保留图片插入位置。");
            var result = new LiveRange(this, range); range = IntPtr.Zero; anchors.Add(result); return result;
        } finally { if (range != IntPtr.Zero) Marshal.Release(range); if (document != IntPtr.Zero) Marshal.Release(document); }
    }
    internal string RawText() {
        Check(); var length = new GetLength { flags = 2 | 8, codepage = 1200 };
        long count = SendLength(hwnd, GetTextLengthEx, ref length, IntPtr.Zero).ToInt64();
        if (count < 0 || count > MaxTextLength) throw new NativeNoteSerializationException("正文超过速记可处理范围，请先保留当前窗口。");
        int capacity = checked((int)count + 2); IntPtr buffer = Marshal.AllocHGlobal(checked(capacity * 2));
        try {
            var options = new GetText { cb = (uint)(capacity * 2), flags = 4, codepage = 1200 };
            int written = checked((int)SendText(hwnd, GetTextEx, ref options, buffer).ToInt64());
            if (written < 0 || written >= capacity) throw new NativeNoteSerializationException("正文读取不完整，已停止保存。");
            return Marshal.PtrToStringUni(buffer, written);
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    Snapshot Objects(string raw) {
        Check(); var snapshot = new Snapshot(); IntPtr current = IntPtr.Zero, document = IntPtr.Zero;
        try {
            if (SendOle(hwnd, GetOleInterface, IntPtr.Zero, out current) == IntPtr.Zero || current == IntPtr.Zero) throw new NativeNoteSerializationException("无法读取当前图片引用。");
            Guid documentId = documentIid; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(current, ref documentId, out document));
            var create = (CreateRange)Slot(document, 24, typeof(CreateRange));
            for (int cp = 0; cp < raw.Length; cp++) if (raw[cp] == '\ufffc') {
                if (snapshot.Items.Count >= MaxRetained) throw new NativeNoteSerializationException("当前图片数量超出安全范围，已停止保存。");
                IntPtr range = IntPtr.Zero, embedded = IntPtr.Zero, identity = IntPtr.Zero;
                try {
                    Marshal.ThrowExceptionForHR(create(document, cp, cp + 1, out range));
                    if (range == IntPtr.Zero) throw new NativeNoteSerializationException("无法定位正文中的图片。");
                    var get = (EmbeddedObject)Slot(range, 57, typeof(EmbeddedObject)); int hr = get(range, out embedded);
                    if (hr != 0 || embedded == IntPtr.Zero) throw new NativeNoteSerializationException("存在无法识别的图片，当前内容仍在，已停止保存。");
                    Guid iid = unknownIid; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(embedded, ref iid, out identity));
                    if (identity == IntPtr.Zero) throw new NativeNoteSerializationException("图片缺少可恢复的引用，已停止保存。");
                    snapshot.Items.Add(new ObjectInfo { Cp = cp, Identity = identity }); identity = IntPtr.Zero;
                } finally { if (identity != IntPtr.Zero) Marshal.Release(identity); if (embedded != IntPtr.Zero) Marshal.Release(embedded); if (range != IntPtr.Zero) Marshal.Release(range); }
            }
            return snapshot;
        } catch { snapshot.Dispose(); throw; }
        finally { if (document != IntPtr.Zero) Marshal.Release(document); if (current != IntPtr.Zero) Marshal.Release(current); }
    }
    internal int[] ObjectPositions() {
        string raw = RawText(); using (Snapshot snapshot = Objects(raw)) {
            var result = new int[snapshot.Items.Count]; for (int i = 0; i < result.Length; i++) result[i] = snapshot.Items[i].Cp; return result;
        }
    }
    void Retain(ObjectInfo item, string markdown, int pixels) {
        Reference existing;
        if (retained.TryGetValue(item.Identity, out existing)) {
            if (existing.Markdown != markdown) throw new NativeNoteSerializationException("图片引用冲突，已停止保存。"); return;
        }
        if (retained.Count >= MaxRetained || (long)retainedPixels + pixels > MaxRetainedPreviewPixels) throw new NativeNoteSerializationException("图片撤销缓存已满，请先保存并重新打开速记。");
        Marshal.AddRef(item.Identity); retained.Add(item.Identity, new Reference { Markdown = markdown, Pixels = pixels }); retainedPixels += pixels;
    }
    internal string Markdown() { string raw = RawText(); return Serialize(raw, 0, raw.Length); }
    internal string MarkdownSelection(int start, int end) {
        string raw = RawText(); if (start < 0 || end < start || end > raw.Length) throw new ArgumentOutOfRangeException("start"); return Serialize(raw, start, end);
    }
    string Serialize(string raw, int start, int end) {
        if (!loadedTextValid) throw new NativeNoteSerializationException("这份正文尚未安全载入，原文件未改动，请先处理载入提示。");
        using (Snapshot snapshot = Objects(raw)) {
            var tokens = new Dictionary<int, string>();
            foreach (ObjectInfo item in snapshot.Items) {
                Reference reference;
                if (!retained.TryGetValue(item.Identity, out reference)) throw new NativeNoteSerializationException("存在未识别的图片，当前内容仍在，已停止保存。可撤销最近操作后重试。");
                if (item.Cp < 0 || item.Cp >= raw.Length || raw[item.Cp] != '\ufffc' || tokens.ContainsKey(item.Cp)) throw new NativeNoteSerializationException("图片与正文位置不一致，已停止保存。");
                tokens.Add(item.Cp, reference.Markdown);
            }
            var result = new StringBuilder();
            for (int cp = start; cp < end; cp++) {
                string token;
                if (tokens.TryGetValue(cp, out token)) result.Append(token);
                else if (raw[cp] == '\r') result.Append("\r\n"); else if (raw[cp] == '\v') result.Append('\n'); else result.Append(raw[cp]);
            }
            return result.ToString();
        }
    }

    internal static NativeNotePrepared PrepareLoad(string markdown, Func<string, byte[]> resolver) { return PrepareWithFallback(markdown, resolver, true); }
    internal static NativeNotePrepared PrepareSelection(string markdown, Func<string, byte[]> resolver) { return PrepareWithFallback(markdown, resolver, false); }
    static NativeNotePrepared PrepareWithFallback(string markdown, Func<string, byte[]> resolver, bool fullDocument) {
        try { return Prepare(markdown, resolver, fullDocument, true); }
        catch (NativeNotePreviewBudgetException) { return Prepare(markdown, null, fullDocument, false, true); }
    }
    internal void Load(string markdown) { ApplyPrepared(PrepareLoad(markdown, resolvePreview)); }
    internal void ApplyPrepared(NativeNotePrepared prepared) {
        Check(); if (prepared == null || !prepared.FullDocument) throw new ArgumentException("Full document preparation required", "prepared");
        // Preparation performs every known compatibility/budget check first.
        // Do not Clear the visible source before that validation has succeeded.
        ReleaseAnchors(); loadedTextValid = false;
        box.Rtf = prepared.Rtf; box.ClearUndo(); ReleaseReferences();
        using (Snapshot snapshot = Objects(RawText())) {
            if (snapshot.Items.Count != prepared.References.Length) throw new NativeNoteSerializationException("图片未完整载入，原文件未改动，请保留当前窗口。");
            for (int i = 0; i < snapshot.Items.Count; i++) Retain(snapshot.Items[i], prepared.References[i], prepared.PreviewPixels[i]);
        }
        loadedTextValid = true; textImageFallback = prepared.TextImageFallback;
        if (Markdown() != prepared.Source) { loadedTextValid = false; throw new NativeNoteSerializationException("正文载入后未能原样还原，原文件未改动，已停止保存。"); }
    }
    internal void ReplaceSelection(string markdown) { ReplaceSelectionPrepared(PrepareSelection(markdown, resolvePreview)); }
    internal void InsertText(string text) { ReplaceSelectionPrepared(Prepare(text, null, false, false)); }
    internal void ReplaceSelectionPrepared(NativeNotePrepared prepared) {
        Check(); if (prepared == null || prepared.FullDocument) throw new ArgumentException("Selection preparation required", "prepared");
        if (!loadedTextValid) throw new NativeNoteSerializationException("正文尚未安全载入，暂时无法插入内容。");
        if (prepared.References.Length > 0 || prepared.TextImageFallback) {
            using (Snapshot before = Objects(RawText())) foreach (ObjectInfo item in before.Items) if (!retained.ContainsKey(item.Identity)) throw new NativeNoteSerializationException("已有未识别图片，请先撤销该操作再插图。");
        }
        if (retained.Count + prepared.References.Length > MaxRetained || (long)retainedPixels + prepared.TotalPreviewPixels > MaxRetainedPreviewPixels)
            prepared = Prepare(prepared.Source, null, false, false, true);
        if ((long)box.TextLength - box.SelectionLength + prepared.NativeLength > MaxTextLength) throw new NativeNoteSerializationException("正文已达速记长度上限，当前内容仍在。");
        int cp = box.SelectionStart;
        Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero); box.SelectedRtf = prepared.Rtf; Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero);
        box.Select(cp + prepared.NativeLength, 0);
        if (prepared.TextImageFallback) textImageFallback = true;
        // Enter/Shift+Enter and plain paste never scan the complete document.
        if (prepared.References.Length == 0) return;
        using (Snapshot snapshot = Objects(RawText())) {
            int index = 0;
            foreach (ObjectInfo item in snapshot.Items) if (!retained.ContainsKey(item.Identity)) {
                if (item.Cp < cp || item.Cp >= cp + prepared.NativeLength || index >= prepared.References.Length) throw new NativeNoteSerializationException("新图片位置不一致，内容仍在，已停止保存。");
                Retain(item, prepared.References[index], prepared.PreviewPixels[index]); index++;
            }
            if (index != prepared.References.Length) throw new NativeNoteSerializationException("新图片未完整插入，内容仍在，已停止保存。");
        }
    }
    static NativeNotePrepared Prepare(string text, Func<string, byte[]> resolver, bool fullDocument, bool recognizeImages, bool textImageFallback = false) {
        ValidateText(text);
        var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\uc1\deff0{\fonttbl{\f0\fnil Microsoft YaHei UI;}}\viewkind4\pard\f0\fs21 ");
        var references = new List<string>(); var pixels = new List<int>(); int nativeLength = 0, totalPixels = 0, offset = 0;
        if (recognizeImages) foreach (Match match in images.Matches(text)) {
            nativeLength += AppendText(rtf, text.Substring(offset, match.Index - offset));
            string target = match.Groups["wiki"].Success ? match.Groups["wiki"].Value.Split('|')[0] : match.Groups["target"].Value;
            target = Regex.Replace(target, @"\s+[""'][^""']*[""']\s*$", "").Trim();
            byte[] png = resolver == null ? null : resolver(target);
            if (png == null) nativeLength += AppendText(rtf, match.Value);
            else {
                int count = AppendPng(rtf, png); totalPixels = checked(totalPixels + count);
                if (references.Count >= MaxRetained || totalPixels > MaxRetainedPreviewPixels) throw new NativeNotePreviewBudgetException("图片预览超出安全容量，将保留 Markdown 图片引用。");
                references.Add(match.Value); pixels.Add(count); nativeLength++;
            }
            offset = match.Index + match.Length;
        }
        nativeLength += AppendText(rtf, text.Substring(offset));
        if (fullDocument) rtf.Append(@"\par ");
        rtf.Append('}'); return new NativeNotePrepared(text, rtf.ToString(), references, pixels, fullDocument, nativeLength, totalPixels, textImageFallback);
    }
    internal static void ValidateText(string text) {
        if (text == null) throw new ArgumentNullException("text");
        if (text.Length > MaxTextLength) throw new NativeNoteSerializationException("正文超过速记长度上限，原文件未改动。");
        for (int i = 0; i < text.Length; i++) {
            char value = text[i];
            if (value == '\0' || value == '\v' || value == '\ufffc' || (value == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))) throw new NativeNoteUnsupportedTextException("正文含原生编辑器暂不支持的特殊控制字符，原文已保留；请用 Obsidian 查看原文。");
        }
    }
    static int AppendText(StringBuilder output, string text) {
        int length = 0;
        for (int i = 0; i < text.Length; i++) {
            char ch = text[i]; length++;
            if (ch == '\r') { output.Append(@"\par "); i++; }
            else if (ch == '\n') output.Append(@"\line "); else if (ch == '\t') output.Append(@"\tab ");
            else if (ch == '\\' || ch == '{' || ch == '}') output.Append('\\').Append(ch);
            else if (ch >= 32 && ch < 127) output.Append(ch); else output.Append(@"\u").Append(unchecked((short)ch)).Append('?');
        }
        return length;
    }
    static int AppendPng(StringBuilder output, byte[] source) {
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (source.Length < 32 || source.Length > 12 * 1024 * 1024) throw new NativeNoteSerializationException("图片预览格式或大小无效。");
        for (int i = 0; i < signature.Length; i++) if (source[i] != signature[i]) throw new NativeNoteSerializationException("只接受已处理的本地 PNG 图片预览。");
        int width, height; byte[] png;
        using (var input = new MemoryStream(source)) using (var image = Image.FromStream(input, false, true)) {
            width = image.Width; height = image.Height;
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > MaxPreviewPixels) throw new NativeNoteSerializationException("请先在后台生成小尺寸图片预览。");
            // Canonicalize only the preview. The original is already retained by
            // QuickNoteStore, and is never resized or rewritten by this adapter.
            var bitmap = image as Bitmap;
            if (bitmap == null) throw new NativeNoteSerializationException("无法读取 PNG 图片预览。");
            using (Bitmap copy = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb)) using (var encoded = new MemoryStream()) { copy.Save(encoded, ImageFormat.Png); png = encoded.ToArray(); }
        }
        // Fixed logical dimensions fit the smallest note window and remain
        // stable during resize/DPI layout; no whole-document RTF reconstruction.
        double scale = Math.Min(1, Math.Min(240.0 / width, 260.0 / height));
        int widthTwips = Math.Max(1, (int)Math.Round(width * scale * 15)), heightTwips = Math.Max(1, (int)Math.Round(height * scale * 15));
        output.Append(@"{\pict\pngblip\picw").Append(width).Append(@"\pich").Append(height).Append(@"\picwgoal").Append(widthTwips).Append(@"\pichgoal").Append(heightTwips).Append(' ');
        foreach (byte value in png) output.Append(value.ToString("x2")); output.Append('}'); return checked(width * height);
    }
}
