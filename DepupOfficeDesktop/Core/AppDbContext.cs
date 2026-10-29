using Microsoft.EntityFrameworkCore;

namespace DepupOfficeDesktop.Core;

public sealed class AppDbContext : DbContext
{
    public DbSet<DocumentHistoryEntity> DocumentHistory => Set<DocumentHistoryEntity>();
    public DbSet<LocalHistoryMigrationEntity> LocalHistoryMigrations => Set<LocalHistoryMigrationEntity>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentHistoryEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.FilePath).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.PathKey).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.FileName).HasMaxLength(512).IsRequired();
            entity.Property(item => item.FileType).HasMaxLength(32).IsRequired();
            entity.HasIndex(item => item.PathKey).IsUnique();
            entity.HasIndex(item => item.LastOpenedUtc);
        });

        modelBuilder.Entity<LocalHistoryMigrationEntity>(entity =>
        {
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasMaxLength(128);
        });
    }
}
