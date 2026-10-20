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
    public static MeetingSettingsSnapshot CreateNew(string path, string suggestedRoot) {
        path = Path.GetFullPath(path);
        MeetingConfig.CheckOrdinaryPath(path);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("配置已经存在，请重新启动以加载；本次未覆盖配置。");
        var defaults = MeetingConfig.Validate(new Dictionary<string, object> { { "vault_root", MeetingFiles.Home }, { "open_in_obsidian", false } });
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
internal sealed class MeetingSettingsDialog : Form {
    readonly MeetingSettingsSnapshot snapshot;
    readonly Func<Dictionary<string, object>, string> apply;
    readonly Dictionary<string, TextBox> inputs = new Dictionary<string, TextBox>();
    readonly Dictionary<string, ComboBox> choices = new Dictionary<string, ComboBox>();
    CheckBox startup, automaticUpdates, echoFilter;
    bool originalStartup;
    readonly Label error = new Label { AutoSize = false, ForeColor = Color.FromArgb(171, 37, 37), Dock = DockStyle.Fill };
    readonly Button save = new Button { Text = "保存", AutoSize = true, MinimumSize = new Size(76, 28) };
    Font dpiFont;
    Image githubImage;
    public void ShowAbout() { ((TabControl)Controls.Find("settingsTabs", true)[0]).SelectedIndex = 2; }
    public MeetingSettingsDialog(MeetingSettingsSnapshot snapshot, Func<Dictionary<string, object>, string> apply, bool firstRun = false) {
        if (snapshot == null || apply == null) throw new ArgumentNullException(snapshot == null ? "snapshot" : "apply");
        this.snapshot = snapshot; this.apply = apply;
        SuspendLayout();
        Text = firstRun ? "verbit · 首次设置" : "verbit · 设置"; Icon = MeetingBrand.IdleIcon;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        dpiFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel); Font = dpiFont;
        ClientSize = new Size(520, 590); Padding = new Padding(16);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Name = "settingsTabs", Dock = DockStyle.Fill };
        var general = new TabPage("常规") { Padding = new Padding(8) };
        var models = new TabPage("转写") { Padding = new Padding(8), AutoScroll = true };
        var about = new TabPage("关于与更新") { Padding = new Padding(12), AutoScroll = true };
        tabs.TabPages.AddRange(new[] { general, models, about });
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
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 76, RowCount = 2, ColumnCount = 1 };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        footer.Controls.Add(error, 0, 0); footer.Controls.Add(actions, 0, 1);
        Controls.Add(tabs); Controls.Add(footer); AcceptButton = save; CancelButton = cancel;
        if (firstRun) {
            save.Text = "保存并启动";
            ClientSize = new Size(520, 642);
            var intro = new Label { Text = "先确认保存目录和快捷键。保存后驻留托盘，速记可直接使用；会议转写需另配本地模型或 API。", Dock = DockStyle.Top, Height = 52, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 0, 0, 8) };
            Controls.Add(intro);
        }
        ResumeLayout(false);
    }
    void AddChoice(TableLayoutPanel layout, int row, string label, string key, Dictionary<string, object> current, string[] values, string[] captions) {
        layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        var input = new ComboBox { Name = key, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = label };
        input.Items.AddRange(captions); input.SelectedIndex = Array.IndexOf(values, (string)current[key]); input.Tag = values;
        choices.Add(key, input); layout.Controls.Add(input, 1, row); layout.SetColumnSpan(input, 2);
    }
    string Choice(string key) { return ((string[])choices[key].Tag)[choices[key].SelectedIndex]; }
    void BuildModels(TabPage page, Dictionary<string, object> current) {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 12 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        for (int row = 0; row < 12; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 1 || row == 11 ? 58 : 32));
        AddChoice(grid, 0, "转写方式", "transcription_mode", current, new[] { "local", "api" }, new[] { "本地 · Qwen3-ASR-1.7B", "API · 音频将发送至服务商" });
        var hint = new Label { Text = "本地模型需先按安装说明准备依赖和权重。API 仅支持 /audio/transcriptions；普通聊天模型不能直接替代。", Dock = DockStyle.Fill };
        grid.Controls.Add(hint, 0, 1); grid.SetColumnSpan(hint, 3);
        AddField(grid, 2, "API 地址", "api_base_url", current);
        AddField(grid, 3, "API 模型 ID", "api_model", current);
        AddField(grid, 4, "密钥变量名", "api_key_env", current);
        AddField(grid, 5, "超时（秒）", "api_timeout_seconds", current);
        AddField(grid, 6, "术语提示", "asr_terms", current); inputs["asr_terms"].MaxLength = 1000;
        AddField(grid, 7, "转写上下文", "asr_context", current); inputs["asr_context"].MaxLength = 2000;
        AddChoice(grid, 8, "轻量降噪", "asr_denoise", current, new[] { "light", "off" }, new[] { "开启 · 降低低频轰鸣", "关闭" });
        echoFilter = new CheckBox { Name = "asr_echo_filter", Text = "去除有高置信证据的重复回声（不修改原录音）", Checked = Convert.ToBoolean(current["asr_echo_filter"]), Dock = DockStyle.Fill };
        grid.Controls.Add(echoFilter, 0, 9); grid.SetColumnSpan(echoFilter, 3);
        var guide = new LinkLabel { Text = "打开本地模型与 API 安装说明", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        guide.LinkClicked += delegate { OpenLink(Path.Combine(MeetingFiles.Home, "documentation", "install-windows.md")); };
        grid.Controls.Add(guide, 0, 10); grid.SetColumnSpan(guide, 3);
        var secret = new Label { Text = "这里只填环境变量名，不填密钥。请在 Windows 用户环境变量中配置密钥后重启 verbit。保存仅影响新会议，不会下载或切换实验模型。", Dock = DockStyle.Fill };
        grid.Controls.Add(secret, 0, 11); grid.SetColumnSpan(secret, 3);
        Action refresh = delegate { bool api = Choice("transcription_mode") == "api"; foreach (string key in new[] { "api_base_url", "api_model", "api_key_env", "api_timeout_seconds" }) inputs[key].Enabled = api; };
        choices["transcription_mode"].SelectedIndexChanged += delegate { refresh(); }; refresh();
        page.Controls.Add(grid);
    }
    void OpenLink(string url) { try { DesktopServices.Open(url); } catch (Exception failure) { error.Text = "无法打开：" + failure.Message; } }
    void BuildAbout(TabPage page) {
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        stack.Controls.Add(new Label { Text = "verbit  " + DesktopServices.Version, AutoSize = true, Padding = new Padding(0, 0, 0, 12) });
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
        var input = new TextBox { Name = key, Text = MeetingFiles.Text(current, key, ""), Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 4), MaxLength = key == "vault_root" ? 32767 : 260 };
        input.AccessibleName = label; inputs.Add(key, input); layout.Controls.Add(input, 1, row);
        if (key != "vault_root") layout.SetColumnSpan(input, 2);
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
            var candidate = snapshot.CreateCandidate(inputs["vault_root"].Text, inputs["meeting_folder"].Text, inputs["quick_note_folder"].Text,
                inputs["hotkey"].Text, inputs["pause_hotkey"].Text, inputs["bookmark_hotkey"].Text, inputs["quick_note_hotkey"].Text, inputs["recording_protection_seconds"].Text);
            foreach (string key in new[] { "api_base_url", "api_model", "api_key_env", "asr_terms", "asr_context" }) candidate[key] = inputs[key].Text.Trim();
            candidate["transcription_mode"] = Choice("transcription_mode"); candidate["asr_denoise"] = Choice("asr_denoise");
            candidate["asr_echo_filter"] = echoFilter.Checked; candidate["check_updates"] = automaticUpdates.Checked;
            int timeout;
            if (!int.TryParse(inputs["api_timeout_seconds"].Text, out timeout)) throw new InvalidOperationException("API 超时请填写 10 到 600 之间的整数秒。");
            candidate["api_timeout_seconds"] = timeout; candidate = MeetingConfig.Validate(candidate);
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
        dpiFont = new Font("Microsoft YaHei UI", 9 * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont; previous.Dispose();
        PerformLayout();
        var tabs = (TabControl)Controls.Find("settingsTabs", true)[0];
        tabs.PerformLayout();
        // WinForms leaves inactive pages at the previous monitor's DPI bounds.
        foreach (TabPage page in tabs.TabPages) page.Bounds = tabs.DisplayRectangle;
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); RestoreDpiFont(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); RestoreDpiFont(); }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && dpiFont != null) { dpiFont.Dispose(); dpiFont = null; }
        if (disposing && githubImage != null) { githubImage.Dispose(); githubImage = null; }
    }
}
