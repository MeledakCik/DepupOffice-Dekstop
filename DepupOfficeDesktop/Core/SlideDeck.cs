using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace DepupOfficeDesktop.Core;

public enum SlideItemKind { Text, Image, Shape }
public enum SlideShape { Rect, RoundRect, Ellipse, Triangle, Arrow }
public enum SlideLayout { Title, TitleContent, TwoContent, TitleOnly, Section, Blank }

/// <summary>Satu elemen di slide. Koordinat dalam satuan slide (1 = 1 px pada 96 dpi; slide 16:9 = 1280×720).</summary>
public sealed class SlideItem
{
    public SlideItemKind Kind;
    public RectangleF Box;
    public string Text = "";
    public float Size = 18f;                 // pt
    public bool Bold, Italic, Underline, Bullets;
    public int Align;                        // 0 kiri, 1 tengah, 2 kanan
    public int VAlign;                       // 0 atas, 1 tengah, 2 bawah
    public string Font = "Segoe UI";
    public Color Color = Color.Black;
    public Color Fill = Color.Empty;         // Empty = tanpa isian
    public Color Line = Color.Empty;         // Empty = tanpa garis
    public SlideShape Shape;
    public byte[]? Image;                    // PNG/JPEG; tidak diubah setelah dipasang (aman dibagi antar-salinan)
    public string ImageExt = "png";
    public string Role = "";                 // "title" / "body" (petunjuk saat kosong) / "deco" (hiasan tata letak)

    public SlideItem Clone() => (SlideItem)MemberwiseClone();
    public bool HasText => Kind != SlideItemKind.Image;
}

public sealed class Slide
{
    public List<SlideItem> Items = new();
    public Color Background = Color.White;
    public Slide Clone() => new() { Background = Background, Items = Items.Select(i => i.Clone()).ToList() };
}

/// <summary>Tema presentasi: latar, warna judul &amp; isi, aksen, huruf.</summary>
public sealed record DeckTheme(string Name, Color Bg, Color Title, Color Text, Color Accent, string Font)
{
    private static Color H(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly DeckTheme[] All =
    {
        new("Depup", H("#FFFFFF"), H("#111111"), H("#3A3A3A"), H("#1652F6"), "Segoe UI"),
        new("Malam", H("#111318"), H("#FFFFFF"), H("#C9CDD6"), H("#7AA2FF"), "Segoe UI"),
        new("Kertas", H("#F7F3EA"), H("#3A2F25"), H("#5A4D40"), H("#B0472F"), "Georgia"),
        new("Sage", H("#F1F6F2"), H("#1E3A2B"), H("#3F5A4A"), H("#35754C"), "Segoe UI"),
        new("Biru", H("#1652F6"), H("#FFFFFF"), H("#E3EAFF"), H("#F5A524"), "Segoe UI"),
    };
}

/// <summary>
/// Presentasi yang bisa disunting (padanan WPS Presentation): model slide, penggambar GDI+ (dipakai editor, thumbnail,
/// slideshow &amp; ekspor PDF sehingga hasilnya identik), penulis .pptx standar OOXML, dan pembaca .pptx.
/// </summary>
public sealed class SlideDeck
{
    public const float EmuPerUnit = 9525f;   // 914400 EMU per inci / 96
    public float W = 1280, H = 720;
    public List<Slide> Slides = new();
    public int ThemeIndex;
    /// <summary>Elemen berkas asli yang tidak dapat disunting (tabel, grafik, grup, SmartArt…) dan tidak ikut tersimpan.</summary>
    public int Unsupported;
    /// <summary>Dibuka dari .pptx buatan aplikasi lain: simpan sebagai salinan agar berkas asli tetap utuh.</summary>
    public bool Foreign;

    public DeckTheme Theme => DeckTheme.All[Math.Clamp(ThemeIndex, 0, DeckTheme.All.Length - 1)];

    public SlideDeck Clone() => new()
    {
        W = W, H = H, ThemeIndex = ThemeIndex, Unsupported = Unsupported, Foreign = Foreign,
        Slides = Slides.Select(s => s.Clone()).ToList()
    };

    // ===================================================================== MEMBUAT

    public const string TEMPLATE_BLANK = "blank", TEMPLATE_SCHOOL = "tugas", TEMPLATE_PITCH = "usaha";

