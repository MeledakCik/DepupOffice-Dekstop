using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Tampilan ruang kerja desktop dengan navigasi samping, toolbar dan halaman yang menyesuaikan lebar.
    /// Semua ukuran memakai Theme.S() (skala DPI); halaman dibangun ulang saat lebar area konten berubah.
    /// </summary>
    public partial class MainForm
    {
        private static int S(int px) => Theme.S(px);

        private SearchBox _searchBox = null!;
        private Label _toolbarTitle = null!;
        private Panel _rightHost = null!;
        private FlowLayoutPanel? _recentListPanel;
        private FlowLayoutPanel? _homeRecentPanel;
        private FlowLayoutPanel? _outputListPanel;
        private int _recentRowWidth;
        private int _builtAvailWidth;
        private Action? _layoutTopBar;
        private System.Windows.Forms.Timer? _reflowTimer;
        private DragEventHandler _dragEnterHandler = null!;
        private DragEventHandler _dragDropHandler = null!;
        private Action? _themeChanged;
        private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 160 };
        private readonly Dictionary<int, int> _tabScroll = new();
        private IReadOnlyList<DocumentHistoryEntity>? _historySnapshot;
        private IReadOnlyList<FileInfo>? _outputSnapshot;
        private string? _historyError;

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
            DoubleBuffered = true;
            if (_themeChanged == null)
            {
                // Tema berganti (Saya › Tampilan atau Windows berpindah terang/gelap): bangun ulang seluruh tampilan.
                _themeChanged = () =>
                {
                    if (IsDisposed || Disposing || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || Disposing) return;
                        // crossfade ala iOS: potret tema lama memudar di atas tampilan tema baru
                        ThemeFade.Run(this, () =>
                        {
                            RebuildShellForDpi();
                            Theme.ApplyWindowChrome(this);
                        });
                    }));
                };
                Theme.Changed += _themeChanged;
                _searchTimer.Tick += (s, e) =>
                {
                    _searchTimer.Stop();
                    if (_currentTab != TAB_DOCS) return;
                    _listLimit = InitialListLimit;
                    _contentHost.ScrollOffset = 0;
                    if (_docsMode == 2) ShowOutputRows();
                    else ShowHistoryRows();
                    StartContentSearch();
                };
            }

            // Responsif: jendela sempit → navigasi ringkas (ikon saja) & jarak tepi lebih kecil.
            _compactNav = WantCompactNav();
            bool compact = _compactNav;
            int sideW = compact ? S(60) : S(208);
            var sidebar = new GlassPane(Theme.SIDEBAR, AnchorStyles.Right) { Dock = DockStyle.Left, Width = sideW };
            Color sidebarBack = sidebar.BackColor;

            var brand = new Panel { Dock = DockStyle.Top, Height = compact ? S(61) : S(98), BackColor = sidebarBack };
            var logo = new LogoTile { Size = new Size(S(28), S(28)), Location = new Point(compact ? (sideW - S(28)) / 2 : S(19), S(17)), Cursor = Cursors.Hand };
            logo.Click += (s, e) => SelectTab(TAB_HOME);
            brand.Controls.Add(logo);
            if (!compact)
            {
                brand.Controls.Add(L("Depup Office", Theme.FontHead(12f), Theme.TEXT, S(56), S(20)));
                brand.Controls.Add(L("Ruang kerja", Theme.FontHead(9f), Theme.MUTED, S(19), S(72)));
            }
            var nav = new Panel { Dock = DockStyle.Fill, BackColor = sidebarBack, Padding = compact ? new Padding(S(8), 0, S(8), 0) : new Padding(S(11), 0, S(11), 0) };
            NavItemIcon[] navIcons = { NavItemIcon.Home, NavItemIcon.Docs, NavItemIcon.Create, NavItemIcon.Tools, NavItemIcon.User };
            for (int i = _tabNames.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var item = new NavItem
                {
                    Caption = _tabNames[i], Icon = navIcons[i], Dock = DockStyle.Top,
                    Height = S(38), Compact = compact, AccessibleName = _tabNames[i],
                    AccessibleRole = AccessibleRole.PushButton, TabIndex = i
                };
                item.SetActive(i == _currentTab, animate: false);
                if (compact) _navTips.SetToolTip(item, _tabNames[i]);
                item.Click += (s, e) => SelectTab(idx);
                _navItems[idx] = item;
                nav.Controls.Add(item);
            }

            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(brand);

            // ---------- Area kanan: bilah atas + konten ----------
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG };
            _rightHost = right;
            var topBar = new GlassPane(Theme.TOOLBAR, AnchorStyles.Bottom) { Dock = DockStyle.Top, Height = S(60) };

            _toolbarTitle = L(_tabNames[_currentTab], Theme.FontHead(13f), Theme.TEXT, 0, 0, S(130), S(32), true);
            _toolbarTitle.TextAlign = ContentAlignment.MiddleLeft;
            _searchBox = new SearchBox();
            _searchBox.Input.PlaceholderText = "Cari nama atau isi berkas";
            _searchBox.Input.AccessibleName = "Cari nama atau isi berkas";
            _searchBox.Input.Text = _searchQuery;
            _searchBox.Input.TextChanged += (s, e) =>
            {
                _searchQuery = _searchBox.Input.Text.Trim();
                if (_currentTab != TAB_DOCS)
                {
                    _docsMode = 0;
                    SelectTab(TAB_DOCS);
                    return;
                }
                _searchTimer.Stop();
                _searchTimer.Start();
            };

            var themeBtn = new CircleButton { Size = new Size(S(34), S(34)), Cursor = Cursors.Hand, Glyph = Theme.Dark ? CircleGlyph.Sun : CircleGlyph.Moon };
            themeBtn.AccessibleName = Theme.Dark ? "Ganti ke tema terang" : "Ganti ke tema gelap";
            themeBtn.Pressed += (s, e) => Theme.SetMode(Theme.Dark ? Theme.MODE_LIGHT : Theme.MODE_DARK);
            _navTips.SetToolTip(themeBtn, themeBtn.AccessibleName);
            var scan = Theme.CreateButton("Pindai", true, Theme.ACCENT, S(34), S(82));
            scan.AccessibleName = "Pindai dokumen dari kamera atau foto";
            _navTips.SetToolTip(scan, "Pindai dokumen · Ctrl+Shift+S");
            scan.Click += (s, e) => RunGuarded(() => StartScanFlow());

            topBar.Controls.AddRange(new Control[] { _toolbarTitle, scan, _searchBox, themeBtn });
            _layoutTopBar = () =>
            {
                int padX = compact ? S(14) : S(24);
                bool stacked = topBar.ClientSize.Width < padX * 2 + scan.Width + themeBtn.Width + S(132);
                topBar.Height = S(stacked ? 98 : 60);
                int h = topBar.ClientSize.Height;
                if (stacked)
                {
                    int right = topBar.ClientSize.Width - padX;
                    themeBtn.Location = new Point(right - themeBtn.Width, S(6));
                    scan.Location = new Point(themeBtn.Left - S(8) - scan.Width, S(6));
                    int width = Math.Max(1, scan.Left - padX - S(8));
                    _toolbarTitle.SetBounds(padX, S(7), width, S(32));
                    _toolbarTitle.Visible = width >= S(72);
                    _searchBox.SetBounds(padX, S(51), Math.Max(1, right - padX), S(34));
                    return;
                }
                themeBtn.Location = new Point(topBar.ClientSize.Width - padX - themeBtn.Width, (h - themeBtn.Height) / 2);
                int available = themeBtn.Left - padX - scan.Width - S(20);
                bool showTitle = available >= S(295);
                int titleWidth = showTitle ? S(136) : 0;
                int searchWidth = Math.Max(1, Math.Min(S(300), available - titleWidth));
                _searchBox.SetBounds(themeBtn.Left - S(10) - searchWidth, (h - S(34)) / 2, searchWidth, S(34));
                scan.Location = new Point(_searchBox.Left - S(10) - scan.Width, (h - scan.Height) / 2);
                _toolbarTitle.SetBounds(padX, (h - S(32)) / 2, titleWidth, S(32));
                _toolbarTitle.Visible = showTitle;
            };
            topBar.Resize += (s, e) => _layoutTopBar?.Invoke();
            topBar.HandleCreated += (s, e) => _layoutTopBar?.Invoke();
            _layoutTopBar();

            _contentHost = new WorkspaceViewport
            {
                Dock = DockStyle.Fill,
                Padding = compact ? new Padding(S(16), S(8), S(16), S(24)) : new Padding(S(28), S(8), S(28), S(28)),
                AllowDrop = true
            };
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
            var status = new GlassPane(Theme.SURFACE, AnchorStyles.Top) { Dock = DockStyle.Bottom, Height = S(28) };
            Label StatusText(string text, Color color, DockStyle dock)
            {
                var f = Theme.FontBody(8.5f);
                return new Label
                {
                    Text = text,
                    Dock = dock,
                    AutoSize = false,
                    Width = TextRenderer.MeasureText(text, f).Width + S(30),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = Theme.FontBody(8.5f),
                    ForeColor = color,
                    BackColor = status.BackColor,
                    UseMnemonic = false,
                    AutoEllipsis = true,
                    Cursor = Cursors.Default,
                    Padding = new Padding(S(14), 0, S(8), 0)
                };
            }
            var outputLocation = StatusText("Hasil disimpan di " + AppStorage.OutDir, Theme.MUTED, DockStyle.Fill);
            _navTips.SetToolTip(outputLocation, AppStorage.OutDir);
            status.Controls.Add(outputLocation);
            if (!compact) status.Controls.Add(StatusText($"Depup Office {Theme.AppVersion}  ·  Tema {(Theme.Mode == Theme.MODE_SYSTEM ? "ikut Windows" : Theme.Dark ? "gelap" : "terang")}", Theme.MUTED, DockStyle.Right));
            var zoomLbl = StatusText($"Zoom {Math.Round(Theme.Zoom * 100)}%   Ctrl + / Ctrl −", Theme.MUTED, DockStyle.Right);
            zoomLbl.Cursor = Cursors.Hand;
            zoomLbl.Click += (s, e) => ChangeZoom(1f);   // klik = kembali ke 100%
            status.Controls.Add(zoomLbl);
            status.Controls.Add(StatusText("●  Offline", Theme.SUCCESS, DockStyle.Left));

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
        private bool _compactNav;
        private bool _reflowOnly;
        private readonly ToolTip _navTips = new() { InitialDelay = 300 };

        /// <summary>Navigasi ringkas bila area jendela sempit (mis. jendela dibagi dua layar / laptop kecil).</summary>
        private bool WantCompactNav() => ClientSize.Width > 0 && ClientSize.Width < S(1080);

        /// <summary>Ganti zoom antarmuka (Ctrl + / Ctrl − / Ctrl 0) lalu bangun ulang tampilan.</summary>
        private void ChangeZoom(float z)
        {
            if (Math.Abs(z - Theme.Zoom) < 0.001f) { Theme.Toast(this, $"Zoom {Math.Round(Theme.Zoom * 100)}%"); return; }
            Theme.SetZoom(z);
            Theme.SetDpi(DeviceDpi);
            _reflowOnly = true;
            try { RebuildShellForDpi(); } finally { _reflowOnly = false; }
            Theme.Toast(this, $"Zoom tampilan {Math.Round(Theme.Zoom * 100)}%  •  Ctrl + / Ctrl − / Ctrl 0");
        }

        /// <summary>Bangun ulang rangka & halaman (DPI monitor berubah atau tema berganti).</summary>
        private void RebuildShellForDpi()
        {
            int scroll = _contentHost.ScrollOffset;
            bool searchFocused = _searchBox.Input.Focused;
            int searchCaret = _searchBox.Input.SelectionStart;
            int searchSelection = _searchBox.Input.SelectionLength;
            var viewerHost = _viewerHost;
            Redraw.Suspend(this);
            SuspendLayout();
            try
            {
                if (viewerHost != null) _rightHost.Controls.Remove(viewerHost);
                var old = Controls.Cast<Control>().ToArray();
                _navItems.Clear();
                BuildShell();
                foreach (var control in old) control.Dispose();
                if (viewerHost != null)
                {
                    _rightHost.Controls.Add(viewerHost);
                    viewerHost.BringToFront();
                }
                ResumeLayout(true);
                RenderCurrentTab(scroll);
                _contentHost.Visible = viewerHost == null;
                if (viewerHost != null) _activeViewer?.Focus();
                else if (searchFocused)
                {
                    _searchBox.Input.Focus();
                    _searchBox.Input.Select(searchCaret, searchSelection);
                }
                else if (_navItems.TryGetValue(_currentTab, out var nav)) nav.Focus();
            }
            finally
            {
                ResumeLayout(false);
                Redraw.Resume(this);
            }
        }

        private void SelectTab(int idx, bool force = false)
        {
            bool hadViewer = _activeViewer != null;
            CloseEmbeddedViewer();
            if (!force && idx == _currentTab && !hadViewer && idx != TAB_DOCS) return;
            _tabScroll[_currentTab] = _contentHost.ScrollOffset;
            bool changed = idx != _currentTab;
            _currentTab = idx;
            _toolbarTitle.Text = _tabNames[idx];
            foreach (var (k, item) in _navItems)
                item.SetActive(k == _currentTab, animate: changed);
            RenderCurrentTab(_tabScroll.GetValueOrDefault(_currentTab));
        }

        private int AvailableWidth() => _contentHost.ContentWidth;

        private int GetContentWidth()
        {
            return Math.Max(1, Math.Min(S(1180), AvailableWidth() - S(4)));
        }

        private void RenderCurrentTab(bool preserveScroll = false) =>
            RenderCurrentTab(preserveScroll ? _contentHost.ScrollOffset : 0);

        private void RenderCurrentTab(int scrollOffset)
        {
            RenderCurrentTabCore(scrollOffset);
            if (!_reflowOnly && (_currentTab == TAB_HOME || (_currentTab == TAB_DOCS && _docsMode != 2)))
                _ = RefreshHistoryAsync();
        }

        private void RenderCurrentTabCore(int scrollOffset)
        {
            _recentListPanel = null;
            _homeRecentPanel = null;
            _outputListPanel = null;
            _contentHitsPanel = null;

            int extra = Math.Max(0, AvailableWidth() - S(4) - GetContentWidth());
            var page = new WorkspacePage { AllowDrop = true, Padding = new Padding(extra / 2, 0, 0, S(8)) };
            page.DragEnter += _dragEnterHandler;
            page.DragDrop += _dragDropHandler;

            page.SuspendLayout();
            switch (_currentTab)
            {
                case TAB_HOME: BuildHomeTab(page); break;
                case TAB_DOCS: BuildDocsTab(page); break;
                case TAB_CREATE: BuildCreateTab(page); break;
                case TAB_TOOLS: BuildToolsTab(page); break;
                default: BuildMeTab(page); break;
            }
            page.ResumeLayout(true);

            _builtAvailWidth = AvailableWidth();
            // Tunggu sampai event klik di halaman lama selesai sebelum membuang kontrolnya.
            if (_contentHost.SetPage(page, scrollOffset) is { } old) BeginInvoke(new Action(old.Dispose));
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
                // Android sectionTitle: tautan abu tebal + chevron, tanpa garis bawah
                var link = new Label
                {
                    Text = badgeOrLink, Font = Theme.FontLabel(10.5f, FontStyle.Bold), ForeColor = Theme.MUTED,
                    AutoSize = true, UseMnemonic = false, BackColor = Color.Transparent, Cursor = Cursors.Hand,
                    AccessibleRole = AccessibleRole.Link, AccessibleName = badgeOrLink + " " + title
                };
                var chev = new IconGlyph("chev", Theme.MUTED) { Size = new Size(S(14), S(14)), Cursor = Cursors.Hand };
                link.Click += (s, e) => onLinkClick();
                chev.Click += (s, e) => onLinkClick();
                chev.Location = new Point(width - S(14), S(11));
                link.Location = new Point(chev.Left - S(2) - link.GetPreferredSize(Size.Empty).Width, S(8));
                panel.Controls.Add(link);
                panel.Controls.Add(chev);
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
            int pad = S(14);   // Android tile: vbox(14)
            int innerW = Math.Max(S(60), width - pad * 2);
            var subFont = Theme.FontBody(9.5f);
            int lineH = TextRenderer.MeasureText("Ag", subFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            // ukur sedikit lebih sempit dari label agar baris ke-2/3 tidak terpotong
            int subH = TextRenderer.MeasureText(sub, subFont, new Size(innerW - S(10), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            subH = Math.Min(Math.Max(subH, lineH), lineH * 3) + S(4);

            int chipSize = S(42);   // Android iconBadge 42 dp, radius 12, ikon 22 dp
            int titleY = pad + chipSize + S(12);
            int titleH = S(26);
            int subY = titleY + titleH + S(2);
            var card = new CardPanel { Width = width, Height = Math.Max(S(132), subY + subH + pad), Cursor = Cursors.Hand, CornerRadius = 22 };

            var icon = ResolveCardIcon(title);
            if (StyleFor(title) is { } st) { icon = st.icon; accent = st.fg; accentBg = st.bg; }   // sama dengan Android
            var chip = new IconChip { Size = new Size(chipSize, chipSize), Radius = S(12), Bg = accentBg, Fg = accent, Icon = icon, Location = new Point(pad, pad) };
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

            card.MakeAction(title, () => RunGuarded(onClick));
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
            if (StyleFor(title) is { } st) { icon = st.icon; fg = st.fg; bg = st.bg; }   // sama dengan Android
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
                void Stack()
                {
                    b.Location = new Point(0, a.Height + gap);
                    host.Height = a.Height + gap + b.Height;
                }
                a.SizeChanged += (s, e) => Stack();
                b.SizeChanged += (s, e) => Stack();
                Stack();
            }
            else
            {
                b.Location = new Point(cw + gap, 0);
                void Fit() => host.Height = Math.Max(a.Height, b.Height);
                a.SizeChanged += (s, e) => Fit();
                b.SizeChanged += (s, e) => Fit();
                Fit();
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

        // ================= BERANDA (urutan & komponen sama dengan Android MainActivity.buildHome) =================
        private void BuildHomeTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            void OpenWord(string tpl) { using var f = new DocEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }
            void OpenSheet(string tpl) { using var f = new SheetEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }

            // Kolom cari (membuka tab Dokumen)
            var search = new CardPanel { Width = w, Height = S(52), CornerRadius = 100, Margin = new Padding(0, S(12), 0, 0), Cursor = Cursors.IBeam };
            search.Controls.Add(new IconGlyph("search", Theme.MUTED) { Bounds = new Rectangle(S(18), S(16), S(20), S(20)) });
            search.Controls.Add(L("Cari dokumen…", Theme.FontBody(11f), Theme.MUTED, S(50), S(15), w - S(70), S(24)));
            search.MakeAction("Cari dokumen", () => { _docsMode = 0; SelectTab(TAB_DOCS); _searchBox.Input.Focus(); });
            p.Controls.Add(search);

            // Disematkan
            BuildHomePins(p, w);

            // Baru dibuka: korsel sampul
            var recentHdr = CreateSectionHeader("Baru dibuka", "Semua", w, () => { _docsMode = 0; SelectTab(TAB_DOCS); });
            _homeRecentHeader = recentHdr;
            p.Controls.Add(recentHdr);
            _homeRecentPanel = new FlowLayoutPanel
            {
                Width = w, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, S(6), 0, 0), BackColor = Theme.BG
            };
            p.Controls.Add(_homeRecentPanel);
            ShowHomeHistory(w);

            // Kartu Scan: aksi utama (panel biru royal, sudut 24)
            int pad = S(20);
            int artW = w >= S(420) ? S(92) : 0;
            var hero = new CardPanel { Width = w, CornerRadius = 24, BackColor = Theme.ACCENT, BorderColor = Theme.ACCENT, Margin = new Padding(0, S(16), 0, 0) };
            int tw = Math.Max(1, w - pad * 2 - (artW > 0 ? artW + S(6) : 0));
            var title = L("Scan dokumen", Theme.FontDisplay(16.5f), Color.White, pad, pad, tw, S(34), true);
            const string desc = "Foto kertas jadi PDF rapi. Sudut otomatis diluruskan.";
            var dfont = Theme.FontBody(10.5f);
            int dh = TextRenderer.MeasureText(desc, dfont, new Size(tw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(2);
            var detail = L(desc, dfont, Color.FromArgb(0xD9, 255, 255, 255), pad, title.Bottom + S(4), tw, dh);
            var start = new HeroButton("Mulai Scan", "camera") { Location = new Point(pad, detail.Bottom + S(14)) };
            start.Click += (s, e) => RunGuarded(() => StartScanFlow());
            hero.Controls.AddRange(new Control[] { title, detail, start });
            hero.Height = start.Bottom + pad;
            if (artW > 0) hero.Controls.Add(new ScanFrameArt { Bounds = new Rectangle(w - pad - artW, Math.Max(pad, (hero.Height - S(112)) / 2), artW, S(112)) });
            p.Controls.Add(hero);

            // Bento: dua ubin angka — permukaan + panel biru
            int nRecent = _historySnapshot?.Count ?? AppStorage.Config.RecentFiles.Count;
            int nOut = (_outputSnapshot ??= AppStorage.GetOutFiles(includeImages: true)).Count;
            int gap = S(12), half = (w - gap) / 2;
            var stats = new Panel { Width = w, Height = S(96), Margin = new Padding(0, S(12), 0, 0) };
            var t1 = StatTile(half, nRecent.ToString(), "Berkas terbaru", false, "folder", () => { _docsMode = 0; SelectTab(TAB_DOCS); });
            var t2 = StatTile(half, nOut.ToString(), "Hasil dibuat", true, "download", () => { _docsMode = 2; _docsCat = "Semua"; SelectTab(TAB_DOCS); });
            t2.Left = half + gap;
            stats.Controls.Add(t1); stats.Controls.Add(t2);
            p.Controls.Add(stats);

            // Catatan
            BuildHomeNotes(p, w);

            // Buka berkas: 4 pintasan sejajar
            p.Controls.Add(CreateSectionHeader("Buka berkas", "", w));
            var quick = new Panel { Width = w, Height = S(92), Margin = new Padding(0, S(8), 0, 0) };
            var qs = new (string icon, Color fg, Color bg, string label, Action act)[]
            {
                ("pdf", Theme.RED, Theme.RED_BG, "PDF", () => PickAndOpenFile(Ui.FilterPdf)),
                ("doc", Theme.SKY, Theme.SKY_BG, "Word", () => PickAndOpenFile("Word (*.docx;*.txt)|*.docx;*.txt")),
                ("sheet", Theme.TEAL, Theme.TEAL_BG, "Excel", () => PickAndOpenFile(Ui.FilterSheets)),
                ("folder", Theme.ACCENT, Theme.BLUE_BG, "Lainnya", OpenAnyMenu),
            };
            int qw = w / qs.Length;
            for (int i = 0; i < qs.Length; i++)
            {
                var q = qs[i];
                var b = new QuickOpen(q.icon, q.fg, q.label) { Bounds = new Rectangle(i * qw, 0, qw, S(92)) };
                b.Click += (s, e) => RunGuarded(q.act);
                quick.Controls.Add(b);
            }
            p.Controls.Add(quick);

            // Buat baru
            p.Controls.Add(CreateSectionHeader("Buat baru", "", w));
            p.Controls.Add(CreateListCard(w, new[]
            {
                Row(CardIcon.Word, Theme.SKY, Theme.SKY_BG, "Dokumen Word", "Halaman kosong, editor ala Word", () => OpenWord(DocEditorForm.TEMPLATE_BLANK)),
                Row(CardIcon.Excel, Theme.TEAL, Theme.TEAL_BG, "Spreadsheet", "Tabel & rumus seperti Excel", () => OpenSheet(SheetEditorForm.TEMPLATE_BLANK)),
                Row(CardIcon.Note, Theme.ACCENT, Theme.BLUE_BG, "Catatan jadi PDF", "Tulis teks, simpan sebagai PDF", QuickTextToPdfDialog),
                Row(CardIcon.Excel, Theme.TEAL, Theme.TEAL_BG, "Nota & Kuitansi Toko", "Buat kuitansi, hitung total & tempel stempel", InvoiceFlow),
                Row(CardIcon.Word, Theme.SKY, Theme.SKY_BG, "Katalog Produk & Portofolio", "Buat katalog jualan / portofolio PDF", CatalogFlow),
                Row(CardIcon.Note, Theme.ACCENT, Theme.BLUE_BG, "Jadwal & Perencana Tugas", "Template agenda kerja / To-Do List ke PDF/Excel", ScheduleFlow),
                Row(CardIcon.Note, Theme.ACCENT, Theme.BLUE_BG, "Ringkas Dokumen PDF / Word", "Ekstrak poin-poin utama & buat PDF ringkasan", SummarizerFlow),
                Row(CardIcon.Signature, Theme.SUCCESS, Theme.SUCCESS_BG, "Tanda tangan PDF", "Gambar tanda tangan, tempel ke PDF", SignExistingPdfFlow),
            }));

            // Template siap pakai (sampul)
            p.Controls.Add(CreateSectionHeader("Template siap pakai", "", w));
            var tpls = new List<(string icon, Color fg, string title, string sub, Action act)>
            {
                ("doc", Theme.SKY, "Surat Lamaran", "Format resmi HRD", () => OpenWord(DocEditorForm.TEMPLATE_SURAT)),
                ("user", Theme.SKY, "CV / Resume", "Riwayat hidup rapi", () => OpenWord(DocEditorForm.TEMPLATE_CV)),
            };
            foreach (var t in DocEditorForm.OrgTemplates)
                tpls.Add((t.Key switch { "hadir" => "grid", "undangan" => "mail", "notulen" => "note", _ => "doc" }, Theme.SKY, t.Title, t.Sub, () => OpenWord(t.Key)));
            tpls.Add(("sheet", Theme.TEAL, "Kuitansi / Invoice", "Nota & pembayaran", () => OpenSheet(SheetEditorForm.TEMPLATE_INVOICE)));
            tpls.Add(("sign", Theme.INDIGO, "TTD Transparan", "Foto TTD jadi PNG", ExtractTransparentSignatureFlow));
            int cw = S(132) + S(16), cgap = S(12);
            int perRow = Math.Max(1, (w + cgap) / (cw + cgap));
            cw = (w - cgap * (perRow - 1)) / perRow;
            var grid = new Panel { Width = w, Margin = new Padding(0, S(6), 0, S(8)) };
            int y = 0;
            for (int i = 0; i < tpls.Count; i++)
            {
                var t = tpls[i];
                var card = CoverCard(cw, t.icon, t.fg, null, t.title, t.sub, () => RunGuarded(t.act));
                card.Location = new Point((i % perRow) * (cw + cgap), y);
                grid.Controls.Add(card);
                if (i % perRow == perRow - 1 || i == tpls.Count - 1) y += card.Height + cgap;
            }
            grid.Height = Math.Max(0, y - cgap);
            p.Controls.Add(grid);
        }

        /// <summary>"Lainnya": berkas apa pun, atau dari link.</summary>
        private void OpenAnyMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Pilih berkas…", null, (_, _) => RunGuarded(PickAndOpenSupportedFile));
            m.Items.Add("Buka dari link…  (Ctrl+L)", null, (_, _) => RunGuarded(() => OpenFromLinkFlow(null)));
            m.Closed += (_, _) => BeginInvoke(new Action(m.Dispose));
            m.Show(Cursor.Position);
        }

        /// <summary>Ubin angka (Android statTile): angka besar + label; <paramref name="ink"/> = panel biru.</summary>
        private Control StatTile(int width, string value, string label, bool ink, string icon, Action onClick)
        {
            var fg = ink ? Color.White : Theme.TEXT;
            var card = new CardPanel { Width = width, Height = S(96), CornerRadius = 20, Cursor = Cursors.Hand };
            if (ink) { card.BackColor = Theme.ACCENT; card.BorderColor = Theme.ACCENT; }
            card.Controls.Add(L(value, Theme.FontDisplay(22f), fg, S(16), S(12), width - S(70), S(42)));
            var chip = new IconChip { Size = new Size(S(36), S(36)), Radius = S(18), Bg = ink ? Color.FromArgb(0x26, 255, 255, 255) : Theme.SOFT, Fg = fg, Icon = CardIcon.Folder, Location = new Point(width - S(16) - S(36), S(16)) };
            chip.Icon = icon == "download" ? CardIcon.Download : CardIcon.Folder;
            card.Controls.Add(chip);
            card.Controls.Add(L(label, Theme.FontBody(9.75f), ink ? Color.FromArgb(0xD9, 255, 255, 255) : Theme.MUTED, S(16), S(62), width - S(32), S(22), true));
            card.MakeAction(value + " " + label, () => RunGuarded(onClick));
            return card;
        }

        /// <summary>Kartu sampul (Android coverCard): wadah lembut dengan ikon kategori, judul &amp; keterangan di bawah.</summary>
        private Control CoverCard(int width, string icon, Color fg, string? badge, string title, string sub, Action onClick)
        {
            int coverH = (int)(width * 0.62f) - S(16);
            var card = new CardPanel { Width = width, CornerRadius = 20, Cursor = Cursors.Hand, Padding = Padding.Empty };
            var cover = new CoverArt(icon, fg, badge) { Bounds = new Rectangle(S(8), S(8), width - S(16), coverH) };
            card.Controls.Add(cover);
            card.Controls.Add(L(title, Theme.FontLabel(10.5f, FontStyle.Bold), Theme.TEXT, S(14), cover.Bottom + S(10), width - S(28), S(22), true));
            card.Controls.Add(L(sub, Theme.FontBody(9.75f), Theme.MUTED, S(14), cover.Bottom + S(32), width - S(28), S(20), true));
            card.Height = cover.Bottom + S(60);
            card.MakeAction(title + ". " + sub, onClick);
            return card;
        }

        private void OpenNotes()
        {
            using var f = new NotesForm();
            f.ShowDialog(this);
            RenderCurrentTab(preserveScroll: true);
        }

        private void OpenNote(long id, bool checklist = false)
        {
            NoteUi.Open(this, id, checklist);
            RenderCurrentTab(preserveScroll: true);
        }

        /// <summary>Korsel catatan Beranda (Android: kartu "Catatan baru" + 8 catatan terbaru).</summary>
        private void BuildHomeNotes(FlowLayoutPanel p, int w)
        {
            var notes = Notes.All();
            p.Controls.Add(CreateSectionHeader("Catatan", notes.Count > 0 ? "Semua" : "", w, notes.Count > 0 ? OpenNotes : null));
            if (notes.Count == 0)
            {
                p.Controls.Add(CreateListCard(w, new[]
                {
                    Row(CardIcon.Note, Theme.AMBER, Theme.AMBER_BG, "Tulis catatan", "Catatan & daftar centang, tersimpan offline di komputer ini", OpenNotes)
                }));
                return;
            }
            int gap = S(12);
            int cw = S(200);
            int cols = Math.Max(2, (w + gap) / (cw + gap));
            cw = (w - gap * (cols - 1)) / cols;
            var shown = notes.Take(cols - 1).ToList();
            var cards = new List<Control>();
            var add = new CardPanel { Width = cw, Cursor = Cursors.Hand, CornerRadius = 20 };
            add.Controls.Add(new IconChip { Size = new Size(S(40), S(40)), Radius = S(20), Bg = Theme.ACCENT, Fg = Color.White, Icon = CardIcon.Plus, Location = new Point(S(14), S(14)) });
            add.Controls.Add(L("Catatan baru", Theme.FontHead(11f), Theme.TEXT, S(14), S(66), cw - S(28), S(24), true));
            add.Controls.Add(L("Teks atau daftar centang", Theme.FontBody(9.5f), Theme.MUTED, S(14), S(90), cw - S(28), S(40)));
            add.MakeAction("Catatan baru", () => OpenNote(-1));
            cards.Add(add);
            foreach (var n in shown)
            {
                long id = n.Id;
                var card = new NoteCard(n, cw, lines: 6, minHeight: S(140));
                card.Click += (s, e) => RunGuarded(() => OpenNote(id));
                card.ContextMenuStrip = NoteUi.Menu(card, id, () => RenderCurrentTab(preserveScroll: true));
                cards.Add(card);
            }
            int h = Math.Min(S(230), Math.Max(S(140), cards.Max(c => c.Height)));
            var row = new Panel { Width = w, Height = h, Margin = new Padding(0, S(4), 0, S(6)) };
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].Height = h;
                cards[i].Location = new Point(i * (cw + gap), 0);
                row.Controls.Add(cards[i]);
            }
            p.Controls.Add(row);
        }

        private void TogglePinFile(string path)
        {
            bool on = AppStorage.TogglePin(path);
            Theme.Toast(this, on ? "Disematkan di Beranda" : "Dilepas dari Beranda");
            RenderCurrentTab(preserveScroll: true);
        }

        /// <summary>Menu klik kanan berkas (hasil olahan / hasil cari / kartu sematan).</summary>
        private ContextMenuStrip FileMenu(string path)
        {
            var m = new ContextMenuStrip();
            m.Opening += (s, e) =>
            {
                m.Items.Clear();
                m.Items.Add("Buka", null, (_, _) => OpenDocument(path));
                if (AppStorage.IsSupportedDocument(path)) m.Items.Add("Edit dokumen", null, (_, _) => EditDocument(path));
                m.Items.Add(AppStorage.IsPinned(path) ? "Lepas dari Beranda" : "Sematkan di Beranda", null, (_, _) => TogglePinFile(path));
                m.Items.Add("Salin untuk dibagikan", null, (_, _) => ShareFilePath(path));
                m.Items.Add("Tampilkan di Windows Explorer", null, (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } });
            };
            return m;
        }

        /// <summary>Deret "Disematkan" di Beranda (Android pinCard): klik = buka, klik kanan = opsi.</summary>
        private void BuildHomePins(FlowLayoutPanel p, int w)
        {
            var pins = AppStorage.Config.PinnedFiles.Where(File.Exists).ToList();
            if (pins.Count == 0) return;
            p.Controls.Add(CreateSectionHeader("Disematkan", $"{pins.Count} berkas", w));
            int gap = S(12);
            int cols = Math.Max(2, (w + gap) / (S(220) + gap));
            int cw = (w - gap * (cols - 1)) / cols;
            var grid = new Panel { Width = w, Margin = new Padding(0, S(4), 0, S(6)) };
            int y = 0;
            for (int i = 0; i < pins.Count; i++)
            {
                var path = pins[i];
                var name = Path.GetFileName(path);
                var (label, fg, bg) = AppStorage.TypeBadgeOf(AppStorage.MimeByName(name), name);
                var card = new CardPanel { Width = cw, Height = S(72), CornerRadius = 22, Cursor = Cursors.Hand };
                card.Controls.Add(new IconChip { Size = new Size(S(40), S(40)), Radius = S(12), Bg = bg, Fg = fg, Icon = IconForFileBadge(label, name), Location = new Point(S(14), S(16)) });
                card.Controls.Add(L(name, Theme.FontLabel(10f, FontStyle.Bold), Theme.TEXT, S(64), S(14), cw - S(78), S(22), true));
                string meta = label;
                try { meta += " • " + AppStorage.FmtSize(new FileInfo(path).Length); } catch { }
                card.Controls.Add(L("📌 " + meta, Theme.FontBody(9f), Theme.MUTED, S(64), S(38), cw - S(78), S(20), true));
                card.MakeAction(name, () => OpenDocument(path));
                card.ContextMenuStrip = FileMenu(path);
                foreach (Control c in card.Controls) c.ContextMenuStrip = card.ContextMenuStrip;
                card.Location = new Point((i % cols) * (cw + gap), y);
                grid.Controls.Add(card);
                if (i % cols == cols - 1 || i == pins.Count - 1) y += card.Height + gap;
            }
            grid.Height = y - gap;
            p.Controls.Add(grid);
        }

        // ---------------- cari ISI berkas (Android DocSearch): hasil bertambah selagi berjalan ----------------
        private FlowLayoutPanel? _contentHitsPanel;
        private CancellationTokenSource? _contentCts;
        private string _contentQuery = "";
        private readonly List<SearchHit> _contentHits = new();
        private bool _contentDone;

        private void AddContentHitsPanel(FlowLayoutPanel p, int w)
        {
            _contentHitsPanel = CreateResultList(w);
            p.Controls.Add(_contentHitsPanel);
            if (_searchQuery.Length >= 2 && _contentQuery == _searchQuery) ShowContentHits();
            else StartContentSearch();
        }

        private void StartContentSearch()
        {
            string q = _searchQuery;
            if (q == _contentQuery && _contentHitsPanel != null && (_contentDone || _contentCts != null)) { ShowContentHits(); return; }
            _contentCts?.Cancel();
            _contentCts = null;
            _contentQuery = q;
            _contentHits.Clear();
            _contentDone = false;
            if (q.Length < 2) { _contentDone = true; ShowContentHits(); return; }
            var cts = new CancellationTokenSource();
            _contentCts = cts;
            ShowContentHits();
            var token = cts.Token;
            var pending = new List<SearchHit>();
            var flush = new System.Windows.Forms.Timer { Interval = 200 };
            void Flush()
            {
                if (token.IsCancellationRequested || IsDisposed) return;
                lock (pending) { if (pending.Count == 0) return; _contentHits.AddRange(pending); pending.Clear(); }
                ShowContentHits();
            }
            flush.Tick += (s, e) => Flush();
            flush.Start();
            Task.Run(() => DocTools.Search(q, 80, 6, null, () => token.IsCancellationRequested,
                h => { lock (pending) pending.Add(h); }), token)
                .ContinueWith(_ =>
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(() =>
                    {
                        flush.Stop(); flush.Dispose();
                        if (token.IsCancellationRequested) return;
                        Flush();
                        _contentDone = true;
                        if (_contentCts == cts) _contentCts = null;
                        ShowContentHits();
                    }));
                }, TaskScheduler.Default);
        }

        private void ShowContentHits()
        {
            if (_contentHitsPanel is not { IsDisposed: false } panel) return;
            int width = panel.Width;
            if (_searchQuery.Length < 2)
            {
                ReplaceRows(panel, new Panel { Width = width, Height = 0, Margin = Padding.Empty });
                return;
            }
            var host = new Panel { Width = width, Margin = Padding.Empty };
            var hdr = CreateSectionHeader("Ditemukan di isi berkas", _contentDone ? $"{_contentHits.Count} berkas" : "Mencari…", width);
            hdr.Location = new Point(0, 0);
            host.Controls.Add(hdr);
            Control body;
            if (_contentHits.Count == 0)
                body = _contentDone
                    ? CreateEmptyState(width, CardIcon.Search, "Tidak ada di isi berkas", $"Kata “{_searchQuery}” tidak ditemukan di PDF, Word, Excel & PowerPoint terbaru.")
                    : CreateEmptyState(width, CardIcon.Search, "Mencari di isi berkas…", "Hasil dari berkas biasa tampil lebih dulu; PDF pindaian dibaca dengan OCR.");
            else
                body = CreateListCard(width, _contentHits.Take(40).Select(h =>
                {
                    var name = Path.GetFileName(h.FilePath);
                    var (label, fg, bg) = AppStorage.TypeBadgeOf(AppStorage.MimeByName(name), name);
                    var row = new ListRow
                    {
                        Icon = IconForFileBadge(label, name), IconFg = fg, IconBg = bg, Title = name,
                        Subtitle = (h.Where.Length > 0 ? h.Where + " • " : "") + "… " + h.Snippet.Trim() + " …"
                    };
                    var path = h.FilePath;
                    row.Click += (s, e) => OpenDocument(path);
                    row.ContextMenuStrip = FileMenu(path);
                    return row;
                }).ToList());
            body.Location = new Point(0, hdr.Bottom + S(6));
            host.Controls.Add(body);
            host.Height = body.Bottom;
            ReplaceRows(panel, host);
        }

        /// <summary>Baris berkas riwayat (nama, jenis, ukuran, waktu) + penanda & menu ⋯.</summary>
        private ListRow RecentRow(DocumentHistoryEntity e, bool withActions)
        {
            var (label, fg, bg) = AppStorage.TypeBadgeOf(e.FileType, e.FileName);
            long size = e.SizeBytes;
            long timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.LastOpenedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var row = new ListRow
            {
                Icon = IconForFileBadge(label, e.FileName),
                IconFg = fg,
                IconBg = bg,
                Title = e.FileName,
                Subtitle = (AppStorage.IsPinned(e.FilePath) ? "📌 " : "") + $"{label} • {AppStorage.FmtSize(size)} • {AppStorage.RelTime(timestamp)}",
                Chevron = !withActions
            };
            row.Click += (s, ev) => OpenDocument(e.FilePath);
            if (!withActions) return row;

            row.AccessoryWidth = S(96);
            var btnStar = Theme.CreateButton(e.IsBookmarked ? "★" : "☆", false, e.IsBookmarked ? Theme.AMBER_FILL : Theme.TEXT, S(36), S(40));
            btnStar.Padding = Padding.Empty;
            btnStar.AccessibleName = e.IsBookmarked ? $"Hapus penanda {e.FileName}" : $"Tandai {e.FileName}";
            btnStar.BackColor = e.IsBookmarked ? Theme.AMBER_BG : Theme.SURFACE2;
            btnStar.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnStar.Click += async (s, ev) =>
            {
                btnStar.Enabled = false;
                try
                {
                    bool bookmarked = await _historyService.SetBookmarkedAsync(e.FilePath, !e.IsBookmarked);
                    AppStorage.SetBookmark(e.FilePath, bookmarked);
                    e.IsBookmarked = bookmarked;
                    if (_docsMode == 1) ShowHistoryRows();
                    else if (!btnStar.IsDisposed)
                    {
                        btnStar.Text = bookmarked ? "★" : "☆";
                        btnStar.AccessibleName = bookmarked ? $"Hapus penanda {e.FileName}" : $"Tandai {e.FileName}";
                        btnStar.BackColor = bookmarked ? Theme.AMBER_BG : Theme.SURFACE2;
                        btnStar.ForeColor = bookmarked ? Theme.AMBER_FILL : Theme.TEXT;
                    }
                }
                catch (Exception ex)
                {
                    Theme.Toast(this, $"Penanda tidak dapat disimpan: {ex.Message}");
                }
                finally { if (!btnStar.IsDisposed) btnStar.Enabled = true; }
            };
            var btnMore = Theme.CreateButton("⋯", false, Theme.TEXT, S(36), S(40));
            btnMore.Padding = Padding.Empty;
            btnMore.AccessibleName = $"Tindakan untuk {e.FileName}";
            btnMore.Click += (s, ev) =>
            {
                var m = new ContextMenuStrip();
                m.Items.Add("Buka di Reader", null, (_, _) => OpenDocument(e.FilePath));
                m.Items.Add("Edit dokumen", null, (_, _) => EditDocument(e.FilePath));
                m.Items.Add(AppStorage.IsPinned(e.FilePath) ? "Lepas dari Beranda" : "Sematkan di Beranda", null, (_, _) => TogglePinFile(e.FilePath));
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
                m.Closed += (menu, args) => m.Dispose();
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
            seg.AccessibleName = "Kategori dokumen";
            _documentSegments = seg;
            seg.SelectedChanged += i =>
            {
                _docsMode = i;
                _listLimit = InitialListLimit;
                RenderCurrentTab();
                _documentSegments?.Focus();
            };
            p.Controls.Add(seg);

            if (_docsMode == 2)
            {
                if (!_reflowOnly || _outputSnapshot == null)
                    _outputSnapshot = AppStorage.GetOutFiles(includeImages: true);

                string[] categories = { "Semua", "PDF", "Word", "Excel", "Gambar", "Lainnya" };
                var counts = _outputSnapshot.GroupBy(OutputCategory).ToDictionary(group => group.Key, group => group.Count());
                p.Controls.Add(ChipRow(w, categories.Select(category =>
                {
                    int count = category == "Semua" ? _outputSnapshot.Count : counts.GetValueOrDefault(category);
                    return CreateChip($"{category} ({count})", _docsCat == category, () =>
                    {
                        _docsCat = category;
                        _listLimit = InitialListLimit;
                        _reflowOnly = true;
                        try { RenderCurrentTab(); }
                        finally { _reflowOnly = false; }
                    });
                }).ToArray()));

                _outputListPanel = CreateResultList(w);
                p.Controls.Add(_outputListPanel);
                ShowOutputRows();
                AddContentHitsPanel(p, w);

                var actions = new FlowLayoutPanel
                {
                    Width = w, AutoSize = true, WrapContents = true, MaximumSize = new Size(w, 0),
                    Margin = new Padding(0, S(12), 0, 0)
                };
                var openFolder = Theme.CreateButton("Buka di Explorer", false, Theme.TEXT, S(42), S(158));
                openFolder.Click += (s, e) => Process.Start(new ProcessStartInfo(AppStorage.OutDir) { UseShellExecute = true });
                var manage = Theme.CreateButton("Kelola folder", false, Theme.ACCENT, S(42), S(132));
                manage.Click += (s, e) => RunGuarded(() => ShowFolderExplorerDialog("SEMUA"));
                actions.Controls.AddRange(new Control[] { openFolder, manage });
                p.Controls.Add(actions);
                return;
            }

            _recentRowWidth = w;
            _recentListPanel = CreateResultList(w);
            p.Controls.Add(_recentListPanel);
            ShowHistoryRows();
            AddContentHitsPanel(p, w);

            var pick = Theme.CreateButton("Pilih berkas lain…", false, Theme.ACCENT, S(42), S(166));
            pick.Margin = new Padding(0, S(12), 0, 0);
            pick.Click += (s, e) => PickAndOpenSupportedFile();
            p.Controls.Add(pick);
        }

        private static string OutputCategory(FileInfo file) =>
            AppStorage.TypeBadgeOf(AppStorage.MimeByName(file.Name), file.Name).Label switch
            {
                "PDF" => "PDF",
                "DOCX" => "Word",
                "XLSX" => "Excel",
                "IMG" => "Gambar",
                _ => "Lainnya"
            };

        private const int InitialListLimit = 40;
        private int _listLimit = InitialListLimit;
        private Segmented? _documentSegments;

        private FlowLayoutPanel CreateResultList(int width) => new()
        {
            Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Margin = new Padding(0, S(6), 0, 0), BackColor = Theme.BG
        };

        private void ReplaceRows(FlowLayoutPanel panel, Control content, int count = 0, Action? showMore = null)
        {
            int scroll = _contentHost.ScrollOffset;
            panel.SuspendLayout();
            try
            {
                var previous = panel.Controls.Cast<Control>().ToArray();
                panel.Controls.Add(content);
                if (showMore != null && count > _listLimit)
                {
                    var more = Theme.CreateButton($"Tampilkan {Math.Min(InitialListLimit, count - _listLimit)} lainnya",
                        false, Theme.ACCENT, S(38), S(196));
                    more.Margin = new Padding(0, S(8), 0, 0);
                    more.Click += (s, e) => { _listLimit += InitialListLimit; showMore(); };
                    panel.Controls.Add(more);
                }
                foreach (var control in previous) control.Dispose();
            }
            finally
            {
                panel.ResumeLayout(true);
                if (scroll > 0) _contentHost.ScrollOffset = scroll;
            }
        }

        private bool MatchesQuery(string name) => name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase);

        private void ShowOutputRows()
        {
            if (_outputListPanel is not { IsDisposed: false } panel || _outputSnapshot == null) return;
            int width = GetContentWidth();
            var matches = _outputSnapshot.Where(file =>
                (_docsCat == "Semua" || OutputCategory(file) == _docsCat) && MatchesQuery(file.Name)).ToList();
            if (matches.Count == 0)
            {
                string title = _searchQuery.Length > 0 ? "Tidak ditemukan" : "Belum ada hasil";
                string message = _searchQuery.Length > 0 ? "Coba kata kunci lain atau pilih kategori Semua."
                    : "Berkas hasil olahan dalam kategori ini akan muncul di sini.";
                ReplaceRows(panel, CreateEmptyState(width, CardIcon.Folder, title, message));
                return;
            }
            var rows = matches.Take(_listLimit).Select(file =>
            {
                var (label, fg, bg) = AppStorage.TypeBadgeOf(AppStorage.MimeByName(file.Name), file.Name);
                var row = new ListRow
                {
                    Icon = IconForFileBadge(label, file.Name), IconFg = fg, IconBg = bg, Title = file.Name,
                    Subtitle = $"{label}  ·  {AppStorage.FmtSize(file.Length)}  ·  {file.LastWriteTime:dd MMM yyyy, HH:mm}"
                };
                row.Click += (s, e) =>
                {
                    if (AppStorage.IsSupportedDocument(file.FullName)) OpenDocument(file.FullName);
                    else Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true });
                };
                if (AppStorage.IsPinned(file.FullName)) row.Subtitle = "📌 " + row.Subtitle;
                row.ContextMenuStrip = FileMenu(file.FullName);
                return row;
            }).ToList();
            ReplaceRows(panel, CreateListCard(width, rows), matches.Count, ShowOutputRows);
        }

        private async Task RefreshHistoryAsync()
        {
            _recentLoadCts?.Cancel();
            _recentLoadCts?.Dispose();
            _recentLoadCts = new CancellationTokenSource();
            var token = _recentLoadCts.Token;
            try
            {
                var snapshot = await Task.Run(async () =>
                {
                    var entries = await _historyService.GetRecentAsync(cancellationToken: token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return entries.Where(entry => File.Exists(entry.FilePath)).ToList();
                }, token);
                if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                _historySnapshot = snapshot;
                _historyError = null;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || IsDisposed || Disposing) return;
                _historyError = ex.Message;
            }

            ShowHistoryRows();
            if (_homeRecentPanel is { IsDisposed: false } home) ShowHomeHistory(home.Width);
        }

        /// <summary>Korsel "Baru dibuka" (Android coverCard 156 dp): sampul berwarna jenis berkas + nama &amp; waktu.</summary>
        private Control RecentCovers(int width, IReadOnlyList<DocumentHistoryEntity> recent)
        {
            int gap = S(12);
            int perRow = Math.Max(2, (width + gap) / (S(156) + gap));
            int cw = (width - gap * (perRow - 1)) / perRow;
            var row = new Panel { Width = width, Margin = Padding.Empty };
            int x = 0, h = 0;
            foreach (var e in recent.Take(perRow))
            {
                var (label, fg, _) = AppStorage.TypeBadgeOf(e.FileType, e.FileName);
                string icon = label switch { "PDF" => "pdf", "DOCX" => "doc", "XLSX" => "sheet", "IMG" => "image", _ => "folder" };
                long ts = new DateTimeOffset(DateTime.SpecifyKind(e.LastOpenedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                var path = e.FilePath;
                var card = CoverCard(cw, icon, fg, label, e.FileName, AppStorage.RelTime(ts), () => OpenDocument(path));
                card.ContextMenuStrip = FileMenu(path);
                card.Location = new Point(x, 0);
                row.Controls.Add(card);
                x += cw + gap; h = Math.Max(h, card.Height);
            }
            row.Height = h;
            return row;
        }

        private void ShowHomeHistory(int width)
        {
            if (_homeRecentPanel is not { IsDisposed: false } panel) return;
            Control body = _historySnapshot switch
            {
                { Count: > 0 } recent => RecentCovers(width, recent),
                { Count: 0 } => new Panel { Width = width, Height = 0, Margin = Padding.Empty },   // Android: bagian disembunyikan bila kosong
                _ when _historyError != null => CreateEmptyState(width, CardIcon.Folder, "Riwayat tidak dapat dimuat", _historyError),
                _ => CreateEmptyState(width, CardIcon.Folder, "Memuat riwayat…", "")
            };
            ReplaceRows(panel, body);
            if (_homeRecentHeader is { IsDisposed: false } hdr) hdr.Visible = _historySnapshot is not { Count: 0 };
        }

        private Control? _homeRecentHeader;

        private void ShowHistoryRows()
        {
            if (_recentListPanel is not { IsDisposed: false } panel) return;
            int width = _recentRowWidth;
            if (_historySnapshot == null)
            {
                string message = _historyError == null ? "Memuat riwayat…" : "Riwayat tidak dapat dimuat";
                ReplaceRows(panel, CreateEmptyState(width, CardIcon.Folder, message, _historyError ?? ""));
                return;
            }

            var matches = _historySnapshot.Where(entry => MatchesQuery(entry.FileName) &&
                (_docsMode != 1 || entry.IsBookmarked)).ToList();
            if (matches.Count > 0)
            {
                ReplaceRows(panel, CreateListCard(width, matches.Take(_listLimit).Select(entry => RecentRow(entry, true)).ToList()),
                    matches.Count, ShowHistoryRows);
                return;
            }
            var (title, detail) = _searchQuery.Length > 0
                ? ("Tidak ditemukan", "Coba kata kunci lain atau periksa tab yang sedang dibuka.")
                : _docsMode == 1
                    ? ("Belum ada penanda", "Klik bintang di samping nama berkas pada tab Terkini.")
                    : ("Belum ada berkas", "Pilih berkas atau seret dokumen ke jendela ini.");
            ReplaceRows(panel, CreateEmptyState(width, CardIcon.Search, title, detail));
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

            p.Controls.Add(CreateSectionHeader("Catatan", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Catatan", "Teks & daftar centang, tersimpan di komputer ini", "Baru", Theme.AMBER, Theme.AMBER_BG, OpenNotes),
                tw => CreateFeatureTile(tw, "Daftar centang", "Belanja, tugas, persiapan — centang yang selesai", "", Theme.AMBER, Theme.AMBER_BG, () => OpenNote(-1, true))));

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
                tw => CreateFeatureTile(tw, "Hapus halaman kosong", "Periksa dan pilih halaman kosong sebelum menyimpan salinan", "", Theme.RED, Theme.RED_BG, RemoveBlankPagesFlow),
                tw => CreateFeatureTile(tw, "Nomor Halaman PDF", "Tambahkan Hal X dari Y & Header", "", Theme.RED, Theme.RED_BG, PageNumberFlow),
                tw => CreateFeatureTile(tw, "Kompres PDF Presisi", "Target < 200 KB / 500 KB / 1 MB", "CPNS", Theme.RED, Theme.RED_BG, CompressPdfFlow),
                tw => CreateFeatureTile(tw, "Ambil gambar", "Simpan foto di dalam PDF", "", Theme.RED, Theme.RED_BG, ExtractPdfImagesFlow),
                tw => CreateFeatureTile(tw, "Edit PDF", "Ubah teks, tambah gambar & tanda tangan", "Baru", Theme.RED, Theme.RED_BG, StartPdfEditorFlow),
                tw => CreateFeatureTile(tw, "Coret-coret PDF", "Tulis dengan pena, stabilo, catatan, dan tanda tangan", "", Theme.RED, Theme.RED_BG, () => StartPdfEditorFlow(annotate: true)),
                tw => CreateFeatureTile(tw, "Watermark PDF", "RAHASIA / DRAFT diagonal di tiap halaman", "", Theme.RED, Theme.RED_BG, WatermarkPdfFlow),
                tw => CreateFeatureTile(tw, "Gabung campuran", "PDF & foto jadi satu PDF", "", Theme.RED, Theme.RED_BG, UniversalMergeFlow)));

            p.Controls.Add(CreateSectionHeader("Konversi", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "PDF ke Word", "Ubah PDF ke .docx yang bisa diedit", "Baru", Theme.SKY, Theme.SKY_BG, PdfToWordFlow),
                tw => CreateFeatureTile(tw, "PDF bisa dicari", "Sisipkan teks OCR pada halaman hasil pindai", "", Theme.SKY, Theme.SKY_BG, SearchablePdfFlow),
                tw => CreateFeatureTile(tw, "Office ke PDF", "Ekspor Word, Excel, dan PowerPoint dari pratinjau", "", Theme.SKY, Theme.SKY_BG, OfficeToPdfFlow),
                tw => CreateFeatureTile(tw, "PDF ke JPG", "Simpan setiap halaman sebagai gambar", "", Theme.RED, Theme.RED_BG, PdfToJpgFlow),
                tw => CreateFeatureTile(tw, "Gambar ke PDF", "Gabung foto jadi satu PDF", "", Theme.SKY, Theme.SKY_BG, ImagesToSinglePdfFlow),
                tw => CreateFeatureTile(tw, "Konversi massal", "Banyak foto jadi PDF terpisah", "", Theme.SKY, Theme.SKY_BG, BatchImagesToPdfFlow),
                tw => CreateFeatureTile(tw, "EXE ke PDF", "Ambil PDF dari berkas .exe (PDF to EXE)", "", Theme.SKY, Theme.SKY_BG, ExeToPdfFlow),
                tw => CreateFeatureTile(tw, "Slideshow presentasi", "PPTX / PDF layar penuh, laser & timer", "", Theme.SKY, Theme.SKY_BG, SlideshowFlow)));

            p.Controls.Add(CreateSectionHeader("Gambar & foto", "", w));
            p.Controls.Add(CreateGrid(w, 240,
                tw => CreateFeatureTile(tw, "Pindai dokumen", "Ambil foto atau impor halaman untuk dipotong dan dirapikan", "", Theme.INDIGO, Theme.INDIGO_BG, () => StartScanFlow()),
                tw => CreateFeatureTile(tw, "Pindai struk", "Periksa hasil OCR lalu ekspor total dan rincian ke Excel", "", Theme.TEAL, Theme.TEAL_BG, () => StartScanFlow(receipt: true)),
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
                tw => CreateFeatureTile(tw, "Simpan salinan", "Salin berkas ke folder Salinan milik Depup Office", "", Theme.ACCENT, Theme.BLUE_BG, SaveDocumentCopyFlow),
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
            var outFiles = AppStorage.GetOutFiles(includeImages: true);

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

                int gy = seg.Bottom + S(22);
                tc.Controls.Add(L("Efek kaca", Theme.FontLabel(10.5f), Theme.TEXT, S(20), gy));
                tc.Controls.Add(L("Warna lembut pada sidebar dan toolbar, dengan efek Mica pada bilah judul di Windows 11.", Theme.FontBody(9.5f), Theme.MUTED, S(20), gy + S(24), cw - S(40), S(40)));
                var segGlass = new Segmented { Items = new[] { "Kaca", "Rata" }, Location = new Point(S(20), gy + S(70)), Width = cw - S(40) };
                segGlass.SelectedIndex = Theme.GlassOn ? 0 : 1;
                segGlass.SelectedChanged += i => Theme.SetGlass(i == 0);
                tc.Controls.Add(segGlass);

                // Ukuran tampilan (zoom antarmuka, seperti VS Code)
                int zy = segGlass.Bottom + S(22);
                tc.Controls.Add(L("Ukuran tampilan", Theme.FontLabel(10.5f), Theme.TEXT, S(20), zy));
                tc.Controls.Add(L("Perbesar / perkecil semua tombol & tulisan. Pintasan: Ctrl + , Ctrl − , Ctrl 0.", Theme.FontBody(9.5f), Theme.MUTED, S(20), zy + S(24), cw - S(40), S(40)));
                var bMinus = Theme.CreateButton("−", false, Theme.TEXT, S(42), S(52));
                bMinus.Font = Theme.FontHead(13f);
                bMinus.Location = new Point(S(20), zy + S(70));
                bMinus.Click += (s, e) => ChangeZoom(Theme.NextZoom(-1));
                var zVal = L($"{Math.Round(Theme.Zoom * 100)}%", Theme.FontHead(12f), Theme.TEXT, bMinus.Right + S(6), zy + S(70), S(70), S(42));
                zVal.TextAlign = ContentAlignment.MiddleCenter;
                var bPlus = Theme.CreateButton("+", false, Theme.TEXT, S(42), S(52));
                bPlus.Font = Theme.FontHead(13f);
                bPlus.Location = new Point(zVal.Right + S(6), zy + S(70));
                bPlus.Click += (s, e) => ChangeZoom(Theme.NextZoom(+1));
                var bReset = Theme.CreateButton("Reset 100%", false, Theme.ACCENT, S(42), S(130));
                bReset.Location = new Point(bPlus.Right + S(12), zy + S(70));
                bReset.Click += (s, e) => ChangeZoom(1f);
                tc.Controls.AddRange(new Control[] { bMinus, zVal, bPlus, bReset });
                tc.Height = bMinus.Bottom + S(20);
                Add(tc);

                var h2 = CreateSectionHeader("Format bawaan", "", cw); Add(h2); y += S(6);
                Add(CreateListCard(cw, new[]
                {
                    Row(CardIcon.Word, Theme.SKY, Theme.SKY_BG, "Dokumen Word", "Ukuran kertas, margin, spasi, dan jenis huruf", ShowWordDefaults),
                    Row(CardIcon.Excel, Theme.TEAL, Theme.TEAL_BG, "Spreadsheet Excel", "Garis kisi, mata uang, jumlah otomatis, dan pemisah CSV", ShowSheetDefaults),
                    Row(CardIcon.Pdf, Theme.RED, Theme.RED_BG, "Pembaca PDF", $"{GetReaderThemeName(AppStorage.Config.PdfReaderTheme)} · Zoom awal {AppStorage.Config.PdfDefaultZoom:P0}", ShowPdfDefaults),
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
            if (k.Contains("notulen") || k.Contains("catatan") || k.Contains("daftar centang")) return CardIcon.Edit;
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

    }
}
