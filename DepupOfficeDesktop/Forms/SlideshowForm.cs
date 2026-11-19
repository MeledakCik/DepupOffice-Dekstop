using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
        private readonly PagedDocument _document;
        private readonly bool _secure;
        private readonly string _title;
        private int _cur;
        private int _displayed = -1;
        private int _failed = -1;
        private Bitmap? _slide;
        private Bitmap? _prev;
        private bool _loading;
        private bool _closed;
        private bool _cursorHidden;
        private long _fadeStarted;
        private float _fade = 1f;
        private bool _auto, _laser, _laserKey, _black, _showHud = true;
        private Point _mouse;
        private int _seconds;
        private int _autoTick;
        private readonly System.Windows.Forms.Timer _clock = new() { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
        private readonly System.Windows.Forms.Timer _hudHide = new() { Interval = 2500 };
        public int AutoSeconds { get; set; } = 4;

        internal SlideshowForm(PagedDocument document, string title, bool secure = false, int start = 0)
        {
            _document = document; _secure = secure; _title = title;
            _cur = Math.Clamp(start, 0, document.Count - 1);
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
                if (_auto && !_loading && ++_autoTick >= AutoSeconds) { _autoTick = 0; if (_cur < _document.Count - 1) Go(_cur + 1); else _auto = false; }
                Invalidate(new Rectangle(0, 0, ClientSize.Width, 60));
            };
            _anim.Tick += (s, e) =>
            {
                _fade = Math.Min(1f, (float)(Stopwatch.GetElapsedTime(_fadeStarted).TotalMilliseconds / 160));
                if (_fade >= 1f) FinishTransition();
                Invalidate();
            };
            _hudHide.Tick += (s, e) => { _hudHide.Stop(); _showHud = false; UpdateCursor(); Invalidate(); };

            KeyDown += OnKey;
            MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) { _laser = true; _mouse = e.Location; UpdateCursor(); Invalidate(); }
                else if (e.Button == MouseButtons.Right) Go(_cur - 1);
            };
            MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                bool wasMoving = _laser && _laserMoved;
                _laser = false; _laserMoved = false;
                UpdateCursor();
                if (!wasMoving) Go(_cur + 1);
                Invalidate();
            };
            MouseMove += (s, e) =>
            {
                if (_laser && (Math.Abs(e.X - _mouse.X) > 2 || Math.Abs(e.Y - _mouse.Y) > 2)) _laserMoved = true;
                _mouse = e.Location;
                _showHud = true;
                UpdateCursor();
                _hudHide.Stop(); _hudHide.Start();
                Invalidate();
            };
            MouseWheel += (s, e) => Go(_cur + (e.Delta < 0 ? 1 : -1));
            Shown += async (s, e) =>
            {
                if (_secure) SecureDisplay.Protect(Handle, true);
                _clock.Start();
                _hudHide.Start();
                await LoadSlideAsync();
            };
        }

        private bool _laserMoved;

        private void OnKey(object? s, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Right: case Keys.Space: case Keys.PageDown: case Keys.Down: case Keys.Enter: Go(_cur + 1); break;
                case Keys.Left: case Keys.PageUp: case Keys.Up: case Keys.Back: Go(_cur - 1); break;
                case Keys.Home: Go(0); break;
                case Keys.End: Go(_document.Count - 1); break;
                case Keys.Escape: Close(); break;
                case Keys.P: _auto = !_auto; _autoTick = 0; Invalidate(); break;
                case Keys.B: _black = !_black; Invalidate(); break;
                case Keys.L: _laserKey = !_laserKey; UpdateCursor(); Invalidate(); break;
                case Keys.T: _seconds = 0; Invalidate(); break;
            }
            e.Handled = true;
        }

        private void Go(int i)
        {
            i = Math.Clamp(i, 0, _document.Count - 1);
            if (i == _cur) return;
            _cur = i; _autoTick = 0; _black = false;
            _ = LoadSlideAsync();
            Invalidate();
        }

        private async Task LoadSlideAsync()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                while (!_closed && _cur != _displayed && _cur != _failed)
                {
                    int index = _cur;
                    int dimension = Math.Clamp(Math.Max(ClientSize.Width, ClientSize.Height), 1280, 2560);
                    Bitmap slide;
                    try { slide = await Task.Run(() => _document.Render(index, dimension)); }
                    catch (Exception) { if (!_closed) _failed = index; continue; }
                    if (_closed || index != _cur) { slide.Dispose(); continue; }
                    FinishTransition();
                    if (Theme.MotionEnabled && _slide != null)
                    {
                        _prev = _slide;
                        _fade = 0;
                        _fadeStarted = Stopwatch.GetTimestamp();
                        _anim.Start();
                    }
                    else _slide?.Dispose();
                    _slide = slide;
                    _displayed = index;
                    _failed = -1;
                }
            }
            finally
            {
                _loading = false;
                if (!_closed) Invalidate();
            }
        }

        private void FinishTransition()
        {
            _anim.Stop();
            _fade = 1f;
            _prev?.Dispose();
            _prev = null;
        }

        private void UpdateCursor()
        {
            bool hide = !_closed && (_laser || _laserKey || !_showHud);
            // A client cursor avoids an unbalanced process-wide Cursor.Hide/Show count.
            if (_cursorHidden == hide) return;
            _cursorHidden = hide;
            if (hide) Cursor.Hide(); else Cursor.Show();
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
            if (_black) { DrawHud(g); return; }
            if (_failed == _cur || _slide == null)
            {
                TextRenderer.DrawText(g, _failed == _cur ? "Slide gagal ditampilkan" : "Memuat slide…",
                    Theme.FontBody(12f), ClientRectangle, Color.WhiteSmoke, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                DrawHud(g);
                return;
            }
            var cur = _slide;
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
            string right = $"{(_auto ? "▶ AUTO  " : "")}⏱ {_seconds / 60:00}:{_seconds % 60:00}   Slide {_cur + 1} / {_document.Count}";
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
            _closed = true;
            UpdateCursor();
            _clock.Stop(); _hudHide.Stop();
            FinishTransition();
            _slide?.Dispose();
            _slide = null;
            _clock.Dispose(); _anim.Dispose(); _hudHide.Dispose();
            base.OnFormClosed(e);
        }

        /// <summary>Muat slide dari PDF atau PPTX lalu tampilkan slideshow.</summary>
        public static void Run(IWin32Window? owner, string path, string? title = null, bool secure = false)
        {
            if (!path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) && !PdfEngine.EnsureUnlocked(owner, path)) return;
            var (ok, document) = Ui.Busy(owner, "Membuka presentasi…", _ => PagedDocument.Open(path).Document);
            using var source = document;
            if (!ok) return;
            if (source is not { Count: > 0 }) { MessageBox.Show(owner, "Tidak ada slide/halaman yang bisa ditampilkan.", "Slideshow", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var f = new SlideshowForm(source, title ?? Path.GetFileName(path), secure: secure);
            f.ShowDialog(owner);
        }
    }
}
