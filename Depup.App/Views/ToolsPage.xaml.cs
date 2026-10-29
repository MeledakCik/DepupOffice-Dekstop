using System.Windows;
using System.Windows.Controls;
using Depup.App.ViewModels;

namespace Depup.App.Views;

public partial class ToolsPage : UserControl
{
    public ToolsPage() => InitializeComponent();

    private void OnPdfDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPdfDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not ToolsPageViewModel viewModel || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths) viewModel.PdfTool.AddInput(path);
    }
}
