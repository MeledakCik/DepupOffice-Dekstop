using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>Dasar kontrol gambar-sendiri yang transparan (latar induk tampak di sudut).</summary>
    public abstract class PaintedControl : Control
    {
        protected static int S(int v) => Theme.S(v);
        protected bool Hot, Down;

        protected PaintedControl()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = Down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    }

    /// <summary>Satu ikon Android tanpa wadah.</summary>
    public sealed class IconGlyph : PaintedControl
    {
        private readonly string _icon;
        private readonly Color _color;
        public IconGlyph(string icon, Color color) { _icon = icon; _color = color; TabStop = false; }
        protected override void OnPaint(PaintEventArgs e) => DepupIcons.Draw(e.Graphics, _icon, ClientRectangle, _color);
    }

    /// <summary>Tombol pil putih di atas panel biru (Android heroBtn): ikon + label berwarna biru.</summary>
    public sealed class HeroButton : PaintedControl
    {
        private readonly string _icon;
        public HeroButton(string text, string icon)
        {
            Text = text; _icon = icon;
            Cursor = Cursors.Hand; TabStop = true;
            AccessibleName = text; AccessibleRole = AccessibleRole.PushButton;
            var f = Theme.FontLabel(10.5f, FontStyle.Bold);
            Size = new Size(TextRenderer.MeasureText(text, f).Width + S(18 + 18 + 8 + 20), S(44));
        }

        protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); return; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(r, Height / 2))
            using (var b = new SolidBrush(Down ? Color.FromArgb(0xE6, 0xEC, 0xFF) : Hot ? Color.FromArgb(0xF3, 0xF6, 0xFF) : Color.White))
                g.FillPath(b, path);
            int ic = S(18);
            DepupIcons.Draw(g, _icon, new RectangleF(S(18), (Height - ic) / 2f, ic, ic), Theme.ACCENT);
            TextRenderer.DrawText(g, Text, Theme.FontLabel(10.5f, FontStyle.Bold), new Rectangle(S(18) + ic + S(8), 0, Width - S(18) - ic - S(8), Height),
                Theme.ACCENT, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) using (var fp = new Pen(Color.White, 2)) using (var p2 = Theme.RoundedRect(Rectangle.Inflate(r, 2, 2), Height / 2 + 2)) g.DrawPath(fp, p2);
        }
    }

    /// <summary>Ilustrasi bingkai pindai di kartu Scan (Android ScanFrameView): kertas + sudut putih.</summary>
    public sealed class ScanFrameArt : PaintedControl
    {
        public ScanFrameArt() { TabStop = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Width, h = Height;
            float pw = w * 0.34f, ph = h * 0.74f, pl = (w - pw) / 2f, pt = (h - ph) / 2f;
            using (var paper = new SolidBrush(Theme.Dark ? Color.FromArgb(0xE8, 0xE6, 0xE1) : Color.White))
            using (var pp = Theme.RoundedRect(Rectangle.Round(new RectangleF(pl, pt, pw, ph)), S(4)))
                g.FillPath(paper, pp);
            using (var ink = new SolidBrush(Color.FromArgb(0x55, 0x15, 0x18, 0x23)))
                for (int i = 0; i <= 4; i++)
                {
                    float y = pt + ph * (0.16f + i * 0.16f);
                    g.FillRectangle(ink, pl + pw * 0.12f, y, pw * ((i % 2 == 0 ? 0.88f : 0.62f) - 0.12f), S(3));
                }
            float m = S(10), L = S(20);
            float x1 = pl - m, y1 = pt - m, x2 = pl + pw + m, y2 = pt + ph + m;
            using var pen = new Pen(Color.White, S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(x1, y1 + L), new PointF(x1, y1), new PointF(x1 + L, y1) });
            g.DrawLines(pen, new[] { new PointF(x2 - L, y1), new PointF(x2, y1), new PointF(x2, y1 + L) });
            g.DrawLines(pen, new[] { new PointF(x1, y2 - L), new PointF(x1, y2), new PointF(x1 + L, y2) });
            g.DrawLines(pen, new[] { new PointF(x2 - L, y2), new PointF(x2, y2), new PointF(x2, y2 - L) });
        }
    }

    /// <summary>Pintasan "Buka berkas" (Android quick): wadah 56 dp putih bersudut 16, ikon 24 dp, label di bawah.</summary>
    public sealed class QuickOpen : PaintedControl
    {
        private readonly string _icon;
        private readonly Color _fg;
        public QuickOpen(string icon, Color fg, string label)
        {
            _icon = icon; _fg = fg; Text = label;
            Cursor = Cursors.Hand; TabStop = true;
            AccessibleName = "Buka berkas " + label; AccessibleRole = AccessibleRole.PushButton;
        }
        protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); return; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Hot || Focused)
                using (var hp = Theme.RoundedRect(new Rectangle(S(4), 0, Width - S(9), Height - 1), S(14)))
                using (var hb = new SolidBrush(Theme.Mix(Theme.BG, Theme.TEXT, 0.05)))
                    g.FillPath(hb, hp);
            int box = S(56);
            var r = new Rectangle((Width - box) / 2, S(8), box, box);
            using (var path = Theme.RoundedRect(r, S(16)))
            using (var b = new SolidBrush(Theme.SURFACE))
                g.FillPath(b, path);
            int ic = S(24);
            DepupIcons.Draw(g, _icon, new RectangleF(r.X + (box - ic) / 2f, r.Y + (box - ic) / 2f, ic, ic), _fg);
            TextRenderer.DrawText(g, Text, Theme.FontLabel(9.75f, FontStyle.Bold), new Rectangle(0, r.Bottom + S(6), Width, S(20)),
                Theme.TEXT, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Sampul kartu korsel (Android cover): wadah lembut SOFT, ikon kategori 28 dp di tengah, label pil di kiri atas.</summary>
    public sealed class CoverArt : PaintedControl
    {
        private readonly string _icon;
        private readonly Color _fg;
        private readonly string? _badge;
        public CoverArt(string icon, Color fg, string? badge) { _icon = icon; _fg = fg; _badge = badge; TabStop = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(14)))
            using (var b = new SolidBrush(Theme.SOFT))
                g.FillPath(b, path);
            int ic = S(28);
            DepupIcons.Draw(g, _icon, new RectangleF((Width - ic) / 2f, (Height - ic) / 2f, ic, ic), _fg);
            if (!string.IsNullOrEmpty(_badge))
            {
                var f = Theme.FontLabel(8.5f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(_badge, f);
                var pr = new Rectangle(S(10), S(10), sz.Width + S(10), sz.Height + S(4));
                using (var pp = Theme.RoundedRect(pr, pr.Height / 2))
                using (var pb = new SolidBrush(Theme.SURFACE))
                    g.FillPath(pb, pp);
                TextRenderer.DrawText(g, _badge, f, pr, _fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
