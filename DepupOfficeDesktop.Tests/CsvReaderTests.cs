using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class CsvReaderTests
{
    [Fact]
    public void PreservesQuotedSeparatorsEmptyRowsAndRowsAfterFiveHundred()
    {
        string path = Path.Combine(Path.GetTempPath(), $"csv_{Guid.NewGuid():N}.csv");
        try
        {
            string record = "\"Satu; dua; tiga\",\"Baris 1\nBaris 2\",\"Dia bilang \"\"ya\"\"\"";
            var lines = new[] { "Nama,Catatan,Keterangan", record, "" }
                .Concat(Enumerable.Range(1, 520).Select(index => $"Item {index},Isi,{index}"))
                .Append("\"\"");
            File.WriteAllText(path, string.Join("\r\n", lines));

            var rows = OfficeReader.ReadCsv(path).Rows;
            Assert.Equal(524, rows.Count);
            Assert.Equal(new[] { "Satu; dua; tiga", "Baris 1\nBaris 2", "Dia bilang \"ya\"" }, rows[1]);
            Assert.Equal(new[] { "" }, rows[2]);
            Assert.Equal(new[] { "Item 520", "Isi", "520" }, rows[^2]);
            Assert.Equal(new[] { "" }, rows[^1]);
        }
        finally { File.Delete(path); }
    }
}
