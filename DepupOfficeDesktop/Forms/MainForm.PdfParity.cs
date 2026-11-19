using System.Diagnostics;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms;

public partial class MainForm
{
    private void SearchablePdfFlow()
    {
        string? path = Ui.PickFile(this, "Pilih PDF hasil pindai", Ui.FilterPdf);
        if (path == null || !PdfEngine.EnsureUnlocked(this, path)) return;
        string output = AppStorage.NewOut(AppStorage.BaseName(path) + "_OCR", "pdf");
        var (ok, result) = Ui.Busy(this, "Mengenali teks pada halaman hasil pindai…",
            progress => PdfToolsX.AddSearchableText(path, output, progress));
        if (!ok) return;
        if (result.Added == 0)
        {
            Theme.Toast(this, result.Scanned == 0 ? "Semua halaman sudah memiliki teks." : "OCR tidak menemukan teks baru.");
            return;
        }
        RegisterPdfResult(output, "PDF bisa dicari", $"{result.Added} dari {result.Scanned} halaman hasil pindai mendapat lapisan teks OCR.");
    }

    private void RemoveBlankPagesFlow()
    {
        string? path = Ui.PickFile(this, "Pilih PDF untuk memeriksa halaman kosong", Ui.FilterPdf);
        if (path == null || !PdfEngine.EnsureUnlocked(this, path)) return;
        var (ok, detected) = Ui.Busy(this, "Memeriksa halaman kosong…", progress => PdfToolsX.FindBlankPages(path, progress));
        if (!ok) return;
        if (detected.Blank.Length == 0) { Theme.Toast(this, "Tidak ditemukan halaman kosong."); return; }
        if (detected.Blank.Length == detected.Total) { Theme.Toast(this, "Semua halaman terdeteksi kosong."); return; }

        using var dialog = new Dlg("Tinjau halaman kosong", "Centang halaman yang ingin dikeluarkan dari salinan PDF.", 500);
        var list = new CheckedListBox
        {
            Height = S(180), CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.FIELD, ForeColor = Theme.TEXT, AccessibleName = "Halaman yang akan dikeluarkan"
        };
        foreach (int index in detected.Blank) list.Items.Add($"Halaman {index + 1}", true);
        dialog.Add(list);
        string? saved = null;
        int removed = 0;
        dialog.Action("Simpan salinan PDF", true, () =>
        {
            var exclude = list.CheckedIndices.Cast<int>().Select(index => detected.Blank[index]).ToHashSet();
            if (exclude.Count == 0) { Theme.Toast(dialog, "Pilih setidaknya satu halaman."); return; }
            int[] keep = Enumerable.Range(0, detected.Total).Where(index => !exclude.Contains(index)).ToArray();
            string output = AppStorage.NewOut(AppStorage.BaseName(path) + "_ringkas", "pdf");
            if (!Ui.Busy(dialog, "Menyusun salinan PDF…",
                    _ => PdfToolsX.OrganizeLossless(path, keep, new Dictionary<int, int>(), output)))
                return;
            saved = output;
            removed = exclude.Count;
            dialog.Close();
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
        if (saved != null) RegisterPdfResult(saved, "Halaman kosong dihapus", $"{removed} halaman dikeluarkan dari salinan PDF.");
    }

    private void PdfToJpgFlow()
    {
        string? path = Ui.PickFile(this, "Pilih PDF untuk diekspor ke JPG", Ui.FilterPdf);
        if (path == null || !PdfEngine.EnsureUnlocked(this, path)) return;
        var (ok, count) = Ui.Busy(this, "Menyimpan setiap halaman sebagai JPG…",
            progress => PdfEngine.ToJpg(path, AppStorage.BaseName(path), progress));
        if (ok) ShowExportedImages(count, "halaman JPG");
    }

    private void ShowExportedImages(int count, string description)
    {
        if (count == 0) { Theme.Toast(this, "Tidak ada gambar yang dapat disimpan."); return; }
        using var dialog = new Dlg("Ekspor gambar", $"{count} {description} tersimpan di folder Gambar.", 470);
        dialog.Action("Buka folder Gambar", true, () =>
            Process.Start(new ProcessStartInfo(AppStorage.PicturesDir) { UseShellExecute = true }));
        dialog.Action("Tutup", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
        RenderCurrentTab(preserveScroll: true);
    }

    private void RegisterPdfResult(string output, string operation, string details)
    {
        AppStorage.AddRecent(output);
        AppStorage.SetLastJob(operation, output);
        ShowOperationResultDialog(operation, output, details);
        RenderCurrentTab(preserveScroll: true);
    }

    private void OfficeToPdfFlow()
    {
        string? path = Ui.PickFile(this, "Pilih Word, Excel, atau PowerPoint",
            "Dokumen Office (*.docx;*.xlsx;*.pptx)|*.docx;*.xlsx;*.pptx");
        if (path == null) return;
        if (Path.GetExtension(path).ToLowerInvariant() is not (".docx" or ".xlsx" or ".pptx"))
        {
            Theme.Toast(this, "Pilih berkas .docx, .xlsx, atau .pptx.");
            return;
        }
        OpenDocument(path);
    }

    private void SaveDocumentCopyFlow()
    {
        using var picker = new OpenFileDialog { Title = "Pilih berkas untuk disalin", Filter = AppStorage.DocumentPickerFilter };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string path = picker.FileName;
        var (ok, destination) = Ui.Busy(this, "Menyalin berkas…", _ => AppStorage.SaveToDownloads(path));
        if (ok && destination != null) Theme.Toast(this, $"Salinan tersimpan di {destination}");
    }
}
