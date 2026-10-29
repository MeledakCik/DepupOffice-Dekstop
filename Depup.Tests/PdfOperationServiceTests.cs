using Depup.Core.Interfaces;
using Depup.Core.Models;
using Depup.Core.Services;
using PdfSharp.Pdf;
using Xunit;

namespace Depup.Tests;

public sealed class PdfOperationServiceTests
{
    [Fact]
    public async Task ExtractsSelectedPagesAndReportsProgress()
    {
        var input = CreatePdf(3);
        var output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        var progress = new List<double>();
        try
        {
            await new PdfOperationService().ExecuteAsync(
                new PdfOperationRequest("ExtractPages", [input], output, "1,3"),
                new InlineProgress(progress.Add));
            using var result = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Assert.Equal(2, result.PageCount);
            Assert.Contains(progress, value => value > 0);
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public async Task RejectsOutputThatWouldOverwriteInput()
    {
        var input = CreatePdf(1);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new PdfOperationService().ExecuteAsync(
                new PdfOperationRequest("Rotate", [input], input, "90")));
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
        }
    }

    private static string CreatePdf(int pages)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        using var document = new PdfDocument();
        for (var index = 0; index < pages; index++) document.AddPage();
        document.Save(path);
        return path;
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
