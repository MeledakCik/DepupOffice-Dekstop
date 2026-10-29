using Microsoft.EntityFrameworkCore;

namespace DepupOfficeDesktop.Core;

public static class LocalHistoryDatabase
{
    private const string LegacyJsonImportKey = "AppConfigJsonImportV1";

    public static void Initialize(string databasePath, AppConfigData legacyConfig)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(legacyConfig);

        string fullDatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDatabasePath)!);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={fullDatabasePath}")
            .Options;

        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        ImportLegacyConfig(db, legacyConfig);
    }

    public static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim());
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static void ImportLegacyConfig(AppDbContext db, AppConfigData config)
    {
        if (db.LocalHistoryMigrations.Any(item => item.Key == LegacyJsonImportKey))
            return;

        using var transaction = db.Database.BeginTransaction();
        var byPath = db.DocumentHistory.ToDictionary(item => item.PathKey, StringComparer.OrdinalIgnoreCase);
        var bookmarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string bookmark in config.Bookmarks)
        {
            if (string.IsNullOrWhiteSpace(bookmark))
                continue;

            try
            {
                bookmarks.Add(NormalizePath(bookmark));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        foreach (var recent in config.RecentFiles)
        {
            if (string.IsNullOrWhiteSpace(recent.FilePath))
                continue;

            try
            {
                string path = Path.GetFullPath(recent.FilePath);
                string pathKey = NormalizePath(path);
                DateTime lastOpened = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, recent.TimestampMs)).UtcDateTime;
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                    fileInfo = null!;

                if (!byPath.TryGetValue(pathKey, out var item))
                {
                    item = new DocumentHistoryEntity { FilePath = path, PathKey = pathKey };
                    byPath.Add(pathKey, item);
                    db.DocumentHistory.Add(item);
                }

                item.FilePath = path;
                item.FileName = string.IsNullOrWhiteSpace(recent.Name) ? Path.GetFileName(path) : recent.Name;
                item.FileType = string.IsNullOrWhiteSpace(recent.Mime) ? AppStorage.MimeByName(item.FileName) : recent.Mime;
                item.SizeBytes = fileInfo?.Length ?? 0;
                item.LastOpenedUtc = lastOpened;
                item.IsBookmarked = bookmarks.Contains(pathKey);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or ArgumentOutOfRangeException)
            {
            }
        }

        foreach (string pathKey in bookmarks)
        {
            if (byPath.ContainsKey(pathKey))
                continue;

            string path = pathKey;
            var fileInfo = new FileInfo(path);
            db.DocumentHistory.Add(new DocumentHistoryEntity
            {
                FilePath = path,
                PathKey = pathKey,
                FileName = Path.GetFileName(path),
                FileType = AppStorage.MimeByName(path),
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                LastOpenedUtc = fileInfo.Exists ? fileInfo.LastAccessTimeUtc : DateTime.UnixEpoch,
                IsBookmarked = true
            });
        }

        db.LocalHistoryMigrations.Add(new LocalHistoryMigrationEntity
        {
            Key = LegacyJsonImportKey,
            CompletedUtc = DateTime.UtcNow
        });
        db.SaveChanges();
        transaction.Commit();
    }
}
