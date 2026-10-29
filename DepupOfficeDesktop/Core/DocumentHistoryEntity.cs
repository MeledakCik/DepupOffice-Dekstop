namespace DepupOfficeDesktop.Core;

public sealed class DocumentHistoryEntity
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string PathKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastOpenedUtc { get; set; }
    public bool IsBookmarked { get; set; }
}

public sealed class LocalHistoryMigrationEntity
{
    public string Key { get; set; } = string.Empty;
    public DateTime CompletedUtc { get; set; }
}
