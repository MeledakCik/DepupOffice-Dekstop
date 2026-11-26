using System.Text;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using DepupOfficeDesktop.Core;
using SkiaSharp;
using CoreRect = DepupOfficeDesktop.Core.Rect;

namespace DepupOffice.Mac.Services;

/// <summary>Dokumen PDF terbuka (PDFium via Docnet) — pengganti Windows.Data.Pdf.</summary>
public sealed class PdfDoc : IDisposable
{
    public const double ViewScale = 1.5;          // ±108 dpi untuk tampilan layar
    public const double ToolScale = 150.0 / 72.0; // 150 dpi untuk alat PDF

    private readonly IDocReader _reader;
    public string FilePath { get; }
    public string? Password { get; }
    public double Scale { get; }
    public int PageCount { get; }

    private PdfDoc(string path, string? password, IDocReader reader, double scale)
    {
        FilePath = path;
        Password = password;
        _reader = reader;
        Scale = scale;
        PageCount = reader.GetPageCount();
    }

    /// <summary>Buka PDF. Melempar <see cref="PdfPasswordException"/> bila butuh/ salah password.</summary>
    public static PdfDoc Open(string path, string? password, double scale)
    {
        try
        {
            var dims = new PageDimensions(scale);
            var reader = string.IsNullOrEmpty(password)
                ? DocLib.Instance.GetDocReader(path, dims)
                : DocLib.Instance.GetDocReader(path, password, dims);
            return new PdfDoc(path, password, reader, scale);
        }
        catch (Exception ex) when (ex is not PdfPasswordException)
        {
            if (PdfService.IsEncrypted(path)) throw new PdfPasswordException(path, !string.IsNullOrEmpty(password));
            throw new InvalidDataException($"PDF tidak bisa dibuka (rusak atau format tidak didukung): {Path.GetFileName(path)}", ex);
        }
    }

    public PdfDoc Reopen(double scale) => Open(FilePath, Password, scale);

    public SKBitmap Render(int index)
    {
        using var page = _reader.GetPageReader(index);
        int w = page.GetPageWidth();
        int h = page.GetPageHeight();
        byte[] raw = page.GetImage(new NaiveTransparencyRemover(), RenderFlags.RenderAnnotations);
        return ImageUtil.FromBgra(raw, w, h);
    }

    /// <summary>Ukuran halaman dalam point.</summary>
    public (double W, double H) PagePoints(int index)
    {
        using var page = _reader.GetPageReader(index);
        return (page.GetPageWidth() / Scale, page.GetPageHeight() / Scale);
    }

    public string Text(int index)
    {
        try
        {
            using var page = _reader.GetPageReader(index);
            return page.GetText() ?? "";
        }
        catch { return ""; }
    }

    public string AllText(int maxPages = 300)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(PageCount, maxPages); i++)
        {
            string t = Text(i).Trim();
            if (t.Length > 0) sb.AppendLine(t).AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>Baris teks asli beserta kotak piksel (pada skala dokumen ini), untuk lapisan teks PDF baru.</summary>
    public List<OcrLine> TextLines(int index)
    {
        var lines = new List<OcrLine>();
        try
        {
            using var page = _reader.GetPageReader(index);
            var sb = new StringBuilder();
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            int lastRight = int.MinValue;

            void Flush()
            {
                string text = sb.ToString().Trim();
                if (text.Length > 0 && r > l && b > t)
                    lines.Add(new OcrLine(text, new CoreRect(l, t, r - l, b - t)));
                sb.Clear();
                l = t = int.MaxValue;
                r = b = int.MinValue;
                lastRight = int.MinValue;
            }

            foreach (var ch in page.GetCharacters())
            {
                char c = ch.Char;
                if (c == '\r' || c == '\n') { Flush(); continue; }
                var box = ch.Box;
                int top = Math.Min(box.Top, box.Bottom), bottom = Math.Max(box.Top, box.Bottom);
                int left = Math.Min(box.Left, box.Right), right = Math.Max(box.Left, box.Right);
                bool hasBox = right > left && bottom > top;
                if (sb.Length > 0 && hasBox && b > t)
                {
                    int lineH = b - t;
                    bool otherLine = Math.Abs((top + bottom) / 2 - (t + b) / 2) > lineH * 0.6;
                    bool jumpBack = left < lastRight - lineH * 2;
                    if (otherLine || jumpBack) Flush();
                }
                if (c == ' ' && sb.Length == 0) continue;
                sb.Append(c);
                if (hasBox)
                {
                    l = Math.Min(l, left); t = Math.Min(t, top);
                    r = Math.Max(r, right); b = Math.Max(b, bottom);
                    lastRight = right;
                }
            }
            Flush();
        }
        catch { }
        return lines;
    }

    public void Dispose() => _reader.Dispose();
}

