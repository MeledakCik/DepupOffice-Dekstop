using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Palet "Depup PDF System" (desain Stitch dari Ui.kt):
    /// Kanvas terang, aksen biru PDF, kategori berkode warna.
    /// </summary>
    public static class Theme
    {
        /// <summary>Faktor skala DPI layar (1.0 = 96 DPI, 1.5 = 150%, dst).</summary>
        public static float Scale { get; private set; } = 1f;

        public static void SetDpi(int dpi) => Scale = Math.Max(0.75f, dpi / 96f);

        /// <summary>Ubah ukuran piksel desain (96 DPI) menjadi piksel nyata layar saat ini.</summary>
        public static int S(int px) => (int)Math.Round(px * Scale);

        public static readonly Color BG = ColorTranslator.FromHtml("#F8FAFC");
        public static readonly Color SURFACE = ColorTranslator.FromHtml("#FFFFFF");
        public static readonly Color SURFACE2 = ColorTranslator.FromHtml("#F1F5F9");
        public static readonly Color ACCENT = ColorTranslator.FromHtml("#1E60D5");
        public static readonly Color ACCENT_DARK = ColorTranslator.FromHtml("#1A52B8");
        public static readonly Color TEXT = ColorTranslator.FromHtml("#0F172A");
        public static readonly Color MUTED = ColorTranslator.FromHtml("#64748B");
        public static readonly Color LINE = ColorTranslator.FromHtml("#E2E8F0");

        public static readonly Color BLUE_BG = ColorTranslator.FromHtml("#EFF6FF");
        public static readonly Color BLUE_LINE = ColorTranslator.FromHtml("#BFDBFE");
        public static readonly Color INDIGO = ColorTranslator.FromHtml("#4F46E5");
        public static readonly Color INDIGO_BG = ColorTranslator.FromHtml("#EEF2FF");
        public static readonly Color SKY = ColorTranslator.FromHtml("#0284C7");
        public static readonly Color SKY_BG = ColorTranslator.FromHtml("#E0F2FE");
        public static readonly Color AMBER = ColorTranslator.FromHtml("#D97706");
        public static readonly Color AMBER_BG = ColorTranslator.FromHtml("#FEF3C7");
        public static readonly Color TEAL = ColorTranslator.FromHtml("#0D9488");
        public static readonly Color TEAL_BG = ColorTranslator.FromHtml("#ECFDF5");
        public static readonly Color RED = ColorTranslator.FromHtml("#DC2626");
        public static readonly Color RED_BG = ColorTranslator.FromHtml("#FEF2F2");

        // Tema Reader Kertas & Eye-Care (ViewerActivity.kt)
        public static readonly Color NIGHT_BG = ColorTranslator.FromHtml("#0F172A");
        public static readonly Color SEPIA_BG = ColorTranslator.FromHtml("#FBF0D9");
        public static readonly Color EYECARE_GREEN_BG = ColorTranslator.FromHtml("#E2F0D9");

        public static Font FontHead(float sizePt = 16f, FontStyle style = FontStyle.Bold)
            => new Font("Segoe UI", sizePt, style, GraphicsUnit.Point);

        public static Font FontLabel(float sizePt = 10.5f, FontStyle style = FontStyle.Regular)
            => new Font("Segoe UI Semibold", sizePt, style, GraphicsUnit.Point);

        public static Font FontBody(float sizePt = 10f, FontStyle style = FontStyle.Regular)
            => new Font("Segoe UI", sizePt, style, GraphicsUnit.Point);

        public static Font FontMono(float sizePt = 10f)
            => new Font("Consolas", sizePt, FontStyle.Regular, GraphicsUnit.Point);

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            d = Math.Min(d, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Button CreateButton(string text, bool filled = true, Color? accent = null, int height = 40, int width = 0)
        {
            Color col = accent ?? ACCENT;
            var btn = new DepupOfficeDesktop.Controls.PillButton
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = FontLabel(10f),
                BackColor = filled ? col : BLUE_BG,
                ForeColor = filled ? Color.White : col,
                Height = height,
                Cursor = Cursors.Hand,
                AutoSize = width == 0,
                Padding = new Padding(12, 4, 12, 4),
                UseVisualStyleBackColor = false
            };
            if (width > 0) btn.Width = width;
            btn.FlatAppearance.BorderSize = filled ? 0 : 1;
            btn.FlatAppearance.BorderColor = filled ? col : BLUE_LINE;
            return btn;
        }

        public static void Toast(Control? owner, string message)
        {
            Form? f = owner?.FindForm();
            if (f != null)
            {
                var banner = new Label
                {
                    Text = "  " + message + "  ",
                    AutoSize = true,
                    BackColor = Color.FromArgb(235, 15, 23, 42),
                    ForeColor = Color.White,
                    Font = FontLabel(10f),
                    Padding = new Padding(S(14), S(8), S(14), S(8))
                };
                f.Controls.Add(banner);
                banner.BringToFront();
                banner.Location = new Point(Math.Max(S(20), (f.ClientSize.Width - banner.PreferredWidth) / 2), f.ClientSize.Height - S(70));
                var t = new System.Windows.Forms.Timer { Interval = 2600 };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    t.Dispose();
                    if (!banner.IsDisposed && f.Controls.Contains(banner))
                        f.Controls.Remove(banner);
                    banner.Dispose();
                };
                t.Start();
            }
            else
            {
                MessageBox.Show(message, "Depup Office", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
