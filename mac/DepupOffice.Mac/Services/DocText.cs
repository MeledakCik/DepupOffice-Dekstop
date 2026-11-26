using Avalonia.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOffice.Mac.Services;

/// <summary>Ambil teks polos dari dokumen apa pun (untuk Baca Lantang & salin).</summary>
public static class DocText
{
    /// <summary>Null = dibatalkan (mis. password PDF tidak diisi).</summary>
    public static async Task<string?> ExtractAsync(Window owner, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".pdf":
                    using (var doc = await PdfService.OpenInteractive(owner, path, 1.0))
                    {
                        if (doc == null) return null;
                        return await Task.Run(() => doc.AllText());
                    }
                case ".docx":
                    return await Task.Run(() => OfficeReader.ReadDocxText(path));
                case ".xlsx":
                case ".csv":
                    return await Task.Run(() =>
                    {
                        var model = SheetModel.FromFile(path);
                        return string.Join("\n", model.ToDisplayRows().Select(r => string.Join(", ", r.Where(c => !string.IsNullOrWhiteSpace(c)))));
                    });
                case ".txt":
                case ".md":
                    return await File.ReadAllTextAsync(path);
                default:
                    if (VisionService.IsReady)
                        return await Task.Run(() => VisionService.LinesToText(VisionService.Ocr(path)));
                    return "";
            }
        }
        catch (Exception ex)
        {
            await Dialogs.Error(owner, "Gagal membaca dokumen: " + ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Cetak di macOS: dokumen disiapkan sebagai PDF lalu dibuka di Preview (⌘P untuk dialog cetak sistem).
/// Pengganti PrintHelper (PrintDialog Windows).
/// </summary>
public static class PrintService
{
    private static string TempPdf(string baseName)
    {
        string dir = Path.Combine(Path.GetTempPath(), "depup_print");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{AppStorage.BaseName(baseName)}_{DateTime.Now:HHmmss}.pdf");
    }

    public static async Task PrintFile(Window owner, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            string target = path;
            switch (ext)
            {
                case ".pdf":
                case ".jpg":
                case ".jpeg":
                case ".png":
                case ".heic":
                case ".bmp":
                case ".webp":
                    break;
                case ".docx":
                    target = TempPdf(Path.GetFileName(path));
                    await Dialogs.Busy(owner, "Menyiapkan dokumen untuk dicetak…", _ =>
                    {
                        var model = DocModel.FromDocx(path);
                        model.Title = "";
                        TextPdfRenderer.DocToPdf(model, target);
                    });
                    break;
                case ".xlsx":
                case ".csv":
                    target = TempPdf(Path.GetFileName(path));
                    await Dialogs.Busy(owner, "Menyiapkan tabel untuk dicetak…", _ =>
                    {
                        var model = SheetModel.FromFile(path);
                        TextPdfRenderer.TableToPdf(model.Title, model.ToDisplayRows(), model.BoldRows(), target, AppStorage.Config.SheetShowGridlines);
                    });
                    break;
                default:
                    target = TempPdf(Path.GetFileName(path));
                    string text = await File.ReadAllTextAsync(path);
                    await Dialogs.Busy(owner, "Menyiapkan teks untuk dicetak…", _ => TextPdfRenderer.TextToPdf(Path.GetFileName(path), text, target));
                    break;
            }
            await OpenPreviewForPrint(owner, target);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(owner, "Gagal menyiapkan cetak: " + ex.Message);
        }
    }

    public static async Task PrintDoc(Window owner, DocModel model)
    {
        string target = TempPdf(string.IsNullOrWhiteSpace(model.Title) ? "Dokumen" : model.Title);
        try
        {
            await Dialogs.Busy(owner, "Menyiapkan dokumen untuk dicetak…", _ => TextPdfRenderer.DocToPdf(model, target));
            await OpenPreviewForPrint(owner, target);
        }
        catch (Exception ex) { await Dialogs.Error(owner, "Gagal menyiapkan cetak: " + ex.Message); }
    }

    public static async Task PrintSheet(Window owner, SheetModel model, bool gridlines)
    {
        string target = TempPdf(model.Title);
        try
        {
            await Dialogs.Busy(owner, "Menyiapkan tabel untuk dicetak…", _ => TextPdfRenderer.TableToPdf(model.Title, model.ToDisplayRows(), model.BoldRows(), target, gridlines));
            await OpenPreviewForPrint(owner, target);
        }
        catch (Exception ex) { await Dialogs.Error(owner, "Gagal menyiapkan cetak: " + ex.Message); }
    }

    private static bool _hintShown;

    private static async Task OpenPreviewForPrint(Window owner, string path)
    {
        MacSystem.OpenWithPreview(path);
        if (!_hintShown)
        {
            _hintShown = true;
            await Dialogs.Info(owner, "Siap dicetak", "Dokumen dibuka di Preview. Tekan ⌘P di Preview untuk memilih printer, jumlah salinan, dan mencetak.");
        }
    }
}
