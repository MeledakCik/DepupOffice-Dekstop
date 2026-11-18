using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Alat PDF hasil migrasi Android v4.1 (PdfPageNumberingManager, PdfTools.watermark, PdfProtectionManager,
    /// ProtectedDocumentManager, UniversalMergeManager, PdfTools.lockLossless, PdfTools.toWord).
    /// Berbeda dengan versi Android yang merender halaman jadi gambar, versi desktop menimpa (overlay) isi halaman
    /// ASLI: teks tetap tajam &amp; bisa dicari, ukuran berkas hampir tidak bertambah.
    /// </summary>
    public static class PdfToolsX
    {
        // ------------------------------------------------------------------ util

        /// <summary>Buka PDF memakai password yang sudah dimasukkan pengguna di sesi ini (bila ada).</summary>
        public static PdfFile OpenPdf(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 400L << 20) throw new InvalidOperationException("Berkas terlalu besar (maks. 400 MB)");
            var pw = PdfRenderer.GetCachedPassword(path) ?? "";
            return PdfFile.Open(bytes, pw);
        }

        private static string F(double v) => Pdf.Fmt(Math.Round(v, 3));

        /// <summary>Matriks dari koordinat "visual" (asal kiri-bawah halaman sebagaimana tampil) ke ruang pengguna PDF.</summary>
        private static (string cm, float vw, float vh) VisualSpace(PdfFile.Page p)
        {
            float x0 = p.Box[0], y0 = p.Box[1], x1 = p.Box[2], y1 = p.Box[3];
            return p.Rotate switch
            {
                90 => ($"0 1 -1 0 {F(x1)} {F(y0)} cm", p.Height, p.Width),
                180 => ($"-1 0 0 -1 {F(x1)} {F(y1)} cm", p.Width, p.Height),
                270 => ($"0 -1 1 0 {F(x0)} {F(y1)} cm", p.Height, p.Width),
                _ => ($"1 0 0 1 {F(x0)} {F(y0)} cm", p.Width, p.Height)
            };
        }

        /// <summary>Bungkus isi halaman asli dengan q/Q lalu tempelkan <paramref name="overlayOps"/> di atasnya.</summary>
        private static void Overlay(PdfSave.Ctx ctx, PdfFile f, PdfFile.Page p, string overlayOps, IEnumerable<int> alphas)
        {
            var pd = ctx.PageDict(p);
            var contents = new List<object?>();
            contents.Add(ctx.Add(new PStream(new PDict(), Pdf.Latin1.GetBytes("q\n"))));
            var c = pd["Contents"];
            if (f.Res(c) is List<object?> l) contents.AddRange(l); else if (c != null) contents.Add(c);
            var ov = new PDict(); ov["Filter"] = new PName("FlateDecode");
            contents.Add(ctx.Add(new PStream(ov, Pdf.Deflate(Pdf.Latin1.GetBytes("\nQ\n" + overlayOps)))));
            pd["Contents"] = contents;

            // Resources eksplisit di halaman (salinan) + font & ExtGState milik Depup
            var res = new PDict(p.Resources);
            PDict Sub(string k)
            {
                var src = f.RDict(res[k]);
                var d = src != null ? new PDict(src) : new PDict();
                res[k] = d; return d;
            }
            var fonts = Sub("Font");
            var fb = new PDict(); fb["Type"] = new PName("Font"); fb["Subtype"] = new PName("Type1"); fb["BaseFont"] = new PName("Helvetica-Bold"); fb["Encoding"] = new PName("WinAnsiEncoding");
            var fr = new PDict(); fr["Type"] = new PName("Font"); fr["Subtype"] = new PName("Type1"); fr["BaseFont"] = new PName("Helvetica"); fr["Encoding"] = new PName("WinAnsiEncoding");
            fonts["DpFB"] = ctx.Add(fb); fonts["DpFR"] = ctx.Add(fr);
            var gs = Sub("ExtGState");
            foreach (var a in alphas.Distinct())
            {
                var g = new PDict(); g["ca"] = a / 255.0; g["CA"] = a / 255.0;
                gs["DpA" + a] = g;
            }
            pd["Resources"] = res;
        }

        private static string Rgb(Color c) => $"{F(c.R / 255.0)} {F(c.G / 255.0)} {F(c.B / 255.0)}";

        private static string TextOp(string s, float x, float y, float size, Color c, bool bold, int alpha = 255) =>
            "q " + (alpha < 255 ? $"/DpA{alpha} gs " : "") + $"{Rgb(c)} rg BT /{(bold ? "DpFB" : "DpFR")} {F(size)} Tf {F(x)} {F(y)} Td {PdfCanvas.Lit(s)} Tj ET Q\n";

        private static void SaveEdited(PdfFile f, string outPath, Action<PdfSave.Ctx> edit)
        {
            string tmp = outPath + ".tmp";
            try
            {
                using (var fs = File.Create(tmp)) PdfSave.Write(f, fs, null, edit);
                File.Move(tmp, outPath, true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // ------------------------------------------------------------------ nomor halaman

        /// <summary>
        /// Nomor halaman + header opsional. <paramref name="format"/>: 0 "Hal X dari Y", 1 "Halaman X", 2 "X".
        /// <paramref name="position"/>: 0 tengah bawah, 1 kanan bawah, 2 kiri bawah.
        /// </summary>
        public static int AddPageNumbers(string src, string outPath, int format, int position, string header, bool skipCover)
        {
            var f = OpenPdf(src);
            var pages = f.Pages();
            int total = pages.Count;
            SaveEdited(f, outPath, ctx =>
            {
                for (int i = 0; i < total; i++)
                {
                    int n = i + 1;
                    if (skipCover && n == 1) continue;
                    var p = pages[i];
                    var (cm, w, h) = VisualSpace(p);
                    string num = format switch { 0 => $"Hal {n} dari {total}", 1 => $"Halaman {n}", _ => $"{n}" };
                    float size = Math.Clamp(Math.Min(w, h) * 0.0185f, 8f, 14f);
                    float margin = w * 0.05f, y = h * 0.03f;
                    float tw = PdfCanvas.Measure(num, size, false);
                    float x = position switch { 0 => (w - tw) / 2f, 1 => w - margin - tw, _ => margin };
                    var ops = new StringBuilder("q ").Append(cm).Append('\n');
                    ops.Append(TextOp(num, x, y, size, Color.FromArgb(0x55, 0x55, 0x55), false));
                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        string hd = header.Trim(); if (hd.Length > 80) hd = hd[..80];
                        float hs = size * 0.95f;
                        ops.Append(TextOp(hd, margin, h - h * 0.04f, hs, Color.FromArgb(0x88, 0x88, 0x88), true));
                        ops.Append($"q 0.8 0.8 0.8 RG 0.8 w {F(margin)} {F(h - h * 0.048f)} m {F(w - margin)} {F(h - h * 0.048f)} l S Q\n");
                    }
                    ops.Append("Q\n");
                    Overlay(ctx, f, p, ops.ToString(), Array.Empty<int>());
                }
            });
            return total;
        }

        // ------------------------------------------------------------------ watermark

        /// <summary>Watermark teks di setiap halaman. <paramref name="style"/>: 0 diagonal besar, 1 pola ubin, 2 stempel pojok kanan bawah.</summary>
        public static int WatermarkPdf(string src, string outPath, string text, int style, Color color, int alpha)
        {
            alpha = Math.Clamp(alpha, 10, 255);
            var f = OpenPdf(src);
            var pages = f.Pages();
            SaveEdited(f, outPath, ctx =>
            {
                foreach (var p in pages)
                {
                    var (cm, w, h) = VisualSpace(p);
                    var ops = new StringBuilder("q ").Append(cm).Append('\n');
                    if (style == 2)
                    {
                        float size = Math.Min(w, h) * 0.032f, margin = Math.Min(w, h) * 0.04f;
                        float tw = PdfCanvas.Measure(text, size, true);
                        ops.Append(TextOp(text, w - margin - tw, margin, size, color, true, alpha));
                    }
                    else
                    {
                        double ang = 35 * Math.PI / 180, cs = Math.Cos(ang), sn = Math.Sin(ang);
                        ops.Append($"1 0 0 1 {F(w / 2)} {F(h / 2)} cm {F(cs)} {F(sn)} {F(-sn)} {F(cs)} 0 0 cm\n");
                        if (style == 0)
                        {
                            float size = Math.Min(w, h) * 0.14f;
                            float diag = (float)Math.Sqrt(w * w + h * h) * 0.8f;
                            float tw = PdfCanvas.Measure(text, size, true);
                            if (tw > diag) { size *= diag / tw; tw = diag; }
                            ops.Append(TextOp(text, -tw / 2, -size * 0.35f, size, color, true, alpha));
                        }
                        else
                        {
                            float size = Math.Min(w, h) * 0.045f;
                            float tw = PdfCanvas.Measure(text, size, true);
                            float stepX = tw + Math.Min(w, h) * 0.12f, stepY = size * 4.5f;
                            float span = (float)Math.Sqrt(w * w + h * h);
                            int row = 0;
                            for (float y = -span; y < span; y += stepY, row++)
                                for (float x = -span + (row % 2 == 1 ? stepX / 2 : 0); x < span; x += stepX)
                                    ops.Append(TextOp(text, x - tw / 2, y, size, color, true, alpha));
                        }
                    }
                    ops.Append("Q\n");
                    Overlay(ctx, f, p, ops.ToString(), new[] { alpha });
                }
            });
            return pages.Count;
        }

        // ------------------------------------------------------------------ kunci / izin

        /// <summary>Kunci AES-256 TANPA render ulang (teks tetap bisa dicari). userPw kosong = tanpa password pembuka.</summary>
        public static void LockLossless(string src, string outPath, string userPw, string ownerPw, PdfSave.Perms perms)
        {
            var f = OpenPdf(src);
            using var fs = File.Create(outPath);
            PdfSave.Write(f, fs, new PdfSave.Lock(userPw, string.IsNullOrEmpty(ownerPw) ? Guid.NewGuid().ToString() : ownerPw, perms));
        }

        /// <summary>Proteksi Anti-Kopi &amp; Anti-Cetak (dibuka tanpa password, hak akses dikunci).</summary>
        public static void RestrictPermissions(string src, string outPath, bool canCopy, bool canPrint, bool canModify) =>
            LockLossless(src, outPath, "", Guid.NewGuid().ToString(), new PdfSave.Perms(canPrint, canCopy, canModify, canModify));

        /// <summary>
        /// Dokumen Terlindungi lengkap (alur ProtectedDocumentManager Android): kunci izin + password pembuka opsional
        /// (AES-256), lalu dibungkus Sampul DepUp + HMAC bila <paramref name="coverHmac"/>.
        /// </summary>
        public static void ProtectDocument(string src, string outPath, bool coverHmac, string userPw, bool canCopy, bool canPrint, bool canModify, string teacherPw)
        {
            string ownerPw = string.IsNullOrWhiteSpace(teacherPw) ? Guid.NewGuid().ToString() : teacherPw;
            var perms = new PdfSave.Perms(canPrint, canCopy, canModify, canModify);
            var f = OpenPdf(src);
            byte[] locked;
            using (var ms = new MemoryStream()) { PdfSave.Write(f, ms, new PdfSave.Lock(userPw, ownerPw, perms)); locked = ms.ToArray(); }
            if (coverHmac) ProtectedDoc.Protect(locked, ownerPw, outPath);
            else File.WriteAllBytes(outPath, locked);
        }

        // ------------------------------------------------------------------ gabung / pisah / susun (lossless)

        private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
        public static bool IsImage(string p) => ImageExt.Contains(Path.GetExtension(p).ToLowerInvariant());

        /// <summary>Halaman A4 berisi satu gambar (rasio dijaga, orientasi mengikuti gambar).</summary>
        private static PdfFile ImagePagePdf(string imgPath)
        {
            using var bmp = PdfEngine.Decode(imgPath, 2400);
            bool land = bmp.Width > bmp.Height;
            float pw = land ? PdfCanvas.A4H : PdfCanvas.A4W, ph = land ? PdfCanvas.A4W : PdfCanvas.A4H;
            var c = new PdfCanvas(pw, ph);
            float s = Math.Min(pw / bmp.Width, ph / bmp.Height);
            float dw = bmp.Width * s, dh = bmp.Height * s;
            c.Image(bmp, new RectangleF((pw - dw) / 2, (ph - dh) / 2, dw, dh), 255, 88, 2400);
            return PdfFile.Open(c.ToBytes());
        }

        /// <summary>Gabung campuran PDF &amp; foto (Universal Merge) jadi satu PDF, halaman PDF tetap asli.</summary>
        public static int MergeAny(IReadOnlyList<string> inputs, string outPath, Action<int, int>? progress = null)
        {
            if (inputs.Count == 0) throw new ArgumentException("Tidak ada file yang dipilih");
            var comp = new PdfComposer();
            for (int i = 0; i < inputs.Count; i++)
            {
                progress?.Invoke(i + 1, inputs.Count);
                var path = inputs[i];
                PdfFile f = IsImage(path) ? ImagePagePdf(path) : OpenPdf(path);
                if (f.Encrypted && !f.OwnerAuth && !f.CanModify)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} dibatasi pemiliknya (tidak boleh diubah). Buka dengan password pemilik terlebih dahulu.");
                foreach (var p in f.Pages()) comp.AddPage(f, p);
            }
            if (comp.PageCount == 0) throw new InvalidOperationException("Tidak ada halaman yang berhasil digabung");
            comp.Save(outPath);
            return comp.PageCount;
        }

        /// <summary>Ambil halaman <paramref name="from"/>..<paramref name="to"/> (1-based) tanpa render ulang.</summary>
        public static void SplitLossless(string src, int from, int to, string outPath)
        {
            var f = OpenPdf(src);
            var pages = f.Pages();
            if (from < 1 || to > pages.Count || from > to) throw new InvalidOperationException("Rentang halaman tidak valid.");
            var comp = new PdfComposer();
            for (int i = from - 1; i < to; i++) comp.AddPage(f, pages[i]);
            comp.Save(outPath);
        }

        /// <summary>Susun ulang, hapus, dan putar halaman tanpa render ulang. <paramref name="order"/> berisi indeks 0-based.</summary>
        public static void OrganizeLossless(string src, IReadOnlyList<int> order, IReadOnlyDictionary<int, int> rotations, string outPath)
        {
            var f = OpenPdf(src);
            var pages = f.Pages();
            var comp = new PdfComposer();
            foreach (var i in order)
                if (i >= 0 && i < pages.Count) comp.AddPage(f, pages[i], rotations.TryGetValue(i, out var r) ? r : 0);
            if (comp.PageCount == 0) throw new InvalidOperationException("Semua halaman telah dihapus");
            comp.Save(outPath);
        }

        // ------------------------------------------------------------------ PDF ke Word

        /// <summary>
        /// PDF → Word bertata letak asli (port PdfToDocx Android): teks, font, tabel, foto &amp; bentuk dibaca langsung dari isi PDF.
        /// Halaman hasil pindai (tanpa teks) di-OCR offline Windows; bila tak terbaca yakin, halaman disertakan sebagai gambar.
        /// Bila PDF dibuka dengan password, Word hasilnya dikunci "hanya-baca" dengan password yang sama (DocxProtect).
        /// Mengembalikan true bila hasil dikunci. Bila struktur PDF tak terbaca sama sekali, jatuh ke jalur OCR penuh.
        /// </summary>
        public static bool PdfToWord(string src, string outPath, Action<int, int>? progress = null)
        {
            string? pw = PdfRenderer.GetCachedPassword(src);
            PdfFile? f = null;
            try
            {
                f = OpenPdf(src);
                if (f.Encrypted && string.IsNullOrEmpty(pw) && !f.CanExtract)
                    throw new PdfPasswordException("PDF ini dibatasi pemiliknya (tidak boleh disalin/diubah). Masukkan password pemilik untuk mengubahnya ke Word.");
            }
            catch (PdfException) { f = null; /* struktur rusak: pakai perender Windows + OCR */ }
            bool converted = false;
            if (f != null)
            {
                try
                {
                    new PdfToDocx(outPath).Convert(f, new WinScan(src), progress);
                    converted = true;
                }
                catch (OutOfMemoryException) { throw; }
                catch (Exception) { try { File.Delete(outPath); } catch { } }
            }
            if (!converted) PdfToWordOcr(src, outPath, progress);
            if (!string.IsNullOrEmpty(pw)) { DocxProtect.Apply(outPath, pw!); return true; }
            return false;
        }

        /// <summary>OCR / gambar halaman pindaian untuk <see cref="PdfToDocx"/> (render satu halaman saja, hemat memori).</summary>
        private sealed class WinScan : PdfToDocx.IScan
        {
            private readonly string _src;
            private int _lastIdx = -1; private Bitmap? _last;
            public WinScan(string src) { _src = src; }
            private Bitmap? Page(int i)
            {
                if (_lastIdx == i && _last != null) return _last;
                _last?.Dispose(); _last = null; _lastIdx = i;
                var l = PdfEngine.RenderPages(_src, 2200, i, 1);
                _last = l.Count > 0 ? l[0] : null;
                for (int k = 1; k < l.Count; k++) l[k].Dispose();
                return _last;
            }
            public List<string>? Ocr(int i)
            {
                var b = Page(i); if (b == null) return null;
                var paras = PdfEngine.RunOcr(b).Paras.Where(p => !string.IsNullOrWhiteSpace(p)).Select(AiText.Clean).ToList();
                return paras.Sum(p => p.Length) < 40 ? null : paras;
            }
            public (byte[] Jpeg, int W, int H)? PageImage(int i)
            {
                var b = Page(i); if (b == null) return null;
                using var small = Scale(b, 1400);
                var r = (PdfEngine.EncodeJpeg(small, 82), small.Width, small.Height);
                _last?.Dispose(); _last = null; _lastIdx = -1;
                return r;
            }
        }

        /// <summary>Jalur cadangan: OCR offline per halaman (untuk PDF yang strukturnya tak terbaca).</summary>
        private static void PdfToWordOcr(string src, string outPath, Action<int, int>? progress)
        {
            int done;
            using (var d = new DocxWriter(outPath))
            {
                done = PdfEngine.ForEachPage(src, 2200, (i, total, page) =>
                {
                    progress?.Invoke(i + 1, total);
                    d.BeginPage();
                    var ocr = PdfEngine.RunOcr(page);
                    var paras = ocr.Paras.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                    if (paras.Sum(p => p.Length) < 40)
                    {
                        using var small = Scale(page, 1400);
                        d.Image(PdfEngine.EncodeJpeg(small, 82), small.Width, small.Height);
                    }
                    foreach (var p in paras) d.Paragraph(AiText.Clean(p));
                });
                if (done > 0) d.Finish();
            }
            if (done == 0)
            {
                try { File.Delete(outPath); } catch { }
                throw new InvalidOperationException("Halaman PDF tidak bisa dibaca.");
            }
        }

        private static Bitmap Scale(Bitmap b, int maxDim)
        {
            float f = Math.Min(1f, maxDim / (float)Math.Max(b.Width, b.Height));
            return new Bitmap(b, Math.Max(1, (int)(b.Width * f)), Math.Max(1, (int)(b.Height * f)));
        }

        // ------------------------------------------------------------------ teks halaman (untuk cari / ringkas)

        /// <summary>Teks per halaman lewat OCR Windows (maks. <paramref name="maxPages"/> halaman).</summary>
        public static List<string> OcrPages(string pdfPath, int maxPages, Action<int, int>? progress = null)
        {
            // Hanya halaman yang dibutuhkan yang dirender (dulu: SEMUA halaman dirender lalu sebagian besar dibuang).
            var outL = new List<string>();
            PdfEngine.ForEachPage(pdfPath, 1800, (i, n, page) =>
            {
                progress?.Invoke(i + 1, n);
                outL.Add(PdfEngine.RunOcr(page).Text);
            }, maxPages);
            return outL;
        }
    }
}
