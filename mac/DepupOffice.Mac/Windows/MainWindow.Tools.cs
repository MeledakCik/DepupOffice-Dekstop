using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;

namespace DepupOffice.Mac.Windows;

/// <summary>Aksi 12 alat PDF & dokumen (port dari MainForm + PdfEngine Windows).</summary>
public sealed partial class MainWindow
{
    private static readonly (string Name, string[] Ext)[] OcrFilter =
    {
        ("Gambar atau PDF", new[] { "pdf", "jpg", "jpeg", "png", "heic", "bmp", "webp", "tif", "tiff" }),
    };

    private async Task<PdfDoc?> PickPdf(string title, double scale = PdfDoc.ToolScale)
    {
        string? path = await Dialogs.PickFile(this, title, Dialogs.PdfFilter);
        if (path == null) return null;
        return await PdfService.OpenInteractive(this, path, scale);
    }

    private async Task<bool> Run(string message, Action<Action<string>> work)
    {
        try
        {
            await Dialogs.Busy(this, message, work);
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, ex.Message);
            return false;
        }
    }

    private async Task Finish(string title, string outPath, string message)
    {
        if (File.Exists(outPath)) AppStorage.AddRecent(outPath);
        AppStorage.SetLastJob(title, outPath);
        RefreshPage();
        await Dialogs.Done(this, title, message, outPath);
    }

    private static string UniqueDir(string dir)
        => Directory.Exists(dir) ? $"{dir}_{DateTime.Now:yyyyMMdd_HHmmss}" : dir;

    // ---------- 1. Gabung ----------
    private async Task ToolMerge()
    {
        var files = await Dialogs.PickFiles(this, "Pilih PDF yang akan digabung (minimal 2)", true, Dialogs.PdfFilter);
        if (files.Count == 0) return;
        var ordered = await Dialogs.Reorder(this, "Urutan penggabungan",
            "Atur urutan berkas. Halaman disusun sesuai urutan ini.", files, 2);
        if (ordered == null) return;

        var docs = new List<PdfDoc>();
        try
        {
            foreach (var f in ordered)
            {
                var d = await PdfService.OpenInteractive(this, f, PdfDoc.ToolScale);
                if (d == null) return;
                docs.Add(d);
            }
            var pages = docs.SelectMany(d => PdfTools.AllPages(d)).ToList();
            string outPath = AppStorage.NamedOut("Gabungan_" + AppStorage.BaseName(Path.GetFileName(ordered[0])), "pdf");
            if (!await Run("Menggabungkan PDF…", r => PdfTools.Build(pages, outPath, null, 85, 0, r))) return;
            await Finish("PDF berhasil digabung", outPath, $"{docs.Count} berkas • {pages.Count} halaman.");
        }
        finally
        {
            foreach (var d in docs) d.Dispose();
        }
    }

    // ---------- 2. Pisah ----------
    private async Task ToolSplit()
    {
        var doc = await PickPdf("Pilih PDF yang akan dipisah");
        if (doc == null) return;
        using (doc)
        {
            string baseName = AppStorage.BaseName(Path.GetFileName(doc.FilePath));
            int choice = await Dialogs.Choose(this, "Pisah PDF", $"{Path.GetFileName(doc.FilePath)} • {doc.PageCount} halaman",
                "Setiap halaman jadi PDF terpisah", "Ambil halaman tertentu (mis. 1-3, 5)");
            if (choice < 0) return;

            if (choice == 0)
            {
                string dir = UniqueDir(Path.Combine(AppStorage.OutDir, $"{baseName}_pisah"));
                bool ok = await Run("Memisahkan PDF…", r =>
                {
                    Directory.CreateDirectory(dir);
                    for (int i = 0; i < doc.PageCount; i++)
                    {
                        r($"Halaman {i + 1} dari {doc.PageCount}…");
                        PdfTools.Build(new[] { new PageRef(doc, i) }, Path.Combine(dir, $"{baseName}_hal{i + 1:000}.pdf"), null, 85, 0, _ => { });
                    }
                });
                if (ok) await Finish("PDF berhasil dipisah", dir, $"{doc.PageCount} berkas PDF dibuat di folder ini.");
                return;
            }

            string? spec = await Dialogs.Prompt(this, "Ambil halaman", $"Dokumen berisi {doc.PageCount} halaman. Tulis halaman yang diambil:",
                PageRange.All(doc.PageCount), watermark: "contoh: 1-3, 5, 8");
            if (spec == null) return;
            List<int> indices;
            try { indices = PageRange.Parse(spec, doc.PageCount); }
            catch (FormatException ex) { await Dialogs.Error(this, ex.Message); return; }

            string outPath = AppStorage.NamedOut($"{baseName}_hal_{SafeSpec(spec)}", "pdf");
            var pages = indices.Select(i => new PageRef(doc, i)).ToList();
            if (await Run("Menyimpan halaman…", r => PdfTools.Build(pages, outPath, null, 85, 0, r)))
                await Finish("Halaman berhasil diambil", outPath, $"{pages.Count} halaman disimpan.");
        }
    }

    private static string SafeSpec(string spec)
    {
        string s = new string(spec.Where(c => char.IsDigit(c) || c is '-' or ',').ToArray()).Replace(',', '_');
        return s.Length > 30 ? s[..30] : s;
    }

    // ---------- 3. Kompres ----------
    private async Task ToolCompress()
    {
        var doc = await PickPdf("Pilih PDF yang akan dikompres");
        if (doc == null) return;
        using (doc)
        {
            long before = new FileInfo(doc.FilePath).Length;
            int c = await Dialogs.Choose(this, "Kompres PDF",
                $"Ukuran sekarang {AppStorage.FmtSize(before)} • {doc.PageCount} halaman",
                "Target ≤ 1 MB (SSCASN / CPNS)", "Target ≤ 500 KB", "Target ≤ 200 KB",
                "Kualitas tinggi", "Seimbang", "Sekecil mungkin");
            if (c < 0) return;
            (long target, int level) = c switch
            {
                0 => (1024L * 1024, 0),
                1 => (500L * 1024, 0),
                2 => (200L * 1024, 0),
                3 => (0L, 1),
                4 => (0L, 3),
                _ => (0L, 6),
            };
            string outPath = AppStorage.NamedOut($"{AppStorage.BaseName(Path.GetFileName(doc.FilePath))}_kompres", "pdf");
            long after = 0;
            if (!await Run("Mengompres PDF…", r => { after = PdfTools.Compress(doc, outPath, target, level, r); })) return;

            double pct = before > 0 ? (1 - (double)after / before) * 100 : 0;
            string msg = $"{AppStorage.FmtSize(before)} → {AppStorage.FmtSize(after)}" + (pct > 0 ? $" ({pct:0}% lebih kecil)." : ".");
            if (target > 0 && after > target)
                msg += $"\n\nTarget {AppStorage.FmtSize(target)} belum tercapai walau sudah memakai kualitas terendah. Coba pisahkan PDF menjadi beberapa bagian.";
            if (after >= before)
                msg += "\n\nBerkas asli sudah sangat ringkas; hasil kompres tidak lebih kecil. Sebaiknya pakai berkas asli.";
            await Finish("Kompres selesai", outPath, msg);
        }
    }

    // ---------- 4. Gambar → PDF ----------
    private async Task ToolImagesToPdf()
    {
        var files = await Dialogs.PickFiles(this, "Pilih gambar (bisa lebih dari satu)", true, Dialogs.ImageFilter);
        if (files.Count == 0) return;
        var ordered = files.Count > 1
            ? await Dialogs.Reorder(this, "Urutan halaman", "Setiap gambar menjadi satu halaman.", files)
            : files;
        if (ordered == null || ordered.Count == 0) return;
        int c = await Dialogs.Choose(this, "Ukuran halaman", "", "A4 (gambar dimuat di tengah)", "Sesuai ukuran gambar");
        if (c < 0) return;
        string name = ordered.Count == 1 ? AppStorage.BaseName(Path.GetFileName(ordered[0])) : "Gambar_ke_PDF";
        string outPath = AppStorage.NamedOut(name, "pdf");
        if (await Run("Menyusun PDF…", r => PdfTools.ImagesToPdf(ordered, outPath, c == 0, r)))
            await Finish("PDF berhasil dibuat", outPath, $"{ordered.Count} gambar disusun menjadi PDF.");
    }

    // ---------- 5. PDF → JPG ----------
    private async Task ToolPdfToJpg()
    {
        var doc = await PickPdf("Pilih PDF yang akan diekspor ke JPG");
        if (doc == null) return;
        using (doc)
        {
            string dir = UniqueDir(Path.Combine(AppStorage.PicturesDir, AppStorage.BaseName(Path.GetFileName(doc.FilePath))));
            List<string> outputs = new();
            if (await Run("Mengekspor halaman…", r => { outputs = PdfTools.PdfToJpg(doc, dir, r); }))
                await Finish("Ekspor JPG selesai", dir, $"{outputs.Count} gambar JPG (150 dpi) dibuat.");
        }
    }

    // ---------- 6. Kunci ----------
    private async Task ToolProtect()
    {
        var doc = await PickPdf("Pilih PDF yang akan dikunci");
        if (doc == null) return;
        using (doc)
        {
            string? pw = await Dialogs.Prompt(this, "Buat password", "Password ini dibutuhkan untuk membuka PDF.", password: true, watermark: "Password baru");
            if (string.IsNullOrEmpty(pw)) return;
            string? pw2 = await Dialogs.Prompt(this, "Ulangi password", "Ketik ulang password untuk memastikan.", password: true, watermark: "Ulangi password");
            if (pw2 == null) return;
            if (pw != pw2) { await Dialogs.Error(this, "Password tidak sama. Silakan ulangi."); return; }
            string outPath = AppStorage.NamedOut($"{AppStorage.BaseName(Path.GetFileName(doc.FilePath))}_terkunci", "pdf");
            if (await Run("Mengunci PDF…", r => PdfTools.Build(PdfTools.AllPages(doc), outPath, pw, 85, 0, r)))
                await Finish("PDF berhasil dikunci", outPath, "PDF dienkripsi AES-128. Simpan password Anda baik-baik — tanpa password berkas tidak bisa dibuka.");
        }
    }

    // ---------- 7. Buka kunci ----------
    private async Task ToolUnlock()
    {
        var doc = await PickPdf("Pilih PDF ber-password");
        if (doc == null) return;
        using (doc)
        {
            if (string.IsNullOrEmpty(doc.Password))
            {
                await Dialogs.Info(this, "Tidak terkunci", "PDF ini tidak memakai password untuk dibuka.");
                return;
            }
            string outPath = AppStorage.NamedOut($"{AppStorage.BaseName(Path.GetFileName(doc.FilePath))}_terbuka", "pdf");
            if (await Run("Membuka kunci PDF…", r => PdfTools.Build(PdfTools.AllPages(doc), outPath, null, 85, 0, r)))
                await Finish("Kunci PDF dibuka", outPath, "Salinan PDF tanpa password sudah dibuat. Berkas asli tidak diubah.");
        }
    }

    // ---------- 8. Putar ----------
    private async Task ToolRotate()
    {
        var doc = await PickPdf("Pilih PDF yang akan diputar");
        if (doc == null) return;
        using (doc)
        {
            int c = await Dialogs.Choose(this, "Putar halaman", $"{doc.PageCount} halaman", "90° searah jarum jam", "180°", "90° berlawanan jarum jam");
            if (c < 0) return;
            int deg = c == 0 ? 90 : c == 1 ? 180 : 270;
            string outPath = AppStorage.NamedOut($"{AppStorage.BaseName(Path.GetFileName(doc.FilePath))}_putar", "pdf");
            if (await Run("Memutar halaman…", r => PdfTools.Build(PdfTools.AllPages(doc), outPath, null, 85, deg, r)))
                await Finish("PDF berhasil diputar", outPath, $"Semua halaman diputar {(c == 2 ? "90° berlawanan jarum jam" : c == 1 ? "180°" : "90° searah jarum jam")}.");
        }
    }

    // ---------- 9. Susun / hapus halaman ----------
    private async Task ToolReorder()
    {
        var doc = await PickPdf("Pilih PDF yang akan disusun ulang");
        if (doc == null) return;
        using (doc)
        {
            string? spec = await Dialogs.Prompt(this, "Susun / hapus halaman",
                $"Dokumen berisi {doc.PageCount} halaman. Tulis urutan baru; halaman yang tidak ditulis akan dibuang.\n" +
                "Contoh: 3, 1, 2   •   1-5, 8   •   10-1 (urutan terbalik)",
                PageRange.All(doc.PageCount), watermark: "urutan halaman");
            if (spec == null) return;
            List<int> indices;
            try { indices = PageRange.Parse(spec, doc.PageCount); }
            catch (FormatException ex) { await Dialogs.Error(this, ex.Message); return; }
            string outPath = AppStorage.NamedOut($"{AppStorage.BaseName(Path.GetFileName(doc.FilePath))}_susun", "pdf");
            var pages = indices.Select(i => new PageRef(doc, i)).ToList();
            if (await Run("Menyusun halaman…", r => PdfTools.Build(pages, outPath, null, 85, 0, r)))
                await Finish("Halaman berhasil disusun", outPath, $"{pages.Count} halaman dalam urutan baru.");
        }
    }

    // ---------- 10. OCR ----------
    public async Task<bool> EnsureVision()
    {
        if (VisionService.IsReady) return true;
        string? err;
        try { err = await Dialogs.Busy<string?>(this, "Menyiapkan Apple Vision (hanya sekali, ±30 detik)…", _ => VisionService.EnsureHelper()); }
        catch (Exception ex) { err = ex.Message; }
        if (err != null)
        {
            await Dialogs.Info(this, "OCR belum siap", err);
            return false;
        }
        return true;
    }

    public async Task ToolOcr() => await OcrFile(await Dialogs.PickFile(this, "Pilih foto atau PDF hasil scan", OcrFilter));

    public async Task OcrFile(string? path)
    {
        if (path == null) return;
        if (!await EnsureVision()) return;

        string tmpDir = Path.Combine(Path.GetTempPath(), "depup_ocr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var pages = new List<OcrPageImage>();
        bool isPdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        PdfDoc? doc = null;
        try
        {
            if (isPdf)
            {
                doc = await PdfService.OpenInteractive(this, path, 200.0 / 72.0);
                if (doc == null) return;
            }
            var pdf = doc;
            bool ok = await Run("Mengenali teks…", r =>
            {
                if (pdf != null)
                {
                    int n = Math.Min(pdf.PageCount, 60);
                    for (int i = 0; i < n; i++)
                    {
                        r($"Mengenali teks halaman {i + 1} dari {n}…");
                        using var bmp = pdf.Render(i);
                        string png = Path.Combine(tmpDir, $"p{i:000}.png");
                        File.WriteAllBytes(png, ImageUtil.EncodePng(bmp));
                        pages.Add(new OcrPageImage(png, bmp.Width, bmp.Height, VisionService.Ocr(png), 200f));
                    }
                }
                else
                {
                    r("Mengenali teks pada gambar…");
                    var lines = VisionService.Ocr(path);
                    using var bmp = ImageUtil.Load(path);
                    // Lebar halaman PDF = lebar A4 (595 pt), tinggi mengikuti rasio foto.
                    pages.Add(new OcrPageImage(path, bmp.Width, bmp.Height, lines, bmp.Width * 72f / 595.3f));
                }
            });
            if (!ok) return;

            string text = string.Join("\n\n", pages.Select(p => VisionService.LinesToText(p.Lines)).Where(t => t.Length > 0));
            if (text.Length == 0)
            {
                await Dialogs.Info(this, "Tidak ada teks", "Apple Vision tidak menemukan teks pada berkas ini. Pastikan foto cukup terang dan tidak buram.");
                return;
            }
            string title = AppStorage.BaseName(Path.GetFileName(path));
            var win = new TextResultWindow(this, $"Hasil OCR — {Path.GetFileName(path)}", title, text,
                outPdf => OcrSearchablePdf(pages, outPdf));
            win.Closed += (_, _) => { try { Directory.Delete(tmpDir, true); } catch { } };
            win.Show();
            tmpDir = "";
        }
        finally
        {
            doc?.Dispose();
            if (tmpDir.Length > 0) { try { Directory.Delete(tmpDir, true); } catch { } }
        }
    }

    public sealed record OcrPageImage(string ImagePath, int Width, int Height, List<OcrLine> Lines, float Dpi);

    /// <summary>PDF hasil scan yang bisa dicari: gambar asli + lapisan teks OCR tak terlihat.</summary>
    private static void OcrSearchablePdf(List<OcrPageImage> pages, string outPath)
    {
        var writer = new PdfWriter(outPath, pages.Count);
        try
        {
            foreach (var p in pages)
            {
                using var loaded = ImageUtil.Load(p.ImagePath);
                using var bmp = ImageUtil.Fit(loaded, 2480);
                double k = (double)bmp.Width / Math.Max(1, loaded.Width);
                var lines = p.Lines.Select(l => new OcrLine(l.Text, new DepupOfficeDesktop.Core.Rect(
                    (int)(l.Box.X * k), (int)(l.Box.Y * k), Math.Max(1, (int)(l.Box.Width * k)), Math.Max(1, (int)(l.Box.Height * k))))).ToList();
                float dpi = p.Dpi * (float)k;
                float pw = bmp.Width * 72f / dpi, ph = bmp.Height * 72f / dpi;
                writer.AddPage(ImageUtil.EncodeJpeg(bmp, 85), bmp.Width, bmp.Height, pw, ph, 0, 0, pw, ph,
                    new OcrPage("", new List<string>(), lines, bmp.Width, bmp.Height));
            }
            writer.Finish();
        }
        catch
        {
            writer.Abort();
            throw;
        }
    }

    // ---------- 12. Pindai QR ----------
    private async Task ToolScanQr()
    {
        string? path = await Dialogs.PickFile(this, "Pilih gambar berisi kode QR / barcode", Dialogs.ImageFilter);
        if (path == null) return;
        if (!await EnsureVision()) return;
        List<string> codes = new();
        if (!await Run("Memindai kode…", _ => { codes = VisionService.ScanCodes(path); })) return;
        if (codes.Count == 0)
        {
            await Dialogs.Info(this, "Tidak ada kode", "Tidak ada kode QR atau barcode yang terdeteksi pada gambar ini.");
            return;
        }
        new TextResultWindow(this, "Hasil pindai QR / barcode", "Hasil_QR", string.Join("\n\n", codes), null).Show();
    }

    // ---------- Fitur cepat ----------
    private async Task ReadAloudFromPicker()
    {
        string? path = await Dialogs.PickFile(this, "Pilih dokumen untuk dibacakan", Dialogs.DocumentFilter);
        if (path == null) return;
        string? text = await DocText.ExtractAsync(this, path);
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            bool ocr = await Dialogs.Confirm(this, "Tidak ada teks",
                "Dokumen ini tidak memiliki teks yang bisa dibaca (mungkin hasil scan). Kenali teksnya dengan OCR?", "Jalankan OCR");
            if (ocr) await OcrFile(path);
            return;
        }
        new SpeechWindow(Path.GetFileName(path), text).Show();
    }

    private async Task PrintFromPicker()
    {
        string? path = await Dialogs.PickFile(this, "Pilih dokumen untuk dicetak", Dialogs.DocumentFilter);
        if (path == null) return;
        await PrintService.PrintFile(this, path);
    }
}
