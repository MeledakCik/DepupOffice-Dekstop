using Depup.Core.Data;
using Depup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Depup.Tests;

public sealed class DocumentHistoryServiceTests
{
    [Fact]
    public async Task RecordsRecentDocumentAndPersistsFavorite()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"depup-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<DepupDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var factory = new TestDbContextFactory(options);
        try
        {
            await using (var context = await factory.CreateDbContextAsync())
                await context.Database.EnsureCreatedAsync();
            var service = new DocumentHistoryService(factory);
            var documentPath = Path.Combine(Path.GetTempPath(), "sample.pdf");
            await service.AddOrUpdateAsync(documentPath);
            var recent = await service.GetRecentAsync();
            Assert.Single(recent);
            Assert.Equal(Path.GetFullPath(documentPath), recent[0].Path);
            await service.ToggleFavoriteAsync(recent[0].Id);
            Assert.True((await service.GetRecentAsync())[0].IsFavorite);
        }
        finally
        {
            await factory.DisposeAsync();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<DepupDbContext> options) : IDbContextFactory<DepupDbContext>, IAsyncDisposable
    {
        private readonly List<DepupDbContext> _contexts = [];
        public DepupDbContext CreateDbContext() => new(options);
        public Task<DepupDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = CreateDbContext();
            _contexts.Add(context);
            return Task.FromResult(context);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts) await context.DisposeAsync();
        }
    }
}
