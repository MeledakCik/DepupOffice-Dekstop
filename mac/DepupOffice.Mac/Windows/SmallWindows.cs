using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DepupOffice.Mac.Services;
using DepupOfficeDesktop.Core;
using SkiaSharp;
using static DepupOffice.Mac.Ui;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace DepupOffice.Mac.Windows;

/// <summary>Voice Reader (port dari VoiceReaderManager): suara macOS, kecepatan 0,75×–2×, simpan audio.</summary>
public sealed class SpeechWindow : Window
{
    private readonly Speaker _speaker = new();
    private readonly Button _play;
    private readonly ComboBox _voice;
    private readonly Slider _speed;
    private readonly TextBlock _speedLabel = T("1.0×", 12, Muted);
    private readonly string _text;
    private readonly List<MacSystem.Voice> _voices;

    public SpeechWindow(string title, string text)
    {
        _text = text.Length > 200_000 ? text[..200_000] : text;
        Title = "Baca Lantang — " + title;
        Width = 620;
        Height = 560;
        MinWidth = 480;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface);

        _voices = MacSystem.Voices();
        _voice = Combo(_voices.Select(v => v.ToString()).DefaultIfEmpty("Suara bawaan sistem"), 0, 260);
        _speed = new Slider { Minimum = 0.75, Maximum = 2.0, Value = 1.0, TickFrequency = 0.25, IsSnapToTickEnabled = true, Width = 180 };
        _speed.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) _speedLabel.Text = $"{_speed.Value:0.00}×";
        };
        _play = Btn("▶  Putar", Toggle, true, Indigo);
        var save = Btn("💾 Simpan audio", () => _ = SaveAudio(), tip: "Simpan sebagai berkas audio .aiff");

        _speaker.Finished += () => Dispatcher.UIThread.Post(() => _play.Content = "▶  Putar");

        var controls = Col(10,
            Row(10, T("Suara", 12, Muted), _voice),
            Row(10, T("Kecepatan", 12, Muted), _speed, _speedLabel),
            Row(8, _play, save));
        if (!MacSystem.CanSpeak) controls.Children.Add(Caption("Baca lantang memakai perintah `say` dan hanya tersedia di macOS."));
        if (_voices.Count > 0 && !_voices[0].Locale.StartsWith("id"))
            controls.Children.Add(Caption("Tip: tambahkan suara Bahasa Indonesia (Damayanti) di System Settings → Accessibility → Spoken Content → System Voice → Manage Voices."));

        var preview = new ScrollViewer
        {
            Content = new SelectableTextBlock { Text = _text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = B(Text), LineHeight = 20 },
        };
        var dock = new DockPanel { Margin = new Thickness(20) };
        var top = Col(14, H2("🔊 Baca Lantang"), controls, HLine());
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(preview);
        Content = dock;

        Closed += (_, _) => _speaker.Dispose();
    }

    private MacSystem.Voice? SelectedVoice
        => _voice.SelectedIndex >= 0 && _voice.SelectedIndex < _voices.Count ? _voices[_voice.SelectedIndex] : null;

    private void Toggle()
    {
        if (_speaker.IsSpeaking)
        {
            _speaker.Stop();
            _play.Content = "▶  Putar";
            return;
        }
        try
        {
            _speaker.Speak(_text, SelectedVoice, _speed.Value);
            _play.Content = "■  Berhenti";
        }
        catch (Exception ex)
        {
            _ = Dialogs.Error(this, "Gagal memutar suara: " + ex.Message);
        }
    }

    private async Task SaveAudio()
    {
        if (!MacSystem.CanSpeak) return;
        string outPath = AppStorage.NamedOut(AppStorage.BaseName(Title ?? "audio") + "_audio", "aiff");
        var voice = SelectedVoice;
        double speed = _speed.Value;
        try
        {
            await Dialogs.Busy(this, "Membuat berkas audio…", _ =>
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"depup_tts_{Guid.NewGuid():N}.txt");
                File.WriteAllText(tmp, _text);
                try
                {
                    var args = new List<string>();
                    if (voice != null) { args.Add("-v"); args.Add(voice.Name); }
                    args.AddRange(new[] { "-r", ((int)Math.Round(180 * speed)).ToString(), "-o", outPath, "-f", tmp });
                    var r = MacSystem.Exec("say", args, 600_000);
                    if (r.Code != 0) throw new InvalidOperationException(r.Err);
                }
                finally { File.Delete(tmp); }
            });
            await Dialogs.Done(this, "Audio tersimpan", "Berkas audio siap diputar atau dibagikan.", outPath);
        }
        catch (Exception ex)
        {
            await Dialogs.Error(this, "Gagal membuat audio: " + ex.Message);
        }
    }
}

