using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Palet Depup v4.1 — sama dengan token Android (Ui.kt, object C, tema terang):
    /// minimalis netral-hangat. Halaman abu batu muda, kartu putih rata tanpa gradien, satu aksen biru royal,
    /// warna kategori dipakai kecil-kecil (ikon di wadah lembut).
    /// </summary>
    public static class Theme
    {
        /// <summary>Faktor skala DPI layar (1.0 = 96 DPI, 1.5 = 150%, dst).</summary>
        public static float Scale { get; private set; } = 1f;

        public static void SetDpi(int dpi) => Scale = Math.Max(0.75f, dpi / 96f);

        /// <summary>Ubah ukuran piksel desain (96 DPI) menjadi piksel nyata layar saat ini.</summary>
        public static int S(int px) => (int)Math.Round(px * Scale);

        private static Color H(string hex) => ColorTranslator.FromHtml(hex);

        // ---------- Permukaan ----------
        public static readonly Color BG = H("#F1F0EC");          // latar halaman
        public static readonly Color SURFACE = H("#FFFFFF");     // kartu, navigasi
        public static readonly Color SURFACE2 = H("#E8E6E1");    // isian, chip, trek
        public static readonly Color SURFACE3 = H("#DCD9D3");    // ditekan / terpilih netral
        public static readonly Color SOFT = H("#F3F2EE");        // wadah ikon netral di dalam kartu putih
        public static readonly Color LINE = H("#E4E2DD");        // pemisah tipis
        public static readonly Color LINE_STRONG = H("#84817B");

        // ---------- Aksen biru royal ----------
        public static readonly Color ACCENT = H("#1652F6");
        public static readonly Color ACCENT_DARK = H("#0F43D4");
        public static readonly Color BRAND = H("#0037FF");       // ubin logo
        public static readonly Color BLUE_BG = H("#E8EEFF");     // wadah lembut biru
        public static readonly Color BLUE_LINE = H("#C9D6FF");

        // ---------- Tinta & teks ----------
        public static readonly Color INK = H("#111111");
        public static readonly Color TEXT = H("#111111");
        public static readonly Color MUTED = H("#65625D");

        // ---------- Kategori (ikon di wadah lembut) ----------
        public static readonly Color RED = H("#B0472F");         // PDF
        public static readonly Color RED_BG = H("#F5E6E1");
        public static readonly Color TEAL = H("#35754C");        // Excel
        public static readonly Color TEAL_BG = H("#E3EEE6");
        public static readonly Color INDIGO = H("#72539E");      // gambar / lain-lain
        public static readonly Color INDIGO_BG = H("#ECE6F3");
        public static readonly Color SKY = H("#33609F");         // Word
        public static readonly Color SKY_BG = H("#E4EAF3");
        public static readonly Color AMBER = H("#8A4508");
        public static readonly Color AMBER_BG = H("#FDF0CC");
        public static readonly Color ERROR = H("#C0262D");
        public static readonly Color ERROR_BG = H("#FCE4E4");
        public static readonly Color SUCCESS = H("#15703A");
        public static readonly Color SUCCESS_BG = H("#DCF5E4");

        // Tema Reader Kertas & Eye-Care (ViewerActivity.kt)
        public static readonly Color NIGHT_BG = H("#0F0F0E");
        public static readonly Color SEPIA_BG = H("#FBF0D9");
        public static readonly Color EYECARE_GREEN_BG = H("#E2F0D9");

        // Font: Android memakai Funnel Display; padanan bawaan Windows 11 = Segoe UI Variable (fallback Segoe UI).
        private static readonly bool HasVariable = FontExists("Segoe UI Variable Display");
        private static bool FontExists(string name)
        {
            try { using var f = new Font(name, 10f); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        private static string HeadFamily => HasVariable ? "Segoe UI Variable Display Semib" : "Segoe UI Semibold";
        private static string LabelFamily => HasVariable ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
        private static string BodyFamily => HasVariable ? "Segoe UI Variable Text" : "Segoe UI";

        public static Font FontHead(float sizePt = 16f, FontStyle style = FontStyle.Bold)
            => new Font(HeadFamily, sizePt, style & ~FontStyle.Bold, GraphicsUnit.Point);

        /// <summary>Judul tampilan besar (sapaan beranda, judul halaman).</summary>
        public static Font FontDisplay(float sizePt = 28f)
            => new Font(HeadFamily, sizePt, FontStyle.Regular, GraphicsUnit.Point);

        public static Font FontLabel(float sizePt = 10.5f, FontStyle style = FontStyle.Regular)
            => new Font(LabelFamily, sizePt, style, GraphicsUnit.Point);

        public static Font FontBody(float sizePt = 10f, FontStyle style = FontStyle.Regular)
            => new Font(BodyFamily, sizePt, style, GraphicsUnit.Point);

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
                UseVisualStyleBackColor = false,
                CornerRadius = 999   // pil penuh (gaya Android)
            };
            if (width > 0) btn.Width = width;
            btn.FlatAppearance.BorderSize = 0;
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
                    BackColor = INK,
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
