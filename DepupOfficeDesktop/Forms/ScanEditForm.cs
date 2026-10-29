using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Porting dari ScanEditActivity.kt & CameraActivity.kt:
    /// Studio Scan & Edit Dokumen Desktop: mendukung multi-halaman, Auto-Crop AI (Otsu+Sobel),
    /// Deskew (Luruskan otomatis), 4 Preset Filter (Magic Color, B&W Dokumen, Grayscale, Warna Asli),
    /// Kecerahan/Kontras, Sensur Hitam (Redaksi NIK), Cap Watermark Diagonal, Tata KTP 2-Sisi ke 1 Lembar A4,
    /// OCR Offline + Penerjemah EN-ID + Ringkasan Poin Krusial + Deteksi Struk ke Excel,
    /// serta Modal Simpan & Ekspor (.PDF dengan enkripsi AES-128, .DOCX, .JPG).
    /// </summary>
    public class ScanEditForm : Form
    {
        private readonly List<ScanPage> _pages = new();
        private int _cur = 0;
        private int _mode = 3; // 0 = Asli, 1 = Grayscale, 2 = B&W Dokumen, 3 = Magic Color
        private int _bright = 0;
        private int _contrast = 0;

        private CropCanvasControl _cropCanvas = null!;
        private Label _subLabel = null!;
        private FlowLayoutPanel _pageChipsFlow = null!;
        private Panel _cropToolsPanel = null!;
        private Panel _filterToolsPanel = null!;
        private Button _tabCropBtn = null!;
        private Button _tabFilterBtn = null!;

        public string? OutputFilePath { get; private set; }
        public int OutputJpgCount { get; private set; }

        public ScanEditForm(IEnumerable<string> initialFiles, int initialMode = 3)
        {
            foreach (string f in initialFiles.Where(File.Exists))
                _pages.Add(new ScanPage(f));

            _mode = initialMode;

            Text = "Studio Smart Scan & Edit Dokumen — Depup Office Desktop";
            Size = new Size(1080, 780);
            MinimumSize = new Size(880, 640);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;

            if (_pages.Count == 0)
            {
                // Buat halaman sampel dokumen cerdas jika pengguna membuka kamera/scanner simulasi langsung
                _pages.Add(CreateSimulatedCameraScanPage(1));
            }

            BuildUi();
            ShowCurrentPage();
            AutoDetectPages(_pages, doCrop: true, doSkew: true, announce: true);
        }

        public static ScanPage CreateSimulatedCameraScanPage(int index)
        {
            string dir = Path.Combine(AppStorage.AppDataDir, "scans");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"scan_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{index}.jpg");

            using var bmp = new Bitmap(1200, 1650);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(ColorTranslator.FromHtml("#334155")); // Latar meja gelap agar Otsu mendeteksi tepi kertas
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillRectangle(Brushes.White, 90, 95, 1020, 1460);

                using var hFont = new Font("Segoe UI", 28f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var bFont = new Font("Segoe UI", 20f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var mFont = new Font("Consolas", 19f, FontStyle.Bold, GraphicsUnit.Pixel);

                g.DrawString("SURAT PERJANJIAN & FAKTUR RESMI", hFont, Brushes.MidnightBlue, 150, 170);
                g.DrawString($"Nomor: 0{index}/INV-DEPUP/{DateTime.Now:yyyy} • Tanggal: {DateTime.Now:dd/MM/yyyy}", bFont, Brushes.DarkSlateGray, 150, 230);
                g.DrawLine(Pens.LightSlateGray, 150, 275, 1050, 275);

                g.DrawString("Pasal 1 — Ruang Lingkup Layanan", hFont, Brushes.Black, 150, 320);
                g.DrawString("Dokumen ini dipindai menggunakan Smart Scanner Depup Office Desktop.\nSeluruh pemrosesan Auto-Crop AI, pelurusan teks (Deskew), filter dokumen\nhitam-putih adaptif, serta pengenalan teks (OCR) berjalan 100% lokal.", bFont, Brushes.Black, 150, 375);

                g.DrawString("Rincian Nota / Tagihan:", hFont, Brushes.Black, 150, 540);
                g.DrawString("1. Lisensi Depup Office Enterprise      Rp 1.500.000\n2. Modul Smart OCR & Brankas AES-256    Rp   750.000\n----------------------------------------------------\nTOTAL BAYAR:                            Rp 2.250.000", mFont, Brushes.DarkGreen, 150, 600);
            }
            File.WriteAllBytes(path, PdfEngine.EncodeJpeg(bmp, 92));
            return new ScanPage(path);
        }

        private void BuildUi()
        {
            // Top Bar
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                BackColor = Theme.SURFACE,
                Padding = new Padding(16, 10, 16, 10)
            };

            var btnClose = Theme.CreateButton("✕ Batal", false, Theme.TEXT, 38, 95);
            btnClose.Location = new Point(16, 11);
            btnClose.Click += (s, e) => Close();
            topBar.Controls.Add(btnClose);

            var lblTitle = new Label
            {
                Text = "Edit Hasil Scan & Kamera",
                Font = Theme.FontHead(13.5f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(126, 9)
            };
            topBar.Controls.Add(lblTitle);

            _subLabel = new Label
            {
                Text = $"Hal 1 dari {_pages.Count}",
                Font = Theme.FontBody(9.5f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(126, 34)
            };
            topBar.Controls.Add(_subLabel);

            var rightBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var btnVoice = Theme.CreateButton("🔊 Bacakan OCR", false, Theme.INDIGO, 36);
            btnVoice.Click += (s, e) =>
            {
                using var bmp = PdfEngine.ProcessScanPage(_pages[_cur], _mode, 1800, _bright, _contrast);
                var ocr = PdfEngine.RunOcr(bmp);
                if (string.IsNullOrWhiteSpace(ocr.Text))
                {
                    Theme.Toast(this, PdfEngine.LastOcrError ?? "Tidak ada teks terdeteksi pada halaman ini");
                    return;
                }
                VoiceReaderManager.ShowVoiceReaderDialog(this, $"Hasil Scan Hal {_cur + 1}", ocr.Text);
            };

            var btnPrint = Theme.CreateButton("🖨️ Cetak Scan", false, Theme.TEAL, 36);
            btnPrint.Click += (s, e) =>
            {
                using var bmp = PdfEngine.ProcessScanPage(_pages[_cur], _mode, 1800, _bright, _contrast);
                PrintHelper.PrintBitmap(this, bmp, $"Hasil_Scan_{_cur + 1}");
            };

            var btnAddPhoto = Theme.CreateButton("+ Tambah Foto/Scan", false, Theme.ACCENT, 36);
            btnAddPhoto.Click += (s, e) => AddPhotoPages();

            var btnSaveNext = Theme.CreateButton("Lanjut Simpan & Ekspor →", true, Theme.ACCENT, 36);
            btnSaveNext.Click += (s, e) => ShowSaveExportModal();

            rightBar.Controls.AddRange(new Control[] { btnVoice, btnPrint, btnAddPhoto, btnSaveNext });
            topBar.Controls.Add(rightBar);

            // Split Main Workspace: Left = Interactive CropCanvas, Right = Controls Panel
            var mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 620,
                BackColor = Theme.BG
            };

            // Left Panel: CropCanvas + Page Selector Chips
            var leftHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
            _cropCanvas = new CropCanvasControl { Dock = DockStyle.Fill };
            _cropCanvas.OnCropChanged = () =>
            {
                if (_cur >= 0 && _cur < _pages.Count)
                {
                    _pages[_cur].Crop = _cropCanvas.Crop;
                }
            };

            _pageChipsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(4, 6, 4, 4),
                AutoScroll = true
            };

            leftHost.Controls.Add(_cropCanvas);
            leftHost.Controls.Add(_pageChipsFlow);
            mainSplit.Panel1.Controls.Add(leftHost);

            // Right Panel: Mode Tabs (Potong vs Filter) + Action Cards
            var rightHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true };

            var tabSwitch = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight
            };
            _tabCropBtn = Theme.CreateButton("✂️ Potong & AI Tools", true, Theme.ACCENT, 36, 185);
            _tabFilterBtn = Theme.CreateButton("🎨 Filter & Kecerahan", false, Theme.ACCENT, 36, 185);
            _tabCropBtn.Click += (s, e) => SwitchRightTab(0);
            _tabFilterBtn.Click += (s, e) => SwitchRightTab(1);
            tabSwitch.Controls.AddRange(new Control[] { _tabCropBtn, _tabFilterBtn });

            // --- Crop & AI Panel ---
            _cropToolsPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 10) };
            var cropFlow = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };

            // Baris 1: Geometri Dasar
            var geoCard = new CardPanel { Width = 380, Height = 105, Margin = new Padding(0, 0, 0, 12) };
            geoCard.Controls.Add(new Label { Text = "Geometri & Orientasi Halaman", Font = Theme.FontLabel(9.5f), ForeColor = Theme.TEXT, Location = new Point(14, 10), AutoSize = true });
            var geoBtns = new FlowLayoutPanel { Location = new Point(12, 36), Size = new Size(356, 56), FlowDirection = FlowDirection.LeftToRight };
            var bRot = Theme.CreateButton("↻ Putar 90°", false, Theme.TEXT, 34);
            bRot.Click += (s, e) => RotateCurrent();
            var bFlip = Theme.CreateButton("⇄ Balikkan", false, Theme.TEXT, 34);
            bFlip.Click += (s, e) => FlipCurrent();
            var bFull = Theme.CreateButton("⛶ Potong Penuh", false, Theme.TEXT, 34);
            bFull.Click += (s, e) => { _pages[_cur].Crop = new RectangleF(0f, 0f, 1f, 1f); ShowCurrentPage(); };
            var bDelPage = Theme.CreateButton("🗑️ Hapus", false, Theme.RED, 34);
            bDelPage.Click += (s, e) => RemoveCurrentPage();
            geoBtns.Controls.AddRange(new Control[] { bRot, bFlip, bFull, bDelPage });
            geoCard.Controls.Add(geoBtns);
            cropFlow.Controls.Add(geoCard);

            // Baris 2: AI Smart Tools
            var aiCard = new CardPanel { Width = 380, Height = 110, BackColor = Theme.BLUE_BG, BorderColor = Theme.BLUE_LINE, Margin = new Padding(0, 0, 0, 12) };
            aiCard.Controls.Add(new Label { Text = "✨ Deteksi Dokumen Cerdas (Offline AI)", Font = Theme.FontLabel(9.5f), ForeColor = Theme.ACCENT, Location = new Point(14, 10), AutoSize = true });
            var aiBtns = new FlowLayoutPanel { Location = new Point(12, 38), Size = new Size(356, 56), FlowDirection = FlowDirection.LeftToRight };
            var bAutoCrop = Theme.CreateButton("🪄 Auto-Crop AI", true, Theme.ACCENT, 34);
            bAutoCrop.Click += (s, e) => AutoDetectPages(new[] { _pages[_cur] }, doCrop: true, doSkew: false, announce: false);
            var bDeskew = Theme.CreateButton("📐 Luruskan Teks", false, Theme.ACCENT, 34);
            bDeskew.BackColor = Color.White;
            bDeskew.Click += (s, e) => AutoDetectPages(new[] { _pages[_cur] }, doCrop: false, doSkew: true, announce: false);
            var bOcr = Theme.CreateButton("📝 Teks (OCR)", false, Theme.ACCENT, 34);
            bOcr.BackColor = Color.White;
            bOcr.Click += (s, e) => RunAndShowOcrModal();
            aiBtns.Controls.AddRange(new Control[] { bAutoCrop, bDeskew, bOcr });
            aiCard.Controls.Add(aiBtns);
            cropFlow.Controls.Add(aiCard);

            // Baris 3: Keamanan, Sensur NIK & Watermark
            var secCard = new CardPanel { Width = 380, Height = 115, BackColor = Theme.AMBER_BG, BorderColor = Theme.AMBER, Margin = new Padding(0, 0, 0, 12) };
            secCard.Controls.Add(new Label { Text = "🛡️ Perlindungan Privasi (Sensur NIK & Watermark)", Font = Theme.FontLabel(9.5f), ForeColor = Theme.AMBER, Location = new Point(14, 10), AutoSize = true });
            var secBtns = new FlowLayoutPanel { Location = new Point(12, 38), Size = new Size(356, 60), FlowDirection = FlowDirection.LeftToRight };
            var bRedact = Theme.CreateButton("⬛ Mode Sensur NIK", false, Theme.AMBER, 34);
            bRedact.BackColor = Color.White;
            bRedact.Click += (s, e) =>
            {
                _cropCanvas.IsRedactMode = !_cropCanvas.IsRedactMode;
                bRedact.BackColor = _cropCanvas.IsRedactMode ? Theme.AMBER : Color.White;
                bRedact.ForeColor = _cropCanvas.IsRedactMode ? Color.White : Theme.AMBER;
                Theme.Toast(this, _cropCanvas.IsRedactMode
                    ? "Mode Sensur Aktif: Seret mouse di atas NIK/data rahasia untuk menutupinya"
                    : "Mode Sensur Nonaktif");
            };
            var bWatermark = Theme.CreateButton("🏷️ Cap Watermark", false, Theme.AMBER, 34);
            bWatermark.BackColor = Color.White;
            bWatermark.Click += (s, e) => ShowWatermarkDialog();
            var bClearRedact = Theme.CreateButton("Hapus Sensur", false, Theme.RED, 34);
            bClearRedact.BackColor = Color.White;
            bClearRedact.Click += (s, e) =>
            {
                _pages[_cur].Redactions.Clear();
                _cropCanvas.ActiveRedactions.Clear();
                _cropCanvas.Invalidate();
                Theme.Toast(this, "Sensur pada halaman ini dibersihkan");
            };
            secBtns.Controls.AddRange(new Control[] { bRedact, bWatermark, bClearRedact });
            secCard.Controls.Add(secBtns);
            cropFlow.Controls.Add(secCard);

            // Baris 4: Tata KTP 2-Sisi ke 1 Lembar A4
            var ktpCard = new CardPanel { Width = 380, Height = 105, BackColor = Theme.SKY_BG, BorderColor = Theme.SKY, Margin = new Padding(0, 0, 0, 12) };
            ktpCard.Controls.Add(new Label
            {
                Text = "🪪 Tata KTP 2-Sisi ke 1 Halaman A4\nGabung foto KTP Depan & Belakang dalam 1 lembar A4 siap cetak.",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.TEXT,
                Location = new Point(14, 10),
                Size = new Size(350, 42)
            });
            var btnKtpA4 = Theme.CreateButton("🪪 Tata KTP Depan & Belakang ke A4", true, Theme.SKY, 34, 350);
            btnKtpA4.Location = new Point(14, 56);
            btnKtpA4.Click += (s, e) => LayoutKtp2SidedA4();
            ktpCard.Controls.Add(btnKtpA4);
            cropFlow.Controls.Add(ktpCard);

            _cropToolsPanel.Controls.Add(cropFlow);

            // --- Filter & Kecerahan Panel ---
            _filterToolsPanel = new Panel { Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(0, 10, 0, 10) };
            var filterFlow = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };

            var presetCard = new CardPanel { Width = 380, Height = 195, Margin = new Padding(0, 0, 0, 12) };
            presetCard.Controls.Add(new Label { Text = "Gaya Filter Dokumen (4 Preset)", Font = Theme.FontLabel(10f), ForeColor = Theme.TEXT, Location = new Point(14, 12), AutoSize = true });

            (int ModeVal, string Name, string Desc)[] presets =
            {
                (3, "✨ Magic Color (Rekomendasi)", "Kertas bersih, teks tegas & warna hidup"),
                (2, "📄 B&W Dokumen (Teks Tajam)", "Ambang adaptif integral-image hitam-putih"),
                (1, "🌑 Grayscale (Abu-abu)", "Foto dokumen netral"),
                (0, "🖼️ Warna Asli", "Tanpa filter warna")
            };

            int py = 40;
            var presetButtons = new List<(int ModeVal, Button Btn)>();
            foreach (var p in presets)
            {
                int mv = p.ModeVal;
                var b = new Button
                {
                    Text = $"{p.Name} — {p.Desc}",
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = Theme.FontLabel(9f),
                    Location = new Point(14, py),
                    Size = new Size(350, 34),
                    Cursor = Cursors.Hand
                };
                b.Click += (s, e) =>
                {
                    _mode = mv;
                    UpdatePresetButtonStyles();
                    _cropCanvas.SetFilter(_mode, _bright, _contrast);
                };
                presetButtons.Add((mv, b));
                presetCard.Controls.Add(b);
                py += 38;
            }

            void UpdatePresetButtonStyles()
            {
                foreach (var (mv, btn) in presetButtons)
                {
                    bool on = mv == _mode;
                    btn.BackColor = on ? Theme.ACCENT : Theme.SURFACE2;
                    btn.ForeColor = on ? Color.White : Theme.TEXT;
                    btn.FlatAppearance.BorderSize = 0;
                }
            }
            UpdatePresetButtonStyles();
            filterFlow.Controls.Add(presetCard);

            var sliderCard = new CardPanel { Width = 380, Height = 170 };
            var lblBright = new Label { Text = "☀️ Kecerahan: +0%", Font = Theme.FontLabel(9.5f), Location = new Point(14, 14), AutoSize = true };
            var tbBright = new TrackBar { Minimum = -50, Maximum = 50, Value = 0, TickFrequency = 10, Location = new Point(14, 36), Width = 350 };
            tbBright.Scroll += (s, e) =>
            {
                _bright = tbBright.Value;
                lblBright.Text = $"☀️ Kecerahan: {(_bright >= 0 ? "+" : "")}{_bright}%";
                _cropCanvas.SetFilter(_mode, _bright, _contrast);
            };

            var lblContrast = new Label { Text = "◐ Kontras Teks: +0%", Font = Theme.FontLabel(9.5f), Location = new Point(14, 90), AutoSize = true };
            var tbContrast = new TrackBar { Minimum = -50, Maximum = 50, Value = 0, TickFrequency = 10, Location = new Point(14, 112), Width = 350 };
            tbContrast.Scroll += (s, e) =>
            {
                _contrast = tbContrast.Value;
                lblContrast.Text = $"◐ Kontras Teks: {(_contrast >= 0 ? "+" : "")}{_contrast}%";
                _cropCanvas.SetFilter(_mode, _bright, _contrast);
            };

            sliderCard.Controls.AddRange(new Control[] { lblBright, tbBright, lblContrast, tbContrast });
            filterFlow.Controls.Add(sliderCard);
            _filterToolsPanel.Controls.Add(filterFlow);

            rightHost.Controls.Add(_filterToolsPanel);
            rightHost.Controls.Add(_cropToolsPanel);
            rightHost.Controls.Add(tabSwitch);
            mainSplit.Panel2.Controls.Add(rightHost);

            Controls.Add(mainSplit);
            Controls.Add(topBar);
        }

        private void SwitchRightTab(int idx)
        {
            _cropToolsPanel.Visible = idx == 0;
            _filterToolsPanel.Visible = idx == 1;
            _tabCropBtn.BackColor = idx == 0 ? Theme.ACCENT : Theme.BLUE_BG;
            _tabCropBtn.ForeColor = idx == 0 ? Color.White : Theme.ACCENT;
            _tabFilterBtn.BackColor = idx == 1 ? Theme.ACCENT : Theme.BLUE_BG;
            _tabFilterBtn.ForeColor = idx == 1 ? Color.White : Theme.ACCENT;
        }

        private void ShowCurrentPage()
        {
            if (_pages.Count == 0) return;
            _cur = Math.Clamp(_cur, 0, _pages.Count - 1);
            var pg = _pages[_cur];

            using var baseBmp = PdfEngine.Decode(pg.FilePath, 1400);
            _cropCanvas.ActiveRedactions.Clear();
            _cropCanvas.ActiveRedactions.AddRange(pg.Redactions);
            _cropCanvas.ActiveWatermark = pg.Watermark;
            _cropCanvas.SetBitmap(baseBmp, pg.Rot, pg.Flip, pg.Crop, pg.Skew);
            _cropCanvas.SetFilter(_mode, _bright, _contrast);
            _cropCanvas.OnCropChanged = () =>
            {
                pg.Crop = _cropCanvas.Crop;
                pg.Redactions.Clear();
                pg.Redactions.AddRange(_cropCanvas.ActiveRedactions);
            };

            _subLabel.Text = $"Hal {_cur + 1} dari {_pages.Count}";

            _pageChipsFlow.Controls.Clear();
            for (int i = 0; i < _pages.Count; i++)
            {
                int idx = i;
                bool active = idx == _cur;
                var b = new Button
                {
                    Text = $"Hal {idx + 1}",
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontLabel(9f),
                    BackColor = active ? Theme.ACCENT : Theme.SURFACE,
                    ForeColor = active ? Color.White : Theme.TEXT,
                    Size = new Size(68, 30),
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderColor = Theme.LINE;
                b.Click += (s, e) => { _cur = idx; ShowCurrentPage(); };
                _pageChipsFlow.Controls.Add(b);
            }
        }

        private void RotateCurrent()
        {
            var pg = _pages[_cur];
            var c = pg.Crop;
            pg.Crop = RectangleF.FromLTRB(1f - c.Bottom, c.Left, 1f - c.Top, c.Right);
            pg.Rot = (pg.Rot + (pg.Flip ? 270 : 90)) % 360;
            ShowCurrentPage();
        }

        private void FlipCurrent()
        {
            var pg = _pages[_cur];
            var c = pg.Crop;
            pg.Crop = RectangleF.FromLTRB(1f - c.Right, c.Top, 1f - c.Left, c.Bottom);
            pg.Skew = -pg.Skew;
            pg.Flip = !pg.Flip;
            ShowCurrentPage();
        }

        private void RemoveCurrentPage()
        {
            if (_pages.Count <= 1)
            {
                Theme.Toast(this, "Minimal harus ada satu halaman");
                return;
            }
            _pages.RemoveAt(_cur);
            ShowCurrentPage();
        }

        private void AddPhotoPages()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Tambah Foto / Gambar Halaman Scan",
                Multiselect = true,
                Filter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                foreach (string f in ofd.FileNames)
                    _pages.Add(new ScanPage(f));
                _cur = _pages.Count - 1;
                ShowCurrentPage();
            }
        }

        private void AutoDetectPages(IReadOnlyList<ScanPage> list, bool doCrop, bool doSkew, bool announce)
        {
            int found = 0, fixedSkew = 0;
            foreach (var pg in list)
            {
                try
                {
                    using var baseBmp = PdfEngine.Decode(pg.FilePath, 1000);
                    using var oriented = PdfEngine.Orient(baseBmp, pg.Rot, pg.Flip);
                    float skew = pg.Skew;
                    if (doSkew)
                        skew = ScanAi.DetectSkew(oriented, ScanAi.DetectCrop(oriented));
                    using var deskewed = PdfEngine.Deskew(oriented, skew);
                    var crop = doCrop ? ScanAi.DetectCrop(deskewed) : null;

                    if (doSkew && Math.Abs(skew - pg.Skew) > 0.05f) { pg.Skew = skew; fixedSkew++; }
                    if (doCrop && crop.HasValue) { pg.Crop = crop.Value; found++; }
                }
                catch { }
            }
            ShowCurrentPage();
            if (announce && found > 0)
                Theme.Toast(this, $"Tepi dokumen terdeteksi di {found} halaman" + (fixedSkew > 0 ? $" · {fixedSkew} diluruskan" : ""));
            else if (!announce)
                Theme.Toast(this, doCrop ? (found > 0 ? "Auto-crop AI diterapkan" : "Tepi sudah optimal") : (fixedSkew > 0 ? "Kemiringan halaman diluruskan" : "Halaman sudah lurus"));
        }

        private void ShowWatermarkDialog()
        {
            using var dlg = new Form
            {
                Text = "Cap Watermark Diagonal",
                Size = new Size(460, 250),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var lbl = new Label { Text = "Teks Watermark Diagonal:", Font = Theme.FontLabel(10f), Location = new Point(20, 18), AutoSize = true };
            var txt = new TextBox
            {
                Text = _pages.FirstOrDefault()?.Watermark ?? "HANYA UNTUK VERIFIKASI",
                Font = Theme.FontBody(10.5f),
                Location = new Point(20, 44),
                Width = 400
            };

            var presetsFlow = new FlowLayoutPanel { Location = new Point(20, 80), Size = new Size(405, 65) };
            string[] presets = { "HANYA UNTUK VERIFIKASI", "DOKUMEN RAHASIA", "CPNS 2026", "SALINAN / COPY", "DRAFT" };
            foreach (string p in presets)
            {
                string val = p;
                var chip = Theme.CreateButton(val, false, Theme.ACCENT, 28);
                chip.Font = Theme.FontLabel(8f);
                chip.Click += (s, e) => txt.Text = val;
                presetsFlow.Controls.Add(chip);
            }

            var btnApply = Theme.CreateButton("Terapkan ke Semua", true, Theme.ACCENT, 36, 200);
            btnApply.Location = new Point(20, 156);
            btnApply.Click += (s, e) =>
            {
                string w = txt.Text.Trim();
                foreach (var pg in _pages) pg.Watermark = string.IsNullOrWhiteSpace(w) ? null : w;
                _cropCanvas.ActiveWatermark = string.IsNullOrWhiteSpace(w) ? null : w;
                _cropCanvas.Invalidate();
                dlg.Close();
            };

            var btnClear = Theme.CreateButton("Hapus Watermark", false, Theme.RED, 36, 180);
            btnClear.Location = new Point(240, 156);
            btnClear.Click += (s, e) =>
            {
                foreach (var pg in _pages) pg.Watermark = null;
                _cropCanvas.ActiveWatermark = null;
                _cropCanvas.Invalidate();
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lbl, txt, presetsFlow, btnApply, btnClear });
            dlg.ShowDialog(this);
        }

        private void LayoutKtp2SidedA4()
        {
            if (_pages.Count < 2)
            {
                // Jika baru ada 1 halaman, tawarkan memilih foto sisi belakang atau gunakan halaman 1 sebagai simulasi 2 sisi
                var res = MessageBox.Show(
                    this,
                    "Tata KTP 2-Sisi idealnya menggunakan 2 foto (Sisi Depan & Sisi Belakang).\nIngin memilih foto Sisi Belakang sekarang? (Pilih 'No' untuk menata halaman saat ini sebagai Sisi Depan & Belakang).",
                    "Tata KTP 2-Sisi A4",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (res == DialogResult.Cancel) return;
                if (res == DialogResult.Yes)
                {
                    AddPhotoPages();
                    if (_pages.Count < 2) return;
                }
                else
                {
                    _pages.Add(new ScanPage(_pages[0].FilePath));
                }
            }

            var ktpPage = PdfEngine.RenderKtpA4(_pages[0], _pages[1], 1800, _bright, _contrast);
            _pages.Clear();
            _pages.Add(ktpPage);
            _cur = 0;
            ShowCurrentPage();
            Theme.Toast(this, "✓ KTP Sisi Depan & Belakang berhasil ditata dalam 1 Halaman A4!");
        }

        private void RunAndShowOcrModal()
        {
            using var bmp = PdfEngine.ProcessScanPage(_pages[_cur], _mode, 1800, _bright, _contrast);
            var ocr = PdfEngine.RunOcr(bmp);
            if (string.IsNullOrWhiteSpace(ocr.Text))
            {
                Theme.Toast(this, PdfEngine.LastOcrError ?? "Tidak ada teks terdeteksi pada halaman ini");
                return;
            }

            using var dlg = new Form
            {
                Text = $"Teks Hasil OCR Dokumen — Halaman {_cur + 1}",
                Size = new Size(640, 620),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(20)
            };

            var txtOcr = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Text = ocr.Text.Replace("\n", Environment.NewLine),
                Font = Theme.FontBody(10.5f),
                Size = new Size(580, 160),
                BackColor = Theme.SURFACE2
            };
            flow.Controls.Add(txtOcr);

            bool translated = false;
            var btnTranslate = Theme.CreateButton("🌐 Terjemahkan Dokumen (Inggris ↔ Indonesia)", false, Theme.INDIGO, 36, 580);
            btnTranslate.Margin = new Padding(0, 10, 0, 10);
            btnTranslate.Click += (s, e) =>
            {
                translated = !translated;
                txtOcr.Text = PdfEngine.TranslateDocText(ocr.Text, translated).Replace("\n", Environment.NewLine);
            };
            flow.Controls.Add(btnTranslate);

            // Ringkasan Poin Krusial Dokumen
            var summary = PdfEngine.ExtractDocSummary(ocr.Text);
            var sumCard = new CardPanel { Width = 580, Height = 96, BackColor = Theme.AMBER_BG, BorderColor = Theme.AMBER, Margin = new Padding(0, 0, 0, 10) };
            sumCard.Controls.Add(new Label
            {
                Text = "📌 RINGKASAN POIN KRUSIAL DOKUMEN\n" +
                       $"• Nomor/Pasal: {(summary.Numbers.Count > 0 ? string.Join(", ", summary.Numbers) : "-")}\n" +
                       $"• Tanggal: {(summary.Dates.Count > 0 ? string.Join(", ", summary.Dates) : "-")}\n" +
                       $"• Nominal: {(summary.Amounts.Count > 0 ? string.Join(", ", summary.Amounts) : "-")}",
                Font = Theme.FontLabel(9f),
                ForeColor = Theme.TEXT,
                Location = new Point(12, 10),
                Size = new Size(550, 76)
            });
            flow.Controls.Add(sumCard);

            // Deteksi Struk / Nota Keuangan
            var receipt = PdfEngine.ParseReceipt(ocr.Text);
            var recCard = new CardPanel { Width = 580, Height = 106, BackColor = Theme.SKY_BG, BorderColor = Theme.SKY, Margin = new Padding(0, 0, 0, 12) };
            recCard.Controls.Add(new Label
            {
                Text = $"🧾 DETEKSI STRUK / NOTA KEUANGAN\nMerchant: {receipt.Merchant} • Tanggal: {receipt.Date}\nTotal Bayar: {receipt.Total}",
                Font = Theme.FontLabel(9.5f),
                ForeColor = Theme.SKY,
                Location = new Point(12, 10),
                Size = new Size(360, 76)
            });
            var btnToExcel = Theme.CreateButton("📊 Ekspor ke Excel (.xlsx)", true, Theme.TEAL, 34, 185);
            btnToExcel.Location = new Point(380, 34);
            btnToExcel.Click += (s, e) =>
            {
                dlg.Close();
                using var sheetEd = new SheetEditorForm(templateKey: SheetEditorForm.TEMPLATE_INVOICE);
                sheetEd.ShowDialog(this);
            };
            recCard.Controls.Add(btnToExcel);
            flow.Controls.Add(recCard);

            // Baris Tombol Simpan Cepat (WPS Style)
            var expRow = new FlowLayoutPanel { Width = 580, Height = 46, FlowDirection = FlowDirection.LeftToRight };
            var bCopy = Theme.CreateButton("📋 Salin Teks", true, Theme.ACCENT, 36, 138);
            bCopy.Click += (s, e) => { Clipboard.SetText(txtOcr.Text); Theme.Toast(dlg, "Teks OCR disalin ke clipboard"); };

            var bSaveWord = Theme.CreateButton("📝 Simpan Word (.docx)", false, Theme.ACCENT, 36, 180);
            bSaveWord.Click += (s, e) =>
            {
                dlg.Close();
                ExecuteExport($"Scan_Word_{DateTime.Now:yyyyMMdd_HHmm}", PdfEngine.F_WORD, 0, PdfEngine.ASPECTS[0], true, null);
            };

            var bSavePdf = Theme.CreateButton("📄 Simpan PDF OCR", false, Theme.RED, 36, 180);
            bSavePdf.Click += (s, e) =>
            {
                dlg.Close();
                ExecuteExport($"Scan_PDF_{DateTime.Now:yyyyMMdd_HHmm}", PdfEngine.F_PDF, 1, PdfEngine.ASPECTS[0], true, null);
            };

            expRow.Controls.AddRange(new Control[] { bCopy, bSaveWord, bSavePdf });
            flow.Controls.Add(expRow);

            dlg.Controls.Add(flow);
            dlg.ShowDialog(this);
        }

        private void ShowSaveExportModal()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
            int fmt = PdfEngine.F_PDF;
            int opt = 1;
            int aIdx = 0;

            using var dlg = new Form
            {
                Text = "Simpan & Ekspor Dokumen (Gaya WPS Office)",
                Size = new Size(540, 560),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };

            var lblFmt = new Label { Text = "1. Pilih Format Simpan:", Font = Theme.FontLabel(10f), Location = new Point(22, 16), AutoSize = true };
            var cmbFmt = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(22, 40), Width = 480, Font = Theme.FontBody(10.5f) };
            cmbFmt.Items.AddRange(new object[]
            {
                "📄 Dokumen PDF (.pdf)",
                "📝 Teks / Microsoft Word (.docx)",
                "📷 Gambar Foto (.jpg ke Documents\\DepupOffice\\Gambar)"
            });
            cmbFmt.SelectedIndex = 0;

            var lblName = new Label { Text = "2. Nama Berkas:", Font = Theme.FontLabel(10f), Location = new Point(22, 84), AutoSize = true };
            var txtName = new TextBox { Text = $"Scan_Dokumen_{stamp}", Location = new Point(22, 108), Width = 480, Font = Theme.FontBody(10.5f) };

            var lblAspect = new Label { Text = "3. Rasio Aspek Halaman:", Font = Theme.FontLabel(10f), Location = new Point(22, 152), AutoSize = true };
            var cmbAspect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(22, 176), Width = 480, Font = Theme.FontBody(10f) };
            foreach (var a in PdfEngine.ASPECTS)
                cmbAspect.Items.Add(a.Name);
            cmbAspect.SelectedIndex = 0;

            var lblQuality = new Label { Text = "4. Kualitas & Resolusi:", Font = Theme.FontLabel(10f), Location = new Point(22, 220), AutoSize = true };
            var cmbQuality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(22, 244), Width = 480, Font = Theme.FontBody(10f) };

            var lblEstSize = new Label { Text = "Perkiraan ukuran berkas: menghitung...", Font = Theme.FontLabel(9.5f), ForeColor = Theme.TEAL, Location = new Point(22, 280), AutoSize = true };

            var chkOcr = new CheckBox
            {
                Text = "Sertakan lapisan teks OCR tak terlihat (PDF bisa dicari & disalin)",
                Font = Theme.FontLabel(9.5f),
                Location = new Point(22, 316),
                AutoSize = true
            };

            var chkLock = new CheckBox
            {
                Text = "Kunci PDF dengan Password (Enkripsi AES-128)",
                Font = Theme.FontLabel(9.5f),
                Location = new Point(22, 348),
                AutoSize = true
            };

            var txtPass = new TextBox
            {
                PlaceholderText = "Password PDF (minimal 4 karakter)",
                PasswordChar = '●',
                Location = new Point(22, 378),
                Width = 480,
                Visible = false,
                Font = Theme.FontBody(10f)
            };
            chkLock.CheckedChanged += (s, e) => txtPass.Visible = chkLock.Checked;

            void RefreshOptionsAndEstimate()
            {
                fmt = cmbFmt.SelectedIndex switch { 0 => PdfEngine.F_PDF, 1 => PdfEngine.F_WORD, _ => PdfEngine.F_JPG };
                cmbQuality.Items.Clear();
                if (fmt == PdfEngine.F_PDF)
                {
                    cmbQuality.Items.AddRange(new object[]
                    {
                        "Kualitas Tinggi (2000 px · detail maksimal)",
                        "Kompresi Cerdas (1600 px · Rekomendasi cetak/arsip)",
                        "Ukuran Sangat Ringan (1000 px · cocok WhatsApp & email)"
                    });
                    cmbQuality.SelectedIndex = 1;
                }
                else if (fmt == PdfEngine.F_WORD)
                {
                    cmbQuality.Items.AddRange(new object[]
                    {
                        "Teks bisa diedit (OCR otomatis ke paragraf Word)",
                        "Halaman sebagai gambar utuh di dalam Word"
                    });
                    cmbQuality.SelectedIndex = 0;
                }
                else
                {
                    cmbQuality.Items.AddRange(new object[]
                    {
                        "1080p Full HD (1920 px)",
                        "2K Quad HD (2560 px · Rekomendasi)",
                        "4K Ultra HD (3840 px)"
                    });
                    cmbQuality.SelectedIndex = 1;
                }
                chkOcr.Visible = fmt == PdfEngine.F_PDF;
                chkLock.Visible = fmt == PdfEngine.F_PDF;
                txtPass.Visible = fmt == PdfEngine.F_PDF && chkLock.Checked;
                UpdateEstimateLabel();
            }

            void UpdateEstimateLabel()
            {
                try
                {
                    opt = Math.Max(0, cmbQuality.SelectedIndex);
                    aIdx = Math.Clamp(cmbAspect.SelectedIndex, 0, PdfEngine.ASPECTS.Count - 1);
                    long estOne = PdfEngine.EstimatePageSize(_pages[0], fmt, opt, _mode, _bright, _contrast, PdfEngine.ASPECTS[aIdx], chkOcr.Checked);
                    lblEstSize.Text = $"Perkiraan ukuran total ({_pages.Count} halaman): ≈ {AppStorage.FmtSize(estOne * _pages.Count)}";
                }
                catch
                {
                    lblEstSize.Text = "Perkiraan ukuran: Ringan";
                }
            }

            cmbFmt.SelectedIndexChanged += (s, e) => RefreshOptionsAndEstimate();
            cmbQuality.SelectedIndexChanged += (s, e) => UpdateEstimateLabel();
            cmbAspect.SelectedIndexChanged += (s, e) => UpdateEstimateLabel();
            chkOcr.CheckedChanged += (s, e) => UpdateEstimateLabel();
            RefreshOptionsAndEstimate();

            var btnConfirm = Theme.CreateButton("✓ Konfirmasi Simpan & Ekspor", true, Theme.ACCENT, 44, 480);
            btnConfirm.Location = new Point(22, 435);
            btnConfirm.Click += (s, e) =>
            {
                if (fmt == PdfEngine.F_PDF && chkLock.Checked && txtPass.Text.Trim().Length < 4)
                {
                    Theme.Toast(dlg, "Password minimal 4 karakter");
                    return;
                }
                string cleanName = txtName.Text.Trim();
                if (string.IsNullOrWhiteSpace(cleanName)) cleanName = $"Scan_Dokumen_{stamp}";
                string? pw = (fmt == PdfEngine.F_PDF && chkLock.Checked) ? txtPass.Text.Trim() : null;
                dlg.Close();
                ExecuteExport(cleanName, fmt, Math.Max(0, cmbQuality.SelectedIndex), PdfEngine.ASPECTS[Math.Clamp(cmbAspect.SelectedIndex, 0, PdfEngine.ASPECTS.Count - 1)], chkOcr.Checked, pw);
            };

            pad.Controls.AddRange(new Control[]
            {
                lblFmt, cmbFmt, lblName, txtName, lblAspect, cmbAspect,
                lblQuality, cmbQuality, lblEstSize, chkOcr, chkLock, txtPass, btnConfirm
            });
            dlg.Controls.Add(pad);
            dlg.ShowDialog(this);
        }

        private void ExecuteExport(string cleanName, int fmt, int opt, AspectOpt aspect, bool ocr, string? password)
        {
            try
            {
                if (fmt == PdfEngine.F_PDF)
                {
                    string outPath = AppStorage.NamedOut(cleanName, "pdf");
                    PdfEngine.ExportPdf(_pages, outPath, _mode, _bright, _contrast, opt, aspect, ocr, password);
                    AppStorage.AddRecent(outPath);
                    AppStorage.SetLastJob("Scan Dokumen PDF", outPath);
                    OutputFilePath = outPath;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else if (fmt == PdfEngine.F_WORD)
                {
                    string outPath = AppStorage.NamedOut(cleanName, "docx");
                    PdfEngine.ExportDocx(_pages, outPath, _mode, _bright, _contrast, asImage: opt == 1, aspect);
                    AppStorage.AddRecent(outPath);
                    AppStorage.SetLastJob("Scan ke Word (.docx)", outPath);
                    OutputFilePath = outPath;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                else
                {
                    int n = PdfEngine.ExportJpg(_pages, cleanName, _mode, _bright, _contrast, opt, aspect);
                    OutputJpgCount = n;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal mengekspor hasil scan: {ex.Message}", "Ekspor Gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
