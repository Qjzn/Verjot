using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

internal sealed class QuickNoteDocument {
    public DateTime Date { get; internal set; }
    public string Path { get; internal set; }
    public string Text { get; internal set; }
    public string Revision { get; internal set; }
    public bool HasRecovery { get; internal set; }
    public string RecoveryText { get; internal set; }
    public string RecoveryRevision { get; internal set; }
    public string RecoveryPath { get; internal set; }
    internal bool HasBom;
    internal object Owner;
}

internal sealed class QuickNoteConflictException : IOException {
    public QuickNoteConflictException(string message) : base(message) { }
}

// Ordinary Markdown and PNG files remain usable without Tingji. Drafts never go in the vault.
internal sealed class QuickNoteStore {
    public const int MaxImageBytes = 12 * 1024 * 1024;
    public const long MaxImagePixels = 36000000;
    const int MaxNoteBytes = 16 * 1024 * 1024;
    const string MissingRevision = "missing";
    const string DraftHeader = "TingjiQuickNoteDraft:1\n";
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    static readonly byte[] PngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
    readonly string vaultRoot, noteRoot, recoveryRoot;
    readonly object owner = new object();

    public string FolderPath { get { return noteRoot; } }
    internal Action<string> RecycleFile = RecycleLocalFile;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileOperation {
        internal IntPtr Window;
        internal uint Operation;
        [MarshalAs(UnmanagedType.LPWStr)] internal string From, To;
        internal ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] internal bool Aborted;
        internal IntPtr Mappings;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Title;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref ShellFileOperation operation);
    static void RecycleLocalFile(string path) {
        // WANTNUKEWARNING overrides NOCONFIRMATION for an unrecyclable item:
        // Windows must ask, rather than silently deleting it permanently.
        var operation = new ShellFileOperation { Operation = 3, From = path + "\0\0", Flags = 0x40 | 0x10 | 0x4 | 0x400 | 0x4000 };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.Aborted) throw new IOException("无法移入 Windows 回收站，便签已保留。错误码：" + result);
    }
    internal void Recycle(QuickNoteDocument doc) {
        Validate(doc);
        RequireRevision(doc);
        if (File.Exists(doc.RecoveryPath)) throw new IOException("此便签有未处理的恢复草稿，请先打开并处理后再删除。");
        // A new, empty tab has no file to recycle. The revision check above
        // still protects any file another editor created at the same path.
        if (doc.Revision == MissingRevision) return;
        RecycleFile(doc.Path);
        if (File.Exists(doc.Path)) throw new IOException("便签未移入回收站，请检查文件权限。");
    }

    public QuickNoteStore(string vaultRoot, string relativeFolder, string recoveryRoot) {
        this.vaultRoot = Full(vaultRoot);
        string folder = SafeRelative(relativeFolder);
        if (String.IsNullOrWhiteSpace(folder)) throw new ArgumentException("速记文件夹不能为空。");
        noteRoot = Full(System.IO.Path.Combine(this.vaultRoot, folder));
        if (!Within(noteRoot, this.vaultRoot)) throw new ArgumentException("速记文件夹必须位于笔记库内。");
        this.recoveryRoot = Full(recoveryRoot);
        if (Within(this.recoveryRoot, this.vaultRoot) || Same(this.recoveryRoot, this.vaultRoot))
            throw new ArgumentException("恢复草稿必须保存在笔记库以外。");
        CheckOrdinaryPath(this.vaultRoot);
        CheckOrdinaryPath(noteRoot);
        CheckOrdinaryPath(this.recoveryRoot);
    }

    public QuickNoteDocument Load(DateTime date) {
        return LoadPath(NotePath(date), date.Date);
    }
    internal QuickNoteDocument LoadFile(string name) {
        string path = FilePath(name);
        DateTime date;
        if (!DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) date = DateTime.Today;
        return LoadPath(path, date);
    }
    internal QuickNoteDocument NewNote() {
        return LoadFile("便签 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".md");
    }
    internal void Rename(QuickNoteDocument doc, string title) {
        Validate(doc);
        title = (title ?? "").Trim();
        if (title.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) title = title.Substring(0, title.Length - 3);
        if (String.IsNullOrWhiteSpace(title) || title.Length > 120) throw new ArgumentException("名称不能为空，且不能超过120个字符。");
        string target = FilePath(title + ".md"), recovery = RecoveryForPath(target);
        RequireRevision(doc);
        if (String.Equals(target, doc.Path, StringComparison.Ordinal)) return;
        if (!Same(target, doc.Path) && (File.Exists(target) || Directory.Exists(target))) throw new IOException("已有同名便签，请换一个名称；不会覆盖原文件。");
        if (File.Exists(doc.RecoveryPath) || File.Exists(recovery)) throw new IOException("存在未处理的恢复草稿，请先处理后再改名。");
        if (doc.Revision == MissingRevision) Save(doc, doc.Text);
        // Same-directory move preserves bytes and relative image references.
        // File.Move fails if another writer creates the target in the meantime.
        File.Move(doc.Path, target);
        doc.Path = target; doc.RecoveryPath = recovery;
    }
    internal string[] RecentFiles() {
        CheckOrdinaryPath(noteRoot);
        if (!Directory.Exists(noteRoot)) return new string[0];
        var files = new System.Collections.Generic.List<FileInfo>();
        foreach (string path in Directory.EnumerateFiles(noteRoot, "*.md", SearchOption.TopDirectoryOnly)) {
            try { FilePath(System.IO.Path.GetFileName(path)); files.Add(new FileInfo(path)); }
            catch (ArgumentException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
        var names = new System.Collections.Generic.List<string>();
        foreach (FileInfo file in files) { if (names.Count == 50) break; names.Add(file.Name); }
        return names.ToArray();
    }
    string FilePath(string name) {
        string safe = SafeRelative(name);
        if (System.IO.Path.GetFileName(safe) != safe || !safe.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("只能打开速记文件夹内的 Markdown 文件。");
        string path = Full(System.IO.Path.Combine(noteRoot, safe));
        CheckOrdinaryPath(path);
        return path;
    }
    QuickNoteDocument LoadPath(string path, DateTime date) {
        CheckOrdinaryPath(path);
        byte[] bytes = ReadOptional(path, MaxNoteBytes);
        var doc = new QuickNoteDocument { Owner = owner, Date = date.Date, Path = path,
            Revision = bytes == null ? MissingRevision : Hash(bytes), Text = Decode(bytes),
            HasBom = HasBom(bytes), RecoveryPath = RecoveryForPath(path) };
        byte[] recovery = ReadOptional(doc.RecoveryPath, MaxNoteBytes + 256);
        if (recovery != null) {
            string stored = Decode(recovery);
            if (!stored.StartsWith(DraftHeader, StringComparison.Ordinal))
                throw new IOException("速记恢复草稿格式无效，原文件已保留：" + doc.RecoveryPath);
            int end = stored.IndexOf('\n', DraftHeader.Length);
            if (end < 0) throw new IOException("速记恢复草稿不完整，原文件已保留：" + doc.RecoveryPath);
            string revision = stored.Substring(DraftHeader.Length, end - DraftHeader.Length);
            if (revision != MissingRevision && !Regex.IsMatch(revision, "^[0-9a-f]{64}$"))
                throw new IOException("速记恢复草稿版本无效，原文件已保留：" + doc.RecoveryPath);
            doc.HasRecovery = true;
            doc.RecoveryRevision = revision;
            doc.RecoveryText = stored.Substring(end + 1);
        }
        return doc;
    }

    public void Save(QuickNoteDocument doc, string text) {
        Validate(doc);
        text = text ?? "";
        try {
            byte[] content = Encode(text, doc.HasBom);
            if (content.Length > MaxNoteBytes) throw new IOException("速记内容超过16 MiB，请拆分为多篇文档。");
            EnsureDirectory(noteRoot);
            RequireRevision(doc);
            if (String.Equals(doc.Text, text, StringComparison.Ordinal) && doc.Revision != MissingRevision) {
                ClearRecovery(doc);
                return;
            }
            AtomicWrite(doc.Path, content, doc.Revision);
            doc.Text = text;
            doc.Revision = Hash(content);
            ClearRecovery(doc);
        } catch (Exception error) {
            if (!(error is IOException) && !(error is UnauthorizedAccessException)) throw;
            // Saving a draft is best effort; never conceal the original save/conflict exception.
            try { SaveRecovery(doc, text); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void SaveRecovery(QuickNoteDocument doc, string text) {
        Validate(doc);
        text = text ?? "";
        byte[] content = Utf8.GetBytes(DraftHeader + doc.Revision + "\n" + text);
        if (content.Length > MaxNoteBytes + 256) throw new IOException("速记恢复草稿过大。");
        EnsureDirectory(recoveryRoot);
        AtomicWrite(doc.RecoveryPath, content, null);
        doc.HasRecovery = true;
        doc.RecoveryRevision = doc.Revision;
        doc.RecoveryText = text;
    }

    public string ArchiveRecovery(QuickNoteDocument doc, string text) {
        Validate(doc);
        byte[] content = Utf8.GetBytes(text ?? "");
        if (content.Length > MaxNoteBytes) throw new IOException("速记恢复草稿过大。");
        EnsureDirectory(recoveryRoot);
        string name = doc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" +
            DateTime.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".md";
        string target = System.IO.Path.Combine(recoveryRoot, name);
        AtomicWrite(target, content, MissingRevision);
        return target;
    }

    public string StoreImage(byte[] png) {
        if (png == null) throw new ArgumentNullException("png");
        ValidatePngHeader(png);
        byte[] canonical;
        try {
            using (var input = new MemoryStream(png, false))
            using (var image = Image.FromStream(input, true, true)) {
                if (image.RawFormat.Guid != ImageFormat.Png.Guid || (long)image.Width * image.Height > MaxImagePixels)
                    throw new IOException("只支持不超过3600万像素的PNG图片。");
                // Re-encode to discard ancillary metadata and trailing data from pasted files.
                // Copy pixels directly: Graphics.DrawImageUnscaled still maps physical DPI and blends alpha.
                using (var bitmap = ((Bitmap)image).Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb))
                using (var output = new MemoryStream()) {
                    bitmap.SetResolution(image.HorizontalResolution, image.VerticalResolution);
                    bitmap.Save(output, ImageFormat.Png);
                    canonical = output.ToArray();
                }
            }
        } catch (ArgumentException error) { throw new IOException("PNG图片无法读取。", error); }
        catch (OutOfMemoryException error) { throw new IOException("PNG图片无效或内存不足。", error); }
        if (canonical.Length > MaxImageBytes) throw new IOException("PNG图片超过12 MiB。");
        string name = Hash(canonical) + ".png";
        string folder = System.IO.Path.Combine(noteRoot, "图片");
        EnsureDirectory(folder);
        string target = System.IO.Path.Combine(folder, name);
        CheckOrdinaryPath(target);
        byte[] existing = ReadOptional(target, MaxImageBytes);
        if (existing != null) {
            if (Hash(existing) != Hash(canonical)) throw new IOException("已有速记图片内容与文件名不符，已保留原文件。");
        } else {
            try { AtomicWrite(target, canonical, MissingRevision); }
            catch (QuickNoteConflictException) {
                byte[] raced = ReadOptional(target, MaxImageBytes);
                if (raced == null || Hash(raced) != Hash(canonical)) throw;
            }
        }
        return "图片/" + name;
    }

    public string ResolveImage(string relativeTarget) {
        try {
            string relative = SafeRelative(Uri.UnescapeDataString(relativeTarget ?? ""));
            string extension = System.IO.Path.GetExtension(relative).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".gif" && extension != ".bmp") return null;
            string full = Full(System.IO.Path.Combine(noteRoot, relative));
            if (!Within(full, noteRoot)) return null;
            CheckOrdinaryPath(full);
            byte[] content = ReadOptional(full, MaxImageBytes);
            if (content == null) return null;
            if (extension == ".png") ValidatePngHeader(content);
            using (var stream = new MemoryStream(content, false))
            using (var image = Image.FromStream(stream, true, true)) {
                Guid format = image.RawFormat.Guid;
                if ((format != ImageFormat.Png.Guid && format != ImageFormat.Jpeg.Guid && format != ImageFormat.Gif.Guid && format != ImageFormat.Bmp.Guid) ||
                    image.Width < 1 || image.Height < 1 || (long)image.Width * image.Height > MaxImagePixels) return null;
            }
            return full;
        } catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (OutOfMemoryException) { return null; }
    }

    void Validate(QuickNoteDocument doc) {
        if (doc == null || !Object.ReferenceEquals(doc.Owner, owner) || !Same(doc.Path, FilePath(System.IO.Path.GetFileName(doc.Path))) ||
            !Same(doc.RecoveryPath, RecoveryForPath(doc.Path))) throw new ArgumentException("速记文档不属于此存储。");
        CheckOrdinaryPath(doc.RecoveryPath);
    }

    void RequireRevision(QuickNoteDocument doc) {
        if (Revision(doc.Path) != doc.Revision)
            throw new QuickNoteConflictException("速记文件已被外部修改或删除。你的输入已保留为恢复草稿，请重新打开后处理。");
    }

    void ClearRecovery(QuickNoteDocument doc) {
        CheckOrdinaryPath(doc.RecoveryPath);
        if (File.Exists(doc.RecoveryPath)) File.Delete(doc.RecoveryPath);
        doc.HasRecovery = false;
        doc.RecoveryText = null;
        doc.RecoveryRevision = null;
    }

    string NotePath(DateTime date) { return System.IO.Path.Combine(noteRoot, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md"); }
    string RecoveryPath(DateTime date) {
        return RecoveryForPath(NotePath(date));
    }
    string RecoveryForPath(string path) {
        // One shared application work directory may serve several vaults/folders. Windows path aliases use one key.
        string identity = Hash(Utf8.GetBytes(Full(path).ToUpperInvariant())).Substring(0, 24);
        string label = System.IO.Path.GetFileNameWithoutExtension(path);
        if (label.Length > 40) label = label.Substring(0, 40);
        return System.IO.Path.Combine(recoveryRoot, label + "-" + identity + ".draft");
    }

    void AtomicWrite(string path, byte[] content, string expectedRevision) {
        CheckOrdinaryPath(path);
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), ".tingji-" + Guid.NewGuid().ToString("N") + ".tmp");
        string backup = temp + ".backup";
        bool backupPreserved = false;
        try {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }
            CheckOrdinaryPath(path);
            string actual = Revision(path);
            if (expectedRevision != null && actual != expectedRevision)
                throw new QuickNoteConflictException("速记文件在保存前发生外部改动，未覆盖原文。");
            if (actual == MissingRevision) {
                try { File.Move(temp, path); }
                catch (IOException) {
                    if (File.Exists(path) && expectedRevision != null)
                        throw new QuickNoteConflictException("速记文件已由其他程序创建，未覆盖原文。");
                    throw;
                }
            } else {
                // A backup also detects an atomic external replacement between the revision check and Replace.
                File.Replace(temp, path, backup, true);
                if (expectedRevision != null && Revision(backup) != expectedRevision) {
                    PreserveExternalBackup(backup);
                    backupPreserved = true;
                    if (Revision(path) == Hash(content)) RestoreBackupPreservingReplacedFile(backup, path, Hash(content));
                    throw new QuickNoteConflictException("保存时检测到外部更新；外部原文和你的恢复草稿均已保留。");
                }
                File.Delete(backup);
            }
        } finally {
            if (File.Exists(temp)) File.Delete(temp);
            if (File.Exists(backup)) {
                if (!backupPreserved) PreserveExternalBackup(backup);
                File.Delete(backup);
            }
        }
    }

    void RestoreBackupPreservingReplacedFile(string backup, string path, string writtenRevision) {
        string displaced = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), ".tingji-" + Guid.NewGuid().ToString("N") + ".rollback");
        try {
            // The path may change after the caller's check. Preserve what Replace actually displaced.
            File.Replace(backup, path, displaced, true);
        } finally {
            if (File.Exists(displaced)) {
                if (Revision(displaced) != writtenRevision) PreserveExternalBackup(displaced);
                // If archival fails, leave this backup intact and propagate the failure.
                File.Delete(displaced);
            }
        }
    }

    void PreserveExternalBackup(string backup) {
        EnsureDirectory(recoveryRoot);
        string target = System.IO.Path.Combine(recoveryRoot, "external-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".md");
        byte[] content = ReadOptional(backup, MaxNoteBytes);
        if (content == null) return;
        using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            stream.Write(content, 0, content.Length);
            stream.Flush(true);
        }
        if (Hash(ReadOptional(target, MaxNoteBytes)) != Hash(content)) throw new IOException("无法校验外部速记备份。");
        // Keep the temporary backup until a caller has either restored it or completed the transaction.
    }

    static string Revision(string path) {
        byte[] content = ReadOptional(path, MaxNoteBytes);
        return content == null ? MissingRevision : Hash(content);
    }

    static byte[] ReadOptional(string path, int limit) {
        CheckOrdinaryPath(path);
        try {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (stream.Length > limit) throw new IOException("速记文件超过允许大小。");
                var bytes = new byte[(int)stream.Length];
                int read = 0;
                while (read < bytes.Length) {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new IOException("速记文件读取不完整。");
                    read += count;
                }
                return bytes;
            }
        } catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    static void EnsureDirectory(string path) { CheckOrdinaryPath(path); Directory.CreateDirectory(path); CheckOrdinaryPath(path); }

    static void CheckOrdinaryPath(string path) {
        string current = Full(path);
        while (!String.IsNullOrEmpty(current)) {
            try {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("速记路径不允许符号链接或重解析点：" + current);
            } catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = System.IO.Path.GetDirectoryName(current);
        }
    }

    static string SafeRelative(string path) {
        if (String.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.IndexOf(':') >= 0 ||
            path.IndexOf('\0') >= 0 || path.IndexOf('%') >= 0 || path.IndexOf('?') >= 0 || path.IndexOf('#') >= 0)
            throw new ArgumentException("速记路径必须是普通相对路径。");
        string normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        foreach (string part in normalized.Split(System.IO.Path.DirectorySeparatorChar)) {
            if (String.IsNullOrWhiteSpace(part) || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\..*)?$", RegexOptions.IgnoreCase))
                throw new ArgumentException("速记路径包含不安全的名称。");
        }
        return normalized;
    }

    static string Full(string path) {
        string full = System.IO.Path.GetFullPath(path);
        string root = System.IO.Path.GetPathRoot(full);
        return full.Length > root.Length ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : full;
    }
    static bool Same(string a, string b) { return String.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase); }
    static bool Within(string path, string parent) {
        string prefix = Full(parent).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return Full(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    static bool HasBom(byte[] bytes) { return bytes != null && bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191; }
    static string Decode(byte[] bytes) { return bytes == null ? "" : Utf8.GetString(bytes, HasBom(bytes) ? 3 : 0, bytes.Length - (HasBom(bytes) ? 3 : 0)); }
    static byte[] Encode(string text, bool bom) {
        byte[] bytes = Utf8.GetBytes(text);
        if (!bom) return bytes;
        var result = new byte[bytes.Length + 3];
        result[0] = 239; result[1] = 187; result[2] = 191;
        Buffer.BlockCopy(bytes, 0, result, 3, bytes.Length);
        return result;
    }
    static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static void ValidatePngHeader(byte[] png) {
        if (png.Length < 33 || png.Length > MaxImageBytes) throw new IOException("PNG图片为空、无效或超过12 MiB。");
        for (int i = 0; i < PngSignature.Length; i++) if (png[i] != PngSignature[i]) throw new IOException("只支持PNG图片。");
        if (png[8] != 0 || png[9] != 0 || png[10] != 0 || png[11] != 13 ||
            png[12] != 73 || png[13] != 72 || png[14] != 68 || png[15] != 82) throw new IOException("PNG图片头无效。");
        long width = BigEndian(png, 16), height = BigEndian(png, 20);
        if (width < 1 || height < 1 || width > MaxImagePixels || height > MaxImagePixels || width * height > MaxImagePixels)
            throw new IOException("PNG图片超过3600万像素。");
    }
    static long BigEndian(byte[] bytes, int index) { return ((long)bytes[index] << 24) | ((long)bytes[index + 1] << 16) | ((long)bytes[index + 2] << 8) | bytes[index + 3]; }
}
