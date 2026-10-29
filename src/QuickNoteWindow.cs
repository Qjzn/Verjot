using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
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
    internal Action PointerMoved, BeforeInput;
    internal Func<int, Point, bool> ImagePointer;
    internal Action<Graphics> ImagePaint;
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
        if (BeforeInput != null) BeforeInput();
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
        if (code == 0x200 && PointerMoved != null) PointerMoved();
        if ((code == 0x100 || code == 0x102 || code == 0x10d || code == 0x300 || code == 0x301 || code == 0x302) && BeforeInput != null) BeforeInput();
        if ((code == 0x201 || code == 0x200 || code == 0x202 || code == 0x203 || code == 0x204 || code == 0x215 || code == 0x100 && message.WParam.ToInt32() == 27) && ImagePointer != null) {
            long packed = message.LParam.ToInt64();
            if (ImagePointer(code, new Point((short)packed, (short)(packed >> 16)))) { message.Result = IntPtr.Zero; return; }
        }
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
        if (code == 0xf && ImagePaint != null) using (Graphics graphics = Graphics.FromHwnd(Handle)) ImagePaint(graphics);
        if (code == 0x10e) { compositionActive = false; if (CompositionChanged != null) CompositionChanged(false); }
    }
}

// A ten-pixel hit area with a three-pixel thumb. The editor retains Windows'
// real scroll range and wheel behavior; no polling timer runs while typing.
internal sealed class QuickNoteScrollBar : Control {
    [StructLayout(LayoutKind.Sequential)] struct ScrollInfo { internal uint Size, Mask; internal int Min, Max; internal uint Page; internal int Pos, Track; }
    [DllImport("user32.dll")] static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr PointMessage(IntPtr hwnd, int message, IntPtr wparam, ref Point point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr NativeMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    RichTextBox target;
    int contentHeight;
    bool extentDirty = true;
    internal RichTextBox Target {
        get { return target; }
        set {
            if (target == value) return;
            if (target != null) { target.TextChanged -= ExtentChanged; target.SizeChanged -= ExtentChanged; target.FontChanged -= ExtentChanged; }
            target = value; contentHeight = 0; extentDirty = true;
            if (target != null) { target.TextChanged += ExtentChanged; target.SizeChanged += ExtentChanged; target.FontChanged += ExtentChanged; }
            Invalidate();
        }
    }
    void ExtentChanged(object sender, EventArgs e) { extentDirty = true; Invalidate(); }
    void ContentResized(object sender, ContentsResizedEventArgs e) { contentHeight = e.NewRectangle.Height; }
    bool hover, dragging;
    int dragOffset;
    internal QuickNoteScrollBar() {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleName = "正文滚动条"; AccessibleRole = AccessibleRole.ScrollBar;
    }
    internal static Point Position(RichTextBox target) { Point p = Point.Empty; if (target.IsHandleCreated) PointMessage(target.Handle, 0x4dd, IntPtr.Zero, ref p); return p; }
    internal static void SetPosition(RichTextBox target, Point position) { if (target.IsHandleCreated) PointMessage(target.Handle, 0x4de, IntPtr.Zero, ref position); }
    ScrollInfo Info() {
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf(typeof(ScrollInfo)), Mask = 0x17 };
        if (Target != null && Target.IsHandleCreated && extentDirty) {
            // Request the extent lazily while painting/using the scrollbar,
            // outside RichEdit's WM_CHAR / IME editing path. A permanently
            // enabled EN_REQUESTRESIZE adds synchronous work to every edit.
            extentDirty = false; contentHeight = 0;
            Target.ContentsResized += ContentResized;
            try { NativeMessage(Target.Handle, 0x441, IntPtr.Zero, IntPtr.Zero); }
            finally { Target.ContentsResized -= ContentResized; }
        }
        // Forced native scrollbars may retain their former range after Clear.
        // Never draw that stale range for an empty document.
        if (Target != null && Target.IsHandleCreated && Target.TextLength != 0 &&
            (contentHeight == 0 || contentHeight > Target.ClientSize.Height)) GetScrollInfo(Target.Handle, 1, ref info);
        return info;
    }
    Rectangle Thumb(ScrollInfo info) {
        if (info.Max <= 0 || info.Page == 0 || info.Max - info.Min < info.Page) return Rectangle.Empty;
        int height = Math.Min(Height, Math.Max(22 * DeviceDpi / 96, (int)((long)Height * info.Page / (info.Max - info.Min + 1L))));
        int max = Math.Max(1, info.Max - info.Min - (int)info.Page + 1);
        int top = (int)((long)Math.Max(0, info.Pos - info.Min) * (Height - height) / max);
        int width = Math.Max(3, (hover || dragging ? 6 : 3) * DeviceDpi / 96);
        return new Rectangle((Width - width) / 2, top, width, height);
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor); Rectangle r = Thumb(Info());
        if (r.IsEmpty) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(hover || dragging ? 135 : 188, 194, 200), r.Width)) {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            e.Graphics.DrawLine(pen, r.Left + r.Width / 2f, r.Top + r.Width / 2f, r.Left + r.Width / 2f, r.Bottom - r.Width / 2f);
        }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        Rectangle thumb = Thumb(Info()); if (thumb.IsEmpty) return;
        dragOffset = e.Y >= thumb.Top && e.Y < thumb.Bottom ? e.Y - thumb.Top : thumb.Height / 2;
        dragging = Capture = true; ScrollTo(e.Y); Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) ScrollTo(e.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = Capture = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; Invalidate(); } }
    void ScrollTo(int y) {
        ScrollInfo info = Info(); Rectangle thumb = Thumb(info); if (thumb.IsEmpty || Target == null) return;
        int span = Math.Max(1, Height - thumb.Height), maximum = Math.Max(0, info.Max - info.Min - (int)info.Page + 1);
        Point position = Position(Target); position.Y = (int)((long)Math.Max(0, Math.Min(span, y - dragOffset)) * maximum / span);
        SetPosition(Target, position); Invalidate();
    }
    protected override void Dispose(bool disposing) { if (disposing) Target = null; base.Dispose(disposing); }
}

// Buffer just the navigation background. Native RichEdit retains its own
// independent paint and input path, including during toolbar transitions.
internal sealed class QuickNoteChromePanel : Panel {
    internal QuickNoteChromePanel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
}

// Owned, non-activating rail. Animation runs only for 150ms transitions; the
// native editor is neither resized nor repainted on each animation frame.
internal sealed class QuickNoteTabs : Form {
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    internal Action<string> HoverFile, SelectFile;
    internal Action<string, Point> FileMenu;
    internal Action More, LeaveRail;
    internal bool OnRight;
    internal bool IsRevealed { get { return targetReveal == 1; } }
    readonly Timer animation = new Timer { Interval = 16 };
    readonly ToolTip tip = new ToolTip { ShowAlways = true };
    Size fileTipSize;
    Font fileTipFont;
    int fileTipDpi;
    string[] files = new string[0];
    string current;
    int hovered = -1;
    float reveal, targetReveal;
    float[] emphasis = new float[1];
    int lastTick;
    internal QuickNoteTabs() {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyKey = Color.Fuchsia;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AccessibleName = "最近便签标签";
        tip.OwnerDraw = true; tip.UseAnimation = tip.UseFading = false;
        tip.Popup += delegate(object sender, PopupEventArgs e) { e.ToolTipSize = fileTipSize; };
        tip.Draw += delegate(object sender, DrawToolTipEventArgs e) {
            e.Graphics.Clear(VerbitUi.Surface);
            using (var border = new Pen(VerbitUi.Border)) e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, fileTipFont,
                new Rectangle(8, 6, Math.Max(1, e.Bounds.Width - 16), Math.Max(1, e.Bounds.Height - 12)), VerbitUi.Ink,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        };
        animation.Tick += delegate {
            int now = Environment.TickCount; float amount = 1 - (float)Math.Exp(-Math.Max(1, unchecked(now - lastTick)) / 36.0); lastTick = now;
            reveal += (targetReveal - reveal) * amount;
            bool settled = Math.Abs(targetReveal - reveal) < .01f;
            for (int i = 0; i < emphasis.Length; i++) {
                float target = i == hovered ? 1 : 0;
                emphasis[i] += (target - emphasis[i]) * amount;
                if (Math.Abs(target - emphasis[i]) < .01f) emphasis[i] = target; else settled = false;
            }
            if (settled) {
                reveal = targetReveal; animation.Stop(); if (reveal == 0) Hide();
            }
            Invalidate();
        };
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; } }
    int Row { get { return Math.Max(30, 36 * DeviceDpi / 96); } }
    internal void SetFiles(string[] names, string selected) {
        bool changed = current != selected || files.Length != names.Length;
        for (int i = 0; !changed && i < names.Length; i++) changed = files[i] != names[i];
        if (!changed) return;
        files = names; current = selected; hovered = -1; emphasis = new float[files.Length + 1]; tip.Hide(this);
        Height = (files.Length + 1) * Row; Invalidate();
    }
    void Animate() { lastTick = Environment.TickCount; if (!animation.Enabled) animation.Start(); }
    internal void Reveal() { if (targetReveal == 1) return; targetReveal = 1; Animate(); }
    internal void Conceal() { tip.Hide(this); hovered = -1; targetReveal = 0; if (Visible) Animate(); }
    internal void SuspendForMove() { tip.Hide(this); hovered = -1; targetReveal = reveal = 0; Array.Clear(emphasis, 0, emphasis.Length); animation.Stop(); Hide(); }
    int Hit(Point point) { int row = point.Y / Row; return (OnRight ? point.X < Width * 4 / 5 : point.X >= Width / 5) && point.X >= 0 && point.X < Width && point.Y >= 0 && row <= files.Length ? row : -1; }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e); int index = Hit(e.Location); if (index == hovered) return;
        tip.Hide(this); hovered = index; Animate();
        if (HoverFile != null) HoverFile(index >= 0 && index < files.Length ? files[index] : null);
        if (index >= 0 && index < files.Length) ShowFileTip(files[index]);
    }
    internal void ShowFileTip(string text) {
        if (hovered < 0 || !Visible) return;
        if (fileTipFont == null || fileTipDpi != DeviceDpi) {
            if (fileTipFont != null) fileTipFont.Dispose();
            fileTipDpi = DeviceDpi;
            fileTipFont = new Font("Microsoft YaHei UI", 12f * DeviceDpi / 96, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        int gap = Math.Max(4, 6 * DeviceDpi / 96);
        Rectangle area = Screen.FromControl(this).WorkingArea;
        int available = OnRight ? area.Right - Right - gap : Left - area.Left - gap;
        if (available < 36 * DeviceDpi / 96) { tip.Hide(this); return; }
        // Bound very long names without omitting characters. Native screen
        // clamping must not move a huge tooltip back over the hover target.
        int lineWidth = Math.Min(260 * DeviceDpi / 96, available - 16);
        var wrapped = new StringBuilder(); string line = "";
        for (int i = 0; i < text.Length; i++) {
            string token = Char.IsHighSurrogate(text[i]) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1]) ? text.Substring(i++, 2) : text[i].ToString();
            if (token == "\n") { wrapped.Append('\n'); line = ""; continue; }
            if (line.Length > 0 && TextRenderer.MeasureText(line + token, fileTipFont).Width > lineWidth) { wrapped.Append('\n'); line = ""; }
            wrapped.Append(token); line += token;
        }
        string label = wrapped.ToString(); Size size = TextRenderer.MeasureText(label, fileTipFont);
        Size padded = new Size(size.Width + 16, size.Height + 12); fileTipSize = padded;
        Rectangle note = Owner == null ? Rectangle.Empty : Owner.Bounds;
        int rowY = Math.Max(area.Top, Math.Min(Top + hovered * Row, area.Bottom - padded.Height));
        // Follow the rail's actual side, never jump across the whole note to
        // an unrelated header/footer. The full name is also available in More.
        Point[] candidates = { new Point(OnRight ? Right + gap : Left - gap - padded.Width, rowY) };
        foreach (Point point in candidates) {
            Rectangle candidate = new Rectangle(point, padded);
            if (!area.Contains(candidate) || candidate.IntersectsWith(note) || candidate.IntersectsWith(Bounds)) continue;
            tip.Show(label, this, PointToClient(point), 6000);
            // Windows may clamp its actual popup at a monitor edge. Never let
            // that turn an otherwise safe placement back into a text overlay.
            IntPtr handle = (IntPtr)typeof(ToolTip).GetProperty("Handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(tip, null);
            NativeRect actual;
            if (GetWindowRect(handle, out actual)) {
                Rectangle bounds = Rectangle.FromLTRB(actual.Left, actual.Top, actual.Right, actual.Bottom);
                if (area.Contains(bounds) && !bounds.IntersectsWith(note) && !bounds.IntersectsWith(Bounds)) return;
            }
            tip.Hide(this);
        }
        tip.Hide(this); // No safe room: the full name remains available in More.
    }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); tip.Hide(this); }
    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e); tip.Hide(this); hovered = -1; Animate();
        if (LeaveRail != null) LeaveRail();
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        int index = Hit(e.Location); tip.Hide(this);
        if (e.Button == MouseButtons.Right) { if (index >= 0 && index < files.Length && FileMenu != null) FileMenu(files[index], PointToScreen(e.Location)); return; }
        if (e.Button != MouseButtons.Left) return;
        if (index >= 0 && index < files.Length && SelectFile != null) SelectFile(files[index]);
        else if (index == files.Length && More != null) More();
    }
    protected override void OnPaint(PaintEventArgs e) {
        // Color-keyed edges must stay opaque; antialiasing against the key
        // would leave pink fringes on light desktops.
        e.Graphics.Clear(TransparencyKey); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        for (int i = 0; i <= files.Length; i++) {
            bool selected = i < files.Length && String.Equals(files[i], current, StringComparison.OrdinalIgnoreCase);
            float extra = selected ? 7 : 7 * emphasis[i];
            int width = (int)((70 + extra) * DeviceDpi / 96f * reveal), height = Row - 7 * DeviceDpi / 96;
            if (width < 8) continue;
            Rectangle r = new Rectangle(Width - width, i * Row + 2, width + 6, height);
            if (OnRight) r.X = -6;
            Color color = selected ? VerbitUi.Hover : VerbitUi.Surface;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath()) {
                int radius = 6 * DeviceDpi / 96;
                if (OnRight) { path.AddLine(r.Left, r.Top, r.Right - radius, r.Top); path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90); path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90); path.AddLine(r.Right - radius, r.Bottom, r.Left, r.Bottom); path.CloseFigure(); }
                else { path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90); path.AddLine(r.Left + radius, r.Top, r.Right, r.Top);
                    path.AddLine(r.Right, r.Top, r.Right, r.Bottom); path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90); path.CloseFigure(); }
                using (var brush = new SolidBrush(color)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Color.FromArgb(211, 219, 220))) e.Graphics.DrawPath(pen, path);
            }
            Rectangle label = new Rectangle(r.Left + (OnRight ? 13 : 9) * DeviceDpi / 96, r.Top, Math.Max(0, width - 12 * DeviceDpi / 96), r.Height);
            if (selected) {
                int dot = 4 * DeviceDpi / 96; using (var brush = new SolidBrush(Color.FromArgb(36, 59, 83))) e.Graphics.FillEllipse(brush, label.Left, r.Top + (r.Height - dot) / 2, dot, dot);
                label.X += dot + 4; label.Width -= dot + 4;
            }
            TextRenderer.DrawText(e.Graphics, i == files.Length ? "更多…" : Path.GetFileNameWithoutExtension(files[i]), Font, label, Color.FromArgb(36, 59, 83), color, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) { animation.Dispose(); tip.Dispose(); if (fileTipFont != null) fileTipFont.Dispose(); } base.Dispose(disposing); }
}

