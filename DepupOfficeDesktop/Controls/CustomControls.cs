using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>Kartu membulat. Menggambar sendiri latar, gradasi, border (putus-putus opsional) dan efek hover.</summary>
    public class CardPanel : Panel
    {
        private bool _hover;
        public int CornerRadius { get; set; } = 14;
        public Color BorderColor { get; set; } = Theme.LINE;
        public int BorderThickness { get; set; } = 1;
        public bool DashedBorder { get; set; }
        public bool Hoverable { get; set; }
        public Color? GradientStart { get; set; }
        public Color? GradientEnd { get; set; }

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.SURFACE;
            Padding = new Padding(16);
        }

        /// <summary>Aktifkan efek hover untuk kartu beserta seluruh anak kontrolnya (panggil setelah semua anak ditambahkan).</summary>
        public void AttachHover()
        {
            Hoverable = true;
            void Hook(Control c)
            {
                c.MouseEnter += (s, e) => UpdateHover();
                c.MouseLeave += (s, e) => UpdateHover();
                foreach (Control child in c.Controls) Hook(child);
            }
            Hook(this);
        }

        private void UpdateHover()
        {
            if (!IsHandleCreated || IsDisposed) return;
            bool inside = ClientRectangle.Contains(PointToClient(Cursor.Position));
            if (inside != _hover)
            {
                _hover = inside;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var b = new SolidBrush(Parent?.BackColor ?? Theme.BG);
            e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int inset = BorderThickness / 2;
            var rect = new Rectangle(inset, inset, Width - 1 - BorderThickness, Height - 1 - BorderThickness);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using var path = Theme.RoundedRect(rect, Theme.S(CornerRadius));

            if (GradientStart.HasValue && GradientEnd.HasValue && Width > 0 && Height > 0)
            {
                using var lgb = new LinearGradientBrush(ClientRectangle, GradientStart.Value, GradientEnd.Value, LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillPath(lgb, path);
            }
            else
            {
                using var sb = new SolidBrush(BackColor);
                e.Graphics.FillPath(sb, path);
            }

            if (BorderThickness > 0)
            {
                Color border = Hoverable && _hover ? Theme.ACCENT : BorderColor;
                using var pen = new Pen(border, BorderThickness);
                if (DashedBorder) pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Bilah proporsi (penyimpanan / progres) dengan ujung membulat.</summary>
    public class StackedStorageBar : Control
    {
        public List<(long Value, Color Color)> Parts { get; set; } = new();
        public Color TrackColor { get; set; } = Theme.LINE;

        public StackedStorageBar()
        {
            DoubleBuffered = true;
            Height = 12;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using var path = Theme.RoundedRect(rect, Height / 2);
            using (var trackBrush = new SolidBrush(TrackColor))
                g.FillPath(trackBrush, path);

            long total = Parts.Sum(p => Math.Max(0, p.Value));
            if (total <= 0) return;

            g.SetClip(path, CombineMode.Replace);
            float x = 0f;
            foreach (var (val, col) in Parts.Where(p => p.Value > 0))
            {
                float w = (float)val / total * Width;
                using var b = new SolidBrush(col);
                g.FillRectangle(b, x, 0, Math.Max(4f, w), Height);
                x += w;
            }
            g.ResetClip();
        }
    }

    /// <summary>Tombol datar membulat (mengikuti desain: radius sedang, hover halus, fokus keyboard terlihat).</summary>
    public class PillButton : Button
    {
        private bool _hover, _down;
        public int CornerRadius { get; set; } = 10;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private static Color Shade(Color c, double factor) =>
            Color.FromArgb(c.A, (int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            try { ButtonRenderer.DrawParentBackground(g, ClientRectangle, this); }
            catch { g.Clear(Parent?.BackColor ?? Theme.BG); }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int radius = Math.Min(Theme.S(CornerRadius), Math.Min(rect.Width, rect.Height) / 2);
            using var path = Theme.RoundedRect(rect, radius);

            Color fill = Enabled ? BackColor : Theme.SURFACE2;
            if (Enabled && _down) fill = Shade(fill, 0.88);
            else if (Enabled && _hover) fill = Shade(fill, 0.94);
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);

            if (FlatAppearance.BorderSize > 0)
            {
                using var pen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize);
                g.DrawPath(pen, path);
            }

            var textRect = new Rectangle(Padding.Left, 0, Math.Max(1, Width - Padding.Horizontal), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.MUTED,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                var ring = Rectangle.Inflate(rect, -2, -2);
                using var ringPath = Theme.RoundedRect(ring, Math.Max(2, radius - 2));
                using var ringPen = new Pen(Theme.ACCENT, 2f);
                g.DrawPath(ringPen, ringPath);
            }
        }
    }

    /// <summary>Lencana kapsul / kotak ikon dengan latar transparan (dipakai untuk tag, badge tipe file, avatar).</summary>
    public class Pill : Control
    {
        private Color _fg = Theme.ACCENT;
        private Color _bg = Theme.BLUE_BG;
        public Color Fg { get => _fg; set { _fg = value; Invalidate(); } }
        public Color Bg { get => _bg; set { _bg = value; Invalidate(); } }
        public bool AutoFit { get; set; } = true;
        /// <summary>Radius sudut dalam piksel nyata. 0 = kapsul penuh.</summary>
        public int Radius { get; set; } = 0;

        public Pill()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Font = Theme.FontLabel(8.5f);
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }

        public void Fit()
        {
            if (!AutoFit) return;
            var sz = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            Size = new Size(sz.Width + Theme.S(20), sz.Height + Theme.S(8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int r = Radius > 0 ? Math.Min(Radius, Math.Min(rect.Width, rect.Height) / 2) : rect.Height / 2;
            using var path = Theme.RoundedRect(rect, r);
            using (var b = new SolidBrush(_bg))
                g.FillPath(b, path);
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), _fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    public enum NavItemIcon
    {
        Book,
        Pdf,
        Scan,
        Vault,
        Privacy,
        Contact,
        Donate,
        About
    }

    /// <summary>Item navigasi sidebar: ikon + label, status aktif/hover, bisa difokus dengan keyboard.</summary>
    public class NavItem : Control
    {
        private static readonly Color Inactive = ColorTranslator.FromHtml("#475569");
        private static readonly Color ChipIdle = ColorTranslator.FromHtml("#BCC3D0");
        private bool _active, _hover;

        public string Caption { get; set; } = "";
        public NavItemIcon Icon { get; set; }
        public bool Active { get => _active; set { _active = value; Invalidate(); } }

        public NavItem()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontLabel(10.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bgBrush = new SolidBrush(Parent?.BackColor ?? Theme.SURFACE))
                g.FillRectangle(bgBrush, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int vGap = Theme.S(3);
            var body = new Rectangle(0, vGap, Width - 1, Height - 1 - vGap * 2);
            if (body.Width <= 0 || body.Height <= 0) return;
            using var path = Theme.RoundedRect(body, Theme.S(12));
            if (_active)
                using (var b = new SolidBrush(Theme.BLUE_BG)) g.FillPath(b, path);
            else if (_hover)
                using (var b = new SolidBrush(Theme.SURFACE2)) g.FillPath(b, path);

            int chipSize = Theme.S(20);
            var chip = new Rectangle(Theme.S(14), (Height - chipSize) / 2, chipSize, chipSize);
            using (var chipPath = Theme.RoundedRect(chip, Theme.S(6)))
            using (var chipBrush = new SolidBrush(_active ? Theme.ACCENT : ChipIdle))
                g.FillPath(chipBrush, chipPath);

            DrawIcon(g, chip);

            var textRect = new Rectangle(chip.Right + Theme.S(12), 0, Math.Max(1, Width - chip.Right - Theme.S(24)), Height);
            TextRenderer.DrawText(g, Caption, Font, textRect, _active ? Theme.ACCENT : Inactive,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                using var ringPath = Theme.RoundedRect(Rectangle.Inflate(body, -1, -1), Theme.S(11));
                using var ringPen = new Pen(Theme.ACCENT, 2f);
                g.DrawPath(ringPen, ringPath);
            }
        }

        private void DrawIcon(Graphics g, Rectangle chip)
        {
            float scale = Theme.Scale;
            float x = chip.X + (chip.Width - 16 * scale) / 2f;
            float y = chip.Y + (chip.Height - 16 * scale) / 2f;
            using var pen = new Pen(Color.White, Math.Max(1f, 1.4f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            PointF P(float px, float py) => new(x + px * scale, y + py * scale);

            switch (Icon)
            {
                case NavItemIcon.Book:
                    g.DrawLine(pen, P(8, 3), P(8, 14));
                    g.DrawLine(pen, P(8, 5), P(3, 4));
                    g.DrawLine(pen, P(3, 4), P(3, 13));
                    g.DrawLine(pen, P(3, 13), P(8, 14));
                    g.DrawLine(pen, P(8, 5), P(13, 4));
                    g.DrawLine(pen, P(13, 4), P(13, 13));
                    g.DrawLine(pen, P(13, 13), P(8, 14));
                    break;
                case NavItemIcon.Pdf:
                    g.DrawRectangle(pen, x + 3 * scale, y + 2 * scale, 9 * scale, 12 * scale);
                    g.DrawLine(pen, P(5, 6), P(10, 6));
                    g.DrawLine(pen, P(5, 9), P(10, 9));
                    g.DrawLine(pen, P(5, 12), P(9, 12));
                    break;
                case NavItemIcon.Scan:
                    g.DrawLine(pen, P(2, 6), P(2, 2));
                    g.DrawLine(pen, P(2, 2), P(6, 2));
                    g.DrawLine(pen, P(10, 2), P(14, 2));
                    g.DrawLine(pen, P(14, 2), P(14, 6));
                    g.DrawLine(pen, P(2, 10), P(2, 14));
                    g.DrawLine(pen, P(2, 14), P(6, 14));
                    g.DrawLine(pen, P(10, 14), P(14, 14));
                    g.DrawLine(pen, P(14, 10), P(14, 14));
                    break;
                case NavItemIcon.Vault:
                    g.DrawRectangle(pen, x + 2 * scale, y + 3 * scale, 12 * scale, 10 * scale);
                    g.DrawEllipse(pen, x + 6 * scale, y + 5 * scale, 4 * scale, 4 * scale);
                    g.DrawLine(pen, P(8, 7), P(8, 10));
                    break;
                case NavItemIcon.Privacy:
                    using (var shield = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        shield.AddPolygon(new[] { P(8, 2), P(13, 4), P(12, 10), P(8, 14), P(4, 10), P(3, 4) });
                        g.DrawPath(pen, shield);
                    }
                    break;
                case NavItemIcon.Contact:
                    g.DrawRectangle(pen, x + 2 * scale, y + 4 * scale, 12 * scale, 8 * scale);
                    g.DrawLine(pen, P(2, 5), P(8, 10));
                    g.DrawLine(pen, P(14, 5), P(8, 10));
                    break;
                case NavItemIcon.Donate:
                    using (var heart = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        heart.AddBezier(P(8, 13), P(6, 11), P(2, 8), P(2, 5));
                        heart.AddBezier(P(2, 5), P(2, 2), P(6, 2), P(8, 5));
                        heart.AddBezier(P(8, 5), P(10, 2), P(14, 2), P(14, 5));
                        heart.AddBezier(P(14, 5), P(14, 8), P(10, 11), P(8, 13));
                        g.DrawPath(pen, heart);
                    }
                    break;
                case NavItemIcon.About:
                    g.DrawEllipse(pen, x + 2 * scale, y + 2 * scale, 12 * scale, 12 * scale);
                    using (var font = new Font("Segoe UI", 9f * scale, FontStyle.Bold, GraphicsUnit.Point))
                        TextRenderer.DrawText(g, "i", font, Rectangle.Round(new RectangleF(x, y - scale, 16 * scale, 17 * scale)), Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    break;
            }
        }
    }

    /// <summary>Kolom pencarian membulat dengan TextBox tanpa border di dalamnya.</summary>
    public class SearchBox : Control
    {
        public TextBox Input { get; }

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Input = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SURFACE2,
                ForeColor = Theme.TEXT,
                Font = Theme.FontBody(10f)
            };
            Input.Enter += (s, e) => Invalidate();
            Input.Leave += (s, e) => Invalidate();
            Controls.Add(Input);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int h = Input.PreferredHeight;
            Input.SetBounds(Theme.S(16), Math.Max(0, (Height - h) / 2), Math.Max(10, Width - Theme.S(32)), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using var path = Theme.RoundedRect(rect, Theme.S(12));
            using (var b = new SolidBrush(Theme.SURFACE2)) g.FillPath(b, path);
            using var pen = new Pen(Input.Focused ? Theme.ACCENT : Theme.LINE, Input.Focused ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        protected override void OnGotFocus(EventArgs e) { Input.Focus(); base.OnGotFocus(e); }
    }

    /// <summary>
    /// Porting dari ScanFrameView (Ui.kt):
    /// Ilustrasi bingkai pindai (sudut putih + kertas + baris tinta) untuk kartu hero Buat.
    /// </summary>
    public class ScanFrameHeroControl : Control
    {
        public ScanFrameHeroControl()
        {
            DoubleBuffered = true;
            Height = 130;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Width, h = Height;
            if (w <= 10 || h <= 10) return;

            using var bgBrush = new SolidBrush(Color.FromArgb(77, 11, 42, 107));
            using var bgPath = Theme.RoundedRect(new Rectangle(0, 0, (int)w - 1, (int)h - 1), 14);
            g.FillPath(bgBrush, bgPath);

            float pw = w * 0.34f, ph = h * 0.74f;
            float pl = (w - pw) / 2f, pt = (h - ph) / 2f;
            using var paperBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.FillRectangle(paperBrush, pl, pt, pw, ph);

            using var inkBrush = new SolidBrush(Color.FromArgb(110, 30, 58, 138));
            for (int i = 0; i <= 4; i++)
            {
                float y = pt + ph * (0.16f + i * 0.16f);
                float lineW = pw * (i % 2 == 0 ? 0.76f : 0.50f);
                g.FillRectangle(inkBrush, pl + pw * 0.12f, y, lineW, 4f);
            }

            using var cornerPen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float m = 14f, L = 22f;
            float x1 = m, y1 = m, x2 = w - m, y2 = h - m;
            g.DrawLine(cornerPen, x1, y1 + L, x1, y1);
            g.DrawLine(cornerPen, x1, y1, x1 + L, y1);
            g.DrawLine(cornerPen, x2 - L, y1, x2, y1);
            g.DrawLine(cornerPen, x2, y1, x2, y1 + L);
            g.DrawLine(cornerPen, x1, y2 - L, x1, y2);
            g.DrawLine(cornerPen, x1, y2, x1 + L, y2);
            g.DrawLine(cornerPen, x2 - L, y2, x2, y2);
            g.DrawLine(cornerPen, x2, y2, x2, y2 - L);
        }
    }
}
