using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;

namespace DepupOffice.Mac.Windows;

/// <summary>
/// Editor Spreadsheet (port dari SheetEditorForm / SheetEditorActivity.kt): formula bar (fx), rumus
/// =SUM/AVERAGE/MIN/MAX/COUNT/ABS/ROUND/INT, templat, simpan .xlsx (rumus Excel asli) + CSV + PDF.
/// </summary>
public sealed class SheetEditorWindow : Window
{
    private readonly MainWindow _app;
    private readonly SheetModel _model;
    private string? _savedPath;
    private readonly TextBox _titleBox;
    private readonly TextBox _formula;
    private readonly TextBlock _cellLabel = T("A1", 13, Accent, FontWeight.Bold);
    private readonly TextBlock _status = Caption("", false);
    private readonly Border _gridHost = new() { Focusable = true, Background = Brushes.Transparent };
    private TextBlock[,] _cells = new TextBlock[0, 0];
    private Border[,] _cellBorders = new Border[0, 0];
    private int _row, _col;
    private bool _gridlines;
    private bool _dirty;
    private bool _forceClose;
    private bool _editing;

    public SheetEditorWindow(MainWindow app, SheetModel model, string? sourcePath)
    {
        _app = app;
        _model = model;
        _model.Currency = AppStorage.Config.SheetCurrencySymbol;
        _gridlines = AppStorage.Config.SheetShowGridlines;
        _savedPath = sourcePath != null && sourcePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? sourcePath : null;

        Title = "Spreadsheet — " + model.Title;
        Width = 1120;
        Height = 780;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface);

        _titleBox = new TextBox { Text = model.Title, Watermark = "Nama sheet", Width = 260, FontWeight = FontWeight.SemiBold };
        _titleBox.TextChanged += (_, _) => { _model.Title = string.IsNullOrWhiteSpace(_titleBox.Text) ? "Sheet1" : _titleBox.Text!.Trim(); MarkDirty(); };

        _formula = new TextBox
        {
            Watermark = "Teks, angka, atau rumus — mis. =SUM(D2:D5), =AVERAGE(B2:B5), =B2*C2",
            FontFamily = new FontFamily("Menlo, Monaco, monospace"),
            FontSize = 13,
        };
        _formula.AddHandler(KeyDownEvent, OnFormulaKey, RoutingStrategies.Tunnel);
        _formula.GotFocus += (_, _) => _editing = true;
        _formula.LostFocus += (_, _) => { if (_editing) Commit(); };

