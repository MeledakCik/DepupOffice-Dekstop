using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Depup.App.Services;
using Depup.Core.Interfaces;

namespace Depup.App.ViewModels;

public partial class ScanPageViewModel : ObservableObject
{
    private readonly IScanExportService _exportService;
    private readonly IDocumentHistoryService _historyService;
    private readonly DocumentPickerService _pickerService;
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<string> Pages { get; } = [];
    [ObservableProperty] private string outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DepupScan.pdf");
    [ObservableProperty] private string statusMessage = "Tambahkan foto atau gambar hasil pemindaian.";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? selectedPage;

    public ScanPageViewModel(IScanExportService exportService, IDocumentHistoryService historyService, DocumentPickerService pickerService)
    {
        _exportService = exportService;
        _historyService = historyService;
        _pickerService = pickerService;
    }

    [RelayCommand]
    private void AddImages()
    {
        var paths = _pickerService.PickImages();
        foreach (var path in paths) AddImage(path);
    }

    public void AddImage(string path)
    {
        if (!File.Exists(path) || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = "Pilih gambar PNG, JPG, BMP, atau TIFF yang valid.";
            return;
        }
        Pages.Add(Path.GetFullPath(path));
        SelectedPage = Pages.LastOrDefault();
    }

    [RelayCommand]
    private void RemovePage(string? path)
    {
        if (path is not null) Pages.Remove(path);
        SelectedPage = Pages.FirstOrDefault();
    }

    [RelayCommand]
    private void MovePageUp(string? path) => Move(path, -1);

    [RelayCommand]
    private void MovePageDown(string? path) => Move(path, 1);

    private void Move(string? path, int delta)
    {
        if (path is null) return;
        var index = Pages.IndexOf(path);
        var newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= Pages.Count) return;
        Pages.Move(index, newIndex);
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Progress = 0;
        _cancellation = new CancellationTokenSource();
        try
        {
            await _exportService.ExportImagesToPdfAsync(Pages.ToArray(), OutputPath, new Progress<double>(value => Progress = value), _cancellation.Token);
            await _historyService.AddOrUpdateAsync(OutputPath);
            StatusMessage = $"PDF scan disimpan: {OutputPath}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Ekspor dibatalkan.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"Ekspor gagal: {exception.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }
}