/// <summary>Hasil teks (OCR / teks PDF / pindai QR) yang bisa disunting, disalin, dan disimpan.</summary>
public sealed class TextResultWindow : Window
{
    private readonly MainWindow _app;
    private readonly TextBox _box;
    private readonly string _baseName;

    public TextResultWindow(MainWindow app, string title, string baseName, string text, Action<string>? searchablePdf)
    {
        _app = app;
        _baseName = AppStorage.BaseName(baseName);
        Title = title;
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface);

        _box = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            CornerRadius = new CornerRadius(10),
        };

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Button b) { b.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(b); }
        Add(Btn("📋 Salin", () => _ = Copy(), true));
        string trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            Add(Btn("🌐 Buka tautan", () => MacSystem.Open(trimmed), true, Teal));
        Add(Btn("🔊 Bacakan", () => new SpeechWindow(Title ?? "Teks", _box.Text ?? "").Show()));
        Add(Btn("💾 .txt", () => _ = Save("txt"), tip: "Simpan teks polos"));
        Add(Btn("📄 Word", () => _ = Save("docx"), tip: "Simpan sebagai dokumen Word"));
        Add(Btn("✏️ Edit di Editor", () => _app.OpenDocEditor(DocModel.FromPlainText(_baseName, _box.Text ?? ""))));
        if (searchablePdf != null)
            Add(Btn("🔎 PDF bisa dicari", () => _ = SaveSearchable(searchablePdf), false, Accent, "Gambar asli + lapisan teks OCR (bisa dicari & disalin)"));

        var dock = new DockPanel { Margin = new Thickness(20) };
        var top = Col(10, H2(title), Caption("Periksa dan perbaiki teks bila perlu sebelum disimpan."), buttons);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_box);
        Content = dock;
    }

    private async Task Copy()
    {
        if (Clipboard != null) await Clipboard.SetTextAsync(_box.Text ?? "");
        await Dialogs.Info(this, "Tersalin", "Teks sudah disalin ke clipboard.");
    }

    private async Task Save(string ext)
    {
        string text = _box.Text ?? "";
        string outPath = AppStorage.NamedOut(_baseName + (ext == "txt" ? "_teks" : "_ocr"), ext);
        try
        {
            if (ext == "txt") await File.WriteAllTextAsync(outPath, text);
            else await Task.Run(() => DocModel.FromPlainText(_baseName, text).SaveDocx(outPath));
            AppStorage.AddRecent(outPath);
            _app.RefreshPage();
            await Dialogs.Done(this, "Tersimpan", ext == "txt" ? "Teks disimpan." : "Dokumen Word dibuat.", outPath);
        }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
    }

    private async Task SaveSearchable(Action<string> build)
    {
        string outPath = AppStorage.NamedOut(_baseName + "_bisa_dicari", "pdf");
        try
        {
            await Dialogs.Busy(this, "Menyusun PDF yang bisa dicari…", _ => build(outPath));
            AppStorage.AddRecent(outPath);
            _app.RefreshPage();
            await Dialogs.Done(this, "PDF dibuat", "Tampilan sama dengan aslinya, tetapi teksnya kini bisa dicari (⌘F) dan disalin.", outPath);
        }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
    }
}

