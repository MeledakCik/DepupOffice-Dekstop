using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>Jendela yang memakai latar aurora + panel kaca (saat ini hanya jendela utama).</summary>
    public interface IGlassHost
    {
    }

    /// <summary>
    /// Efek kaca ala Android (Glass.kt): latar "aurora" lembut (gumpalan warna biru, ungu, hijau, kuning) di belakang
    /// jendela, dan versi buramnya (blur) untuk mengisi panel kaca. Panel kaca = potongan latar buram + lapisan warna
    /// permukaan semi-transparan + garis sorot tipis — seperti kaca buram di atas wallpaper.
    /// Gambar dibuat sekali per ukuran jendela & tema, lalu dipakai ulang (murah saat menggulir).
    /// </summary>
    public static class GlassBackdrop
    {
        private static Bitmap? _sharp, _blur;
        private static Size _size;
        private static bool _dark;

        /// <summary>true bila kontrol berada di jendela kaca dan efek kaca aktif.</summary>
        public static bool Active(Control c) => Theme.GlassOn && c.FindForm() is IGlassHost;

        private static void Ensure(Size sz)
        {
            sz = new Size(Math.Max(64, sz.Width), Math.Max(64, sz.Height));
            if (_sharp != null && _size == sz && _dark == Theme.Dark) return;
            _sharp?.Dispose();
            _blur?.Dispose();
            _size = sz;
            _dark = Theme.Dark;
            _sharp = Render(sz);
            _blur = Blur(_sharp);
        }

        /// <summary>Buang gambar tersimpan (mis. setelah tema berganti).</summary>
        public static void Reset()
        {
            _sharp?.Dispose();
            _blur?.Dispose();
            _sharp = _blur = null;
        }

        private static Bitmap Render(Size sz)
        {
            var bmp = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.BG);
            bool d = Theme.Dark;
            float w = sz.Width, h = sz.Height, m = Math.Max(w, h);
            // (x, y, radius relatif, warna, alpha) — posisi tetap agar tampilan konsisten
            var blobs = new (float X, float Y, float R, Color C, int A)[]
            {
                (0.12f, 0.10f, 0.55f, Theme.ACCENT, d ? 120 : 70),
                (0.92f, 0.18f, 0.48f, Theme.INDIGO, d ? 100 : 70),
                (0.70f, 0.95f, 0.60f, Theme.TEAL, d ? 70 : 55),
                (0.20f, 0.85f, 0.42f, Theme.AMBER_FILL, d ? 50 : 55),
                (0.52f, 0.45f, 0.38f, Theme.ACCENT, d ? 60 : 35),
            };
            foreach (var b in blobs)
            {
                float r = b.R * m;
                var rect = new RectangleF(b.X * w - r, b.Y * h - r, r * 2, r * 2);
                using var path = new GraphicsPath();
                path.AddEllipse(rect);
                using var pg = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb(b.A, b.C),
                    SurroundColors = new[] { Color.FromArgb(0, b.C) },
                    FocusScales = new PointF(0.05f, 0.05f)
                };
                g.FillPath(pg, path);
            }
            return bmp;
        }

        /// <summary>Blur murah & halus: perkecil 1/14 lalu perbesar kembali dengan interpolasi bikubik.</summary>
        private static Bitmap Blur(Bitmap src)
        {
            int sw = Math.Max(4, src.Width / 14), sh = Math.Max(4, src.Height / 14);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, sw, sh));
            }
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using var ia = new ImageAttributes();
                ia.SetWrapMode(WrapMode.TileFlipXY);   // tepi tidak gelap
                g.DrawImage(small, new Rectangle(0, 0, dst.Width, dst.Height), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }

        /// <summary>Gambar latar (tajam atau buram) di belakang <paramref name="c"/> pada area <paramref name="area"/> (koordinat c).</summary>
        public static void Paint(Control c, Graphics g, Rectangle area, bool blurred)
        {
            var form = c.FindForm();
            if (form == null || area.Width <= 0 || area.Height <= 0)
            {
                using var b = new SolidBrush(Theme.BG);
                g.FillRectangle(b, area);
                return;
            }
            Ensure(form.ClientSize);
            Point off = c == form ? Point.Empty : form.PointToClient(c.PointToScreen(Point.Empty));
            var bmp = blurred ? _blur! : _sharp!;
            var src = new Rectangle(area.X + off.X, area.Y + off.Y, area.Width, area.Height);
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, area, src, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        /// <summary>Warna lapisan kaca di atas latar buram (permukaan semi-transparan).</summary>
        public static Color Tint(Color surface) => Color.FromArgb(Theme.Dark ? 168 : 178, surface);

        /// <summary>Garis tepi kaca (sorot halus).</summary>
        public static Color Edge => Theme.Dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(200, 255, 255, 255);

        /// <summary>Isi bentuk <paramref name="path"/> sebagai kaca buram (latar buram + lapisan warna).</summary>
        public static void FillGlass(Control c, Graphics g, GraphicsPath path, Color surface)
        {
            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            var r = Rectangle.Ceiling(path.GetBounds());
            r.Inflate(1, 1);
            r.Intersect(c.ClientRectangle);
            Paint(c, g, r, true);
            using (var tb = new SolidBrush(Tint(surface))) g.FillPath(tb, path);
            g.Restore(state);
        }
    }

    /// <summary>Panel kaca persegi (sidebar, bilah atas, status bar). Anak kontrol dibuat transparan agar kaca terlihat.</summary>
    public class GlassPane : Panel
    {
        /// <summary>Warna permukaan kaca (diberi alpha otomatis).</summary>
        public Color Surface { get; set; } = Theme.SURFACE;
        /// <summary>Garis tepi: 0 = tidak ada, else gabungan AnchorStyles (Top/Bottom/Left/Right).</summary>
        public AnchorStyles EdgeLines { get; set; }

        public GlassPane()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (!GlassBackdrop.Active(this))
            {
                using var b = new SolidBrush(BackColor.A == 255 ? BackColor : Theme.BG);
                e.Graphics.FillRectangle(b, ClientRectangle);
                return;
            }
            var area = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
            GlassBackdrop.Paint(this, e.Graphics, area, true);
            using (var tb = new SolidBrush(GlassBackdrop.Tint(Surface))) e.Graphics.FillRectangle(tb, area);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (EdgeLines == AnchorStyles.None) return;
            using var pen = new Pen(GlassBackdrop.Active(this) ? GlassBackdrop.Edge : Theme.LINE);
            using var pen2 = new Pen(Theme.LINE);
            var g = e.Graphics;
            int w = Width - 1, h = Height - 1;
            if (EdgeLines.HasFlag(AnchorStyles.Top)) g.DrawLine(pen2, 0, 0, w, 0);
            if (EdgeLines.HasFlag(AnchorStyles.Bottom)) g.DrawLine(pen2, 0, h, w, h);
            if (EdgeLines.HasFlag(AnchorStyles.Left)) g.DrawLine(pen2, 0, 0, 0, h);
            if (EdgeLines.HasFlag(AnchorStyles.Right)) g.DrawLine(pen2, w, 0, w, h);
            if (GlassBackdrop.Active(this) && EdgeLines.HasFlag(AnchorStyles.Right)) g.DrawLine(pen, w - 1, 0, w - 1, h);
        }
    }
}
