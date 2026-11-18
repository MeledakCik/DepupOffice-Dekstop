using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core.Office
{
    // =============================================================================================
    // Mesin dokumen Word (.docx) untuk editor (port DocxEngine.kt Android v4.1).
    //
    // Prinsip: berkas asli TIDAK ditulis ulang dari nol. document.xml dibaca sebagai token XML mentah; saat menyimpan,
    // hanya bagian yang benar-benar diubah pengguna yang disunting (teks, format karakter/paragraf, paragraf/tabel/gambar
    // baru atau yang dihapus, pengaturan halaman). Tema, header/footer, kotak teks, field, bookmark, komentar, tabel
    // kompleks, dsb. keluar persis seperti aslinya.
    // =============================================================================================

    /// <summary>Format karakter langsung (null = ikut gaya). Ukuran dalam setengah-poin seperti Word.</summary>
    public sealed record CharFmt(bool? B = null, bool? I = null, bool? U = null, bool? S = null, int? Color = null, int? Bg = null,
        int? Sz = null, string? Font = null, int? Va = null)
    {
        public const int AUTO = 0x00000001;
        public const int NONE = 0x00000000;
        public static readonly CharFmt EMPTY = new();

        public CharFmt Over(CharFmt? o) => o == null ? this : new CharFmt(o.B ?? B, o.I ?? I, o.U ?? U, o.S ?? S, o.Color ?? Color, o.Bg ?? Bg, o.Sz ?? Sz, o.Font ?? Font, o.Va ?? Va);
    }

    /// <summary>Format paragraf langsung (null = ikut gaya). Twip. FirstLine negatif = gantung. Align: 0 kiri,1 tengah,2 kanan,3 rata.</summary>
    public sealed record ParaFmt(string? Style = null, int? Align = null, int? Line = null, string? LineRule = null, int? Before = null, int? After = null,
        int? Left = null, int? Right = null, int? FirstLine = null, int? NumId = null, int? Ilvl = null)
    {
        public ParaFmt Over(ParaFmt o) => new(o.Style ?? Style, o.Align ?? Align, o.Line ?? Line, o.Line != null ? o.LineRule : LineRule,
            o.Before ?? Before, o.After ?? After, o.Left ?? Left, o.Right ?? Right, o.FirstLine ?? FirstLine, o.NumId ?? NumId, o.NumId != null ? o.Ilvl : Ilvl);
    }

    public sealed record PageSetup(int W, int H, bool Landscape, int Top, int Right, int Bottom, int Left)
    {
        public int TextWidth => Math.Max(1440, W - Left - Right);
    }

    // ------------------------------------------------------------------ model editor
    public abstract class EBlock { public abstract EBlock DeepCopy(); }

    public sealed record ImgRef(string RId, int RunStart, int RunEnd, long Cx, long Cy);

    public sealed class EPara : EBlock
    {
        /// <summary>id paragraf asli; -1 = paragraf baru.</summary>
        public readonly int Origin;
        public string Text;
        public CharFmt[] Fmts;
        public ParaFmt Pf;
        /// <summary>Paragraf asli acuan format untuk paragraf baru (mis. hasil Enter).</summary>
        public int Tpl;
        public List<ImgRef> Images = new();
        public bool HasObject, PageBreak;
        /// <summary>Paragraf pembawa pemisah bagian (section) tidak boleh dihapus.</summary>
        public bool Locked;

        public EPara(int origin, string text, CharFmt[] fmts, ParaFmt pf, int? tpl = null)
        { Origin = origin; Text = text; Fmts = fmts; Pf = pf; Tpl = tpl ?? origin; }

        public override EPara DeepCopy() => new(Origin, Text, (CharFmt[])Fmts.Clone(), Pf, Tpl)
        { Images = new List<ImgRef>(Images), HasObject = HasObject, PageBreak = PageBreak, Locked = Locked };

        public static EPara Blank(int tpl, ParaFmt pf, CharFmt? fmt = null, string text = "")
            => new(-1, text, Enumerable.Repeat(fmt ?? CharFmt.EMPTY, text.Length).ToArray(), pf, tpl);
    }

    public sealed class ECell
    {
        public readonly int Origin; public readonly List<EPara> Paras; public readonly int Span; public readonly bool VCont; public readonly int? Fill;
        /// <summary>Sel asli acuan properti (tcPr) untuk sel baru.</summary>
        public readonly int TplCell;
        /// <summary>Sel berisi tabel bersarang: hanya teks/format yang bisa diubah, bukan jumlah paragraf.</summary>
        public readonly bool Locked;
        public ECell(int origin, List<EPara> paras, int span = 1, bool vCont = false, int? fill = null, int? tplCell = null, bool locked = false)
        { Origin = origin; Paras = paras; Span = span; VCont = vCont; Fill = fill; TplCell = tplCell ?? origin; Locked = locked; }
        public ECell DeepCopy() => new(Origin, Paras.Select(p => p.DeepCopy()).ToList(), Span, VCont, Fill, TplCell, Locked);
    }

    public sealed class ERow
    {
        public readonly int Origin; public readonly List<ECell> Cells; public readonly int TplRow;
        public ERow(int origin, List<ECell> cells, int? tplRow = null) { Origin = origin; Cells = cells; TplRow = tplRow ?? origin; }
        public ERow DeepCopy() => new(Origin, Cells.Select(c => c.DeepCopy()).ToList(), TplRow);
    }

    public sealed class ETable : EBlock
    {
        public readonly int Origin; public readonly List<ERow> Rows; public List<int> Grid;
        /// <summary>Tanpa sel gabungan: baris/kolom boleh ditambah/dihapus.</summary>
        public readonly bool Simple; public readonly bool Bordered;
        public ETable(int origin, List<ERow> rows, List<int> grid, bool simple, bool bordered = true) { Origin = origin; Rows = rows; Grid = grid; Simple = simple; Bordered = bordered; }
        public override ETable DeepCopy() => new(Origin, Rows.Select(r => r.DeepCopy()).ToList(), new List<int>(Grid), Simple, Bordered);
    }

    /// <summary>Gambar baru yang disisipkan pengguna (satu paragraf rata tengah).</summary>
    public sealed class EImage : EBlock
    {
        public readonly byte[] Bytes; public readonly string Ext; public readonly int WPx, HPx; public float WidthFrac;
        public EImage(byte[] bytes, string ext, int wPx, int hPx, float widthFrac = 0.6f) { Bytes = bytes; Ext = ext; WPx = wPx; HPx = hPx; WidthFrac = widthFrac; }
        public override EImage DeepCopy() => new(Bytes, Ext, WPx, HPx, WidthFrac);
    }

    /// <summary>Pemisah halaman baru.</summary>
    public sealed class EBreak : EBlock { public override EBreak DeepCopy() => new(); }

    public enum StdStyle { Normal, Title, Subtitle, H1, H2, H3, Quote }

    public static class StdStyles
    {
        public static string Label(StdStyle s) => s switch
        {
            StdStyle.Title => "Judul Dokumen", StdStyle.Subtitle => "Subjudul", StdStyle.H1 => "Judul 1", StdStyle.H2 => "Judul 2", StdStyle.H3 => "Judul 3", StdStyle.Quote => "Kutipan", _ => "Normal"
        };
        public static string WordName(StdStyle s) => s switch
        {
            StdStyle.Title => "Title", StdStyle.Subtitle => "Subtitle", StdStyle.H1 => "heading 1", StdStyle.H2 => "heading 2", StdStyle.H3 => "heading 3", StdStyle.Quote => "Quote", _ => "Normal"
        };
    }

    // =============================================================================================
    // Dokumen
    // =============================================================================================
    public sealed class DocxDocument
    {
        public const string REL_STYLES = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
        public const string REL_NUMBERING = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering";
        public const string REL_IMAGE = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";

        private readonly string _src;
        private readonly string _mainPart;
        private readonly string _docXml;
        private readonly Encoding _docEnc;
        public readonly List<EBlock> Body = new();
        public Styles Styles { get; private set; } = null!;
        public Numbering Numbering { get; private set; } = null!;
        public PageSetup? Page;
        private PageSetup? _origPage;
        private readonly Dictionary<string, string> _rels = new();
        private string? _stylesPart, _numberingPart;
        private string MainDir => _mainPart.Contains('/') ? _mainPart[.._mainPart.LastIndexOf('/')] : "";

        private DocxDocument(string src, string mainPart, string docXml, Encoding enc) { _src = src; _mainPart = mainPart; _docXml = docXml; _docEnc = enc; }

        /// <summary>null bila bukan .docx yang valid.</summary>
        public static DocxDocument? Open(string src)
        {
            try
            {
                using var z = ZipFile.OpenRead(src);
                var main = FindMainPart(z);
                var bytes = ReadBytes(z, main); if (bytes == null) return null;
                var enc = EncodingOf(bytes);
                var d = new DocxDocument(src, main, Decode(bytes, enc), enc);
                d.Load(z);
                return d;
            }
            catch { return null; }
        }

        internal static Encoding EncodingOf(byte[] b) =>
            b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode :
            b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode : new UTF8Encoding(false);

        private static string Decode(byte[] b, Encoding enc)
        {
            var s = enc.GetString(b);
            return s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;
        }

        private static ZipArchiveEntry? Entry(ZipArchive z, string name) =>
            z.GetEntry(name) ?? z.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), name, StringComparison.OrdinalIgnoreCase));

        private static byte[]? ReadBytes(ZipArchive z, string? name)
        {
            if (name == null) return null;
            var e = Entry(z, name); if (e == null) return null;
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }

        private static string? ReadPart(ZipArchive z, string? name) { var b = ReadBytes(z, name); return b == null ? null : Decode(b, EncodingOf(b)); }

        private static string FindMainPart(ZipArchive z)
        {
            var r = ReadPart(z, "_rels/.rels");
            if (r != null)
                foreach (var t in XmlLite.Tokenize(r))
                    if ((t.Kind == XTok.EMPTY || t.Kind == XTok.START) && XmlLite.Local(t.Name) == "Relationship")
                    {
                        var type = XmlLite.Attr(t.Raw, "Type") ?? "";
                        if (type.EndsWith("/officeDocument", StringComparison.Ordinal))
                        {
                            var target = XmlLite.Attr(t.Raw, "Target")?.TrimStart('/');
                            if (target != null && Entry(z, target) != null) return target;
                        }
                    }
            return "word/document.xml";
        }

        internal static string Resolve(string dir, string target) => OPackage.Resolve(dir, target);

        private string RelsPath() => (MainDir.Length == 0 ? "" : MainDir + "/") + "_rels/" + _mainPart[(_mainPart.LastIndexOf('/') + 1)..] + ".rels";

        private void Load(ZipArchive z)
        {
            var r = ReadPart(z, RelsPath());
            if (r != null)
                foreach (var t in XmlLite.Tokenize(r))
                {
                    if (!((t.Kind == XTok.EMPTY || t.Kind == XTok.START) && XmlLite.Local(t.Name) == "Relationship")) continue;
                    var id = XmlLite.Attr(t.Raw, "Id"); var target = XmlLite.Attr(t.Raw, "Target");
                    if (id == null || target == null) continue;
                    var type = XmlLite.Attr(t.Raw, "Type") ?? "";
                    if (XmlLite.Attr(t.Raw, "TargetMode") == "External") continue;
                    _rels[id] = target;
                    if (type == REL_STYLES) _stylesPart = Resolve(MainDir, target);
                    if (type == REL_NUMBERING) _numberingPart = Resolve(MainDir, target);
                }
            Styles = Styles.Parse(ReadPart(z, _stylesPart));
            Numbering = Numbering.Parse(ReadPart(z, _numberingPart));
            var p = new Parsed(_docXml);
            Page = p.PageSetup();
            _origPage = Page;
            Body.AddRange(p.BuildModel(Styles));
        }

        // ---------------------------------------------------------------- layanan editor
        public byte[]? ImageBytes(string rId)
        {
            if (!_rels.TryGetValue(rId, out var target)) return null;
            try { using var z = ZipFile.OpenRead(_src); return ReadBytes(z, Resolve(MainDir, target)); } catch { return null; }
        }

        /// <summary>id gaya untuk gaya bawaan; bila belum ada, definisinya ditambahkan saat simpan.</summary>
        public string? StyleId(StdStyle s)
        {
            if (s == StdStyle.Normal) return Styles.DefaultPara ?? Styles.ByName("Normal");
            return Styles.ByName(StdStyles.WordName(s)) ?? Styles.AddStd(s);
        }

        /// <summary>Gaya paragraf efektif (gaya + format langsung).</summary>
        public ParaFmt EffPara(ParaFmt pf)
        {
            var b = Styles.ParaBase(pf.Style);
            var e = b.Over(pf);
            var nid = e.NumId;
            if (nid is > 0 && pf.Left == null && pf.FirstLine == null && Numbering.Level(nid.Value, e.Ilvl ?? 0) is { } l && b.Left == null)
                e = e with { Left = l.Left ?? e.Left, FirstLine = l.FirstLine ?? e.FirstLine };
            return e;
        }

        /// <summary>Format karakter dasar sebuah paragraf (dari gaya), semua nilai terisi.</summary>
        public CharFmt RunBase(ParaFmt pf) => Styles.RunBase(pf.Style);
        public int NumIdFor(bool bullet) => Numbering.EnsureOwn(bullet);
        public bool IsBulletList(int? numId) => numId is > 0 && Numbering.IsBullet(numId.Value);

        public List<EPara> AllParas()
        {
            var outL = new List<EPara>();
            foreach (var b in Body)
            {
                if (b is EPara p) outL.Add(p);
                else if (b is ETable t) foreach (var r in t.Rows) foreach (var c in r.Cells) outL.AddRange(c.Paras);
            }
            return outL;
        }

        /// <summary>Label nomor/poin daftar untuk tiap paragraf.</summary>
        public Dictionary<EPara, string> ListLabels()
        {
            var ps = AllParas();
            var labels = Numbering.Labels(ps.Select(p => EffPara(p.Pf)).ToList());
            var outD = new Dictionary<EPara, string>();
            for (int i = 0; i < ps.Count; i++) if (labels[i] is { } s) outD[ps[i]] = s;
            return outD;
        }

        internal bool PageChanged() => Page != null && Page != _origPage;

        // ---------------------------------------------------------------- simpan
        public void Save(string outPath)
        {
            var parts = new PartWriter();
            var p = new Parsed(_docXml);
            new Patcher(p, this, parts).Apply();
            var newDoc = p.Render();
            var stylesAdd = Styles.PendingXml();
            var numAdd = Numbering.PendingXml();

            var tmp = outPath + ".part";
            try
            {
                using (var z = ZipFile.OpenRead(_src))
                {
                    var existing = new HashSet<string>(z.Entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
                    var ct = ReadPart(z, "[Content_Types].xml") ?? "";
                    var ctNew = ct;
                    var relsOld = ReadPart(z, RelsPath());
                    var relsNew = relsOld ?? "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"></Relationships>";
                    var replace = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                    var added = new List<(string, byte[])>();
                    var utf8 = new UTF8Encoding(false);
                    replace[_mainPart] = _docEnc.GetBytes(newDoc);
                    string md = MainDir.Length == 0 ? "" : MainDir + "/";
                    if (stylesAdd != null)
                    {
                        if (_stylesPart != null && existing.Contains(_stylesPart)) replace[_stylesPart] = utf8.GetBytes(Styles.Inject(ReadPart(z, _stylesPart)!, stylesAdd));
                        else
                        {
                            added.Add((md + "styles.xml", utf8.GetBytes(Styles.Fresh(stylesAdd))));
                            ctNew = PartWriter.AddOverride(ctNew, "/" + md + "styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
                            relsNew = PartWriter.AddRel(relsNew, PartWriter.FreeId(relsNew, "rIdDpStyles"), REL_STYLES, "styles.xml");
                        }
                    }
                    if (numAdd != null)
                    {
                        if (_numberingPart != null && existing.Contains(_numberingPart)) replace[_numberingPart] = utf8.GetBytes(Numbering.Inject(ReadPart(z, _numberingPart)!, numAdd.Value));
                        else
                        {
                            added.Add((md + "numbering.xml", utf8.GetBytes(Numbering.Fresh(numAdd.Value))));
                            ctNew = PartWriter.AddOverride(ctNew, "/" + md + "numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml");
                            relsNew = PartWriter.AddRel(relsNew, PartWriter.FreeId(relsNew, "rIdDpNumbering"), REL_NUMBERING, "numbering.xml");
                        }
                    }
                    foreach (var m in parts.Media)
                    {
                        added.Add((md + "media/" + m.File, m.Bytes));
                        relsNew = PartWriter.AddRel(relsNew, m.RId, REL_IMAGE, "media/" + m.File);
                        ctNew = PartWriter.AddDefault(ctNew, m.Ext, m.Ext == "png" ? "image/png" : "image/jpeg");
                    }
                    if (ctNew != ct) replace["[Content_Types].xml"] = utf8.GetBytes(ctNew);
                    var rp = RelsPath();
                    if (relsOld == null) { if (parts.Media.Count > 0 || stylesAdd != null || numAdd != null) added.Add((rp, utf8.GetBytes(relsNew))); }
                    else if (relsNew != relsOld) replace[rp] = utf8.GetBytes(relsNew);

                    using var fs = File.Create(tmp);
                    using var zo = new ZipArchive(fs, ZipArchiveMode.Create);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in z.Entries)
                    {
                        if (!seen.Add(e.FullName)) continue;
                        var ne = zo.CreateEntry(e.FullName, CompressionLevel.Optimal);
                        if (e.LastWriteTime.Year > 1980) ne.LastWriteTime = e.LastWriteTime;
                        using var os = ne.Open();
                        if (replace.TryGetValue(e.FullName.Replace('\\', '/').TrimStart('/'), out var rep)) os.Write(rep, 0, rep.Length);
                        else if (!e.FullName.EndsWith('/')) { using var ins = e.Open(); ins.CopyTo(os); }
                    }
                    foreach (var (n, b) in added)
                    {
                        if (!seen.Add(n)) continue;
                        using var os = zo.CreateEntry(n, CompressionLevel.Optimal).Open();
                        os.Write(b, 0, b.Length);
                    }
                }
                File.Move(tmp, outPath, true);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        /// <summary>Dokumen kosong baru (A4, margin 2,54 cm) yang bisa langsung diedit lalu disimpan.</summary>
        public static DocxDocument? CreateBlank(string path)
        {
            using (var fs = File.Create(path))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Put(string n, string x) { using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false)); w.Write(x); }
                Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                Put("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p/><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
            }
            return Open(path);
        }
    }

    // =============================================================================================
    // Tokenizer XML ringan (menyimpan teks mentah tiap token)
    // =============================================================================================
    internal sealed class XTok
    {
        public const int START = 0, END = 1, EMPTY = 2, TEXT = 3, OTHER = 4;
        public readonly int Kind; public string Raw; public readonly string Name;
        public XTok(int kind, string raw, string name) { Kind = kind; Raw = raw; Name = name; }
    }

    internal readonly record struct Rng(int First, int Last)
    {
        public IEnumerable<int> All() { for (int i = First; i <= Last; i++) yield return i; }
    }

    internal static class XmlLite
    {
        private static readonly Regex ATTR = new("([A-Za-z_][\\w:.-]*)\\s*=\\s*(\"([^\"]*)\"|'([^']*)')", RegexOptions.Compiled);
        private static readonly Dictionary<string, Regex> LocalAttr = new();

        public static List<XTok> Tokenize(string s)
        {
            var outL = new List<XTok>(s.Length / 12 + 16);
            int i = 0, n = s.Length;
            while (i < n)
            {
                if (s[i] != '<')
                {
                    int j = s.IndexOf('<', i); if (j < 0) j = n;
                    outL.Add(new XTok(XTok.TEXT, s[i..j], ""));
                    i = j; continue;
                }
                int end;
                if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0) { end = Idx(s, "-->", i, n) + 3; outL.Add(new XTok(XTok.OTHER, s[i..Math.Min(end, n)], "")); }
                else if (string.CompareOrdinal(s, i, "<![CDATA[", 0, 9) == 0) { end = Idx(s, "]]>", i, n) + 3; outL.Add(new XTok(XTok.TEXT, s[i..Math.Min(end, n)], "")); }
                else if (string.CompareOrdinal(s, i, "<?", 0, 2) == 0) { end = Idx(s, "?>", i, n) + 2; outL.Add(new XTok(XTok.OTHER, s[i..Math.Min(end, n)], "")); }
                else if (string.CompareOrdinal(s, i, "<!", 0, 2) == 0) { end = Idx(s, ">", i, n) + 1; outL.Add(new XTok(XTok.OTHER, s[i..Math.Min(end, n)], "")); }
                else
                {
                    int j = i + 1; char q = '\0';
                    while (j < n)
                    {
                        char c = s[j];
                        if (q != '\0') { if (c == q) q = '\0'; }
                        else if (c == '"' || c == '\'') q = c;
                        else if (c == '>') break;
                        j++;
                    }
                    end = Math.Min(j + 1, n);
                    var raw = s[i..end];
                    if (raw.StartsWith("</", StringComparison.Ordinal)) outL.Add(new XTok(XTok.END, raw, raw[2..^1].Trim()));
                    else
                    {
                        int k = 1;
                        while (k < raw.Length && !char.IsWhiteSpace(raw[k]) && raw[k] != '/' && raw[k] != '>') k++;
                        outL.Add(new XTok(raw.EndsWith("/>", StringComparison.Ordinal) ? XTok.EMPTY : XTok.START, raw, raw[1..k]));
                    }
                }
                i = Math.Min(end, n);
            }
            return outL;
        }

        private static int Idx(string s, string what, int from, int n) { int i = s.IndexOf(what, from, StringComparison.Ordinal); return i < 0 ? n - what.Length : i; }

        /// <summary>Atribut tag sebagai peta berurutan (nilai masih ter-escape).</summary>
        public static List<KeyValuePair<string, string>> Attrs(string raw)
        {
            var m = new List<KeyValuePair<string, string>>();
            var body = raw.TrimStart('<');
            if (body.EndsWith('>')) body = body[..^1];
            if (body.EndsWith('/')) body = body[..^1];
            int sp = -1; for (int i = 0; i < body.Length; i++) if (char.IsWhiteSpace(body[i])) { sp = i; break; }
            if (sp < 0) return m;
            foreach (Match mt in ATTR.Matches(body[sp..]))
                m.Add(new(mt.Groups[1].Value, mt.Groups[3].Success ? mt.Groups[3].Value : mt.Groups[4].Value));
            return m;
        }

        public static void Put(List<KeyValuePair<string, string>> m, string k, string v)
        {
            int i = m.FindIndex(x => x.Key == k);
            if (i >= 0) m[i] = new(k, v); else m.Add(new(k, v));
        }
        public static void Remove(List<KeyValuePair<string, string>> m, string k) => m.RemoveAll(x => x.Key == k);
        public static bool Has(List<KeyValuePair<string, string>> m, string k) => m.Any(x => x.Key == k);

        public static string Tag(string name, List<KeyValuePair<string, string>> attrs, bool empty)
        {
            var sb = new StringBuilder("<").Append(name);
            foreach (var kv in attrs) sb.Append(' ').Append(kv.Key).Append("=\"").Append(kv.Value).Append('"');
            return sb.Append(empty ? "/>" : ">").ToString();
        }

        public static string? Attr(string raw, string qname)
        {
            int from = 0;
            while (true)
            {
                int i = raw.IndexOf(qname, from, StringComparison.Ordinal);
                if (i < 0) return null;
                from = i + qname.Length;
                if (i == 0 || !char.IsWhiteSpace(raw[i - 1])) continue;
                int j = from;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                if (j >= raw.Length || raw[j] != '=') continue;
                j++;
                while (j < raw.Length && char.IsWhiteSpace(raw[j])) j++;
                if (j >= raw.Length) return null;
                char q = raw[j];
                if (q != '"' && q != '\'') continue;
                int e = raw.IndexOf(q, j + 1);
                if (e < 0) return null;
                return DecodeText(raw.Substring(j + 1, e - j - 1));
            }
        }

        /// <summary>Atribut dengan nama lokal tertentu, apa pun prefiksnya (mis. r:embed).</summary>
        public static string? AttrLocal(string raw, string local)
        {
            Regex re;
            lock (LocalAttr)
                if (!LocalAttr.TryGetValue(local, out re!))
                    LocalAttr[local] = re = new Regex("\\s([\\w.-]+:)?" + Regex.Escape(local) + "\\s*=\\s*(\"([^\"]*)\"|'([^']*)')", RegexOptions.Compiled);
            var m = re.Match(raw);
            if (!m.Success) return null;
            return DecodeText(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        }

        public static string DecodeText(string s)
        {
            if (s.StartsWith("<![CDATA[", StringComparison.Ordinal)) { s = s[9..]; return s.EndsWith("]]>", StringComparison.Ordinal) ? s[..^3] : s; }
            return XmlDom.Unescape(s);
        }

        public static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') break;
                        if (c == '\uFFFE' || c == '\uFFFF') break;
                        sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Local(string name) { int i = name.IndexOf(':'); return i >= 0 ? name[(i + 1)..] : name; }
        public static string Prefix(string name) { int i = name.IndexOf(':'); return i >= 0 ? name[..i] : ""; }

        /// <summary>Anak langsung sebuah elemen (potongan XML mentah).</summary>
        public sealed class Elem
        {
            public readonly string Open, Close, Name;
            public readonly List<KeyValuePair<string, string>> Children;
            public Elem(string open, string close, List<KeyValuePair<string, string>> children, string name) { Open = open; Close = close; Children = children; Name = name; }
        }

        public static Elem? ParseElem(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var toks = Tokenize(raw);
            var first = toks.FirstOrDefault(t => t.Kind == XTok.START || t.Kind == XTok.EMPTY);
            if (first == null) return null;
            if (first.Kind == XTok.EMPTY) return new Elem(first.Raw[..^2].TrimEnd() + ">", "</" + first.Name + ">", new(), first.Name);
            var kids = new List<KeyValuePair<string, string>>();
            int depth = 0; StringBuilder? cur = null; string curName = ""; string close = "</" + first.Name + ">";
            foreach (var t in toks)
            {
                if (ReferenceEquals(t, first)) { depth = 1; continue; }
                if (depth == 0) continue;
                switch (t.Kind)
                {
                    case XTok.START:
                        if (depth == 1) { cur = new StringBuilder(); curName = t.Name; }
                        cur?.Append(t.Raw); depth++; break;
                    case XTok.EMPTY:
                        if (depth == 1) kids.Add(new(t.Name, t.Raw)); else cur?.Append(t.Raw); break;
                    case XTok.END:
                        depth--;
                        if (depth == 0) { close = t.Raw; continue; }
                        cur?.Append(t.Raw);
                        if (depth == 1 && cur != null) { kids.Add(new(curName, cur.ToString())); cur = null; }
                        break;
                    default:
                        if (depth > 1) cur?.Append(t.Raw); break;
                }
            }
            return new Elem(first.Raw, close, kids, first.Name);
        }
    }

    // =============================================================================================
    // Format <-> XML
    // =============================================================================================
    internal static class Fmt
    {
        public static bool IsOff(string? v) => v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase) || v == "none";
        public static string Q(string w, string l) => w.Length == 0 ? l : w + ":" + l;
        public static string Hex(int c) => (c & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
        public static int? ParseHex(string? v)
        {
            if (v == null || v.Length != 6 || v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? unchecked((int)0xFF000000) | x : null;
        }

        public static readonly List<KeyValuePair<string, int>> HIGHLIGHT = new()
        {
            new("yellow", unchecked((int)0xFFFFFF00)), new("green", unchecked((int)0xFF00FF00)), new("cyan", unchecked((int)0xFF00FFFF)),
            new("magenta", unchecked((int)0xFFFF00FF)), new("blue", unchecked((int)0xFF0000FF)), new("red", unchecked((int)0xFFFF0000)),
            new("darkBlue", unchecked((int)0xFF000080)), new("darkCyan", unchecked((int)0xFF008080)), new("darkGreen", unchecked((int)0xFF008000)),
            new("darkMagenta", unchecked((int)0xFF800080)), new("darkRed", unchecked((int)0xFF800000)), new("darkYellow", unchecked((int)0xFF808000)),
            new("darkGray", unchecked((int)0xFF808080)), new("lightGray", unchecked((int)0xFFC0C0C0)), new("black", unchecked((int)0xFF000000)), new("white", unchecked((int)0xFFFFFFFF))
        };

        public static int? JcOf(string? v) => v switch { "center" => 1, "right" or "end" => 2, "both" or "distribute" or "justify" => 3, "left" or "start" => 0, _ => null };

        private static int? Num(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;

        public static (CharFmt Fmt, string? RStyle) ParseRPr(List<XTok> toks, Rng? range, string w)
        {
            if (range == null) return (CharFmt.EMPTY, null);
            var f = CharFmt.EMPTY; string? rStyle = null; int depth = 0;
            foreach (int k in range.Value.All())
            {
                var t = toks[k];
                if (t.Kind == XTok.START || t.Kind == XTok.EMPTY)
                {
                    if (depth == 1 && XmlLite.Prefix(t.Name) == w)
                    {
                        string? V() => XmlLite.Attr(t.Raw, Q(w, "val"));
                        switch (XmlLite.Local(t.Name))
                        {
                            case "rStyle": rStyle = V(); break;
                            case "b": f = f with { B = !IsOff(V()) }; break;
                            case "i": f = f with { I = !IsOff(V()) }; break;
                            case "strike": f = f with { S = !IsOff(V()) }; break;
                            case "u": { var v = V(); f = f with { U = v == null || v != "none" }; break; }
                            case "color": { var v = V(); f = f with { Color = ParseHex(v) ?? (v == "auto" ? CharFmt.AUTO : f.Color) }; break; }
                            case "sz": if (Num(V()) is { } sz) f = f with { Sz = sz }; break;
                            case "rFonts": if ((XmlLite.Attr(t.Raw, Q(w, "ascii")) ?? XmlLite.Attr(t.Raw, Q(w, "hAnsi"))) is { } font) f = f with { Font = font }; break;
                            case "highlight":
                                {
                                    var v = V();
                                    int? hv = v == null ? null : HIGHLIGHT.Where(h => h.Key == v).Select(h => (int?)h.Value).FirstOrDefault();
                                    f = f with { Bg = v == "none" ? CharFmt.NONE : hv ?? f.Bg }; break;
                                }
                            case "shd": if (f.Bg == null && ParseHex(XmlLite.Attr(t.Raw, Q(w, "fill"))) is { } fill) f = f with { Bg = fill }; break;
                            case "vertAlign": f = f with { Va = V() switch { "superscript" => 1, "subscript" => 2, _ => 0 } }; break;
                        }
                    }
                    if (t.Kind == XTok.START) depth++;
                }
                else if (t.Kind == XTok.END) depth--;
            }
            return (f, rStyle);
        }

        public static (ParaFmt Fmt, bool Sect) ParsePPr(List<XTok> toks, Rng? range, string w)
        {
            if (range == null) return (new ParaFmt(), false);
            var f = new ParaFmt(); bool sect = false; int depth = 0; bool inNum = false;
            foreach (int k in range.Value.All())
            {
                var t = toks[k];
                if (t.Kind == XTok.START || t.Kind == XTok.EMPTY)
                {
                    var l = XmlLite.Local(t.Name);
                    bool isW = XmlLite.Prefix(t.Name) == w;
                    if (isW && depth == 1)
                    {
                        string? A(string n) => XmlLite.Attr(t.Raw, Q(w, n));
                        switch (l)
                        {
                            case "pStyle": f = f with { Style = A("val") }; break;
                            case "jc": f = f with { Align = JcOf(A("val")) }; break;
                            case "spacing":
                                f = f with { Before = Num(A("before")) ?? f.Before, After = Num(A("after")) ?? f.After, Line = Num(A("line")) ?? f.Line, LineRule = A("line") != null ? (A("lineRule") ?? "auto") : f.LineRule };
                                break;
                            case "ind":
                                {
                                    int? left = Num(A("left") ?? A("start")), right = Num(A("right") ?? A("end")), first = Num(A("firstLine")), hang = Num(A("hanging"));
                                    f = f with { Left = left ?? f.Left, Right = right ?? f.Right, FirstLine = hang != null ? -hang : first ?? f.FirstLine };
                                    break;
                                }
                            case "sectPr": sect = true; break;
                            case "numPr": inNum = t.Kind == XTok.START; if (!inNum) f = f with { NumId = null }; break;
                        }
                    }
                    else if (isW && depth == 2 && inNum)
                    {
                        if (l == "numId") f = f with { NumId = Num(XmlLite.Attr(t.Raw, Q(w, "val"))) ?? 0 };
                        else if (l == "ilvl") f = f with { Ilvl = Num(XmlLite.Attr(t.Raw, Q(w, "val"))) };
                    }
                    if (t.Kind == XTok.START) depth++;
                }
                else if (t.Kind == XTok.END) { depth--; if (depth == 1) inNum = false; }
            }
            if (f.NumId != null && f.Ilvl == null) f = f with { Ilvl = 0 };
            return (f, sect);
        }

        private static readonly List<string> RPR_ORDER = new()
        {
            "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow",
            "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern",
            "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs",
            "em", "lang", "eastAsianLayout", "specVanish", "oMath"
        };
        private static readonly List<string> PPR_ORDER = new()
        {
            "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr",
            "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct",
            "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd", "snapToGrid", "spacing", "ind",
            "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc", "textDirection", "textAlignment",
            "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr", "sectPr"
        };

        private static int Order(List<string> list, string w, string name)
        {
            var l = XmlLite.Local(name);
            if (XmlLite.Prefix(name) != w) return 900;
            if (l is "rPrChange" or "pPrChange") return 1000;
            int i = list.IndexOf(l);
            return i < 0 ? 500 : i;
        }

        private static string Emit(XmlLite.Elem e, List<string> list, string w)
        {
            if (e.Children.Count == 0) return "";
            var sorted = e.Children.Select((c, i) => (c, i)).OrderBy(x => Order(list, w, x.c.Key)).ThenBy(x => x.i).Select(x => x.c);
            var sb = new StringBuilder(e.Open);
            foreach (var c in sorted) sb.Append(c.Value);
            return sb.Append(e.Close).ToString();
        }

        private static void Set(XmlLite.Elem e, string w, string l, string? xml)
        {
            e.Children.RemoveAll(c => c.Key == Q(w, l));
            if (xml != null) e.Children.Add(new(Q(w, l), xml));
        }

        private static string? GetRaw(XmlLite.Elem e, string w, string l) { foreach (var c in e.Children) if (c.Key == Q(w, l)) return c.Value; return null; }

        /// <summary>Ubah properti yang dikelola dari <paramref name="from"/> ke <paramref name="to"/>; properti lain dibiarkan.</summary>
        public static string RprWith(string raw, CharFmt from, CharFmt to, string w)
        {
            if (from == to) return raw;
            var e = XmlLite.ParseElem(raw) ?? new XmlLite.Elem("<" + Q(w, "rPr") + ">", "</" + Q(w, "rPr") + ">", new(), Q(w, "rPr"));
            string Tg(string l, string? v) => v == null ? "<" + Q(w, l) + "/>" : "<" + Q(w, l) + " " + Q(w, "val") + "=\"" + v + "\"/>";
            string? Onoff(string l, bool? v) => v == null ? null : v.Value ? Tg(l, null) : Tg(l, "0");
            if (from.B != to.B) { Set(e, w, "b", Onoff("b", to.B)); Set(e, w, "bCs", Onoff("bCs", to.B)); }
            if (from.I != to.I) { Set(e, w, "i", Onoff("i", to.I)); Set(e, w, "iCs", Onoff("iCs", to.I)); }
            if (from.U != to.U) Set(e, w, "u", to.U == null ? null : Tg("u", to.U.Value ? "single" : "none"));
            if (from.S != to.S) Set(e, w, "strike", Onoff("strike", to.S));
            if (from.Color != to.Color) Set(e, w, "color", to.Color == null ? null : Tg("color", to.Color == CharFmt.AUTO ? "auto" : Hex(to.Color.Value)));
            if (from.Sz != to.Sz) { Set(e, w, "sz", to.Sz == null ? null : Tg("sz", to.Sz.Value.ToString(CultureInfo.InvariantCulture))); Set(e, w, "szCs", to.Sz == null ? null : Tg("szCs", to.Sz.Value.ToString(CultureInfo.InvariantCulture))); }
            if (from.Va != to.Va) Set(e, w, "vertAlign", to.Va == null ? null : Tg("vertAlign", to.Va switch { 1 => "superscript", 2 => "subscript", _ => "baseline" }));
            if (from.Bg != to.Bg)
            {
                Set(e, w, "highlight", null); Set(e, w, "shd", null);
                if (to.Bg is { } c)
                {
                    string? name = c == CharFmt.NONE ? "none" : HIGHLIGHT.Where(h => h.Value == c).Select(h => h.Key).FirstOrDefault();
                    if (name != null) Set(e, w, "highlight", Tg("highlight", name));
                    else Set(e, w, "shd", "<" + Q(w, "shd") + " " + Q(w, "val") + "=\"clear\" " + Q(w, "color") + "=\"auto\" " + Q(w, "fill") + "=\"" + Hex(c) + "\"/>");
                }
            }
            if (from.Font != to.Font)
            {
                var old = GetRaw(e, w, "rFonts");
                var at = old != null ? XmlLite.Attrs(old) : new List<KeyValuePair<string, string>>();
                foreach (var k in new[] { "asciiTheme", "hAnsiTheme", "cstheme" }) XmlLite.Remove(at, Q(w, k));
                if (to.Font == null) foreach (var k in new[] { "ascii", "hAnsi", "cs" }) XmlLite.Remove(at, Q(w, k));
                else { var v = XmlLite.Esc(to.Font); XmlLite.Put(at, Q(w, "ascii"), v); XmlLite.Put(at, Q(w, "hAnsi"), v); XmlLite.Put(at, Q(w, "cs"), v); }
                Set(e, w, "rFonts", at.Count == 0 ? null : XmlLite.Tag(Q(w, "rFonts"), at, true));
            }
            return Emit(e, RPR_ORDER, w);
        }

        public static string PprWith(string raw, ParaFmt from, ParaFmt to, string w)
        {
            if (from == to) return raw;
            var e = XmlLite.ParseElem(raw) ?? new XmlLite.Elem("<" + Q(w, "pPr") + ">", "</" + Q(w, "pPr") + ">", new(), Q(w, "pPr"));
            string I(int v) => v.ToString(CultureInfo.InvariantCulture);
            if (from.Style != to.Style) Set(e, w, "pStyle", to.Style == null ? null : "<" + Q(w, "pStyle") + " " + Q(w, "val") + "=\"" + XmlLite.Esc(to.Style) + "\"/>");
            if (from.NumId != to.NumId || from.Ilvl != to.Ilvl)
                Set(e, w, "numPr", to.NumId == null ? null : "<" + Q(w, "numPr") + "><" + Q(w, "ilvl") + " " + Q(w, "val") + "=\"" + I(to.Ilvl ?? 0) + "\"/><" + Q(w, "numId") + " " + Q(w, "val") + "=\"" + I(to.NumId.Value) + "\"/></" + Q(w, "numPr") + ">");
            if (from.Align != to.Align)
                Set(e, w, "jc", to.Align == null ? null : "<" + Q(w, "jc") + " " + Q(w, "val") + "=\"" + (to.Align switch { 1 => "center", 2 => "right", 3 => "both", _ => "left" }) + "\"/>");
            if (from.Before != to.Before || from.After != to.After || from.Line != to.Line || from.LineRule != to.LineRule)
            {
                var at = GetRaw(e, w, "spacing") is { } sr ? XmlLite.Attrs(sr) : new List<KeyValuePair<string, string>>();
                void P(string k, int? v) { if (v == null) XmlLite.Remove(at, Q(w, k)); else XmlLite.Put(at, Q(w, k), I(v.Value)); }
                if (from.Before != to.Before) { P("before", to.Before); XmlLite.Remove(at, Q(w, "beforeAutospacing")); XmlLite.Remove(at, Q(w, "beforeLines")); }
                if (from.After != to.After) { P("after", to.After); XmlLite.Remove(at, Q(w, "afterAutospacing")); XmlLite.Remove(at, Q(w, "afterLines")); }
                if (from.Line != to.Line || from.LineRule != to.LineRule)
                {
                    P("line", to.Line);
                    if (to.Line == null) XmlLite.Remove(at, Q(w, "lineRule")); else XmlLite.Put(at, Q(w, "lineRule"), to.LineRule ?? "auto");
                }
                Set(e, w, "spacing", at.Count == 0 ? null : XmlLite.Tag(Q(w, "spacing"), at, true));
            }
            if (from.Left != to.Left || from.Right != to.Right || from.FirstLine != to.FirstLine)
            {
                var at = GetRaw(e, w, "ind") is { } ir ? XmlLite.Attrs(ir) : new List<KeyValuePair<string, string>>();
                var lk = XmlLite.Has(at, Q(w, "start")) ? "start" : "left";
                var rk = XmlLite.Has(at, Q(w, "end")) ? "end" : "right";
                if (from.Left != to.Left) { if (to.Left == null) XmlLite.Remove(at, Q(w, lk)); else XmlLite.Put(at, Q(w, lk), I(to.Left.Value)); XmlLite.Remove(at, Q(w, "leftChars")); XmlLite.Remove(at, Q(w, "startChars")); }
                if (from.Right != to.Right) { if (to.Right == null) XmlLite.Remove(at, Q(w, rk)); else XmlLite.Put(at, Q(w, rk), I(to.Right.Value)); XmlLite.Remove(at, Q(w, "rightChars")); XmlLite.Remove(at, Q(w, "endChars")); }
                if (from.FirstLine != to.FirstLine)
                {
                    foreach (var k in new[] { "firstLine", "hanging", "firstLineChars", "hangingChars" }) XmlLite.Remove(at, Q(w, k));
                    if (to.FirstLine is { } fl) { if (fl < 0) XmlLite.Put(at, Q(w, "hanging"), I(-fl)); else XmlLite.Put(at, Q(w, "firstLine"), I(fl)); }
                }
                Set(e, w, "ind", at.Count == 0 ? null : XmlLite.Tag(Q(w, "ind"), at, true));
            }
            return Emit(e, PPR_ORDER, w);
        }
    }

    internal static class MainNs
    {
        private const string NS = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private static readonly Regex Rx = new("xmlns(?::([A-Za-z0-9_.-]+))?\\s*=\\s*[\"']" + Regex.Escape(NS) + "[\"']", RegexOptions.Compiled);
        public static string Prefix(List<XTok> toks)
        {
            var root = toks.FirstOrDefault(t => t.Kind == XTok.START || t.Kind == XTok.EMPTY);
            if (root == null) return "w";
            var m = Rx.Match(root.Raw);
            if (m.Success) return m.Groups[1].Value;
            var p = XmlLite.Prefix(root.Name);
            return p.Length == 0 ? "w" : p;
        }
    }

    // =============================================================================================
    // Gaya (styles.xml)
    // =============================================================================================
    public sealed class Styles
    {
        internal sealed record Def(string Id, string Name, string Type, string? BasedOn, CharFmt Rp, ParaFmt Pf);

        private readonly string _w;
        private readonly Dictionary<string, Def> _defs = new();
        private readonly List<string> _defOrder = new();
        private CharFmt _defaults = CharFmt.EMPTY;
        private ParaFmt _defaultsPf = new();
        public string? DefaultPara { get; private set; }
        private readonly Dictionary<string, string> _pending = new();
        internal HashSet<string> Used = new();

        private static readonly CharFmt HARD = new(false, false, false, false, CharFmt.AUTO, CharFmt.NONE, 20, null, 0);

        private Styles(string w) { _w = w; }

        public static Styles Parse(string? xml)
        {
            if (xml == null) return new Styles("w");
            var toks = XmlLite.Tokenize(xml);
            var w = MainNs.Prefix(toks);
            var st = new Styles(w);
            var stack = new List<int>();
            int curStart = -1;
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                switch (t.Kind)
                {
                    case XTok.START:
                        stack.Add(i);
                        if (XmlLite.Local(t.Name) == "style") curStart = i;
                        break;
                    case XTok.END:
                        {
                            if (stack.Count == 0) break;
                            int s = stack[^1]; stack.RemoveAt(stack.Count - 1);
                            var l = XmlLite.Local(t.Name);
                            string? par = stack.Count > 0 ? XmlLite.Local(toks[stack[^1]].Name) : null;
                            if (l == "rPr" && par == "rPrDefault") st._defaults = Fmt.ParseRPr(toks, new Rng(s, i), w).Fmt;
                            if (l == "pPr" && par == "pPrDefault") st._defaultsPf = Fmt.ParsePPr(toks, new Rng(s, i), w).Fmt;
                            if (l == "style" && curStart >= 0) { st.ReadStyle(toks, new Rng(curStart, i)); curStart = -1; }
                            break;
                        }
                    case XTok.EMPTY:
                        if (XmlLite.Local(t.Name) == "style") st.ReadStyle(toks, new Rng(i, i));
                        break;
                }
            }
            return st;
        }

        public static string Inject(string xml, string add)
        {
            var toks = XmlLite.Tokenize(xml);
            var w = MainNs.Prefix(toks);
            int end = toks.FindLastIndex(t => t.Kind == XTok.END && XmlLite.Local(t.Name) == "styles");
            if (end < 0) return xml;
            var sb = new StringBuilder();
            for (int i = 0; i < toks.Count; i++) { if (i == end) sb.Append(add.Replace("w:", w.Length == 0 ? "" : w + ":")); sb.Append(toks[i].Raw); }
            return sb.ToString();
        }

        public static string Fresh(string add) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" + add + "</w:styles>";

        private void ReadStyle(List<XTok> toks, Rng r)
        {
            var open = toks[r.First];
            var type = XmlLite.Attr(open.Raw, Fmt.Q(_w, "type")) ?? "paragraph";
            var id = XmlLite.Attr(open.Raw, Fmt.Q(_w, "styleId")); if (id == null) return;
            bool isDefault = XmlLite.Attr(open.Raw, Fmt.Q(_w, "default")) is "1" or "true";
            string name = id; string? based = null;
            var rp = CharFmt.EMPTY; var pf = new ParaFmt();
            int depth = 0, sub = -1;
            foreach (int k in r.All())
            {
                var t = toks[k];
                if (t.Kind == XTok.START || t.Kind == XTok.EMPTY)
                {
                    if (depth == 1)
                    {
                        switch (XmlLite.Local(t.Name))
                        {
                            case "name": name = XmlLite.Attr(t.Raw, Fmt.Q(_w, "val")) ?? name; break;
                            case "basedOn": based = XmlLite.Attr(t.Raw, Fmt.Q(_w, "val")); break;
                        }
                        if (t.Kind == XTok.START) sub = k;
                    }
                    if (t.Kind == XTok.START) depth++;
                }
                else if (t.Kind == XTok.END)
                {
                    depth--;
                    if (depth == 1 && sub >= 0)
                    {
                        switch (XmlLite.Local(t.Name))
                        {
                            case "rPr": rp = Fmt.ParseRPr(toks, new Rng(sub, k), _w).Fmt; break;
                            case "pPr": pf = Fmt.ParsePPr(toks, new Rng(sub, k), _w).Fmt; break;
                        }
                        sub = -1;
                    }
                }
            }
            if (!_defs.ContainsKey(id)) _defOrder.Add(id);
            _defs[id] = new Def(id, name, type, based, rp, pf);
            if (isDefault && type == "paragraph") DefaultPara = id;
        }

        public string? ByName(string name)
        {
            foreach (var id in _defOrder) { var d = _defs[id]; if (d.Type == "paragraph" && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return d.Id; }
            var compact = name.Replace(" ", "");
            foreach (var id in _defOrder) { var d = _defs[id]; if (d.Type == "paragraph" && d.Id.Equals(compact, StringComparison.OrdinalIgnoreCase)) return d.Id; }
            return null;
        }

        public string Name(string? id) => _defs.TryGetValue(id ?? DefaultPara ?? "", out var d) ? d.Name : id ?? "Normal";

        /// <summary>Gaya paragraf yang ada di dokumen (id, nama) untuk galeri gaya.</summary>
        public List<(string Id, string Name)> ParagraphStyles() => _defOrder.Select(i => _defs[i]).Where(d => d.Type == "paragraph").Select(d => (d.Id, d.Name)).ToList();

        private List<Def> Chain(string? id)
        {
            var outL = new List<Def>();
            var cur = id ?? DefaultPara; int guard = 0;
            while (cur != null && guard++ < 20) { if (!_defs.TryGetValue(cur, out var d)) break; outL.Insert(0, d); cur = d.BasedOn; }
            return outL;
        }

        public CharFmt RunBase(string? styleId)
        {
            var f = HARD.Over(_defaults);
            foreach (var d in Chain(styleId)) f = f.Over(d.Rp);
            return f;
        }

        public ParaFmt ParaBase(string? styleId)
        {
            var f = new ParaFmt(Align: 0, Line: 240, LineRule: "auto", Before: 0, After: 0).Over(_defaultsPf);
            foreach (var d in Chain(styleId)) f = f.Over(d.Pf);
            return f with { Style = styleId };
        }

        public CharFmt CharStyle(string? id)
        {
            if (id == null) return CharFmt.EMPTY;
            var f = CharFmt.EMPTY;
            foreach (var d in Chain(id)) f = f.Over(d.Rp);
            return f;
        }

        public int HeadingLevel(string? id)
        {
            var ch = Chain(id); ch.Reverse();
            foreach (var d in ch)
            {
                var n = d.Name.ToLowerInvariant();
                if (n == "title") return 1;
                if (n.StartsWith("heading", StringComparison.Ordinal)) return int.TryParse(n[7..].Trim(), out var lv) ? lv : 1;
            }
            return 0;
        }

        internal string AddStd(StdStyle s)
        {
            var baseId = s switch { StdStyle.Title => "Title", StdStyle.Subtitle => "Subtitle", StdStyle.H1 => "Heading1", StdStyle.H2 => "Heading2", StdStyle.H3 => "Heading3", StdStyle.Quote => "Quote", _ => "Normal" };
            string wn = StdStyles.WordName(s);
            string id = baseId; int n = 1;
            while (_defs.TryGetValue(id, out var ex) && !ex.Name.Equals(wn, StringComparison.OrdinalIgnoreCase)) id = baseId + "Dp" + n++;
            if (_defs.ContainsKey(id)) return id;
            var basedOn = DefaultPara;
            (CharFmt rp, ParaFmt pf, string ppr, string rpr) = s switch
            {
                StdStyle.Title => (new CharFmt(Sz: 52, Color: unchecked((int)0xFF17365D)), new ParaFmt(After: 300), "<w:spacing w:after=\"300\"/><w:contextualSpacing/>", "<w:color w:val=\"17365D\"/><w:kern w:val=\"28\"/><w:sz w:val=\"52\"/><w:szCs w:val=\"52\"/>"),
                StdStyle.Subtitle => (new CharFmt(I: true, Sz: 26, Color: unchecked((int)0xFF5A5A5A)), new ParaFmt(After: 160), "<w:spacing w:after=\"160\"/>", "<w:i/><w:color w:val=\"5A5A5A\"/><w:sz w:val=\"26\"/><w:szCs w:val=\"26\"/>"),
                StdStyle.H1 => (new CharFmt(B: true, Sz: 32, Color: unchecked((int)0xFF2F5496)), new ParaFmt(Before: 240, After: 80), "<w:keepNext/><w:keepLines/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"0\"/>", "<w:b/><w:bCs/><w:color w:val=\"2F5496\"/><w:sz w:val=\"32\"/><w:szCs w:val=\"32\"/>"),
                StdStyle.H2 => (new CharFmt(B: true, Sz: 26, Color: unchecked((int)0xFF2F5496)), new ParaFmt(Before: 160, After: 60), "<w:keepNext/><w:keepLines/><w:spacing w:before=\"160\" w:after=\"60\"/><w:outlineLvl w:val=\"1\"/>", "<w:b/><w:bCs/><w:color w:val=\"2F5496\"/><w:sz w:val=\"26\"/><w:szCs w:val=\"26\"/>"),
                StdStyle.H3 => (new CharFmt(B: true, Sz: 24, Color: unchecked((int)0xFF1F3763)), new ParaFmt(Before: 120, After: 40), "<w:keepNext/><w:keepLines/><w:spacing w:before=\"120\" w:after=\"40\"/><w:outlineLvl w:val=\"2\"/>", "<w:b/><w:bCs/><w:color w:val=\"1F3763\"/><w:sz w:val=\"24\"/><w:szCs w:val=\"24\"/>"),
                StdStyle.Quote => (new CharFmt(I: true, Color: unchecked((int)0xFF404040)), new ParaFmt(Before: 120, After: 120, Left: 864, Right: 864, Align: 1), "<w:spacing w:before=\"120\" w:after=\"120\"/><w:ind w:left=\"864\" w:right=\"864\"/><w:jc w:val=\"center\"/>", "<w:i/><w:iCs/><w:color w:val=\"404040\"/>"),
                _ => (CharFmt.EMPTY, new ParaFmt(), "", "")
            };
            _defOrder.Add(id);
            _defs[id] = new Def(id, wn, "paragraph", basedOn, rp, pf);
            _pending[id] = "<w:style w:type=\"paragraph\" w:styleId=\"" + id + "\"><w:name w:val=\"" + wn + "\"/>" +
                (basedOn != null ? "<w:basedOn w:val=\"" + XmlLite.Esc(basedOn) + "\"/><w:next w:val=\"" + XmlLite.Esc(basedOn) + "\"/>" : "") +
                "<w:uiPriority w:val=\"9\"/><w:qFormat/><w:pPr>" + ppr + "</w:pPr><w:rPr>" + rpr + "</w:rPr></w:style>";
            return id;
        }

        internal string? PendingXml()
        {
            var x = string.Concat(_pending.Where(kv => Used.Contains(kv.Key)).Select(kv => kv.Value));
            return x.Length == 0 ? null : x;
        }
    }

    // =============================================================================================
    // Penomoran (numbering.xml)
    // =============================================================================================
    public sealed class Numbering
    {
        public sealed record Lvl(string Fmt, string Text, int Start, int? Left, int? FirstLine);

        private readonly Dictionary<int, Dictionary<int, Lvl>> _abstracts = new();
        private readonly Dictionary<int, int> _nums = new();
        private int _maxAbs, _maxNum, _ownBullet = -1, _ownDecimal = -1;
        private readonly Dictionary<int, int> _ownAbs = new();
        internal HashSet<int> Used = new();

        private static readonly string[] BULLETS = { "•", "◦", "▪" };
        private static readonly (string, string)[] DEC = { ("decimal", "%1."), ("lowerLetter", "%2."), ("lowerRoman", "%3.") };

        public static Numbering Parse(string? xml)
        {
            var n = new Numbering();
            if (xml == null) return n;
            var toks = XmlLite.Tokenize(xml);
            var w = MainNs.Prefix(toks);
            int curAbs = -1, curNum = -1, lvl = -1;
            string fmt = "decimal", text = "%1."; int start = 1; int? left = null, first = null;
            static int? D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;
            foreach (var t in toks)
            {
                if (t.Kind != XTok.START && t.Kind != XTok.EMPTY && t.Kind != XTok.END) continue;
                var l = XmlLite.Local(t.Name);
                string? V(string a = "val") => XmlLite.Attr(t.Raw, Fmt.Q(w, a));
                if (t.Kind == XTok.END)
                {
                    if (l == "lvl" && curAbs >= 0 && lvl >= 0)
                    {
                        if (!n._abstracts.TryGetValue(curAbs, out var m)) n._abstracts[curAbs] = m = new();
                        m[lvl] = new Lvl(fmt, text, start, left, first);
                    }
                    if (l == "abstractNum") curAbs = -1;
                    if (l == "num") curNum = -1;
                    continue;
                }
                switch (l)
                {
                    case "abstractNum": curAbs = OxUtil.ToInt(V("abstractNumId")) ?? -1; n._maxAbs = Math.Max(n._maxAbs, curAbs); break;
                    case "lvl": lvl = OxUtil.ToInt(V("ilvl")) ?? 0; fmt = "decimal"; text = "%" + (lvl + 1) + "."; start = 1; left = null; first = null; break;
                    case "numFmt": fmt = V() ?? fmt; break;
                    case "lvlText": text = V() ?? ""; break;
                    case "start": start = OxUtil.ToInt(V()) ?? 1; break;
                    case "ind":
                        left = D(V("left") ?? V("start"));
                        first = D(V("hanging")) is { } h ? -h : D(V("firstLine"));
                        break;
                    case "num": curNum = OxUtil.ToInt(V("numId")) ?? -1; n._maxNum = Math.Max(n._maxNum, curNum); break;
                    case "abstractNumId": if (curNum >= 0 && OxUtil.ToInt(V()) is { } a) n._nums[curNum] = a; break;
                }
            }
            return n;
        }

        public static string Inject(string xml, (string Abs, string Nums) add)
        {
            var toks = XmlLite.Tokenize(xml);
            var w = MainNs.Prefix(toks);
            string Fix(string s) => s.Replace("w:", w.Length == 0 ? "" : w + ":");
            int firstNum = toks.FindIndex(t => (t.Kind == XTok.START || t.Kind == XTok.EMPTY) && XmlLite.Local(t.Name) == "num" && XmlLite.Prefix(t.Name) == w);
            int end = toks.FindLastIndex(t => t.Kind == XTok.END && XmlLite.Local(t.Name) == "numbering");
            if (end < 0) return xml;
            int absAt = firstNum >= 0 ? firstNum : end;
            var sb = new StringBuilder();
            for (int i = 0; i < toks.Count; i++)
            {
                if (i == absAt) sb.Append(Fix(add.Abs));
                if (i == end) sb.Append(Fix(add.Nums));
                sb.Append(toks[i].Raw);
            }
            return sb.ToString();
        }

        public static string Fresh((string Abs, string Nums) add) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" + add.Abs + add.Nums + "</w:numbering>";

        public Lvl? Level(int numId, int ilvl) => _nums.TryGetValue(numId, out var a) && _abstracts.TryGetValue(a, out var m) && m.TryGetValue(ilvl, out var l) ? l : null;
        public bool IsBullet(int numId) => Level(numId, 0)?.Fmt == "bullet";

        internal int EnsureOwn(bool bullet)
        {
            if (bullet && _ownBullet > 0) return _ownBullet;
            if (!bullet && _ownDecimal > 0) return _ownDecimal;
            int abs = ++_maxAbs + 900, num = ++_maxNum;
            _maxAbs = abs;
            var m = new Dictionary<int, Lvl>();
            for (int l = 0; l <= 8; l++) m[l] = bullet ? new Lvl("bullet", BULLETS[l % 3], 1, 720 * (l + 1), -360) : new Lvl(DEC[l % 3].Item1, "%" + (l + 1) + ".", 1, 720 * (l + 1), -360);
            _abstracts[abs] = m; _nums[num] = abs;
            if (bullet) _ownBullet = num; else _ownDecimal = num;
            _ownAbs[num] = abs;
            return num;
        }

        internal (string Abs, string Nums)? PendingXml()
        {
            var ids = _ownAbs.Keys.Where(Used.Contains).ToList();
            if (ids.Count == 0) return null;
            var a = new StringBuilder(); var n = new StringBuilder();
            foreach (var num in ids)
            {
                var abs = _ownAbs[num];
                a.Append("<w:abstractNum w:abstractNumId=\"").Append(abs).Append("\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
                foreach (var (l, lv) in _abstracts[abs].OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
                    a.Append("<w:lvl w:ilvl=\"").Append(l).Append("\"><w:start w:val=\"1\"/><w:numFmt w:val=\"").Append(lv.Fmt).Append("\"/><w:lvlText w:val=\"").Append(XmlLite.Esc(lv.Text))
                     .Append("\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"").Append(lv.Left).Append("\" w:hanging=\"360\"/></w:pPr></w:lvl>");
                a.Append("</w:abstractNum>");
                n.Append("<w:num w:numId=\"").Append(num).Append("\"><w:abstractNumId w:val=\"").Append(abs).Append("\"/></w:num>");
            }
            return (a.ToString(), n.ToString());
        }

        /// <summary>Label tiap paragraf (null = bukan daftar).</summary>
        public List<string?> Labels(List<ParaFmt> eff)
        {
            var counters = new Dictionary<int, int[]>();
            var outL = new List<string?>(eff.Count);
            foreach (var pf in eff)
            {
                if (pf.NumId is not > 0) { outL.Add(null); continue; }
                int id = pf.NumId.Value;
                int lv = Math.Clamp(pf.Ilvl ?? 0, 0, 8);
                var l = Level(id, lv);
                if (l == null || !_nums.TryGetValue(id, out var abs)) { outL.Add(null); continue; }
                if (!counters.TryGetValue(abs, out var c)) counters[abs] = c = Enumerable.Repeat(-1, 9).ToArray();
                for (int k = 0; k < lv; k++) if (c[k] < 0) c[k] = Level(id, k)?.Start ?? 1;
                c[lv] = c[lv] < 0 ? l.Start : c[lv] + 1;
                for (int k = lv + 1; k < 9; k++) c[k] = -1;
                if (l.Fmt == "bullet") { outL.Add(BulletChar(l.Text)); continue; }
                if (l.Fmt == "none") { outL.Add(""); continue; }
                var s = l.Text;
                for (int k = 0; k <= lv; k++)
                {
                    var lk = Level(id, k); if (lk == null) continue;
                    s = s.Replace("%" + (k + 1), DocxHtml.FmtNum(Math.Max(c[k], lk.Start), lk.Fmt));
                }
                outL.Add(s);
            }
            return outL;
        }

        private static string BulletChar(string t)
        {
            if (t.Length == 0) return "•";
            char c = t[0];
            if (c >= 0xF000 && c <= 0xF0FF) return c == 0xF0A7 ? "▪" : "•";
            if (c == 'o') return "◦";
            if (c == '§') return "▪";
            return t;
        }
    }

    // =============================================================================================
    // Bagian paket tambahan
    // =============================================================================================
    internal sealed class PartWriter
    {
        public sealed record MediaItem(string RId, string File, string Ext, byte[] Bytes);
        public readonly List<MediaItem> Media = new();

        public static string FreeId(string rels, string b) { var id = b; int n = 1; while (rels.Contains("Id=\"" + id + "\"")) id = b + n++; return id; }

        public static string AddRel(string rels, string id, string type, string target)
        {
            int end = rels.LastIndexOf("</Relationships>", StringComparison.Ordinal);
            var r = "<Relationship Id=\"" + id + "\" Type=\"" + type + "\" Target=\"" + target + "\"/>";
            if (end < 0)
            {
                var sc = Regex.Match(rels, "<Relationships([^>]*)/>");
                return sc.Success ? rels[..sc.Index] + "<Relationships" + sc.Groups[1].Value + ">" + r + "</Relationships>" + rels[(sc.Index + sc.Length)..] : rels;
            }
            return rels[..end] + r + rels[end..];
        }

        public static string AddOverride(string ct, string part, string type)
        {
            if (ct.Contains("PartName=\"" + part + "\"")) return ct;
            int end = ct.LastIndexOf("</Types>", StringComparison.Ordinal);
            return end < 0 ? ct : ct[..end] + "<Override PartName=\"" + part + "\" ContentType=\"" + type + "\"/>" + ct[end..];
        }

        public static string AddDefault(string ct, string ext, string type)
        {
            if (Regex.IsMatch(ct, "Extension=\"" + Regex.Escape(ext) + "\"", RegexOptions.IgnoreCase)) return ct;
            int end = ct.LastIndexOf("</Types>", StringComparison.Ordinal);
            return end < 0 ? ct : ct[..end] + "<Default Extension=\"" + ext + "\" ContentType=\"" + type + "\"/>" + ct[end..];
        }
    }

    // =============================================================================================
    // Parsing struktur document.xml
    // =============================================================================================
    internal sealed record Atom(int Tok, int Off, int Run);

    internal sealed class RInfo
    {
        public readonly int Start; public int End; public Rng? RPr; public CharFmt Direct = CharFmt.EMPTY; public string? RStyle; public CharFmt Merged = CharFmt.EMPTY;
        public bool HasDrawing; public readonly List<string> Blips = new(); public long Cx, Cy;
        public RInfo(int start) { Start = start; End = start; }
    }

    internal sealed class PInfo
    {
        public readonly int Id, Start; public readonly bool EmptyTag; public int End; public bool Editable = true, BodyLevel;
        public readonly List<Atom> Atoms = new(); public readonly StringBuilder Text = new();
        public Rng? PPr; public ParaFmt Direct = new(); public bool HasSect; public Rng? MarkRPr; public int FirstRun = -1, AnyRun = -1;
        public bool HasObject, PageBreak; public readonly List<ImgRef> Images = new(); public int Cell = -1;
        public PInfo(int id, int start, bool emptyTag) { Id = id; Start = start; End = start; EmptyTag = emptyTag; }
    }

    internal sealed class CInfo
    {
        public readonly int Id, Start; public int End; public Rng? TcPr; public int TcW = -1, Span = 1; public bool VCont, HasVMerge, Nested; public int? Fill;
        public readonly List<int> Paras = new();
        public CInfo(int id, int start) { Id = id; Start = start; End = start; }
    }

    internal sealed class RowInfo { public readonly int Id, Start; public int End; public Rng? TrPr; public readonly List<int> Cells = new(); public RowInfo(int id, int start) { Id = id; Start = start; End = start; } }

    internal sealed class TInfo
    {
        public readonly int Id, Start; public int End; public readonly List<int> GridToks = new(), Grid = new(), Rows = new(); public int GridEnd = -1; public bool Bordered;
        public TInfo(int id, int start) { Id = id; Start = start; End = start; }
    }

    internal sealed class SectInfo { public int PgSz = -1, PgMar = -1; }

    internal sealed class Parsed
    {
        public readonly List<XTok> Toks;
        public readonly string W;
        public readonly List<PInfo> Paras = new();
        public readonly List<RInfo> Runs = new();
        public readonly List<CInfo> Cells = new();
        public readonly List<RowInfo> RowsI = new();
        public readonly List<TInfo> Tables = new();
        public readonly List<SectInfo> Sects = new();
        public readonly List<object> Order = new();
        public int BodySectPr = -1, BodyEnd = -1;
        public readonly Dictionary<int, int> TOwner = new(), TRun = new();

        public Parsed(string xml)
        {
            Toks = XmlLite.Tokenize(xml);
            W = MainNs.Prefix(Toks);
            Del = new bool[Toks.Count];
            Walk();
        }

        private void Walk()
        {
            var stack = new List<string>();
            var pStack = new List<PInfo>();
            var runStack = new List<int>();
            int skip = 0, tblDepth = 0, curT = -1, pPrStart = -1, tcPrStart = -1, trPrStart = -1;
            TInfo? table = null; RowInfo? row = null; CInfo? cell = null; SectInfo? sect = null;
            var rPrStarts = new List<int>();
            string w = W;

            string? ParentL(int n = 1)
            {
                if (stack.Count - n < 0) return null;
                var it = stack[stack.Count - n];
                return XmlLite.Prefix(it) == w ? XmlLite.Local(it) : "~" + XmlLite.Local(it);
            }
            PInfo NewP(int i, bool emptyTag)
            {
                var p = new PInfo(Paras.Count, i, emptyTag) { Editable = skip == 0 };
                Paras.Add(p);
                if (p.Editable)
                {
                    if (tblDepth >= 1 && cell != null) { cell.Paras.Add(p.Id); p.Cell = cell.Id; if (tblDepth >= 2) cell.Nested = true; }
                    else if (tblDepth == 0 && pStack.Count == 0) { p.BodyLevel = true; Order.Add(p); }
                }
                return p;
            }
            void AddAtom(PInfo p, Atom a, char ch) { if (p.FirstRun < 0) p.FirstRun = a.Run; p.Atoms.Add(a); p.Text.Append(ch); }
            int? LastRun() => runStack.Count > 0 ? runStack[^1] : null;

            for (int i = 0; i < Toks.Count; i++)
            {
                var t = Toks[i];
                if (t.Kind == XTok.START || t.Kind == XTok.EMPTY)
                {
                    bool isWn = XmlLite.Prefix(t.Name) == w;
                    var l = XmlLite.Local(t.Name);
                    var par = ParentL(1);
                    var p = pStack.Count > 0 ? pStack[^1] : null;
                    bool start = t.Kind == XTok.START;
                    if (isWn)
                    {
                        switch (l)
                        {
                            case "p": { var np = NewP(i, !start); if (start) pStack.Add(np); break; }
                            case "r": if (start) { Runs.Add(new RInfo(i)); runStack.Add(Runs.Count - 1); } break;
                            case "rPr":
                                if (start) rPrStarts.Add(i);
                                else
                                {
                                    if (par == "r" && LastRun() is { } lr) Runs[lr].RPr = new Rng(i, i);
                                    if (par == "pPr" && p != null) p.MarkRPr = new Rng(i, i);
                                }
                                break;
                            case "pPr": if (par == "p" && p != null) { if (start) pPrStart = i; else p.PPr = new Rng(i, i); } break;
                            case "t": if (par == "r" && start) curT = i; break;
                            case "tab": if (par == "r" && p != null && LastRun() is { } r1) AddAtom(p, new Atom(i, -1, r1), '\t'); break;
                            case "cr": if (par == "r" && p != null && LastRun() is { } r2) AddAtom(p, new Atom(i, -1, r2), '\n'); break;
                            case "noBreakHyphen": if (par == "r" && p != null && LastRun() is { } r3) AddAtom(p, new Atom(i, -1, r3), '-'); break;
                            case "br":
                                if (par == "r" && p != null)
                                {
                                    var type = XmlLite.Attr(t.Raw, Fmt.Q(w, "type"));
                                    if (type is "page" or "column") p.PageBreak = true;
                                    else if (LastRun() is { } r4) AddAtom(p, new Atom(i, -1, r4), '\n');
                                }
                                break;
                            case "drawing": case "pict": case "object": if (LastRun() is { } r5) Runs[r5].HasDrawing = true; break;
                            case "txbxContent": if (start) { skip++; if (p != null) p.HasObject = true; } break;
                            case "tbl":
                                if (start)
                                {
                                    tblDepth++;
                                    if (tblDepth == 1 && skip == 0 && pStack.Count == 0)
                                    {
                                        table = new TInfo(Tables.Count, i); Tables.Add(table);
                                        if (cell == null) Order.Add(table);
                                    }
                                }
                                break;
                            case "tblStyle": if (tblDepth == 1 && table != null) table.Bordered = true; break;
                            case "gridCol":
                                if (tblDepth == 1 && par == "tblGrid" && table != null)
                                { table.GridToks.Add(i); table.Grid.Add(double.TryParse(XmlLite.Attr(t.Raw, Fmt.Q(w, "w")), NumberStyles.Float, CultureInfo.InvariantCulture, out var gw) ? (int)gw : 0); }
                                break;
                            case "tr": if (tblDepth == 1 && start && table != null) { row = new RowInfo(RowsI.Count, i); RowsI.Add(row); table.Rows.Add(row.Id); } break;
                            case "trPr": if (tblDepth == 1 && par == "tr") { if (start) trPrStart = i; else if (row != null) row.TrPr = new Rng(i, i); } break;
                            case "tc": if (tblDepth == 1 && start && row != null) { cell = new CInfo(Cells.Count, i); Cells.Add(cell); row.Cells.Add(cell.Id); } break;
                            case "tcPr": if (tblDepth == 1 && par == "tc") { if (start) tcPrStart = i; else if (cell != null) cell.TcPr = new Rng(i, i); } break;
                            case "sectPr":
                                if (par == "body") BodySectPr = i;
                                sect = new SectInfo(); Sects.Add(sect);
                                if (!start) sect = null;
                                if (par == "pPr" && p != null) p.HasSect = true;
                                break;
                            case "pgSz": if (par == "sectPr" && sect != null) sect.PgSz = i; break;
                            case "pgMar": if (par == "sectPr" && sect != null) sect.PgMar = i; break;
                            case "pageBreakBefore": if (par == "pPr" && p != null && !Fmt.IsOff(XmlLite.Attr(t.Raw, Fmt.Q(w, "val")))) p.PageBreak = true; break;
                        }
                    }
                    if (skip == 0 && (l == "blip" || l == "imagedata") && LastRun() is { } ri)
                    {
                        var id = XmlLite.AttrLocal(t.Raw, "embed") ?? XmlLite.AttrLocal(t.Raw, "id");
                        if (id != null) Runs[ri].Blips.Add(id);
                    }
                    if (l == "extent" && skip == 0 && LastRun() is { } re)
                    {
                        Runs[re].Cx = long.TryParse(XmlLite.Attr(t.Raw, "cx"), out var cx) ? cx : 0;
                        Runs[re].Cy = long.TryParse(XmlLite.Attr(t.Raw, "cy"), out var cy) ? cy : 0;
                    }
                    if (l == "Fallback" && start) skip++;
                    if (isWn)
                    {
                        if (par == "tcPr" && ParentL(2) == "tc" && tblDepth == 1 && cell != null)
                        {
                            switch (l)
                            {
                                case "gridSpan": cell.Span = Math.Max(1, OxUtil.ToInt(XmlLite.Attr(t.Raw, Fmt.Q(w, "val"))) ?? 1); break;
                                case "vMerge": cell.HasVMerge = true; cell.VCont = XmlLite.Attr(t.Raw, Fmt.Q(w, "val")) != "restart"; break;
                                case "shd": cell.Fill = Fmt.ParseHex(XmlLite.Attr(t.Raw, Fmt.Q(w, "fill"))); break;
                                case "tcW": cell.TcW = i; break;
                            }
                        }
                        if (par == "tblBorders" && tblDepth == 1 && table != null)
                        {
                            var v = XmlLite.Attr(t.Raw, Fmt.Q(w, "val"));
                            if (v != null && v != "nil" && v != "none") table.Bordered = true;
                        }
                    }
                    if (start) stack.Add(t.Name);
                }
                else if (t.Kind == XTok.END)
                {
                    bool isWn = XmlLite.Prefix(t.Name) == w;
                    var l = XmlLite.Local(t.Name);
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    var par = ParentL(1);
                    if (isWn)
                    {
                        switch (l)
                        {
                            case "p": if (pStack.Count > 0) { pStack[^1].End = i; pStack.RemoveAt(pStack.Count - 1); } break;
                            case "r":
                                {
                                    int? ri = null; if (runStack.Count > 0) { ri = runStack[^1]; runStack.RemoveAt(runStack.Count - 1); }
                                    var p = pStack.Count > 0 ? pStack[^1] : null;
                                    if (ri != null)
                                    {
                                        var r = Runs[ri.Value]; r.End = i;
                                        if (p != null)
                                        {
                                            if (p.AnyRun < 0 && r.RPr != null) p.AnyRun = ri.Value;
                                            if (r.Blips.Count > 0) foreach (var b in r.Blips) p.Images.Add(new ImgRef(b, r.Start, i, r.Cx, r.Cy));
                                            else if (r.HasDrawing) p.HasObject = true;
                                        }
                                    }
                                    break;
                                }
                            case "rPr":
                                {
                                    int s = -1; if (rPrStarts.Count > 0) { s = rPrStarts[^1]; rPrStarts.RemoveAt(rPrStarts.Count - 1); }
                                    if (s >= 0)
                                    {
                                        if (par == "r" && LastRun() is { } lr) Runs[lr].RPr = new Rng(s, i);
                                        if (par == "pPr" && pStack.Count > 0) pStack[^1].MarkRPr = new Rng(s, i);
                                    }
                                    break;
                                }
                            case "pPr": if (par == "p" && pPrStart >= 0) { if (pStack.Count > 0) pStack[^1].PPr = new Rng(pPrStart, i); pPrStart = -1; } break;
                            case "t": curT = -1; break;
                            case "txbxContent": skip--; break;
                            case "tbl": if (tblDepth == 1) { if (table != null) table.End = i; table = null; row = null; cell = null; } tblDepth--; break;
                            case "tblGrid": if (tblDepth == 1 && table != null) table.GridEnd = i; break;
                            case "tr": if (tblDepth == 1) { if (row != null) row.End = i; row = null; } break;
                            case "trPr": if (tblDepth == 1 && trPrStart >= 0) { if (row != null) row.TrPr = new Rng(trPrStart, i); trPrStart = -1; } break;
                            case "tc": if (tblDepth == 1) { if (cell != null) cell.End = i; cell = null; } break;
                            case "tcPr": if (tblDepth == 1 && tcPrStart >= 0) { if (cell != null) cell.TcPr = new Rng(tcPrStart, i); tcPrStart = -1; } break;
                            case "sectPr": sect = null; break;
                            case "body": BodyEnd = i; break;
                        }
                    }
                    if (l == "Fallback") skip--;
                }
                else if (t.Kind == XTok.TEXT)
                {
                    var p = pStack.Count > 0 ? pStack[^1] : null;
                    if (curT >= 0 && p != null && LastRun() is { } ri)
                    {
                        TOwner[i] = curT; TRun[i] = ri;
                        var s = XmlLite.DecodeText(t.Raw);
                        for (int k = 0; k < s.Length; k++) AddAtom(p, new Atom(i, k, ri), s[k]);
                    }
                }
            }
            foreach (var p in Paras)
            {
                var (f, s) = Fmt.ParsePPr(Toks, p.PPr, w);
                p.Direct = f; if (s) p.HasSect = true;
            }
        }

        public PageSetup? PageSetup()
        {
            var s = Sects.LastOrDefault(x => x.PgSz >= 0); if (s == null) return null;
            var sz = Toks[s.PgSz].Raw;
            int? A(string raw, string n) => double.TryParse(XmlLite.Attr(raw, Fmt.Q(W, n)), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;
            int pw = A(sz, "w") ?? 11906, ph = A(sz, "h") ?? 16838;
            bool land = XmlLite.Attr(sz, Fmt.Q(W, "orient")) == "landscape";
            var m = s.PgMar >= 0 ? Toks[s.PgMar].Raw : "";
            return new PageSetup(pw, ph, land, A(m, "top") ?? 1440, A(m, "right") ?? 1440, A(m, "bottom") ?? 1440, A(m, "left") ?? 1440);
        }

        public List<EBlock> BuildModel(Styles styles)
        {
            foreach (var r in Runs)
            {
                var (d, rs) = Fmt.ParseRPr(Toks, r.RPr, W);
                r.Direct = d; r.RStyle = rs;
                r.Merged = styles.CharStyle(rs).Over(d);
            }
            EPara Para(PInfo p)
            {
                var e = new EPara(p.Id, p.Text.ToString(), p.Atoms.Select(a => Runs[a.Run].Merged).ToArray(), p.Direct)
                { Images = new List<ImgRef>(p.Images), HasObject = p.HasObject, PageBreak = p.PageBreak, Locked = p.HasSect };
                return e;
            }
            var outL = new List<EBlock>();
            foreach (var o in Order)
            {
                if (o is PInfo pi) outL.Add(Para(pi));
                else if (o is TInfo ti)
                {
                    var rws = ti.Rows.Select(x => RowsI[x]).ToList();
                    bool simple = ti.Grid.Count > 0 && rws.All(r => r.Cells.Count == ti.Grid.Count && r.Cells.All(ci => Cells[ci].Span == 1 && !Cells[ci].HasVMerge));
                    outL.Add(new ETable(ti.Id, rws.Select(r => new ERow(r.Id, r.Cells.Select(ci =>
                    {
                        var c = Cells[ci];
                        return new ECell(c.Id, c.Paras.Select(pp => Para(Paras[pp])).ToList(), c.Span, c.VCont, c.Fill, c.Id, c.Nested);
                    }).ToList())).ToList(), new List<int>(ti.Grid), simple, ti.Bordered));
                }
            }
            return outL;
        }

        // ---------------------------------------------------------------- status patch
        public readonly Dictionary<int, StringBuilder> BeforeM = new(), AfterM = new();
        public readonly bool[] Del;
        public readonly Dictionary<int, string> Repl = new();
        public readonly HashSet<int> Blank = new();
        public readonly Dictionary<int, bool[]> TDel = new();
        public readonly Dictionary<int, string?[]> TOvr = new();
        public readonly Dictionary<int, Dictionary<int, List<(char C, string? Ov)>>> TIns = new();

        public StringBuilder Before(int i) { if (!BeforeM.TryGetValue(i, out var sb)) BeforeM[i] = sb = new(); return sb; }
        public StringBuilder After(int i) { if (!AfterM.TryGetValue(i, out var sb)) AfterM[i] = sb = new(); return sb; }
        public void DelRange(Rng r) { foreach (var k in r.All()) if (k >= 0 && k < Del.Length) Del[k] = true; }
        public void ReplRange(Rng r, string xml) { DelRange(r); Repl[r.First] = xml; }

        public string Wq(string l) => W.Length == 0 ? l : W + ":" + l;
        public string TOpen() => "<" + Wq("t") + " xml:space=\"preserve\">";
        public string TClose() => "</" + Wq("t") + ">";

        public string CharXml(char c) => c switch
        {
            '\n' => TClose() + "<" + Wq("br") + "/>" + TOpen(),
            '\t' => TClose() + "<" + Wq("tab") + "/>" + TOpen(),
            '\r' => "",
            _ => XmlLite.Esc(c.ToString())
        };

        /// <summary>Isi di dalam &lt;w:t&gt; yang terbuka pada run berformat <paramref name="orig"/>; override memecah run.</summary>
        public string EmitInRun(List<(char C, string? Ov)> seq, string orig)
        {
            var sb = new StringBuilder();
            string? cur = null;
            foreach (var (c, ov) in seq)
            {
                if (ov != cur)
                {
                    sb.Append(TClose()).Append("</").Append(Wq("r")).Append("><").Append(Wq("r")).Append('>').Append(ov ?? orig).Append(TOpen());
                    cur = ov;
                }
                sb.Append(CharXml(c));
            }
            if (cur != null) sb.Append(TClose()).Append("</").Append(Wq("r")).Append("><").Append(Wq("r")).Append('>').Append(orig).Append(TOpen());
            return sb.ToString();
        }

        public string RangeRaw(Rng? r)
        {
            if (r == null) return "";
            var sb = new StringBuilder();
            foreach (var k in r.Value.All()) sb.Append(Toks[k].Raw);
            return sb.ToString();
        }

        private static readonly HashSet<string> STRIP = new() { "ins", "del", "moveFrom", "moveTo", "rPrChange", "pPrChange", "sectPr", "pageBreakBefore" };

        /// <summary>Salin rentang token tanpa sub-pohon yang tidak boleh diduplikasi.</summary>
        public string CopyRange(Rng? r, HashSet<string>? strip = null)
        {
            if (r == null) return "";
            strip ??= STRIP;
            var sb = new StringBuilder();
            int skipDepth = 0;
            foreach (var i in r.Value.All())
            {
                var t = Toks[i];
                bool drop = XmlLite.Prefix(t.Name) == W && strip.Contains(XmlLite.Local(t.Name));
                if (skipDepth > 0) { if (t.Kind == XTok.START) skipDepth++; else if (t.Kind == XTok.END) skipDepth--; }
                else if (drop && t.Kind == XTok.START) skipDepth = 1;
                else if (drop && t.Kind == XTok.EMPTY) { }
                else sb.Append(t.Raw);
            }
            return sb.ToString();
        }

        public string Render()
        {
            var sb = new StringBuilder(Toks.Count * 24);
            var preserve = new HashSet<int>();
            foreach (var i in TDel.Keys.Concat(TOvr.Keys).Concat(TIns.Keys)) if (TOwner.TryGetValue(i, out var o)) preserve.Add(o);
            for (int i = 0; i < Toks.Count; i++)
            {
                if (Repl.TryGetValue(i, out var rp)) sb.Append(rp);
                if (Del[i]) continue;
                var t = Toks[i];
                if (BeforeM.TryGetValue(i, out var bf)) sb.Append(bf);
                if (Blank.Contains(i)) { }
                else if (preserve.Contains(i)) sb.Append('<').Append(t.Name).Append(" xml:space=\"preserve\">");
                else if (t.Kind == XTok.TEXT && (TDel.ContainsKey(i) || TOvr.ContainsKey(i) || TIns.ContainsKey(i)))
                {
                    var s = XmlLite.DecodeText(t.Raw);
                    TDel.TryGetValue(i, out var d); TOvr.TryGetValue(i, out var o); TIns.TryGetValue(i, out var ins);
                    var seq = new List<(char, string?)>(s.Length + 8);
                    for (int k = 0; k <= s.Length; k++)
                    {
                        if (ins != null && ins.TryGetValue(k, out var add)) seq.AddRange(add);
                        if (k < s.Length && (d == null || !d[k])) seq.Add((s[k], o?[k]));
                    }
                    string runRaw = TRun.TryGetValue(i, out var run) ? RangeRaw(Runs[run].RPr) : "";
                    sb.Append(EmitInRun(seq, runRaw));
                }
                else sb.Append(t.Raw);
                if (AfterM.TryGetValue(i, out var af)) sb.Append(af);
            }
            return sb.ToString();
        }
    }

    // =============================================================================================
    // Patcher: menerapkan model editor ke token XML asli
    // =============================================================================================
    internal sealed class Patcher
    {
        private readonly Parsed _p; private readonly DocxDocument _doc; private readonly PartWriter _parts; private readonly string _w;
        private int _imgN;
        private readonly HashSet<string> _usedStyles = new();
        private readonly HashSet<int> _usedNums = new();

        public Patcher(Parsed p, DocxDocument doc, PartWriter parts) { _p = p; _doc = doc; _parts = parts; _w = p.W; }

        public void Apply()
        {
            _p.BuildModel(_doc.Styles);
            var presentP = new HashSet<int>(); var presentT = new HashSet<int>(); var presentR = new HashSet<int>(); var presentC = new HashSet<int>();
            void MarkPara(EPara e) { if (e.Origin >= 0) presentP.Add(e.Origin); if (e.Pf.Style != null) _usedStyles.Add(e.Pf.Style); if (e.Pf.NumId != null) _usedNums.Add(e.Pf.NumId.Value); }
            foreach (var b in _doc.Body)
            {
                if (b is EPara ep) MarkPara(ep);
                else if (b is ETable et)
                {
                    if (et.Origin >= 0) presentT.Add(et.Origin);
                    foreach (var r in et.Rows) { if (r.Origin >= 0) presentR.Add(r.Origin); foreach (var c in r.Cells) { if (c.Origin >= 0) presentC.Add(c.Origin); c.Paras.ForEach(MarkPara); } }
                }
            }
            _doc.Styles.Used = _usedStyles;
            _doc.Numbering.Used = _usedNums;

            foreach (var t in _p.Tables) if (!presentT.Contains(t.Id) && _p.Order.Contains(t)) _p.DelRange(new Rng(t.Start, t.End));
            foreach (var r in _p.RowsI) if (!presentR.Contains(r.Id)) _p.DelRange(new Rng(r.Start, r.End));
            foreach (var c in _p.Cells) if (!presentC.Contains(c.Id)) _p.DelRange(new Rng(c.Start, c.End));
            foreach (var pi in _p.Paras)
            {
                if (!pi.Editable || presentP.Contains(pi.Id)) continue;
                if (!pi.BodyLevel && pi.Cell < 0) continue;
                if (pi.HasSect) { EditPara(pi, new EPara(pi.Id, "", Array.Empty<CharFmt>(), pi.Direct)); continue; }
                _p.DelRange(new Rng(pi.Start, pi.End));
            }
            foreach (var b in _doc.AllParas())
            {
                if (b.Origin < 0) continue;
                foreach (var im in _p.Paras[b.Origin].Images)
                    if (!b.Images.Any(x => x.RunStart == im.RunStart)) _p.DelRange(new Rng(im.RunStart, im.RunEnd));
            }

            Flush(_doc.Body, b => b switch
            {
                EPara e when e.Origin >= 0 => new Rng(_p.Paras[e.Origin].Start, _p.Paras[e.Origin].End),
                ETable t when t.Origin >= 0 => new Rng(_p.Tables[t.Origin].Start, _p.Tables[t.Origin].End),
                _ => (Rng?)null
            }, BlockXml, _p.BodySectPr >= 0 ? _p.BodySectPr : _p.BodyEnd);

            foreach (var b in _doc.Body)
            {
                if (b is EPara e && e.Origin >= 0) EditPara(_p.Paras[e.Origin], e);
                else if (b is ETable t && t.Origin >= 0) EditTable(_p.Tables[t.Origin], t);
            }

            if (_doc.PageChanged() && _doc.Page is { } pg)
            {
                string I(int v) => v.ToString(CultureInfo.InvariantCulture);
                foreach (var s in _p.Sects)
                {
                    if (s.PgSz >= 0)
                    {
                        var t = _p.Toks[s.PgSz]; var a = XmlLite.Attrs(t.Raw);
                        XmlLite.Put(a, Fmt.Q(_w, "w"), I(pg.W)); XmlLite.Put(a, Fmt.Q(_w, "h"), I(pg.H));
                        if (pg.Landscape) XmlLite.Put(a, Fmt.Q(_w, "orient"), "landscape"); else XmlLite.Remove(a, Fmt.Q(_w, "orient"));
                        t.Raw = XmlLite.Tag(t.Name, a, true);
                    }
                    if (s.PgMar >= 0)
                    {
                        var t = _p.Toks[s.PgMar]; var a = XmlLite.Attrs(t.Raw);
                        XmlLite.Put(a, Fmt.Q(_w, "top"), I(pg.Top)); XmlLite.Put(a, Fmt.Q(_w, "right"), I(pg.Right));
                        XmlLite.Put(a, Fmt.Q(_w, "bottom"), I(pg.Bottom)); XmlLite.Put(a, Fmt.Q(_w, "left"), I(pg.Left));
                        t.Raw = XmlLite.Tag(t.Name, a, true);
                    }
                }
            }
        }

        private void Flush<T>(List<T> items, Func<T, Rng?> range, Func<T, string> xml, int fallback)
        {
            int anchor = -1;
            var pending = new StringBuilder();
            foreach (var it in items)
            {
                var r = range(it);
                if (r == null) { pending.Append(xml(it)); continue; }
                if (pending.Length > 0)
                {
                    if (anchor >= 0) _p.After(anchor).Append(pending); else _p.Before(r.Value.First).Append(pending);
                    pending.Clear();
                }
                anchor = r.Value.Last;
            }
            if (pending.Length > 0)
            {
                if (anchor >= 0) _p.After(anchor).Append(pending); else if (fallback >= 0) _p.Before(fallback).Append(pending);
            }
        }

        private string BlockXml(EBlock b) => b switch
        {
            EPara e => NewParaXml(e),
            ETable t => NewTableXml(t),
            EImage im => ImageXml(im),
            EBreak => "<" + _p.Wq("p") + "><" + _p.Wq("r") + "><" + _p.Wq("br") + " " + Fmt.Q(_w, "type") + "=\"page\"/></" + _p.Wq("r") + "></" + _p.Wq("p") + ">",
            _ => ""
        };

        // ------------------------------- paragraf
        private string RunRaw(int? ri) => ri != null ? _p.CopyRange(_p.Runs[ri.Value].RPr) : "";
        private CharFmt RunFmt(int? ri) => ri != null ? _p.Runs[ri.Value].Merged : CharFmt.EMPTY;
        private static CharFmt At(CharFmt[] a, int i, CharFmt def) => i >= 0 && i < a.Length ? a[i] : def;

        private string RunsXml(string text, CharFmt[] fmts, string tplRaw, CharFmt tplFmt)
        {
            var sb = new StringBuilder();
            int k = 0;
            while (k < text.Length)
            {
                int j = k;
                var fk = At(fmts, k, CharFmt.EMPTY);
                while (j < text.Length && At(fmts, j, CharFmt.EMPTY) == fk) j++;
                sb.Append('<').Append(_p.Wq("r")).Append('>').Append(Fmt.RprWith(tplRaw, tplFmt, fk, _w)).Append(_p.TOpen());
                for (int q = k; q < j; q++) sb.Append(_p.CharXml(text[q]));
                sb.Append(_p.TClose()).Append("</").Append(_p.Wq("r")).Append('>');
                k = j;
            }
            return sb.ToString();
        }

        private string NewParaXml(EPara e)
        {
            var tpl = e.Tpl >= 0 && e.Tpl < _p.Paras.Count ? _p.Paras[e.Tpl] : null;
            var tplPf = tpl?.Direct ?? new ParaFmt();
            var pPr = Fmt.PprWith(_p.CopyRange(tpl?.PPr), tplPf, e.Pf, _w);
            int? ri = tpl == null ? null : tpl.FirstRun >= 0 ? tpl.FirstRun : tpl.AnyRun >= 0 ? tpl.AnyRun : null;
            string rRaw; CharFmt rFmt;
            if (ri != null) { rRaw = RunRaw(ri); rFmt = RunFmt(ri); }
            else { rRaw = tpl?.MarkRPr != null ? _p.CopyRange(tpl.MarkRPr) : ""; rFmt = tpl?.MarkRPr != null ? Fmt.ParseRPr(_p.Toks, tpl.MarkRPr, _w).Fmt : CharFmt.EMPTY; }
            var br = e.PageBreak && e.Origin < 0 ? "<" + _p.Wq("r") + "><" + _p.Wq("br") + " " + Fmt.Q(_w, "type") + "=\"page\"/></" + _p.Wq("r") + ">" : "";
            return "<" + _p.Wq("p") + ">" + pPr + br + RunsXml(e.Text, e.Fmts, rRaw, rFmt) + "</" + _p.Wq("p") + ">";
        }

        private void EditPara(PInfo pi, EPara e)
        {
            var origText = pi.Text.ToString();
            var origFmts = pi.Atoms.Select(a => _p.Runs[a.Run].Merged).ToArray();
            bool pfChanged = e.Pf != pi.Direct;
            bool textChanged = origText != e.Text || !origFmts.SequenceEqual(e.Fmts);
            if (!pfChanged && !textChanged) return;

            if (pi.EmptyTag)
            {
                var t = _p.Toks[pi.Start];
                var pPr = Fmt.PprWith("", new ParaFmt(), e.Pf, _w);
                t.Raw = t.Raw[..^2].TrimEnd() + ">" + pPr + RunsXml(e.Text, e.Fmts, "", CharFmt.EMPTY) + "</" + t.Name + ">";
                return;
            }
            if (pfChanged)
            {
                var newPPr = Fmt.PprWith(_p.RangeRaw(pi.PPr), pi.Direct, e.Pf, _w);
                if (pi.PPr is { } r) _p.ReplRange(r, newPPr); else if (newPPr.Length > 0) _p.After(pi.Start).Append(newPPr);
            }
            if (!textChanged) return;

            if (pi.Atoms.Count == 0)
            {
                if (e.Text.Length == 0) return;
                int? ri = pi.AnyRun >= 0 ? pi.AnyRun : null;
                string raw; CharFmt f;
                if (ri != null) { raw = RunRaw(ri); f = RunFmt(ri); }
                else { raw = pi.MarkRPr != null ? _p.CopyRange(pi.MarkRPr) : ""; f = pi.MarkRPr != null ? Fmt.ParseRPr(_p.Toks, pi.MarkRPr, _w).Fmt : CharFmt.EMPTY; }
                _p.Before(pi.End).Append(RunsXml(e.Text, e.Fmts, raw, f));
                return;
            }

            var d = TextDiff.Align(origText, e.Text);
            int n = origText.Length;
            for (int k = 0; k < n; k++)
            {
                if (d.OldToNew[k] >= 0) continue;
                var a = pi.Atoms[k];
                if (a.Off >= 0)
                {
                    if (!_p.TDel.TryGetValue(a.Tok, out var arr)) _p.TDel[a.Tok] = arr = new bool[XmlLite.DecodeText(_p.Toks[a.Tok].Raw).Length];
                    arr[a.Off] = true;
                }
                else _p.Blank.Add(a.Tok);
            }
            for (int k = 0; k < n; k++)
            {
                int j = d.OldToNew[k];
                if (j < 0) continue;
                var a = pi.Atoms[k];
                if (a.Off < 0) continue;
                var want = At(e.Fmts, j, origFmts[k]);
                if (want != origFmts[k])
                {
                    if (!_p.TOvr.TryGetValue(a.Tok, out var arr)) _p.TOvr[a.Tok] = arr = new string?[XmlLite.DecodeText(_p.Toks[a.Tok].Raw).Length];
                    arr[a.Off] = Fmt.RprWith(RunRaw(a.Run), origFmts[k], want, _w);
                }
            }
            foreach (var ins in d.Inserts)
            {
                int k = ins.At;
                int ai; bool afterAtom;
                if (k > 0 && d.OldToNew[k - 1] < 0) { ai = k - 1; afterAtom = true; }
                else if (k < n && d.OldToNew[k] < 0) { ai = k; afterAtom = false; }
                else if (k > 0) { ai = k - 1; afterAtom = true; }
                else { ai = 0; afterAtom = false; }
                var a = pi.Atoms[ai];
                var host = origFmts[ai];
                var raw = RunRaw(a.Run);
                var seq = new List<(char, string?)>();
                for (int j = ins.From; j < ins.To; j++)
                {
                    var f = At(e.Fmts, j, host);
                    seq.Add((e.Text[j], f == host ? null : Fmt.RprWith(raw, host, f, _w)));
                }
                if (a.Off >= 0)
                {
                    int pos = afterAtom ? a.Off + 1 : a.Off;
                    if (!_p.TIns.TryGetValue(a.Tok, out var m)) _p.TIns[a.Tok] = m = new();
                    if (!m.TryGetValue(pos, out var lst)) m[pos] = lst = new();
                    lst.AddRange(seq);
                }
                else
                {
                    var xml = _p.TOpen() + _p.EmitInRun(seq, raw) + _p.TClose();
                    (afterAtom ? _p.After(a.Tok) : _p.Before(a.Tok)).Append(xml);
                }
            }
        }

        // ------------------------------- tabel
        private string TcPrXml(int tplCell, int width, int? fill)
        {
            var c = tplCell >= 0 && tplCell < _p.Cells.Count ? _p.Cells[tplCell] : null;
            var raw = c?.TcPr != null ? _p.CopyRange(c.TcPr, new HashSet<string> { "vMerge", "tcPrChange", "gridSpan" }) : null;
            var e = (raw != null ? XmlLite.ParseElem(raw) : null) ?? new XmlLite.Elem("<" + _p.Wq("tcPr") + ">", "</" + _p.Wq("tcPr") + ">", new(), _p.Wq("tcPr"));
            e.Children.RemoveAll(x => XmlLite.Local(x.Key) == "tcW");
            e.Children.Insert(0, new(_p.Wq("tcW"), "<" + _p.Wq("tcW") + " " + Fmt.Q(_w, "w") + "=\"" + width + "\" " + Fmt.Q(_w, "type") + "=\"dxa\"/>"));
            if (fill != null && !e.Children.Any(x => XmlLite.Local(x.Key) == "shd"))
                e.Children.Add(new(_p.Wq("shd"), "<" + _p.Wq("shd") + " " + Fmt.Q(_w, "val") + "=\"clear\" " + Fmt.Q(_w, "color") + "=\"auto\" " + Fmt.Q(_w, "fill") + "=\"" + Fmt.Hex(fill.Value) + "\"/>"));
            var sb = new StringBuilder(e.Open); foreach (var ch in e.Children) sb.Append(ch.Value); return sb.Append(e.Close).ToString();
        }

        private string CellParasXml(ECell c) => c.Paras.Count == 0 ? "<" + _p.Wq("p") + "/>" : string.Concat(c.Paras.Select(NewParaXml));
        private string NewCellXml(ECell c, int width) => "<" + _p.Wq("tc") + ">" + TcPrXml(c.TplCell, width, c.Fill) + CellParasXml(c) + "</" + _p.Wq("tc") + ">";
        private static int GridAt(List<int> grid, int i) => i >= 0 && i < grid.Count ? grid[i] : grid.Count > 0 ? grid[^1] : 2000;

        private string NewRowXml(ERow r, List<int> grid)
        {
            var tplRow = r.TplRow >= 0 && r.TplRow < _p.RowsI.Count ? _p.RowsI[r.TplRow] : null;
            var trPr = tplRow?.TrPr != null ? _p.CopyRange(tplRow.TrPr, new HashSet<string> { "ins", "del", "trPrChange", "tblHeader" }) : "";
            var sb = new StringBuilder("<" + _p.Wq("tr") + ">").Append(trPr);
            for (int i = 0; i < r.Cells.Count; i++) sb.Append(NewCellXml(r.Cells[i], GridAt(grid, i)));
            return sb.Append("</" + _p.Wq("tr") + ">").ToString();
        }

        private string NewTableXml(ETable t)
        {
            string W(string l) => _p.Wq(l);
            string A(string l) => Fmt.Q(_w, l);
            // tabel baru tanpa garis (mis. kolom tanda tangan) memakai val="nil"
            var bv = t.Bordered ? "single" : "nil";
            var bd = string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(s => "<" + W(s) + " " + A("val") + "=\"" + bv + "\" " + A("sz") + "=\"4\" " + A("space") + "=\"0\" " + A("color") + "=\"000000\"/>"));
            var sb = new StringBuilder("<" + W("tbl") + "><" + W("tblPr") + "><" + W("tblW") + " " + A("w") + "=\"" + t.Grid.Sum() + "\" " + A("type") + "=\"dxa\"/>")
                .Append("<" + W("tblBorders") + ">" + bd + "</" + W("tblBorders") + "><" + W("tblLayout") + " " + A("type") + "=\"fixed\"/>")
                .Append("<" + W("tblCellMar") + "><" + W("left") + " " + A("w") + "=\"108\" " + A("type") + "=\"dxa\"/><" + W("right") + " " + A("w") + "=\"108\" " + A("type") + "=\"dxa\"/></" + W("tblCellMar") + ">")
                .Append("</" + W("tblPr") + "><" + W("tblGrid") + ">");
            foreach (var g in t.Grid) sb.Append("<" + W("gridCol") + " " + A("w") + "=\"" + g + "\"/>");
            sb.Append("</" + W("tblGrid") + ">");
            foreach (var r in t.Rows)
            {
                sb.Append("<" + W("tr") + ">");
                for (int i = 0; i < r.Cells.Count; i++)
                {
                    var c = r.Cells[i];
                    sb.Append("<" + W("tc") + "><" + W("tcPr") + "><" + W("tcW") + " " + A("w") + "=\"" + GridAt(t.Grid, i) + "\" " + A("type") + "=\"dxa\"/>");
                    if (c.Fill != null) sb.Append("<" + W("shd") + " " + A("val") + "=\"clear\" " + A("color") + "=\"auto\" " + A("fill") + "=\"" + Fmt.Hex(c.Fill.Value) + "\"/>");
                    sb.Append("</" + W("tcPr") + ">").Append(CellParasXml(c)).Append("</" + W("tc") + ">");
                }
                sb.Append("</" + W("tr") + ">");
            }
            return sb.Append("</" + W("tbl") + ">").ToString();
        }

        private void EditTable(TInfo ti, ETable t)
        {
            bool gridChanged = !t.Grid.SequenceEqual(ti.Grid);
            if (gridChanged && ti.GridEnd >= 0)
            {
                foreach (var g in ti.GridToks) _p.DelRange(new Rng(g, g));
                _p.Before(ti.GridEnd).Append(string.Concat(t.Grid.Select(x => "<" + _p.Wq("gridCol") + " " + Fmt.Q(_w, "w") + "=\"" + x + "\"/>")));
            }
            Flush(t.Rows, r => r.Origin >= 0 ? new Rng(_p.RowsI[r.Origin].Start, _p.RowsI[r.Origin].End) : null, r => NewRowXml(r, t.Grid), ti.End);
            foreach (var r in t.Rows)
            {
                if (r.Origin < 0) continue;
                Flush(r.Cells, c => c.Origin >= 0 ? new Rng(_p.Cells[c.Origin].Start, _p.Cells[c.Origin].End) : null, c => NewCellXml(c, GridAt(t.Grid, r.Cells.IndexOf(c))), _p.RowsI[r.Origin].End);
                for (int ci = 0; ci < r.Cells.Count; ci++)
                {
                    var c = r.Cells[ci];
                    if (c.Origin < 0) continue;
                    var info = _p.Cells[c.Origin];
                    if (gridChanged && t.Simple && info.TcW >= 0)
                    {
                        var tok = _p.Toks[info.TcW]; var at = XmlLite.Attrs(tok.Raw);
                        XmlLite.Put(at, Fmt.Q(_w, "w"), GridAt(t.Grid, ci).ToString(CultureInfo.InvariantCulture)); XmlLite.Put(at, Fmt.Q(_w, "type"), "dxa");
                        tok.Raw = XmlLite.Tag(tok.Name, at, true);
                    }
                    Flush(c.Paras, e => e.Origin >= 0 ? new Rng(_p.Paras[e.Origin].Start, _p.Paras[e.Origin].End) : null, NewParaXml, info.End);
                    foreach (var e in c.Paras) if (e.Origin >= 0) EditPara(_p.Paras[e.Origin], e);
                }
            }
        }

        // ------------------------------- gambar
        private string ImageXml(EImage img)
        {
            _imgN++;
            var ext = img.Ext.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
            var rId = "rIdDpImg" + (DateTime.UtcNow.Ticks % 100000) + _imgN;
            var file = "depup_img_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" + _imgN + "." + ext;
            _parts.Media.Add(new PartWriter.MediaItem(rId, file, ext, img.Bytes));
            long textW = (long)(_doc.Page?.TextWidth ?? 9026) * 635;
            long cx = (long)(textW * Math.Clamp(img.WidthFrac, 0.1f, 1f));
            long cy = (long)(cx * (double)img.HPx / Math.Max(1, img.WPx));
            int id = 30000 + _imgN;
            return "<" + _p.Wq("p") + "><" + _p.Wq("pPr") + "><" + _p.Wq("jc") + " " + Fmt.Q(_w, "val") + "=\"center\"/></" + _p.Wq("pPr") + "><" + _p.Wq("r") + "><" + _p.Wq("drawing") + ">" +
                "<wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                "<wp:extent cx=\"" + cx + "\" cy=\"" + cy + "\"/><wp:docPr id=\"" + id + "\" name=\"Gambar " + id + "\"/>" +
                "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"" + id + "\" name=\"" + file + "\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                "<pic:blipFill><a:blip xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:embed=\"" + rId + "\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>" +
                "</a:graphicData></a:graphic></wp:inline></" + _p.Wq("drawing") + "></" + _p.Wq("r") + "></" + _p.Wq("p") + ">";
        }
    }

    // =============================================================================================
    // Diff karakter (Myers) dengan pemetaan posisi lama -> baru
    // =============================================================================================
    internal static class TextDiff
    {
        private const int MAX_D = 1500;
        public sealed record Ins(int At, int From, int To);
        public sealed record Result(int[] OldToNew, List<Ins> Inserts);

        public static Result Align(string a, string b)
        {
            var map = Enumerable.Repeat(-1, a.Length).ToArray();
            var ins = new List<Ins>();
            int pre = 0;
            while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
            if (pre > 0 && pre < a.Length && char.IsHighSurrogate(a[pre - 1])) pre--;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
            if (suf > 0 && char.IsLowSurrogate(a[a.Length - suf])) suf--;
            for (int k = 0; k < pre; k++) map[k] = k;
            for (int k = 0; k < suf; k++) map[a.Length - suf + k] = b.Length - suf + k;
            var aa = a.Substring(pre, a.Length - suf - pre);
            var bb = b.Substring(pre, b.Length - suf - pre);
            if (aa.Length == 0 && bb.Length == 0) return new Result(map, ins);
            if (aa.Length == 0) { ins.Add(new Ins(pre, pre, pre + bb.Length)); return new Result(map, ins); }
            if (bb.Length == 0) return new Result(map, ins);
            var ops = Myers(aa, bb);
            if (ops == null) { ins.Add(new Ins(pre, pre, pre + bb.Length)); return new Result(map, ins); }
            int curAt = -1, curFrom = -1, curTo = -1;
            foreach (var op in ops)
            {
                if (op[0] == 0) map[pre + op[1]] = pre + op[2];
                else if (op[0] == 2)
                {
                    int at = pre + op[1], y = pre + op[2];
                    if (at == curAt && y == curTo) curTo++;
                    else { if (curAt >= 0) ins.Add(new Ins(curAt, curFrom, curTo)); curAt = at; curFrom = y; curTo = y + 1; }
                }
            }
            if (curAt >= 0) ins.Add(new Ins(curAt, curFrom, curTo));
            return new Result(map, ins);
        }

        private static List<int[]>? Myers(string a, string b)
        {
            int n = a.Length, m = b.Length, max = n + m, off = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();
            int found = -1;
            for (int d = 0; d <= max && found < 0; d++)
            {
                if (d > MAX_D) return null;
                trace.Add(v[(off - d - 1)..(off + d + 2)]);
                for (int k = -d; k <= d; k += 2)
                {
                    int x = (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) ? v[off + k + 1] : v[off + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                    v[off + k] = x;
                    if (x >= n && y >= m) { found = d; break; }
                }
            }
            if (found < 0) return null;
            var ops = new List<int[]>();
            int xx = n, yy = m;
            for (int d = found; d >= 1; d--)
            {
                var snap = trace[d];
                int Vv(int k) => snap[k + d + 1];
                int k = xx - yy;
                int prevK = (k == -d || (k != d && Vv(k - 1) < Vv(k + 1))) ? k + 1 : k - 1;
                int prevX = Vv(prevK), prevY = prevX - prevK;
                while (xx > prevX && yy > prevY) { xx--; yy--; ops.Add(new[] { 0, xx, yy }); }
                if (xx == prevX) ops.Add(new[] { 2, xx, prevY }); else ops.Add(new[] { 1, prevX, -1 });
                xx = prevX; yy = prevY;
            }
            while (xx > 0 && yy > 0) { xx--; yy--; ops.Add(new[] { 0, xx, yy }); }
            ops.Reverse();
            return ops;
        }
    }
}
