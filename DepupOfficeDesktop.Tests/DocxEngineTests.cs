using System.IO.Compression;
using System.Text;
using System.Xml;
using DepupOfficeDesktop.Core.Office;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>Mesin editor Word (port DocxEngine.kt): hanya bagian yang diubah yang disunting, sisanya utuh.</summary>
public sealed class DocxEngineTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Docx(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"de_{Guid.NewGuid():N}.docx");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, string x) { using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false)); w.Write(x); }
        Put("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Put("_rels/.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Put("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{W}\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>");
        return path;
    }

    private static string Part(string docx, string name)
    {
        using var z = ZipFile.OpenRead(docx);
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    private static void AssertAllXmlValid(string docx)
    {
        using var z = ZipFile.OpenRead(docx);
        foreach (var e in z.Entries.Where(x => x.FullName.EndsWith(".xml") || x.FullName.EndsWith(".rels")))
        {
            using var r = new StreamReader(e.Open());
            new XmlDocument().LoadXml(r.ReadToEnd());   // melempar bila XML rusak
        }
    }

    private const string Body =
        "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:b/><w:color w:val=\"C00000\"/></w:rPr><w:t>Judul Laporan</w:t></w:r></w:p>" +
        "<w:p><w:r><w:t xml:space=\"preserve\">Halo </w:t></w:r><w:bookmarkStart w:id=\"0\" w:name=\"tanda\"/><w:r><w:rPr><w:i/></w:rPr><w:t>dunia</w:t></w:r><w:bookmarkEnd w:id=\"0\"/></w:p>" +
        "<w:tbl><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid>" +
        "<w:tr><w:tc><w:tcPr><w:shd w:val=\"clear\" w:fill=\"FFFF00\"/></w:tcPr><w:p><w:r><w:t>A1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B1</w:t></w:r></w:p></w:tc></w:tr>" +
        "<w:tr><w:tc><w:p><w:r><w:t>A2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";

    [Fact]
    public void SaveWithoutChangesKeepsDocumentIdentical()
    {
        var src = Docx(Body); var outp = src + ".out.docx";
        try
        {
            var d = DocxDocument.Open(src)!;
            d.Save(outp);
            Assert.Equal(Part(src, "word/document.xml"), Part(outp, "word/document.xml"));
        }
        finally { File.Delete(src); File.Delete(outp); }
    }

    [Fact]
    public void EditsOnlyTouchChangedPartsAndKeepEverythingElse()
    {
        var src = Docx(Body); var outp = src + ".out.docx";
        try
        {
            var d = DocxDocument.Open(src)!;
            var paras = d.Body.OfType<EPara>().ToList();
            Assert.Equal("Judul Laporan", paras[0].Text);
            Assert.Equal(true, paras[0].Fmts[0].B);
            Assert.Equal(1, paras[0].Pf.Align);

            // ubah kata di paragraf kedua (format miring "dunia" tetap), ubah sel, tambah baris
            var p2 = paras[1];
            p2.Text = "Halo dunia baru";
            p2.Fmts = p2.Fmts.Concat(Enumerable.Repeat(p2.Fmts[^1], 5)).ToArray();
            var t = d.Body.OfType<ETable>().Single();
            Assert.True(t.Simple);
            var b2 = t.Rows[1].Cells[1].Paras[0]; b2.Text = "B2 diubah"; b2.Fmts = Enumerable.Repeat(b2.Fmts[0], b2.Text.Length).ToArray();
            t.Rows.Add(new ERow(-1, t.Rows[1].Cells.Select(c => new ECell(-1, new List<EPara> { EPara.Blank(c.Paras[0].Origin, c.Paras[0].Pf, null, "BARU") }, 1, false, null, c.Origin)).ToList(), t.Rows[1].Origin));
            d.Body.Add(EPara.Blank(paras[1].Origin, new ParaFmt(Align: 2), CharFmt.EMPTY with { B = true }, "Penutup"));
            d.Save(outp);

            AssertAllXmlValid(outp);
            var xml = Part(outp, "word/document.xml");
            Assert.Contains("w:bookmarkStart", xml);                 // unsur yang tidak dikelola editor tetap ada
            Assert.Contains("w:fill=\"FFFF00\"", xml);                // arsiran sel tetap
            Assert.Contains("<w:color w:val=\"C00000\"/>", xml);      // format judul tetap

            var r = DocxDocument.Open(outp)!;
            var rp = r.Body.OfType<EPara>().ToList();
            Assert.Equal("Halo dunia baru", rp[1].Text);
            Assert.Equal(true, rp[1].Fmts[6].I);                      // "dunia" masih miring
            var rt = r.Body.OfType<ETable>().Single();
            Assert.Equal(3, rt.Rows.Count);
            Assert.Equal("B2 diubah", rt.Rows[1].Cells[1].Paras[0].Text);
            Assert.Equal("BARU", rt.Rows[2].Cells[0].Paras[0].Text);
            Assert.Equal("Penutup", rp[^1].Text);
            Assert.Equal(2, rp[^1].Pf.Align);
            Assert.Equal(true, rp[^1].Fmts[0].B);
        }
        finally { File.Delete(src); File.Delete(outp); }
    }

    [Fact]
    public void DeletingTableAndParagraphRemovesOnlyThem()
    {
        var src = Docx(Body); var outp = src + ".out.docx";
        try
        {
            var d = DocxDocument.Open(src)!;
            d.Body.RemoveAll(b => b is ETable);
            d.Body.RemoveAt(0);
            d.Save(outp);
            AssertAllXmlValid(outp);
            var r = DocxDocument.Open(outp)!;
            Assert.Empty(r.Body.OfType<ETable>());
            Assert.Equal("Halo dunia", r.Body.OfType<EPara>().Single().Text);
            Assert.Contains("w:sectPr", Part(outp, "word/document.xml"));
        }
        finally { File.Delete(src); File.Delete(outp); }
    }

    [Fact]
    public void NewDocumentWithStylesAndListsSavesValidParts()
    {
        var blank = Path.Combine(Path.GetTempPath(), $"blank_{Guid.NewGuid():N}.docx"); var outp = blank + ".out.docx";
        try
        {
            var d = DocxDocument.CreateBlank(blank)!;
            d.Body.Clear();
            d.Body.Add(EPara.Blank(0, new ParaFmt(Style: d.StyleId(StdStyle.Title)), null, "Judul Baru"));
            int bul = d.NumIdFor(true);
            d.Body.Add(EPara.Blank(0, new ParaFmt(NumId: bul, Ilvl: 0), null, "Poin satu"));
            d.Body.Add(EPara.Blank(0, new ParaFmt(NumId: bul, Ilvl: 0), null, "Poin dua"));
            d.Page = new PageSetup(16838, 11906, true, 720, 720, 720, 720);
            d.Save(outp);

            AssertAllXmlValid(outp);
            Assert.Contains("w:styleId=\"Title\"", Part(outp, "word/styles.xml"));
            Assert.Contains("w:numFmt w:val=\"bullet\"", Part(outp, "word/numbering.xml"));
            Assert.Contains("w:orient=\"landscape\"", Part(outp, "word/document.xml"));
            var r = DocxDocument.Open(outp)!;
            var labels = r.ListLabels();
            Assert.Equal(2, labels.Count);
            Assert.All(labels.Values, l => Assert.Equal("•", l));
            Assert.Equal(1, r.Styles.HeadingLevel(r.Body.OfType<EPara>().First().Pf.Style));

            // berkas ini juga harus terbaca pembaca (Reader)
            using var html = DocxHtml.Render(outp, "x");
            Assert.Contains("Judul Baru", html.Html);
        }
        finally { File.Delete(blank); File.Delete(outp); }
    }

    [Fact]
    public void TextDiffMapsKeptCharacters()
    {
        var r = DocxEngineTestsAccess.Align("abcdef", "abXcdeYf");
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 7 }, r);
    }
}

/// <summary>Akses TextDiff (internal) lewat model: sisipan yang menjaga format karakter lama.</summary>
internal static class DocxEngineTestsAccess
{
    public static int[] Align(string a, string b)
    {
        var t = typeof(DocxDocument).Assembly.GetType("DepupOfficeDesktop.Core.Office.TextDiff")!;
        var res = t.GetMethod("Align")!.Invoke(null, new object[] { a, b })!;
        return (int[])res.GetType().GetProperty("OldToNew")!.GetValue(res)!;
    }
}
