using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Porting dari ViewerActivity.kt:
    /// Pembaca dokumen multi-format (.pdf, .docx, .xlsx, .csv, .txt, gambar)
    /// dengan 4 Tema Kertas Eye-Care (Siang, Malam, Sepia, Eye-Care Hijau), Zoom 50%-250%,
    /// Penanda (Bookmark), Tanda Tangan Digital langsung ke PDF, Voice Reader, dan Cetak.
    /// </summary>
    public class ViewerForm : Form
    {
        private readonly string _filePath;
        private readonly string _fileName;
        private readonly string _mime;
        private readonly bool _isPdf;

        private List<Bitmap> _pdfPages = new();
        private bool _locked = false; // PDF ber-password yang belum berhasil dibuka
        private int _curPage = 0;
        private float _zoom = 1.0f;
        private int _paperTheme = 0; // 0 = Siang, 1 = Malam, 2 = Sepia, 3 = Eye-Care Hijau
        private bool _marked = false;

        private Label _counterPill = null!;
        private Label _modeLabel = null!;
        private Label _zoomLabel = null!;
        private Button _btnBookmark = null!;
        private Button _btnTheme = null!;
        private Panel _stagePanel = null!;
        private Label _lblName = null!;

        private static int S(int px) => Theme.S(px);

        /// <summary>Form pemilik untuk dialog: saat tertanam di jendela utama, pakai jendela utamanya.</summary>
        private Form DlgOwner => TopLevelControl as Form ?? this;

        public ViewerForm(string filePath)
        {
            _filePath = filePath;
            _fileName = Path.GetFileName(filePath);
            _mime = AppStorage.MimeByName(_fileName);
            _isPdf = _mime == AppStorage.MIME_PDF || _fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            _paperTheme = AppStorage.Config.PdfReaderTheme;
            _zoom = Math.Clamp(AppStorage.Config.PdfDefaultZoom, 0.5f, 2.5f);
            _marked = AppStorage.IsBookmarked(_filePath);

            Text = $"{_fileName} — Depup Office Reader";
            Size = new Size(1040, 780);
            MinimumSize = new Size(780, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColorTranslator.FromHtml("#E9EDF7");

            if (_isPdf && File.Exists(_filePath))
            {
                // PDF ber-password: minta password (dilewati otomatis bila sudah dibuka sebelumnya).
                PdfEngine.EnsureUnlocked(null, _filePath);
                _locked = PdfEngine.IsLocked(_filePath);
                if (!_locked)
                    _pdfPages = PdfEngine.RenderAllPages(_filePath, 1600);
            }

            BuildUi();
            RenderStage();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var b in _pdfPages) b.Dispose();
            _pdfPages.Clear();
            base.OnFormClosed(e);
        }

        private void BuildUi()
        {
            // ---- Baris 1: tombol kembali + nama berkas ----
            var header = new Panel { Dock = DockStyle.Top, Height = S(66), BackColor = Theme.SURFACE };

            var btnBack = Theme.CreateButton("←  Kembali", false, Theme.TEXT, S(42), S(122));
            btnBack.Location = new Point(S(14), S(12));
            btnBack.Click += (s, e) => Close();
            header.Controls.Add(btnBack);

            int nameLeft = S(150);
            _lblName = new Label
            {
                Text = _fileName,
                Font = Theme.FontHead(12.5f),
                ForeColor = Theme.TEXT,
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(nameLeft, S(8)),
                Size = new Size(S(300), S(28))
            };
            header.Controls.Add(_lblName);

            _counterPill = new Label
            {
                Text = _pdfPages.Count > 0 ? $"Hal 1 / {_pdfPages.Count}" : "Dokumen",
                Font = Theme.FontLabel(9f),
                ForeColor = Theme.ACCENT,
                BackColor = Theme.BLUE_BG,
                AutoSize = true,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Location = new Point(nameLeft, S(37))
            };
            header.Controls.Add(_counterPill);
            header.Resize += (s, e) => _lblName.Width = Math.Max(S(120), header.ClientSize.Width - nameLeft - S(16));

            // ---- Baris 2: tombol aksi (membungkus ke baris baru bila sempit) ----
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Theme.SURFACE,
                Padding = new Padding(S(10), 0, S(10), S(8))
            };
            Button Act(string text, bool filled, Color col, Action click)
            {
                var b = Theme.CreateButton(text, filled, col, S(40));
                b.Margin = new Padding(S(4), S(2), S(4), S(2));
                b.Click += (s, e) => click();
                return b;
            }

            _btnTheme = Act(GetThemeButtonText(), false, Theme.INDIGO, () =>
            {
                _paperTheme = (_paperTheme + 1) % 4;
                AppStorage.Config.PdfReaderTheme = _paperTheme;
                AppStorage.SaveConfig();
                _btnTheme.Text = GetThemeButtonText();
                RenderStage();
            });
            _btnBookmark = Act(_marked ? "★ Ditandai" : "☆ Tandai", _marked, Theme.ACCENT, () =>
            {
                _marked = AppStorage.ToggleBookmark(_filePath);
                _btnBookmark.Text = _marked ? "★ Ditandai" : "☆ Tandai";
                _btnBookmark.BackColor = _marked ? Theme.ACCENT : Theme.BLUE_BG;
                _btnBookmark.ForeColor = _marked ? Color.White : Theme.ACCENT;
            });
            var btnVoice = Act("🔊 Voice Reader", false, Theme.TEAL, StartVoiceReader);
            var btnPrint = Act("🖨️ Cetak", false, Theme.ACCENT, PrintCurrentDocument);
            var btnSign = Act("✍️ Tanda Tangan PDF", false, Theme.TEAL, ShowSignDialog);
            btnSign.Visible = _isPdf && _pdfPages.Count > 0;
            var btnEdit = Act("✏️ Edit PDF", true, Theme.ACCENT, EditThisPdf);
            btnEdit.Visible = _isPdf && _pdfPages.Count > 0;
            Button? btnMore = null;
            btnMore = Act("⋮ Lainnya", false, Theme.TEXT, () => ShowMoreMenu(btnMore!));
            actions.Controls.AddRange(new Control[] { _btnTheme, _btnBookmark, btnVoice, btnPrint, btnEdit, btnSign, btnMore });

            // ---- Baris 3: status tema/zoom + navigasi halaman ----
            var statusBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Theme.BG,
                Padding = new Padding(S(14), S(6), S(14), S(6))
            };

            _modeLabel = new Label
            {
                Text = $"● Tema: {GetThemeName()} • Zoom {(int)(_zoom * 100)}%",
                Font = Theme.FontLabel(9.5f),
                ForeColor = Theme.ACCENT,
                AutoSize = true,
                Margin = new Padding(0, S(9), S(18), 0)
            };
            statusBar.Controls.Add(_modeLabel);

            Button Nav(string text, Color col, Action click)
            {
                var b = Theme.CreateButton(text, false, col, S(34));
                b.Margin = new Padding(S(3), S(2), S(3), S(2));
                b.Click += (s, e) => click();
                return b;
            }

            _zoomLabel = new Label
            {
                Text = $"{(int)(_zoom * 100)}%",
                Font = Theme.FontLabel(9.5f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Margin = new Padding(S(6), S(9), S(6), 0)
            };

            var btnPrev = Nav("↑ Sebelumnya", Theme.ACCENT, () => GoToPage(_curPage - 1));
            var btnNext = Nav("↓ Berikutnya", Theme.ACCENT, () => GoToPage(_curPage + 1));
            btnPrev.Visible = btnNext.Visible = _pdfPages.Count > 1;
            statusBar.Controls.AddRange(new Control[]
            {
                btnPrev, btnNext,
                Nav("−  Zoom", Theme.TEXT, () => SetZoom(_zoom - 0.25f)),
                _zoomLabel,
                Nav("+  Zoom", Theme.TEXT, () => SetZoom(_zoom + 0.25f))
            });

            _stagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(S(24)),
                BackColor = ColorTranslator.FromHtml("#E9EDF7")
            };

            // Urutan penambahan: yang paling akhir ditambahkan menempel paling atas.
            Controls.Add(_stagePanel);
            Controls.Add(statusBar);
            Controls.Add(actions);
            Controls.Add(header);
        }

        private string GetThemeName() => _paperTheme switch
        {
            1 => "Malam (#0F172A)",
            2 => "Sepia (#FBF0D9)",
            3 => "Eye-Care Hijau (#E2F0D9)",
            _ => "Siang (#FFFFFF)"
        };

        private string GetThemeButtonText() => _paperTheme switch
        {
            1 => "🌙 Malam",
            2 => "📜 Sepia",
            3 => "🌿 Eye-Care",
            _ => "☀️ Siang"
        };

        private Color GetPaperBackColor() => _paperTheme switch
        {
            1 => Theme.NIGHT_BG,
            2 => Theme.SEPIA_BG,
            3 => Theme.EYECARE_GREEN_BG,
            _ => Color.White
        };

        private Color GetPaperForeColor() => _paperTheme == 1 ? Color.White : Theme.TEXT;

        private void SetZoom(float z)
        {
            _zoom = Math.Clamp(z, 0.5f, 2.5f);
            _zoomLabel.Text = $"{(int)(_zoom * 100)}%";
            _modeLabel.Text = $"● Tema: {GetThemeName()} • Zoom {(int)(_zoom * 100)}%";
            RenderStage();
        }

        private void GoToPage(int pageIdx)
        {
            if (_pdfPages.Count == 0) return;
            _curPage = Math.Clamp(pageIdx, 0, _pdfPages.Count - 1);
            _counterPill.Text = $"Hal {_curPage + 1} / {_pdfPages.Count}";
            if (_stagePanel.Controls.Count > 0 && _stagePanel.Controls[0] is FlowLayoutPanel flow && _curPage < flow.Controls.Count)
            {
                flow.ScrollControlIntoView(flow.Controls[_curPage]);
            }
        }

        private Bitmap ApplyThemeToPage(Bitmap src)
        {
            if (_paperTheme == 0) return (Bitmap)src.Clone();
            int w = src.Width, h = src.Height;
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(dst);
            using var ia = new ImageAttributes();

            ColorMatrix cm = _paperTheme switch
            {
                1 => new ColorMatrix(new float[][]
                {
                    new float[] { -1,  0,  0, 0, 0 },
                    new float[] {  0, -1,  0, 0, 0 },
                    new float[] {  0,  0, -1, 0, 0 },
                    new float[] {  0,  0,  0, 1, 0 },
                    new float[] {  1,  1,  1, 0, 1 }
                }),
                2 => new ColorMatrix(new float[][]
                {
                    new float[] { 0.92f, 0,     0,     0, 0 },
                    new float[] { 0,     0.85f, 0,     0, 0 },
                    new float[] { 0,     0,     0.72f, 0, 0 },
                    new float[] { 0,     0,     0,     1, 0 },
                    new float[] { 0.06f, 0.03f, -0.02f,0, 1 }
                }),
                _ => new ColorMatrix(new float[][]
                {
                    new float[] { 0.86f, 0,     0,     0, 0 },
                    new float[] { 0,     0.95f, 0,     0, 0 },
                    new float[] { 0,     0,     0.84f, 0, 0 },
                    new float[] { 0,     0,     0,     1, 0 },
                    new float[] { -0.02f,0.03f, -0.02f,0, 1 }
                })
            };

            ia.SetColorMatrix(cm);
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            return dst;
        }

        private void TryUnlockAndReload()
        {
            if (!PdfEngine.EnsureUnlocked(DlgOwner, _filePath)) return;
            _locked = PdfEngine.IsLocked(_filePath);
            if (_locked) return;

            foreach (var b in _pdfPages) b.Dispose();
            _pdfPages = PdfEngine.RenderAllPages(_filePath, 1600);
            _curPage = 0;
            _counterPill.Text = _pdfPages.Count > 0 ? $"Hal 1 / {_pdfPages.Count}" : "Dokumen";
            RenderStage();
        }

        private void RenderStage()
        {
            _stagePanel.SuspendLayout();
            _stagePanel.Controls.Clear();
            _modeLabel.Text = $"● Tema: {GetThemeName()} • Zoom {(int)(_zoom * 100)}%";

            if (_isPdf)
            {
                if (_locked)
                {
                    var errCard = new CardPanel { Width = S(720), Height = S(210), BackColor = Theme.RED_BG, BorderColor = Theme.RED };
                    errCard.Controls.Add(new Label
                    {
                        Text = "🔒 PDF Dilindungi Password\n\nFile PDF ini dienkripsi dengan password. Klik tombol di bawah untuk memasukkan password dan membukanya di sini.",
                        Font = Theme.FontLabel(11f),
                        ForeColor = Theme.RED,
                        Dock = DockStyle.Top,
                        Height = S(120),
                        TextAlign = ContentAlignment.MiddleCenter
                    });
                    var btnUnlock = Theme.CreateButton("🔑  Masukkan Password", true, Theme.ACCENT, S(42), S(240));
                    btnUnlock.Location = new Point((S(720) - S(240)) / 2, S(140));
                    btnUnlock.Click += (s, e) => TryUnlockAndReload();
                    errCard.Controls.Add(btnUnlock);
                    _stagePanel.Controls.Add(errCard);
                }
                else
                {
                    var flow = new FlowLayoutPanel
                    {
                        Dock = DockStyle.Top,
                        FlowDirection = FlowDirection.TopDown,
                        WrapContents = false,
                        AutoSize = true
                    };

                    int baseWidth = (int)(S(760) * _zoom);
                    for (int i = 0; i < _pdfPages.Count; i++)
                    {
                        int pageIndex = i;
                        var srcBmp = _pdfPages[i];
                        var themedBmp = ApplyThemeToPage(srcBmp);
                        int targetH = (int)((float)srcBmp.Height / Math.Max(1, srcBmp.Width) * baseWidth);

                        var pb = new PictureBox
                        {
                            Image = themedBmp,
                            SizeMode = PictureBoxSizeMode.Zoom,
                            Size = new Size(baseWidth, targetH),
                            BackColor = GetPaperBackColor(),
                            Margin = new Padding(S(12), 0, S(12), S(18)),
                            BorderStyle = BorderStyle.FixedSingle
                        };
                        pb.Click += (s, e) =>
                        {
                            _curPage = pageIndex;
                            _counterPill.Text = $"Hal {_curPage + 1} / {_pdfPages.Count}";
                        };
                        flow.Controls.Add(pb);
                    }
                    _stagePanel.Controls.Add(flow);
                }
            }
            else if (_mime.StartsWith("image/"))
            {
                using var raw = PdfEngine.Decode(_filePath, 2200);
                var themed = ApplyThemeToPage(raw);
                int w = (int)(S(760) * _zoom);
                int h = (int)((float)raw.Height / Math.Max(1, raw.Width) * w);
                var pb = new PictureBox
                {
                    Image = themed,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(w, h),
                    BackColor = GetPaperBackColor(),
                    BorderStyle = BorderStyle.FixedSingle
                };
                _stagePanel.Controls.Add(pb);
            }
            else if (_mime == AppStorage.MIME_XLSX || _fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var sheet = _fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    ? OfficeReader.ReadCsv(_filePath)
                    : OfficeReader.ReadXlsx(_filePath);

                var host = new Panel { Dock = DockStyle.Fill };
                var topBanner = new CardPanel { Dock = DockStyle.Top, Height = S(68), BackColor = Theme.BLUE_BG, BorderColor = Theme.ACCENT };
                var lblBanner = new Label
                {
                    Text = $"📊 Pratinjau Spreadsheet: {sheet?.Name ?? "Sheet1"} ({sheet?.Rows.Count ?? 0} baris)",
                    Font = Theme.FontLabel(11f),
                    ForeColor = Theme.TEXT,
                    AutoSize = true,
                    Location = new Point(S(16), S(22))
                };
                var btnEditExcel = Theme.CreateButton("✏️ Buka di Editor Excel", true, Theme.TEAL, S(40));
                btnEditExcel.Location = new Point(S(400), S(14));
                topBanner.Resize += (s, e) => btnEditExcel.Left = Math.Max(lblBanner.Right + S(12), topBanner.Width - btnEditExcel.Width - S(16));
                btnEditExcel.Click += (s, e) =>
                {
                    using var editor = new SheetEditorForm(_filePath);
                    editor.ShowDialog(DlgOwner);
                };
                topBanner.Controls.AddRange(new Control[] { lblBanner, btnEditExcel });

                var dgv = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    AllowUserToAddRows = false,
                    BackgroundColor = GetPaperBackColor(),
                    ForeColor = GetPaperForeColor(),
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    Font = Theme.FontBody(10f * _zoom)
                };
                if (sheet != null && sheet.Rows.Count > 0)
                {
                    int cols = Math.Clamp(sheet.Rows.Max(r => r.Count), 1, 12);
                    for (int c = 0; c < cols; c++)
                        dgv.Columns.Add(XlsxWriter.ColumnName(c), XlsxWriter.ColumnName(c));
                    foreach (var r in sheet.Rows.Take(100))
                        dgv.Rows.Add(r.Take(cols).Cast<object>().ToArray());
                }

                host.Controls.Add(dgv);
                host.Controls.Add(topBanner);
                _stagePanel.Controls.Add(host);
            }
            else if (_mime == AppStorage.MIME_DOCX)
            {
                var paras = OfficeReader.ReadDocx(_filePath);
                var card = new CardPanel
                {
                    Width = (int)(S(820) * _zoom),
                    AutoSize = true,
                    BackColor = GetPaperBackColor(),
                    Padding = new Padding(S(32))
                };
                var flow = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true
                };

                var btnEditWord = Theme.CreateButton("✏️ Edit di Editor Word", true, Theme.ACCENT, S(40));
                btnEditWord.Click += (s, e) =>
                {
                    using var ed = new DocEditorForm(_filePath);
                    ed.ShowDialog(DlgOwner);
                };
                var btnToPdf = Theme.CreateButton("📄 Konversi ke PDF", false, Theme.RED, S(40));
                btnToPdf.Click += (s, e) =>
                {
                    string outPdf = AppStorage.NamedOut(AppStorage.BaseName(_fileName), "pdf");
                    PdfEngine.TextToPdf(string.Join("\n\n", paras.Select(p => p.Text)), outPdf);
                    AppStorage.AddRecent(outPdf);
                    Theme.Toast(this, $"PDF dibuat: {Path.GetFileName(outPdf)}");
                };

                var actionRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 18) };
                actionRow.Controls.AddRange(new Control[] { btnEditWord, btnToPdf });
                flow.Controls.Add(actionRow);

                foreach (var p in paras)
                {
                    FontStyle fs = FontStyle.Regular;
                    if (p.Bold) fs |= FontStyle.Bold;
                    if (p.Italic) fs |= FontStyle.Italic;
                    var lbl = new Label
                    {
                        Text = p.Text,
                        Font = Theme.FontBody(11f * _zoom, fs),
                        ForeColor = GetPaperForeColor(),
                        MaximumSize = new Size((int)(S(740) * _zoom), 0),
                        AutoSize = true,
                        Margin = new Padding(0, 0, 0, 12)
                    };
                    flow.Controls.Add(lbl);
                }
                card.Controls.Add(flow);
                _stagePanel.Controls.Add(card);
            }
            else
            {
                string txt = "";
                try { txt = File.ReadAllText(_filePath); } catch { }
                var rtb = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    ReadOnly = true,
                    BackColor = GetPaperBackColor(),
                    ForeColor = GetPaperForeColor(),
                    Font = Theme.FontMono(10.5f * _zoom),
                    Text = txt,
                    BorderStyle = BorderStyle.None
                };
                _stagePanel.Controls.Add(rtb);
            }

            _stagePanel.ResumeLayout();
        }

        private void StartVoiceReader()
        {
            string text = "";
            if (_isPdf && _pdfPages.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Math.Min(_pdfPages.Count, 10); i++)
                {
                    var ocr = PdfEngine.RunOcr(_pdfPages[i]);
                    sb.AppendLine($"Halaman {i + 1}:").AppendLine(ocr.Text).AppendLine();
                }
                text = sb.ToString();
                if (text.Replace("Halaman", "").Trim().Length < 12 && PdfEngine.LastOcrError != null)
                {
                    Theme.Toast(this, PdfEngine.LastOcrError);
                    return;
                }
            }
            else if (_mime == AppStorage.MIME_DOCX)
            {
                text = OfficeReader.ReadDocxText(_filePath);
            }
            else if (_mime == AppStorage.MIME_XLSX || _fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var sh = _fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                    ? OfficeReader.ReadCsv(_filePath)
                    : OfficeReader.ReadXlsx(_filePath);
                if (sh != null)
                    text = string.Join("\n", sh.Rows.Select((r, i) => $"Baris {i + 1}: " + string.Join(", ", r)));
            }
            else
            {
                try { text = File.ReadAllText(_filePath); } catch { }
            }

            VoiceReaderManager.ShowVoiceReaderDialog(DlgOwner, _fileName, text);
        }

        private void PrintCurrentDocument()
        {
            if (_isPdf)
                PrintHelper.PrintPdf(DlgOwner, _filePath, _fileName);
            else if (_mime == AppStorage.MIME_DOCX)
                PrintHelper.PrintText(DlgOwner, _fileName, OfficeReader.ReadDocxText(_filePath));
            else if (_mime.StartsWith("image/"))
            {
                using var bmp = PdfEngine.Decode(_filePath, 1800);
                PrintHelper.PrintBitmap(DlgOwner, bmp, _fileName);
            }
            else
            {
                string txt = File.Exists(_filePath) ? File.ReadAllText(_filePath) : _fileName;
                PrintHelper.PrintText(DlgOwner, _fileName, txt);
            }
        }

        private void EditThisPdf()
        {
            using var editor = new PdfEditorForm(_filePath);
            if (!editor.Ready) return;
            editor.ShowDialog(DlgOwner);
            if (!string.IsNullOrWhiteSpace(editor.SavedPath))
                Theme.Toast(DlgOwner, $"Hasil edit tersimpan: {Path.GetFileName(editor.SavedPath)}");
        }

        private void ShowSignDialog()
        {
            using var dlg = new Form
            {
                Text = "Tanda Tangan Digital PDF",
                Size = new Size(480, 380),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var lbl = new Label
            {
                Text = $"Gambar Tanda Tangan pada Halaman {_curPage + 1} dari {_pdfPages.Count}:",
                Font = Theme.FontLabel(10f),
                Location = new Point(20, 16),
                AutoSize = true
            };

            var pad = new SignaturePadControl { Location = new Point(20, 44), Size = new Size(420, 170) };

            var lblPos = new Label { Text = "Posisi TTD:", Font = Theme.FontLabel(9.5f), Location = new Point(20, 228), AutoSize = true };
            var cmbPos = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 224), Width = 140 };
            cmbPos.Items.AddRange(new object[] { "Kiri Bawah", "Tengah Bawah", "Kanan Bawah" });
            cmbPos.SelectedIndex = 2;

            var btnClear = Theme.CreateButton("Bersihkan", false, Theme.TEXT, 36, 110);
            btnClear.Location = new Point(20, 274);
            btnClear.Click += (s, e) => pad.Clear();

            var btnApply = Theme.CreateButton("✍️ Tempel Tanda Tangan ke PDF", true, Theme.TEAL, 36, 300);
            btnApply.Location = new Point(140, 274);
            btnApply.Click += (s, e) =>
            {
                using var sigBmp = pad.ToBitmap();
                if (sigBmp == null)
                {
                    Theme.Toast(dlg, "Tanda tangan masih kosong");
                    return;
                }
                string outPdf = AppStorage.NamedOut("TTD_" + AppStorage.BaseName(_fileName), "pdf");
                PdfEngine.Sign(_filePath, sigBmp, _curPage + 1, cmbPos.SelectedIndex, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Tanda Tangan PDF", outPdf);
                dlg.Close();
                MessageBox.Show(DlgOwner, $"Tanda tangan berhasil ditempel!\nDisimpan di:\n{outPdf}", "Tanda Tangan PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            dlg.Controls.AddRange(new Control[] { lbl, pad, lblPos, cmbPos, btnClear, btnApply });
            dlg.ShowDialog(DlgOwner);
        }

        private void ShowMoreMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Ubah Nama Berkas (Rename)", null, (s, e) =>
            {
                string? input = PromptInput("Ubah Nama Berkas", "Masukkan nama berkas baru:", AppStorage.BaseName(_fileName));
                if (!string.IsNullOrWhiteSpace(input))
                {
                    string? renamed = AppStorage.RenameFile(_filePath, input);
                    if (renamed != null)
                        Theme.Toast(this, $"Nama berkas diubah ke {Path.GetFileName(renamed)}");
                }
            });
            if (_isPdf && _pdfPages.Count > 0)
            {
                menu.Items.Add("Lompat ke Halaman...", null, (s, e) =>
                {
                    string? input = PromptInput("Lompat Halaman", $"Masukkan nomor halaman (1 - {_pdfPages.Count}):", $"{_curPage + 1}");
                    if (int.TryParse(input, out int p) && p >= 1 && p <= _pdfPages.Count)
                        GoToPage(p - 1);
                });
                menu.Items.Add("Sedot Foto / Ekstrak Gambar ke Dokumen", null, (s, e) =>
                {
                    int n = PdfEngine.ExtractImagesFromPdf(_filePath, AppStorage.BaseName(_fileName));
                    MessageBox.Show(DlgOwner, $"✓ {n} halaman/gambar berhasil diekstrak ke:\n{AppStorage.PicturesDir}", "Ekstrak Gambar PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }
            menu.Items.Add("Buka Lokasi Folder di Windows Explorer", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_filePath}\""); } catch { }
            });
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        private string? PromptInput(string title, string label, string defaultVal)
        {
            using var f = new Form
            {
                Text = title,
                Size = new Size(400, 180),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };
            var lbl = new Label { Text = label, Location = new Point(18, 18), AutoSize = true, Font = Theme.FontLabel(9.5f) };
            var txt = new TextBox { Text = defaultVal, Location = new Point(18, 46), Width = 345, Font = Theme.FontBody(10f) };
            var btnOk = Theme.CreateButton("OK", true, Theme.ACCENT, 34, 100);
            btnOk.Location = new Point(263, 88);
            string? result = null;
            btnOk.Click += (s, e) => { result = txt.Text.Trim(); f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.AddRange(new Control[] { lbl, txt, btnOk });
            return f.ShowDialog(DlgOwner) == DialogResult.OK ? result : null;
        }
    }
}