public sealed class PdfPasswordException : Exception
{
    public string FilePath { get; }
    public bool WrongPassword { get; }

    public PdfPasswordException(string path, bool wrong)
        : base(wrong ? "Password PDF salah." : "PDF ini dilindungi password.")
    {
        FilePath = path;
        WrongPassword = wrong;
    }
}

public static class PdfService
{
    private static readonly Dictionary<string, string> PasswordCache = new(StringComparer.Ordinal);

    public static bool IsEncrypted(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            byte[] pattern = Encoding.ASCII.GetBytes("/Encrypt");
            using var fs = fi.OpenRead();
            const int chunk = 1 << 20;
            IEnumerable<long> starts = fi.Length <= 8L * chunk
                ? new[] { 0L }
                : new[] { 0L, Math.Max(0, fi.Length - 2L * chunk) };
            foreach (long start in starts)
            {
                fs.Position = start;
                int len = (int)Math.Min(fi.Length - start, fi.Length <= 8L * chunk ? fi.Length : 2L * chunk);
                byte[] buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = fs.Read(buf, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (buf.AsSpan(0, read).IndexOf(pattern) >= 0) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Buka PDF; bila terkunci, minta password (diingat selama aplikasi berjalan).</summary>
    public static async Task<PdfDoc?> OpenInteractive(Avalonia.Controls.Window owner, string path, double scale)
    {
        string? pw = PasswordCache.TryGetValue(path, out var cached) ? cached : null;
        bool retry = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var doc = await Task.Run(() => PdfDoc.Open(path, pw, scale));
                if (!string.IsNullOrEmpty(pw)) PasswordCache[path] = pw!;
                return doc;
            }
            catch (PdfPasswordException ex)
            {
                if (pw != null) retry = true;
                PasswordCache.Remove(path);
                pw = await Dialogs.Password(owner, Path.GetFileName(path), retry || ex.WrongPassword && pw != null);
                if (pw == null) return null;
            }
            catch (Exception ex)
            {
                await Dialogs.Error(owner, ex.Message);
                return null;
            }
        }
        return null;
    }
}

/// <summary>Satu halaman sumber (dokumen + indeks) untuk dibangun ulang.</summary>
public sealed record PageRef(PdfDoc Doc, int Index);

/// <summary>
/// Alat PDF lintas platform. Seperti versi Windows, halaman dirender ulang menjadi gambar JPEG lalu disusun
/// menjadi PDF baru — tetapi lapisan teks asli dipertahankan (tak terlihat) agar tetap bisa dicari & disalin.
/// </summary>
public static class PdfTools
{
    public static void Build(IReadOnlyList<PageRef> pages, string outPath, string? password, int quality,
        int rotate, Action<string> report, bool keepText = true)
    {
        if (pages.Count == 0) throw new InvalidOperationException("Tidak ada halaman untuk disimpan.");
        string tmp = outPath + ".tmp";
        var writer = new PdfWriter(tmp, pages.Count, password);
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                report($"Memproses halaman {i + 1} dari {pages.Count}…");
                var p = pages[i];
                using var raw = p.Doc.Render(p.Index);
                using var bmp = rotate % 360 != 0 ? ImageUtil.Rotate(raw, rotate) : raw.Copy();
                double scale = p.Doc.Scale;
                float pw = (float)(bmp.Width / scale), ph = (float)(bmp.Height / scale);
                byte[] jpeg = ImageUtil.EncodeJpeg(bmp, quality);
                OcrPage? layer = null;
                if (keepText && rotate % 360 == 0)
                {
                    var lines = p.Doc.TextLines(p.Index);
                    if (lines.Count > 0) layer = new OcrPage("", new List<string>(), lines, bmp.Width, bmp.Height);
                }
                writer.AddPage(jpeg, bmp.Width, bmp.Height, pw, ph, 0, 0, pw, ph, layer);
            }
            writer.Finish();
            File.Move(tmp, outPath, true);
        }
        catch
        {
            writer.Abort();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static List<PageRef> AllPages(PdfDoc doc) => Enumerable.Range(0, doc.PageCount).Select(i => new PageRef(doc, i)).ToList();

    /// <summary>Gambar → PDF. fitA4: tiap gambar di halaman A4 (orientasi otomatis); jika tidak, ukuran halaman = gambar.</summary>
    public static void ImagesToPdf(IReadOnlyList<string> images, string outPath, bool fitA4, Action<string> report, string? password = null)
    {
        string tmp = outPath + ".tmp";
        var writer = new PdfWriter(tmp, images.Count, password);
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                report($"Menyusun gambar {i + 1} dari {images.Count}…");
                using var loaded = ImageUtil.Load(images[i]);
                using var bmp = ImageUtil.Fit(loaded, 2480);
                byte[] jpeg = ImageUtil.EncodeJpeg(bmp, 85);
                if (fitA4)
                {
                    bool landscape = bmp.Width > bmp.Height;
                    float pw = landscape ? 841.9f : 595.3f, ph = landscape ? 595.3f : 841.9f;
                    const float margin = 24f;
                    float k = Math.Min((pw - 2 * margin) / bmp.Width, (ph - 2 * margin) / bmp.Height);
                    float iw = bmp.Width * k, ih = bmp.Height * k;
                    writer.AddPage(jpeg, bmp.Width, bmp.Height, pw, ph, (pw - iw) / 2, (ph - ih) / 2, iw, ih);
                }
                else
                {
                    float pw = bmp.Width * 72f / 150f, ph = bmp.Height * 72f / 150f;
                    writer.AddPage(jpeg, bmp.Width, bmp.Height, pw, ph, 0, 0, pw, ph);
                }
            }
            writer.Finish();
            File.Move(tmp, outPath, true);
        }
        catch
        {
            writer.Abort();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    public static List<string> PdfToJpg(PdfDoc doc, string outDir, Action<string> report)
    {
        Directory.CreateDirectory(outDir);
        string baseName = AppStorage.BaseName(Path.GetFileName(doc.FilePath));
        var outputs = new List<string>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            report($"Mengekspor halaman {i + 1} dari {doc.PageCount}…");
            using var bmp = doc.Render(i);
            string path = Path.Combine(outDir, $"{baseName}_hal{i + 1:000}.jpg");
            File.WriteAllBytes(path, ImageUtil.EncodeJpeg(bmp, 92));
            outputs.Add(path);
        }
        return outputs;
    }

    /// <summary>
    /// Kompres bertahap hingga ukuran ≤ target (mis. syarat unggah SSCASN/CPNS). targetBytes ≤ 0 = satu langkah standar.
    /// Mengembalikan ukuran akhir.
    /// </summary>
    public static long Compress(PdfDoc source, string outPath, long targetBytes, int level, Action<string> report)
    {
        (int Dpi, int Q)[] steps = { (150, 80), (130, 70), (110, 62), (96, 55), (84, 48), (72, 42), (60, 36), (50, 30) };
        int startStep = targetBytes > 0 ? 0 : Math.Clamp(level, 0, steps.Length - 1);
        int endStep = targetBytes > 0 ? steps.Length - 1 : startStep;
        long size = 0;
        for (int s = startStep; s <= endStep; s++)
        {
            var (dpi, q) = steps[s];
            report($"Mengompres ({dpi} dpi, kualitas {q})…");
            using var doc = source.Reopen(dpi / 72.0);
            Build(AllPages(doc), outPath, null, q, 0, msg => report($"{msg} • {dpi} dpi"));
            size = new FileInfo(outPath).Length;
            if (targetBytes <= 0 || size <= targetBytes) break;
        }
        return size;
    }
}
