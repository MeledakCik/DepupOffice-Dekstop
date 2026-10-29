using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    public sealed class DocSection
    {
        public string Text { get; set; } = "";
        public bool IsHeading { get; set; }
        public bool IsNote { get; set; }
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public int Align { get; set; } = 0; // 0 = Kiri, 1 = Tengah, 2 = Kanan
    }

    /// <summary>
    /// Porting dari DocEditorActivity.kt:
    /// Editor Dokumen Word (.docx) Desktop dengan dukungan Templat (Surat Lamaran Kerja, CV Profesional, Kosong),
    /// pengaturan ukuran kertas (A4, Letter, F4), margin, spasi baris, Voice Reader, Cetak Langsung, dan simpan .docx + .pdf.
    /// </summary>
    public class DocEditorForm : Form
    {
        public const string TEMPLATE_SURAT = "surat";
        public const string TEMPLATE_CV = "cv";
        public const string TEMPLATE_BLANK = "blank";

        private readonly string? _sourceFilePath;
        private readonly string? _templateKey;
        private string? _sourceName;

        private TextBox _docTitleEt = null!;
        private TextBox _docSubEt = null!;
        private Label _formatInfoLbl = null!;
        private FlowLayoutPanel _sectionsBox = null!;
        private Panel _paperScroll = null!;

        private readonly List<DocSection> _sections = new();
        private int _currentFontFamily;
        private int _currentMarginMode;
        private float _currentLineSpacing;
        private string _currentPageSize;

        public DocEditorForm(string? filePath = null, string? templateKey = null)
        {
            _sourceFilePath = filePath;
            _templateKey = templateKey;

            _currentFontFamily = AppStorage.Config.DocFontFamily;
            _currentMarginMode = AppStorage.Config.DocMarginMode;
            _currentLineSpacing = AppStorage.Config.DocLineSpacing;
            _currentPageSize = AppStorage.Config.DocPageSize;

            Text = "Editor Dokumen Word — Depup Office Desktop";
            Size = new Size(980, 760);
            MinimumSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.SURFACE2;

            BuildUi();
            LoadInitialContent();
            UpdateFormatInfo();
            RenderSections();
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

            var btnBack = Theme.CreateButton("← Kembali", false, Theme.TEXT, 38, 105);
            btnBack.Location = new Point(16, 11);
            btnBack.Click += (s, e) => Close();
            topBar.Controls.Add(btnBack);

            var lblTitle = new Label
            {
                Text = "Editor Dokumen Word",
                Font = Theme.FontHead(13f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(134, 10)
            };
            topBar.Controls.Add(lblTitle);

            _formatInfoLbl = new Label
            {
                Text = $"Format .docx • Ukuran {_currentPageSize} • Spasi {_currentLineSpacing:0.##}x",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(134, 34)
            };
            topBar.Controls.Add(_formatInfoLbl);

            var rightActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 2, 0, 0)
            };

            var btnVoice = Theme.CreateButton("🔊 Voice Reader", false, Theme.INDIGO, 36);
            btnVoice.Click += (s, e) =>
            {
                string title = string.IsNullOrWhiteSpace(_docTitleEt.Text) ? "Dokumen Word" : _docTitleEt.Text.Trim();
                string fullText = string.Join("\n\n", new[] { title, _docSubEt.Text }.Concat(_sections.Select(x => x.Text)).Where(x => !string.IsNullOrWhiteSpace(x)));
                VoiceReaderManager.ShowVoiceReaderDialog(this, title, fullText);
            };

            var btnPrint = Theme.CreateButton("🖨️ Cetak", false, Theme.TEAL, 36);
            btnPrint.Click += (s, e) =>
            {
                string title = string.IsNullOrWhiteSpace(_docTitleEt.Text) ? "Dokumen Word" : _docTitleEt.Text.Trim();
                string fullText = string.Join("\n\n", new[] { title, _docSubEt.Text }.Concat(_sections.Select(x => x.Text)).Where(x => !string.IsNullOrWhiteSpace(x)));
                PrintHelper.PrintText(this, title, fullText);
            };

            var btnSettings = Theme.CreateButton("⚙️ Format", false, Theme.ACCENT, 36);
            btnSettings.Click += (s, e) => ShowWordSettingsDialog();

            var btnSave = Theme.CreateButton("💾 Simpan (.docx + .pdf)", true, Theme.ACCENT, 36);
            btnSave.Click += (s, e) => SaveWordDocument();

            rightActions.Controls.Add(btnVoice);
            rightActions.Controls.Add(btnPrint);
            rightActions.Controls.Add(btnSettings);
            rightActions.Controls.Add(btnSave);
            topBar.Controls.Add(rightActions);

            // Formatting Toolbar
            var toolBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = ColorTranslator.FromHtml("#E2E8F0"),
                Padding = new Padding(16, 6, 16, 6),
                FlowDirection = FlowDirection.LeftToRight
            };

            var btnAddPara = Theme.CreateButton("+ Paragraf Baru", false, Theme.ACCENT, 32);
            btnAddPara.Click += (s, e) => { _sections.Add(new DocSection { Text = "Ketik paragraf baru di sini..." }); RenderSections(); };

            var btnAddHeading = Theme.CreateButton("+ Judul Bab", false, Theme.ACCENT, 32);
            btnAddHeading.Click += (s, e) => { _sections.Add(new DocSection { Text = "Pasal / Judul Bagian Baru", IsHeading = true, IsBold = true }); RenderSections(); };

            var btnAddNote = Theme.CreateButton("+ Catatan / Quote", false, Theme.AMBER, 32);
            btnAddNote.Click += (s, e) => { _sections.Add(new DocSection { Text = "Catatan penting atau klausul pendukung.", IsNote = true, IsItalic = true }); RenderSections(); };

            var btnTmplSurat = Theme.CreateButton("📄 Templat Surat Lamaran", false, Theme.INDIGO, 32);
            btnTmplSurat.Click += (s, e) => { _sections.Clear(); ApplyTemplate(TEMPLATE_SURAT); RenderSections(); };

            var btnTmplCv = Theme.CreateButton("📋 Templat CV Profesional", false, Theme.TEAL, 32);
            btnTmplCv.Click += (s, e) => { _sections.Clear(); ApplyTemplate(TEMPLATE_CV); RenderSections(); };

            toolBar.Controls.AddRange(new Control[] { btnAddPara, btnAddHeading, btnAddNote, btnTmplSurat, btnTmplCv });

            // Paper Canvas Scroll Area
            _paperScroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(36, 24, 36, 36),
                BackColor = Theme.SURFACE2
            };

            var paperCard = new CardPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = Color.White,
                Padding = new Padding(32, 28, 32, 32)
            };

            var paperLayout = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true
            };

            var lblDocHeading = new Label
            {
                Text = "JUDUL DOKUMEN WORD",
                Font = Theme.FontLabel(8.5f),
                ForeColor = Theme.ACCENT,
                AutoSize = true
            };
            paperLayout.Controls.Add(lblDocHeading);

            _docTitleEt = new TextBox
            {
                Font = Theme.FontHead(16f),
                ForeColor = Theme.TEXT,
                BackColor = Theme.BG,
                BorderStyle = BorderStyle.FixedSingle,
                Width = 820,
                PlaceholderText = "Judul Dokumen Word"
            };
            paperLayout.Controls.Add(_docTitleEt);

            _docSubEt = new TextBox
            {
                Font = Theme.FontBody(10.5f),
                ForeColor = Theme.MUTED,
                BackColor = Theme.BG,
                BorderStyle = BorderStyle.FixedSingle,
                Width = 820,
                Margin = new Padding(3, 8, 3, 14),
                PlaceholderText = "Sub-judul / Nomor Dokumen (opsional)"
            };
            paperLayout.Controls.Add(_docSubEt);

            _sectionsBox = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Width = 825
            };
            paperLayout.Controls.Add(_sectionsBox);

            paperCard.Controls.Add(paperLayout);
            _paperScroll.Controls.Add(paperCard);

            Controls.Add(_paperScroll);
            Controls.Add(toolBar);
            Controls.Add(topBar);
        }

        private void LoadInitialContent()
        {
            if (!string.IsNullOrWhiteSpace(_sourceFilePath) && File.Exists(_sourceFilePath))
            {
                _sourceName = AppStorage.BaseName(Path.GetFileName(_sourceFilePath));
                var paragraphs = OfficeReader.ReadDocx(_sourceFilePath);
                if (paragraphs.Count == 0)
                {
                    SeedDefault();
                    return;
                }
                var first = paragraphs[0];
                bool useFirstAsTitle = first.Text.Length <= 100 && paragraphs.Count > 1;
                _docTitleEt.Text = useFirstAsTitle ? first.Text : (_sourceName ?? "Dokumen Word");
                foreach (var p in paragraphs.Skip(useFirstAsTitle ? 1 : 0))
                {
                    _sections.Add(new DocSection
                    {
                        Text = p.Text,
                        IsBold = p.Bold,
                        IsItalic = p.Italic,
                        Align = p.Align
                    });
                }
            }
            else if (!string.IsNullOrWhiteSpace(_templateKey))
            {
                ApplyTemplate(_templateKey);
            }
            else
            {
                SeedDefault();
            }
        }

        private void SeedDefault()
        {
            _docTitleEt.Text = "Perjanjian Kerjasama Operasional";
            _docSubEt.Text = $"Nomor: 001/PKO/DEPUP/{DateTime.Now.Year}";
            _sections.Add(new DocSection
            {
                Text = "Pasal 1 — Ketentuan Umum\nPIHAK PERTAMA dan PIHAK KEDUA dengan ini sepakat untuk menjalin kerjasama operasional sesuai dengan syarat dan ketentuan yang tercantum dalam dokumen ini."
            });
            _sections.Add(new DocSection
            {
                Text = "Catatan Tambahan: Mohon periksa kembali tanggal dan nomor dokumen sebelum ditandatangani secara digital.",
                IsNote = true
            });
        }

        private void ApplyTemplate(string key)
        {
            switch (key)
            {
                case TEMPLATE_SURAT:
                    _docTitleEt.Text = "Surat Lamaran Kerja";
                    _docSubEt.Text = $"Nomor: 001/LAMARAN/{DateTime.Now.Year}";
                    _sections.Add(new DocSection { Text = "Kepada Yth.\nHRD Manager\nPT Nama Perusahaan\nDi Tempat", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "Dengan hormat,\n\nBerdasarkan informasi lowongan pekerjaan yang saya peroleh, saya bermaksud mengajukan lamaran kerja untuk posisi yang tersedia di perusahaan Bapak/Ibu." });
                    _sections.Add(new DocSection { Text = "Data Diri\nNama: (Nama Lengkap)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nPendidikan Terakhir: (Jenjang / Jurusan)\nAlamat: (Alamat Lengkap)\nNo. HP: (Nomor Aktif)\nEmail: (Alamat Email)" });
                    _sections.Add(new DocSection { Text = "Sebagai bahan pertimbangan, saya lampirkan Curriculum Vitae, fotokopi ijazah, transkrip nilai, dan dokumen pendukung lainnya. Besar harapan saya untuk dapat diberikan kesempatan wawancara." });
                    _sections.Add(new DocSection { Text = "Demikian surat lamaran ini saya buat dengan sebenar-benarnya. Atas perhatian dan kesempatan yang diberikan, saya ucapkan terima kasih.", IsNote = true });
                    _sections.Add(new DocSection { Text = "Hormat saya,\n\n\n(Nama Lengkap)", Align = 2 });
                    break;
                case TEMPLATE_CV:
                    _docTitleEt.Text = "Curriculum Vitae";
                    _docSubEt.Text = "Data Pribadi & Riwayat Profesional";
                    _sections.Add(new DocSection { Text = "DATA PRIBADI", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "Nama Lengkap: (Nama)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nJenis Kelamin: (L/P)\nAlamat: (Alamat)\nNo. HP: (Nomor)\nEmail: (Email)" });
                    _sections.Add(new DocSection { Text = "RINGKASAN PROFIL", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "Profesional yang berorientasi pada hasil dengan pengalaman di bidang teknologi & administrasi. Terbiasa bekerja secara mandiri maupun dalam tim, teliti, dan mampu beradaptasi dengan cepat." });
                    _sections.Add(new DocSection { Text = "PENGALAMAN KERJA", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "(Tahun – Sekarang) — (Nama Perusahaan)\nPosisi: (Jabatan)\nTanggung jawab: (Uraikan pekerjaan dan pencapaian utama)" });
                    _sections.Add(new DocSection { Text = "PENDIDIKAN", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "(Tahun – Tahun) — (Nama Institusi)\nJenjang / Jurusan: (S1 / Teknik Informatika)\nIPK: (Nilai)" });
                    _sections.Add(new DocSection { Text = "KEAHLIAN", IsHeading = true, IsBold = true });
                    _sections.Add(new DocSection { Text = "• Microsoft Office & Administrasi Dokumen\n• Analisis Data & Pelaporan\n• Komunikasi & Manajemen Proyek" });
                    break;
                case TEMPLATE_BLANK:
                    _docTitleEt.Text = "";
                    _docSubEt.Text = "";
                    _sections.Add(new DocSection { Text = "" });
                    break;
                default:
                    SeedDefault();
                    break;
            }
        }

        private void UpdateFormatInfo()
        {
            string fontName = _currentFontFamily == 1 ? "Times New Roman" : "Sans (Calibri)";
            _formatInfoLbl.Text = $"Format .docx • {_currentPageSize} • Spasi {_currentLineSpacing:0.##}x • Font {fontName}";
        }

        private void RenderSections()
        {
            _sectionsBox.SuspendLayout();
            _sectionsBox.Controls.Clear();

            for (int idx = 0; idx < _sections.Count; idx++)
            {
                int index = idx;
                var sec = _sections[index];

                var card = new Panel
                {
                    Width = 820,
                    Height = sec.IsHeading ? 105 : 140,
                    BackColor = sec.IsNote ? Theme.AMBER_BG : (sec.IsHeading ? Theme.BLUE_BG : Theme.BG),
                    Padding = new Padding(12, 8, 12, 8),
                    Margin = new Padding(0, 0, 0, 10),
                    BorderStyle = BorderStyle.FixedSingle
                };

                string tag = sec.IsHeading ? "JUDUL BAB" : (sec.IsNote ? "CATATAN" : $"PARAGRAF {index + 1}");
                Color tagCol = sec.IsHeading ? Theme.ACCENT : (sec.IsNote ? Theme.AMBER : Theme.MUTED);

                var lblTag = new Label
                {
                    Text = tag,
                    Font = Theme.FontLabel(8.5f),
                    ForeColor = tagCol,
                    AutoSize = true,
                    Location = new Point(12, 10)
                };
                card.Controls.Add(lblTag);

                var btnBold = new Button
                {
                    Text = "B",
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontLabel(9f),
                    Size = new Size(32, 26),
                    Location = new Point(660, 6),
                    BackColor = sec.IsBold ? Theme.ACCENT : Color.White,
                    ForeColor = sec.IsBold ? Color.White : Theme.TEXT
                };
                btnBold.Click += (s, e) => { sec.IsBold = !sec.IsBold; RenderSections(); };

                var btnItalic = new Button
                {
                    Text = "I",
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Georgia", 9f, FontStyle.Italic),
                    Size = new Size(32, 26),
                    Location = new Point(696, 6),
                    BackColor = sec.IsItalic ? Theme.ACCENT : Color.White,
                    ForeColor = sec.IsItalic ? Color.White : Theme.TEXT
                };
                btnItalic.Click += (s, e) => { sec.IsItalic = !sec.IsItalic; RenderSections(); };

                var btnAlign = new Button
                {
                    Text = sec.Align == 1 ? "Tengah" : (sec.Align == 2 ? "Kanan" : "Kiri"),
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontBody(8.5f),
                    Size = new Size(56, 26),
                    Location = new Point(732, 6),
                    BackColor = Color.White,
                    ForeColor = Theme.TEXT
                };
                btnAlign.Click += (s, e) =>
                {
                    sec.Align = (sec.Align + 1) % 3;
                    RenderSections();
                };

                var btnDel = new Button
                {
                    Text = "✕",
                    FlatStyle = FlatStyle.Flat,
                    Font = Theme.FontLabel(8.5f),
                    Size = new Size(26, 26),
                    Location = new Point(790, 6),
                    BackColor = Theme.RED_BG,
                    ForeColor = Theme.RED
                };
                btnDel.FlatAppearance.BorderSize = 0;
                btnDel.Click += (s, e) =>
                {
                    if (_sections.Count > 1) { _sections.RemoveAt(index); RenderSections(); }
                    else Theme.Toast(this, "Minimal harus ada 1 paragraf");
                };

                FontStyle fs = FontStyle.Regular;
                if (sec.IsBold || sec.IsHeading) fs |= FontStyle.Bold;
                if (sec.IsItalic) fs |= FontStyle.Italic;
                string fam = _currentFontFamily == 1 ? "Times New Roman" : "Segoe UI";

                var et = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    Text = sec.Text.Replace("\n", Environment.NewLine),
                    Font = new Font(fam, sec.IsHeading ? 12.5f : 10.5f, fs),
                    ForeColor = sec.IsNote ? ColorTranslator.FromHtml("#92400E") : Theme.TEXT,
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = sec.Align == 1 ? HorizontalAlignment.Center : (sec.Align == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left),
                    Location = new Point(12, 36),
                    Size = new Size(796, sec.IsHeading ? 56 : 92)
                };
                et.TextChanged += (s, e) => sec.Text = et.Text.Replace("\r\n", "\n");

                card.Controls.AddRange(new Control[] { btnBold, btnItalic, btnAlign, btnDel, et });
                _sectionsBox.Controls.Add(card);
            }

            _sectionsBox.ResumeLayout();
        }

        private void ShowWordSettingsDialog()
        {
            using var dlg = new Form
            {
                Text = "Pengaturan Format Word (.docx)",
                Size = new Size(440, 350),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var lblSize = new Label { Text = "Ukuran Kertas:", Font = Theme.FontLabel(10f), Location = new Point(20, 20), AutoSize = true };
            var cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 18), Width = 210, Font = Theme.FontBody(10f) };
            cmbSize.Items.AddRange(new object[] { "A4", "Letter", "F4" });
            cmbSize.SelectedItem = _currentPageSize;

            var lblMargin = new Label { Text = "Batas Margin Halaman:", Font = Theme.FontLabel(10f), Location = new Point(20, 68), AutoSize = true };
            var cmbMargin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 66), Width = 210, Font = Theme.FontBody(10f) };
            cmbMargin.Items.AddRange(new object[] { "Normal (2.5 cm)", "Sempit (1.2 cm)", "Lebar (3.8 cm)" });
            cmbMargin.SelectedIndex = Math.Clamp(_currentMarginMode, 0, 2);

            var lblSpacing = new Label { Text = "Spasi Baris Teks:", Font = Theme.FontLabel(10f), Location = new Point(20, 116), AutoSize = true };
            var cmbSpacing = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 114), Width = 210, Font = Theme.FontBody(10f) };
            float[] spacings = { 1.0f, 1.15f, 1.5f, 2.0f };
            cmbSpacing.Items.AddRange(new object[] { "1.0x", "1.15x", "1.5x", "2.0x" });
            int spIdx = Array.FindIndex(spacings, x => Math.Abs(x - _currentLineSpacing) < 0.05f);
            cmbSpacing.SelectedIndex = spIdx >= 0 ? spIdx : 1;

            var lblFont = new Label { Text = "Jenis Huruf (Font):", Font = Theme.FontLabel(10f), Location = new Point(20, 164), AutoSize = true };
            var cmbFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 162), Width = 210, Font = Theme.FontBody(10f) };
            cmbFont.Items.AddRange(new object[] { "Sans (Calibri / Segoe UI)", "Serif (Times New Roman)" });
            cmbFont.SelectedIndex = Math.Clamp(_currentFontFamily, 0, 1);

            var btnApply = Theme.CreateButton("Simpan Pengaturan", true, Theme.ACCENT, 40, 370);
            btnApply.Location = new Point(20, 230);
            btnApply.Click += (s, e) =>
            {
                _currentPageSize = cmbSize.SelectedItem?.ToString() ?? "A4";
                _currentMarginMode = cmbMargin.SelectedIndex;
                _currentLineSpacing = spacings[Math.Clamp(cmbSpacing.SelectedIndex, 0, spacings.Length - 1)];
                _currentFontFamily = cmbFont.SelectedIndex;

                AppStorage.Config.DocPageSize = _currentPageSize;
                AppStorage.Config.DocMarginMode = _currentMarginMode;
                AppStorage.Config.DocLineSpacing = _currentLineSpacing;
                AppStorage.Config.DocFontFamily = _currentFontFamily;
                AppStorage.SaveConfig();

                UpdateFormatInfo();
                RenderSections();
                dlg.Close();
            };

            dlg.Controls.AddRange(new Control[] { lblSize, cmbSize, lblMargin, cmbMargin, lblSpacing, cmbSpacing, lblFont, cmbFont, btnApply });
            dlg.ShowDialog(this);
        }

        private void SaveWordDocument()
        {
            string title = string.IsNullOrWhiteSpace(_docTitleEt.Text) ? "Dokumen Word" : _docTitleEt.Text.Trim();
            string sub = _docSubEt.Text.Trim();
            string defaultName = _sourceName ?? AppStorage.BaseName(title);

            using var sfd = new SaveFileDialog
            {
                Title = "Simpan Dokumen Word (.docx)",
                InitialDirectory = AppStorage.OutDir,
                FileName = $"{defaultName}.docx",
                Filter = "Word Document (*.docx)|*.docx"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            string docxPath = sfd.FileName;
            string pdfPath = Path.ChangeExtension(docxPath, ".pdf");

            try
            {
                using (var writer = new DocxWriter(
                    docxPath,
                    pageSize: _currentPageSize,
                    marginMode: _currentMarginMode,
                    lineSpacing: _currentLineSpacing,
                    fontFamily: _currentFontFamily))
                {
                    writer.BeginPage();
                    writer.Paragraph(title.ToUpperInvariant(), bold: true, align: 1);
                    if (!string.IsNullOrWhiteSpace(sub)) writer.Note(sub);
                    writer.Paragraph(" ");
                    foreach (var sec in _sections)
                    {
                        if (sec.IsNote)
                            writer.Note(sec.Text);
                        else
                            writer.Paragraph(sec.Text, bold: sec.IsBold || sec.IsHeading, italic: sec.IsItalic, align: sec.Align);
                    }
                    writer.Finish();
                }

                string fullText = $"{title}\n{sub}\n\n" + string.Join("\n\n", _sections.Select(s => s.Text));
                PdfEngine.TextToPdf(fullText, pdfPath);

                AppStorage.AddRecent(docxPath);
                AppStorage.AddRecent(pdfPath);
                AppStorage.SetLastJob("Dokumen Word Dibuat", docxPath);

                var res = MessageBox.Show(
                    this,
                    $"Dokumen Word berhasil disimpan:\n• {docxPath}\n\nSalinan PDF siap cetak juga dibuat:\n• {pdfPath}\n\nBuka pratinjau dokumen sekarang?",
                    "Dokumen Word Berhasil Dibuat",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (res == DialogResult.Yes)
                {
                    using var viewer = new ViewerForm(docxPath);
                    viewer.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal menyimpan dokumen Word: {ex.Message}", "Gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
