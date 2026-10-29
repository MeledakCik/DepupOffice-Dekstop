using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting dari DocxWriter.kt:
    /// Penulis .docx mini (tanpa library pihak ketiga): paragraf teks hasil OCR dan/atau gambar halaman, ukuran A4/Letter/F4.
    /// </summary>
    public sealed class DocxWriter : IDisposable
    {
        private const string XML = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
        private readonly FileStream _fs;
        private readonly ZipArchive _zip;
        private readonly StringBuilder _body = new();
        private readonly StringBuilder _rels = new();
        private readonly string _pageSize;
        private readonly int _marginMode;
        private readonly float _lineSpacing;
        private readonly int _fontFamily;
        private int _imgCount = 0;
        private bool _first = true;
        private bool _finished = false;

        public DocxWriter(
            string outPath,
            string pageSize = "A4",
            int marginMode = 0,
            float lineSpacing = 1.15f,
            int fontFamily = 0)
        {
            _pageSize = pageSize;
            _marginMode = marginMode;
            _lineSpacing = lineSpacing;
            _fontFamily = fontFamily;

            _fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            _zip = new ZipArchive(_fs, ZipArchiveMode.Create, leaveOpen: false);

            string types = XML +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>";

            string root = XML +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>";

            Put("[Content_Types].xml", Encoding.UTF8.GetBytes(types));
            Put("_rels/.rels", Encoding.UTF8.GetBytes(root));
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c < 32 && c != '\t') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private void Put(string name, byte[] data)
        {
            var entry = _zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(data, 0, data.Length);
        }

        public void BeginPage()
        {
            if (!_first)
                _body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            _first = false;
        }

        private static void AppendRunText(StringBuilder target, string text)
        {
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) target.Append("<w:br/>");
                target.Append("<w:t xml:space=\"preserve\">").Append(Esc(lines[i])).Append("</w:t>");
            }
        }

        public void Paragraph(string text, bool bold = false, bool italic = false, int align = 0)
        {
            string jc = align switch
            {
                1 => "<w:jc w:val=\"center\"/>",
                2 => "<w:jc w:val=\"right\"/>",
                3 => "<w:jc w:val=\"both\"/>",
                _ => ""
            };
            var rPr = new StringBuilder();
            if (bold) rPr.Append("<w:b/>");
            if (italic) rPr.Append("<w:i/>");
            rPr.Append("<w:sz w:val=\"22\"/>");
            if (_fontFamily == 1)
                rPr.Append("<w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"/>");

            int lineVal = (int)(_lineSpacing * 240);
            _body.Append("<w:p><w:pPr>")
                 .Append($"<w:spacing w:after=\"120\" w:line=\"{lineVal}\" w:lineRule=\"auto\"/>")
                 .Append(jc)
                 .Append("</w:pPr><w:r><w:rPr>").Append(rPr).Append("</w:rPr>");
            AppendRunText(_body, text);
            _body.Append("</w:r></w:p>");
        }

        public void Note(string text)
        {
            int lineVal = (int)(_lineSpacing * 240);
            _body.Append($"<w:p><w:pPr><w:spacing w:after=\"120\" w:line=\"{lineVal}\" w:lineRule=\"auto\"/></w:pPr>")
                 .Append("<w:r><w:rPr><w:i/><w:color w:val=\"64748B\"/><w:sz w:val=\"20\"/></w:rPr>");
            AppendRunText(_body, text);
            _body.Append("</w:r></w:p>");
        }

        public void Image(byte[] jpeg, int wPx, int hPx)
        {
            _imgCount++;
            int n = _imgCount;
            Put($"word/media/image{n}.jpeg", jpeg);
            _rels.Append($"<Relationship Id=\"rIdImg{n}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{n}.jpeg\"/>");
            double maxW = 6120000.0, maxH = 9252000.0;
            double s = Math.Min(maxW / Math.Max(1, wPx), maxH / Math.Max(1, hPx));
            long cx = (long)(wPx * s), cy = (long)(hPx * s);
            _body.Append(
                "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing>" +
                $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
                $"<wp:docPr id=\"{n}\" name=\"Halaman {n}\"/>" +
                "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
                "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                $"<pic:nvPicPr><pic:cNvPr id=\"{n}\" name=\"image{n}.jpeg\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                $"<pic:blipFill><a:blip r:embed=\"rIdImg{n}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>" +
                "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>" +
                "</a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>"
            );
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            (int pgW, int pgH) = _pageSize switch
            {
                "Letter" => (12240, 15840),
                "F4" => (11907, 18720),
                _ => (11906, 16838)
            };
            int margin = _marginMode switch
            {
                1 => 680,   // Sempit ±1,2 cm
                2 => 2154,  // Lebar ±3,8 cm
                _ => 1417   // Normal ±2,5 cm
            };
            string doc = XML +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                $"<w:body>{_body}<w:sectPr><w:pgSz w:w=\"{pgW}\" w:h=\"{pgH}\"/>" +
                $"<w:pgMar w:top=\"{margin}\" w:right=\"{margin}\" w:bottom=\"{margin}\" w:left=\"{margin}\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/>" +
                "</w:sectPr></w:body></w:document>";
            string docRels = XML +
                $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{_rels}</Relationships>";
            Put("word/document.xml", Encoding.UTF8.GetBytes(doc));
            Put("word/_rels/document.xml.rels", Encoding.UTF8.GetBytes(docRels));
            _zip.Dispose();
            _fs.Dispose();
        }

        public void Dispose()
        {
            if (!_finished)
            {
                try { _zip.Dispose(); } catch { }
                try { _fs.Dispose(); } catch { }
            }
        }
    }
}
