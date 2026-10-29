using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Depup.App.Services;
using Depup.Core.Interfaces;
using Depup.Core.Models;
using System.IO;

namespace Depup.App.ViewModels;

public partial class PdfToolViewModel : ObservableObject
{
    private const long MaximumInputBytes = 50L * 1024 * 1024;
    private readonly IPdfOperationService _operationService;
    private readonly IDocumentHistoryService _historyService;
    private readonly DocumentPickerService _pickerService;
    private CancellationTokenSource? _operationCancellation;

    public ObservableCollection<string> InputFiles { get; } = [];
    public IReadOnlyList<string> Operations { get; } = ["Gabung", "Ekstrak halaman", "Rotasi", "Kompres", "Lindungi"];
    [ObservableProperty] private string selectedOperation = "Gabung";
    [ObservableProperty] private string outputPath = string.Empty;
    [ObservableProperty] private string options = string.Empty;
    [ObservableProperty] private string statusMessage = "Pilih PDF untuk memulai.";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? lastOutputPath;

    public PdfToolViewModel(IPdfOperationService operationService, IDocumentHistoryService historyService, DocumentPickerService pickerService)
    {
        _operationService = operationService;
        _historyService = historyService;
        _pickerService = pickerService;
    }

    [RelayCommand]
    private void AddFile()
    {
        var path = _pickerService.PickDocument();
        if (path is null) return;
        AddInput(path);
    }

    public void AddInput(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || !string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Pilih berkas PDF yang valid.";
            return;
        }
        if (new FileInfo(fullPath).Length > MaximumInputBytes)
        {
            StatusMessage = "Ukuran maksimal tiap PDF adalah 50 MB.";
            return;
        }
        if (!InputFiles.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) InputFiles.Add(fullPath);
        if (string.IsNullOrWhiteSpace(OutputPath)) OutputPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $"{Path.GetFileNameWithoutExtension(fullPath)}_hasil.pdf");
    }

    [RelayCommand]
    private void RemoveFile(string? path)
    {
        if (path is not null) InputFiles.Remove(path);
    }

    [RelayCommand]
    private void Cancel() => _operationCancellation?.Cancel();

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (IsBusy) return;
        if (InputFiles.Count == 0)
        {
            StatusMessage = "Tambahkan setidaknya satu PDF.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        StatusMessage = "Memproses PDF secara lokal...";
        _operationCancellation = new CancellationTokenSource();
        var operation = SelectedOperation switch
        {
            "Gabung" => "Merge",
            "Ekstrak halaman" => "ExtractPages",
            "Rotasi" => "Rotate",
            "Kompres" => "Compress",
            "Lindungi" => "Protect",
            _ => throw new InvalidOperationException("Operasi tidak dikenal.")
        };
        var progressReporter = new Progress<double>(value => Progress = value);
        try
        {
            await _operationService.ExecuteAsync(new PdfOperationRequest(operation, InputFiles.ToArray(), OutputPath, Options), progressReporter, _operationCancellation.Token);
            LastOutputPath = OutputPath;
            await _historyService.AddOrUpdateAsync(OutputPath);
            StatusMessage = "PDF selesai diproses.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Pemrosesan dibatalkan.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            StatusMessage = $"Pemrosesan gagal: {exception.Message}";
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }
}
