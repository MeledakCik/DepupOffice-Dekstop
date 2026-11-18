using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Lapisan tampilan jendela utama — versi desktop dari layar Android (MainActivity.kt):
    /// Beranda · Dokumen · Buat · Alat · Saya. Token warna, komponen (kartu rata, tombol pil, ubin ikon, daftar
    /// berkelompok, segmen) dan tema terang/gelap sama dengan Android; tata letaknya memakai lebar layar PC
    /// (navigasi samping, grid 3–4 kolom, dua kolom berdampingan, status bar).
    /// Semua ukuran memakai Theme.S() (skala DPI); halaman dibangun ulang saat lebar area konten berubah.
    /// </summary>
    public partial class MainForm
    {
        private static int S(int px) => Theme.S(px);

        private SearchBox _searchBox = null!;
        private Panel _rightHost = null!;
        private FlowLayoutPanel? _recentListPanel;
        private int _recentRowWidth;
        private int _builtAvailWidth;
        private Action? _layoutTopBar;
        private System.Windows.Forms.Timer? _reflowTimer;
        private DragEventHandler _dragEnterHandler = null!;
        private DragEventHandler _dragDropHandler = null!;
        private bool _themeHooked;

        // Dokumen: 0 = Terkini, 1 = Ditandai, 2 = Hasil olahan; kategori hasil olahan
        private int _docsMode;
        private string _docsCat = "Semua";

        private const int TAB_HOME = 0, TAB_DOCS = 1, TAB_CREATE = 2, TAB_TOOLS = 3, TAB_ME = 4;

        // ================= RANGKA JENDELA =================

        private static Label L(string text, Font font, Color fg, int x, int y, int w = 0, int h = 0, bool ellipsis = false)
        {
            var lbl = new Label
            {
                Text = text,
                Font = font,
                ForeColor = fg,
                BackColor = Color.Transparent,
                UseMnemonic = false,
                Location = new Point(x, y)
            };
            if (w > 0)
            {
                lbl.AutoSize = false;
                lbl.Size = new Size(w, Math.Max(h, 1));
                lbl.AutoEllipsis = ellipsis;
            }
            else
            {
                lbl.AutoSize = true;
            }
            return lbl;
        }

        private void BuildShell()
        {
            BackColor = Theme.BG;
            if (!_themeHooked)
            {
                _themeHooked = true;
                // Tema berganti (Saya › Tampilan atau Windows berpindah terang/gelap): bangun ulang seluruh tampilan.
                Theme.Changed += () =>
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(() =>
                    {
                        Theme.ApplyWindowChrome(this);
                        RebuildShellForDpi();
                    }));
                };
            }

            int sideW = S(236);

            // ---------- Navigasi samping (padanan navigasi bawah Android) ----------
            var sidebar = new Panel { Dock = DockStyle.Left, Width = sideW, BackColor = Theme.BG };
            int innerW = sideW - S(32);

            var brand = new Panel { Dock = DockStyle.Top, Height = S(150), BackColor = Theme.BG };
            var logo = new LogoTile { Size = new Size(S(40), S(40)), Location = new Point(S(18), S(20)), Cursor = Cursors.Hand };
            logo.Click += (s, e) => SelectTab(TAB_HOME);
            brand.Controls.Add(logo);
            brand.Controls.Add(L("Depup Office", Theme.FontHead(13.5f), Theme.TEXT, S(68), S(19)));
            brand.Controls.Add(L("100% offline", Theme.FontBody(9f), Theme.MUTED, S(69), S(41)));
            // Tombol utama (padanan tombol Scan biru di tengah navigasi Android)
            var btnEditPdf = Theme.CreateButton("Edit PDF", true, Theme.ACCENT, S(46), innerW);
            btnEditPdf.Font = Theme.FontLabel(10.5f);
            btnEditPdf.Location = new Point(S(16), S(84));
            btnEditPdf.Click += (s, e) => StartPdfEditorFlow();
            brand.Controls.Add(btnEditPdf);

            var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG, Padding = new Padding(S(16), S(2), S(16), 0) };
            string[] navNames = { "Beranda", "Dokumen", "Buat", "Alat", "Saya" };
            NavItemIcon[] navIcons = { NavItemIcon.Home, NavItemIcon.Docs, NavItemIcon.Create, NavItemIcon.Tools, NavItemIcon.User };
            for (int i = navNames.Length - 1; i >= 0; i--)   // Dock=Top: yang ditambahkan terakhir berada paling atas
            {
                int idx = i;
                var item = new NavItem { Caption = navNames[i], Icon = navIcons[i], Dock = DockStyle.Top, Height = S(50) };
                item.Click += (s, e) => SelectTab(idx);
                _navItems[idx] = item;
                nav.Controls.Add(item);
            }

            // Bawah: kartu kecil penyimpanan (data nyata dari disk)
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(104), BackColor = Theme.BG, Padding = new Padding(S(16), S(4), S(16), S(16)) };
            var (dFree, dTotal) = AppStorage.GetDeviceStorage();
            var storeCard = new CardPanel { Dock = DockStyle.Fill, BackColor = Theme.SURFACE, CornerRadius = 18, Cursor = Cursors.Hand };
            storeCard.Controls.Add(L("Penyimpanan PC", Theme.FontLabel(9f), Theme.TEXT, S(14), S(12)));
            storeCard.Controls.Add(L($"{AppStorage.FmtGb(dFree)} tersisa dari {AppStorage.FmtGb(dTotal)}", Theme.FontBody(8.5f), Theme.MUTED, S(14), S(32), innerW - S(28), S(18), true));
            storeCard.Controls.Add(new StackedStorageBar
            {
                Location = new Point(S(14), S(58)),
                Size = new Size(innerW - S(28), S(6)),
                TrackColor = Theme.SURFACE2,
                Parts = new List<(long, Color)> { (Math.Max(1, dTotal - dFree), Theme.ACCENT), (Math.Max(1, dFree), Color.Transparent) }
            });
            void OpenMe(object? s, EventArgs e) => SelectTab(TAB_ME);
            storeCard.Click += OpenMe;
            foreach (Control c in storeCard.Controls) c.Click += OpenMe;
            bottom.Controls.Add(storeCard);

            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(bottom);
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.LINE });

            // ---------- Area kanan: bilah atas + konten ----------
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG };
            _rightHost = right;
            var topBar = new Panel { Dock = DockStyle.Top, Height = S(72), BackColor = Theme.BG };
            topBar.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.LINE);
                e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            _barTitleLbl = L(_tabNames[Math.Clamp(_currentTab, 0, _tabNames.Length - 1)], Theme.FontHead(17f), Theme.TEXT, 0, 0);
            _barTitleLbl.Visible = false;
            _searchBox = new SearchBox { Height = S(48) };
            _searchBox.Input.PlaceholderText = "Cari dokumen…";
            _searchBox.Input.Text = _searchQuery;
            _searchBox.Input.TextChanged += (s, e) =>
            {
                CloseEmbeddedViewer();
                _searchQuery = _searchBox.Input.Text.Trim();
                // Seperti Android: mengetik di kolom cari langsung menampilkan hasil di Dokumen.
                if (_currentTab != TAB_DOCS || _docsMode == 2)
                {
                    if (_docsMode == 2) _docsMode = 0;
                    SelectTab(TAB_DOCS);
                    _searchBox.Input.Focus();
                    _searchBox.Input.SelectionStart = _searchBox.Input.TextLength;
                    return;
                }
                if (_recentListPanel != null)
                    FillRecentFilesBox(_recentListPanel, 50, _searchQuery, _recentRowWidth);
            };

            var themeBtn = new CircleButton { Size = new Size(S(46), S(46)), Cursor = Cursors.Hand, Glyph = Theme.Dark ? CircleGlyph.Sun : CircleGlyph.Moon };
            themeBtn.AccessibleName = Theme.Dark ? "Ganti ke tema terang" : "Ganti ke tema gelap";
            themeBtn.Pressed += (s, e) => Theme.SetMode(Theme.Dark ? Theme.MODE_LIGHT : Theme.MODE_DARK);
            var avatar = new CircleButton { Size = new Size(S(46), S(46)), Cursor = Cursors.Hand, Glyph = CircleGlyph.Person };
            avatar.AccessibleName = "Saya — brankas, penyimpanan, tampilan";
            avatar.Pressed += (s, e) => SelectTab(TAB_ME);

            topBar.Controls.AddRange(new Control[] { _barTitleLbl, _searchBox, themeBtn, avatar });
            themeBtn.BringToFront();
            avatar.BringToFront();
            _layoutTopBar = () =>
            {
                int padX = S(32);
                int h = topBar.ClientSize.Height;
                int wAll = topBar.ClientSize.Width;
                avatar.Location = new Point(wAll - padX - avatar.Width, (h - avatar.Height) / 2);
                themeBtn.Location = new Point(avatar.Left - S(10) - themeBtn.Width, (h - themeBtn.Height) / 2);
                int sw = Math.Max(S(160), Math.Min(S(560), themeBtn.Left - S(24) - padX));
                _searchBox.SetBounds(padX, (h - S(48)) / 2, sw, S(48));
            };
            topBar.Resize += (s, e) => _layoutTopBar?.Invoke();
            topBar.HandleCreated += (s, e) => _layoutTopBar?.Invoke();
            _layoutTopBar();

            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(S(32), S(6), S(32), S(32)),
                BackColor = Theme.BG,
                AllowDrop = true
            };
            Theme.DarkScrollbars(_contentHost);
            _contentHost.DragEnter += _dragEnterHandler;
            _contentHost.DragDrop += _dragDropHandler;
            _contentHost.SizeChanged += (s, e) =>
            {
                if (_reflowTimer == null) return;
                _reflowTimer.Stop();
                _reflowTimer.Start();
            };

            right.Controls.Add(_contentHost);
            right.Controls.Add(topBar);

            // ---------- Status bar khas desktop (seperti MS Word / WPS) ----------
            var status = new Panel { Dock = DockStyle.Bottom, Height = S(28), BackColor = Theme.SURFACE };
            status.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.LINE);
                e.Graphics.DrawLine(pen, 0, 0, status.Width, 0);
            };
            Label StatusText(string text, Color color, DockStyle dock)
            {
                using var f = Theme.FontBody(8.5f);
                return new Label
                {
                    Text = text,
                    Dock = dock,
                    AutoSize = false,
                    Width = TextRenderer.MeasureText(text, f).Width + S(30),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = Theme.FontBody(8.5f),
                    ForeColor = color,
                    BackColor = Theme.SURFACE,
                    UseMnemonic = false,
                    AutoEllipsis = true,
                    Padding = new Padding(S(14), 0, S(8), 0)
                };
            }
            status.Controls.Add(StatusText("Hasil disimpan di " + AppStorage.OutDir, Theme.MUTED, DockStyle.Fill));
            status.Controls.Add(StatusText($"Depup Office {Theme.AppVersion}  ·  Tema {(Theme.Mode == Theme.MODE_SYSTEM ? "ikut Windows" : Theme.Dark ? "gelap" : "terang")}", Theme.MUTED, DockStyle.Right));
            status.Controls.Add(StatusText("●  100% offline — berkas tidak pernah diunggah", Theme.SUCCESS, DockStyle.Left));

            Controls.Add(right);
            Controls.Add(sidebar);
            Controls.Add(status);

            if (!_initialFocusHooked)
            {
                _initialFocusHooked = true;
                Shown += (s, e) => { if (_navItems.TryGetValue(_currentTab, out var n)) n.Focus(); };
            }
        }

        private bool _initialFocusHooked;

        /// <summary>Bangun ulang rangka & halaman (DPI monitor berubah atau tema berganti).</summary>
        private void RebuildShellForDpi()
        {
            CloseEmbeddedViewer();
            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            _navItems.Clear();
            _recentListPanel = null;
            BuildShell();
            ResumeLayout(true);
            SelectTab(_currentTab);
        }

        private void SelectTab(int idx)
        {
            CloseEmbeddedViewer();
            bool changed = idx != _currentTab;
            _currentTab = Math.Clamp(idx, 0, _tabNames.Length - 1);
            _barTitleLbl.Text = _tabNames[_currentTab];
            foreach (var (k, item) in _navItems)
                item.Active = k == _currentTab;
            _layoutTopBar?.Invoke();
            RenderCurrentTab();
            if (changed) _contentHost.AutoScrollPosition = Point.Empty;   // halaman baru mulai dari atas
        }

        private int AvailableWidth()
        {
            int sb = _contentHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            return Math.Max(1, _contentHost.ClientSize.Width + sb - _contentHost.Padding.Horizontal);
        }

        private int GetContentWidth()
        {
            // Sisakan ruang untuk scrollbar vertikal agar tidak muncul scrollbar horizontal.
            return Math.Max(S(320), Math.Min(S(1180), AvailableWidth() - S(18)));
        }

        private void RenderCurrentTab()
        {
            _contentHost.SuspendLayout();
            var old = _contentHost.Controls.Cast<Control>().ToList();
            _contentHost.Controls.Clear();
            // Dibuang setelah event saat ini selesai: RenderCurrentTab sering dipanggil dari klik tombol di halaman lama.
            if (old.Count > 0)
                BeginInvoke(new Action(() => { foreach (var c in old) c.Dispose(); }));
            _recentListPanel = null;

            var page = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 0, S(8)),
                BackColor = Theme.BG,
                AllowDrop = true
            };
            page.DragEnter += _dragEnterHandler;
            page.DragDrop += _dragDropHandler;

            switch (_currentTab)
            {
                case TAB_HOME: BuildHomeTab(page); break;
                case TAB_DOCS: BuildDocsTab(page); break;
                case TAB_CREATE: BuildCreateTab(page); break;
                case TAB_TOOLS: BuildToolsTab(page); break;
                default: BuildMeTab(page); break;
            }

            _contentHost.Controls.Add(page);
            _builtAvailWidth = AvailableWidth();
            _contentHost.ResumeLayout(true);
        }

        private static string Greeting()
        {
            int h = DateTime.Now.Hour;
            if (h < 11) return "Selamat pagi";
            if (h < 15) return "Selamat siang";
            if (h < 18) return "Selamat sore";
            return "Selamat malam";
        }

        // ================= KOMPONEN (padanan Ui.kt Android) =================

        /// <summary>Judul layar (Android heading: judul 24sp tebal + keterangan abu).</summary>
        private Control CreateHeading(int width, string title, string? subtitle = null)
        {
            var panel = new Panel { Width = width, Margin = new Padding(0, S(18), 0, S(14)) };
            var t = L(title, Theme.FontDisplay(26f), Theme.TEXT, -S(2), 0);
            panel.Controls.Add(t);
            int y = t.PreferredHeight;
            if (subtitle != null)
            {
                var font = Theme.FontBody(11f);
                int sh = TextRenderer.MeasureText(subtitle, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                panel.Controls.Add(L(subtitle, font, Theme.MUTED, 0, y + S(4), width, sh));
                y += S(4) + sh;
            }
            panel.Height = y + S(2);
            return panel;
        }

        /// <summary>Judul seksi (Android sectionTitle) + tautan/keterangan opsional di kanan.</summary>
        private Control CreateSectionHeader(string title, string badgeOrLink, int width, Action? onLinkClick = null)
        {
            var panel = new Panel { Width = width, Height = S(38), Margin = new Padding(0, S(20), 0, S(6)) };
            var titleLbl = L(title, Theme.FontHead(14.5f), Theme.TEXT, 0, S(4));
            panel.Controls.Add(titleLbl);
            if (onLinkClick != null)
            {
                var link = L(badgeOrLink, Theme.FontLabel(10f), Theme.ACCENT_LIGHT, 0, S(9));
                link.Cursor = Cursors.Hand;
                link.Click += (s, e) => onLinkClick();
                link.Left = width - link.GetPreferredSize(Size.Empty).Width;
                panel.Controls.Add(link);
            }
            else if (!string.IsNullOrEmpty(badgeOrLink))
            {
                var meta = L(badgeOrLink, Theme.FontBody(9.5f), Theme.MUTED, 0, S(9));
                meta.Left = width - meta.GetPreferredSize(Size.Empty).Width;
                panel.Controls.Add(meta);
            }
            return panel;
        }

        /// <summary>Ubin alat (Android tile): ikon di wadah lembut sewarna, judul tebal, keterangan abu, lencana opsional.</summary>
        private Control CreateFeatureTile(int width, string title, string sub, string tag, Color accent, Color accentBg, Action onClick)
        {
            int pad = S(20);
            int innerW = Math.Max(S(60), width - pad * 2);
            var subFont = Theme.FontBody(9.5f);
            int lineH = TextRenderer.MeasureText("Ag", subFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            // ukur sedikit lebih sempit dari label agar baris ke-2/3 tidak terpotong
            int subH = TextRenderer.MeasureText(sub, subFont, new Size(innerW - S(10), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            subH = Math.Min(Math.Max(subH, lineH), lineH * 3) + S(4);

            int chipSize = S(46);
            int titleY = pad + chipSize + S(14);
            int titleH = S(26);
            int subY = titleY + titleH + S(2);
            var card = new CardPanel { Width = width, Height = subY + subH + pad, Cursor = Cursors.Hand, CornerRadius = 22 };

            var chip = new IconChip { Size = new Size(chipSize, chipSize), Radius = S(14), Bg = accentBg, Fg = accent, Icon = ResolveCardIcon(title), Location = new Point(pad, pad) };
            var lblTitle = L(title, Theme.FontHead(12f), Theme.TEXT, pad, titleY, innerW, titleH, true);
            var lblSub = L(sub, subFont, Theme.MUTED, pad, subY, innerW, subH, true);
            card.Controls.AddRange(new Control[] { chip, lblTitle, lblSub });

            if (!string.IsNullOrWhiteSpace(tag))
            {
                var pill = new Pill { Text = tag, Fg = accent, Bg = accentBg, Font = Theme.FontLabel(8.5f) };
                int room = width - pad * 2 - chipSize - S(10);
                if (pill.Width <= room)
                {
                    pill.Location = new Point(width - pad - pill.Width, pad + (chipSize - pill.Height) / 2);
                    card.Controls.Add(pill);
                }
                else pill.Dispose();
            }

            void Fire(object? s, EventArgs e) => RunGuarded(onClick);
            card.Click += Fire;
            foreach (Control c in card.Controls) { c.Click += Fire; c.Cursor = Cursors.Hand; }
            card.AttachHover();
            return card;
        }

        /// <summary>Kartu berisi baris-baris (Android listCard): daftar yang dikelompokkan dengan garis pemisah.</summary>
        private CardPanel CreateListCard(int width, IList<ListRow> rows)
        {
            int pad = S(6);
            var card = new CardPanel { Width = width, CornerRadius = 22, Padding = new Padding(pad) };
            int y = pad;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                r.Divider = i < rows.Count - 1;
                r.SetBounds(pad, y, width - pad * 2, r.Height);
                card.Controls.Add(r);
                y += r.Height;
            }
            card.Height = y + pad;
            return card;
        }

        private ListRow Row(CardIcon icon, Color fg, Color bg, string title, string sub, Action onClick)
        {
            var r = new ListRow { Icon = icon, IconFg = fg, IconBg = bg, Title = title, Subtitle = sub };
            r.Click += (s, e) => RunGuarded(onClick);
            return r;
        }

        /// <summary>Kartu kosong (Android emptyState): ikon, judul, penjelasan.</summary>
        private Control CreateEmptyState(int width, CardIcon icon, string title, string text)
        {
            var card = new CardPanel { Width = width, Height = S(170), CornerRadius = 22, Margin = new Padding(0, 0, 0, S(12)) };
            card.Controls.Add(new IconChip { Size = new Size(S(52), S(52)), Radius = S(26), Bg = Theme.SURFACE2, Fg = Theme.MUTED, Icon = icon, Location = new Point((width - S(52)) / 2, S(22)) });
            var t = L(title, Theme.FontHead(12f), Theme.TEXT, S(20), S(86), width - S(40), S(24));
            t.TextAlign = ContentAlignment.MiddleCenter;
            var d = L(text, Theme.FontBody(9.5f), Theme.MUTED, S(30), S(112), width - S(60), S(44));
            d.TextAlign = ContentAlignment.TopCenter;
            card.Controls.Add(t);
            card.Controls.Add(d);
            return card;
        }

        /// <summary>Grid responsif: jumlah kolom menyesuaikan lebar (HP: 2 kolom, PC: 3–4 kolom).</summary>
        private Control CreateGrid(int w, int minTile, params Func<int, Control>[] makers)
        {
            int gap = S(14);
            int cols = Math.Max(1, Math.Min(makers.Length, (w + gap) / (S(minTile) + gap)));
            int tw = (w - gap * (cols - 1)) / cols;
            var tiles = makers.Select(m => m(tw)).ToList();
            int rows = (tiles.Count + cols - 1) / cols;
            var rowH = new int[rows];
            for (int i = 0; i < tiles.Count; i++) rowH[i / cols] = Math.Max(rowH[i / cols], tiles[i].Height);
            var grid = new Panel { Width = w, Margin = new Padding(0, S(4), 0, S(6)) };
            int y = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols && r * cols + c < tiles.Count; c++)
                {
                    var t = tiles[r * cols + c];
                    t.Height = rowH[r];   // satu baris sama tinggi
                    t.Location = new Point(c * (tw + gap), y);
                    grid.Controls.Add(t);
                }
                y += rowH[r] + gap;
            }
            grid.Height = Math.Max(0, y - gap);
            return grid;
        }

        private Control CreateTwoColumns(int w, Func<int, Control> left, Func<int, Control> right, int breakAt = 820)
        {
            int gap = S(18);
            bool stacked = w < S(breakAt);
            int cw = stacked ? w : (w - gap) / 2;
            var a = left(cw);
            var b = right(cw);
            var host = new Panel { Width = w, Margin = new Padding(0, 0, 0, S(6)) };
            if (stacked)
            {
                b.Location = new Point(0, a.Height + gap);
                host.Height = a.Height + gap + b.Height;
            }
            else
            {
                b.Location = new Point(cw + gap, 0);
                host.Height = Math.Max(a.Height, b.Height);
            }
            host.Controls.Add(a);
            host.Controls.Add(b);
            return host;
        }

        /// <summary>Chip filter (Android filterChip): pil; terpilih = isi tinta, lainnya = kartu bergaris.</summary>
        private PillButton CreateChip(string text, bool active, Action onClick)
        {
            var btn = (PillButton)Theme.CreateButton(text, active, Theme.INK, S(38));
            btn.AutoSize = false;
            btn.Font = Theme.FontLabel(9.5f);
            btn.Width = TextRenderer.MeasureText(text, btn.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + S(36);
            if (!active)
            {
                btn.BackColor = Theme.SURFACE;
                btn.ForeColor = Theme.TEXT;
                btn.FlatAppearance.BorderSize = 1;
                btn.FlatAppearance.BorderColor = Theme.LINE;
            }
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private Control ChipRow(int w, params PillButton[] chips)
        {
            var row = new Panel { Width = w, Margin = new Padding(0, S(6), 0, S(6)) };
            int x = 0, y = 0, gap = S(8);
            foreach (var c in chips)
            {
                if (x > 0 && x + c.Width > w) { x = 0; y += c.Height + gap; }
                c.Location = new Point(x, y);
                row.Controls.Add(c);
                x += c.Width + gap;
            }
            row.Height = y + (chips.Length > 0 ? chips[0].Height : 0);
            return row;
        }

        // ================= BERANDA =================
        private void BuildHomeTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();

            // ---- Sapaan: tanggal, "Selamat siang," + "mau kerjakan apa?" (persis Android) ----
            var greet = new Panel { Width = w, Margin = new Padding(0, S(20), 0, S(20)) };
            var dateLbl = L(DateTime.Now.ToString("dddd, d MMMM", new System.Globalization.CultureInfo("id-ID")), Theme.FontBody(11.5f), Theme.MUTED, 0, 0);
            var g1 = L(Greeting() + ",", Theme.FontDisplay(30f), Theme.TEXT, -S(3), dateLbl.PreferredHeight + S(6));
            var g2 = L("mau kerjakan apa?", Theme.FontDisplay(30f), Theme.MUTED, -S(3), g1.Top + g1.PreferredHeight - S(10));
            greet.Controls.AddRange(new Control[] { dateLbl, g1, g2 });
            greet.Height = g2.Top + g2.PreferredHeight;
            p.Controls.Add(greet);

            var recent = GetHistorySnapshot().Where(entry => File.Exists(entry.FilePath)).ToList();
            int outCount = AppStorage.GetOutFiles().Count;

            // ---- Kartu biru (padanan kartu "Scan dokumen") + 2 ubin angka. PC: berdampingan; sempit: bertumpuk ----
            int gap = S(16);
            bool wide = w >= S(900);
            int statW = wide ? S(290) : (w - gap) / 2;
            int heroW = wide ? w - statW - gap : w;
            int heroH = S(236);
            var top = new Panel { Width = w, Margin = new Padding(0, 0, 0, S(6)) };

            var hero = new CardPanel { Width = heroW, Height = heroH, BackColor = Theme.ACCENT, BorderThickness = 0, CornerRadius = 30, Location = Point.Empty };
            int artW = heroW >= S(560) ? S(124) : 0;
            int textW = heroW - S(68) - (artW > 0 ? artW + S(24) : 0);
            var hTitle = L("Edit PDF seperti di Word", Theme.FontDisplay(21f), Color.White, S(34), S(32), textW, S(40), true);
            var hSub = L("Ubah teks asli, tambah gambar & tanda tangan, lalu simpan jadi PDF baru. Semua diproses di PC Anda.",
                Theme.FontBody(11f), Color.FromArgb(217, 255, 255, 255), S(34), S(78), textW, S(54));
            var btnHero = Theme.CreateButton("Buka Editor", true, Color.White, S(52), S(200));
            btnHero.ForeColor = Theme.ACCENT;
            btnHero.Font = Theme.FontLabel(11f);
            btnHero.Location = new Point(S(34), heroH - S(52) - S(32));
            btnHero.Click += (s, e) => StartPdfEditorFlow();
            hero.Controls.AddRange(new Control[] { hTitle, hSub, btnHero });
            if (artW > 0)
                hero.Controls.Add(new HeroDocArt { Size = new Size(artW, S(156)), Location = new Point(heroW - artW - S(44), (heroH - S(156)) / 2) });
            top.Controls.Add(hero);

            Control Stat(int sw, int sh, string number, string label, bool blue, CardIcon icon, Action onClick)
            {
                var c = new CardPanel { Width = sw, Height = sh, BackColor = blue ? Theme.ACCENT : Theme.SURFACE, BorderThickness = blue ? 0 : 1, CornerRadius = 28, Cursor = Cursors.Hand };
                var num = L(number, Theme.FontDisplay(26f), blue ? Color.White : Theme.TEXT, S(26), S(14));
                var lbl = L(label, Theme.FontBody(11f), blue ? Color.FromArgb(217, 255, 255, 255) : Theme.MUTED, S(28), 0);
                lbl.Top = Math.Max(num.Top + num.PreferredHeight - S(2), sh - lbl.PreferredHeight - S(20));
                var ic = new IconChip { Size = new Size(S(46), S(46)), Radius = S(23), Bg = blue ? Theme.Mix(Theme.ACCENT, Color.White, 0.2) : Theme.SOFT, Fg = blue ? Color.White : Theme.TEXT, Icon = icon, Location = new Point(sw - S(46) - S(20), S(20)) };
                c.Controls.AddRange(new Control[] { num, lbl, ic });
                void Fire(object? s, EventArgs e) => RunGuarded(onClick);
                c.Click += Fire;
                foreach (Control ch in c.Controls) { ch.Click += Fire; ch.Cursor = Cursors.Hand; }
                return c;
            }
            int statH = wide ? (heroH - gap) / 2 : S(132);
            var st1 = Stat(statW, statH, recent.Count.ToString(), "Berkas terbaru", false, CardIcon.Folder, () => { _docsMode = 0; SelectTab(TAB_DOCS); });
            var st2 = Stat(statW, statH, outCount.ToString(), "Hasil dibuat", true, CardIcon.Download, () => { _docsMode = 2; _docsCat = "Semua"; SelectTab(TAB_DOCS); });
            if (wide)
            {
                st1.Location = new Point(heroW + gap, 0);
                st2.Location = new Point(heroW + gap, statH + gap);
                top.Height = heroH;
            }
            else
            {
                st1.Location = new Point(0, heroH + gap);
                st2.Location = new Point(statW + gap, heroH + gap);
                top.Height = heroH + gap + statH;
            }
            top.Controls.Add(st1);
            top.Controls.Add(st2);
            p.Controls.Add(top);

            // ---- Buka berkas: ubin putih membulat + label di bawah (persis Android) ----
            p.Controls.Add(CreateSectionHeader("Buka berkas", "", w));
            var opens = new (string Label, CardIcon Icon, Color Fg, Action Act)[]
            {
                ("PDF", CardIcon.Pdf, Theme.RED, () => PickAndOpenFile("PDF Files (*.pdf)|*.pdf")),
                ("Word", CardIcon.Word, Theme.SKY, () => PickAndOpenFile("Word (*.docx)|*.docx")),
                ("Excel", CardIcon.Excel, Theme.TEAL, () => PickAndOpenFile("Excel & CSV (*.xlsx;*.csv)|*.xlsx;*.csv")),
                ("PowerPoint", CardIcon.ImagePdf, Theme.AMBER, () => PickAndOpenFile("PowerPoint (*.pptx)|*.pptx")),
                ("Gambar", CardIcon.Image, Theme.INDIGO, () => PickAndOpenFile("Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp")),
                ("Lainnya", CardIcon.Folder, Theme.ACCENT, PickAndOpenSupportedFile),
            };
            var openRow = new Panel { Width = w, Height = S(132), Margin = new Padding(0, S(4), 0, S(6)) };
            int tile = S(88), slot = Math.Max(tile + S(18), Math.Min(S(150), w / opens.Length));
            for (int i = 0; i < opens.Length; i++)
            {
                var o = opens[i];
                int x = i * slot + (slot - tile) / 2;
                var card = new CardPanel { Size = new Size(tile, tile), CornerRadius = 26, Location = new Point(x, 0), Cursor = Cursors.Hand };
                card.Controls.Add(new IconChip { Size = new Size(S(40), S(40)), Bg = Theme.SURFACE, Fg = o.Fg, Icon = o.Icon, Location = new Point((tile - S(40)) / 2, (tile - S(40)) / 2), Cursor = Cursors.Hand });
                var cap = L(o.Label, Theme.FontLabel(10.5f), Theme.TEXT, i * slot, tile + S(12), slot, S(24));
                cap.TextAlign = ContentAlignment.TopCenter;
                cap.Cursor = Cursors.Hand;
                var act = o.Act;
                void Fire(object? s, EventArgs e) => RunGuarded(act);
                card.Click += Fire;
                foreach (Control ch in card.Controls) ch.Click += Fire;
                cap.Click += Fire;
                card.AttachHover();
                openRow.Controls.Add(card);
                openRow.Controls.Add(cap);
            }
            p.Controls.Add(openRow);

            // ---- Buat baru (daftar berkelompok) + Terakhir dibuka: berdampingan di PC ----
            void OpenWord(string tpl) { using var f = new DocEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }
            void OpenSheet(string tpl) { using var f = new SheetEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }

            p.Controls.Add(CreateTwoColumns(w,
                cw =>
                {
                    var col = new Panel { Width = cw };
                    var hdr = CreateSectionHeader("Buat baru", "Semua template", cw, () => SelectTab(TAB_CREATE));
                    hdr.Location = new Point(0, S(20));
                    var list = CreateListCard(cw, new[]
                    {
                        Row(CardIcon.Word, Theme.SKY, Theme.SKY_BG, "Dokumen Word", "Halaman kosong, editor ala Word", () => OpenWord(DocEditorForm.TEMPLATE_BLANK)),
                        Row(CardIcon.Excel, Theme.TEAL, Theme.TEAL_BG, "Spreadsheet Excel", "Tabel dengan rumus seperti Excel", () => OpenSheet(SheetEditorForm.TEMPLATE_BLANK)),
                        Row(CardIcon.Pdf, Theme.RED, Theme.RED_BG, "PDF dari teks", "Ketik catatan, jadi PDF A4 rapi", QuickTextToPdfDialog),
                        Row(CardIcon.Signature, Theme.SUCCESS, Theme.SUCCESS_BG, "Tanda tangan PDF", "Goreskan TTD lalu tempel ke PDF", SignExistingPdfFlow),
                    });
                    list.Location = new Point(0, hdr.Bottom + S(6));
                    col.Controls.Add(hdr);
                    col.Controls.Add(list);
                    col.Height = list.Bottom;
                    return col;
                },
                cw =>
                {
                    var col = new Panel { Width = cw };
                    var hdr = CreateSectionHeader("Terakhir dibuka", "Lihat semua", cw, () => { _docsMode = 0; SelectTab(TAB_DOCS); });
                    hdr.Location = new Point(0, S(20));
                    col.Controls.Add(hdr);
                    Control body;
                    if (recent.Count == 0)
                        body = CreateEmptyState(cw, CardIcon.Folder, "Belum ada berkas", "Buka atau buat dokumen PDF, Word, atau Excel — akan muncul di sini.");
                    else
                        body = CreateListCard(cw, recent.Take(4).Select(e => RecentRow(e, false)).ToList());
                    body.Location = new Point(0, hdr.Bottom + S(6));
                    col.Controls.Add(body);
                    col.Height = body.Bottom;
                    return col;
                }));

            // ---- Fitur cepat ----
            p.Controls.Add(CreateSectionHeader("Fitur cepat", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Baca Lantang", "Bacakan dokumen, offline (ID & EN)", "TTS", Theme.SKY, Theme.SKY_BG, PickAndOpenSupportedFile),
                tw => CreateFeatureTile(tw, "Cetak", "PDF atau gambar ke printer", "", Theme.ACCENT, Theme.BLUE_BG, QuickPrintFlow),
                tw => CreateFeatureTile(tw, "QR Code", "Pindai dari gambar atau buat QR", "", Theme.INDIGO, Theme.INDIGO_BG, ShowQrManagerDialog),
                tw => CreateFeatureTile(tw, "Cari teks dalam file", "Cari kata di isi PDF, Word, Excel & PPTX", "", Theme.ACCENT, Theme.BLUE_BG, GlobalSearchFlow)));
        }

        /// <summary>Baris berkas riwayat (nama, jenis, ukuran, waktu) + penanda & menu ⋯.</summary>
        private ListRow RecentRow(DocumentHistoryEntity e, bool withActions)
        {
            var (label, fg, bg) = AppStorage.TypeBadgeOf(e.FileType, e.FileName);
            long size = File.Exists(e.FilePath) ? new FileInfo(e.FilePath).Length : e.SizeBytes;
            long timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.LastOpenedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var row = new ListRow
            {
                Icon = IconForFileBadge(label, e.FileName),
                IconFg = fg,
                IconBg = bg,
                Title = e.FileName,
                Subtitle = $"{label} • {AppStorage.FmtSize(size)} • {AppStorage.RelTime(timestamp)}",
                Chevron = !withActions
            };
            row.Click += (s, ev) => OpenDocument(e.FilePath);
            if (!withActions) return row;

            row.AccessoryWidth = S(96);
            var btnStar = Theme.CreateButton(e.IsBookmarked ? "★" : "☆", false, e.IsBookmarked ? Theme.AMBER_FILL : Theme.TEXT, S(36), S(40));
            btnStar.Padding = Padding.Empty;
            btnStar.Font = new Font("Segoe UI Symbol", 11f);
            btnStar.BackColor = e.IsBookmarked ? Theme.AMBER_BG : Theme.SURFACE2;
            btnStar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnStar.Click += async (s, ev) =>
            {
                try
                {
                    await _historyService.SetBookmarkedAsync(e.FilePath, !e.IsBookmarked);
                    AppStorage.ToggleBookmark(e.FilePath);
                    RenderCurrentTab();
                }
                catch (Exception ex)
                {
                    Theme.Toast(this, $"Penanda tidak dapat disimpan: {ex.Message}");
                }
            };
            var btnMore = Theme.CreateButton("⋯", false, Theme.TEXT, S(36), S(40));
            btnMore.Padding = Padding.Empty;
            btnMore.Font = new Font("Segoe UI Symbol", 11f, FontStyle.Bold);
            btnMore.Click += (s, ev) =>
            {
                var m = new ContextMenuStrip();
                m.Items.Add("Buka di Reader", null, (_, _) => OpenDocument(e.FilePath));
                m.Items.Add("Edit dokumen", null, (_, _) => EditDocument(e.FilePath));
                m.Items.Add("Salin untuk dibagikan", null, (_, _) => ShareFilePath(e.FilePath));
                m.Items.Add("Ubah nama", null, (_, _) => ShowRenameDialog(e.FilePath));
                m.Items.Add("Kunci ke Brankas AES-256", null, (_, _) => LockFileIntoVaultFlow(e.FilePath));
                m.Items.Add("Tampilkan di Windows Explorer", null, (_, _) =>
                {
                    try { Process.Start("explorer.exe", $"/select,\"{e.FilePath}\""); } catch { }
                });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Hapus dari riwayat", null, async (_, _) =>
                {
                    try
                    {
                        await _historyService.RemoveAsync(e.FilePath);
                        AppStorage.RemoveRecent(e.FilePath);
                        RenderCurrentTab();
                    }
                    catch (Exception ex)
                    {
                        Theme.Toast(this, $"Riwayat tidak dapat dihapus: {ex.Message}");
                    }
                });
                m.Show(btnMore, new Point(0, btnMore.Height));
            };
            row.Controls.Add(btnStar);
            row.Controls.Add(btnMore);
            void Place()
            {
                btnMore.Location = new Point(row.Width - S(14) - btnMore.Width, (row.Height - btnMore.Height) / 2);
                btnStar.Location = new Point(btnMore.Left - S(8) - btnStar.Width, btnMore.Top);
            }
            row.Resize += (s, ev) => Place();
            Place();
            return row;
        }

        // ================= DOKUMEN =================
        private void BuildDocsTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            p.Controls.Add(CreateHeading(w, "Dokumen", "Berkas yang terakhir dibuka, yang Anda tandai, dan semua hasil olahan Depup Office."));

            var seg = new Segmented { Items = new[] { "Terkini", "Ditandai", "Hasil olahan" }, Width = Math.Min(w, S(460)), Margin = new Padding(0, 0, 0, S(8)) };
            seg.SelectedIndex = Math.Clamp(_docsMode, 0, 2);
            seg.SelectedChanged += i => { _docsMode = i; _filterMode = i == 1 ? 1 : 0; RenderCurrentTab(); };
            p.Controls.Add(seg);

            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                var info = L($"Hasil pencarian untuk “{_searchQuery}”", Theme.FontBody(10f), Theme.MUTED, 0, 0);
                info.Margin = new Padding(0, S(6), 0, S(2));
                p.Controls.Add(info);
            }

            if (_docsMode == 2)
            {
                var all = AppStorage.GetOutFiles();
                string[] cats = { "Semua", "PDF", "Word", "Excel", "Gambar" };
                string Key(string c) => c switch { "PDF" => "PDF", "Word" => "DOCX", "Excel" => "XLSX", "Gambar" => "IMG", _ => "" };
                p.Controls.Add(ChipRow(w, cats.Select(c =>
                {
                    int n = c == "Semua" ? all.Count : all.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == Key(c));
                    return CreateChip($"{c} ({n})", _docsCat == c, () => { _docsCat = c; RenderCurrentTab(); });
                }).ToArray()));

                var files = all
                    .Where(f => _docsCat == "Semua" || AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == Key(_docsCat))
                    .Where(f => string.IsNullOrWhiteSpace(_searchQuery) || f.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(60)
                    .ToList();
                if (files.Count == 0)
                {
                    p.Controls.Add(CreateEmptyState(w, CardIcon.Batch, "Belum ada hasil", "Hasil konversi, gabungan, dan dokumen yang Anda simpan akan muncul di sini."));
                }
                else
                {
                    p.Controls.Add(CreateListCard(w, files.Select(f =>
                    {
                        var (label, fg, bg) = AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name);
                        var r = new ListRow
                        {
                            Icon = IconForFileBadge(label, f.Name), IconFg = fg, IconBg = bg, Title = f.Name,
                            Subtitle = $"{label} • {AppStorage.FmtSize(f.Length)} • {f.LastWriteTime:dd MMM yyyy, HH:mm}"
                        };
                        string path = f.FullName;
                        r.Click += (s, e) => { if (AppStorage.IsSupportedDocument(path)) OpenDocument(path); else ShareFilePath(path); };
                        return r;
                    }).ToList()));
                }
                var actions = new Panel { Width = w, Height = S(48), Margin = new Padding(0, S(12), 0, 0) };
                var bOpen = Theme.CreateButton("Buka folder di Explorer", false, Theme.TEXT, S(42), S(240));
                bOpen.Click += (s, e) => { try { Process.Start("explorer.exe", $"\"{AppStorage.OutDir}\""); } catch { } };
                var bManage = Theme.CreateButton("Kelola folder", false, Theme.ACCENT, S(42), S(170));
                bManage.Location = new Point(bOpen.Right + S(10), 0);
                bManage.Click += (s, e) => RunGuarded(() => ShowFolderExplorerDialog("SEMUA"));
                actions.Controls.Add(bOpen);
                actions.Controls.Add(bManage);
                p.Controls.Add(actions);
                return;
            }

            _recentRowWidth = w;
            _recentListPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = w,
                Margin = new Padding(0, S(6), 0, 0),
                Padding = Padding.Empty,
                BackColor = Theme.BG
            };
            FillRecentFilesBox(_recentListPanel, 50, _searchQuery, w);
            p.Controls.Add(_recentListPanel);

            var pick = Theme.CreateButton("Pilih berkas lain…", false, Theme.ACCENT, S(42), S(200));
            pick.Margin = new Padding(0, S(12), 0, 0);
            pick.Click += (s, e) => PickAndOpenSupportedFile();
            p.Controls.Add(pick);
        }

        private async void FillRecentFilesBox(FlowLayoutPanel box, int maxCount, string filter, int width)
        {
            _recentLoadCts?.Cancel();
            _recentLoadCts?.Dispose();
            _recentLoadCts = new CancellationTokenSource();
            var token = _recentLoadCts.Token;
            box.SuspendLayout();
            box.Controls.Clear();
            box.Controls.Add(L("Memuat riwayat berkas…", Theme.FontBody(10f), Theme.MUTED, 0, 0));
            box.ResumeLayout();

            try
            {
                var all = (await _historyService.GetRecentAsync(cancellationToken: token))
                    .Where(entry => File.Exists(entry.FilePath))
                    .ToList();
                if (token.IsCancellationRequested || box.IsDisposed) return;

                var filtered = all
                    .Where(entry => string.IsNullOrWhiteSpace(filter) || entry.FileName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .Where(entry => _filterMode != 1 || entry.IsBookmarked)
                    .Take(maxCount)
                    .ToList();

                box.SuspendLayout();
                box.Controls.Clear();
                if (filtered.Count == 0)
                {
                    if (_filterMode == 1)
                        box.Controls.Add(CreateEmptyState(width, CardIcon.Folder, "Belum ada yang ditandai", "Klik ☆ pada salah satu berkas di Terkini untuk menandainya."));
                    else if (all.Count == 0)
                        box.Controls.Add(CreateEmptyState(width, CardIcon.Folder, "Belum ada berkas", "Buka atau buat dokumen PDF, Word, atau Excel untuk memulai. Anda juga bisa menyeret berkas ke jendela ini."));
                    else
                        box.Controls.Add(CreateEmptyState(width, CardIcon.Search, "Tidak ditemukan", "Tidak ada berkas yang cocok dengan kata kunci pencarian."));
                }
                else
                {
                    box.Controls.Add(CreateListCard(width, filtered.Select(e => RecentRow(e, true)).ToList()));
                }
                box.ResumeLayout();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (box.IsDisposed) return;
                box.SuspendLayout();
                box.Controls.Clear();
                box.Controls.Add(L($"Riwayat tidak dapat dimuat: {ex.Message}", Theme.FontBody(10f), Theme.ERROR, 0, 0));
                box.ResumeLayout();
            }
        }

        // ================= BUAT =================
        private void BuildCreateTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            p.Controls.Add(CreateHeading(w, "Buat", "Dokumen baru, template siap pakai, dan alat bisnis. Semua dibuat di PC Anda."));

            void OpenWord(string tpl) { using var f = new DocEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }
            void OpenSheet(string tpl) { using var f = new SheetEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }

            p.Controls.Add(CreateSectionHeader("Dokumen baru", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Dokumen Word", "Halaman kosong, editor ala Word (A4/Letter/F4)", "", Theme.SKY, Theme.SKY_BG, () => OpenWord(DocEditorForm.TEMPLATE_BLANK)),
                tw => CreateFeatureTile(tw, "Spreadsheet Excel", "Tabel dengan rumus =SUM, mata uang Rp", "", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_BLANK)),
                tw => CreateFeatureTile(tw, "PDF dari teks", "Ketik catatan, otomatis jadi PDF A4 rapi", "", Theme.RED, Theme.RED_BG, QuickTextToPdfDialog),
                tw => CreateFeatureTile(tw, "Edit PDF", "Ubah teks asli, tambah gambar & tanda tangan", "Populer", Theme.ACCENT, Theme.BLUE_BG, StartPdfEditorFlow)));

            p.Controls.Add(CreateSectionHeader("Template", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Surat Lamaran Kerja", "Template resmi HRD siap edit", "", Theme.SKY, Theme.SKY_BG, () => OpenWord(DocEditorForm.TEMPLATE_SURAT)),
                tw => CreateFeatureTile(tw, "CV / Resume", "Curriculum Vitae terstruktur", "", Theme.SKY, Theme.SKY_BG, () => OpenWord(DocEditorForm.TEMPLATE_CV)),
                tw => CreateFeatureTile(tw, "Kuitansi / Invoice", "Nota penjualan dengan total otomatis", "", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_INVOICE)),
                tw => CreateFeatureTile(tw, "Daftar Hadir", "Rekap kehadiran karyawan / siswa", "", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_PRESENSI))));

            p.Controls.Add(CreateSectionHeader("Surat organisasi & warga", "Karang Taruna • RT/RW • Panitia", w));
            p.Controls.Add(CreateGrid(w, 240, DocEditorForm.OrgTemplates.Select(t =>
                (Func<int, Control>)(tw => CreateFeatureTile(tw, t.Title, t.Sub, "", Theme.SKY, Theme.SKY_BG, () => OpenWord(t.Key)))).ToArray()));

            p.Controls.Add(CreateSectionHeader("Usaha & UMKM", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Nota / Kuitansi Toko", "Nomor otomatis, hitung total, stempel toko", "", Theme.ACCENT, Theme.BLUE_BG, InvoiceFlow),
                tw => CreateFeatureTile(tw, "Katalog Produk", "Katalog jualan / portofolio dengan foto", "", Theme.INDIGO, Theme.INDIGO_BG, CatalogFlow),
                tw => CreateFeatureTile(tw, "Jadwal & Agenda", "Perencana tugas siap cetak", "", Theme.TEAL, Theme.TEAL_BG, ScheduleFlow),
                tw => CreateFeatureTile(tw, "Label Harga & Barcode", "Code 128, EAN-13, QR • lembar A4", "", Theme.ACCENT, Theme.BLUE_BG, PriceTagFlow),
                tw => CreateFeatureTile(tw, "Pembuat Pasfoto", "2×3, 3×4, 4×6 • latar merah/biru • cetak A4", "", Theme.INDIGO, Theme.INDIGO_BG, IdPhotoFlow),
                tw => CreateFeatureTile(tw, "Grafik dari Excel", "Batang, lingkaran, garis → PDF laporan", "", Theme.TEAL, Theme.TEAL_BG, ChartFlow)));
        }

        // ================= ALAT =================
        private void BuildToolsTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            p.Controls.Add(CreateHeading(w, "Alat", "Pilih alat, lalu pilih berkasnya. Semua diproses di PC Anda — tidak ada yang diunggah."));

            // Jalan pintas: pilih berkas dulu, alat menyesuaikan (atau seret berkas ke jendela ini)
            void PickFileForQuickProcess()
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Pilih Berkas PDF atau Gambar",
                    Filter = "PDF & Gambar (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png"
                };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                    QuickProcessDroppedFiles(new[] { ofd.FileName });
            }
            var pickRow = Row(CardIcon.Upload, Theme.ACCENT, Theme.BLUE_BG, "Pilih berkas dulu", "PDF atau foto — lalu pilih tindakannya. Bisa juga seret berkas ke jendela ini.", PickFileForQuickProcess);
            var pickCard = CreateListCard(w, new[] { pickRow });
            pickCard.Margin = new Padding(0, 0, 0, S(4));
            p.Controls.Add(pickCard);

            if (AppStorage.Config.LastJob != null && File.Exists(AppStorage.Config.LastJob.FilePath))
            {
                var lj = AppStorage.Config.LastJob;
                var jr = new ListRow { Icon = CardIcon.Check, IconFg = Theme.SUCCESS, IconBg = Theme.SUCCESS_BG, Title = $"Terakhir: {lj.Title}", Subtitle = lj.FileName, Trailing = "Buka hasil" };
                jr.Click += (s, e) => OpenDocument(lj.FilePath);
                var jc = CreateListCard(w, new[] { jr });
                jc.Margin = new Padding(0, S(12), 0, S(4));
                p.Controls.Add(jc);
            }

            p.Controls.Add(CreateSectionHeader("Kelola PDF", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Gabung PDF", "Satukan beberapa PDF, urutan bebas", "", Theme.RED, Theme.RED_BG, MergePdfFlow),
                tw => CreateFeatureTile(tw, "Pisah PDF", "Ambil halaman tertentu", "", Theme.RED, Theme.RED_BG, SplitPdfFlow),
                tw => CreateFeatureTile(tw, "Susun halaman", "Urutkan, putar, atau hapus halaman", "", Theme.RED, Theme.RED_BG, OrganizePdfFlow),
                tw => CreateFeatureTile(tw, "Nomor Halaman PDF", "Tambahkan Hal X dari Y & Header", "", Theme.RED, Theme.RED_BG, PageNumberFlow),
                tw => CreateFeatureTile(tw, "Kompres PDF Presisi", "Target < 200 KB / 500 KB / 1 MB", "CPNS", Theme.RED, Theme.RED_BG, CompressPdfFlow),
                tw => CreateFeatureTile(tw, "Ambil gambar", "Simpan foto di dalam PDF", "", Theme.RED, Theme.RED_BG, ExtractPdfImagesFlow),
                tw => CreateFeatureTile(tw, "Edit PDF", "Ubah teks, tambah gambar & tanda tangan", "Baru", Theme.RED, Theme.RED_BG, StartPdfEditorFlow),
                tw => CreateFeatureTile(tw, "Watermark PDF", "RAHASIA / DRAFT diagonal di tiap halaman", "", Theme.RED, Theme.RED_BG, WatermarkPdfFlow),
                tw => CreateFeatureTile(tw, "Gabung campuran", "PDF & foto jadi satu PDF", "", Theme.RED, Theme.RED_BG, UniversalMergeFlow)));

            p.Controls.Add(CreateSectionHeader("Konversi", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "PDF ke Word", "Ubah PDF ke .docx yang bisa diedit", "Baru", Theme.SKY, Theme.SKY_BG, PdfToWordFlow),
                tw => CreateFeatureTile(tw, "Gambar ke PDF", "Gabung foto jadi satu PDF", "", Theme.SKY, Theme.SKY_BG, ImagesToSinglePdfFlow),
                tw => CreateFeatureTile(tw, "Konversi massal", "Banyak foto jadi PDF terpisah", "", Theme.SKY, Theme.SKY_BG, BatchImagesToPdfFlow),
                tw => CreateFeatureTile(tw, "EXE ke PDF", "Ambil PDF dari berkas .exe (PDF to EXE)", "", Theme.SKY, Theme.SKY_BG, ExeToPdfFlow),
                tw => CreateFeatureTile(tw, "Slideshow presentasi", "PPTX / PDF layar penuh, laser & timer", "", Theme.SKY, Theme.SKY_BG, SlideshowFlow)));

            p.Controls.Add(CreateSectionHeader("Gambar & foto", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Watermark foto massal", "Teks/stempel pada banyak foto produk", "", Theme.INDIGO, Theme.INDIGO_BG, BatchWatermarkFlow),
                tw => CreateFeatureTile(tw, "Pembersih bayangan foto", "Hapus bayangan tangan/HP & putihkan kertas", "", Theme.INDIGO, Theme.INDIGO_BG, ShadowRemoverFlow),
                tw => CreateFeatureTile(tw, "Pindai kartu nama", "Ekstrak nama, HP & email ke kontak .vcf", "", Theme.INDIGO, Theme.INDIGO_BG, BusinessCardFlow),
                tw => CreateFeatureTile(tw, "Pindai tabel ke Excel", "Foto tabel/kuitansi jadi berkas .xlsx", "", Theme.TEAL, Theme.TEAL_BG, TableToExcelFlow),
                tw => CreateFeatureTile(tw, "Kompres foto", "Kecilkan sebelum kirim WA/email", "", Theme.INDIGO, Theme.INDIGO_BG, CompressGalleryImagesFlow),
                tw => CreateFeatureTile(tw, "TTD transparan", "Foto tanda tangan jadi PNG transparan", "", Theme.INDIGO, Theme.INDIGO_BG, ExtractTransparentSignatureFlow)));

            p.Controls.Add(CreateSectionHeader("Tanda tangan & keamanan", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Pustaka TTD & Stempel", "Kelola & simpan tanda tangan & stempel", "", Theme.SUCCESS, Theme.SUCCESS_BG, () => Ui.PickStamp(this, manageOnly: true)),
                tw => CreateFeatureTile(tw, "Tanda tangan PDF", "Goreskan TTD lalu tempel ke halaman", "", Theme.SUCCESS, Theme.SUCCESS_BG, SignExistingPdfFlow),
                tw => CreateFeatureTile(tw, "Dokumen Terlindungi", "Password, anti-kopi, anti-cetak & sampul DepUp", "Utama", Theme.ACCENT, Theme.BLUE_BG, ProtectedDocFlow),
                tw => CreateFeatureTile(tw, "Password PDF AES-256", "Kunci PDF tanpa mengubah jadi gambar", "", Theme.ACCENT, Theme.BLUE_BG, PasswordPdfFlow),
                tw => CreateFeatureTile(tw, "Anti-kopi & anti-cetak", "Bisa dibuka, tapi salin/cetak dikunci", "", Theme.ACCENT, Theme.BLUE_BG, RestrictPdfFlow),
                tw => CreateFeatureTile(tw, "Kunci berkas", "Enkripsi ke Brankas AES-256", "", Theme.ACCENT, Theme.BLUE_BG, () =>
                {
                    using var ofd = new OpenFileDialog { Title = "Pilih Berkas untuk Dienkripsi ke Brankas", Filter = "Semua Berkas (*.*)|*.*" };
                    if (ofd.ShowDialog(this) == DialogResult.OK) LockFileIntoVaultFlow(ofd.FileName);
                }),
                tw => CreateFeatureTile(tw, "Cari teks dalam file", "Cari kata kunci di isi PDF & Word", "", Theme.ACCENT, Theme.BLUE_BG, GlobalSearchFlow),
                tw => CreateFeatureTile(tw, "QR Code", "Pindai dari gambar atau buat QR", "", Theme.ACCENT, Theme.BLUE_BG, ShowQrManagerDialog)));

            p.Controls.Add(CreateSectionHeader("Baca & bagikan", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Baca lantang", "Bacakan dokumen, offline (ID & EN)", "", Theme.SKY, Theme.SKY_BG, PickAndOpenSupportedFile),
                tw => CreateFeatureTile(tw, "Ringkas dokumen", "Poin-poin utama → PDF ringkasan", "", Theme.TEAL, Theme.TEAL_BG, SummarizerFlow),
                tw => CreateFeatureTile(tw, "Cetak", "Printer atau simpan PDF", "", Theme.ACCENT, Theme.BLUE_BG, QuickPrintFlow),
                tw => CreateFeatureTile(tw, "Salin untuk dibagikan", "Tempel ke WhatsApp Desktop, email, atau folder", "", Theme.SUCCESS, Theme.SUCCESS_BG, () =>
                {
                    using var ofd = new OpenFileDialog { Title = "Pilih Berkas untuk Dibagikan", Filter = "Semua Berkas (*.*)|*.*" };
                    if (ofd.ShowDialog(this) == DialogResult.OK) ShareFilePath(ofd.FileName);
                })));
        }

        // ================= SAYA =================
        private void BuildMeTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            var vaultFiles = VaultManager.ListEncryptedFiles();
            var outFiles = AppStorage.GetOutFiles();

            // ---- Kepala: logo + nama + status offline ----
            var head = new Panel { Width = w, Height = S(96), Margin = new Padding(0, S(20), 0, S(4)) };
            head.Controls.Add(new LogoTile { Size = new Size(S(64), S(64)), Location = new Point(0, S(12)) });
            head.Controls.Add(L("Depup Office", Theme.FontDisplay(22f), Theme.TEXT, S(80), S(10)));
            head.Controls.Add(new Pill { Text = "100% offline · berkas tidak diunggah", Dot = Theme.SUCCESS, Fg = Theme.SUCCESS, Bg = Theme.SUCCESS_BG, Font = Theme.FontLabel(9f), Location = new Point(S(80), S(52)) });
            p.Controls.Add(head);

            Control Left(int cw)
            {
                var col = new Panel { Width = cw };
                int y = 0;
                void Add(Control c) { c.Location = new Point(0, y); col.Controls.Add(c); y = c.Bottom; }

                var h1 = CreateSectionHeader("Keamanan", "", cw); Add(h1); y += S(6);
                Add(CreateListCard(cw, new[]
                {
                    Row(CardIcon.Lock, Theme.ACCENT, Theme.BLUE_BG, "Brankas pribadi", $"{vaultFiles.Count} berkas · terkunci PIN, AES-256-GCM", () => { using var vf = new VaultForm(); vf.ShowDialog(this); RenderCurrentTab(); }),
                    Row(CardIcon.Shield, Theme.SUCCESS, Theme.SUCCESS_BG, "Privasi & tentang aplikasi", "Bagaimana data Anda dijaga", OpenPrivacyPolicyForm),
                }));

                // Penyimpanan
                long vaultSize = AppStorage.GetDirectorySize(AppStorage.VaultDir);
                long outSize = AppStorage.GetDirectorySize(AppStorage.OutDir);
                var (free, total) = AppStorage.GetDeviceStorage();
                var h2 = CreateSectionHeader("Penyimpanan", "", cw); Add(h2); y += S(6);
                var st = new CardPanel { Width = cw, CornerRadius = 22 };
                st.Controls.Add(L("Dipakai Depup Office", Theme.FontLabel(10.5f), Theme.TEXT, S(20), S(18)));
                var used = L(AppStorage.FmtSize(vaultSize + outSize), Theme.FontLabel(10.5f), Theme.TEXT, 0, S(18), cw - S(40), S(22));
                used.Left = S(20); used.TextAlign = ContentAlignment.TopRight;
                st.Controls.Add(used);
                st.Controls.Add(new StackedStorageBar
                {
                    Location = new Point(S(20), S(52)),
                    Size = new Size(cw - S(40), S(10)),
                    TrackColor = Theme.SURFACE2,
                    Parts = new List<(long, Color)> { (Math.Max(1024, outSize), Theme.ACCENT), (Math.Max(512, vaultSize), Theme.INDIGO) }
                });
                int ly = S(76);
                foreach (var (dot, text, val) in new[]
                {
                    (Theme.ACCENT, $"Hasil olahan ({outFiles.Count} berkas)", AppStorage.FmtSize(outSize)),
                    (Theme.INDIGO, "Brankas", AppStorage.FmtSize(vaultSize)),
                    (Theme.LINE_STRONG, "Sisa ruang PC", $"{AppStorage.FmtGb(free)} dari {AppStorage.FmtGb(total)}")
                })
                {
                    st.Controls.Add(L("●", Theme.FontBody(9f), dot, S(20), ly));
                    st.Controls.Add(L(text, Theme.FontBody(9.5f), Theme.TEXT, S(38), ly, cw / 2, S(20), true));
                    var v = L(val, Theme.FontBody(9.5f), Theme.MUTED, S(20), ly, cw - S(40), S(20));
                    v.TextAlign = ContentAlignment.TopRight;
                    st.Controls.Add(v);
                    ly += S(24);
                }
                var bSee = Theme.CreateButton("Lihat hasil olahan", false, Theme.ACCENT, S(42), Math.Min(S(220), cw - S(40)));
                bSee.Location = new Point(S(20), ly + S(10));
                bSee.Click += (s, e) => { _docsMode = 2; _docsCat = "Semua"; SelectTab(TAB_DOCS); };
                st.Controls.Add(bSee);
                st.Height = bSee.Bottom + S(20);
                // label kiri ditambahkan sebelum label kanan: urutan z agar teks kiri tidak tertutup
                foreach (Control c in st.Controls.Cast<Control>().ToList()) if (c is Label lb && lb.TextAlign == ContentAlignment.TopRight) lb.SendToBack();
                Add(st);
                col.Height = y;
                return col;
            }

            Control Right(int cw)
            {
                var col = new Panel { Width = cw };
                int y = 0;
                void Add(Control c) { c.Location = new Point(0, y); col.Controls.Add(c); y = c.Bottom; }

                // Tampilan: tema (Ikuti Windows / Terang / Gelap) — sama dengan Android "Ikuti HP / Terang / Gelap"
                var h1 = CreateSectionHeader("Tampilan", "", cw); Add(h1); y += S(6);
                var tc = new CardPanel { Width = cw, CornerRadius = 22 };
                tc.Controls.Add(L("Tema", Theme.FontLabel(10.5f), Theme.TEXT, S(20), S(18)));
                tc.Controls.Add(L("Mode gelap lebih nyaman di malam hari. \"Ikuti Windows\" mengikuti Pengaturan › Personalisasi › Warna.", Theme.FontBody(9.5f), Theme.MUTED, S(20), S(42), cw - S(40), S(40)));
                var seg = new Segmented { Items = new[] { "Ikuti Windows", "Terang", "Gelap" }, Location = new Point(S(20), S(88)), Width = cw - S(40) };
                seg.SelectedIndex = Theme.Mode;
                seg.SelectedChanged += i => Theme.SetMode(i);
                tc.Controls.Add(seg);
                tc.Height = seg.Bottom + S(20);
                Add(tc);

                var h2 = CreateSectionHeader("Format bawaan", "", cw); Add(h2); y += S(6);
                Add(CreateListCard(cw, new[]
                {
                    Row(CardIcon.Word, Theme.SKY, Theme.SKY_BG, "Dokumen Word", "Ukuran kertas, margin, spasi, huruf", () => { using var f = new DocEditorForm(templateKey: DocEditorForm.TEMPLATE_BLANK); f.ShowDialog(this); }),
                    Row(CardIcon.Excel, Theme.TEAL, Theme.TEAL_BG, "Spreadsheet Excel", "Garis kisi, mata uang, jumlah otomatis", () => { using var f = new SheetEditorForm(templateKey: SheetEditorForm.TEMPLATE_BLANK); f.ShowDialog(this); }),
                    Row(CardIcon.Pdf, Theme.RED, Theme.RED_BG, "Pembaca PDF", $"Tema baca: {GetReaderThemeName(AppStorage.Config.PdfReaderTheme)} — klik untuk ganti", CycleDefaultReaderTheme),
                }));

                var h3 = CreateSectionHeader("Dukungan", "", cw); Add(h3); y += S(6);
                Add(CreateListCard(cw, new[]
                {
                    Row(CardIcon.Heart, Theme.ERROR, Theme.ERROR_BG, "Dukung developer", "Donasi sukarela — semua fitur tetap gratis", ShowDonationDialog),
                    Row(CardIcon.Mail, Theme.ACCENT, Theme.BLUE_BG, "Kirim masukan", PrivacyManager.DeveloperEmail, PrivacyManager.OpenDeveloperEmail),
                    Row(CardIcon.Shield, Theme.MUTED, Theme.SURFACE2, "Tentang Depup Office", $"Versi {Theme.AppVersion} · desktop Windows", ShowAboutDialog),
                }));
                col.Height = y;
                return col;
            }

            p.Controls.Add(CreateTwoColumns(w, Left, Right, 900));
            var ver = L($"Depup Office Desktop versi {Theme.AppVersion}", Theme.FontBody(9.5f), Theme.MUTED, 0, 0, w, S(24));
            ver.TextAlign = ContentAlignment.MiddleCenter;
            ver.Margin = new Padding(0, S(24), 0, 0);
            p.Controls.Add(ver);
        }

        // ================= IKON KARTU (vektor, bukan teks/emoji) =================

        /// <summary>Pilih ikon vektor berdasarkan judul kartu. Urutan pengecekan penting (yang lebih spesifik dulu).</summary>
        private static CardIcon ResolveCardIcon(string title)
        {
            string k = title.ToLowerInvariant();
            // fitur migrasi Android v4.1 (dicek lebih dulu karena lebih spesifik)
            if (k.Contains("terlindungi") || k.Contains("anti-kopi")) return CardIcon.Lock;
            if (k.Contains("nomor halaman")) return CardIcon.Organize;
            if (k.Contains("watermark foto")) return CardIcon.Batch;
            if (k.Contains("watermark")) return CardIcon.Edit;
            if (k.Contains("campuran")) return CardIcon.Merge;
            if (k.Contains("pdf ke word")) return CardIcon.Word;
            if (k.Contains("slideshow") || k.Contains("presentasi") || k.Contains("pptx")) return CardIcon.ImagePdf;
            if (k.Contains("exe ke pdf")) return CardIcon.Pdf;
            if (k.Contains("pustaka ttd")) return CardIcon.Signature;
            if (k.Contains("nota")) return CardIcon.Receipt;
            if (k.Contains("undangan") || k.Contains("izin") || k.Contains("keterangan") || k.Contains("proposal")) return CardIcon.Letter;
            if (k.Contains("notulen")) return CardIcon.Edit;
            if (k.Contains("katalog")) return CardIcon.ImagePdf;
            if (k.Contains("jadwal")) return CardIcon.Calendar;
            if (k.Contains("barcode") || k.Contains("label harga")) return CardIcon.Qr;
            if (k.Contains("pasfoto") || k.Contains("kartu nama")) return CardIcon.Person;
            if (k.Contains("grafik") || k.Contains("tabel foto")) return CardIcon.Excel;
            if (k.Contains("bayangan")) return CardIcon.Image;
            if (k.Contains("cari teks")) return CardIcon.Folder;
            if (k.Contains("ringkas")) return CardIcon.Letter;
            if (k.Contains("gabung")) return CardIcon.Merge;
            if (k.Contains("pisah")) return CardIcon.Split;
            if (k.Contains("susun")) return CardIcon.Organize;
            if (k.Contains("sedot")) return CardIcon.Image;
            if (k.Contains("kompres")) return CardIcon.Compress;
            if (k.Contains("konversi massal")) return CardIcon.Batch;
            if (k.Contains("gambar ke pdf")) return CardIcon.ImagePdf;
            if (k.Contains("ttd") || k.Contains("tanda tangan")) return CardIcon.Signature;
            if (k.Contains("qr")) return CardIcon.Qr;
            if (k.Contains("password") || k.Contains("kunci")) return CardIcon.Lock;
            if (k.Contains("brankas")) return CardIcon.Vault;
            if (k.Contains("tema")) return CardIcon.Theme;
            if (k.Contains("scan") || k.Contains("studio") || k.Contains("pindai")) return CardIcon.Scan;
            if (k.Contains("lantang")) return CardIcon.Speaker;
            if (k.Contains("cetak")) return CardIcon.Printer;
            if (k.Contains("surat")) return CardIcon.Letter;
            if (k.Contains("cv") || k.Contains("resume")) return CardIcon.Person;
            if (k.Contains("kuitansi") || k.Contains("invoice")) return CardIcon.Receipt;
            if (k.Contains("hadir")) return CardIcon.Calendar;
            if (k.Contains("tulis")) return CardIcon.Edit;
            if (k.Contains("semua") || k.Contains("folder")) return CardIcon.Folder;
            if (k.Contains("pengaturan word") || k.Contains("word")) return CardIcon.Word;
            if (k.Contains("pengaturan excel") || k.Contains("excel")) return CardIcon.Excel;
            if (k.Contains("pengaturan")) return CardIcon.Sliders;
            if (k.Contains("pdf")) return CardIcon.Pdf;
            return CardIcon.Pdf;
        }

        /// <summary>Ikon untuk badge tipe berkas pada daftar "File Terakhir" (label dari AppStorage.TypeBadgeOf).</summary>
        private static CardIcon IconForFileBadge(string label, string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (ext is ".pdf") return CardIcon.Pdf;
            if (ext is ".docx" or ".doc" or ".txt") return CardIcon.Word;
            if (ext is ".xlsx" or ".xls" or ".csv") return CardIcon.Excel;
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp") return CardIcon.Image;
            return label switch { "PDF" => CardIcon.Pdf, "DOCX" => CardIcon.Word, "XLSX" => CardIcon.Excel, "IMG" => CardIcon.Image, _ => CardIcon.Pdf };
        }

        private void QuickPrintFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Berkas untuk Dicetak",
                Filter = "PDF & Gambar (*.pdf;*.jpg;*.jpeg;*.png;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string name = Path.GetFileName(ofd.FileName);
                if (string.Equals(Path.GetExtension(ofd.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    if (!PdfEngine.EnsureUnlocked(this, ofd.FileName)) return;
                    PrintHelper.PrintPdf(this, ofd.FileName, name);
                }
                else
                {
                    using var bmp = new Bitmap(ofd.FileName);
                    PrintHelper.PrintBitmap(this, bmp, name);
                }
            }
            catch (Exception ex)
            {
                Theme.Toast(this, $"Berkas tidak dapat dicetak: {ex.Message}");
            }
        }

        private List<DocumentHistoryEntity> GetHistorySnapshot()
        {
            try
            {
                return _historyService.GetRecentAsync().GetAwaiter().GetResult().ToList();
            }
            catch
            {
                return AppStorage.Config.RecentFiles.Select(entry => new DocumentHistoryEntity
                {
                    FilePath = entry.FilePath,
                    FileName = entry.Name,
                    FileType = entry.Mime,
                    SizeBytes = File.Exists(entry.FilePath) ? new FileInfo(entry.FilePath).Length : 0,
                    LastOpenedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, entry.TimestampMs)).UtcDateTime,
                    IsBookmarked = AppStorage.IsBookmarked(entry.FilePath)
                }).ToList();
            }
        }

        private void ShareFilePath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Theme.Toast(this, "Berkas tidak ditemukan");
                return;
            }

            try
            {
                var files = new System.Collections.Specialized.StringCollection { filePath };
                Clipboard.SetFileDropList(files);
                Theme.Toast(this, "Berkas disalin — tempel (Ctrl+V) di WhatsApp, email, atau folder tujuan");
            }
            catch (Exception ex)
            {
                Theme.Toast(this, $"Clipboard sedang dipakai aplikasi lain: {ex.Message}");
            }
        }

        private static string GetReaderThemeName(int t) => t switch { 1 => "Malam", 2 => "Sepia", 3 => "Eye-Care", _ => "Siang" };

        private void CycleDefaultReaderTheme()
        {
            AppStorage.Config.PdfReaderTheme = (AppStorage.Config.PdfReaderTheme + 1) % 4;
            AppStorage.SaveConfig();
            Theme.Toast(this, $"Tema standar Reader diubah ke: {GetReaderThemeName(AppStorage.Config.PdfReaderTheme)}");
            RenderCurrentTab();
        }
    }
}
