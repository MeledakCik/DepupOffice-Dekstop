using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace DepupOffice.Mac.Windows;

/// <summary>
/// Pembaca multi-format (port dari ViewerForm): PDF, DOCX, XLSX/CSV, TXT, gambar.
/// Tema kertas Eye-Care (Siang, Malam, Sepia, Hijau), zoom 50–250%, bookmark, baca lantang, cetak.
/// </summary>
public sealed class ViewerWindow : Window
{
    private static readonly string[] ThemeNames = { "☀️ Siang", "🌙 Malam", "📜 Sepia", "🌿 Hijau" };
    private static readonly string[] PaperBg = { "#FFFFFF", "#0F172A", "#FBF0D9", "#E2F0D9" };
    private static readonly string[] PaperFg = { "#0F172A", "#E2E8F0", "#3B2F1E", "#1F2D16" };

    private readonly MainWindow _app;
    private readonly string _path;
    private readonly bool _temporary;
    private readonly string _ext;
    private readonly Border _content = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _info = Caption("", false);
    private readonly TextBlock _zoomLabel = T("100%", 12, Muted);
    private readonly StackPanel _toolbarRight = Row(6);
    private readonly Border _canvasBg = new();

    private double _zoom;
    private int _theme;

    // PDF
    private PdfDoc? _doc;
    private int _page;
    private TextBox? _pageBox;
    private TextBlock? _pageCount;
    private readonly Image _pdfImage = new() { Stretch = Stretch.Uniform };
    private int _renderToken;
    private readonly Dictionary<int, AvBitmap> _cache = new();

    public ViewerWindow(MainWindow app, string path, bool temporary = false)
    {
        _app = app;
        _path = path;
        _temporary = temporary;
        _ext = Path.GetExtension(path).ToLowerInvariant();
        _zoom = AppStorage.Config.PdfDefaultZoom is >= 0.5f and <= 2.5f ? AppStorage.Config.PdfDefaultZoom : 1.0;
        _theme = Math.Clamp(AppStorage.Config.PdfReaderTheme, 0, 3);

        Title = Path.GetFileName(path) + (temporary ? " — Brankas" : "");
        Width = 1000;
        Height = 820;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface2);

