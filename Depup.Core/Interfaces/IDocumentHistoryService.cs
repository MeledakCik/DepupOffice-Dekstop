using Depup.Core.Models;

namespace Depup.Core.Interfaces;

public interface IDocumentHistoryService
{
    Task<IReadOnlyList<RecentDocument>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task AddOrUpdateAsync(string path, CancellationToken cancellationToken = default);
    Task ToggleFavoriteAsync(int id, CancellationToken cancellationToken = default);
}
