using System.Windows;
using System.Windows.Input;
using Depup.App.ViewModels;

namespace Depup.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    public MainWindowViewModel ViewModel => _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            GlobalSearch.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _viewModel.ToggleNavigationCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _ = _viewModel.OpenDocumentAsync(files[0], App.Services);
            if (files.Length > 1) _viewModel.StatusMessage = "Berkas pertama dibuka; buka berkas lain satu per satu";
        }
    }
}
