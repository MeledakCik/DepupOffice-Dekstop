using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Xml.Linq;
using DepupOfficeDesktop.Core;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Editor presentasi (ala WPS): model, penulis .pptx, pembaca .pptx, penggambar, ekspor PDF.</summary>
public sealed class PresentationEditorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("depup-slides-");
    private string F(string name) => Path.Combine(_dir.FullName, name);
    public void Dispose() { try { _dir.Delete(true); } catch { } }

    private static byte[] Png(Color c, int w = 40, int h = 20)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) g.Clear(c);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(SlideDeck.TEMPLATE_BLANK, 1)]
    [InlineData(SlideDeck.TEMPLATE_SCHOOL, 6)]
    [InlineData(SlideDeck.TEMPLATE_PITCH, 6)]
    public void TemplatesOpenInTheExistingReader(string template, int slides)
    {
        var deck = SlideDeck.Create(template);
        Assert.Equal(slides, deck.Slides.Count);
        string path = F(template + ".pptx");
        deck.Save(path);
        using var reader = new PptxRenderer(path);
        Assert.Equal(slides, reader.SlideCount);
        Assert.Equal(16f / 9f, reader.Aspect, 3);
        using var bmp = reader.RenderSlide(0, 320);
        Assert.Equal(320, bmp.Width);
        if (template == SlideDeck.TEMPLATE_SCHOOL) Assert.Contains(reader.Texts(), t => t.Contains("Pendahuluan"));
    }

    [Fact]
    public void PackageIsCompleteOoxml()
    {
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_SCHOOL);
        deck.Slides[1].Items.Add(new SlideItem { Kind = SlideItemKind.Image, Box = new RectangleF(10, 10, 80, 40), Image = Png(Color.Red), ImageExt = "png" });
        string path = F("valid.pptx");
        deck.Save(path);

        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        // semua XML terbaca
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            using (var s = e.Open()) XDocument.Load(s);
        // setiap target relasi ada di paket
        foreach (var rels in zip.Entries.Where(e => e.FullName.EndsWith(".rels")))
        {
            XDocument doc; using (var s = rels.Open()) doc = XDocument.Load(s);
            string owner = rels.FullName.Replace("_rels/", "").Replace(".rels", "");
            string baseDir = (Path.GetDirectoryName(owner) ?? "").Replace('\\', '/');
            foreach (var r in doc.Root!.Elements())
            {
                string target = (string)r.Attribute("Target")!;
                string full = Path.GetFullPath(Path.Combine("/root", baseDir, target)).Replace('\\', '/');
                full = full[(full.IndexOf("/root/") + 6)..];
                Assert.True(names.Contains(full), $"{rels.FullName} → {target} tidak ada");
            }
        }
        // setiap bagian XML (kecuali .rels) punya jenis isi
        XDocument ct; using (var s = zip.GetEntry("[Content_Types].xml")!.Open()) ct = XDocument.Load(s);
        var overrides = ct.Root!.Elements().Where(e => e.Name.LocalName == "Override").Select(e => ((string)e.Attribute("PartName")!).TrimStart('/')).ToHashSet();
        foreach (var n in names.Where(n => n.EndsWith(".xml") && n != "[Content_Types].xml"))
            Assert.True(overrides.Contains(n), $"{n} tanpa Override jenis isi");
        Assert.Contains("ppt/media/image1.png", names);
    }

    [Fact]
    public void SaveThenLoadKeepsEverythingEditable()
    {
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_BLANK, theme: 2);
        var s = deck.Slides[0];
        s.Items[0].Text = "Judul & <uji> \"kutip\"";
        s.Items[2].Text = "Baris satu\nBaris dua";
        var extra = deck.NewSlide(SlideLayout.TitleContent, "Isi", "Poin A\n\nPoin B");
        extra.Items.Add(new SlideItem
        {
            Kind = SlideItemKind.Shape, Shape = SlideShape.Ellipse, Box = new RectangleF(900, 400, 200, 120),
            Fill = Color.FromArgb(0x35, 0x75, 0x4C), Line = Color.FromArgb(0x11, 0x11, 0x11), Text = "Lingkaran", Size = 20, Align = 1, VAlign = 1,
            Italic = true, Underline = true, Color = Color.White
        });
        var png = Png(Color.Blue);
        extra.Items.Add(new SlideItem { Kind = SlideItemKind.Image, Box = new RectangleF(100.5f, 500, 160, 80), Image = png, ImageExt = "png" });
        deck.Slides.Add(extra);
        string path = F("roundtrip.pptx");
        deck.Save(path);

        var back = SlideDeck.Load(path);
        Assert.False(back.Foreign);
        Assert.Equal(0, back.Unsupported);
        Assert.Equal(2, back.ThemeIndex);
        Assert.Equal(2, back.Slides.Count);
        Assert.Equal(deck.Slides[0].Background.ToArgb(), back.Slides[0].Background.ToArgb());

        var title = back.Slides[0].Items[0];
        Assert.Equal("Judul & <uji> \"kutip\"", title.Text);
        Assert.True(title.Bold);
        Assert.Equal(44f, title.Size);
        Assert.Equal(1, title.Align);
        Assert.Equal(2, title.VAlign);
        Assert.Equal("Georgia", title.Font);
        Assert.Equal(deck.Theme.Title.ToArgb(), title.Color.ToArgb());
        Assert.Equal("Baris satu\nBaris dua", back.Slides[0].Items[2].Text);

        var items = back.Slides[1].Items;
        Assert.Equal(4, items.Count);
        Assert.True(items[1].Bullets);
        Assert.Equal("Poin A\n\nPoin B", items[1].Text);
        var ellipse = items[2];
        Assert.Equal(SlideItemKind.Shape, ellipse.Kind);
        Assert.Equal(SlideShape.Ellipse, ellipse.Shape);
        Assert.Equal("Lingkaran", ellipse.Text);
        Assert.True(ellipse.Italic && ellipse.Underline);
        Assert.Equal(Color.FromArgb(0x35, 0x75, 0x4C).ToArgb(), ellipse.Fill.ToArgb());
        Assert.Equal(Color.FromArgb(0x11, 0x11, 0x11).ToArgb(), ellipse.Line.ToArgb());
        var img = items[3];
        Assert.Equal(SlideItemKind.Image, img.Kind);
        Assert.Equal(png, img.Image);
        Assert.Equal(100.5f, img.Box.X, 1);
        Assert.Equal(160f, img.Box.Width, 1);
    }

    [Fact]
    public void ForeignPresentationUsesLayoutPlaceholdersAndSchemeColors()
    {
        string path = F("lain.pptx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            const string ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
            const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
            void Add(string part, string xml) { using var w = new StreamWriter(zip.CreateEntry(part).Open()); w.Write(xml); }
            void Rels(string part, params (string id, string type, string target)[] r) =>
                Add(part, "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    string.Concat(r.Select(x => $"<Relationship Id=\"{x.id}\" Type=\"{rel}{x.type}\" Target=\"{x.target}\"/>")) + "</Relationships>");
            Add("ppt/presentation.xml", $"<p:presentation {ns}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/></p:presentation>");
            Rels("ppt/_rels/presentation.xml.rels", ("rId1", "theme", "theme/theme1.xml"), ("rId2", "slide", "slides/slide1.xml"));
            Add("ppt/theme/theme1.xml", "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Office\"><a:themeElements><a:clrScheme name=\"x\">" +
                "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:accent1><a:srgbClr val=\"C00000\"/></a:accent1></a:clrScheme>" +
                "<a:fontScheme name=\"x\"><a:majorFont><a:latin typeface=\"Cambria\"/></a:majorFont><a:minorFont><a:latin typeface=\"Calibri\"/></a:minorFont></a:fontScheme></a:themeElements></a:theme>");
            Add("ppt/slideLayouts/slideLayout1.xml", $"<p:sldLayout {ns}><p:cSld><p:spTree>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"t\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"274638\"/><a:ext cx=\"8229600\" cy=\"1143000\"/></a:xfrm></p:spPr></p:sp>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"b\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"457200\" y=\"1600200\"/><a:ext cx=\"8229600\" cy=\"4525963\"/></a:xfrm></p:spPr></p:sp>" +
                "</p:spTree></p:cSld></p:sldLayout>");
            Add("ppt/slides/slide1.xml", $"<p:sld {ns}><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
                "<p:txBody><a:bodyPr/><a:p><a:r><a:rPr lang=\"en-US\"><a:solidFill><a:schemeClr val=\"accent1\"/></a:solidFill></a:rPr><a:t>Laporan</a:t></a:r><a:r><a:rPr/><a:t> Tahunan</a:t></a:r></a:p></p:txBody></p:sp>" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Body\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
                "<p:txBody><a:bodyPr/><a:p><a:r><a:rPr sz=\"2000\"/><a:t>Satu</a:t></a:r></a:p><a:p><a:r><a:rPr/><a:t>Dua</a:t></a:r><a:br/><a:r><a:t>Tiga</a:t></a:r></a:p></p:txBody></p:sp>" +
                "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"9\" name=\"g\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:grpSp>" +
                "<p:pic><p:nvPicPr><p:cNvPr id=\"4\" name=\"pic\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId2\"/></p:blipFill>" +
                "<p:spPr><a:xfrm><a:off x=\"952500\" y=\"952500\"/><a:ext cx=\"1905000\" cy=\"952500\"/></a:xfrm></p:spPr></p:pic>" +
                "</p:spTree></p:cSld></p:sld>");
            Rels("ppt/slides/_rels/slide1.xml.rels", ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "image", "../media/foto.png"));
            using (var s = zip.CreateEntry("ppt/media/foto.png").Open()) s.Write(Png(Color.Green));
        }

        var deck = SlideDeck.Load(path);
        Assert.True(deck.Foreign);
        Assert.Equal(1, deck.Unsupported);   // grup
        Assert.Equal(960f, deck.W, 1);       // 4:3
        Assert.Equal(720f, deck.H, 1);
        var items = deck.Slides[0].Items;
        Assert.Equal(3, items.Count);

        var title = items[0];
        Assert.Equal("Laporan Tahunan", title.Text);
        Assert.Equal("title", title.Role);
        Assert.Equal(48f, title.Box.X, 1);   // dari layout
        Assert.Equal(40f, title.Size);
        Assert.Equal("Cambria", title.Font); // huruf mayor tema
        Assert.Equal(Color.FromArgb(0xC0, 0, 0).ToArgb(), title.Color.ToArgb());

        var body = items[1];
        Assert.Equal("Satu\nDua\nTiga", body.Text);
        Assert.True(body.Bullets);
        Assert.Equal(20f, body.Size);
        Assert.Equal("Calibri", body.Font);
        Assert.Equal(168f, body.Box.Y, 1);

        Assert.Equal(SlideItemKind.Image, items[2].Kind);
        Assert.Equal(new RectangleF(100, 100, 200, 100), items[2].Box);

        // menyunting & menyimpan berkas asing menghasilkan .pptx yang terbaca lagi
        title.Text = "Laporan 2026";
        string saved = F("lain_edit.pptx");
        deck.Save(saved);
        var again = SlideDeck.Load(saved);
        Assert.False(again.Foreign);
        Assert.Equal("Laporan 2026", again.Slides[0].Items[0].Text);
        Assert.Equal(960f, again.W, 1);
    }

    [Fact]
    public void RenderingShowsContentAndHidesHintsOutsideEditor()
    {
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_BLANK);   // judul & subjudul kosong
        using var plain = deck.RenderBitmap(0, 640);
        using var g = Graphics.FromImage(plain);
        int Ink(Bitmap b) { int n = 0; for (int y = 0; y < b.Height; y += 3) for (int x = 0; x < b.Width; x += 3) if (b.GetPixel(x, y).GetBrightness() < 0.6f) n++; return n; }
        int empty = Ink(plain);   // hanya garis aksen
        using var hinted = new Bitmap(640, 360);
        using (var hg = Graphics.FromImage(hinted)) deck.Render(hg, deck.Slides[0], new RectangleF(0, 0, 640, 360), hints: true);
        Assert.True(Ink(hinted) > empty, "petunjuk tampil di editor");

        deck.Slides[0].Items[0].Text = "Presentasi";
        using var withText = deck.RenderBitmap(0, 640);
        Assert.True(Ink(withText) > empty + 20, "judul tergambar");
        Assert.Equal(360, withText.Height);
    }

    [Fact]
    public void ThemeChangeRecolorsThemeColorsOnly()
    {
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_SCHOOL);
        var custom = Color.FromArgb(0x12, 0x34, 0x56);
        deck.Slides[1].Items[1].Color = custom;
        deck.ApplyTheme(1);
        Assert.Equal(DeckTheme.All[1].Bg.ToArgb(), deck.Slides[0].Background.ToArgb());
        Assert.Equal(DeckTheme.All[1].Title.ToArgb(), deck.Slides[0].Items[0].Color.ToArgb());
        Assert.Equal(DeckTheme.All[1].Accent.ToArgb(), deck.Slides[0].Items[1].Fill.ToArgb());
        Assert.Equal(custom.ToArgb(), deck.Slides[1].Items[1].Color.ToArgb());
    }

    [Fact]
    public void ExportPdfHasOnePagePerSlide()
    {
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_PITCH);
        string pdf = F("deck.pdf");
        deck.ExportPdf(pdf);
        var pages = PdfToolsX.OpenPdf(pdf).Pages();
        Assert.Equal(6, pages.Count);
        Assert.All(pages, p => { Assert.Equal(960f, p.Width, 1); Assert.Equal(540f, p.Height, 1); });
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.tmp"));
    }

    /// <summary>Contoh berisi semua jenis elemen untuk diperiksa di PowerPoint / WPS / LibreOffice (hanya bila DEPUP_SHOTS diisi).</summary>
    [Fact]
    public void WritesSampleForExternalCheck()
    {
        if (Environment.GetEnvironmentVariable("DEPUP_SHOTS") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var deck = SlideDeck.Create(SlideDeck.TEMPLATE_SCHOOL, theme: 3);
        var s = deck.NewSlide(SlideLayout.TitleOnly, "Bentuk & gambar");
        float x = 80;
        foreach (var shape in Enum.GetValues<SlideShape>())
        {
            s.Items.Add(new SlideItem { Kind = SlideItemKind.Shape, Shape = shape, Box = new RectangleF(x, 200, 200, 130), Fill = deck.Theme.Accent, Line = Color.Black, Text = shape.ToString(), Color = Color.White, Size = 18, Align = 1, VAlign = 1, Font = deck.Theme.Font });
            x += 225;
        }
        s.Items.Add(new SlideItem { Kind = SlideItemKind.Image, Box = new RectangleF(80, 380, 300, 150), Image = Png(Color.Orange, 300, 150), ImageExt = "png" });
        s.Items.Add(new SlideItem { Kind = SlideItemKind.Text, Box = new RectangleF(420, 380, 700, 150), Text = "Teks miring & bergaris <bawah>\nBaris kedua", Italic = true, Underline = true, Size = 24, Color = Color.DarkRed, Align = 2, Font = "Georgia" });
        deck.Slides.Add(s);
        deck.Save(Path.Combine(dir, "contoh.pptx"));
    }

    /// <summary>Berkas asli dari PowerPoint (DEPUP_PPTX_FROM_OFFICE = jalur berkas) bisa disunting &amp; disimpan ulang.</summary>
    [Fact]
    public void LoadsRealPowerPointFileWhenProvided()
    {
        if (Environment.GetEnvironmentVariable("DEPUP_PPTX_FROM_OFFICE") is not { Length: > 0 } path || !File.Exists(path)) return;
        var deck = SlideDeck.Load(path);
        Assert.True(deck.Foreign);
        Assert.Equal(2, deck.Slides.Count);
        Assert.Contains(deck.Slides[0].Items, i => i.Text == "Laporan Kuartal" && i.Role == "title" && i.Box.Width > 100);
        Assert.Contains(deck.Slides[0].Items, i => i.Text == "Tim Penjualan" && i.Align == 1);
        var body = deck.Slides[1].Items.Single(i => i.Text.StartsWith("Pendapatan"));
        Assert.Equal("Pendapatan naik\nBiaya turun\nTarget tercapai", body.Text);
        Assert.True(body.Bullets);
        var oval = deck.Slides[1].Items.Single(i => i.Kind == SlideItemKind.Shape && i.Shape == SlideShape.Ellipse && i.Text == "Catatan");
        Assert.False(oval.Fill.IsEmpty, "warna isi dari <p:style> fillRef");
        Assert.False(oval.Line.IsEmpty, "warna garis dari <p:style> lnRef");
        Assert.Equal(Color.White.ToArgb(), oval.Color.ToArgb());   // fontRef lt1
        Assert.Equal(0, deck.Unsupported);
        string outPath = F("dari_powerpoint_edit.pptx");
        deck.Save(outPath);
        Assert.Equal(2, SlideDeck.Load(outPath).Slides.Count);
        if (Environment.GetEnvironmentVariable("DEPUP_SHOTS") is { Length: > 0 } shots)
            for (int i = 0; i < deck.Slides.Count; i++) { using var b = deck.RenderBitmap(i, 960); b.Save(Path.Combine(shots, $"depup_dari_pp_{i + 1}.png")); }
    }

    [Fact]
    public void TextHeightGrowsWithLines()
    {
        var i = new SlideItem { Kind = SlideItemKind.Text, Box = new RectangleF(0, 0, 400, 40), Size = 24, Text = "Satu" };
        float one = SlideDeck.TextHeight(i);
        i.Text = "Satu\nDua\nTiga";
        Assert.True(SlideDeck.TextHeight(i) > one + 50, "dua baris tambahan ≈ 2 × 38 px");
    }
}
