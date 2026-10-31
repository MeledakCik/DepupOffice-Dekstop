using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Porting menyeluruh dari MainActivity.kt (2.557 baris):
    /// Jendela Utama Desktop Depup Office v2.2.0 dengan 4 Tab Utama (Baca, Tools, Buat, Simpan),
    /// Pencarian Berkas, Drag & Drop PDF/Gambar, 12 Alat PDF & Office, Manajer Folder DepupOffice,
    /// Pembuat & Pemindai QR Code, Tanda Tangan Digital, Ekstrak TTD Transparan, dan Donasi QR.
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly string? _initialFilePath;
        private readonly IDocumentHistoryService _historyService;
        private CancellationTokenSource? _recentLoadCts;
        private int _currentTab = 0; // 0 = Baca, 1 = Tools, 2 = Buat, 3 = Simpan
        private int _filterMode = 0; // 0 = Semua Terkini, 1 = Ditandai
        private string _searchQuery = "";

        // Pengaman anti "tab/jendela ganda": klik ganda atau drop ganda tidak boleh membuka berkas dua kali.
        private bool _openingDocument;
        private Panel? _viewerHost;
        private ViewerForm? _activeViewer;
        private bool _actionRunning;
        private long _lastActionTick;
        private string _lastDropKey = "";
        private long _lastDropTick;

        private Label _barTitleLbl = null!;
        private Panel _contentHost = null!;
        private readonly Dictionary<int, NavItem> _navItems = new();
        private readonly string[] _tabNames = { "Baca", "Tools", "Buat", "Simpan" };

        public MainForm(string? initialFilePath = null)
        {
            _initialFilePath = initialFilePath;
            _historyService = DocumentHistoryService.CreateDefault();

            // Skala DPI harus ditetapkan sebelum satu pun kontrol dibuat.
            Theme.SetDpi(DeviceDpi);

            Text = "Depup Office Desktop v2.2.0 — 100% Offline Document, PDF Editor & Vault Suite";
            var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Size = new Size(Math.Min(Theme.S(1280), (int)(workArea.Width * 0.92)), Math.Min(Theme.S(840), (int)(workArea.Height * 0.92)));
            MinimumSize = new Size(Math.Min(Theme.S(940), workArea.Width), Math.Min(Theme.S(640), workArea.Height));
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; // ukuran diskalakan manual lewat Theme.S()
            KeyPreview = true;
            BackColor = Theme.BG;
            AllowDrop = true;

            _dragEnterHandler = (s, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && files.All(AppStorage.IsSupportedDocument))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            };
            DragEnter += _dragEnterHandler;
            _dragDropHandler = (s, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

                // Satu drop bisa terkirim ke lebih dari satu kontrol / terulang: abaikan drop identik dalam 1,5 detik.
                string key = string.Join("|", files);
                long now = Environment.TickCount64;
                if (key == _lastDropKey && now - _lastDropTick < 1500) return;
                _lastDropKey = key;
                _lastDropTick = now;

                // Jangan membuka dialog modal di dalam callback drag-drop OLE (Explorer ikut terkunci & event bisa ganda).
                BeginInvoke(new Action(() => HandleDroppedFiles(files)));
            };

            DragDrop += _dragDropHandler;

            BuildShell();
            SelectTab(0);

            // Susun ulang halaman (dengan jeda singkat) saat lebar area konten berubah: drag, maximize, restore.
            _reflowTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _reflowTimer.Tick += (s, e) =>
            {
                _reflowTimer.Stop();
                if (Math.Abs(AvailableWidth() - _builtAvailWidth) > 2)
                    RenderCurrentTab();
            };

            // Pindah ke monitor dengan DPI berbeda: hitung ulang seluruh ukuran.
            DpiChanged += (s, e) =>
            {
                Theme.SetDpi(e.DeviceDpiNew);
                BeginInvoke(new Action(RebuildShellForDpi));
            };

            // Ctrl+K memfokuskan kolom pencarian.
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && _activeViewer != null)
                {
                    e.SuppressKeyPress = true;
                    CloseEmbeddedViewer();
                    return;
                }
                if (e.Control && e.KeyCode == Keys.K)
                {
                    e.SuppressKeyPress = true;
                    _searchBox.Input.Focus();
                    _searchBox.Input.SelectAll();
                }
            };

            Shown += (s, e) =>
            {
                // The client area has its final DPI-scaled size only after the
                // form is shown, so perform one final responsive layout pass.
                RenderCurrentTab();
                if (!string.IsNullOrWhiteSpace(_initialFilePath) && File.Exists(_initialFilePath))
                    OpenDocument(_initialFilePath);
            };
        }

        // ================= ALUR FITUR & DIALOG LENGKAP =================

        private void HandleDroppedFiles(string[] files)
        {
            if (files.Any(file => !AppStorage.IsSupportedDocument(file)))
            {
                string unsupported = files.First(file => !AppStorage.IsSupportedDocument(file));
                Theme.Toast(this, AppStorage.UnsupportedDocumentMessage(unsupported));
            }
            else if (files.Length == 1)
            {
                OpenDocument(files[0]);
            }
            else if (files.All(file => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
            {
                RunGuarded(() => QuickProcessDroppedFiles(files));
            }
            else
            {
                Theme.Toast(this, "Seret satu berkas untuk dibuka, atau pilih beberapa gambar untuk dijadikan PDF.");
            }
        }

        /// <summary>
        /// Jalankan aksi kartu/tombol sekali saja: abaikan klik berikutnya selama aksi berjalan
        /// atau dalam 600 ms setelahnya (klik ganda).
        /// </summary>
        private void RunGuarded(Action action)
        {
            if (_actionRunning) return;
            if (Environment.TickCount64 - _lastActionTick < 600) return;
            _actionRunning = true;
            try { action(); }
            finally
            {
                _actionRunning = false;
                _lastActionTick = Environment.TickCount64;
            }
        }

        private void PickAndOpenFile(string filter)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Dokumen untuk Dibuka",
                Filter = filter
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                if (!AppStorage.IsSupportedDocument(ofd.FileName))
                    Theme.Toast(this, AppStorage.UnsupportedDocumentMessage(ofd.FileName));
                else
                    OpenDocument(ofd.FileName);
            }
        }

        private void PickAndOpenSupportedFile()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Dokumen untuk Dibuka",
                Filter = "Format yang didukung (*.pdf;*.docx;*.xlsx;*.csv;*.jpg;*.jpeg;*.png)|*.pdf;*.docx;*.xlsx;*.csv;*.jpg;*.jpeg;*.png|Semua berkas (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                OpenDocument(ofd.FileName);
        }

        private async void OpenDocument(string filePath)
        {
            // Sedang membuka satu berkas (termasuk saat menunggu riwayat tersimpan): tolak permintaan kedua.
            if (_openingDocument) return;

            if (!AppStorage.IsSupportedDocument(filePath))
            {
                Theme.Toast(this, AppStorage.UnsupportedDocumentMessage(filePath));
                return;
            }
            if (!File.Exists(filePath))
            {
                Theme.Toast(this, "Berkas tidak ditemukan");
                return;
            }

            // PDF ber-password: minta password dulu (dibatalkan = tidak jadi dibuka).
            if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !PdfEngine.EnsureUnlocked(this, filePath))
                return;

            _openingDocument = true;
            try
            {
                try
                {
                    await _historyService.RecordOpenedAsync(filePath);
                }
                catch (Exception ex)
                {
                    Theme.Toast(this, $"Riwayat lokal tidak dapat diperbarui: {ex.Message}");
                }

                AppStorage.AddRecent(filePath);
                await ShowViewerEmbeddedAsync(filePath);
            }
            finally
            {
                _openingDocument = false;
                _lastActionTick = Environment.TickCount64;
            }
            RenderCurrentTab();
        }

        /// <summary>
        /// Tampilkan Reader DI DALAM jendela utama (menutupi area konten), bukan sebagai jendela kedua.
        /// Task selesai saat Reader ditutup.
        /// </summary>
        private Task ShowViewerEmbeddedAsync(string filePath)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var viewer = new ViewerForm(filePath)
            {
                TopLevel = false,
                FormBorderStyle = FormBorderStyle.None,
                MinimumSize = Size.Empty,
                Dock = DockStyle.Fill
            };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BG };
            host.Controls.Add(viewer);
            _rightHost.Controls.Add(host);
            _rightHost.Controls.SetChildIndex(host, 0); // paling depan: menutupi halaman di belakangnya
            _viewerHost = host;
            _activeViewer = viewer;

            viewer.FormClosed += (s, e) =>
            {
                _activeViewer = null;
                _viewerHost = null;
                _rightHost.Controls.Remove(host);
                host.Dispose();
                tcs.TrySetResult();
            };
            viewer.Show();
            return tcs.Task;
        }

        private void CloseEmbeddedViewer() => _activeViewer?.Close();

        private void EditDocument(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is ".docx" or ".doc")
            {
                using var ed = new DocEditorForm(filePath);
                ed.ShowDialog(this);
            }
            else if (ext is ".xlsx" or ".xls" or ".csv")
            {
                using var ed = new SheetEditorForm(filePath);
                ed.ShowDialog(this);
            }
            else
            {
                OpenDocument(filePath);
            }
            RenderCurrentTab();
        }

        private void ShowRenameDialog(string filePath)
        {
            using var dlg = new Form
            {
                Text = "Ubah Nama Berkas",
                Size = new Size(420, 180),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };
            var lbl = new Label { Text = "Nama berkas baru:", Location = new Point(18, 18), AutoSize = true, Font = Theme.FontLabel(9.5f) };
            var txt = new TextBox { Text = AppStorage.BaseName(Path.GetFileName(filePath)), Location = new Point(18, 46), Width = 365, Font = Theme.FontBody(10f) };
            var btn = Theme.CreateButton("Simpan Nama", true, Theme.ACCENT, 36, 140);
            btn.Location = new Point(243, 86);
            btn.Click += async (s, e) =>
            {
                bool wasBookmarked = AppStorage.IsBookmarked(filePath);
                string? renamed = AppStorage.RenameFile(filePath, txt.Text);
                if (renamed != null)
                {
                    try
                    {
                        if (!await _historyService.UpdatePathAsync(filePath, renamed))
                            await _historyService.RecordOpenedAsync(renamed);
                        if (wasBookmarked)
                        {
                            AppStorage.ToggleBookmark(filePath);
                            AppStorage.ToggleBookmark(renamed);
                        }
                    }
                    catch (Exception ex)
                    {
                        Theme.Toast(this, $"Nama berubah, tetapi riwayat gagal diperbarui: {ex.Message}");
                    }
                    Theme.Toast(this, $"Nama berkas diubah ke {Path.GetFileName(renamed)}");
                    dlg.Close();
                    RenderCurrentTab();
                }
            };
            dlg.Controls.AddRange(new Control[] { lbl, txt, btn });
            dlg.ShowDialog(this);
        }

        private void StartPdfEditorFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih PDF atau Gambar untuk Diedit",
                Filter = "PDF / Gambar (*.pdf;*.jpg;*.jpeg;*.png;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                OpenPdfEditor(ofd.FileName);
        }

        internal void OpenPdfEditor(string path)
        {
            using var editor = new PdfEditorForm(path);
            if (!editor.Ready) return;
            editor.ShowDialog(this);
            if (!string.IsNullOrWhiteSpace(editor.SavedPath) && File.Exists(editor.SavedPath))
            {
                RenderCurrentTab();
                ShowOperationResultDialog("PDF Hasil Edit Tersimpan", editor.SavedPath);
            }
        }

        private void QuickProcessDroppedFiles(string[] files)
        {
            string first = files[0];
            string mime = AppStorage.MimeByName(first);
            if (mime.StartsWith("image/"))
            {
                string outPdf = AppStorage.NewOut("gambar", "pdf");
                PdfEngine.ImagesToPdf(files.Where(f => AppStorage.MimeByName(f).StartsWith("image/")).ToList(), outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Gambar ke PDF", outPdf);
                ShowOperationResultDialog("PDF Dibuat dari Gambar", outPdf);
                RenderCurrentTab();
                return;
            }
            if (mime != AppStorage.MIME_PDF)
            {
                OpenDocument(first);
                return;
            }

            using var dlg = new Form
            {
                Text = $"Olah Berkas PDF: {Path.GetFileName(first)}",
                Size = new Size(420, 310),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20) };
            flow.Controls.Add(new Label { Text = "Pilih tindakan untuk berkas PDF ini:", Font = Theme.FontLabel(10f), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });

            void AddAct(string label, Action act)
            {
                var b = Theme.CreateButton(label, false, Theme.ACCENT, 38, 360);
                b.Margin = new Padding(0, 0, 0, 8);
                b.Click += (s, e) => { dlg.Close(); act(); };
                flow.Controls.Add(b);
            }
            AddAct("📖 Buka di Pembaca PDF (Eye-Care Reader)", () => OpenDocument(first));
            AddAct("✂️ Pisah Halaman PDF", () => SplitPdfFlowForFile(first));
            AddAct("🗜️ Kompres Ukuran PDF (SSCASN / CPNS)", () => CompressPdfFlowForFile(first));
            AddAct("🖼️ Ekstrak Halaman PDF ke JPG", () =>
            {
                if (!PdfEngine.EnsureUnlocked(this, first)) return;
                int n = PdfEngine.ToJpg(first, AppStorage.BaseName(Path.GetFileName(first)));
                MessageBox.Show(this, $"{n} halaman disimpan sebagai JPG di:\n{AppStorage.PicturesDir}", "PDF ke JPG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            AddAct("✍️ Bubuhkan Tanda Tangan Digital", () => SignPdfForFile(first));

            dlg.Controls.Add(flow);
            dlg.ShowDialog(this);
        }

        // --- 1. Gabung PDF ---
        private void MergePdfFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Minimal 2 Berkas PDF untuk Digabungkan",
                Multiselect = true,
                Filter = "PDF Files (*.pdf)|*.pdf"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length < 2)
            {
                if (ofd.FileNames.Length == 1) Theme.Toast(this, "Pilih minimal 2 berkas PDF untuk digabungkan");
                return;
            }

            var list = ofd.FileNames.ToList();
            foreach (string pdf in list)
                if (!PdfEngine.EnsureUnlocked(this, pdf)) return;
            using var dlg = new Form
            {
                Text = "Urutan Penggabungan PDF",
                Size = new Size(520, 420),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var lb = new ListBox { Location = new Point(20, 44), Size = new Size(460, 240), Font = Theme.FontBody(10f) };
            void RebuildList()
            {
                lb.Items.Clear();
                for (int i = 0; i < list.Count; i++)
                    lb.Items.Add($"{i + 1}. {Path.GetFileName(list[i])}");
            }
            RebuildList();

            dlg.Controls.Add(new Label { Text = "Atur urutan berkas PDF dari atas ke bawah:", Font = Theme.FontLabel(9.5f), Location = new Point(20, 16), AutoSize = true });

            var btnUp = Theme.CreateButton("↑ Naik", false, Theme.TEXT, 36, 90);
            btnUp.Location = new Point(20, 296);
            btnUp.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i > 0) { (list[i - 1], list[i]) = (list[i], list[i - 1]); RebuildList(); lb.SelectedIndex = i - 1; }
            };

            var btnDown = Theme.CreateButton("↓ Turun", false, Theme.TEXT, 36, 90);
            btnDown.Location = new Point(118, 296);
            btnDown.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i >= 0 && i < list.Count - 1) { (list[i + 1], list[i]) = (list[i], list[i + 1]); RebuildList(); lb.SelectedIndex = i + 1; }
            };

            var btnMerge = Theme.CreateButton("🔗 Gabungkan PDF Sekarang", true, Theme.ACCENT, 38, 250);
            btnMerge.Location = new Point(230, 295);
            btnMerge.Click += (s, e) =>
            {
                string outPdf = AppStorage.NewOut("gabung", "pdf");
                PdfEngine.Merge(list, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Gabung PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("PDF Berhasil Digabungkan", outPdf);
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { lb, btnUp, btnDown, btnMerge });
            dlg.ShowDialog(this);
        }

        // --- 2. Pisah PDF ---
        private void SplitPdfFlow()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih Berkas PDF untuk Dipisah", Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                SplitPdfFlowForFile(ofd.FileName);
        }

        private void SplitPdfFlowForFile(string pdfPath)
        {
            if (!PdfEngine.EnsureUnlocked(this, pdfPath)) return;
            int pages = PdfEngine.PageCount(pdfPath);
            using var dlg = new Form
            {
                Text = "Pratinjau & Pisah PDF",
                Size = new Size(440, 270),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            dlg.Controls.Add(new Label { Text = $"{Path.GetFileName(pdfPath)} (Total: {pages} halaman)", Font = Theme.FontLabel(10f), Location = new Point(20, 18), AutoSize = true });
            dlg.Controls.Add(new Label { Text = "Dari Halaman:", Font = Theme.FontBody(9.5f), Location = new Point(20, 58), AutoSize = true });
            var numFrom = new NumericUpDown { Minimum = 1, Maximum = pages, Value = 1, Location = new Point(140, 56), Width = 100 };

            dlg.Controls.Add(new Label { Text = "Sampai Halaman:", Font = Theme.FontBody(9.5f), Location = new Point(20, 98), AutoSize = true });
            var numTo = new NumericUpDown { Minimum = 1, Maximum = pages, Value = pages, Location = new Point(140, 96), Width = 100 };

            var btnSplit = Theme.CreateButton("✂️ Pisahkan Halaman PDF", true, Theme.ACCENT, 40, 380);
            btnSplit.Location = new Point(20, 152);
            btnSplit.Click += (s, e) =>
            {
                int f = (int)numFrom.Value;
                int t = (int)numTo.Value;
                if (f > t) { Theme.Toast(dlg, "Rentang halaman tidak valid"); return; }
                string outPdf = AppStorage.NewOut($"pisah_{f}-{t}", "pdf");
                PdfEngine.Split(pdfPath, f, t, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Pisah PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("PDF Berhasil Dipisah", outPdf);
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { numFrom, numTo, btnSplit });
            dlg.ShowDialog(this);
        }

        // --- 3. Susun & Hapus Halaman PDF ---
        private void OrganizePdfFlow()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih Berkas PDF untuk Disusun", Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string pdfPath = ofd.FileName;
            if (!PdfEngine.EnsureUnlocked(this, pdfPath)) return;
            int total = PdfEngine.PageCount(pdfPath);
            var pageOrder = Enumerable.Range(0, total).ToList();
            var rotations = new Dictionary<int, int>();

            using var dlg = new Form
            {
                Text = "Kelola & Susun Ulang Halaman PDF",
                Size = new Size(540, 440),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            dlg.Controls.Add(new Label { Text = $"Susun urutan, putar 90°, atau hapus halaman ({Path.GetFileName(pdfPath)}):", Font = Theme.FontLabel(9.5f), Location = new Point(20, 16), AutoSize = true });
            var lb = new ListBox { Location = new Point(20, 44), Size = new Size(480, 250), Font = Theme.FontBody(10f) };

            void RefreshItems()
            {
                lb.Items.Clear();
                for (int i = 0; i < pageOrder.Count; i++)
                {
                    int orig = pageOrder[i];
                    int rot = rotations.TryGetValue(orig, out int r) ? r : 0;
                    lb.Items.Add($"Urutan {i + 1}  ←  Halaman Asli #{orig + 1}" + (rot > 0 ? $" (Putar {rot}°)" : ""));
                }
            }
            RefreshItems();

            var bUp = Theme.CreateButton("↑ Naik", false, Theme.TEXT, 34, 85);
            bUp.Location = new Point(20, 306);
            bUp.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i > 0) { (pageOrder[i - 1], pageOrder[i]) = (pageOrder[i], pageOrder[i - 1]); RefreshItems(); lb.SelectedIndex = i - 1; }
            };

            var bDown = Theme.CreateButton("↓ Turun", false, Theme.TEXT, 34, 85);
            bDown.Location = new Point(112, 306);
            bDown.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i >= 0 && i < pageOrder.Count - 1) { (pageOrder[i + 1], pageOrder[i]) = (pageOrder[i], pageOrder[i + 1]); RefreshItems(); lb.SelectedIndex = i + 1; }
            };

            var bRot = Theme.CreateButton("↻ Putar 90°", false, Theme.ACCENT, 34, 105);
            bRot.Location = new Point(204, 306);
            bRot.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i >= 0)
                {
                    int orig = pageOrder[i];
                    rotations[orig] = ((rotations.TryGetValue(orig, out int r) ? r : 0) + 90) % 360;
                    RefreshItems();
                    lb.SelectedIndex = i;
                }
            };

            var bDel = Theme.CreateButton("✕ Hapus Hal", false, Theme.RED, 34, 105);
            bDel.Location = new Point(316, 306);
            bDel.Click += (s, e) =>
            {
                int i = lb.SelectedIndex;
                if (i >= 0 && pageOrder.Count > 1) { pageOrder.RemoveAt(i); RefreshItems(); }
            };

            var bSave = Theme.CreateButton("💾 Simpan PDF Baru", true, Theme.ACCENT, 40, 480);
            bSave.Location = new Point(20, 350);
            bSave.Click += (s, e) =>
            {
                string outPdf = AppStorage.NewOut("susun_" + AppStorage.BaseName(Path.GetFileName(pdfPath)), "pdf");
                PdfEngine.OrganizePdf(pdfPath, pageOrder, rotations, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Susun PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("PDF Berhasil Disusun Ulang", outPdf);
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { lb, bUp, bDown, bRot, bDel, bSave });
            dlg.ShowDialog(this);
        }

        // --- 4. Sedot Foto PDF ---
        private void ExtractPdfImagesFlow()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih PDF untuk Diekstrak Gambarnya", Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            if (!PdfEngine.EnsureUnlocked(this, ofd.FileName)) return;
            int count = PdfEngine.ExtractImagesFromPdf(ofd.FileName, AppStorage.BaseName(Path.GetFileName(ofd.FileName)));
            MessageBox.Show(this, $"✓ Berhasil menyedot & mengekstrak {count} gambar halaman dari PDF ke:\n{AppStorage.PicturesDir}", "Ekstrak Foto Selesai", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // --- 5. Kompres PDF Target SSCASN / CPNS ---
        private void CompressPdfFlow()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih Berkas PDF untuk Dikompres", Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                CompressPdfFlowForFile(ofd.FileName);
        }

        private void CompressPdfFlowForFile(string pdfPath)
        {
            if (!PdfEngine.EnsureUnlocked(this, pdfPath)) return;
            long before = new FileInfo(pdfPath).Length;
            using var dlg = new Form
            {
                Text = "Pratinjau & Kompres PDF Target (SSCASN / CPNS / BUMN)",
                Size = new Size(480, 320),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            dlg.Controls.Add(new Label
            {
                Text = $"Berkas: {Path.GetFileName(pdfPath)}\nUkuran Saat Ini: {AppStorage.FmtSize(before)}",
                Font = Theme.FontLabel(10f),
                ForeColor = Theme.TEXT,
                Location = new Point(20, 16),
                Size = new Size(420, 44)
            });

            var rbTarget = new RadioButton { Text = "Target Ukuran Presisi (SSCASN / CPNS):", Checked = true, Font = Theme.FontLabel(9.5f), Location = new Point(20, 68), AutoSize = true };
            var cmbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(40, 94), Width = 390 };
            int[] targetVals = { 200, 500, 1000, 2000 };
            cmbTarget.Items.AddRange(new object[] { "< 200 KB", "< 500 KB (Rekomendasi SSCASN)", "< 1 MB", "< 2 MB" });
            cmbTarget.SelectedIndex = 1;

            var rbLevel = new RadioButton { Text = "Atau Pilih Tingkat Kompresi Persentase:", Font = Theme.FontLabel(9.5f), Location = new Point(20, 134), AutoSize = true };
            var cmbLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(40, 160), Width = 390 };
            cmbLevel.Items.AddRange(new object[] { "Ringan (~15%)", "Sedang (~35%)", "Tinggi (~60%)" });
            cmbLevel.SelectedIndex = 1;

            var btnCompress = Theme.CreateButton("🗜️ Kompres Sekarang", true, Theme.TEAL, 40, 410);
            btnCompress.Location = new Point(20, 210);
            btnCompress.Click += (s, e) =>
            {
                string outPdf = AppStorage.NewOut("kompres", "pdf");
                if (rbTarget.Checked)
                    PdfEngine.CompressToTarget(pdfPath, targetVals[Math.Clamp(cmbTarget.SelectedIndex, 0, 3)], outPdf);
                else
                    PdfEngine.Compress(pdfPath, Math.Clamp(cmbLevel.SelectedIndex, 0, 2), outPdf);

                long after = new FileInfo(outPdf).Length;
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Kompres PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("PDF Berhasil Dikompres", outPdf, $"Sebelum: {AppStorage.FmtSize(before)}  →  Sesudah: {AppStorage.FmtSize(after)}");
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { rbTarget, cmbTarget, rbLevel, cmbLevel, btnCompress });
            dlg.ShowDialog(this);
        }

        // --- 7. Konversi Massal (Batch) ---
        private void BatchImagesToPdfFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Foto-Foto untuk Dikonversi Menjadi PDF Terpisah",
                Multiselect = true,
                Filter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;
            int n = PdfEngine.BatchImagesToIndividualPdf(ofd.FileNames);
            MessageBox.Show(this, $"✓ Berhasil mengonversi {n} foto menjadi {n} berkas PDF terpisah di:\n{AppStorage.OutDir}", "Konversi Massal Selesai", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RenderCurrentTab();
        }

        // --- 8. Kompres Foto Galeri ---
        private void CompressGalleryImagesFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Foto untuk Dikompres",
                Multiselect = true,
                Filter = "Gambar (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;

            var (count, saved) = PdfEngine.CompressGalleryImages(ofd.FileNames, 1600, 75);
            MessageBox.Show(this, $"✓ Berhasil mengompres {count} foto ke:\n{AppStorage.PicturesDir}\n\nHemat ruang penyimpanan: {AppStorage.FmtSize(saved)}", "Kompres Foto Selesai", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // --- 9. Pindai & Buat QR Code ---
        private void ShowQrManagerDialog()
        {
            using var dlg = new Form
            {
                Text = "Manajer Kode QR & Barcode (Standar ISO/IEC 18004)",
                Size = new Size(560, 560),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22) };

            var btnScanImg = Theme.CreateButton("🔍 Pindai Kode QR dari File Gambar / Foto...", false, Theme.TEAL, 40, 500);
            btnScanImg.Location = new Point(20, 16);
            btnScanImg.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog { Title = "Pilih Gambar Kode QR", Filter = "Gambar (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
                if (ofd.ShowDialog(dlg) == DialogResult.OK)
                {
                    using var bmp = PdfEngine.Decode(ofd.FileName, 1600);
                    var res = QrManager.ScanAll(bmp);
                    if (res.Count == 0)
                        MessageBox.Show(dlg, "Kode QR tidak terdeteksi pada gambar tersebut.", "Hasil Pindai QR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else
                    {
                        string all = string.Join("\n\n", res.Select(r => $"{r.Format}:\n{r.Text}"));
                        Clipboard.SetText(res[0].Text);
                        MessageBox.Show(dlg, $"Hasil Pindai ({res.Count} kode) — telah disalin ke Clipboard:\n\n{all}", "Hasil Pindai QR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            };
            pad.Controls.Add(btnScanImg);

            pad.Controls.Add(new Label { Text = "Pembuat Kode QR Baru (Pratinjau Langsung):", Font = Theme.FontLabel(10f), Location = new Point(20, 70), AutoSize = true });

            var pbQr = new PictureBox
            {
                Location = new Point(140, 96),
                Size = new Size(260, 260),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            pad.Controls.Add(pbQr);

            var lblStatus = new Label
            {
                Text = "Ketik teks atau link di bawah untuk melihat pratinjau QR.",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.MUTED,
                Location = new Point(20, 364),
                AutoSize = true
            };
            pad.Controls.Add(lblStatus);

            var txtQr = new TextBox
            {
                Text = "https://github.com/MeledakCik/DepupOffice",
                Font = Theme.FontBody(10.5f),
                Location = new Point(20, 390),
                Width = 500
            };
            pad.Controls.Add(txtQr);

            Bitmap? currentQrBmp = null;
            void UpdateQrPreview()
            {
                string t = txtQr.Text.Trim();
                if (string.IsNullOrEmpty(t))
                {
                    pbQr.Image = null;
                    lblStatus.Text = "Ketik sesuatu untuk membuat kode QR.";
                    return;
                }
                if (QrManager.IsTextTooLong(t))
                {
                    lblStatus.Text = "Teks terlalu panjang (maksimal 1.200 byte).";
                    return;
                }
                currentQrBmp?.Dispose();
                currentQrBmp = QrManager.GenerateQr(t, 520, 520);
                pbQr.Image = currentQrBmp;
                lblStatus.Text = $"✓ Kode QR siap dipindai • {System.Text.Encoding.UTF8.GetByteCount(t)} byte";
            }
            txtQr.TextChanged += (s, e) => UpdateQrPreview();
            UpdateQrPreview();

            var btnSavePng = Theme.CreateButton("💾 Simpan PNG ke Documents", true, Theme.ACCENT, 40, 500);
            btnSavePng.Location = new Point(20, 434);
            btnSavePng.Click += (s, e) =>
            {
                if (currentQrBmp == null) return;
                string dst = Path.Combine(AppStorage.PicturesDir, $"QR_Code_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");
                currentQrBmp.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                MessageBox.Show(dlg, $"Kode QR berhasil disimpan di:\n{dst}", "Simpan QR PNG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            pad.Controls.Add(btnSavePng);

            dlg.FormClosed += (s, e) => currentQrBmp?.Dispose();
            dlg.Controls.Add(pad);
            dlg.ShowDialog(this);
        }

        // --- 10. Kunci & Enkripsi ke Brankas ---
        private void LockFileIntoVaultFlow(string filePath)
        {
            if (!VaultManager.HasPin())
            {
                MessageBox.Show(this, "Brankas belum memiliki PIN. Silakan buat PIN 4–6 digit terlebih dahulu.", "Brankas AES-256", MessageBoxButtons.OK, MessageBoxIcon.Information);
                using var vf = new VaultForm();
                vf.ShowDialog(this);
                if (!VaultManager.HasPin()) return;
            }
            VaultManager.EncryptFile(filePath);
            Theme.Toast(this, $"✓ '{Path.GetFileName(filePath)}' berhasil dienkripsi AES-256 ke Brankas!");
            RenderCurrentTab();
        }

        // --- 11. Gambar ke PDF ---
        private void ImagesToSinglePdfFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih 1 atau Lebih Gambar untuk Dijadikan 1 PDF",
                Multiselect = true,
                Filter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK || ofd.FileNames.Length == 0) return;
            string outPdf = AppStorage.NewOut("gambar_gabung", "pdf");
            PdfEngine.ImagesToPdf(ofd.FileNames, outPdf);
            AppStorage.AddRecent(outPdf);
            AppStorage.SetLastJob("Gambar ke PDF", outPdf);
            ShowOperationResultDialog($"PDF Dibuat dari {ofd.FileNames.Length} Gambar", outPdf);
            RenderCurrentTab();
        }

        // --- 12. Ekstrak TTD Transparan ---
        private void ExtractTransparentSignatureFlow()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Foto Tanda Tangan / Stempel di Atas Kertas Putih",
                Filter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            using var raw = PdfEngine.Decode(ofd.FileName, 1200);
            using var trans = PdfEngine.ExtractTransparentSignature(raw);
            string dst = Path.Combine(AppStorage.PicturesDir, $"TTD_Transparan_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");
            trans.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
            MessageBox.Show(this, $"✓ Latar kertas putih berhasil dihapus!\nPNG Tanda Tangan Transparan disimpan di:\n{dst}", "Ekstrak TTD Transparan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // --- Tulis Teks Cepat jadi PDF ---
        private void QuickTextToPdfDialog()
        {
            using var dlg = new Form
            {
                Text = "Tulis Teks Jadi PDF (Ukuran A4 Rapi)",
                Size = new Size(560, 460),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var txtTitle = new TextBox { PlaceholderText = "Judul Dokumen (opsional)", Location = new Point(20, 20), Width = 500, Font = Theme.FontHead(12f) };
            var txtBody = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Ketik isi dokumen atau catatan di sini...", Location = new Point(20, 62), Size = new Size(500, 280), Font = Theme.FontBody(10.5f) };
            var btnCreate = Theme.CreateButton("📄 Buat PDF Sekarang", true, Theme.INDIGO, 42, 500);
            btnCreate.Location = new Point(20, 356);
            btnCreate.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtBody.Text)) { Theme.Toast(dlg, "Teks masih kosong"); return; }
                string ttl = txtTitle.Text.Trim();
                string content = string.IsNullOrEmpty(ttl) ? txtBody.Text : $"{ttl}\n\n{txtBody.Text}";
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(string.IsNullOrEmpty(ttl) ? "catatan" : ttl), "pdf");
                PdfEngine.TextToPdf(content, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Teks ke PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("PDF Berhasil Dibuat dari Teks", outPdf);
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { txtTitle, txtBody, btnCreate });
            dlg.ShowDialog(this);
        }

        // --- Bubuhkan Tanda Tangan ke PDF ---
        private void SignExistingPdfFlow()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih PDF untuk Ditandatangani", Filter = "PDF Files (*.pdf)|*.pdf" };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                SignPdfForFile(ofd.FileName);
        }

        private void SignPdfForFile(string pdfPath)
        {
            if (!PdfEngine.EnsureUnlocked(this, pdfPath)) return;
            int pages = PdfEngine.PageCount(pdfPath);
            using var dlg = new Form
            {
                Text = "Bubuhkan Tanda Tangan Digital ke PDF",
                Size = new Size(480, 410),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            dlg.Controls.Add(new Label { Text = "Gambar tanda tangan di kotak putih:", Location = new Point(20, 16), AutoSize = true, Font = Theme.FontLabel(9.5f) });
            var pad = new SignaturePadControl { Location = new Point(20, 42), Size = new Size(420, 170) };

            var btnClear = Theme.CreateButton("Bersihkan", false, Theme.TEXT, 30, 100);
            btnClear.Location = new Point(20, 220);
            btnClear.Click += (s, e) => pad.Clear();

            dlg.Controls.Add(new Label { Text = $"Halaman (1-{pages}):", Location = new Point(20, 264), AutoSize = true, Font = Theme.FontLabel(9.5f) });
            var numPage = new NumericUpDown { Minimum = 1, Maximum = pages, Value = pages, Location = new Point(145, 262), Width = 80 };

            dlg.Controls.Add(new Label { Text = "Posisi:", Location = new Point(240, 264), AutoSize = true, Font = Theme.FontLabel(9.5f) });
            var cmbPos = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(295, 260), Width = 145 };
            cmbPos.Items.AddRange(new object[] { "Kiri", "Tengah", "Kanan" });
            cmbPos.SelectedIndex = 2;

            var btnApply = Theme.CreateButton("✍️ Tempel Tanda Tangan", true, Theme.TEAL, 40, 420);
            btnApply.Location = new Point(20, 310);
            btnApply.Click += (s, e) =>
            {
                using var sig = pad.ToBitmap();
                if (sig == null) { Theme.Toast(dlg, "Tanda tangan masih kosong"); return; }
                string outPdf = AppStorage.NewOut("ttd_" + AppStorage.BaseName(Path.GetFileName(pdfPath)), "pdf");
                PdfEngine.Sign(pdfPath, sig, (int)numPage.Value, cmbPos.SelectedIndex, outPdf);
                AppStorage.AddRecent(outPdf);
                AppStorage.SetLastJob("Tanda Tangan PDF", outPdf);
                dlg.Close();
                ShowOperationResultDialog("Tanda Tangan Berhasil Ditempel", outPdf);
                RenderCurrentTab();
            };

            dlg.Controls.AddRange(new Control[] { pad, btnClear, numPage, cmbPos, btnApply });
            dlg.ShowDialog(this);
        }

        // --- Modal Hasil Olahan (ResultSheet di Ui.kt) ---
        private void ShowOperationResultDialog(string title, string filePath, string extraInfo = "")
        {
            var fi = new FileInfo(filePath);
            using var dlg = new Form
            {
                Text = title,
                Size = new Size(480, 340),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(22) };
            flow.Controls.Add(new Label { Text = $"✓ {title}", Font = Theme.FontHead(14f), ForeColor = Theme.TEAL, AutoSize = true });
            flow.Controls.Add(new Label { Text = $"{fi.Name} • {AppStorage.FmtSize(fi.Length)}", Font = Theme.FontBody(9.5f), ForeColor = Theme.MUTED, AutoSize = true, Margin = new Padding(0, 4, 0, 8) });

            if (!string.IsNullOrWhiteSpace(extraInfo))
            {
                flow.Controls.Add(new Label { Text = extraInfo, Font = Theme.FontLabel(9.5f), ForeColor = Theme.ACCENT, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            }

            var btnOpen = Theme.CreateButton("📖 Buka Dokumen Sekarang", true, Theme.ACCENT, 40, 420);
            btnOpen.Margin = new Padding(0, 4, 0, 8);
            btnOpen.Click += (s, e) => { dlg.Close(); OpenDocument(filePath); };

            var btnSaveDl = Theme.CreateButton("⬇️ Simpan Salinan ke Documents\\DepupOffice\\Salinan", false, Theme.TEAL, 36, 420);
            btnSaveDl.Margin = new Padding(0, 0, 0, 8);
            btnSaveDl.Click += (s, e) =>
            {
                string dst = AppStorage.SaveToDownloads(filePath);
                Theme.Toast(dlg, $"Disimpan di: {dst}");
            };

            var btnVault = Theme.CreateButton("🔒 Masukkan ke Brankas AES-256", false, Theme.AMBER, 36, 420);
            btnVault.Margin = new Padding(0, 0, 0, 8);
            btnVault.Click += (s, e) => { dlg.Close(); LockFileIntoVaultFlow(filePath); };

            var btnExplorer = Theme.CreateButton("📂 Tampilkan di Windows Explorer", false, Theme.TEXT, 36, 420);
            btnExplorer.Click += (s, e) =>
            {
                try { Process.Start("explorer.exe", $"/select,\"{filePath}\""); } catch { }
            };

            flow.Controls.AddRange(new Control[] { btnOpen, btnSaveDl, btnVault, btnExplorer });
            dlg.Controls.Add(flow);
            dlg.ShowDialog(this);
        }

        // --- Manajer Folder DepupOffice (showFolderExplorerSheet di MainActivity.kt) ---
        private void ShowFolderExplorerDialog(string initialCategory = "SEMUA")
        {
            string activeFilter = initialCategory;

            using var dlg = new Form
            {
                Text = $"Folder DepupOffice — {AppStorage.OutDir}",
                Size = new Size(760, 580),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Theme.BG
            };

            var topPanel = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.SURFACE, Padding = new Padding(16, 10, 16, 10) };
            var chipsFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            topPanel.Controls.Add(chipsFlow);

            var listFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(16)
            };

            void RenderFolderList()
            {
                var allFiles = AppStorage.GetOutFiles();
                chipsFlow.Controls.Clear();
                string[] cats = { "SEMUA", "PDF", "Word", "Excel", "Gambar" };
                foreach (string cat in cats)
                {
                    string cName = cat;
                    int count = cName switch
                    {
                        "PDF" => allFiles.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == "PDF"),
                        "Word" => allFiles.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == "DOCX"),
                        "Excel" => allFiles.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == "XLSX"),
                        "Gambar" => allFiles.Count(f => AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label == "IMG"),
                        _ => allFiles.Count
                    };
                    bool sel = string.Equals(cName, activeFilter, StringComparison.OrdinalIgnoreCase);
                    var b = Theme.CreateButton($"{cName} ({count})", sel, Theme.ACCENT, 34);
                    b.Click += (s, e) => { activeFilter = cName; RenderFolderList(); };
                    chipsFlow.Controls.Add(b);
                }

                var filtered = allFiles.Where(f =>
                {
                    string lbl = AppStorage.TypeBadgeOf(AppStorage.MimeByName(f.Name), f.Name).Label;
                    return activeFilter switch
                    {
                        "PDF" => lbl == "PDF",
                        "Word" => lbl == "DOCX",
                        "Excel" => lbl == "XLSX",
                        "Gambar" => lbl == "IMG",
                        _ => true
                    };
                }).ToList();

                listFlow.SuspendLayout();
                listFlow.Controls.Clear();

                if (filtered.Count == 0)
                {
                    var empty = new CardPanel { Width = 690, Height = 70 };
                    empty.Controls.Add(new Label
                    {
                        Text = $"Belum ada berkas kategori {activeFilter} di folder Documents\\DepupOffice.",
                        Font = Theme.FontBody(10f),
                        ForeColor = Theme.MUTED,
                        Location = new Point(18, 24),
                        AutoSize = true
                    });
                    listFlow.Controls.Add(empty);
                }
                else
                {
                    foreach (var f in filtered)
                    {
                        FileInfo fi = f;
                        string mime = AppStorage.MimeByName(fi.Name);
                        var (lbl, fg, bg) = AppStorage.TypeBadgeOf(mime, fi.Name);

                        var row = new CardPanel { Width = 690, Height = 64, Margin = new Padding(0, 0, 0, 8) };
                        row.Controls.Add(new Label
                        {
                            Text = lbl,
                            Font = Theme.FontLabel(9f),
                            ForeColor = fg,
                            BackColor = bg,
                            TextAlign = ContentAlignment.MiddleCenter,
                            Size = new Size(46, 38),
                            Location = new Point(12, 13)
                        });
                        row.Controls.Add(new Label
                        {
                            Text = fi.Name,
                            Font = Theme.FontLabel(10f),
                            ForeColor = Theme.TEXT,
                            Location = new Point(70, 12),
                            AutoSize = true
                        });
                        row.Controls.Add(new Label
                        {
                            Text = $"{AppStorage.FmtSize(fi.Length)} • {fi.LastWriteTime:dd MMM yyyy HH:mm}",
                            Font = Theme.FontBody(8.5f),
                            ForeColor = Theme.MUTED,
                            Location = new Point(70, 34),
                            AutoSize = true
                        });

                        var bOpen = Theme.CreateButton("Buka", true, Theme.ACCENT, 30, 68);
                        bOpen.Location = new Point(445, 17);
                        bOpen.Click += (s, e) => { dlg.Close(); OpenDocument(fi.FullName); };

                        var bEdit = Theme.CreateButton("Edit", false, Theme.TEAL, 30, 68);
                        bEdit.Location = new Point(518, 17);
                        bEdit.Click += (s, e) => { dlg.Close(); EditDocument(fi.FullName); };

                        var bDel = Theme.CreateButton("Hapus", false, Theme.RED, 30, 76);
                        bDel.Location = new Point(592, 17);
                        bDel.Click += (s, e) =>
                        {
                            if (MessageBox.Show(dlg, $"Hapus '{fi.Name}' dari perangkat?", "Hapus Berkas", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            {
                                fi.Delete();
                                RenderFolderList();
                                RenderCurrentTab();
                            }
                        };

                        row.Controls.AddRange(new Control[] { bOpen, bEdit, bDel });
                        listFlow.Controls.Add(row);
                    }
                }
                listFlow.ResumeLayout();
            }

            RenderFolderList();
            dlg.Controls.Add(listFlow);
            dlg.Controls.Add(topPanel);
            dlg.ShowDialog(this);
        }

        private void ShowSearchDialog()
        {
            SelectTab(0);
            _searchBox.Input.Focus();
        }

        // --- Privacy Policy, Saweria External Link & About Dialog (Compliant v2.2.0) ---
        private void OpenPrivacyPolicyForm()
        {
            using var form = new PrivacyPolicyForm();
            form.ShowDialog(this);
        }

        private void ShowDonationDialog()
        {
            PrivacyManager.OpenSaweriaDonation();
        }

        private void ShowAboutDialog()
        {
            using var dlg = new Form
            {
                Text = "Tentang Depup Office Desktop v2.2.0",
                Size = new Size(560, 360),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };

            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };

            var lblTitle = new Label
            {
                Text = "🚀 Depup Office Desktop v2.2.0",
                Font = Theme.FontHead(15f),
                ForeColor = Theme.ACCENT,
                Location = new Point(24, 18),
                AutoSize = true
            };

            var lblCompliance = new Label
            {
                Text = PrivacyManager.ComplianceSummaryText,
                Font = Theme.FontLabel(10f),
                ForeColor = Theme.TEXT,
                Location = new Point(24, 56),
                Size = new Size(495, 60)
            };

            var lblDetails = new Label
            {
                Text = $"• Email Developer: {PrivacyManager.DeveloperEmail}\n" +
                       $"• Link Donasi Resmi: {PrivacyManager.OfficialSaweriaUrl}\n" +
                       $"• Link Privacy Policy Resmi: {PrivacyManager.OfficialPrivacyUrl}\n" +
                       $"• File Privacy Policy Offline: {PrivacyManager.GetPrivacyHtmlPath()}",
                Font = Theme.FontBody(9.5f),
                ForeColor = Theme.MUTED,
                Location = new Point(24, 124),
                Size = new Size(495, 90)
            };

            var btnPrivacy = Theme.CreateButton("🛡️ Buka Privacy Policy", false, Theme.ACCENT, 38, 160);
            btnPrivacy.Location = new Point(24, 232);
            btnPrivacy.Click += (s, e) => { dlg.Close(); OpenPrivacyPolicyForm(); };

            var btnSaweria = Theme.CreateButton("Dukung Developer ❤️", true, Theme.RED, 38, 175);
            btnSaweria.Location = new Point(194, 232);
            btnSaweria.Click += (s, e) => PrivacyManager.OpenSaweriaDonation();

            var btnEmail = Theme.CreateButton("✉️ Kontak Developer", false, Theme.TEAL, 38, 145);
            btnEmail.Location = new Point(379, 232);
            btnEmail.Click += (s, e) => PrivacyManager.OpenDeveloperEmail();

            pad.Controls.AddRange(new Control[] { lblTitle, lblCompliance, lblDetails, btnPrivacy, btnSaweria, btnEmail });
            dlg.Controls.Add(pad);
            dlg.ShowDialog(this);
        }
    }
}