internal sealed class QuickNoteRecentList : ListBox {
    internal Action Scrolled;
    protected override void WndProc(ref Message m) {
        base.WndProc(ref m);
        if (Scrolled != null && (m.Msg == 0x115 || m.Msg == 0x20a || m.Msg == 0x197 || m.Msg == 0x100)) Scrolled();
    }
}

// Clip the native list's scrollbar, keeping its keyboard, wheel and selection
// behavior. This small overlay has no idle timer and never touches RichEdit.
internal sealed class QuickNoteRecentScrollBar : Control {
    internal ListBox Target;
    bool hover, dragging;
    int dragOffset;
    internal QuickNoteRecentScrollBar() {
        AccessibleName = "便签列表滚动条"; AccessibleRole = AccessibleRole.ScrollBar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    internal Rectangle ThumbBounds {
        get {
            if (Target == null || Target.ItemHeight <= 0) return Rectangle.Empty;
            int page = Math.Max(1, Target.ClientSize.Height / Target.ItemHeight), count = Target.Items.Count;
            if (count <= page || Height <= 0) return Rectangle.Empty;
            int height = Math.Min(Height, Math.Max(20 * DeviceDpi / 96, Height * page / count));
            int top = (int)((long)Math.Min(count - page, Target.TopIndex) * (Height - height) / (count - page));
            int width = Math.Max(3, (hover || dragging ? 5 : 3) * DeviceDpi / 96);
            return new Rectangle((Width - width) / 2, top, width, height);
        }
    }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor); Rectangle r = ThumbBounds; if (r.IsEmpty) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(hover || dragging ? VerbitUi.Muted : Color.FromArgb(188, 194, 200), r.Width)) {
            pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            e.Graphics.DrawLine(pen, r.Left + r.Width / 2f, r.Top + r.Width / 2f, r.Left + r.Width / 2f, r.Bottom - r.Width / 2f);
        }
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        Rectangle thumb = ThumbBounds; if (thumb.IsEmpty) return;
        dragOffset = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
        dragging = Capture = true; ScrollTo(e.Y);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) ScrollTo(e.Y); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = Capture = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { dragging = false; Invalidate(); } }
    void ScrollTo(int y) {
        Rectangle thumb = ThumbBounds; if (thumb.IsEmpty) return;
        int page = Math.Max(1, Target.ClientSize.Height / Target.ItemHeight), span = Math.Max(1, Height - thumb.Height);
        Target.TopIndex = (int)((long)Math.Max(0, Math.Min(span, y - dragOffset)) * (Target.Items.Count - page) / span);
        Invalidate();
    }
}

internal sealed class QuickNoteRecentPicker : UserControl {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, string text);
    internal readonly TextBox Search = new TextBox { BorderStyle = BorderStyle.None, AccessibleName = "查找便签文件名" };
    internal readonly QuickNoteRecentList Results = new QuickNoteRecentList { BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, AccessibleName = "最近便签，方向键选择，回车打开" };
    internal readonly QuickNoteRecentScrollBar ListScroll = new QuickNoteRecentScrollBar();
    readonly QuickNoteChromePanel searchFrame = new QuickNoteChromePanel();
    readonly Panel viewport = new Panel();
    internal Action<string> SelectFile;
    internal Action<string, Point> FileMenu;
    internal Action ClosePopup;
    readonly Label heading = new Label { Text = "最近便签", AutoSize = false };
    readonly Label count = new Label { TextAlign = ContentAlignment.MiddleRight };
    readonly Label empty = new Label { Text = "没有匹配的便签", TextAlign = ContentAlignment.MiddleCenter };
    readonly string[] files;
    readonly string current;
    internal QuickNoteRecentPicker(string[] names, string selected, Font font, int dpi, Size maximum) {
        files = names; current = selected; AutoScaleMode = AutoScaleMode.None; Font = Search.Font = font;
        BackColor = Results.BackColor = Search.BackColor = VerbitMenuRenderer.Surface;
        Search.HandleCreated += delegate { SendMessage(Search.Handle, 0x1501, new IntPtr(1), "搜索便签名称…"); };
        VerbitMenu.AttachTextMenu(Search);
        ForeColor = Results.ForeColor = Search.ForeColor = VerbitMenuRenderer.Ink;
        heading.ForeColor = VerbitMenuRenderer.Ink; count.ForeColor = empty.ForeColor = VerbitMenuRenderer.Muted;
        int unit = Math.Max(1, dpi), pad = Math.Max(6, 10 * unit / 96), gap = Math.Max(4, 6 * unit / 96);
        int row = Math.Max(28 * unit / 96, font.Height + 8 * unit / 96), header = Math.Max(20 * unit / 96, font.Height);
        int searchHeight = Math.Max(30 * unit / 96, font.Height + 12 * unit / 96);
        int desiredWidth = 220 * unit / 96;
        foreach (string file in files) desiredWidth = Math.Max(desiredWidth,
            TextRenderer.MeasureText(Path.GetFileNameWithoutExtension(file), font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 50 * unit / 96);
        Size = new Size(Math.Min(Math.Min(280 * unit / 96, desiredWidth), maximum.Width), Math.Min(pad * 2 + header + gap * 2 + searchHeight + row * Math.Min(6, Math.Max(1, files.Length)), maximum.Height));
        heading.SetBounds(pad, pad, Math.Max(1, Width - pad * 2 - 44 * unit / 96), header);
        count.SetBounds(heading.Right, pad, 44 * unit / 96, header);
        searchFrame.SetBounds(pad, heading.Bottom + gap, Math.Max(1, Width - pad * 2), searchHeight);
        Search.SetBounds(pad, (searchHeight - Search.PreferredHeight) / 2, Math.Max(1, searchFrame.Width - pad * 2), Search.PreferredHeight);
        searchFrame.BackColor = BackColor; searchFrame.Controls.Add(Search);
        searchFrame.Paint += delegate(object sender, PaintEventArgs e) {
            Rectangle r = new Rectangle(0, 0, searchFrame.Width - 1, searchFrame.Height - 1); int radius = 6 * unit / 96;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath()) using (var pen = new Pen(Search.Focused ? VerbitUi.Accent : VerbitUi.Border)) {
                path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90); path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90);
                path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90); path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90); path.CloseFigure();
                e.Graphics.DrawPath(pen, path);
            }
        };
        Search.GotFocus += delegate { searchFrame.Invalidate(); }; Search.LostFocus += delegate { searchFrame.Invalidate(); };
        int barWidth = Math.Max(8, 10 * unit / 96);
        viewport.SetBounds(pad, searchFrame.Bottom + gap, Math.Max(1, Width - pad * 2 - barWidth), Math.Max(1, Height - searchFrame.Bottom - gap - pad));
        Results.SetBounds(0, 0, viewport.Width + SystemInformation.VerticalScrollBarWidth + 2, viewport.Height);
        Results.ItemHeight = row; viewport.Controls.Add(Results);
        ListScroll.SetBounds(viewport.Right, viewport.Top, barWidth, viewport.Height); ListScroll.BackColor = BackColor; ListScroll.Target = Results;
        Results.Scrolled = delegate { ListScroll.Invalidate(); };
        Results.SelectedIndexChanged += delegate { ListScroll.Invalidate(); };
        empty.Bounds = new Rectangle(viewport.Location, new Size(viewport.Width + barWidth, viewport.Height));
        Controls.AddRange(new Control[] { heading, count, searchFrame, viewport, ListScroll, empty });
        Results.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            if (e.Index < 0) return;
            bool active = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(VerbitMenuRenderer.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            Rectangle rowBounds = new Rectangle(0, e.Bounds.Top, viewport.Width, e.Bounds.Height);
            if (active) VerbitUi.ToolBackground(e.Graphics, Rectangle.Inflate(rowBounds, 0, -2 * unit / 96), VerbitMenuRenderer.Surface, VerbitMenuRenderer.Hover, 1, unit);
            Color ink = active && SystemInformation.HighContrast ? SystemColors.HighlightText : VerbitMenuRenderer.Ink;
            string name = (string)Results.Items[e.Index]; bool isCurrent = String.Equals(name, current, StringComparison.OrdinalIgnoreCase);
            Rectangle text = Rectangle.Inflate(rowBounds, -8 * unit / 96, 0);
            if (isCurrent) { int diameter = 5 * unit / 96; using (var brush = new SolidBrush(active && SystemInformation.HighContrast ? ink : VerbitMenuRenderer.Accent)) e.Graphics.FillEllipse(brush, text.Left, text.Top + (text.Height - diameter) / 2, diameter, diameter); }
            text.X += 13 * unit / 96; text.Width -= 13 * unit / 96;
            TextRenderer.DrawText(e.Graphics, Path.GetFileNameWithoutExtension(name), Font, text, ink, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        };
        Results.MouseMove += delegate(object sender, MouseEventArgs e) { int index = Results.IndexFromPoint(e.Location); if (index >= 0 && Results.SelectedIndex != index) Results.SelectedIndex = index; };
        Results.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && Results.IndexFromPoint(e.Location) >= 0) Choose(); };
        Results.MouseDown += delegate(object sender, MouseEventArgs e) {
            int index = Results.IndexFromPoint(e.Location);
            if (e.Button == MouseButtons.Right && index >= 0) { Results.SelectedIndex = index; if (FileMenu != null) FileMenu((string)Results.Items[index], Results.PointToScreen(e.Location)); }
        };
        KeyEventHandler key = delegate(object sender, KeyEventArgs e) {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (ClosePopup != null) ClosePopup(); }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Choose(); }
            else if (sender == Search && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)) { e.SuppressKeyPress = true; if (Results.Items.Count != 0) Results.SelectedIndex = Math.Max(0, Math.Min(Results.Items.Count - 1, Results.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1))); }
        };
        Search.KeyDown += key; Results.KeyDown += key; Search.TextChanged += delegate { Filter(); }; Filter();
    }
    void Choose() { if (Results.SelectedItem != null && SelectFile != null) SelectFile((string)Results.SelectedItem); }
    void Filter() {
        Results.BeginUpdate(); Results.Items.Clear(); string query = Search.Text.Trim();
        foreach (string file in files) if (file.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) Results.Items.Add(file);
        int selected = Results.Items.IndexOf(current); Results.SelectedIndex = Results.Items.Count == 0 ? -1 : Math.Max(0, selected);
        Results.EndUpdate(); empty.Visible = Results.Items.Count == 0; Results.Visible = !empty.Visible;
        count.Text = Results.Items.Count.ToString(CultureInfo.InvariantCulture); ListScroll.Invalidate();
    }
}

