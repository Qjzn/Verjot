using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

internal sealed class SilenceMonitor {
    double? quietSince;
    bool warned;
    public void Reset() { quietSince = null; warned = false; }
    public bool Observe(double seconds, float microphone, float system, bool microphoneMuted, bool paused, int limit) {
        if (limit == 0 || paused || (!microphoneMuted && microphone > .003f) || system > .003f) { Reset(); return false; }
        if (!quietSince.HasValue || seconds < quietSince.Value) quietSince = seconds;
        if (warned || seconds - quietSince.Value < limit) return false;
        warned = true;
        return true;
    }
}

internal static class MeetingRename {
    public static string Filename(string title) {
        title = title.Trim().Normalize();
        if (title.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 3);
        if (title.Length == 0 || title.Length > 80 || title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            title.EndsWith(".") || title.EndsWith(" ") || Regex.IsMatch(title, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("名称需为 1–80 个字符，不能包含路径、换行或 Windows 保留名称。");
        foreach (char value in title) if (char.IsControl(value) || char.GetUnicodeCategory(value) == System.Globalization.UnicodeCategory.Format)
            throw new InvalidOperationException("会议名称不能包含不可见控制字符。");
        return title + ".md";
    }
    public static string Apply(SessionCatalog catalog, string session, string title) {
        SessionRecord record = catalog.Get(session);
        if (record == null || record.State != "completed" || record.WorkerAlive)
            throw new InvalidOperationException("请等待这场会议转写完成后再命名。");
        string filename = Filename(title);
        string original = record.NotePath;
        string renamed = Path.Combine(Path.GetDirectoryName(original), filename);
        MeetingConfig.CheckOrdinaryPath(original);
        MeetingConfig.CheckOrdinaryPath(renamed);
        bool same = string.Equals(original, renamed, StringComparison.OrdinalIgnoreCase);
        if (!same && (File.Exists(renamed) || Directory.Exists(renamed))) throw new IOException("已有同名文件，请换一个会议名称；原笔记未修改。");
        string metadataPath = Path.Combine(session, "session.json"), statusPath = Path.Combine(session, "status.json");
        var metadata = MeetingFiles.ReadJson(metadataPath);
        var status = MeetingFiles.ReadJson(statusPath);
        string previousMetadata = MeetingFiles.Json.Serialize(metadata), previousStatus = MeetingFiles.Json.Serialize(status);
        metadata["title"] = Path.GetFileNameWithoutExtension(filename);
        metadata["note_filename"] = same ? Path.GetFileName(original) : filename;
        status["note_path"] = same ? original : renamed;
        object managed;
        if (status.TryGetValue("managed_note", out managed) && managed is Dictionary<string, object>) {
            var region = (Dictionary<string, object>)managed;
            if (string.Equals(MeetingFiles.Text(region, "path", ""), original, StringComparison.OrdinalIgnoreCase)) region["path"] = same ? original : renamed;
        }
        bool moved = false;
        try {
            if (!same) { File.Move(original, renamed); moved = true; }
            MeetingFiles.WriteJson(metadataPath, metadata);
            MeetingFiles.WriteJson(statusPath, status);
        } catch {
            // Never overwrite or delete a destination. Restore the exact old
            // metadata if a local persistence step fails after the move.
            try {
                if (moved) File.Move(renamed, original);
                MeetingFiles.WriteJson(metadataPath, MeetingFiles.Json.Deserialize<Dictionary<string, object>>(previousMetadata));
                MeetingFiles.WriteJson(statusPath, MeetingFiles.Json.Deserialize<Dictionary<string, object>>(previousStatus));
            } catch (Exception rollback) { MeetingFiles.Log(rollback); }
            throw;
        }
        return same ? original : renamed;
    }
}

internal sealed class MeetingNameDialog : Form {
    readonly TextBox name = new TextBox { Dock = DockStyle.Top, MaxLength = 83 };
    Font dpiFont;
    public string MeetingTitle { get { return name.Text; } }
    public MeetingNameDialog(string current) {
        SuspendLayout();
        Text = "命名这场会议";
        dpiFont = new Font("Microsoft YaHei UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(330, 112);
        Padding = new Padding(12);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var label = new Label { Text = "会议名称（转录文件名）", Dock = DockStyle.Top, Height = 25 };
        name.Text = current;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        actions.Controls.Add(save); actions.Controls.Add(cancel);
        Controls.Add(name); Controls.Add(label); Controls.Add(actions);
        AcceptButton = save; CancelButton = cancel;
        Shown += delegate { name.Focus(); name.SelectAll(); };
        AutoScaleDimensions = new SizeF(96, 96);
        ResumeLayout(false);
    }
    void RestoreDpiFont() {
        var previous = dpiFont;
        dpiFont = new Font("Microsoft YaHei UI", 9 * DeviceDpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
        Font = dpiFont;
        previous.Dispose();
        Padding = new Padding((int)Math.Round(12 * DeviceDpi / 96f));
        foreach (Control child in Controls) {
            if (child is Label) child.Height = (int)Math.Round(25 * DeviceDpi / 96f);
            if (child is FlowLayoutPanel) child.Height = (int)Math.Round(32 * DeviceDpi / 96f);
        }
        ClientSize = new Size((int)Math.Round(330 * DeviceDpi / 96f), (int)Math.Round(112 * DeviceDpi / 96f));
    }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        RestoreDpiFont();
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        RestoreDpiFont();
    }
    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        if (disposing && dpiFont != null) { dpiFont.Dispose(); dpiFont = null; }
    }
}
