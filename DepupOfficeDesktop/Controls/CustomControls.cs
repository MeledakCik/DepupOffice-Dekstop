using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Kartu kaca membulat: gradasi kilau halus dari atas, tepi tipis bercahaya, garis sorot di sisi atas,
    /// border (putus-putus opsional), garis aksen kiri opsional, dan efek hover.
    /// </summary>
    public class CardPanel : Panel
    {
        private bool _hover;
        public int CornerRadius { get; set; } = 20;
        public Color BorderColor { get; set; } = Theme.LINE;
        public int BorderThickness { get; set; } = 1;
        public bool DashedBorder { get; set; }
        public bool Hoverable { get; set; }
        public Color? GradientStart { get; set; }
        public Color? GradientEnd { get; set; }
        /// <summary>Garis aksen vertikal di tepi kiri (mis. kartu statistik aktif).</summary>
        public Color? AccentEdge { get; set; }
        /// <summary>Kilau kaca (gradasi + garis sorot atas). Default mati: gaya Android rata tanpa gradasi.</summary>
        public bool Glass { get; set; }

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

        /// <summary>Kartu kaca: permukaan biasa (SURFACE) di jendela kaca saat efek kaca aktif.</summary>
        private bool IsGlassCard => !GradientStart.HasValue && BackColor.ToArgb() == Theme.SURFACE.ToArgb() && GlassBackdrop.Active(this);

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // sudut membulat menampilkan apa pun yang ada di belakang kartu
            Color pb = Parent?.BackColor ?? Theme.BG;
            if (pb.A < 255 || Parent is GlassPane) ButtonRenderer.DrawParentBackground(e.Graphics, ClientRectangle, this);
            else
            {
                using var b = new SolidBrush(pb);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (IsGlassCard)
            {
                PaintGlassCard(g);
                return;
            }
            int inset = BorderThickness / 2;
            var rect = new Rectangle(inset, inset, Width - 1 - BorderThickness, Height - 1 - BorderThickness);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int radius = Theme.S(CornerRadius);
            using var path = Theme.RoundedRect(rect, radius);
            bool hot = Hoverable && _hover;

            if (GradientStart.HasValue && GradientEnd.HasValue)
            {
                using var lgb = new LinearGradientBrush(ClientRectangle, GradientStart.Value, GradientEnd.Value, LinearGradientMode.ForwardDiagonal);
                g.FillPath(lgb, path);
                if (Glass)
                {
                    // sorotan kaca: lapisan putih tipis di separuh atas
                    var top = new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(1, rect.Height / 2));
                    using var gloss = new LinearGradientBrush(new Rectangle(top.X, top.Y - 1, top.Width, top.Height + 2),
                        Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                    var oldClip = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    g.FillRectangle(gloss, top);
                    g.Clip = oldClip;
                }
            }
            else
            {
                Color baseCol = BackColor;
                if (Glass && Theme.Luma(BackColor) < 0.3)
                {
                    using var lgb = new LinearGradientBrush(new Rectangle(0, -1, Width, Height + 2),
                        Theme.Mix(baseCol, Color.White, 0.045), baseCol, LinearGradientMode.Vertical);
                    g.FillPath(lgb, path);
                }
                else
                {
                    using var sb = new SolidBrush(baseCol);
                    g.FillPath(sb, path);
                }
            }

            if (AccentEdge is Color edge)
            {
                var oldClip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                using var eb = new SolidBrush(edge);
                g.FillRectangle(eb, rect.X, rect.Y, Theme.S(3), rect.Height);
                g.Clip = oldClip;
            }

            // Gaya Android: kartu rata tanpa garis di atas latar halaman. Garis hanya bila kartu menyatu dengan
            // latar induknya (mis. di dialog), bila putus-putus / diberi warna khusus, atau saat disorot.
            bool blends = (Parent?.BackColor ?? Theme.BG).ToArgb() == BackColor.ToArgb();
            bool custom = BorderColor.ToArgb() != Theme.LINE.ToArgb();
            if (BorderThickness > 0 && (blends || DashedBorder || custom || hot || Theme.Dark && !GradientStart.HasValue))
            {
                Color border = hot ? Theme.ACCENT : BorderColor;
                using var pen = new Pen(border, hot ? Math.Max(1.5f, BorderThickness) : BorderThickness);
                if (DashedBorder && !hot) pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, path);
            }
            else if (hot)
            {
                using var pen = new Pen(Theme.ACCENT, 1.5f);
                g.DrawPath(pen, path);
            }

            // garis sorot kaca di tepi atas
            if (Glass && rect.Width > radius * 2 + 4)
            {
                using var hl = new Pen(Color.FromArgb(GradientStart.HasValue ? 70 : 26, 255, 255, 255), 1f);
                g.DrawLine(hl, rect.X + radius, rect.Y + 1, rect.Right - radius, rect.Y + 1);
            }
        }

        /// <summary>Kartu kaca: latar buram + lapisan putih/gelap semi-transparan + tepi sorot (gaya kaca Android).</summary>
        private void PaintGlassCard(Graphics g)
        {
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            int radius = Theme.S(CornerRadius);
            using var path = Theme.RoundedRect(rect, radius);
            GlassBackdrop.FillGlass(this, g, path, Theme.SURFACE);
            bool hot = Hoverable && _hover;
            if (hot)
                using (var hb = new SolidBrush(Color.FromArgb(Theme.Dark ? 18 : 60, 255, 255, 255))) g.FillPath(hb, path);
            // tepi: sorot terang di atas, garis halus di sekeliling
            using (var edge = new Pen(hot ? Theme.ACCENT : GlassBackdrop.Edge, hot ? 1.5f : 1f)) g.DrawPath(edge, path);
            if (!hot && !Theme.Dark)
                using (var line = new Pen(Color.FromArgb(40, Theme.LINE_STRONG))) g.DrawPath(line, path);
            if (DashedBorder)
                using (var dp = new Pen(BorderColor, Math.Max(1, BorderThickness)) { DashStyle = DashStyle.Dash }) g.DrawPath(dp, path);
            if (AccentEdge is Color edgeC)
            {
                var st = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                using var eb = new SolidBrush(edgeC);
                g.FillRectangle(eb, 0, 0, Theme.S(3), Height);
                g.Restore(st);
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
            bool darkFill = Theme.Luma(fill) < 0.5;
            if (Enabled && _down) fill = darkFill ? Theme.Mix(fill, Color.Black, 0.18) : Shade(fill, 0.86);
            else if (Enabled && _hover) fill = darkFill ? Theme.Mix(fill, Color.White, 0.08) : Shade(fill, 0.93);
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);

            if (FlatAppearance.BorderSize > 0)
            {
                Color bc = FlatAppearance.BorderColor.IsEmpty ? Theme.LINE : FlatAppearance.BorderColor;
                if (Enabled && _hover && darkFill) bc = Theme.Mix(bc, Color.White, 0.12);
                using var pen = new Pen(bc, FlatAppearance.BorderSize);
                g.DrawPath(pen, path);
            }

            var textRect = new Rectangle(Padding.Left, 0, Math.Max(1, Width - Padding.Horizontal), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.FAINT,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                var ring = Rectangle.Inflate(rect, -2, -2);
                using var ringPath = Theme.RoundedRect(ring, Math.Max(2, radius - 2));
                using var ringPen = new Pen(Theme.ACCENT_LIGHT, 2f);
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
        /// <summary>Garis tepi tipis (gaya lencana kaca). Null = tanpa garis.</summary>
        public Color? Outline { get; set; }
        /// <summary>Titik status kecil di kiri teks (mis. hijau = aktif).</summary>
        public Color? Dot { get => _dot; set { _dot = value; Fit(); Invalidate(); } }
        private Color? _dot;

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
            Size = new Size(sz.Width + Theme.S(22) + (Dot.HasValue ? Theme.S(16) : 0), sz.Height + Theme.S(8));
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
            if (Outline is Color oc)
                using (var pen = new Pen(oc)) g.DrawPath(pen, path);
            var textRect = new Rectangle(0, 0, Width, Height);
            if (Dot is Color dc)
            {
                int d = Theme.S(6);
                int dx = Theme.S(10);
                using (var db = new SolidBrush(dc)) g.FillEllipse(db, dx, (Height - d) / 2, d, d);
                textRect = new Rectangle(dx + d, 0, Width - dx - d, Height);
            }
            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, textRect, _fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    public enum NavItemIcon
    {
        Home,
        Docs,
        Create,
        Tools,
        User,
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
        private static Color Inactive => Theme.MUTED;
        private bool _active, _hover;

        public string Caption { get; set; } = "";
        /// <summary>Tautan sekunder (teks abu), mis. Privacy / Tentang di bawah sidebar.</summary>
        public bool Muted { get; set; }
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
            Color pb = Parent?.BackColor ?? Theme.SURFACE;
            if (pb.A < 255) ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
            else using (var bgBrush = new SolidBrush(pb)) g.FillRectangle(bgBrush, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int vGap = Theme.S(3);
            var body = new Rectangle(0, vGap, Width - 1, Height - 1 - vGap * 2);
            if (body.Width <= 0 || body.Height <= 0) return;
            int radius = body.Height / 2;   // pil penuh seperti navigasi bawah Android
            using var path = Theme.RoundedRect(body, radius);
            if (_active)
                using (var b = new SolidBrush(Theme.ACCENT)) g.FillPath(b, path);
            else if (_hover)
                using (var b = new SolidBrush(pb.A < 255 ? Color.FromArgb(Theme.Dark ? 40 : 110, Theme.Dark ? Color.White : Theme.SURFACE2) : Theme.SURFACE2)) g.FillPath(b, path);

            Color fg = _active ? Color.White : (Muted ? Inactive : Theme.TEXT);
            int chipSize = Theme.S(20);
            var chip = new Rectangle(Theme.S(16), (Height - chipSize) / 2, chipSize, chipSize);
            DrawIcon(g, chip, fg);

            var textRect = new Rectangle(chip.Right + Theme.S(12), 0, Math.Max(1, Width - chip.Right - Theme.S(24)), Height);
            TextRenderer.DrawText(g, Caption, Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                using var ringPath = Theme.RoundedRect(Rectangle.Inflate(body, -1, -1), Math.Max(2, radius - 1));
                using var ringPen = new Pen(_active ? Color.White : Theme.ACCENT, 2f);
                g.DrawPath(ringPen, ringPath);
            }
        }

        private void DrawIcon(Graphics g, Rectangle chip, Color color)
        {
            float scale = Theme.Scale;
            float x = chip.X + (chip.Width - 16 * scale) / 2f;
            float y = chip.Y + (chip.Height - 16 * scale) / 2f;
            using var pen = new Pen(color, Math.Max(1f, 1.6f * scale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            PointF P(float px, float py) => new(x + px * scale, y + py * scale);

            switch (Icon)
            {
                case NavItemIcon.Home:
                    using (var house = new GraphicsPath())
                    {
                        house.AddLines(new[] { P(2.5f, 7.5f), P(8, 2.5f), P(13.5f, 7.5f), P(13.5f, 14), P(2.5f, 14) });
                        house.CloseFigure();
                        g.DrawPath(pen, house);
                    }
                    g.DrawLine(pen, P(6.5f, 14), P(6.5f, 10));
                    g.DrawLine(pen, P(6.5f, 10), P(9.5f, 10));
                    g.DrawLine(pen, P(9.5f, 10), P(9.5f, 14));
                    break;
                case NavItemIcon.Docs:
                    using (var folder = new GraphicsPath())
                    {
                        folder.AddLines(new[] { P(2, 4), P(6.5f, 4), P(8, 5.5f), P(14, 5.5f), P(14, 13.5f), P(2, 13.5f) });
                        folder.CloseFigure();
                        g.DrawPath(pen, folder);
                    }
                    g.DrawLine(pen, P(2, 7.5f), P(14, 7.5f));
                    break;
                case NavItemIcon.Create:
                    using (var page = new GraphicsPath())
                    {
                        page.AddLines(new[] { P(3.5f, 2), P(9.5f, 2), P(12.5f, 5), P(12.5f, 14), P(3.5f, 14) });
                        page.CloseFigure();
                        g.DrawPath(pen, page);
                    }
                    g.DrawLine(pen, P(8, 7), P(8, 11.5f));
                    g.DrawLine(pen, P(5.75f, 9.25f), P(10.25f, 9.25f));
                    break;
                case NavItemIcon.Tools:
                    foreach (var (ox, oy) in new[] { (2.5f, 2.5f), (9f, 2.5f), (2.5f, 9f), (9f, 9f) })
                    {
                        using var r = Theme.RoundedRect(Rectangle.Round(new RectangleF(x + ox * scale, y + oy * scale, 4.5f * scale, 4.5f * scale)), (int)Math.Max(1, 1.3f * scale));
                        g.DrawPath(pen, r);
                    }
                    break;
                case NavItemIcon.User:
                    g.DrawEllipse(pen, x + 5 * scale, y + 2 * scale, 6 * scale, 6 * scale);
                    g.DrawArc(pen, x + 2.5f * scale, y + 9.5f * scale, 11 * scale, 10 * scale, 180, 180);
                    break;
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
                case NavItemIcon.Scan: // ikon "Buat": kotak dengan tanda plus
                    g.DrawRectangle(pen, x + 2 * scale, y + 2 * scale, 12 * scale, 12 * scale);
                    g.DrawLine(pen, P(8, 5), P(8, 11));
                    g.DrawLine(pen, P(5, 8), P(11, 8));
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
                        TextRenderer.DrawText(g, "i", font, Rectangle.Round(new RectangleF(x, y - scale, 16 * scale, 17 * scale)), color,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    break;
            }
        }
    }

    /// <summary>Kolom pencarian kaca dengan TextBox tanpa border di dalamnya dan petunjuk pintasan di kanan.</summary>
    public class SearchBox : Control
    {
        public TextBox Input { get; }
        /// <summary>Teks pintasan di sisi kanan (mis. "Ctrl+K"). Kosong = disembunyikan.</summary>
        public string Shortcut { get; set; } = "Ctrl+K";

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Input = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SURFACE,
                ForeColor = Theme.TEXT,
                Font = Theme.FontBody(10.5f)
            };
            Input.Enter += (s, e) => Invalidate();
            Input.Leave += (s, e) => Invalidate();
            Controls.Add(Input);
        }

        private int IconZone => Theme.S(46);
        private int ShortcutZone => string.IsNullOrEmpty(Shortcut) || Width < Theme.S(260) ? Theme.S(14) : Theme.S(76);

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int h = Input.PreferredHeight;
            Input.SetBounds(IconZone, Math.Max(0, (Height - h) / 2), Math.Max(10, Width - IconZone - ShortcutZone), h);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Input.Focus(); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using var path = Theme.RoundedRect(rect, rect.Height / 2);   // pil penuh seperti kolom cari Android
            using (var b = new SolidBrush(Theme.SURFACE)) g.FillPath(b, path);
            using (var pen = new Pen(Input.Focused ? Theme.ACCENT : Theme.LINE, Input.Focused ? 2f : 1f))
                g.DrawPath(pen, path);

            // ikon kaca pembesar
            float s = Theme.Scale, cx = Theme.S(24), cy = Height / 2f;
            using var ip = new Pen(Theme.TEXT, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(ip, cx - 6 * s, cy - 6 * s, 11 * s, 11 * s);
            g.DrawLine(ip, cx + 3.5f * s, cy + 3.5f * s, cx + 7.5f * s, cy + 7.5f * s);

            // petunjuk pintasan
            if (ShortcutZone > Theme.S(20))
            {
                using var f = Theme.FontMono(8f);
                var sz = TextRenderer.MeasureText(Shortcut, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                var kr = new Rectangle(Width - Theme.S(12) - sz.Width - Theme.S(12), (Height - sz.Height - Theme.S(8)) / 2, sz.Width + Theme.S(12), sz.Height + Theme.S(8));
                using var kp = Theme.RoundedRect(kr, kr.Height / 2);
                using (var kb = new SolidBrush(Theme.SURFACE2)) g.FillPath(kb, kp);
                TextRenderer.DrawText(g, Shortcut, f, kr, Theme.MUTED, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override void OnGotFocus(EventArgs e) { Input.Focus(); base.OnGotFocus(e); }
    }

    /// <summary>Ilustrasi kartu hero beranda (gaya Android): sudut bingkai putih + kertas putih bergaris, latar transparan.</summary>
    public class HeroDocArt : Control
    {
        public HeroDocArt()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Width, h = Height;
            if (w < 20 || h < 20) return;
            float stroke = Math.Max(2f, w * 0.03f), L = w * 0.24f;
            using var corner = new Pen(Color.White, stroke) { StartCap = LineCap.Square, EndCap = LineCap.Square };
            float x1 = stroke, y1 = stroke, x2 = w - stroke, y2 = h - stroke;
            g.DrawLines(corner, new[] { new PointF(x1, y1 + L), new PointF(x1, y1), new PointF(x1 + L, y1) });
            g.DrawLines(corner, new[] { new PointF(x2 - L, y1), new PointF(x2, y1), new PointF(x2, y1 + L) });
            g.DrawLines(corner, new[] { new PointF(x1, y2 - L), new PointF(x1, y2), new PointF(x1 + L, y2) });
            g.DrawLines(corner, new[] { new PointF(x2 - L, y2), new PointF(x2, y2), new PointF(x2, y2 - L) });

            float pw = w * 0.5f, ph = h * 0.78f, pl = (w - pw) / 2f, pt = (h - ph) / 2f;
            using (var paper = Theme.RoundedRect(Rectangle.Round(new RectangleF(pl, pt, pw, ph)), (int)(pw * 0.12f)))
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, paper);
            using var ink = new Pen(Color.FromArgb(150, 160, 160, 160), Math.Max(2f, w * 0.03f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 5; i++)
            {
                float y = pt + ph * (0.18f + i * 0.16f);
                float lw = pw * (i % 2 == 0 ? 0.6f : 0.4f);
                g.DrawLine(ink, pl + pw * 0.18f, y, pl + pw * 0.18f + lw, y);
            }
        }
    }

    public enum CircleGlyph { Person, Moon, Sun, Search }

    /// <summary>Tombol lingkaran (pojok kanan atas, gaya Android): kartu putih bergaris tipis dengan ikon.</summary>
    public class CircleButton : Button
    {
        private bool _hover;
        private CircleGlyph _glyph = CircleGlyph.Person;
        public CircleGlyph Glyph { get => _glyph; set { _glyph = value; Invalidate(); } }

        public CircleButton()
        {
            // Button asli (bukan Control polos) agar klik, Enter/Spasi, dan fokus keyboard selalu bekerja.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            Text = "";
        }

        /// <summary>Dipicu saat tombol ditekan (mouse kiri, Enter, atau Spasi).</summary>
        public event EventHandler? Pressed;
        private bool _down;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _down = true; base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = _down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            _down = false;
            base.OnMouseUp(e);
            if (fire) Pressed?.Invoke(this, EventArgs.Empty);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode is Keys.Space or Keys.Enter) Pressed?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            try { ButtonRenderer.DrawParentBackground(g, ClientRectangle, this); }
            catch { g.Clear(Parent?.BackColor ?? Theme.BG); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
            using (var b = new SolidBrush(_hover ? Theme.SURFACE2 : Theme.SURFACE)) g.FillEllipse(b, r);
            using (var p = new Pen(Theme.LINE)) g.DrawEllipse(p, r);

            float s = Theme.Scale * Math.Min(Width, Height) / (44f * Theme.Scale), cx = Width / 2f, cy = Height / 2f;
            using var pen = new Pen(Theme.TEXT, 1.7f * Theme.Scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float k = s * Theme.Scale;
            switch (_glyph)
            {
                case CircleGlyph.Person:
                    g.DrawEllipse(pen, cx - 4.5f * k, cy - 9f * k, 9f * k, 9f * k);
                    g.DrawArc(pen, cx - 8f * k, cy + 1.5f * k, 16f * k, 14f * k, 180, 180);
                    break;
                case CircleGlyph.Search:
                    g.DrawEllipse(pen, cx - 7 * k, cy - 7 * k, 12 * k, 12 * k);
                    g.DrawLine(pen, cx + 3.5f * k, cy + 3.5f * k, cx + 7.5f * k, cy + 7.5f * k);
                    break;
                case CircleGlyph.Moon:
                    {
                        // bulan sabit terisi (ikon mode gelap)
                        using var outer = new GraphicsPath();
                        outer.AddEllipse(cx - 7.5f * k, cy - 7.5f * k, 15 * k, 15 * k);
                        using var inner = new GraphicsPath();
                        inner.AddEllipse(cx - 3f * k, cy - 11f * k, 15 * k, 15 * k);
                        using var region = new Region(outer);
                        region.Exclude(inner);
                        using var mb = new SolidBrush(Theme.TEXT);
                        g.FillRegion(mb, region);
                    }
                    break;
                case CircleGlyph.Sun:
                    g.DrawEllipse(pen, cx - 4.5f * k, cy - 4.5f * k, 9 * k, 9 * k);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4;
                        float c1 = 7.5f * k, c2 = 10f * k;
                        g.DrawLine(pen, cx + (float)Math.Cos(a) * c1, cy + (float)Math.Sin(a) * c1, cx + (float)Math.Cos(a) * c2, cy + (float)Math.Sin(a) * c2);
                    }
                    break;
            }
            if (Focused && ShowFocusCues)
                using (var fp = new Pen(Theme.ACCENT, 2f)) g.DrawEllipse(fp, 1.5f, 1.5f, Width - 4f, Height - 4f);
        }
    }

    /// <summary>
    /// Ubin logo Depup (sama dengan ikon aplikasi Android / ic_logo_tile.xml): kotak biru #0037FF bersudut 35%
    /// dengan tanda "D" putih — huruf D bersudut kiri atas terpotong miring, lensa bulat di dalam, persegi kecil di sudut.
    /// </summary>
    public class LogoTile : Control
    {
        /// <summary>true = ubin biru + tanda putih; false = tanda satu warna (ikut tema) tanpa ubin.</summary>
        public bool Tile { get; set; } = true;

        public LogoTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        /// <summary>Gambar logo pada kotak <paramref name="r"/> (ruang desain 2000×2000, sama dengan vektor Android).</summary>
        public static void Draw(Graphics g, RectangleF r, bool tile, Color mark)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = Math.Min(r.Width, r.Height) / 2000f;
            PointF P(float x, float y) => new(r.X + x * u, r.Y + y * u);
            if (tile)
            {
                using var bg = Theme.RoundedRect(Rectangle.Round(r), (int)(708.1f * u));
                using var b = new SolidBrush(Theme.BRAND);
                g.FillPath(b, bg);
            }
            using var mk = new GraphicsPath(FillMode.Alternate);
            mk.StartFigure();
            mk.AddLine(P(1075.32f, 481.36f), P(539.81f, 1016.87f));
            mk.AddLine(P(539.81f, 1016.87f), P(539.81f, 1518.9f));
            mk.AddLine(P(539.81f, 1518.9f), P(1058.71f, 1518.9f));
            mk.AddBezier(P(1058.71f, 1518.9f), P(1344.83f, 1518.9f), P(1577.61f, 1286.11f), P(1577.61f, 1000f));
            mk.AddBezier(P(1577.61f, 1000f), P(1577.61f, 713.89f), P(1353.77f, 490.15f), P(1075.32f, 481.36f));
            mk.CloseFigure();
            mk.AddEllipse(r.X + (1058.71f - 251.03f) * u, r.Y + (1000f - 251.03f) * u, 502.06f * u, 502.06f * u);
            using var mb = new SolidBrush(mark);
            g.FillPath(mb, mk);
            g.FillRectangle(mb, r.X + 539.81f * u, r.Y + 481.1f * u, (807.69f - 539.81f) * u, (748.98f - 481.1f) * u);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float side = Math.Min(Width, Height) - 1;
            if (side <= 4) return;
            Draw(e.Graphics, new RectangleF((Width - side) / 2f, (Height - side) / 2f, side, side), Tile, Tile ? Color.White : Theme.TEXT);
        }
    }

    /// <summary>
    /// Kontrol segmen (Android <c>segmented</c>): trek abu membulat, pilihan aktif berupa pil putih/kartu.
    /// Bisa dipakai dengan keyboard (panah kiri/kanan).
    /// </summary>
    public class Segmented : Control
    {
        private string[] _items = Array.Empty<string>();
        private int _sel;
        private int _hover = -1;
        public event Action<int>? SelectedChanged;

        public string[] Items { get => _items; set { _items = value ?? Array.Empty<string>(); Invalidate(); } }
        public int SelectedIndex
        {
            get => _sel;
            set { int v = Math.Clamp(value, 0, Math.Max(0, _items.Length - 1)); if (v == _sel) return; _sel = v; Invalidate(); SelectedChanged?.Invoke(v); }
        }

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.FontLabel(9.5f);
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Theme.S(42);
        }

        private RectangleF Slot(int i)
        {
            float pad = Theme.S(4);
            float w = (Width - pad * 2) / Math.Max(1, _items.Length);
            return new RectangleF(pad + i * w, pad, w, Height - pad * 2);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Length; i++) if (Slot(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { int h = HitTest(e.Location); if (h >= 0) { Focus(); SelectedIndex = h; } base.OnMouseDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) SelectedIndex = Math.Max(0, _sel - 1);
            else if (e.KeyCode == Keys.Right) SelectedIndex = Math.Min(_items.Length - 1, _sel + 1);
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var track = Theme.RoundedRect(rect, rect.Height / 2))
            using (var b = new SolidBrush(Theme.SURFACE2))
                g.FillPath(b, track);
            for (int i = 0; i < _items.Length; i++)
            {
                var r = Rectangle.Round(Slot(i));
                if (i == _sel)
                {
                    using var thumb = Theme.RoundedRect(r, r.Height / 2);
                    using var tb = new SolidBrush(Theme.Dark ? Theme.SURFACE3 : Theme.SURFACE);
                    g.FillPath(tb, thumb);
                    if (Focused && ShowFocusCues)
                        using (var fp = new Pen(Theme.ACCENT, 2f)) g.DrawPath(fp, thumb);
                }
                else if (i == _hover)
                {
                    using var hp = Theme.RoundedRect(r, r.Height / 2);
                    using var hb = new SolidBrush(Theme.SURFACE3);
                    g.FillPath(hb, hp);
                }
                TextRenderer.DrawText(g, _items[i], Font, r, i == _sel ? Theme.TEXT : Theme.MUTED,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
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

    /// <summary>
    /// Baris daftar gaya Android (<c>listCard</c> / <c>createRow</c>): ikon di wadah lembut, judul + keterangan,
    /// chevron di kanan, garis pemisah tipis di bawah. Satu kartu berisi beberapa baris = daftar yang dikelompokkan.
    /// Kontrol tambahan (mis. tombol bintang / ⋯) bisa ditaruh di kanan; sediakan ruangnya lewat <see cref="AccessoryWidth"/>.
    /// </summary>
    public class ListRow : Control
    {
        private bool _hover;
        public CardIcon Icon { get; set; } = CardIcon.Pdf;
        public Color IconFg { get; set; } = Theme.ACCENT;
        public Color IconBg { get; set; } = Theme.BLUE_BG;
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public bool Chevron { get; set; } = true;
        public bool Divider { get; set; } = true;
        public int AccessoryWidth { get; set; }
        /// <summary>Teks kecil di kanan (mis. ukuran berkas). Kosong = tidak ada.</summary>
        public string Trailing { get; set; } = "";

        public ListRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = Theme.S(68);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        {
            // tetap tersorot bila kursor pindah ke tombol tambahan di dalam baris
            _hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Invalidate();
            base.OnMouseLeave(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }
        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control == null) return;
            e.Control.MouseEnter += (s, a) => { _hover = true; Invalidate(); };
            e.Control.MouseLeave += (s, a) => { _hover = ClientRectangle.Contains(PointToClient(Cursor.Position)); Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.S(8);
            if (_hover || Focused && ShowFocusCues)
            {
                var hr = new Rectangle(pad / 2, Theme.S(3), Width - pad - 1, Height - Theme.S(6) - 1);
                using var hp = Theme.RoundedRect(hr, Theme.S(14));
                using (var hb = new SolidBrush(Theme.Dark ? Theme.SURFACE2 : Theme.SOFT)) g.FillPath(hb, hp);
                if (Focused && ShowFocusCues) using (var fp = new Pen(Theme.ACCENT, 2f)) g.DrawPath(fp, hp);
            }

            int chip = Theme.S(42);
            var cr = new Rectangle(Theme.S(14), (Height - chip) / 2, chip, chip);
            using (var cp = Theme.RoundedRect(cr, Theme.S(12)))
            using (var cb = new SolidBrush(IconBg))
                g.FillPath(cb, cp);
            float side = chip * 0.54f;
            CardIconPainter.Draw(g, new RectangleF(cr.X + (chip - side) / 2f, cr.Y + (chip - side) / 2f, side, side), Icon, IconFg, IconBg);

            int tx = cr.Right + Theme.S(14);
            int right = Width - Theme.S(14) - (Chevron ? Theme.S(18) : 0) - AccessoryWidth;
            int trailW = 0;
            using var fTrail = Theme.FontBody(8.5f);
            if (!string.IsNullOrEmpty(Trailing))
            {
                trailW = TextRenderer.MeasureText(Trailing, fTrail).Width + Theme.S(8);
                TextRenderer.DrawText(g, Trailing, fTrail, new Rectangle(right - trailW, 0, trailW, Height), Theme.MUTED,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            int tw = Math.Max(Theme.S(40), right - trailW - tx - Theme.S(6));
            using var fTitle = Theme.FontLabel(10.5f);
            using var fSub = Theme.FontBody(9f);
            bool hasSub = !string.IsNullOrEmpty(Subtitle);
            int th = TextRenderer.MeasureText("Ag", fTitle).Height;
            int sh = hasSub ? TextRenderer.MeasureText("Ag", fSub).Height : 0;
            int ty = (Height - th - sh) / 2;
            const TextFormatFlags one = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, Title, fTitle, new Rectangle(tx, ty, tw, th), Theme.TEXT, one);
            if (hasSub)
                TextRenderer.DrawText(g, Subtitle, fSub, new Rectangle(tx, ty + th, tw, sh), Theme.MUTED, one);

            if (Chevron)
            {
                float cx = Width - Theme.S(22) - AccessoryWidth, cy = Height / 2f, k = Theme.Scale;
                using var pen = new Pen(Theme.MUTED, 1.7f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(pen, new[] { new PointF(cx - 2.5f * k, cy - 5 * k), new PointF(cx + 2.5f * k, cy), new PointF(cx - 2.5f * k, cy + 5 * k) });
            }
            if (Divider)
            {
                using var lp = new Pen(Theme.LINE);
                g.DrawLine(lp, tx, Height - 1, Width - Theme.S(14), Height - 1);
            }
        }
    }
}
