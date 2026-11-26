using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using static DepupOffice.Mac.Ui;

namespace DepupOffice.Mac.Windows;

/// <summary>
/// Editor Dokumen Word (port dari DocEditorForm / DocEditorActivity.kt): judul, subjudul, bagian per paragraf
/// (judul bagian, tebal, miring, catatan, rata), templat, ukuran kertas/margin/spasi/font, simpan .docx + .pdf.
/// </summary>
public sealed class DocEditorWindow : Window
{
    private static readonly string[] AlignNames = { "Kiri", "Tengah", "Kanan", "Rata" };

    private readonly MainWindow _app;
    private readonly DocModel _model;
    private string? _savedPath;
    private readonly TextBox _title;
    private readonly TextBox _subtitle;
    private readonly StackPanel _sections = Col(12);
    private readonly TextBlock _status = Caption("", false);
    private bool _dirty;
    private bool _forceClose;

    public DocEditorWindow(MainWindow app, DocModel model, string? sourcePath)
    {
        _app = app;
        _model = model;
        _savedPath = sourcePath != null && sourcePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? sourcePath : null;

        Title = "Editor Dokumen — " + (string.IsNullOrWhiteSpace(model.Title) ? "Tanpa judul" : model.Title);
        Width = 1040;
        Height = 860;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface2);

        _title = new TextBox
        {
            Text = model.Title,
            Watermark = "Judul dokumen",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
        };
        _title.TextChanged += (_, _) => { _model.Title = _title.Text ?? ""; MarkDirty(); };
        _subtitle = new TextBox
        {
            Text = model.Subtitle,
            Watermark = "Subjudul / nomor surat (opsional)",
            FontSize = 13,
            FontStyle = FontStyle.Italic,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = B(Muted),
        };
        _subtitle.TextChanged += (_, _) => { _model.Subtitle = _subtitle.Text ?? ""; MarkDirty(); };

        var paper = new Border
        {
            Width = 800,
            Background = B(Surface),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(56, 48),
            Margin = new Thickness(0, 24, 0, 40),
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 4 18 0 #22000000"),
            Child = Col(14, _title, _subtitle, _sections, AddRow()),
        };

        var dock = new DockPanel();
        var toolbar = BuildToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(new ScrollViewer { Content = paper });
        Content = dock;

        RenderSections();
        _dirty = false;
        UpdateStatus();

