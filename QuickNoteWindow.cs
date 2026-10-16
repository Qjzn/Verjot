using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Import/export commands are controlled; normal text, undo and IME remain in
// Windows RichEdit. No WPF dispatcher or replacement keyboard engine is loaded.
internal sealed class QuickNoteInputBox : NativeNoteInputBox {
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetCompositionStringW(IntPtr context, int index, IntPtr output, int length);
    internal Action UserPaste, UndoOrRedo, UserContextMenu;
    internal Action<bool> UserCopyOrCut, CompositionChanged;
    internal Func<Keys, bool> HandleKey;
    internal Action InsertNewline;
    bool compositionActive;
    internal bool CompositionActive {
        get {
            if (compositionActive) return true;
            if (!IsHandleCreated) return false;
            IntPtr context = ImmGetContext(Handle);
            if (context == IntPtr.Zero) return false;
            try { return ImmGetCompositionStringW(context, 8, IntPtr.Zero, 0) > 0; }
            finally { ImmReleaseContext(Handle, context); }
        }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keyData) {
        Keys key = keyData & Keys.KeyCode;
        Keys modifiers = keyData & Keys.Modifiers;
        if ((modifiers == Keys.Control && key == Keys.V) || (modifiers == Keys.Shift && key == Keys.Insert)) { if (UserPaste != null) UserPaste(); return true; }
        if (modifiers == Keys.Control && (key == Keys.C || key == Keys.X || key == Keys.Insert)) { if (UserCopyOrCut != null) UserCopyOrCut(key == Keys.X); return true; }
        if (modifiers == Keys.Shift && key == Keys.Delete) { if (UserCopyOrCut != null) UserCopyOrCut(true); return true; }
        if ((((keyData & Keys.Control) != 0) && (key == Keys.Z || key == Keys.Y)) || ((keyData & Keys.Alt) != 0 && key == Keys.Back)) { if (UndoOrRedo != null) UndoOrRedo(); }
        if (HandleKey != null && HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref message, keyData);
    }
    protected override void WndProc(ref Message message) {
        int code = message.Msg;
        if (code == 0x302 || code == 0x440) { if (UserPaste != null) UserPaste(); message.Result = IntPtr.Zero; return; }
        if (code == 0x301 || code == 0x300) { if (UserCopyOrCut != null) UserCopyOrCut(code == 0x300); message.Result = IntPtr.Zero; return; }
        if (code == 0x432) { message.Result = IntPtr.Zero; return; } // EM_CANPASTE: disable default paste availability
        if (code == 0x7b) { if (UserContextMenu != null) UserContextMenu(); message.Result = IntPtr.Zero; return; }
        if (code == 0x304 || code == 0xc7 || code == 0x454) { if (UndoOrRedo != null) UndoOrRedo(); }
        if ((code == 0x100 || code == 0x102) && message.WParam.ToInt64() == 13 && !CompositionActive && !ReadOnly && InsertNewline != null && (ModifierKeys & (Keys.Control | Keys.Alt)) == Keys.None) {
            // RichEdit50 edits on Return KeyDown as well as WM_CHAR. Keep the
            // naturally translated character as the only insertion route.
            if (!ProcessKeyMessage(ref message) && code == 0x102) InsertNewline();
            message.Result = IntPtr.Zero; return;
        }
        if (code == 0x102) {
            int character = unchecked((int)message.WParam.ToInt64());
            if (character == 22) { if (UserPaste != null) UserPaste(); message.Result = IntPtr.Zero; return; }
            if (character == 3 || character == 24) { if (UserCopyOrCut != null) UserCopyOrCut(character == 24); message.Result = IntPtr.Zero; return; }
            if (character == 26 && UndoOrRedo != null) UndoOrRedo();
        }
        if (code == 0x10d) { compositionActive = true; if (CompositionChanged != null) CompositionChanged(true); }
        base.WndProc(ref message);
        if (code == 0x10e) { compositionActive = false; if (CompositionChanged != null) CompositionChanged(false); }
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
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    readonly QuickNoteStore store;
    readonly string stateRoot;
    readonly QuickNoteInputBox editor;
    readonly NativeNoteDocument nativeDocument;
    readonly Label dateLabel;
    readonly LinkLabel status;
    readonly QuickNoteHeaderButton pin;
    readonly Timer saveTimer = new Timer { Interval = 100 };
    readonly Timer dayTimer = new Timer { Interval = 30000 };
    readonly ToolTip tips = new ToolTip();
    QuickNoteDocument note;
    internal Exception LastLoadError { get; private set; }
    bool loading, dirty, composing, closeAllowed;
    string newline = Environment.NewLine;
    string failedText, archivedRecovery, archivedRecoverySource, archivedRecoveryText, loadFailureRecoveryPath;
    // Only one worker owns the mutable storage document. The UI reads cached
    // metadata and never waits for durable writes or image preparation.
    bool workerBusy, loadRequested, reloadRequested, hideRequested, closeRequested;
    long editVersion, savedVersion, pendingVersion, lastEditStamp;
    string pendingText, reloadText, notePath, recoveryPath;
    DateTime noteDate;
    Action closeContinuation;
    int imageEpoch, documentGeneration;
    long queuedImagePixels;
    int queuedImageCount;
    PendingImage lastImageRequest, completedImage;
    readonly Action beforeStorageWork;
    readonly Queue<PendingImage> imageQueue = new Queue<PendingImage>();
    readonly List<PendingImage> imageAnchors = new List<PendingImage>();
    Font uiFont, editorFont;
    sealed class PendingImage {
        internal Bitmap Source;
        internal string File, SelectedText;
        internal NativeNoteDocument.LiveRange Range, InsertEnd;
        internal long Version, Pixels;
        internal int Epoch, Generation;
        internal PendingImage Previous;
        internal NativeNotePrepared Prepared;
        internal Exception Failure;
    }
    bool IsComposing { get { return composing || editor.CompositionActive; } }

    public QuickNoteWindow(QuickNoteStore store, string stateRoot) : this(store, stateRoot, null) { }
    internal QuickNoteWindow(QuickNoteStore store, string stateRoot, Action beforeStorageWork) {
        if (store == null) throw new ArgumentNullException("store");
        this.store = store; this.stateRoot = Path.GetFullPath(stateRoot); this.beforeStorageWork = beforeStorageWork;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        uiFont = new Font("Microsoft YaHei UI", 9); editorFont = new Font("Microsoft YaHei UI", 10.5f); Font = uiFont;
        Text = "听记 · 速记"; ShowInTaskbar = false; TopMost = true;
        MaximizeBox = MinimizeBox = false; FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(360, 420); MinimumSize = new Size(280, 240);
        StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(250, 250, 248);
        var top = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(14, 5, 8, 5), BackColor = BackColor };
        dateLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(36, 59, 83), AccessibleName = "速记日期" };
        pin = new QuickNoteHeaderButton { Dock = DockStyle.Right, IsPin = true, Pinned = true, AccessibleName = "取消置顶" };
        var hide = new QuickNoteHeaderButton { Dock = DockStyle.Right, AccessibleName = "保存并收起" };
        tips.SetToolTip(pin, "取消置顶"); tips.SetToolTip(hide, "保存并收起 · Esc");
        top.Controls.Add(dateLabel); top.Controls.Add(pin); top.Controls.Add(hide);
        status = new LinkLabel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(14, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, LinkBehavior = LinkBehavior.HoverUnderline, ForeColor = Color.FromArgb(88, 107, 128), LinkColor = Color.FromArgb(55, 96, 138), ActiveLinkColor = Color.FromArgb(40, 104, 168), AccessibleName = "速记保存位置与状态" };
        editor = new QuickNoteInputBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = Color.FromArgb(36, 49, 68), Font = editorFont, AcceptsTab = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, HideSelection = false, AllowDrop = true, AccessibleName = "速记正文", Name = "quick-note-editor", ReadOnly = true };
        var body = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Padding = new Padding(14, 8, 14, 8) };
        body.Controls.Add(editor); Controls.Add(body); Controls.Add(status); Controls.Add(top);
        nativeDocument = new NativeNoteDocument(editor, null);
        pin.Click += delegate { TopMost = !TopMost; pin.Pinned = TopMost; pin.AccessibleName = TopMost ? "取消置顶" : "置顶"; tips.SetToolTip(pin, pin.AccessibleName); pin.Invalidate(); SaveWindowState(); };
        hide.Click += delegate { TryHide(); };
        // The verified native prototype uses Windows caption dragging. Keep
        // this path and leave the user's DragFullWindows setting untouched.
        MouseEventHandler drag = delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } };
        top.MouseDown += drag; dateLabel.MouseDown += drag;
        editor.TextChanged += delegate {
            if (loading) return;
            editVersion++; if (!dirty) SetStatus("正在输入…", false);
            dirty = true; failedText = null; ScheduleSave();
        };
        editor.CompositionChanged = delegate(bool active) { composing = active; if (active) saveTimer.Stop(); else if (dirty) ScheduleSave(); else if (completedImage != null) saveTimer.Start(); };
        editor.HandleKey = delegate(Keys keys) { var e = new KeyEventArgs(keys); EditorKeyDown(editor, e); return e.Handled; };
        editor.UserPaste = PasteFromClipboard; editor.UserCopyOrCut = CopyOrCut;
        editor.UserContextMenu = ShowEditorMenu; editor.UndoOrRedo = CancelPendingImages;
        editor.InsertNewline = delegate { try { nativeDocument.InsertText(newline); } catch (Exception error) { SetStatus("无法插入换行：" + error.Message, true); } };
        editor.DragEnter += EditorDragOver; editor.DragOver += EditorDragOver; editor.DragDrop += Drop;
        saveTimer.Tick += delegate {
            if (completedImage != null) { if (IsComposing) return; CompleteImageWork(); }
            if (!IsComposing && dirty && (System.Diagnostics.Stopwatch.GetTimestamp() - lastEditStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 500) { saveTimer.Stop(); Flush(); }
            else if (!dirty) saveTimer.Stop();
        };
        dayTimer.Tick += delegate { if (Visible && !IsComposing && note != null && noteDate.Date != DateTime.Today) LoadToday(); };
        status.LinkClicked += delegate { ShowSaveMenu(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!closeAllowed) { e.Cancel = true; TryHide(); } };
        ResizeEnd += delegate { SaveWindowState(); };
        Shown += delegate { editor.Focus(); };
        dayTimer.Start();
    }
    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        // Persisted bounds are physical pixels, after initial WinForms scaling.
        RestoreWindowState();
    }
    public void Toggle() {
        if (IsDisposed) return;
        if (Visible) { TryHide(); return; }
        try {
            hideRequested = false;
            if (note == null || noteDate.Date != DateTime.Today || (!dirty && !workerBusy)) LoadToday();
            Show(); Activate(); editor.Focus();
        } catch (Exception error) { if (note == null) editor.ReadOnly = true; SetStatus("无法载入速记：" + error.Message, true); Show(); Activate(); }
    }
    public bool FlushAndClose() { return FlushAndClose(null); }
    public bool FlushAndClose(Action onReady) {
        if (IsDisposed) return true;
        if (IsComposing) { SetStatus("请先完成输入法中的文字，再退出。", true); return false; }
        closeRequested = true; hideRequested = false;
        if (closeContinuation == null) closeContinuation = onReady;
        if (Flush()) { FinishClose(false); return true; }
        return false;
    }
    void TryHide() {
        if (IsComposing) return;
        hideRequested = true;
        if (Flush()) { hideRequested = false; SaveWindowState(); Hide(); }
    }
    void LoadToday() {
        if (IsComposing || loadRequested || reloadRequested || (editor.ReadOnly && workerBusy)) return;
        if (dirty && !CaptureSave()) return;
        loadRequested = true; editor.ReadOnly = true; DrainWork();
    }
    void ApplyLoaded(QuickNoteDocument next, string text, NativeNotePrepared prepared, bool recoveryConflict, bool fromReload) {
        loading = true;
        try {
            // Never bind visible old text to a new revision after failed import.
            ClearImageAnchors();
            // RichEdit's OLE callback rejects image import while read-only.
            // Only this synchronous UI import is writable; queued loading
            // remains read-only and no user message loop is pumped here.
            editor.ReadOnly = false;
            nativeDocument.ApplyPrepared(prepared);
            LastLoadError = null;
            loadFailureRecoveryPath = null;
            note = next; CacheNoteMetadata(); documentGeneration++; imageEpoch++;
            newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            editor.ReadOnly = false; dirty = !fromReload && next.HasRecovery && !recoveryConflict;
            failedText = null; editVersion++; savedVersion = dirty ? editVersion - 1 : editVersion;
            editor.Select(editor.TextLength, 0);
        } catch { editor.ReadOnly = true; throw; }
        finally { loading = false; }
        dateLabel.Text = next.Date.ToString("M月d日 dddd"); tips.SetToolTip(dateLabel, next.Path);
        if (fromReload) SetStatus("已载入磁盘版本；原输入另存为恢复草稿", false);
        else if (recoveryConflict) SetStatus("另有未合并草稿，点击查看；磁盘内容未改动", true);
        else { SetSavedStatus(); if (dirty) ScheduleSave(); }
    }
    void ScheduleSave() {
        lastEditStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!saveTimer.Enabled) saveTimer.Start();
    }
    bool Flush() {
        if (IsComposing) return false;
        if (!dirty) return !workerBusy && imageQueue.Count == 0 && !loadRequested && !reloadRequested;
        // A timer must not capture former text during a document transition.
        if (editor.ReadOnly) return false;
        if (note == null) { SetStatus("笔记尚未载入，当前内容仍在，请重试打开。", true); return false; }
        if (!CaptureSave()) return false;
        DrainWork(); return false;
    }
    internal string ReadMarkdown() {
        try { return nativeDocument.Markdown(); }
        catch (NativeNoteSerializationException error) {
            if (LastLoadError == null) throw;
            throw new NativeNoteSerializationException(error.Message + " 载入原因：" + LastLoadError.GetType().Name + "：" + LastLoadError.Message);
        }
    }
    bool CaptureSave() {
        if (!dirty || IsComposing || note == null) return !dirty;
        try { pendingText = ReadMarkdown(); pendingVersion = editVersion; return true; }
        catch (Exception error) {
            pendingText = null; hideRequested = closeRequested = false; closeContinuation = null;
            SetStatus("正文暂时无法安全保存，请保留窗口并撤销最近操作：" + error.Message, true);
            return false;
        }
    }
    void CacheNoteMetadata() { if (note != null) { notePath = note.Path; noteDate = note.Date; recoveryPath = note.HasRecovery ? note.RecoveryPath : null; } }
    void DrainWork() {
        if (workerBusy || IsDisposed) return;
        if (imageQueue.Count > 0) { StartImageWork(imageQueue.Dequeue()); return; }
        if (pendingText == null && !reloadRequested && !loadRequested) { FinishRequests(); return; }
        bool save = pendingText != null, reload = !save && reloadRequested;
        string text = save ? pendingText : reloadText; long version = pendingVersion;
        if (save) pendingText = null;
        else if (reload) {
            try { text = ReadMarkdown(); }
            catch (Exception error) { reloadRequested = false; editor.ReadOnly = false; SetStatus("无法保留当前草稿：" + error.Message, true); return; }
            reloadRequested = false;
        } else loadRequested = false;
        QuickNoteDocument document = note;
        DateTime date = DateTime.Today;
        string priorArchiveSource = archivedRecoverySource, priorArchiveText = archivedRecoveryText;
        IntPtr dispatcherHandle = Handle;
        workerBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null; bool conflict = false; string archive = null;
            QuickNoteDocument result = document; string loadedText = null; NativeNotePrepared prepared = null;
            try {
                if (beforeStorageWork != null) beforeStorageWork();
                if (save) { store.SaveRecovery(document, text); store.Save(document, text); }
                else {
                    if (reload) archive = store.ArchiveRecovery(document, text);
                    result = store.Load(date); loadedText = result.Text;
                    conflict = result.HasRecovery && !string.Equals(result.RecoveryRevision, result.Revision, StringComparison.Ordinal);
                    if (!reload && result.HasRecovery && !conflict) loadedText = result.RecoveryText;
                    if (!reload && conflict && (priorArchiveSource != result.Path || priorArchiveText != result.RecoveryText)) archive = store.ArchiveRecovery(result, result.RecoveryText);
                    prepared = NativeNoteDocument.PrepareLoad(loadedText, ResolvePreview);
                }
            } catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                workerBusy = false; if (IsDisposed) return;
                // Archiving can succeed before import fails. Keep that route
                // without rebinding visible text to the newly loaded revision.
                if (archive != null) { archivedRecovery = archive; archivedRecoverySource = reload ? document.Path : result.Path; archivedRecoveryText = reload ? text : result.RecoveryText; }
                if (failure != null) {
                    if (!save) { LastLoadError = failure; CacheLoadFailureRecovery(result, archive); }
                    failedText = save ? text : reload ? text : failedText;
                    hideRequested = closeRequested = false; closeContinuation = null;
                    if (save) { CacheNoteMetadata(); loadRequested = false; if (!reloadRequested) editor.ReadOnly = false; }
                    else editor.ReadOnly = true;
                    SetStatus(failure is QuickNoteConflictException ? "外部内容已变化；草稿保留，点击处理" : "保存或载入失败，原文件未改动；点击查看恢复方式", true);
                    tips.SetToolTip(status, failure.Message);
                } else if (save) {
                    CacheNoteMetadata(); savedVersion = Math.Max(savedVersion, version); dirty = editVersion != savedVersion;
                    if (!dirty) { failedText = null; SetSavedStatus(); }
                } else {
                    try { ApplyLoaded(result, loadedText, prepared, conflict, reload); }
                    catch (Exception error) {
                        LastLoadError = error;
                        CacheLoadFailureRecovery(result, archive);
                        editor.ReadOnly = true; pendingText = null; loadRequested = reloadRequested = false;
                        hideRequested = closeRequested = false; closeContinuation = null;
                        SetStatus("此笔记暂时无法安全编辑，原 Markdown 未改动：" + error.Message, true);
                    }
                }
                if ((hideRequested || closeRequested) && dirty && failedText == null) CaptureSave();
                DrainWork();
            }); } catch (InvalidOperationException) { }
        });
    }
    void CacheLoadFailureRecovery(QuickNoteDocument result, string archive) {
        loadFailureRecoveryPath = archive ?? (result != null && result.HasRecovery ? result.RecoveryPath : null);
    }
    void FinishRequests() {
        if (dirty || IsComposing || workerBusy || imageQueue.Count > 0) return;
        if (closeRequested) { FinishClose(true); return; }
        if (hideRequested) { hideRequested = false; SaveWindowState(); Hide(); }
    }
    void FinishClose(bool continueExit) {
        Action continuation = closeContinuation; closeContinuation = null; closeRequested = false;
        SaveWindowState(); closeAllowed = true; Close(); if (!IsDisposed) Dispose();
        if (continueExit && continuation != null) continuation();
    }
    void SetStatus(string message, bool error) {
        status.Text = message; status.LinkColor = error ? Color.FromArgb(155, 73, 33) : Color.FromArgb(55, 96, 138);
        status.AccessibleName = message;
        string tip = !error && notePath != null ? notePath : message;
        if (!error && nativeDocument.TextImageFallback) tip += "\n图片较多，部分图片以 Markdown 引用显示；原图已保留。";
        tips.SetToolTip(status, tip);
    }
    void SetSavedStatus() {
        string folder = Path.GetFileName(store.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SetStatus(folder + " / " + Path.GetFileName(notePath), false);
    }
    void OpenFolder() {
        try {
            if (!Directory.Exists(store.FolderPath)) { SetStatus("输入内容后，会自动创建速记文件夹。", false); return; }
            System.Diagnostics.Process.Start("explorer.exe", "\"" + store.FolderPath.Replace("\"", "") + "\"");
        } catch (Exception error) { SetStatus("无法打开文件夹：" + error.Message, true); }
    }
    void ShowSaveMenu() {
        var menu = new ContextMenuStrip { Font = Font, RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("打开速记文件夹", null, delegate { OpenFolder(); });
        menu.Items.Add("复制当前内容", null, delegate { try { string text = ReadMarkdown(); if (text.Length > 0) Clipboard.SetText(text); } catch (Exception error) { SetStatus("无法复制：" + error.Message, true); } });
        string draftPath = loadFailureRecoveryPath ?? archivedRecovery ?? recoveryPath;
        if (!string.IsNullOrEmpty(draftPath)) menu.Items.Add("打开草稿所在文件夹", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + draftPath.Replace("\"", "") + "\""); } catch (Exception error) { tips.SetToolTip(status, error.Message); } });
        if (note != null && failedText != null) menu.Items.Add("保留草稿并载入磁盘版本", null, delegate { ReloadDiskKeepingDraft(); });
        menu.Closed += delegate { menu.Dispose(); }; menu.Show(status, new Point(0, status.Height));
    }
    void ShowEditorMenu() {
        var menu = new ContextMenuStrip { Font = Font, RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("撤销", null, delegate { CancelPendingImages(); editor.Undo(); }).Enabled = !editor.ReadOnly && editor.CanUndo;
        menu.Items.Add("重做", null, delegate { CancelPendingImages(); editor.Redo(); }).Enabled = !editor.ReadOnly && editor.CanRedo;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("剪切", null, delegate { CopyOrCut(true); }).Enabled = !editor.ReadOnly && editor.SelectionLength > 0;
        menu.Items.Add("复制", null, delegate { CopyOrCut(false); }).Enabled = editor.SelectionLength > 0;
        menu.Items.Add("粘贴", null, delegate { PasteFromClipboard(); }).Enabled = !editor.ReadOnly;
        menu.Items.Add("全选", null, delegate { editor.SelectAll(); });
        menu.Closed += delegate { menu.Dispose(); }; menu.Show(editor, editor.PointToClient(Cursor.Position));
    }
    void ReloadDiskKeepingDraft() {
        if (IsComposing || note == null || editor.ReadOnly && workerBusy) return;
        try { reloadText = ReadMarkdown(); }
        catch (Exception error) { SetStatus("无法保留当前草稿：" + error.Message, true); return; }
        pendingText = null; loadRequested = false; reloadRequested = true;
        hideRequested = closeRequested = false; closeContinuation = null;
        editor.ReadOnly = true; DrainWork();
    }
    void EditorKeyDown(object sender, KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape && !IsComposing) { e.Handled = true; TryHide(); return; }
        if (e.Control && (e.KeyCode == Keys.B || e.KeyCode == Keys.I || e.KeyCode == Keys.U || e.KeyCode == Keys.E || e.KeyCode == Keys.L || e.KeyCode == Keys.R)) e.Handled = true;
        if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; Flush(); }
    }
    void CopyOrCut(bool cut) {
        if (editor.SelectionLength == 0 || IsComposing || (cut && editor.ReadOnly)) return;
        try {
            string text = nativeDocument.MarkdownSelection(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength);
            if (text.Length == 0) return;
            Clipboard.SetText(text); // Only an explicit user command writes to the clipboard.
            if (cut) nativeDocument.InsertText("");
        } catch (Exception error) { SetStatus("无法复制或剪切，原内容仍在：" + error.Message, true); }
    }
    void PasteFromClipboard() {
        if (editor.ReadOnly || IsComposing) return;
        try { IDataObject snapshot = Clipboard.GetDataObject(); if (snapshot != null) PasteData(snapshot); }
        catch (Exception error) { SetStatus("无法读取剪贴板：" + error.Message, true); }
    }
    internal void PasteData(IDataObject data) {
        if (data == null) return;
        try {
            if (editor.ReadOnly || IsComposing) throw new IOException("请等待当前笔记载入或输入法提交完成。");
            var image = data.GetData(DataFormats.Bitmap, false) as Bitmap;
            // A screenshot can expose a DIB rather than CF_BITMAP. Conversion
            // stays limited to the standard Bitmap format on this one snapshot.
            if (image == null) image = data.GetData(DataFormats.Bitmap, true) as Bitmap;
            if (image != null) {
                long pixels = (long)image.Width * image.Height; CheckImageBudget(pixels);
                Bitmap snapshot = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
                try { snapshot.SetResolution(image.HorizontalResolution, image.VerticalResolution); QueueImage(snapshot, null); }
                catch { snapshot.Dispose(); throw; }
                return;
            }
            var files = data.GetData(DataFormats.FileDrop, false) as string[];
            if (files != null) { foreach (string path in files) InsertImageFile(path); return; }
            string text = data.GetData(DataFormats.UnicodeText, false) as string;
            if (text == null) text = data.GetData(DataFormats.Text, false) as string;
            if (text != null) nativeDocument.InsertText(text);
        } catch (Exception error) { SetStatus("无法插入内容：" + error.Message, true); }
    }
    void EditorDragOver(object sender, DragEventArgs e) {
        e.Effect = !editor.ReadOnly && !IsComposing && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop, false) ? DragDropEffects.Copy : DragDropEffects.None;
    }
    void Drop(object sender, DragEventArgs e) {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop, false)) { e.Effect = DragDropEffects.None; return; }
        DropData(e.Data, editor.PointToClient(new Point(e.X, e.Y)));
        e.Effect = editor.ReadOnly || IsComposing ? DragDropEffects.None : DragDropEffects.Copy;
    }
    internal void DropData(IDataObject data, Point clientPoint) {
        try {
            if (editor.ReadOnly || IsComposing || data == null) return;
            var files = data.GetData(DataFormats.FileDrop, false) as string[];
            if (files == null) return;
            editor.Select(editor.GetCharIndexFromPosition(clientPoint), 0);
            foreach (string path in files) InsertImageFile(path);
        } catch (Exception error) { SetStatus("无法插入图片：" + error.Message, true); }
    }
    static byte[] EncodePng(Bitmap source) {
        if (source == null || (long)source.Width * source.Height > QuickNoteStore.MaxImagePixels) throw new IOException("图片过大，请使用3600万像素以内的图片。");
        using (var stream = new MemoryStream()) { source.Save(stream, ImageFormat.Png); if (stream.Length > QuickNoteStore.MaxImageBytes) throw new IOException("图片需小于12MB。"); return stream.ToArray(); }
    }
    static byte[] ReadImageFile(string path) {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".gif" && extension != ".bmp") throw new IOException("请选择 PNG、JPG、GIF 或 BMP 图片。");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > QuickNoteStore.MaxImageBytes) throw new IOException("请选择12MB以内的图片文件。");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var image = Image.FromStream(stream, false, false)) {
            if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid && image.RawFormat.Guid != ImageFormat.Gif.Guid && image.RawFormat.Guid != ImageFormat.Bmp.Guid) throw new IOException("不支持此图片格式。");
            if ((long)image.Width * image.Height > QuickNoteStore.MaxImagePixels) throw new IOException("图片过大，请使用3600万像素以内的图片。");
            var source = image as Bitmap;
            if (source == null) throw new IOException("无法读取此图片。");
            // Pixel clone, not DPI-dependent DrawImageUnscaled. The original
            // image's full dimensions and alpha survive the PNG conversion.
            using (var bitmap = source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb)) {
                bitmap.SetResolution(source.HorizontalResolution, source.VerticalResolution);
                return EncodePng(bitmap);
            }
        }
    }
    byte[] ResolvePreview(string target) {
        string path = store.ResolveImage(target);
        if (path == null) return null;
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var image = Image.FromStream(stream, false, false)) {
                if ((long)image.Width * image.Height > QuickNoteStore.MaxImagePixels) return null;
                double scale = Math.Min(1, Math.Min(720.0 / image.Width, 780.0 / image.Height));
                int width = Math.Max(1, (int)Math.Round(image.Width * scale)), height = Math.Max(1, (int)Math.Round(image.Height * scale));
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb)) {
                    using (Graphics graphics = Graphics.FromImage(bitmap)) {
                        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                    }
                    using (var output = new MemoryStream()) { bitmap.Save(output, ImageFormat.Png); return output.ToArray(); }
                }
            }
        } catch (Exception) { return null; }
    }
    void InsertImageFile(string path) { QueueImage(null, Path.GetFullPath(path)); }
    void CheckImageBudget(long pixels) {
        if (pixels > QuickNoteStore.MaxImagePixels || pixels + queuedImagePixels > QuickNoteStore.MaxImagePixels || queuedImageCount >= 16)
            throw new IOException("图片处理量较大，请等待当前图片完成后再插入（最多3600万像素）。");
    }
    void QueueImage(Bitmap source, string file) {
        if (editor.ReadOnly) throw new IOException("请等待当前笔记载入完成。");
        long pixels = source == null ? 0 : (long)source.Width * source.Height; CheckImageBudget(pixels);
        var range = nativeDocument.CaptureRange(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength);
        PendingImage request;
        try {
            request = new PendingImage { Source = source, File = file, Range = range, SelectedText = range.Text, Version = editVersion, Epoch = imageEpoch, Generation = documentGeneration, Pixels = pixels };
            if (lastImageRequest != null && lastImageRequest.Generation == request.Generation && lastImageRequest.Version == request.Version && lastImageRequest.Range != null && lastImageRequest.Range.Start == range.Start) request.Previous = lastImageRequest;
        } catch { range.Dispose(); throw; }
        queuedImageCount++; queuedImagePixels += pixels; lastImageRequest = request; imageAnchors.Add(request);
        imageQueue.Enqueue(request); SetStatus("正在插入图片…", false); DrainWork();
    }
    void ReleaseImage(PendingImage request) {
        queuedImageCount--; queuedImagePixels -= request.Pixels;
        if (request.Source != null) { request.Source.Dispose(); request.Source = null; }
        if (queuedImageCount == 0) { lastImageRequest = null; ClearImageAnchors(); }
    }
    void ClearImageAnchors() {
        foreach (PendingImage item in imageAnchors) {
            if (item.Range != null) { item.Range.Dispose(); item.Range = null; }
            if (item.InsertEnd != null) { item.InsertEnd.Dispose(); item.InsertEnd = null; }
        }
        imageAnchors.Clear();
    }
    void CancelPendingImages() {
        imageEpoch++;
        while (imageQueue.Count > 0) ReleaseImage(imageQueue.Dequeue());
    }
    void StartImageWork(PendingImage request) {
        workerBusy = true; string lineEnding = newline;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null; NativeNotePrepared prepared = null;
            try {
                byte[] png = request.Source != null ? EncodePng(request.Source) : ReadImageFile(request.File);
                string target = store.StoreImage(png);
                prepared = NativeNoteDocument.PrepareSelection("![图片](<" + target + ">)" + lineEnding, ResolvePreview);
                if (prepared.References.Length != 1 && !prepared.TextImageFallback) throw new IOException("图片已保存，但无法显示预览。");
            } catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                if (IsDisposed) { if (request.Source != null) request.Source.Dispose(); return; }
                request.Prepared = prepared; request.Failure = failure; completedImage = request;
                // Changing selection or streaming RTF can interrupt an IME
                // candidate. Hold this one completed worker until commit; the
                // existing timer resumes even if composition changes no text.
                if (IsComposing) { saveTimer.Start(); return; }
                CompleteImageWork();
            }); } catch (InvalidOperationException) { if (request.Source != null) request.Source.Dispose(); }
        });
    }
    void CompleteImageWork() {
        if (completedImage == null || IsComposing || IsDisposed) return;
        PendingImage request = completedImage; completedImage = null; workerBusy = false;
        try {
            if (request.Failure != null) {
                hideRequested = closeRequested = false; closeContinuation = null;
                SetStatus("无法插入图片：" + request.Failure.Message, true);
            } else if (request.Generation == documentGeneration && request.Epoch == imageEpoch) {
                using (NativeNoteDocument.LiveRange liveSelection = nativeDocument.CaptureRange(editor.SelectionStart, editor.SelectionStart + editor.SelectionLength)) {
                    bool unchanged = request.Version == editVersion && request.Range.Start == liveSelection.Start && request.Range.End == liveSelection.End;
                    bool followsImage = request.Previous != null && request.Previous.InsertEnd != null;
                    int start = followsImage ? request.Previous.InsertEnd.Start : request.Range.Start;
                    int end = followsImage || request.Range.Text != request.SelectedText ? start : request.Range.End;
                    editor.Select(start, Math.Max(0, end - start));
                    nativeDocument.ReplaceSelectionPrepared(request.Prepared);
                    request.InsertEnd = nativeDocument.CaptureRange(editor.SelectionStart, editor.SelectionStart);
                    if (!unchanged) editor.Select(liveSelection.Start, Math.Max(0, liveSelection.End - liveSelection.Start));
                    if (nativeDocument.TextImageFallback) SetStatus("图片已保留，部分按 Markdown 引用显示", false);
                }
            }
        } catch (Exception error) {
            hideRequested = closeRequested = false; closeContinuation = null;
            SetStatus("图片未插入，文字仍在：" + error.Message, true);
        } finally { ReleaseImage(request); }
        if ((hideRequested || closeRequested || loadRequested) && dirty) CaptureSave();
        DrainWork();
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
            string[] parts = File.ReadAllText(Path.Combine(stateRoot, "quick-note-window.txt")).Split(','); int x, y, width, height;
            if (parts.Length != 5 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y) || !int.TryParse(parts[2], out width) || !int.TryParse(parts[3], out height)) return;
            var wanted = new Rectangle(x, y, Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
            Rectangle screen = Screen.FromRectangle(wanted).WorkingArea;
            wanted.Width = Math.Min(wanted.Width, screen.Width); wanted.Height = Math.Min(wanted.Height, screen.Height);
            wanted.X = Math.Max(screen.Left, Math.Min(wanted.X, screen.Right - wanted.Width)); wanted.Y = Math.Max(screen.Top, Math.Min(wanted.Y, screen.Bottom - wanted.Height));
            Bounds = wanted; TopMost = parts[4] != "0"; pin.Pinned = TopMost; pin.AccessibleName = TopMost ? "取消置顶" : "置顶"; tips.SetToolTip(pin, pin.AccessibleName);
        } catch { }
    }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            saveTimer.Dispose(); dayTimer.Dispose(); tips.Dispose();
            while (imageQueue.Count > 0) ReleaseImage(imageQueue.Dequeue());
            if (completedImage != null) { ReleaseImage(completedImage); completedImage = null; }
            ClearImageAnchors(); nativeDocument.Dispose();
            if (uiFont != null) { uiFont.Dispose(); uiFont = null; }
            if (editorFont != null) { editorFont.Dispose(); editorFont = null; }
        }
        base.Dispose(disposing);
    }


}