/// <summary>Generator QR (ISO/IEC 18004) + pemindai via Apple Vision. Port dari QrManager.</summary>
public sealed class QrWindow : Window
{
    private readonly MainWindow _app;
    private readonly TextBox _input;
    private readonly ComboBox _ecc;
    private readonly Image _preview = new() { Width = 300, Height = 300, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = Caption("", true);
    private bool[,]? _matrix;
    private DispatcherTimer? _debounce;

    public QrWindow(MainWindow app)
    {
        _app = app;
        Title = "Kode QR";
        Width = 780;
        Height = 560;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = B(Surface);

        _input = Input("Ketik teks, tautan, nomor, atau data Wi-Fi…", "https://github.com/MeledakCik/DepupOffice", true);
        _input.Height = 160;
        _input.TextChanged += (_, _) => Schedule();
        _ecc = Combo(new[] { "L — 7% (paling padat)", "M — 15% (disarankan)", "Q — 25%", "H — 30% (tahan rusak)" }, 1, 220);
        _ecc.SelectionChanged += (_, _) => Schedule();

        var presets = Row(6,
            SmallBtn("Tautan", () => _input.Text = "https://"),
            SmallBtn("WhatsApp", () => _input.Text = "https://wa.me/628xxxxxxxxxx"),
            SmallBtn("Wi-Fi", () => _input.Text = "WIFI:T:WPA;S:NamaWiFi;P:PasswordWiFi;;"),
            SmallBtn("Email", () => _input.Text = "mailto:nama@contoh.com"),
            SmallBtn("Telepon", () => _input.Text = "tel:+62"));

        var left = Col(12,
            H2("Buat kode QR"),
            Caption("Semua dibuat offline di Mac ini. Kode bisa dipindai kamera ponsel mana pun."),
            presets,
            _input,
            Row(8, T("Koreksi error", 12, Muted), _ecc),
            Row(8,
                Btn("💾 Simpan PNG", () => _ = SavePng(), true, Teal),
                Btn("📷 Pindai dari gambar…", () => _ = Scan())),
            _status);

        var right = Card(Col(8, _preview), 16);
        right.VerticalAlignment = VerticalAlignment.Top;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(22) };
        grid.Children.Add(left);
        right.Margin = new Thickness(22, 0, 0, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;

        Opened += (_, _) => UpdatePreview();
    }

    private QrEncoder.Ecc SelectedEcc => (QrEncoder.Ecc)Math.Clamp(_ecc.SelectedIndex, 0, 3);

    private void Schedule()
    {
        _debounce?.Stop();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce?.Stop(); UpdatePreview(); };
        _debounce.Start();
    }

    private void UpdatePreview()
    {
        string text = _input.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _matrix = null;
            _preview.Source = null;
            _status.Text = "Masukkan teks untuk membuat kode QR.";
            return;
        }
        try
        {
            _matrix = QrEncoder.Encode(text, SelectedEcc);
            using var bmp = Render(_matrix, 600);
            var old = _preview.Source as AvBitmap;
            _preview.Source = ImageUtil.ToAvalonia(bmp);
            old?.Dispose();
            int n = _matrix.GetLength(0);
            _status.Text = $"Versi {(n - 17) / 4} • {n}×{n} modul • {System.Text.Encoding.UTF8.GetByteCount(text)} byte";
        }
        catch (Exception ex)
        {
            _matrix = null;
            _preview.Source = null;
            _status.Text = ex.Message;
        }
    }

    public static SKBitmap Render(bool[,] m, int size, int border = 4)
    {
        int n = m.GetLength(0);
        int total = n + border * 2;
        int scale = Math.Max(1, size / total);
        int px = total * scale;
        var bmp = new SKBitmap(px, px, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (m[y, x])
                    canvas.DrawRect((x + border) * scale, (y + border) * scale, scale, scale, paint);
        return bmp;
    }

    private async Task SavePng()
    {
        if (_matrix == null) return;
        string name = "QR_" + AppStorage.BaseName((_input.Text ?? "kode").Trim().Replace("https://", "").Replace("http://", ""));
        if (name.Length > 40) name = name[..40];
        string outPath = Path.Combine(AppStorage.PicturesDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        try
        {
            using var bmp = Render(_matrix, 1200);
            await File.WriteAllBytesAsync(outPath, ImageUtil.EncodePng(bmp));
            AppStorage.AddRecent(outPath);
            _app.RefreshPage();
            await Dialogs.Done(this, "QR tersimpan", "Gambar PNG resolusi tinggi siap dicetak atau dibagikan.", outPath);
        }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
    }

    private async Task Scan()
    {
        string? path = await Dialogs.PickFile(this, "Pilih gambar berisi kode QR", Dialogs.ImageFilter);
        if (path == null) return;
        if (!await _app.EnsureVision()) return;
        try
        {
            var codes = await Dialogs.Busy<List<string>>(this, "Memindai…", _ => VisionService.ScanCodes(path));
            if (codes.Count == 0) { await Dialogs.Info(this, "Tidak ada kode", "Tidak ada kode QR/barcode terdeteksi."); return; }
            new TextResultWindow(_app, "Hasil pindai QR / barcode", "Hasil_QR", string.Join("\n\n", codes), null).Show();
        }
        catch (Exception ex) { await Dialogs.Error(this, ex.Message); }
    }
}
