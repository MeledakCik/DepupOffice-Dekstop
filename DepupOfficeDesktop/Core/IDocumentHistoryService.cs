namespace DepupOfficeDesktop.Core;

public interface IDocumentHistoryService
{
    Task<IReadOnlyList<DocumentHistoryEntity>> GetRecentAsync(
        string? searchText = null,
        string? fileType = null,
        bool? bookmarkedOnly = null,
        CancellationToken cancellationToken = default);

    Task RecordOpenedAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> SetBookmarkedAsync(string filePath, bool isBookmarked, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> UpdatePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default);
}
