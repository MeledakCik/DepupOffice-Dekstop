using System.IO.Compression;
using System.Text;
using System.Xml;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Core.Office;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>PDF → Word bertata letak asli (port PdfLayout + PdfToDocx Android).</summary>
public sealed class PdfToDocxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "depup_p2w_" + Guid.NewGuid().ToString("N"));
    public PdfToDocxTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>PDF kecil dari daftar objek (objek 1 = Catalog). Isi Latin-1 agar byte biner (gambar) tetap utuh.</summary>
    private static byte[] Build(params string[] objs)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offs = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offs.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Stream(string dict, string body) => $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

    private static string DocXml(string docx)
    {
        using var z = ZipFile.OpenRead(docx);
        foreach (var e in z.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
        {
            using var s = e.Open(); using var r = XmlReader.Create(s);
            while (r.Read()) { }   // XML wajib sah (Word menolak berkas rusak)
        }
        using var d = new StreamReader(z.GetEntry("word/document.xml")!.Open());
        return d.ReadToEnd();
    }

    private static string Text(string xml) => string.Concat(System.Text.RegularExpressions.Regex.Matches(xml, "<w:t[^>]*>([^<]*)</w:t>").Select(m => m.Groups[1].Value));

    private (PdfToDocx conv, string docx) Convert(byte[] pdf, PdfToDocx.IScan? scan = null)
    {
        var docx = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".docx");
        var conv = new PdfToDocx(docx);
        conv.Convert(PdfFile.Open(pdf), scan);
        return (conv, docx);
    }

    [Fact]
    public void TextTablesFontsAndInlineImageAreKept()
    {
        var content =
            "BT /F1 18 Tf 72 780 Td (Laporan Kegiatan) Tj ET\n" +
            "BT /F2 11 Tf 72 750 Td (Paragraf tebal) Tj ET\n" +
            "0.5 w 72 700 m 472 700 l S 72 680 m 472 680 l S 72 660 m 472 660 l S\n" +
            "72 700 m 72 660 l S 272 700 m 272 660 l S 472 700 m 472 660 l S\n" +
            "BT /F1 10 Tf 76 686 Td (Nama) Tj ET BT /F1 10 Tf 276 686 Td (Jabatan) Tj ET\n" +
            "BT /F1 10 Tf 76 666 Td (Budi) Tj ET BT /F1 10 Tf 276 666 Td (Ketua) Tj ET\n" +
            "BT /F3 12 Tf 72 620 Td <00010002> Tj ET\n" +
            "q 40 0 0 40 72 540 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u0010ðð\u0010 EI Q\n";
        var cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange " +
                   "2 beginbfchar <0001> <03A9> <0002> <20AC> endbfchar endcmap";
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R /F2 5 0 R /F3 6 0 R >> >> /Contents 9 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Calibri /Encoding /Identity-H /DescendantFonts [7 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Calibri /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 >>",
            Stream("", cmap),
            Stream("", content));

        var (conv, docx) = Convert(pdf);
        var xml = DocXml(docx);
        var text = Text(xml);

        Assert.Equal(1, conv.TextPages);
        Assert.Contains("Laporan Kegiatan", text);
        Assert.Contains("Ω€", text);                                   // ToUnicode CMap font Type0
        Assert.Contains("w:ascii=\"Calibri\"", xml);                   // awalan subset "ABCDEF+" dibuang
        Assert.Matches("<w:b/>.*?Paragraf tebal", xml);                // Helvetica-Bold → tebal
        Assert.Contains("<w:pgSz w:w=\"11900\" w:h=\"16840\"/>", xml); // A4 dari MediaBox

        // tabel 2×2 dari garis PDF, isi sel di sel yang benar
        var tbl = xml[xml.IndexOf("<w:tbl>", StringComparison.Ordinal)..(xml.IndexOf("</w:tbl>", StringComparison.Ordinal) + 8)];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(tbl, "<w:gridCol ").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(tbl, "<w:tr>").Count);
        Assert.Equal("NamaJabatanBudiKetua", Text(tbl));

        // gambar inline → PNG
        using (var z = ZipFile.OpenRead(docx))
        {
            var media = z.Entries.Where(e => e.FullName.StartsWith("word/media/")).ToList();
            Assert.Single(media);
            using var s = media[0].Open(); var sig = new byte[8]; s.ReadExactly(sig);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, sig);
        }
        Assert.NotNull(DocxDocument.Open(docx));   // bisa langsung disunting di editor Word aplikasi
    }

    [Fact]
    public void SharedImageIsStoredOnceAndPagesKeepOrder()
    {
        var img = Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "ÿ\u0010\u0010\u0010\u0010ÿ");
        string Page(int contents) => $"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> /Contents {contents} 0 R >>";
        string Body(string t) => $"q 100 0 0 50 72 700 cm /Im0 Do Q BT /F1 12 Tf 72 650 Td ({t}) Tj ET";
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 595 842] >>",
            Page(7), Page(8),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>",
            img,
            Stream("", Body("Halaman satu")),
            Stream("", Body("Halaman dua")));

        var (conv, docx) = Convert(pdf);
        var xml = DocXml(docx);
        Assert.Equal(2, conv.TextPages);
        var text = Text(xml);
        Assert.True(text.IndexOf("Halaman satu", StringComparison.Ordinal) < text.IndexOf("Halaman dua", StringComparison.Ordinal));
        Assert.Contains("<w:pageBreakBefore/>", xml);
        Assert.Contains("w:ascii=\"Times New Roman\"", xml);
        using var z = ZipFile.OpenRead(docx);
        Assert.Single(z.Entries, e => e.FullName.StartsWith("word/media/"));   // logo berulang = satu media
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "r:embed=\"rIdImg1\"").Count);
    }

    private sealed class FakeScan : PdfToDocx.IScan
    {
        public int OcrCalls;
        public List<string>? Ocr(int i) { OcrCalls++; return new() { "Hasil OCR halaman pindaian" }; }
        public (byte[] Jpeg, int W, int H)? PageImage(int i) => null;
    }

    [Fact]
    public void ScannedPageGoesThroughOcrOnlyOnce()
    {
        // halaman pindaian: satu gambar sehalaman, tanpa teks
        var img = Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\u0080");
        var pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 595 842] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>",
            img,
            Stream("", "q 595 0 0 842 0 0 cm /Im0 Do Q"));
        var scan = new FakeScan();
        var (conv, docx) = Convert(pdf, scan);
        Assert.Equal(1, scan.OcrCalls);          // tahap ukur tidak menjalankan OCR
        Assert.Equal(1, conv.OcrPages);
        Assert.Contains("Hasil OCR halaman pindaian", Text(DocXml(docx)));
    }

    [Fact]
    public void CMapParsesRangesArraysAndCodespace()
    {
        var cm = "1 begincodespacerange <00> <FF> endcodespacerange " +
                 "2 beginbfrange <41> <43> <0061> <44> <45> [<0078> <00660069>] endbfrange " +
                 "1 beginbfchar <46> /Euro endbfchar";
        var map = new Dictionary<int, string>(); var ranges = new List<int[]>();
        PdfLayout.CMap.Parse(Encoding.Latin1.GetBytes(cm), map, ranges);
        Assert.Equal("a", map[0x41]); Assert.Equal("c", map[0x43]);   // rentang menaik
        Assert.Equal("x", map[0x44]); Assert.Equal("fi", map[0x45]);  // larik tujuan (ligatur)
        Assert.Equal("€", map[0x46]);                                  // nama glif
        Assert.Single(ranges); Assert.Equal(new[] { 1, 0, 255 }, ranges[0]);
    }

    [Fact]
    public void FontInfoNormalisesCommonFamilies()
    {
        var a = PdfLayout.Font.FontInfoOf("XYZABC+TimesNewRomanPS-BoldItalicMT", null);
        Assert.Equal("Times New Roman", a.Family); Assert.True(a.Bold); Assert.True(a.Italic); Assert.True(a.Serif);
        var b = PdfLayout.Font.FontInfoOf("Helvetica-Oblique", null);
        Assert.Equal("Arial", b.Family); Assert.False(b.Bold); Assert.True(b.Italic);
        Assert.True(PdfLayout.Font.FontInfoOf("CourierNewPSMT", null).Mono);
    }
}
