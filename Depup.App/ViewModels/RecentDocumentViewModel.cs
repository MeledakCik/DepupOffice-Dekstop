using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Depup.Core.Interfaces;
using Depup.Core.Models;

namespace Depup.App.ViewModels;

public partial class RecentDocumentViewModel(RecentDocument document, IDocumentHistoryService historyService, Action<string> openDocument) : ObservableObject
{
    public int Id => document.Id;
    public string Name => document.Name;
    public string Path => document.Path;
    public DateTime LastOpenedUtc => document.LastOpenedUtc;
    [ObservableProperty] private bool isFavorite = document.IsFavorite;

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        await historyService.ToggleFavoriteAsync(Id);
        IsFavorite = !IsFavorite;
    }

    [RelayCommand]
    private void Open() => openDocument(Path);
}
