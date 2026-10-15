using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using W = System.Windows;
using WC = System.Windows.Controls;
using WD = System.Windows.Documents;
using WM = System.Windows.Media;

// The editor only renders local image references. Markdown remains plain text;
// neither XAML nor a browser is used to load a note or clipboard contents.
internal static class QuickNoteMarkdown {
    static readonly Regex images = new Regex(@"!\[[^\]\r\n]*\]\(\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^)\r\n]+?))\s*\)|!\[\[(?<wiki>[^\]\r\n]+)\]\]", RegexOptions.Compiled);

    internal static WD.FlowDocument Create(string text, QuickNoteStore store) {
        var document = new WD.FlowDocument { PagePadding = new W.Thickness(0), FontFamily = new WM.FontFamily("Microsoft YaHei UI"), FontSize = 14, LineHeight = 23 };
        var paragraph = new WD.Paragraph { Margin = new W.Thickness(0) };
        document.Blocks.Add(paragraph);
        int offset = 0;
        foreach (Match match in images.Matches(text)) {
            if (match.Index > offset) paragraph.Inlines.Add(new WD.Run(text.Substring(offset, match.Index - offset)));
            string target = match.Groups["wiki"].Success ? match.Groups["wiki"].Value.Split('|')[0] : match.Groups["target"].Value;
            target = Regex.Replace(target, @"\s+[""'][^""']*[""']\s*$", "").Trim();
            WC.Image image = LoadImage(store.ResolveImage(target));
            if (image == null) paragraph.Inlines.Add(new WD.Run(match.Value));
            else paragraph.Inlines.Add(ImageInline(image, match.Value));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) paragraph.Inlines.Add(new WD.Run(text.Substring(offset)));
        if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new WD.Run(""));
        return document;
    }

    internal static WD.InlineUIContainer ImageInline(WC.Image image, string markdown) {
        image.Margin = new W.Thickness(0, 5, 0, 5);
        image.ToolTip = "图片已保存在速记文件夹";
        return new WD.InlineUIContainer(image) { Tag = markdown, BaselineAlignment = W.BaselineAlignment.Center };
    }

    internal static WC.Image LoadImage(string path) {
        BitmapSource bitmap = LoadBitmap(path);
        return bitmap == null ? null : new WC.Image { Source = bitmap, MaxWidth = 280, MaxHeight = 260, Stretch = WM.Stretch.Uniform };
    }

    internal static BitmapSource LoadBitmap(string path) {
        if (string.IsNullOrEmpty(path)) return null;
        try {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 12L * 1024 * 1024) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0 || (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 36000000) return null;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Min(720, decoder.Frames[0].PixelWidth);
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            }
        } catch (Exception) {
            return null;
        }
    }

    internal static string Text(WD.FlowDocument document, string newline) {
        var text = new StringBuilder();
        bool first = true;
        foreach (WD.Block block in document.Blocks) {
            if (!first) text.Append(newline);
            AppendBlock(text, block, newline); first = false;
        }
        return text.ToString();
    }

    static void AppendBlock(StringBuilder text, WD.Block block, string newline) {
        var paragraph = block as WD.Paragraph;
        if (paragraph != null) { foreach (WD.Inline inline in paragraph.Inlines) AppendInline(text, inline, newline); return; }
        var section = block as WD.Section;
        if (section != null) { bool first = true; foreach (WD.Block child in section.Blocks) { if (!first) text.Append(newline); AppendBlock(text, child, newline); first = false; } }
    }

    static void AppendInline(StringBuilder text, WD.Inline inline, string newline) {
        var run = inline as WD.Run;
        if (run != null) { text.Append(run.Text); return; }
        if (inline is WD.LineBreak) { text.Append(newline); return; }
        var image = inline as WD.InlineUIContainer;
        if (image != null) { text.Append(image.Tag as string ?? ""); return; }
        var span = inline as WD.Span;
        if (span != null) foreach (WD.Inline child in span.Inlines) AppendInline(text, child, newline);
    }
}

internal sealed class QuickNoteInputBox : WC.RichTextBox {
    int inputDepth;

    static QuickNoteInputBox() {
        // Derived class handlers run before RichTextBox's typing handler. WPF
        // queues rich-text input at Background, which can wait behind the host's
        // native input. Change its scheduling, not its text/IME/undo handling.
        W.EventManager.RegisterClassHandler(typeof(QuickNoteInputBox), TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(BeginInput), true);
    }

