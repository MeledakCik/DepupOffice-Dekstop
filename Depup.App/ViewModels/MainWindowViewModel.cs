using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Depup.App.Services;
using Depup.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Depup.App.Views;
using System.IO;

namespace Depup.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    [ObservableProperty] private object currentPage = null!;
    [ObservableProperty] private bool isNavigationCollapsed;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string statusMessage = "Siap bekerja offline";

    public ReadPageViewModel ReadPage { get; }
    public ToolsPageViewModel ToolsPage { get; }
    public CreatePageViewModel CreatePage { get; }
    public SavePageViewModel SavePage { get; } = new();

    public MainWindowViewModel(INavigationService navigationService, IDocumentHistoryService historyService, DocumentPickerService pickerService, PdfToolViewModel pdfToolViewModel, ScanPageViewModel scanPageViewModel, IServiceProvider serviceProvider)
    {
        _navigationService = navigationService;
        ReadPage = new ReadPageViewModel(historyService, pickerService, path => _ = OpenDocumentAsync(path, serviceProvider));
        ToolsPage = new ToolsPageViewModel(pdfToolViewModel);
        CreatePage = new CreatePageViewModel(scanPageViewModel);
        _navigationService.Register("Baca", ReadPage);
        _navigationService.Register("Tools", ToolsPage);
        _navigationService.Register("Buat", CreatePage);
        _navigationService.Register("Simpan", SavePage);
        _navigationService.Navigate("Baca");
        CurrentPage = _navigationService.CurrentPage;
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        _navigationService.Navigate(destination);
        CurrentPage = _navigationService.CurrentPage;
        StatusMessage = $"Halaman {((PageViewModel)CurrentPage).Title} aktif";
    }

    [RelayCommand]
    private void ToggleNavigation() => IsNavigationCollapsed = !IsNavigationCollapsed;

    [RelayCommand]
    private void QuickScan() => StatusMessage = "Fitur Scan Cepat akan tersedia pada tahap berikutnya";

    public async Task HandleOpenRequestAsync(string path)
    {
        try
        {
            await ReadPage.RecordOpenedAsync(path);
            StatusMessage = $"Dibuka: {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Tidak dapat mencatat berkas: {exception.Message}";
        }
    }

    public async Task OpenDocumentAsync(string path, IServiceProvider serviceProvider)
    {
        if (!File.Exists(path))
        {
            StatusMessage = "Berkas tidak ditemukan";
            return;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".pdf" or ".docx" or ".xlsx" or ".xlsm" or ".csv"))
        {
            StatusMessage = "Format berkas ini belum didukung";
            return;
        }

        try
        {
            await ReadPage.RecordOpenedAsync(path);
            var viewer = serviceProvider.GetRequiredService<DocumentViewer>();
            await viewer.OpenAsync(path);
            viewer.Show();
            StatusMessage = $"Dibuka: {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"Tidak dapat membuka berkas: {exception.Message}";
        }
    }
}
