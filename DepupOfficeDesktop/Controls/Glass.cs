using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>Jendela yang memakai latar aurora + panel kaca (saat ini hanya jendela utama).</summary>
    public interface IGlassHost
    {
    }

    /// <summary>
    /// Pemilik latar aurora. Ada dua jenis: jendela utama (latar diam, untuk sidebar / bilah atas / status bar) dan
    /// area konten yang bisa digulir (<see cref="GlassScrollPanel"/>, latar ikut bergulir bersama isi).
    /// </summary>
    public interface IBackdropHost
    {
        GlassBackdrop Backdrop { get; }
        /// <summary>Ukuran "dokumen" latar (koordinat host).</summary>
        Size BackdropSize { get; }
        /// <summary>Posisi kiri-atas kontrol <paramref name="c"/> dalam koordinat dokumen host.</summary>
        Point DocOffset(Control c);
    }

    /// <summary>
    /// Efek kaca ala Android (Glass.kt): latar "aurora" lembut (gumpalan biru, ungu, hijau, kuning) dan versi buramnya
    /// untuk mengisi panel kaca (potongan latar buram + lapisan warna permukaan semi-transparan + tepi sorot).
    /// Gambar disimpan beresolusi rendah (latar memang lembut), lalu diperbesar saat digambar: hemat memori & cepat.
    /// </summary>
    public sealed class GlassBackdrop : IDisposable
    {
        private const int K_SHARP = 4;    // 1 piksel gambar = 4 piksel layar
        private const int K_BLUR = 24;    // versi buram: lebih kecil lagi lalu diperbesar halus

        private Bitmap? _sharp, _blur;
        private Size _size;
        private bool _dark;

        /// <summary>true bila kontrol berada di jendela kaca dan efek kaca aktif.</summary>
        public static bool Active(Control c) => Theme.GlassOn && c.FindForm() is IGlassHost;

        /// <summary>Host latar terdekat (konten bergulir atau jendela utama).</summary>
        public static IBackdropHost? HostOf(Control c)
        {
            for (Control? x = c; x != null; x = x.Parent)
                if (x is IBackdropHost h) return h;
            return null;
        }

        private void Ensure(Size sz)
        {
            sz = new Size(Math.Max(64, sz.Width), Math.Max(64, sz.Height));
            if (_sharp != null && _dark == Theme.Dark && Math.Abs(_size.Width - sz.Width) < 8 && Math.Abs(_size.Height - sz.Height) < 8) return;
            _sharp?.Dispose();
            _blur?.Dispose();
            _size = sz;
            _dark = Theme.Dark;
            _sharp = Render(sz);
            _blur = Blur(_sharp, sz);
        }

        public void Dispose()
        {
            _sharp?.Dispose();
            _blur?.Dispose();
            _sharp = _blur = null;
        }

        private static Bitmap Render(Size sz)
        {
            int w = (sz.Width + K_SHARP - 1) / K_SHARP, h = (sz.Height + K_SHARP - 1) / K_SHARP;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.BG);
            bool d = Theme.Dark;
            // Pola gumpalan hanya bergantung pada LEBAR dan diulang tiap periode vertikal: halaman yang memanjang
            // (mis. daftar berkas selesai dimuat) menghasilkan piksel yang sama persis di bagian yang sudah tampil.
            float period = w * 1.2f;
            var blobs = new (float X, float Y, float R, Color C, int A)[]
            {
                (0.10f, 0.08f, 0.55f, Theme.ACCENT, d ? 120 : 70),
                (0.92f, 0.20f, 0.48f, Theme.INDIGO, d ? 100 : 70),
                (0.72f, 0.80f, 0.60f, Theme.TEAL, d ? 70 : 55),
                (0.18f, 0.72f, 0.42f, Theme.AMBER_FILL, d ? 50 : 55),
                (0.50f, 0.45f, 0.36f, Theme.ACCENT, d ? 60 : 35),
            };
            float span = period;
            for (float top = 0; top < h; top += period)
            {
                foreach (var b in blobs)
                {
                    float r = b.R * span;
                    var rect = new RectangleF(b.X * w - r, top + b.Y * span - r, r * 2, r * 2);
                    if (rect.Bottom < 0 || rect.Top > h) continue;
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
            }
            return bmp;
        }

        private static Bitmap Blur(Bitmap sharp, Size full)
        {
            int w = (full.Width + K_BLUR - 1) / K_BLUR, h = (full.Height + K_BLUR - 1) / K_BLUR;
            var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(small);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(sharp, new Rectangle(0, 0, w, h));
            return small;
        }

        private static readonly ImageAttributes Wrap = CreateWrap();
        private static ImageAttributes CreateWrap()
        {
            var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);   // tepi potongan tidak gelap / bergaris
            return ia;
        }

        /// <summary>Gambar latar pada <paramref name="dest"/> (koordinat kontrol) yang sesuai posisi dokumen <paramref name="docTopLeft"/>.</summary>
        public void Draw(Graphics g, Rectangle dest, Point docTopLeft, Size docSize, bool blurred)
        {
            if (dest.Width <= 0 || dest.Height <= 0) return;
            Ensure(docSize);
            var bmp = blurred ? _blur! : _sharp!;
            float kx = blurred ? K_BLUR : K_SHARP, ky = kx;   // skala tetap → potongan di ukuran berbeda selalu sejajar
            var state = g.Save();
            g.InterpolationMode = blurred ? InterpolationMode.HighQualityBicubic : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.DrawImage(bmp, dest, docTopLeft.X / kx, docTopLeft.Y / ky, dest.Width / kx, dest.Height / ky, GraphicsUnit.Pixel, Wrap);
            g.Restore(state);
        }

        /// <summary>Gambar latar (tajam / buram) di belakang <paramref name="c"/> pada <paramref name="area"/> (koordinat c).</summary>
        public static void Paint(Control c, Graphics g, Rectangle area, bool blurred)
        {
            var host = HostOf(c);
            if (host == null || area.Width <= 0 || area.Height <= 0)
            {
                using var b = new SolidBrush(Theme.BG);
                g.FillRectangle(b, area);
                return;
            }
            Point off = host.DocOffset(c);
            host.Backdrop.Draw(g, area, new Point(area.X + off.X, area.Y + off.Y), host.BackdropSize, blurred);
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

    /// <summary>
    /// Area konten bergulir dengan latar aurora yang IKUT bergulir bersama isinya. Karena latar & isi bergeser bersama,
    /// geseran piksel bawaan Windows saat menggulir selalu benar — tidak ada sisa gambar lama / tampilan dobel.
    /// </summary>
    public class GlassScrollPanel : Panel, IBackdropHost
    {
        public GlassBackdrop Backdrop { get; } = new();

        public GlassScrollPanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public Size BackdropSize => new(Math.Max(ClientSize.Width, DisplayRectangle.Width), Math.Max(ClientSize.Height, DisplayRectangle.Height));

        public Point DocOffset(Control c)
        {
            Point p = c == this ? Point.Empty : PointToClient(c.PointToScreen(Point.Empty));
            // AutoScrollPosition bernilai negatif saat digulir: koordinat dokumen = klien − posisi gulir
            return new Point(p.X - AutoScrollPosition.X, p.Y - AutoScrollPosition.Y);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var area = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
            if (!GlassBackdrop.Active(this))
            {
                using var b = new SolidBrush(BackColor.A == 255 ? BackColor : Theme.BG);
                e.Graphics.FillRectangle(b, area);
                return;
            }
            GlassBackdrop.Paint(this, e.Graphics, area, false);
        }

        /// <summary>
        /// Jangan menggulir otomatis ke kontrol yang mendapat fokus (mis. saat halaman dibangun ulang): halaman tidak
        /// "melompat" sendiri. Menggulir tetap dengan roda mouse, scrollbar, PageUp/PageDown.
        /// </summary>
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

        protected override void Dispose(bool disposing)
        {
            if (disposing) Backdrop.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Panel kaca persegi (sidebar, bilah atas, status bar). Anak kontrol dibuat transparan agar kaca terlihat.</summary>
    public class GlassPane : Panel
    {
        /// <summary>Warna permukaan kaca (diberi alpha otomatis).</summary>
        public Color Surface { get; set; } = Theme.SURFACE;
        /// <summary>Garis tepi (gabungan AnchorStyles Top/Bottom/Left/Right).</summary>
        public AnchorStyles EdgeLines { get; set; }

        public GlassPane()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var area = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
            if (!GlassBackdrop.Active(this))
            {
                using var b = new SolidBrush(BackColor.A == 255 ? BackColor : Theme.BG);
                e.Graphics.FillRectangle(b, area);
                return;
            }
            GlassBackdrop.Paint(this, e.Graphics, area, true);
            using (var tb = new SolidBrush(GlassBackdrop.Tint(Surface))) e.Graphics.FillRectangle(tb, area);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (EdgeLines == AnchorStyles.None) return;
            using var pen = new Pen(Theme.LINE);
            var g = e.Graphics;
            int w = Width - 1, h = Height - 1;
            if (EdgeLines.HasFlag(AnchorStyles.Top)) g.DrawLine(pen, 0, 0, w, 0);
            if (EdgeLines.HasFlag(AnchorStyles.Bottom)) g.DrawLine(pen, 0, h, w, h);
            if (EdgeLines.HasFlag(AnchorStyles.Left)) g.DrawLine(pen, 0, 0, 0, h);
            if (EdgeLines.HasFlag(AnchorStyles.Right)) g.DrawLine(pen, w, 0, w, h);
        }
    }

    /// <summary>Pembantu: hentikan / lanjutkan penggambaran ulang jendela (bangun ulang tampilan tanpa kedip).</summary>
    public static class Redraw
    {
        private const int WM_SETREDRAW = 0x000B;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

        public static void Suspend(Control c)
        {
            if (c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Resume(Control c)
        {
            if (!c.IsHandleCreated) return;
            SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            // RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW
            RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0400 | 0x0080 | 0x0100);
        }
    }
}