// A tiny, interruptible color transition. The timer is off at rest, and never
// changes control bounds or repaints the native document.
internal sealed class QuickNoteHoverFeedback : IDisposable {
    readonly Timer timer = new Timer { Interval = 16 };
    internal float Value;
    float target;
    int previous;
    internal QuickNoteHoverFeedback(Action invalidate) {
        timer.Tick += delegate {
            int now = Environment.TickCount;
            Value += (target - Value) * (1 - (float)Math.Exp(-Math.Max(1, unchecked(now - previous)) / 26.0)); previous = now;
            if (Math.Abs(Value - target) < .015f) { Value = target; timer.Stop(); }
            invalidate();
        };
    }
    internal void Set(bool hovered) {
        float next = hovered ? 1 : 0;
        if (target == next) return;
        target = next; previous = Environment.TickCount; timer.Start();
    }
    public void Dispose() { timer.Dispose(); }
}

internal sealed class QuickNoteHeaderButton : Button {
    public bool IsPin;
    internal string ActionIcon;
    public bool Pinned;
    readonly QuickNoteHoverFeedback hover;
    public QuickNoteHeaderButton() {
        hover = new QuickNoteHoverFeedback(Invalidate);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = VerbitUi.Surface; ForeColor = VerbitUi.Accent;
        TabStop = true; Size = new Size(32, 30);
        MouseEnter += delegate { hover.Set(true); };
        MouseLeave += delegate { hover.Set(false); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnPaint(PaintEventArgs e) {
        VerbitUi.ToolBackground(e.Graphics, ClientRectangle, BackColor, VerbitUi.Hover, hover.Value, DeviceDpi);
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
            } else if (ActionIcon == "close") {
                e.Graphics.DrawLine(pen, cx - 4 * scale, cy - 4 * scale, cx + 4 * scale, cy + 4 * scale);
                e.Graphics.DrawLine(pen, cx + 4 * scale, cy - 4 * scale, cx - 4 * scale, cy + 4 * scale);
            } else {
                e.Graphics.DrawLine(pen, cx - 5 * scale, cy, cx + 5 * scale, cy);
                if (ActionIcon == "new") e.Graphics.DrawLine(pen, cx, cy - 5 * scale, cx, cy + 5 * scale);
            }
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
    protected override void Dispose(bool disposing) { if (disposing) hover.Dispose(); base.Dispose(disposing); }
}

internal sealed class QuickNoteTitle : Label {
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

internal sealed class QuickNoteFormatButton : Button {
    internal string Format;
    internal bool Active;
    Font glyphFont;
    int glyphDpi;
    string glyphFormat;
    readonly QuickNoteHoverFeedback hover;
    internal QuickNoteFormatButton() {
        hover = new QuickNoteHoverFeedback(Invalidate);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TabStop = true;
        Size = new Size(34, 30); Margin = new Padding(0, 0, 2, 0);
        BackColor = VerbitUi.Surface; ForeColor = VerbitUi.Ink;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        MouseEnter += delegate { hover.Set(true); }; MouseLeave += delegate { hover.Set(false); };
    }
    protected override void OnPaint(PaintEventArgs e) {
        VerbitUi.ToolBackground(e.Graphics, ClientRectangle, BackColor, VerbitUi.Hover, Active ? 1 : hover.Value, DeviceDpi);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f, x = Width / 2f, y = Height / 2f;
        using (var pen = new Pen(Enabled ? ForeColor : SystemColors.GrayText, 1.4f * scale)) {
            if (Format == "bullet") {
                for (int row = -1; row <= 1; row++) {
                    float yy = y + row * 5 * scale;
                    e.Graphics.DrawEllipse(pen, x - 9 * scale, yy - scale, 2 * scale, 2 * scale);
                    e.Graphics.DrawLine(pen, x - 3 * scale, yy, x + 8 * scale, yy);
                }
            } else if (Format == "location") {
                e.Graphics.DrawLines(pen, new[] { new PointF(x - 6 * scale, y + 8 * scale), new PointF(x - 6 * scale, y - 8 * scale), new PointF(x + 2 * scale, y - 8 * scale), new PointF(x + 7 * scale, y - 3 * scale), new PointF(x + 7 * scale, y + 8 * scale), new PointF(x - 6 * scale, y + 8 * scale) });
                e.Graphics.DrawLines(pen, new[] { new PointF(x + 2 * scale, y - 8 * scale), new PointF(x + 2 * scale, y - 3 * scale), new PointF(x + 7 * scale, y - 3 * scale) });
                e.Graphics.DrawLine(pen, x - 3 * scale, y + 1 * scale, x + 4 * scale, y + 1 * scale);
                e.Graphics.DrawLine(pen, x - 3 * scale, y + 4 * scale, x + 3 * scale, y + 4 * scale);
            } else if (Format == "image") {
                e.Graphics.DrawRectangle(pen, x - 9 * scale, y - 7 * scale, 18 * scale, 14 * scale);
                e.Graphics.DrawEllipse(pen, x + 3 * scale, y - 4 * scale, 2 * scale, 2 * scale);
                e.Graphics.DrawLines(pen, new[] { new PointF(x - 8 * scale, y + 5 * scale), new PointF(x - 3 * scale, y - scale), new PointF(x + 2 * scale, y + 4 * scale), new PointF(x + 5 * scale, y + scale), new PointF(x + 8 * scale, y + 5 * scale) });
            } else {
                FontStyle style = Format == "bold" ? FontStyle.Bold : Format == "italic" ? FontStyle.Italic : Format == "underline" ? FontStyle.Underline : FontStyle.Strikeout;
                string label = Format == "bold" ? "B" : Format == "italic" ? "I" : Format == "underline" ? "U" : "ab";
                if (glyphFont == null || glyphDpi != DeviceDpi || glyphFormat != Format) {
                    if (glyphFont != null) glyphFont.Dispose();
                    glyphFont = new Font("Segoe UI", 14f * DeviceDpi / 96f, style, GraphicsUnit.Pixel); glyphDpi = DeviceDpi; glyphFormat = Format;
                }
                TextRenderer.DrawText(e.Graphics, label, glyphFont, ClientRectangle, Enabled ? ForeColor : SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
    protected override void Dispose(bool disposing) { if (disposing) { hover.Dispose(); if (glyphFont != null) { glyphFont.Dispose(); glyphFont = null; } } base.Dispose(disposing); }
}

internal sealed class QuickNoteWindow : Form {
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool HideCaret(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowCaret(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    readonly QuickNoteStore store;
    readonly string stateRoot;
    readonly QuickNoteInputBox editor;
    readonly NativeNoteDocument nativeDocument;
    readonly Label dateLabel;
    readonly Panel top, body, viewport;
    readonly QuickNoteHeaderButton newButton, hideButton, closeButton;
    readonly PictureBox brand;
    readonly QuickNoteScrollBar scroll;
    readonly Timer hoverTimer = new Timer { Interval = 250 };
    readonly Timer leaveTimer = new Timer { Interval = 300 };
    readonly Dictionary<string, NoteView> views = new Dictionary<string, NoteView>(StringComparer.OrdinalIgnoreCase);
    readonly List<string> recentFiles = new List<string>();
    QuickNoteTabs tabs;
    internal Action Minimized;
    bool noteMinimized;
    QuickNoteInputBox previewEditor;
    NativeNoteDocument previewDocument;
    string requestedFile, hoverFile, previewFile, lastSavedFile;
    string deleteRequested;
    bool createRequested, restoreLast, navigationRestored, previewBusy, previewVisible, previewOnHover = true;
    int previewEpoch;
    bool chromeVisible = true, statusShown, statusError;
    bool tabsHaveSpace = true;
    bool movingWindow;
    Rectangle imageDragStart, imageDragOutline;
    int imageDragCp = -1, imageDragHandle;
    bool previewCaretHidden;
    internal Action<string> OpenImageFile = delegate(string path) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); };
    internal Func<string, bool> ConfirmDelete = null;
    sealed class NoteView { public int Start, Length, X, Y; }
    sealed class NavigationState { public string Last; public bool Preview = true; public Dictionary<string, NoteView> Views; }
    readonly LinkLabel status;
    readonly QuickNoteHeaderButton pin;
    readonly Panel formatBar;
    readonly QuickNoteFormatButton location;
    readonly Timer saveTimer = new Timer { Interval = 100 };
    readonly ToolTip tips = new ToolTip { ShowAlways = true, InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 10000 };
    readonly System.ComponentModel.Container transientMenus = new System.ComponentModel.Container();
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
        Text = "速记"; Icon = MeetingBrand.IdleIcon; ShowInTaskbar = false; TopMost = true;
        MaximizeBox = false; MinimizeBox = true; FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(4); SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        ClientSize = new Size(360, 420); MinimumSize = new Size(280, 240);
        StartPosition = FormStartPosition.Manual; BackColor = VerbitUi.Surface;
        top = new QuickNoteChromePanel { Dock = DockStyle.Top, Height = 40, BackColor = BackColor };
        dateLabel = new QuickNoteTitle { TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false, ForeColor = Color.FromArgb(36, 59, 83), AccessibleName = "当前便签文件名" };
        brand = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Image = MeetingBrand.LogoBitmap(48), AccessibleName = "verbit Logo" };
        newButton = new QuickNoteHeaderButton { ActionIcon = "new", AccessibleName = "新建便签", Name = "note-new" };
        pin = new QuickNoteHeaderButton { IsPin = true, Pinned = true, AccessibleName = "取消置顶", Name = "note-pin" };
        hideButton = new QuickNoteHeaderButton { AccessibleName = "最小化", Name = "note-minimize" };
        closeButton = new QuickNoteHeaderButton { ActionIcon = "close", AccessibleName = "保存并关闭便签", Name = "note-close" };
        tips.SetToolTip(newButton, "新建便签 · Ctrl+N"); tips.SetToolTip(pin, "取消置顶"); tips.SetToolTip(hideButton, "最小化"); tips.SetToolTip(closeButton, "保存并关闭便签 · Esc");
        top.Controls.AddRange(new Control[] { newButton, brand, dateLabel, pin, hideButton, closeButton });
        foreach (Control control in new Control[] { newButton, dateLabel, pin, hideButton, closeButton }) {
            Control target = control;
            target.MouseEnter += delegate { ShowFormatTip(target); };
            target.MouseLeave += delegate { tips.Hide(target); };
            target.MouseDown += delegate { tips.Hide(target); };
        }
        top.Layout += delegate { LayoutHeader(); };
        status = new LinkLabel { Dock = DockStyle.Bottom, Height = 32, Visible = false, Padding = new Padding(14, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, LinkBehavior = LinkBehavior.HoverUnderline, ForeColor = Color.FromArgb(88, 107, 128), LinkColor = VerbitUi.Accent, ActiveLinkColor = Color.FromArgb(40, 104, 168), AccessibleName = "速记保存提示" };
        editor = new QuickNoteInputBox { BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = VerbitUi.Ink, Font = editorFont, AcceptsTab = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.ForcedVertical, HideSelection = false, AllowDrop = true, AccessibleName = "速记正文", Name = "quick-note-editor", ReadOnly = true };
        body = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Padding = new Padding(14, 12, 4, 8) };
        viewport = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        scroll = new QuickNoteScrollBar { Dock = DockStyle.Right, Width = 10, BackColor = BackColor, Target = editor };
        viewport.Controls.Add(editor); body.Controls.Add(viewport); body.Controls.Add(scroll);
        viewport.Resize += delegate { LayoutEditors(); };
        editor.VScroll += delegate { CancelImageDrag(); scroll.Invalidate(); };
        editor.HScroll += delegate { CancelImageDrag(); };
        formatBar = new QuickNoteChromePanel { Dock = DockStyle.Bottom, Height = 38, BackColor = BackColor, AccessibleName = "速记格式工具栏", Enabled = false };
        string[] formats = { "bold", "italic", "underline", "strike", "bullet", "image", "location" };
        string[] labels = { "加粗 · Ctrl+B", "斜体 · Ctrl+I", "下划线 · Ctrl+U", "删除线 · Ctrl+Shift+X", "圆点列表 · Ctrl+Shift+L", "插入图片…", "保存位置与恢复选项" };
        for (int index = 0; index < formats.Length; index++) {
            string format = formats[index];
            var button = new QuickNoteFormatButton { Format = format, Name = "format-" + format, AccessibleName = labels[index] };
            tips.SetToolTip(button, labels[index]);
            // Tracking tips remain visible above this topmost window, including
            // when another application owns focus. No mouse polling/timer.
            button.MouseEnter += delegate { ShowFormatTip(button); };
            button.MouseLeave += delegate { tips.Hide(button); };
            button.MouseDown += delegate { tips.Hide(button); };
            button.Click += delegate { if (format == "location") ShowSaveMenu(); else ApplyFormat(format); }; formatBar.Controls.Add(button);
        }
        location = (QuickNoteFormatButton)formatBar.Controls["format-location"];
        formatBar.Layout += delegate { LayoutFormatBar(); };
        LayoutFormatBar();
        formatBar.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Color.FromArgb(226, 230, 233))) e.Graphics.DrawLine(pen, 12, 0, formatBar.Width - 12, 0); };
        Controls.Add(body); Controls.Add(status); Controls.Add(formatBar); Controls.Add(top);
        // Chrome is clipped on leave; the editor's geometry never changes.
        top.Dock = body.Dock = formatBar.Dock = status.Dock = DockStyle.None;
        nativeDocument = new NativeNoteDocument(editor, null);
        editor.ImagePointer = HandleImagePointer;
        editor.PointerMoved = RevealForPointer;
        editor.BeforeInput = delegate { if (previewVisible || hoverFile != null) CancelPreview(); };
        editor.ImagePaint = delegate(Graphics graphics) {
            if (imageDragCp < 0) return;
            using (var pen = new Pen(Color.FromArgb(40, 104, 168), Math.Max(1, DeviceDpi / 96f))) {
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                graphics.DrawRectangle(pen, imageDragOutline);
            }
        };
        pin.Click += delegate { TopMost = !TopMost; pin.Pinned = TopMost; pin.AccessibleName = TopMost ? "取消置顶" : "置顶"; tips.SetToolTip(pin, pin.AccessibleName); pin.Invalidate(); SaveWindowState(); };
        newButton.Click += delegate { NewNote(); };
        hideButton.Click += delegate { MinimizeNote(); };
        closeButton.Click += delegate { TryHide(); };
        // The verified native prototype uses Windows caption dragging. Keep
        // this path and leave the user's DragFullWindows setting untouched.
        MouseEventHandler drag = delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xa1, new IntPtr(2), IntPtr.Zero); } };
        top.MouseDown += drag; brand.MouseDown += drag;
        Point titlePress = Point.Empty;
        dateLabel.MouseDown += delegate(object sender, MouseEventArgs e) { titlePress = e.Location; };
        dateLabel.MouseMove += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && (Math.Abs(e.X - titlePress.X) >= SystemInformation.DragSize.Width || Math.Abs(e.Y - titlePress.Y) >= SystemInformation.DragSize.Height)) drag(sender, e); };
        editor.MouseEnter += delegate { CancelPreview(); RevealForPointer(); };
        editor.MouseLeave += delegate { ScheduleLeave(); };
        top.MouseEnter += delegate { leaveTimer.Stop(); }; top.MouseLeave += delegate { ScheduleLeave(); };
        MouseLeave += delegate { ScheduleLeave(); };
        MouseMove += delegate(object sender, MouseEventArgs e) { if (body.Bounds.Contains(e.Location)) RevealForPointer(); };
        dateLabel.Cursor = Cursors.Hand;
        tips.SetToolTip(dateLabel, "点击切换便签");
        dateLabel.Click += delegate { ShowRecentMenu(); };
        Move += delegate { EraseImageOutline(); if (!movingWindow && tabs != null && tabs.Visible) PositionTabs(); }; Resize += delegate { CancelImageDrag(); LayoutChrome(); if (!movingWindow && tabs != null && tabs.Visible) { UpdateTabs(); PositionTabs(); } };
        VisibleChanged += delegate { if (!Visible) { CancelPreview(); HideTabs(); } };
        Deactivate += delegate { CancelImageDrag(); if (tabs == null || !tabs.Bounds.Contains(Cursor.Position)) { CancelPreview(); HideTabs(); UpdateChromeForPointer(Cursor.Position); } };
        hoverTimer.Tick += delegate { hoverTimer.Stop(); StartPreview(); };
        leaveTimer.Tick += delegate {
            leaveTimer.Stop();
            UpdateChromeForPointer(Cursor.Position);
            if (tabs != null && tabs.Visible && tabs.Bounds.Contains(Cursor.Position)) return;
            CancelPreview();
            if (!Visible || WindowState == FormWindowState.Minimized || !Bounds.Contains(Cursor.Position)) HideTabs();
        };
        editor.TextChanged += delegate {
            if (loading) return;
            editVersion++;
            dirty = true; failedText = null; ScheduleSave(); scroll.Invalidate();
        };
        editor.SelectionChanged += delegate { if (imageDragCp >= 0 && (editor.SelectionStart != imageDragCp || editor.SelectionLength != 1)) CancelImageDrag(); RefreshFormatButtons(); };
        editor.ReadOnlyChanged += delegate { formatBar.Enabled = !editor.ReadOnly && !IsComposing; };
        editor.CompositionChanged = delegate(bool active) { composing = active; formatBar.Enabled = !editor.ReadOnly && !active; if (active) saveTimer.Stop(); else if (dirty) ScheduleSave(); else if (completedImage != null) saveTimer.Start(); };
        editor.HandleKey = delegate(Keys keys) { var e = new KeyEventArgs(keys); EditorKeyDown(editor, e); return e.Handled; };
        editor.UserPaste = PasteFromClipboard; editor.UserCopyOrCut = CopyOrCut;
        editor.UserContextMenu = ShowEditorMenu; editor.UndoOrRedo = delegate {
            CancelPendingImages();
            if (!loading && !editor.ReadOnly) { editVersion++; dirty = true; failedText = null; ScheduleSave(); }
        };
        editor.InsertNewline = InsertEditorNewline;
        editor.DragEnter += EditorDragOver; editor.DragOver += EditorDragOver; editor.DragDrop += Drop;
        saveTimer.Tick += delegate {
            if (completedImage != null) { if (IsComposing) return; CompleteImageWork(); }
            if (!IsComposing && dirty && (System.Diagnostics.Stopwatch.GetTimestamp() - lastEditStamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 500) { saveTimer.Stop(); Flush(); }
            else if (!dirty) saveTimer.Stop();
        };
        status.LinkClicked += delegate { ShowSaveMenu(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!closeAllowed) { e.Cancel = true; TryHide(); } };
        ResizeEnd += delegate { SaveWindowState(); };
        Shown += delegate { editor.Focus(); };
        ObserveHover(this); LayoutChrome(); LayoutHeader(); LayoutEditors();
    }
    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        // Persisted bounds are physical pixels, after initial WinForms scaling.
        RestoreWindowState();
    }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e); RefreshChromeScale();
        // OnLoad can move to a monitor with a different DPI. Its suggested
        // bounds are then scaled by Windows; reapply the saved physical size
        // only after that initial transition, before presenting the note.
        RestoreWindowState(); RefreshChromeScale();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); RefreshChromeScale(); }
    internal static Size MinimumForWorkArea(int dpi, Size work) {
        return new Size(Math.Min((int)Math.Round(280 * dpi / 96f), work.Width), Math.Min((int)Math.Round(240 * dpi / 96f), work.Height));
    }
    void RefreshChromeScale() {
        if (top == null || formatBar == null || IsDisposed) return;
        Rectangle work = Screen.FromControl(this).WorkingArea;
        MinimumSize = MinimumForWorkArea(DeviceDpi, work.Size);
        LayoutChrome(); LayoutHeader(); LayoutFormatBar();
        if (Visible && !movingWindow) Bounds = FitWindowBounds(Bounds, work, MinimumSize);
    }
    public void Toggle() {
        if (IsDisposed) return;
        if (noteMinimized) { noteMinimized = false; SetChromeVisible(true); Show(); Activate(); editor.Focus(); return; }
        if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; Show(); Activate(); editor.Focus(); return; }
        if (Visible) { TryHide(); return; }
        try {
            hideRequested = false;
            if (note == null) LoadLastNote();
            else if (!dirty && !workerBusy) RequestNote(Path.GetFileName(notePath), false);
            Show(); Activate(); editor.Focus(); UpdateChromeForPointer(Cursor.Position);
        } catch (Exception error) { if (note == null) editor.ReadOnly = true; SetStatus("无法载入速记：" + error.Message, true); Show(); Activate(); }
    }
    void MinimizeNote() {
        if (IsComposing) return;
        CancelPreview(); CancelImageDrag(); RememberView(); SaveWindowState(); HideTabs();
        noteMinimized = true; Hide(); Flush();
        if (Minimized != null) Minimized();
    }
    public bool FlushAndClose() { return FlushAndClose(null); }
    public bool FlushAndClose(Action onReady) {
        if (IsDisposed) return true;
        if (IsComposing) { SetStatus("请先完成输入法中的文字，再退出。", true); return false; }
        CancelPreview(); HideTabs(); closeRequested = true; hideRequested = false;
        if (closeContinuation == null) closeContinuation = onReady;
        if (Flush()) { FinishClose(false); return true; }
        return false;
    }
    void TryHide() {
        if (IsComposing) return;
        CancelPreview(); HideTabs();
        hideRequested = true;
        if (Flush()) { hideRequested = false; SaveWindowState(); Hide(); }
    }
    void LoadToday() {
        RequestNote(null, false);
    }
    void RequestNote(string file, bool create) {
        CancelImageDrag();
        if (IsComposing || loadRequested || reloadRequested || (editor.ReadOnly && workerBusy)) return;
        if (queuedImageCount != 0) { SetStatus("图片正在插入，请稍后切换便签。", false); return; }
        CancelPreview(); RememberView();
        if (dirty && !CaptureSave()) return;
        if (note != null && note.Revision == "missing" && !dirty) recentFiles.Remove(Path.GetFileName(notePath));
        requestedFile = file; createRequested = create;
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
            RestoreView();
        } catch { editor.ReadOnly = true; throw; }
        finally { loading = false; }
        RefreshFormatButtons();
        TouchRecent(Path.GetFileName(next.Path)); UpdateTitle(); UpdateTabs(); scroll.Invalidate();
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
    void CacheNoteMetadata() {
        if (note == null) return;
        notePath = note.Path; noteDate = note.Date; recoveryPath = note.HasRecovery ? note.RecoveryPath : null;
        if (note.Revision != "missing" || note.HasRecovery) lastSavedFile = Path.GetFileName(note.Path);
    }
    void DrainWork() {
        if (workerBusy || IsDisposed) return;
        if (imageQueue.Count > 0) { StartImageWork(imageQueue.Dequeue()); return; }
        if (pendingText == null && deleteRequested != null) { StartDeleteWork(); return; }
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
        string file = requestedFile; bool create = createRequested, fallback = restoreLast;
        if (!save) { createRequested = false; restoreLast = false; }
        string priorArchiveSource = archivedRecoverySource, priorArchiveText = archivedRecoveryText;
        IntPtr dispatcherHandle = Handle;
        workerBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null; bool conflict = false; string archive = null;
            QuickNoteDocument result = document; string loadedText = null; NativeNotePrepared prepared = null; string[] discovered = null;
            try {
                if (beforeStorageWork != null) beforeStorageWork();
                if (save) {
                    if (text.Length != 0 || document.Revision != "missing") { store.SaveRecovery(document, text); store.Save(document, text); }
                }
                else {
                    if (reload) archive = store.ArchiveRecovery(document, text);
                    result = reload ? store.LoadFile(Path.GetFileName(document.Path)) : create ? store.NewNote() : file == null ? store.Load(date) : store.LoadFile(file);
                    if (!reload && !create && file != null && result.Revision == "missing" && !result.HasRecovery) {
                        if (fallback) result = store.Load(date);
                        else throw new FileNotFoundException("此便签已被移动或删除，当前内容保持不变。", file);
                    }
                    loadedText = result.Text;
                    conflict = result.HasRecovery && !string.Equals(result.RecoveryRevision, result.Revision, StringComparison.Ordinal);
                    if (!reload && result.HasRecovery && !conflict) loadedText = result.RecoveryText;
                    if (!reload && conflict && (priorArchiveSource != result.Path || priorArchiveText != result.RecoveryText)) archive = store.ArchiveRecovery(result, result.RecoveryText);
                    prepared = NativeNoteDocument.PrepareLoad(loadedText, ResolvePreview);
                    discovered = store.RecentFiles();
                }
            } catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                workerBusy = false; if (IsDisposed) return;
                // Archiving can succeed before import fails. Keep that route
                // without rebinding visible text to the newly loaded revision.
                if (archive != null) { archivedRecovery = archive; archivedRecoverySource = reload ? document.Path : result.Path; archivedRecoveryText = reload ? text : result.RecoveryText; }
                if (failure != null) {
                    deleteRequested = null;
                    if (!save) { LastLoadError = failure; CacheLoadFailureRecovery(result, archive); }
                    failedText = save ? text : reload ? text : failedText;
                    hideRequested = closeRequested = false; closeContinuation = null;
                    if (save) { CacheNoteMetadata(); loadRequested = false; if (!reloadRequested) editor.ReadOnly = false; }
                    else editor.ReadOnly = note == null || reload || file == null || String.Equals(file, Path.GetFileName(notePath), StringComparison.OrdinalIgnoreCase);
                    SetStatus(failure is QuickNoteConflictException ? "外部内容已变化；草稿保留，点击处理" : "保存或载入失败，原文件未改动；点击查看恢复方式", true);
                    tips.SetToolTip(status, failure.Message);
                } else if (save) {
                    CacheNoteMetadata(); savedVersion = Math.Max(savedVersion, version); dirty = editVersion != savedVersion;
                    if (document.Revision == "missing" && text.Length == 0) { recentFiles.Remove(Path.GetFileName(document.Path)); UpdateTabs(); }
                    if (!dirty) { failedText = null; SetSavedStatus(); }
                } else {
                    try {
                        if (discovered != null) {
                            recentFiles.RemoveAll(delegate(string name) { return Array.IndexOf(discovered, name) < 0; });
                            foreach (string name in discovered) if (!recentFiles.Contains(name)) recentFiles.Add(name);
                        }
                        ApplyLoaded(result, loadedText, prepared, conflict, reload);
                    }
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
    void SetStatus(string message, bool error, bool show = true) {
        status.Text = message; status.LinkColor = error ? Color.FromArgb(155, 73, 33) : VerbitUi.Accent;
        statusShown = show; statusError = error && show;
        if (statusError) SetChromeVisible(true);
        LayoutChrome();
        location.ForeColor = error ? Color.FromArgb(155, 73, 33) : VerbitUi.Ink; location.Invalidate();
        status.AccessibleName = message;
        string tip = !error && notePath != null ? notePath : message;
        if (!error && nativeDocument.TextImageFallback) tip += "\n图片较多，部分图片以 Markdown 引用显示；原图已保留。";
        tips.SetToolTip(status, tip);
        tips.SetToolTip(location, message + (notePath == null ? "" : "\n" + notePath));
    }
    void SetSavedStatus() {
        string folder = Path.GetFileName(store.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        SetStatus(folder + " / " + Path.GetFileName(notePath), false, false);
        tips.SetToolTip(location, "保存位置与恢复选项\n" + notePath);
    }
    void OpenFolder() {
        try {
            if (!Directory.Exists(store.FolderPath)) { SetStatus("输入内容后，会自动创建速记文件夹。", false); return; }
            System.Diagnostics.Process.Start("explorer.exe", "\"" + store.FolderPath.Replace("\"", "") + "\"");
        } catch (Exception error) { SetStatus("无法打开文件夹：" + error.Message, true); }
    }
    void ShowSaveMenu() {
        var menu = CreateTransientMenu();
        var hoverOption = new ToolStripMenuItem("悬停标签预览内容") { Checked = previewOnHover, CheckOnClick = true };
        hoverOption.Click += delegate { previewOnHover = hoverOption.Checked; if (!previewOnHover) CancelPreview(); SaveWindowState(); };
        menu.Items.Add(hoverOption);
        menu.Items.Add("打开速记文件夹", null, delegate { OpenFolder(); });
        menu.Items.Add("复制当前内容", null, delegate { try { string text = ReadMarkdown(); if (text.Length > 0) Clipboard.SetText(text); } catch (Exception error) { SetStatus("无法复制：" + error.Message, true); } });
        string draftPath = loadFailureRecoveryPath ?? archivedRecovery ?? recoveryPath;
        if (!string.IsNullOrEmpty(draftPath)) menu.Items.Add("打开草稿所在文件夹", null, delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + draftPath.Replace("\"", "") + "\""); } catch (Exception error) { tips.SetToolTip(status, error.Message); } });
        if (note != null && failedText != null) menu.Items.Add("保留草稿并载入磁盘版本", null, delegate { ReloadDiskKeepingDraft(); });
        menu.Show(location, new Point(location.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }
    void ShowEditorMenu() {
        var menu = CreateTransientMenu();
        if (!editor.ReadOnly && !IsComposing && !nativeDocument.SelectedImageBounds().IsEmpty) {
            menu.Items.Add("查看原图", null, delegate { OpenSelectedImage(); });
            menu.Items.Add("图片适合窗口", null, delegate { Rectangle picture = nativeDocument.SelectedImageBounds(); if (!picture.IsEmpty) ScaleImage(Math.Max(16, viewport.Width - Math.Max(0, picture.Left) - 10) / (double)picture.Width); });
            var resize = new ToolStripMenuItem("调整图片大小");
            resize.DropDownItems.Add("等比例放大", null, delegate { ScaleImage(1.25); });
            resize.DropDownItems.Add("等比例缩小", null, delegate { ScaleImage(.8); });
            resize.DropDownItems.Add("恢复原图比例", null, delegate { Rectangle picture = nativeDocument.SelectedImageBounds(); if (!picture.IsEmpty) ResizeImageTo((int)Math.Round(picture.Width * 96.0 / DeviceDpi / editor.ZoomFactor)); });
            menu.Items.Add(resize);
            menu.Items.Add(new ToolStripSeparator());
        }
        if (!editor.ReadOnly && editor.CanUndo) menu.Items.Add("撤销", null, delegate { CancelPendingImages(); editor.Undo(); });
        if (!editor.ReadOnly && editor.CanRedo) menu.Items.Add("重做", null, delegate { CancelPendingImages(); editor.Redo(); });
        if (!editor.ReadOnly && (editor.CanUndo || editor.CanRedo)) menu.Items.Add(new ToolStripSeparator());
        if (!editor.ReadOnly) menu.Items.Add("剪切", null, delegate { CopyOrCut(true); }).Enabled = editor.SelectionLength > 0;
        menu.Items.Add("复制", null, delegate { CopyOrCut(false); }).Enabled = editor.SelectionLength > 0;
        if (!editor.ReadOnly) menu.Items.Add("粘贴", null, delegate { PasteFromClipboard(); });
        menu.Items.Add("全选", null, delegate { editor.SelectAll(); });
        menu.Show(editor, editor.PointToClient(Cursor.Position));
    }
    VerbitMenu CreateTransientMenu() {
        SetChromeVisible(true); leaveTimer.Stop();
        var menu = new VerbitMenu(transientMenus);
        menu.Closed += delegate {
            // WinForms still uses the menu handle after Closed returns.
            // The container owns cleanup if the host closes before this runs.
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!menu.IsDisposed) menu.Dispose(); ScheduleLeave(); }); }
            catch (InvalidOperationException) { }
        };
        return menu;
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
        if (e.KeyCode == Keys.Escape && !IsComposing) { e.Handled = true; if (imageDragCp >= 0) CancelImageDrag(); else TryHide(); return; }
        if (e.KeyData == (Keys.Control | Keys.N)) { e.Handled = true; NewNote(); return; }
        string format = e.KeyData == (Keys.Control | Keys.B) ? "bold" : e.KeyData == (Keys.Control | Keys.I) ? "italic" : e.KeyData == (Keys.Control | Keys.U) ? "underline" : e.KeyData == (Keys.Control | Keys.Shift | Keys.X) ? "strike" : e.KeyData == (Keys.Control | Keys.Shift | Keys.L) ? "bullet" : null;
        if (format != null) { e.Handled = true; ApplyFormat(format); return; }
        if (e.Control && (e.KeyCode == Keys.B || e.KeyCode == Keys.I || e.KeyCode == Keys.U || e.KeyCode == Keys.E || e.KeyCode == Keys.L || e.KeyCode == Keys.R)) e.Handled = true;
        if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; Flush(); }
    }
    void LayoutFormatBar() {
        // Bounds are already WinForms-scaled; do not apply DPI a second time.
        int height = formatBar.ClientSize.Height, inset = 10 * height / 38, top = 4 * height / 38;
        int count = formatBar.Controls.Count;
        int width = Math.Min(height - 2 * top, Math.Max(1, (formatBar.ClientSize.Width - 2 * inset) / Math.Max(1, count)));
        int span = Math.Max(0, formatBar.ClientSize.Width - 2 * inset - width);
        for (int i = 0; i < count; i++) formatBar.Controls[i].Bounds = new Rectangle(inset + (count < 2 ? 0 : (int)Math.Round((double)span * i / (count - 1))), top, width, height - 2 * top);
    }
    void ShowFormatTip(Control button) {
        string text = tips.GetToolTip(button);
        Rectangle area = Screen.FromControl(button).WorkingArea;
        Size content;
        using (Graphics graphics = button.CreateGraphics())
            content = TextRenderer.MeasureText(graphics, text, SystemFonts.StatusFont, new Size(Math.Max(1, area.Width / 2), int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        // Measure every line, including long paths. The native tooltip adds
        // themed padding; keep that and a gap outside the whole hover target.
        int gap = 8 * DeviceDpi / 96, height = content.Height + 12 * DeviceDpi / 96;
        int y = button.PointToScreen(Point.Empty).Y - height - gap < area.Top ? button.Height + gap : -height - gap;
        tips.Show(text, button, new Point(0, y), 10000);
    }
    void InsertEditorNewline() {
        if (editor.ReadOnly || IsComposing) return;
        try {
            // Use native line boundaries: wrapping is visual, not a new item.
            // Only Enter inspects the line; ordinary typing stays native.
            string raw = nativeDocument.RawText(); int start = editor.SelectionStart, end = start + editor.SelectionLength;
            int first = start; while (first > 0 && !IsLineBreak(raw[first - 1])) first--;
            int last = end; while (last < raw.Length && !IsLineBreak(raw[last])) last++;
            bool bullet = first + 2 <= raw.Length && raw.Substring(first, 2) == "\u2022 " && start >= first + 2 && (ModifierKeys & Keys.Shift) == 0;
            for (int cp = start; bullet && cp < end; cp++) if (IsLineBreak(raw[cp])) bullet = false;
            if (bullet && raw.Substring(first + 2, last - first - 2).Trim().Length == 0) {
                nativeDocument.EditCollection(delegate { editor.Select(first, last - first); editor.SelectedText = ""; });
            } else nativeDocument.InsertText(newline + (bullet ? "\u2022 " : ""));
        } catch (Exception error) { SetStatus("无法插入换行：" + error.Message, true); }
    }
    static bool IsLineBreak(char character) { return character == '\r' || character == '\n' || character == '\v'; }
    void RefreshFormatButtons() {
        if (IsDisposed || Disposing || loading || editor.ReadOnly || nativeDocument == null) return;
        int style = nativeDocument.SelectionStyle;
        foreach (QuickNoteFormatButton button in formatBar.Controls) {
            int bit = button.Format == "bold" ? 1 : button.Format == "italic" ? 2 : button.Format == "underline" ? 4 : button.Format == "strike" ? 8 : 0;
            bool active = bit != 0 && (style & bit) != 0;
            if (button.Active != active) { button.Active = active; button.Invalidate(); }
        }
    }
    void ApplyFormat(string format) {
        if (editor.ReadOnly || IsComposing || note == null) return;
        try {
            if (format == "image") {
                using (var dialog = new OpenFileDialog { Title = "插入图片", Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp", Multiselect = false, CheckFileExists = true }) {
                    if (dialog.ShowDialog(this) == DialogResult.OK) InsertImageFile(dialog.FileName);
                }
                editor.Focus(); return;
            }
            if (format == "bullet") {
                string raw = nativeDocument.RawText(); int start = editor.SelectionStart, end = start + editor.SelectionLength;
                int first = start; while (first > 0 && raw[first - 1] != '\r' && raw[first - 1] != '\v' && raw[first - 1] != '\n') first--;
                int last = end;
                if (last > start && (raw[last - 1] == '\r' || raw[last - 1] == '\v' || raw[last - 1] == '\n')) last--;
                while (last < raw.Length && raw[last] != '\r' && raw[last] != '\v' && raw[last] != '\n') last++;
                var lines = new List<int>(); lines.Add(first);
                for (int cp = first; cp < last; cp++) if (raw[cp] == '\r' || raw[cp] == '\v' || raw[cp] == '\n') lines.Add(cp + 1);
                bool remove = true;
                foreach (int cp in lines) if (cp + 2 > raw.Length || raw.Substring(cp, 2) != "\u2022 ") { remove = false; break; }
                if (!remove && (long)raw.Length + 2L * lines.Count > NativeNoteDocument.MaxTextLength) throw new IOException("正文已达速记长度上限。");
                nativeDocument.EditCollection(delegate {
                    for (int index = lines.Count - 1; index >= 0; index--) { editor.Select(lines[index], remove ? 2 : 0); editor.SelectedText = remove ? "" : "\u2022 "; }
                });
                if (start == end) editor.Select(Math.Max(first, start + (remove ? -Math.Min(2, start - first) : 2)), 0);
                else editor.Select(first, Math.Max(0, last - first + (remove ? -2 : 2) * lines.Count));
            } else {
                int bit = format == "bold" ? 1 : format == "italic" ? 2 : format == "underline" ? 4 : format == "strike" ? 8 : 0;
                if (bit == 0) return;
                nativeDocument.ToggleStyle(bit);
            }
            // RichEdit font-only changes do not reliably raise TextChanged.
            editVersion++; dirty = true; failedText = null; ScheduleSave();
            RefreshFormatButtons(); editor.Focus();
        } catch (Exception error) { SetStatus("无法应用格式：" + error.Message, true); }
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
        RememberView();
        try {
            Directory.CreateDirectory(stateRoot);
            string value = Bounds.X + "," + Bounds.Y + "," + Bounds.Width + "," + Bounds.Height + "," + (TopMost ? "1" : "0");
            if (Visible && WindowState == FormWindowState.Normal) File.WriteAllText(Path.Combine(stateRoot, "quick-note-window.txt"), value, new UTF8Encoding(false));
            var state = new NavigationState { Last = lastSavedFile, Preview = previewOnHover, Views = views };
            string target = NavigationPath, temporary = target + ".tmp";
            File.WriteAllText(temporary, new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state), new UTF8Encoding(false));
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        } catch { }
    }
    void RestoreWindowState() {
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Rectangle wanted = new Rectangle(area.Right - Width - 24, area.Top + 60, Width, Height);
        bool pinned = TopMost;
        try {
            string[] parts = File.ReadAllText(Path.Combine(stateRoot, "quick-note-window.txt")).Split(','); int x, y, width, height;
            if (parts.Length == 5 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) && int.TryParse(parts[2], out width) && int.TryParse(parts[3], out height)) {
                wanted = new Rectangle(x, y, width, height); area = Screen.FromRectangle(wanted).WorkingArea; pinned = parts[4] != "0";
            }
        } catch { }
        // The first window must use the same screen constraint as a restored
        // window; otherwise a short working area moves it only after reopening.
        Bounds = FitWindowBounds(wanted, area, MinimumSize);
        TopMost = pinned; pin.Pinned = TopMost; pin.AccessibleName = TopMost ? "取消置顶" : "置顶"; tips.SetToolTip(pin, pin.AccessibleName);
    }
    internal static Rectangle FitWindowBounds(Rectangle wanted, Rectangle area, Size minimum) {
        wanted.Width = Math.Min(Math.Max(minimum.Width, wanted.Width), area.Width);
        wanted.Height = Math.Min(Math.Max(minimum.Height, wanted.Height), area.Height);
        wanted.X = Math.Max(area.Left, Math.Min(wanted.X, area.Right - wanted.Width));
        wanted.Y = Math.Max(area.Top, Math.Min(wanted.Y, area.Bottom - wanted.Height));
        return wanted;
    }
    protected override void Dispose(bool disposing) {
        if (disposing) {
            CancelImageDrag(); if (previewCaretHidden) { ShowCaret(editor.Handle); previewCaretHidden = false; }
            editor.ImagePointer = null; editor.ImagePaint = null; editor.PointerMoved = null; editor.BeforeInput = null;
            saveTimer.Dispose(); hoverTimer.Dispose(); leaveTimer.Dispose(); tips.Dispose(); transientMenus.Dispose();
            if (tabs != null) tabs.Dispose();
            if (previewDocument != null) previewDocument.Dispose();
            if (brand.Image != null) { brand.Image.Dispose(); brand.Image = null; }
            while (imageQueue.Count > 0) ReleaseImage(imageQueue.Dequeue());
            if (completedImage != null) { ReleaseImage(completedImage); completedImage = null; }
            ClearImageAnchors(); nativeDocument.Dispose();
            if (uiFont != null) { uiFont.Dispose(); uiFont = null; }
            if (editorFont != null) { editorFont.Dispose(); editorFont = null; }
        }
        base.Dispose(disposing);
    }

    string NavigationPath {
        get {
            string key;
            using (var hash = System.Security.Cryptography.SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(store.FolderPath.ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            return Path.Combine(stateRoot, "quick-note-files-" + key + ".json");
        }
    }
    void LoadLastNote() {
        string last = null;
        if (!navigationRestored) {
            navigationRestored = true;
            try {
                string path = NavigationPath;
                if (new FileInfo(path).Length <= 65536) {
                    var state = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<NavigationState>(File.ReadAllText(path));
                    if (state != null) {
                        previewOnHover = state.Preview; last = state.Last; lastSavedFile = last;
                        if (state.Views != null) foreach (var entry in state.Views) {
                            if (views.Count == 50) break;
                            if (entry.Value != null && entry.Key == Path.GetFileName(entry.Key)) views[entry.Key] = entry.Value;
                        }
                    }
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        if (last != null && (last != Path.GetFileName(last) || !last.EndsWith(".md", StringComparison.OrdinalIgnoreCase))) last = null;
        restoreLast = true; RequestNote(last, false);
    }
    void NewNote() { RequestNote(null, true); }
    void RememberView() {
        if (notePath == null || loading) return;
        Point position = QuickNoteScrollBar.Position(editor);
        string name = Path.GetFileName(notePath);
        if (!views.ContainsKey(name) && views.Count >= 50) views.Remove(new List<string>(views.Keys)[0]);
        views[name] = new NoteView { Start = editor.SelectionStart, Length = editor.SelectionLength, X = position.X, Y = position.Y };
    }
    void RestoreView() {
        NoteView view;
        if (notePath == null || !views.TryGetValue(Path.GetFileName(notePath), out view)) return;
        int start = Math.Min(editor.TextLength, Math.Max(0, view.Start));
        editor.Select(start, Math.Min(editor.TextLength - start, Math.Max(0, view.Length)));
        QuickNoteScrollBar.SetPosition(editor, new Point(Math.Max(0, view.X), Math.Max(0, view.Y)));
    }
    void TouchRecent(string name) {
        if (recentFiles.Exists(delegate(string value) { return String.Equals(value, name, StringComparison.OrdinalIgnoreCase); })) return;
        recentFiles.Insert(0, name); if (recentFiles.Count > 50) recentFiles.RemoveRange(50, recentFiles.Count - 50);
    }
    void UpdateTitle() {
        string file = previewVisible ? previewFile : notePath == null ? null : Path.GetFileName(notePath);
        dateLabel.Text = (file ?? "新便签") + (previewVisible ? " · 预览" : "");
        Text = file ?? "速记"; tips.SetToolTip(dateLabel, dateLabel.Text); LayoutHeader();
    }
    void LayoutHeader() {
        if (closeButton == null || brand == null) return;
        int h = top.Height, unit = Math.Max(24, h - 8), y = (h - unit) / 2, pad = Math.Max(4, h / 8);
        newButton.Bounds = new Rectangle(pad, y, unit, unit);
        closeButton.Bounds = new Rectangle(top.Width - pad - unit, y, unit, unit);
        hideButton.Bounds = new Rectangle(closeButton.Left - unit, y, unit, unit);
        pin.Bounds = new Rectangle(hideButton.Left - unit, y, unit, unit);
        int left = newButton.Right + pad, available = Math.Max(0, pin.Left - pad - left), icon = h / 2;
        int textWidth = Math.Min(Math.Max(0, available - icon - pad), TextRenderer.MeasureText(dateLabel.Text, dateLabel.Font).Width);
        int x = left + Math.Max(0, (available - icon - pad - textWidth) / 2);
        brand.Bounds = new Rectangle(x, (h - icon) / 2, icon, icon);
        dateLabel.Bounds = new Rectangle(x + icon + pad, y, textWidth, unit);
    }
    void LayoutChrome() {
        if (Disposing || IsDisposed || body == null || formatBar == null) return;
        Rectangle area = DisplayRectangle;
        // Window state stores physical pixels; toolbar metrics stay in logical
        // pixels after initial scaling and every monitor transition.
        top.Height = (int)Math.Round(40 * DeviceDpi / 96f);
        formatBar.Height = (int)Math.Round(38 * DeviceDpi / 96f);
        status.Height = (int)Math.Round(32 * DeviceDpi / 96f);
        top.Bounds = new Rectangle(area.Left, area.Top, area.Width, top.Height);
        formatBar.Bounds = new Rectangle(area.Left, area.Bottom - formatBar.Height, area.Width, formatBar.Height);
        status.Bounds = new Rectangle(area.Left, formatBar.Top - status.Height, area.Width, status.Height);
        body.Bounds = Rectangle.FromLTRB(area.Left, top.Bottom, area.Right, statusShown ? status.Top : formatBar.Top);
        top.Visible = formatBar.Visible = chromeVisible;
        status.Visible = chromeVisible && statusShown;
        UpdateChromeClip();
    }
    void UpdateChromeClip() {
        Region old = Region;
        Region = chromeVisible ? null : new Region(body.Bounds);
        if (old != null) old.Dispose();
        Invalidate();
    }
    void SetChromeVisible(bool visible) {
        if (chromeVisible == visible) return;
        if (!visible) { tips.Hide(status); foreach (Control c in top.Controls) tips.Hide(c); foreach (Control c in formatBar.Controls) tips.Hide(c); }
        // One clip change, no editor resize/scroll reset or animation loop.
        chromeVisible = visible;
        top.Visible = formatBar.Visible = visible; status.Visible = visible && statusShown;
        if (!visible) HideTabs();
        UpdateChromeClip();
    }
    void UpdateChromeForPointer(Point screen) {
        bool held = statusError || imageDragCp >= 0 || editor.Capture;
        foreach (System.ComponentModel.Component component in transientMenus.Components) {
            var menu = component as ContextMenuStrip; if (menu != null && menu.Visible) held = true;
        }
        SetChromeVisible(held || Visible && WindowState == FormWindowState.Normal &&
            ((chromeVisible ? ClientRectangle : body.Bounds).Contains(PointToClient(screen)) || tabs != null && tabs.Visible && tabs.Bounds.Contains(screen)));
    }
    void LayoutEditors() {
        if (viewport == null) return;
        // Clip the native scrollbar offscreen. RichEdit keeps its native wheel,
        // IME and scrolling range; only the narrow external thumb is painted.
        Rectangle bounds = new Rectangle(0, 0, viewport.Width + GetSystemMetricsForDpi(2, (uint)DeviceDpi), viewport.Height);
        editor.Bounds = bounds; if (previewEditor != null) previewEditor.Bounds = bounds;
        if (scroll != null) scroll.Invalidate();
    }
    void ScaleImage(double scale) {
        Rectangle bounds = nativeDocument.SelectedImageBounds();
        if (!bounds.IsEmpty) {
            double ratio = nativeDocument.SelectedImageDisplayRatio;
            int maximum = Math.Max(16, Math.Min(NativeNoteDocument.MaxImageLogicalSize, (int)(NativeNoteDocument.MaxImageLogicalSize * ratio)));
            int width = Math.Max(16, Math.Min(maximum, (int)Math.Round(bounds.Width * 96.0 / DeviceDpi / editor.ZoomFactor * scale)));
            ResizeImageDimensions(width, Math.Max(1, (int)Math.Round(width / ratio)));
        }
    }
    void ResizeImageTo(int width) {
        ResizeImageDimensions(width, 0);
    }
    void ResizeImageDimensions(int width, int height) {
        if (IsComposing || queuedImageCount != 0 || editor.ReadOnly) return;
        try {
            Rectangle bounds = nativeDocument.SelectedImageBounds(); if (bounds.IsEmpty) return;
            int maximum = height > 0 ? NativeNoteDocument.MaxImageLogicalSize : Math.Max(16, Math.Min(NativeNoteDocument.MaxImageLogicalSize, (int)(NativeNoteDocument.MaxImageLogicalSize * nativeDocument.SelectedImageAspectRatio)));
            nativeDocument.ResizeSelectedImage(Math.Max(16, Math.Min(maximum, width)), Math.Max(0, Math.Min(NativeNoteDocument.MaxImageLogicalSize, height)));
            editor.Invalidate(); editor.Focus();
        } catch (Exception error) { SetStatus("无法缩放图片，原图仍在：" + error.Message, true); }
    }
    void EraseImageOutline() { if (imageDragCp >= 0) editor.Invalidate(); }
    void CancelImageDrag() {
        if (imageDragCp < 0) return;
        imageDragCp = -1; editor.Capture = false; editor.Invalidate(); editor.Cursor = Cursors.IBeam;
        if (!Disposing && !IsDisposed) ScheduleLeave();
    }
    bool HandleImagePointer(int message, Point point) {
        if (message == 0x204 || message == 0x203) {
            CancelImageDrag();
            if (SelectImageAt(point) && message == 0x203) { OpenSelectedImage(); return true; }
            return false;
        }
        if (imageDragCp >= 0) {
            if (message == 0x215 || message == 0x100) { CancelImageDrag(); return message == 0x100; }
            if (message == 0x200 || message == 0x202) {
                // Wheel scrolling, zoom and reflow invalidate the captured
                // handle coordinates; never commit using stale geometry.
                if (nativeDocument.SelectedImageBounds() != imageDragStart) { CancelImageDrag(); return true; }
                int column = imageDragHandle % 3, row = imageDragHandle / 3;
                double sx = column == 0 ? (imageDragStart.Right - point.X) / (double)imageDragStart.Width : (point.X - imageDragStart.Left) / (double)imageDragStart.Width;
                double sy = row == 0 ? (imageDragStart.Bottom - point.Y) / (double)imageDragStart.Height : (point.Y - imageDragStart.Top) / (double)imageDragStart.Height;
                double factor = column == 1 ? sy : row == 1 ? sx : Math.Abs(sx - 1) > Math.Abs(sy - 1) ? sx : sy;
                double pixelsPerUnit = DeviceDpi * editor.ZoomFactor / 96.0;
                double ratio = nativeDocument.SelectedImageDisplayRatio;
                bool free = row == 1 || column == 1;
                int maximum = Math.Max(16, Math.Min(NativeNoteDocument.MaxImageLogicalSize, (int)(NativeNoteDocument.MaxImageLogicalSize * ratio)));
                double minimum = 16 * pixelsPerUnit;
                double limit = Math.Max(minimum, Math.Min(Math.Floor(maximum * pixelsPerUnit), viewport.Width - imageDragStart.Left - 10));
                double freeLimit = Math.Max(minimum, Math.Min(NativeNoteDocument.MaxImageLogicalSize * pixelsPerUnit, viewport.Width - imageDragStart.Left - 10));
                double widthFactor = free ? (row == 1 ? sx : 1) : factor;
                int width = (int)Math.Round(Math.Max(minimum, Math.Min(free ? freeLimit : limit, imageDragStart.Width * widthFactor)));
                int height = free ? (int)Math.Round(Math.Max(pixelsPerUnit, Math.Min(NativeNoteDocument.MaxImageLogicalSize * pixelsPerUnit, imageDragStart.Height * (column == 1 ? sy : 1)))) : Math.Max(1, (int)Math.Round(width / ratio));
                imageDragOutline = new Rectangle(imageDragStart.Location, new Size(width, height));
                editor.Invalidate();
                if (message == 0x202) {
                    int cp = imageDragCp, logical = (int)Math.Round(width * 96.0 / DeviceDpi / editor.ZoomFactor);
                    CancelImageDrag();
                    if (editor.SelectionStart == cp && editor.SelectionLength == 1) ResizeImageDimensions(logical, Math.Max(1, (int)Math.Round(height / pixelsPerUnit)));
                }
                return true;
            }
        }
        if (editor.ReadOnly || IsComposing || queuedImageCount != 0) return false;
        Rectangle bounds = nativeDocument.SelectedImageBounds();
        int hit = -1, radius = Math.Max(6, 6 * DeviceDpi / 96);
        if (!bounds.IsEmpty) for (int row = 0; row < 3; row++) for (int col = 0; col < 3; col++) {
            if (row == 1 && col == 1) continue;
            int x = bounds.Left + bounds.Width * col / 2, y = bounds.Top + bounds.Height * row / 2;
            if (Math.Abs(point.X - x) <= radius && Math.Abs(point.Y - y) <= radius) hit = row * 3 + col;
        }
        if (message == 0x200) {
            Cursor cursor = hit < 0 ? Cursors.IBeam : hit == 1 || hit == 7 ? Cursors.SizeNS : hit == 3 || hit == 5 ? Cursors.SizeWE : hit == 2 || hit == 6 ? Cursors.SizeNESW : Cursors.SizeNWSE;
            if (editor.Cursor != cursor) editor.Cursor = cursor;
        }
        if (message != 0x201 || hit < 0) return false;
        imageDragCp = editor.SelectionStart; imageDragHandle = hit;
        imageDragStart = imageDragOutline = bounds; editor.Capture = true; return true;
    }
    bool SelectImageAt(Point point) {
        if (IsComposing || queuedImageCount != 0) return false;
        if (nativeDocument.SelectedImageBounds().Contains(point)) return true;
        int position = editor.GetCharIndexFromPosition(point), start = editor.SelectionStart, length = editor.SelectionLength;
        string raw = nativeDocument.RawText();
        foreach (int candidate in new[] { position, position - 1 }) if (candidate >= 0 && candidate < raw.Length && raw[candidate] == '\ufffc') {
            editor.Select(candidate, 1);
            if (nativeDocument.SelectedImageBounds().Contains(point)) return true;
        }
        editor.Select(start, length); return false;
    }
    void OpenSelectedImage() {
        try {
            string target = nativeDocument.SelectedImageTarget(), path = target == null ? null : store.ResolveImage(target);
            if (path == null) throw new IOException("原图已移动、删除或不是安全的本地图片。");
            OpenImageFile(path);
        } catch (Exception error) { SetStatus("无法查看原图：" + error.Message, true); }
    }
    void RevealForPointer() {
        if (!Visible || movingWindow || WindowState != FormWindowState.Normal) return;
        leaveTimer.Stop(); SetChromeVisible(true);
        if (tabs == null || tabsHaveSpace && (!tabs.Visible || !tabs.IsRevealed)) ShowTabs();
    }
    void ShowTabs() {
        leaveTimer.Stop();
        if (IsDisposed || movingWindow || !Visible || WindowState != FormWindowState.Normal || note == null) return;
        if (tabs != null && tabs.Visible && tabs.IsRevealed) return;
        if (tabs == null) {
            tabs = new QuickNoteTabs(); tabs.Font = uiFont;
            tabs.HoverFile = HoverNote; tabs.SelectFile = delegate(string file) { RequestNote(file, false); editor.Focus(); };
            tabs.FileMenu = ShowTabMenu;
            tabs.More = ShowRecentMenu; tabs.LeaveRail = ScheduleLeave;
        }
        UpdateTabs(); if (!PositionTabs()) return; if (!tabs.Visible) tabs.Show(this);
        tabs.Reveal();
    }
    void ObserveHover(Control control) {
        if (control != this) {
            control.MouseEnter += delegate {
                SetChromeVisible(true);
                leaveTimer.Stop();
                if (control == editor || control == viewport || control == body || control == scroll || tabs != null && tabs.Visible) ShowTabs();
            };
            control.MouseLeave += delegate { ScheduleLeave(); };
            if (control is Button) control.Enter += delegate { SetChromeVisible(true); };
        }
        foreach (Control child in control.Controls) ObserveHover(child);
    }
    void UpdateTabs() {
        if (tabs == null) return;
        int count = Math.Min(5, Math.Max(1, (body.Height - 30 * DeviceDpi / 96) / (36 * DeviceDpi / 96)));
        var visible = recentFiles.GetRange(0, Math.Min(count, recentFiles.Count));
        string selected = Path.GetFileName(notePath ?? "");
        if (visible.Count > 0 && !visible.Exists(delegate(string value) { return String.Equals(value, selected, StringComparison.OrdinalIgnoreCase); }) && selected.Length != 0) visible[visible.Count - 1] = selected;
        tabs.SetFiles(visible.ToArray(), selected);
    }
    bool PositionTabs() {
        if (tabs == null || !IsHandleCreated) return false;
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Point origin = PointToScreen(new Point(0, top.Bottom));
        tabs.Width = 94 * DeviceDpi / 96;
        bool right = Left - tabs.Width < area.Left;
        tabsHaveSpace = !right || Right + tabs.Width <= area.Right;
        if (!tabsHaveSpace) { tabs.Hide(); return false; }
        if (tabs.OnRight != right) { tabs.OnRight = right; tabs.Invalidate(); }
        tabs.Location = new Point(right ? Right : Left - tabs.Width, Math.Max(area.Top, Math.Min(origin.Y, area.Bottom - tabs.Height)));
        return true;
    }
    void ScheduleLeave() { leaveTimer.Stop(); leaveTimer.Start(); }
    void HideTabs() { hoverTimer.Stop(); leaveTimer.Stop(); if (tabs != null) tabs.Conceal(); }
    void HoverNote(string file) {
        leaveTimer.Stop(); hoverTimer.Stop();
        if (String.Equals(hoverFile, file, StringComparison.OrdinalIgnoreCase) && previewVisible) return;
        CancelPreview(); hoverFile = file;
        if (file != null && previewOnHover && !String.Equals(file, Path.GetFileName(notePath ?? ""), StringComparison.OrdinalIgnoreCase)) hoverTimer.Start();
    }
    void StartPreview() {
        if (!previewOnHover || hoverFile == null || IsComposing || loadRequested || reloadRequested || note == null || !Visible) return;
        if (previewBusy) { hoverTimer.Start(); return; }
        string file = hoverFile; int epoch = previewEpoch;
        previewBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            NativeNotePrepared prepared = null; string failure = null;
            try {
                var candidate = store.LoadFile(file);
                if (candidate.Revision == "missing") throw new IOException("文件已移动或删除");
                string text = candidate.Text;
                // A preview is bounded and read-only. Full text and images are
                // loaded on click; no image decode or save occurs on hover.
                if (text.Length > 12000) text = text.Substring(0, 12000) + "\n…（点击标签查看完整便签）";
                prepared = NativeNoteDocument.PrepareLoad(text, null);
            } catch (Exception error) { failure = error.Message; }
            try { BeginInvoke((Action)delegate {
                previewBusy = false;
                if (IsDisposed || epoch != previewEpoch || hoverFile != file || IsComposing || loadRequested || !Visible) return;
                if (failure != null) { if (tabs != null) tabs.ShowFileTip("无法预览：" + failure); return; }
                try {
                    EnsurePreviewEditor(); previewEditor.ReadOnly = false;
                    previewDocument.ApplyPrepared(prepared); previewEditor.ReadOnly = true;
                    previewEditor.Select(0, 0); QuickNoteScrollBar.SetPosition(previewEditor, Point.Empty);
                    previewFile = file; previewVisible = true;
                    previewCaretHidden = editor.Focused && HideCaret(editor.Handle);
                    previewEditor.Show(); previewEditor.BringToFront(); scroll.Target = previewEditor;
                    formatBar.Enabled = false; UpdateTitle(); scroll.Invalidate();
                } catch (Exception error) { CancelPreview(); if (tabs != null) tabs.ShowFileTip("无法预览：" + error.Message); }
            }); } catch (InvalidOperationException) { }
        });
    }
    void EnsurePreviewEditor() {
        if (previewEditor != null) return;
        previewEditor = new QuickNoteInputBox { Visible = false, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = editor.ForeColor, Font = editorFont, WordWrap = true, ScrollBars = RichTextBoxScrollBars.ForcedVertical, TabStop = false, AccessibleName = "便签只读预览；点击标签切换" };
        // Every clipboard path is swallowed, including context menus and
        // keyboard fallback. This view never owns editable note state.
        previewEditor.UserCopyOrCut = delegate { }; previewEditor.UserPaste = delegate { }; previewEditor.UserContextMenu = delegate { };
        previewEditor.HandleKey = delegate { CancelPreview(); editor.Focus(); return true; };
        previewEditor.MouseEnter += delegate { CancelPreview(); RevealForPointer(); };
        previewEditor.PointerMoved = delegate { CancelPreview(); RevealForPointer(); };
        previewEditor.MouseDown += delegate { CancelPreview(); editor.Focus(); };
        previewEditor.MouseLeave += delegate { ScheduleLeave(); };
        previewEditor.VScroll += delegate { scroll.Invalidate(); };
        viewport.Controls.Add(previewEditor); previewDocument = new NativeNoteDocument(previewEditor, null); LayoutEditors();
    }
    void CancelPreview() {
        hoverTimer.Stop(); previewEpoch++; hoverFile = null;
        if (!previewVisible) return;
        previewVisible = false;
        if (previewEditor != null) previewEditor.Hide();
        if (previewCaretHidden) { ShowCaret(editor.Handle); previewCaretHidden = false; }
        editor.Visible = true; scroll.Target = editor; formatBar.Enabled = !editor.ReadOnly && !IsComposing;
        UpdateTitle(); scroll.Invalidate();
    }
    void ShowRecentMenu() {
        CancelPreview(); leaveTimer.Stop();
        var menu = CreateTransientMenu();
        Rectangle area = Screen.FromControl(this).WorkingArea;
        menu.ApplyStyle(DeviceDpi);
        var picker = new QuickNoteRecentPicker(recentFiles.ToArray(), Path.GetFileName(notePath ?? ""), menu.Font, DeviceDpi, new Size(Math.Min(area.Width - 8, Math.Max(220 * DeviceDpi / 96, Width - 8)), area.Height - 8));
        var host = new ToolStripControlHost(picker) { AutoSize = false, Size = picker.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        menu.ShowImageMargin = menu.ShowCheckMargin = false; menu.Padding = new Padding(1); menu.Items.Add(host);
        picker.SelectFile = delegate(string name) { menu.Close(); RequestNote(name, false); editor.Focus(); };
        picker.FileMenu = delegate(string name, Point position) {
            menu.Close();
            // Wait for the old list's native MouseDown/focus handling to end;
            // otherwise it immediately dismisses the newly opened context menu.
            BeginInvoke((Action)delegate { if (!IsDisposed) ShowTabMenu(name, position); });
        };
        picker.ClosePopup = delegate { menu.Close(); };
        menu.Opened += delegate { picker.Search.Focus(); };
        Point point = tabs == null || !tabs.Visible ? PointToScreen(new Point(0, top.Bottom)) : new Point(tabs.Left, tabs.Bottom);
        point.X = Math.Max(area.Left, Math.Min(point.X, area.Right - picker.Width - 4));
        point.Y = Math.Max(area.Top, Math.Min(point.Y, area.Bottom - picker.Height - 4));
        menu.Show(this, PointToClient(point));
    }
    void ShowTabMenu(string file, Point position) {
        CancelPreview();
        var menu = CreateTransientMenu();
        var remove = new ToolStripMenuItem("删除便签…") { Name = "deleteNote" };
        remove.Click += delegate { DeleteNote(file); };
        menu.Items.Add(remove); menu.Show(this, PointToClient(position));
    }
    void DeleteNote(string file) {
        CancelPreview(); CancelImageDrag();
        if (IsComposing || workerBusy || queuedImageCount != 0 || loadRequested || reloadRequested || editor.ReadOnly || note == null) {
            SetStatus("请先完成输入、图片插入或保存，再删除便签。", true); return;
        }
        bool confirmed = ConfirmDelete != null ? ConfirmDelete(file) : MessageBox.Show(this,
            "将「" + Path.GetFileNameWithoutExtension(file) + "」移入 Windows 回收站？\n可在回收站还原。图片附件会保留，不会删除共用图片。",
            "删除便签", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (!confirmed) return;
        // The modal confirmation pumps messages: autosave or IME may have
        // changed state while it was open. Recheck before freezing the editor.
        if (IsComposing || workerBusy || queuedImageCount != 0 || loadRequested || reloadRequested || editor.ReadOnly) {
            SetStatus("便签正在更新，请保存完成后再删除。", true); return;
        }
        if (dirty && !CaptureSave()) return;
        deleteRequested = file; editor.ReadOnly = true; DrainWork();
    }
    void StartDeleteWork() {
        string file = deleteRequested; deleteRequested = null;
        bool current = String.Equals(file, Path.GetFileName(notePath), StringComparison.OrdinalIgnoreCase);
        QuickNoteDocument target = current ? note : null;
        workerBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate {
            Exception failure = null;
            try { store.Recycle(target ?? store.LoadFile(file)); }
            catch (Exception error) { failure = error; }
            try { BeginInvoke((Action)delegate {
                workerBusy = false; if (IsDisposed) return;
                editor.ReadOnly = false;
                if (failure != null) { hideRequested = closeRequested = false; closeContinuation = null; SetStatus("无法删除便签：" + failure.Message, true); return; }
                recentFiles.RemoveAll(delegate(string name) { return String.Equals(name, file, StringComparison.OrdinalIgnoreCase); }); views.Remove(file);
                if (String.Equals(lastSavedFile, file, StringComparison.OrdinalIgnoreCase)) lastSavedFile = null;
                if (current) {
                    note = null; notePath = null; dirty = false; failedText = pendingText = null; savedVersion = editVersion;
                    editor.ReadOnly = true;
                    RequestNote(recentFiles.Count == 0 ? null : recentFiles[0], recentFiles.Count == 0);
                } else { UpdateTabs(); SetSavedStatus(); DrainWork(); }
                SaveWindowState();
            }); } catch (InvalidOperationException) { }
        });
    }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        Rectangle edge = chromeVisible ? ClientRectangle : body.Bounds;
        using (var border = new Pen(Color.FromArgb(211, 219, 225))) e.Graphics.DrawRectangle(border, edge.Left, edge.Top, edge.Width - 1, edge.Height - 1);
    }
    protected override void WndProc(ref Message message) {
        if (message.Msg == 0x231) {
            movingWindow = true; CancelPreview(); CancelImageDrag(); hoverTimer.Stop(); leaveTimer.Stop();
            if (tabs != null) tabs.SuspendForMove();
        }
        base.WndProc(ref message);
        if (message.Msg == 0x232) {
            movingWindow = false; RefreshChromeScale(); UpdateChromeForPointer(Cursor.Position);
            if (Bounds.Contains(Cursor.Position)) RevealForPointer();
        }
        if (message.Msg == 0x84 && message.Result.ToInt32() == 1 && WindowState == FormWindowState.Normal) {
            long packed = message.LParam.ToInt64(); Point p = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
            int edge = Math.Max(4, 4 * DeviceDpi / 96);
            bool left = p.X < edge, right = p.X >= Width - edge, up = p.Y < edge, down = p.Y >= Height - edge;
            message.Result = new IntPtr(up ? left ? 13 : right ? 14 : 12 : down ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 1);
        }
    }


}
