using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DepupOffice.Mac;

/// <summary>Sistem desain "Stitch Depup" (port dari Theme.cs Windows) + pembuat kontrol singkat.</summary>
public static class Ui
{
    // Token warna = Android Ui.kt (objek C), tema terang
    public const string Bg = "#F1F0EC";          // C.BG
    public const string Surface = "#FFFFFF";     // C.SURFACE
    public const string Surface2 = "#E8E6E1";    // C.SURFACE2
    public const string Soft = "#F3F2EE";        // C.SOFT
    public const string Accent = "#1652F6";      // C.BLUE_ACCENT / PRIMARY
    public const string AccentDark = "#0F43D6";
    public const string Brand = "#0037FF";       // ubin logo
    public const string Ink = "#111111";         // C.INK
    public const string Text = "#111111";        // C.TEXT
    public const string Muted = "#65625D";       // C.MUTED
    public const string Line = "#E4E2DD";        // C.LINE
    public const string LineStrong = "#84817B";  // C.LINE_STRONG
    public const string BlueBg = "#E8EEFF";      // C.PRIMARY_C
    public const string Indigo = "#72539E";      // C.IMG_FG (gambar)
    public const string IndigoBg = "#ECE6F3";    // C.IMG_BG
    public const string Sky = "#33609F";         // C.DOC_FG (Word)
    public const string SkyBg = "#E4EAF3";       // C.DOC_BG
    public const string Amber = "#8A4508";       // C.WARN_T / AMBER
    public const string AmberBg = "#FDF0CC";     // C.WARN_C
    public const string Teal = "#35754C";        // C.XLS_FG (Excel)
    public const string TealBg = "#E3EEE6";      // C.XLS_BG
    public const string Red = "#B0472F";         // C.PDF_FG (PDF)
    public const string RedBg = "#F5E6E1";       // C.PDF_BG
    public const string Success = "#15703A";     // C.SUCCESS
    public const string SuccessBg = "#DCF5E4";   // C.SUCCESS_C
    public const string Error = "#C0262D";       // C.ERROR
    public const string ErrorBg = "#FCE4E4";     // C.ERROR_C

    /// <summary>Huruf Android: Funnel Display (dibawa di Assets/Fonts).</summary>
    public static readonly FontFamily Font = new("avares://DepupOffice/Assets/Fonts#Funnel Display");

    private static readonly Dictionary<string, IBrush> BrushCache = new();

    public static IBrush B(string hex)
    {
        if (!BrushCache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush(Color.Parse(hex));
            BrushCache[hex] = brush;
        }
        return brush;
    }

    public static TextBlock T(string text, double size = 13, string color = Text, FontWeight? weight = null, bool wrap = false)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = B(color),
            FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static TextBlock H1(string text) => T(text, 22, Text, FontWeight.Bold);
    public static TextBlock H2(string text) => T(text, 16, Text, FontWeight.SemiBold);
    public static TextBlock Caption(string text, bool wrap = true) => T(text, 12, Muted, null, wrap);

