using Microsoft.Win32;

namespace Depup.App.Services;

public sealed class DocumentPickerService
{
    public string? PickDocument()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Buka dokumen",
            Filter = "Dokumen yang didukung|*.pdf;*.docx;*.xlsx;*.xlsm;*.csv|PDF|*.pdf|Word|*.docx|Excel|*.xlsx;*.xlsm|CSV|*.csv|Semua berkas|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickImages()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pilih gambar halaman scan",
            Filter = "Gambar|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff",
            CheckFileExists = true,
            Multiselect = true
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
