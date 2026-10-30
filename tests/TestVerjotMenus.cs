using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Synthetic menus on a private desktop; no clipboard, input injection or audio.
internal static class TestVerjotMenus {
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Pump() { Application.DoEvents(); System.Threading.Thread.Sleep(25); Application.DoEvents(); }
    static void Capture(ToolStrip strip, string file) {
        using (var bitmap = new Bitmap(strip.Width, strip.Height)) {
            strip.DrawToBitmap(bitmap, new Rectangle(Point.Empty, strip.Size)); bitmap.Save(file);
        }
    }
    static void CaptureHosted(ToolStrip strip, string file) {
        // DrawToBitmap paints native children beyond a clipping panel. Capture
        // the presented HWND to verify that the old list scrollbar is clipped.
        using (var bitmap = new Bitmap(strip.Width, strip.Height)) using (var graphics = Graphics.FromImage(bitmap)) {
            IntPtr dc = graphics.GetHdc();
            try { Check(PrintWindow(strip.Handle, dc, 0), "Hosted recent picker native capture succeeds"); }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(file);
        }
    }
    static int Ink(Bitmap bitmap, Rectangle area) {
        int count = 0;
        for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++) {
            Color pixel = bitmap.GetPixel(x, y);
            if (pixel.R < 150 && pixel.G < 160 && pixel.B < 180) count++;
        }
        return count;
    }
    static void CheckPresentedRows(QuickNoteRecentPicker picker, string file) {
        // Copy the current native pixels without asking PrintWindow/DrawToBitmap
        // to repaint every row, which would mask incremental selection damage.
        int width = picker.Results.Parent.ClientSize.Width, height = picker.Results.ClientSize.Height;
        using (var bitmap = new Bitmap(width, height)) using (var graphics = Graphics.FromImage(bitmap)) {
            IntPtr source = GetDC(picker.Results.Handle), target = graphics.GetHdc();
            try { Check(BitBlt(target, 0, 0, width, height, source, 0, 0, 0x00CC0020), "Presented list pixels can be captured without repaint"); }
            finally { graphics.ReleaseHdc(target); ReleaseDC(picker.Results.Handle, source); }
            int row = picker.Results.ItemHeight, top = picker.Results.TopIndex;
            int visible = Math.Min(height / row, picker.Results.Items.Count - top);
            for (int i = 0; i < visible; i++)
                Check(Ink(bitmap, new Rectangle(24 * picker.DeviceDpi / 96, i * row, width - 24 * picker.DeviceDpi / 96, row)) > 0,
                    "Visible row retains text after " + Path.GetFileNameWithoutExtension(file) + ": " + (top + i));
            bitmap.Save(file);
        }
    }
    static void CheckPartialPainting() {
        foreach (int dpi in new[] { 96, 120, 144, 192, 240, 288 })
        using (var font = new Font("Microsoft YaHei UI", 12f * dpi / 96, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var picker = new QuickNoteRecentPicker(new[] { "第一份记录.md", "第二份记录.md", "第三份记录.md", "第四份记录.md", "第五份记录.md", "第六份记录.md" }, "第三份记录.md", font, dpi, new Size(900, 1000))) {
            int width = picker.Results.Parent.Width, row = picker.Results.ItemHeight;
            using (var bitmap = new Bitmap(width, row * 6)) using (var graphics = Graphics.FromImage(bitmap)) {
                MethodInfo draw = typeof(ListBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic);
                graphics.Clear(Color.White);
                for (int i = 0; i < 6; i++) draw.Invoke(picker.Results, new object[] { new DrawItemEventArgs(graphics, font, new Rectangle(0, i * row, width, row), i, DrawItemState.None) });
                for (int selected = 0; selected < 6; selected++) using (var before = (Bitmap)bitmap.Clone()) {
                    var area = new Rectangle(0, selected * row, width, row);
                    draw.Invoke(picker.Results, new object[] { new DrawItemEventArgs(graphics, font, area, selected, DrawItemState.Selected) });
                    bool unchanged = true;
                    for (int y = 0; y < bitmap.Height && unchanged; y++) if (y < area.Top || y >= area.Bottom)
                        for (int x = 0; x < width; x++) if (before.GetPixel(x, y) != bitmap.GetPixel(x, y)) { unchanged = false; break; }
                    Check(unchanged, "Partial selected-row drawing preserves every other pixel at " + dpi + "; row=" + selected);
                    for (int i = 0; i < 6; i++) Check(Ink(bitmap, new Rectangle(24 * dpi / 96, i * row, width - 24 * dpi / 96, row)) > 0, "All row text remains after partial redraw at " + dpi);
                }
            }
        }
    }
    static void Event(Control control, string name, EventArgs args) {
        typeof(Control).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new object[] { args });
    }
    static void CheckButtonStates(Form owner, string folder) {
        using (var header = new QuickNoteHeaderButton { ActionIcon = "new", Location = new Point(20, 350) })
        using (var format = new QuickNoteFormatButton { Format = "bold", Location = new Point(60, 350) }) {
            owner.Controls.Add(header); owner.Controls.Add(format); Pump();
            foreach (Button button in new Button[] { header, format }) {
                var feedback = (QuickNoteHoverFeedback)button.GetType().GetField("hover", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(button);
                var timer = (System.Windows.Forms.Timer)typeof(QuickNoteHoverFeedback).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(feedback);
                Size size = button.Size; Point location = button.Location;
                Event(button, "OnMouseEnter", EventArgs.Empty);
                for (int i = 0; i < 10; i++) Pump();
                Check(feedback.Value == 1 && !timer.Enabled, "Hover settles and stops its timer");
                Event(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
                Check(feedback.Pressed, "Mouse down has immediate pressed feedback");
                using (var bitmap = new Bitmap(button.Width, button.Height)) {
                    button.DrawToBitmap(bitmap, button.ClientRectangle);
                    Check(bitmap.GetPixel(button.Width / 2, 5) == VerjotUi.Pressed, "Pressed uses the shared color without moving the icon");
                    bitmap.Save(Path.Combine(folder, button.GetType().Name + "-pressed.png"));
                }
                Event(button, "OnMouseLeave", EventArgs.Empty);
                Check(!feedback.Pressed, "Dragging outside clears pressed feedback");
                Event(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, -1, -1, 0));
                Event(button, "OnKeyDown", new KeyEventArgs(Keys.Space)); Check(feedback.Pressed, "Space gives keyboard press feedback");
                Event(button, "OnKeyUp", new KeyEventArgs(Keys.Space)); Check(!feedback.Pressed, "Space release restores the state");
                Event(button, "OnKeyDown", new KeyEventArgs(Keys.Space)); Event(button, "OnLostFocus", EventArgs.Empty);
                Check(!feedback.Pressed, "Losing focus cannot leave a stuck pressed state");
                Event(button, "OnMouseEnter", EventArgs.Empty); button.Enabled = false;
                Check(!feedback.Pressed && feedback.Value == 0 && !timer.Enabled, "Disabling cancels the transition and pressed state");
                int clicks = 0; button.Click += delegate { clicks++; }; button.PerformClick();
                Check(clicks == 0, "Disabled icons do not invoke actions");
                button.Enabled = true; Event(button, "OnMouseEnter", EventArgs.Empty); button.Hide();
                Check(feedback.Value == 0 && !timer.Enabled, "Hiding chrome stops its button animation immediately");
                button.Show(); Check(button.Size == size && button.Location == location, "Feedback never changes button geometry");
                Event(button, "OnMouseLeave", EventArgs.Empty);
            }
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            string folder = args[0];
            CheckPartialPainting();
            using (var owner = new Form { Text = "Verjot synthetic menu fixture", Size = new Size(800, 600) }) {
                owner.Show(); Pump();
                CheckButtonStates(owner, folder);
                foreach (int dpi in new[] { 96, 120, 144, 192, 240, 288 }) using (var menu = new VerjotMenu()) {
                    menu.Items.Add(new ToolStripMenuItem("Verjot已就绪") { Enabled = false });
                    var note = new ToolStripMenuItem("速记便签 · Ctrl+Alt+J"); menu.Items.Add(note);
                    var mic = new ToolStripMenuItem("暂停麦克风 · Alt+Space") { Enabled = false }; menu.Items.Add(mic);
                    menu.Items.Add(new ToolStripSeparator());
                    var preview = new ToolStripMenuItem("悬停标签预览内容") { Checked = true }; menu.Items.Add(preview);
                    var recent = new ToolStripMenuItem("最近会议"); recent.DropDownItems.Add("占位项"); menu.Items.Add(recent);
                    recent.DropDownOpening += delegate { recent.DropDownItems.Clear(); recent.DropDownItems.Add("本周产品需求讨论"); recent.DropDownItems.Add("打开成品位置"); };
                    menu.Items.Add(new ToolStripMenuItem("删除便签…") { Name = "deleteNote" });
                    menu.Items.Add("设置…");
                    menu.Items.Add(new ToolStripMenuItem(new string('长', 180)));
                    menu.Show(owner, new Point(8, 8)); menu.ApplyStyle(dpi); note.Select(); Pump();
                    Check(menu.Renderer is VerjotMenuRenderer, "Every root uses the shared renderer");
                    Check(menu.Font.Unit == GraphicsUnit.Pixel && Math.Abs(menu.Font.Size - 12f * dpi / 96) < .01f, "Compact font scales once at " + dpi);
                    Check(menu.Width <= 360 * dpi / 96 + 16 * dpi / 96, "Long labels cannot create an unbounded menu: " + menu.Width + " at " + dpi);
                    Check(menu.Items[0].Height >= menu.Font.Height + 8 * dpi / 96 && menu.Items[0].Height <= 28 * dpi / 96, "Chinese text fits the compact row at " + dpi);
                    Check(recent.Bounds.Right <= menu.ClientSize.Width, "Submenu arrow stays inside the constrained popup: item=" + recent.Bounds + "; menu=" + menu.Size);
                    Check(preview.Checked && !mic.Enabled, "Styling preserves checked and disabled semantics");
                    Capture(menu, Path.Combine(folder, "menu-" + dpi + ".png"));
                    using (var bitmap = new Bitmap(menu.Width, menu.Height)) {
                        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
                        bool arrowVisible = false;
                        for (int x = menu.Width - 24 * dpi / 96; x < menu.Width - 7 * dpi / 96; x++)
                            for (int y = recent.Bounds.Top + recent.Height / 2 - 5 * dpi / 96; y <= recent.Bounds.Top + recent.Height / 2 + 5 * dpi / 96; y++) {
                                Color pixel = bitmap.GetPixel(x, y); if (pixel.R < 140 && pixel.G < 150 && pixel.B < 170) arrowVisible = true;
                            }
                        Check(arrowVisible, "The submenu arrow remains visibly discoverable with long names at " + dpi);
                    }
                    recent.ShowDropDown(); Pump();
                    Check(recent.DropDown.Renderer is VerjotMenuRenderer && recent.DropDown.Font == menu.Font, "Dynamically rebuilt submenu inherits renderer and DPI font");
                    Check(recent.DropDownItems.Count == 2, "Submenu still populates before styling");
                    Capture(recent.DropDown, Path.Combine(folder, "submenu-" + dpi + ".png"));
                    recent.HideDropDown();
                    bool clicked = false; note.Click += delegate { clicked = true; }; note.PerformClick();
                    Check(clicked, "Selection still dispatches the original command"); menu.Close();
                }
                using (var field = new TextBox { Text = "中文输入 abc", Width = 250 }) {
                    owner.Controls.Add(field); VerjotMenu.AttachTextMenu(field);
                    var menu = field.ContextMenuStrip;
                    Check(menu is VerjotMenu, "Settings/search/text fields share the same style");
                    VerjotMenu.AttachTextMenu(field); Check(menu == field.ContextMenuStrip, "Repeated settings styling does not allocate another menu");
                    field.Select(0, 2); menu.Show(field, new Point(0, field.Height)); Pump();
                    Check(menu.Items[2].Enabled && menu.Items[3].Enabled && menu.Items[4].Enabled, "Editable selection allows clipboard commands without probing clipboard");
                    Capture(menu, Path.Combine(folder, "text-menu.png")); menu.Close();
                    field.ReadOnly = true; menu.Show(field, new Point(0, field.Height)); Pump();
                    Check(!menu.Items[0].Available && !menu.Items[2].Available && !menu.Items[4].Available && menu.Items[3].Enabled, "Read-only fields only expose useful text commands"); menu.Close();
                    field.ReadOnly = false; field.UseSystemPasswordChar = true; menu.Show(field, new Point(0, field.Height)); Pump();
                    Check(!menu.Items[2].Enabled && !menu.Items[3].Enabled, "Password fields cannot expose selected secrets"); menu.Close();
                    menu.Items[6].PerformClick(); Check(field.SelectionLength == field.TextLength, "Select all uses the field's native selection");
                    field.Dispose(); Check(menu.IsDisposed, "Text-menu resources follow their owning field");
                }
                foreach (var screen in Screen.AllScreens) using (var hosted = new VerjotMenu()) {
                    owner.Location = new Point(screen.WorkingArea.Left + 20, screen.WorkingArea.Top + 20); Pump();
                    hosted.ApplyStyle(owner.DeviceDpi);
                    var noteNames = new string[41]; noteNames[0] = "演示便签.md";
                    for (int i = 1; i < noteNames.Length; i++) noteNames[i] = "便签列表验证-" + i + ".md";
                    using (var picker = new QuickNoteRecentPicker(noteNames, "演示便签.md", hosted.Font, owner.DeviceDpi, new Size(350 * owner.DeviceDpi / 96, 450 * owner.DeviceDpi / 96))) {
                    hosted.Items.Add(new ToolStripControlHost(picker) { AutoSize = false, Size = picker.Size, Margin = Padding.Empty, Padding = Padding.Empty });
                    hosted.ShowImageMargin = hosted.ShowCheckMargin = false; hosted.Padding = new Padding(1);
                    var before = picker.Size; hosted.Show(owner, new Point(8, 8)); Pump();
                    Check(before == picker.Size, "Recent-note search popup retains its own layout");
                    Check(hosted.ClientSize.Width >= picker.Width + hosted.Padding.Horizontal && hosted.Items[0].Width >= picker.Width, "Hosted popup clears the stale normal-menu width cap so the search and long names are not clipped");
                    Check(hosted.ClientSize.Width <= picker.Width + hosted.Padding.Horizontal + 1,
                        "Hosted picker has no reserved right command gutter: menu=" + hosted.ClientSize + "; picker=" + picker.Size);
                    Check(picker.Width <= 280 * owner.DeviceDpi / 96, "Picker width follows compact filename measure rather than a fixed wide sheet");
                    Check(picker.Search.ContextMenuStrip is VerjotMenu, "The recent-note search field also uses the common menu");
                    Check(picker.Font.Unit == GraphicsUnit.Pixel && Math.Abs(picker.Font.Size - 12f * owner.DeviceDpi / 96) < .01, "More-notes text uses the common menu font rather than the editor font");
                    Check(picker.BackColor == VerjotMenuRenderer.Surface && picker.Results.ForeColor == VerjotMenuRenderer.Ink, "More-notes surface and foreground match every other menu");
                    Console.WriteLine("Recent dpi=" + owner.DeviceDpi + "; picker=" + picker.Size + "; viewport=" + picker.Results.Parent.Bounds + "; list=" + picker.Results.Bounds + "; bar=" + picker.ListScroll.Bounds + "; font=" + picker.Font);
                    Check(picker.Results.ItemHeight <= 28 * owner.DeviceDpi / 96 && picker.Results.ItemHeight >= picker.Font.Height + 8 * owner.DeviceDpi / 96, "More-notes uses compact rows with complete Chinese glyphs");
                    CaptureHosted(hosted, Path.Combine(folder, "recent-picker.png"));
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-open-" + owner.DeviceDpi + ".png"));
                    for (int hover = 0; hover < 6; hover++) {
                        typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(picker.Results,
                            new object[] { new MouseEventArgs(MouseButtons.None, 0, 30, hover * picker.Results.ItemHeight + picker.Results.ItemHeight / 2, 0) });
                        Pump();
                        Check(picker.Results.SelectedIndex == hover, "Hover selects its real native row");
                        CheckPresentedRows(picker, Path.Combine(folder, "presented-hover-" + owner.DeviceDpi + "-" + hover + ".png"));
                    }
                    SendMessage(picker.Results.Handle, 0x100, new IntPtr((int)Keys.Up), IntPtr.Zero); Pump();
                    Check(picker.Results.SelectedIndex == 4, "Native keyboard selection still changes the selected row");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-keyboard-" + owner.DeviceDpi + ".png"));
                    SendMessage(picker.Results.Handle, 0x115, new IntPtr(1), IntPtr.Zero); Pump();
                    Check(picker.Results.TopIndex > 0, "Native line scroll moves the list");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-line-scroll-" + owner.DeviceDpi + ".png"));
                    int wheelBefore = picker.Results.TopIndex;
                    SendMessage(picker.Results.Handle, 0x20a, new IntPtr(-120 << 16), IntPtr.Zero); Pump();
                    Check(picker.Results.TopIndex > wheelBefore, "Native wheel scrolling moves the list");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-wheel-" + owner.DeviceDpi + ".png"));
                    Check(!picker.ListScroll.ThumbBounds.IsEmpty && picker.ListScroll.Width <= 10 * owner.DeviceDpi / 96, "Overflow exposes a small dedicated scrollbar hit area");
                    Rectangle thumbBefore = picker.ListScroll.ThumbBounds;
                    typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(picker.ListScroll,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 1, picker.ListScroll.Width / 2, picker.ListScroll.Height - 2, 0) });
                    typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(picker.ListScroll,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 1, picker.ListScroll.Width / 2, picker.ListScroll.Height - 2, 0) }); Pump();
                    Check(picker.Results.TopIndex > 0 && picker.ListScroll.ThumbBounds.Top > thumbBefore.Top, "The slim scrollbar scrolls the real native list");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-thumb-" + owner.DeviceDpi + ".png"));
                    CaptureHosted(hosted, Path.Combine(folder, "recent-picker-overflow-" + owner.DeviceDpi + ".png"));
                    picker.Search.Text = "演示"; Pump();
                    Check(picker.ClearSearch.Visible && picker.Search.Right < picker.ClearSearch.Left, "Search clear action never overlaps native text");
                    Check(picker.ClearSearch.AccessibleName == "清空搜索", "Icon-only search action has a keyboard-readable name");
                    CaptureHosted(hosted, Path.Combine(folder, "recent-search-clear-" + owner.DeviceDpi + ".png"));
                    Check(picker.Results.Items.Count == 1 && picker.Results.TopIndex == 0 && picker.ListScroll.ThumbBounds.IsEmpty, "Filtering clears stale scroll range and hides an unnecessary thumb");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-filter-" + owner.DeviceDpi + ".png"));
                    picker.Search.Text = "没有此名称"; Pump();
                    Check(picker.Results.Items.Count == 0 && picker.ListScroll.ThumbBounds.IsEmpty, "Empty search results never retain an old scrollbar");
                    picker.ClearSearch.PerformClick(); Pump();
                    Check(picker.Search.Text == "" && !picker.ClearSearch.Visible && picker.Search.Focused, "Clear restores all files and returns focus for typing");
                    Check(picker.Results.Items.Count == noteNames.Length, "Clearing search restores all filenames");
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-filter-clear-" + owner.DeviceDpi + ".png"));
                    hosted.Close(); hosted.Show(owner, new Point(8, 8)); Pump();
                    CheckPresentedRows(picker, Path.Combine(folder, "presented-reopen-" + owner.DeviceDpi + ".png"));
                    hosted.Close();
                    }
                    using (var freshMenu = new VerjotMenu()) {
                        freshMenu.ApplyStyle(owner.DeviceDpi);
                        using (var freshPicker = new QuickNoteRecentPicker(noteNames, noteNames[2], freshMenu.Font, owner.DeviceDpi, new Size(350 * owner.DeviceDpi / 96, 450 * owner.DeviceDpi / 96))) {
                            freshMenu.ShowImageMargin = freshMenu.ShowCheckMargin = false; freshMenu.Padding = new Padding(1);
                            freshMenu.Items.Add(new ToolStripControlHost(freshPicker) { AutoSize = false, Size = freshPicker.Size, Margin = Padding.Empty, Padding = Padding.Empty });
                            freshMenu.Show(owner, new Point(8, 8)); Pump();
                            Check(freshPicker.Results.Items.Count == noteNames.Length && freshPicker.Search.Text == "", "Recreating the popup restores all files and a fresh search");
                            CheckPresentedRows(freshPicker, Path.Combine(folder, "presented-recreated-" + owner.DeviceDpi + ".png"));
                            freshMenu.Close();
                        }
                    }
                }
                owner.Close();
            }
            Console.WriteLine("Verjot menu tests passed: " + assertions + " assertions; isolated DPI layout simulation, no clipboard/audio."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
