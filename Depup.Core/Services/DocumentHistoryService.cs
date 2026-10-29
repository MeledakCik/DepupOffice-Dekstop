using Depup.Core.Data;
using Depup.Core.Interfaces;
using Depup.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Depup.Core.Services;

public sealed class DocumentHistoryService(IDbContextFactory<DepupDbContext> contextFactory) : IDocumentHistoryService
{
    public async Task<IReadOnlyList<RecentDocument>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.RecentDocuments
            .OrderByDescending(document => document.LastOpenedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
    }

    public async Task AddOrUpdateAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await dbContext.RecentDocuments
            .SingleOrDefaultAsync(item => item.Path == fullPath, cancellationToken);

        if (document is null)
        {
            document = new RecentDocument { Path = fullPath, Name = Path.GetFileName(fullPath) };
            dbContext.RecentDocuments.Add(document);
        }

        document.LastOpenedUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ToggleFavoriteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await dbContext.RecentDocuments.FindAsync([id], cancellationToken);
        if (document is null) return;

        document.IsFavorite = !document.IsFavorite;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
