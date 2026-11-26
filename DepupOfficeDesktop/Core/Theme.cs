using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Palet ruang kerja desktop, dengan sidebar dan toolbar bernuansa netral serta warna khusus jenis berkas.
    /// Permukaan kertas dokumen dan warna cetak memakai token PAPER_* / SHEET_*.
    /// </summary>
    public static class Theme
    {
        public const string AppVersion = "4.4.3";
        public const string AppName = "Depup Office Desktop";

        /// <summary>Faktor skala total = DPI layar × zoom tampilan (1.0 = 96 DPI, zoom 100%).</summary>
        public static float Scale { get; private set; } = 1f;
        private static float _dpiScale = 1f;

        public static void SetDpi(int dpi)
        {
            _dpiScale = Math.Max(0.75f, dpi / 96f);
            Scale = _dpiScale * Zoom;
        }

        // ================= Zoom tampilan (Ctrl + / Ctrl − / Ctrl 0, seperti VS Code) =================
        public static readonly float[] ZoomSteps = { 0.7f, 0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.4f, 1.6f };
        public const float DefaultZoom = 0.9f;

        /// <summary>Zoom antarmuka (bukan isi dokumen). Memengaruhi ukuran komponen & huruf.</summary>
        public static float Zoom { get; private set; } = DefaultZoom;

        public static void SetZoom(float z)
        {
            Zoom = Math.Clamp(z, ZoomSteps[0], ZoomSteps[^1]);
            Scale = _dpiScale * Zoom;
            try { AppStorage.Config.UiZoom = Zoom; AppStorage.SaveConfig(); } catch { }
        }

        public static float NextZoom(int dir)
        {
            int i = Array.FindIndex(ZoomSteps, v => Math.Abs(v - Zoom) < 0.01f);
            if (i < 0) i = Array.FindIndex(ZoomSteps, v => v > Zoom) - (dir > 0 ? 1 : 0);
            return ZoomSteps[Math.Clamp(i + dir, 0, ZoomSteps.Length - 1)];
        }

        /// <summary>Gunakan warna kaca di rangka dan Mica pada bilah judul jika didukung Windows.</summary>
        public static bool GlassOn => AppStorage.Config.GlassEffect;

        public static void SetGlass(bool on)
        {
            AppStorage.Config.GlassEffect = on;
            AppStorage.SaveConfig();
            Changed?.Invoke();
        }

        public static bool MotionEnabled =>
            !SystemInformation.HighContrast && SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out bool enabled, 0) && enabled;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

        /// <summary>Ubah ukuran piksel desain (96 DPI) menjadi piksel nyata layar saat ini.</summary>
        public static int S(int px) => (int)Math.Round(px * Scale);

        private static Color H(string hex) => ColorTranslator.FromHtml(hex);

        // ================= Mode tema (Ikuti Windows / Terang / Gelap) =================
        public const int MODE_SYSTEM = 0, MODE_LIGHT = 1, MODE_DARK = 2;

        /// <summary>true = tema gelap aktif.</summary>
        public static bool Dark { get; private set; }

        /// <summary>Dipicu setelah tema berganti (jendela utama membangun ulang tampilannya).</summary>
        public static event Action? Changed;

        public static int Mode => Math.Clamp(AppStorage.Config.ThemeMode, 0, 2);

        /// <summary>Hitung ulang tema dari pengaturan (panggil saat aplikasi mulai).</summary>
        public static void Init()
        {
            float z = AppStorage.Config.UiZoom;
            Zoom = z >= 0.5f && z <= 2f ? z : DefaultZoom;
            Scale = _dpiScale * Zoom;
            Dark = ResolveDark(Mode);
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (Mode != MODE_SYSTEM) return;
                    bool d = ResolveDark(MODE_SYSTEM);
                    if (d == Dark) return;
                    Dark = d;
                    Changed?.Invoke();
                };
            }
            catch { }
        }

        public static void SetMode(int mode)
        {
            int next = Math.Clamp(mode, 0, 2);
            if (next == Mode) return;
            AppStorage.Config.ThemeMode = next;
            AppStorage.SaveConfig();
            bool d = ResolveDark(Mode);
            Dark = d;
            Changed?.Invoke();
        }

        private static bool ResolveDark(int mode)
        {
            if (mode == MODE_LIGHT) return false;
            if (mode == MODE_DARK) return true;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        private static Color T(string light, string dark) => H(Dark ? dark : light);

        // ---------- Permukaan (Android: BG, SURFACE, SURFACE2, SURFACE3, SOFT, LINE) ----------
        public static Color BG => T("#F1F0EC", "#0F0F0E");          // Android C.BG
        public static Color SIDEBAR => T("#FFFFFF", "#1B1B1A");     // Android: navigasi = SURFACE
        public static Color TOOLBAR => T("#F1F0EC", "#0F0F0E");     // Android: bilah atas di atas halaman
        public static Color SURFACE => T("#FFFFFF", "#1B1B1A");
        public static Color SURFACE_HI => SURFACE;
        public static Color SURFACE2 => T("#E8E6E1", "#272725");
        public static Color SURFACE3 => T("#DCD9D3", "#34342F");
        public static Color SOFT => T("#F3F2EE", "#242422");
        public static Color FIELD => SURFACE;
        public static Color LINE => T("#E4E2DD", "#2C2C29");
        public static Color LINE_STRONG => T("#84817B", "#6F6D68");
        /// <summary>Kanvas di sekeliling halaman dokumen (Android: TRACK/CANVAS = SURFACE3).</summary>
        public static Color CANVAS => T("#DCD9D3", "#34342F");

        // ---------- Aksen biru royal (Android BLUE_ACCENT / PRIMARY) ----------
        public static Color ACCENT => T("#1652F6", "#1D56FF");      // Android BLUE_ACCENT
        public static Color ACCENT_DARK => T("#0F43D6", "#1647E0");
        public static Color ACCENT_LIGHT => T("#1652F6", "#1D56FF"); // Android ON_PRIMARY_C
        public static Color BRAND => H("#0037FF");                     // ubin logo (sama dengan ikon aplikasi)
        public static Color VIOLET => T("#72539E", "#C2A9E6");
        public static Color BLUE_BG => T("#E8EEFF", "#19254D");      // Android PRIMARY_C
        public static Color BLUE_LINE => T("#E8EEFF", "#19254D");
        public static Color CYAN => ACCENT;

        // ---------- Tinta: aksi utama & panel kontras (Android INK / ON_INK) ----------
        public static Color INK => T("#111111", "#F2F1EE");
        public static Color ON_INK => T("#FFFFFF", "#111111");

        // ---------- Teks ----------
        public static Color TEXT => T("#111111", "#F2F1EE");
        public static Color MUTED => T("#65625D", "#9C9A95");
        public static Color FAINT => LINE_STRONG;

        // ---------- Jenis berkas & status (Android PDF_FG/BG, DOC, XLS, IMG, SUCCESS, WARN, ERROR) ----------
        public static Color RED => T("#B0472F", "#EE9A84");          // PDF
        public static Color RED_BG => T("#F5E6E1", "#33221E");
        public static Color SKY => T("#33609F", "#97B4E6");          // Word
        public static Color SKY_BG => T("#E4EAF3", "#1E2631");
        public static Color TEAL => T("#35754C", "#8CC9A0");         // Excel
        public static Color TEAL_BG => T("#E3EEE6", "#1C2A21");
        public static Color INDIGO => T("#72539E", "#C2A9E6");       // Gambar / lain-lain
        public static Color INDIGO_BG => T("#ECE6F3", "#292230");
        public static Color AMBER => T("#8A4508", "#F6C35A");        // peringatan / brankas
        public static Color AMBER_BG => T("#FDF0CC", "#372A0C");
        public static Color AMBER_FILL => T("#F5A524", "#F5B53D");
        public static Color ERROR => T("#C0262D", "#FF8A8A");
        public static Color ERROR_BG => T("#FCE4E4", "#3A1618");
        public static Color SUCCESS => T("#15703A", "#5BD88A");
        public static Color SUCCESS_BG => T("#DCF5E4", "#15301F");

        // ---------- Kertas: isi dokumen, cetak, berkas hasil (TIDAK ikut tema) ----------
        public static readonly Color PAPER = Color.White;
        public static readonly Color PAPER_INK = H("#111111");
        public static readonly Color PAPER_MUTED = H("#65625D");
        public static readonly Color PAPER_LINE = H("#E4E2DD");
        public static readonly Color PAPER_ACCENT = H("#1652F6");
        public static readonly Color PAPER_SKY = H("#33609F");
        public static readonly Color PAPER_SOFT = H("#E8E6E1");
        /// <summary>Sel judul & total di lembar Excel (ikut tersimpan ke berkas, jadi tetap terang).</summary>
        public static readonly Color SHEET_HEAD = H("#E8E6E1");
        public static readonly Color SHEET_TOTAL = H("#E4EAF3");

        // Tema Reader Kertas & Eye-Care (ViewerActivity.kt)
        public static readonly Color NIGHT_BG = H("#0F0F0E");
        public static readonly Color SEPIA_BG = H("#FBF0D9");
        public static readonly Color EYECARE_GREEN_BG = H("#E2F0D9");

        // ================= Warna pembantu =================

        /// <summary>Luminans relatif (0 = hitam, 1 = putih).</summary>
        public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

        /// <summary>Warna teks yang terbaca di atas <paramref name="bg"/> (putih atau navy gelap).</summary>
        public static Color ReadableOn(Color bg) => Luma(bg) > 0.62 ? H("#111111") : Color.White;

        /// <summary>Campur dua warna; t = 0 → a, t = 1 → b.</summary>
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>Wadah lembut bernuansa warna di atas permukaan gelap (mis. tombol sekunder berwarna).</summary>
        public static Color Tint(Color c) =>
            c.ToArgb() == ACCENT.ToArgb() ? BLUE_BG : Mix(SURFACE, c, Dark ? 0.2 : 0.12);

        /// <summary>Versi warna yang cukup terang untuk dipakai sebagai teks di atas permukaan gelap.</summary>
        public static Color OnDark(Color c) => Dark && Luma(c) < 0.45 ? Mix(c, Color.White, 0.45) : c;

        // ================= Huruf =================

        // Huruf: sama dengan Android (Funnel Display, dibawa di folder Fonts). Bila gagal dimuat → Segoe UI Variable / Segoe UI.
        private static readonly bool HasVariable = FontExists("Segoe UI Variable Text");
        private static bool FontExists(string name)
        {
            try { using var f = new Font(name, 10f); return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // Cache font: setiap kombinasi (keluarga, ukuran, gaya) dibuat sekali lalu dipakai bersama.
        // Sebelumnya tiap label / tiap Paint membuat Font baru → ratusan handle GDI & lambat saat zoom / ganti tab.
        // Font dari cache TIDAK boleh di-Dispose oleh pemanggil.
        private static readonly Dictionary<(string, int, FontStyle), Font> FontCache = new();
        private static readonly object FontGate = new();

        private static Font Make(string family, float sizePt, FontStyle style)
        {
            sizePt = Math.Max(6f, sizePt * Zoom);   // huruf ikut zoom tampilan
            var key = (family, (int)Math.Round(sizePt * 100), style);
            lock (FontGate)
            {
                if (FontCache.TryGetValue(key, out var cached)) return cached;
                var f = new Font(family, sizePt, style, GraphicsUnit.Point);
                FontCache[key] = f;
                return f;
            }
        }

        private static string? _monoName;

        private static string BodyFamily => HasVariable ? "Segoe UI Variable Text" : "Segoe UI";

        public static Font FontHead(float sizePt = 16f, FontStyle style = FontStyle.Bold)
            => Make(BodyFamily, sizePt, style);

        /// <summary>Judul tampilan besar (sapaan beranda, judul halaman).</summary>
        public static Font FontDisplay(float sizePt = 28f)
            => Make(BodyFamily, sizePt, FontStyle.Bold);

        public static Font FontLabel(float sizePt = 10.5f, FontStyle style = FontStyle.Regular)
            => Make(BodyFamily, sizePt, style);

        public static Font FontBody(float sizePt = 10f, FontStyle style = FontStyle.Regular)
            => Make(BodyFamily, sizePt, style);

        public static Font FontMono(float sizePt = 10f)
        {
            _monoName ??= FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            return Make(_monoName, sizePt, FontStyle.Regular);
        }

        // ================= Bentuk =================

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }
            const float k = 1.52866f;
            float l = bounds.Left, t = bounds.Top, r = bounds.Right, b = bounds.Bottom;
            if (radius * k * 2f > Math.Min(bounds.Width, bounds.Height))
            {
                // bentuk pil / radius terlalu besar: busur biasa (sama dengan Android addContinuousRect)
                int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
                path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
                path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
                path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
            // Sudut KONTINU ala iOS/Android DepUp (Ui.kt addContinuousRect): lengkung mulai lebih awal dan menyatu halus
            float rad = radius;
            PointF last = default;
            bool first = true;
            void Corner(float ox, float oy, float ix, float iy, float jx, float jy)
            {
                PointF P(float a, float c) => new(ox - a * rad * ix + c * rad * jx, oy - a * rad * iy + c * rad * jy);
                var start = P(k, 0);
                if (!first) path.AddLine(last, start);
                first = false;
                path.AddBezier(start, P(1.08849f, 0), P(0.86840f, 0), P(0.66993f, 0.06549f));
                path.AddLine(P(0.66993f, 0.06549f), P(0.63149f, 0.07491f));
                path.AddBezier(P(0.63149f, 0.07491f), P(0.37282f, 0.16906f), P(0.16906f, 0.37282f), P(0.07491f, 0.63149f));
                path.AddLine(P(0.07491f, 0.63149f), P(0.06549f, 0.66993f));
                path.AddBezier(P(0.06549f, 0.66993f), P(0, 0.86840f), P(0, 1.08849f), last = P(0, k));
            }
            Corner(r, t, 1, 0, 0, 1);     // kanan atas
            Corner(r, b, 0, 1, -1, 0);    // kanan bawah
            Corner(l, b, -1, 0, 0, -1);   // kiri bawah
            Corner(l, t, 0, -1, 1, 0);    // kiri atas
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Tombol membulat. <paramref name="filled"/> = tombol utama (isi warna penuh, teks kontras otomatis);
        /// selain itu tombol sekunder bernuansa warna (gaya tombol "tonal" di WPS / Office).
        /// </summary>
        public static Button CreateButton(string text, bool filled = true, Color? accent = null, int height = 40, int width = 0)
        {
            Color col = accent ?? ACCENT;
            bool neutral = col.ToArgb() == TEXT.ToArgb() || col.ToArgb() == MUTED.ToArgb() || Luma(col) > 0.85 && !filled;
            Color back, fore, border;
            if (filled)
            {
                back = col;
                fore = ReadableOn(col);
                border = col;
            }
            else if (neutral)
            {
                back = SURFACE2;          // Android: tombol tonal netral
                fore = TEXT;
                border = SURFACE2;
            }
            else
            {
                back = Tint(col);         // Android: tombol tonal berwarna (wadah lembut + teks warna)
                fore = OnDark(col);
                border = back;
            }

            var btn = new DepupOfficeDesktop.Controls.PillButton
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Font = FontLabel(10f),
                BackColor = back,
                ForeColor = fore,
                Height = height,
                Cursor = Cursors.Hand,
                AutoSize = width == 0,
                Padding = new Padding(12, 4, 12, 4),
                UseVisualStyleBackColor = false,
                CornerRadius = 8
            };
            if (width > 0) btn.Width = width;
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.BorderColor = border;
            return btn;
        }

        /// <summary>
        /// Dialog lama yang ukurannya ditulis dalam piksel 96 DPI: skalakan bentuk & posisi kontrolnya
        /// mengikuti DPI layar (huruf sudah berskala sendiri) sebelum ditampilkan.
        /// </summary>
        public static void ScaleRawDialog(Form dlg)
        {
            if (Math.Abs(Scale - 1f) < 0.01f) return;
            dlg.AutoScaleMode = AutoScaleMode.None;
            dlg.Scale(new SizeF(Scale, Scale));
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
                    MaximumSize = new Size(Math.Max(S(240), f.ClientSize.Width - S(80)), 0),
                    BackColor = INK,
                    ForeColor = ON_INK,
                    Font = FontLabel(10f),
                    UseMnemonic = false,
                    Padding = new Padding(S(14), S(9), S(14), S(9))
                };
                f.Controls.Add(banner);
                banner.BringToFront();
                var pref = banner.PreferredSize;
                banner.Location = new Point(Math.Max(S(20), (f.ClientSize.Width - pref.Width) / 2), Math.Max(S(10), f.ClientSize.Height - pref.Height - S(56)));
                var t = new System.Windows.Forms.Timer { Interval = 2800 };
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

        // =====================================================================
        //  Tema otomatis untuk SEMUA jendela (termasuk dialog yang dibuat cepat dengan `new Form { ... }`):
        //  bilah judul gelap (DWM), menu konteks gelap, dan isian / tombol / label bawaan yang disesuaikan
        //  agar terbaca di atas permukaan gelap. Kertas dokumen (kontrol berlatar putih) tidak disentuh.
        // =====================================================================

        private static IntPtr _cbtHook = IntPtr.Zero;
        private static HookProc? _cbtProc;
        private static readonly HashSet<IntPtr> _themedForms = new();

        /// <summary>Pasang tema otomatis. Panggil sekali di Program.Main sebelum jendela pertama dibuat.</summary>
        public static void InstallAutoTheme()
        {
            try { ToolStripManager.Renderer = new DarkToolStripRenderer(); } catch { }
            if (_cbtHook != IntPtr.Zero) return;
            try
            {
                _cbtProc = CbtCallback;
                _cbtHook = SetWindowsHookEx(WH_CBT, _cbtProc, IntPtr.Zero, GetCurrentThreadId());
                Application.ApplicationExit += (s, e) =>
                {
                    if (_cbtHook != IntPtr.Zero) UnhookWindowsHookEx(_cbtHook);
                    _cbtHook = IntPtr.Zero;
                };
            }
            catch
            {
                _cbtHook = IntPtr.Zero;
            }
        }

        private static IntPtr CbtCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HCBT_ACTIVATE)
            {
                try
                {
                    if (Control.FromHandle(wParam) is Form form && _themedForms.Add(form.Handle))
                    {
                        IntPtr h = form.Handle;
                        form.HandleDestroyed += (s, e) => _themedForms.Remove(h);
                        StyleForm(form);
                    }
                }
                catch
                {
                    // tema tidak boleh menggagalkan pembukaan jendela
                }
            }
            return CallNextHookEx(_cbtHook, nCode, wParam, lParam);
        }

        /// <summary>Terapkan tema gelap pada sebuah form beserta kontrol bawaannya (aman dipanggil berulang).</summary>
        public static void StyleForm(Form form)
        {
            ApplyWindowChrome(form);
            FitToScreen(form);
            if (IsDefaultBack(form.BackColor) || form.BackColor.ToArgb() == Color.White.ToArgb())
                form.BackColor = SURFACE;
            if (form.ForeColor == SystemColors.ControlText || Math.Abs(Luma(form.ForeColor) - Luma(form.BackColor)) < 0.35)
                form.ForeColor = TEXT;
            StyleTree(form);
        }

        /// <summary>Jendela yang lebih besar dari layar (laptop / skala 125–150%) diperkecil & ditengahkan agar tidak terpotong.</summary>
        public static void FitToScreen(Form form)
        {
            try
            {
                if (!form.TopLevel || form.WindowState != FormWindowState.Normal) return;
                var wa = Screen.FromControl(form).WorkingArea;
                if (form.Width <= wa.Width && form.Height <= wa.Height && wa.Contains(form.Bounds)) return;
                int w = Math.Min(form.Width, (int)(wa.Width * 0.96));
                int h = Math.Min(form.Height, (int)(wa.Height * 0.96));
                if (form.MinimumSize.Width > w || form.MinimumSize.Height > h)
                    form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, w), Math.Min(form.MinimumSize.Height, h));
                form.Size = new Size(w, h);
                form.Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            }
            catch { }
        }

        /// <summary>Bilah judul gelap (Windows 10 20H1+) dan warna keterangan navy (Windows 11).</summary>
        private static Icon? _appIcon;
        /// <summary>Ikon aplikasi = logo Android (ic_logo_tile), dari Assets/depup.ico yang tertanam.</summary>
        public static Icon? AppIcon
        {
            get
            {
                if (_appIcon != null) return _appIcon;
                try
                {
                    using var s = typeof(Theme).Assembly.GetManifestResourceStream("DepupOfficeDesktop.depup.ico");
                    if (s != null) _appIcon = new Icon(s);
                }
                catch { }
                return _appIcon;
            }
        }

        public static void ApplyWindowChrome(Form form)
        {
            if (form.ShowIcon && AppIcon is { } ic && form.Icon?.Handle != ic.Handle) form.Icon = ic;
            void Apply()
            {
                try
                {
                    int on = Dark ? 1 : 0;
                    if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                        DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
                    if (form is DepupOfficeDesktop.Forms.MainForm && GlassOn)
                    {
                        // Windows 11: bilah judul memakai bahan Mica (kaca buram sistem); warna keterangan dikembalikan ke bawaan
                        int def = unchecked((int)0xFFFFFFFF);
                        DwmSetWindowAttribute(form.Handle, 35, ref def, sizeof(int));
                        int mica = 2;                                                     // DWMSBT_MAINWINDOW
                        DwmSetWindowAttribute(form.Handle, 38, ref mica, sizeof(int));    // DWMWA_SYSTEMBACKDROP_TYPE
                    }
                    else
                    {
                        // seperti bilah status Android: bilah judul menyatu dengan latar halaman
                        int caption = ColorTranslator.ToWin32(form is DepupOfficeDesktop.Forms.MainForm || form.BackColor.ToArgb() == BG.ToArgb() ? BG : SURFACE);
                        DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR
                        int none = 1;                                                     // DWMSBT_NONE
                        if (form is DepupOfficeDesktop.Forms.MainForm) DwmSetWindowAttribute(form.Handle, 38, ref none, sizeof(int));
                    }
                    int border = ColorTranslator.ToWin32(LINE);
                    DwmSetWindowAttribute(form.Handle, 34, ref border, sizeof(int));    // DWMWA_BORDER_COLOR
                    int text = ColorTranslator.ToWin32(TEXT);
                    DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(int));      // DWMWA_TEXT_COLOR
                }
                catch { }
            }
            if (form.IsHandleCreated) Apply();
            else form.HandleCreated += (s, e) => Apply();
        }

        private static bool IsDefaultBack(Color c) => c == SystemColors.Control || c == SystemColors.ButtonFace;

        internal static Color BackgroundBehind(Control control) =>
            control.Parent is { } parent ? EffectiveBack(parent) : BG;

        private static Color EffectiveBack(Control c)
        {
            for (Control? x = c; x != null; x = x.Parent)
                if (x.BackColor.A == 255) return x.BackColor;
            return SURFACE;
        }

        /// <summary>Kontrol berlatar putih eksplisit = kertas dokumen / pratinjau: biarkan apa adanya.</summary>
        private static bool IsPaper(Control c) =>
            c is not Form && !c.BackColor.IsSystemColor && c.BackColor.ToArgb() == Color.White.ToArgb()
            || c.Tag is string tag && tag == "paper";

        private static void StyleTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                try { StyleControl(c); }
                catch { /* satu kontrol gagal diberi tema tidak boleh menghentikan yang lain */ }
            }
            root.ControlAdded -= OnControlAdded;
            root.ControlAdded += OnControlAdded;
        }

        private static void OnControlAdded(object? sender, ControlEventArgs e)
        {
            try { if (e.Control != null) StyleControl(e.Control); } catch { }
        }

        private static void StyleControl(Control c)
        {
            if (IsPaper(c)) return;

            switch (c)
            {
                case DepupOfficeDesktop.Controls.PillButton:
                    return; // sudah bergaya sendiri

                case RichTextBox:
                    if (c.BackColor == SystemColors.Window) { c.BackColor = FIELD; c.ForeColor = TEXT; }
                    DarkScrollbars(c);
                    return;

                case TextBoxBase tb:
                    if (c.BackColor == SystemColors.Window || c.BackColor == SystemColors.Control && tb.ReadOnly)
                    {
                        c.BackColor = FIELD;
                        c.ForeColor = TEXT;
                        if (tb.BorderStyle == BorderStyle.Fixed3D) tb.BorderStyle = BorderStyle.FixedSingle;
                    }
                    if (tb.Multiline) DarkScrollbars(c);
                    return;

                case ComboBox cb:
                    if (c.BackColor == SystemColors.Window)
                    {
                        c.BackColor = FIELD;
                        c.ForeColor = TEXT;
                        cb.FlatStyle = FlatStyle.Flat;
                    }
                    return;

                case ListBox:
                case ListView:
                case TreeView:
                    if (c.BackColor == SystemColors.Window) { c.BackColor = FIELD; c.ForeColor = TEXT; }
                    if (c is ListBox lb && lb.BorderStyle == BorderStyle.Fixed3D) lb.BorderStyle = BorderStyle.FixedSingle;
                    DarkScrollbars(c);
                    return;

                case UpDownBase ud:
                    if (c.BackColor == SystemColors.Window) { c.BackColor = FIELD; c.ForeColor = TEXT; }
                    if (ud.BorderStyle == BorderStyle.Fixed3D) ud.BorderStyle = BorderStyle.FixedSingle;
                    return;

                case DataGridView g:
                    StyleGrid(g);
                    return;

                case Button b:
                    if (IsDefaultBack(b.BackColor))
                    {
                        b.UseVisualStyleBackColor = false;
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = SURFACE2;
                        b.ForeColor = TEXT;
                        b.FlatAppearance.BorderColor = LINE_STRONG;
                        b.FlatAppearance.MouseOverBackColor = SURFACE3;
                        b.FlatAppearance.MouseDownBackColor = LINE_STRONG;
                        b.Cursor = Cursors.Hand;
                    }
                    else if (Math.Abs(Luma(b.ForeColor) - Luma(b.BackColor)) < 0.3)
                        b.ForeColor = ReadableOn(b.BackColor);
                    return;

                case Label:
                case CheckBox:
                case RadioButton:
                case GroupBox:
                    FixTextContrast(c);
                    if (c is GroupBox) StyleTree(c);
                    return;

                case PictureBox:
                case WebBrowser:
                    return;
            }

            if (c is ToolStrip) return;           // digambar oleh DarkToolStripRenderer
            if (c is ScrollableControl sc && sc.AutoScroll) DarkScrollbars(c);
            if (c.GetType().Namespace?.StartsWith("Microsoft.Web") == true) return;
            StyleTree(c);
        }

        private static void FixTextContrast(Control c)
        {
            // Hanya warna teks netral (abu/hitam/putih) yang disesuaikan; warna kategori/status dibiarkan.
            Color fc = c.ForeColor;
            bool neutral = Math.Max(fc.R, Math.Max(fc.G, fc.B)) - Math.Min(fc.R, Math.Min(fc.G, fc.B)) < 40;
            if (!neutral) return;
            Color bg = EffectiveBack(c);
            if (Luma(bg) < 0.3 && Luma(fc) < 0.4 || Luma(bg) > 0.6 && Luma(fc) > 0.75)
                c.ForeColor = TEXT;
        }

        private static void StyleGrid(DataGridView g)
        {
            // Hanya tabel dengan warna bawaan; lembar kerja / tabel di atas kertas (putih, sepia, dll.) dibiarkan.
            if (g.BackgroundColor != SystemColors.AppWorkspace) return;
            g.BackgroundColor = FIELD;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = LINE;
            g.EnableHeadersVisualStyles = false;
            if (g.DefaultCellStyle.BackColor == SystemColors.Window || g.DefaultCellStyle.BackColor.IsEmpty)
            {
                g.DefaultCellStyle.BackColor = FIELD;
                g.DefaultCellStyle.ForeColor = TEXT;
            }
            g.DefaultCellStyle.SelectionBackColor = BLUE_LINE;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.BackColor = SURFACE2;
            g.ColumnHeadersDefaultCellStyle.ForeColor = TEXT;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = SURFACE2;
            g.RowHeadersDefaultCellStyle.BackColor = SURFACE2;
            g.RowHeadersDefaultCellStyle.ForeColor = MUTED;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        }

        /// <summary>Scrollbar gelap gaya Windows Explorer mode gelap (diabaikan bila tidak didukung OS).</summary>
        public static void DarkScrollbars(Control c)
        {
            void Apply() { try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }
            if (c.IsHandleCreated) Apply();
            else c.HandleCreated += (s, e) => Apply();
        }

        /// <summary>Menu konteks / toolstrip mengikuti tema (terang / gelap).</summary>
        private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer() : base(new DarkColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (e.Item.ForeColor == SystemColors.ControlText || Luma(e.Item.ForeColor) < 0.4)
                    e.TextColor = e.Item.Enabled ? TEXT : FAINT;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = MUTED;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                var r = new Rectangle(e.ImageRectangle.X - 2, e.ImageRectangle.Y - 2, e.ImageRectangle.Width + 4, e.ImageRectangle.Height + 4);
                using (var b = new SolidBrush(BLUE_BG)) e.Graphics.FillRectangle(b, r);
                using var p = new Pen(ACCENT_LIGHT, 2f);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                e.Graphics.DrawLines(p, new[] { new Point(cx - 5, cy), new Point(cx - 1, cy + 4), new Point(cx + 6, cy - 4) });
            }
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => SURFACE;
            public override Color ImageMarginGradientBegin => SURFACE;
            public override Color ImageMarginGradientMiddle => SURFACE;
            public override Color ImageMarginGradientEnd => SURFACE;
            public override Color MenuBorder => LINE_STRONG;
            public override Color MenuItemBorder => BLUE_LINE;
            public override Color MenuItemSelected => SURFACE3;
            public override Color MenuItemSelectedGradientBegin => SURFACE3;
            public override Color MenuItemSelectedGradientEnd => SURFACE3;
            public override Color MenuItemPressedGradientBegin => SURFACE2;
            public override Color MenuItemPressedGradientMiddle => SURFACE2;
            public override Color MenuItemPressedGradientEnd => SURFACE2;
            public override Color MenuStripGradientBegin => SURFACE;
            public override Color MenuStripGradientEnd => SURFACE;
            public override Color ToolStripBorder => LINE;
            public override Color ToolStripGradientBegin => SURFACE;
            public override Color ToolStripGradientMiddle => SURFACE;
            public override Color ToolStripGradientEnd => SURFACE;
            public override Color ToolStripContentPanelGradientBegin => BG;
            public override Color ToolStripContentPanelGradientEnd => BG;
            public override Color SeparatorDark => LINE;
            public override Color SeparatorLight => LINE;
            public override Color ButtonSelectedHighlight => SURFACE3;
            public override Color ButtonSelectedGradientBegin => SURFACE3;
            public override Color ButtonSelectedGradientMiddle => SURFACE3;
            public override Color ButtonSelectedGradientEnd => SURFACE3;
            public override Color ButtonSelectedBorder => BLUE_LINE;
            public override Color ButtonPressedGradientBegin => SURFACE2;
            public override Color ButtonPressedGradientMiddle => SURFACE2;
            public override Color ButtonPressedGradientEnd => SURFACE2;
            public override Color CheckBackground => BLUE_BG;
            public override Color CheckSelectedBackground => BLUE_BG;
            public override Color StatusStripGradientBegin => SIDEBAR;
            public override Color StatusStripGradientEnd => SIDEBAR;
        }

        // ================= Win32 =================

        private const int WH_CBT = 5;
        private const int HCBT_ACTIVATE = 5;
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
    }
}
