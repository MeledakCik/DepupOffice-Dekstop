using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DepupOfficeDesktop.Core.Office;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Pembaca Word/Excel hasil port Android (DocxHtml / XlsxHtml / NumFmt).</summary>
public sealed class OfficeRenderTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Zip(string ext, params (string Name, string Xml)[] parts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp_{Guid.NewGuid():N}.{ext}");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        return path;
    }

    private static string Docx(string body, string numbering = "") => Zip("docx",
        ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
        ("word/_rels/document.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            (numbering.Length > 0 ? "<Relationship Id=\"n1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" : "") + "</Relationships>"),
        ("word/numbering.xml", $"<w:numbering xmlns:w=\"{W}\">{numbering}</w:numbering>"),
        ("word/document.xml", $"<w:document xmlns:w=\"{W}\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/></w:sectPr></w:body></w:document>"));

    [Fact]
    public void DocxTableKeepsCellsMergesAndPageSize()
    {
        var path = Docx(
            "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Judul &amp; Tabel</w:t></w:r></w:p>" +
            "<w:tbl><w:tblGrid><w:gridCol w:w=\"2000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid>" +
            "<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr><w:p><w:r><w:t>Gabung</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:tcPr><w:vMerge w:val=\"restart\"/></w:tcPr><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p><w:r><w:t>B2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
        try
        {
            using var doc = DocxHtml.Render(path, "uji");
            var h = doc.Html;
            Assert.Contains("SECTS", h);                                   // render lengkap (bukan cadangan)
            Assert.Contains("\"w\":841.9", h);                             // A4 lanskap (pt)
            Assert.Contains("colspan=\"2\"", h);
            Assert.Contains("rowspan=\"2\"", h);
            Assert.Contains("Judul &amp; Tabel", h);                       // teks di-escape
            Assert.Single(Regex.Matches(h, "<table"));
            foreach (var t in new[] { "Gabung", "A", "B1", "B2" }) Assert.Contains(">" + t + "<", h);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DocxNumberingProducesLabels()
    {
        var num = "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum>" +
                  "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>";
        string P(string t) => $"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>{t}</w:t></w:r></w:p>";
        var path = Docx(P("satu") + P("dua") + P("tiga"), num);
        try
        {
            using var doc = DocxHtml.Render(path, "uji");
            Assert.Contains(">1.<", doc.Html);
            Assert.Contains(">2.<", doc.Html);
            Assert.Contains(">3.<", doc.Html);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DocxScriptInjectionIsEscaped()
    {
        var path = Docx("<w:p><w:r><w:t>&lt;script&gt;alert(1)&lt;/script&gt;</w:t></w:r></w:p>");
        try
        {
            using var doc = DocxHtml.Render(path, "uji");
            Assert.DoesNotContain("<script>alert", doc.Html);
            Assert.Contains("&lt;script&gt;alert(1)", doc.Html);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void XlsxRendersSheetsMergesAndFormats()
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var path = Zip("xlsx",
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"s1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"st\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"ss\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{S}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Data Toko\" sheetId=\"1\" r:id=\"s1\"/></sheets></workbook>"),
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{S}\"><si><t>Laporan</t></si></sst>"),
            ("xl/styles.xml", $"<styleSheet xmlns=\"{S}\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"&quot;Rp &quot;#,##0\"/></numFmts><fonts><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills><fill/></fills><borders><border/></borders><cellXfs><xf/><xf numFmtId=\"164\"/><xf numFmtId=\"10\"/></cellXfs></styleSheet>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{S}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c></row><row r=\"2\"><c r=\"A2\" s=\"1\"><v>1250000</v></c><c r=\"B2\" s=\"2\"><v>0.125</v></c></row></sheetData><mergeCells><mergeCell ref=\"A1:C1\"/></mergeCells></worksheet>"));
        try
        {
            using var doc = XlsxHtml.Render(path, "uji");
            var h = doc.Html;
            Assert.Contains("Data Toko", h);
            Assert.Contains("colspan=\"3\"", h);
            Assert.Contains("Rp 1,250,000", h);
            Assert.Contains("12.50%", h);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1234567.891, "#,##0.00", "1,234,567.89")]
    [InlineData(0.075, "0.0%", "7.5%")]
    [InlineData(-1500, "#,##0;[Red](#,##0)", "(1,500)")]
    [InlineData(46344, "dd mmmm yyyy", "18 November 2026")]
    [InlineData(0.5, "h:mm AM/PM", "12:00 PM")]
    [InlineData(12345678901234.0, "General", "1.23457E+13")]
    [InlineData(2.5, "General", "2.5")]
    public void NumFmtMatchesExcel(double v, string code, string expected)
    {
        Assert.Equal(expected, NumFmt.Format(v, code, false).Text);
    }

    [Fact]
    public void NegativeRedSectionReportsColor()
    {
        var (_, color) = NumFmt.Format(-5, "#,##0;[Red](#,##0)", false);
        Assert.Equal(0xFF0000, color);
    }
}
