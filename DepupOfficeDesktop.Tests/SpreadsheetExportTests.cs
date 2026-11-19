using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class SpreadsheetExportTests
{
    [Fact]
    public void WorkbookCanReadFormulaAndCachedValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sheet_{Guid.NewGuid():N}.xlsx");
        try
        {
            IReadOnlyList<string>[] rows = [["0.5", "1.75", "2.25", " Teks "]];
            var formulas = new Dictionary<(int, int), (string, double)>
            {
                [(0, 2)] = ("SUM($A$1:B1)", 2.25)
            };
            XlsxWriter.Write(path, "Uji rumus", rows, formulas: formulas);

            var values = Assert.IsType<OfficeReader.Sheet>(OfficeReader.ReadXlsx(path));
            Assert.Equal("2.25", values.Rows[0][2]);
            Assert.Equal(" Teks ", values.Rows[0][3]);
            var editable = Assert.IsType<OfficeReader.Sheet>(OfficeReader.ReadXlsx(path, preserveFormulas: true));
            Assert.Equal("=SUM($A$1:B1)", editable.Rows[0][2]);
        }
        finally { File.Delete(path); }
    }
}
