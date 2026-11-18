using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Fitur hasil migrasi DepupOffice Android v4.1.0 → desktop: Dokumen Terlindungi, alat PDF lanjutan (nomor halaman,
    /// watermark, anti-kopi/cetak, password AES-256, gabung campuran, PDF→Word), generator bisnis (nota, katalog, jadwal,
    /// label harga & barcode, pasfoto, grafik Excel, tabel foto→Excel, kartu nama→VCF, watermark foto massal, penjernih
    /// bayangan), dan produktivitas (cari isi berkas, ringkasan dokumen, PPTX &amp; slideshow).
    /// </summary>
    public partial class MainForm
    {
        private static int SS(int v) => Theme.S(v);

        private void Done(string jobTitle, string outPath, string resultTitle, string extra = "")
        {
            AppStorage.AddRecent(outPath);
            AppStorage.SetLastJob(jobTitle, outPath);
            RenderCurrentTab();
            ShowOperationResultDialog(resultTitle, outPath, extra);
        }

        /// <summary>Pilih PDF + pastikan password pembuka (bila ada) sudah dimasukkan.</summary>
        private string? PickPdf(string title)
        {
            var p = Ui.PickFile(this, title, Ui.FilterPdf);
            if (p == null) return null;
            if (ProtectedDoc.IsProtected(p))
            {
                MessageBox.Show(this, "Berkas ini adalah Dokumen Terlindungi DepUp Office. Buka dulu di Reader, lalu gunakan \"Ekspor PDF Asli\" dengan password guru/pemilik untuk mengolahnya.", "Dokumen Terlindungi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            return PdfEngine.EnsureUnlocked(this, p) ? p : null;
        }

        // =====================================================================================
        // DOKUMEN TERLINDUNGI
        // =====================================================================================

        private void ProtectedDocFlow()
        {
            var src = PickPdf("Pilih PDF untuk Dilindungi");
            if (src == null) return;
            string name = Path.GetFileName(src);
            using var d = new Dlg("Dokumen Terlindungi", name + " • Pengaturan Keamanan");
            d.Label("Pilih tingkat keamanan & akses dokumen:", true);
            var chkCover = d.Check("Bungkus Sampul DepUp & Integritas HMAC (hanya bisa dibuka di DepUp Office)", true);
            var chkUser = d.Check("Kunci dengan Password Pembuka (User Password)", false);
            var fUser = d.Field("Password pembuka:", "", true);
            var fUser2 = d.Field("Konfirmasi password pembuka:", "", true);
            fUser.Enabled = fUser2.Enabled = false;
            chkUser.CheckedChanged += (s, e) => fUser.Enabled = fUser2.Enabled = chkUser.Checked;
            var chkCopy = d.Check("Proteksi Anti-Kopi Teks & Gambar (Read-Only)", true);
            var chkPrint = d.Check("Proteksi Anti-Cetak (Print Restriction)", true);
            var chkEdit = d.Check("Proteksi Anti-Edit / Ubah Dokumen", true);
            var chkTeacher = d.Check("Set Password Edit Guru / Pemilik (Master Password)", true);
            var fTeacher = d.Field("Password guru / pemilik (untuk membuka batasan & ekspor PDF asli):", "", true);
            chkTeacher.CheckedChanged += (s, e) => fTeacher.Enabled = chkTeacher.Checked;
            d.Label("Catatan: sampul DepUp memakai format yang sama dengan DepUp Office Android & iOS — dokumen bisa dibuka di HP maupun PC.", false, Theme.MUTED, 8);
            d.Action("Terapkan Proteksi", true, () =>
            {
                string userPw = chkUser.Checked ? fUser.Text.Trim() : "";
                if (chkUser.Checked && userPw.Length < 4) { Theme.Toast(d, "Password pembuka minimal 4 karakter"); return; }
                if (chkUser.Checked && userPw != fUser2.Text.Trim()) { Theme.Toast(d, "Konfirmasi password pembuka tidak sama"); return; }
                string teacher = chkTeacher.Checked ? fTeacher.Text.Trim() : "";
                if (chkTeacher.Checked && teacher.Length < 4) { Theme.Toast(d, "Password guru/pemilik minimal 4 karakter"); return; }
                if (!chkTeacher.Checked && chkCover.Checked)
                {
                    // Tanpa password guru, PDF asli tidak akan pernah bisa diekspor kembali.
                    if (MessageBox.Show(d, "Tanpa password guru/pemilik, PDF asli TIDAK akan bisa diekspor kembali dari dokumen terlindungi. Lanjutkan?", "Konfirmasi", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
                d.Close();
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(name) + "_terlindungi", "pdf");
                if (Ui.Busy(this, "Menerapkan proteksi dokumen…", _ =>
                    PdfToolsX.ProtectDocument(src, outPdf, chkCover.Checked, userPw, !chkCopy.Checked, !chkPrint.Checked, !chkEdit.Checked, teacher)))
                    Done("Dokumen Terlindungi", outPdf, "Dokumen Terlindungi Berhasil Dibuat",
                        chkCover.Checked ? "Hanya bisa dibuka di DepUp Office (PC, Android, iOS)." : "PDF dikunci AES-256 + izin akses.");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        /// <summary>Buka Dokumen Terlindungi di Reader mode aman (salinan terdekripsi sementara, dihapus saat ditutup).</summary>
        private async Task<bool> OpenProtectedDocumentAsync(string path)
        {
            byte[] original;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var (ok, res) = Ui.Busy(this, "Memverifikasi & membuka Dokumen Terlindungi…", _ => ProtectedDoc.Open(bytes));
                if (!ok || res == null) return true;
                original = res;
            }
            catch (Exception ex) { Ui.ShowError(this, ex); return true; }

            // PDF di dalamnya bisa ber-password pembuka & berizin
            string pw = "";
            PdfFile inner;
            while (true)
            {
                try { inner = PdfFile.Open(original, pw); break; }
                catch (PdfPasswordException)
                {
                    var ask = PdfPasswordDialog.Ask(this, Path.GetFileName(path), pw.Length > 0 ? "Password salah, coba lagi." : null);
                    if (ask == null) { Array.Clear(original); return true; }
                    pw = ask;
                }
            }
            string tmp = ProtectedDoc.WriteTempOriginal(original, Path.GetFileName(path));
            if (pw.Length > 0) PdfRenderer.Unlock(tmp, pw);
            bool allowPrint = inner.CanPrint, allowCopy = inner.CanExtract;
            var protectedBytes = File.ReadAllBytes(path);
            var opt = new ViewerOptions
            {
                Secure = true,
                DisplayName = Path.GetFileName(path),
                AllowPrint = allowPrint,
                AllowCopy = allowCopy,
                OnClosed = () => { ProtectedDoc.DeleteTemp(tmp); Array.Clear(original); },
                ExportOriginal = owner =>
                {
                    var t = Ui.AskText(owner, "Ekspor PDF Asli", "Masukkan password guru / pemilik dokumen:", true);
                    if (t == null) return;
                    if (!ProtectedDoc.VerifyEditPassword(protectedBytes, t)) { MessageBox.Show(owner, "Password guru/pemilik salah.", "Ekspor PDF Asli", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(path)) + "_asli", "pdf");
                    try
                    {
                        // dibuka dengan password pemilik → semua batasan & kunci dilepas
                        PdfFile f;
                        try { f = PdfFile.Open(original, t); } catch (PdfPasswordException) { f = PdfFile.Open(original, pw); }
                        using (var fs = File.Create(outPdf)) PdfSave.Write(f, fs);
                        AppStorage.AddRecent(outPdf);
                        MessageBox.Show(owner, "PDF asli (tanpa kunci) disimpan:\n" + outPdf, "Ekspor PDF Asli", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex) { Ui.ShowError(owner, ex); }
                }
            };
            AppStorage.AddRecent(path);
            try { await _historyService.RecordOpenedAsync(path); } catch { }
            await ShowViewerEmbeddedAsync(tmp, opt);
            return true;
        }

        // =====================================================================================
        // ALAT PDF LANJUTAN
        // =====================================================================================

        private void PageNumberFlow()
        {
            var src = PickPdf("Pilih PDF untuk Diberi Nomor Halaman");
            if (src == null) return;
            int total = PdfEngine.PageCount(src);
            using var d = new Dlg("Penomoran Halaman PDF", $"{Path.GetFileName(src)} • {total} halaman");
            var fmt = d.Choice("Format penomoran", new[] { "Hal X dari Y", "Halaman X", "Hanya Angka (X)" });
            var pos = d.Choice("Posisi nomor halaman", new[] { "Tengah Bawah", "Kanan Bawah", "Kiri Bawah" });
            var head = d.Field("Header teks opsional (atas), mis. Laporan Resmi:");
            var skip = d.Check("Lewati halaman sampul pertama (mulai dari halaman 2)", true);
            d.Label("Nomor ditimpa di atas halaman asli — teks PDF tetap tajam & bisa dicari.", false, Theme.TEAL, 6);
            d.Action("Terapkan Nomor Halaman", true, () =>
            {
                d.Close();
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)) + "_nomor", "pdf");
                if (Ui.Busy(this, "Menambahkan nomor halaman…", _ => PdfToolsX.AddPageNumbers(src, outPdf, fmt.SelectedIndex, pos.SelectedIndex, head.Text, skip.Checked)).ok)
                    Done("Nomor Halaman PDF", outPdf, "Nomor Halaman Ditambahkan");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void WatermarkPdfFlow()
        {
            var src = PickPdf("Pilih PDF untuk Diberi Watermark");
            if (src == null) return;
            using var d = new Dlg("Watermark PDF", Path.GetFileName(src));
            var txt = d.Field("Teks watermark:", "RAHASIA");
            var style = d.Choice("Gaya", new[] { "Diagonal besar di tengah", "Diulang (pola ubin)", "Stempel pojok kanan bawah" });
            var colorNames = new[] { "Merah", "Abu-abu", "Biru", "Hitam" };
            var colors = new[] { Color.FromArgb(220, 38, 38), Color.Gray, Theme.ACCENT, Color.Black };
            var col = d.Choice("Warna", colorNames);
            var op = d.Choice("Transparansi", new[] { "Samar (25%)", "Sedang (40%)", "Tebal (65%)" }, 1);
            d.Action("Terapkan Watermark", true, () =>
            {
                string text = txt.Text.Trim();
                if (text.Length == 0) { Theme.Toast(d, "Teks watermark kosong"); return; }
                d.Close();
                int alpha = op.SelectedIndex switch { 0 => 64, 2 => 166, _ => 102 };
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)) + "_watermark", "pdf");
                if (Ui.Busy(this, "Menempelkan watermark…", _ => PdfToolsX.WatermarkPdf(src, outPdf, text, style.SelectedIndex, colors[col.SelectedIndex], alpha)).ok)
                    Done("Watermark PDF", outPdf, "Watermark Berhasil Ditempel");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void RestrictPdfFlow()
        {
            var src = PickPdf("Pilih PDF untuk Dikunci Hak Aksesnya");
            if (src == null) return;
            using var d = new Dlg("Proteksi Anti-Kopi & Anti-Cetak", Path.GetFileName(src));
            d.Label("Dokumen tetap bisa dibuka tanpa password, tetapi hak akses dikunci (AES-256):");
            var copy = d.Check("Izinkan salin teks / gambar (copy-paste)", false);
            var print = d.Check("Izinkan cetak / print dokumen", false);
            var modify = d.Check("Izinkan ubah / edit dokumen", false);
            d.Label("Berlaku di pembaca yang menghormati izin PDF (Adobe Reader, Edge, Chrome, DepUp Office).", false, Theme.MUTED, 6);
            d.Action("Terapkan Proteksi Read-Only", true, () =>
            {
                d.Close();
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)) + "_readonly", "pdf");
                if (Ui.Busy(this, "Menerapkan proteksi read-only…", _ => PdfToolsX.RestrictPermissions(src, outPdf, copy.Checked, print.Checked, modify.Checked)))
                    Done("Proteksi Anti-Kopi PDF", outPdf, "Proteksi Read-Only Diterapkan");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void PasswordPdfFlow()
        {
            var src = PickPdf("Pilih PDF untuk Dikunci Password");
            if (src == null) return;
            using var d = new Dlg("Kunci PDF dengan Password (AES-256)", Path.GetFileName(src) + " • tanpa mengubah halaman jadi gambar");
            var p1 = d.Field("Password pembuka:", "", true);
            var p2 = d.Field("Ulangi password:", "", true);
            var owner = d.Field("Password pemilik (opsional, untuk membuka batasan):", "", true);
            var copy = d.Check("Izinkan salin teks", true);
            var print = d.Check("Izinkan cetak", true);
            d.Action("Kunci PDF", true, () =>
            {
                if (p1.Text.Length < 4) { Theme.Toast(d, "Password minimal 4 karakter"); return; }
                if (p1.Text != p2.Text) { Theme.Toast(d, "Password tidak sama"); return; }
                d.Close();
                string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)) + "_terkunci", "pdf");
                if (Ui.Busy(this, "Mengunci PDF (AES-256)…", _ => PdfToolsX.LockLossless(src, outPdf, p1.Text, owner.Text, new PdfSave.Perms(print.Checked, copy.Checked, true, true))))
                    Done("Password PDF", outPdf, "PDF Berhasil Dikunci", "Enkripsi AES-256 (PDF 2.0 / Acrobat X+). Teks tetap bisa dicari setelah dibuka.");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void UniversalMergeFlow()
        {
            var files = Ui.PickFiles(this, "Pilih Campuran PDF & Foto untuk Digabung", "PDF & Gambar (*.pdf;*.jpg;*.jpeg;*.png;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp");
            if (files == null || files.Length == 0) return;
            var list = files.ToList();
            foreach (var f in list.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                if (!PdfEngine.EnsureUnlocked(this, f)) return;
            using var d = new Dlg("Gabung Campuran ke PDF", $"{list.Count} berkas • PDF tetap asli, foto jadi halaman A4");
            var lb = d.Add(new ListBox { Height = SS(220), Font = Theme.FontBody(10f) });
            void Rebuild(int sel = -1) { lb.Items.Clear(); for (int i = 0; i < list.Count; i++) lb.Items.Add($"{i + 1}. {Path.GetFileName(list[i])}"); if (sel >= 0) lb.SelectedIndex = sel; }
            Rebuild();
            d.Row(d.SmallButton("↑ Naik", () => { int i = lb.SelectedIndex; if (i > 0) { (list[i - 1], list[i]) = (list[i], list[i - 1]); Rebuild(i - 1); } }, Theme.TEXT),
                  d.SmallButton("↓ Turun", () => { int i = lb.SelectedIndex; if (i >= 0 && i < list.Count - 1) { (list[i + 1], list[i]) = (list[i], list[i + 1]); Rebuild(i + 1); } }, Theme.TEXT),
                  d.SmallButton("✕ Hapus", () => { int i = lb.SelectedIndex; if (i >= 0) { list.RemoveAt(i); Rebuild(Math.Min(i, list.Count - 1)); } }, Theme.RED),
                  d.SmallButton("+ Tambah", () => { var more = Ui.PickFiles(d, "Tambah Berkas", "PDF & Gambar (*.pdf;*.jpg;*.jpeg;*.png;*.bmp)|*.pdf;*.jpg;*.jpeg;*.png;*.bmp"); if (more != null) { list.AddRange(more); Rebuild(); } }));
            d.Action("Gabungkan Sekarang", true, () =>
            {
                if (list.Count == 0) { Theme.Toast(d, "Daftar berkas kosong"); return; }
                d.Close();
                string outPdf = AppStorage.NewOut("gabung_campuran", "pdf");
                var (ok, n) = Ui.Busy(this, "Menggabungkan berkas…", p => PdfToolsX.MergeAny(list, outPdf, p));
                if (ok) Done("Gabung Campuran", outPdf, "Berkas Berhasil Digabung", $"{n} halaman dari {list.Count} berkas.");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void PdfToWordFlow()
        {
            var src = PickPdf("Pilih PDF untuk Diubah ke Word");
            if (src == null) return;
            string outDocx = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)), "docx");
            PdfEngine.ClearOcrError();
            var (ok, locked) = Ui.Busy(this, "Mengubah PDF ke Word (teks, tabel & tata letak asli)…", p => PdfToolsX.PdfToWord(src, outDocx, p));
            if (!ok) return;
            if (PdfEngine.LastOcrError != null) Theme.Toast(this, PdfEngine.LastOcrError);
            Done("PDF ke Word", outDocx, "PDF Berhasil Diubah ke Word",
                locked ? "PDF terkunci → Word dibuat HANYA-BACA. Untuk mengedit, gunakan password yang sama (Word: Tinjau → Batasi Pengeditan)." : "Teks, font, tabel, foto & bentuk diambil langsung dari PDF dengan tata letak aslinya. Halaman hasil pindai dibaca dengan OCR offline Windows.");
        }

        /// <summary>EXE → PDF: ambil PDF dari program "PDF to EXE". Berkas .exe hanya dibaca, TIDAK dijalankan.</summary>
        private void ExeToPdfFlow()
        {
            var src = Ui.PickFile(this, "Pilih berkas .exe hasil \"PDF to EXE\" (tidak akan dijalankan)", "Program PDF (*.exe)|*.exe");
            if (src == null) return;
            if (new FileInfo(src).Length > 400L << 20) { Theme.Toast(this, "Berkas terlalu besar (maks. 400 MB)"); return; }
            string outPdf = AppStorage.NewOut(AppStorage.BaseName(Path.GetFileName(src)), "pdf");
            var (ok, res) = Ui.Busy(this, "Mencari PDF di dalam berkas…", _ =>
            {
                var r = ExeToPdf.Extract(File.ReadAllBytes(src));
                File.WriteAllBytes(outPdf, r.Pdf);
                return r;
            });
            if (!ok || res == null) return;
            int pages = Math.Max(0, PdfEngine.PageCount(outPdf));
            Done("EXE ke PDF", outPdf, "PDF Berhasil Diambil",
                $"Metode: {res.Method}{(res.KeyHex != null ? $" (kunci {res.KeyHex})" : "")} • {pages} halaman. Berkas .exe tidak dijalankan sama sekali.");
        }

        private void SlideshowFlow()
        {
            var p = Ui.PickFile(this, "Pilih Presentasi (PPTX) atau PDF", "Presentasi (*.pptx;*.pdf)|*.pptx;*.pdf");
            if (p == null) return;
            if (ProtectedDoc.IsProtected(p)) { OpenDocument(p); return; }
            try { SlideshowForm.Run(this, p); } catch (Exception ex) { Ui.ShowError(this, ex); }
        }

        // =====================================================================================
        // GENERATOR BISNIS
        // =====================================================================================

        /// <summary>Tabel isian baris (DataGridView) untuk daftar barang / agenda.</summary>
        private static DataGridView Grid(Dlg d, string[] headers, IEnumerable<string[]> rows, int height = 200)
        {
            var g = new DataGridView
            {
                Height = SS(height), AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = SS(28),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Font = Theme.FontBody(9.5f),
                BorderStyle = BorderStyle.FixedSingle, SelectionMode = DataGridViewSelectionMode.CellSelect
            };
            foreach (var h in headers) g.Columns.Add(h, h);
            foreach (var r in rows) g.Rows.Add(r.Cast<object>().ToArray());
            d.Add(g);
            return g;
        }

        private static List<string[]> GridRows(DataGridView g) =>
            g.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
             .Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value, CultureInfo.InvariantCulture)?.Trim() ?? "").ToArray())
             .Where(a => a.Any(x => x.Length > 0)).ToList();

        private static long ParseMoney(string s)
        {
            var digits = new string((s ?? "").Where(char.IsDigit).ToArray());
            return long.TryParse(digits, out var v) ? v : 0;
        }

        private void OpenSheetFile(string path)
        {
            AppStorage.AddRecent(path);
            using var f = new SheetEditorForm(path);
            f.ShowDialog(this);
            RenderCurrentTab();
        }

        private void InvoiceFlow()
        {
            using var d = new Dlg("Pembuat Kuitansi / Nota Toko", "Kuitansi resmi • hitung total otomatis", 640);
            var shop = d.Field("Nama toko / usaha:", "Toko Usaha Mandiri");
            var no = d.Field("Nomor nota:", "INV-" + DateTime.Now.ToString("yyyyMMdd-HHmm"));
            var date = d.Field("Tanggal:", DateTime.Now.ToString("dd/MM/yyyy"));
            var cust = d.Field("Nama pembeli / pelanggan:", "Bpk. Pelanggan");
            d.Label("Daftar barang / jasa (Qty & harga angka saja):", true, null, 8);
            var g = Grid(d, new[] { "Nama Barang / Jasa", "Qty", "Harga Satuan" }, new[] { new[] { "Barang A", "2", "25000" }, new[] { "Jasa Service", "1", "100000" } });
            var totalLbl = d.Label("", true, Theme.ACCENT);
            List<InvoiceItem> Items() => GridRows(g).Select(r => new InvoiceItem(r[0], int.TryParse(r.ElementAtOrDefault(1), out var q) ? q : 0, ParseMoney(r.ElementAtOrDefault(2) ?? "")))
                                                    .Where(i => i.Name.Length > 0 && i.Qty > 0).ToList();
            void Recalc() => totalLbl.Text = "Grand total: " + BizGenerators.Rupiah(Items().Sum(i => i.Total));
            g.CellValueChanged += (s, e) => Recalc(); g.RowsRemoved += (s, e) => Recalc(); Recalc();
            Bitmap? stamp = null;
            var stampLbl = d.Label("Stempel / TTD toko: belum dipilih");
            d.Row(d.SmallButton("Pilih Stempel / TTD…", () => { stamp?.Dispose(); stamp = Ui.PickStamp(d); stampLbl.Text = stamp != null ? "Stempel / TTD toko: siap ditempel ✓" : "Stempel / TTD toko: belum dipilih"; }));
            d.Action("Buat PDF Nota", true, () =>
            {
                var items = Items();
                if (items.Count == 0) { Theme.Toast(d, "Tambahkan minimal 1 barang (Qty > 0)"); return; }
                string outPdf = AppStorage.NewOut("Nota_" + AppStorage.BaseName(no.Text), "pdf");
                var st = stamp;
                d.Close();
                if (Ui.Busy(this, "Membuat PDF kuitansi…", _ => BizGenerators.InvoicePdf(outPdf, shop.Text.Trim(), no.Text.Trim(), date.Text.Trim(), cust.Text.Trim(), items, st)))
                    Done("Nota / Kuitansi", outPdf, "PDF Nota Berhasil Dibuat");
            });
            d.Action("Buka di Excel", false, () =>
            {
                var items = Items();
                string outX = AppStorage.NewOut("Nota_" + AppStorage.BaseName(no.Text), "xlsx");
                XlsxWriter.Write(outX, "Nota", BizGenerators.InvoiceGrid(shop.Text.Trim(), no.Text.Trim(), date.Text.Trim(), cust.Text.Trim(), items), new HashSet<int> { 0, 4 });
                d.Close(); OpenSheetFile(outX);
            }, Theme.TEAL);
            d.ShowDialog(this);
            stamp?.Dispose();
        }

        private void CatalogFlow()
        {
            using var d = new Dlg("Pembuat Katalog Produk / Portofolio", "Desain katalog PDF profesional untuk olshop / usaha", 680);
            var title = d.Field("Judul katalog:", "Katalog Produk Olshop");
            var contact = d.Field("Nama toko & kontak:", "Toko Usaha • WA: 08123456789");
            d.Label("Daftar produk (kolom Foto berisi lokasi berkas; klik ganda sel Foto untuk memilih):", true, null, 8);
            var g = Grid(d, new[] { "Nama Produk", "Harga / Subjudul", "Deskripsi", "Foto" }, Array.Empty<string[]>(), 240);
            g.Columns[3].FillWeight = 60;
            g.CellDoubleClick += (s, e) =>
            {
                if (e.ColumnIndex != 3 || e.RowIndex < 0) return;
                var p = Ui.PickFile(d, "Pilih Foto Produk", Ui.FilterImages);
                if (p != null) { g.Rows[e.RowIndex].Cells[3].Value = p; }
            };
            d.Row(d.SmallButton("+ Tambah Banyak Foto…", () =>
            {
                var ps = Ui.PickFiles(d, "Pilih Foto-Foto Produk", Ui.FilterImages);
                if (ps == null) return;
                foreach (var p in ps) g.Rows.Add(Path.GetFileNameWithoutExtension(p), "Rp -", "-", p);
            }));
            d.Action("Buat PDF Katalog", true, () =>
            {
                var items = GridRows(g).Where(r => r[0].Length > 0)
                    .Select(r => new CatalogItem(r[0], string.IsNullOrWhiteSpace(r.ElementAtOrDefault(1)) ? "Rp -" : r[1], string.IsNullOrWhiteSpace(r.ElementAtOrDefault(2)) ? "-" : r[2], r.ElementAtOrDefault(3))).ToList();
                if (items.Count == 0) { Theme.Toast(d, "Tambahkan minimal 1 produk"); return; }
                d.Close();
                string outPdf = AppStorage.NewOut("Katalog_" + AppStorage.BaseName(title.Text), "pdf");
                var (ok, pages) = Ui.Busy(this, "Membuat PDF katalog…", _ => BizGenerators.CatalogPdf(outPdf, title.Text.Trim(), contact.Text.Trim(), items));
                if (ok) Done("Katalog Produk", outPdf, "PDF Katalog Berhasil Dibuat", $"{items.Count} produk • {pages} halaman (8 produk per halaman).");
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void ScheduleFlow()
        {
            using var d = new Dlg("Pembuat Jadwal & Perencana Tugas", "Template agenda kerja & to-do list siap cetak", 640);
            var title = d.Field("Judul perencana:", "Jadwal Kerja & Agenda");
            var owner = d.Field("Nama / tim penanggung jawab:", "Tim Operasional");
            d.Label("Daftar kegiatan & agenda:", true, null, 8);
            var g = Grid(d, new[] { "Hari / Waktu", "Tugas / Kegiatan", "Catatan" }, new[]
            {
                new[] { "Senin, 08:00", "Rapat Evaluasi", "Dir. Operasional" },
                new[] { "Selasa, 10:00", "Review Dokumen & Laporan", "Tim Admin" },
                new[] { "Rabu, 14:00", "Kunjungan Lapangan", "Tim Teknis" }
            }, 220);
            g.Columns[1].FillWeight = 180;
            List<ScheduleTask> Tasks() => GridRows(g).Where(r => r.ElementAtOrDefault(1)?.Length > 0)
                .Select(r => new ScheduleTask(string.IsNullOrWhiteSpace(r[0]) ? "-" : r[0], r[1], string.IsNullOrWhiteSpace(r.ElementAtOrDefault(2)) ? "-" : r[2])).ToList();
            d.Action("Buat PDF Jadwal", true, () =>
            {
                var t = Tasks();
                if (t.Count == 0) { Theme.Toast(d, "Tambahkan minimal 1 agenda"); return; }
                d.Close();
                string outPdf = AppStorage.NewOut("Jadwal_" + AppStorage.BaseName(title.Text), "pdf");
                if (Ui.Busy(this, "Membuat PDF jadwal…", _ => BizGenerators.SchedulePdf(outPdf, title.Text.Trim(), owner.Text.Trim(), t)))
                    Done("Jadwal & Agenda", outPdf, "PDF Jadwal Berhasil Dibuat");
            });
            d.Action("Buka di Excel", false, () =>
            {
                string outX = AppStorage.NewOut("Jadwal_" + AppStorage.BaseName(title.Text), "xlsx");
                XlsxWriter.Write(outX, "Jadwal", BizGenerators.ScheduleGrid(title.Text.Trim(), owner.Text.Trim(), Tasks()), new HashSet<int> { 0, 3 });
                d.Close(); OpenSheetFile(outX);
            }, Theme.TEAL);
            d.ShowDialog(this);
        }

        private void PriceTagFlow()
        {
            string auto = "899" + Random.Shared.Next(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
            using var d = new Dlg("Pembuat Barcode & Label Harga", "Buat label harga toko & cetak lembar A4 (21 label)", 600);
            var pb = d.Add(new PictureBox { Height = SS(170), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SURFACE });
            var prod = d.Field("Nama produk:", "Kopi Susu Gula Aren");
            var code = d.Field("Kode barcode / SKU:", auto);
            var price = d.Field("Harga produk:", "Rp 18.000");
            var fmt = d.Choice("Format barcode", new[] { "Code 128 (huruf & angka)", "EAN-13 (12–13 digit)", "QR Code" });
            Bitmap? tag = null;
            Barcode1D.Format F() => fmt.SelectedIndex switch { 1 => Barcode1D.Format.Ean13, 2 => Barcode1D.Format.Qr, _ => Barcode1D.Format.Code128 };
            var err = d.Label("", false, Theme.RED, 0);
            void Preview()
            {
                try
                {
                    var t = BizGenerators.PriceTagBitmap(prod.Text.Trim().Length > 0 ? prod.Text.Trim() : "Produk", price.Text.Trim(), code.Text.Trim(), F());
                    var old = tag; tag = t; pb.Image = t; old?.Dispose(); err.Text = "";
                }
                catch (Exception ex) { err.Text = ex.Message; }
            }
            foreach (var c in new Control[] { prod, code, price }) c.TextChanged += (s, e) => Preview();
            fmt.SelectedIndexChanged += (s, e) => Preview();
            Preview();
            d.Action("Cetak Lembar A4 (PDF)", true, () =>
            {
                if (err.Text.Length > 0) { Theme.Toast(d, err.Text); return; }
                string outPdf = AppStorage.NewOut("Label_A4_" + AppStorage.BaseName(prod.Text), "pdf");
                string p = prod.Text.Trim(), pr = price.Text.Trim(), cd = code.Text.Trim(); var f = F();
                d.Close();
                if (Ui.Busy(this, "Menyusun lembar label A4…", _ => BizGenerators.PriceTagSheetPdf(outPdf, p, pr, cd, f)))
                    Done("Label Harga", outPdf, "Lembar Label A4 Siap Cetak", "21 label (3 × 7) • barcode vektor tajam. Cetak dengan skala 100%.");
            });
            d.Action("Simpan 1 Label (PNG)", false, () =>
            {
                if (tag == null) return;
                string outPng = Ui.PictureOut("Label_" + prod.Text, "png");
                tag.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Theme.Toast(d, "Label disimpan: " + Path.GetFileName(outPng));
            });
            d.ShowDialog(this);
            tag?.Dispose();
        }

        private void IdPhotoFlow()
        {
            var src = Ui.PickFile(this, "Pilih Foto untuk Pasfoto", Ui.FilterImages);
            if (src == null) return;
            Bitmap raw;
            try { raw = PdfEngine.Decode(src, 1600); } catch (Exception ex) { Ui.ShowError(this, ex); return; }
            using var _r = raw;
            using var d = new Dlg("Pembuat Pasfoto Resmi", "Ukuran 2×3, 3×4, 4×6 & warna latar Merah/Biru/Putih", 600, 760);
            var pb = d.Add(new PictureBox { Height = SS(300), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SURFACE });
            var size = d.Choice("Ukuran pasfoto", BizGenerators.PhotoSizes.Select(x => x.Label), 1);
            var bg = d.Choice("Warna latar belakang", BizGenerators.PhotoBackgrounds.Select(x => x.Label).Append("Asli (tanpa ganti latar)"));
            var tol = d.Choice("Kepekaan deteksi latar", new[] { "Rendah", "Sedang", "Tinggi" }, 1);
            d.Label("Tips: gunakan foto dengan latar polos & kontras dengan baju/rambut.", false, Theme.MUTED, 2);
            Bitmap? cur = null;
            void Update()
            {
                var sz = BizGenerators.PhotoSizes[size.SelectedIndex];
                using var crop = BizGenerators.CropRatio(raw, sz.WCm, sz.HCm);
                Bitmap next = bg.SelectedIndex < BizGenerators.PhotoBackgrounds.Length
                    ? BizGenerators.ChangeBackground(crop, BizGenerators.PhotoBackgrounds[bg.SelectedIndex].Color, tol.SelectedIndex switch { 0 => 55, 2 => 120, _ => 85 })
                    : new Bitmap(crop);
                var old = cur; cur = next; pb.Image = next; old?.Dispose();
            }
            size.SelectedIndexChanged += (s, e) => Update(); bg.SelectedIndexChanged += (s, e) => Update(); tol.SelectedIndexChanged += (s, e) => Update();
            Update();
            d.Action("Cetak Lembar A4 (PDF)", true, () =>
            {
                if (cur == null) return;
                var sz = BizGenerators.PhotoSizes[size.SelectedIndex];
                string outPdf = AppStorage.NewOut($"Pasfoto_A4_{sz.WCm:0}x{sz.HCm:0}", "pdf");
                var photo = new Bitmap(cur);
                d.Close();
                var (ok, n) = Ui.Busy(this, "Menyusun lembar cetak A4…", _ => BizGenerators.IdPhotoSheetPdf(outPdf, photo, sz.WCm, sz.HCm));
                photo.Dispose();
                if (ok) Done("Pasfoto", outPdf, "Lembar Pasfoto A4 Siap Cetak", $"{n} pasfoto ukuran asli {sz.Label}. Cetak dengan skala 100% / \"Actual size\".");
            });
            d.Action("Simpan 1 Pasfoto (PNG)", false, () =>
            {
                if (cur == null) return;
                string outPng = Ui.PictureOut("Pasfoto_" + BizGenerators.PhotoSizes[size.SelectedIndex].Label, "png");
                cur.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Theme.Toast(d, "Pasfoto disimpan: " + Path.GetFileName(outPng));
            });
            d.ShowDialog(this);
            cur?.Dispose();
        }

        private void ChartFlow()
        {
            var src = Ui.PickFile(this, "Pilih Tabel Excel / CSV", Ui.FilterSheets);
            if (src == null) return;
            var sheet = src.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? OfficeReader.ReadCsv(src) : OfficeReader.ReadXlsx(src);
            if (sheet == null || sheet.Rows.Count == 0) { MessageBox.Show(this, "Tabel Excel kosong atau tidak dapat dibaca.", "Grafik Excel"); return; }
            var series = BizGenerators.ExtractSeries(sheet.Rows.Cast<IReadOnlyList<string>>().ToList());
            if (series.Count == 0) { MessageBox.Show(this, "Tidak ditemukan kolom angka untuk dibuat grafik.\nFormat: kolom A = label, kolom B = angka.", "Grafik Excel"); return; }
            string name = Path.GetFileName(src);
            using var d = new Dlg("Grafik & Chart Excel", $"{name} • {series.Count} data", 720, 760);
            var pb = d.Add(new PictureBox { Height = SS(360), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle });
            var type = d.Choice("Jenis grafik", new[] { "Grafik Batang", "Grafik Lingkaran", "Grafik Garis" });
            void Draw() { var old = pb.Image; pb.Image = BizGenerators.ChartBitmap(series, type.SelectedIndex, name, 1200, 750); old?.Dispose(); }
            type.SelectedIndexChanged += (s, e) => Draw(); Draw();
            d.Action("Simpan PDF Laporan", true, () =>
            {
                string outPdf = AppStorage.NewOut("Laporan_Grafik_" + AppStorage.BaseName(name), "pdf");
                int t = type.SelectedIndex;
                d.Close();
                if (Ui.Busy(this, "Membuat PDF laporan grafik…", _ => BizGenerators.ChartPdf(outPdf, series, t, name)))
                    Done("Grafik Excel", outPdf, "PDF Laporan Grafik Disimpan");
            });
            d.Action("Simpan Gambar (PNG)", false, () =>
            {
                string outPng = Ui.PictureOut("Grafik_" + name, "png");
                using var b = BizGenerators.ChartBitmap(series, type.SelectedIndex, name);
                b.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Theme.Toast(d, "Grafik disimpan: " + Path.GetFileName(outPng));
            });
            d.ShowDialog(this);
            pb.Image?.Dispose();
        }

        private void TableToExcelFlow()
        {
            var src = Ui.PickFile(this, "Pilih Foto / Gambar Tabel", Ui.FilterImages);
            if (src == null) return;
            var (ok, rows) = Ui.Busy(this, "Membaca struktur tabel (OCR offline)…", _ =>
            {
                using var bmp = PdfEngine.Decode(src, 2600);
                return DocTools.TableFromImage(bmp);
            });
            if (!ok || rows == null) return;
            if (rows.Count == 0) { MessageBox.Show(this, "Tabel atau teks tidak terdeteksi pada foto ini." + (PdfEngine.LastOcrError != null ? "\n" + PdfEngine.LastOcrError : ""), "Tabel ke Excel"); return; }
            string outX = AppStorage.NewOut("Tabel_" + AppStorage.BaseName(Path.GetFileName(src)), "xlsx");
            XlsxWriter.Write(outX, "Hasil Scan", rows, new HashSet<int> { 0 });
            AppStorage.AddRecent(outX);
            using var d = new Dlg("Tabel Berhasil Dipindai", $"{rows.Count} baris • {rows.Max(r => r.Count)} kolom • {Path.GetFileName(outX)}", 680);
            var g = Grid(d, Enumerable.Range(0, rows.Max(r => r.Count)).Select(XlsxWriter.ColumnName).ToArray(), rows.Take(200).Select(r => r.ToArray()), 300);
            g.ReadOnly = true; g.AllowUserToAddRows = false;
            d.Action("Edit di Excel", true, () => { d.Close(); OpenSheetFile(outX); }, Theme.TEAL);
            d.Action("Salin Teks", false, () => { Clipboard.SetText(string.Join("\r\n", rows.Select(r => string.Join("\t", r)))); Theme.Toast(d, "Tabel disalin ke papan klip"); });
            d.ShowDialog(this);
            RenderCurrentTab();
        }

        private void BusinessCardFlow()
        {
            var src = Ui.PickFile(this, "Pilih Foto Kartu Nama", Ui.FilterImages);
            if (src == null) return;
            var (ok, info) = Ui.Busy(this, "Membaca kartu nama (OCR)…", _ =>
            {
                using var bmp = PdfEngine.Decode(src, 1800);
                return DocTools.ParseCard(AiText.Clean(PdfEngine.RunOcr(bmp).Text));
            });
            if (!ok || info == null) return;
            using var d = new Dlg("Kartu Nama Terdeteksi", "Periksa lalu simpan sebagai kontak (.vcf) — bisa diimpor ke Outlook, Google Contacts, atau HP", 560);
            var fName = d.Field("Nama lengkap:", info.Name); var fPhone = d.Field("Nomor HP / telepon:", info.Phone);
            var fMail = d.Field("Email:", info.Email); var fComp = d.Field("Perusahaan:", info.Company);
            var fTitle = d.Field("Jabatan:", info.Title); var fWeb = d.Field("Situs web:", info.Website); var fAddr = d.Field("Alamat:", info.Address);
            BusinessCardInfo Cur() => new() { Name = fName.Text.Trim(), Phone = fPhone.Text.Trim(), Email = fMail.Text.Trim(), Company = fComp.Text.Trim(), Title = fTitle.Text.Trim(), Website = fWeb.Text.Trim(), Address = fAddr.Text.Trim() };
            d.Action("Simpan Kontak (.vcf)", true, () =>
            {
                var c = Cur();
                string outV = AppStorage.NewOut("Kontak_" + AppStorage.BaseName(string.IsNullOrWhiteSpace(c.Name) ? "kontak" : c.Name), "vcf");
                File.WriteAllText(outV, DocTools.Vcf(c), new System.Text.UTF8Encoding(false));
                d.Close();
                if (MessageBox.Show(this, "Kontak disimpan:\n" + outV + "\n\nBuka sekarang (impor ke aplikasi Kontak/Outlook)?", "Kartu Nama", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start(new ProcessStartInfo(outV) { UseShellExecute = true }); } catch { }
            });
            d.Action("Salin Teks", false, () =>
            {
                var c = Cur();
                Clipboard.SetText(string.Join("\r\n", new[] { c.Name, c.Title, c.Company, c.Phone, c.Email, c.Website, c.Address }.Where(x => x.Length > 0)));
                Theme.Toast(d, "Disalin ke papan klip");
            });
            d.ShowDialog(this);
        }

        private void BatchWatermarkFlow()
        {
            var files = Ui.PickFiles(this, "Pilih Foto-Foto Produk", Ui.FilterImages);
            if (files == null || files.Length == 0) return;
            using var d = new Dlg("Watermark Foto Massal", $"{files.Length} foto dipilih • tempel teks atau stempel toko");
            var txt = d.Field("Teks watermark:", "TOKO OLSHOP");
            Bitmap? stamp = null;
            var stLbl = d.Label("Atau gunakan stempel / logo toko: belum dipilih");
            d.Row(d.SmallButton("Pilih Stempel / Logo…", () => { stamp?.Dispose(); stamp = Ui.PickStamp(d); stLbl.Text = stamp != null ? "Stempel / logo toko siap ditempel ✓ (teks diabaikan)" : "Atau gunakan stempel / logo toko: belum dipilih"; }));
            var pos = d.Choice("Posisi", new[] { "Kanan Bawah", "Tengah", "Kanan Atas", "Kiri Bawah" });
            var op = d.Choice("Ketebalan", new[] { "Samar", "Sedang", "Tebal" }, 2);
            d.Action($"Terapkan ke {files.Length} Foto", true, () =>
            {
                string text = txt.Text.Trim();
                if (stamp == null && text.Length == 0) { Theme.Toast(d, "Isi teks atau pilih stempel"); return; }
                int opacity = op.SelectedIndex switch { 0 => 110, 1 => 160, _ => 210 };
                int p = pos.SelectedIndex; var st = stamp;
                d.Close();
                var outDir = Path.Combine(AppStorage.PicturesDir, "Watermark_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                var (ok, n) = Ui.Busy(this, "Memberi watermark foto…", prog =>
                {
                    Directory.CreateDirectory(outDir);
                    int done = 0;
                    for (int i = 0; i < files.Length; i++)
                    {
                        prog(i + 1, files.Length);
                        try
                        {
                            using var raw = PdfEngine.Decode(files[i], 2400);
                            using var res = DocTools.WatermarkImage(raw, text, st, p, opacity);
                            res.Save(Path.Combine(outDir, Path.GetFileNameWithoutExtension(files[i]) + "_wm.png"), System.Drawing.Imaging.ImageFormat.Png);
                            done++;
                        }
                        catch { }
                    }
                    return done;
                });
                if (ok)
                {
                    MessageBox.Show(this, $"{n} dari {files.Length} foto diberi watermark.\nFolder: {outDir}", "Watermark Foto Massal", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    try { Process.Start("explorer.exe", $"\"{outDir}\""); } catch { }
                }
            });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
            stamp?.Dispose();
        }

        private void ShadowRemoverFlow()
        {
            var src = Ui.PickFile(this, "Pilih Foto Dokumen", Ui.FilterImages);
            if (src == null) return;
            var (ok, pair) = Ui.Busy(this, "Menghapus bayangan & menjernihkan dokumen…", _ =>
            {
                var orig = PdfEngine.Decode(src, 2400);
                return (orig, DocFilter.Clean(orig));
            });
            if (!ok) return;
            var (orig, clean) = pair;
            using var _o = orig; using var _c = clean;
            using var d = new Dlg("Pembersih Bayangan & Penjernih Foto", "Bayangan tangan/HP dihapus • kertas putih • teks dipertajam", 720, 800);
            var pb = d.Add(new PictureBox { Height = SS(420), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.SURFACE, Image = clean });
            var stLbl = d.Label("Tampilan: kertas jernih (tahan tombol di bawah untuk melihat foto asli)", false, Theme.MUTED);
            var cmp = d.SmallButton("👁 Tahan untuk Lihat Asli", () => { }, Theme.TEXT);
            cmp.MouseDown += (s, e) => { pb.Image = orig; stLbl.Text = "Tampilan: foto asli"; };
            cmp.MouseUp += (s, e) => { pb.Image = clean; stLbl.Text = "Tampilan: kertas jernih"; };
            d.Row(cmp);
            d.Action("Simpan ke PDF", true, () =>
            {
                string tmpPng = Path.Combine(Path.GetTempPath(), "depup_clean_" + Guid.NewGuid().ToString("N") + ".png");
                clean.Save(tmpPng, System.Drawing.Imaging.ImageFormat.Png);
                string outPdf = AppStorage.NewOut("Jernih_" + AppStorage.BaseName(Path.GetFileName(src)), "pdf");
                try { PdfEngine.ImagesToPdf(new[] { tmpPng }, outPdf); } finally { try { File.Delete(tmpPng); } catch { } }
                d.Close();
                Done("Penjernih Bayangan", outPdf, "PDF Dokumen Jernih Disimpan");
            });
            d.Action("Simpan PNG", false, () =>
            {
                string outPng = Ui.PictureOut("Jernih_" + Path.GetFileNameWithoutExtension(src), "png");
                clean.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
                Theme.Toast(d, "Disimpan: " + Path.GetFileName(outPng));
            });
            d.ShowDialog(this);
            pb.Image = null;
        }

        // =====================================================================================
        // PRODUKTIVITAS
        // =====================================================================================

        private void GlobalSearchFlow()
        {
            using var d = new Dlg("Cari Teks di Dalam Berkas", "Mencari kata kunci di isi PDF, Word, Excel & PowerPoint (folder DepupOffice + berkas terakhir)", 680, 760);
            var q = d.Field("Kata kunci (minimal 2 huruf):", _searchQuery);
            var deep = d.Check("Termasuk PDF hasil scan (OCR, lebih lambat — maks. 15 halaman per berkas)", true);
            var list = d.Add(new ListView { View = View.Details, FullRowSelect = true, Height = SS(330), Font = Theme.FontBody(9.5f), HeaderStyle = ColumnHeaderStyle.Nonclickable });
            list.Columns.Add("Berkas", SS(180)); list.Columns.Add("Lokasi", SS(90)); list.Columns.Add("Cuplikan", SS(340));
            var status = d.Label("", false, Theme.MUTED);
            void Run()
            {
                string k = q.Text.Trim();
                if (k.Length < 2) { Theme.Toast(d, "Ketik minimal 2 karakter"); return; }
                bool withPdf = deep.Checked;
                var (ok, hits) = Ui.Busy(d, $"Mencari \"{k}\" di dalam berkas…", p =>
                    DocTools.Search(k, 40, withPdf ? 15 : 0, (i, n, _) => p(i, n)));
                if (!ok || hits == null) return;
                list.Items.Clear();
                foreach (var h in hits)
                {
                    var it = new ListViewItem(new[] { Path.GetFileName(h.FilePath), h.Where.Length > 0 ? h.Where : Path.GetExtension(h.FilePath).TrimStart('.').ToUpperInvariant(), "… " + h.Snippet + " …" }) { Tag = h.FilePath, ToolTipText = h.FilePath };
                    list.Items.Add(it);
                }
                status.Text = hits.Count == 0 ? $"Tidak ditemukan \"{k}\" di dalam berkas." : $"Ditemukan {hits.Count} berkas. Klik ganda untuk membuka.";
            }
            q.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(); } };
            list.DoubleClick += (s, e) => { if (list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is string p) { d.Close(); OpenDocument(p); } };
            d.Action("Cari", true, Run);
            d.Action("Tutup", false, d.Close, Theme.TEXT);
            d.ShowDialog(this);
        }

        private void SummarizerFlow()
        {
            var src = Ui.PickFile(this, "Pilih Dokumen untuk Diringkas", Ui.FilterDocs);
            if (src == null) return;
            if (ProtectedDoc.IsProtected(src)) { MessageBox.Show(this, "Isi Dokumen Terlindungi terenkripsi dan tidak bisa diringkas.", "Ringkasan Dokumen"); return; }
            if (src.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !PdfEngine.EnsureUnlocked(this, src)) return;
            var (ok, res) = Ui.Busy(this, "Menganalisis & merangkum dokumen…", p =>
            {
                var text = DocTools.ReadText(src, 20, p);
                return (text, DocTools.KeyPoints(text));
            });
            if (!ok) return;
            var (text, points) = res;
            if (string.IsNullOrWhiteSpace(text)) { MessageBox.Show(this, "Tidak ada teks yang dapat dibaca di berkas ini." + (PdfEngine.LastOcrError != null ? "\n" + PdfEngine.LastOcrError : ""), "Ringkasan Dokumen"); return; }
            int words = DocTools.WordCount(text);
            string name = Path.GetFileName(src);
            using var d = new Dlg("Ringkasan Dokumen", $"{words} kata • {name}", 680, 760);
            d.Label("Poin-poin utama / ringkasan (ekstraktif, 100% offline):", true);
            var box = d.Field("", string.Join("\r\n\r\n", points.Select((p, i) => $"{i + 1}. {p}")), false, true, 14);
            box.ReadOnly = true; box.BackColor = Theme.FIELD; box.ForeColor = Theme.TEXT;
            if (AiText.LooksLikeInjection(text)) d.Label("⚠ Dokumen berisi kalimat yang tampak seperti instruksi tersembunyi untuk AI — kalimat tersebut tidak dimasukkan ke ringkasan.", false, Theme.RED);
            d.Action("Simpan PDF Ringkasan", true, () =>
            {
                string outPdf = AppStorage.NewOut("Ringkasan_" + AppStorage.BaseName(name), "pdf");
                DocTools.SummaryPdf(outPdf, name, words, points);
                d.Close();
                Done("Ringkasan Dokumen", outPdf, "PDF Ringkasan Disimpan");
            });
            d.Action("Salin Teks", false, () => { Clipboard.SetText($"RINGKASAN DOKUMEN: {name}\r\n\r\n" + box.Text); Theme.Toast(d, "Ringkasan disalin"); });
            d.ShowDialog(this);
        }
    }
}
