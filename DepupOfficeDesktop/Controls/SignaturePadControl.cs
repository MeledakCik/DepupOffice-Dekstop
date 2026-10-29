using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Porting dari SignatureView.kt:
    /// Kotak gambar tanda tangan dengan mouse/pena. ToBitmap() menghasilkan PNG transparan yang sudah dipotong pas.
    /// </summary>
    public class SignaturePadControl : Control
    {
        private readonly List<List<PointF>> _strokes = new();
        private List<PointF>? _currentStroke;
        private bool _drawing = false;

        public SignaturePadControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Cursor = Cursors.Cross;
            Size = new Size(420, 170);
        }

        public bool IsEmpty => _strokes.Count == 0;

        public void Clear()
        {
            _strokes.Clear();
            _currentStroke = null;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _drawing = true;
                _currentStroke = new List<PointF> { e.Location };
                _strokes.Add(_currentStroke);
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drawing && _currentStroke != null)
            {
                _currentStroke.Add(e.Location);
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drawing = false;
            _currentStroke = null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);

            using var borderPen = new Pen(Theme.LINE, 1f);
            g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            if (_strokes.Count == 0)
            {
                using var hintFont = Theme.FontBody(9.5f, FontStyle.Italic);
                using var hintBrush = new SolidBrush(Theme.MUTED);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("Goreskan tanda tangan Anda di sini menggunakan mouse / stylus...", hintFont, hintBrush, ClientRectangle, sf);
                return;
            }

            using var inkPen = new Pen(ColorTranslator.FromHtml("#0B1F6B"), 3.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            foreach (var stroke in _strokes)
            {
                if (stroke.Count > 1)
                    g.DrawLines(inkPen, stroke.ToArray());
                else if (stroke.Count == 1)
                    g.FillEllipse(Brushes.DarkBlue, stroke[0].X - 1.5f, stroke[0].Y - 1.5f, 3f, 3f);
            }
        }

        public Bitmap? ToBitmap()
        {
            if (_strokes.Count == 0 || Width <= 0 || Height <= 0) return null;

            float minX = Width, minY = Height, maxX = 0, maxY = 0;
            foreach (var s in _strokes)
            {
                foreach (var p in s)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }

            float pad = 8f;
            int l = Math.Max(0, (int)(minX - pad));
            int t = Math.Max(0, (int)(minY - pad));
            int r = Math.Min(Width, (int)(maxX + pad));
            int b = Math.Min(Height, (int)(maxY + pad));
            int cw = Math.Max(1, r - l);
            int ch = Math.Max(1, b - t);

            using var full = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var inkPen = new Pen(ColorTranslator.FromHtml("#0B1F6B"), 3.5f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                foreach (var stroke in _strokes)
                {
                    if (stroke.Count > 1)
                        g.DrawLines(inkPen, stroke.ToArray());
                }
            }

            var cropped = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(cropped))
            {
                g2.DrawImage(full, new Rectangle(0, 0, cw, ch), new Rectangle(l, t, cw, ch), GraphicsUnit.Pixel);
            }
            return cropped;
        }
    }
}