    public static Button Btn(string text, Action onClick, bool primary = false, string? color = null, string? tip = null)
    {
        string c = color ?? Accent;
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(16, 8),
            CornerRadius = new CornerRadius(999),
            FontSize = 13,
            Background = primary ? B(c) : B(Surface),
            Foreground = primary ? Brushes.White : B(c),
            BorderBrush = primary ? B(c) : B(Line),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (primary) b.FontWeight = FontWeight.SemiBold;
        // Warna hover/tekan per tombol (tema Fluent memakai DynamicResource ini), agar teks tetap kontras.
        IBrush hoverBg = primary ? B(Darken(c, 0.12)) : B(Surface2);
        IBrush pressBg = primary ? B(Darken(c, 0.22)) : B(Line);
        IBrush fg = primary ? Brushes.White : B(c);
        b.Resources["ButtonBackgroundPointerOver"] = hoverBg;
        b.Resources["ButtonBackgroundPressed"] = pressBg;
        b.Resources["ButtonForegroundPointerOver"] = fg;
        b.Resources["ButtonForegroundPressed"] = fg;
        b.Resources["ButtonBorderBrushPointerOver"] = primary ? hoverBg : B(LineStrong);
        b.Resources["ButtonBorderBrushPressed"] = primary ? pressBg : B(LineStrong);
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static string Darken(string hex, double amount)
    {
        var col = Color.Parse(hex);
        byte D(byte v) => (byte)Math.Max(0, v * (1 - amount));
        return $"#{D(col.R):X2}{D(col.G):X2}{D(col.B):X2}";
    }

    public static Button SmallBtn(string text, Action onClick, string? color = null, string? tip = null)
    {
        var b = Btn(text, onClick, false, color, tip);
        b.Padding = new Thickness(9, 4);
        b.FontSize = 12;
        return b;
    }

    public static Border Card(Control child, double padding = 16, string bg = Surface, string border = Line)
        => new()
        {
            Background = B(bg),
            BorderBrush = B(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),   // Android rippleCard → radius 22
            Padding = new Thickness(padding),
            Child = child,
            BoxShadow = BoxShadows.Parse("0 1 2 0 #0F111111"),
        };

    /// <summary>Kartu yang bisa diklik dengan efek hover.</summary>
    public static Border ClickCard(Control child, Action onClick, double padding = 16, string bg = Surface, string hoverBg = BlueBg)
    {
        var card = Card(child, padding, bg);
        card.Cursor = new Cursor(StandardCursorType.Hand);
        card.PointerEntered += (_, _) => { card.Background = B(hoverBg == BlueBg ? Soft : hoverBg); card.BorderBrush = B(Line); };
        card.PointerExited += (_, _) => { card.Background = B(bg); card.BorderBrush = B(Line); };
        card.Tapped += (_, _) => onClick();
        return card;
    }

    /// <summary>Emoji lama → nama ikon Android (res/drawable/ic_NAMA.xml).</summary>
    private static readonly Dictionary<string, string> Emoji = new()
    {
        ["📖"] = "doc", ["🧰"] = "tools", ["✏️"] = "edit", ["💾"] = "download", ["🔒"] = "lock", ["🛡"] = "shield",
        ["❤"] = "heart", ["✉"] = "mail", ["ℹ"] = "verified", ["📂"] = "folder", ["🔊"] = "volume", ["🖨"] = "print",
        ["🔳"] = "qr", ["🔍"] = "search", ["✓"] = "check_ok", ["🧩"] = "merge", ["✂️"] = "cut", ["🗜"] = "compress",
        ["🖼"] = "image", ["🏞"] = "image", ["🔐"] = "lock", ["🔓"] = "lock", ["🔄"] = "rotate", ["📑"] = "crop",
        ["📷"] = "camera", ["📄"] = "doc", ["✉️"] = "mail", ["🧑‍💼"] = "user", ["📊"] = "sheet", ["💰"] = "sheet",
        ["🧾"] = "sheet", ["🗓"] = "clock", ["📭"] = "folder",
    };

    public static string IconName(string icon) => MacIcons.Has(icon) ? icon : Emoji.TryGetValue(icon, out var n) ? n : icon;

    /// <summary>Ikon Android tanpa wadah.</summary>
    public static Control Icon(string icon, double size, string color) => MacIcons.Create(IconName(icon), size, B(color));

    /// <summary>Android iconBadge: wadah bersudut 12 (dari 42) berisi ikon 22/42 dari ukuran wadah.</summary>
    public static Border IconBadge(string icon, string fg, string bg, double size = 40)
    {
        var name = IconName(icon);
        Control child = MacIcons.Has(name)
            ? MacIcons.Create(name, size * 22 / 42, B(fg))
            : new TextBlock { Text = icon, FontSize = size * 0.48, Foreground = B(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (child is Viewbox vb) { vb.HorizontalAlignment = HorizontalAlignment.Center; vb.VerticalAlignment = VerticalAlignment.Center; }
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 12 / 42), Background = B(bg), Child = child, HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>Ubin logo DepUp (ic_logo_tile.xml): biru #0037FF + tanda putih.</summary>
    public static Control Logo(double size) => MacIcons.Create("logo_tile", size, Brushes.White);

    /// <summary>Ikon &amp; warna fitur sama dengan Android (MainActivity.kt tile/createRow).</summary>
    public static (string icon, string fg, string bg) AndroidStyle(string title, string icon, string fg, string bg)
    {
        (string, string, string) Pdf(string i) => (i, Red, RedBg);
        (string, string, string) Doc(string i) => (i, Sky, SkyBg);
        (string, string, string) Xls(string i) => (i, Teal, TealBg);
        (string, string, string) Img(string i) => (i, Indigo, IndigoBg);
        (string, string, string) Pri(string i) => (i, Accent, BlueBg);
        return title switch
        {
            "Gabung PDF" => Pdf("merge"), "Pisah PDF" => Pdf("cut"), "Kompres PDF" => Pdf("compress"),
            "Gambar ke PDF" or "PDF dari Gambar" => Doc("image"), "PDF ke JPG" => Pdf("image"), "Foto ke PDF" => Img("pdf"),
            "Kunci PDF" or "Buka Kunci PDF" => Pri("lock"), "Putar PDF" => Pdf("rotate"), "Susun / Hapus Halaman" => Pdf("crop"),
            "OCR Teks" or "OCR" => Doc("search"), "Buat QR" or "Kode QR" => Pri("qr"), "Pindai QR" => Pri("qr"),
            "Baca Lantang" => Pri("volume"), "Cetak" => Pri("print"),
            "Dokumen Kosong" => Doc("doc"), "Surat Lamaran Kerja" => Doc("doc"), "CV Profesional" => Doc("user"),
            "Spreadsheet Kosong" or "Laporan Keuangan" or "Invoice / Tagihan" or "Presensi" => Xls("sheet"),
            _ => (IconName(icon), fg, bg),
        };
    }

    public static Border Pill(string text, string fg, string bg)
        => new()
        {
            Background = B(bg),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = T(text, 11, fg, FontWeight.SemiBold),
        };

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Col(double spacing, params Control[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static TextBox Input(string watermark, string text = "", bool multiline = false)
        => new()
        {
            Text = text,
            Watermark = watermark,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            CornerRadius = new CornerRadius(8),
            FontSize = 13,
        };

    public static ComboBox Combo(IEnumerable<string> items, int selected = 0, double width = 140)
        => new()
        {
            ItemsSource = items.ToList(),
            SelectedIndex = selected,
            Width = width,
            CornerRadius = new CornerRadius(8),
            FontSize = 13,
        };

    public static Border HLine() => new() { Height = 1, Background = B(Line), Margin = new Thickness(0, 4) };

    /// <summary>Warna lencana jenis berkas.</summary>
    public static (string Label, string Fg, string Bg) Badge(string path)
    {
        var (label, fg, bg) = DepupOfficeDesktop.Core.AppStorage.TypeBadgeOf(
            DepupOfficeDesktop.Core.AppStorage.MimeByName(System.IO.Path.GetFileName(path)), System.IO.Path.GetFileName(path));
        return (label, fg, bg);
    }

    public static Border FileBadge(string path, double size = 38)
    {
        var (label, fg, bg) = Badge(path);
        string icon = label switch { "PDF" => "pdf", "DOCX" => "doc", "XLSX" or "CSV" => "sheet", "IMG" => "image", _ => "folder" };
        return IconBadge(icon, fg, bg, size);
    }
}
