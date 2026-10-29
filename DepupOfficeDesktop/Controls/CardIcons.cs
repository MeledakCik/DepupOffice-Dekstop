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
        Vault, Letter, Person, Receipt, Calendar, Edit, Camera
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

            float side = Math.Min(Width, Height) * 0.56f;
            var box = new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side);
            CardIconPainter.Draw(g, box, _icon, _fg, _bg);
        }
    }

    public static class CardIconPainter
    {
        /// <summary>Menggambar ikon pada kotak <paramref name="box"/> (ruang desain 24x24).</summary>
        public static void Draw(Graphics g, RectangleF box, CardIcon icon, Color fg, Color bg)
        {
            float u = box.Width / 24f;
            float x0 = box.X, y0 = box.Y;
            PointF P(float x, float y) => new(x0 + x * u, y0 + y * u);
            RectangleF R(float x, float y, float w, float h) => new(x0 + x * u, y0 + y * u, w * u, h * u);

            using var pen = new Pen(fg, Math.Max(1.3f, 1.8f * u))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var fill = new SolidBrush(fg);
            using var bgFill = new SolidBrush(bg);

            void Line(float ax, float ay, float bx, float by) => g.DrawLine(pen, P(ax, ay), P(bx, by));
            void Poly(params float[] c)
            {
                var pts = new PointF[c.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = P(c[i * 2], c[i * 2 + 1]);
                g.DrawLines(pen, pts);
            }
            void Closed(params float[] c)
            {
                var pts = new PointF[c.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = P(c[i * 2], c[i * 2 + 1]);
                g.DrawPolygon(pen, pts);
            }
            void Box(float x, float y, float w, float h, float rad = 2f)
            {
                using var p = Theme.RoundedRect(Rectangle.Round(R(x, y, w, h)), (int)Math.Round(rad * u));
                g.DrawPath(pen, p);
            }
            void Dot(float cx, float cy, float rad)
            {
                var c = P(cx, cy);
                g.FillEllipse(fill, c.X - rad * u, c.Y - rad * u, rad * 2 * u, rad * 2 * u);
            }
            void Page()
            {
                Closed(6, 3, 14, 3, 19, 8, 19, 21, 6, 21);
                Poly(14, 3, 14, 8, 19, 8);
            }

            switch (icon)
            {
                case CardIcon.Pdf:
                    Page();
                    Line(9, 12, 16, 12);
                    Line(9, 15, 16, 15);
                    Line(9, 18, 13, 18);
                    break;
                case CardIcon.Word:
                    Page();
                    Poly(8.5f, 11, 10.2f, 18, 12.5f, 13, 14.8f, 18, 16.5f, 11);
                    break;
                case CardIcon.Excel:
                    Page();
                    Line(9, 12, 16, 19);
                    Line(16, 12, 9, 19);
                    break;
                case CardIcon.Folder:
                    Closed(3, 6.5f, 9, 6.5f, 11, 9, 21, 9, 21, 19, 3, 19);
                    break;
                case CardIcon.Speaker:
                    Closed(4, 9.5f, 8, 9.5f, 13, 5.5f, 13, 18.5f, 8, 14.5f, 4, 14.5f);
                    g.DrawArc(pen, R(11, 8, 7, 8), -55, 110);
                    g.DrawArc(pen, R(10, 4.5f, 11, 15), -55, 110);
                    break;
                case CardIcon.Printer:
                    Box(7, 3, 10, 5, 1);
                    Box(3, 8, 18, 9, 2);
                    Box(7, 14, 10, 7, 1);
                    Dot(17.5f, 11, 0.9f);
                    break;
                case CardIcon.Qr:
                    Box(3.5f, 3.5f, 7, 7, 1.5f);
                    Box(13.5f, 3.5f, 7, 7, 1.5f);
                    Box(3.5f, 13.5f, 7, 7, 1.5f);
                    g.FillRectangle(fill, R(6, 6, 2, 2));
                    g.FillRectangle(fill, R(16, 6, 2, 2));
                    g.FillRectangle(fill, R(6, 16, 2, 2));
                    Line(14, 15, 14, 15.2f);
                    Line(17, 14, 20.5f, 14);
                    Line(14, 18, 17, 18);
                    Line(20.5f, 17, 20.5f, 20.5f);
                    Line(17, 20.5f, 17.1f, 20.5f);
                    break;
                case CardIcon.Merge:
                    Line(5, 4, 12, 11);
                    Line(19, 4, 12, 11);
                    Line(12, 11, 12, 20);
                    Poly(8.5f, 16.5f, 12, 20, 15.5f, 16.5f);
                    break;
                case CardIcon.Split:
                    Line(12, 4, 12, 11);
                    Line(12, 11, 5, 19);
                    Line(12, 11, 19, 19);
                    Poly(5, 14.5f, 5, 19, 9.5f, 19);
                    Poly(14.5f, 19, 19, 19, 19, 14.5f);
                    break;
                case CardIcon.Organize:
                    Box(3.5f, 3.5f, 7, 7, 1.5f);
                    Box(13.5f, 3.5f, 7, 7, 1.5f);
                    Box(3.5f, 13.5f, 7, 7, 1.5f);
                    Box(13.5f, 13.5f, 7, 7, 1.5f);
                    break;
                case CardIcon.Image:
                    Box(3, 5, 18, 14, 2);
                    Poly(3.5f, 16.5f, 9, 11, 13.5f, 15.5f, 16.5f, 12.5f, 20.5f, 16.5f);
                    Dot(8, 9, 1.3f);
                    break;
                case CardIcon.ImagePdf:
                    Page();
                    Poly(8.5f, 18, 11.5f, 14, 13.5f, 16.5f, 15, 15, 17, 18);
                    Dot(10.5f, 11, 1f);
                    break;
                case CardIcon.Compress:
                    Line(12, 3, 12, 9.5f);
                    Poly(8.8f, 6.3f, 12, 9.5f, 15.2f, 6.3f);
                    Line(12, 21, 12, 14.5f);
                    Poly(8.8f, 17.7f, 12, 14.5f, 15.2f, 17.7f);
                    Line(4, 12, 20, 12);
                    break;
                case CardIcon.Scan:
                    Poly(3, 8, 3, 3, 8, 3);
                    Poly(16, 3, 21, 3, 21, 8);
                    Poly(3, 16, 3, 21, 8, 21);
                    Poly(16, 21, 21, 21, 21, 16);
                    Line(6.5f, 12, 17.5f, 12);
                    break;
                case CardIcon.Batch:
                    Box(8, 3, 13, 12, 2);
                    Box(3, 8, 13, 12, 2);
                    Poly(3.5f, 17, 7, 13.5f, 10, 16.5f, 12.5f, 14.5f, 15.5f, 17.5f);
                    break;
                case CardIcon.Lock:
                    Box(5, 11, 14, 10, 2);
                    g.DrawArc(pen, R(8, 3, 8, 16), 180, 180);
                    Dot(12, 15.5f, 1.3f);
                    Line(12, 16, 12, 18);
                    break;
                case CardIcon.Signature:
                    Closed(4, 20, 5.2f, 15, 15.5f, 4.5f, 19.5f, 8.5f, 9, 19);
                    Line(13.5f, 6.5f, 17.5f, 10.5f);
                    break;
                case CardIcon.Sliders:
                    Line(4, 6, 20, 6);
                    Line(4, 12, 20, 12);
                    Line(4, 18, 20, 18);
                    foreach (var (cx, cy) in new[] { (8.5f, 6f), (15.5f, 12f), (9.5f, 18f) })
                    {
                        var c = P(cx, cy);
                        g.FillEllipse(bgFill, c.X - 2.4f * u, c.Y - 2.4f * u, 4.8f * u, 4.8f * u);
                        g.DrawEllipse(pen, c.X - 2.4f * u, c.Y - 2.4f * u, 4.8f * u, 4.8f * u);
                    }
                    break;
                case CardIcon.Theme:
                    g.DrawEllipse(pen, R(4, 4, 16, 16));
                    g.FillPie(fill, R(4, 4, 16, 16), 90, 180);
                    break;
                case CardIcon.Vault:
                    Box(3.5f, 4, 17, 14, 2);
                    g.DrawEllipse(pen, R(8.5f, 8, 7, 7));
                    Dot(12, 11.5f, 1f);
                    Line(7, 18, 7, 21);
                    Line(17, 18, 17, 21);
                    break;
                case CardIcon.Letter:
                    Box(3, 6, 18, 13, 2);
                    Poly(3.5f, 7, 12, 13.5f, 20.5f, 7);
                    break;
                case CardIcon.Person:
                    g.DrawEllipse(pen, R(8, 3.5f, 8, 8));
                    g.DrawArc(pen, R(4.5f, 14, 15, 14), 180, 180);
                    break;
                case CardIcon.Receipt:
                    Closed(6, 3, 18, 3, 18, 21, 15, 19, 12, 21, 9, 19, 6, 21);
                    Line(9, 8, 15, 8);
                    Line(9, 12, 15, 12);
                    break;
                case CardIcon.Calendar:
                    Box(4, 5, 16, 15, 2);
                    Line(4, 10, 20, 10);
                    Line(8.5f, 3, 8.5f, 7);
                    Line(15.5f, 3, 15.5f, 7);
                    Dot(9, 14, 0.9f);
                    Dot(12, 14, 0.9f);
                    Dot(15, 14, 0.9f);
                    Dot(9, 17.2f, 0.9f);
                    Dot(12, 17.2f, 0.9f);
                    break;
                case CardIcon.Edit:
                    Line(4, 20.5f, 20, 20.5f);
                    Closed(6, 17, 7, 12.5f, 16, 3.5f, 19.5f, 7, 10.5f, 16);
                    break;
                case CardIcon.Camera:
                    Box(3, 7, 18, 13, 2.5f);
                    Poly(8, 7, 9.5f, 4, 14.5f, 4, 16, 7);
                    g.DrawEllipse(pen, R(8.5f, 10.5f, 7, 7));
                    break;
            }
        }
    }
}
