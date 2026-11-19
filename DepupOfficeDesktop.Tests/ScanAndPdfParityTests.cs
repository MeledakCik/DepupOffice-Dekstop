using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

public sealed class ScanAndPdfParityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ScanRedactionsStayInPlaceAfterCroppingAndFiltering(int filter)
    {
        using var temp = new TestFolder();
        string input = temp.File("photo.png");
        using (var picture = new Bitmap(200, 200))
        {
            using var graphics = Graphics.FromImage(picture);
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Red, 100, 100, 20, 20);
            picture.Save(input, ImageFormat.Png);
        }

        var page = new ScanPage(input) { Crop = new RectangleF(0.25f, 0.25f, 0.5f, 0.5f) };
        page.Redactions.Add(new RectangleF(0.5f, 0.5f, 0.1f, 0.1f));
        using var result = PdfEngine.ProcessScanPage(page, filter, 200, 0, 0);

        Assert.Equal(new Size(100, 100), result.Size);
        Assert.Equal(Color.Black.ToArgb(), result.GetPixel(60, 60).ToArgb());
        Assert.Equal(Color.White.ToArgb(), result.GetPixel(20, 20).ToArgb());
    }

    [Fact]
    public void BlankPageDetectionKeepsSparseMarksAndExistingText()
    {
        using var temp = new TestFolder();
        string input = temp.File("pages.pdf");
        var canvas = new PdfCanvas();
        canvas.NewPage();
        canvas.Text("Isi halaman tetap asli", 48, 70, 12, Color.Black);
        canvas.NewPage();
        canvas.Text(".", 4, 9, 10, Color.Black);
        File.WriteAllBytes(input, canvas.ToBytes());

        using var native = Assert.IsType<PagedDocument>(PagedDocument.Open(input).Document);
        using var blank = native.Render(0, 800);
        Assert.True(PdfToolsX.IsBlankPaper(blank), $"Blank page pixels: {blank.GetPixel(0, 0)}, {blank.GetPixel(blank.Width / 2, blank.Height / 2)}.");
        var result = PdfToolsX.FindBlankPages(input);
        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { 0 }, result.Blank);
        string output = temp.File("selected.pdf");
        PdfToolsX.OrganizeLossless(input, [1, 2], new Dictionary<int, int>(), output);
        var copy = PdfToolsX.OpenPdf(output);
        Assert.Equal(2, copy.Pages().Count);
        Assert.Contains("Isi halaman tetap asli", Pdf.Latin1.GetString(copy.Contents(copy.Pages()[0])));
    }

    [Fact]
    public void SearchablePdfAddsInvisibleTextAndKeepsOriginalPageContent()
    {
        using var temp = new TestFolder();
        string input = temp.File("mixed.pdf");
        var canvas = new PdfCanvas(500, 400);
        canvas.Text("Teks vektor asli", 35, 46, 12, Color.Black);
        canvas.NewPage(320, 460);
        using var photo = new Bitmap(135, 90);
        using (var graphics = Graphics.FromImage(photo)) graphics.Clear(Color.AntiqueWhite);
        canvas.Image(photo, new RectangleF(20, 20, 280, 400));
        File.WriteAllBytes(input, canvas.ToBytes());

        string output = temp.File("searchable.pdf");
        var result = PdfToolsX.AddSearchableText(input, output, recognize: rendered =>
            new OcrPage("Teks OCR", ["Teks OCR"], [new OcrLine("Teks OCR", new Rectangle(30, 50, 220, 30))],
                rendered.Width, rendered.Height));
        Assert.Equal((1, 1), result);

        var copy = PdfToolsX.OpenPdf(output);
        var pages = copy.Pages();
        Assert.Contains("Teks vektor asli", Pdf.Latin1.GetString(copy.Contents(pages[0])));
        Assert.Equal(320f, pages[1].Width);
        Assert.Equal(460f, pages[1].Height);
        var page = PdfLayout.Page(copy, pages[1]);
        Assert.Single(page.Images);
        Assert.Equal("TeksOCR", string.Concat(page.Glyphs.Where(glyph => glyph.Invisible && !string.IsNullOrWhiteSpace(glyph.Text)).Select(glyph => glyph.Text)));
        Assert.Equal("Teks vektor asli", PdfLayout.ReadText(copy, pages[0]));
        Assert.Equal("Teks OCR", PdfLayout.ReadText(copy, pages[1]));

        Assert.Equal(1, PdfToolsX.ExtractEmbeddedImages(output, temp.Path, "embedded"));
        string jpeg = Assert.Single(Directory.GetFiles(temp.Path, "embedded_*.jpg"));
        using var extracted = new Bitmap(jpeg);
        Assert.Equal(photo.Size, extracted.Size);
    }

    [Fact]
    public void ReceiptTotalPrefersPaymentTotalAndMissingValuesStayEmpty()
    {
        var receipt = PdfEngine.ParseReceipt("Toko Pagi\n2026-11-19\nTeh Rp 5.000\nTOTAL BAYAR Rp 5.000\nTUNAI Rp 10.000\nKEMBALI Rp 5.000");
        Assert.Equal("Toko Pagi", receipt.Merchant);
        Assert.Equal("2026-11-19", receipt.Date);
        Assert.Equal("5.000", receipt.Total);
        Assert.Contains("Teh Rp 5.000", receipt.Items);
        Assert.Equal("", PdfEngine.ParseReceipt("Toko Pagi\nRincian tanpa nominal").Total);
        Assert.Equal("", PdfEngine.ParseReceipt("Toko Pagi\nRincian tanpa tanggal").Date);
    }

    [Fact]
    public void PresentationExportsEverySlideAtItsOriginalPageSize()
    {
        using var temp = new TestFolder();
        string input = temp.File("slides.pptx");
        using (var zip = ZipFile.Open(input, ZipArchiveMode.Create))
        {
            Add(zip, "ppt/presentation.xml", """
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                    <p:sldIdLst><p:sldId id="256" r:id="rId1"/><p:sldId id="257" r:id="rId2"/></p:sldIdLst>
                    <p:sldSz cx="9144000" cy="5143500"/>
                </p:presentation>
                """);
            Add(zip, "ppt/_rels/presentation.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                    <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
                </Relationships>
                """);
            const string slide = """<p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree/></p:cSld></p:sld>""";
            Add(zip, "ppt/slides/slide1.xml", slide);
            Add(zip, "ppt/slides/slide2.xml", slide);
        }

        string output = temp.File("slides.pdf");
        PdfToolsX.ExportPresentation(input, output);
        var pages = PdfToolsX.OpenPdf(output).Pages();
        Assert.Equal(2, pages.Count);
        Assert.All(pages, page =>
        {
            Assert.Equal(720f, page.Width);
            Assert.Equal(405f, page.Height);
        });

        static void Add(ZipArchive zip, string part, string xml)
        {
            using var writer = new StreamWriter(zip.CreateEntry(part).Open());
            writer.Write(xml);
        }
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("depup-parity-");
        public string Path => _directory.FullName;
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            foreach (var file in _directory.EnumerateFiles()) file.Delete();
            _directory.Delete();
        }
    }
}
