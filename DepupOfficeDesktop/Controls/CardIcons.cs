using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>Daftar ikon vektor yang dipakai pada kartu (bukan sidebar).</summary>
    public enum CardIcon
    {
        Pdf, Word, Excel, Folder, Speaker, Printer, Qr, Merge, Split, Organize,
        Image, ImagePdf, Compress, Scan, Batch, Lock, Signature, Sliders, Theme,
        Vault, Letter, Person, Receipt, Calendar, Edit, Camera,
        Upload, Download, Share, Heart, Mail, Shield, Search, Chat, Wand, Check, Grid,
        // ikon Android tambahan (nama sama dengan res/drawable/ic_*.xml)
        Note, Checklist, Crop, Cut, Bright, Widget, ZoomIn, Clock, Star, Pin, Home, Tools, Box, Bulb, Palette, Verified, Spark, Cloud, Offline, Plus
    }

    /// <summary>
    /// Kotak ikon membulat dengan ikon digambar langsung (GDI+), sehingga tajam di semua DPI
    /// dan tidak bergantung pada font/emoji. Menggantikan Pill berisi teks glyph.
    /// </summary>
    public class IconChip : Control
    {
        private CardIcon _icon;
        private Color _bg = Theme.ACCENT;
        private Color _fg = Color.White;

        public CardIcon Icon { get => _icon; set { _icon = value; Invalidate(); } }
        public Color Bg { get => _bg; set { _bg = value; Invalidate(); } }
        public Color Fg { get => _fg; set { _fg = value; Invalidate(); } }
        /// <summary>Radius sudut dalam piksel nyata (0 = otomatis 28% dari sisi).</summary>
        public int Radius { get; set; }

        public IconChip()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;

            int r = Radius > 0 ? Radius : (int)(Math.Min(rect.Width, rect.Height) * 0.28f);
            using (var path = Theme.RoundedRect(rect, r))
            using (var b = new SolidBrush(_bg))
                g.FillPath(b, path);

            float side = Math.Min(Width, Height) * 0.5f;   // Android iconBadge: ikon 24 dp di wadah 48 dp
            var box = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
            CardIconPainter.Draw(g, box, _icon, _fg, _bg);
        }
    }

    public static class CardIconPainter
    {
        /// <summary>Menggambar ikon pada kotak <paramref name="box"/> (ruang desain 24x24).</summary>
        /// <summary>Nama ikon Android (res/drawable/ic_NAMA.xml) untuk tiap <see cref="CardIcon"/>.</summary>
        public static string AndroidName(CardIcon icon) => icon switch
        {
            CardIcon.Pdf => "pdf", CardIcon.Word => "doc", CardIcon.Excel => "sheet", CardIcon.Folder => "folder",
            CardIcon.Speaker => "volume", CardIcon.Printer => "print", CardIcon.Qr => "qr", CardIcon.Merge => "merge",
            CardIcon.Split => "cut", CardIcon.Organize => "crop", CardIcon.Image => "image", CardIcon.ImagePdf => "img_add",
            CardIcon.Compress => "compress", CardIcon.Scan => "scan", CardIcon.Batch => "grid", CardIcon.Lock => "lock",
            CardIcon.Signature => "sign", CardIcon.Sliders => "tune", CardIcon.Theme => "moon_sun", CardIcon.Vault => "lock",
            CardIcon.Letter => "doc", CardIcon.Person => "user", CardIcon.Receipt => "sheet", CardIcon.Calendar => "clock",
            CardIcon.Edit => "edit", CardIcon.Camera => "camera", CardIcon.Upload => "upload", CardIcon.Download => "download",
            CardIcon.Share => "share", CardIcon.Heart => "heart", CardIcon.Mail => "mail", CardIcon.Shield => "shield",
            CardIcon.Search => "search", CardIcon.Chat => "chat", CardIcon.Wand => "wand", CardIcon.Check => "check_ok",
            CardIcon.Grid => "grid", CardIcon.Note => "note", CardIcon.Checklist => "checklist", CardIcon.Crop => "crop",
            CardIcon.Cut => "cut", CardIcon.Bright => "bright", CardIcon.Widget => "widget", CardIcon.ZoomIn => "zoom_in",
            CardIcon.Clock => "clock", CardIcon.Star => "star", CardIcon.Pin => "pin", CardIcon.Home => "home",
            CardIcon.Tools => "tools", CardIcon.Box => "box", CardIcon.Bulb => "bulb", CardIcon.Palette => "palette",
            CardIcon.Verified => "verified", CardIcon.Spark => "spark", CardIcon.Cloud => "cloud", CardIcon.Offline => "offline", CardIcon.Plus => "plus",
            _ => "doc"
        };

        /// <summary>Menggambar ikon pada kotak <paramref name="box"/> — vektor Android "Depup Solid" yang sama persis.</summary>
        public static void Draw(Graphics g, RectangleF box, CardIcon icon, Color fg, Color bg) =>
            DepupIcons.Draw(g, AndroidName(icon), box, fg);
    }
}
