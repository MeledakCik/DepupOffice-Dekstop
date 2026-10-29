using CommunityToolkit.Mvvm.ComponentModel;
using Depup.Core.Interfaces;
using Depup.Core.Models;
using System.IO;

namespace Depup.App.ViewModels;

public partial class DocumentViewerViewModel(IDocumentReader reader) : ObservableObject
{
    [ObservableProperty] private DocumentContent? content;
    [ObservableProperty] private string? filePath;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private int selectedSheetIndex;

    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        FilePath = path;
        ErrorMessage = null;
        Content = null;
        IsLoading = true;
        try
        {
            Content = await reader.ReadAsync(path, cancellationToken);
            SelectedSheetIndex = 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            ErrorMessage = $"Berkas tidak dapat dibuka: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
