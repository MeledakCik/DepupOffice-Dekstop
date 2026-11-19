using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms;

public partial class MainForm
{
    private void ShowWordDefaults()
    {
        var config = AppStorage.Config;
        using var dialog = new Dlg("Format Word", "Berlaku untuk dokumen baru.", 450);
        string[] sizes = ["A4", "Letter", "F4"];
        float[] spacing = [1f, 1.15f, 1.5f, 2f];
        var size = dialog.Choice("Ukuran kertas", sizes, Array.IndexOf(sizes, config.DocPageSize));
        var margin = dialog.Choice("Margin", ["Normal", "Sempit", "Lebar"], config.DocMarginMode);
        var line = dialog.Choice("Spasi baris", ["1,0", "1,15", "1,5", "2,0"], Array.IndexOf(spacing, config.DocLineSpacing));
        var font = dialog.Choice("Jenis huruf", ["Sans serif", "Times New Roman"], config.DocFontFamily);
        dialog.Action("Simpan", true, () =>
        {
            config.DocPageSize = sizes[size.SelectedIndex];
            config.DocMarginMode = margin.SelectedIndex;
            config.DocLineSpacing = spacing[line.SelectedIndex];
            config.DocFontFamily = font.SelectedIndex;
            AppStorage.SaveConfig();
            dialog.Close();
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
    }

    private void ShowSheetDefaults()
    {
        var config = AppStorage.Config;
        using var dialog = new Dlg("Format spreadsheet", "Berlaku ketika editor spreadsheet dibuka.", 450);
        string[] currencies = ["Rp", "$", "€"];
        string[] separators = [",", ";", "\t"];
        var grid = dialog.Check("Tampilkan garis kisi", config.SheetShowGridlines);
        var sum = dialog.Check("Tampilkan tombol Jumlah Otomatis", config.SheetAutoSum);
        var currency = dialog.Choice("Simbol mata uang", currencies, Array.IndexOf(currencies, config.SheetCurrencySymbol));
        var separator = dialog.Choice("Pemisah CSV saat mengekspor", ["Koma", "Titik koma", "Tab"], Array.IndexOf(separators, config.SheetCsvSeparator));
        dialog.Action("Simpan", true, () =>
        {
            config.SheetShowGridlines = grid.Checked;
            config.SheetAutoSum = sum.Checked;
            config.SheetCurrencySymbol = currencies[currency.SelectedIndex];
            config.SheetCsvSeparator = separators[separator.SelectedIndex];
            AppStorage.SaveConfig();
            dialog.Close();
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
    }

    private void ShowPdfDefaults()
    {
        var config = AppStorage.Config;
        using var dialog = new Dlg("Format pembaca PDF", "Berlaku ketika dokumen berikutnya dibuka.", 450);
        var zoom = dialog.Number("Zoom awal (%)", 50, 250, (decimal)Math.Round(config.PdfDefaultZoom * 100));
        var paper = dialog.Choice("Warna kertas", Enumerable.Range(0, 4).Select(GetReaderThemeName), config.PdfReaderTheme);
        var smooth = dialog.Check("Gulir halus saat berpindah halaman", config.PdfSmoothScroll);
        dialog.Action("Simpan", true, () =>
        {
            config.PdfDefaultZoom = (float)zoom.Value / 100f;
            config.PdfReaderTheme = paper.SelectedIndex;
            config.PdfSmoothScroll = smooth.Checked;
            AppStorage.SaveConfig();
            dialog.Close();
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
        RenderCurrentTab(preserveScroll: true);
    }
}
