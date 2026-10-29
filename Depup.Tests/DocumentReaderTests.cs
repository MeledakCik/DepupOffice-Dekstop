using Depup.Core.Services;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Depup.Tests;

public sealed class DocumentReaderTests
{
    [Fact]
    public async Task ReadsCsvFieldsContainingCommas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "Name,Note\nAda,\"hello, world\"");
            var reader = new DocumentReader();
            var result = await reader.ReadAsync(path);
            Assert.Equal("hello, world", result.SheetRows[0][1][1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsDocxTextWithoutModifyingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text("Dokumen baca")))));
                mainPart.Document.Save();
            }

            var before = File.GetLastWriteTimeUtc(path);
            var result = await new DocumentReader().ReadAsync(path);
            Assert.Equal("Dokumen baca", result.SheetRows[0][0][0]);
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
