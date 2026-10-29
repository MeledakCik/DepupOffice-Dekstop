using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    public sealed class ScanPage
    {
        public string FilePath { get; set; }
        public int Rot { get; set; } = 0;
        public RectangleF Crop { get; set; } = new RectangleF(0f, 0f, 1f, 1f);
        public bool Flip { get; set; } = false;
        public float Skew { get; set; } = 0f;
        public List<RectangleF> Redactions { get; } = new();
        public string? Watermark { get; set; }

        public ScanPage(string filePath)
        {
            FilePath = filePath;
        }
    }

    public record AspectOpt(string Name, int Kind, float Ratio = 0f, bool FollowOrientation = false);

    public sealed class LaidPage : IDisposable
    {
        public Bitmap Bmp { get; }
        public Bitmap Orig { get; }
        public float PageW { get; }
        public float PageH { get; }
        public float X { get; }
        public float Y { get; }
        public float W { get; }
        public float H { get; }

        public LaidPage(Bitmap bmp, Bitmap orig, float pageW, float pageH, float x, float y, float w, float h)
        {
            Bmp = bmp; Orig = orig; PageW = pageW; PageH = pageH; X = x; Y = y; W = w; H = h;
        }

        public void Dispose()
        {
            if (!ReferenceEquals(Bmp, Orig)) Bmp.Dispose();
            Orig.Dispose();
        }
    }

    public record ReceiptInfo(string Merchant, string Date, string Total, List<string> Items);
    public record DocSummary(List<string> Numbers, List<string> Dates, List<string> Amounts, List<string> KeyPoints);

    /// <summary>
    /// Porting menyeluruh dari Pdf.kt, Export.kt, dan Ocr.kt:
    /// Meliputi pemrosesan PDF, ekstraksi gambar JPEG dari PDF, pembuatan halaman PDF dengan PdfWriter,
    /// filter dokumen adaptif integral-image (ToDocument), Magic Color, KTP 2-Sisi A4, TTD Transparan,
    /// Parser Struk, Ringkasan Dokumen, Penerjemah Dokumen EN-ID, dan OCR Offline.
    /// </summary>
    public static class PdfEngine
    {
        public static readonly List<AspectOpt> ASPECTS = new()
        {
            new AspectOpt("Otomatis", 0),
            new AspectOpt("Asli", 1),
            new AspectOpt("A4", 2, 210f / 297f, true),
            new AspectOpt("Letter", 2, 8.5f / 11f, true),
            new AspectOpt("F4", 2, 215f / 330f, true),
            new AspectOpt("1:1", 2, 1f),
            new AspectOpt("4:3", 2, 4f / 3f),
            new AspectOpt("16:9", 2, 16f / 9f)
        };

        public const int F_PDF = 0;
        public const int F_WORD = 1;
        public const int F_JPG = 2;

        public static readonly int[] PDF_DIM = { 2000, 1600, 1000 };
        public static readonly int[] PDF_Q = { 88, 80, 65 };
        public static readonly int[] JPG_DIM = { 1920, 2560, 3840 };
        public const int JPG_Q = 92;
        public const int WORD_IMG_DIM = 1600;
        public const int WORD_IMG_Q = 80;
        public const int OCR_MIN_DIM = 1600;

        // ================= PEMBACA & RENDERER PDF MURNI C# =================

        /// <summary>True bila PDF ber-password dan belum dibuka (password belum diketahui).</summary>
        public static bool IsLocked(string pdfPath)
        {
            try
            {
                return PdfRenderer.IsEncrypted(pdfPath) && !PdfRenderer.IsOpenable(pdfPath);
            }
            catch { return false; }
        }

        /// <summary>
        /// Pastikan PDF siap diproses; bila ber-password, minta password ke pengguna.
        /// False = dibatalkan / tidak bisa dibuka.
        /// </summary>
        public static bool EnsureUnlocked(System.Windows.Forms.IWin32Window? owner, string pdfPath)
            => PdfRenderer.EnsureUnlocked(owner, pdfPath);

        public static int PageCount(string pdfPath)
        {
            int native = PdfRenderer.PageCount(pdfPath);
            if (native > 0) return native;
            if (IsLocked(pdfPath)) return 1;
            var pages = RenderAllPages(pdfPath, 400);
            int c = pages.Count;
            foreach (var b in pages) b.Dispose();
            return Math.Max(1, c);
        }

        public static Bitmap? Thumbnail(string pdfPath, int pageIdx = 0, int targetW = 300)
        {
            var pages = RenderAllPages(pdfPath, targetW * 2);
            if (pages.Count == 0) return null;
            int idx = Math.Clamp(pageIdx, 0, pages.Count - 1);
            Bitmap chosen = (Bitmap)pages[idx].Clone();
            foreach (var b in pages) b.Dispose();
            return chosen;
        }

        /// <summary>
        /// Mengekstrak halaman dari berkas PDF:
        /// 1. Mengekstrak seluruh stream gambar JPEG (/DCTDecode) dari dalam PDF.
        /// 2. Jika PDF berisi teks murni tanpa stream JPEG, mengekstrak teks PDF (BT ... ET) dan merendernya menjadi halaman A4 tajam.
        /// </summary>
        public static List<Bitmap> RenderAllPages(string pdfPath, int maxDim = 1600)
        {
            var pages = new List<Bitmap>();
            if (!File.Exists(pdfPath)) return pages;

            // 0. Perender PDF asli Windows (teks, vektor, scan, dan PDF ber-password).
            var status = PdfRenderer.TryRender(pdfPath, maxDim, out var nativePages);
            if (status == PdfOpenStatus.Ok && nativePages.Count > 0) return nativePages;
            if (status == PdfOpenStatus.PasswordRequired || status == PdfOpenStatus.WrongPassword)
                return pages; // PDF terkunci & password belum diketahui: jangan tampilkan isi sampah

            try
            {
                byte[] data = File.ReadAllBytes(pdfPath);
                // 1. Cari embedded JPEG streams (FF D8 FF ... FF D9) di dalam objek stream PDF
                var jpegs = ExtractEmbeddedJpegs(data);
                foreach (var jpgBytes in jpegs)
                {
                    try
                    {
                        using var ms = new MemoryStream(jpgBytes);
                        using var raw = new Bitmap(ms);
                        float scale = Math.Min(1f, (float)maxDim / Math.Max(raw.Width, raw.Height));
                        int w = Math.Max(1, (int)(raw.Width * scale));
                        int h = Math.Max(1, (int)(raw.Height * scale));
                        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(raw, 0, 0, w, h);
                        }
                        pages.Add(bmp);
                    }
                    catch { }
                }

                if (pages.Count > 0) return pages;

                // 2. Fallback untuk PDF berbasis teks: ekstrak teks dari operator Tj / TJ dan render ke halaman A4
                var extractedPagesText = ExtractPdfTextPages(data);
                if (extractedPagesText.Count == 0)
                    extractedPagesText.Add($"Dokumen PDF: {Path.GetFileName(pdfPath)}\nUkuran: {AppStorage.FmtSize(data.Length)}");

                foreach (string pageText in extractedPagesText)
                {
                    pages.Add(RenderTextPageBitmap(pageText, maxDim));
                }
            }
            catch
            {
                pages.Add(RenderTextPageBitmap($"Pratinjau PDF: {Path.GetFileName(pdfPath)}", maxDim));
            }
            return pages;
        }

        private static List<byte[]> ExtractEmbeddedJpegs(byte[] pdfBytes)
        {
            var list = new List<byte[]>();
            var latin = Encoding.GetEncoding(28591);
            string text = latin.GetString(pdfBytes);
            int pos = 0;
            while (pos < text.Length)
            {
                int dctIdx = text.IndexOf("/DCTDecode", pos, StringComparison.Ordinal);
                if (dctIdx < 0) break;
                int streamIdx = text.IndexOf("stream", dctIdx, StringComparison.Ordinal);
                if (streamIdx < 0 || streamIdx - dctIdx > 400) { pos = dctIdx + 10; continue; }

                int start = streamIdx + 6;
                if (start < pdfBytes.Length && pdfBytes[start] == '\r') start++;
                if (start < pdfBytes.Length && pdfBytes[start] == '\n') start++;

                int endStreamIdx = text.IndexOf("endstream", start, StringComparison.Ordinal);
                if (endStreamIdx < 0) break;

                int end = endStreamIdx;
                while (end > start && (pdfBytes[end - 1] == '\n' || pdfBytes[end - 1] == '\r'))
                    end--;

                int len = end - start;
                if (len > 64 && pdfBytes[start] == 0xFF && pdfBytes[start + 1] == 0xD8)
                {
                    byte[] jpg = new byte[len];
                    Buffer.BlockCopy(pdfBytes, start, jpg, 0, len);
                    list.Add(jpg);
                }
                pos = endStreamIdx + 9;
            }
            return list;
        }

        private static List<string> ExtractPdfTextPages(byte[] pdfBytes)
        {
            var pages = new List<string>();
            string raw = Encoding.GetEncoding(28591).GetString(pdfBytes);
            var matches = Regex.Matches(raw, @"BT\s*(.*?)\s*ET", RegexOptions.Singleline);
            var currentLines = new List<string>();
            foreach (Match m in matches)
            {
                string block = m.Groups[1].Value;
                if (block.Contains("3 Tr")) continue; // Lewati lapisan teks OCR tak terlihat bila tidak ada gambar
                var strMatches = Regex.Matches(block, @"\(((?:[^()\\]|\\.)*)\)\s*Tj");
                foreach (Match sm in strMatches)
                {
                    string s = sm.Groups[1].Value
                        .Replace("\\(", "(")
                        .Replace("\\)", ")")
                        .Replace("\\\\", "\\");
                    if (!string.IsNullOrWhiteSpace(s))
                        currentLines.Add(s);
                }
                if (currentLines.Count >= 38)
                {
                    pages.Add(string.Join("\n", currentLines));
                    currentLines.Clear();
                }
            }
            if (currentLines.Count > 0)
                pages.Add(string.Join("\n", currentLines));
            return pages;
        }

        private static Bitmap RenderTextPageBitmap(string content, int maxDim = 1400)
        {
            int w = Math.Max(595, (int)(maxDim * 0.707f));
            int h = Math.Max(842, maxDim);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float margin = w * 0.08f;
            using var font = new Font("Segoe UI", Math.Max(10f, w / 55f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.Black);
            var rect = new RectangleF(margin, margin, w - margin * 2, h - margin * 2);
            g.DrawString(content, font, brush, rect);
            return bmp;
        }

        public static byte[] EncodeJpeg(Bitmap bmp, int quality)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageDecoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (encoder != null)
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
                bmp.Save(ms, encoder, ep);
            }
            else
            {
                bmp.Save(ms, ImageFormat.Jpeg);
            }
            return ms.ToArray();
        }

        private static void WriteBitmapsToPdf(IReadOnlyList<Bitmap> bitmaps, string outPath, int jpegQuality = 85, string? password = null)
        {
            using var w = new PdfWriter(outPath, bitmaps.Count, password);
            foreach (var bmp in bitmaps)
            {
                bool land = bmp.Width > bmp.Height;
                float pw = land ? 842f : 595f;
                float ph = land ? 595f : 842f;
                byte[] jp = EncodeJpeg(bmp, jpegQuality);
                w.AddPage(jp, bmp.Width, bmp.Height, pw, ph, 0f, 0f, pw, ph, null);
            }
            w.Finish();
        }

        // ================= OPERASI PDF (MERGE, SPLIT, COMPRESS, ORGANIZE, SIGN, TEXT) =================

        public static void Merge(IReadOnlyList<string> pdfPaths, string outPath)
        {
            var allPages = new List<Bitmap>();
            try
            {
                foreach (string p in pdfPaths)
                    allPages.AddRange(RenderAllPages(p, 1600));
                if (allPages.Count == 0)
                    throw new InvalidOperationException("Tidak ada halaman PDF untuk digabungkan.");
                WriteBitmapsToPdf(allPages, outPath, 85);
            }
            finally
            {
                foreach (var b in allPages) b.Dispose();
            }
        }

        public static void Split(string pdfPath, int fromPage, int toPage, string outPath)
        {
            var allPages = RenderAllPages(pdfPath, 1600);
            try
            {
                var selected = new List<Bitmap>();
                for (int i = Math.Max(0, fromPage - 1); i < Math.Min(allPages.Count, toPage); i++)
                    selected.Add(allPages[i]);
                if (selected.Count == 0)
                    throw new InvalidOperationException("Rentang halaman tidak valid.");
                WriteBitmapsToPdf(selected, outPath, 85);
            }
            finally
            {
                foreach (var b in allPages) b.Dispose();
            }
        }

        public static void Compress(string pdfPath, int level, string outPath)
        {
            int maxDim = new[] { 1500, 1100, 800 }[Math.Clamp(level, 0, 2)];
            int q = new[] { 80, 65, 50 }[Math.Clamp(level, 0, 2)];
            var pages = RenderAllPages(pdfPath, maxDim);
            try
            {
                WriteBitmapsToPdf(pages, outPath, q);
            }
            finally
            {
                foreach (var b in pages) b.Dispose();
            }
        }

        public static void CompressToTarget(string pdfPath, int targetKb, string outPath)
        {
            long targetBytes = targetKb * 1024L;
            int[] dims = { 1600, 1300, 1000, 800, 640, 480 };
            int[] qualities = { 85, 75, 65, 50, 35, 22 };

            for (int idx = 0; idx < dims.Length; idx++)
            {
                var pages = RenderAllPages(pdfPath, dims[idx]);
                try
                {
                    WriteBitmapsToPdf(pages, outPath, qualities[idx]);
                }
                finally
                {
                    foreach (var b in pages) b.Dispose();
                }
                var fi = new FileInfo(outPath);
                if (fi.Exists && (fi.Length <= targetBytes || idx == dims.Length - 1))
                    break;
            }
        }

        public static int ToJpg(string pdfPath, string baseName)
        {
            var pages = RenderAllPages(pdfPath, 1800);
            int n = 0;
            try
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    string outFile = Path.Combine(AppStorage.PicturesDir, $"{baseName}_{i + 1}.jpg");
                    File.WriteAllBytes(outFile, EncodeJpeg(pages[i], 92));
                    n++;
                }
            }
            finally
            {
                foreach (var b in pages) b.Dispose();
            }
            return n;
        }

        public static void OrganizePdf(string pdfPath, IReadOnlyList<int> pageOrder, IReadOnlyDictionary<int, int> rotations, string outPath)
        {
            var srcPages = RenderAllPages(pdfPath, 1600);
            var outPages = new List<Bitmap>();
            try
            {
                foreach (int origIdx in pageOrder)
                {
                    if (origIdx >= 0 && origIdx < srcPages.Count)
                    {
                        int rot = rotations.TryGetValue(origIdx, out int r) ? r : 0;
                        Bitmap oriented = Orient(srcPages[origIdx], rot, false);
                        outPages.Add((Bitmap)oriented.Clone());
                        if (!ReferenceEquals(oriented, srcPages[origIdx])) oriented.Dispose();
                    }
                }
                WriteBitmapsToPdf(outPages, outPath, 85);
            }
            finally
            {
                foreach (var b in srcPages) b.Dispose();
                foreach (var b in outPages) b.Dispose();
            }
        }

        public static int ExtractImagesFromPdf(string pdfPath, string baseName)
            => ToJpg(pdfPath, $"{baseName}_foto");

        public static (int Count, long SavedBytes) CompressGalleryImages(IReadOnlyList<string> imagePaths, int targetMaxDim, int quality)
        {
            int count = 0;
            long savedBytes = 0;
            for (int idx = 0; idx < imagePaths.Count; idx++)
            {
                string src = imagePaths[idx];
                if (!File.Exists(src)) continue;
                long origSize = new FileInfo(src).Length;
                using var bmp = Decode(src, targetMaxDim);
                byte[] jpg = EncodeJpeg(bmp, quality);
                string dst = Path.Combine(AppStorage.PicturesDir, $"Kompres_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{idx + 1}.jpg");
                File.WriteAllBytes(dst, jpg);
                if (origSize > jpg.Length) savedBytes += (origSize - jpg.Length);
                count++;
            }
            return (count, savedBytes);
        }

        public static int BatchImagesToIndividualPdf(IReadOnlyList<string> imagePaths)
        {
            int count = 0;
            foreach (string path in imagePaths)
            {
                if (!File.Exists(path)) continue;
                string baseName = AppStorage.BaseName(Path.GetFileName(path));
                string outPdf = AppStorage.NamedOut($"Batch_{baseName}", "pdf");
                ImagesToPdf(new[] { path }, outPdf, mode: 0);
                count++;
            }
            return count;
        }

        public static void ImagesToPdf(IReadOnlyList<string> imagePaths, string outPath, int mode = 0)
        {
            var pages = new List<Bitmap>();
            try
            {
                foreach (string p in imagePaths)
                {
                    using var raw = Decode(p, 1600);
                    Bitmap filtered = ApplyMode((Bitmap)raw.Clone(), mode);
                    pages.Add(filtered);
                }
                using var w = new PdfWriter(outPath, pages.Count, null);
                foreach (var bmp in pages)
                {
                    bool land = bmp.Width > bmp.Height;
                    float pw = land ? 842f : 595f;
                    float ph = land ? 595f : 842f;
                    float m = 16f;
                    float s = Math.Min((pw - 2 * m) / bmp.Width, (ph - 2 * m) / bmp.Height);
                    float dw = bmp.Width * s;
                    float dh = bmp.Height * s;
                    float l = (pw - dw) / 2f;
                    float t = (ph - dh) / 2f;
                    byte[] jp = EncodeJpeg(bmp, 85);
                    w.AddPage(jp, bmp.Width, bmp.Height, pw, ph, l, t, dw, dh, null);
                }
                w.Finish();
            }
            finally
            {
                foreach (var b in pages) b.Dispose();
            }
        }

        public static void TextToPdf(string content, string outPath)
        {
            string[] allLines = content.Replace("\r\n", "\n").Split('\n');
            const int linesPerPage = 42;
            var pageBitmaps = new List<Bitmap>();
            try
            {
                for (int i = 0; i < Math.Max(1, allLines.Length); i += linesPerPage)
                {
                    string chunk = string.Join("\n", allLines.Skip(i).Take(linesPerPage));
                    pageBitmaps.Add(RenderTextPageBitmap(chunk, 1600));
                }
                WriteBitmapsToPdf(pageBitmaps, outPath, 88);
            }
            finally
            {
                foreach (var b in pageBitmaps) b.Dispose();
            }
        }

        public static void Sign(string pdfPath, Bitmap sig, int pageNo, int pos, string outPath)
        {
            var pages = RenderAllPages(pdfPath, 1600);
            try
            {
                int idx = Math.Clamp(pageNo - 1, 0, Math.Max(0, pages.Count - 1));
                if (pages.Count > 0)
                {
                    var bmp = pages[idx];
                    using var g = Graphics.FromImage(bmp);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float sw = bmp.Width * 0.28f;
                    float sh = sw * sig.Height / Math.Max(1, sig.Width);
                    if (sh > bmp.Height * 0.2f)
                    {
                        sh = bmp.Height * 0.2f;
                        sw = sh * sig.Width / Math.Max(1, sig.Height);
                    }
                    float x = pos switch
                    {
                        0 => 48f,
                        1 => (bmp.Width - sw) / 2f,
                        _ => bmp.Width - sw - 48f
                    };
                    float y = bmp.Height - sh - 64f;
                    g.DrawImage(sig, new RectangleF(x, y, sw, sh));
                }
                WriteBitmapsToPdf(pages, outPath, 88);
            }
            finally
            {
                foreach (var b in pages) b.Dispose();
            }
        }

        // ================= PARSER STRUK, TERJEMAHAN, TTD TRANSPARAN, RINGKASAN =================

        public static ReceiptInfo ParseReceipt(string ocrText)
        {
            var lines = ocrText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            string merchant = lines.Count > 0 ? lines[0] : "Toko / Merchant";
            if (merchant.Length < 3 && lines.Count > 1) merchant = lines[1];

            string date = "";
            var dateRegex = new Regex(@"(?i)(\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}|\d{1,2}\s+(jan|feb|mar|apr|mei|jun|jul|agu|sep|okt|nov|des)\w*\s+\d{2,4})");
            foreach (string l in lines)
            {
                var m = dateRegex.Match(l);
                if (m.Success) { date = m.Value; break; }
            }
            if (string.IsNullOrWhiteSpace(date))
                date = DateTime.Now.ToString("dd/MM/yyyy", new CultureInfo("id-ID"));

            string total = "";
            var totalRegex = new Regex(@"(?i)(total|grand total|bayar|jumlah|rp)\s*[:=]?\s*(rp)?\s*([\d.,]{3,15})");
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var m = totalRegex.Match(lines[i]);
                if (m.Success)
                {
                    total = m.Groups[3].Value;
                    if (string.IsNullOrWhiteSpace(total)) total = m.Value;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(total)) total = "Rp 0";

            var items = lines.Where(l => !l.Contains(merchant, StringComparison.OrdinalIgnoreCase) &&
                                         !l.Contains("total", StringComparison.OrdinalIgnoreCase))
                             .Take(8).ToList();
            return new ReceiptInfo(merchant, date, total, items);
        }

        public static string TranslateDocText(string text, bool toIndonesian)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            if (toIndonesian)
            {
                var enToId = new Dictionary<string, string>
                {
                    ["agreement"] = "perjanjian", ["contract"] = "kontrak", ["article"] = "pasal", ["section"] = "bagian",
                    ["invoice"] = "faktur", ["receipt"] = "struk / nota", ["total"] = "jumlah total",
                    ["date"] = "tanggal", ["name"] = "nama", ["signature"] = "tanda tangan", ["approved"] = "disetujui",
                    ["confidential"] = "rahasia", ["document"] = "dokumen", ["payment"] = "pembayaran",
                    ["party"] = "pihak", ["first party"] = "pihak pertama", ["second party"] = "pihak kedua",
                    ["subject"] = "perihal", ["description"] = "keterangan", ["amount"] = "nominal", ["address"] = "alamat"
                };
                string res = text;
                foreach (var kv in enToId)
                    res = Regex.Replace(res, $@"(?i)\b{Regex.Escape(kv.Key)}\b", kv.Value.ToUpperInvariant());
                return res;
            }
            else
            {
                var idToEn = new Dictionary<string, string>
                {
                    ["perjanjian"] = "agreement", ["kontrak"] = "contract", ["pasal"] = "article", ["bagian"] = "section",
                    ["faktur"] = "invoice", ["nota"] = "receipt", ["total"] = "total", ["tanggal"] = "date",
                    ["nama"] = "name", ["tanda tangan"] = "signature", ["disetujui"] = "approved", ["rahasia"] = "confidential",
                    ["dokumen"] = "document", ["pembayaran"] = "payment", ["alamat"] = "address"
                };
                string res = text;
                foreach (var kv in idToEn)
                    res = Regex.Replace(res, $@"(?i)\b{Regex.Escape(kv.Key)}\b", kv.Value.ToUpperInvariant());
                return res;
            }
        }

        public static Bitmap ExtractTransparentSignature(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = src.GetPixel(x, y);
                    int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                    if (luma > 195)
                    {
                        outBmp.SetPixel(x, y, Color.FromArgb(0, 255, 255, 255));
                    }
                    else
                    {
                        int alpha = Math.Clamp((int)((255 - luma) * 1.3f), 0, 255);
                        outBmp.SetPixel(x, y, Color.FromArgb(alpha, c.R, c.G, c.B));
                    }
                }
            }
            return outBmp;
        }

        public static DocSummary ExtractDocSummary(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var dates = new List<string>();
            var amounts = new List<string>();
            var numbers = new List<string>();

            var dateRegex = new Regex(@"(?i)(\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4}|\d{1,2}\s+(jan|feb|mar|apr|mei|jun|jul|agu|sep|okt|nov|des)\w*\s+\d{2,4})");
            var amountRegex = new Regex(@"(?i)(rp|idr)?\s*([\d.,]{4,15})");
            var numberRegex = new Regex(@"(?i)(pasal|nomor|no\.|no)\s*[:=]?\s*([\w./\-]+)");

            foreach (string l in lines)
            {
                var dm = dateRegex.Match(l);
                if (dm.Success && !dates.Contains(dm.Value)) dates.Add(dm.Value);
                var nm = numberRegex.Match(l);
                if (nm.Success && !numbers.Contains(nm.Value)) numbers.Add(nm.Value);
                if (l.Contains("rp", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("total", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("biaya", StringComparison.OrdinalIgnoreCase))
                {
                    var am = amountRegex.Match(l);
                    if (am.Success && !amounts.Contains(am.Value)) amounts.Add(am.Value);
                }
            }
            return new DocSummary(numbers.Take(4).ToList(), dates.Take(4).ToList(), amounts.Take(4).ToList(), lines.Take(5).ToList());
        }

        // ================= DECODE, FILTER, ORIENT, DESKEW, RENDER SCAN & KTP A4 =================

        public static Bitmap Decode(string filePath, int maxDim)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var raw = new Bitmap(fs);
            float f = Math.Min(1f, (float)maxDim / Math.Max(raw.Width, raw.Height));
            int w = Math.Max(1, (int)(raw.Width * f));
            int h = Math.Max(1, (int)(raw.Height * f));
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(raw, 0, 0, w, h);
            }
            return outBmp;
        }

        /// <summary>
        /// Mode dokumen: ambang adaptif lokal (integral image) -> hitam-putih tajam, ukuran PDF kecil.
        /// </summary>
        public static Bitmap ToDocument(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            int[] g = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = src.GetPixel(x, y);
                    g[y * w + x] = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                }
            }

            int iw = w + 1;
            long[] integ = new long[iw * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += g[y * w + x];
                    integ[(y + 1) * iw + x + 1] = integ[y * iw + x + 1] + row;
                }
            }

            int r = Math.Max(8, Math.Max(w, h) / 32);
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - r);
                int y1 = Math.Min(h, y + r + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r);
                    int x1 = Math.Min(w, x + r + 1);
                    long sum = integ[y1 * iw + x1] - integ[y0 * iw + x1] - integ[y1 * iw + x0] + integ[y0 * iw + x0];
                    long mean = sum / ((y1 - y0) * (x1 - x0));
                    bool isBlack = g[y * w + x] * 100 < mean * 92;
                    outBmp.SetPixel(x, y, isBlack ? Color.Black : Color.White);
                }
            }
            return outBmp;
        }

        public static Bitmap ApplyMode(Bitmap raw, int mode)
        {
            if (mode == 2)
            {
                var docBmp = ToDocument(raw);
                raw.Dispose();
                return docBmp;
            }
            if (mode == 0) return raw;

            int w = raw.Width, h = raw.Height;
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = raw.GetPixel(x, y);
                    if (mode == 1)
                    {
                        int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        outBmp.SetPixel(x, y, Color.FromArgb(c.A, luma, luma, luma));
                    }
                    else
                    {
                        // Mode 3: Magic Color (kontras 1.25x, offset -25, saturasi 1.15x)
                        int r = Math.Clamp((int)(c.R * 1.25f - 25f), 0, 255);
                        int g = Math.Clamp((int)(c.G * 1.25f - 25f), 0, 255);
                        int b = Math.Clamp((int)(c.B * 1.25f - 25f), 0, 255);
                        outBmp.SetPixel(x, y, Color.FromArgb(c.A, r, g, b));
                    }
                }
            }
            raw.Dispose();
            return outBmp;
        }

        public static Bitmap AdjustBrightnessContrast(Bitmap src, int bright, int contrast)
        {
            if (bright == 0 && contrast == 0) return src;
            int w = src.Width, h = src.Height;
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            float c = 1f + contrast / 100f;
            float t = 128f * (1f - c) + bright / 100f * 120f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color col = src.GetPixel(x, y);
                    int r = Math.Clamp((int)(col.R * c + t), 0, 255);
                    int g = Math.Clamp((int)(col.G * c + t), 0, 255);
                    int b = Math.Clamp((int)(col.B * c + t), 0, 255);
                    outBmp.SetPixel(x, y, Color.FromArgb(col.A, r, g, b));
                }
            }
            src.Dispose();
            return outBmp;
        }

        public static Bitmap Orient(Bitmap src, int rot, bool flip)
        {
            int normRot = ((rot % 360) + 360) % 360;
            if (normRot == 0 && !flip) return src;
            var clone = (Bitmap)src.Clone();
            RotateFlipType rft = (normRot, flip) switch
            {
                (90, false) => RotateFlipType.Rotate90FlipNone,
                (180, false) => RotateFlipType.Rotate180FlipNone,
                (270, false) => RotateFlipType.Rotate270FlipNone,
                (0, true) => RotateFlipType.RotateNoneFlipX,
                (90, true) => RotateFlipType.Rotate90FlipX,
                (180, true) => RotateFlipType.Rotate180FlipX,
                (270, true) => RotateFlipType.Rotate270FlipX,
                _ => RotateFlipType.RotateNoneFlipNone
            };
            clone.RotateFlip(rft);
            return clone;
        }

        public static Bitmap Deskew(Bitmap src, float deg)
        {
            if (Math.Abs(deg) < 0.05f) return src;
            var outBmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(outBmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TranslateTransform(src.Width / 2f, src.Height / 2f);
                g.RotateTransform(deg);
                g.TranslateTransform(-src.Width / 2f, -src.Height / 2f);
                g.DrawImage(src, 0, 0);
            }
            return outBmp;
        }

        public static Bitmap RenderScan(ScanPage pg, int maxDim, int bright, int contrast)
        {
            var raw = Decode(pg.FilePath, maxDim);
            var oriented = Orient(raw, pg.Rot, pg.Flip);
            if (!ReferenceEquals(oriented, raw)) raw.Dispose();
            var rotated = Deskew(oriented, pg.Skew);
            if (!ReferenceEquals(rotated, oriented)) oriented.Dispose();

            int w = rotated.Width, h = rotated.Height;
            int x = Math.Clamp((int)(pg.Crop.Left * w), 0, Math.Max(0, w - 2));
            int y = Math.Clamp((int)(pg.Crop.Top * h), 0, Math.Max(0, h - 2));
            int cw = Math.Clamp((int)(pg.Crop.Width * w), 2, Math.Max(2, w - x));
            int ch = Math.Clamp((int)(pg.Crop.Height * h), 2, Math.Max(2, h - y));

            var cropped = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(rotated, new Rectangle(0, 0, cw, ch), new Rectangle(x, y, cw, ch), GraphicsUnit.Pixel);
            }
            rotated.Dispose();

            var baseBmp = AdjustBrightnessContrast(cropped, bright, contrast);

            // Lukis sensur hitam (blackout) jika ada
            if (pg.Redactions.Count > 0)
            {
                using var g = Graphics.FromImage(baseBmp);
                foreach (var r in pg.Redactions)
                {
                    float rx = r.Left * baseBmp.Width;
                    float ry = r.Top * baseBmp.Height;
                    float rw = r.Width * baseBmp.Width;
                    float rh = r.Height * baseBmp.Height;
                    g.FillRectangle(Brushes.Black, rx, ry, rw, rh);
                }
            }

            // Lukis watermark diagonal jika ada
            if (!string.IsNullOrWhiteSpace(pg.Watermark))
            {
                using var g = Graphics.FromImage(baseBmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(baseBmp.Width / 2f, baseBmp.Height / 2f);
                g.RotateTransform(-32f);
                float fontSize = Math.Max(14f, Math.Min(baseBmp.Width, baseBmp.Height) * 0.06f);
                using var wmFont = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var wmBrush = new SolidBrush(Color.FromArgb(95, 220, 38, 38));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(pg.Watermark, wmFont, wmBrush, PointF.Empty, sf);
            }

            return baseBmp;
        }

        public static ScanPage RenderKtpA4(ScanPage frontPg, ScanPage backPg, int maxDim, int bright, int contrast)
        {
            using var fBmp = RenderScan(frontPg, maxDim, bright, contrast);
            using var bBmp = RenderScan(backPg, maxDim, bright, contrast);

            int a4W = 1800, a4H = 2545;
            using var page = new Bitmap(a4W, a4H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(page))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var sfCenter = new StringFormat { Alignment = StringAlignment.Center };
                using var headerFont = new Font("Segoe UI", 36f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var subFont = new Font("Segoe UI", 22f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var labelFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var headerBrush = new SolidBrush(Theme.TEXT);
                using var subBrush = new SolidBrush(Theme.MUTED);
                using var labelBrush = new SolidBrush(Theme.SKY);

                g.DrawString("SALINAN KARTU IDENTITAS (KTP / SIM / ID CARD)", headerFont, headerBrush, a4W / 2f, 90f, sfCenter);
                string stamp = DateTime.Now.ToString("dd MMMM yyyy", new CultureInfo("id-ID"));
                g.DrawString($"Dipindai via Depup Office Desktop • Tanggal: {stamp}", subFont, subBrush, a4W / 2f, 145f, sfCenter);

                using var linePen = new Pen(ColorTranslator.FromHtml("#CBD5E1"), 3f);
                g.DrawLine(linePen, 100f, 200f, a4W - 100f, 200f);

                float cardW = 1200f;
                float cardH = cardW / 1.586f;
                using var borderPen = new Pen(ColorTranslator.FromHtml("#94A3B8"), 4f);

                // Depan
                float fLeft = (a4W - cardW) / 2f;
                float fTop = 300f;
                var fRect = new RectangleF(fLeft, fTop, cardW, cardH);
                g.DrawImage(fBmp, fRect);
                g.DrawRectangle(borderPen, fLeft, fTop, cardW, cardH);
                g.DrawString("● SISI DEPAN (FRONT)", labelFont, labelBrush, a4W / 2f, fTop + cardH + 25f, sfCenter);

                // Belakang
                float bLeft = (a4W - cardW) / 2f;
                float bTop = fTop + cardH + 160f;
                var bRect = new RectangleF(bLeft, bTop, cardW, cardH);
                g.DrawImage(bBmp, bRect);
                g.DrawRectangle(borderPen, bLeft, bTop, cardW, cardH);
                g.DrawString("● SISI BELAKANG (BACK)", labelFont, labelBrush, a4W / 2f, bTop + cardH + 25f, sfCenter);
            }

            string ktpPath = Path.Combine(AppStorage.AppDataDir, $"ktp_a4_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jpg");
            File.WriteAllBytes(ktpPath, EncodeJpeg(page, 92));
            return new ScanPage(ktpPath);
        }

        // ================= EXPORT LAYOUT & OCR =================

        private static Bitmap FitRatio(Bitmap src, float ratio)
        {
            float inner = 0.95f;
            float cw = Math.Max(src.Width / inner, src.Height / inner * ratio);
            float ch = cw / ratio;
            int w = Math.Max(src.Width, (int)cw);
            int h = Math.Max(src.Height, (int)ch);
            var o = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(o))
            {
                g.Clear(Color.White);
                g.DrawImage(src, (w - src.Width) / 2f, (h - src.Height) / 2f, src.Width, src.Height);
            }
            return o;
        }

        public static LaidPage LayoutPage(Bitmap src, AspectOpt a)
        {
            if (a.Kind == 0)
            {
                bool land = src.Width > src.Height;
                float pw = land ? 842f : 595f;
                float ph = land ? 595f : 842f;
                float mg = 16f;
                float s = Math.Min((pw - 2 * mg) / src.Width, (ph - 2 * mg) / src.Height);
                float dw = src.Width * s;
                float dh = src.Height * s;
                return new LaidPage(src, src, pw, ph, (pw - dw) / 2f, (ph - dh) / 2f, dw, dh);
            }
            if (a.Kind == 1)
            {
                float s = 842f / Math.Max(src.Width, src.Height);
                float pw = src.Width * s;
                float ph = src.Height * s;
                return new LaidPage(src, src, pw, ph, 0f, 0f, pw, ph);
            }
            float r = a.Ratio;
            if (a.FollowOrientation && src.Width > src.Height) r = 1f / r;
            var fitted = FitRatio(src, r);
            float fpw = r <= 1f ? 595f : 842f;
            float fph = fpw / r;
            return new LaidPage(fitted, src, fpw, fph, 0f, 0f, fpw, fph);
        }

        public static Bitmap ProcessScanPage(ScanPage pg, int mode, int maxDim, int bright, int contrast)
            => ApplyMode(RenderScan(pg, maxDim, bright, contrast), mode);

        public static long EstimatePageSize(ScanPage pg, int fmt, int opt, int mode, int bright, int contrast, AspectOpt a, bool ocr)
        {
            (int dim0, int q) = fmt switch
            {
                F_PDF => (PDF_DIM[Math.Clamp(opt, 0, 2)], PDF_Q[Math.Clamp(opt, 0, 2)]),
                F_WORD => (WORD_IMG_DIM, WORD_IMG_Q),
                _ => (JPG_DIM[Math.Clamp(opt, 0, 2)], JPG_Q)
            };
            int dim = (fmt == F_PDF && ocr) ? Math.Max(dim0, OCR_MIN_DIM) : dim0;
            using var lay = LayoutPage(ProcessScanPage(pg, mode, dim, bright, contrast), a);
            return EncodeJpeg(lay.Bmp, q).Length;
        }

        public static void ExportPdf(
            IReadOnlyList<ScanPage> pages, string outPath, int mode, int bright, int contrast,
            int opt, AspectOpt a, bool ocr, string? password)
        {
            int dim = ocr ? Math.Max(PDF_DIM[Math.Clamp(opt, 0, 2)], OCR_MIN_DIM) : PDF_DIM[Math.Clamp(opt, 0, 2)];
            int q = PDF_Q[Math.Clamp(opt, 0, 2)];
            using var w = new PdfWriter(outPath, pages.Count, password);
            try
            {
                foreach (var pg in pages)
                {
                    using var lay = LayoutPage(ProcessScanPage(pg, mode, dim, bright, contrast), a);
                    OcrPage? res = ocr ? RunOcr(lay.Bmp) : null;
                    byte[] jp = EncodeJpeg(lay.Bmp, q);
                    w.AddPage(jp, lay.Bmp.Width, lay.Bmp.Height, lay.PageW, lay.PageH, lay.X, lay.Y, lay.W, lay.H, res);
                }
                w.Finish();
            }
            catch
            {
                w.Abort();
                throw;
            }
        }

        public static void ExportDocx(
            IReadOnlyList<ScanPage> pages, string outPath, int mode, int bright, int contrast,
            bool asImage, AspectOpt a)
        {
            using var d = new DocxWriter(outPath);
            for (int i = 0; i < pages.Count; i++)
            {
                var pg = pages[i];
                d.BeginPage();
                if (asImage)
                {
                    using var lay = LayoutPage(ProcessScanPage(pg, mode, WORD_IMG_DIM, bright, contrast), a);
                    d.Image(EncodeJpeg(lay.Bmp, WORD_IMG_Q), lay.Bmp.Width, lay.Bmp.Height);
                }
                else
                {
                    using var bmp = ProcessScanPage(pg, mode, 2000, bright, contrast);
                    var res = RunOcr(bmp);
                    if (res.Paras.Count == 0)
                        d.Note($"(Tidak ada teks terdeteksi di halaman {i + 1})");
                    else
                        foreach (string p in res.Paras) d.Paragraph(p);
                }
            }
            d.Finish();
        }

        public static int ExportJpg(
            IReadOnlyList<ScanPage> pages, string baseName, int mode, int bright, int contrast, int opt, AspectOpt a)
        {
            int n = 0;
            int dim = JPG_DIM[Math.Clamp(opt, 0, 2)];
            for (int idx = 0; idx < pages.Count; idx++)
            {
                using var lay = LayoutPage(ProcessScanPage(pages[idx], mode, dim, bright, contrast), a);
                string fileName = pages.Count == 1 ? $"{baseName}.jpg" : $"{baseName}_{idx + 1}.jpg";
                string dst = Path.Combine(AppStorage.PicturesDir, fileName);
                File.WriteAllBytes(dst, EncodeJpeg(lay.Bmp, JPG_Q));
                n++;
            }
            return n;
        }

        /// <summary>Pesan kesalahan OCR terakhir (null bila OCR terakhir berhasil).</summary>
        public static string? LastOcrError { get; private set; }

        /// <summary>OCR offline sungguhan (mesin OCR bawaan Windows) → baris, paragraf, dan kotak teks.</summary>
        public static OcrPage RunOcr(Bitmap bmp)
        {
            LastOcrError = null;
            try
            {
                return WinOcrRunner.Recognize(bmp);
            }
            catch (Exception ex)
            {
                LastOcrError = ex is InvalidOperationException ? ex.Message : $"OCR gagal: {ex.Message}";
                return new OcrPage("", new List<string>(), new List<OcrLine>(), bmp.Width, bmp.Height);
            }
        }
    }
}
