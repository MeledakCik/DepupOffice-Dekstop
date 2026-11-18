using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Porting dari VaultActivity.kt:
    /// Brankas Pribadi Terenkripsi AES-256-GCM yang dikunci dengan PIN 4-6 digit.
    /// </summary>
    public class VaultForm : Form
    {
        private Panel _bodyHost = null!;
        private Label _subLabel = null!;
        private Pill _statePill = null!;
        private readonly string _tmpDir;
        private Action? _relayout;

        private static int S(int px) => Theme.S(px);

        public VaultForm()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "DepupOffice_VaultTmp");
            CleanupTmp();

            Text = "Brankas AES-256 — " + Theme.AppName;
            AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(760), wa.Width - S(40)), Math.Min(S(640), wa.Height - S(40)));
            MinimumSize = new Size(Math.Min(S(520), wa.Width), Math.Min(S(460), wa.Height));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;
            ForeColor = Theme.TEXT;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            BuildFrame();
            if (VaultManager.HasPin()) ShowLoginView();
            else ShowCreatePinView();
        }

        private void CleanupTmp()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true); } catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CleanupTmp();
            base.OnFormClosed(e);
        }

        private void BuildFrame()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = S(76), BackColor = Theme.SURFACE };
            header.Paint += (s, e) => { using var pen = new Pen(Theme.LINE); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };

            var icon = new IconChip { Size = new Size(S(42), S(42)), Radius = S(12), Bg = Theme.AMBER_BG, Fg = Theme.AMBER, Icon = CardIcon.Vault, Location = new Point(S(20), S(17)) };
            header.Controls.Add(icon);
            header.Controls.Add(new Label
            {
                Text = "Brankas Pribadi AES-256",
                Font = Theme.FontHead(14f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                UseMnemonic = false,
                Location = new Point(S(74), S(14))
            });
            _subLabel = new Label
            {
                Text = "Terenkripsi AES-256-GCM • tersimpan lokal di perangkat ini",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                UseMnemonic = false,
                Location = new Point(S(75), S(44))
            };
            header.Controls.Add(_subLabel);

            _statePill = new Pill { Text = "Terkunci", Dot = Theme.AMBER, Fg = Theme.AMBER, Bg = Theme.AMBER_BG, Outline = Theme.Mix(Theme.AMBER_BG, Theme.AMBER, 0.35), Font = Theme.FontMono(8f) };
            header.Controls.Add(_statePill);
            var btnClose = Theme.CreateButton("Tutup", false, Theme.TEXT, S(34), S(84));
            btnClose.Click += (s, e) => Close();
            header.Controls.Add(btnClose);
            void LayoutHeader()
            {
                btnClose.Location = new Point(header.ClientSize.Width - btnClose.Width - S(20), (header.Height - btnClose.Height) / 2);
                _statePill.Location = new Point(btnClose.Left - _statePill.Width - S(12), (header.Height - _statePill.Height) / 2);
                _statePill.Visible = _statePill.Left > _subLabel.Right + S(8);
            }
            header.Resize += (s, e) => LayoutHeader();
            _statePill.SizeChanged += (s, e) => LayoutHeader();

            _bodyHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.BG,
                Padding = new Padding(S(24))
            };
            Theme.DarkScrollbars(_bodyHost);
            _bodyHost.Resize += (s, e) => _relayout?.Invoke();

            Controls.Add(_bodyHost);
            Controls.Add(header);
        }

        private void SetState(bool unlocked)
        {
            _statePill.Text = unlocked ? "Sesi terbuka" : "Terkunci";
            _statePill.Dot = unlocked ? Theme.SUCCESS : Theme.AMBER;
            _statePill.Fg = unlocked ? Theme.SUCCESS : Theme.AMBER;
            _statePill.Bg = unlocked ? Theme.SUCCESS_BG : Theme.AMBER_BG;
            _statePill.Outline = Theme.Mix(_statePill.Bg, _statePill.Fg, 0.35);
            _statePill.Fit();
        }

        private static TextBox PinBox(string placeholder, int width) => new TextBox
        {
            UseSystemPasswordChar = true,
            PlaceholderText = placeholder,
            MaxLength = 6,
            Font = Theme.FontHead(13f),
            BackColor = Theme.FIELD,
            ForeColor = Theme.TEXT,
            BorderStyle = BorderStyle.FixedSingle,
            Width = width
        };

        /// <summary>Fokuskan kontrol setelah jendela tampil (aman dipanggil dari konstruktor).</summary>
        private void FocusLater(Control c)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() => { if (!c.IsDisposed) c.Focus(); }));
                return;
            }
            void OnShown(object? s, EventArgs e)
            {
                Shown -= OnShown;
                if (!c.IsDisposed) c.Focus();
            }
            Shown += OnShown;
        }

        private static bool IsPin(string pin) => pin.Length is >= 4 and <= 6 && pin.All(char.IsDigit);

        /// <summary>Kartu di tengah area isi (ikut berpindah saat jendela diubah ukurannya).</summary>
        private void ShowCentered(CardPanel card)
        {
            _bodyHost.Controls.Clear();
            _bodyHost.Controls.Add(card);
            _relayout = () =>
            {
                if (card.IsDisposed) return;
                card.Location = new Point(Math.Max(S(16), (_bodyHost.ClientSize.Width - card.Width) / 2), S(36));
            };
            _relayout();
        }

        private void ShowCreatePinView()
        {
            SetState(false);
            _subLabel.Text = "Buat PIN 4–6 digit untuk mengunci Brankas";
            int cw = S(460), inner = cw - S(48);
            var card = new CardPanel { Size = new Size(cw, S(318)), CornerRadius = 16 };

            var lblInfo = new Label { Text = "Buat PIN Brankas baru", Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, AutoSize = true, Location = new Point(S(24), S(22)) };
            var lblHint = new Label { Text = "Gunakan 4–6 digit angka.", Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, AutoSize = true, Location = new Point(S(24), S(52)) };
            var txtPin1 = PinBox("PIN baru (4–6 digit)", inner);
            txtPin1.Location = new Point(S(24), S(84));
            var txtPin2 = PinBox("Ulangi PIN baru", inner);
            txtPin2.Location = new Point(S(24), S(130));

            var btnCreate = Theme.CreateButton("Buat PIN & Buka Brankas", true, Theme.ACCENT, S(42), inner);
            btnCreate.Location = new Point(S(24), S(184));
            btnCreate.Click += (s, e) =>
            {
                string a = txtPin1.Text.Trim();
                if (!IsPin(a))
                {
                    Theme.Toast(this, "PIN harus 4–6 digit angka");
                    return;
                }
                if (a != txtPin2.Text.Trim())
                {
                    Theme.Toast(this, "PIN tidak sama");
                    return;
                }
                VaultManager.SetPin(a);
                ShowVaultListView();
            };
            txtPin2.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; btnCreate.PerformClick(); } };

            var lblWarn = new Label
            {
                Text = "Simpan PIN ini baik-baik. Berkas di Brankas dienkripsi AES-256-GCM dan PIN tidak bisa di-reset.",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.AMBER,
                Location = new Point(S(24), S(240)),
                Size = new Size(inner, S(48))
            };

            card.Controls.AddRange(new Control[] { lblInfo, lblHint, txtPin1, txtPin2, btnCreate, lblWarn });
            ShowCentered(card);
            FocusLater(txtPin1);
        }

        private void ShowLoginView()
        {
            SetState(false);
            _subLabel.Text = "Masukkan PIN Brankas untuk mendekripsi dokumen";
            int cw = S(420), inner = cw - S(48);
            var card = new CardPanel { Size = new Size(cw, S(236)), CornerRadius = 16 };

            var lblInfo = new Label { Text = "Masukkan PIN Brankas", Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, AutoSize = true, Location = new Point(S(24), S(22)) };
            var txtPin = PinBox("PIN 4–6 digit", inner);
            txtPin.Location = new Point(S(24), S(64));

            var btnUnlock = Theme.CreateButton("🔓  Buka Brankas", true, Theme.ACCENT, S(42), inner);
            btnUnlock.Location = new Point(S(24), S(118));
            var lblErr = new Label { Text = "", Font = Theme.FontBody(9f), ForeColor = Theme.ERROR, AutoSize = true, Location = new Point(S(24), S(176)) };
            int attempts = 0;
            btnUnlock.Click += (s, e) =>
            {
                if (VaultManager.CheckPin(txtPin.Text.Trim()))
                {
                    ShowVaultListView();
                }
                else
                {
                    attempts++;
                    lblErr.Text = attempts >= 3 ? $"PIN salah ({attempts}×). Periksa kembali PIN Anda." : "PIN salah.";
                    txtPin.Clear();
                    txtPin.Focus();
                }
            };
            txtPin.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; btnUnlock.PerformClick(); } };

            card.Controls.AddRange(new Control[] { lblInfo, txtPin, btnUnlock, lblErr });
            ShowCentered(card);
            FocusLater(txtPin);
        }

        private void ShowVaultListView()
        {
            SetState(true);
            _subLabel.Text = "Terenkripsi AES-256-GCM • tersimpan hanya di perangkat ini";
            _bodyHost.Controls.Clear();

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.BG
            };
            _bodyHost.Controls.Add(flow);

            int Width0() => Math.Max(S(420), _bodyHost.ClientSize.Width - _bodyHost.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

            void Build()
            {
                int w = Width0();
                flow.SuspendLayout();
                var old = flow.Controls.Cast<Control>().ToList();
                flow.Controls.Clear();
                foreach (var c in old) c.Dispose();

                // bilah aksi atas (gaya toolbar)
                var bar = new Panel { Width = w, Height = S(44), Margin = new Padding(0, 0, 0, S(14)), BackColor = Theme.BG };
                var btnAdd = Theme.CreateButton("+  Tambah & Enkripsi Berkas", true, Theme.ACCENT, S(40), S(250));
                btnAdd.Location = new Point(0, S(2));
                btnAdd.Click += (s, e) => AddFiles();
                var btnLock = Theme.CreateButton("🔒  Kunci Brankas", false, Theme.AMBER, S(40), S(170));
                btnLock.Location = new Point(w - btnLock.Width, S(2));
                btnLock.Click += (s, e) => { CleanupTmp(); ShowLoginView(); };
                bar.Controls.Add(btnAdd);
                bar.Controls.Add(btnLock);
                flow.Controls.Add(bar);

                var files = VaultManager.ListEncryptedFiles();
                var cap = new Label { Text = $"BERKAS TERENKRIPSI ({files.Count})", Font = Theme.FontLabel(8.5f), ForeColor = Theme.MUTED, AutoSize = true, Margin = new Padding(0, 0, 0, S(8)) };
                flow.Controls.Add(cap);

                if (files.Count == 0)
                {
                    var emptyCard = new CardPanel { Width = w, Height = S(96), DashedBorder = true, BorderColor = Theme.LINE_STRONG };
                    emptyCard.Controls.Add(new Label
                    {
                        Text = "Brankas masih kosong. Klik “Tambah & Enkripsi Berkas” untuk mengamankan KTP, ijazah, kontrak, atau dokumen rahasia lain.",
                        Font = Theme.FontBody(9.5f),
                        ForeColor = Theme.MUTED,
                        Location = new Point(S(20), S(16)),
                        Size = new Size(w - S(40), S(64)),
                        TextAlign = ContentAlignment.MiddleCenter
                    });
                    flow.Controls.Add(emptyCard);
                }
                else
                {
                    foreach (var f in files)
                        flow.Controls.Add(BuildRow(f, w));
                }
                flow.ResumeLayout(true);
            }

            _relayout = () =>
            {
                if (flow.IsDisposed) return;
                if (flow.Controls.Count > 0 && Math.Abs(flow.Controls[0].Width - Width0()) < S(4)) return;
                Build();
            };
            Build();
        }

        private Control BuildRow(FileInfo fi, int w)
        {
            string disp = VaultManager.DisplayName(fi);
            var row = new CardPanel { Width = w, Height = S(72), Margin = new Padding(0, 0, 0, S(10)), CornerRadius = 14 };

            var (label, fg, bg) = AppStorage.TypeBadgeOf(AppStorage.MimeByName(disp), disp);
            var chip = new IconChip
            {
                Size = new Size(S(42), S(42)), Radius = S(10), Bg = bg, Fg = fg, Location = new Point(S(16), S(15)),
                Icon = label switch { "PDF" => CardIcon.Pdf, "DOCX" => CardIcon.Word, "XLSX" => CardIcon.Excel, "IMG" => CardIcon.Image, "PPTX" => CardIcon.ImagePdf, _ => CardIcon.Lock }
            };

            bool compact = w < S(600);
            int bw = compact ? S(40) : S(78);
            int bx = w - S(16) - bw * 3 - S(8) * 2;
            int textW = Math.Max(S(80), bx - S(74) - S(12));
            var lblName = new Label { Text = disp, Font = Theme.FontLabel(10.5f), ForeColor = Theme.TEXT, AutoEllipsis = true, UseMnemonic = false, Location = new Point(S(72), S(15)), Size = new Size(textW, S(22)) };
            var lblMeta = new Label { Text = $"{AppStorage.FmtSize(fi.Length)} • AES-256-GCM • {fi.LastWriteTime:dd MMM yyyy, HH:mm}", Font = Theme.FontMono(8f), ForeColor = Theme.MUTED, AutoEllipsis = true, Location = new Point(S(72), S(40)), Size = new Size(textW, S(18)) };

            var btnOpen = Theme.CreateButton(compact ? "👁" : "Buka", false, Theme.ACCENT, S(34), bw);
            btnOpen.Location = new Point(bx, S(19));
            btnOpen.Click += (s, e) => OpenEncryptedFile(fi);
            var btnExport = Theme.CreateButton(compact ? "⇩" : "Ekspor", false, Theme.TEAL, S(34), bw);
            btnExport.Location = new Point(bx + bw + S(8), S(19));
            btnExport.Click += (s, e) => ExportEncryptedFile(fi);
            var btnDel = Theme.CreateButton(compact ? "✕" : "Hapus", false, Theme.RED, S(34), bw);
            btnDel.Location = new Point(bx + (bw + S(8)) * 2, S(19));
            btnDel.Click += (s, e) =>
            {
                if (MessageBox.Show(this, $"Hapus permanen '{disp}' dari Brankas?\nBerkas terenkripsi ini tidak bisa dipulihkan.", "Hapus dari Brankas", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                try
                {
                    fi.Delete();
                    ShowVaultListView();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Berkas tidak dapat dihapus: {ex.Message}", "Brankas", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            btnOpen.AccessibleName = "Buka (didekripsi sementara)";
            btnExport.AccessibleName = "Ekspor salinan terdekripsi";
            btnDel.AccessibleName = "Hapus permanen dari Brankas";

            row.Controls.AddRange(new Control[] { chip, lblName, lblMeta, btnOpen, btnExport, btnDel });
            row.AttachHover();
            return row;
        }

        private void AddFiles()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Pilih Berkas untuk Dienkripsi ke Brankas",
                Multiselect = true,
                Filter = "Semua Berkas (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            int ok = 0;
            var failed = new List<string>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (string f in ofd.FileNames)
                {
                    try { VaultManager.EncryptFile(f); ok++; }
                    catch (Exception ex) { failed.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
                }
            }
            finally { Cursor = Cursors.Default; }
            ShowVaultListView();
            if (failed.Count > 0)
                MessageBox.Show(this, $"{ok} berkas terenkripsi, {failed.Count} gagal:\n\n" + string.Join("\n", failed.Take(8)), "Brankas", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Theme.Toast(this, $"{ok} berkas berhasil dienkripsi ke Brankas");
        }

        private void OpenEncryptedFile(FileInfo encFile)
        {
            try
            {
                Directory.CreateDirectory(_tmpDir);
                string outTmp = Path.Combine(_tmpDir, VaultManager.DisplayName(encFile));
                VaultManager.DecryptFile(encFile.FullName, outTmp);
                using var viewer = new ViewerForm(outTmp);
                viewer.ShowDialog(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal mendekripsi berkas: {ex.Message}", "Vault Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportEncryptedFile(FileInfo encFile)
        {
            try
            {
                Directory.CreateDirectory(_tmpDir);
                string outTmp = Path.Combine(_tmpDir, VaultManager.DisplayName(encFile));
                VaultManager.DecryptFile(encFile.FullName, outTmp);
                string saved = AppStorage.SaveToDownloads(outTmp);
                Theme.Toast(this, $"Diekspor ke Documents\\DepupOffice\\Salinan: {Path.GetFileName(saved)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal mengekspor berkas: {ex.Message}", "Vault Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
