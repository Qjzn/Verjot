using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

// A settings edit is based on an exact file snapshot. Never rebuild the file
// from the visible fields: model/API settings not shown here must survive.
internal sealed class MeetingSettingsSnapshot {
    readonly string path;
    byte[] original;
    Dictionary<string, object> values;
    MeetingSettingsSnapshot(string path, byte[] original, Dictionary<string, object> values) {
        this.path = path; this.original = original; this.values = values;
    }
    public static MeetingSettingsSnapshot Open(string path) {
        path = Path.GetFullPath(path);
        MeetingConfig.CheckOrdinaryPath(path);
        byte[] bytes = File.ReadAllBytes(path);
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
        var parsed = MeetingFiles.Json.Deserialize<Dictionary<string, object>>(text);
        // Unknown keys and malformed existing values fail without touching disk.
        return new MeetingSettingsSnapshot(path, bytes, MeetingConfig.Validate(parsed, false));
    }
    public static MeetingSettingsSnapshot CreateNew(string path, string suggestedRoot) {
        path = Path.GetFullPath(path);
        MeetingConfig.CheckOrdinaryPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("配置已经存在，请重新启动以加载；本次未覆盖配置。");
        var defaults = MeetingConfig.Validate(new Dictionary<string, object> { { "vault_root", MeetingFiles.Home }, { "open_in_obsidian", false }, { "transcription_mode", "api" }, { "meeting_folder", "verbit" } }, false);
        // An unavailable/redirected Documents folder must still let the user
        // choose a valid destination. Validate the selection only when saved.
        defaults["vault_root"] = suggestedRoot ?? "";
        return new MeetingSettingsSnapshot(path, null, defaults);
    }
    static Dictionary<string, object> Clone(Dictionary<string, object> source) {
        return MeetingFiles.Json.Deserialize<Dictionary<string, object>>(MeetingFiles.Json.Serialize(source));
    }
    public Dictionary<string, object> Values { get { return Clone(values); } }
    public Dictionary<string, object> CreateCandidate(string root, string meetingFolder, string quickFolder,
            string start, string pause, string bookmark, string quickNote, string protectionSeconds = null, Dictionary<string, object> transcription = null) {
        var candidate = Values;
        candidate["vault_root"] = root.Trim();
        candidate["meeting_folder"] = meetingFolder.Trim();
        candidate["quick_note_folder"] = quickFolder.Trim();
        candidate["hotkey"] = start.Trim(); candidate["pause_hotkey"] = pause.Trim();
        candidate["bookmark_hotkey"] = bookmark.Trim(); candidate["quick_note_hotkey"] = quickNote.Trim();
        if (protectionSeconds != null) {
            int seconds;
            if (!int.TryParse(protectionSeconds.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 0 || seconds > 300)
                throw new InvalidOperationException("录音保护时间请填写 0 到 300 之间的整数秒；0 表示关闭。");
            candidate["recording_protection_seconds"] = seconds;
        }
        if (transcription != null) foreach (var item in transcription) candidate[item.Key] = item.Value;
        return MeetingConfig.Validate(candidate, false);
    }
    static bool Same(byte[] first, byte[] second) {
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
        return true;
    }
    static byte[] Serialize(Dictionary<string, object> candidate) {
        var text = new StringBuilder("{\r\n");
        int index = 0;
        foreach (var entry in candidate) {
            if (index++ != 0) text.Append(",\r\n");
            text.Append("  ").Append(MeetingFiles.Json.Serialize(entry.Key)).Append(": ").Append(MeetingFiles.Json.Serialize(entry.Value));
        }
        text.Append("\r\n}\r\n");
        return new UTF8Encoding(false).GetBytes(text.ToString());
    }
    public void Commit(Dictionary<string, object> candidate) {
        var validated = MeetingConfig.Validate(Clone(candidate), false);
        byte[] replacement = Serialize(validated);
        MeetingConfig.CheckOrdinaryPath(path);
        if (original == null) {
            string firstTemporary = path + ".settings-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var stream = new FileStream(firstTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(replacement, 0, replacement.Length); stream.Flush(true);
                }
                // Move does not overwrite a configuration created while the
                // first-run window was open. Cancel never creates either file.
                File.Move(firstTemporary, path);
                original = replacement; values = validated;
            } finally {
                try { if (File.Exists(firstTemporary)) File.Delete(firstTemporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return;
        }
        if (!Same(original, File.ReadAllBytes(path)))
            throw new IOException("配置已被其他程序修改。请关闭设置后重新打开，再保存；本次未覆盖配置。");
        string token = Guid.NewGuid().ToString("N");
        string temporary = path + ".settings-" + token + ".tmp";
        string backup = path + ".settings-" + token + ".bak";
        bool keepBackup = true, replaced = false, committed = false;
        try {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(replacement, 0, replacement.Length); stream.Flush(true);
            }
            // ReplaceFile keeps the actual replaced preimage. A second editor
            // can race the check above; retain/restore its bytes if that occurs.
            File.Replace(temporary, path, backup); replaced = true;
            byte[] preimage = File.ReadAllBytes(backup);
            if (!Same(original, preimage)) {
                if (Same(replacement, File.ReadAllBytes(path))) {
                    File.Replace(backup, path, null); keepBackup = false;
                }
                throw new IOException(keepBackup
                    ? "保存时配置又发生变化。未继续应用；另一份配置已保留在：" + backup
                    : "保存时配置又发生变化，已恢复另一程序的修改。请重新打开设置。");
            }
            if (!Same(replacement, File.ReadAllBytes(path)))
                throw new IOException("保存后配置被其他程序再次修改。请关闭设置并重启程序以重新加载；旧配置保留在：" + backup);
            original = replacement; values = validated; committed = true; keepBackup = false;
        } catch {
            // Do not turn an uncertain write/readback into success. Keep the
            // replaced bytes recoverable if another writer now owns the path.
            if (replaced && !committed && File.Exists(backup)) {
                try {
                    if (Same(replacement, File.ReadAllBytes(path))) {
                        File.Replace(backup, path, null); keepBackup = false;
                    }
                } catch { keepBackup = true; }
            }
            throw;
        } finally {
            // A cleanup failure after the atomic commit must not make the
            // caller roll back live shortcuts while the new config is active.
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { if (!keepBackup && File.Exists(backup)) File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class MeetingFirstRunSetup {
    public static bool Run(string path, string suggestedRoot) {
        MeetingConfig.CheckOrdinaryPath(path);
        // Existing installations keep their config and normal startup path.
        // Missing config is the only first-run marker; cancelling remains safe
        // to retry and does not construct the recorder.
        if (File.Exists(path)) return true;
        var snapshot = MeetingSettingsSnapshot.CreateNew(path, suggestedRoot);
        using (var dialog = new MeetingSettingsDialog(snapshot, delegate(Dictionary<string, object> next) {
            using (var probe = new MeetingHotkey((string)next["hotkey"], (string)next["pause_hotkey"], (string)next["bookmark_hotkey"], (string)next["quick_note_hotkey"]))
                probe.Reconfigure((string)next["hotkey"], (string)next["pause_hotkey"], (string)next["bookmark_hotkey"], (string)next["quick_note_hotkey"]);
            snapshot.Commit(next); return null;
        }, true)) return dialog.ShowDialog() == DialogResult.OK;
    }
}

// Native controls keep the settings window small and retain normal keyboard,
// IME, DPI and accessibility behavior. Show() makes this a modeless window.
internal sealed class LocalPreparationPrompt : Form {
    public bool PreparationAccepted { get; private set; }
    public LocalPreparationPrompt(Func<bool> prepare) {
        Text = "verbit · 录音已保存"; Icon = MeetingBrand.IdleIcon;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Microsoft YaHei UI", 10); BackColor = Color.White;
        ClientSize = new Size(410, 210); Padding = new Padding(22);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false;
        var text = new Label { Text = "录音已保存，转写可以稍后进行\n\n转成文字可在设置中配置第三方 API。\n现有本地模型和配置保留；首期不提供模型下载。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(42, 65, 92) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var later = new Button { Text = "仅保留录音", AutoSize = true, Height = 32, DialogResult = DialogResult.Cancel };
        var start = new Button { Text = "打开转写设置", AutoSize = true, Height = 32, BackColor = Color.FromArgb(40, 104, 168), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        start.Click += delegate { if (prepare()) { PreparationAccepted = true; Close(); } }; later.Click += delegate { Close(); };
        actions.Controls.Add(later); actions.Controls.Add(start); Controls.Add(text); Controls.Add(actions); CancelButton = later;
        DesktopServices.KeepWindowOnScreen(this);
    }
}

// Composite only the settings content tree. Keep native dropdown popups and the
// note/recording windows outside this scope so their input paths stay unchanged.
internal sealed class SettingsTabControl : TabControl {
    protected override CreateParams CreateParams {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x02000000; return parameters; }
    }
}

// A native edit retains accessibility and DPI behavior, but records keys rather
// than accepting text, paste or IME input. No keyboard hook or clipboard access.
internal sealed class HotkeyInput : TextBox {
    public event Action<bool> CaptureChanged;
    public event Action Recorded;
    public event Action<string> CaptureError;
    public bool Capturing { get; private set; }
    string previous, candidate;
    Keys candidateKey;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetKeyState(int key);
    public HotkeyInput() { ReadOnly = true; ShortcutsEnabled = false; Cursor = Cursors.Hand; ImeMode = ImeMode.Disable; }
    internal void BeginCapture() {
        if (Capturing) return;
        try { if (CaptureChanged != null) CaptureChanged(true); }
        catch (Exception failure) { if (CaptureError != null) CaptureError(failure.Message); return; }
        previous = Text; candidate = null; Capturing = true;
        Text = "请按快捷键…"; BackColor = Color.FromArgb(238, 245, 253); SelectAll();
    }
    internal void FinishCapture(bool accept) {
        if (!Capturing) return;
        Text = accept && candidate != null ? candidate : previous;
        Capturing = false; candidate = null; BackColor = Color.FromArgb(252, 253, 255); SelectionLength = 0;
        try { if (CaptureChanged != null) CaptureChanged(false); }
        catch (Exception failure) { if (CaptureError != null) CaptureError(failure.Message); }
        finally { if (Recorded != null) Recorded(); }
    }
    internal bool CaptureKey(Keys keys, bool windows = false) {
        if (!Capturing) return false;
        Keys key = keys & Keys.KeyCode;
        if (key == Keys.Escape || key == Keys.Tab) { FinishCapture(false); return key != Keys.Tab; }
        bool allowed = key >= Keys.A && key <= Keys.Z || key >= Keys.F1 && key <= Keys.F24 || key == Keys.Space;
        if (!allowed || (keys & Keys.Modifiers) == Keys.None && !windows || keys == (Keys.Alt | Keys.F4)) return true;
        string value = ((keys & Keys.Control) != 0 ? "Ctrl+" : "") + ((keys & Keys.Alt) != 0 ? "Alt+" : "") +
            ((keys & Keys.Shift) != 0 ? "Shift+" : "") + (windows ? "Win+" : "") + (key == Keys.Space ? "Space" : key.ToString());
        uint modifiers, code; MeetingConfig.ParseHotkey(value, out modifiers, out code);
        candidate = value; candidateKey = key; Text = value; SelectAll(); return true;
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) BeginCapture(); }
    protected override bool ProcessCmdKey(ref Message message, Keys keys) {
        if (!Capturing && (keys == Keys.Enter || keys == Keys.Space)) { BeginCapture(); return true; }
        if (CaptureKey(keys, GetKeyState(0x5b) < 0 || GetKeyState(0x5c) < 0)) return true;
        return base.ProcessCmdKey(ref message, keys);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
        if (CaptureKey(e.KeyData, GetKeyState(0x5b) < 0 || GetKeyState(0x5c) < 0)) e.Handled = true;
        else base.OnKeyDown(e);
    }
    protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }
    protected override void OnKeyUp(KeyEventArgs e) {
        if (Capturing && candidate != null && e.KeyCode == candidateKey) { FinishCapture(true); e.Handled = true; }
        base.OnKeyUp(e);
    }
    protected override void OnLostFocus(EventArgs e) { FinishCapture(false); base.OnLostFocus(e); }
    protected override void Dispose(bool disposing) { if (disposing) FinishCapture(false); base.Dispose(disposing); }
}

internal sealed class MeetingSettingsDialog : Form {
    readonly MeetingSettingsSnapshot snapshot;
    readonly Func<Dictionary<string, object>, string> apply;
    readonly Dictionary<string, TextBox> inputs = new Dictionary<string, TextBox>();
    readonly Dictionary<string, ComboBox> choices = new Dictionary<string, ComboBox>();
    CheckBox startup, automaticUpdates, echoFilter, autoTranscribe;
    bool originalStartup;
    readonly Label error = new Label { AutoSize = false, ForeColor = Color.FromArgb(171, 37, 37), Dock = DockStyle.Fill };
    readonly Button save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(76, 28) };
    Font dpiFont;
    Image githubImage;
    Label shortcutHint;
    public event Action<bool> HotkeyCaptureChanged;
    readonly System.Windows.Forms.Timer engineTimer = new System.Windows.Forms.Timer { Interval = 1000 };
    Label engineTitle, engineDetail;
    public void ShowAbout() { ((TabControl)Controls.Find("settingsTabs", true)[0]).SelectedIndex = 2; }
    public void ShowTranscription() { ((TabControl)Controls.Find("settingsTabs", true)[0]).SelectedIndex = 1; }
    public MeetingSettingsDialog(MeetingSettingsSnapshot snapshot, Func<Dictionary<string, object>, string> apply, bool firstRun = false) {
        if (snapshot == null || apply == null) throw new ArgumentNullException(snapshot == null ? "snapshot" : "apply");
        this.snapshot = snapshot; this.apply = apply;
        SuspendLayout();
        Text = firstRun ? "verbit · 首次设置" : "verbit · 设置"; Icon = MeetingBrand.IdleIcon;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        dpiFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel); Font = dpiFont;
        ClientSize = new Size(500, 480); Padding = new Padding(8);
        BackColor = Color.White; ForeColor = Color.FromArgb(34, 46, 63);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var tabs = new SettingsTabControl { Name = "settingsTabs", Dock = DockStyle.Fill, DrawMode = TabDrawMode.OwnerDrawFixed, SizeMode = TabSizeMode.Fixed, ItemSize = new Size(156, 30) };
        tabs.DrawItem += delegate(object sender, DrawItemEventArgs e) {
            bool selected = e.Index == tabs.SelectedIndex;
            using (var background = new SolidBrush(selected ? Color.FromArgb(238, 245, 253) : Color.FromArgb(248, 250, 252))) e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, selected ? Color.FromArgb(30, 91, 156) : Color.FromArgb(77, 91, 111), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected) using (var pen = new Pen(Color.FromArgb(40, 104, 168), Math.Max(2, DeviceDpi / 48f))) e.Graphics.DrawLine(pen, e.Bounds.Left + 18, e.Bounds.Bottom - 2, e.Bounds.Right - 18, e.Bounds.Bottom - 2);
        };
        var general = new TabPage("常规") { Padding = new Padding(8), AutoScroll = true, BackColor = Color.White };
        var models = new TabPage("转写") { Padding = new Padding(8), AutoScroll = true, BackColor = Color.White };
        var about = new TabPage("关于与更新") { Padding = new Padding(8), AutoScroll = true, BackColor = Color.White };
        tabs.TabPages.AddRange(new[] { general, models, about });
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 14 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        for (int row = 0; row < 14; row++) layout.RowStyles.Add(row == 3 || row == 8 || row == 10
            ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, row == 0 ? 36 : row == 11 || row == 12 ? 24 : row == 13 ? 0 : 26));
        var current = snapshot.Values;
        AddField(layout, 0, "保存目录", "vault_root", current);
        var browse = new Button { Text = "选择…", Dock = DockStyle.Fill, Margin = new Padding(4, 1, 0, 4) };
        browse.Click += delegate { Browse(); }; layout.Controls.Add(browse, 2, 0);
        AddField(layout, 1, "会议子文件夹", "meeting_folder", current);
        AddField(layout, 2, "速记子文件夹", "quick_note_folder", current);
        var folders = new Label { Text = "子文件夹位于保存目录内。修改后用于新内容，已有文件保留在原处。", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Color.FromArgb(85, 101, 123), Padding = new Padding(0, 2, 0, 4) };
        layout.Controls.Add(folders, 0, 3); layout.SetColumnSpan(folders, 3);
        AddField(layout, 4, "开始 / 结束", "hotkey", current);
        AddField(layout, 5, "麦克风暂停", "pause_hotkey", current);
        AddField(layout, 6, "标记重点", "bookmark_hotkey", current);
        AddField(layout, 7, "打开速记", "quick_note_hotkey", current);
        shortcutHint = new Label { Text = "点击快捷键后按组合键，Esc 取消，保存后生效。\n麦克风暂停不影响电脑声音。", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Color.FromArgb(85, 101, 123), Padding = new Padding(0, 2, 0, 4) };
        layout.Controls.Add(shortcutHint, 0, 8); layout.SetColumnSpan(shortcutHint, 3);
        AddField(layout, 9, "录音保护（秒）", "recording_protection_seconds", current);
        var protection = new Label { Text = "不足此时长的录音不保留、不转录；0 关闭。暂停麦克风仍计时。", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = Color.FromArgb(85, 101, 123), Padding = new Padding(0, 2, 0, 4) };
        layout.Controls.Add(protection, 0, 10); layout.SetColumnSpan(protection, 3);
        originalStartup = DesktopServices.StartsWithWindows;
        startup = new CheckBox { Name = "start_with_windows", Text = "登录 Windows 后启动 verbit", Checked = originalStartup, Dock = DockStyle.Fill, AccessibleName = "开机自启" };
        layout.Controls.Add(startup, 0, 11); layout.SetColumnSpan(startup, 3);
        automaticUpdates = new CheckBox { Name = "check_updates", Text = "自动检查更新（启动时及每 24 小时）", Checked = Convert.ToBoolean(current["check_updates"]), Dock = DockStyle.Fill, AccessibleName = "自动检查更新" };
        layout.Controls.Add(automaticUpdates, 0, 12); layout.SetColumnSpan(automaticUpdates, 3);
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(76, 28), DialogResult = DialogResult.Cancel };
        save.Click += delegate { SaveSettings(); }; cancel.Click += delegate { Close(); };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        general.Controls.Add(layout);
        BuildModels(models, current); BuildAbout(about);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 4, 0, 0), RowCount = 2, ColumnCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        footer.Controls.Add(error, 0, 0); footer.Controls.Add(actions, 0, 1);
        error.TextChanged += delegate {
            int height = error.Text.Length == 0 ? 0 : TextRenderer.MeasureText(error.Text, Font, new Size(Math.Max(100, footer.Width), int.MaxValue), TextFormatFlags.WordBreak).Height + 8 * DeviceDpi / 96;
            footer.RowStyles[0].Height = height; footer.Height = 44 * DeviceDpi / 96 + height;
        };
        Controls.Add(tabs); Controls.Add(footer); AcceptButton = save; CancelButton = cancel;
        if (firstRun) {
            save.Text = "保存并启动";
        }
        StyleControls(this); StyleButton(save, true);
        Deactivate += delegate { CancelKeyCapture(); };
        engineTimer.Tick += delegate { RefreshEngine(); }; engineTimer.Start();
        ResumeLayout(false);
    }
    void CancelKeyCapture() { foreach (var input in inputs.Values) { var recorder = input as HotkeyInput; if (recorder != null) recorder.FinishCapture(false); } }
    protected override void OnFormClosing(FormClosingEventArgs e) { CancelKeyCapture(); base.OnFormClosing(e); }
    static void StyleButton(Button button, bool primary) {
        // FlatAppearance setters invalidate even when their values are unchanged.
        // Readiness polling must not repaint the page (including under an open popup).
        if (button.FlatStyle != FlatStyle.Flat) button.FlatStyle = FlatStyle.Flat;
        if (button.FlatAppearance.BorderSize != 1) button.FlatAppearance.BorderSize = 1;
        Color border = primary ? Color.FromArgb(40, 104, 168) : Color.FromArgb(211, 220, 231);
        Color background = primary ? Color.FromArgb(40, 104, 168) : Color.White;
        Color foreground = primary ? Color.White : Color.FromArgb(42, 65, 92);
        Color hover = primary ? Color.FromArgb(31, 85, 143) : Color.FromArgb(240, 245, 251);
        if (button.FlatAppearance.BorderColor != border) button.FlatAppearance.BorderColor = border;
        if (button.BackColor != background) button.BackColor = background;
        if (button.ForeColor != foreground) button.ForeColor = foreground;
        if (button.FlatAppearance.MouseOverBackColor != hover) button.FlatAppearance.MouseOverBackColor = hover;
        if (button.MinimumSize != new Size(80, 30)) button.MinimumSize = new Size(80, 30);
        if (button.Cursor != Cursors.Hand) button.Cursor = Cursors.Hand;
    }
    static void StyleControls(Control root) {
        foreach (Control control in root.Controls) {
            var button = control as Button; if (button != null) StyleButton(button, false);
            var field = control as TextBox; if (field != null) { field.BorderStyle = BorderStyle.FixedSingle; field.BackColor = Color.FromArgb(252, 253, 255); if (!(field is HotkeyInput)) VerbitMenu.AttachTextMenu(field); }
            StyleControls(control);
        }
    }
    void AddChoice(TableLayoutPanel layout, int row, string label, string key, Dictionary<string, object> current, string[] values, string[] captions) {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        var input = new ComboBox { Name = key, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = label };
        input.Items.AddRange(captions); input.SelectedIndex = Array.IndexOf(values, (string)current[key]); input.Tag = values;
        choices.Add(key, input); layout.Controls.Add(input, 1, row); layout.SetColumnSpan(input, 2);
    }
    string Choice(string key) { return ((string[])choices[key].Tag)[choices[key].SelectedIndex]; }
    void BuildModels(TabPage page, Dictionary<string, object> current) {
        var container = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        autoTranscribe = new CheckBox { Name = "auto_transcribe", Text = "录音 + 转写", AccessibleName = "结束录音后自动转写", Checked = Convert.ToBoolean(current["auto_transcribe"]), Dock = DockStyle.Top, Height = 36 };
        var modeHint = new Label { Name = "recording_mode_hint", Dock = DockStyle.Top, Height = 44, ForeColor = Color.FromArgb(76, 92, 114) };
        container.Controls.Add(autoTranscribe, 0, 0); container.Controls.Add(modeHint, 0, 1);
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 7 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var local = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 12) };
        local.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        engineTitle = new Label { Name = "transcription_readiness", Dock = DockStyle.Top, Height = 28 };
        engineDetail = new Label { Name = "engine_detail", Dock = DockStyle.Top, Height = 48, ForeColor = Color.FromArgb(76, 92, 114) };
        local.Controls.Add(engineTitle, 0, 0); local.Controls.Add(engineDetail, 0, 1);
        stack.Controls.Add(local, 0, 0);
        var disclosure = new Button { Text = "展开高级选项", Name = "transcription_advanced", Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 8, 0, 8) };
        stack.Controls.Add(disclosure, 0, 4);
        var advanced = new TableLayoutPanel { Name = "transcription_advanced_fields", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false, Margin = Padding.Empty };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var modeGrid = SettingsGrid(); modeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        bool keepLocal = (string)current["transcription_mode"] == "local" || LocalEngineState.Read(MeetingFiles.Home).Ready;
        AddChoice(modeGrid, 0, "转写方式", "transcription_mode", current, keepLocal ? new[] { "local", "api" } : new[] { "api" }, keepLocal ? new[] { "已有本地环境（兼容）", "自定义第三方 API" } : new[] { "自定义第三方 API" }); stack.Controls.Add(modeGrid, 0, 2);
        var apiFields = SettingsGrid();
        for (int row = 0; row < 5; row++) apiFields.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 4 ? 44 : 32));
        AddField(apiFields, 0, "API 地址", "api_base_url", current); AddField(apiFields, 1, "API 模型 ID", "api_model", current);
        AddField(apiFields, 2, "密钥变量名", "api_key_env", current); AddField(apiFields, 3, "超时（秒）", "api_timeout_seconds", current);
        var secret = new Label { Text = "只支持音频转写接口。密钥通过 Windows 用户环境变量配置，不写入配置文件；提交音频前还会询问。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(76, 92, 114), Padding = new Padding(0, 4, 0, 0) };
        apiFields.Controls.Add(secret, 0, 4); apiFields.SetColumnSpan(secret, 3); stack.Controls.Add(apiFields, 0, 3);
        var grid = SettingsGrid();
        for (int row = 0; row < 4; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 3 ? 48 : 36));
        AddField(grid, 0, "术语（可选）", "asr_terms", current); inputs["asr_terms"].MaxLength = 1000;
        AddField(grid, 1, "转写上下文", "asr_context", current); inputs["asr_context"].MaxLength = 2000;
        AddChoice(grid, 2, "轻量降噪", "asr_denoise", current, new[] { "light", "off" }, new[] { "开启 · 降低低频轰鸣", "关闭" });
        echoFilter = new CheckBox { Name = "asr_echo_filter", Text = "去除有高置信证据的重复回声（不修改原录音）", Checked = Convert.ToBoolean(current["asr_echo_filter"]), Dock = DockStyle.Fill };
        grid.Controls.Add(echoFilter, 0, 3); grid.SetColumnSpan(echoFilter, 3); advanced.Controls.Add(grid, 0, 2);
        stack.Controls.Add(advanced, 0, 5);
        var guide = new LinkLabel { Text = "第三方 API 配置说明", Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 14, 0, 0) };
        guide.LinkClicked += delegate { OpenLink(Path.Combine(MeetingFiles.Home, "documentation", "install-windows.md")); };
        stack.Controls.Add(guide, 0, 6);
        disclosure.Click += delegate {
            stack.SuspendLayout();
            try { advanced.Visible = !advanced.Visible; disclosure.Text = advanced.Visible ? "收起高级选项" : "展开高级选项"; }
            finally { stack.ResumeLayout(true); }
        };
        Action refresh = delegate {
            bool api = Choice("transcription_mode") == "api";
            page.SuspendLayout(); stack.SuspendLayout(); advanced.SuspendLayout();
            try {
                apiFields.Visible = api; local.Visible = !api;
                choices["asr_denoise"].Enabled = echoFilter.Enabled = !api;
                echoFilter.Text = api ? "降噪与回声处理由第三方服务负责" : "去除有高置信证据的重复回声（不修改原录音）";
                foreach (string key in new[] { "api_base_url", "api_model", "api_key_env", "api_timeout_seconds" }) inputs[key].Enabled = api;
                RefreshEngine();
            } finally { advanced.ResumeLayout(true); stack.ResumeLayout(true); page.ResumeLayout(true); }
        };
        choices["transcription_mode"].SelectedIndexChanged += delegate { refresh(); };
        Activated += delegate { refresh(); }; refresh();
        container.Controls.Add(stack, 0, 2);
        Action updateRecordingMode = delegate {
            container.SuspendLayout();
            try {
                stack.Visible = autoTranscribe.Checked;
                modeHint.Text = autoTranscribe.Checked ? "结束录音先保存 MP3，再后台转写。\n配置第三方音频转写 API 后使用；录音不依赖转写。" : "仅录音 · 不需要模型或网络，结束后保存 MP3。\n可稍后选配 API；已有本地环境保留兼容。";
            } finally { container.ResumeLayout(true); }
        };
        autoTranscribe.CheckedChanged += delegate { updateRecordingMode(); }; updateRecordingMode();
        page.Controls.Add(container);
    }
    static TableLayoutPanel SettingsGrid() {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); return grid;
    }
    void RefreshEngine() {
        if (IsDisposed || engineTitle == null || Choice("transcription_mode") != "local") return;
        var state = LocalEngineState.Read(MeetingFiles.Home);
        string title = state.Ready ? "已有本地环境可用（兼容模式）" : "已有本地环境需要检查";
        string detail = "首期不提供推荐模型或下载入口。原有模型和配置保留；需要时可改用第三方音频转写 API。";
        if (engineTitle.Text != title) engineTitle.Text = title;
        if (engineDetail.Text != detail) engineDetail.Text = detail;
    }
    void OpenLink(string url) { try { DesktopServices.Open(url); } catch (Exception failure) { error.Text = "无法打开：" + failure.Message; } }
    void BuildAbout(TabPage page) {
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        stack.Controls.Add(new Label { Text = "版本 " + DesktopServices.Version, AutoSize = true, Padding = new Padding(0, 0, 0, 12) });
        stack.Controls.Add(new Label { Text = "Windows 速记与本地会议记录\n许可：MIT · 本地模式下音频不上传", AutoSize = true, Padding = new Padding(0, 0, 0, 12) });
        var github = new LinkLabel { Text = "GitHub · Qjzn/verbit", AutoSize = true, AccessibleName = "GitHub 项目地址" };
        using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Verbit.GitHub.png"))
            if (stream != null) using (var original = Image.FromStream(stream)) githubImage = new Bitmap(original, new Size(16, 16));
        github.Image = githubImage; github.ImageAlign = ContentAlignment.MiddleLeft; github.Padding = new Padding(22, 4, 4, 4);
        github.LinkClicked += delegate { OpenLink(DesktopServices.Repository); }; stack.Controls.Add(github);
        var notes = new LinkLabel { Text = "版本说明（本机离线可读）", AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        notes.LinkClicked += delegate { OpenLink(Path.Combine(MeetingFiles.Home, "documentation", "changelog.md")); }; stack.Controls.Add(notes);
        var releases = new LinkLabel { Text = "GitHub 发布与安装包", AutoSize = true, Padding = new Padding(0, 0, 0, 12) };
        releases.LinkClicked += delegate { OpenLink(DesktopServices.Repository + "/releases"); }; stack.Controls.Add(releases);
        var status = new Label { Name = "update_status", Text = "更新需网络连接。私有仓库需在浏览器登录有权限的账号。", AutoSize = true, MaximumSize = new Size(430, 0) };
        var check = new Button { Text = "检查更新", AutoSize = true, Name = "check_update_now" };
        check.Click += async delegate {
            check.Enabled = false; status.Text = "正在检查…";
            string result;
            try { result = await System.Threading.Tasks.Task.Run(delegate { return DesktopServices.CheckUpdates(); }); }
            catch (Exception failure) { result = failure.Message; }
            if (!IsDisposed) { status.Text = result; check.Enabled = true; }
        };
        stack.Controls.Add(check); stack.Controls.Add(status);
        stack.Controls.Add(new Label { Text = "自动检查默认关闭，只查询版本，不上传会议内容。\n安装更新由你确认，不会在录音或转写时替换程序。", AutoSize = true, MaximumSize = new Size(430, 0), Padding = new Padding(0, 12, 0, 0) });
        page.Controls.Add(stack);
    }
    void AddField(TableLayoutPanel layout, int row, string label, string key, Dictionary<string, object> current) {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false }, 0, row);
        var recorder = key == "hotkey" || key.EndsWith("_hotkey") ? new HotkeyInput() : null;
        TextBox input = recorder == null ? new TextBox() : recorder;
        input.Name = key; input.Text = MeetingFiles.Text(current, key, ""); input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 4); input.MaxLength = key == "vault_root" ? 32767 : 260;
        if (recorder != null) {
            recorder.CaptureChanged += delegate(bool capturing) {
                if (HotkeyCaptureChanged != null) HotkeyCaptureChanged(capturing);
                save.Enabled = !capturing;
                if (shortcutHint != null) shortcutHint.Text = capturing ? "按下组合键，松开后完成；Esc 或点击别处取消。" : "点击快捷键后按组合键，Esc 取消，保存后生效。\n麦克风暂停不影响电脑声音。";
            };
            recorder.Recorded += ValidateShortcutChoices;
            recorder.CaptureError += delegate(string message) { error.Text = message; save.Enabled = true; };
        }
        input.AccessibleName = label; inputs.Add(key, input); layout.Controls.Add(input, 1, row);
        if (key != "vault_root") layout.SetColumnSpan(input, 2);
    }
    void ValidateShortcutChoices() {
        var used = new Dictionary<string, string>();
        foreach (string key in new[] { "hotkey", "pause_hotkey", "bookmark_hotkey", "quick_note_hotkey" }) {
            uint modifiers, code;
            try { MeetingConfig.ParseHotkey(inputs[key].Text, out modifiers, out code); } catch (InvalidOperationException) { return; }
            string identity = modifiers + ":" + code;
            if (used.ContainsKey(identity)) {
                shortcutHint.Text = inputs[used[identity]].AccessibleName + "与" + inputs[key].AccessibleName + "不能使用同一快捷键。";
                shortcutHint.ForeColor = Color.FromArgb(171, 37, 37); return;
            }
            used.Add(identity, key);
        }
        shortcutHint.ForeColor = Color.FromArgb(85, 101, 123);
    }
    void Browse() {
        using (var dialog = new FolderBrowserDialog { Description = MeetingBrand.Name + " · 选择会议与速记的保存目录", ShowNewFolderButton = true }) {
            string root = inputs["vault_root"].Text;
            if (Directory.Exists(root)) dialog.SelectedPath = root;
            if (dialog.ShowDialog(this) == DialogResult.OK) inputs["vault_root"].Text = dialog.SelectedPath;
        }
    }
    void SaveSettings() {
        error.Text = ""; save.Enabled = false;
        try {
            var edits = new Dictionary<string, object>();
            foreach (string key in new[] { "api_base_url", "api_model", "api_key_env", "asr_terms", "asr_context" }) edits[key] = inputs[key].Text.Trim();
            edits["transcription_mode"] = Choice("transcription_mode"); edits["asr_denoise"] = Choice("asr_denoise");
            edits["asr_echo_filter"] = echoFilter.Checked; edits["check_updates"] = automaticUpdates.Checked;
            edits["auto_transcribe"] = autoTranscribe.Checked;
            int timeout;
            if (!int.TryParse(inputs["api_timeout_seconds"].Text, out timeout)) throw new InvalidOperationException("API 超时请填写 10 到 600 之间的整数秒。");
            edits["api_timeout_seconds"] = timeout;
            var candidate = snapshot.CreateCandidate(inputs["vault_root"].Text, inputs["meeting_folder"].Text, inputs["quick_note_folder"].Text,
                inputs["hotkey"].Text, inputs["pause_hotkey"].Text, inputs["bookmark_hotkey"].Text, inputs["quick_note_hotkey"].Text, inputs["recording_protection_seconds"].Text, edits);
            bool changed = startup.Checked != originalStartup, accepted = false;
            try {
                if (changed) DesktopServices.SetStartup(startup.Checked);
                string failure = apply(candidate);
                if (!string.IsNullOrEmpty(failure)) { error.Text = failure; return; }
                accepted = true;
            } finally { if (changed && !accepted) DesktopServices.SetStartup(originalStartup); }
            DialogResult = DialogResult.OK; Close();
        } catch (Exception failure) { error.Text = failure.Message; }
        finally { if (!IsDisposed) save.Enabled = true; }
    }
    void RestoreDpiFont() {
        var previous = dpiFont;
        dpiFont = new Font("Microsoft YaHei UI", 12 * DeviceDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont; previous.Dispose();
        PerformLayout();
        FitToWorkArea(Screen.FromControl(this).WorkingArea);
        var tabs = (TabControl)Controls.Find("settingsTabs", true)[0];
        tabs.PerformLayout();
        // WinForms leaves inactive pages at the previous monitor's DPI bounds.
        foreach (TabPage page in tabs.TabPages) page.Bounds = tabs.DisplayRectangle;
    }
    void FitToWorkArea(Rectangle work) {
        Bounds = DesktopServices.FitWindowBounds(Bounds, work);
        LayoutTabHeaders();
    }
    void LayoutTabHeaders() {
        Control[] found = Controls.Find("settingsTabs", true);
        if (found.Length == 0) return;
        var tabs = (TabControl)found[0];
        // The native strip reserves two pixels at either edge. Share the rest
        // instead of capping each tab and leaving an unused trailing region.
        var size = new Size(Math.Max(1, (tabs.ClientSize.Width - 4) / Math.Max(1, tabs.TabCount)), (int)Math.Round(30 * DeviceDpi / 96f));
        if (tabs.ItemSize != size) tabs.ItemSize = size;
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutTabHeaders(); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); RestoreDpiFont(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e); RestoreDpiFont();
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { if (!IsDisposed) RestoreDpiFont(); }));
    }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && dpiFont != null) { dpiFont.Dispose(); dpiFont = null; }
        if (disposing && githubImage != null) { githubImage.Dispose(); githubImage = null; }
        if (disposing) engineTimer.Dispose();
    }
}
