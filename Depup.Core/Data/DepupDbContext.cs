using Depup.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Depup.Core.Data;

public sealed class DepupDbContext(DbContextOptions<DepupDbContext> options) : DbContext(options)
{
    public DbSet<RecentDocument> RecentDocuments => Set<RecentDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecentDocument>()
            .HasIndex(document => document.Path)
            .IsUnique();
    }
}
