using Depup.Core.Interfaces;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Depup.Core.Services;

public sealed class ScanExportService : IScanExportService
{
    public Task ExportImagesToPdfAsync(IReadOnlyList<string> imagePaths, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (imagePaths.Count == 0) throw new ArgumentException("Tambahkan setidaknya satu halaman scan.");
            using var document = new PdfDocument();
            for (var index = 0; index < imagePaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = imagePaths[index];
                if (!File.Exists(path)) throw new FileNotFoundException("Gambar scan tidak ditemukan.", path);
                using var image = XImage.FromFile(path);
                var page = document.AddPage();
                var landscape = image.PixelWidth > image.PixelHeight;
                page.Orientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                var width = page.Width.Point - 72;
                var height = page.Height.Point - 72;
                var scale = Math.Min(width / image.PointWidth, height / image.PointHeight);
                var drawWidth = image.PointWidth * scale;
                var drawHeight = image.PointHeight * scale;
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(image, (page.Width.Point - drawWidth) / 2, (page.Height.Point - drawHeight) / 2, drawWidth, drawHeight);
                progress?.Report((index + 1d) / imagePaths.Count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            document.Save(outputPath);
        }, cancellationToken);
}
