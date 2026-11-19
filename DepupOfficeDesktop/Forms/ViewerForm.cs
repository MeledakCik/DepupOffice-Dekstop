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
    /// <summary>Opsi Reader: mode aman Dokumen Terlindungi (tangkapan layar diblokir, izin cetak/salin/ekspor).</summary>
    public sealed class ViewerOptions
    {
        public bool Secure;
        public string? DisplayName;
        public bool AllowPrint = true, AllowCopy = true;
        /// <summary>Ekspor PDF asli dengan password guru/pemilik (null = tombol disembunyikan).</summary>
        public Action<IWin32Window>? ExportOriginal;
        public Func<string, bool, Task<bool>>? BookmarkChanged;
        public Func<string, Task<bool>>? Rename;
        public Action? OnClosed;
    }

    /// <summary>
    /// Porting dari ViewerActivity.kt:
    /// Pembaca dokumen multi-format (.pdf, .docx, .xlsx, .pptx, .csv, .txt, gambar)
    /// dengan 4 Tema Kertas Eye-Care (Siang, Malam, Sepia, Eye-Care Hijau), Zoom 50%-250%,
    /// Penanda (Bookmark), Tanda Tangan Digital langsung ke PDF, Voice Reader, dan Cetak.
    /// </summary>
    public class ViewerForm : Form
    {
        internal event Action? DocumentReady;
        private readonly ViewerOptions _opt;
        private readonly bool _isPptx;
        private readonly string _filePath;
        private readonly string _fileName;
        private readonly string _mime;
        private readonly bool _isPdf;

        private PagedDocument? _pagedDocument;
        private PagedReaderCanvas? _pagedCanvas;
        private readonly CancellationTokenSource _readerLifetime = new();
        private Task<(PdfOpenStatus Status, PagedDocument? Document)>? _documentLoad;
        private bool _closing;
        private Label? _pageMessage;
        private Button? _unlockButton;
        private readonly List<Button> _pageActions = new();
        private int PageCount => _pagedDocument?.Count ?? 0;
        private OfficeReader.Sheet? _spreadsheet;
        private DateTime _spreadsheetModified;
        private int _curPage = 0;
        private float _zoom = 1.0f;
        private int _paperTheme = 0; // 0 = Siang, 1 = Malam, 2 = Sepia, 3 = Eye-Care Hijau
        private bool _marked = false;

        private Label _counterPill = null!;
        private Label _modeLabel = null!;
        private Label _zoomLabel = null!;
        private Button _btnBookmark = null!;
        private Button _btnTheme = null!;
        private Button _previousPage = null!;
        private Button _nextPage = null!;
        private Panel _stagePanel = null!;
        private Label _lblName = null!;

        private static int S(int px) => Theme.S(px);

        /// <summary>Form pemilik untuk dialog: saat tertanam di jendela utama, pakai jendela utamanya.</summary>
        private Form DlgOwner => TopLevelControl as Form ?? this;

        public ViewerForm(string filePath, ViewerOptions? options = null)
        {
            _opt = options ?? new ViewerOptions();
            _filePath = filePath;
            _fileName = _opt.DisplayName ?? Path.GetFileName(filePath);
            _isPptx = filePath.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase);
            _mime = AppStorage.MimeByName(_fileName);
            _isPdf = _mime == AppStorage.MIME_PDF || _fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

            _paperTheme = AppStorage.Config.PdfReaderTheme;
            _zoom = Math.Clamp(AppStorage.Config.PdfDefaultZoom, 0.5f, 2.5f);
            _marked = AppStorage.IsBookmarked(_filePath);

            Text = $"{_fileName} — Depup Office Reader";
            Size = new Size(1040, 780);
            MinimumSize = new Size(780, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;

            BuildUi();
            if (_isPdf || _isPptx)
                ShowPageMessage("Membuka dokumen…");
        }

        private IntPtr _securedHwnd;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_opt.Secure)
            {
                // Jendela tertanam: perlindungan dipasang di jendela tingkat-atas (jendela utama).
                var top = TopLevelControl as Form ?? this;
                if (SecureDisplay.Protect(top.Handle, true)) _securedHwnd = top.Handle;
            }
            if (_isPdf || _isPptx)
                _ = OpenPagesAsync();
            else
            {
                RenderStage();
                DocumentReady?.Invoke();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            _readerLifetime.Cancel();
            var document = _pagedDocument;
            var pending = _pagedCanvas?.PendingRender ?? Task.CompletedTask;
            ClearStage();
            if (_securedHwnd != IntPtr.Zero) SecureDisplay.Protect(_securedHwnd, false);
            _ = ReleasePagesAsync(pending, document);
            if (_opt.OnClosed != null)
            {
                // Do not leave the protected temporary file behind when the main message loop exits.
                // The open task runs on the thread pool and no page rendering reads the file after opening.
                try { _documentLoad?.GetAwaiter().GetResult(); }
                catch (Exception) { }
                _opt.OnClosed();
            }
            base.OnFormClosed(e);
        }

        private static async Task ReleasePagesAsync(Task rendering, PagedDocument? document)
        {
            try { await rendering.ConfigureAwait(false); }
            finally { document?.Dispose(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _readerLifetime.Dispose();
            base.Dispose(disposing);
        }

        private void StartSlideshow()
        {
            if (_pagedDocument == null) return;
            using var f = new SlideshowForm(_pagedDocument, _fileName, secure: _opt.Secure, start: _curPage);
            f.ShowDialog(DlgOwner);
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
                Text = _isPptx ? "Presentasi" : "Dokumen",
                Font = Theme.FontLabel(9f),
                ForeColor = Theme.ACCENT_LIGHT,
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
            _btnBookmark = Act(_marked ? "★ Ditandai" : "☆ Tandai", _marked, Theme.ACCENT, async () =>
            {
                _btnBookmark.Enabled = false;
                try
                {
                    _marked = _opt.BookmarkChanged == null ? !_marked : await _opt.BookmarkChanged(_filePath, !_marked);
                    AppStorage.SetBookmark(_filePath, _marked);
                    if (IsDisposed) return;
                    _btnBookmark.Text = _marked ? "★ Ditandai" : "☆ Tandai";
                    _btnBookmark.BackColor = _marked ? Theme.ACCENT : Theme.BLUE_BG;
                    _btnBookmark.ForeColor = _marked ? Color.White : Theme.ACCENT_LIGHT;
                }
                catch (Exception ex) { if (!IsDisposed) Ui.ShowError(DlgOwner, ex); }
                finally { if (!IsDisposed) _btnBookmark.Enabled = true; }
            });
            var btnVoice = Act("🔊 Voice Reader", false, Theme.TEAL, StartVoiceReader);
            var btnPrint = Act("🖨️ Cetak", false, Theme.ACCENT, PrintCurrentDocument);
            var btnSign = Act("✍️ Tanda Tangan PDF", false, Theme.TEAL, ShowSignDialog);
            btnSign.Visible = _isPdf;
            var btnEdit = Act("✏️ Edit PDF", true, Theme.ACCENT, EditThisPdf);
            btnEdit.Visible = _isPdf;
            var btnShow = Act("▶ Slideshow", false, Theme.INDIGO, StartSlideshow);
            btnShow.Visible = _isPdf || _isPptx;
            var btnSlidesPdf = Act("Ekspor PDF…", false, Theme.RED, ExportSlidesToPdf);
            btnSlidesPdf.Visible = _isPptx && !_opt.Secure;
            Button? btnMore = null;
            btnMore = Act("⋮ Lainnya", false, Theme.TEXT, () => ShowMoreMenu(btnMore!));
            btnMore.Visible = !_isPptx;
            if (_isPdf || _isPptx)
            {
                _pageActions.AddRange(new[] { btnVoice, btnPrint, btnShow, btnEdit, btnSign, btnMore, btnSlidesPdf });
                foreach (var button in _pageActions) button.Enabled = false;
            }
            actions.Controls.AddRange(new Control[] { _btnTheme, _btnBookmark, btnVoice, btnPrint, btnShow, btnEdit, btnSign, btnSlidesPdf, btnMore });
            if (_opt.Secure)
            {
                // Dokumen Terlindungi: tanpa sunting/tanda tangan/ekspor; cetak & baca-lantang mengikuti izin dokumen.
                btnEdit.Visible = btnSign.Visible = btnMore.Visible = _btnBookmark.Visible = false;
                btnPrint.Visible = _opt.AllowPrint;
                btnVoice.Visible = _opt.AllowCopy;
                if (_opt.ExportOriginal != null)
                    actions.Controls.Add(Act("🔑 Ekspor PDF Asli", false, Theme.RED, () => _opt.ExportOriginal(DlgOwner)));
                var secPill = new Label
                {
                    Text = "🔒 Dokumen Terlindungi • tangkapan & rekam layar diblokir" + (_opt.AllowCopy ? "" : " • salin dinonaktifkan") + (_opt.AllowPrint ? "" : " • cetak dinonaktifkan"),
                    Font = Theme.FontLabel(9f), ForeColor = Theme.RED, BackColor = Theme.RED_BG, AutoSize = true,
                    Padding = new Padding(S(8), S(3), S(8), S(3)), Margin = new Padding(S(8), S(10), S(4), S(2))
                };
                actions.Controls.Add(secPill);
            }

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

            _previousPage = Nav("↑ Sebelumnya", Theme.ACCENT, () => _pagedCanvas?.StepPage(-1));
            _nextPage = Nav("↓ Berikutnya", Theme.ACCENT, () => _pagedCanvas?.StepPage(1));
            _previousPage.Visible = _nextPage.Visible = _isPdf || _isPptx;
            _previousPage.Enabled = _nextPage.Enabled = false;
            statusBar.Controls.AddRange(new Control[]
            {
                _previousPage, _nextPage,
                Nav("−  Zoom", Theme.TEXT, () => SetZoom(_zoom - 0.25f)),
                _zoomLabel,
                Nav("+  Zoom", Theme.TEXT, () => SetZoom(_zoom + 0.25f))
            });

            _stagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(S(24)),
                BackColor = Theme.BG
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

        private Color GetPaperForeColor() => _paperTheme == 1 ? Color.White : Theme.PAPER_INK;

        private void SetZoom(float z)
        {
            _zoom = Math.Clamp(z, 0.25f, 4f);
            UpdateZoomLabels();
            if (WebStageActive) { _office!.Zoom = _zoom; return; }   // Word/Excel: zoom halus tanpa membangun ulang
            _zoom = Math.Clamp(_zoom, 0.5f, 2.5f);
            UpdateZoomLabels();
            RenderStage();
        }

        private void UpdateZoomLabels()
        {
            _zoomLabel.Text = $"{(int)Math.Round(_zoom * 100)}%";
            _modeLabel.Text = $"● Tema: {GetThemeName()} • Zoom {(int)Math.Round(_zoom * 100)}%";
        }

        // ---------- Word/Excel lewat WebView2 (tata letak halaman, tabel, gambar seperti Android) ----------
        private OfficeWebView? _office;
        private Control? _officeHost;
        private bool WebStageActive => _office != null && !_office.IsDisposed && _officeHost != null && _stagePanel.Controls.Contains(_officeHost);
        private bool UseWebOffice => (_mime == AppStorage.MIME_DOCX || _mime == AppStorage.MIME_XLSX) && OfficeWebView.RuntimeAvailable();
        private bool IsSpreadsheet => _mime == AppStorage.MIME_XLSX || _mime == "text/csv";

        private OfficeReader.Sheet? ReadSpreadsheet()
        {
            DateTime modified = File.GetLastWriteTimeUtc(_filePath);
            if (_spreadsheet == null || modified != _spreadsheetModified)
            {
                _spreadsheet = _mime == "text/csv" ? OfficeReader.ReadCsv(_filePath) : OfficeReader.ReadXlsx(_filePath);
                _spreadsheetModified = modified;
            }
            return _spreadsheet;
        }

        private void ReloadOfficeStage()
        {
            if (IsDisposed || _officeHost == null) return;
            _stagePanel.Controls.Remove(_officeHost);
            _officeHost.Dispose();
            _officeHost = null; _office = null;
            RenderStage();
        }

        /// <summary>Panggung WebView: bilah aksi + penampil. Dokumen dirender di thread latar agar UI tetap responsif.</summary>
        private Control BuildOfficeStage(bool isWord)
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG };
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(8), S(6), S(8), S(6)), BackColor = Theme.TOOLBAR, WrapContents = true
            };
            var office = new OfficeWebView { Dock = DockStyle.Fill, AllowCopy = !_opt.Secure || _opt.AllowCopy };
            _office = office;

            var btnEdit = Theme.CreateButton(isWord ? "✏️ Edit di Editor Word" : "✏️ Buka di Editor Excel", true, isWord ? Theme.ACCENT : Theme.TEAL, S(40));
            btnEdit.Margin = new Padding(S(4), 0, S(4), 0);
            btnEdit.Click += (s, e) =>
            {
                var before = File.Exists(_filePath) ? File.GetLastWriteTimeUtc(_filePath) : default;
                if (isWord) { using var ed = new DocEditorForm(_filePath); ed.ShowDialog(DlgOwner); }
                else { using var ed = new SheetEditorForm(_filePath); ed.ShowDialog(DlgOwner); }
                // berkas disimpan di editor → tampilkan versi terbaru
                if (File.Exists(_filePath) && File.GetLastWriteTimeUtc(_filePath) != before) BeginInvoke(new Action(ReloadOfficeStage));
            };
            var btnPdf = Theme.CreateButton("Ekspor PDF…", false, Theme.RED, S(36));
            btnPdf.Enabled = false;
            btnPdf.Margin = new Padding(S(4), 0, S(4), 0);
            btnPdf.Click += async (s, e) =>
            {
                string? path = PickPdfExportPath();
                if (path == null) return;
                string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                btnPdf.Enabled = false;
                try
                {
                    if (await office.SaveAsPdfAsync(staged))
                    {
                        File.Move(staged, path, overwrite: true);
                        RecordPdfExport(path);
                    }
                    else if (!_closing) Theme.Toast(DlgOwner, "PDF gagal diekspor.");
                }
                catch (Exception ex) { if (!_closing) Ui.ShowError(DlgOwner, ex); }
                finally
                {
                    if (File.Exists(staged)) File.Delete(staged);
                    if (!btnPdf.IsDisposed) btnPdf.Enabled = true;
                }
            };
            var hint = new Label
            {
                Text = isWord ? "Ctrl+F untuk mencari teks" : "Ekspor PDF menggunakan lembar yang sedang tampil.",
                AutoSize = true, Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, Margin = new Padding(S(12), S(12), 0, 0)
            };
            if (!_opt.Secure) bar.Controls.AddRange(new Control[] { btnEdit, btnPdf });
            bar.Controls.Add(hint);

            office.ZoomChanged += z => { if (IsDisposed) return; _zoom = (float)z; UpdateZoomLabels(); };
            office.DocumentReady += async n =>
            {
                if (IsDisposed) return;
                btnPdf.Enabled = true;
                office.ApplyPaperTheme(_paperTheme);
                if (n > 0) _counterPill.Text = $"{n} halaman";
                if (isWord)
                {
                    // halaman lebih lebar dari layar (mis. lanskap): perkecil agar pas lebar; selain itu pakai zoom bawaan
                    double fit = await office.FitWidthAsync();
                    _zoom = (float)Math.Min(fit, _zoom);
                    office.Zoom = _zoom;
                }
                else office.Zoom = _zoom;
                UpdateZoomLabels();
            };
            host.Controls.Add(office);
            host.Controls.Add(bar);
            _officeHost = host;

            string path = _filePath, title = _fileName;
            _ = Task.Run(() => isWord ? Core.Office.DocxHtml.Render(path, title) : Core.Office.XlsxHtml.Render(path, title))
                .ContinueWith(t =>
                {
                    if (office.IsDisposed) { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); return; }
                    office.BeginInvoke(new Action(async () =>
                    {
                        if (office.IsDisposed) { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); return; }
                        if (t.Status == TaskStatus.RanToCompletion) await office.LoadAsync(t.Result);
                        else office.ShowMessage("Dokumen tidak dapat dibaca.\n" + (t.Exception?.GetBaseException().Message ?? ""));
                    }));
                });
            return host;
        }

        private string? PickPdfExportPath()
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Ekspor ke PDF", Filter = Ui.FilterPdf, DefaultExt = "pdf", AddExtension = true,
                InitialDirectory = AppStorage.OutDir, FileName = AppStorage.BaseName(_fileName) + ".pdf"
            };
            return dialog.ShowDialog(DlgOwner) == DialogResult.OK ? dialog.FileName : null;
        }

        private void RecordPdfExport(string path)
        {
            AppStorage.AddRecent(path);
            AppStorage.SetLastJob("Office ke PDF", path);
            if (!_closing) Theme.Toast(DlgOwner, $"PDF tersimpan di {path}");
        }

        private void ExportSlidesToPdf()
        {
            if (_opt.Secure || _pagedDocument == null) return;
            string? path = PickPdfExportPath();
            if (path == null) return;
            if (Ui.Busy(DlgOwner, "Mengekspor slide ke PDF…", progress =>
                    PdfToolsX.ExportPresentation(_filePath, path, progress)))
                RecordPdfExport(path);
        }

        private void GoToPage(int pageIdx)
        {
            _pagedCanvas?.GoToPage(pageIdx);
        }

        private void UpdatePageIndicator()
        {
            _counterPill.Text = $"{(_isPptx ? "Slide" : "Halaman")} {_curPage + 1} / {PageCount}";
            _previousPage.Enabled = _curPage > 0;
            _nextPage.Enabled = _curPage + 1 < PageCount;
        }

        private void ShowPageMessage(string message, bool allowUnlock = false)
        {
            if (_pageMessage == null)
            {
                var box = new Panel { Dock = DockStyle.Top, Height = S(144), Padding = new Padding(S(18)) };
                _pageMessage = new Label
                {
                    Dock = DockStyle.Top, Height = S(58), Font = Theme.FontBody(11f), ForeColor = Theme.MUTED,
                    TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
                };
                _unlockButton = Theme.CreateButton("Masukkan password", true, Theme.ACCENT, S(42), S(200));
                _unlockButton.Location = new Point(S(18), S(88));
                _unlockButton.Click += (s, e) => _ = OpenPagesAsync(askPassword: true);
                box.Controls.AddRange(new Control[] { _unlockButton, _pageMessage });
                _stagePanel.Controls.Add(box);
            }
            _pageMessage.Text = message;
            _unlockButton!.Visible = allowUnlock;
        }

        private async Task OpenPagesAsync(bool askPassword = false)
        {
            try
            {
                if (askPassword && !PdfEngine.EnsureUnlocked(DlgOwner, _filePath)) return;
                ShowPageMessage("Membuka dokumen…");
                var token = _readerLifetime.Token;
                _documentLoad = Task.Run(() => PagedDocument.Open(_filePath, token), token);
                var (status, document) = await _documentLoad;
                if (_closing || IsDisposed) { document?.Dispose(); return; }
                if (document is not { Count: > 0 })
                {
                    document?.Dispose();
                    bool locked = status is PdfOpenStatus.PasswordRequired or PdfOpenStatus.WrongPassword;
                    ShowPageMessage(locked
                        ? "PDF ini memerlukan password."
                        : "Dokumen tidak dapat ditampilkan. Periksa format dan kondisi berkas.", locked);
                    if (locked) _unlockButton?.Focus();
                    return;
                }

                _pagedDocument = document;
                ClearStage();
                _stagePanel.Padding = Padding.Empty;
                _stagePanel.AutoScroll = false;
                _pagedCanvas = new PagedReaderCanvas(document, _zoom, _paperTheme) { Dock = DockStyle.Fill };
                _pagedCanvas.PageChanged += page => { _curPage = page; UpdatePageIndicator(); };
                _stagePanel.Controls.Add(_pagedCanvas);
                foreach (var button in _pageActions) button.Enabled = true;
                UpdatePageIndicator();
                DocumentReady?.Invoke();
            }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception ex)
            {
                if (!_closing && !IsDisposed) ShowPageMessage($"Dokumen gagal dibuka. {ex.Message}");
            }
        }

        private Bitmap ApplyThemeToPage(Bitmap src)
        {
            using var tint = PagedReaderCanvas.CreatePaperTint(_paperTheme);
            if (tint == null) return src;
            var result = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImage(src, new Rectangle(Point.Empty, src.Size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, tint);
            return result;
        }

        /// <summary>
        /// Kosongkan panggung DAN buang kontrol + gambar bertemanya. Controls.Clear() saja tidak membuang apa pun, sehingga
        /// tiap ganti zoom/tema dulu membocorkan satu salinan bitmap per halaman.
        /// </summary>
        private void ClearStage()
        {
            var old = _stagePanel.Controls.Cast<Control>().ToList();
            _stagePanel.Controls.Clear();
            void Dispose(Control c)
            {
                foreach (var ch in c.Controls.Cast<Control>().ToList()) Dispose(ch);
                if (c is PictureBox pb && pb.Image is { } img)
                {
                    pb.Image = null;
                    img.Dispose();
                }
                c.Dispose();
            }
            foreach (var c in old) Dispose(c);
            _pageMessage = null;
            _unlockButton = null;
        }

        private void RenderStage()
        {
            if (_isPdf || _isPptx)
            {
                _pagedCanvas?.SetAppearance(_zoom, _paperTheme);
                UpdateZoomLabels();
                return;
            }
            if (WebStageActive)
            {
                // Word/Excel di WebView: ganti tema tanpa memuat ulang dokumen
                _office!.ApplyPaperTheme(_paperTheme);
                UpdateZoomLabels();
                return;
            }
            _stagePanel.SuspendLayout();
            ClearStage();
            _modeLabel.Text = $"● Tema: {GetThemeName()} • Zoom {(int)(_zoom * 100)}%";

            if (UseWebOffice)
            {
                _stagePanel.Padding = Padding.Empty;
                _stagePanel.AutoScroll = false;
                _stagePanel.Controls.Add(BuildOfficeStage(_mime == AppStorage.MIME_DOCX));
            }
            else if (_mime.StartsWith("image/"))
            {
                var raw = PdfEngine.Decode(_filePath, 2200);
                var themed = ApplyThemeToPage(raw);
                if (!ReferenceEquals(themed, raw)) raw.Dispose();   // tema Siang: raw dipakai langsung & dibuang oleh ClearStage
                int w = (int)(S(760) * _zoom);
                int h = (int)((float)themed.Height / Math.Max(1, themed.Width) * w);
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
            else if (IsSpreadsheet)
            {
                var sheet = ReadSpreadsheet();
                var host = new Panel { Dock = DockStyle.Fill };
                var topBanner = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Theme.SURFACE,
                    Padding = new Padding(S(8))
                };
                var lblBanner = new Label
                {
                    Text = $"{sheet?.Name ?? "Spreadsheet"}  ·  {sheet?.Rows.Count ?? 0} baris",
                    Font = Theme.FontLabel(10f), ForeColor = Theme.TEXT, AutoSize = true,
                    Margin = new Padding(S(8), S(12), S(12), 0), UseMnemonic = false
                };
                var edit = Theme.CreateButton("Edit spreadsheet", false, Theme.TEAL, S(36));
                edit.Click += (s, e) =>
                {
                    using var editor = new SheetEditorForm(_filePath);
                    editor.ShowDialog(DlgOwner);
                };
                topBanner.Controls.AddRange(new Control[] { lblBanner, edit });

                var dgv = new DataGridView
                {
                    Dock = DockStyle.Fill, ReadOnly = true, VirtualMode = true,
                    AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                    BackgroundColor = GetPaperBackColor(), ForeColor = GetPaperForeColor(),
                    Font = Theme.FontBody(10f * _zoom),
                    RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
                };
                dgv.DefaultCellStyle.BackColor = GetPaperBackColor();
                dgv.DefaultCellStyle.ForeColor = GetPaperForeColor();
                dgv.RowTemplate.Height = Math.Max(S(24), (int)(S(27) * _zoom));
                if (sheet is { Rows.Count: > 0 })
                {
                    int cols = Math.Clamp(sheet.Rows.Max(row => row.Count), 1, 16_384);
                    for (int column = 0; column < cols; column++)
                        dgv.Columns.Add(new DataGridViewTextBoxColumn
                        {
                            HeaderText = XlsxWriter.ColumnName(column),
                            Width = Math.Max(S(64), (int)(S(136) * _zoom)),
                            SortMode = DataGridViewColumnSortMode.NotSortable
                        });
                    dgv.CellValueNeeded += (s, e) =>
                    {
                        var row = sheet.Rows[e.RowIndex];
                        if (e.ColumnIndex < row.Count) e.Value = row[e.ColumnIndex];
                    };
                    dgv.RowCount = sheet.Rows.Count;
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

        private async void StartVoiceReader()
        {
            string text = "";
            if (_isPptx)
            {
                try { text = string.Join("\n\n", PptxRenderer.SlideTexts(_filePath).Select((t, i) => $"Slide {i + 1}:\n{t}")); } catch { }
            }
            else if (_isPdf && _pagedDocument is { } document)
            {
                var range = (First: _curPage, Last: _curPage);
                if (document.Count > 1)
                {
                    using var dialog = new Dlg("Bacakan PDF", "Pilih rentang halaman yang ingin didengar.", 435);
                    var first = dialog.Number("Mulai halaman", 1, document.Count, _curPage + 1);
                    var last = dialog.Number("Sampai halaman", 1, document.Count, Math.Min(document.Count, _curPage + 10));
                    dialog.Action("Baca", true, () =>
                    {
                        if (first.Value > last.Value) { Theme.Toast(dialog, "Halaman awal harus sebelum atau sama dengan halaman akhir."); return; }
                        range = ((int)first.Value - 1, (int)last.Value - 1);
                        dialog.DialogResult = DialogResult.OK;
                    });
                    dialog.Action("Batal", false, dialog.Close, Theme.TEXT);
                    if (dialog.ShowDialog(DlgOwner) != DialogResult.OK) return;
                }
                var (ok, result) = Ui.Busy(DlgOwner, "Membaca teks pada halaman terpilih…", progress =>
                {
                    PdfFile? file = null;
                    try { file = PdfToolsX.OpenPdf(_filePath); }
                    catch (PdfException) { }
                    if (file != null && !file.CanExtract) throw new InvalidOperationException("Pemilik PDF membatasi pengambilan teks.");
                    var originalPages = file?.Pages();
                    int count = range.Last - range.First + 1;
                    var pages = new List<string>();
                    string? ocrError = null;
                    for (int i = range.First; i <= range.Last; i++)
                    {
                        string pageText = file != null && originalPages != null && i < originalPages.Count
                            ? PdfLayout.ReadText(file, originalPages[i])
                            : "";
                        if (string.IsNullOrWhiteSpace(pageText) && ocrError == null)
                        {
                            using var bitmap = document.Render(i, 1700);
                            pageText = PdfEngine.RunOcr(bitmap).Text;
                            ocrError = PdfEngine.LastOcrError;
                        }
                        if (!string.IsNullOrWhiteSpace(pageText)) pages.Add($"Halaman {i + 1}:\n{pageText}");
                        progress(i - range.First + 1, count);
                    }
                    return (Text: string.Join("\n\n", pages), OcrError: ocrError);
                });
                if (!ok) return;
                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    Theme.Toast(DlgOwner, result.OcrError ?? "Tidak ada teks yang dapat dibacakan.");
                    return;
                }
                text = result.Text;
            }
            else if (WebStageActive)
            {
                var office = _office!;
                try { text = await office.AllTextAsync(); }
                catch (Exception ex) { if (!_closing) Ui.ShowError(DlgOwner, ex); return; }
                if (_closing || IsDisposed) return;
            }
            else if (_mime == AppStorage.MIME_DOCX)
            {
                text = OfficeReader.ReadDocxText(_filePath);
            }
            else if (IsSpreadsheet)
            {
                var sh = ReadSpreadsheet();
                if (sh != null)
                    text = string.Join("\n", sh.Rows.Select((r, i) => $"Baris {i + 1}: " + string.Join(", ", r)));
            }
            else if (_mime.StartsWith("image/"))
            {
                var (ok, result) = Ui.Busy(DlgOwner, "Mengenali teks pada gambar…", _ =>
                {
                    using var bitmap = PdfEngine.Decode(_filePath, 2200);
                    return PdfEngine.RunOcr(bitmap).Text;
                });
                if (!ok) return;
                if (string.IsNullOrWhiteSpace(result))
                {
                    Theme.Toast(DlgOwner, PdfEngine.LastOcrError ?? "Tidak ada teks yang dapat dibacakan.");
                    return;
                }
                text = result;
            }
            else
            {
                try { text = File.ReadAllText(_filePath); } catch { }
            }

            VoiceReaderManager.ShowVoiceReaderDialog(DlgOwner, _fileName, text);
        }

        private void PrintCurrentDocument()
        {
            if (_opt.Secure && !_opt.AllowPrint) { Theme.Toast(DlgOwner, "Pemilik dokumen menonaktifkan cetak."); return; }
            if (_pagedDocument is { } paged)
            {
                try { PrintHelper.PrintDocumentPages(DlgOwner, paged, _fileName); }
                catch (Exception ex) { Ui.ShowError(DlgOwner, ex); }
            }
            else if (WebStageActive)
                _office!.Print();   // cetak sesuai tata letak halaman asli (tabel, gambar, header/footer)
            else if (_mime == AppStorage.MIME_DOCX)
                PrintHelper.PrintText(DlgOwner, _fileName, OfficeReader.ReadDocxText(_filePath));
            else if (IsSpreadsheet && ReadSpreadsheet() is { } sheet)
                PrintHelper.PrintSpreadsheet(DlgOwner, _fileName, sheet.Rows);
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
            using var editor = new PdfEditorForm(_filePath, DlgOwner);
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
                BackColor = Theme.SURFACE
            };

            var lbl = new Label
            {
                Text = $"Gambar Tanda Tangan pada Halaman {_curPage + 1} dari {PageCount}:",
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
                int page = _curPage + 1, position = cmbPos.SelectedIndex;
                if (!Ui.Busy(dlg, "Menempelkan tanda tangan…", _ => PdfEngine.Sign(_filePath, sigBmp, page, position, outPdf))) return;
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Tanda Tangan PDF", outPdf);
                dlg.Close();
                MessageBox.Show(DlgOwner, $"Tanda tangan berhasil ditempel!\nDisimpan di:\n{outPdf}", "Tanda Tangan PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // Pustaka TTD & stempel tersimpan (atau buat & simpan yang baru)
            var btnLib = Theme.CreateButton("📚 Pustaka TTD…", false, Theme.ACCENT, 36, 140);
            btnLib.Location = new Point(300, 220);
            btnLib.Click += (s, e) =>
            {
                using var sig = Ui.PickStamp(dlg, "Pilih Tanda Tangan / Stempel");
                if (sig == null) return;
                string outPdf = AppStorage.NamedOut("TTD_" + AppStorage.BaseName(_fileName), "pdf");
                int page = _curPage + 1, position = cmbPos.SelectedIndex;
                if (!Ui.Busy(dlg, "Menempelkan tanda tangan…", _ => PdfEngine.Sign(_filePath, sig, page, position, outPdf))) return;
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Tanda Tangan PDF", outPdf);
                dlg.Close();
                MessageBox.Show(DlgOwner, $"Tanda tangan berhasil ditempel!\nDisimpan di:\n{outPdf}", "Tanda Tangan PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            dlg.Controls.AddRange(new Control[] { lbl, pad, lblPos, cmbPos, btnLib, btnClear, btnApply });
            Theme.ScaleRawDialog(dlg);
            dlg.ShowDialog(DlgOwner);
        }

        private void ShowMoreMenu(Control anchor)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Ubah nama berkas", null, async (s, e) =>
            {
                if (_opt.Rename != null)
                {
                    if (await _opt.Rename(_filePath) && !IsDisposed) Close();
                    return;
                }
                string? input = PromptInput("Ubah Nama Berkas", "Masukkan nama berkas baru:", Path.GetFileNameWithoutExtension(_fileName));
                if (!string.IsNullOrWhiteSpace(input))
                {
                    string? renamed = AppStorage.RenameFile(_filePath, input);
                    if (renamed != null) Close();
                    else Theme.Toast(this, "Nama tidak dapat disimpan. Periksa nama atau pilih nama lain.");
                }
            });
            if (_isPdf && PageCount > 0)
            {
                menu.Items.Add("Lompat ke Halaman...", null, (s, e) =>
                {
                    string? input = PromptInput("Lompat Halaman", $"Masukkan nomor halaman (1 - {PageCount}):", $"{_curPage + 1}");
                    if (int.TryParse(input, out int p) && p >= 1 && p <= PageCount)
                        GoToPage(p - 1);
                });
                menu.Items.Add("Ambil gambar tertanam…", null, (s, e) =>
                {
                    var (ok, n) = Ui.Busy(DlgOwner, "Mengambil gambar asli di PDF…",
                        progress => PdfToolsX.ExtractEmbeddedImages(_filePath, AppStorage.PicturesDir, AppStorage.BaseName(_fileName), progress));
                    if (!ok) return;
                    Theme.Toast(DlgOwner, n > 0 ? $"{n} gambar tersimpan di folder Gambar." : "Tidak ada gambar tertanam yang dapat disimpan.");
                });
            }
            menu.Items.Add("Buka Lokasi Folder di Windows Explorer", null, (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_filePath}\""); } catch { }
            });
            menu.Closed += (s, e) => menu.Dispose();
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
                BackColor = Theme.SURFACE
            };
            var lbl = new Label { Text = label, Location = new Point(18, 18), AutoSize = true, Font = Theme.FontLabel(9.5f) };
            var txt = new TextBox { Text = defaultVal, Location = new Point(18, 46), Width = 345, Font = Theme.FontBody(10f) };
            var btnOk = Theme.CreateButton("OK", true, Theme.ACCENT, 34, 100);
            btnOk.Location = new Point(263, 88);
            string? result = null;
            btnOk.Click += (s, e) => { result = txt.Text.Trim(); f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.AddRange(new Control[] { lbl, txt, btnOk });
            Theme.ScaleRawDialog(f);
            return f.ShowDialog(DlgOwner) == DialogResult.OK ? result : null;
        }
    }
}
