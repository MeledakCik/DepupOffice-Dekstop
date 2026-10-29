using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Depup.App.Services;
using Depup.Core.Interfaces;
using System.Collections.ObjectModel;
using System.IO;

namespace Depup.App.ViewModels;

public abstract partial class PageViewModel(string title, string subtitle) : ObservableObject
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
}

public sealed partial class ReadPageViewModel : PageViewModel
{
    private readonly IDocumentHistoryService _historyService;
    private readonly DocumentPickerService _pickerService;
    private readonly Action<string> _openDocument;

    public ObservableCollection<RecentDocumentViewModel> RecentDocuments { get; } = [];

    public ReadPageViewModel(IDocumentHistoryService historyService, DocumentPickerService pickerService, Action<string> openDocument)
        : base("Baca", "Ruang baca dan manajemen berkas")
    {
        _historyService = historyService;
        _pickerService = pickerService;
        _openDocument = openDocument;
        _ = LoadRecentAsync();
    }

    [RelayCommand]
    private void PickAndOpen()
    {
        var path = _pickerService.PickDocument();
        if (path is not null) _openDocument(path);
    }

    public async Task LoadRecentAsync()
    {
        RecentDocuments.Clear();
        foreach (var item in await _historyService.GetRecentAsync())
            RecentDocuments.Add(new RecentDocumentViewModel(item, _historyService, _openDocument));
    }

    public async Task RecordOpenedAsync(string path)
    {
        if (!File.Exists(path)) return;
        await _historyService.AddOrUpdateAsync(path);
        await LoadRecentAsync();
    }
}

public sealed class ToolsPageViewModel(PdfToolViewModel pdfTool) : PageViewModel("Tools", "Peralatan PDF, OCR, dan konversi instan")
{
    public PdfToolViewModel PdfTool { get; } = pdfTool;
}

public sealed class CreatePageViewModel(ScanPageViewModel scanPage) : PageViewModel("Buat", "Studio kreasi PDF, Word, Excel, dan scan")
{
    public ScanPageViewModel ScanPage { get; } = scanPage;
}

public sealed class SavePageViewModel() : PageViewModel("Simpan", "Folder berkas dan keamanan Vault")
{
    public string EmptyTitle => "Berkas Anda tetap lokal";
    public string EmptyDescription => "Kelola hasil olahan dan simpan dokumen penting di Vault terenkripsi.";
}
