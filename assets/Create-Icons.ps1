$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class TingjiArtwork {
    static string logoPath;
    static Bitmap Draw(int size, Color color) {
        if (logoPath != null) {
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var source = new Bitmap(logoPath))
            using (var g = Graphics.FromImage(result)) {
                int left = source.Width, top = source.Height, right = -1, bottom = -1;
                for (int y = 0; y < source.Height; y++) for (int x = 0; x < source.Width; x++)
                    if (source.GetPixel(x, y).A >= 128) { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
                if (right < left) throw new InvalidDataException("Logo is fully transparent.");
                float margin = size / 16f, scale = (size - margin * 2) / Math.Max(right - left + 1f, bottom - top + 1f);
                float width = (right - left + 1) * scale, height = (bottom - top + 1) * scale;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new RectangleF((size - width) / 2, (size - height) / 2, width, height), new RectangleF(left, top, right - left + 1, bottom - top + 1), GraphicsUnit.Pixel);
                // The recording badge is application state, not part of the source logo.
                if (color.R > 150) {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var border = new SolidBrush(Color.White)) g.FillEllipse(border, size * .64f, size * .02f, size * .34f, size * .34f);
                    using (var badge = new SolidBrush(color)) g.FillEllipse(badge, size * .68f, size * .06f, size * .26f, size * .26f);
                }
            }
            return result;
        }
        using (var large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(large)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size * 4f / 32, size * 4f / 32);
                using (var path = new GraphicsPath()) {
                    path.AddArc(1, 1, 16, 16, 180, 90);
                    path.AddArc(15, 1, 16, 16, 270, 90);
                    path.AddArc(15, 15, 16, 16, 0, 90);
                    path.AddArc(1, 15, 16, 16, 90, 90);
                    path.CloseFigure();
                    using (var fill = new SolidBrush(color)) g.FillPath(fill, path);
                }
                using (var pen = new Pen(Color.White, 3)) {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, 9, 13, 9, 19);
                    g.DrawLine(pen, 16, 8, 16, 24);
                    g.DrawLine(pen, 23, 11, 23, 21);
                }
            }
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result)) {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(large, new Rectangle(0, 0, size, size));
            }
            return result;
        }
    }
    static byte[] Frame(Bitmap bitmap) {
        using (var stream = new MemoryStream()) {
            if (bitmap.Width == 256) bitmap.Save(stream, ImageFormat.Png);
            else using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) {
                int size = bitmap.Width, maskStride = ((size + 31) / 32) * 4;
                writer.Write(40); writer.Write(size); writer.Write(size * 2);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(0); writer.Write(size * size * 4 + maskStride * size);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int y = size - 1; y >= 0; y--) for (int x = 0; x < size; x++) {
                    var pixel = bitmap.GetPixel(x, y);
                    writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                }
                for (int y = size - 1; y >= 0; y--) {
                    var row = new byte[maskStride];
                    for (int x = 0; x < size; x++) if (bitmap.GetPixel(x, y).A < 128) row[x / 8] |= (byte)(128 >> (x % 8));
                    writer.Write(row);
                }
            }
            return stream.ToArray();
        }
    }
    public static void Save(string directory, string name, Color color) {
        string source = Path.Combine(directory, "verbit-logo.png");
        logoPath = File.Exists(source) ? source : null;
        int[] sizes = {16, 20, 24, 32, 48, 64, 128, 256};
        var frames = new List<byte[]>();
        foreach (int size in sizes) using (var bitmap = Draw(size, color)) {
            frames.Add(Frame(bitmap));
            if (size == 256) bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        }
        using (var stream = File.Create(Path.Combine(directory, name + ".ico")))
        using (var writer = new BinaryWriter(stream)) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++) {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
    }
}
'@
[TingjiArtwork]::Save($PSScriptRoot, 'tingji', [Drawing.Color]::FromArgb(40,104,168))
[TingjiArtwork]::Save($PSScriptRoot, 'tingji-recording', [Drawing.Color]::FromArgb(216,57,69))
Write-Output 'Generated Pianyan / Verbit icons (16 through 256 pixels); resource filenames retained for compatibility.'