    public static SlideDeck Create(string template = TEMPLATE_BLANK, int theme = 0)
    {
        var d = new SlideDeck { ThemeIndex = theme };
        switch (template)
        {
            case TEMPLATE_SCHOOL:
                d.Slides.Add(d.NewSlide(SlideLayout.Title, "Judul Presentasi", "Nama · Kelas / Program studi · Mata pelajaran"));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Pendahuluan", "Latar belakang\nRumusan masalah\nTujuan"));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Pembahasan", "Poin utama pertama\nPoin utama kedua\nContoh atau data pendukung"));
                d.Slides.Add(d.NewSlide(SlideLayout.TwoContent, "Perbandingan", "Kelebihan\nPoin 1\nPoin 2", "Kekurangan\nPoin 1\nPoin 2"));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Kesimpulan", "Ringkasan jawaban rumusan masalah\nSaran"));
                d.Slides.Add(d.NewSlide(SlideLayout.Section, "Terima kasih", "Ada pertanyaan?"));
                break;
            case TEMPLATE_PITCH:
                d.Slides.Add(d.NewSlide(SlideLayout.Title, "Nama Usaha", "Proposal usaha · " + DateTime.Now.Year));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Masalah", "Apa yang dialami pelanggan\nSeberapa sering & seberapa mahal"));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Solusi", "Produk / layanan kami\nKeunggulan dibanding cara lama"));
                d.Slides.Add(d.NewSlide(SlideLayout.TitleContent, "Pasar & pelanggan", "Siapa pembelinya\nPerkiraan jumlah pembeli\nCara menjangkau"));
                d.Slides.Add(d.NewSlide(SlideLayout.TwoContent, "Rencana biaya", "Modal awal\nPeralatan\nBahan baku", "Target\nPenjualan per bulan\nBalik modal"));
                d.Slides.Add(d.NewSlide(SlideLayout.Section, "Terima kasih", "Kontak: nama · nomor HP · email"));
                break;
            default:
                d.Slides.Add(d.NewSlide(SlideLayout.Title));
                break;
        }
        return d;
    }

    private SlideItem TextItem(RectangleF box, string text, float size, bool bold, Color color, string role, int align = 0, int valign = 0, bool bullets = false) => new()
    {
        Kind = SlideItemKind.Text, Box = box, Text = text, Size = size, Bold = bold, Color = color,
        Font = Theme.Font, Role = role, Align = align, VAlign = valign, Bullets = bullets
    };

    /// <summary>Slide baru dengan tata letak tertentu (teks kosong = petunjuk "klik dua kali" di editor).</summary>
    public Slide NewSlide(SlideLayout layout, string title = "", string body = "", string body2 = "")
    {
        var t = Theme;
        float sx = W / 1280f, mx = 80 * sx, cw = W - 2 * mx;
        RectangleF R(float x, float y, float w, float h) => new(x, y, w, h);
        var s = new Slide { Background = t.Bg };
        switch (layout)
        {
            case SlideLayout.Title:
                s.Items.Add(TextItem(R(mx, H * 0.30f, cw, H * 0.20f), title, 44, true, t.Title, "title", 1, 2));
                s.Items.Add(new SlideItem { Kind = SlideItemKind.Shape, Shape = SlideShape.Rect, Box = R(W / 2 - 50, H * 0.53f, 100, 6), Fill = t.Accent, Role = "deco" });
                s.Items.Add(TextItem(R(mx, H * 0.57f, cw, H * 0.12f), body, 22, false, t.Text, "body", 1));
                break;
            case SlideLayout.TitleContent:
                s.Items.Add(TextItem(R(mx, 48, cw, 92), title, 36, true, t.Title, "title", 0, 2));
                s.Items.Add(TextItem(R(mx, 160, cw, H - 220), body, 24, false, t.Text, "body", bullets: true));
                break;
            case SlideLayout.TwoContent:
            {
                float half = (cw - 40) / 2;
                s.Items.Add(TextItem(R(mx, 48, cw, 92), title, 36, true, t.Title, "title", 0, 2));
                s.Items.Add(TextItem(R(mx, 160, half, H - 220), body, 22, false, t.Text, "body", bullets: true));
                s.Items.Add(TextItem(R(mx + half + 40, 160, half, H - 220), body2, 22, false, t.Text, "body", bullets: true));
                break;
            }
            case SlideLayout.TitleOnly:
                s.Items.Add(TextItem(R(mx, 48, cw, 92), title, 36, true, t.Title, "title", 0, 2));
                break;
            case SlideLayout.Section:
                s.Items.Add(new SlideItem { Kind = SlideItemKind.Shape, Shape = SlideShape.Rect, Box = R(mx, H * 0.36f, 8, H * 0.28f), Fill = t.Accent, Role = "deco" });
                s.Items.Add(TextItem(R(mx + 32, H * 0.34f, cw - 32, H * 0.18f), title, 44, true, t.Title, "title", 0, 2));
                s.Items.Add(TextItem(R(mx + 32, H * 0.52f, cw - 32, H * 0.12f), body, 22, false, t.Text, "body"));
                break;
        }
        return s;
    }

    /// <summary>Ganti tema: warna &amp; huruf yang masih mengikuti tema lama ikut berganti; warna pilihan sendiri dibiarkan.</summary>
    public void ApplyTheme(int index)
    {
        var o = Theme;
        ThemeIndex = Math.Clamp(index, 0, DeckTheme.All.Length - 1);
        var n = Theme;
        Color Map(Color c) =>
            c.IsEmpty ? c : c.ToArgb() == o.Bg.ToArgb() ? n.Bg : c.ToArgb() == o.Title.ToArgb() ? n.Title
            : c.ToArgb() == o.Text.ToArgb() ? n.Text : c.ToArgb() == o.Accent.ToArgb() ? n.Accent : c;
        foreach (var s in Slides)
        {
            s.Background = Map(s.Background);
            foreach (var i in s.Items)
            {
                i.Color = Map(i.Color); i.Fill = Map(i.Fill); i.Line = Map(i.Line);
                if (i.Font == o.Font) i.Font = n.Font;
            }
        }
    }

    // ===================================================================== MENGGAMBAR

    private static readonly HashSet<string> Installed = new(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<byte[], Image> ImageCache = new();

    internal static Font MakeFont(SlideItem i, float scale = 1f)
    {
        var family = Installed.Contains(i.Font) ? i.Font : "Segoe UI";
        var style = (i.Bold ? FontStyle.Bold : 0) | (i.Italic ? FontStyle.Italic : 0) | (i.Underline ? FontStyle.Underline : 0);
        float px = Math.Max(1f, i.Size * 96f / 72f * scale);
        try { return new Font(family, px, style, GraphicsUnit.Pixel); }
        catch (ArgumentException) { return new Font("Segoe UI", px, style, GraphicsUnit.Pixel); }
    }

    /// <summary>Teks yang benar-benar digambar (poin "•" di depan tiap baris berisi).</summary>
    internal static string DisplayText(SlideItem i) =>
        !i.Bullets ? i.Text : string.Join("\n", i.Text.Replace("\r", "").Split('\n').Select(l => l.Trim().Length == 0 ? l : "•  " + l));

    internal static StringFormat Format(SlideItem i) => new()
    {
        Alignment = i.Align switch { 1 => StringAlignment.Center, 2 => StringAlignment.Far, _ => StringAlignment.Near },
        LineAlignment = i.VAlign switch { 1 => StringAlignment.Center, 2 => StringAlignment.Far, _ => StringAlignment.Near },
        Trimming = StringTrimming.None
    };

    /// <summary>Jarak teks ke tepi kotak (sama dengan bawaan PowerPoint: 0,1" kiri/kanan, 0,05" atas/bawah).</summary>
    internal static RectangleF TextArea(RectangleF box) => RectangleF.FromLTRB(box.Left + 9.6f, box.Top + 4.8f, box.Right - 9.6f, box.Bottom - 4.8f);

    private static Image? ImageOf(byte[] bytes)
    {
        if (ImageCache.TryGetValue(bytes, out var img)) return img;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var raw = System.Drawing.Image.FromStream(ms);
            img = new Bitmap(raw);   // salinan: tidak bergantung pada stream
            ImageCache.AddOrUpdate(bytes, img);
            return img;
        }
        catch { return null; }
    }

    internal static GraphicsPath ShapePath(SlideShape shape, RectangleF r)
    {
        var p = new GraphicsPath();
        switch (shape)
        {
            case SlideShape.Ellipse: p.AddEllipse(r); break;
            case SlideShape.RoundRect:
            {
                float d = Math.Min(r.Width, r.Height) * 0.33f;
                if (d < 1) { p.AddRectangle(r); break; }
                p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                break;
            }
            case SlideShape.Triangle:
                p.AddPolygon(new[] { new PointF(r.X + r.Width / 2, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) });
                break;
            case SlideShape.Arrow:
            {
                // sama dengan prstGeom "rightArrow" bawaan (adj1 50 %, adj2 50 %)
                float head = Math.Min(r.Width, r.Height * 0.5f * 2) * 0.5f, y1 = r.Y + r.Height * 0.25f, y2 = r.Y + r.Height * 0.75f;
                p.AddPolygon(new[]
                {
                    new PointF(r.X, y1), new PointF(r.Right - head, y1), new PointF(r.Right - head, r.Y), new PointF(r.Right, r.Y + r.Height / 2),
                    new PointF(r.Right - head, r.Bottom), new PointF(r.Right - head, y2), new PointF(r.X, y2)
                });
                break;
            }
            default: p.AddRectangle(r); break;
        }
        return p;
    }

    public static string Hint(SlideItem i) => i.Role switch
    {
        "title" => "Klik dua kali untuk menambah judul",
        "body" => "Klik dua kali untuk menambah teks",
        _ => "Klik dua kali untuk mengetik"
    };

    /// <summary>Gambar satu slide ke <paramref name="dest"/>. <paramref name="hints"/> = petunjuk teks kosong (hanya di editor).</summary>
    public void Render(Graphics g, Slide s, RectangleF dest, bool hints = false, SlideItem? skipText = null)
    {
        float k = dest.Width / W;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var bg = new SolidBrush(s.Background)) g.FillRectangle(bg, dest);
        var state = g.Save();
        g.SetClip(dest);
        g.TranslateTransform(dest.X, dest.Y);
        g.ScaleTransform(k, k);
        foreach (var i in s.Items) DrawItem(g, i, s.Background, hints, i == skipText);
        g.Restore(state);
    }

    private static void DrawItem(Graphics g, SlideItem i, Color slideBg, bool hints, bool skipText)
    {
        var r = i.Box;
        if (r.Width <= 0 || r.Height <= 0) return;
        if (i.Kind == SlideItemKind.Image)
        {
            if (i.Image != null && ImageOf(i.Image) is { } img) g.DrawImage(img, r);
            return;
        }
        if (i.Kind == SlideItemKind.Shape)
        {
            using var path = ShapePath(i.Shape, r);
            if (!i.Fill.IsEmpty) using (var b = new SolidBrush(i.Fill)) g.FillPath(b, path);
            if (!i.Line.IsEmpty) using (var p = new Pen(i.Line, 2f)) g.DrawPath(p, path);
        }
        else
        {
            if (!i.Fill.IsEmpty) using (var b = new SolidBrush(i.Fill)) g.FillRectangle(b, r);
            if (!i.Line.IsEmpty) using (var p = new Pen(i.Line, 2f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        }
        if (skipText) return;
        var area = TextArea(r);
        if (i.Text.Length == 0)
        {
            if (!hints || i.Kind != SlideItemKind.Text) return;
            var back = i.Fill.IsEmpty ? slideBg : i.Fill;
            var muted = Color.FromArgb(130, back.GetBrightness() < 0.5f ? Color.White : Color.Black);
            using (var pen = new Pen(muted, 1f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            var hintStyle = i.Clone();
            hintStyle.Size = Math.Min(i.Size, 28);
            hintStyle.Bold = hintStyle.Underline = false;
            using var hf = MakeFont(hintStyle);
            using var hb = new SolidBrush(muted);
            using var hfmt = Format(i);
            g.DrawString(Hint(i), hf, hb, area, hfmt);
            return;
        }
        using var font = MakeFont(i);
        using var brush = new SolidBrush(i.Color);
        using var fmt = Format(i);
        g.DrawString(DisplayText(i), font, brush, area, fmt);
    }

    public Bitmap RenderBitmap(int index, int width)
    {
        width = Math.Clamp(width, 32, 4096);
        int height = Math.Max(1, (int)Math.Round(width * H / W));
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        Render(g, Slides[index], new RectangleF(0, 0, width, height));
        return bmp;
    }

    /// <summary>Tinggi yang dibutuhkan teks pada lebar kotaknya (untuk kotak teks yang tumbuh mengikuti isi).</summary>
    public static float TextHeight(SlideItem i)
    {
        if (i.Text.Length == 0) return 0;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = MakeFont(i);
        using var fmt = Format(i);
        var area = TextArea(i.Box);
        return g.MeasureString(DisplayText(i), font, new SizeF(Math.Max(1, area.Width), 100000), fmt).Height + 9.6f + 2;
    }

    // ===================================================================== EKSPOR PDF

    public void ExportPdf(string path, Action<int, int>? progress = null)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            float pw = W * 0.75f, ph = H * 0.75f;   // satuan pt (1280×720 → 960×540 pt, 13,33×7,5 inci)
            using (var w = new PdfWriter(temp, Slides.Count))
            {
                for (int i = 0; i < Slides.Count; i++)
                {
                    progress?.Invoke(i, Slides.Count);
                    using var bmp = RenderBitmap(i, 1920);
                    var jpeg = PdfEngine.EncodeJpeg(bmp, 90);
                    w.AddPage(jpeg, bmp.Width, bmp.Height, pw, ph, 0, 0, pw, ph);
                }
                w.Finish();
            }
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    // ===================================================================== MENULIS .PPTX

    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Ns = $"xmlns:a=\"{NsA}\" xmlns:r=\"{NsR}\" xmlns:p=\"{NsP}\"";
    private const string RelT = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
    private static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";
    private static long E(float v) => (long)Math.Round(v * EmuPerUnit);
    private static string Fill(Color c) => c.IsEmpty ? "<a:noFill/>" : $"<a:solidFill><a:srgbClr val=\"{Hex(c)}\"/></a:solidFill>";

    /// <summary>Simpan sebagai .pptx standar (terbuka di PowerPoint, WPS, Google Slides, LibreOffice &amp; Reader Depup).</summary>
    public void Save(string path)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                WritePackage(zip, Path.GetFileNameWithoutExtension(path));
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private void WritePackage(ZipArchive zip, string title)
    {
        void Put(string name, string xml)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        string Rels(IEnumerable<(string id, string type, string target)> rels) =>
            Decl + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            string.Concat(rels.Select(r => $"<Relationship Id=\"{r.id}\" Type=\"{(r.type.StartsWith("http") ? r.type : RelT + r.type)}\" Target=\"{X(r.target)}\"/>")) +
            "</Relationships>";

        int n = Slides.Count;
        var ct = new StringBuilder(Decl + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            "<Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/>" +
            "<Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/>" +
            "<Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        for (int i = 1; i <= n; i++)
            ct.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        ct.Append("</Types>");
        Put("[Content_Types].xml", ct.ToString());

        Put("_rels/.rels", Rels(new[]
        {
            ("rId1", "officeDocument", "ppt/presentation.xml"),
            ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
            ("rId3", "extended-properties", "docProps/app.xml"),
        }));
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Put("docProps/core.xml", Decl + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{X(title)}</dc:title><dc:creator>DEPUP OFFICE</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>");
        Put("docProps/app.xml", Decl + "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\">" +
            $"<Application>DEPUP OFFICE</Application><PresentationFormat>Custom</PresentationFormat><Slides>{n}</Slides></Properties>");

        // ---- presentation
        var pres = new StringBuilder(Decl + $"<p:presentation {Ns} saveSubsetFonts=\"1\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>");
        for (int i = 0; i < n; i++) pres.Append($"<p:sldId id=\"{256 + i}\" r:id=\"rId{6 + i}\"/>");
        pres.Append($"</p:sldIdLst><p:sldSz cx=\"{E(W)}\" cy=\"{E(H)}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        Put("ppt/presentation.xml", pres.ToString());
        var presRels = new List<(string, string, string)>
        {
            ("rId1", "slideMaster", "slideMasters/slideMaster1.xml"), ("rId2", "theme", "theme/theme1.xml"),
            ("rId3", "presProps", "presProps.xml"), ("rId4", "viewProps", "viewProps.xml"), ("rId5", "tableStyles", "tableStyles.xml"),
        };
        for (int i = 0; i < n; i++) presRels.Add(($"rId{6 + i}", "slide", $"slides/slide{i + 1}.xml"));
        Put("ppt/_rels/presentation.xml.rels", Rels(presRels));
        Put("ppt/presProps.xml", Decl + $"<p:presentationPr {Ns}/>");
        Put("ppt/viewProps.xml", Decl + $"<p:viewPr {Ns}><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        Put("ppt/tableStyles.xml", Decl + $"<a:tblStyleLst xmlns:a=\"{NsA}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>");

        const string group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
        var t = Theme;
        Put("ppt/slideMasters/slideMaster1.xml", Decl + $"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgPr>{Fill(t.Bg)}<a:effectLst/></p:bgPr></p:bg>" +
            $"<p:spTree>{group}</p:spTree></p:cSld>" +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
            "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"2400\"/></a:lvl1pPr></p:bodyStyle>" +
            "<p:otherStyle><a:lvl1pPr><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>");
        Put("ppt/slideMasters/_rels/slideMaster1.xml.rels", Rels(new[] { ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("rId2", "theme", "../theme/theme1.xml") }));
        Put("ppt/slideLayouts/slideLayout1.xml", Decl + $"<p:sldLayout {Ns} type=\"blank\" preserve=\"1\"><p:cSld name=\"Kosong\"><p:spTree>{group}</p:spTree></p:cSld>" +
            "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Put("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Rels(new[] { ("rId1", "slideMaster", "../slideMasters/slideMaster1.xml") }));
        Put("ppt/theme/theme1.xml", ThemeXml(t));

        // ---- slide
        int media = 0;
        for (int si = 0; si < n; si++)
        {
            var s = Slides[si];
            var rels = new List<(string, string, string)> { ("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml") };
            var sb = new StringBuilder(Decl + $"<p:sld {Ns}><p:cSld><p:bg><p:bgPr>{Fill(s.Background)}<a:effectLst/></p:bgPr></p:bg><p:spTree>{group}");
            int id = 2;
            foreach (var i in s.Items)
            {
                string xfrm = $"<a:xfrm><a:off x=\"{E(i.Box.X)}\" y=\"{E(i.Box.Y)}\"/><a:ext cx=\"{Math.Max(1, E(i.Box.Width))}\" cy=\"{Math.Max(1, E(i.Box.Height))}\"/></a:xfrm>";
                if (i.Kind == SlideItemKind.Image)
                {
                    if (i.Image == null) continue;
                    string ext = i.ImageExt == "jpeg" ? "jpeg" : "png";
                    string name = $"image{++media}.{ext}";
                    var entry = zip.CreateEntry("ppt/media/" + name, CompressionLevel.NoCompression);
                    using (var es = entry.Open()) es.Write(i.Image);
                    string rid = $"rId{rels.Count + 1}";
                    rels.Add((rid, "image", "../media/" + name));
                    sb.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"Gambar {id}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                              $"<p:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                              $"<p:spPr>{xfrm}<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
                }
                else
                {
                    bool isText = i.Kind == SlideItemKind.Text;
                    string prst = isText ? "rect" : i.Shape switch
                    {
                        SlideShape.RoundRect => "roundRect", SlideShape.Ellipse => "ellipse",
                        SlideShape.Triangle => "triangle", SlideShape.Arrow => "rightArrow", _ => "rect"
                    };
                    // nama bentuk menyimpan peran (dibaca lagi oleh Load) — juga terlihat rapi di panel Seleksi PowerPoint
                    string name = i.Role switch { "title" => $"Judul {id}", "body" => $"Isi {id}", "deco" => $"Aksen {id}", _ => isText ? $"Kotak Teks {id}" : $"Bentuk {id}" };
                    string ln = i.Line.IsEmpty ? "<a:ln><a:noFill/></a:ln>" : $"<a:ln w=\"19050\">{Fill(i.Line)}</a:ln>";
                    sb.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr{(isText ? " txBox=\"1\"" : "")}/><p:nvPr/></p:nvSpPr>" +
                              $"<p:spPr>{xfrm}<a:prstGeom prst=\"{prst}\"><a:avLst/></a:prstGeom>{Fill(i.Fill)}{ln}</p:spPr>");
                    if (isText || i.Text.Length > 0) sb.Append(TextBody(i));
                    sb.Append("</p:sp>");
                }
                id++;
            }
            sb.Append("</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            Put($"ppt/slides/slide{si + 1}.xml", sb.ToString());
            Put($"ppt/slides/_rels/slide{si + 1}.xml.rels", Rels(rels));
        }
    }

    private static string TextBody(SlideItem i)
    {
        string anchor = i.VAlign switch { 1 => "ctr", 2 => "b", _ => "t" };
        string algn = i.Align switch { 1 => "ctr", 2 => "r", _ => "l" };
        string sz = ((int)Math.Round(i.Size * 100)).ToString(CultureInfo.InvariantCulture);
        string rpr = $"lang=\"id-ID\" sz=\"{sz}\"{(i.Bold ? " b=\"1\"" : "")}{(i.Italic ? " i=\"1\"" : "")}{(i.Underline ? " u=\"sng\"" : "")} dirty=\"0\"";
        string rprBody = $"{Fill(i.Color)}<a:latin typeface=\"{X(i.Font)}\"/><a:cs typeface=\"{X(i.Font)}\"/>";
        var sb = new StringBuilder($"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"{anchor}\"><a:noAutofit/></a:bodyPr><a:lstStyle/>");
        foreach (var line in i.Text.Replace("\r", "").Split('\n'))
        {
            bool bullet = i.Bullets && line.Trim().Length > 0;
            string ppr = bullet
                ? $"<a:pPr marL=\"342900\" indent=\"-342900\" algn=\"{algn}\"><a:buFont typeface=\"Arial\"/><a:buChar char=\"•\"/></a:pPr>"
                : $"<a:pPr algn=\"{algn}\"><a:buNone/></a:pPr>";
            sb.Append("<a:p>").Append(ppr);
            if (line.Length > 0) sb.Append($"<a:r><a:rPr {rpr}>{rprBody}</a:rPr><a:t>{X(line)}</a:t></a:r>");
            sb.Append($"<a:endParaRPr {rpr}>{rprBody}</a:endParaRPr></a:p>");
        }
        return sb.Append("</p:txBody>").ToString();
    }

    private static string ThemeXml(DeckTheme t)
    {
        string C(string name, Color c) => $"<a:{name}><a:srgbClr val=\"{Hex(c)}\"/></a:{name}>";
        string font = $"<a:latin typeface=\"{X(t.Font)}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>";
        const string ph = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        string three(string s) => s + s + s;
        return Decl + $"<a:theme xmlns:a=\"{NsA}\" name=\"{X(t.Name)}\"><a:themeElements><a:clrScheme name=\"{X(t.Name)}\">" +
               C("dk1", Color.Black) + C("lt1", Color.White) + C("dk2", t.Title) + C("lt2", t.Bg) + C("accent1", t.Accent) +
               C("accent2", ColorTranslator.FromHtml("#B0472F")) + C("accent3", ColorTranslator.FromHtml("#35754C")) +
               C("accent4", ColorTranslator.FromHtml("#72539E")) + C("accent5", ColorTranslator.FromHtml("#F5A524")) +
               C("accent6", ColorTranslator.FromHtml("#33609F")) + C("hlink", ColorTranslator.FromHtml("#1652F6")) + C("folHlink", ColorTranslator.FromHtml("#72539E")) +
               $"</a:clrScheme><a:fontScheme name=\"{X(t.Name)}\"><a:majorFont>{font}</a:majorFont><a:minorFont>{font}</a:minorFont></a:fontScheme>" +
               "<a:fmtScheme name=\"Depup\">" +
               $"<a:fillStyleLst>{three(ph)}</a:fillStyleLst>" +
               $"<a:lnStyleLst>{three($"<a:ln w=\"6350\">{ph}</a:ln>")}</a:lnStyleLst>" +
               $"<a:effectStyleLst>{three("<a:effectStyle><a:effectLst/></a:effectStyle>")}</a:effectStyleLst>" +
               $"<a:bgFillStyleLst>{three(ph)}</a:bgFillStyleLst></a:fmtScheme>" +
               "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    /// <summary>Gambar untuk slide: PNG/JPEG dipakai apa adanya, format lain diubah ke PNG.</summary>
    public static (byte[] bytes, string ext, Size size) PrepareImage(byte[] raw)
    {
        using var ms = new MemoryStream(raw);
        using var img = System.Drawing.Image.FromStream(ms);
        var size = img.Size;
        if (img.RawFormat.Equals(ImageFormat.Png)) return (raw, "png", size);
        if (img.RawFormat.Equals(ImageFormat.Jpeg)) return (raw, "jpeg", size);
        using var outMs = new MemoryStream();
        img.Save(outMs, ImageFormat.Png);
        return (outMs.ToArray(), "png", size);
    }

    // ===================================================================== MEMBACA .PPTX

    private static readonly XNamespace A = NsA, R = NsR, P = NsP;

    /// <summary>Buka .pptx untuk disunting: teks, bentuk, dan gambar menjadi elemen yang bisa diubah.</summary>
    public static SlideDeck Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Doc(string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            using var s = e.Open();
            return XDocument.Load(s);
        }
        static string Resolve(string baseFile, string target)
        {
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = (Path.GetDirectoryName(baseFile)?.Replace('\\', '/') ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var seg in target.Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != ".") parts.Add(seg);
            }
            return string.Join("/", parts);
        }
        Dictionary<string, (string type, string target)> RelsOf(string part)
        {
            string dir = Path.GetDirectoryName(part)?.Replace('\\', '/') ?? "";
            var doc = Doc($"{dir}/_rels/{Path.GetFileName(part)}.rels");
            var map = new Dictionary<string, (string, string)>();
            if (doc == null) return map;
            foreach (var r in doc.Root!.Elements())
                map[(string?)r.Attribute("Id") ?? ""] = (((string?)r.Attribute("Type") ?? "").Split('/').Last(), Resolve(part, (string?)r.Attribute("Target") ?? ""));
            return map;
        }

        const string presPart = "ppt/presentation.xml";
        var pres = Doc(presPart) ?? throw new InvalidDataException("Berkas ini bukan presentasi PowerPoint (.pptx) yang valid.");
        var presRels = RelsOf(presPart);
        var d = new SlideDeck();
        var sz = pres.Root!.Element(P + "sldSz");
        if (sz != null && long.TryParse((string?)sz.Attribute("cx"), out var cx) && long.TryParse((string?)sz.Attribute("cy"), out var cy) && cx > 0 && cy > 0)
        { d.W = cx / EmuPerUnit; d.H = cy / EmuPerUnit; }

        var app = Doc("docProps/app.xml");
        d.Foreign = !string.Equals(app?.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Application")?.Value, "DEPUP OFFICE", StringComparison.Ordinal);

        // tema: warna skema & huruf mayor/minor
        var scheme = new Dictionary<string, Color>();
        string major = "Segoe UI", minor = "Segoe UI";
        var themeRel = presRels.Values.FirstOrDefault(r => r.type == "theme");
        if (themeRel.target != null && Doc(themeRel.target) is { } th)
        {
            var cs = th.Descendants(A + "clrScheme").FirstOrDefault();
            if (cs != null)
                foreach (var c in cs.Elements())
                {
                    var v = c.Element(A + "srgbClr")?.Attribute("val") ?? c.Element(A + "sysClr")?.Attribute("lastClr");
                    if (v != null && TryHex((string)v, out var col)) scheme[c.Name.LocalName] = col;
                }
            major = (string?)th.Descendants(A + "majorFont").Elements(A + "latin").FirstOrDefault()?.Attribute("typeface") ?? major;
            minor = (string?)th.Descendants(A + "minorFont").Elements(A + "latin").FirstOrDefault()?.Attribute("typeface") ?? minor;
            string themeName = (string?)th.Root?.Attribute("name") ?? "";
            int ti = Array.FindIndex(DeckTheme.All, x => x.Name == themeName);
            if (!d.Foreign && ti >= 0) d.ThemeIndex = ti;
        }
        Color Scheme(string key, Color fallback)
        {
            key = key switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => key };
            return scheme.TryGetValue(key, out var c) ? c : fallback;
        }
        // warna DrawingML: srgbClr / schemeClr / sysClr + pengubah lumMod, lumOff, shade, tint (dipakai tema Office)
        Color? ColorOf(XElement? clr)
        {
            if (clr == null) return null;
            Color c;
            if (clr.Name == A + "srgbClr" && TryHex((string?)clr.Attribute("val") ?? "", out var rgb)) c = rgb;
            else if (clr.Name == A + "schemeClr" && (string?)clr.Attribute("val") is { } key) c = Scheme(key, Color.Black);
            else if (clr.Name == A + "sysClr" && TryHex((string?)clr.Attribute("lastClr") ?? "", out var sys)) c = sys;
            else return null;
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            foreach (var m in clr.Elements())
            {
                double v = double.TryParse((string?)m.Attribute("val"), System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw) ? raw / 100000.0 : 1;
                switch (m.Name.LocalName)
                {
                    case "shade": r *= v; g *= v; b *= v; break;
                    case "tint": r += (1 - r) * (1 - v); g += (1 - g) * (1 - v); b += (1 - b) * (1 - v); break;
                    case "lumMod": r *= v; g *= v; b *= v; break;
                    case "lumOff": r += v; g += v; b += v; break;
                }
            }
            static int B(double x) => (int)Math.Round(Math.Clamp(x, 0, 1) * 255);
            return Color.FromArgb(255, B(r), B(g), B(b));
        }
        Color? ColorIn(XElement? holder)
        {
            if (holder == null) return null;
            var fill = holder.Element(A + "solidFill") ?? (holder.Name == A + "solidFill" ? holder : null);
            return fill == null ? null : ColorOf(fill.Elements().FirstOrDefault());
        }
        // <p:style>: warna bawaan bentuk Office (fillRef / lnRef / fontRef, idx 0 = tidak ada)
        Color? StyleRef(XElement? style, string name)
        {
            var r = style?.Element(A + name);
            if (r == null || (string?)r.Attribute("idx") is "0" or "none") return null;
            return ColorOf(r.Elements().FirstOrDefault());
        }
        string FontName(string? face) => face switch { null or "" => minor, "+mj-lt" => major, "+mn-lt" => minor, _ => face };

        foreach (var sid in pres.Root.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>())
        {
            var rid = (string?)sid.Attribute(R + "id");
            if (rid == null || !presRels.TryGetValue(rid, out var rel)) continue;
            var slidePart = rel.target;
            var sx = Doc(slidePart);
            if (sx == null) continue;
            var srels = RelsOf(slidePart);

            // tempat placeholder dari layout lalu master (posisi bawaan bila slide tidak menulis xfrm)
            var phBoxes = new Dictionary<string, RectangleF>();
            var layoutPart = srels.Values.FirstOrDefault(r => r.type == "slideLayout").target;
            XDocument? layout = layoutPart != null ? Doc(layoutPart) : null, master = null;
            if (layoutPart != null)
            {
                var masterPart = RelsOf(layoutPart).Values.FirstOrDefault(r => r.type == "slideMaster").target;
                if (masterPart != null) master = Doc(masterPart);
            }
            foreach (var src in new[] { master, layout })
                foreach (var sp in src?.Descendants(P + "sp") ?? Enumerable.Empty<XElement>())
                    if (sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph") is { } ph && XfrmOf(sp.Element(P + "spPr")) is { } box)
                    {
                        phBoxes["t:" + PhType(ph)] = box;
                        if ((string?)ph.Attribute("idx") is { } idx) phBoxes["i:" + idx] = box;
                    }

            var slide = new Slide { Background = Color.White };
            Color? bg = null;
            foreach (var src in new[] { sx, layout, master })
            {
                var bgPr = src?.Root?.Element(P + "cSld")?.Element(P + "bg");
                if (bgPr == null) continue;
                bg = ColorIn(bgPr.Element(P + "bgPr")) ?? (bgPr.Element(P + "bgRef") is { } bref ? ColorIn(new XElement(A + "x", new XElement(A + "solidFill", bref.Elements()))) : null);
                if (bg != null) break;
            }
            slide.Background = bg ?? Scheme("bg1", Color.White);
            var defaultText = slide.Background.GetBrightness() < 0.45f ? Color.White : Scheme("tx1", Color.Black);

            var tree = sx.Root!.Element(P + "cSld")?.Element(P + "spTree");
            foreach (var el in tree?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (el.Name == P + "nvGrpSpPr" || el.Name == P + "grpSpPr") continue;
                if (el.Name == P + "pic")
                {
                    var embed = (string?)el.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
                    var box = XfrmOf(el.Element(P + "spPr"));
                    if (embed == null || box == null || !srels.TryGetValue(embed, out var mr) || zip.GetEntry(mr.target) is not { } me) { d.Unsupported++; continue; }
                    byte[] raw;
                    using (var ms = new MemoryStream()) { using (var es = me.Open()) es.CopyTo(ms); raw = ms.ToArray(); }
                    try
                    {
                        var (bytes, ext, _) = PrepareImage(raw);
                        slide.Items.Add(new SlideItem { Kind = SlideItemKind.Image, Box = box.Value, Image = bytes, ImageExt = ext });
                    }
                    catch { d.Unsupported++; }
                    continue;
                }
                if (el.Name != P + "sp") { d.Unsupported++; continue; }

                var phEl = el.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
                string phType = phEl != null ? PhType(phEl) : "";
                var spPr = el.Element(P + "spPr");
                var rect = XfrmOf(spPr);
                if (rect == null && phEl != null)
                {
                    if ((string?)phEl.Attribute("idx") is { } idx && phBoxes.TryGetValue("i:" + idx, out var b1)) rect = b1;
                    else if (phBoxes.TryGetValue("t:" + phType, out var b2)) rect = b2;
                    else if (phType is "ctrTitle" or "subTitle" or "body" && phBoxes.TryGetValue(phType == "ctrTitle" ? "t:title" : "t:body", out var b3)) rect = b3;
                }
                if (rect == null) { d.Unsupported++; continue; }
                if (phType is "dt" or "ftr" or "sldNum") continue;   // tanggal / footer / nomor slide bawaan

                var paras = el.Element(P + "txBody")?.Elements(A + "p").ToList() ?? new List<XElement>();
                string text = string.Join("\n", paras.Select(p => string.Concat(p.Elements().Select(r =>
                    r.Name == A + "br" ? "\n" : string.Concat(r.Descendants(A + "t").Select(t => t.Value))))));
                bool isTitle = phType is "title" or "ctrTitle";
                string prst = (string?)spPr?.Element(A + "prstGeom")?.Attribute("prst") ?? "rect";
                var style = el.Element(P + "style");
                var fill = spPr?.Element(A + "noFill") != null ? null : ColorIn(spPr) ?? StyleRef(style, "fillRef");
                var lnEl = spPr?.Element(A + "ln");
                var line = lnEl?.Element(A + "noFill") != null ? null : ColorIn(lnEl) ?? StyleRef(style, "lnRef");
                bool shape = fill != null || line != null || prst != "rect";
                if (!shape && text.Trim().Length == 0 && phEl == null) continue;

                var firstR = paras.SelectMany(p => p.Elements(A + "r")).FirstOrDefault()?.Element(A + "rPr")
                             ?? paras.Select(p => p.Element(A + "endParaRPr")).FirstOrDefault(x => x != null);
                var firstP = paras.Select(p => p.Element(A + "pPr")).FirstOrDefault(x => x != null);
                bool bodyPh = phEl != null && phType is "body" or "subTitle" or "obj";
                float size = firstR?.Attribute("sz") is { } szA && int.TryParse((string)szA, out var szv) ? szv / 100f
                    : isTitle ? (phType == "ctrTitle" ? 44 : 40) : bodyPh ? 24 : 18;
                var bodyPr = el.Element(P + "txBody")?.Element(A + "bodyPr");
                var item = new SlideItem
                {
                    Kind = shape ? SlideItemKind.Shape : SlideItemKind.Text,
                    Box = rect.Value, Text = text, Size = size,
                    Bold = (string?)firstR?.Attribute("b") is "1" or "true",
                    Italic = (string?)firstR?.Attribute("i") is "1" or "true",
                    Underline = firstR?.Attribute("u") is { } u && (string)u != "none",
                    Align = (string?)firstP?.Attribute("algn") switch { "ctr" => 1, "r" => 2, _ => phType is "ctrTitle" or "subTitle" ? 1 : 0 },
                    VAlign = (string?)bodyPr?.Attribute("anchor") switch { "ctr" => 1, "b" => 2, _ => isTitle ? 1 : 0 },
                    Bullets = paras.Any(p => p.Element(A + "pPr")?.Element(A + "buChar") != null)
                              || (phEl != null && phType is "body" or "obj" && !paras.Any(p => p.Element(A + "pPr")?.Element(A + "buNone") != null)),
                    Font = FontName((string?)firstR?.Element(A + "latin")?.Attribute("typeface") ?? (isTitle ? "+mj-lt" : null)),
                    Color = ColorIn(firstR) ?? StyleRef(style, "fontRef") ?? defaultText,
                    Fill = fill ?? Color.Empty, Line = line ?? Color.Empty,
                    Shape = prst switch
                    {
                        "roundRect" => SlideShape.RoundRect, "ellipse" => SlideShape.Ellipse, "triangle" => SlideShape.Triangle,
                        "rightArrow" => SlideShape.Arrow, _ => SlideShape.Rect
                    },
                    Role = isTitle ? "title" : phEl != null ? "body" : RoleFromName((string?)el.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("name"))
                };
                if (prst is not ("rect" or "roundRect" or "ellipse" or "triangle" or "rightArrow")) d.Unsupported++;   // bentuk lain digambar persegi
                slide.Items.Add(item);
            }
            d.Slides.Add(slide);
        }
        if (d.Slides.Count == 0) d.Slides.Add(d.NewSlide(SlideLayout.Title));
        return d;
    }

    private static string RoleFromName(string? name) => name switch
    {
        not null when name.StartsWith("Judul ", StringComparison.Ordinal) => "title",
        not null when name.StartsWith("Isi ", StringComparison.Ordinal) => "body",
        not null when name.StartsWith("Aksen ", StringComparison.Ordinal) => "deco",
        _ => ""
    };

    private static string PhType(XElement ph) => (string?)ph.Attribute("type") ?? "body";

    private static RectangleF? XfrmOf(XElement? spPr)
    {
        var x = spPr?.Element(A + "xfrm");
        var off = x?.Element(A + "off");
        var ext = x?.Element(A + "ext");
        if (off == null || ext == null) return null;
        static float V(XElement e, string n) => long.TryParse((string?)e.Attribute(n), out var v) ? v / EmuPerUnit : 0;
        return new RectangleF(V(off, "x"), V(off, "y"), V(ext, "cx"), V(ext, "cy"));
    }

    private static bool TryHex(string hex, out Color c)
    {
        c = Color.Empty;
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        c = Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }
}
