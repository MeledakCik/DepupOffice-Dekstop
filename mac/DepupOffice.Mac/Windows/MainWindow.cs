using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;

namespace DepupOffice.Mac.Windows;

/// <summary>
/// Jendela utama (port dari MainForm + MainForm.Ui): sidebar, halaman Baca, Tools, Buat, Simpan, dan Brankas.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Border _host = new();
    private readonly TextBlock _pageTitle = H1("Baca");
    private readonly TextBlock _pageSub = Caption("", false);
    private readonly StackPanel _nav = Col(4);
    private readonly Dictionary<string, Border> _navItems = new();
    private readonly List<string> _pendingOpen = new();
    private Border? _recentHost;
    private string _page = "baca";
    private string _filter = "all";
    private string _search = "";
    private bool _opened;

    public MainWindow()
    {
        Title = "Depup Office";
        Width = 1200;
        Height = 800;
        MinWidth = 940;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Bg);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        grid.Children.Add(BuildSidebar());

        var main = new DockPanel();
        var top = new Border { Padding = new Thickness(30, 24, 30, 12), Child = Col(3, _pageTitle, _pageSub) };
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(30, 4, 30, 30), Child = _host },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        Grid.SetColumn(main, 1);
        grid.Children.Add(main);
        Content = grid;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            _opened = true;
            foreach (var p in _pendingOpen.ToList()) OpenDocument(p);
            _pendingOpen.Clear();
        };
        Closing += (_, _) => LockVault();

        Navigate("baca");
    }

    // ================= Sidebar & navigasi =================
    private Control BuildSidebar()
    {
        var logo = Row(10,
            Logo(38),
            Col(0, T("Depup Office", 16, Text, FontWeight.Bold), Caption("v4.4.3 • macOS • offline", false)));

        var openBtn = Btn("＋  Buka Berkas", () => _ = OpenPicker(), true, tip: "⌘O");
        openBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        openBtn.HorizontalContentAlignment = HorizontalAlignment.Center;
        openBtn.Padding = new Thickness(14, 10);

        AddNav("baca", "📖", "Baca");
        AddNav("tools", "🧰", "Tools PDF");
        AddNav("buat", "✏️", "Buat");
        AddNav("simpan", "💾", "Simpan");
        AddNav("brankas", "🔒", "Brankas");

        var links = Col(2,
            LinkItem("🛡", "Kebijakan Privasi", () => _ = ShowPrivacy()),
            LinkItem("❤", "Dukung Developer", () => MacSystem.Open(PrivacyManager.OfficialSaweriaUrl)),
            LinkItem("✉", "Kontak Developer", () => MacSystem.Open(PrivacyManager.DeveloperMailto)),
            LinkItem("ℹ", "Tentang", () => _ = ShowAbout()));

        var dock = new DockPanel { Margin = new Thickness(16, 22, 16, 16) };
        var topPart = Col(18, logo, openBtn, _nav);
        DockPanel.SetDock(topPart, Dock.Top);
        DockPanel.SetDock(links, Dock.Bottom);
        dock.Children.Add(topPart);
        dock.Children.Add(links);
        dock.Children.Add(new Border());

        return new Border
        {
            Background = B(Surface),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = dock,
        };
    }

    private void AddNav(string key, string icon, string label)
    {
        var item = new Border
        {
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Child = Row(12, Icon(icon, 20, Text), T(label, 14, Text, FontWeight.Medium)),
        };
        item.Tapped += (_, _) => Navigate(key);
        item.PointerEntered += (_, _) => { if (_page != key) item.Background = B(Surface2); };
        item.PointerExited += (_, _) => { if (_page != key) item.Background = Brushes.Transparent; };
        _navItems[key] = item;
        _nav.Children.Add(item);
    }

    private static Border LinkItem(string icon, string label, Action onClick)
    {
        var item = new Border
        {
            Padding = new Thickness(10, 6),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
            Child = Row(10, Icon(icon, 16, Muted), T(label, 12.5, Muted)),
        };
        item.Tapped += (_, _) => onClick();
        item.PointerEntered += (_, _) => item.Background = B(Surface2);
        item.PointerExited += (_, _) => item.Background = Brushes.Transparent;
        return item;
    }

    public void Navigate(string key)
    {
        if (_page == "brankas" && key != "brankas") LockVault();
        _page = key;
        foreach (var (k, b) in _navItems)
        {
            bool active = k == key;
            b.Background = active ? B(BlueBg) : Brushes.Transparent;
            if (b.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock label)
            {
                label.Foreground = active ? B(Accent) : B(Text);
                label.FontWeight = active ? FontWeight.SemiBold : FontWeight.Medium;
                if (sp.Children[0] is Viewbox { Child: Canvas cv })
                    foreach (var path in cv.Children.OfType<Avalonia.Controls.Shapes.Path>()) path.Fill = active ? B(Accent) : B(Text);
            }
        }

        (_pageTitle.Text, _pageSub.Text) = key switch
        {
            "tools" => ("Tools PDF & Dokumen", "Gabung, pisah, kompres, kunci, putar, susun halaman, OCR, dan QR — semuanya diproses di Mac ini."),
            "buat" => ("Buat Dokumen", "Mulai dari templat Word, spreadsheet, atau kode QR."),
            "simpan" => ("Simpan & Kelola", "Berkas hasil Depup Office di folder Documents/DepupOffice."),
            "brankas" => ("Brankas Pribadi", "Berkas terenkripsi AES-256-GCM, dikunci dengan PIN."),
            _ => ("Baca", "Buka PDF, Word, Excel, CSV, teks, dan gambar."),
        };

        _host.Child = key switch
        {
            "tools" => BuildTools(),
            "buat" => BuildBuat(),
            "simpan" => BuildSimpan(),
            "brankas" => BuildBrankas(),
            _ => BuildBaca(),
        };
    }

    public void RefreshPage()
    {
        if (_page is "baca" or "simpan") Navigate(_page);
    }

    // ================= Membuka berkas =================
    public async Task OpenPicker()
    {
        var files = await Dialogs.PickFiles(this, "Buka dokumen", true, Dialogs.DocumentFilter);
        foreach (var f in files.Take(8)) OpenDocument(f);
    }

    public void OpenDocument(string path)
    {
        if (!_opened)
        {
            _pendingOpen.Add(path);
            return;
        }
        if (!File.Exists(path))
        {
            AppStorage.RemoveRecent(path);
            RefreshPage();
            _ = Dialogs.Error(this, $"Berkas tidak ditemukan:\n{path}");
            return;
        }
        if (!AppStorage.IsSupportedDocument(path))
        {
            _ = Dialogs.Info(this, "Format belum didukung", AppStorage.UnsupportedDocumentMessage(path));
            return;
        }
        AppStorage.AddRecent(path);
        var viewer = new ViewerWindow(this, path);
        viewer.Closed += (_, _) => RefreshPage();
        viewer.Show();
        RefreshPage();
    }

    public void OpenDocEditor(DocModel model, string? sourcePath = null)
    {
        var w = new DocEditorWindow(this, model, sourcePath);
        w.Closed += (_, _) => RefreshPage();
        w.Show();
    }

    public void OpenSheetEditor(SheetModel model, string? sourcePath = null)
    {
        var w = new SheetEditorWindow(this, model, sourcePath);
        w.Closed += (_, _) => RefreshPage();
        w.Show();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.GetFiles() != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList() ?? new List<string>();
        e.Handled = true;
        if (paths.Count == 0) return;
        if (_page == "brankas" && VaultManager.HasPin() && _vaultUnlocked)
        {
            _ = AddToVault(paths);
            return;
        }
        foreach (var p in paths.Take(8)) OpenDocument(p);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!cmd) return;
        switch (e.Key)
        {
            case Key.O:
                e.Handled = true;
                _ = OpenPicker();
                break;
            case Key.K:
            case Key.F:
                e.Handled = true;
                if (_page != "baca") Navigate("baca");
                _searchBox?.Focus();
                break;
            case Key.D1: e.Handled = true; Navigate("baca"); break;
            case Key.D2: e.Handled = true; Navigate("tools"); break;
            case Key.D3: e.Handled = true; Navigate("buat"); break;
            case Key.D4: e.Handled = true; Navigate("simpan"); break;
            case Key.D5: e.Handled = true; Navigate("brankas"); break;
        }
    }

    // ================= Halaman: Baca =================
    private TextBox? _searchBox;

    private Control BuildBaca()
    {
        var root = Col(20);

        // Hero
        var pick = Btn("Pilih Berkas…", () => _ = OpenPicker());
        pick.Background = Brushes.White;
        pick.Foreground = B(Accent);
        pick.FontWeight = FontWeight.SemiBold;
        pick.Padding = new Thickness(18, 10);
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var heroIcon = IconBadge("folder", Accent, "#FFFFFF", 54);
        var heroText = Col(4,
            T("Buka & baca dokumen", 19, "#FFFFFF", FontWeight.Bold),
            T("PDF (termasuk yang ber-password), Word .docx, Excel .xlsx/.csv, teks, dan gambar. " +
              "Bisa juga seret & lepas berkas ke jendela ini.", 13, "#D9FFFFFF", null, true));
        heroText.Margin = new Thickness(16, 0);
        Grid.SetColumn(heroText, 1);
        Grid.SetColumn(pick, 2);
        heroGrid.Children.Add(heroIcon);
        heroGrid.Children.Add(heroText);
        heroGrid.Children.Add(pick);
        var heroCard = Card(heroGrid, 22, Accent, Accent); heroCard.CornerRadius = new CornerRadius(24); root.Children.Add(heroCard);

        // Fitur cepat
        root.Children.Add(H2("Fitur cepat"));
        var quick = new WrapPanel { Orientation = Orientation.Horizontal };
        quick.Children.Add(QuickCard("🔊", "Baca Lantang", "Dengarkan isi dokumen dengan suara macOS", Indigo, IndigoBg, () => _ = ReadAloudFromPicker()));
        quick.Children.Add(QuickCard("🖨", "Cetak", "Cetak dokumen lewat Preview", Sky, SkyBg, () => _ = PrintFromPicker()));
        quick.Children.Add(QuickCard("🔳", "Kode QR", "Buat atau pindai kode QR", Teal, TealBg, () => new QrWindow(this).Show()));
        quick.Children.Add(QuickCard("🔍", "OCR", "Ambil teks dari foto / PDF hasil scan", Amber, AmberBg, () => _ = ToolOcr()));
        root.Children.Add(quick);

        // Terakhir dibuka
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(H2("Terakhir dibuka"));
        _searchBox = Input("Cari berkas…  (⌘K)", _search);
        _searchBox.Width = 260;
        _searchBox.TextChanged += (_, _) =>
        {
            _search = _searchBox.Text ?? "";
            if (_recentHost != null) _recentHost.Child = BuildRecentList();
        };
        Grid.SetColumn(_searchBox, 1);
        header.Children.Add(_searchBox);
        root.Children.Add(header);

        var chips = Row(6);
        foreach (var (key, label) in new[] { ("all", "Semua"), ("pdf", "PDF"), ("word", "Word"), ("excel", "Excel/CSV"), ("img", "Gambar"), ("star", "★ Ditandai") })
        {
            bool active = _filter == key;
            var chip = new Border
            {
                Padding = new Thickness(12, 5),
                CornerRadius = new CornerRadius(999),
                Background = B(active ? Ink : Surface),   // Android filterChip: terpilih = tinta
                BorderBrush = B(active ? Ink : Line),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = T(label, 12, active ? "#FFFFFF" : Text, FontWeight.Medium),
            };
            chip.Tapped += (_, _) => { _filter = key; Navigate("baca"); };
            chips.Children.Add(chip);
        }
        root.Children.Add(chips);

        _recentHost = new Border { Child = BuildRecentList() };
        root.Children.Add(_recentHost);
        return root;
    }

    private static Border QuickCard(string icon, string title, string desc, string fg, string bg, Action onClick)
    {
        (icon, fg, bg) = AndroidStyle(title, icon, fg, bg);
        var card = ClickCard(Row(12, IconBadge(icon, fg, bg, 42), Col(2, T(title, 14, Text, FontWeight.SemiBold), Caption(desc))), onClick, 14);
        card.Width = 262;
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private bool MatchesFilter(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool typeOk = _filter switch
        {
            "pdf" => ext == ".pdf",
            "word" => ext == ".docx",
            "excel" => ext is ".xlsx" or ".csv",
            "img" => ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".heic",
            "star" => AppStorage.IsBookmarked(path),
            _ => true,
        };
        if (!typeOk) return false;
        return string.IsNullOrWhiteSpace(_search) || Path.GetFileName(path).Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private Control BuildRecentList()
    {
        var entries = AppStorage.Config.RecentFiles.Where(r => MatchesFilter(r.FilePath)).ToList();
        if (entries.Count == 0)
        {
            string msg = AppStorage.Config.RecentFiles.Count == 0
                ? "Belum ada dokumen. Klik \"Pilih Berkas…\" atau seret berkas ke sini."
                : "Tidak ada berkas yang cocok dengan filter/pencarian.";
            return Card(Col(10, IconBadge("folder", Muted, Surface2, 56), T(msg, 13, Muted, null, true)), 26);
        }
        var list = Col(0);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            bool exists = File.Exists(e.FilePath);
            string folder = Path.GetDirectoryName(e.FilePath) ?? "";
            string home = MacPaths.Home;
            if (folder.StartsWith(home)) folder = "~" + folder[home.Length..];
            string sub = exists ? $"{folder}  •  {AppStorage.RelTime(e.TimestampMs)}" : "Berkas sudah dipindah atau dihapus";
            bool star = AppStorage.IsBookmarked(e.FilePath);
            var actions = Row(6,
                SmallBtn("Buka", () => OpenDocument(e.FilePath)),
                SmallBtn("Finder", () => MacSystem.Reveal(e.FilePath), tip: "Tampilkan di Finder"),
                SmallBtn(star ? "★" : "☆", () => { AppStorage.ToggleBookmark(e.FilePath); _recentHost!.Child = BuildRecentList(); }, Amber, "Tandai"),
                SmallBtn("✕", () => { AppStorage.RemoveRecent(e.FilePath); _recentHost!.Child = BuildRecentList(); }, Muted, "Hapus dari daftar"));
            actions.IsVisible = true;
            if (!exists) actions.Children.RemoveRange(0, 3);
            list.Children.Add(FileRow(e.FilePath, e.Name, sub, actions, i < entries.Count - 1, exists ? () => OpenDocument(e.FilePath) : null));
        }
        return Card(list, 6);
    }

    private static Border FileRow(string path, string name, string sub, Control actions, bool separator, Action? onDoubleTap)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var badge = FileBadge(path);
        var text = Col(2, T(name, 13.5, Text, FontWeight.Medium), T(sub, 12, Muted));
        text.Margin = new Thickness(12, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(badge);
        grid.Children.Add(text);
        grid.Children.Add(actions);
        var row = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderBrush = B(Line),
            BorderThickness = separator ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            Child = grid,
        };
        row.PointerEntered += (_, _) => row.Background = B(Surface2);
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        if (onDoubleTap != null) row.DoubleTapped += (_, _) => onDoubleTap();
        return row;
    }

    // ================= Halaman: Tools =================
    private Control BuildTools()
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(string icon, string title, string desc, string fg, string bg, Func<Task> action)
        {
            (icon, fg, bg) = AndroidStyle(title, icon, fg, bg);
            var card = ClickCard(Col(12, IconBadge(icon, fg, bg, 42), T(title, 14.5, Text, FontWeight.SemiBold), Caption(desc)), () => _ = action(), 14);
            card.Width = 252;
            card.Height = 150;
            card.Margin = new Thickness(0, 0, 14, 14);
            wrap.Children.Add(card);
        }
        Add("🧩", "Gabung PDF", "Satukan beberapa PDF menjadi satu berkas.", Accent, BlueBg, ToolMerge);
        Add("✂️", "Pisah PDF", "Pecah per halaman atau ambil rentang halaman.", Indigo, IndigoBg, ToolSplit);
        Add("🗜", "Kompres PDF", "Perkecil ukuran, termasuk target ≤ 1 MB / 500 KB (SSCASN/CPNS).", Teal, TealBg, ToolCompress);
        Add("🖼", "Gambar ke PDF", "Susun foto/scan (JPG, PNG, HEIC) jadi satu PDF.", Sky, SkyBg, ToolImagesToPdf);
        Add("🏞", "PDF ke JPG", "Ekspor setiap halaman menjadi gambar JPG.", Amber, AmberBg, ToolPdfToJpg);
        Add("🔐", "Kunci PDF", "Tambahkan password (AES-128) ke PDF.", Red, RedBg, ToolProtect);
        Add("🔓", "Buka Kunci PDF", "Simpan salinan PDF tanpa password.", Teal, TealBg, ToolUnlock);
        Add("🔄", "Putar PDF", "Putar semua halaman 90° / 180°.", Indigo, IndigoBg, ToolRotate);
        Add("📑", "Susun / Hapus Halaman", "Ubah urutan atau buang halaman tertentu.", Accent, BlueBg, ToolReorder);
        Add("🔍", "OCR Teks", "Ambil teks dari foto atau PDF hasil scan (Apple Vision).", Amber, AmberBg, ToolOcr);
        Add("🔳", "Buat QR", "Buat kode QR dari teks atau tautan.", Teal, TealBg, () => { new QrWindow(this).Show(); return Task.CompletedTask; });
        Add("📷", "Pindai QR", "Baca kode QR/barcode dari gambar.", Sky, SkyBg, ToolScanQr);
        return wrap;
    }

    // ================= Halaman: Buat =================
    private Control BuildBuat()
    {
        var root = Col(14);
        WrapPanel Section(string title)
        {
            root.Children.Add(H2(title));
            var w = new WrapPanel { Orientation = Orientation.Horizontal };
            root.Children.Add(w);
            return w;
        }
        void Add(WrapPanel w, string icon, string title, string desc, string fg, string bg, Action action)
        {
            (icon, fg, bg) = AndroidStyle(title, icon, fg, bg);
            var card = ClickCard(Row(12, IconBadge(icon, fg, bg, 42), Col(3, T(title, 14, Text, FontWeight.SemiBold), Caption(desc))), action, 14);
            card.Width = 300;
            card.Margin = new Thickness(0, 0, 14, 14);
            w.Children.Add(card);
        }

        var docs = Section("Dokumen Word (.docx + PDF)");
        Add(docs, "📄", "Dokumen Kosong", "Mulai dari halaman kosong.", Accent, BlueBg, () => OpenDocEditor(DocModel.FromTemplate(DocModel.TemplateBlank)));
        Add(docs, "✉️", "Surat Lamaran Kerja", "Templat surat lamaran siap isi.", Indigo, IndigoBg, () => OpenDocEditor(DocModel.FromTemplate(DocModel.TemplateSurat)));
        Add(docs, "🧑‍💼", "CV Profesional", "Daftar riwayat hidup rapi.", Teal, TealBg, () => OpenDocEditor(DocModel.FromTemplate(DocModel.TemplateCv)));

        var sheets = Section("Spreadsheet (.xlsx + CSV + PDF)");
        Add(sheets, "📊", "Spreadsheet Kosong", "Tabel kosong dengan rumus.", Teal, TealBg, () => OpenSheetEditor(SheetModel.FromTemplate(SheetModel.TemplateBlank)));
        Add(sheets, "💰", "Laporan Keuangan", "Contoh =SUM dan perkalian sel.", Accent, BlueBg, () => OpenSheetEditor(SheetModel.FromTemplate(SheetModel.TemplateFinance)));
        Add(sheets, "🧾", "Invoice / Tagihan", "Daftar tagihan dengan total.", Amber, AmberBg, () => OpenSheetEditor(SheetModel.FromTemplate(SheetModel.TemplateInvoice)));
        Add(sheets, "🗓", "Presensi", "Daftar hadir karyawan.", Sky, SkyBg, () => OpenSheetEditor(SheetModel.FromTemplate(SheetModel.TemplatePresensi)));

        var other = Section("Lainnya");
        Add(other, "🔳", "Kode QR", "Teks / tautan → gambar QR (PNG).", Teal, TealBg, () => new QrWindow(this).Show());
        Add(other, "🖼", "PDF dari Gambar", "Foto dokumen → satu PDF.", Sky, SkyBg, () => _ = ToolImagesToPdf());
        return root;
    }

    // ================= Halaman: Simpan =================
    private Control BuildSimpan()
    {
        var root = Col(18);
        var files = AppStorage.GetOutFiles()
            .Where(f => !f.Name.StartsWith('.') && !f.Name.Equals("privacy.html", StringComparison.OrdinalIgnoreCase))
            .ToList();
        long used = files.Sum(f => f.Length);
        var (avail, total) = AppStorage.GetDeviceStorage();

        var info = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        info.Children.Add(IconBadge("download", Accent, BlueBg, 48));
        var infoText = Col(3,
            T("Folder dokumen", 15, Text, FontWeight.SemiBold),
            Caption(AppStorage.OutDir, false),
            Caption($"{files.Count} berkas • {AppStorage.FmtSize(used)} • ruang kosong {AppStorage.FmtGb(avail)} dari {AppStorage.FmtGb(total)}", false));
        infoText.Margin = new Thickness(14, 0);
        Grid.SetColumn(infoText, 1);
        info.Children.Add(infoText);
        var btns = Row(8,
            Btn("Buka di Finder", () => MacSystem.Reveal(AppStorage.OutDir), true),
            Btn("Gambar", () => MacSystem.Reveal(AppStorage.PicturesDir)),
            Btn("Salinan", () => MacSystem.Reveal(AppStorage.DownloadsDir)),
            Btn("↻", () => Navigate("simpan"), tip: "Segarkan"));
        Grid.SetColumn(btns, 2);
        info.Children.Add(btns);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = total > 0 ? (total - avail) * 100.0 / total : 0,
            Height = 6,
            Margin = new Thickness(0, 14, 0, 0),
        };
        root.Children.Add(Card(Col(0, info, bar), 20));

        root.Children.Add(H2("Berkas hasil"));
        if (files.Count == 0)
        {
            root.Children.Add(Card(T("Belum ada berkas. Hasil dari Tools, editor dokumen, dan spreadsheet akan tersimpan di sini.", 13, Muted, null, true), 24));
            return root;
        }
        var list = Col(0);
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            string path = f.FullName;
            var actions = Row(6,
                SmallBtn("Buka", () => OpenDocument(path)),
                SmallBtn("Finder", () => MacSystem.Reveal(path)),
                SmallBtn("Ganti Nama", () => _ = RenameFile(path)),
                SmallBtn("🔒", () => _ = AddToVault(new List<string> { path }), Amber, "Pindahkan ke Brankas"),
                SmallBtn("🗑", () => _ = DeleteFile(path), Red, "Pindahkan ke Trash"));
            string sub = $"{AppStorage.FmtSize(f.Length)}  •  {AppStorage.RelTime(new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds())}";
            list.Children.Add(FileRow(path, f.Name, sub, actions, i < files.Count - 1, () => OpenDocument(path)));
        }
        root.Children.Add(Card(list, 6));
        return root;
    }

    private async Task RenameFile(string path)
    {
        string? name = await Dialogs.Prompt(this, "Ganti nama", "Nama baru (ekstensi dipertahankan):", Path.GetFileNameWithoutExtension(path));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (AppStorage.RenameFile(path, name) == null)
            await Dialogs.Error(this, "Gagal mengganti nama. Pastikan nama belum dipakai dan tidak mengandung karakter / :");
        Navigate("simpan");
    }

    private async Task DeleteFile(string path)
    {
        if (!await Dialogs.Confirm(this, "Hapus berkas?", $"\"{Path.GetFileName(path)}\" akan dipindahkan ke Trash.", "Pindahkan ke Trash", danger: true)) return;
        if (!MacSystem.MoveToTrash(path))
        {
            if (await Dialogs.Confirm(this, "Gagal memindahkan ke Trash", "Hapus permanen saja? Tindakan ini tidak bisa dibatalkan.", "Hapus Permanen", danger: true))
            {
                try { File.Delete(path); }
                catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
            }
        }
        AppStorage.RemoveRecent(path);
        Navigate(_page);
    }

    // ================= Tentang & Privasi =================
    private async Task ShowAbout()
    {
        await Dialogs.Info(this, "Tentang Depup Office",
            "Depup Office for Mac v2.3.0\n\n" + PrivacyManager.ComplianceSummaryText +
            "\n\nVersi macOS dibangun dengan Avalonia UI + PDFium + Apple Vision.\n" +
            $"Kontak: {PrivacyManager.DeveloperEmail}\nRepo: {PrivacyManager.OfficialGithubUrl}");
    }

    private async Task ShowPrivacy()
    {
        int choice = await Dialogs.Choose(this, "Kebijakan Privasi",
            "Depup Office memproses semua dokumen secara lokal di Mac ini. Tidak ada unggahan ke cloud.",
            "Buka versi online", "Buka salinan offline");
        if (choice == 0) MacSystem.Open(PrivacyManager.OfficialPrivacyUrl);
        else if (choice == 1)
        {
            PrivacyManager.EnsurePrivacyFileExists();
            MacSystem.Open(PrivacyManager.GetPrivacyHtmlPath());
        }
    }
}
