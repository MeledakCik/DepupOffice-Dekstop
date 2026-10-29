using Depup.Core.Interfaces;
using Depup.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Depup.Core.Services;

public sealed class PdfOperationService : IPdfOperationService
{
    public Task ExecuteAsync(PdfOperationRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(request, progress, cancellationToken), cancellationToken);

    private static void Execute(PdfOperationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Validate(request);
        var inputs = request.Inputs.Select(path => PdfReader.Open(path, PdfDocumentOpenMode.Import)).ToArray();
        try
        {
            using var output = new PdfDocument();
            switch (request.Operation)
            {
                case "Merge":
                    for (var documentIndex = 0; documentIndex < inputs.Length; documentIndex++)
                        CopyPages(inputs[documentIndex], output, (page, _) => true, documentIndex, inputs.Length, progress, cancellationToken);
                    break;
                case "Split":
                case "ExtractPages":
                    var extracted = ParseRanges(request.Options, inputs[0].PageCount);
                    var totalPages = extracted.Count;
                    var completedPages = 0;
                    for (var pageIndex = 0; pageIndex < inputs[0].PageCount; pageIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!extracted.Contains(pageIndex + 1)) continue;
                        output.AddPage(inputs[0].Pages[pageIndex]);
                        progress?.Report(++completedPages / (double)totalPages);
                    }
                    break;
                case "Rotate":
                    var degrees = int.TryParse(request.Options, out var parsed) ? parsed : 90;
                    if (degrees is not (90 or 180 or 270)) throw new ArgumentException("Rotasi hanya mendukung 90, 180, atau 270 derajat.");
                    CopyPages(inputs[0], output, (page, target) =>
                    {
                        target.Rotate = (target.Rotate + degrees) % 360;
                        return true;
                    }, 0, 1, progress, cancellationToken);
                    break;
                case "Watermark":
                    throw new NotSupportedException("Watermark belum tersedia pada PDFsharp versi saat ini.");
                case "Protect":
                    CopyPages(inputs[0], output, (_, _) => true, 0, 1, progress, cancellationToken);
                    output.SecuritySettings.UserPassword = request.Options ?? throw new ArgumentException("Kata sandi diperlukan.");
                    output.SecuritySettings.OwnerPassword = Guid.NewGuid().ToString("N");
                    output.SecuritySettings.PermitPrint = true;
                    output.SecuritySettings.PermitModifyDocument = false;
                    break;
                case "Compress":
                    CopyPages(inputs[0], output, (_, _) => true, 0, 1, progress, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Operasi {request.Operation} tidak dikenal.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
            output.Save(request.OutputPath);
            progress?.Report(1d);
        }
        finally
        {
            foreach (var input in inputs) input.Dispose();
        }
    }

    private static void CopyPages(PdfDocument source, PdfDocument target, Func<int, PdfPage, bool> transform,
        int documentIndex, int documentCount, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        for (var index = 0; index < source.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = target.AddPage(source.Pages[index]);
            _ = transform(index, page);
            progress?.Report((documentIndex + (index + 1d) / Math.Max(1, source.PageCount)) / documentCount);
        }
    }

    private static HashSet<int> ParseRanges(string? value, int pageCount)
    {
        var pages = new HashSet<int>();
        foreach (var item in (value ?? "1").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = item.Split('-', StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var first) || first < 1 || first > pageCount)
                throw new ArgumentException($"Nomor halaman '{item}' tidak valid.");
            var last = bounds.Length == 1 ? first : int.TryParse(bounds[1], out var parsed) ? parsed : 0;
            if (last < first || last > pageCount) throw new ArgumentException($"Rentang halaman '{item}' tidak valid.");
            for (var page = first; page <= last; page++) pages.Add(page);
        }
        return pages;
    }

    private static void Validate(PdfOperationRequest request)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("Pilih setidaknya satu PDF.");
        if (string.IsNullOrWhiteSpace(request.OutputPath)) throw new ArgumentException("Lokasi output wajib diisi.");
        if (request.Operation != "Merge" && request.Inputs.Count != 1) throw new ArgumentException("Operasi ini hanya menerima satu PDF.");
        foreach (var input in request.Inputs)
        {
            if (!File.Exists(input)) throw new FileNotFoundException("File tidak ditemukan.", input);
            if (!string.Equals(Path.GetExtension(input), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Operasi ini hanya mendukung PDF.");
            if (new FileInfo(input).Length > 50L * 1024 * 1024) throw new ArgumentException("Ukuran PDF maksimal 50 MB.");
        }
        if (request.Operation == "Merge" && request.Inputs.Count < 2) throw new ArgumentException("Pilih setidaknya dua PDF untuk digabung.");
        if (request.Inputs.Any(input => string.Equals(Path.GetFullPath(input), Path.GetFullPath(request.OutputPath), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Berkas output tidak boleh sama dengan berkas input.");
    }
}
