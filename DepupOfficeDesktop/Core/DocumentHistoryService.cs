using Microsoft.EntityFrameworkCore;

namespace DepupOfficeDesktop.Core;

public sealed class DocumentHistoryService : IDocumentHistoryService
{
    private readonly DbContextOptions<AppDbContext> _options;

    public DocumentHistoryService(DbContextOptions<AppDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public static DocumentHistoryService CreateDefault()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={AppStorage.HistoryDatabasePath}")
            .Options;
        return new DocumentHistoryService(options);
    }

    public async Task<IReadOnlyList<DocumentHistoryEntity>> GetRecentAsync(
        string? searchText = null,
        string? fileType = null,
        bool? bookmarkedOnly = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = new AppDbContext(_options);
        IQueryable<DocumentHistoryEntity> query = db.DocumentHistory.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string search = searchText.Trim().ToLowerInvariant();
            query = query.Where(item => item.FileName.ToLower().Contains(search) || item.FilePath.ToLower().Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(fileType))
        {
            string type = fileType.Trim();
            query = query.Where(item => item.FileType == type);
        }

        if (bookmarkedOnly.HasValue)
            query = query.Where(item => item.IsBookmarked == bookmarkedOnly.Value);

        return await query
            .OrderByDescending(item => item.LastOpenedUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task RecordOpenedAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var file = GetExistingFile(filePath);
        string pathKey = LocalHistoryDatabase.NormalizePath(file.FullName);

        await using var db = new AppDbContext(_options);
        var item = await db.DocumentHistory.SingleOrDefaultAsync(entry => entry.PathKey == pathKey, cancellationToken);
        if (item is null)
        {
            item = new DocumentHistoryEntity { FilePath = file.FullName, PathKey = pathKey };
            db.DocumentHistory.Add(item);
        }

        item.FilePath = file.FullName;
        item.FileName = file.Name;
        item.FileType = AppStorage.MimeByName(file.Name);
        item.SizeBytes = file.Length;
        item.LastOpenedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> SetBookmarkedAsync(string filePath, bool isBookmarked, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(filePath);
        string pathKey = LocalHistoryDatabase.NormalizePath(fullPath);
        await using var db = new AppDbContext(_options);
        var item = await db.DocumentHistory.SingleOrDefaultAsync(entry => entry.PathKey == pathKey, cancellationToken);

        if (item is null)
        {
            var file = GetExistingFile(fullPath);
            item = new DocumentHistoryEntity
            {
                FilePath = file.FullName,
                PathKey = pathKey,
                FileName = file.Name,
                FileType = AppStorage.MimeByName(file.Name),
                SizeBytes = file.Length,
                LastOpenedUtc = file.LastAccessTimeUtc,
                IsBookmarked = isBookmarked
            };
            db.DocumentHistory.Add(item);
        }
        else
        {
            item.IsBookmarked = isBookmarked;
        }

        await db.SaveChangesAsync(cancellationToken);
        return item.IsBookmarked;
    }

    public async Task<bool> RemoveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        string pathKey = LocalHistoryDatabase.NormalizePath(filePath);
        await using var db = new AppDbContext(_options);
        var item = await db.DocumentHistory.SingleOrDefaultAsync(entry => entry.PathKey == pathKey, cancellationToken);
        if (item is null)
            return false;

        db.DocumentHistory.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdatePathAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        string oldKey = LocalHistoryDatabase.NormalizePath(oldPath);
        var file = GetExistingFile(newPath);
        string newKey = LocalHistoryDatabase.NormalizePath(file.FullName);

        await using var db = new AppDbContext(_options);
        var item = await db.DocumentHistory.SingleOrDefaultAsync(entry => entry.PathKey == oldKey, cancellationToken);
        if (item is null)
            return false;

        item.FilePath = file.FullName;
        item.PathKey = newKey;
        item.FileName = file.Name;
        item.FileType = AppStorage.MimeByName(file.Name);
        item.SizeBytes = file.Length;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static FileInfo GetExistingFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
            throw new FileNotFoundException("Berkas tidak ditemukan.", file.FullName);
        return file;
    }
}
