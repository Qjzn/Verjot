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
        return new MeetingSettingsSnapshot(path, bytes, MeetingConfig.Validate(parsed));
    }
    static Dictionary<string, object> Clone(Dictionary<string, object> source) {
        return MeetingFiles.Json.Deserialize<Dictionary<string, object>>(MeetingFiles.Json.Serialize(source));
    }
    public Dictionary<string, object> Values { get { return Clone(values); } }
    public Dictionary<string, object> CreateCandidate(string root, string meetingFolder, string quickFolder,
            string start, string pause, string bookmark, string quickNote, string protectionSeconds = null) {
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
        return MeetingConfig.Validate(candidate);
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
        var validated = MeetingConfig.Validate(Clone(candidate));
        byte[] replacement = Serialize(validated);
        MeetingConfig.CheckOrdinaryPath(path);
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

// Native controls keep the settings window small and retain normal keyboard,
// IME, DPI and accessibility behavior. Show() makes this a modeless window.
internal sealed class MeetingSettingsDialog : Form {
    readonly MeetingSettingsSnapshot snapshot;
    readonly Func<Dictionary<string, object>, string> apply;
    readonly Dictionary<string, TextBox> inputs = new Dictionary<string, TextBox>();
    readonly Label error = new Label { AutoSize = false, ForeColor = Color.FromArgb(171, 37, 37), Dock = DockStyle.Fill };
    readonly Button save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(76, 28) };
    Font dpiFont;
    public MeetingSettingsDialog(MeetingSettingsSnapshot snapshot, Func<Dictionary<string, object>, string> apply) {
        if (snapshot == null || apply == null) throw new ArgumentNullException(snapshot == null ? "snapshot" : "apply");
        this.snapshot = snapshot; this.apply = apply;
        SuspendLayout();
        Text = "verbit · 设置"; Icon = MeetingBrand.IdleIcon;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        dpiFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel); Font = dpiFont;
        ClientSize = new Size(460, 492); Padding = new Padding(16);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 14 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        for (int row = 0; row < 14; row++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 3 || row == 8 ? 38 : row == 11 ? 44 : row == 12 ? 30 : row == 13 ? 0 : 32));
        var current = snapshot.Values;
        AddField(layout, 0, "保存目录", "vault_root", current);
        var browse = new Button { Text = "选择…", Dock = DockStyle.Fill, Margin = new Padding(4, 1, 0, 4) };
        browse.Click += delegate { Browse(); }; layout.Controls.Add(browse, 2, 0);
        AddField(layout, 1, "会议子文件夹", "meeting_folder", current);
        AddField(layout, 2, "速记子文件夹", "quick_note_folder", current);
        var folders = new Label { Text = "子文件夹位于保存目录内。修改后用于新内容，已有文件保留在原处。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
        layout.Controls.Add(folders, 0, 3); layout.SetColumnSpan(folders, 3);
        AddField(layout, 4, "开始 / 结束", "hotkey", current);
        AddField(layout, 5, "暂停 / 继续", "pause_hotkey", current);
        AddField(layout, 6, "标记重点", "bookmark_hotkey", current);
        AddField(layout, 7, "打开速记", "quick_note_hotkey", current);
        var shortcuts = new Label { Text = "快捷键填写示例：Ctrl+Alt+J、Alt+Space。保存后立即生效。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
        layout.Controls.Add(shortcuts, 0, 8); layout.SetColumnSpan(shortcuts, 3);
        AddField(layout, 9, "录音保护（秒）", "recording_protection_seconds", current);
        var protection = new Label { Text = "不足此时长的录音不保留、不转录；0 关闭。暂停时间不计入。", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) };
        layout.Controls.Add(protection, 0, 10); layout.SetColumnSpan(protection, 3);
        layout.Controls.Add(error, 0, 11); layout.SetColumnSpan(error, 3);
        var actions = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var cancel = new Button { Text = "取消", AutoSize = true, MinimumSize = new Size(76, 28), DialogResult = DialogResult.Cancel };
        save.Click += delegate { SaveSettings(); }; cancel.Click += delegate { Close(); };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 12); layout.SetColumnSpan(actions, 3);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        ResumeLayout(false);
    }
    void AddField(TableLayoutPanel layout, int row, string label, string key, Dictionary<string, object> current) {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false }, 0, row);
        var input = new TextBox { Name = key, Text = MeetingFiles.Text(current, key, ""), Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4), MaxLength = key == "vault_root" ? 32767 : 260 };
        input.AccessibleName = label; inputs.Add(key, input); layout.Controls.Add(input, 1, row);
        if (key != "vault_root") layout.SetColumnSpan(input, 2);
    }
    void Browse() {
        using (var dialog = new FolderBrowserDialog { Description = "选择会议与速记的保存目录", ShowNewFolderButton = true }) {
            string root = inputs["vault_root"].Text;
            if (Directory.Exists(root)) dialog.SelectedPath = root;
            if (dialog.ShowDialog(this) == DialogResult.OK) inputs["vault_root"].Text = dialog.SelectedPath;
        }
    }
    void SaveSettings() {
        error.Text = ""; save.Enabled = false;
        try {
            var candidate = snapshot.CreateCandidate(inputs["vault_root"].Text, inputs["meeting_folder"].Text, inputs["quick_note_folder"].Text,
                inputs["hotkey"].Text, inputs["pause_hotkey"].Text, inputs["bookmark_hotkey"].Text, inputs["quick_note_hotkey"].Text, inputs["recording_protection_seconds"].Text);
            string failure = apply(candidate);
            if (!string.IsNullOrEmpty(failure)) { error.Text = failure; return; }
            DialogResult = DialogResult.OK; Close();
        } catch (Exception failure) { error.Text = failure.Message; }
        finally { if (!IsDisposed) save.Enabled = true; }
    }
    void RestoreDpiFont() {
        var previous = dpiFont;
        dpiFont = new Font("Microsoft YaHei UI", 9 * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont; previous.Dispose();
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); RestoreDpiFont(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); RestoreDpiFont(); }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && dpiFont != null) { dpiFont.Dispose(); dpiFont = null; }
    }
}
