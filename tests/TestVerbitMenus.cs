using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

// Synthetic menus on a private desktop; no clipboard, input injection or audio.
internal static class TestVerbitMenus {
    static int assertions;
    static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    static void Pump() { Application.DoEvents(); System.Threading.Thread.Sleep(25); Application.DoEvents(); }
    static void Capture(ToolStrip strip, string file) {
        using (var bitmap = new Bitmap(strip.Width, strip.Height)) {
            strip.DrawToBitmap(bitmap, new Rectangle(Point.Empty, strip.Size)); bitmap.Save(file);
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            string folder = args[0];
            using (var owner = new Form { Text = "verbit synthetic menu fixture", Size = new Size(800, 600) }) {
                owner.Show(); Pump();
                foreach (int dpi in new[] { 96, 120, 144, 192, 240, 288 }) using (var menu = new VerbitMenu()) {
                    menu.Items.Add(new ToolStripMenuItem("verbit已就绪") { Enabled = false });
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
                    Check(menu.Renderer is VerbitMenuRenderer, "Every root uses the shared renderer");
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
                    Check(recent.DropDown.Renderer is VerbitMenuRenderer && recent.DropDown.Font == menu.Font, "Dynamically rebuilt submenu inherits renderer and DPI font");
                    Check(recent.DropDownItems.Count == 2, "Submenu still populates before styling");
                    Capture(recent.DropDown, Path.Combine(folder, "submenu-" + dpi + ".png"));
                    recent.HideDropDown();
                    bool clicked = false; note.Click += delegate { clicked = true; }; note.PerformClick();
                    Check(clicked, "Selection still dispatches the original command"); menu.Close();
                }
                using (var field = new TextBox { Text = "中文输入 abc", Width = 250 }) {
                    owner.Controls.Add(field); VerbitMenu.AttachTextMenu(field);
                    var menu = field.ContextMenuStrip;
                    Check(menu is VerbitMenu, "Settings/search/text fields share the same style");
                    VerbitMenu.AttachTextMenu(field); Check(menu == field.ContextMenuStrip, "Repeated settings styling does not allocate another menu");
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
                using (var hosted = new VerbitMenu()) using (var picker = new QuickNoteRecentPicker(new[] { "演示便签.md" }, "演示便签.md", owner.Font, 96, new Size(350, 450))) {
                    hosted.Items.Add(new ToolStripControlHost(picker) { AutoSize = false, Size = picker.Size, Margin = Padding.Empty, Padding = Padding.Empty });
                    hosted.ShowImageMargin = hosted.ShowCheckMargin = false; hosted.Padding = new Padding(1);
                    var before = picker.Size; hosted.Show(owner, new Point(8, 8)); Pump();
                    Check(before == picker.Size, "Recent-note search popup retains its own layout");
                    Check(picker.Search.ContextMenuStrip is VerbitMenu, "The recent-note search field also uses the common menu");
                    hosted.Close();
                }
                owner.Close();
            }
            Console.WriteLine("Verbit menu tests passed: " + assertions + " assertions; isolated DPI layout simulation, no clipboard/audio."); return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
