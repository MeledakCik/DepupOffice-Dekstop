namespace Depup.Core.Models;

public sealed class RecentDocument
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public DateTime LastOpenedUtc { get; set; }
    public bool IsFavorite { get; set; }
}