    internal QuickNoteInputBox() {
        AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(EndInput), true);
        Unloaded += delegate { DetachInput(); };
    }

    static void BeginInput(object sender, TextCompositionEventArgs e) {
        var editor = (QuickNoteInputBox)sender;
        if (editor.inputDepth++ == 0) editor.Dispatcher.Hooks.OperationPosted += editor.InputPosted;
    }

    void EndInput(object sender, TextCompositionEventArgs e) {
        if (inputDepth > 0 && --inputDepth == 0) Dispatcher.Hooks.OperationPosted -= InputPosted;
    }

    void InputPosted(object sender, DispatcherHookEventArgs e) {
        if (Dispatcher.CheckAccess() && !IsReadOnly && e.Operation.Status == DispatcherOperationStatus.Pending &&
            e.Operation.Priority == DispatcherPriority.Background)
            e.Operation.Priority = DispatcherPriority.Normal;
    }

    internal void DetachInput() {
        Dispatcher.Hooks.OperationPosted -= InputPosted; inputDepth = 0;
    }
}

internal sealed class QuickNoteHeaderButton : Button {
    public bool IsPin;
    public bool Pinned;
    bool hover;
    public QuickNoteHeaderButton() {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.FromArgb(250, 250, 248); ForeColor = Color.FromArgb(55, 96, 138);
        TabStop = true; Size = new Size(32, 30);
        MouseEnter += delegate { hover = true; Invalidate(); };
        MouseLeave += delegate { hover = false; Invalidate(); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(hover ? Color.FromArgb(231, 239, 248) : BackColor);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f, cx = Width / 2f, cy = Height / 2f;
        using (var pen = new Pen(IsPin && Pinned ? Color.FromArgb(40, 104, 168) : ForeColor, 1.6f * scale)) {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            if (IsPin) {
                e.Graphics.DrawLine(pen, cx - 4 * scale, cy - 6 * scale, cx + 4 * scale, cy - 6 * scale);
                e.Graphics.DrawLine(pen, cx - 3 * scale, cy - 6 * scale, cx - 3 * scale, cy - 1 * scale);
                e.Graphics.DrawLine(pen, cx + 3 * scale, cy - 6 * scale, cx + 3 * scale, cy - 1 * scale);
                e.Graphics.DrawLine(pen, cx - 3 * scale, cy - 1 * scale, cx - 5 * scale, cy + 2 * scale);
                e.Graphics.DrawLine(pen, cx + 3 * scale, cy - 1 * scale, cx + 5 * scale, cy + 2 * scale);
                e.Graphics.DrawLine(pen, cx - 5 * scale, cy + 2 * scale, cx + 5 * scale, cy + 2 * scale);
                e.Graphics.DrawLine(pen, cx, cy + 2 * scale, cx, cy + 7 * scale);
                if (!Pinned) e.Graphics.DrawLine(pen, cx - 7 * scale, cy + 7 * scale, cx + 7 * scale, cy - 7 * scale);
            } else e.Graphics.DrawLine(pen, cx - 5 * scale, cy + 2 * scale, cx + 5 * scale, cy + 2 * scale);
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class QuickNoteWindow : Form {
    readonly QuickNoteStore store;
    readonly string stateRoot;
    readonly ElementHost host;
    readonly QuickNoteInputBox editor;
    readonly Label dateLabel;
    readonly LinkLabel status;
    readonly QuickNoteHeaderButton pin;
    readonly Timer saveTimer = new Timer { Interval = 500 };
    readonly Timer dayTimer = new Timer { Interval = 30000 };
    readonly ToolTip tips = new ToolTip();
    QuickNoteDocument note;
    bool loading, dirty, composing, closeAllowed;
    string newline = Environment.NewLine;
    string failedText;
    string archivedRecovery;
    string archivedRecoverySource, archivedRecoveryText;
    // One storage operation at a time owns the mutable document. The UI only
    // reads the cached metadata until its completion is posted back.
    bool workerBusy, loadRequested, reloadRequested, hideRequested, closeRequested;
    long editVersion, savedVersion, pendingVersion;
    string pendingText, reloadText, notePath, recoveryPath;
    DateTime noteDate;
    Action closeContinuation;
    int imageEpoch;
    long queuedImagePixels;
    int queuedImageCount;
    PendingImage lastImageRequest;
    readonly Action beforeStorageWork;
    readonly Queue<PendingImage> imageQueue = new Queue<PendingImage>();
    sealed class PendingImage {
        internal BitmapSource Source;
        internal string File, SelectedText;
        internal WD.FlowDocument Document;
        internal WD.TextPointer Start, End;
        internal long Version;
        internal int Epoch;
        internal long Pixels;
        internal PendingImage Previous;
        internal WD.TextPointer InsertEnd;
    }
    Font uiFont;
    bool draggingWindow;
    Point dragOrigin, dragPointer;

    public QuickNoteWindow(QuickNoteStore store, string stateRoot) : this(store, stateRoot, null) { }

    internal QuickNoteWindow(QuickNoteStore store, string stateRoot, Action beforeStorageWork) {
        if (store == null) throw new ArgumentNullException("store");
        this.store = store; this.stateRoot = Path.GetFullPath(stateRoot); this.beforeStorageWork = beforeStorageWork;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        uiFont = new Font("Microsoft YaHei UI", 9); Font = uiFont;
        Text = "听记 · 速记"; ShowInTaskbar = false; TopMost = true;
        MaximizeBox = MinimizeBox = false; FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ClientSize = new Size(360, 420); MinimumSize = new Size(280, 240);
        StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(250, 250, 248);
        var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(14, 5, 8, 5), BackColor = BackColor };
        dateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(36, 59, 83) };
        pin = new QuickNoteHeaderButton { Dock = DockStyle.Right, IsPin = true, Pinned = true, AccessibleName = "取消置顶" };
        var hide = new QuickNoteHeaderButton { Dock = DockStyle.Right, AccessibleName = "保存并收起" };
        tips.SetToolTip(pin, "取消置顶"); tips.SetToolTip(hide, "保存并收起 · Esc");
        top.Controls.Add(dateLabel); top.Controls.Add(pin); top.Controls.Add(hide);
        status = new LinkLabel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(14, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, LinkBehavior = LinkBehavior.HoverUnderline, ForeColor = Color.FromArgb(88, 107, 128), LinkColor = Color.FromArgb(55, 96, 138), ActiveLinkColor = Color.FromArgb(40, 104, 168) };
        editor = new QuickNoteInputBox { BorderThickness = new W.Thickness(0), Background = new WM.SolidColorBrush(WM.Color.FromRgb(250, 250, 248)), Foreground = new WM.SolidColorBrush(WM.Color.FromRgb(36, 49, 68)), Padding = new W.Thickness(14, 8, 14, 8), VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled, AcceptsTab = true, AllowDrop = true, UndoLimit = 100 };
        System.Windows.Automation.AutomationProperties.SetName(editor, "速记正文");
        System.Windows.Automation.AutomationProperties.SetAutomationId(editor, "quick-note-editor");
        dateLabel.AccessibleName = "速记日期"; status.AccessibleName = "速记保存位置与状态";
        WC.SpellCheck.SetIsEnabled(editor, false);
        host = new ElementHost { Dock = DockStyle.Fill, Child = editor, BackColor = BackColor };
        Controls.Add(host); Controls.Add(status); Controls.Add(top);
        pin.Click += delegate { TopMost = !TopMost; pin.Pinned = TopMost; pin.AccessibleName = TopMost ? "取消置顶" : "置顶"; tips.SetToolTip(pin, pin.AccessibleName); pin.Invalidate(); SaveWindowState(); };
        hide.Click += delegate { TryHide(); };
        System.Windows.Forms.MouseEventHandler drag = delegate(object sender, System.Windows.Forms.MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginWindowDrag(((Control)sender).PointToScreen(e.Location)); };
        top.MouseDown += drag; dateLabel.MouseDown += drag;
        editor.TextChanged += delegate(object sender, WC.TextChangedEventArgs e) { if (!loading) {
            if (e.UndoAction == WC.UndoAction.Undo || e.UndoAction == WC.UndoAction.Redo) { imageEpoch++; while (imageQueue.Count > 0) ReleaseImage(imageQueue.Dequeue()); }
            editVersion++; if (!dirty) SetStatus("正在输入…", false); dirty = true; failedText = null; ScheduleSave();
        } };
        TextCompositionManager.AddPreviewTextInputStartHandler(editor, delegate { composing = true; saveTimer.Stop(); });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(editor, delegate { composing = true; saveTimer.Stop(); });
        TextCompositionManager.AddPreviewTextInputHandler(editor, delegate { composing = false; if (dirty) ScheduleSave(); });
        editor.PreviewKeyDown += EditorKeyDown;
        W.DataObject.AddPastingHandler(editor, Paste);
        editor.PreviewDragOver += EditorDragOver;
        editor.PreviewDrop += Drop;
        editor.SizeChanged += delegate { ResizeImages(); };
        foreach (RoutedCommand command in new RoutedCommand[] { WD.EditingCommands.ToggleBullets, WD.EditingCommands.ToggleNumbering })
            editor.CommandBindings.Add(new CommandBinding(command, delegate(object sender, ExecutedRoutedEventArgs e) { e.Handled = true; }));
        saveTimer.Tick += delegate { saveTimer.Stop(); if (!composing) Flush(); };
        dayTimer.Tick += delegate { if (Visible && !composing && note != null && noteDate.Date != DateTime.Today) LoadToday(); };
        status.LinkClicked += delegate { ShowSaveMenu(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!closeAllowed) { e.Cancel = true; TryHide(); } };
        ResizeEnd += delegate { SaveWindowState(); ResizeImages(); };
        Shown += delegate { ResizeImages(); editor.Focus(); };
        dayTimer.Start();
    }

    protected override void OnLoad(EventArgs e) {
        // Bounds in the state file are already physical screen pixels. Restore
        // only after WinForms finishes its first automatic DPI/layout scale.
        base.OnLoad(e);
        RestoreWindowState();
    }

    void BeginWindowDrag(Point screenPoint) {
        if (WindowState != FormWindowState.Normal) return;
        dragOrigin = Location; dragPointer = screenPoint; draggingWindow = true; Capture = true;
    }

    protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e) {
        if (draggingWindow) {
            Point pointer = PointToScreen(e.Location);
            Location = new Point(dragOrigin.X + pointer.X - dragPointer.X, dragOrigin.Y + pointer.Y - dragPointer.Y);
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(System.Windows.Forms.MouseEventArgs e) {
        if (draggingWindow && e.Button == MouseButtons.Left) { draggingWindow = false; Capture = false; SaveWindowState(); }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e) {
        if (draggingWindow && !Capture) { draggingWindow = false; SaveWindowState(); }
        base.OnMouseCaptureChanged(e);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0xA1 && m.WParam.ToInt64() == 2) { // WM_NCLBUTTONDOWN / HTCAPTION
            long packed = m.LParam.ToInt64();
            BeginWindowDrag(new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff))));
            m.Result = IntPtr.Zero; return;
        }
        base.WndProc(ref m);
    }

    public void Toggle() {
        if (IsDisposed) return;
        if (Visible) { TryHide(); return; }
        try {
            hideRequested = false;
            if (note == null || noteDate.Date != DateTime.Today || (!dirty && !workerBusy)) LoadToday();
            Show(); Activate(); editor.Focus();
        } catch (Exception error) { if (note == null) editor.IsReadOnly = true; SetStatus("无法载入速记：" + error.Message, true); Show(); Activate(); }
    }

    public bool FlushAndClose() { return FlushAndClose(null); }

    public bool FlushAndClose(Action onReady) {
        if (IsDisposed) return true;
        if (composing) { SetStatus("请先完成输入法中的文字，再退出。", true); return false; }
        closeRequested = true; hideRequested = false;
        if (closeContinuation == null) closeContinuation = onReady;
        if (Flush()) { FinishClose(false); return true; }
        return false;
    }

    void TryHide() {
        if (composing) return;
        hideRequested = true;
        if (Flush()) { hideRequested = false; SaveWindowState(); Hide(); }
    }

    void LoadToday() {
        if (composing || loadRequested || reloadRequested || (editor.IsReadOnly && workerBusy)) return;
        if (dirty) CaptureSave();
        loadRequested = true; editor.IsReadOnly = true; DrainWork();
    }

    void ApplyLoaded(QuickNoteDocument next, string text, bool recoveryConflict, bool fromReload) {
        loading = true;
        try { note = next; CacheNoteMetadata(); newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n"; editor.Document = QuickNoteMarkdown.Create(text, store); editor.IsReadOnly = false; dirty = !fromReload && next.HasRecovery && !recoveryConflict; failedText = null; editVersion++; savedVersion = dirty ? editVersion - 1 : editVersion; }
        finally { loading = false; }
        dateLabel.Text = next.Date.ToString("M月d日 dddd");
        tips.SetToolTip(dateLabel, next.Path);
        if (fromReload) SetStatus("已载入磁盘版本；原输入另存为恢复草稿", false);
        else if (recoveryConflict) SetStatus("另有未合并草稿，点击查看；磁盘内容未改动", true);
        else { SetSavedStatus(); if (dirty) ScheduleSave(); }
        ResizeImages(); editor.CaretPosition = editor.Document.ContentEnd;
    }

    void ScheduleSave() { saveTimer.Stop(); saveTimer.Start(); }

    bool Flush() {
        // A load owns the document transition. A timer must not capture the
        // former editor text and later save it with the replacement revision.
        if (composing) return false;
        if (!dirty) return !workerBusy && imageQueue.Count == 0 && !loadRequested && !reloadRequested;
        if (editor.IsReadOnly) return false;
        if (note == null) { SetStatus("笔记尚未载入，当前内容仍在，请重试打开。", true); return false; }
        CaptureSave(); DrainWork(); return false;
    }

    void CaptureSave() {
        if (!dirty || composing || note == null) return;
        pendingText = QuickNoteMarkdown.Text(editor.Document, newline); pendingVersion = editVersion;
    }

    void CacheNoteMetadata() {
        if (note == null) return;
        notePath = note.Path; noteDate = note.Date;
        recoveryPath = note.HasRecovery ? note.RecoveryPath : null;
    }

    void DrainWork() {
        if (workerBusy || IsDisposed) return;
        if (imageQueue.Count > 0) { StartImageWork(imageQueue.Dequeue()); return; }
        if (pendingText == null && !reloadRequested && !loadRequested) { FinishRequests(); return; }
        bool save = pendingText != null, reload = !save && reloadRequested;
        string text = save ? pendingText : reloadText;
        long version = pendingVersion;
        if (save) pendingText = null;
        else if (reload) { reloadRequested = false; text = QuickNoteMarkdown.Text(editor.Document, newline); }
        else loadRequested = false;
        QuickNoteDocument document = note;
        DateTime date = DateTime.Today;
        string priorArchiveSource = archivedRecoverySource, priorArchiveText = archivedRecoveryText;
        IntPtr dispatcherHandle = Handle;
        workerBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null; bool conflict = false; string archive = null;
            QuickNoteDocument result = document; string loadedText = null;
            try {
                if (beforeStorageWork != null) beforeStorageWork();
                if (save) { store.SaveRecovery(document, text); store.Save(document, text); }
                else {
                    if (reload) archive = store.ArchiveRecovery(document, text);
                    result = store.Load(date); loadedText = result.Text;
                    conflict = result.HasRecovery && !string.Equals(result.RecoveryRevision, result.Revision, StringComparison.Ordinal);
                    if (!reload && result.HasRecovery && !conflict) loadedText = result.RecoveryText;
                    if (!reload && conflict && (priorArchiveSource != result.Path || priorArchiveText != result.RecoveryText)) archive = store.ArchiveRecovery(result, result.RecoveryText);
                }
            } catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                workerBusy = false;
                if (IsDisposed) return;
                note = result; CacheNoteMetadata();
                if (failure != null) {
                    failedText = save ? text : reload ? text : failedText;
                    // Save already retains its recovery on failure. Keep any
                    // newer snapshot queued so its draft is retained as well.
                    hideRequested = closeRequested = false; closeContinuation = null;
                    if (!save) editor.IsReadOnly = note == null;
                    if (save) { loadRequested = false; if (!reloadRequested) editor.IsReadOnly = false; }
                    SetStatus(failure is QuickNoteConflictException ? "外部内容已变化；草稿保留，点击处理" : "保存或载入失败，内容仍在；点击查看恢复方式", true);
                    tips.SetToolTip(status, failure.Message);
                } else if (save) {
                    savedVersion = Math.Max(savedVersion, version); dirty = editVersion != savedVersion;
                    if (!dirty) { failedText = null; SetSavedStatus(); }
                } else {
                    if (archive != null) { archivedRecovery = archive; archivedRecoverySource = reload ? document.Path : result.Path; archivedRecoveryText = reload ? text : result.RecoveryText; }
                    ApplyLoaded(result, loadedText, conflict, reload);
                }
                if ((hideRequested || closeRequested) && dirty && failedText == null) CaptureSave();
                DrainWork();
            }); } catch (InvalidOperationException) { } // Only forced disposal can bypass the close protocol.
        });
    }

    void FinishRequests() {
        if (dirty || composing || workerBusy || imageQueue.Count > 0) return;
        if (closeRequested) { FinishClose(true); return; }
        if (hideRequested) { hideRequested = false; SaveWindowState(); Hide(); }
    }

    void FinishClose(bool continueExit) {
        Action continuation = closeContinuation;
        closeContinuation = null; closeRequested = false;
        SaveWindowState(); closeAllowed = true; Close(); if (!IsDisposed) Dispose();
        // The controller's recursive continuation must see an already disposed
        // window, and must run only for an asynchronous completion.
        if (continueExit && continuation != null) continuation();
    }

    void SetStatus(string message, bool error) {
        status.Text = message; status.LinkColor = error ? Color.FromArgb(155, 73, 33) : Color.FromArgb(55, 96, 138);
        status.AccessibleName = message;
        tips.SetToolTip(status, !error && notePath != null ? notePath : message);
    }

    void SetSavedStatus() {
        string folder = Path.GetFileName(store.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SetStatus(folder + " / " + Path.GetFileName(notePath), false);
    }

    void ShowSaveMenu() {
        var menu = new ContextMenuStrip { Font = Font, RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("打开速记文件夹", null, delegate {
            try {
                if (!Directory.Exists(store.FolderPath)) { SetStatus("输入内容后，会自动创建速记文件夹。", false); return; }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + store.FolderPath.Replace("\"", "") + "\"");
            } catch (Exception error) { SetStatus("无法打开文件夹：" + error.Message, true); }
        });
        menu.Items.Add("复制当前内容", null, delegate { try { Clipboard.SetText(QuickNoteMarkdown.Text(editor.Document, newline)); } catch (Exception error) { tips.SetToolTip(status, error.Message); } });
        string draftPath = archivedRecovery ?? recoveryPath;
        if (!string.IsNullOrEmpty(draftPath))
            menu.Items.Add("打开草稿所在文件夹", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + draftPath.Replace("\"", "") + "\""); } catch (Exception error) { tips.SetToolTip(status, error.Message); } });
        if (note != null && failedText != null)
            menu.Items.Add("保留草稿并载入磁盘版本", null, delegate { ReloadDiskKeepingDraft(); });
        menu.Closed += delegate { menu.Dispose(); };
        menu.Show(status, new Point(0, status.Height));
    }

    void ReloadDiskKeepingDraft() {
        if (composing || note == null) return;
        reloadText = QuickNoteMarkdown.Text(editor.Document, newline);
        pendingText = null; loadRequested = false; reloadRequested = true;
        hideRequested = closeRequested = false; closeContinuation = null;
        editor.IsReadOnly = true; DrainWork();
    }

    void EditorKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == Key.Escape && !composing && e.ImeProcessedKey == Key.None) { e.Handled = true; TryHide(); return; }
        if ((Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && (e.Key == Key.B || e.Key == Key.I || e.Key == Key.U || e.Key == Key.E || e.Key == Key.L || e.Key == Key.R)) e.Handled = true;
        if ((Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 && e.Key == Key.S) { e.Handled = true; Flush(); }
    }

    void Paste(object sender, W.DataObjectPastingEventArgs e) {
        e.CancelCommand();
        try {
            // Use the event's clipboard snapshot. Reopening the global clipboard
            // here would race another application and can fail while it is busy.
            var image = e.DataObject.GetData(W.DataFormats.Bitmap, true) as BitmapSource;
            if (image != null) {
                // Snapshot event-owned pixels before returning; never retain a
                // clipboard COM/DataObject on the storage thread.
                long pixels = (long)image.PixelWidth * image.PixelHeight;
                if (pixels > 36000000 || pixels + queuedImagePixels > 36000000 || queuedImageCount >= 16) throw new IOException("图片处理量较大，请等待当前图片完成后再粘贴（最多3600万像素）。");
                int stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
                byte[] data = new byte[checked(stride * image.PixelHeight)]; image.CopyPixels(data, stride, 0);
                var snapshot = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY, image.Format, image.Palette, data, stride); snapshot.Freeze();
                QueueImage(snapshot, null); return;
            }
            var files = e.DataObject.GetData(W.DataFormats.FileDrop, true) as string[];
            if (files != null) { foreach (string path in files) InsertImageFile(path); return; }
            string text = e.DataObject.GetData(W.DataFormats.UnicodeText, true) as string;
            if (text != null) editor.Selection.Text = text;
        } catch (Exception error) { SetStatus("无法插入内容：" + error.Message, true); }
    }

    void EditorDragOver(object sender, W.DragEventArgs e) { e.Effects = e.Data.GetDataPresent(W.DataFormats.FileDrop) ? W.DragDropEffects.Copy : W.DragDropEffects.None; e.Handled = true; }
    void Drop(object sender, W.DragEventArgs e) {
        e.Handled = true;
        try {
            WD.TextPointer position = editor.GetPositionFromPoint(e.GetPosition(editor), true);
            if (position != null) editor.CaretPosition = position;
            var files = e.Data.GetData(W.DataFormats.FileDrop) as string[]; if (files != null) foreach (string path in files) InsertImageFile(path);
        }
        catch (Exception error) { SetStatus("无法插入图片：" + error.Message, true); }
    }

    static byte[] EncodePng(BitmapSource source) {
        if (source == null || (long)source.PixelWidth * source.PixelHeight > 36000000) throw new IOException("图片过大，请使用3600万像素以内的图片。");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = new MemoryStream()) { encoder.Save(stream); if (stream.Length > 12L * 1024 * 1024) throw new IOException("图片需小于12MB。"); return stream.ToArray(); }
    }

    void InsertImageFile(string path) {
        QueueImage(null, Path.GetFullPath(path));
    }

    void QueueImage(BitmapSource source, string file) {
        if (editor.IsReadOnly) throw new IOException("请等待当前笔记载入完成。");
        long pixels = source == null ? 0 : (long)source.PixelWidth * source.PixelHeight;
        if (queuedImageCount >= 16 || queuedImagePixels + pixels > 36000000) throw new IOException("请等待当前图片处理完成后再插入。");
        queuedImageCount++; queuedImagePixels += pixels;
        var request = new PendingImage { Source = source, File = file, Document = editor.Document, Start = editor.Selection.Start.GetPositionAtOffset(0, WD.LogicalDirection.Backward), End = editor.Selection.End.GetPositionAtOffset(0, WD.LogicalDirection.Backward), SelectedText = editor.Selection.Text, Version = editVersion, Epoch = imageEpoch, Pixels = pixels };
        if (lastImageRequest != null && lastImageRequest.Document == request.Document && lastImageRequest.Version == request.Version && lastImageRequest.Start.CompareTo(request.Start) == 0) request.Previous = lastImageRequest;
        lastImageRequest = request;
        imageQueue.Enqueue(request);
        SetStatus("正在插入图片…", false); DrainWork();
    }

    void ReleaseImage(PendingImage request) { queuedImageCount--; queuedImagePixels -= request.Pixels; request.Source = null; if (queuedImageCount == 0) lastImageRequest = null; }

    void StartImageWork(PendingImage request) {
        workerBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null; string target = null; BitmapSource preview = null;
            try {
                byte[] png;
                if (request.Source != null) png = EncodePng(request.Source);
                else {
                    if (!File.Exists(request.File) || new FileInfo(request.File).Length > 12L * 1024 * 1024) throw new IOException("请选择12MB以内的图片文件。");
                    using (var stream = File.OpenRead(request.File)) {
                        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        if (decoder.Frames.Count == 0) throw new IOException("无法读取此图片。");
                        png = EncodePng(decoder.Frames[0]);
                    }
                }
                target = store.StoreImage(png);
                preview = QuickNoteMarkdown.LoadBitmap(store.ResolveImage(target));
                if (preview == null) throw new IOException("图片已保存，但无法显示。");
            } catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                workerBusy = false;
                ReleaseImage(request);
                if (IsDisposed) return;
                try {
                if (failure != null) {
                    hideRequested = closeRequested = false; closeContinuation = null;
                    SetStatus("无法插入图片：" + failure.Message, true);
                } else if (request.Document == editor.Document && request.Epoch == imageEpoch) {
                    // TextPointers follow edits while encoding is in progress.
                    // Do not move the live caret if the user has continued typing.
                    var liveStart = editor.Selection.Start; var liveEnd = editor.Selection.End;
                    bool unchanged = request.Version == editVersion;
                    bool followsImage = request.Previous != null && request.Previous.InsertEnd != null;
                    WD.TextPointer start = followsImage ? request.Previous.InsertEnd : request.Start;
                    var range = new WD.TextRange(start, followsImage ? start : request.End);
                    editor.BeginChange();
                    try {
                        if (range.Text == request.SelectedText) range.Text = "";
                        var image = new WC.Image { Source = preview, MaxHeight = 260, Stretch = WM.Stretch.Uniform, Margin = new W.Thickness(0, 5, 0, 5) };
                        var container = new WD.InlineUIContainer(image, start.GetInsertionPosition(WD.LogicalDirection.Forward)) { Tag = "![图片](<" + target + ">)", BaselineAlignment = W.BaselineAlignment.Center };
                        var after = new WD.LineBreak(container.ElementEnd);
                        request.InsertEnd = after.ElementEnd.GetPositionAtOffset(0, WD.LogicalDirection.Backward);
                        if (unchanged) editor.CaretPosition = after.ElementEnd.GetInsertionPosition(WD.LogicalDirection.Forward);
                        else editor.Selection.Select(liveStart, liveEnd);
                    } finally { editor.EndChange(); }
                    ResizeImages();
                }
                } catch (Exception error) {
                    hideRequested = closeRequested = false; closeContinuation = null;
                    SetStatus("图片未插入，文字仍在：" + error.Message, true);
                }
                if ((hideRequested || closeRequested || loadRequested) && dirty) CaptureSave();
                DrainWork();
            }); } catch (InvalidOperationException) { }
        });
    }

    void ResizeImages() {
        double width = Math.Max(100, editor.ActualWidth - 32);
        foreach (WD.Block block in editor.Document.Blocks) {
            var paragraph = block as WD.Paragraph; if (paragraph == null) continue;
            foreach (WD.Inline inline in paragraph.Inlines) {
                var container = inline as WD.InlineUIContainer;
                var image = container == null ? null : container.Child as WC.Image;
                if (image != null) image.MaxWidth = width;
            }
        }
    }

    void SaveWindowState() {
        if (!Visible || WindowState != FormWindowState.Normal) return;
        try {
            Directory.CreateDirectory(stateRoot);
            string value = Bounds.X + "," + Bounds.Y + "," + Bounds.Width + "," + Bounds.Height + "," + (TopMost ? "1" : "0");
            File.WriteAllText(Path.Combine(stateRoot, "quick-note-window.txt"), value, new UTF8Encoding(false));
        } catch { }
    }

    void RestoreWindowState() {
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 24, area.Top + 60);
        try {
            string[] parts = File.ReadAllText(Path.Combine(stateRoot, "quick-note-window.txt")).Split(',');
            int x, y, width, height;
            if (parts.Length != 5 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y) || !int.TryParse(parts[2], out width) || !int.TryParse(parts[3], out height)) return;
            var wanted = new Rectangle(x, y, Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
            Rectangle screen = Screen.FromRectangle(wanted).WorkingArea;
            wanted.Width = Math.Min(wanted.Width, screen.Width); wanted.Height = Math.Min(wanted.Height, screen.Height);
            wanted.X = Math.Max(screen.Left, Math.Min(wanted.X, screen.Right - wanted.Width)); wanted.Y = Math.Max(screen.Top, Math.Min(wanted.Y, screen.Bottom - wanted.Height));
            Bounds = wanted; TopMost = parts[4] != "0"; pin.Pinned = TopMost;
        } catch { }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) { editor.DetachInput(); saveTimer.Dispose(); dayTimer.Dispose(); tips.Dispose(); if (uiFont != null) { uiFont.Dispose(); uiFont = null; } }
        base.Dispose(disposing);
    }
}