        KeyDown += (_, e) =>
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (cmd && e.Key == Key.S) { e.Handled = true; _ = Save(false); }
            else if (cmd && e.Key == Key.P) { e.Handled = true; _ = PrintService.PrintDoc(this, _model); }
        };
        Closing += OnClosing;
    }

    // ================= Toolbar =================
    private Control BuildToolbar()
    {
        var c = AppStorage.Config;
        var pageSize = Combo(DocModel.PageSizes, Math.Max(0, Array.IndexOf(DocModel.PageSizes, _model.PageSize)), 90);
        pageSize.SelectionChanged += (_, _) => { _model.PageSize = DocModel.PageSizes[Math.Max(0, pageSize.SelectedIndex)]; Settings(); };
        var margin = Combo(new[] { "Margin normal", "Margin sempit", "Margin lebar" }, _model.MarginMode, 140);
        margin.SelectionChanged += (_, _) => { _model.MarginMode = Math.Max(0, margin.SelectedIndex); Settings(); };
        float[] spacings = { 1.0f, 1.15f, 1.5f, 2.0f };
        int spIdx = Array.FindIndex(spacings, s => Math.Abs(s - _model.LineSpacing) < 0.01f);
        var spacing = Combo(new[] { "Spasi 1.0", "Spasi 1.15", "Spasi 1.5", "Spasi 2.0" }, spIdx < 0 ? 1 : spIdx, 110);
        spacing.SelectionChanged += (_, _) => { _model.LineSpacing = spacings[Math.Max(0, spacing.SelectedIndex)]; Settings(); };
        var font = Combo(new[] { "Sans (Helvetica)", "Serif (Times)" }, _model.FontFamily, 150);
        font.SelectionChanged += (_, _) => { _model.FontFamily = Math.Max(0, font.SelectedIndex); Settings(); RenderSections(); };

        var row1 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = Row(8,
            Btn("📋 Templat", () => _ = ChooseTemplate()),
            Btn("＋ Paragraf", () => AddSection(new DocSection())),
            Btn("＋ Judul Bagian", () => AddSection(new DocSection { IsHeading = true, IsBold = true, Text = "JUDUL BAGIAN" })),
            Btn("＋ Catatan", () => AddSection(new DocSection { IsNote = true })));
        var right = Row(8,
            Btn("🔊 Bacakan", () => new SpeechWindow(_model.Title, _model.FullText()).Show()),
            Btn("🖨 Cetak", () => _ = PrintService.PrintDoc(this, _model), tip: "⌘P"),
            Btn("Simpan Sebagai…", () => _ = Save(true)),
            Btn("💾 Simpan", () => _ = Save(false), true, tip: "⌘S — simpan .docx dan salinan .pdf"));
        Grid.SetColumn(right, 1);
        row1.Children.Add(left);
        row1.Children.Add(right);

        var row2 = Row(8, pageSize, margin, spacing, font, _status);
        return new Border
        {
            Background = B(Surface),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = Col(10, row1, row2),
        };
    }

    private void Settings()
    {
        _model.SaveSettings();
        MarkDirty();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string font = _model.FontFamily == 1 ? "Times New Roman" : "Sans";
        _status.Text = $".docx • {_model.PageSize} • spasi {_model.LineSpacing:0.##}× • {font}" +
                       (_savedPath != null ? $" • {Path.GetFileName(_savedPath)}" : " • belum disimpan") + (_dirty ? " •  ✎ ada perubahan" : "");
    }

    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;
        UpdateStatus();
    }

    private Control AddRow()
    {
        var row = Row(8,
            SmallBtn("＋ Paragraf", () => AddSection(new DocSection())),
            SmallBtn("＋ Judul Bagian", () => AddSection(new DocSection { IsHeading = true, IsBold = true })),
            SmallBtn("＋ Catatan", () => AddSection(new DocSection { IsNote = true })));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        row.Margin = new Thickness(0, 8, 0, 0);
        return row;
    }

    private void AddSection(DocSection s)
    {
        _model.Sections.Add(s);
        MarkDirty();
        RenderSections();
        if (_sections.Children.LastOrDefault() is Border b && b.Child is StackPanel sp && sp.Children.LastOrDefault() is TextBox tb)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => tb.Focus());
    }

    // ================= Bagian =================
    private void RenderSections()
    {
        _sections.Children.Clear();
        for (int i = 0; i < _model.Sections.Count; i++)
            _sections.Children.Add(SectionEditor(i));
    }

    private Control SectionEditor(int index)
    {
        var s = _model.Sections[index];
        var box = new TextBox
        {
            Text = s.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44,
            FontSize = s.IsHeading ? 15 : s.IsNote ? 12.5 : 14,
            FontWeight = s.IsBold || s.IsHeading ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = s.IsItalic || s.IsNote ? FontStyle.Italic : FontStyle.Normal,
            Foreground = B(s.IsNote ? Muted : Text),
            FontFamily = new FontFamily(_model.FontFamily == 1 ? "Times New Roman, Times, serif" : "Helvetica Neue, Helvetica, Arial"),
            TextAlignment = s.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, 3 => TextAlignment.Justify, _ => TextAlignment.Left },
            Watermark = s.IsHeading ? "Judul bagian" : s.IsNote ? "Catatan" : "Tulis paragraf…",
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(4),
        };
        box.TextChanged += (_, _) => { s.Text = box.Text ?? ""; MarkDirty(); };

        ToggleButtonLike(out var heading, "H", "Judul bagian", s.IsHeading, v => { s.IsHeading = v; if (v) s.IsNote = false; });
        ToggleButtonLike(out var bold, "B", "Tebal", s.IsBold, v => s.IsBold = v);
        ToggleButtonLike(out var italic, "I", "Miring", s.IsItalic, v => s.IsItalic = v);
        ToggleButtonLike(out var note, "✎", "Catatan (kecil, miring, abu-abu)", s.IsNote, v => { s.IsNote = v; if (v) s.IsHeading = false; });
        var align = Combo(AlignNames, Math.Clamp(s.Align, 0, 3), 92);
        align.FontSize = 12;
        align.SelectionChanged += (_, _) => { s.Align = Math.Max(0, align.SelectedIndex); MarkDirty(); RenderSections(); };

        var tools = Row(4, heading, bold, italic, note, align,
            new Border { Width = 10 },
            SmallBtn("↑", () => Move(index, -1), tip: "Naikkan"),
            SmallBtn("↓", () => Move(index, 1), tip: "Turunkan"),
            SmallBtn("⧉", () => { _model.Sections.Insert(index + 1, s.Clone()); MarkDirty(); RenderSections(); }, tip: "Duplikat"),
            SmallBtn("✕", () => { _model.Sections.RemoveAt(index); if (_model.Sections.Count == 0) _model.Sections.Add(new DocSection()); MarkDirty(); RenderSections(); }, Red, "Hapus bagian"));
        tools.Opacity = 0.55;

        var card = new Border
        {
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(8),
            BorderBrush = B("#00000000"),
            BorderThickness = new Thickness(1),
            Background = s.IsNote ? B("#F8FAFC") : Brushes.Transparent,
            Child = Col(4, tools, box),
        };
        card.PointerEntered += (_, _) => { tools.Opacity = 1; card.BorderBrush = B(Line); };
        card.PointerExited += (_, _) => { tools.Opacity = 0.55; card.BorderBrush = B("#00000000"); };
        return card;
    }

    private void ToggleButtonLike(out Button button, string label, string tip, bool active, Action<bool> set)
    {
        bool state = active;
        var b = SmallBtn(label, () => { }, active ? "#FFFFFF" : Text, tip);
        b.MinWidth = 30;
        b.FontWeight = label == "B" ? FontWeight.Bold : FontWeight.Normal;
        b.FontStyle = label == "I" ? FontStyle.Italic : FontStyle.Normal;
        b.Background = active ? B(Accent) : B(Surface);
        if (active)
        {
            b.Resources["ButtonBackgroundPointerOver"] = B(AccentDark);
            b.Resources["ButtonBackgroundPressed"] = B(AccentDark);
            b.Resources["ButtonForegroundPointerOver"] = Brushes.White;
            b.Resources["ButtonForegroundPressed"] = Brushes.White;
        }
        b.Click += (_, _) =>
        {
            state = !state;
            set(state);
            MarkDirty();
            RenderSections();
        };
        button = b;
    }

    private void Move(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= _model.Sections.Count) return;
        (_model.Sections[index], _model.Sections[target]) = (_model.Sections[target], _model.Sections[index]);
        MarkDirty();
        RenderSections();
    }

    private async Task ChooseTemplate()
    {
        int c = await Dialogs.Choose(this, "Pilih templat", "Isi dokumen saat ini akan diganti.", "Surat Lamaran Kerja", "CV Profesional", "Dokumen Kosong");
        if (c < 0) return;
        if (_dirty && !await Dialogs.Confirm(this, "Ganti isi dokumen?", "Perubahan yang belum disimpan akan hilang.", "Ganti")) return;
        _model.ApplyTemplate(c switch { 0 => DocModel.TemplateSurat, 1 => DocModel.TemplateCv, _ => DocModel.TemplateBlank });
        _title.Text = _model.Title;
        _subtitle.Text = _model.Subtitle;
        RenderSections();
        MarkDirty();
    }

    // ================= Simpan =================
    private async Task<bool> Save(bool saveAs)
    {
        string? path = _savedPath;
        if (path == null || saveAs)
        {
            string suggested = AppStorage.BaseName(string.IsNullOrWhiteSpace(_model.Title) ? "Dokumen" : _model.Title);
            path = await Dialogs.PickSave(this, "Simpan dokumen Word", suggested, "docx");
            if (path == null) return false;
        }
        string pdfPath = Path.ChangeExtension(path, ".pdf");
        try
        {
            var snapshot = _model;
            await Dialogs.Busy(this, "Menyimpan .docx dan .pdf…", r =>
            {
                r("Menulis dokumen Word…");
                snapshot.SaveDocx(path);
                r("Membuat salinan PDF siap cetak…");
                TextPdfRenderer.DocToPdf(snapshot, pdfPath);
            });
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal menyimpan: " + ex.Message);
            return false;
        }
        _savedPath = path;
        _dirty = false;
        Title = "Editor Dokumen — " + Path.GetFileName(path);
        UpdateStatus();
        AppStorage.AddRecent(pdfPath);
        AppStorage.AddRecent(path);
        AppStorage.SetLastJob("Dokumen Word Dibuat", path);
        _app.RefreshPage();
        await Dialogs.Done(this, "Dokumen tersimpan", $"Word: {Path.GetFileName(path)}\nPDF siap cetak: {Path.GetFileName(pdfPath)}", path);
        return true;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_dirty || _forceClose) return;
        e.Cancel = true;
        _ = AskClose();
    }

    private async Task AskClose()
    {
        int c = await Dialogs.Choose(this, "Simpan perubahan?", "Dokumen ini punya perubahan yang belum disimpan.", "Simpan", "Jangan simpan");
        if (c < 0) return;
        if (c == 0 && !await Save(false)) return;
        _forceClose = true;
        Close();
    }
}
