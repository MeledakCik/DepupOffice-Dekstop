using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Mode Slideshow &amp; Peragaan Presentasi (port PresenterSlideshowManager.kt) untuk PDF dan PPTX: layar penuh,
    /// transisi pudar halus, laser pointer (tahan klik kiri / tombol L), timer, auto-play, nomor slide, papan hitam (B).
    /// Kontrol: → / Spasi / PageDown / klik = berikutnya, ← / PageUp = sebelumnya, Home/End, Esc = keluar, P = auto-play.
    /// </summary>
    public sealed class SlideshowForm : Form
    {
        private readonly List<Bitmap> _slides;
        private readonly bool _ownsSlides;
        private readonly bool _secure;
        private readonly string _title;
        private int _cur;
        private Bitmap? _prev;
        private float _fade = 1f;
        private bool _auto, _laser, _laserKey, _black, _showHud = true;
        private Point _mouse;
        private int _seconds;
        private int _autoTick;
        private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
        private readonly System.Windows.Forms.Timer _hudHide = new() { Interval = 2500 };
        public int AutoSeconds { get; set; } = 4;

        public SlideshowForm(List<Bitmap> slides, string title, bool ownsSlides = false, bool secure = false, int start = 0)
        {
            _slides = slides; _ownsSlides = ownsSlides; _secure = secure; _title = title;
            _cur = Math.Clamp(start, 0, Math.Max(0, slides.Count - 1));
            Text = "Slideshow — " + title;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            BackColor = Color.Black;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = true;
            Cursor = Cursors.Default;

            _clock.Tick += (s, e) =>
            {
                _seconds++;
                if (_auto && ++_autoTick >= AutoSeconds) { _autoTick = 0; if (_cur < _slides.Count - 1) Go(_cur + 1); else _auto = false; }
                Invalidate(new Rectangle(0, 0, ClientSize.Width, 60));
            };
            _anim.Tick += (s, e) => { _fade = Math.Min(1f, _fade + 0.08f); if (_fade >= 1f) { _anim.Stop(); _prev = null; } Invalidate(); };
            _hudHide.Tick += (s, e) => { _hudHide.Stop(); _showHud = false; if (!_laser) Cursor.Hide(); Invalidate(); };

            KeyDown += OnKey;
            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { _laser = true; Cursor.Hide(); Invalidate(); }
                else if (e.Button == MouseButtons.Right) Go(_cur - 1);
            };
            MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                bool wasMoving = _laser && _laserMoved;
                _laser = false; _laserMoved = false;
                if (!wasMoving) Go(_cur + 1);
                Invalidate();
            };
            MouseMove += (s, e) =>
            {
                if (_laser && (Math.Abs(e.X - _mouse.X) > 2 || Math.Abs(e.Y - _mouse.Y) > 2)) _laserMoved = true;
                _mouse = e.Location;
                if (!_showHud) { _showHud = true; Cursor.Show(); }
                _hudHide.Stop(); _hudHide.Start();
                Invalidate();
            };
            MouseWheel += (s, e) => Go(_cur + (e.Delta < 0 ? 1 : -1));
            Shown += (s, e) => { if (_secure) SecureDisplay.Protect(Handle, true); _clock.Start(); _hudHide.Start(); };
        }

        private bool _laserMoved;

        private void OnKey(object? s, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Right: case Keys.Space: case Keys.PageDown: case Keys.Down: case Keys.Enter: Go(_cur + 1); break;
                case Keys.Left: case Keys.PageUp: case Keys.Up: case Keys.Back: Go(_cur - 1); break;
                case Keys.Home: Go(0); break;
                case Keys.End: Go(_slides.Count - 1); break;
                case Keys.Escape: Close(); break;
                case Keys.P: _auto = !_auto; _autoTick = 0; Invalidate(); break;
                case Keys.B: _black = !_black; Invalidate(); break;
                case Keys.L: _laserKey = !_laserKey; Invalidate(); break;
                case Keys.T: _seconds = 0; Invalidate(); break;
            }
            e.Handled = true;
        }

        private void Go(int i)
        {
            i = Math.Clamp(i, 0, _slides.Count - 1);
            if (i == _cur || _slides.Count == 0) return;
            _prev = _slides[_cur];
            _cur = i; _fade = 0f; _autoTick = 0; _black = false;
            _anim.Start();
            Invalidate();
        }

        private Rectangle Fit(Bitmap b)
        {
            var c = ClientRectangle;
            float s = Math.Min(c.Width / (float)b.Width, c.Height / (float)b.Height);
            int w = (int)(b.Width * s), h = (int)(b.Height * s);
            return new Rectangle(c.X + (c.Width - w) / 2, c.Y + (c.Height - h) / 2, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_slides.Count == 0 || _black) { DrawHud(g); return; }
            var cur = _slides[_cur];
            if (_prev != null && _fade < 1f) g.DrawImage(_prev, Fit(_prev));
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = _fade });
                var r = Fit(cur);
                g.DrawImage(cur, r, 0, 0, cur.Width, cur.Height, GraphicsUnit.Pixel, ia);
            }
            if (_laser || _laserKey)
            {
                for (int k = 3; k >= 1; k--)
                {
                    int rr = 6 + k * 6;
                    using var glow = new SolidBrush(Color.FromArgb(40 * (4 - k), 255, 30, 30));
                    g.FillEllipse(glow, _mouse.X - rr, _mouse.Y - rr, rr * 2, rr * 2);
                }
                g.FillEllipse(Brushes.Red, _mouse.X - 6, _mouse.Y - 6, 12, 12);
            }
            DrawHud(g);
        }

        private void DrawHud(Graphics g)
        {
            if (!_showHud && !_auto) return;
            using var f = new Font("Segoe UI", Theme.S(11), FontStyle.Bold, GraphicsUnit.Pixel);
            using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            string left = $"{_title}";
            string right = $"{(_auto ? "▶ AUTO  " : "")}⏱ {_seconds / 60:00}:{_seconds % 60:00}   Slide {_cur + 1} / {_slides.Count}";
            var rs = g.MeasureString(right, f);
            g.FillRectangle(bg, 0, 0, ClientSize.Width, rs.Height + Theme.S(14));
            g.DrawString(left.Length > 80 ? left[..80] + "…" : left, f, Brushes.White, Theme.S(14), Theme.S(7));
            g.DrawString(right, f, Brushes.LightGray, ClientSize.Width - rs.Width - Theme.S(14), Theme.S(7));
            if (_showHud)
            {
                const string help = "→/Spasi berikutnya • ← sebelumnya • P auto-play • B layar hitam • L / tahan klik laser • Esc keluar";
                using var hf = new Font("Segoe UI", Theme.S(10), GraphicsUnit.Pixel);
                var hs = g.MeasureString(help, hf);
                g.FillRectangle(bg, (ClientSize.Width - hs.Width) / 2 - Theme.S(10), ClientSize.Height - hs.Height - Theme.S(18), hs.Width + Theme.S(20), hs.Height + Theme.S(8));
                g.DrawString(help, hf, Brushes.Gainsboro, (ClientSize.Width - hs.Width) / 2, ClientSize.Height - hs.Height - Theme.S(14));
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _clock.Stop(); _anim.Stop(); _hudHide.Stop();
            Cursor.Show();
            if (_ownsSlides) foreach (var b in _slides) b.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>Muat slide dari PDF atau PPTX lalu tampilkan slideshow.</summary>
        public static void Run(IWin32Window? owner, string path, string? title = null, bool secure = false)
        {
            List<Bitmap> slides;
            var screen = Screen.FromPoint(Cursor.Position).Bounds;
            int w = Math.Max(1280, Math.Min(2560, screen.Width));
            if (path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                using var r = new PptxRenderer(path);
                slides = Enumerable.Range(0, r.SlideCount).Select(i => r.RenderSlide(i, w)).ToList();
            }
            else
            {
                if (!PdfEngine.EnsureUnlocked(owner, path)) return;
                slides = PdfEngine.RenderAllPages(path, PdfEngine.BudgetDim(path, w, 1000));
            }
            if (slides.Count == 0) { MessageBox.Show(owner, "Tidak ada slide/halaman yang bisa ditampilkan.", "Slideshow", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var f = new SlideshowForm(slides, title ?? Path.GetFileName(path), ownsSlides: true, secure: secure);
            f.ShowDialog(owner);
        }
    }
}