        _scroll = new ScrollViewer
        {
            Content = _content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _canvasBg.Child = _scroll;

        var dock = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(_canvasBg);
        Content = dock;
        ApplyCanvasTheme();

        KeyDown += OnKeyDown;
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _doc?.Dispose();
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear();
        };
    }

    private bool IsPdf => _ext == ".pdf";
    private bool IsImage => _ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".heic";
    private bool IsSheet => _ext is ".xlsx" or ".csv";
    private bool IsDocx => _ext == ".docx";

    // ================= Toolbar =================
    private Control BuildToolbar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(FileBadge(_path, 36));
        var title = Col(1, T(Path.GetFileName(_path), 14, Text, FontWeight.SemiBold), _info);
        title.Margin = new Thickness(12, 0);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        Grid.SetColumn(_toolbarRight, 2);
        grid.Children.Add(_toolbarRight);

        var second = Row(8);
        if (IsPdf)
        {
            _pageBox = new TextBox { Width = 52, Text = "1", HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12 };
            _pageBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && int.TryParse(_pageBox.Text, out int p)) { e.Handled = true; GoTo(p - 1); }
            };
            _pageCount = T("/ –", 12, Muted);
            second.Children.Add(SmallBtn("◀", () => GoTo(_page - 1), tip: "Halaman sebelumnya (←)"));
            second.Children.Add(_pageBox);
            second.Children.Add(_pageCount);
            second.Children.Add(SmallBtn("▶", () => GoTo(_page + 1), tip: "Halaman berikutnya (→)"));
            second.Children.Add(Sep());
        }
        if (!IsSheet)
        {
            second.Children.Add(SmallBtn("−", () => SetZoom(_zoom - 0.1), tip: "Perkecil (⌘−)"));
            second.Children.Add(_zoomLabel);
            second.Children.Add(SmallBtn("+", () => SetZoom(_zoom + 0.1), tip: "Perbesar (⌘+)"));
            second.Children.Add(SmallBtn("Pas", () => SetZoom(1.0), tip: "Zoom 100% (⌘0)"));
        }
        if (!IsImage && !IsSheet)
        {
            second.Children.Add(Sep());
            var themeBox = Combo(ThemeNames, _theme, 120);
            themeBox.SelectionChanged += (_, _) =>
            {
                _theme = Math.Max(0, themeBox.SelectedIndex);
                AppStorage.Config.PdfReaderTheme = _theme;
                AppStorage.SaveConfig();
                ApplyCanvasTheme();
                Rerender();
            };
            ToolTip.SetTip(themeBox, "Tema kertas Eye-Care");
            second.Children.Add(themeBox);
        }

        var right = _toolbarRight;
        if (!_temporary && (IsDocx || IsSheet))
            right.Children.Add(Btn("✏️ Edit", Edit, true));
        if (IsPdf || IsImage)
            right.Children.Add(Btn("🔍 OCR", () => _ = _app.OcrFile(_path), tip: "Kenali teks (Apple Vision)"));
        if (IsPdf)
            right.Children.Add(Btn("📋 Teks", () => _ = ShowText(), tip: "Lihat & salin teks"));
        if (IsImage)
            right.Children.Add(Btn("📄 Jadikan PDF", () => _ = ImageToPdf()));
        right.Children.Add(Btn("🔊 Bacakan", () => _ = ReadAloud()));
        right.Children.Add(Btn("🖨 Cetak", () => _ = PrintService.PrintFile(this, _path), tip: "Cetak lewat Preview"));
        if (!_temporary)
        {
            bool star = AppStorage.IsBookmarked(_path);
            Button? starBtn = null;
            starBtn = Btn(star ? "★" : "☆", () =>
            {
                bool now = AppStorage.ToggleBookmark(_path);
                starBtn!.Content = now ? "★" : "☆";
            }, false, Amber, "Tandai dokumen");
            right.Children.Add(starBtn);
            right.Children.Add(Btn("Finder", () => MacSystem.Reveal(_path), tip: "Tampilkan di Finder"));
        }

        return new Border
        {
            Background = B(Surface),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = Col(10, grid, second),
        };
    }

    private static Border Sep() => new() { Width = 1, Height = 20, Background = B(Line), Margin = new Thickness(4, 0) };

    private void ApplyCanvasTheme()
    {
        _canvasBg.Background = B(_theme == 1 ? "#020617" : Surface2);
    }

    // ================= Memuat =================
    private async Task LoadAsync()
    {
        try
        {
            if (IsPdf) await LoadPdf();
            else if (IsDocx) await LoadDocx();
            else if (IsSheet) await LoadSheet();
            else if (IsImage) await LoadImage();
            else await LoadText();
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, $"Gagal membuka {Path.GetFileName(_path)}:\n{ex.Message}");
            Close();
        }
    }

    private double PdfRenderScale => Math.Clamp(1.3 * _zoom * Math.Max(1.0, RenderScaling), 1.0, 7.0);

    private async Task LoadPdf()
    {
        _doc = await PdfService.OpenInteractive(this, _path, PdfRenderScale);
        if (_doc == null) { Close(); return; }
        _pageCount!.Text = $"/ {_doc.PageCount}";
        var fi = new FileInfo(_path);
        _info.Text = $"PDF • {_doc.PageCount} halaman • {AppStorage.FmtSize(fi.Length)}" + (string.IsNullOrEmpty(_doc.Password) ? "" : " • 🔐 dilindungi password");
        _content.Child = new Border
        {
            Child = _pdfImage,
            BoxShadow = BoxShadows.Parse("0 4 16 0 #26000000"),
        };
        _zoomLabel.Text = $"{_zoom * 100:0}%";
        await RenderPage();
    }

    private void GoTo(int page)
    {
        if (_doc == null) return;
        page = Math.Clamp(page, 0, _doc.PageCount - 1);
        if (page == _page && _pdfImage.Source != null) { if (_pageBox != null) _pageBox.Text = (page + 1).ToString(); return; }
        _page = page;
        if (_pageBox != null) _pageBox.Text = (page + 1).ToString();
        _ = RenderPage();
        _scroll.Offset = new Vector(_scroll.Offset.X, 0);
    }

    private async Task RenderPage()
    {
        if (_doc == null) return;
        int token = ++_renderToken;
        int page = _page;
        var doc = _doc;
        int theme = _theme;
        if (!_cache.TryGetValue(page, out var bmp))
        {
            AvBitmap rendered;
            try
            {
                rendered = await Task.Run(() =>
                {
                    using var raw = doc.Render(page);
                    using var themed = ImageUtil.ApplyPaperTheme(raw, theme);
                    return ImageUtil.ToAvalonia(themed);
                });
            }
            catch (Exception ex)
            {
                if (token == _renderToken) _info.Text = "Gagal merender halaman: " + ex.Message;
                return;
            }
            if (token != _renderToken || doc != _doc)
            {
                rendered.Dispose();
                return;
            }
            bmp = rendered;
            _cache[page] = bmp;
        }
        _pdfImage.Source = bmp;
        // Ukuran tampil (DIP) = ukuran halaman (point) × 1,3 × zoom; bitmap dirender lebih rapat untuk layar Retina.
        _pdfImage.Width = bmp.PixelSize.Width / doc.Scale * 1.3 * _zoom;
        _pdfImage.Height = bmp.PixelSize.Height / doc.Scale * 1.3 * _zoom;
        _pageBox!.Text = (page + 1).ToString();

        // Pra-render halaman berikutnya di latar.
        int next = page + 1;
        if (next < doc.PageCount && !_cache.ContainsKey(next) && _cache.Count < 12)
        {
            try
            {
                var pre = await Task.Run(() =>
                {
                    using var raw = doc.Render(next);
                    using var themed = ImageUtil.ApplyPaperTheme(raw, theme);
                    return ImageUtil.ToAvalonia(themed);
                });
                if (doc == _doc && theme == _theme && !_cache.ContainsKey(next)) _cache[next] = pre;
                else pre.Dispose();
            }
            catch { }
        }
    }

    private void Rerender()
    {
        if (IsPdf)
        {
            foreach (var b in _cache.Values) b.Dispose();
            _cache.Clear();
            _ = RenderPage();
        }
        else if (IsDocx) _ = LoadDocx();
        else if (!IsImage && !IsSheet) _ = LoadText();
    }

    private void SetZoom(double z)
    {
        z = Math.Clamp(Math.Round(z, 2), 0.5, 2.5);
        if (Math.Abs(z - _zoom) < 0.001) return;
        _zoom = z;
        _zoomLabel.Text = $"{_zoom * 100:0}%";
        AppStorage.Config.PdfDefaultZoom = (float)_zoom;
        AppStorage.SaveConfig();
        if (IsPdf && _doc != null)
        {
            // Buka ulang pada skala baru agar tetap tajam.
            try
            {
                var fresh = _doc.Reopen(PdfRenderScale);
                var old = _doc;
                _doc = fresh;
                old.Dispose();
            }
            catch { }
            Rerender();
        }
        else if (IsImage && _imageCtl != null)
        {
            _imageCtl.Width = _imageBaseWidth * _zoom;
        }
        else Rerender();
    }

    private async Task LoadDocx()
    {
        var paragraphs = await Task.Run(() => OfficeReader.ReadDocx(_path));
        _info.Text = $"Word • {paragraphs.Count} paragraf • {AppStorage.FmtSize(new FileInfo(_path).Length)}";
        var stack = Col(10);
        foreach (var p in paragraphs)
        {
            stack.Children.Add(new SelectableTextBlock
            {
                Text = p.Text,
                FontSize = 14 * _zoom,
                FontWeight = p.Bold ? FontWeight.Bold : FontWeight.Normal,
                FontStyle = p.Italic ? FontStyle.Italic : FontStyle.Normal,
                TextAlignment = p.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, 3 => TextAlignment.Justify, _ => TextAlignment.Left },
                TextWrapping = TextWrapping.Wrap,
                Foreground = B(PaperFg[_theme]),
                LineHeight = 14 * _zoom * 1.5,
            });
        }
        if (paragraphs.Count == 0) stack.Children.Add(T("(Dokumen kosong atau format tidak terbaca)", 13, Muted));
        _content.Child = Paper(stack);
    }

    private Border Paper(Control child) => new()
    {
        Width = 780 * _zoom,
        MinHeight = 1000 * _zoom,
        Background = B(PaperBg[_theme]),
        Padding = new Thickness(64 * _zoom, 56 * _zoom),
        BoxShadow = BoxShadows.Parse("0 4 16 0 #26000000"),
        Child = child,
    };

    private async Task LoadText()
    {
        string text = await File.ReadAllTextAsync(_path);
        if (text.Length > 2_000_000) text = text[..2_000_000] + "\n\n… (dipotong)";
        _info.Text = $"Teks • {text.Split('\n').Length} baris • {AppStorage.FmtSize(new FileInfo(_path).Length)}";
        _content.Child = Paper(new SelectableTextBlock
        {
            Text = text,
            FontSize = 14 * _zoom,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B(PaperFg[_theme]),
            FontFamily = new FontFamily("Menlo, Monaco, monospace"),
        });
    }

    private async Task LoadSheet()
    {
        var model = await Task.Run(() => SheetModel.FromFile(_path));
        var rows = model.ToDisplayRows();
        var bold = model.BoldRows();
        _info.Text = $"{(_ext == ".csv" ? "CSV" : "Excel")} • {rows.Count} baris × {model.ColumnCount} kolom • {AppStorage.FmtSize(new FileInfo(_path).Length)}";

        int showRows = Math.Min(rows.Count, 400);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int c = 0; c < model.ColumnCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r <= showRows; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        Border Cell(string text, int row, int col, bool header, bool isBold, bool right)
        {
            var cell = new Border
            {
                BorderBrush = B(Line),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = B(header ? Surface2 : isBold ? "#F8FAFC" : Surface),
                Padding = new Thickness(10, 6),
                MinWidth = header && col == 0 ? 44 : 90,
                MaxWidth = 320,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    FontWeight = header || isBold ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = B(header ? Muted : Text),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                },
            };
            if (!header && text.Length > 30) ToolTip.SetTip(cell, text);
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            return cell;
        }

        grid.Children.Add(Cell("", 0, 0, true, false, false));
        for (int c = 0; c < model.ColumnCount; c++)
            grid.Children.Add(Cell(XlsxWriter.ColumnName(c), 0, c + 1, true, false, false));
        for (int r = 0; r < showRows; r++)
        {
            grid.Children.Add(Cell((r + 1).ToString(), r + 1, 0, true, false, true));
            for (int c = 0; c < model.ColumnCount; c++)
            {
                string v = c < rows[r].Count ? rows[r][c] : "";
                bool numeric = model.ParseNumber(v) != null || model.Rows[r][c].Value.TrimStart().StartsWith('=');
                grid.Children.Add(Cell(v, r + 1, c + 1, false, bold.Contains(r), numeric));
            }
        }
        var box = Col(8, new Border
        {
            Background = B(Surface),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = grid,
        });
        if (rows.Count > showRows) box.Children.Add(Caption($"Menampilkan {showRows} dari {rows.Count} baris. Buka di editor untuk melihat semuanya."));
        _content.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Child = box;
    }

    private async Task LoadImage()
    {
        var (bmp, w, h) = await Task.Run(() =>
        {
            using var sk = ImageUtil.Load(_path);
            using var fit = ImageUtil.Fit(sk, 4096);
            return (ImageUtil.ToAvalonia(fit), sk.Width, sk.Height);
        });
        _info.Text = $"Gambar • {w} × {h} px • {AppStorage.FmtSize(new FileInfo(_path).Length)}";
        double baseW = Math.Min(bmp.Size.Width, 900);
        var img = new Image { Source = bmp, Stretch = Stretch.Uniform, Width = baseW * _zoom };
        _imageBaseWidth = baseW;
        _imageCtl = img;
        _content.Child = new Border { BoxShadow = BoxShadows.Parse("0 4 16 0 #26000000"), Child = img };
        _zoomLabel.Text = $"{_zoom * 100:0}%";
    }

    private double _imageBaseWidth = 900;
    private Image? _imageCtl;

    // ================= Aksi =================
    private void Edit()
    {
        try
        {
            if (IsDocx) _app.OpenDocEditor(DocModel.FromDocx(_path), _path);
            else if (IsSheet) _app.OpenSheetEditor(SheetModel.FromFile(_path), _path);
        }
        catch (Exception ex)
        {
            _ = Dialogs.Error(this, ex.Message);
        }
    }

    private async Task ReadAloud()
    {
        string? text;
        if (IsPdf && _doc != null)
        {
            var doc = _doc;
            text = await Task.Run(() => doc.AllText());
        }
        else text = await DocText.ExtractAsync(this, _path);
        if (text == null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (await Dialogs.Confirm(this, "Tidak ada teks",
                    "Dokumen ini tidak memiliki lapisan teks (kemungkinan hasil scan). Kenali teksnya dengan OCR?", "Jalankan OCR"))
                await _app.OcrFile(_path);
            return;
        }
        new SpeechWindow(Path.GetFileName(_path), text).Show();
    }

    private async Task ShowText()
    {
        if (_doc == null) return;
        var doc = _doc;
        string text = await Task.Run(() => doc.AllText());
        if (string.IsNullOrWhiteSpace(text))
        {
            if (await Dialogs.Confirm(this, "Tidak ada teks",
                    "PDF ini tidak memiliki lapisan teks (hasil scan). Kenali teksnya dengan OCR?", "Jalankan OCR"))
                await _app.OcrFile(_path);
            return;
        }
        new TextResultWindow(_app, $"Teks — {Path.GetFileName(_path)}", AppStorage.BaseName(Path.GetFileName(_path)), text, null).Show();
    }

    private async Task ImageToPdf()
    {
        string outPath = AppStorage.NamedOut(AppStorage.BaseName(Path.GetFileName(_path)), "pdf");
        try
        {
            await Dialogs.Busy(this, "Membuat PDF…", r => PdfTools.ImagesToPdf(new[] { _path }, outPath, true, r));
            AppStorage.AddRecent(outPath);
            await Dialogs.Done(this, "PDF berhasil dibuat", "Gambar disimpan sebagai PDF A4.", outPath);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, ex.Message);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Down when IsPdf && (e.Key != Key.Down || cmd):
                GoTo(_page + 1); e.Handled = true; break;
            case Key.Left or Key.PageUp or Key.Up when IsPdf && (e.Key != Key.Up || cmd):
                GoTo(_page - 1); e.Handled = true; break;
            case Key.Home when IsPdf:
                GoTo(0); e.Handled = true; break;
            case Key.End when IsPdf && _doc != null:
                GoTo(_doc.PageCount - 1); e.Handled = true; break;
            case Key.OemPlus or Key.Add when cmd:
                SetZoom(_zoom + 0.1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract when cmd:
                SetZoom(_zoom - 0.1); e.Handled = true; break;
            case Key.D0 when cmd:
                SetZoom(1.0); e.Handled = true; break;
            case Key.W when cmd:
                Close(); e.Handled = true; break;
            case Key.P when cmd:
                _ = PrintService.PrintFile(this, _path); e.Handled = true; break;
        }
    }
}
