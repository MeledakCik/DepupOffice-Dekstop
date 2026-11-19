using DepupOfficeDesktop.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class DocumentHistoryServiceTests
{
    [Fact]
    public async Task RecordsFiltersBookmarksAndRemovesHistory()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = Path.Combine(temp.Path, "history.db");
        string documentPath = Path.Combine(temp.Path, "Laporan.pdf");
        await File.WriteAllTextAsync(documentPath, "sample pdf content");
        var service = CreateService(databasePath);

        await service.RecordOpenedAsync(documentPath);
        await service.SetBookmarkedAsync(documentPath, true);

        var filtered = await service.GetRecentAsync("laporan", "application/pdf", bookmarkedOnly: true);
        var item = Assert.Single(filtered);
        Assert.Equal(Path.GetFullPath(documentPath), item.FilePath);
        Assert.True(item.IsBookmarked);
        Assert.True(item.SizeBytes > 0);

        Assert.True(await service.RemoveAsync(documentPath));
        Assert.Empty(await service.GetRecentAsync());
    }

    [Fact]
    public async Task UpdatesPathAfterRenameAndRejectsMissingFiles()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = Path.Combine(temp.Path, "history.db");
        string oldPath = Path.Combine(temp.Path, "old.pdf");
        string newPath = Path.Combine(temp.Path, "new.pdf");
        await File.WriteAllTextAsync(oldPath, "pdf content");
        var service = CreateService(databasePath);
        await service.RecordOpenedAsync(oldPath);

        File.Move(oldPath, newPath);
        Assert.True(await service.UpdatePathAsync(oldPath, newPath));
        var item = Assert.Single(await service.GetRecentAsync("new.pdf"));
        Assert.Equal(Path.GetFullPath(newPath), item.FilePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.RecordOpenedAsync(oldPath));
    }

    [Fact]
    public async Task RenamingToPreviouslyUsedPathKeepsSourceBookmark()
    {
        using var temp = new TemporaryDirectory();
        var service = CreateService(Path.Combine(temp.Path, "history.db"));
        string source = Path.Combine(temp.Path, "current.pdf");
        string destination = Path.Combine(temp.Path, "reused.pdf");
        await File.WriteAllTextAsync(destination, "old file");
        await service.RecordOpenedAsync(destination);
        File.Delete(destination);
        await File.WriteAllTextAsync(source, "current file");
        await service.RecordOpenedAsync(source);
        await service.SetBookmarkedAsync(source, true);
        File.Move(source, destination);

        Assert.True(await service.UpdatePathAsync(source, destination));
        var entry = Assert.Single(await service.GetRecentAsync());
        Assert.Equal(destination, entry.FilePath);
        Assert.True(entry.IsBookmarked);
    }

    [Fact]
    public async Task ImportsLegacyJsonHistoryAndBookmarksOnce()
    {
        using var temp = new TemporaryDirectory();
        string databasePath = Path.Combine(temp.Path, "history.db");
        string documentPath = Path.Combine(temp.Path, "lama.pdf");
        await File.WriteAllTextAsync(documentPath, "old document");
        var config = new AppConfigData();
        config.RecentFiles.Add(new RecentEntry(documentPath, "lama.pdf", AppStorage.MIME_PDF, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        config.Bookmarks.Add(documentPath);

        LocalHistoryDatabase.Initialize(databasePath, config);
        LocalHistoryDatabase.Initialize(databasePath, new AppConfigData());
        var service = CreateService(databasePath);

        var item = Assert.Single(await service.GetRecentAsync(bookmarkedOnly: true));
        Assert.Equal("lama.pdf", item.FileName);
        Assert.True(item.IsBookmarked);
    }

    private static DocumentHistoryService CreateService(string databasePath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        using var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return new DocumentHistoryService(options);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DepupHistoryTests_{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
