using System;
using System.Drawing;
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
        private readonly string _tmpDir;

        public VaultForm()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "DepupOffice_VaultTmp");
            CleanupTmp();

            Text = "Vault Brankas AES-256 — Depup Office Desktop";
            Size = new Size(680, 620);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;

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
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Theme.SURFACE,
                Padding = new Padding(18, 12, 18, 12)
            };

            var btnBack = Theme.CreateButton("← Tutup", false, Theme.TEXT, 36, 95);
            btnBack.Location = new Point(18, 16);
            btnBack.Click += (s, e) => Close();
            header.Controls.Add(btnBack);

            var lblTitle = new Label
            {
                Text = "🔒 Vault Brankas AES-256",
                Font = Theme.FontHead(14f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(128, 12)
            };
            header.Controls.Add(lblTitle);

            _subLabel = new Label
            {
                Text = "Terenkripsi AES-256-GCM • Tersimpan lokal di perangkat ini",
                Font = Theme.FontBody(9.5f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(128, 38)
            };
            header.Controls.Add(_subLabel);

            _bodyHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(24)
            };

            Controls.Add(_bodyHost);
            Controls.Add(header);
        }

        private void ShowCreatePinView()
        {
            _subLabel.Text = "Buat PIN 4–6 digit untuk mengunci Brankas";
            _bodyHost.Controls.Clear();

            var card = new CardPanel
            {
                Size = new Size(460, 290),
                Location = new Point(95, 40)
            };

            var lblInfo = new Label
            {
                Text = "Buat PIN Brankas Baru (4–6 digit angka)",
                Font = Theme.FontHead(12f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(24, 24)
            };

            var txtPin1 = new TextBox
            {
                PasswordChar = '●',
                PlaceholderText = "PIN baru (4-6 digit)",
                Font = Theme.FontHead(12f),
                Location = new Point(24, 64),
                Width = 410
            };

            var txtPin2 = new TextBox
            {
                PasswordChar = '●',
                PlaceholderText = "Ulangi PIN baru",
                Font = Theme.FontHead(12f),
                Location = new Point(24, 110),
                Width = 410
            };

            var btnCreate = Theme.CreateButton("Buat PIN & Buka Brankas", true, Theme.ACCENT, 42, 410);
            btnCreate.Location = new Point(24, 162);
            btnCreate.Click += (s, e) =>
            {
                string a = txtPin1.Text.Trim();
                if (a.Length < 4 || a.Length > 6)
                {
                    Theme.Toast(this, "PIN harus 4–6 digit");
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

            var lblWarn = new Label
            {
                Text = "Jangan lupa PIN ini. File di Brankas dienkripsi AES-256-GCM dan tidak memiliki fitur reset.",
                Font = Theme.FontBody(9f),
                ForeColor = Theme.MUTED,
                Location = new Point(24, 218),
                Size = new Size(410, 44)
            };

            card.Controls.AddRange(new Control[] { lblInfo, txtPin1, txtPin2, btnCreate, lblWarn });
            _bodyHost.Controls.Add(card);
        }

        private void ShowLoginView()
        {
            _subLabel.Text = "Masukkan PIN Brankas Anda untuk mendekripsi dokumen";
            _bodyHost.Controls.Clear();

            var card = new CardPanel
            {
                Size = new Size(460, 230),
                Location = new Point(95, 50)
            };

            var lblInfo = new Label
            {
                Text = "Masukkan PIN Brankas",
                Font = Theme.FontHead(12f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(24, 24)
            };

            var txtPin = new TextBox
            {
                PasswordChar = '●',
                PlaceholderText = "PIN 4-6 digit",
                Font = Theme.FontHead(13f),
                Location = new Point(24, 64),
                Width = 410
            };

            var btnUnlock = Theme.CreateButton("🔓 Buka Brankas dengan PIN", true, Theme.ACCENT, 42, 410);
            btnUnlock.Location = new Point(24, 120);
            btnUnlock.Click += (s, e) =>
            {
                if (VaultManager.CheckPin(txtPin.Text.Trim()))
                {
                    ShowVaultListView();
                }
                else
                {
                    Theme.Toast(this, "PIN salah");
                    txtPin.Clear();
                }
            };
            txtPin.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) btnUnlock.PerformClick(); };

            card.Controls.AddRange(new Control[] { lblInfo, txtPin, btnUnlock });
            _bodyHost.Controls.Add(card);
        }

        private void ShowVaultListView()
        {
            _subLabel.Text = "Terenkripsi AES-256-GCM · Tersimpan hanya di perangkat ini";
            _bodyHost.Controls.Clear();

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true
            };

            var btnAdd = Theme.CreateButton("+ Tambah & Enkripsi Berkas ke Brankas", true, Theme.ACCENT, 42, 600);
            btnAdd.Margin = new Padding(0, 0, 0, 16);
            btnAdd.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Pilih Berkas untuk Dienkripsi ke Brankas",
                    Multiselect = true,
                    Filter = "Semua Berkas (*.*)|*.*"
                };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    foreach (string f in ofd.FileNames)
                        VaultManager.EncryptFile(f);
                    Theme.Toast(this, $"{ofd.FileNames.Length} berkas berhasil dienkripsi ke Brankas");
                    ShowVaultListView();
                }
            };
            flow.Controls.Add(btnAdd);

            var files = VaultManager.ListEncryptedFiles();
            if (files.Count == 0)
            {
                var emptyCard = new CardPanel { Width = 600, Height = 80 };
                emptyCard.Controls.Add(new Label
                {
                    Text = "Brankas masih kosong. Klik tombol di atas untuk mengenkripsi KTP, ijazah, atau dokumen rahasia.",
                    Font = Theme.FontBody(10f),
                    ForeColor = Theme.MUTED,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter
                });
                flow.Controls.Add(emptyCard);
            }
            else
            {
                foreach (var f in files)
                {
                    FileInfo fi = f;
                    string disp = VaultManager.DisplayName(fi);
                    var row = new CardPanel
                    {
                        Width = 600,
                        Height = 68,
                        Margin = new Padding(0, 0, 0, 10)
                    };

                    var badge = new Label
                    {
                        Text = "AES",
                        Font = Theme.FontLabel(9f),
                        ForeColor = Theme.AMBER,
                        BackColor = Theme.AMBER_BG,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Size = new Size(44, 40),
                        Location = new Point(14, 14)
                    };

                    var lblName = new Label
                    {
                        Text = disp,
                        Font = Theme.FontLabel(10.5f),
                        ForeColor = Theme.TEXT,
                        AutoSize = true,
                        Location = new Point(70, 14)
                    };

                    var lblMeta = new Label
                    {
                        Text = $"{AppStorage.FmtSize(fi.Length)} • Terenkripsi AES-256-GCM",
                        Font = Theme.FontBody(9f),
                        ForeColor = Theme.MUTED,
                        AutoSize = true,
                        Location = new Point(70, 36)
                    };

                    var btnOpen = Theme.CreateButton("Buka", false, Theme.ACCENT, 32, 74);
                    btnOpen.Location = new Point(345, 18);
                    btnOpen.Click += (s, e) => OpenEncryptedFile(fi);

                    var btnExport = Theme.CreateButton("Ekspor", false, Theme.TEAL, 32, 80);
                    btnExport.Location = new Point(425, 18);
                    btnExport.Click += (s, e) => ExportEncryptedFile(fi);

                    var btnDel = Theme.CreateButton("Hapus", false, Theme.RED, 32, 74);
                    btnDel.Location = new Point(511, 18);
                    btnDel.Click += (s, e) =>
                    {
                        if (MessageBox.Show(this, $"Hapus permanen '{disp}' dari Brankas?", "Hapus dari Brankas", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        {
                            fi.Delete();
                            ShowVaultListView();
                        }
                    };

                    row.Controls.AddRange(new Control[] { badge, lblName, lblMeta, btnOpen, btnExport, btnDel });
                    flow.Controls.Add(row);
                }
            }

            _bodyHost.Controls.Add(flow);
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
