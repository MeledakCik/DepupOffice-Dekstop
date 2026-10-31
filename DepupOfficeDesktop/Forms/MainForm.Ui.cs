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
    /// Lapisan tampilan jendela utama. Semua ukuran memakai Theme.S() (skala DPI) sehingga teks tidak lagi
    /// terpotong di layar 125% / 150% / 200%. Halaman dibangun ulang saat lebar area konten berubah.
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
            int sideW = S(248);

            // ---------- Sidebar ----------
            var sidebar = new Panel { Dock = DockStyle.Left, Width = sideW, BackColor = Theme.SURFACE };
            int innerW = sideW - S(32) - 1;

            var brand = new Panel { Dock = DockStyle.Top, Height = S(128), BackColor = Theme.SURFACE };
            brand.Controls.Add(L("Depup Office", Theme.FontHead(15f), Theme.ACCENT, S(24), S(24)));
            var btnEditPdf = Theme.CreateButton("✎  Edit PDF", true, Theme.ACCENT, S(44), innerW);
            btnEditPdf.Location = new Point(S(16), S(70));
            btnEditPdf.Click += (s, e) => StartPdfEditorFlow();
            brand.Controls.Add(btnEditPdf);

            // Menu utama (Dock=Top ditumpuk terbalik, jadi ditambahkan dari bawah ke atas)
            var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SURFACE, Padding = new Padding(S(16), S(4), S(16), 0) };
            string[] navNames = { "Baca", "Tools PDF", "Buat", "Simpan" };
            NavItemIcon[] navIcons = { NavItemIcon.Book, NavItemIcon.Pdf, NavItemIcon.Scan, NavItemIcon.Vault };
            for (int i = navNames.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var item = new NavItem { Caption = navNames[i], Icon = navIcons[i], Dock = DockStyle.Top, Height = S(48) };
                item.Click += (s, e) => SelectTab(idx);
                _navItems[idx] = item;
                nav.Controls.Add(item);
            }

            // Tautan bawah
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(4 * 42 + 20), BackColor = Theme.SURFACE, Padding = new Padding(S(16), S(8), S(16), S(12)) };
            string[] linkNames = { "Privacy Policy", "Kontak Developer", "Dukung Developer", "Tentang" };
            NavItemIcon[] linkIcons = { NavItemIcon.Privacy, NavItemIcon.Contact, NavItemIcon.Donate, NavItemIcon.About };
            Action[] linkActions =
            {
                () => OpenPrivacyPolicyForm(),
                () => PrivacyManager.OpenDeveloperEmail(),
                () => PrivacyManager.OpenSaweriaDonation(),
                () => ShowAboutDialog()
            };
            for (int i = linkNames.Length - 1; i >= 0; i--)
            {
                var act = linkActions[i];
                var item = new NavItem { Caption = linkNames[i], Icon = linkIcons[i], Dock = DockStyle.Top, Height = S(42) };
                item.Click += (s, e) => act();
                bottom.Controls.Add(item);
            }

            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(bottom);
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.LINE }); // garis pemisah

            // ---------- Area kanan: top bar + konten ----------
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG };
            _rightHost = right;
            var topBar = new Panel { Dock = DockStyle.Top, Height = S(76), BackColor = Theme.BG };

            _barTitleLbl = L(_tabNames[Math.Clamp(_currentTab, 0, 3)], Theme.FontHead(17f), Theme.TEXT, 0, 0);
            _searchBox = new SearchBox { Height = S(42) };
            _searchBox.Input.PlaceholderText = "Cari dokumen PDF, Word, Excel, CSV... (Ctrl+K)";
            _searchBox.Input.Text = _searchQuery;
            _searchBox.Input.TextChanged += (s, e) =>
            {
                CloseEmbeddedViewer();
                _searchQuery = _searchBox.Input.Text.Trim();
                if (_currentTab != 0)
                {
                    SelectTab(0);
                    return;
                }
                if (_recentListPanel != null)
                    FillRecentFilesBox(_recentListPanel, 10, _searchQuery, _recentRowWidth);
            };

            var (free, total) = AppStorage.GetDeviceStorage();
            var badge = new Pill { Text = $"Lokal: {AppStorage.FmtGb(total - free)} terpakai", Fg = Theme.ACCENT, Bg = Theme.BLUE_BG };
            var avatar = new Pill
            {
                AutoFit = false,
                Size = new Size(S(40), S(40)),
                Text = "D",
                Font = Theme.FontHead(11f),
                Fg = Color.White,
                Bg = Theme.ACCENT,
                Cursor = Cursors.Hand
            };
            avatar.Click += (s, e) => ShowAboutDialog();

            topBar.Controls.AddRange(new Control[] { _barTitleLbl, _searchBox, badge, avatar });

            _layoutTopBar = () =>
            {
                int padX = S(28);
                int h = topBar.ClientSize.Height;
                int wAll = topBar.ClientSize.Width;
                var ts = _barTitleLbl.GetPreferredSize(Size.Empty);
                avatar.Location = new Point(wAll - padX - avatar.Width, (h - avatar.Height) / 2);
                _barTitleLbl.Location = new Point(padX, (h - ts.Height) / 2);
                int searchLeft = _barTitleLbl.Left + ts.Width + S(32);
                int rightEdge = avatar.Left - S(16);
                badge.Location = new Point(Math.Max(0, rightEdge - badge.Width), (h - badge.Height) / 2);
                badge.Visible = rightEdge - searchLeft > S(560);
                if (badge.Visible)
                    rightEdge = badge.Left - S(16);
                int sw = Math.Max(S(120), Math.Min(S(520), rightEdge - searchLeft));
                _searchBox.SetBounds(searchLeft, (h - S(42)) / 2, sw, S(42));
            };
            topBar.Resize += (s, e) => _layoutTopBar?.Invoke();
            badge.SizeChanged += (s, e) => _layoutTopBar?.Invoke();
            topBar.HandleCreated += (s, e) => _layoutTopBar?.Invoke();
            _layoutTopBar();

            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(S(28), S(4), S(28), S(28)),
                BackColor = Theme.BG,
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
            Controls.Add(right);
            Controls.Add(sidebar);
        }

        /// <summary>Dipanggil saat DPI monitor berubah (mis. jendela dipindah ke layar lain): bangun ulang rangka & halaman.</summary>
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
            _currentTab = idx;
            _barTitleLbl.Text = _tabNames[Math.Clamp(idx, 0, _tabNames.Length - 1)];
            foreach (var (k, item) in _navItems)
                item.Active = k == idx;
            _layoutTopBar?.Invoke();
            RenderCurrentTab();
        }

        private int AvailableWidth()
        {
            int sb = _contentHost.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0;
            return Math.Max(1, _contentHost.ClientSize.Width + sb - _contentHost.Padding.Horizontal);
        }

        private int GetContentWidth()
        {
            // Sisakan ruang untuk scrollbar vertikal agar tidak muncul scrollbar horizontal.
            return Math.Max(S(320), Math.Min(S(1120), AvailableWidth() - S(18)));
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
                case 0: BuildBacaTab(page); break;
                case 1: BuildToolsTab(page); break;
                case 2: BuildBuatTab(page); break;
                default: BuildSimpanTab(page); break;
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

        // ================= KOMPONEN PEMBANTU UI =================

        private Control CreateHeading(int width, string eyebrow, string title, string? subtitle = null)
        {
            var panel = new Panel { Width = width, Height = S(subtitle == null ? 76 : 100), Margin = new Padding(0, S(6), 0, S(8)) };
            panel.Controls.Add(L(eyebrow, Theme.FontBody(9.5f), Theme.MUTED, 0, S(4)));
            panel.Controls.Add(L(title, Theme.FontHead(20f), Theme.TEXT, 0, S(26)));
            if (subtitle != null)
                panel.Controls.Add(L(subtitle, Theme.FontBody(10f), Theme.MUTED, 0, S(66), width, S(24), true));
            return panel;
        }

        private Control CreateSectionHeader(string title, string badgeOrLink, int width, Action? onLinkClick = null)
        {
            var panel = new Panel { Width = width, Height = S(36), Margin = new Padding(0, S(10), 0, S(4)) };
            var titleLbl = L(title, Theme.FontHead(13f), Theme.TEXT, 0, S(4));
            panel.Controls.Add(titleLbl);

            if (onLinkClick != null)
            {
                var link = L(badgeOrLink, Theme.FontLabel(9.5f), Theme.ACCENT, 0, S(9));
                link.Cursor = Cursors.Hand;
                link.Click += (s, e) => onLinkClick();
                link.Left = width - link.GetPreferredSize(Size.Empty).Width;
                panel.Controls.Add(link);
            }
            else if (!string.IsNullOrEmpty(badgeOrLink))
            {
                int tw = TextRenderer.MeasureText(title, titleLbl.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                panel.Controls.Add(L(badgeOrLink, Theme.FontLabel(9f), Theme.MUTED, tw + S(14), S(9)));
            }
            return panel;
        }

        private Control CreateFeatureTile(int width, string title, string sub, string tag, Color accent, Color accentBg, Action onClick)
        {
            int pad = S(16);
            int innerW = Math.Max(S(60), width - pad * 2);
            var subFont = Theme.FontBody(8.8f);
            int lineH = TextRenderer.MeasureText("Ag", subFont, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Height;
            int subH = TextRenderer.MeasureText(sub, subFont, new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
            subH = Math.Min(Math.Max(subH, lineH), lineH * 3);

            int chipSize = S(40);
            int titleY = pad + chipSize + S(10);
            int titleH = S(24);
            int subY = titleY + titleH + S(2);
            var card = new CardPanel { Width = width, Height = subY + subH + pad, Cursor = Cursors.Hand };

            var chip = new IconChip { Size = new Size(chipSize, chipSize), Radius = S(11), Bg = accent, Fg = Color.White, Icon = ResolveCardIcon(title), Location = new Point(pad, pad) };

            var lblTitle = L(title, Theme.FontHead(11f), Theme.TEXT, pad, titleY, innerW, titleH, true);
            lblTitle.Cursor = Cursors.Hand;
            var lblSub = L(sub, subFont, Theme.MUTED, pad, subY, innerW, subH);
            lblSub.Cursor = Cursors.Hand;

            card.Controls.AddRange(new Control[] { chip, lblTitle, lblSub });

            // Lencana tag: hanya tampil bila muat di sebelah kanan ikon (tile sempit tidak bertabrakan).
            if (!string.IsNullOrWhiteSpace(tag))
            {
                var pill = new Pill { Text = tag, Fg = accent, Bg = accentBg };
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
            foreach (Control c in card.Controls) c.Click += Fire;
            card.AttachHover();
            return card;
        }

        // ================= IKON KARTU (vektor, bukan teks/emoji) =================

        /// <summary>Pilih ikon vektor berdasarkan judul kartu. Urutan pengecekan penting (yang lebih spesifik dulu).</summary>
        private static CardIcon ResolveCardIcon(string title)
        {
            string k = title.ToLowerInvariant();
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

        /// <summary>Grid responsif: jumlah kolom menyesuaikan lebar, posisi dihitung eksplisit (tanpa FlowLayout AutoSize).</summary>
        private Control CreateGrid(int w, int minTile, params Func<int, Control>[] makers)
        {
            int gap = S(14);
            int cols = Math.Max(1, Math.Min(makers.Length, (w + gap) / (S(minTile) + gap)));
            int tw = (w - gap * (cols - 1)) / cols;
            int rowH = 0;
            var tiles = new List<Control>();
            foreach (var make in makers)
            {
                var c = make(tw);
                rowH = Math.Max(rowH, c.Height);
                tiles.Add(c);
            }
            foreach (var t in tiles) t.Height = rowH;
            int rows = (tiles.Count + cols - 1) / cols;
            var grid = new Panel { Width = w, Height = rows * rowH + (rows - 1) * gap, Margin = new Padding(0, S(4), 0, S(18)) };
            for (int i = 0; i < tiles.Count; i++)
            {
                tiles[i].Location = new Point((i % cols) * (tw + gap), (i / cols) * (rowH + gap));
                grid.Controls.Add(tiles[i]);
            }
            return grid;
        }

        private Control CreateTwoColumns(int w, Func<int, Control> left, Func<int, Control> right)
        {
            int gap = S(16);
            bool stacked = w < S(760);
            int cw = stacked ? w : (w - gap) / 2;
            var a = left(cw);
            var b = right(cw);
            var host = new Panel { Width = w, Margin = new Padding(0, S(4), 0, S(18)) };
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

        private PillButton CreateChip(string text, bool active, Action onClick)
        {
            var btn = (PillButton)Theme.CreateButton(text, active, Theme.ACCENT, S(38));
            btn.AutoSize = false;
            btn.CornerRadius = 20;
            btn.Width = TextRenderer.MeasureText(text, btn.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + S(40);
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

        // ================= TAB 0: BACA =================
        private void BuildBacaTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();

            p.Controls.Add(CreateHeading(w, Greeting(), "Ruang Baca Berkas"));

            // Kartu hero Editor PDF
            bool stacked = w < S(760);
            var hero = new CardPanel
            {
                Width = w,
                Height = stacked ? S(204) : S(148),
                GradientStart = Theme.ACCENT,
                GradientEnd = Theme.INDIGO,
                BorderThickness = 0,
                CornerRadius = 16,
                Margin = new Padding(0, 0, 0, S(20))
            };
            int btnW = S(150);
            int textW = stacked ? w - S(48) : w - S(48) - btnW - S(24);
            hero.Controls.Add(new Pill
            {
                Text = "EDITOR PDF • 100% OFFLINE",
                Fg = Color.White,
                Bg = Color.FromArgb(56, 255, 255, 255),
                Location = new Point(S(24), S(18))
            });
            hero.Controls.Add(L("Edit PDF Seperti di Word", Theme.FontHead(15f), Color.White, S(24), S(50), textW, S(30), true));
            hero.Controls.Add(L("Ubah teks asli, tambah teks baru, gambar & tanda tangan, sorot atau tutup bagian, lalu simpan sebagai PDF baru.",
                Theme.FontBody(9.5f), Color.FromArgb(235, 255, 255, 255), S(24), S(86), textW, S(44), true));
            var btnHero = Theme.CreateButton("Buka Editor", true, Color.White, S(42), btnW);
            btnHero.ForeColor = Theme.ACCENT;
            btnHero.Location = stacked ? new Point(S(24), S(150)) : new Point(w - btnW - S(24), (hero.Height - S(42)) / 2);
            btnHero.Click += (s, e) => StartPdfEditorFlow();
            hero.Controls.Add(btnHero);
            p.Controls.Add(hero);

            // Format dokumen
            var recent = GetHistorySnapshot().Where(entry => File.Exists(entry.FilePath)).ToList();
            int pdfCount = recent.Count(r => r.FileType == AppStorage.MIME_PDF);
            int docCount = recent.Count(r => AppStorage.TypeBadgeOf(r.FileType, r.FileName).Label == "DOCX");
            int xlsCount = recent.Count(r => AppStorage.TypeBadgeOf(r.FileType, r.FileName).Label == "XLSX");

            p.Controls.Add(CreateSectionHeader("Format Dokumen", "4 kategori", w));
            p.Controls.Add(CreateGrid(w, 200,
                tw => CreateFeatureTile(tw, "Buka PDF", "Baca, anotasi & tanda tangan", $"{pdfCount} file", Theme.RED, Theme.RED_BG, () => PickAndOpenFile("PDF Files (*.pdf)|*.pdf")),
                tw => CreateFeatureTile(tw, "Buka Word", "Edit .docx & .txt", $"{docCount} file", Theme.ACCENT, Theme.BLUE_BG, () => PickAndOpenFile("Word (*.docx)|*.docx")),
                tw => CreateFeatureTile(tw, "Buka Excel", "Edit .xlsx & .csv", $"{xlsCount} file", Theme.TEAL, Theme.TEAL_BG, () => PickAndOpenFile("Excel & CSV (*.xlsx;*.csv)|*.xlsx;*.csv")),
                tw => CreateFeatureTile(tw, "Semua Berkas", "Eksplor folder DepupOffice", $"{recent.Count} total", Theme.INDIGO, Theme.INDIGO_BG, () => ShowFolderExplorerDialog("SEMUA"))));

            // Fitur cepat
            p.Controls.Add(CreateSectionHeader("Fitur Cepat", "", w));
            p.Controls.Add(CreateGrid(w, 220,
                tw => CreateFeatureTile(tw, "Baca Lantang", "Text-to-Speech offline, ID & EN", "TTS", Theme.SKY, Theme.SKY_BG, PickAndOpenSupportedFile),
                tw => CreateFeatureTile(tw, "Cetak Langsung", "Cetak PDF atau gambar ke printer", "Print", Theme.ACCENT, Theme.BLUE_BG, QuickPrintFlow),
                tw => CreateFeatureTile(tw, "QR & Barcode", "Pindai, buat, dan stempel ke PDF", "QR", Theme.INDIGO, Theme.INDIGO_BG, ShowQrManagerDialog)));

            // Filter
            var chips = new Panel { Width = w, Height = S(46), Margin = new Padding(0, S(4), 0, S(4)) };
            var chipAll = CreateChip("Semua Terkini", _filterMode == 0, () => { _filterMode = 0; RenderCurrentTab(); });
            var chipStar = CreateChip("Ditandai (Bookmark)", _filterMode == 1, () => { _filterMode = 1; RenderCurrentTab(); });
            var chipVault = CreateChip("Brankas Pribadi AES-256", false, () => { using var vf = new VaultForm(); vf.ShowDialog(this); });
            int cx = 0;
            foreach (var c in new Control[] { chipAll, chipStar, chipVault })
            {
                c.Location = new Point(cx, S(4));
                cx += c.Width + S(10);
                chips.Controls.Add(c);
            }
            p.Controls.Add(chips);

            // File terakhir dibuka
            p.Controls.Add(CreateSectionHeader("File Terakhir Dibuka", "Pilih Berkas Lain...", w, PickAndOpenSupportedFile));
            _recentRowWidth = w;
            _recentListPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = w,
                Margin = new Padding(0, S(4), 0, 0),
                Padding = Padding.Empty,
                BackColor = Theme.BG
            };
            FillRecentFilesBox(_recentListPanel, 10, _searchQuery, w);
            p.Controls.Add(_recentListPanel);
        }

        private async void FillRecentFilesBox(FlowLayoutPanel box, int maxCount, string filter, int width)
        {
            _recentLoadCts?.Cancel();
            _recentLoadCts?.Dispose();
            _recentLoadCts = new CancellationTokenSource();
            var token = _recentLoadCts.Token;
            box.SuspendLayout();
            box.Controls.Clear();
            box.Controls.Add(L("Memuat riwayat berkas...", Theme.FontBody(10f), Theme.MUTED, 0, 0));
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
                    string msg = _filterMode == 1
                        ? "Belum ada file yang ditandai. Klik ikon bintang pada salah satu berkas untuk menandainya."
                        : (all.Count == 0
                            ? "Belum ada file terakhir dibuka. Buka atau buat dokumen PDF, Word, atau Excel untuk memulai."
                            : "Tidak ada berkas yang cocok dengan kata kunci pencarian.");
                    var empty = new CardPanel { Width = width, Height = S(76), Margin = new Padding(0, 0, 0, S(10)) };
                    empty.Controls.Add(L(msg, Theme.FontBody(10f), Theme.MUTED, S(20), S(14), width - S(40), S(48), true));
                    box.Controls.Add(empty);
                }
                else
                {
                    foreach (var entry in filtered)
                        box.Controls.Add(BuildRecentFileRow(entry, width));
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
                box.Controls.Add(L($"Riwayat tidak dapat dimuat: {ex.Message}", Theme.FontBody(10f), Theme.RED, 0, 0));
                box.ResumeLayout();
            }
        }

        private Control BuildRecentFileRow(DocumentHistoryEntity e, int width)
        {
            var (label, fg, bg) = AppStorage.TypeBadgeOf(e.FileType, e.FileName);
            long size = File.Exists(e.FilePath) ? new FileInfo(e.FilePath).Length : e.SizeBytes;
            long timestamp = new DateTimeOffset(DateTime.SpecifyKind(e.LastOpenedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            string meta = $"{AppStorage.FmtSize(size)} • Dibuka {AppStorage.RelTime(timestamp)}";

            var card = new CardPanel { Width = width, Height = S(72), Margin = new Padding(0, 0, 0, S(10)), Cursor = Cursors.Hand };

            var chip = new IconChip { Size = new Size(S(46), S(46)), Radius = S(12), Bg = bg, Fg = fg, Icon = IconForFileBadge(label, e.FileName), Location = new Point(S(16), S(13)) };
            int textLeft = S(78);
            int textW = Math.Max(S(80), width - textLeft - S(112));
            var lblName = L(e.FileName, Theme.FontLabel(10.5f), Theme.TEXT, textLeft, S(13), textW, S(22), true);
            var lblMeta = L(meta, Theme.FontBody(9f), Theme.MUTED, textLeft, S(38), textW, S(20), true);

            var btnStar = Theme.CreateButton(e.IsBookmarked ? "★" : "☆", e.IsBookmarked, Theme.ACCENT, S(36), S(40));
            btnStar.Location = new Point(width - S(100), S(18));
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

            var btnMore = Theme.CreateButton("...", false, Theme.TEXT, S(36), S(40));
            btnMore.BackColor = Theme.SURFACE2;
            btnMore.FlatAppearance.BorderColor = Theme.LINE;
            btnMore.Location = new Point(width - S(54), S(18));
            btnMore.Click += (s, ev) =>
            {
                var m = new ContextMenuStrip();
                m.Items.Add("Buka di Reader", null, (_, _) => OpenDocument(e.FilePath));
                m.Items.Add("Edit Dokumen", null, (_, _) => EditDocument(e.FilePath));
                m.Items.Add("Bagikan berkas", null, (_, _) => ShareFilePath(e.FilePath));
                m.Items.Add("Ubah Nama (Rename)", null, (_, _) => ShowRenameDialog(e.FilePath));
                m.Items.Add("Enkripsi ke Brankas AES-256", null, (_, _) => LockFileIntoVaultFlow(e.FilePath));
                m.Items.Add("Tampilkan di Windows Explorer", null, (_, _) =>
                {
                    try { Process.Start("explorer.exe", $"/select,\"{e.FilePath}\""); } catch { }
                });
                m.Items.Add("Hapus dari Riwayat", null, async (_, _) =>
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

            card.Controls.AddRange(new Control[] { chip, lblName, lblMeta, btnStar, btnMore });
            card.Click += (s, ev) => OpenDocument(e.FilePath);
            chip.Click += (s, ev) => OpenDocument(e.FilePath);
            lblName.Click += (s, ev) => OpenDocument(e.FilePath);
            lblMeta.Click += (s, ev) => OpenDocument(e.FilePath);
            card.AttachHover();
            return card;
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

            var files = new System.Collections.Specialized.StringCollection { filePath };
            Clipboard.SetFileDropList(files);
            Theme.Toast(this, "Berkas siap ditempel ke aplikasi tujuan");
        }

        // ================= TAB 1: TOOLS =================
        private void BuildToolsTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();

            // Zona seret & lepas
            var dz = new CardPanel
            {
                Width = w,
                Height = S(140),
                BackColor = ColorTranslator.FromHtml("#F8FBFF"),
                BorderColor = Theme.BLUE_LINE,
                BorderThickness = 2,
                DashedBorder = true,
                CornerRadius = 16,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, S(8), 0, S(16))
            };
            var dzTitle = L("Pilih atau Tarik Berkas", Theme.FontHead(13f), Theme.TEXT, 0, S(22), w, S(28));
            dzTitle.TextAlign = ContentAlignment.MiddleCenter;
            var dzSub = L("Klik untuk memilih berkas atau seret ke sini untuk memulai pengolahan instan", Theme.FontBody(9.5f), Theme.MUTED, 0, S(54), w, S(22));
            dzSub.TextAlign = ContentAlignment.MiddleCenter;
            dz.Controls.Add(dzTitle);
            dz.Controls.Add(dzSub);

            var dzPills = new List<Pill>();
            foreach (var t in new[] { "PDF", "JPG", "PNG", "Maks 50MB" })
                dzPills.Add(new Pill { Text = t, Fg = Theme.TEXT, Bg = Theme.SURFACE, Font = Theme.FontLabel(9f) });
            int pillGap = S(8);
            int totalPills = dzPills.Sum(x => x.Width) + pillGap * (dzPills.Count - 1);
            int px = Math.Max(0, (w - totalPills) / 2);
            foreach (var pill in dzPills)
            {
                pill.Location = new Point(px, S(90));
                px += pill.Width + pillGap;
                dz.Controls.Add(pill);
            }

            void OnDropZoneClick(object? s, EventArgs e) => RunGuarded(PickFileForQuickProcess);
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
            dz.Click += OnDropZoneClick;
            dzTitle.Click += OnDropZoneClick;
            dzSub.Click += OnDropZoneClick;
            p.Controls.Add(dz);

            // Proses terakhir
            if (AppStorage.Config.LastJob != null && File.Exists(AppStorage.Config.LastJob.FilePath))
            {
                var lj = AppStorage.Config.LastJob;
                var card = new CardPanel { Width = w, Height = S(96), Margin = new Padding(0, 0, 0, S(18)) };
                int leftW = Math.Max(S(120), w - S(268));
                card.Controls.Add(L($"Proses Terakhir: {lj.Title}", Theme.FontBody(9f), Theme.MUTED, S(20), S(14), leftW, S(18), true));
                card.Controls.Add(L(lj.FileName, Theme.FontHead(11.5f), Theme.TEXT, S(20), S(34), leftW, S(24), true));
                card.Controls.Add(new StackedStorageBar
                {
                    Parts = new List<(long, Color)> { (1, Theme.TEAL) },
                    Location = new Point(S(20), S(68)),
                    Size = new Size(leftW, S(8))
                });
                var done = L("Selesai 100%", Theme.FontLabel(9.5f), Theme.TEAL, w - S(250), S(36), S(112), S(22));
                done.TextAlign = ContentAlignment.MiddleRight;
                card.Controls.Add(done);
                var open = Theme.CreateButton("Buka Hasil", true, Theme.ACCENT, S(38), S(108));
                open.Location = new Point(w - S(20) - S(108), S(29));
                open.Click += (s, e) => OpenDocument(lj.FilePath);
                card.Controls.Add(open);
                p.Controls.Add(card);
            }

            p.Controls.Add(CreateSectionHeader("Peralatan Utama", "12 alat tersedia", w));
            p.Controls.Add(CreateGrid(w, 230,
                tw => CreateFeatureTile(tw, "Gabung PDF", "Satukan banyak PDF jadi satu, urutan bebas", "Populer", Theme.INDIGO, Theme.INDIGO_BG, MergePdfFlow),
                tw => CreateFeatureTile(tw, "Pisah PDF", "Pilah halaman atau pisahkan sesuai rentang", "Rentang", Theme.INDIGO, Theme.INDIGO_BG, SplitPdfFlow),
                tw => CreateFeatureTile(tw, "Susun & Hapus PDF", "Atur urutan, putar 90°, atau buang halaman", "Baru", Theme.ACCENT, Theme.BLUE_BG, OrganizePdfFlow),
                tw => CreateFeatureTile(tw, "Sedot Foto PDF", "Ekstrak seluruh gambar halaman PDF ke Documents\\DepupOffice\\Gambar", "JPG", Theme.INDIGO, Theme.INDIGO_BG, ExtractPdfImagesFlow),
                tw => CreateFeatureTile(tw, "Kompres PDF", "Kompres presisi <200KB, <500KB, <1MB (CPNS)", "Hemat 80%", Theme.TEAL, Theme.TEAL_BG, CompressPdfFlow),
                tw => CreateFeatureTile(tw, "Edit PDF", "Ubah teks, sisipkan gambar & tanda tangan seperti di Word", "Baru", Theme.ACCENT, Theme.BLUE_BG, StartPdfEditorFlow),
                tw => CreateFeatureTile(tw, "Konversi Massal", "Ubah banyak foto sekaligus jadi PDF terpisah", "Batch", Theme.INDIGO, Theme.INDIGO_BG, BatchImagesToPdfFlow),
                tw => CreateFeatureTile(tw, "Kompres Foto", "Kecilkan ukuran foto sebelum dikirim WA/Email", "Hemat", Theme.TEAL, Theme.TEAL_BG, CompressGalleryImagesFlow),
                tw => CreateFeatureTile(tw, "Pindai & Buat QR", "Pindai QR/Barcode dari gambar atau buat QR ISO", "QR ISO", Theme.TEAL, Theme.TEAL_BG, ShowQrManagerDialog),
                tw => CreateFeatureTile(tw, "Kunci Password", "Proteksi dokumen rahasia ke Brankas AES-256", "AES-256", Theme.INDIGO, Theme.INDIGO_BG, () =>
                {
                    using var ofd = new OpenFileDialog { Title = "Pilih Berkas untuk Dienkripsi ke Brankas", Filter = "Semua Berkas (*.*)|*.*" };
                    if (ofd.ShowDialog(this) == DialogResult.OK) LockFileIntoVaultFlow(ofd.FileName);
                }),
                tw => CreateFeatureTile(tw, "Gambar ke PDF", "Gabungkan banyak foto JPG/PNG menjadi 1 PDF", "Multi-Foto", Theme.ACCENT, Theme.BLUE_BG, ImagesToSinglePdfFlow),
                tw => CreateFeatureTile(tw, "TTD Transparan", "Hapus latar putih dari foto tanda tangan", "PNG Alpha", Theme.ACCENT, Theme.BLUE_BG, ExtractTransparentSignatureFlow)));

            p.Controls.Add(CreateSectionHeader("Pengaturan Format Dokumen", "4 modul", w));
            p.Controls.Add(CreateGrid(w, 230,
                tw => CreateFeatureTile(tw, "Pengaturan Word", "Ukuran A4/Letter/F4, margin & spasi", "DOCX", Theme.ACCENT, Theme.BLUE_BG, () => { using var f = new DocEditorForm(templateKey: DocEditorForm.TEMPLATE_BLANK); f.ShowDialog(this); }),
                tw => CreateFeatureTile(tw, "Pengaturan Excel", "Garis kisi, mata uang Rp/$/€ & rumus", "XLSX", Theme.TEAL, Theme.TEAL_BG, () => { using var f = new SheetEditorForm(templateKey: SheetEditorForm.TEMPLATE_BLANK); f.ShowDialog(this); }),
                tw => CreateFeatureTile(tw, "Tema Reader PDF", $"Tema aktif: {GetReaderThemeName(AppStorage.Config.PdfReaderTheme)}", "Eye-Care", Theme.RED, Theme.RED_BG, CycleDefaultReaderTheme),
                tw => CreateFeatureTile(tw, "Brankas Vault", "Kelola berkas terenkripsi AES-256-GCM", "Keamanan", Theme.INDIGO, Theme.INDIGO_BG, () => { using var vf = new VaultForm(); vf.ShowDialog(this); })));
        }

        private static string GetReaderThemeName(int t) => t switch { 1 => "Malam", 2 => "Sepia", 3 => "Eye-Care", _ => "Siang" };

        private void CycleDefaultReaderTheme()
        {
            AppStorage.Config.PdfReaderTheme = (AppStorage.Config.PdfReaderTheme + 1) % 4;
            AppStorage.SaveConfig();
            Theme.Toast(this, $"Tema standar Reader diubah ke: {GetReaderThemeName(AppStorage.Config.PdfReaderTheme)}");
            RenderCurrentTab();
        }

        // ================= TAB 2: BUAT =================
        private void BuildBuatTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();

            p.Controls.Add(CreateHeading(w, "Studio kreasi PDF & dokumen Office", "Buat Dokumen Baru",
                "Edit PDF seperti di Word, tulis surat/kontrak, olah tabel rumus di Excel, atau bubuhkan tanda tangan resmi."));

            // Hero editor PDF
            bool narrow = w < S(780);
            int btnW = S(270);
            var hero = new CardPanel
            {
                Width = w,
                Height = narrow ? S(318) : S(206),
                GradientStart = Theme.ACCENT,
                GradientEnd = Theme.INDIGO,
                BorderThickness = 0,
                CornerRadius = 16,
                Margin = new Padding(0, 0, 0, S(18))
            };
            hero.Controls.Add(new Pill
            {
                Text = "PALING POPULER • EDITOR PDF OFFLINE",
                Fg = Theme.TEAL,
                Bg = ColorTranslator.FromHtml("#CCFBF1"),
                Location = new Point(S(24), S(18))
            });
            hero.Controls.Add(L("Edit PDF Seperti di Word", Theme.FontHead(15f), Color.White, S(24), S(50), w - S(48), S(30), true));

            int frameW = narrow ? w - S(48) : Math.Min(S(480), w - S(48) - btnW - S(24));
            hero.Controls.Add(L("Klik teks asli untuk mengubahnya, tambah teks baru, gambar, tanda tangan, sorotan, atau tutup bagian yang ingin dihapus.", Theme.FontBody(9.5f), Color.FromArgb(235, 255, 255, 255), S(24), S(90), Math.Max(S(160), frameW), S(70), true));

            var btnStart = Theme.CreateButton("Buka Editor PDF", true, Color.White, S(44), narrow ? Math.Min(btnW, w - S(48)) : btnW);
            btnStart.ForeColor = Theme.ACCENT;
            btnStart.Click += (s, e) => StartPdfEditorFlow();
            var btnImport = Theme.CreateButton("Buat PDF dari Teks", false, Color.White, S(42), narrow ? Math.Min(btnW, w - S(48)) : btnW);
            btnImport.BackColor = Color.FromArgb(255, 255, 255);
            btnImport.ForeColor = Theme.ACCENT;
            btnImport.Click += (s, e) => QuickTextToPdfDialog();
            if (narrow)
            {
                btnStart.Location = new Point(S(24), S(196));
                btnImport.Location = new Point(S(24), S(252));
            }
            else
            {
                btnStart.Location = new Point(w - btnW - S(24), S(86));
                btnImport.Location = new Point(w - btnW - S(24), S(142));
            }
            hero.Controls.Add(btnStart);
            hero.Controls.Add(btnImport);
            p.Controls.Add(hero);

            void OpenWord(string tpl) { using var f = new DocEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }
            void OpenSheet(string tpl) { using var f = new SheetEditorForm(templateKey: tpl); f.ShowDialog(this); RenderCurrentTab(); }

            p.Controls.Add(CreateSectionHeader("Editor Office & Template Siap Pakai", "6 pilihan", w));
            p.Controls.Add(CreateGrid(w, 230,
                tw => CreateFeatureTile(tw, "Buat Word Baru", "Editor .docx interaktif (A4/Letter/F4)", "Word .docx", Theme.ACCENT, Theme.BLUE_BG, () => OpenWord(DocEditorForm.TEMPLATE_BLANK)),
                tw => CreateFeatureTile(tw, "Buat Excel Baru", "Spreadsheet interaktif & rumus =SUM", "Excel .xlsx", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_BLANK)),
                tw => CreateFeatureTile(tw, "Surat Lamaran Kerja", "Template resmi HRD siap edit & simpan", "Siap Kerja", Theme.ACCENT, Theme.BLUE_BG, () => OpenWord(DocEditorForm.TEMPLATE_SURAT)),
                tw => CreateFeatureTile(tw, "CV / Resume", "Template Curriculum Vitae terstruktur", "Karir", Theme.INDIGO, Theme.INDIGO_BG, () => OpenWord(DocEditorForm.TEMPLATE_CV)),
                tw => CreateFeatureTile(tw, "Kuitansi / Invoice", "Format pembayaran & nota penjualan", "Keuangan", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_INVOICE)),
                tw => CreateFeatureTile(tw, "Daftar Hadir", "Template rekap kehadiran karyawan", "HR / Admin", Theme.TEAL, Theme.TEAL_BG, () => OpenSheet(SheetEditorForm.TEMPLATE_PRESENSI))));

            Control ActionCard(int cw, string title, string desc, string btnText, Color color, Action act)
            {
                var card = new CardPanel { Width = cw, Height = S(156) };

                // Icon chip untuk action card
                var iconChip = new IconChip { Size = new Size(S(40), S(40)), Radius = S(11), Bg = color, Fg = Color.White, Icon = ResolveCardIcon(title), Location = new Point(S(18), S(12)) };
                card.Controls.Add(iconChip);

                card.Controls.Add(L(title, Theme.FontHead(12f), Theme.TEXT, S(66), S(14), cw - S(84), S(24), true));
                card.Controls.Add(L(desc, Theme.FontBody(9.5f), Theme.MUTED, S(18), S(46), cw - S(36), S(40), true));
                var b = Theme.CreateButton(btnText, true, color, S(40), cw - S(36));
                b.Location = new Point(S(18), S(98));
                b.Click += (s, e) => act();
                card.Controls.Add(b);
                return card;
            }

            p.Controls.Add(CreateTwoColumns(w,
                cw => ActionCard(cw, "Tulis Teks Cepat Jadi PDF", "Editor memo cepat, otomatis ditata ke halaman A4 multi-halaman rapi.", "Buat Dokumen Teks ke PDF", Theme.INDIGO, QuickTextToPdfDialog),
                cw => ActionCard(cw, "Tanda Tangan Digital PDF", "Goreskan tanda tangan dengan mouse/pena lalu tempel ke halaman PDF.", "Pilih PDF & Bubuhkan Tanda Tangan", Theme.TEAL, SignExistingPdfFlow)));
        }

        // ================= TAB 3: SIMPAN =================
        private void BuildSimpanTab(FlowLayoutPanel p)
        {
            int w = GetContentWidth();
            var vaultFiles = VaultManager.ListEncryptedFiles();
            var outFiles = AppStorage.GetOutFiles();
            Color amber = Theme.AMBER;
            Color amberLine = ColorTranslator.FromHtml("#FDE68A");

            p.Controls.Add(CreateHeading(w, "Offline • berkas tidak pernah diunggah ke cloud", "Penyimpanan & Keamanan"));

            // Brankas
            var vault = new CardPanel
            {
                Width = w,
                Height = S(172),
                BackColor = ColorTranslator.FromHtml("#FFFBEB"),
                BorderColor = amberLine,
                CornerRadius = 16,
                Margin = new Padding(0, 0, 0, S(16))
            };
            vault.Controls.Add(new IconChip { Size = new Size(S(48), S(48)), Radius = S(14), Bg = amber, Fg = Color.White, Icon = CardIcon.Vault, Location = new Point(S(24), S(22)) });
            int vx = S(88);
            var cnt = new Pill { Text = $"{vaultFiles.Count} berkas terkunci", Fg = amber, Bg = Theme.AMBER_BG };
            cnt.Location = new Point(w - S(24) - cnt.Width, S(26));
            vault.Controls.Add(cnt);
            vault.Controls.Add(L("Brankas Terenkripsi AES-256", Theme.FontHead(14f), Theme.TEXT, vx, S(22), Math.Max(S(100), cnt.Left - vx - S(12)), S(28), true));
            vault.Controls.Add(L("Simpan KTP, ijazah, sertifikat & dokumen finansial dengan enkripsi AES-256-GCM + Windows DPAPI. Dikunci dengan PIN pribadi Anda dan 100% tersimpan lokal.",
                Theme.FontBody(9.5f), Theme.MUTED, vx, S(54), w - vx - S(24), S(48), true));
            var btnVault = Theme.CreateButton("Buka Brankas", true, amber, S(42), S(190));
            btnVault.Location = new Point(vx, S(112));
            btnVault.Click += (s, e) => { using var vf = new VaultForm(); vf.ShowDialog(this); RenderCurrentTab(); };
            vault.Controls.Add(btnVault);
            p.Controls.Add(vault);

            // Dokumen terproteksi
            p.Controls.Add(CreateSectionHeader("Dokumen Terproteksi", $"{vaultFiles.Count} berkas", w));
            if (vaultFiles.Count == 0)
            {
                var empty = new CardPanel { Width = w, Height = S(72), Margin = new Padding(0, 0, 0, S(16)) };
                empty.Controls.Add(L("Belum ada berkas di brankas. Gunakan Tools > Kunci Password untuk menambahkan dokumen rahasia.",
                    Theme.FontBody(10f), Theme.MUTED, S(20), S(14), w - S(40), S(44), true));
                p.Controls.Add(empty);
            }
            else
            {
                foreach (var f in vaultFiles.Take(5))
                {
                    var row = new CardPanel { Width = w, Height = S(68), Margin = new Padding(0, 0, 0, S(10)) };
                    row.Controls.Add(new IconChip { Size = new Size(S(44), S(44)), Radius = S(12), Bg = Theme.AMBER_BG, Fg = amber, Icon = CardIcon.Lock, Location = new Point(S(16), S(12)) });
                    var lock1 = new Pill { Text = "Terkunci", Fg = ColorTranslator.FromHtml("#92400E"), Bg = Theme.AMBER_BG };
                    lock1.Location = new Point(w - S(16) - lock1.Width, (S(68) - lock1.Height) / 2);
                    int tw = Math.Max(S(80), lock1.Left - S(80) - S(12));
                    row.Controls.Add(lock1);
                    row.Controls.Add(L(VaultManager.DisplayName(f), Theme.FontLabel(10.5f), Theme.TEXT, S(76), S(12), tw, S(22), true));
                    row.Controls.Add(L($"Dienkripsi • {AppStorage.FmtSize(f.Length)}", Theme.FontBody(9f), Theme.MUTED, S(76), S(36), tw, S(20), true));
                    p.Controls.Add(row);
                }
                p.Controls.Add(new Panel { Width = w, Height = S(6) });
            }

            // Penyimpanan
            long vaultSize = AppStorage.GetDirectorySize(AppStorage.VaultDir);
            long outSize = AppStorage.GetDirectorySize(AppStorage.OutDir);
            var (free, total) = AppStorage.GetDeviceStorage();

            var st = new CardPanel { Width = w, Height = S(128), Margin = new Padding(0, 0, 0, S(16)) };
            st.Controls.Add(L($"Penyimpanan DepupOffice Terpakai: {AppStorage.FmtSize(vaultSize + outSize)}", Theme.FontHead(12f), Theme.TEXT, S(20), S(16), w - S(40) - S(220), S(26), true));
            var remain = L($"Tersisa {AppStorage.FmtGb(free)} dari {AppStorage.FmtGb(total)}", Theme.FontBody(9.5f), Theme.MUTED, w - S(20) - S(210), S(19), S(210), S(22));
            remain.TextAlign = ContentAlignment.MiddleRight;
            st.Controls.Add(remain);
            st.Controls.Add(new StackedStorageBar
            {
                Location = new Point(S(20), S(54)),
                Size = new Size(w - S(40), S(10)),
                Parts = new List<(long, Color)> { (Math.Max(1024, outSize), Theme.ACCENT), (Math.Max(512, vaultSize), Theme.INDIGO) }
            });
            int lx = S(20);
            foreach (var (text, col) in new[]
            {
                ($"Hasil olahan: {AppStorage.FmtSize(outSize)}", Theme.ACCENT),
                ($"Brankas: {AppStorage.FmtSize(vaultSize)}", Theme.INDIGO),
                ($"Sisa disk: {AppStorage.FmtGb(free)}", Theme.MUTED)
            })
            {
                st.Controls.Add(L("●", Theme.FontBody(9f), col, lx, S(82)));
                lx += S(18);
                var lbl = L(text, Theme.FontBody(9.5f), Theme.TEXT, lx, S(82));
                st.Controls.Add(lbl);
                lx += TextRenderer.MeasureText(text, lbl.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + S(30);
            }
            p.Controls.Add(st);

            // Manajer folder (4 kategori sebaris; 2x2 bila sempit)
            int cgap = S(10);
            int cw4 = (w - S(40) - cgap * 3) / 4;
            bool catNarrow = cw4 < S(124);
            int catCols = catNarrow ? 2 : 4;
            int catW = (w - S(40) - cgap * (catCols - 1)) / catCols;
            int catRows = catNarrow ? 2 : 1;
            int catH = S(44);
            int catBottom = S(78) + catRows * catH + (catRows - 1) * cgap;
            bool stackActions = (w - S(40) - cgap) / 2 < S(190);
            int actH = S(42);
            int folderH = catBottom + S(14) + (stackActions ? actH * 2 + cgap : actH) + S(20);
            var folder = new CardPanel { Width = w, Height = folderH, Margin = new Padding(0, 0, 0, S(16)) };
            folder.Controls.Add(L("Manajer Folder DepupOffice", Theme.FontHead(13f), Theme.TEXT, S(20), S(16), w - S(40), S(26), true));
            folder.Controls.Add(L($"{AppStorage.OutDir} • {outFiles.Count} berkas tersimpan", Theme.FontBody(9.5f), Theme.MUTED, S(20), S(44), w - S(40), S(22), true));

            int Count(string label) => outFiles.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == label);
            var cats = new (string Name, string Key, Color Fg, Color Bg)[]
            {
                ("PDF", "PDF", Theme.RED, Theme.RED_BG),
                ("Word", "DOCX", Theme.ACCENT, Theme.BLUE_BG),
                ("Excel", "XLSX", Theme.TEAL, Theme.TEAL_BG),
                ("Gambar", "IMG", Theme.SKY, Theme.SKY_BG)
            };
            for (int i = 0; i < cats.Length; i++)
            {
                var c = cats[i];
                string name = c.Name;
                var b = Theme.CreateButton($"{name} ({Count(c.Key)})", false, c.Fg, catH, catW);
                b.BackColor = c.Bg;
                b.FlatAppearance.BorderSize = 0;
                b.Location = new Point(S(20) + (i % catCols) * (catW + cgap), S(78) + (i / catCols) * (catH + cgap));
                b.Click += (s, e) => RunGuarded(() => ShowFolderExplorerDialog(name));
                folder.Controls.Add(b);
            }
            int half = stackActions ? w - S(40) : (w - S(40) - cgap) / 2;
            int actY = catBottom + S(14);
            var btnAll = Theme.CreateButton("Eksplor Isi Folder", true, Theme.ACCENT, actH, half);
            btnAll.Location = new Point(S(20), actY);
            btnAll.Click += (s, e) => RunGuarded(() => ShowFolderExplorerDialog("SEMUA"));
            var btnWin = Theme.CreateButton("Buka di Windows Explorer", false, Theme.TEXT, actH, half);
            btnWin.BackColor = Theme.SURFACE2;
            btnWin.FlatAppearance.BorderColor = Theme.LINE;
            btnWin.Location = stackActions ? new Point(S(20), actY + actH + cgap) : new Point(S(20) + half + cgap, actY);
            btnWin.Click += (s, e) => { try { Process.Start("explorer.exe", AppStorage.OutDir); } catch { } };
            folder.Controls.Add(btnAll);
            folder.Controls.Add(btnWin);
            p.Controls.Add(folder);

            // Privasi, dukungan & kontak (3 tombol sebaris; menumpuk bila sempit)
            int bw3 = (w - S(40) - cgap * 2) / 3;
            bool stackComp = bw3 < S(210);
            int bh = S(42);
            int compH = stackComp ? S(106) + bh * 3 + cgap * 2 + S(20) : S(162);
            var comp = new CardPanel { Width = w, Height = compH, Margin = new Padding(0, 0, 0, S(16)) };
            comp.Controls.Add(L("Kebijakan Privasi, Dukungan & Kontak Developer", Theme.FontHead(13f), Theme.TEXT, S(20), S(16), w - S(40), S(26), true));
            comp.Controls.Add(L(PrivacyManager.ComplianceSummaryText, Theme.FontBody(9.5f), Theme.MUTED, S(20), S(46), w - S(40), S(46), true));
            int bw = stackComp ? w - S(40) : bw3;
            Point BtnPos(int i) => stackComp ? new Point(S(20), S(106) + i * (bh + cgap)) : new Point(S(20) + i * (bw + cgap), S(106));
            var b1 = Theme.CreateButton("Privacy Policy", true, Theme.ACCENT, bh, bw);
            b1.Location = BtnPos(0);
            b1.Click += (s, e) => RunGuarded(OpenPrivacyPolicyForm);
            var b2 = Theme.CreateButton("Dukung Developer (Saweria)", true, Theme.RED, bh, bw);
            b2.Location = BtnPos(1);
            b2.Click += (s, e) => PrivacyManager.OpenSaweriaDonation();
            var b3 = Theme.CreateButton("Kontak Developer (Email)", false, Theme.TEAL, bh, bw);
            b3.Location = BtnPos(2);
            b3.Click += (s, e) => PrivacyManager.OpenDeveloperEmail();
            comp.Controls.AddRange(new Control[] { b1, b2, b3 });
            p.Controls.Add(comp);
        }
    }
}
