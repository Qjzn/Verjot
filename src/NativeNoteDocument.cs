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
    internal readonly string Source, Rtf, NativeText;
    internal readonly string[] References;
    internal readonly int[] PreviewPixels;
    internal readonly double[] ImageRatios;
    internal readonly bool FullDocument, TextImageFallback;
    internal readonly int NativeLength, TotalPreviewPixels;
    internal NativeNotePrepared(string source, string rtf, string nativeText, List<string> references, List<int> pixels, List<double> ratios, bool fullDocument, int nativeLength, int totalPixels, bool textImageFallback) {
        Source = source; Rtf = rtf; NativeText = nativeText; References = references.ToArray(); PreviewPixels = pixels.ToArray();
        FullDocument = fullDocument; NativeLength = nativeLength; TotalPreviewPixels = totalPixels; TextImageFallback = textImageFallback;
        ImageRatios = ratios.ToArray();
    }
}

internal sealed class NativeNoteDocument : IDisposable {
    internal const int MaxTextLength = 4 * 1024 * 1024;
    internal const int MaxImageLogicalSize = 2000;
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
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DocumentAction(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ObjectProperty(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ExpandRange(IntPtr self, int unit, out int count);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct CharacterFormat {
        internal uint Size, Mask, Effects; internal int Height, Offset, Color;
        internal byte Charset, Family;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Face;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendFormat(IntPtr hwnd, int message, IntPtr flags, ref CharacterFormat value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr Send(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendOle(IntPtr hwnd, int message, IntPtr wparam, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendText(IntPtr hwnd, int message, ref GetText options, IntPtr output);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendLength(IntPtr hwnd, int message, ref GetLength options, IntPtr unused);

    sealed class Reference { internal string Markdown; internal int Pixels; internal double Aspect; }
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
    string loadedSource, loadedCanonical;
    int measuredImageCp = -1;
    Size measuredImageTwips;
    Size measuredImageSourceSize;
    internal int RetainedObjectCount { get { Check(); return retained.Count; } }
    internal bool TextImageFallback { get { Check(); return textImageFallback; } }

    internal NativeNoteDocument(RichTextBox control, Func<string, byte[]> safeLocalPngPreview) {
        if (control == null) throw new ArgumentNullException("control");
        box = control; resolvePreview = safeLocalPngPreview; ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        hwnd = box.Handle;
        if (SendOle(hwnd, GetOleInterface, IntPtr.Zero, out ole) == IntPtr.Zero || ole == IntPtr.Zero) throw new NativeNoteSerializationException("原生文本组件尚未就绪。");
        box.HandleDestroyed += HandleDestroyed;
        box.TextChanged += InvalidateImageMeasure;
    }
    static Delegate Slot(IntPtr pointer, int slot, Type type) {
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size), type);
    }
    void Check() {
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Native note requires its owning STA thread");
        if (disposed || invalidated || !box.IsHandleCreated || box.Handle != hwnd) throw new NativeNoteSerializationException("当前编辑器已卸载，请重新打开速记。");
    }
    void HandleDestroyed(object sender, EventArgs e) { invalidated = true; ReleaseAll(); }
    void InvalidateImageMeasure(object sender, EventArgs e) { measuredImageCp = -1; }
    void ReleaseAnchors() { while (anchors.Count != 0) anchors[anchors.Count - 1].Dispose(); generation++; }
    void ReleaseReferences() { foreach (IntPtr identity in retained.Keys) Marshal.Release(identity); retained.Clear(); retainedPixels = 0; }
    void ReleaseAll() {
        ReleaseAnchors(); ReleaseReferences();
        if (ole != IntPtr.Zero) { Marshal.Release(ole); ole = IntPtr.Zero; }
    }
    public void Dispose() {
        if (disposed) return;
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Dispose requires owning STA");
        box.HandleDestroyed -= HandleDestroyed; box.TextChanged -= InvalidateImageMeasure; ReleaseAll(); disposed = true;
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
    // TOM BeginEditCollection/EndEditCollection combine local marker changes
    // into one native Undo without rebuilding the document or its OLE objects.
    internal void EditCollection(Action edit) {
        Check(); if (!loadedTextValid) throw new NativeNoteSerializationException("正文尚未安全载入。");
        IntPtr document = IntPtr.Zero;
        try {
            Guid iid = documentIid; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ole, ref iid, out document));
            var begin = (DocumentAction)Slot(document, 20, typeof(DocumentAction));
            var end = (DocumentAction)Slot(document, 21, typeof(DocumentAction));
            Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(begin(document));
            try { edit(); } finally { Marshal.ThrowExceptionForHR(end(document)); }
            Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero);
        } finally { if (document != IntPtr.Zero) Marshal.Release(document); }
    }
    internal int SelectionStyle {
        get { Check(); var value = new CharacterFormat { Size = (uint)Marshal.SizeOf(typeof(CharacterFormat)) };
            SendFormat(hwnd, 0x43a, new IntPtr(1), ref value); return (int)(value.Mask & value.Effects & 15); }
    }
    internal void ToggleStyle(int bit) {
        Check(); if (!loadedTextValid || (bit != 1 && bit != 2 && bit != 4 && bit != 8)) throw new NativeNoteSerializationException("正文尚未安全载入，无法设置格式。");
        var value = new CharacterFormat { Size = (uint)Marshal.SizeOf(typeof(CharacterFormat)), Mask = (uint)bit, Effects = (uint)((SelectionStyle & bit) == 0 ? bit : 0) };
        EditCollection(delegate { SendFormat(hwnd, 0x444, new IntPtr(1), ref value); });
    }
    sealed class StyleRun { internal int Start, End, Style; }
    List<StyleRun> StyleRuns(int length) {
        var result = new List<StyleRun>(); IntPtr document = IntPtr.Zero;
        try {
            Guid iid = documentIid; Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ole, ref iid, out document));
            var create = (CreateRange)Slot(document, 24, typeof(CreateRange));
            for (int cp = 0; cp < length;) {
                IntPtr range = IntPtr.Zero, font = IntPtr.Zero;
                try {
                    Marshal.ThrowExceptionForHR(create(document, cp, cp + 1, out range));
                    int changed, end; Marshal.ThrowExceptionForHR(((ExpandRange)Slot(range, 25, typeof(ExpandRange)))(range, 13, out changed));
                    Marshal.ThrowExceptionForHR(((RangePosition)Slot(range, 16, typeof(RangePosition)))(range, out end));
                    if (end <= cp) throw new NativeNoteSerializationException("无法读取原生字体区间，正文仍在。");
                    Marshal.ThrowExceptionForHR(((ObjectProperty)Slot(range, 18, typeof(ObjectProperty)))(range, out font));
                    int style = 0; int[] slots = { 20, 30, 58, 52 };
                    for (int index = 0; index < slots.Length; index++) { int value; Marshal.ThrowExceptionForHR(((RangePosition)Slot(font, slots[index], typeof(RangePosition)))(font, out value)); if (value != 0 && value != -9999999) style |= 1 << index; }
                    end = Math.Min(end, length);
                    if (result.Count > 0 && result[result.Count - 1].Style == style) result[result.Count - 1].End = end;
                    else result.Add(new StyleRun { Start = cp, End = end, Style = style });
                    cp = end;
                } finally { if (font != IntPtr.Zero) Marshal.Release(font); if (range != IntPtr.Zero) Marshal.Release(range); }
            }
        } finally { if (document != IntPtr.Zero) Marshal.Release(document); }
        return result;
    }
    static void ChangeStyle(StringBuilder output, int before, int after) {
        int[] bits = { 1, 2, 4, 8 }; string[] tags = { "b", "i", "u", "s" };
        var oldStack = new List<int>(); var nextStack = new List<int>();
        for (int i = 0; i < bits.Length; i++) { if ((before & bits[i]) != 0) oldStack.Add(i); if ((after & bits[i]) != 0) nextStack.Add(i); }
        int common = 0; while (common < oldStack.Count && common < nextStack.Count && oldStack[common] == nextStack[common]) common++;
        for (int i = oldStack.Count - 1; i >= common; i--) output.Append("</").Append(tags[oldStack[i]]).Append('>');
        for (int i = common; i < nextStack.Count; i++) output.Append('<').Append(tags[nextStack[i]]).Append('>');
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
    internal Rectangle SelectedImageBounds() {
        Check(); int cp = box.SelectionStart;
        if (box.SelectionLength != 1) return Rectangle.Empty;
        if (measuredImageCp != cp) {
            // Static PNGs in current RichEdit are not exposed by GetObject.
            // Read only this selection's native dimensions, once per edit or
            // selection change; mouse movement never re-exports image bytes.
            string rtf = box.SelectedRtf;
            Match w = Regex.Match(rtf, @"\\picwgoal(\d+)"), h = Regex.Match(rtf, @"\\pichgoal(\d+)");
            Match sourceW = Regex.Match(rtf, @"\\picw(\d+)"), sourceH = Regex.Match(rtf, @"\\pich(\d+)");
            int widthTwips, heightTwips;
            measuredImageTwips = w.Success && h.Success && Int32.TryParse(w.Groups[1].Value, out widthTwips) && Int32.TryParse(h.Groups[1].Value, out heightTwips)
                ? new Size(widthTwips, heightTwips) : Size.Empty;
            int sourceWidth, sourceHeight;
            measuredImageSourceSize = sourceW.Success && sourceH.Success && Int32.TryParse(sourceW.Groups[1].Value, out sourceWidth) && Int32.TryParse(sourceH.Groups[1].Value, out sourceHeight)
                && sourceWidth > 0 && sourceHeight > 0 ? new Size(sourceWidth, sourceHeight) : Size.Empty;
            measuredImageCp = cp;
        }
        if (measuredImageTwips.IsEmpty || measuredImageSourceSize.IsEmpty) return Rectangle.Empty;
        Point origin = box.GetPositionFromCharIndex(cp);
        int width = Math.Max(1, (int)Math.Round(measuredImageTwips.Width * box.DeviceDpi * box.ZoomFactor / 1440.0));
        int height = Math.Max(1, (int)Math.Round(measuredImageTwips.Height * box.DeviceDpi * box.ZoomFactor / 1440.0));
        return new Rectangle(origin, new Size(width, height));
    }
    internal double SelectedImageAspectRatio {
        get { Reference reference = SelectedReference(); return reference == null ? 0 : reference.Aspect; }
    }
    internal double SelectedImageDisplayRatio {
        get { return SelectedImageBounds().IsEmpty ? 0 : measuredImageTwips.Width / (double)measuredImageTwips.Height; }
    }
    Reference SelectedReference() {
        Check(); if (SelectedImageBounds().IsEmpty) return null;
        using (Snapshot snapshot = Objects(RawText())) foreach (ObjectInfo item in snapshot.Items) if (item.Cp == box.SelectionStart) {
            Reference reference;
            return retained.TryGetValue(item.Identity, out reference) ? reference : null;
        }
        return null;
    }
    internal string SelectedImageTarget() {
        Reference reference = SelectedReference(); if (reference == null) return null;
        Match image = images.Match(reference.Markdown); if (!image.Success) return null;
        string target = image.Groups["wiki"].Success ? image.Groups["wiki"].Value.Split('|')[0] : image.Groups["target"].Value;
        return Regex.Replace(target, @"\s+[""'][^""']*[""']\s*$", "").Trim();
    }
    internal bool ResizeSelectedImage(int logicalWidth) {
        return ResizeSelectedImage(logicalWidth, 0);
    }
    internal bool ResizeSelectedImage(int logicalWidth, int logicalHeight) {
        Check(); if (box.ReadOnly || SelectedImageBounds().IsEmpty) return false;
        if (logicalWidth < 16 || logicalWidth > MaxImageLogicalSize) throw new ArgumentOutOfRangeException("logicalWidth");
        if (logicalHeight < 0 || logicalHeight > MaxImageLogicalSize) throw new ArgumentOutOfRangeException("logicalHeight");
        int cp = box.SelectionStart; string rtf = box.SelectedRtf;
        using (Snapshot before = Objects(RawText())) {
            Reference original = null;
            foreach (ObjectInfo item in before.Items) if (item.Cp == cp) retained.TryGetValue(item.Identity, out original);
            if (original == null) throw new NativeNoteSerializationException("图片引用不可恢复，已停止缩放。");
            if (logicalHeight == 0 && logicalWidth / original.Aspect > MaxImageLogicalSize) throw new NativeNoteSerializationException("图片高度已达到预览上限，请缩小显示或查看原图。");
            if (retained.Count >= MaxRetained || (long)retainedPixels + original.Pixels > MaxRetainedPreviewPixels)
                throw new NativeNoteSerializationException("图片撤销缓存已满，请保存并重开后继续缩放。");
            string markdown = ImageDimensions(original.Markdown, logicalWidth, logicalHeight);
            int twips = logicalWidth * 15, high = logicalHeight > 0 ? logicalHeight * 15 : Math.Max(1, (int)Math.Round(twips / original.Aspect));
            rtf = Regex.Replace(rtf, @"\\picwgoal-?\d+", @"\picwgoal" + twips);
            rtf = Regex.Replace(rtf, @"\\pichgoal-?\d+", @"\pichgoal" + high);
            // Only one selected image is replaced. Native undo retains the old
            // object; the original PNG, surrounding text and clipboard stay put.
            EditCollection(delegate { box.SelectedRtf = rtf; });
            measuredImageCp = -1;
            box.Select(cp, 1);
            using (Snapshot after = Objects(RawText())) foreach (ObjectInfo item in after.Items) if (item.Cp == cp) Retain(item, markdown, original.Pixels, original.Aspect);
        }
        return true;
    }
    static string ImageDimensions(string markdown, int width, int height) {
        string dimensions = width + (height > 0 ? "x" + height : "");
        if (markdown.StartsWith("![[", StringComparison.Ordinal))
            return Regex.Replace(markdown, @"(?:\|\d+(?:x\d+)?)?\]\]$", "|" + dimensions + "]]");
        int close = markdown.IndexOf(']');
        if (close < 2) throw new NativeNoteSerializationException("图片引用格式无效。");
        string alt = Regex.Replace(markdown.Substring(2, close - 2), @"\|\d+(?:x\d+)?$", "");
        return "![" + alt + "|" + dimensions + markdown.Substring(close);
    }
    void Retain(ObjectInfo item, string markdown, int pixels, double aspect) {
        Reference existing;
        if (retained.TryGetValue(item.Identity, out existing)) {
            if (existing.Markdown != markdown) throw new NativeNoteSerializationException("图片引用冲突，已停止保存。"); return;
        }
        if (retained.Count >= MaxRetained || (long)retainedPixels + pixels > MaxRetainedPreviewPixels) throw new NativeNoteSerializationException("图片撤销缓存已满，请先保存并重新打开速记。");
        Marshal.AddRef(item.Identity); retained.Add(item.Identity, new Reference { Markdown = markdown, Pixels = pixels, Aspect = aspect }); retainedPixels += pixels;
    }
    internal string Markdown() { string raw = RawText(); string canonical = Serialize(raw, 0, raw.Length); return canonical == loadedCanonical ? loadedSource : canonical; }
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
            var result = new StringBuilder(); var styles = StyleRuns(raw.Length); int run = 0, active = 0;
            for (int cp = start; cp < end; cp++) {
                while (run + 1 < styles.Count && cp >= styles[run].End) run++;
                int style = styles.Count == 0 ? 0 : styles[run].Style;
                if (raw[cp] == '\r' || raw[cp] == '\v' || raw[cp] == '\n') style = 0;
                if (active != style) { ChangeStyle(result, active, style); active = style; }
                string token;
                if (tokens.TryGetValue(cp, out token)) result.Append(token);
                else if (raw[cp] == '\u2022' && cp + 1 < end && raw[cp + 1] == ' ' && (cp == 0 || raw[cp - 1] == '\r' || raw[cp - 1] == '\v' || raw[cp - 1] == '\n')) result.Append('-');
                else if (raw[cp] == '\r') result.Append("\r\n"); else if (raw[cp] == '\v') result.Append('\n'); else result.Append(raw[cp]);
            }
            ChangeStyle(result, active, 0);
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
            for (int i = 0; i < snapshot.Items.Count; i++) Retain(snapshot.Items[i], prepared.References[i], prepared.PreviewPixels[i], prepared.ImageRatios[i]);
        }
        loadedTextValid = true; textImageFallback = prepared.TextImageFallback;
        string raw = RawText();
        if (raw != prepared.NativeText) { loadedTextValid = false; throw new NativeNoteSerializationException("正文载入后未能原样还原，原文件未改动，已停止保存。"); }
        loadedCanonical = Serialize(raw, 0, raw.Length); loadedSource = prepared.Source;
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
        int style = SelectionStyle;
        string insertion = prepared.Rtf.Replace(@"\viewkind4\pard\f0\fs21 ", @"\viewkind4\f0\fs21 " + StyleControls(style));
        Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero); box.SelectedRtf = insertion; Send(hwnd, StopGroupTyping, IntPtr.Zero, IntPtr.Zero);
        box.Select(cp + prepared.NativeLength, 0);
        if (prepared.TextImageFallback) textImageFallback = true;
        // Enter/Shift+Enter and plain paste never scan the complete document.
        if (prepared.References.Length == 0) return;
        using (Snapshot snapshot = Objects(RawText())) {
            int index = 0;
            foreach (ObjectInfo item in snapshot.Items) if (!retained.ContainsKey(item.Identity)) {
                if (item.Cp < cp || item.Cp >= cp + prepared.NativeLength || index >= prepared.References.Length) throw new NativeNoteSerializationException("新图片位置不一致，内容仍在，已停止保存。");
                Retain(item, prepared.References[index], prepared.PreviewPixels[index], prepared.ImageRatios[index]); index++;
            }
            if (index != prepared.References.Length) throw new NativeNoteSerializationException("新图片未完整插入，内容仍在，已停止保存。");
        }
    }
    static NativeNotePrepared Prepare(string text, Func<string, byte[]> resolver, bool fullDocument, bool recognizeImages, bool textImageFallback = false) {
        ValidateText(text);
        var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\uc1\deff0{\fonttbl{\f0\fnil Microsoft YaHei UI;}}\viewkind4\pard\f0\fs21 ");
        var renderer = new RichImport(text, rtf, resolver, fullDocument, recognizeImages);
        renderer.Append(0, text.Length, 0);
        if (fullDocument) rtf.Append(@"\par ");
        rtf.Append('}'); return new NativeNotePrepared(text, rtf.ToString(), renderer.Native.ToString(), renderer.References, renderer.Pixels, renderer.Ratios, fullDocument, renderer.Native.Length, renderer.TotalPixels, textImageFallback);
    }
    static string StyleControls(int style) { return @"\b" + ((style & 1) != 0 ? "1" : "0") + @"\i" + ((style & 2) != 0 ? "1" : "0") + @"\ul" + ((style & 4) != 0 ? "1" : "0") + @"\strike" + ((style & 8) != 0 ? "1" : "0") + " "; }
    // Only a bounded, local subset is interpreted. No incoming RTF, HTML
    // engine, remote image, script or live whole-document parsing is enabled.
    sealed class RichImport {
        readonly string text; readonly StringBuilder rtf; readonly Func<string, byte[]> resolver; readonly bool rich;
        readonly Dictionary<int, Match> imageAt = new Dictionary<int, Match>();
        internal readonly StringBuilder Native = new StringBuilder();
        internal readonly List<string> References = new List<string>(); internal readonly List<int> Pixels = new List<int>();
        internal readonly List<double> Ratios = new List<double>();
        internal int TotalPixels;
        readonly int[] missingFrom = new int[11], missingUntil = new int[11];
        static readonly string[] opening = { "<strong>", "<em>", "<del>", "<b>", "<i>", "<u>", "<s>", "***", "**", "~~", "*" };
        static readonly string[] closing = { "</strong>", "</em>", "</del>", "</b>", "</i>", "</u>", "</s>", "***", "**", "~~", "*" };
        static readonly string[] controls = { @"\b1 ", @"\i1 ", @"\strike1 ", @"\b1 ", @"\i1 ", @"\ul1 ", @"\strike1 ", @"\b1\i1 ", @"\b1 ", @"\strike1 ", @"\i1 " };
        internal RichImport(string source, StringBuilder output, Func<string, byte[]> imageResolver, bool render, bool recognizeImages) {
            text = source; rtf = output; resolver = recognizeImages ? imageResolver : null; rich = render;
            // Even text-only fallback must keep image paths atomic: style-like
            // characters in a filename must never become formatting syntax.
            foreach (Match match in images.Matches(text)) imageAt.Add(match.Index, match);
        }
        void Literal(string value) { AppendText(rtf, value); Native.Append(value.Replace("\r\n", "\r").Replace('\n', '\v')); }
        internal void Append(int start, int end, int depth) {
            int plain = start;
            for (int cp = start; cp < end;) {
                Match image;
                if (imageAt.TryGetValue(cp, out image) && cp + image.Length <= end) {
                    Literal(text.Substring(plain, cp - plain));
                    string target = image.Groups["wiki"].Success ? image.Groups["wiki"].Value.Split('|')[0] : image.Groups["target"].Value;
                    target = Regex.Replace(target, @"\s+[""'][^""']*[""']\s*$", "").Trim();
                    byte[] png = resolver == null ? null : resolver(target);
                    if (png == null) Literal(image.Value);
                    else {
                        Match dimension = Regex.Match(image.Value, @"\|(\d+)(?:x(\d+))?(?:\]\]|\])");
                        int requestedWidth = 0, requestedHeight = 0;
                        if (dimension.Success) Int32.TryParse(dimension.Groups[1].Value, out requestedWidth);
                        if (dimension.Groups[2].Success) Int32.TryParse(dimension.Groups[2].Value, out requestedHeight);
                        double aspect;
                        int count = AppendPng(rtf, png, requestedWidth, requestedHeight, out aspect); TotalPixels = checked(TotalPixels + count);
                        if (References.Count >= MaxRetained || TotalPixels > MaxRetainedPreviewPixels) throw new NativeNotePreviewBudgetException("图片预览超出安全容量，将保留 Markdown 图片引用。");
                        References.Add(image.Value); Pixels.Add(count); Ratios.Add(aspect); Native.Append('\ufffc');
                    }
                    cp += image.Length; plain = cp; continue;
                }
                if (!rich || depth >= 16) { cp++; continue; }
                if (text[cp] == '\\' && cp + 1 < end) { cp += 2; continue; }
                if (text[cp] == '`') {
                    int count = 1; while (cp + count < end && text[cp + count] == '`') count++;
                    string marker = new string('`', count); int stop = text.IndexOf(marker, cp + count, StringComparison.Ordinal);
                    cp = stop >= 0 && stop + count <= end ? stop + count : cp + count; continue;
                }
                // A formatting tag can precede the first visible character.
                // Source offsets alone mistake <b>- item</b> for plain text.
                bool visibleLineStart = cp == plain && (Native.Length == 0 || Native[Native.Length - 1] == '\r' || Native[Native.Length - 1] == '\v' || Native[Native.Length - 1] == '\n');
                if ((cp == 0 || text[cp - 1] == '\n' || visibleLineStart) && cp + 2 <= end && text[cp] == '-' && text[cp + 1] == ' ') {
                    Literal(text.Substring(plain, cp - plain)); Literal("\u2022 "); cp += 2; plain = cp; continue;
                }
                bool matched = false;
                if (text[cp] == '<' || text[cp] == '*' || text[cp] == '~') for (int index = 0; index < opening.Length; index++) {
                    string left = opening[index], right = closing[index];
                    if (cp + left.Length > end || string.CompareOrdinal(text, cp, left, 0, left.Length) != 0) continue;
                    if (missingUntil[index] >= end && cp >= missingFrom[index]) continue;
                    int close = text.IndexOf(right, cp + left.Length, StringComparison.Ordinal);
                    if (close < 0 || close + right.Length > end) { missingFrom[index] = cp; missingUntil[index] = end; continue; }
                    if (close == cp + left.Length && depth == 0) continue;
                    Literal(text.Substring(plain, cp - plain)); rtf.Append('{').Append(controls[index]); Append(cp + left.Length, close, depth + 1); rtf.Append('}');
                    cp = close + right.Length; plain = cp; matched = true; break;
                }
                if (!matched) cp++;
            }
            Literal(text.Substring(plain, end - plain));
        }
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
    static int AppendPng(StringBuilder output, byte[] source, int requestedWidth, int requestedHeight, out double aspect) {
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (source.Length < 32 || source.Length > 12 * 1024 * 1024) throw new NativeNoteSerializationException("图片预览格式或大小无效。");
        for (int i = 0; i < signature.Length; i++) if (source[i] != signature[i]) throw new NativeNoteSerializationException("只接受已处理的本地 PNG 图片预览。");
        int width, height; byte[] png;
        using (var input = new MemoryStream(source)) using (var image = Image.FromStream(input, false, true)) {
            width = image.Width; height = image.Height;
            aspect = width / (double)height;
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > MaxPreviewPixels) throw new NativeNoteSerializationException("请先在后台生成小尺寸图片预览。");
            if (width > height * 100L || height > width * 100L) throw new NativeNotePreviewBudgetException("图片过于狭长，保留原图引用供查看。");
            // Canonicalize only the preview. The original is already retained by
            // QuickNoteStore, and is never resized or rewritten by this adapter.
            var bitmap = image as Bitmap;
            if (bitmap == null) throw new NativeNoteSerializationException("无法读取 PNG 图片预览。");
            using (Bitmap copy = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb)) using (var encoded = new MemoryStream()) { copy.Save(encoded, ImageFormat.Png); png = encoded.ToArray(); }
        }
        // Fixed logical dimensions fit the smallest note window and remain
        // stable during resize/DPI layout; no whole-document RTF reconstruction.
        double scale = Math.Min(1, Math.Min(240.0 / width, 260.0 / height));
        if (requestedWidth >= 16 && requestedWidth <= 4096) scale = (double)requestedWidth / width;
        scale = Math.Min(scale, Math.Min((double)MaxImageLogicalSize / width, (double)MaxImageLogicalSize / height));
        int widthTwips = Math.Max(1, (int)Math.Round(width * scale * 15)), heightTwips = Math.Max(1, (int)Math.Round(height * scale * 15));
        if (requestedWidth >= 16 && requestedWidth <= 4096 && requestedHeight >= 1 && requestedHeight <= 4096) {
            double fit = Math.Min(1, Math.Min((double)MaxImageLogicalSize / requestedWidth, (double)MaxImageLogicalSize / requestedHeight));
            widthTwips = Math.Max(1, (int)Math.Round(requestedWidth * fit * 15)); heightTwips = Math.Max(1, (int)Math.Round(requestedHeight * fit * 15));
        }
        output.Append(@"{\pict\pngblip\picw").Append(width).Append(@"\pich").Append(height).Append(@"\picwgoal").Append(widthTwips).Append(@"\pichgoal").Append(heightTwips).Append(' ');
        foreach (byte value in png) output.Append(value.ToString("x2")); output.Append('}'); return checked(width * height);
    }
}
