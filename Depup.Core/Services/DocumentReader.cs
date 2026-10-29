using System.Text;
using ClosedXML.Excel;
using Depup.Core.Interfaces;
using Depup.Core.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Depup.Core.Services;

public sealed class DocumentReader : IDocumentReader
{
    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return Task.FromResult(extension switch
        {
            ".docx" => ReadWord(path),
            ".xlsx" or ".xlsm" => ReadSpreadsheet(path),
            ".csv" => ReadCsv(path),
            _ => throw new NotSupportedException($"Format {extension} belum didukung untuk tampilan teks.")
        });
    }

    private static DocumentContent ReadWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var paragraphs = document.MainDocumentPart?.Document.Body?.Descendants<Paragraph>()
            .Select(paragraph => string.Concat(paragraph.Descendants<Text>().Select(text => text.Text)))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => (IReadOnlyList<string>)new[] { text })
            .Select(row => (IReadOnlyList<IReadOnlyList<string>>)new[] { row })
            .ToArray() ?? [];

        return new DocumentContent(Path.GetFileName(path), "DOCX", [], paragraphs);
    }

    private static DocumentContent ReadSpreadsheet(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheets = workbook.Worksheets.ToArray();
        var rows = sheets.Select(sheet => (IReadOnlyList<IReadOnlyList<string>>)sheet.RowsUsed().Take(1000)
            .Select(row => (IReadOnlyList<string>)row.CellsUsed()
                .Select(cell => cell.GetFormattedString()).ToArray()).ToArray()).ToArray();
        return new DocumentContent(Path.GetFileName(path), "XLSX", sheets.Select(sheet => sheet.Name).ToArray(), rows);
    }

    private static DocumentContent ReadCsv(string path)
    {
        var rows = File.ReadLines(path, Encoding.UTF8).Take(1000)
            .Select(line => (IReadOnlyList<string>)ParseCsvLine(line))
            .ToArray();
        return new DocumentContent(Path.GetFileName(path), "CSV", ["CSV"], [rows]);
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"')
            {
                value.Append('"');
                index++;
            }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
            }
            else value.Append(character);
        }
        fields.Add(value.ToString());
        return fields.ToArray();
    }
}
