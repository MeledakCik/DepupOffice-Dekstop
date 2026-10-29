using System.Data;
using System.IO;
using System.Windows;
using Depup.App.ViewModels;

namespace Depup.App.Views;

public partial class DocumentViewer : Window
{
    private readonly DocumentViewerViewModel _viewModel;

    public DocumentViewer(DocumentViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    public async Task OpenAsync(string path)
    {
        await _viewModel.OpenAsync(path);
        DocumentTitle.Text = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".pdf")
        {
            PdfView.Visibility = Visibility.Visible;
            await PdfView.EnsureCoreWebView2Async();
            PdfView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PdfView.Source = new Uri(path);
            return;
        }

        if (_viewModel.ErrorMessage is not null)
        {
            ErrorText.Text = _viewModel.ErrorMessage;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (_viewModel.Content is null) return;

        if (_viewModel.Content.Kind is "XLSX" or "CSV")
        {
            SheetTabs.ItemsSource = _viewModel.Content.Sheets;
            SheetTabs.Visibility = _viewModel.Content.Sheets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SheetTabs.SelectedIndex = 0;
            TableView.ItemsSource = ToDataTable(_viewModel.Content.SheetRows[0]).DefaultView;
            TableView.Visibility = Visibility.Visible;
        }
        else
        {
            TextView.Text = string.Join(Environment.NewLine + Environment.NewLine,
                _viewModel.Content.SheetRows.SelectMany(sheet => sheet).Select(row => string.Join(" ", row)));
            TextView.Visibility = Visibility.Visible;
        }
    }

    private void OnSheetSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel.Content is null || SheetTabs.SelectedIndex < 0 || SheetTabs.SelectedIndex >= _viewModel.Content.SheetRows.Count) return;
        TableView.ItemsSource = ToDataTable(_viewModel.Content.SheetRows[SheetTabs.SelectedIndex]).DefaultView;
    }

    private static DataTable ToDataTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new DataTable();
        var columnCount = rows.Count == 0 ? 1 : rows.Max(row => row.Count);
        for (var index = 0; index < columnCount; index++) table.Columns.Add($"Kolom {index + 1}");
        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            for (var index = 0; index < row.Count; index++) dataRow[index] = row[index];
            table.Rows.Add(dataRow);
        }
        return table;
    }
}
