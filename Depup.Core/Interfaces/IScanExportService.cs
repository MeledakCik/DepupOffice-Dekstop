namespace Depup.Core.Interfaces;

public interface IScanExportService
{
    Task ExportImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