        var dock = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);

        var fxBar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(16, 10) };
        var label = new Border { Width = 64, Child = _cellLabel, VerticalAlignment = VerticalAlignment.Center };
        var fx = T("fx", 14, Muted, FontWeight.Bold);
        fx.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(fx, 1);
        Grid.SetColumn(_formula, 2);
        fxBar.Children.Add(label);
        fxBar.Children.Add(fx);
        fxBar.Children.Add(_formula);
        DockPanel.SetDock(fxBar, Dock.Top);
        dock.Children.Add(fxBar);

        var statusBar = new Border { Padding = new Thickness(16, 6), BorderBrush = B(Line), BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);

        dock.Children.Add(new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(16, 0, 16, 16), Child = _gridHost },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        Content = dock;

        _gridHost.KeyDown += OnGridKey;
        _gridHost.TextInput += OnGridText;
        KeyDown += OnWindowKey;
        Closing += OnClosing;
        Opened += (_, _) => { BuildGrid(); Select(0, 0); _gridHost.Focus(); };
        UpdateStatus();
    }

    // ================= Toolbar =================
    private Control BuildToolbar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new WrapPanel { Orientation = Orientation.Horizontal };
        void L(Control c) { c.Margin = new Thickness(0, 0, 6, 6); left.Children.Add(c); }
        L(_titleBox);
        L(Btn("📊 Templat", () => _ = ChooseTemplate()));
        L(SmallBtn("＋ Baris", () => { _model.AddRow(); Structure(); }));
        L(SmallBtn("− Baris", () => { _model.RemoveLastRow(); Structure(); }));
        L(SmallBtn("＋ Kolom", () => { _model.AddColumn(); Structure(); }));
        L(SmallBtn("− Kolom", () => { _model.RemoveLastColumn(); Structure(); }));
        L(SmallBtn("Σ Auto-SUM", AutoSum, Teal, "Jumlahkan angka di atas sel aktif"));
        L(SmallBtn("B Tebal", ToggleBold, tip: "Tebalkan / normalkan baris aktif"));
        var gl = new CheckBox { Content = "Garis sel saat cetak", IsChecked = _gridlines, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        gl.IsCheckedChanged += (_, _) =>
        {
            _gridlines = gl.IsChecked == true;
            AppStorage.Config.SheetShowGridlines = _gridlines;
            AppStorage.SaveConfig();
        };
        L(gl);

        var right = Row(6,
            Btn("🔊", () => new SpeechWindow(_model.Title, string.Join("\n", _model.ToDisplayRows().Select(r => string.Join(", ", r.Where(v => v.Length > 0))))).Show(), tip: "Bacakan"),
            Btn("🖨", () => _ = PrintService.PrintSheet(this, _model, _gridlines), tip: "Cetak lewat Preview (⌘P)"),
            Btn("CSV", () => _ = Export("csv"), tip: "Ekspor CSV"),
            Btn("PDF", () => _ = Export("pdf"), tip: "Ekspor PDF"),
            Btn("💾 Simpan .xlsx", () => _ = Save(false), true, Teal, "⌘S"));
        right.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return new Border
        {
            Background = B(Surface),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12, 16, 6),
            Child = grid,
        };
    }

    private void UpdateStatus()
    {
        int filled = _model.Rows.Sum(r => r.Count(c => !string.IsNullOrWhiteSpace(c.Value)));
        _status.Text = $"{_model.Rows.Count} baris × {_model.ColumnCount} kolom • {filled} sel terisi" +
                       (_savedPath != null ? $" • {Path.GetFileName(_savedPath)}" : " • belum disimpan") +
                       (_dirty ? " • ✎ ada perubahan" : "") +
                       "   |   ←↑↓→ pindah sel • Enter/F2 edit • ketik langsung untuk mengisi • ⌘V tempel dari Excel/Numbers";
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateStatus();
    }

    private void Structure()
    {
        MarkDirty();
        BuildGrid();
        Select(Math.Min(_row, _model.Rows.Count - 1), Math.Min(_col, _model.ColumnCount - 1));
    }

    // ================= Grid =================
    private void BuildGrid()
    {
        int rows = _model.Rows.Count, cols = _model.ColumnCount;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(46)));
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r <= rows; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        _cells = new TextBlock[rows, cols];
        _cellBorders = new Border[rows, cols];

        Border Header(string text, int r, int c)
        {
            var b = new Border
            {
                Background = B(Surface2),
                BorderBrush = B(Line),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(8, 5),
                Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = B(Muted), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            };
            Grid.SetRow(b, r);
            Grid.SetColumn(b, c);
            return b;
        }

        grid.Children.Add(Header("", 0, 0));
        for (int c = 0; c < cols; c++) grid.Children.Add(Header(XlsxWriter.ColumnName(c), 0, c + 1));
        for (int r = 0; r < rows; r++)
        {
            grid.Children.Add(Header((r + 1).ToString(), r + 1, 0));
            for (int c = 0; c < cols; c++)
            {
                int rr = r, cc = c;
                var tb = new TextBlock
                {
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var border = new Border
                {
                    MinWidth = 118,
                    MaxWidth = 300,
                    Height = 30,
                    Padding = new Thickness(8, 0),
                    BorderBrush = B(Line),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Child = tb,
                    Cursor = new Cursor(StandardCursorType.Ibeam),
                };
                border.PointerPressed += (_, e) =>
                {
                    if (_editing) Commit();
                    Select(rr, cc);
                    _gridHost.Focus();
                    e.Handled = true;
                };
                border.DoubleTapped += (_, _) => BeginEdit(null);
                Grid.SetRow(border, r + 1);
                Grid.SetColumn(border, c + 1);
                grid.Children.Add(border);
                _cells[r, c] = tb;
                _cellBorders[r, c] = border;
            }
        }
        _gridHost.Child = new Border
        {
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid,
        };
        RefreshValues();
    }

    private void RefreshValues()
    {
        int rows = Math.Min(_model.Rows.Count, _cells.GetLength(0));
        int cols = Math.Min(_model.ColumnCount, _cells.GetLength(1));
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var cell = _model.Cell(r, c);
                string shown = _model.Display(r, c);
                var tb = _cells[r, c];
                tb.Text = shown;
                bool formula = cell.Value.TrimStart().StartsWith('=');
                bool numeric = formula || _model.ParseNumber(cell.Value) != null;
                tb.HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                tb.FontWeight = cell.Bold ? FontWeight.SemiBold : FontWeight.Normal;
                tb.Foreground = B(shown == "#ERROR!" ? Red : formula ? "#0F4C81" : Text);
                bool selected = r == _row && c == _col;
                _cellBorders[r, c].Background = B(selected ? BlueBg : cell.Background ?? Surface);
            }
        UpdateStatus();
    }

    private void Select(int r, int c)
    {
        if (_model.Rows.Count == 0) return;
        r = Math.Clamp(r, 0, _model.Rows.Count - 1);
        c = Math.Clamp(c, 0, _model.ColumnCount - 1);
        if (_row < _cellBorders.GetLength(0) && _col < _cellBorders.GetLength(1))
        {
            var prev = _model.Cell(_row, _col);
            _cellBorders[_row, _col].Background = B(prev.Background ?? Surface);
            _cellBorders[_row, _col].BorderBrush = B(Line);
            _cellBorders[_row, _col].BorderThickness = new Thickness(0, 0, 1, 1);
        }
        _row = r;
        _col = c;
        if (r < _cellBorders.GetLength(0) && c < _cellBorders.GetLength(1))
        {
            _cellBorders[r, c].Background = B(BlueBg);
            _cellBorders[r, c].BorderBrush = B(Accent);
            _cellBorders[r, c].BorderThickness = new Thickness(1.5);
            _cellBorders[r, c].BringIntoView();
        }
        _cellLabel.Text = $"{XlsxWriter.ColumnName(c)}{r + 1}";
        _editing = false;
        _formula.Text = _model.Cell(r, c).Value;
    }

    private void BeginEdit(string? initial)
    {
        _formula.Focus();
        if (initial != null) _formula.Text = initial;
        _formula.CaretIndex = _formula.Text?.Length ?? 0;
        _editing = true;
    }

    private void Commit()
    {
        _editing = false;
        var cell = _model.Cell(_row, _col);
        string text = _formula.Text ?? "";
        if (text == cell.Value) return;
        cell.Value = text;
        cell.Cached = null;
        MarkDirty();
        RefreshValues();
    }

    private void OnFormulaKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                int next = _row + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (next >= _model.Rows.Count && _model.Rows.Count < SheetModel.MaxRows)
                {
                    _model.AddRow();
                    MarkDirty();
                    BuildGrid();
                }
                Select(next, _col);
                _gridHost.Focus();
                break;
            case Key.Tab:
                e.Handled = true;
                Commit();
                Select(_row, _col + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1));
                _gridHost.Focus();
                break;
            case Key.Escape:
                e.Handled = true;
                _editing = false;
                _formula.Text = _model.Cell(_row, _col).Value;
                _gridHost.Focus();
                break;
        }
    }

    private void OnGridKey(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Up: Select(_row - 1, _col); e.Handled = true; break;
            case Key.Down: Select(_row + 1, _col); e.Handled = true; break;
            case Key.Left: Select(_row, _col - 1); e.Handled = true; break;
            case Key.Right: Select(_row, _col + 1); e.Handled = true; break;
            case Key.Tab: Select(_row, _col + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1)); e.Handled = true; break;
            case Key.Enter:
            case Key.F2:
                BeginEdit(null); e.Handled = true; break;
            case Key.Delete:
            case Key.Back:
                _formula.Text = "";
                Commit();
                e.Handled = true;
                break;
            case Key.C when cmd:
                _ = Clipboard?.SetTextAsync(_model.Display(_row, _col));
                e.Handled = true;
                break;
            case Key.V when cmd:
                _ = Paste();
                e.Handled = true;
                break;
        }
    }

    private void OnGridText(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        BeginEdit(e.Text);
    }

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (cmd && e.Key == Key.S) { e.Handled = true; _ = Save(false); }
        else if (cmd && e.Key == Key.P) { e.Handled = true; _ = PrintService.PrintSheet(this, _model, _gridlines); }
    }

    /// <summary>Tempel teks berpemisah Tab/baris (salinan dari Excel, Numbers, Google Sheets) mulai dari sel aktif.</summary>
    private async Task Paste()
    {
        if (Clipboard == null) return;
        string? text = await Clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length && _row + i < SheetModel.MaxRows; i++)
        {
            var parts = lines[i].Split('\t');
            for (int j = 0; j < parts.Length && _col + j < SheetModel.MaxCols; j++)
            {
                if (_col + j >= _model.ColumnCount) _model.EnsureColumns(_col + j + 1);
                var cell = _model.Cell(_row + i, _col + j);
                cell.Value = parts[j];
                cell.Cached = null;
            }
        }
        MarkDirty();
        BuildGrid();
        Select(_row, _col);
    }

    private void AutoSum()
    {
        string name = XlsxWriter.ColumnName(_col);
        string formula = _row >= 2 ? $"=SUM({name}2:{name}{_row})" : _model.AutoSumFormula(_col);
        var cell = _model.Cell(_row, _col);
        cell.Value = formula;
        cell.Cached = null;
        MarkDirty();
        RefreshValues();
        Select(_row, _col);
    }

    private void ToggleBold()
    {
        var row = _model.Rows[_row];
        bool bold = !row.Any(c => c.Bold);
        foreach (var c in row)
        {
            c.Bold = bold;
            c.Background = bold ? SheetModel.HeaderBg : null;
        }
        MarkDirty();
        RefreshValues();
        Select(_row, _col);
    }

    private async Task ChooseTemplate()
    {
        int c = await Dialogs.Choose(this, "Pilih templat", "Isi tabel saat ini akan diganti.",
            "Laporan Keuangan & Sales", "Invoice / Tagihan", "Presensi & Daftar Hadir", "Kosongkan Tabel");
        if (c < 0) return;
        if (_dirty && !await Dialogs.Confirm(this, "Ganti isi tabel?", "Perubahan yang belum disimpan akan hilang.", "Ganti")) return;
        _model.LoadTemplate(c switch { 0 => SheetModel.TemplateFinance, 1 => SheetModel.TemplateInvoice, 2 => SheetModel.TemplatePresensi, _ => SheetModel.TemplateBlank });
        _titleBox.Text = _model.Title;
        _row = _col = 0;
        Structure();
    }

    // ================= Simpan & ekspor =================
    private async Task<bool> Save(bool saveAs)
    {
        if (_editing) Commit();
        string? path = _savedPath;
        if (path == null || saveAs)
        {
            path = await Dialogs.PickSave(this, "Simpan spreadsheet", AppStorage.BaseName(_model.Title), "xlsx");
            if (path == null) return false;
        }
        try
        {
            string target = path;
            await Task.Run(() => _model.SaveXlsx(target));
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal menyimpan: " + ex.Message);
            return false;
        }
        _savedPath = path;
        _dirty = false;
        Title = "Spreadsheet — " + Path.GetFileName(path);
        UpdateStatus();
        AppStorage.AddRecent(path);
        AppStorage.SetLastJob("Spreadsheet Disimpan", path);
        _app.RefreshPage();
        await Dialogs.Done(this, "Spreadsheet tersimpan", "Berkas .xlsx bisa dibuka di Excel, Numbers, atau Google Sheets. Rumus ikut tersimpan.", path);
        return true;
    }

    private async Task Export(string ext)
    {
        if (_editing) Commit();
        string? path = await Dialogs.PickSave(this, ext == "csv" ? "Ekspor CSV" : "Ekspor PDF", AppStorage.BaseName(_model.Title), ext);
        if (path == null) return;
        try
        {
            if (ext == "csv")
            {
                string sep = string.IsNullOrEmpty(AppStorage.Config.SheetCsvSeparator) ? "," : AppStorage.Config.SheetCsvSeparator;
                await File.WriteAllTextAsync(path, _model.ToCsv(sep), new System.Text.UTF8Encoding(true));
            }
            else
            {
                await Dialogs.Busy(this, "Membuat PDF…", _ => TextPdfRenderer.TableToPdf(_model.Title, _model.ToDisplayRows(), _model.BoldRows(), path, _gridlines));
            }
            AppStorage.AddRecent(path);
            _app.RefreshPage();
            await Dialogs.Done(this, "Ekspor selesai", ext == "csv" ? "CSV (UTF-8) siap dibuka di aplikasi lain." : "PDF tabel siap dicetak atau dibagikan.", path);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal mengekspor: " + ex.Message);
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_editing) Commit();
        if (!_dirty || _forceClose) return;
        e.Cancel = true;
        _ = AskClose();
    }

    private async Task AskClose()
    {
        int c = await Dialogs.Choose(this, "Simpan perubahan?", "Spreadsheet ini punya perubahan yang belum disimpan.", "Simpan", "Jangan simpan");
        if (c < 0) return;
        if (c == 0 && !await Save(false)) return;
        _forceClose = true;
        Close();
    }
}
