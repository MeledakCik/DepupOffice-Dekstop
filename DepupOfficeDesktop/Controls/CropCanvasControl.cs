using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Porting dari CropView (ScanEditActivity.kt):
    /// Kanvas potong interaktif: gambar + 4 sudut + 4 sisi yang bisa digeser, kaca pembesar (loupe 2.5x),
    /// mode gambar sensur hitam (blackout redaction), dan pratinjau watermark diagonal.
    /// </summary>
    public class CropCanvasControl : Control
    {
        private Bitmap? _baseShown;
        private Bitmap? _filteredShown;
        public RectangleF Crop { get; set; } = new RectangleF(0f, 0f, 1f, 1f);
        public Action? OnCropChanged { get; set; }

        public List<RectangleF> ActiveRedactions { get; } = new();
        public string? ActiveWatermark { get; set; }
        public bool IsRedactMode { get; set; } = false;

        private RectangleF _imgRect = RectangleF.Empty;
        private int _dragMask = 0;
        private float _lastX, _lastY;
        private float _startRx, _startRy;
        private RectangleF? _currentRedactRect;
        private const float MinCropSize = 0.08f;

        private int _filterMode = 3;
        private int _bright = 0;
        private int _contrast = 0;

        public CropCanvasControl()
        {
            DoubleBuffered = true;
            BackColor = ColorTranslator.FromHtml("#283044");
            Cursor = Cursors.Hand;
        }

        public void SetBitmap(Bitmap baseBmp, int rot, bool flip, RectangleF crop, float skew = 0f)
        {
            _baseShown?.Dispose();
            var oriented = PdfEngine.Orient(baseBmp, rot, flip);
            var deskewed = PdfEngine.Deskew(oriented, skew);
            if (!ReferenceEquals(oriented, baseBmp) && !ReferenceEquals(oriented, deskewed))
                oriented.Dispose();
            _baseShown = (Bitmap)deskewed.Clone();
            if (!ReferenceEquals(deskewed, baseBmp))
                deskewed.Dispose();

            Crop = crop;
            RebuildFilteredPreview();
            FitImageRect();
            Invalidate();
        }

        public void SetFilter(int mode, int bright, int contrast)
        {
            _filterMode = mode;
            _bright = bright;
            _contrast = contrast;
            RebuildFilteredPreview();
            Invalidate();
        }

        private void RebuildFilteredPreview()
        {
            _filteredShown?.Dispose();
            _filteredShown = null;
            if (_baseShown == null) return;
            var clone = (Bitmap)_baseShown.Clone();
            var adj = PdfEngine.AdjustBrightnessContrast(clone, _bright, _contrast);
            _filteredShown = PdfEngine.ApplyMode(adj, _filterMode);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FitImageRect();
            Invalidate();
        }

        private void FitImageRect()
        {
            var b = _filteredShown ?? _baseShown;
            if (b == null || Width <= 0 || Height <= 0) return;
            float pad = 22f;
            float aw = Math.Max(10f, Width - 2 * pad);
            float ah = Math.Max(10f, Height - 2 * pad);
            float s = Math.Min(aw / b.Width, ah / b.Height);
            float w = b.Width * s;
            float h = b.Height * s;
            _imgRect = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
        }

        private RectangleF GetCropPx()
            => new RectangleF(
                _imgRect.Left + Crop.Left * _imgRect.Width,
                _imgRect.Top + Crop.Top * _imgRect.Height,
                Crop.Width * _imgRect.Width,
                Crop.Height * _imgRect.Height);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            var b = _filteredShown ?? _baseShown;
            if (b == null || _imgRect.Width <= 0) return;

            g.DrawImage(b, _imgRect);
            RectangleF r = GetCropPx();

            // Redupkan area di luar kotak potong
            using (var dimBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                g.FillRectangle(dimBrush, _imgRect.Left, _imgRect.Top, _imgRect.Width, Math.Max(0, r.Top - _imgRect.Top));
                g.FillRectangle(dimBrush, _imgRect.Left, r.Bottom, _imgRect.Width, Math.Max(0, _imgRect.Bottom - r.Bottom));
                g.FillRectangle(dimBrush, _imgRect.Left, r.Top, Math.Max(0, r.Left - _imgRect.Left), r.Height);
                g.FillRectangle(dimBrush, r.Right, r.Top, Math.Max(0, _imgRect.Right - r.Right), r.Height);
            }

            // Grid 3x3
            using (var gridPen = new Pen(Color.FromArgb(110, 255, 255, 255), 1f))
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = r.Left + r.Width * i / 3f;
                    float y = r.Top + r.Height * i / 3f;
                    g.DrawLine(gridPen, x, r.Top, x, r.Bottom);
                    g.DrawLine(gridPen, r.Left, y, r.Right, y);
                }
            }

            // Bingkai potong & 8 titik gagang (handles)
            using (var linePen = new Pen(Color.White, 2f))
            {
                g.DrawRectangle(linePen, r.Left, r.Top, r.Width, r.Height);
            }

            float rad = 8f;
            float cx = r.Left + r.Width / 2f;
            float cy = r.Top + r.Height / 2f;
            PointF[] pts =
            {
                new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom),
                new(cx, r.Top), new(cx, r.Bottom), new(r.Left, cy), new(r.Right, cy)
            };
            using (var ringPen = new Pen(Theme.ACCENT, 3f))
            {
                foreach (var pt in pts)
                {
                    g.FillEllipse(Brushes.White, pt.X - rad, pt.Y - rad, rad * 2, rad * 2);
                    g.DrawEllipse(ringPen, pt.X - rad, pt.Y - rad, rad * 2, rad * 2);
                }
            }

            // Lukis sensur hitam (blackout/redaksi)
            if (ActiveRedactions.Count > 0)
            {
                using var redPen = new Pen(Color.Red, 1.5f);
                foreach (var red in ActiveRedactions)
                {
                    float rx = _imgRect.Left + red.Left * _imgRect.Width;
                    float ry = _imgRect.Top + red.Top * _imgRect.Height;
                    float rw = red.Width * _imgRect.Width;
                    float rh = red.Height * _imgRect.Height;
                    g.FillRectangle(Brushes.Black, rx, ry, rw, rh);
                    g.DrawRectangle(redPen, rx, ry, rw, rh);
                }
            }

            if (IsRedactMode && _currentRedactRect.HasValue)
            {
                var cr = _currentRedactRect.Value;
                using var fillB = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var redPen = new Pen(Color.Red, 2f);
                g.FillRectangle(fillB, cr);
                g.DrawRectangle(redPen, cr.Left, cr.Top, cr.Width, cr.Height);
            }

            // Lukis pratinjau watermark diagonal
            if (!string.IsNullOrWhiteSpace(ActiveWatermark))
            {
                var state = g.Save();
                g.TranslateTransform(_imgRect.Left + _imgRect.Width / 2f, _imgRect.Top + _imgRect.Height / 2f);
                g.RotateTransform(-32f);
                float fs = Math.Max(12f, Math.Min(_imgRect.Width, _imgRect.Height) * 0.07f);
                using var wmFont = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
                using var wmBrush = new SolidBrush(Color.FromArgb(105, 220, 38, 38));
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(ActiveWatermark, wmFont, wmBrush, PointF.Empty, sf);
                g.Restore(state);
            }

            // Kaca Pembesar (Magnifier Loupe 2.5x) saat sudut/sisi digeser
            if (_dragMask != 0 && !IsRedactMode)
            {
                float rx = (_dragMask & 1) != 0 ? Crop.Left : ((_dragMask & 4) != 0 ? Crop.Right : (Crop.Left + Crop.Right) / 2f);
                float ry = (_dragMask & 2) != 0 ? Crop.Top : ((_dragMask & 8) != 0 ? Crop.Bottom : (Crop.Top + Crop.Bottom) / 2f);
                int px = Math.Clamp((int)(rx * b.Width), 0, b.Width - 1);
                int py = Math.Clamp((int)(ry * b.Height), 0, b.Height - 1);

                float size = 44f;
                float lCx = Width - size - 16f;
                float lCy = size + 16f;

                var state = g.Save();
                using var clipPath = new GraphicsPath();
                clipPath.AddEllipse(lCx - size, lCy - size, size * 2, size * 2);
                g.SetClip(clipPath);
                g.Clear(Color.White);

                int srcHalf = (int)(size / 2.5f);
                var srcR = new Rectangle(
                    Math.Max(0, px - srcHalf),
                    Math.Max(0, py - srcHalf),
                    Math.Min(b.Width - Math.Max(0, px - srcHalf), srcHalf * 2),
                    Math.Min(b.Height - Math.Max(0, py - srcHalf), srcHalf * 2));
                var dstR = new RectangleF(lCx - size, lCy - size, size * 2, size * 2);
                g.DrawImage(b, dstR, srcR, GraphicsUnit.Pixel);

                using var crossPen = new Pen(Theme.ACCENT, 2f);
                g.DrawLine(crossPen, lCx - 14, lCy, lCx + 14, lCy);
                g.DrawLine(crossPen, lCx, lCy - 14, lCx, lCy + 14);
                g.Restore(state);

                using var borderLoupe = new Pen(Theme.ACCENT, 3f);
                g.DrawEllipse(borderLoupe, lCx - size, lCy - size, size * 2, size * 2);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (IsRedactMode)
            {
                _startRx = e.X;
                _startRy = e.Y;
                _currentRedactRect = new RectangleF(e.X, e.Y, 0, 0);
                Invalidate();
                return;
            }

            RectangleF c = GetCropPx();
            float t = 24f;
            bool inY = e.Y > c.Top - t && e.Y < c.Bottom + t;
            bool inX = e.X > c.Left - t && e.X < c.Right + t;
            int m = 0;
            if (Math.Abs(e.X - c.Left) < t && inY) m |= 1;
            else if (Math.Abs(e.X - c.Right) < t && inY) m |= 4;
            if (Math.Abs(e.Y - c.Top) < t && inX) m |= 2;
            else if (Math.Abs(e.Y - c.Bottom) < t && inX) m |= 8;
            if (m == 0 && c.Contains(e.X, e.Y)) m = 16;

            _dragMask = m;
            _lastX = e.X;
            _lastY = e.Y;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (IsRedactMode)
            {
                if (e.Button == MouseButtons.Left && _currentRedactRect.HasValue)
                {
                    float x1 = Math.Min(_startRx, e.X);
                    float y1 = Math.Min(_startRy, e.Y);
                    float x2 = Math.Max(_startRx, e.X);
                    float y2 = Math.Max(_startRy, e.Y);
                    _currentRedactRect = RectangleF.FromLTRB(x1, y1, x2, y2);
                    Invalidate();
                }
                return;
            }

            if (_dragMask == 0 || _imgRect.Width <= 0 || _imgRect.Height <= 0) return;
            float dx = (e.X - _lastX) / _imgRect.Width;
            float dy = (e.Y - _lastY) / _imgRect.Height;
            _lastX = e.X;
            _lastY = e.Y;

            float left = Crop.Left, top = Crop.Top, right = Crop.Right, bottom = Crop.Bottom;
            if (_dragMask == 16)
            {
                float w = Crop.Width, h = Crop.Height;
                float nl = Math.Clamp(left + dx, 0f, 1f - w);
                float nt = Math.Clamp(top + dy, 0f, 1f - h);
                Crop = new RectangleF(nl, nt, w, h);
            }
            else
            {
                if ((_dragMask & 1) != 0) left = Math.Clamp(left + dx, 0f, right - MinCropSize);
                if ((_dragMask & 4) != 0) right = Math.Clamp(right + dx, left + MinCropSize, 1f);
                if ((_dragMask & 2) != 0) top = Math.Clamp(top + dy, 0f, bottom - MinCropSize);
                if ((_dragMask & 8) != 0) bottom = Math.Clamp(bottom + dy, top + MinCropSize, 1f);
                Crop = RectangleF.FromLTRB(left, top, right, bottom);
            }
            OnCropChanged?.Invoke();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsRedactMode)
            {
                if (_currentRedactRect.HasValue && _currentRedactRect.Value.Width > 8f && _currentRedactRect.Value.Height > 8f && _imgRect.Width > 0)
                {
                    var cr = _currentRedactRect.Value;
                    float rl = Math.Clamp((cr.Left - _imgRect.Left) / _imgRect.Width, 0f, 1f);
                    float rt = Math.Clamp((cr.Top - _imgRect.Top) / _imgRect.Height, 0f, 1f);
                    float rr = Math.Clamp((cr.Right - _imgRect.Left) / _imgRect.Width, 0f, 1f);
                    float rb = Math.Clamp((cr.Bottom - _imgRect.Top) / _imgRect.Height, 0f, 1f);
                    ActiveRedactions.Add(RectangleF.FromLTRB(rl, rt, rr, rb));
                    OnCropChanged?.Invoke();
                }
                _currentRedactRect = null;
                Invalidate();
                return;
            }
            _dragMask = 0;
            Invalidate();
        }
    }
}
