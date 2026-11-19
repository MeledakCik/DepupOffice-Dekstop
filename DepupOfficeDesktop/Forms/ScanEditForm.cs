using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms;

public sealed class ScanEditForm : Form
{
    internal const string ImageFilter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff";
    private const int PreviewSize = 1100;

    private readonly List<ScanPage> _pages;
    private readonly Func<string> _nextPhotoPath;
    private readonly CropCanvasControl _canvas = new() { Dock = DockStyle.Fill, AccessibleName = "Pratinjau dan batas potong dokumen" };
    private readonly ComboBox _pageSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Halaman pindai" };
    private readonly SplitContainer _workspace = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 100 };
    private Panel _cropTools = null!, _filterTools = null!;
    private Button _previous = null!, _next = null!, _moveUp = null!, _moveDown = null!, _remove = null!, _redact = null!;
    private int _index, _brightness, _contrast;
    private int _mode = 3;
    private bool _updatingPages, _arranging;
    private ScanPage Current => _pages[_index];

    public string? OutputFilePath { get; private set; }
    public int OutputJpgCount { get; private set; }

    internal ScanEditForm(List<ScanPage> pages, Func<string> nextPhotoPath, bool receipt)
    {
        _pages = pages;
        _nextPhotoPath = nextPhotoPath;
        Text = "Studio Pindai";
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Theme.S(1080), Theme.S(720));
        MinimumSize = new Size(Math.Min(Theme.S(640), 800), Math.Min(Theme.S(460), 600));
        BackColor = Theme.BG;
        ShowInTaskbar = false;
        Build();
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _canvas.SetFilter(_mode, _brightness, _contrast);
        };
        Shown += (_, _) =>
        {
            ArrangeWorkspace();
            UpdatePageSelector();
            ShowPage();
            if (receipt) ShowRecognizedText(editReceipt: true);
        };
    }

    internal static List<ScanPage> ImportPages(IWin32Window owner, string[] files)
    {
        var (ok, result) = Ui.Busy(owner, "Membaca foto dan mencari tepi dokumen…", progress =>
        {
            var pages = new List<ScanPage>();
            var rejected = new List<string>();
            for (int index = 0; index < files.Length; index++)
            {
                string file = files[index];
                try
                {
                    using var source = PdfEngine.Decode(file, 900);
                    var page = new ScanPage(file);
                    DetectGeometry(page, source, crop: true, straighten: true);
                    pages.Add(page);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
                {
                    rejected.Add(Path.GetFileName(file));
                }
                progress(index + 1, files.Length);
            }
            return (pages, rejected);
        });
        if (!ok) return [];
        if (result.rejected.Count > 0)
        {
            string names = string.Join(", ", result.rejected.Take(5));
            if (result.rejected.Count > 5) names += "…";
            MessageBox.Show(owner, $"Foto yang tidak dapat dibaca ({result.rejected.Count})\n{names}", "Impor foto",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return result.pages;
    }

    private static Button ActionButton(string label, Action run, bool primary = false)
    {
        var button = Theme.CreateButton(label, primary, primary ? Theme.ACCENT : Theme.TEXT, Theme.S(34));
        button.Click += (_, _) => run();
        return button;
    }

    private void Build()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.TOOLBAR,
            Padding = new Padding(Theme.S(12), Theme.S(6), Theme.S(12), Theme.S(6))
        };
        toolbar.Controls.AddRange([
            ActionButton("Impor foto…", () => AddPages(Ui.PickFiles(this, "Tambah foto dokumen", ImageFilter))),
            ActionButton("Kamera…", () => AddPages(ScanCameraForm.TakePhotos(this, _nextPhotoPath))),
            ActionButton("Teks OCR…", () => ShowRecognizedText()),
            ActionButton("Cetak halaman…", PrintCurrent),
            ActionButton("Ekspor…", ShowExport, primary: true)
        ]);

        var pageBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, BackColor = Theme.TOOLBAR,
            Padding = new Padding(Theme.S(8), Theme.S(6), Theme.S(8), Theme.S(6))
        };
        _pageSelector.Width = Theme.S(130);
        _pageSelector.Margin = new Padding(Theme.S(3), Theme.S(8), Theme.S(6), 0);
        _pageSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingPages || _pageSelector.SelectedIndex < 0) return;
            _index = _pageSelector.SelectedIndex;
            ShowPage();
        };
        _previous = ActionButton("‹", () => _pageSelector.SelectedIndex = _index - 1);
        _next = ActionButton("›", () => _pageSelector.SelectedIndex = _index + 1);
        _previous.AccessibleName = "Halaman sebelumnya";
        _next.AccessibleName = "Halaman berikutnya";
        pageBar.Controls.AddRange([_previous, _pageSelector, _next]);

        _workspace.Panel1.Controls.Add(_canvas);
        _workspace.Panel1.Controls.Add(pageBar);
        _workspace.Panel1.Padding = new Padding(Theme.S(8));
        _workspace.SplitterWidth = Math.Max(4, Theme.S(6));
        _workspace.SizeChanged += (_, _) => ArrangeWorkspace();
        var inspector = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.S(10)), BackColor = Theme.SURFACE };
        var tabs = new Segmented { Dock = DockStyle.Top, Items = ["Halaman", "Filter"], AccessibleName = "Alat pindai" };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _cropTools = BuildPageTools(scroll);
        _filterTools = BuildFilterTools(scroll);
        _filterTools.Hide();
        tabs.SelectedChanged += index => { _cropTools.Visible = index == 0; _filterTools.Visible = index == 1; scroll.AutoScrollPosition = Point.Empty; };
        inspector.Controls.Add(scroll);
        inspector.Controls.Add(tabs);
        _workspace.Panel2.Controls.Add(inspector);
        Controls.Add(_workspace);
        Controls.Add(toolbar);
    }

    private static FlowLayoutPanel ToolList(Panel scroll)
    {
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(0, Theme.S(10), Theme.S(8), Theme.S(10))
        };
        scroll.Controls.Add(content);
        return content;
    }

    private static void Group(FlowLayoutPanel list, string title, params Control[] actions)
    {
        list.Controls.Add(new Label
        {
            Text = title, Font = Theme.FontHead(10f), ForeColor = Theme.TEXT, AutoSize = true,
            Margin = new Padding(Theme.S(4), Theme.S(10), 0, Theme.S(4)), UseMnemonic = false
        });
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        row.Controls.AddRange(actions);
        list.Controls.Add(row);
        void LayoutRow() => row.MaximumSize = new Size(Math.Max(Theme.S(72), list.ClientSize.Width - Theme.S(20)), 0);
        list.SizeChanged += (_, _) => LayoutRow();
        LayoutRow();
    }

    private Panel BuildPageTools(Panel scroll)
    {
        var content = ToolList(scroll);
        _moveUp = ActionButton("Majukan", () => MovePage(-1));
        _moveDown = ActionButton("Mundurkan", () => MovePage(1));
        _remove = ActionButton("Hapus halaman", RemovePage);
        Group(content, "Urutan", _moveUp, _moveDown, _remove);
        Group(content, "Orientasi",
            ActionButton("Putar 90°", RotatePage),
            ActionButton("Cermin", FlipPage),
            ActionButton("Halaman penuh", () => { Current.Crop = new RectangleF(0, 0, 1, 1); _canvas.Crop = Current.Crop; _canvas.Invalidate(); }));
        Group(content, "Perapihan",
            ActionButton("Cari tepi", () => DetectCurrent(crop: true, straighten: false)),
            ActionButton("Luruskan", () => DetectCurrent(crop: false, straighten: true)),
            ActionButton("Tata KTP A4…", LayoutIdCard));
        _redact = ActionButton("Tutup area", () =>
        {
            _canvas.IsRedactMode = !_canvas.IsRedactMode;
            UpdateRedactionButton();
            if (_canvas.IsRedactMode) Theme.Toast(this, "Seret pada pratinjau untuk menutup area.");
        });
        Group(content, "Markah dan sensur", _redact, ActionButton("Bersihkan sensur", () =>
        {
            Current.Redactions.Clear();
            _canvas.ActiveRedactions.Clear();
            _canvas.Invalidate();
        }), ActionButton("Watermark…", () =>
        {
            string? text = Ui.AskText(this, "Watermark halaman", "Kosongkan isian untuk menghapus watermark.", value: Current.Watermark ?? "");
            if (text == null) return;
            Current.Watermark = text.Trim();
            _canvas.ActiveWatermark = Current.Watermark;
            _canvas.Invalidate();
        }));
        Group(content, "Struk", ActionButton("Baca struk ke Excel…", () => ShowRecognizedText(editReceipt: true)));
        return content;
    }

    private Panel BuildFilterTools(Panel scroll)
    {
        var content = ToolList(scroll);
        var mode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = Theme.S(180), AccessibleName = "Filter dokumen"
        };
        mode.Items.AddRange(["Warna asli", "Abu-abu", "Hitam putih", "Warna dokumen"]);
        mode.SelectedIndex = _mode;
        mode.SelectedIndexChanged += (_, _) => { _mode = mode.SelectedIndex; QueuePreview(); };
        Group(content, "Filter dokumen", mode);
        AddSlider("Kecerahan", value => _brightness = value);
        AddSlider("Kontras", value => _contrast = value);
        return content;

        void AddSlider(string label, Action<int> update)
        {
            var slider = new TrackBar
            {
                Minimum = -50, Maximum = 50, Value = 0, SmallChange = 2, LargeChange = 10,
                TickStyle = TickStyle.None, Width = Theme.S(180), AccessibleName = label
            };
            slider.ValueChanged += (_, _) => { update(slider.Value); QueuePreview(); };
            Group(content, label, slider);
        }
    }

    private void ArrangeWorkspace()
    {
        if (_arranging || !_workspace.IsHandleCreated) return;
        bool stacked = _workspace.Width < Theme.S(780);
        var orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
        if (_workspace.Orientation == orientation && _workspace.Tag != null) return;
        _arranging = true;
        try
        {
            _workspace.Orientation = orientation;
            int extent = stacked ? _workspace.ClientSize.Height : _workspace.ClientSize.Width;
            if (extent <= _workspace.SplitterWidth) return;
            int desired = extent - (stacked ? Theme.S(216) : Theme.S(292));
            _workspace.SplitterDistance = Math.Clamp(desired, 0, Math.Max(0, extent - _workspace.SplitterWidth));
            _workspace.Tag = orientation;
        }
        finally { _arranging = false; }
    }

    private void QueuePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void ShowPage()
    {
        _previewTimer.Stop();
        var page = Current;
        try
        {
            using var preview = PdfEngine.Decode(page.FilePath, PreviewSize);
            _canvas.ActiveRedactions.Clear();
            _canvas.ActiveRedactions.AddRange(page.Redactions);
            _canvas.ActiveWatermark = page.Watermark;
            _canvas.IsRedactMode = false;
            _canvas.SetBitmap(preview, page.Rot, page.Flip, page.Crop, page.Skew, _mode, _brightness, _contrast);
            _canvas.OnCropChanged = () =>
            {
                page.Crop = _canvas.Crop;
                page.Redactions.Clear();
                page.Redactions.AddRange(_canvas.ActiveRedactions);
            };
        }
        catch (Exception ex)
        {
            _canvas.ClearBitmap();
            Ui.ShowError(this, ex);
        }
        UpdateRedactionButton();
        _previous.Enabled = _moveUp.Enabled = _index > 0;
        _next.Enabled = _moveDown.Enabled = _index < _pages.Count - 1;
        _remove.Enabled = _pages.Count > 1;
    }

    private void UpdateRedactionButton()
    {
        _redact.Text = _canvas.IsRedactMode ? "Selesai menutup area" : "Tutup area";
        _redact.BackColor = _canvas.IsRedactMode ? Theme.ERROR_BG : Theme.SURFACE2;
        _redact.ForeColor = _canvas.IsRedactMode ? Theme.ERROR : Theme.TEXT;
    }

    private void UpdatePageSelector()
    {
        _updatingPages = true;
        _pageSelector.BeginUpdate();
        try
        {
            _pageSelector.Items.Clear();
            for (int index = 0; index < _pages.Count; index++)
                _pageSelector.Items.Add($"Halaman {index + 1} / {_pages.Count}");
            _pageSelector.SelectedIndex = _index;
        }
        finally { _pageSelector.EndUpdate(); _updatingPages = false; }
    }

    private void AddPages(string[]? files)
    {
        if (files == null || files.Length == 0) return;
        var pages = ImportPages(this, files);
        if (pages.Count == 0) return;
        _index = _pages.Count;
        _pages.AddRange(pages);
        UpdatePageSelector();
        ShowPage();
    }

    private void MovePage(int direction)
    {
        int target = _index + direction;
        if (target < 0 || target >= _pages.Count) return;
        (_pages[_index], _pages[target]) = (_pages[target], _pages[_index]);
        _index = target;
        UpdatePageSelector();
        ShowPage();
    }

    private void RemovePage()
    {
        if (_pages.Count <= 1) return;
        _pages.RemoveAt(_index);
        _index = Math.Min(_index, _pages.Count - 1);
        UpdatePageSelector();
        ShowPage();
    }

    private void RotatePage()
    {
        var page = Current;
        TransformAreas(page, rect => RectangleF.FromLTRB(1 - rect.Bottom, rect.Left, 1 - rect.Top, rect.Right));
        page.Rot = (page.Rot + (page.Flip ? 270 : 90)) % 360;
        ShowPage();
    }

    private void FlipPage()
    {
        var page = Current;
        TransformAreas(page, rect => RectangleF.FromLTRB(1 - rect.Right, rect.Top, 1 - rect.Left, rect.Bottom));
        page.Skew = -page.Skew;
        page.Flip = !page.Flip;
        ShowPage();
    }

    private static void TransformAreas(ScanPage page, Func<RectangleF, RectangleF> transform)
    {
        page.Crop = transform(page.Crop);
        for (int index = 0; index < page.Redactions.Count; index++)
            page.Redactions[index] = transform(page.Redactions[index]);
    }

    private static void DetectGeometry(ScanPage page, Bitmap source, bool crop, bool straighten)
    {
        var rawOrientation = PdfEngine.Orient(source, page.Rot, page.Flip);
        using var owned = ReferenceEquals(rawOrientation, source) ? null : rawOrientation;
        float skew = straighten ? ScanAi.DetectSkew(rawOrientation, ScanAi.DetectCrop(rawOrientation)) : page.Skew;
        if (Math.Abs(skew - page.Skew) >= 0.05f)
        {
            var full = new RectangleF(0, 0, 1, 1);
            float angle = (skew - page.Skew) * MathF.PI / 180;
            float cos = MathF.Cos(angle), sin = MathF.Sin(angle), ratio = rawOrientation.Width / (float)rawOrientation.Height;
            TransformAreas(page, rect =>
            {
                if (rect == full) return rect;
                PointF Rotate(float x, float y) => new(0.5f + (x - 0.5f) * cos - (y - 0.5f) * sin / ratio,
                    0.5f + (x - 0.5f) * sin * ratio + (y - 0.5f) * cos);
                PointF[] points = [Rotate(rect.Left, rect.Top), Rotate(rect.Right, rect.Top), Rotate(rect.Left, rect.Bottom), Rotate(rect.Right, rect.Bottom)];
                return RectangleF.Intersect(full, RectangleF.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)));
            });
            if (page.Crop.Width < 0.08f || page.Crop.Height < 0.08f) page.Crop = full;
            page.Skew = skew;
        }
        if (!crop) return;
        var rotated = PdfEngine.Deskew(rawOrientation, page.Skew);
        using var ownedRotation = ReferenceEquals(rotated, rawOrientation) ? null : rotated;
        if (ScanAi.DetectCrop(rotated) is { Width: >= 0.08f, Height: >= 0.08f } bounds) page.Crop = bounds;
    }

    private void DetectCurrent(bool crop, bool straighten)
    {
        var page = Current;
        if (Ui.Busy(this, "Menganalisis halaman…", _ =>
        {
            using var source = PdfEngine.Decode(page.FilePath, 900);
            DetectGeometry(page, source, crop, straighten);
        })) ShowPage();
    }

    private void LayoutIdCard()
    {
        if (_pages.Count < 2)
        {
            Theme.Toast(this, "Tambahkan foto sisi depan dan belakang terlebih dahulu.");
            return;
        }
        using var dialog = new Dlg("Tata kartu identitas ke A4", "Pilih dua halaman yang akan ditata. Lembar A4 ditambahkan sebagai halaman baru.", 490);
        var labels = _pages.Select((_, index) => $"Halaman {index + 1}").ToArray();
        var front = dialog.Choice("Sisi depan", labels);
        var back = dialog.Choice("Sisi belakang", labels, selected: 1);
        dialog.Action("Tambahkan lembar A4", true, () =>
        {
            if (front.SelectedIndex == back.SelectedIndex) { Theme.Toast(dialog, "Pilih dua halaman yang berbeda."); return; }
            var frontPage = _pages[front.SelectedIndex];
            var backPage = _pages[back.SelectedIndex];
            string path = _nextPhotoPath();
            var (ok, page) = Ui.Busy(dialog, "Menata kartu identitas…", _ => PdfEngine.RenderKtpA4(frontPage, backPage, 1800, _brightness, _contrast, path));
            if (!ok || page == null) return;
            _pages.Add(page);
            _index = _pages.Count - 1;
            UpdatePageSelector();
            ShowPage();
            dialog.Close();
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
    }

    private void PrintCurrent()
    {
        var (ok, image) = Ui.Busy(this, "Menyiapkan halaman untuk cetak…", _ =>
            PdfEngine.ProcessScanPage(Current, _mode, 2000, _brightness, _contrast));
        using (image)
            if (ok && image != null) PrintHelper.PrintBitmap(this, image, $"Pindai halaman {_index + 1}");
    }

    private void ShowRecognizedText(bool editReceipt = false)
    {
        var (ok, text) = Ui.Busy(this, "Mengenali teks di halaman…", _ =>
        {
            using var image = PdfEngine.ProcessScanPage(Current, _mode, 2000, _brightness, _contrast);
            return WinOcrRunner.Recognize(image).Text;
        });
        if (!ok) return;
        if (string.IsNullOrWhiteSpace(text)) { Theme.Toast(this, "Teks tidak terdeteksi pada halaman ini."); return; }
        if (editReceipt) { ShowReceipt(text); return; }

        using var dialog = new Dlg($"Teks halaman {_index + 1}", "Periksa hasil OCR sebelum menyalin atau menggunakannya.", 650);
        var field = dialog.Field("Teks", text.ReplaceLineEndings(), multiline: true, lines: 8);
        dialog.Row(
            dialog.SmallButton("Baca lantang…", () => VoiceReaderManager.ShowVoiceReaderDialog(dialog, "Teks hasil pindai", field.Text)),
            dialog.SmallButton("Ringkasan…", () =>
            {
                var summary = PdfEngine.ExtractDocSummary(field.Text);
                using var info = new Dlg("Ringkasan hasil pindai", "", 470);
                info.Label("Nomor", bold: true);
                info.Label(string.Join(", ", summary.Numbers.DefaultIfEmpty("Tidak terdeteksi")));
                info.Label("Tanggal", bold: true);
                info.Label(string.Join(", ", summary.Dates.DefaultIfEmpty("Tidak terdeteksi")));
                info.Label("Nominal", bold: true);
                info.Label(string.Join(", ", summary.Amounts.DefaultIfEmpty("Tidak terdeteksi")));
                info.Action("Tutup", true, info.Close);
                info.ShowDialog(dialog);
            }),
            dialog.SmallButton("Terjemahkan istilah…", () =>
            {
                using var translate = new Dlg("Terjemahkan istilah", "Gunakan kamus istilah lokal untuk teks hasil pindai.", 470);
                var direction = translate.Choice("Arah terjemahan", ["Inggris ke Indonesia", "Indonesia ke Inggris"]);
                translate.Action("Terapkan", true, () => { field.Text = PdfEngine.TranslateDocText(field.Text, direction.SelectedIndex == 0); translate.Close(); });
                translate.ShowDialog(dialog);
            }));
        dialog.Action("Salin teks", true, () =>
        {
            if (field.TextLength > 0) { Clipboard.SetText(field.Text); Theme.Toast(dialog, "Teks disalin."); }
        });
        dialog.Action("Baca sebagai struk…", false, () => ShowReceipt(field.Text));
        dialog.ShowDialog(this);
    }

    private void ShowReceipt(string text)
    {
        var receipt = PdfEngine.ParseReceipt(text);
        using var dialog = new Dlg("Ekspor struk ke Excel", "Periksa nama toko, tanggal, jumlah, dan rincian hasil OCR.", 630);
        var merchant = dialog.Field("Toko", receipt.Merchant);
        var date = dialog.Field("Tanggal", receipt.Date);
        var total = dialog.Field("Total bayar", receipt.Total);
        var items = dialog.Field("Rincian", string.Join(Environment.NewLine, receipt.Items), multiline: true, lines: 6);
        dialog.Action("Simpan XLSX", true, () =>
        {
            var rows = new List<IReadOnlyList<string>> { new[] { "Toko", "Tanggal", "Total bayar" },
                new[] { merchant.Text.Trim(), date.Text.Trim(), total.Text.Trim() }, new[] { "Rincian struk" } };
            rows.AddRange(items.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => new[] { line.Trim() }));
            string output = AppStorage.NewOut("struk", "xlsx");
            if (!Ui.Busy(dialog, "Menyimpan rincian struk…", _ => XlsxWriter.Write(output, "Struk", rows, new HashSet<int> { 0, 2 }))) return;
            SaveResult(output, "Pindai struk ke Excel");
            dialog.Close();
            DialogResult = DialogResult.OK;
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
    }

    private void ShowExport()
    {
        using var dialog = new Dlg("Ekspor hasil pindai", $"{_pages.Count} halaman siap diekspor.", 550);
        var format = dialog.Choice("Format", ["PDF", "Word (.docx)", "Gambar JPG"]);
        var name = dialog.Field("Nama berkas", $"Pindai_{DateTime.Now:yyyyMMdd_HHmmss}");
        var aspect = dialog.Choice("Ukuran halaman", PdfEngine.ASPECTS.Select(option => option.Name));
        var quality = dialog.Choice("Kualitas atau isi", []);
        var ocr = dialog.Check("Sisipkan teks OCR agar PDF bisa dicari", false);
        var lockPdf = dialog.Check("Lindungi PDF dengan password", false);
        var password = dialog.Field("", password: true);
        password.PlaceholderText = "Password, minimal 4 karakter";
        void UpdateOptions()
        {
            bool pdf = format.SelectedIndex == PdfEngine.F_PDF;
            quality.Items.Clear();
            quality.Items.AddRange(format.SelectedIndex switch
            {
                PdfEngine.F_PDF => ["Tinggi", "Seimbang", "Ringan"],
                PdfEngine.F_WORD => ["Teks hasil OCR", "Halaman sebagai gambar"],
                _ => ["1920 px", "2560 px", "3840 px"]
            });
            quality.SelectedIndex = pdf || format.SelectedIndex == PdfEngine.F_JPG ? 1 : 0;
            ocr.Visible = lockPdf.Visible = pdf;
            password.Visible = pdf && lockPdf.Checked;
        }
        format.SelectedIndexChanged += (_, _) => UpdateOptions();
        lockPdf.CheckedChanged += (_, _) => password.Visible = lockPdf.Checked;
        UpdateOptions();
        dialog.Action("Ekspor", true, () =>
        {
            int kind = format.SelectedIndex, option = quality.SelectedIndex;
            var layout = PdfEngine.ASPECTS[aspect.SelectedIndex];
            string? encryption = kind == PdfEngine.F_PDF && lockPdf.Checked ? password.Text : null;
            if (encryption is { Length: < 4 }) { Theme.Toast(dialog, "Password harus berisi minimal empat karakter."); return; }
            string title = string.IsNullOrWhiteSpace(name.Text) ? "Pindai dokumen" : name.Text.Trim();
            bool searchable = kind == PdfEngine.F_PDF && ocr.Checked;
            if (!Export(dialog, title, kind, option, layout, searchable, encryption)) return;
            dialog.Close();
            DialogResult = DialogResult.OK;
        });
        dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
        dialog.ShowDialog(this);
    }

    private bool Export(Form owner, string title, int format, int option, AspectOpt layout, bool searchable, string? password)
    {
        if (format == PdfEngine.F_JPG)
        {
            string baseName = Path.GetFileNameWithoutExtension(AppStorage.NamedOut(title, "jpg"));
            var (ok, count) = Ui.Busy(owner, "Mengekspor halaman JPG…", progress =>
                PdfEngine.ExportJpg(_pages, baseName, _mode, _brightness, _contrast, option, layout, progress));
            if (!ok) return false;
            OutputJpgCount = count;
            return count > 0;
        }

        string output = AppStorage.NamedOut(title, format == PdfEngine.F_PDF ? "pdf" : "docx");
        if (!Ui.Busy(owner, "Menyimpan dokumen hasil pindai…", progress =>
        {
            string staged = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (format == PdfEngine.F_PDF)
                    PdfEngine.ExportPdf(_pages, staged, _mode, _brightness, _contrast, option, layout, searchable, password, progress);
                else
                    PdfEngine.ExportDocx(_pages, staged, _mode, _brightness, _contrast, option == 1, layout, progress);
                File.Move(staged, output, overwrite: false);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        })) return false;
        SaveResult(output, format == PdfEngine.F_PDF ? "Pindai dokumen ke PDF" : "Pindai dokumen ke Word");
        return true;
    }

    private void SaveResult(string path, string title)
    {
        OutputFilePath = path;
        AppStorage.AddRecent(path);
        AppStorage.SetLastJob(title, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _previewTimer.Dispose();
        base.Dispose(disposing);
    }
}
