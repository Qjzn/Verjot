using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class TestQuickNoteStore {
    static int passed, skipped;
    static string root, vault, work;
    static readonly DateTime Date = new DateTime(2026, 10, 15);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CreateSymbolicLink(string link, string target, int flags);

    static int Main(string[] args) {
        string parent = Path.GetFullPath(args.Length == 0 ? Path.GetTempPath() : args[0]);
        root = Path.Combine(parent, "tingji-quick-note-" + Guid.NewGuid().ToString("N"));
        vault = Path.Combine(root, "vault");
        work = Path.Combine(root, "work", "quick-note");
        Directory.CreateDirectory(vault);
        try {
            Run();
            Console.WriteLine("PASS: " + passed + " assertions; SKIP: " + skipped + "; isolated Markdown/PNG only");
            return 0;
        } catch (Exception error) {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        } finally {
            if (Path.GetDirectoryName(root) == parent.TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    static void Run() {
        var store = new QuickNoteStore(vault, "速记", work);
        var fresh = store.Load(Date);
        True(fresh.Text == "" && fresh.Revision == "missing" && !fresh.HasRecovery, "new date has no placeholder or synthetic heading");
        True(!Directory.Exists(store.FolderPath), "opening does not create a user note folder");
        string original = "---\r\n标签: [速记]\r\n---\r\n# 我的原笔记\r\n\r\n- 内容和 **格式**\n末行没有换行";
        store.Save(fresh, original);
        Equal(File.ReadAllText(fresh.Path, Encoding.UTF8), original, "existing Unicode and mixed newlines round trip exactly");
        True(!File.ReadAllBytes(fresh.Path)[0].Equals((byte)239), "new Markdown uses UTF8 without BOM");
        var loaded = store.Load(Date.AddHours(22));
        Equal(loaded.Path, fresh.Path, "same local calendar date uses one note");
        Equal(loaded.Text, original, "loading does not rewrite existing Markdown");
        True(store.Load(Date.AddDays(1)).Path != fresh.Path, "next day gets a separate note");
        var bom = store.Load(Date.AddDays(-1));
        Directory.CreateDirectory(Path.GetDirectoryName(bom.Path));
        File.WriteAllText(bom.Path, "旧文\r\n", new UTF8Encoding(true));
        bom = store.Load(Date.AddDays(-1));
        Equal(bom.Text, "旧文\r\n", "BOM is not editor content");
        store.Save(bom, "旧文\r\n新文");
        byte[] bomBytes = File.ReadAllBytes(bom.Path);
        True(bomBytes[0] == 239 && bomBytes[1] == 187 && bomBytes[2] == 191, "existing UTF8 BOM is preserved");

        store.SaveRecovery(loaded, "尚未保存的草稿");
        var recover = new QuickNoteStore(vault, "速记", work).Load(Date);
        True(recover.HasRecovery && recover.RecoveryRevision == recover.Revision, "matching draft is discoverable after restart");
        Equal(recover.RecoveryText, "尚未保存的草稿", "recovery text survives exactly");
        Equal(recover.Text, original, "draft never silently replaces disk text");
        True(!recover.RecoveryPath.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "recovery stays outside Obsidian vault");
        store.Save(loaded, original + "\n新增");
        True(!File.Exists(loaded.RecoveryPath) && !loaded.HasRecovery, "successful save clears only current recovery draft");

        var conflict = store.Load(Date);
        File.WriteAllText(conflict.Path, "Obsidian 外部改动", new UTF8Encoding(false));
        Throws<QuickNoteConflictException>(delegate { store.Save(conflict, "听记中的改动"); }, "external edits cause a conflict");
        Equal(File.ReadAllText(conflict.Path), "Obsidian 外部改动", "external version is never overwritten");
        var conflicted = store.Load(Date);
        True(conflicted.HasRecovery && conflicted.RecoveryRevision != conflicted.Revision, "conflicting draft keeps its original base revision");
        Equal(conflicted.RecoveryText, "听记中的改动", "conflicting local input remains recoverable");
        string archive = store.ArchiveRecovery(conflict, "听记中的改动");
        Equal(File.ReadAllText(archive), "听记中的改动", "conflict can be archived before safe reload");
        store.Save(conflicted, "重新载入后编辑");
        True(File.Exists(archive), "later autosave never deletes an explicit conflict archive");
        File.Delete(conflicted.Path);
        Throws<QuickNoteConflictException>(delegate { store.Save(conflicted, "删除后仍在输入"); }, "external deletion is a conflict");
        True(!File.Exists(conflicted.Path), "autosave does not recreate a manually deleted note");
        Equal(store.Load(Date).RecoveryText, "删除后仍在输入", "deleted-file input remains recoverable");

        var locked = store.Load(Date.AddDays(2));
        store.Save(locked, "锁定前的内容");
        using (var handle = new FileStream(locked.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            Throws<IOException>(delegate { store.Save(locked, "锁定时新增"); }, "failed atomic replace reports I/O failure");
        }
        Equal(File.ReadAllText(locked.Path), "锁定前的内容", "failed save preserves original bytes");
        Equal(store.Load(Date.AddDays(2)).RecoveryText, "锁定时新增", "failed save retains input outside vault");
        True(Directory.GetFiles(store.FolderPath, ".tingji-*").Length == 0, "failed save leaves no user-folder temp files");
        store.Save(locked, "解锁后继续");
        True(!File.Exists(locked.RecoveryPath), "retry after unlock saves and clears recovery");

        TestAtomicVisibility(store);
        TestRollbackPreservesConcurrentEdit(store);
        TestRecoveryNamespaces(store);
        TestImages(store);
        foreach (string unsafeFolder in new[] { "../out", "..\\out", "C:\\outside", "\\\\server\\share", "http://example.com", "note:stream", "速记/../附件", "NUL", "目录.", "目录 " })
            Throws<ArgumentException>(delegate { new QuickNoteStore(vault, unsafeFolder, work); }, "unsafe note folder rejected: " + unsafeFolder);
        Throws<ArgumentException>(delegate { new QuickNoteStore(vault, "速记", Path.Combine(vault, "drafts")); }, "draft folder inside vault is rejected");
        var another = new QuickNoteStore(vault, "速记", work);
        Throws<ArgumentException>(delegate { another.Save(locked, "other owner"); }, "document cannot be saved by another store instance");
        var invalidUtf8 = store.Load(Date.AddDays(6));
        File.WriteAllBytes(invalidUtf8.Path, new byte[] { 255, 254, 65 });
        Throws<DecoderFallbackException>(delegate { store.Load(Date.AddDays(6)); }, "invalid encoding never silently corrupts existing content");

        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        string linked = Path.Combine(vault, "linked");
        if (CreateSymbolicLink(linked, outside, 1 | 2) || CreateSymbolicLink(linked, outside, 1)) {
            try { Throws<IOException>(delegate { new QuickNoteStore(vault, "linked", work); }, "junction/symlink note root rejected"); }
            finally { Directory.Delete(linked); }
        } else { skipped++; Console.WriteLine("SKIP: symlink creation unavailable (Win32 " + Marshal.GetLastWin32Error() + ")"); }
    }

    static void TestRecoveryNamespaces(QuickNoteStore store) {
        DateTime date = Date.AddDays(7);
        var original = store.Load(date);
        store.SaveRecovery(original, "原速记目录的未创建文件草稿");
        var otherFolder = new QuickNoteStore(vault, "其他速记", work);
        var other = otherFolder.Load(date);
        True(!other.HasRecovery, "same-date missing note in another folder never restores unrelated draft");
        otherFolder.SaveRecovery(other, "其他速记目录的草稿");
        Equal(store.Load(date).RecoveryText, "原速记目录的未创建文件草稿", "another folder cannot overwrite first folder recovery");
        Equal(otherFolder.Load(date).RecoveryText, "其他速记目录的草稿", "each note folder keeps its own recovery");

        string otherVaultPath = Path.Combine(root, "other-vault");
        Directory.CreateDirectory(otherVaultPath);
        var otherVault = new QuickNoteStore(otherVaultPath, "速记", work);
        var vaultDoc = otherVault.Load(date);
        True(!vaultDoc.HasRecovery, "another vault sharing recovery root never restores unrelated draft");
        otherVault.SaveRecovery(vaultDoc, "另一个笔记库的草稿");
        Equal(store.Load(date).RecoveryText, "原速记目录的未创建文件草稿", "another vault cannot overwrite original vault draft");
        Equal(otherVault.Load(date).RecoveryText, "另一个笔记库的草稿", "each vault keeps its own recovery");
        var caseAlias = new QuickNoteStore(vault.ToUpperInvariant(), "速记", work);
        Equal(caseAlias.Load(date).RecoveryPath, original.RecoveryPath, "Windows path case aliases share the same recovery namespace");

        date = date.AddDays(1);
        var one = store.Load(date);
        var two = otherFolder.Load(date);
        store.Save(one, "同样的磁盘内容");
        otherFolder.Save(two, "同样的磁盘内容");
        store.SaveRecovery(one, "版本相同但路径不同的草稿A");
        True(!otherFolder.Load(date).HasRecovery, "equal file revisions in separate note paths do not cross-restore drafts");
        otherFolder.SaveRecovery(two, "版本相同但路径不同的草稿B");
        Equal(store.Load(date).RecoveryText, "版本相同但路径不同的草稿A", "equal revision cannot cause cross-folder recovery overwrite");
    }

    static void TestRollbackPreservesConcurrentEdit(QuickNoteStore store) {
        var doc = store.Load(Date.AddDays(4));
        store.Save(doc, "本工具刚刚写入的版本");
        string expectedWrittenRevision = doc.Revision;
        string backup = Path.Combine(store.FolderPath, ".tingji-rollback-fixture.backup");
        File.WriteAllText(backup, "先前已归档的外部版本", new UTF8Encoding(false));
        // Deterministically reproduce the exact race: caller checked our revision, then another editor replaced the file.
        File.WriteAllText(doc.Path, "第三方在检查之后写入的较新版本", new UTF8Encoding(false));
        MethodInfo rollback = typeof(QuickNoteStore).GetMethod("RestoreBackupPreservingReplacedFile", BindingFlags.Instance | BindingFlags.NonPublic);
        rollback.Invoke(store, new object[] { backup, doc.Path, expectedWrittenRevision });
        Equal(File.ReadAllText(doc.Path), "先前已归档的外部版本", "rollback restores the previously displaced version");
        bool foundLatest = false;
        foreach (string archived in Directory.GetFiles(work, "external-*.md"))
            if (File.ReadAllText(archived) == "第三方在检查之后写入的较新版本") foundLatest = true;
        True(foundLatest, "rollback never discards a newer external update racing after the revision check");
        True(!File.Exists(backup), "rollback consumes its original backup");
        True(Directory.GetFiles(store.FolderPath, ".tingji-*").Length == 0, "successful rollback archives outside the vault and removes temporary backups");

        store.Save(doc = store.Load(Date.AddDays(4)), "本工具另一版本");
        File.WriteAllText(backup, "待还原的外部原文", new UTF8Encoding(false));
        int countBefore = Directory.GetFiles(work, "external-*.md").Length;
        rollback.Invoke(store, new object[] { backup, doc.Path, doc.Revision });
        Equal(File.ReadAllText(doc.Path), "待还原的外部原文", "ordinary rollback restores original external content");
        True(Directory.GetFiles(work, "external-*.md").Length == countBefore, "ordinary rollback does not archive disposable content from this tool");

        string blockedRecovery = Path.Combine(root, "blocked-recovery");
        var blockedStore = new QuickNoteStore(vault, "速记", blockedRecovery);
        File.WriteAllText(blockedRecovery, "a file blocks recovery directory creation");
        File.WriteAllText(backup, "仍需还原的外部原文", new UTF8Encoding(false));
        File.WriteAllText(doc.Path, "归档失败也不能丢失的较新外文", new UTF8Encoding(false));
        try {
            rollback.Invoke(blockedStore, new object[] { backup, doc.Path, doc.Revision });
            throw new Exception("expected recovery directory creation to fail");
        } catch (TargetInvocationException error) {
            True(error.InnerException is IOException, "rollback archival failure is reported");
        }
        string[] retained = Directory.GetFiles(store.FolderPath, ".tingji-*.rollback");
        True(retained.Length == 1, "failed archival retains its displaced-file backup instead of deleting it");
        Equal(File.ReadAllText(retained[0]), "归档失败也不能丢失的较新外文", "even failed recovery retains the complete newer external version");
        File.Delete(retained[0]); // Exact fixture-created rollback path; no user files are involved.
    }

    static void TestAtomicVisibility(QuickNoteStore store) {
        var doc = store.Load(Date.AddDays(3));
        string first = "版本A\n" + new string('甲', 9000), second = "版本B\n" + new string('乙', 13000);
        store.Save(doc, first);
        Exception readError = null;
        int readCount = 0;
        var stop = new ManualResetEvent(false);
        var reader = new Thread(delegate() {
            while (!stop.WaitOne(0)) {
                try {
                    using (var stream = new FileStream(doc.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var text = new StreamReader(stream, new UTF8Encoding(false, true))) {
                        string actual = text.ReadToEnd();
                        if (actual != first && actual != second) throw new Exception("partial Markdown observed");
                        Interlocked.Increment(ref readCount);
                    }
                // Windows ReplaceFile can briefly hide the name while replacing; no partial content is accepted.
                } catch (FileNotFoundException) { Thread.Sleep(1); }
                catch (IOException error) {
                    int code = error.HResult & 65535;
                    if (code == 32 || code == 33) Thread.Sleep(1); else { readError = error; break; }
                }
                catch (Exception error) { readError = error; break; }
            }
        });
        reader.Start();
        try {
            for (int i = 0; i < 40; i++) store.Save(doc, i % 2 == 0 ? second : first);
        } finally { stop.Set(); reader.Join(); stop.Dispose(); }
        True(readError == null, "concurrent reader sees only complete old or complete new Markdown: " + (readError == null ? "OK" : readError.Message));
        True(readCount > 0, "atomic replacement was observed by a real concurrent file reader");
        True(Directory.GetFiles(store.FolderPath, ".tingji-*").Length == 0, "normal saves leave no temp or backup attachments");
    }

    static void TestImages(QuickNoteStore store) {
        byte[] png;
        using (var bitmap = new Bitmap(20, 12))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var stream = new MemoryStream()) {
            graphics.Clear(Color.CornflowerBlue);
            graphics.FillEllipse(Brushes.Yellow, 3, 2, 8, 8);
            bitmap.Save(stream, ImageFormat.Png);
            png = stream.ToArray();
        }
        string relative = store.StoreImage(png);
        True(relative.StartsWith("图片/") && relative.EndsWith(".png"), "image returns portable relative Markdown target");
        Equal(store.StoreImage(png), relative, "repeated pasted PNG is content-deduplicated");
        True(Directory.GetFiles(Path.Combine(store.FolderPath, "图片")).Length == 1, "deduplication stores one image file");
        string resolved = store.ResolveImage(relative);
        True(resolved != null && File.Exists(resolved), "saved PNG resolves inside note folder");
        using (var image = Image.FromFile(resolved)) True(image.Width == 20 && image.Height == 12, "saved image is a readable PNG with original dimensions");
        string spaced = Path.Combine(store.FolderPath, "图片", "with space.png");
        File.Copy(resolved, spaced);
        Equal(store.ResolveImage("图片/with%20space.png"), spaced, "Markdown percent-encoded spaces resolve safely");
        string jpeg = Path.Combine(store.FolderPath, "图片", "existing.jpg");
        using (var bitmap = new Bitmap(24, 20)) bitmap.Save(jpeg, ImageFormat.Jpeg);
        Equal(store.ResolveImage("图片/existing.jpg"), jpeg, "existing local JPEG Markdown attachments remain readable");
        string invalid = Path.Combine(store.FolderPath, "图片", "invalid.jpg");
        File.WriteAllText(invalid, "not a JPEG");
        True(store.ResolveImage("图片/invalid.jpg") == null, "local extension cannot disguise non-image content");
        foreach (string unsafeTarget in new[] { "../outside.png", "图片/../../outside.png", "C:\\outside.png", "\\\\server\\share\\image.png", "https://example.com/a.png", "data:image/png;base64,AA", "图片/x.png:ads", "图片/%2e%2e/%2e%2e/outside.png", "图片/%252e%252e/x.png", "图片/x.svg", "图片/missing.png" })
            True(store.ResolveImage(unsafeTarget) == null, "unsafe or unavailable image rejected: " + unsafeTarget);
        Throws<IOException>(delegate { store.StoreImage(new byte[] { 1, 2, 3 }); }, "non-PNG paste is rejected");
        byte[] huge = (byte[])png.Clone();
        huge[16] = 0x7f; huge[17] = 255; huge[18] = 255; huge[19] = 255;
        Throws<IOException>(delegate { store.StoreImage(huge); }, "oversized pixel dimensions rejected before image allocation");
        Throws<IOException>(delegate { store.StoreImage(new byte[QuickNoteStore.MaxImageBytes + 1]); }, "oversized compressed input rejected");
        byte[] corrupt = new byte[40];
        Buffer.BlockCopy(png, 0, corrupt, 0, 33);
        Throws<IOException>(delegate { store.StoreImage(corrupt); }, "truncated PNG is rejected by decoder");
        foreach (float dpi in new float[] { 96, 144, 192 }) TestImagePixels(store, dpi);
    }

    static void TestImagePixels(QuickNoteStore store, float dpi) {
        byte[] input;
        using (var bitmap = new Bitmap(320, 96, PixelFormat.Format32bppArgb)) {
            bitmap.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Arial", 12, FontStyle.Regular, GraphicsUnit.Pixel)) {
                graphics.PageUnit = GraphicsUnit.Pixel;
                graphics.Clear(Color.White);
                for (int y = 0; y < bitmap.Height; y += 8)
                    for (int x = 0; x < bitmap.Width; x += 8)
                        graphics.FillRectangle(((x + y) / 8 % 2 == 0) ? Brushes.Orange : Brushes.SkyBlue, x, y, 8, 8);
                graphics.DrawString("0,0 -- checkerboard -- 319,95", font, Brushes.Black, 6, 35);
                graphics.DrawRectangle(Pens.Red, 0, 0, 319, 95);
            }
            bitmap.SetPixel(2, 2, Color.FromArgb(64, 17, 133, 240));
            bitmap.SetPixel(3, 2, Color.FromArgb(192, 250, 33, 71));
            bitmap.SetPixel(4, 2, Color.FromArgb(0, 12, 34, 56));
            using (var output = new MemoryStream()) { bitmap.Save(output, ImageFormat.Png); input = output.ToArray(); }
        }
        string target = store.ResolveImage(store.StoreImage(input));
        using (var sourceBytes = new MemoryStream(input, false))
        using (var source = new Bitmap(sourceBytes))
        using (var saved = new Bitmap(target)) {
            True(saved.Width == source.Width && saved.Height == source.Height, "PNG dimensions remain exact at " + dpi + " DPI");
            int differences = 0;
            for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
                if (source.GetPixel(x, y).ToArgb() != saved.GetPixel(x, y).ToArgb()) differences++;
            True(differences == 0, "PNG RGBA pixels remain exact at " + dpi + " DPI; changed pixels=" + differences);
            True(Math.Abs(saved.HorizontalResolution - source.HorizontalResolution) < 0.03f &&
                Math.Abs(saved.VerticalResolution - source.VerticalResolution) < 0.03f, "PNG retains source DPI within one pHYs quantization unit at " + dpi + " DPI");
        }
    }

    static void True(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
    static void Equal(string actual, string expected, string message) { True(actual == expected, message); }
    static void Throws<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T) { passed++; return; }
        throw new Exception(message + ": expected " + typeof(T).Name);
    }
}
