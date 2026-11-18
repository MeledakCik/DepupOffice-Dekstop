using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Core.Office;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Editor Dokumen Word (.docx) — port DocEditorActivity.kt Android v4.1 di atas <see cref="DocxDocument"/>.
    /// Berkas asli TIDAK ditulis ulang dari nol: hanya teks/format/paragraf/tabel/gambar yang diubah pengguna yang disunting,
    /// sehingga tabel kompleks, gambar, header/footer, kotak teks, dan format lain tetap utuh setelah disimpan.
    /// </summary>
    public class DocEditorForm : Form, IMessageFilter
    {
        public const string TEMPLATE_SURAT = "surat";
        public const string TEMPLATE_CV = "cv";
        public const string TEMPLATE_BLANK = "blank";
        // template organisasi & administrasi warga (DocTemplates.kt Android)
        public const string TEMPLATE_IZIN = "izin", TEMPLATE_KET_RTRW = "ket_rtrw", TEMPLATE_UNDANGAN = "undangan",
            TEMPLATE_NOTULEN = "notulen", TEMPLATE_HADIR = "hadir", TEMPLATE_PROPOSAL = "proposal";

        /// <summary>(kunci, judul, keterangan) template organisasi untuk galeri di tab Buat.</summary>
        public static readonly (string Key, string Title, string Sub)[] OrgTemplates =
        {
            (TEMPLATE_IZIN, "Surat Izin", "Tidak masuk sekolah / kerja"),
            (TEMPLATE_KET_RTRW, "Surat Keterangan RT/RW", "Domisili / pengantar"),
            (TEMPLATE_UNDANGAN, "Undangan Rapat", "Rapat pengurus / warga"),
            (TEMPLATE_NOTULEN, "Notulen Rapat", "Catatan hasil rapat"),
            (TEMPLATE_HADIR, "Daftar Hadir", "Tabel nama & tanda tangan"),
            (TEMPLATE_PROPOSAL, "Proposal Kegiatan", "Latar belakang s.d. anggaran"),
        };

        /// <summary>Password "Batasi Pengeditan" yang akan dipasang saat simpan (null = tidak diubah).</summary>
        private string? _protectPw;
        private bool _removeProtection;
        /// <summary>Dokumen terkunci & password tidak dimasukkan: editor ditutup, dokumen dibuka di Reader.</summary>
        private bool _readOnlyRejected;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_readOnlyRejected && _savePath != null)
            {
                var path = _savePath;
                _dirty = false;
                BeginInvoke(new Action(() =>
                {
                    Hide();
                    using (var v = new ViewerForm(path)) v.ShowDialog(Owner);
                    Close();
                }));
            }
        }

        private DocxDocument _doc = null!;
        private string? _savePath;            // null = dokumen baru (Simpan → pilih nama)
        private string _displayName;
        private bool _dirty;
        private float _zoom = 1f;

        private Panel _scroll = null!;
        private Panel _paper = null!;
        private FlowLayoutPanel _bodyFlow = null!;
        private Label _titleLbl = null!, _statusLbl = null!;
        private ComboBox _cmbStyle = null!, _cmbFont = null!, _cmbSize = null!;
        private ParaBox? _current;
        private readonly List<List<EBlock>> _undo = new(), _redo = new();
        private readonly System.Windows.Forms.Timer _snapTimer = new() { Interval = 900 };
        private bool _typingSnapshotTaken;
        private bool _loadingUi;
        private Dictionary<EPara, string> _labels = new();

        internal float PxScale => DeviceDpi / 96f * _zoom;
        internal int Px(int twips) => (int)Math.Round(twips / 15.0 * PxScale);

        public DocEditorForm(string? filePath = null, string? templateKey = null)
        {
            Text = "Editor Dokumen Word — Depup Office";
            Theme.SetDpi(DeviceDpi);   // ukuran tombol & teks mengikuti skala layar (juga bila dibuka langsung)
            Size = new Size(Theme.S(1180), Theme.S(820));
            MinimumSize = new Size(Theme.S(820), Theme.S(560));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;
            KeyPreview = true;
            _displayName = "Dokumen Baru";

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var d = DocxDocument.Open(filePath);
                if (d == null)
                {
                    MessageBox.Show("Berkas ini bukan dokumen Word (.docx) yang valid.", "Editor Word", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    d = NewBlank();
                }
                else { _savePath = filePath; _displayName = Path.GetFileName(filePath); }
                _doc = d!;
                // "Batasi Pengeditan" (hanya-baca + password): seperti Word, mengedit butuh password
                if (_savePath != null && DocxProtect.Of(_savePath) is { } prot)
                {
                    string? msg = null;
                    while (true)
                    {
                        var pw = PdfPasswordDialog.Ask(null, _displayName + " (dikunci hanya-baca)", msg);
                        if (pw == null) { _readOnlyRejected = true; break; }
                        if (prot.Check(pw)) break;
                        msg = "Password salah. Coba lagi, atau Batal untuk membuka di Reader (hanya-baca).";
                    }
                }
            }
            else
            {
                _doc = NewBlank()!;
                ApplyTemplate(templateKey ?? TEMPLATE_BLANK);
            }

            BuildUi();
            RebuildDocument();
            _snapTimer.Tick += (s, e) => { _snapTimer.Stop(); _typingSnapshotTaken = false; };
            Application.AddMessageFilter(this);
            FormClosed += (s, e) => Application.RemoveMessageFilter(this);
        }

        // ================================================================ dokumen baru & templat
        private static DocxDocument? NewBlank()
        {
            var dir = Path.Combine(Path.GetTempPath(), "DepupOffice", "word_new");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "baru_" + Guid.NewGuid().ToString("N") + ".docx");
            var d = DocxDocument.CreateBlank(path);
            if (d != null)
            {
                (int w, int h) = AppStorage.Config.DocPageSize switch { "Letter" => (12240, 15840), "F4" => (11907, 18720), _ => (11906, 16838) };
                int m = AppStorage.Config.DocMarginMode switch { 1 => 680, 2 => 2154, _ => 1417 };
                d.Page = new PageSetup(w, h, false, m, m, m, m);
            }
            return d;
        }

        private void ApplyTemplate(string key)
        {
            var first = _doc.Body.OfType<EPara>().FirstOrDefault();
            int tpl = first?.Origin ?? 0;
            _doc.Body.Clear();
            string? font = AppStorage.Config.DocFontFamily == 1 ? "Times New Roman" : null;
            void P(string text, StdStyle style = StdStyle.Normal, bool bold = false, int align = 0)
            {
                var f = CharFmt.EMPTY with { B = bold ? true : null, Font = font };
                var pf = new ParaFmt(Style: style == StdStyle.Normal ? null : _doc.StyleId(style), Align: align == 0 ? null : align);
                if (AppStorage.Config.DocLineSpacing is > 0 and var ls && Math.Abs(ls - 1f) > 0.01f) pf = pf with { Line = (int)(240 * ls), LineRule = "auto" };
                _doc.Body.Add(EPara.Blank(tpl, pf, f, text));
            }
            switch (key)
            {
                case TEMPLATE_SURAT:
                    P("Surat Lamaran Kerja", StdStyle.Title);
                    P($"Nomor: 001/LAMARAN/{DateTime.Now.Year}", StdStyle.Subtitle);
                    P("Kepada Yth.\nHRD Manager\nPT Nama Perusahaan\nDi Tempat", bold: true);
                    P("Dengan hormat,");
                    P("Berdasarkan informasi lowongan pekerjaan yang saya peroleh, saya bermaksud mengajukan lamaran kerja untuk posisi yang tersedia di perusahaan Bapak/Ibu.", align: 3);
                    P("Nama: (Nama Lengkap)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nPendidikan Terakhir: (Jenjang / Jurusan)\nAlamat: (Alamat Lengkap)\nNo. HP: (Nomor Aktif)\nEmail: (Alamat Email)");
                    P("Sebagai bahan pertimbangan, saya lampirkan Curriculum Vitae, fotokopi ijazah, transkrip nilai, dan dokumen pendukung lainnya. Besar harapan saya untuk dapat diberikan kesempatan wawancara.", align: 3);
                    P("Demikian surat lamaran ini saya buat dengan sebenar-benarnya. Atas perhatian dan kesempatan yang diberikan, saya ucapkan terima kasih.", align: 3);
                    P("Hormat saya,\n\n\n(Nama Lengkap)", align: 2);
                    _displayName = "Surat Lamaran Kerja.docx";
                    break;
                case TEMPLATE_CV:
                    P("Curriculum Vitae", StdStyle.Title);
                    P("Data Pribadi & Riwayat Profesional", StdStyle.Subtitle);
                    P("Data Pribadi", StdStyle.H1);
                    P("Nama Lengkap: (Nama)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nJenis Kelamin: (L/P)\nAlamat: (Alamat)\nNo. HP: (Nomor)\nEmail: (Email)");
                    P("Ringkasan Profil", StdStyle.H1);
                    P("Profesional yang berorientasi pada hasil dengan pengalaman di bidang teknologi & administrasi. Terbiasa bekerja secara mandiri maupun dalam tim, teliti, dan mampu beradaptasi dengan cepat.", align: 3);
                    P("Pengalaman Kerja", StdStyle.H1);
                    P("(Tahun – Sekarang) — (Nama Perusahaan)\nPosisi: (Jabatan)\nTanggung jawab: (Uraikan pekerjaan dan pencapaian utama)");
                    P("Pendidikan", StdStyle.H1);
                    P("(Tahun – Tahun) — (Nama Institusi)\nJenjang / Jurusan: (S1 / Teknik Informatika)\nIPK: (Nilai)");
                    P("Keahlian", StdStyle.H1);
                    int bul = _doc.NumIdFor(true);
                    foreach (var s in new[] { "Microsoft Office & Administrasi Dokumen", "Analisis Data & Pelaporan", "Komunikasi & Manajemen Proyek" })
                        _doc.Body.Add(EPara.Blank(tpl, new ParaFmt(NumId: bul, Ilvl: 0), CharFmt.EMPTY with { Font = font }, s));
                    _displayName = "Curriculum Vitae.docx";
                    break;
                case TEMPLATE_IZIN:
                    P($"(Kota), {Today()}", align: 2);
                    P("Kepada Yth.\n(Bapak/Ibu Wali Kelas / Atasan)\n(Nama Sekolah / Instansi)\ndi Tempat");
                    P("Perihal: Permohonan Izin", bold: true);
                    P("Dengan hormat,");
                    P("Yang bertanda tangan di bawah ini:");
                    P("Nama\t\t: (Nama Lengkap)\nKelas / Jabatan\t: (Kelas / Jabatan)\nAlamat\t\t: (Alamat)");
                    P("Dengan ini memohon izin tidak dapat mengikuti kegiatan belajar / bekerja pada hari (Hari), tanggal (Tanggal), dikarenakan (alasan, misalnya: sakit / keperluan keluarga yang tidak dapat ditinggalkan).", align: 3);
                    P("Demikian surat izin ini saya sampaikan. Atas perhatian dan izin yang diberikan, saya ucapkan terima kasih.", align: 3);
                    P("Hormat saya,\n\n\n\n(Nama Lengkap)", align: 2);
                    _displayName = "Surat Izin.docx";
                    break;
                case TEMPLATE_KET_RTRW:
                    Kop("RUKUN TETANGGA (RT) 00X / RUKUN WARGA (RW) 00X", "Kelurahan (Nama) · Kecamatan (Nama) · Kota/Kabupaten (Nama)");
                    P("SURAT KETERANGAN", bold: true, align: 1);
                    P($"Nomor: (00X)/RT.00X/RW.00X/{RomanMonth()}/{DateTime.Now.Year}", align: 1);
                    P("Yang bertanda tangan di bawah ini Ketua RT 00X / RW 00X Kelurahan (Nama), Kecamatan (Nama), menerangkan bahwa:", align: 3);
                    P("Nama\t\t: (Nama Lengkap)\nNIK\t\t: (16 digit NIK)\nTempat/Tgl Lahir\t: (Kota, Tanggal)\nJenis Kelamin\t: (Laki-laki / Perempuan)\nAgama\t\t: (Agama)\nPekerjaan\t\t: (Pekerjaan)\nAlamat\t\t: (Alamat Lengkap)");
                    P("Adalah benar warga kami yang berdomisili di alamat tersebut di atas. Surat keterangan ini dibuat untuk keperluan (keperluan, misalnya: pengurusan KTP / KK / SKCK / domisili usaha).", align: 3);
                    P("Demikian surat keterangan ini dibuat dengan sebenarnya untuk dapat dipergunakan sebagaimana mestinya.", align: 3);
                    P($"(Kota), {Today()}", align: 2);
                    SignTable("Ketua RW 00X", "Ketua RT 00X");
                    _displayName = "Surat Keterangan RT-RW.docx";
                    break;
                case TEMPLATE_UNDANGAN:
                    Kop("(NAMA ORGANISASI, misalnya KARANG TARUNA ...)", "Sekretariat: (Alamat) · Telp: (Nomor)");
                    P($"Nomor\t: (00X)/UND/(Singkatan)/{RomanMonth()}/{DateTime.Now.Year}\nLampiran\t: -\nPerihal\t: Undangan Rapat");
                    P($"(Kota), {Today()}", align: 2);
                    P("Kepada Yth.\nSeluruh Pengurus dan Anggota\n(Nama Organisasi)\ndi Tempat");
                    P("Dengan hormat,");
                    P("Sehubungan dengan (maksud rapat, misalnya: persiapan kegiatan peringatan HUT RI), kami mengundang Saudara/i untuk hadir dalam rapat yang akan dilaksanakan pada:", align: 3);
                    P("Hari / Tanggal\t: (Hari, Tanggal)\nWaktu\t\t: (Jam) WIB s.d. selesai\nTempat\t\t: (Lokasi)\nAcara\t\t: 1. (Agenda pertama)\n\t\t  2. (Agenda kedua)\n\t\t  3. Lain-lain");
                    P("Mengingat pentingnya acara tersebut, kami mengharapkan kehadiran Saudara/i tepat waktu. Atas perhatian dan kehadirannya, kami ucapkan terima kasih.", align: 3);
                    SignTable("Ketua", "Sekretaris");
                    _displayName = "Undangan Rapat.docx";
                    break;
                case TEMPLATE_NOTULEN:
                    P("Notulen Rapat", StdStyle.Title);
                    P("(Nama Organisasi)", align: 1);
                    P("Hari / Tanggal\t: (Hari, Tanggal)\nWaktu\t\t: (Jam mulai) – (Jam selesai) WIB\nTempat\t\t: (Lokasi)\nPimpinan Rapat\t: (Nama)\nNotulis\t\t: (Nama)\nPeserta\t\t: (Jumlah) orang (daftar hadir terlampir)");
                    P("Agenda", StdStyle.H1);
                    P("1. (Agenda pertama)\n2. (Agenda kedua)\n3. Lain-lain");
                    P("Jalannya Rapat", StdStyle.H1);
                    P("1. Rapat dibuka oleh (Nama) pada pukul (Jam) WIB.\n2. (Ringkasan pembahasan agenda pertama).\n3. (Ringkasan pembahasan agenda kedua).", align: 3);
                    P("Keputusan / Kesimpulan", StdStyle.H1);
                    P("1. (Keputusan pertama)\n2. (Keputusan kedua)");
                    P("Tindak Lanjut", StdStyle.H1);
                    GridTable(new[] { "No", "Kegiatan", "Penanggung Jawab", "Batas Waktu" }, 4, new[] { 1, 5, 3, 3 }, numbered: true);
                    P("Rapat ditutup pada pukul (Jam) WIB.");
                    P($"(Kota), {Today()}", align: 2);
                    SignTable("Pimpinan Rapat", "Notulis");
                    _displayName = "Notulen Rapat.docx";
                    break;
                case TEMPLATE_HADIR:
                    P("Daftar Hadir", StdStyle.Title);
                    P("Kegiatan\t\t: (Nama Kegiatan / Rapat)\nHari / Tanggal\t: (Hari, Tanggal)\nTempat\t\t: (Lokasi)");
                    GridTable(new[] { "No", "Nama", "Jabatan / Alamat", "No. HP", "Tanda Tangan" }, 20, new[] { 1, 5, 4, 3, 3 }, numbered: true);
                    P($"(Kota), {Today()}", align: 2);
                    P("Mengetahui,\nKetua\n\n\n\n(Nama Lengkap)", align: 2);
                    _displayName = "Daftar Hadir.docx";
                    break;
                case TEMPLATE_PROPOSAL:
                    P("Proposal Kegiatan", StdStyle.Title);
                    P($"(Nama Kegiatan) · (Nama Organisasi) · {DateTime.Now.Year}", StdStyle.Subtitle);
                    P("I. Latar Belakang", StdStyle.H1);
                    P("(Jelaskan alasan kegiatan perlu diadakan, kondisi saat ini, dan manfaatnya bagi warga / anggota.)", align: 3);
                    P("II. Nama Kegiatan", StdStyle.H1);
                    P("(Nama lengkap kegiatan)");
                    P("III. Tujuan", StdStyle.H1);
                    P("1. (Tujuan pertama)\n2. (Tujuan kedua)\n3. (Tujuan ketiga)");
                    P("IV. Sasaran / Peserta", StdStyle.H1);
                    P("(Siapa peserta dan perkiraan jumlahnya.)");
                    P("V. Waktu dan Tempat", StdStyle.H1);
                    P("Hari / Tanggal\t: (Hari, Tanggal)\nWaktu\t\t: (Jam) WIB s.d. selesai\nTempat\t\t: (Lokasi)");
                    P("VI. Susunan Panitia", StdStyle.H1);
                    GridTable(new[] { "No", "Jabatan", "Nama" }, 6, new[] { 1, 4, 6 }, numbered: true,
                        fill: new[] { "Penanggung Jawab", "Ketua Pelaksana", "Sekretaris", "Bendahara", "Sie Acara", "Sie Perlengkapan" });
                    P("VII. Rencana Anggaran Biaya", StdStyle.H1);
                    GridTable(new[] { "No", "Uraian", "Jumlah", "Harga Satuan (Rp)", "Total (Rp)" }, 5, new[] { 1, 5, 2, 3, 3 }, numbered: true,
                        footer: new[] { "", "TOTAL", "", "", "(Jumlah)" });
                    P("VIII. Penutup", StdStyle.H1);
                    P("Demikian proposal ini kami susun. Besar harapan kami atas dukungan dan partisipasi Bapak/Ibu demi terlaksananya kegiatan ini. Atas perhatian dan bantuannya, kami ucapkan terima kasih.", align: 3);
                    P($"(Kota), {Today()}", align: 2);
                    SignTable("Ketua Pelaksana", "Sekretaris");
                    P("Mengetahui,\n(Ketua Organisasi / Lurah / Kepala Desa)\n\n\n\n(Nama Lengkap)", align: 1);
                    _displayName = "Proposal Kegiatan.docx";
                    break;
                default:
                    P("");
                    break;
            }
            if (_doc.Body.Count == 0 || _doc.Body[^1] is not EPara) P("");

            // ---- bantuan template
            void Kop(string org, string addr)
            {
                P(org, bold: true, align: 1);
                P(addr, align: 1);
                P("______________________________________________________________", align: 1);
            }
            ECell Cell(string text, bool bold = false) =>
                new(-1, new List<EPara> { EPara.Blank(tpl, new ParaFmt(After: 0), CharFmt.EMPTY with { B = bold ? true : null, Font = font }, text) });
            void GridTable(string[] header, int rows, int[] weights, bool numbered = false, string[]? fill = null, string[]? footer = null)
            {
                int tw = (_doc.Page ?? new PageSetup(11906, 16838, false, 1440, 1440, 1440, 1440)).TextWidth;
                int sum = weights.Sum();
                var grid = weights.Select(w => tw * w / sum).ToList();
                var list = new List<ERow> { new(-1, header.Select(h => Cell(h, true)).ToList()) };
                for (int r = 1; r <= rows; r++)
                    list.Add(new ERow(-1, header.Select((_, c) => Cell(c == 0 && numbered ? r.ToString() : c == 1 && fill != null && r - 1 < fill.Length ? fill[r - 1] : "")).ToList()));
                if (footer != null) list.Add(new ERow(-1, footer.Select(t => Cell(t, true)).ToList()));
                _doc.Body.Add(new ETable(-1, list, grid, true));
                P("");
            }
            void SignTable(string left, string right)
            {
                int tw = (_doc.Page ?? new PageSetup(11906, 16838, false, 1440, 1440, 1440, 1440)).TextWidth;
                ECell Sign(string role) => new(-1, new List<EPara>
                {
                    EPara.Blank(tpl, new ParaFmt(Align: 1, After: 0), CharFmt.EMPTY with { Font = font }, role),
                    EPara.Blank(tpl, new ParaFmt(Align: 1, After: 0)), EPara.Blank(tpl, new ParaFmt(Align: 1, After: 0)), EPara.Blank(tpl, new ParaFmt(Align: 1, After: 0)),
                    EPara.Blank(tpl, new ParaFmt(Align: 1, After: 0), CharFmt.EMPTY with { Font = font, B = true }, "(Nama Lengkap)")
                });
                _doc.Body.Add(new ETable(-1, new List<ERow> { new(-1, new List<ECell> { Sign(left), Sign(right) }) }, new List<int> { tw / 2, tw - tw / 2 }, true, bordered: false));
            }
        }

        private static string Today() => DateTime.Now.ToString("d MMMM yyyy", new System.Globalization.CultureInfo("id-ID"));
        private static string RomanMonth() => new[] { "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII" }[DateTime.Now.Month - 1];

        // ================================================================ UI
        private Button Tb(string text, string tip, Action act, int w = 0)
        {
            var b = Theme.CreateButton(text, false, Theme.TEXT, Theme.S(34), w > 0 ? Theme.S(w) : 0);
            b.BackColor = Theme.SURFACE; b.Font = Theme.FontLabel(9.5f);
            b.Padding = new Padding(Theme.S(6), 0, Theme.S(6), 0);
            b.Margin = new Padding(Theme.S(2), Theme.S(3), Theme.S(2), Theme.S(3));
            b.TabStop = false;
            b.Click += (s, e) => { act(); _current?.Focus(); };
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        private Control Sep() => new Panel { Width = 1, Height = Theme.S(26), BackColor = Theme.LINE, Margin = new Padding(Theme.S(6), Theme.S(7), Theme.S(6), 0) };

        private void BuildUi()
        {
            int S(int v) => Theme.S(v);
            // ---- bilah atas
            var top = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Theme.BG };
            var back = Theme.CreateButton("←  Kembali", false, Theme.TEXT, S(40), S(120));
            back.BackColor = Theme.SURFACE; back.Location = new Point(S(16), S(12)); back.Click += (s, e) => Close();
            _titleLbl = new Label { AutoSize = true, Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, Location = new Point(S(150), S(20)), UseMnemonic = false };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, S(12), S(16), 0), BackColor = Theme.BG };
            var undo = Theme.CreateButton("↩", false, Theme.TEXT, S(40), S(44)); undo.BackColor = Theme.SURFACE; undo.Click += (s, e) => Undo();
            var redo = Theme.CreateButton("↪", false, Theme.TEXT, S(40), S(44)); redo.BackColor = Theme.SURFACE; redo.Click += (s, e) => Redo();
            undo.Font = redo.Font = new Font("Segoe UI Symbol", 13f); undo.Padding = redo.Padding = Padding.Empty;
            var preview = Theme.CreateButton("👁  Pratinjau / PDF", false, Theme.ACCENT, S(40)); preview.BackColor = Theme.SURFACE; preview.Click += (s, e) => PreviewDoc();
            var saveAs = Theme.CreateButton("Simpan Sebagai…", false, Theme.TEXT, S(40)); saveAs.BackColor = Theme.SURFACE; saveAs.Click += (s, e) => SaveAs();
            var save = Theme.CreateButton("💾  Simpan", true, Theme.ACCENT, S(40), S(120)); save.Click += (s, e) => Save();
            foreach (var b in new Control[] { undo, redo, preview, saveAs, save }) { b.Margin = new Padding(S(4), 0, S(4), 0); right.Controls.Add(b); }
            new ToolTip().SetToolTip(undo, "Urungkan (Ctrl+Z)"); new ToolTip().SetToolTip(redo, "Ulangi (Ctrl+Y)");
            top.Controls.AddRange(new Control[] { back, _titleLbl, right });

            // ---- toolbar format
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, BackColor = Theme.SURFACE, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoScroll = false, Padding = new Padding(S(10), S(2), S(10), S(6)) };  // membungkus ke baris kedua seperti ribbon saat jendela sempit
            _cmbStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(150), Font = Theme.FontBody(9.5f), Margin = new Padding(S(2), S(8), S(4), 0) };
            foreach (StdStyle s in Enum.GetValues(typeof(StdStyle))) _cmbStyle.Items.Add(StdStyles.Label(s));
            _cmbStyle.SelectionChangeCommitted += (s, e) => { ApplyStyle((StdStyle)_cmbStyle.SelectedIndex); _current?.Focus(); };
            _cmbFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = S(150), Font = Theme.FontBody(9.5f), Margin = new Padding(S(2), S(8), S(4), 0) };
            _cmbFont.Items.AddRange(new object[] { "Calibri", "Arial", "Times New Roman", "Cambria", "Georgia", "Verdana", "Tahoma", "Segoe UI", "Garamond", "Book Antiqua", "Century Gothic", "Comic Sans MS", "Courier New", "Bookman Old Style" });
            _cmbFont.SelectionChangeCommitted += (s, e) => { if (_cmbFont.SelectedItem is string f) ApplyChar(c => c with { Font = f }); _current?.Focus(); };
            _cmbFont.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; var f = _cmbFont.Text.Trim(); if (f.Length > 0) ApplyChar(c => c with { Font = f }); _current?.Focus(); } };
            _cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = S(58), Font = Theme.FontBody(9.5f), Margin = new Padding(S(2), S(8), S(4), 0) };
            _cmbSize.Items.AddRange(new object[] { "8", "9", "10", "10.5", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48", "72" });
            void SetSize() { if (double.TryParse(_cmbSize.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt) && pt >= 1 && pt <= 400) ApplyChar(c => c with { Sz = (int)Math.Round(pt * 2) }); }
            _cmbSize.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(() => { SetSize(); _current?.Focus(); }));
            _cmbSize.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetSize(); _current?.Focus(); } };

            bar.Controls.AddRange(new Control[]
            {
                _cmbStyle, _cmbFont, _cmbSize, Sep(),
                Tb("B", "Tebal (Ctrl+B)", () => ToggleChar(c => c.B == true, (c, v) => c with { B = v }), 36),
                Tb("I", "Miring (Ctrl+I)", () => ToggleChar(c => c.I == true, (c, v) => c with { I = v }), 36),
                Tb("U", "Garis bawah (Ctrl+U)", () => ToggleChar(c => c.U == true, (c, v) => c with { U = v }), 36),
                Tb("S", "Coret", () => ToggleChar(c => c.S == true, (c, v) => c with { S = v }), 36),
                Tb("A▾", "Warna teks", PickColor, 46),
                Tb("🖍", "Stabilo", PickHighlight, 42),
                Sep(),
                Tb("⯇", "Rata kiri (Ctrl+L)", () => ApplyPara(p => p with { Align = 0 }), 36),
                Tb("≡", "Rata tengah (Ctrl+E)", () => ApplyPara(p => p with { Align = 1 }), 36),
                Tb("⯈", "Rata kanan (Ctrl+R)", () => ApplyPara(p => p with { Align = 2 }), 36),
                Tb("☰", "Rata kiri-kanan (Ctrl+J)", () => ApplyPara(p => p with { Align = 3 }), 36),
                Sep(),
                Tb("•≡", "Daftar poin", () => ToggleList(true), 42),
                Tb("1≡", "Daftar nomor", () => ToggleList(false), 42),
                Tb("⇤", "Kurangi indentasi", () => Indent(-1), 36),
                Tb("⇥", "Tambah indentasi", () => Indent(1), 36),
                Tb("↕", "Spasi baris", LineSpacingMenu, 36),
                Sep(),
                Tb("▦ Tabel", "Sisipkan tabel", InsertTable),
                Tb("🖼 Gambar", "Sisipkan gambar", InsertImage),
                Tb("✍ TTD", "Sisipkan tanda tangan / stempel dari pustaka", InsertSignature),
                Tb("⤓ Halaman", "Pemisah halaman", InsertPageBreak),
                Tb("📄 Halaman…", "Ukuran kertas, orientasi & margin", PageSetupDialog),
                Sep(),
                Tb("🔍 Cari", "Cari & Ganti (Ctrl+F / Ctrl+H)", () => ShowFindReplace(false)),
                Tb("⋯ Lainnya", "Hapus format, superskrip, kunci dokumen, bacakan", MoreMenu),
            });

            // ---- status
            var status = new Panel { Dock = DockStyle.Bottom, Height = S(30), BackColor = Theme.SURFACE };
            _statusLbl = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(16), 0, 0, 0), Font = Theme.FontBody(9f), ForeColor = Theme.MUTED };
            var zoomOut = Theme.CreateButton("−", false, Theme.TEXT, S(24), S(36)); zoomOut.Click += (s, e) => SetZoom(_zoom - 0.1f);
            var zoomIn = Theme.CreateButton("+", false, Theme.TEXT, S(24), S(36)); zoomIn.Click += (s, e) => SetZoom(_zoom + 0.1f);
            zoomOut.Padding = zoomIn.Padding = Padding.Empty; zoomOut.BackColor = zoomIn.BackColor = Theme.SOFT;
            var zf = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(0, S(3), S(12), 0) };
            zf.Controls.AddRange(new Control[] { zoomOut, zoomIn });
            status.Controls.Add(_statusLbl); status.Controls.Add(zf);

            // ---- kertas
            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.CANVAS };
            Theme.DarkScrollbars(_scroll);
            _paper = new Panel { BackColor = Color.White };
            _bodyFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.White, Margin = Padding.Empty, Padding = Padding.Empty };
            _bodyFlow.SizeChanged += (s, e) => LayoutPaper();
            _paper.Controls.Add(_bodyFlow);
            _scroll.Controls.Add(_paper);
            _scroll.Resize += (s, e) => LayoutPaper();
            _paper.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(40, 0, 0, 0));
                e.Graphics.DrawRectangle(pen, 0, 0, _paper.Width - 1, _paper.Height - 1);
            };

            Controls.Add(_scroll);
            Controls.Add(status);
            Controls.Add(bar);
            Controls.Add(top);
            UpdateTitle();
        }

        private PageSetup Page => _doc.Page ?? new PageSetup(11906, 16838, false, 1440, 1440, 1440, 1440);
        internal int TextWidthPx => Px(Page.TextWidth);

        private void LayoutPaper()
        {
            var pg = Page;
            int w = Px(pg.W), padL = Px(pg.Left), padT = Px(pg.Top), padB = Px(pg.Bottom);
            _bodyFlow.Location = new Point(padL, padT);
            int h = Math.Max(Px(pg.H), padT + _bodyFlow.Height + padB);
            int x = Math.Max(Theme.S(20), (_scroll.ClientSize.Width - w) / 2);
            _paper.Bounds = new Rectangle(x + _scroll.AutoScrollPosition.X, Theme.S(20) + _scroll.AutoScrollPosition.Y, w, h);
            _scroll.AutoScrollMinSize = new Size(w + Theme.S(40), h + Theme.S(40));
        }

        // ================================================================ membangun tampilan dari model
        private void RebuildDocument(EPara? focusPara = null, int caret = 0)
        {
            _loadingUi = true;
            _scroll.SuspendLayout(); _bodyFlow.SuspendLayout();
            var old = _bodyFlow.Controls.Cast<Control>().ToList();
            _bodyFlow.Controls.Clear();
            foreach (var c in old) c.Dispose();
            _current = null;
            _labels = _doc.ListLabels();
            foreach (var b in _doc.Body) _bodyFlow.Controls.Add(MakeBlock(b));
            _bodyFlow.ResumeLayout(true); _scroll.ResumeLayout(true);
            _loadingUi = false;
            LayoutPaper();
            UpdateStatus();
            var target = focusPara != null ? AllBoxes().FirstOrDefault(x => ReferenceEquals(x.Para, focusPara)) : AllBoxes().FirstOrDefault();
            if (target == null) return;
            void FocusTarget() { if (target.IsDisposed) return; target.Focus(); target.Select(Math.Min(caret, target.TextLength), 0); _scroll.ScrollControlIntoView(target); }
            if (IsHandleCreated) BeginInvoke(new Action(FocusTarget));
            else
            {
                // dipanggil dari konstruktor: jendela belum ada → fokuskan saat pertama tampil
                void OnShown(object? s, EventArgs e) { Shown -= OnShown; FocusTarget(); }
                Shown += OnShown;
            }
        }

        private Control MakeBlock(EBlock b) => b switch
        {
            EPara p => new ParaHost(this, p, _doc.Body, null, null, TextWidthPx),
            ETable t => new TableHost(this, t),
            EImage im => MakeImageBlock(im),
            EBreak br => MakeBreakBlock(br),
            _ => new Panel()
        };

        private Control MakeImageBlock(EImage im)
        {
            var host = new Panel { Width = TextWidthPx, BackColor = Color.White, Margin = new Padding(0, Theme.S(4), 0, Theme.S(4)), Tag = im };
            try
            {
                using var ms = new MemoryStream(im.Bytes);
                var img = Image.FromStream(ms);
                int w = (int)(TextWidthPx * im.WidthFrac), h = (int)(w * (double)img.Height / Math.Max(1, img.Width));
                var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(w, h), Location = new Point((TextWidthPx - w) / 2, 0), Cursor = Cursors.Hand };
                host.Height = h;
                host.Controls.Add(pb);
                pb.ContextMenuStrip = BlockMenu(im, "gambar");
            }
            catch { host.Height = Theme.S(30); }
            return host;
        }

        private Control MakeBreakBlock(EBreak br)
        {
            var l = new Label { Width = TextWidthPx, Height = Theme.S(22), Text = "──────── Pemisah halaman ────────", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.PAPER_MUTED, Font = Theme.FontBody(8.5f), Tag = br };
            l.ContextMenuStrip = BlockMenu(br, "pemisah halaman");
            return l;
        }

        private ContextMenuStrip BlockMenu(EBlock b, string what)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Hapus " + what, null, (s, e) => { Snapshot(); _doc.Body.Remove(b); MarkDirty(); RebuildDocument(); });
            return m;
        }

        internal IEnumerable<ParaBox> AllBoxes()
        {
            foreach (Control c in _bodyFlow.Controls)
            {
                if (c is ParaHost ph) yield return ph.Box;
                else if (c is TableHost th) foreach (var b in th.Boxes()) yield return b;
            }
        }

        internal string? LabelFor(EPara p) => _labels.TryGetValue(p, out var l) ? l : null;

        internal void RefreshLabels()
        {
            _labels = _doc.ListLabels();
            foreach (var b in AllBoxes()) (b.Parent as ParaHost)?.UpdateLabel();
        }

        // ================================================================ status & kotor
        internal void MarkDirty()
        {
            if (!_dirty) { _dirty = true; UpdateTitle(); }
            _redo.Clear();
        }

        private void UpdateTitle()
        {
            _titleLbl.Text = (_dirty ? "● " : "") + _displayName;
            Text = _displayName + " — Editor Word Depup Office";
        }

        internal void UpdateStatus()
        {
            var pg = Page;
            string size = (pg.W, pg.H) switch
            {
                (11906, 16838) or (16838, 11906) => "A4",
                (12240, 15840) or (15840, 12240) => "Letter",
                (11907, 18720) or (18720, 11907) => "F4",
                _ => $"{pg.W / 567.0:0.#}×{pg.H / 567.0:0.#} cm"
            };
            int words = _doc.AllParas().Sum(p => p.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            _statusLbl.Text = $"{size} {(pg.Landscape || pg.W > pg.H ? "lanskap" : "potret")} • {words:N0} kata • Zoom {(int)Math.Round(_zoom * 100)}% • Ctrl + roda mouse untuk zoom";
        }

        private void SetZoom(float z)
        {
            z = Math.Clamp(z, 0.5f, 2.5f);
            if (Math.Abs(z - _zoom) < 0.001f) return;
            var focus = _current?.Para; int caret = _current?.SelectionStart ?? 0;
            _zoom = z;
            RebuildDocument(focus, caret);
        }

        // ================================================================ undo / redo
        private List<EBlock> CloneBody() => _doc.Body.Select(b => b.DeepCopy()).ToList();

        /// <summary>Simpan titik urungkan sebelum perubahan.</summary>
        internal void Snapshot()
        {
            _undo.Add(CloneBody());
            if (_undo.Count > 60) _undo.RemoveAt(0);
        }

        /// <summary>Titik urungkan untuk mengetik: satu per jeda mengetik, bukan per huruf.</summary>
        internal void TypingSnapshot()
        {
            if (!_typingSnapshotTaken) { Snapshot(); _typingSnapshotTaken = true; }
            _snapTimer.Stop(); _snapTimer.Start();
        }

        private void Undo()
        {
            if (_undo.Count == 0) { Theme.Toast(this, "Tidak ada yang bisa diurungkan"); return; }
            _redo.Add(CloneBody());
            Restore(_undo[^1]); _undo.RemoveAt(_undo.Count - 1);
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(CloneBody());
            Restore(_redo[^1]); _redo.RemoveAt(_redo.Count - 1);
        }

        private void Restore(List<EBlock> body)
        {
            _doc.Body.Clear(); _doc.Body.AddRange(body);
            _typingSnapshotTaken = false;
            _dirty = true; UpdateTitle();
            RebuildDocument();
        }

        // ================================================================ fokus & toolbar sinkron
        internal void OnBoxFocused(ParaBox b)
        {
            _current = b;
            SyncToolbar();
        }

        internal void SyncToolbar()
        {
            var b = _current; if (b == null) return;
            var p = b.Para;
            var lvl = _doc.Styles.HeadingLevel(p.Pf.Style);
            var name = _doc.Styles.Name(p.Pf.Style).ToLowerInvariant();
            int idx = name switch { "title" => 1, "subtitle" => 2, "quote" => 6, _ => lvl switch { 1 => 3, 2 => 4, 3 => 5, _ => 0 } };
            if (_cmbStyle.SelectedIndex != idx) _cmbStyle.SelectedIndex = idx;
            var f = b.FormatAtCaret();
            _cmbFont.Text = f.Font ?? "Calibri";
            _cmbSize.Text = ((f.Sz ?? 22) / 2.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ================================================================ perintah format
        private void ApplyChar(Func<CharFmt, CharFmt> change)
        {
            var b = _current; if (b == null) return;
            Snapshot();
            b.ApplyCharFormat(change);
            MarkDirty();
        }

        private void ToggleChar(Func<CharFmt, bool> isOn, Func<CharFmt, bool, CharFmt> set)
        {
            var b = _current; if (b == null) return;
            bool on = isOn(b.EffectiveAtSelection());
            ApplyChar(c => set(c, !on));
        }

        private void ApplyPara(Func<ParaFmt, ParaFmt> change)
        {
            var b = _current; if (b == null) return;
            Snapshot();
            b.Para.Pf = change(b.Para.Pf);
            b.RenderAll();
            RefreshLabels();
            MarkDirty();
        }

        private void ApplyStyle(StdStyle s)
        {
            var id = _doc.StyleId(s);
            ApplyPara(p => p with { Style = id });
            SyncToolbar();
        }

        private void ToggleList(bool bullet)
        {
            var b = _current; if (b == null) return;
            var eff = _doc.EffPara(b.Para.Pf);
            bool isThis = eff.NumId is > 0 && _doc.IsBulletList(eff.NumId) == bullet;
            if (isThis) ApplyPara(p => p with { NumId = _doc.Styles.ParaBase(p.Style).NumId is > 0 ? 0 : null, Ilvl = null, Left = null, FirstLine = null });
            else { int id = _doc.NumIdFor(bullet); ApplyPara(p => p with { NumId = id, Ilvl = 0, Left = null, FirstLine = null }); }
        }

        private void Indent(int dir)
        {
            var b = _current; if (b == null) return;
            var eff = _doc.EffPara(b.Para.Pf);
            if (eff.NumId is > 0) ApplyPara(p => p with { Ilvl = Math.Clamp((eff.Ilvl ?? 0) + dir, 0, 8), Left = null, FirstLine = null });
            else ApplyPara(p => p with { Left = Math.Max(0, (eff.Left ?? 0) + dir * 720) });
        }

        private void LineSpacingMenu()
        {
            var m = new ContextMenuStrip();
            foreach (var (label, v) in new[] { ("1,0", 240), ("1,15", 276), ("1,5", 360), ("2,0", 480), ("2,5", 600), ("3,0", 720) })
                m.Items.Add(label, null, (s, e) => ApplyPara(p => p with { Line = v, LineRule = "auto" }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Tambah spasi sesudah paragraf", null, (s, e) => ApplyPara(p => p with { After = 200 }));
            m.Items.Add("Hapus spasi sesudah paragraf", null, (s, e) => ApplyPara(p => p with { After = 0 }));
            m.Show(Cursor.Position);
        }

        private void PickColor()
        {
            if (_current == null) return;
            var m = new ContextMenuStrip();
            m.Items.Add("Otomatis (hitam)", null, (s, e) => ApplyChar(c => c with { Color = CharFmt.AUTO }));
            foreach (var (n, hex) in new[] { ("Merah", 0xC00000), ("Biru", 0x1F4E79), ("Biru terang", 0x2F5496), ("Hijau", 0x38761D), ("Oranye", 0xC55A11), ("Ungu", 0x7030A0), ("Abu-abu", 0x595959) })
            {
                var item = new ToolStripMenuItem(n) { Image = Swatch(Color.FromArgb(hex | unchecked((int)0xFF000000))) };
                int v = hex;
                item.Click += (s, e) => ApplyChar(c => c with { Color = unchecked((int)0xFF000000) | v });
                m.Items.Add(item);
            }
            m.Items.Add("Warna lain…", null, (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true };
                if (cd.ShowDialog(this) == DialogResult.OK) ApplyChar(c => c with { Color = cd.Color.ToArgb() | unchecked((int)0xFF000000) });
            });
            m.Show(Cursor.Position);
        }

        private void PickHighlight()
        {
            if (_current == null) return;
            var m = new ContextMenuStrip();
            m.Items.Add("Tanpa stabilo", null, (s, e) => ApplyChar(c => c with { Bg = CharFmt.NONE }));
            foreach (var (n, key) in new[] { ("Kuning", "yellow"), ("Hijau", "green"), ("Biru muda", "cyan"), ("Merah muda", "magenta"), ("Abu-abu", "lightGray") })
            {
                int v = Fmt.HIGHLIGHT.First(h => h.Key == key).Value;
                var item = new ToolStripMenuItem(n) { Image = Swatch(Color.FromArgb(v)) };
                item.Click += (s, e) => ApplyChar(c => c with { Bg = v });
                m.Items.Add(item);
            }
            m.Show(Cursor.Position);
        }

        // ================================================================ menu Lainnya
        private void MoreMenu()
        {
            var m = new ContextMenuStrip { Font = Theme.FontBody(10f) };
            m.Items.Add("Hapus format karakter", null, (s, e) => ApplyChar(_ => CharFmt.EMPTY));
            m.Items.Add("Superskrip (x²)", null, (s, e) => ToggleChar(c => c.Va == 1, (c, v) => c with { Va = v ? 1 : 0 }));
            m.Items.Add("Subskrip (H₂O)", null, (s, e) => ToggleChar(c => c.Va == 2, (c, v) => c with { Va = v ? 2 : 0 }));
            m.Items.Add("HURUF BESAR SEMUA", null, (s, e) => ChangeCase(true));
            m.Items.Add("huruf kecil semua", null, (s, e) => ChangeCase(false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Cari & Ganti…", null, (s, e) => ShowFindReplace(true));
            m.Items.Add("🔊 Bacakan dokumen", null, (s, e) => VoiceReaderManager.ShowVoiceReaderDialog(this, _displayName, string.Join("\n", _doc.AllParas().Select(p => p.Text))));
            m.Items.Add(new ToolStripSeparator());
            bool locked = _protectPw != null || (!_removeProtection && _savePath != null && DocxProtect.Of(_savePath) != null);
            if (locked) m.Items.Add("🔓 Buka kunci dokumen (bebas diedit)", null, (s, e) => { _protectPw = null; _removeProtection = true; MarkDirty(); Theme.Toast(this, "Kunci dilepas saat dokumen disimpan"); });
            else m.Items.Add("🔒 Kunci dokumen (hanya-baca + password)…", null, (s, e) => LockDocument());
            m.Show(Cursor.Position);
        }

        private void ChangeCase(bool upper)
        {
            var b = _current; if (b == null || b.SelectionLength == 0) { Theme.Toast(this, "Pilih teks terlebih dahulu"); return; }
            Snapshot();
            int s = b.SelectionStart, l = b.SelectionLength;
            var t = b.Para.Text;
            var seg = upper ? t.Substring(s, l).ToUpper(new System.Globalization.CultureInfo("id-ID")) : t.Substring(s, l).ToLower(new System.Globalization.CultureInfo("id-ID"));
            if (seg.Length != l) return;
            b.Para.Text = t[..s] + seg + t[(s + l)..];
            b.RenderAll(); b.Select(s, l);
            MarkDirty();
        }

        private void LockDocument()
        {
            using var dlg = new Form { Text = "Kunci Dokumen", ClientSize = new Size(Theme.S(380), Theme.S(210)), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.SURFACE };
            var info = new Label { Text = "Dokumen hanya bisa dibaca di Word / Depup Office. Untuk mengedit perlu password ini.", Location = new Point(Theme.S(16), Theme.S(12)), Size = new Size(Theme.S(350), Theme.S(40)), Font = Theme.FontBody(9.5f), ForeColor = Theme.MUTED };
            var p1 = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "Password", Location = new Point(Theme.S(16), Theme.S(60)), Width = Theme.S(348) };
            var p2 = new TextBox { UseSystemPasswordChar = true, PlaceholderText = "Ulangi password", Location = new Point(Theme.S(16), Theme.S(98)), Width = Theme.S(348) };
            var ok = Theme.CreateButton("Kunci", true, Theme.ACCENT, Theme.S(36), Theme.S(120)); ok.Location = new Point(Theme.S(244), Theme.S(150));
            ok.Click += (s, e) =>
            {
                if (p1.Text.Length < 4) { Theme.Toast(dlg, "Password minimal 4 karakter"); return; }
                if (p1.Text != p2.Text) { Theme.Toast(dlg, "Password tidak sama"); return; }
                dlg.DialogResult = DialogResult.OK;
            };
            dlg.Controls.AddRange(new Control[] { info, p1, p2, ok }); dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _protectPw = p1.Text; _removeProtection = false;
            MarkDirty();
            Theme.Toast(this, "Dokumen dikunci saat disimpan");
        }

        // ================================================================ Cari & Ganti
        private Form? _findForm;

        private void ShowFindReplace(bool replace)
        {
            if (_findForm is { IsDisposed: false }) { _findForm.Activate(); return; }
            var f = new Form { Text = "Cari & Ganti", ClientSize = new Size(Theme.S(420), Theme.S(196)), FormBorderStyle = FormBorderStyle.FixedToolWindow, StartPosition = FormStartPosition.Manual, Owner = this, ShowInTaskbar = false, BackColor = Theme.SURFACE };
            f.Location = new Point(Right - f.Width - Theme.S(40), Top + Theme.S(140));
            var q = new TextBox { PlaceholderText = "Cari…", Location = new Point(Theme.S(14), Theme.S(14)), Width = Theme.S(392), Font = Theme.FontBody(10.5f) };
            var r = new TextBox { PlaceholderText = "Ganti dengan…", Location = new Point(Theme.S(14), Theme.S(52)), Width = Theme.S(392), Font = Theme.FontBody(10.5f) };
            var cs = new CheckBox { Text = "Huruf besar/kecil sama persis", Location = new Point(Theme.S(14), Theme.S(90)), AutoSize = true, Font = Theme.FontBody(9.5f) };
            var info = new Label { Location = new Point(Theme.S(240), Theme.S(92)), AutoSize = true, ForeColor = Theme.MUTED, Font = Theme.FontBody(9f) };
            Button B(string t, int x, bool primary) { var b = Theme.CreateButton(t, primary, Theme.ACCENT, Theme.S(34), Theme.S(124)); b.Location = new Point(Theme.S(x), Theme.S(138)); if (!primary) b.BackColor = Theme.SOFT; return b; }
            var next = B("Berikutnya", 14, true); var rep = B("Ganti", 148, false); var all = B("Ganti Semua", 282, false);
            StringComparison Cmp() => cs.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            next.Click += (s, e) => info.Text = FindNext(q.Text, Cmp()) ? "" : "Tidak ditemukan";
            rep.Click += (s, e) =>
            {
                var b = _current;
                if (b != null && q.Text.Length > 0 && b.SelectionLength == q.Text.Length && string.Equals(b.SelectedText, q.Text, Cmp())) { b.SelectedText = r.Text; }
                info.Text = FindNext(q.Text, Cmp()) ? "" : "Tidak ada lagi";
            };
            all.Click += (s, e) => info.Text = $"{ReplaceAll(q.Text, r.Text, Cmp())} diganti";
            q.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; next.PerformClick(); } };
            f.Controls.AddRange(new Control[] { q, r, cs, info, next, rep, all });
            f.AcceptButton = next;
            if (_current?.SelectionLength > 0 && !_current.SelectedText.Contains('\n')) q.Text = _current.SelectedText;
            _findForm = f;
            f.Show(this);
            (replace ? r : q).Focus();
        }

        private bool FindNext(string q, StringComparison cmp)
        {
            if (q.Length == 0) return false;
            var boxes = AllBoxes().ToList();
            if (boxes.Count == 0) return false;
            int start = _current != null ? Math.Max(0, boxes.IndexOf(_current)) : 0;
            int from = _current != null ? _current.SelectionStart + Math.Max(1, _current.SelectionLength) : 0;
            for (int k = 0; k <= boxes.Count; k++)
            {
                var b = boxes[(start + k) % boxes.Count];
                int pos = k == 0 ? (from <= b.TextLength ? b.Text.IndexOf(q, from, cmp) : -1) : b.Text.IndexOf(q, cmp);
                if (k == boxes.Count && pos >= from) pos = -1;
                if (pos < 0) continue;
                b.Focus(); b.Select(pos, q.Length);
                _scroll.ScrollControlIntoView(b);
                return true;
            }
            return false;
        }

        private int ReplaceAll(string q, string rep, StringComparison cmp)
        {
            if (q.Length == 0) return 0;
            int count = 0;
            var paras = _doc.AllParas().Where(p => p.Text.IndexOf(q, cmp) >= 0).ToList();
            if (paras.Count == 0) return 0;
            Snapshot();
            foreach (var p in paras)
            {
                var sb = new System.Text.StringBuilder(); var fm = new List<CharFmt>();
                int i = 0;
                while (i < p.Text.Length)
                {
                    int at = p.Text.IndexOf(q, i, cmp);
                    if (at < 0) { sb.Append(p.Text, i, p.Text.Length - i); fm.AddRange(p.Fmts.Skip(i).Take(p.Text.Length - i)); break; }
                    sb.Append(p.Text, i, at - i); fm.AddRange(p.Fmts.Skip(i).Take(at - i));
                    var f = at < p.Fmts.Length ? p.Fmts[at] : CharFmt.EMPTY;
                    sb.Append(rep); fm.AddRange(Enumerable.Repeat(f, rep.Length));
                    i = at + q.Length; count++;
                }
                p.Text = sb.ToString(); p.Fmts = fm.ToArray();
            }
            MarkDirty();
            RebuildDocument(_current?.Para, _current?.SelectionStart ?? 0);
            return count;
        }

        private static Bitmap Swatch(Color c)
        {
            var b = new Bitmap(16, 16);
            using var g = Graphics.FromImage(b);
            g.Clear(c); g.DrawRectangle(Pens.Gray, 0, 0, 15, 15);
            return b;
        }

        // ================================================================ sisip blok
        /// <summary>Indeks blok tingkat-body tempat kursor berada (paragraf, atau tabel yang memuat sel aktif).</summary>
        private int CurrentBodyIndex()
        {
            var b = _current; if (b == null) return _doc.Body.Count - 1;
            if (b.Table != null) return _doc.Body.IndexOf(b.Table);
            return _doc.Body.IndexOf(b.Para);
        }

        private EPara NewNormalPara()
        {
            int tpl = _current?.Para.Origin >= 0 ? _current.Para.Origin : _current?.Para.Tpl ?? -1;
            return EPara.Blank(tpl, new ParaFmt());
        }

        private void InsertTable()
        {
            using var dlg = new Form { Text = "Sisipkan Tabel", ClientSize = new Size(Theme.S(300), Theme.S(150)), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.SURFACE };
            var lr = new Label { Text = "Baris", Location = new Point(Theme.S(20), Theme.S(22)), AutoSize = true, Font = Theme.FontBody(10f) };
            var nr = new NumericUpDown { Minimum = 1, Maximum = 200, Value = 3, Location = new Point(Theme.S(110), Theme.S(18)), Width = Theme.S(80) };
            var lc = new Label { Text = "Kolom", Location = new Point(Theme.S(20), Theme.S(58)), AutoSize = true, Font = Theme.FontBody(10f) };
            var nc = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 3, Location = new Point(Theme.S(110), Theme.S(54)), Width = Theme.S(80) };
            var ok = Theme.CreateButton("Sisipkan", true, Theme.ACCENT, Theme.S(36), Theme.S(120)); ok.Location = new Point(Theme.S(160), Theme.S(100)); ok.DialogResult = DialogResult.OK;
            dlg.Controls.AddRange(new Control[] { lr, nr, lc, nc, ok }); dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int rows = (int)nr.Value, cols = (int)nc.Value;
            int tpl = _current?.Para.Origin >= 0 ? _current.Para.Origin : -1;
            int colW = Math.Max(400, Page.TextWidth / cols);
            var t = new ETable(-1, Enumerable.Range(0, rows).Select(_ => new ERow(-1, Enumerable.Range(0, cols).Select(_ => new ECell(-1, new List<EPara> { EPara.Blank(tpl, new ParaFmt(After: 0)) })).ToList())).ToList(),
                Enumerable.Repeat(colW, cols).ToList(), true);
            Snapshot();
            int at = CurrentBodyIndex() + 1;
            _doc.Body.Insert(at, t);
            if (at + 1 >= _doc.Body.Count || _doc.Body[at + 1] is not EPara) _doc.Body.Insert(at + 1, NewNormalPara());
            MarkDirty();
            RebuildDocument(t.Rows[0].Cells[0].Paras[0]);
        }

        private void InsertImage()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih Gambar", Filter = "Gambar (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var bytes = File.ReadAllBytes(ofd.FileName);
                using var ms = new MemoryStream(bytes);
                using var img = Image.FromStream(ms);
                var ext = Path.GetExtension(ofd.FileName).TrimStart('.').ToLowerInvariant() == "png" ? "png" : "jpeg";
                Snapshot();
                int at = CurrentBodyIndex() + 1;
                _doc.Body.Insert(at, new EImage(bytes, ext, img.Width, img.Height));
                MarkDirty();
                RebuildDocument(_current?.Para);
            }
            catch (Exception ex) { MessageBox.Show(this, "Gambar tidak dapat dibaca: " + ex.Message, "Editor Word", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void InsertSignature()
        {
            using var sig = Ui.PickStamp(this, "Sisipkan Tanda Tangan / Stempel");
            if (sig == null) return;
            using var ms = new MemoryStream();
            sig.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            Snapshot();
            int at = CurrentBodyIndex() + 1;
            // TTD kira-kira 4,5 cm lebarnya: pecahan dari lebar teks halaman
            float frac = Math.Clamp(2551f / Math.Max(1, Page.TextWidth), 0.12f, 0.5f);
            _doc.Body.Insert(at, new EImage(ms.ToArray(), "png", sig.Width, sig.Height, frac));
            MarkDirty();
            RebuildDocument(_current?.Para);
        }

        private void InsertPageBreak()
        {
            Snapshot();
            int at = CurrentBodyIndex() + 1;
            _doc.Body.Insert(at, new EBreak());
            var np = NewNormalPara();
            _doc.Body.Insert(at + 1, np);
            MarkDirty();
            RebuildDocument(np);
        }

        private void PageSetupDialog()
        {
            var pg = Page;
            using var dlg = new Form { Text = "Pengaturan Halaman", ClientSize = new Size(Theme.S(380), Theme.S(250)), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.SURFACE };
            Label L(string t, int y) => new() { Text = t, Location = new Point(Theme.S(20), Theme.S(y + 4)), AutoSize = true, Font = Theme.FontBody(10f) };
            var size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(Theme.S(150), Theme.S(20)), Width = Theme.S(200) };
            size.Items.AddRange(new object[] { "A4 (21 × 29,7 cm)", "Letter (21,6 × 27,9 cm)", "F4 / Folio (21,5 × 33 cm)", "Legal (21,6 × 35,6 cm)" });
            int sw = Math.Min(pg.W, pg.H), sh = Math.Max(pg.W, pg.H);
            size.SelectedIndex = (sw, sh) switch { (12240, 15840) => 1, (11907, 18720) => 2, (12240, 20160) => 3, _ => 0 };
            var orient = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(Theme.S(150), Theme.S(60)), Width = Theme.S(200) };
            orient.Items.AddRange(new object[] { "Potret", "Lanskap" }); orient.SelectedIndex = pg.W > pg.H ? 1 : 0;
            var margin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(Theme.S(150), Theme.S(100)), Width = Theme.S(200) };
            margin.Items.AddRange(new object[] { "Biarkan seperti sekarang", "Normal (2,54 cm)", "Sempit (1,27 cm)", "Sedang (1,9 cm)", "Lebar (3,8 cm)" }); margin.SelectedIndex = 0;
            var ok = Theme.CreateButton("Terapkan", true, Theme.ACCENT, Theme.S(36), Theme.S(120)); ok.Location = new Point(Theme.S(230), Theme.S(190)); ok.DialogResult = DialogResult.OK;
            dlg.Controls.AddRange(new Control[] { L("Ukuran kertas", 20), size, L("Orientasi", 60), orient, L("Margin", 100), margin, ok });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            (int w, int h) = size.SelectedIndex switch { 1 => (12240, 15840), 2 => (11907, 18720), 3 => (12240, 20160), _ => (11906, 16838) };
            bool land = orient.SelectedIndex == 1;
            if (land) (w, h) = (h, w);
            int? m = margin.SelectedIndex switch { 1 => 1440, 2 => 720, 3 => 1080, 4 => 2160, _ => null };
            Snapshot();
            _doc.Page = new PageSetup(w, h, land, m ?? pg.Top, m ?? pg.Right, m ?? pg.Bottom, m ?? pg.Left);
            MarkDirty();
            RebuildDocument(_current?.Para, _current?.SelectionStart ?? 0);
        }

        // ================================================================ struktur paragraf (dipanggil ParaBox)
        internal void SplitParagraph(ParaBox box)
        {
            var p = box.Para;
            var list = box.OwnerList;
            int pos = box.SelectionStart;
            if (box.SelectionLength > 0) { box.SelectedText = ""; pos = box.SelectionStart; }
            Snapshot();
            var eff = _doc.EffPara(p.Pf);
            // Enter pada butir daftar kosong = keluar dari daftar (seperti Word)
            if (p.Text.Length == 0 && eff.NumId is > 0)
            {
                p.Pf = p.Pf with { NumId = _doc.Styles.ParaBase(p.Pf.Style).NumId is > 0 ? 0 : null, Ilvl = null, Left = null, FirstLine = null };
                box.RenderAll(); RefreshLabels(); MarkDirty();
                return;
            }
            var right = p.Text[pos..];
            var rightF = p.Fmts.Skip(pos).ToArray();
            var carry = pos > 0 && p.Fmts.Length >= pos ? p.Fmts[pos - 1] : p.Fmts.FirstOrDefault() ?? CharFmt.EMPTY;
            p.Text = p.Text[..pos];
            p.Fmts = p.Fmts.Take(pos).ToArray();
            // judul → paragraf berikutnya kembali Normal (bila Enter di akhir judul)
            var pf = p.Pf;
            if (right.Length == 0 && _doc.Styles.HeadingLevel(pf.Style) > 0) pf = pf with { Style = null };
            var np = new EPara(-1, right, rightF, pf with { }, p.Origin >= 0 ? p.Origin : p.Tpl);
            if (right.Length == 0) np.Fmts = Array.Empty<CharFmt>();
            int idx = list.IndexOf(p);
            list.Insert(idx + 1, np);
            box.RenderAll();
            var host = (ParaHost)box.Parent!;
            var newHost = host.CloneFor(np);
            newHost.Box.TypingFormat = right.Length == 0 && carry != CharFmt.EMPTY ? carry : null;
            var flow = (FlowLayoutPanel)host.Parent!;
            flow.Controls.Add(newHost);
            flow.Controls.SetChildIndex(newHost, flow.Controls.GetChildIndex(host) + 1);
            RefreshLabels();
            MarkDirty();
            newHost.Box.Focus(); newHost.Box.Select(0, 0);
        }

        internal void MergeWithPrevious(ParaBox box)
        {
            var list = box.OwnerList;
            int idx = list.IndexOf(box.Para);
            if (idx <= 0) { if (box.Para.Pf.NumId is > 0) ToggleListOff(box); return; }
            if (list[idx - 1] is not EPara prev) return;   // di atasnya tabel/gambar: tidak digabung
            if (box.Para.Locked) return;
            // Backspace di awal butir daftar: lepas daftar dulu
            if (_doc.EffPara(box.Para.Pf).NumId is > 0 && box.Para.Pf.NumId != 0) { ToggleListOff(box); return; }
            Snapshot();
            int caret = prev.Text.Length;
            prev.Text += box.Para.Text;
            prev.Fmts = prev.Fmts.Concat(box.Para.Fmts).ToArray();
            prev.Images.AddRange(box.Para.Images);
            list.RemoveAt(idx);
            var host = (ParaHost)box.Parent!;
            var flow = (FlowLayoutPanel)host.Parent!;
            var prevHost = flow.Controls.OfType<ParaHost>().First(h => ReferenceEquals(h.Box.Para, prev));
            flow.Controls.Remove(host); host.Dispose();
            prevHost.Rebuild();
            RefreshLabels();
            MarkDirty();
            prevHost.Box.Focus(); prevHost.Box.Select(caret, 0);
        }

        private void ToggleListOff(ParaBox box)
        {
            Snapshot();
            box.Para.Pf = box.Para.Pf with { NumId = _doc.Styles.ParaBase(box.Para.Pf.Style).NumId is > 0 ? 0 : null, Ilvl = null, Left = null, FirstLine = null };
            box.RenderAll(); RefreshLabels(); MarkDirty();
        }

        internal void MergeNext(ParaBox box)
        {
            var list = box.OwnerList;
            int idx = list.IndexOf(box.Para);
            if (idx < 0 || idx + 1 >= list.Count) return;
            if (list[idx + 1] is not EPara next || next.Locked) return;
            Snapshot();
            int caret = box.Para.Text.Length;
            box.Para.Text += next.Text;
            box.Para.Fmts = box.Para.Fmts.Concat(next.Fmts).ToArray();
            box.Para.Images.AddRange(next.Images);
            list.RemoveAt(idx + 1);
            var host = (ParaHost)box.Parent!;
            var flow = (FlowLayoutPanel)host.Parent!;
            var nextHost = flow.Controls.OfType<ParaHost>().FirstOrDefault(h => ReferenceEquals(h.Box.Para, next));
            if (nextHost != null) { flow.Controls.Remove(nextHost); nextHost.Dispose(); }
            host.Rebuild();
            RefreshLabels(); MarkDirty();
            host.Box.Focus(); host.Box.Select(caret, 0);
        }

        /// <summary>Tempel teks polos; baris baru menjadi paragraf baru.</summary>
        internal void PasteText(ParaBox box)
        {
            string text;
            try { text = Clipboard.GetText(TextDataFormat.UnicodeText); } catch { return; }
            if (string.IsNullOrEmpty(text)) return;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = text.Split('\n');
            Snapshot();
            box.SelectedText = lines[0];
            if (lines.Length == 1) return;
            var cur = box;
            for (int i = 1; i < lines.Length; i++)
            {
                SplitParagraph(cur);
                cur = _current ?? cur;
                cur.SelectedText = lines[i];
            }
        }

        internal void MoveFocus(ParaBox from, int dir)
        {
            var all = AllBoxes().ToList();
            int i = all.IndexOf(from);
            int j = i + dir;
            if (j < 0 || j >= all.Count) return;
            var to = all[j];
            to.Focus();
            to.Select(dir > 0 ? 0 : to.TextLength, 0);
            to.Parent?.Parent?.Focus();
            to.Focus();
            _scroll.ScrollControlIntoView(to);
        }

        // ================================================================ tabel (dipanggil TableHost)
        internal void TableOp(ETable t, ERow row, ECell cell, string op)
        {
            int ri = t.Rows.IndexOf(row), ci = row.Cells.IndexOf(cell);
            Snapshot();
            switch (op)
            {
                case "rowBelow":
                case "rowAbove":
                    {
                        var nr = new ERow(-1, row.Cells.Select(c => new ECell(-1, new List<EPara> { EPara.Blank(c.Paras.FirstOrDefault()?.Origin ?? c.Paras.FirstOrDefault()?.Tpl ?? -1, c.Paras.FirstOrDefault()?.Pf ?? new ParaFmt()) }, c.Span, false, null, c.Origin >= 0 ? c.Origin : c.TplCell)).ToList(), row.Origin >= 0 ? row.Origin : row.TplRow);
                        t.Rows.Insert(op == "rowBelow" ? ri + 1 : ri, nr);
                        break;
                    }
                case "rowDel":
                    if (t.Rows.Count <= 1) { _doc.Body.Remove(t); break; }
                    t.Rows.RemoveAt(ri);
                    break;
                case "colRight":
                    {
                        int w = t.Grid[ci];
                        int half = Math.Max(400, w / 2);
                        t.Grid[ci] = Math.Max(400, w - half); t.Grid.Insert(ci + 1, half);
                        foreach (var r in t.Rows)
                        {
                            var src = r.Cells[Math.Min(ci, r.Cells.Count - 1)];
                            r.Cells.Insert(Math.Min(ci + 1, r.Cells.Count), new ECell(-1, new List<EPara> { EPara.Blank(src.Paras.FirstOrDefault()?.Origin ?? -1, src.Paras.FirstOrDefault()?.Pf ?? new ParaFmt()) }, 1, false, null, src.Origin >= 0 ? src.Origin : src.TplCell));
                        }
                        break;
                    }
                case "colDel":
                    if (t.Grid.Count <= 1) { _doc.Body.Remove(t); break; }
                    {
                        int w = t.Grid[ci]; t.Grid.RemoveAt(ci);
                        t.Grid[Math.Min(ci, t.Grid.Count - 1)] += w;
                        foreach (var r in t.Rows) if (ci < r.Cells.Count) r.Cells.RemoveAt(ci);
                    }
                    break;
                case "tableDel":
                    _doc.Body.Remove(t);
                    break;
            }
            MarkDirty();
            var focus = _doc.Body.Contains(t) && t.Rows.Count > 0 ? t.Rows[Math.Clamp(ri, 0, t.Rows.Count - 1)].Cells.FirstOrDefault()?.Paras.FirstOrDefault() : null;
            RebuildDocument(focus);
        }

        // ================================================================ simpan / pratinjau
        private bool Save()
        {
            if (_savePath == null) return SaveAs();
            return SaveTo(_savePath);
        }

        private bool SaveAs()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Simpan Dokumen Word (.docx)",
                InitialDirectory = _savePath != null ? Path.GetDirectoryName(_savePath) : AppStorage.OutDir,
                FileName = _savePath != null ? Path.GetFileName(_savePath) : _displayName,
                Filter = "Dokumen Word (*.docx)|*.docx"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return false;
            return SaveTo(sfd.FileName);
        }

        private bool SaveTo(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                var focus = _current?.Para; int caret = _current?.SelectionStart ?? 0;
                int focusIdx = focus != null ? _doc.AllParas().IndexOf(focus) : -1;
                _doc.Save(path);
                if (_protectPw != null) DocxProtect.Apply(path, _protectPw);
                else if (_removeProtection) DocxProtect.Remove(path);
                _protectPw = null; _removeProtection = false;
                // muat ulang dari berkas tersimpan agar penyimpanan berikutnya berbasis isi terbaru
                var re = DocxDocument.Open(path) ?? throw new IOException("Berkas tersimpan tidak dapat dibuka kembali.");
                _doc = re; _savePath = path; _displayName = Path.GetFileName(path);
                _dirty = false; _undo.Clear(); _redo.Clear(); _typingSnapshotTaken = false;
                AppStorage.AddRecent(path);
                AppStorage.SetLastJob("Dokumen Word Disimpan", path);
                UpdateTitle();
                var paras = _doc.AllParas();
                RebuildDocument(focusIdx >= 0 && focusIdx < paras.Count ? paras[focusIdx] : null, caret);
                Theme.Toast(this, "Tersimpan: " + Path.GetFileName(path));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Gagal menyimpan dokumen: " + ex.Message, "Editor Word", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally { Cursor = Cursors.Default; }
        }

        private void PreviewDoc()
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "DepupOffice", "word_preview");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(_displayName) + "_pratinjau.docx");
                _doc.Save(tmp);
                using var v = new ViewerForm(tmp);
                v.ShowDialog(this);
            }
            catch (Exception ex) { MessageBox.Show(this, "Pratinjau gagal: " + ex.Message, "Editor Word", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty)
            {
                var r = MessageBox.Show(this, $"Simpan perubahan pada \"{_displayName}\"?", "Editor Word", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !Save())) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.V: case Keys.Shift | Keys.Insert: if (ActiveControlDeep() is ParaBox pb) { PasteText(pb); return true; } break;
                case Keys.Control | Keys.D1: case Keys.Control | Keys.D2: case Keys.Control | Keys.D5: case Keys.Control | Keys.Shift | Keys.L: return true;
                case Keys.Control | Keys.F: ShowFindReplace(false); return true;
                case Keys.Control | Keys.H: ShowFindReplace(true); return true;
                case Keys.Control | Keys.S: Save(); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.Z: Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.Control | Keys.B: ToggleChar(c => c.B == true, (c, v) => c with { B = v }); return true;
                case Keys.Control | Keys.I: ToggleChar(c => c.I == true, (c, v) => c with { I = v }); return true;
                case Keys.Control | Keys.U: ToggleChar(c => c.U == true, (c, v) => c with { U = v }); return true;
                case Keys.Control | Keys.L: ApplyPara(p => p with { Align = 0 }); return true;
                case Keys.Control | Keys.E: ApplyPara(p => p with { Align = 1 }); return true;
                case Keys.Control | Keys.R: ApplyPara(p => p with { Align = 2 }); return true;
                case Keys.Control | Keys.J: ApplyPara(p => p with { Align = 3 }); return true;
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: SetZoom(_zoom + 0.1f); return true;
                case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: SetZoom(_zoom - 0.1f); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private Control? ActiveControlDeep() { Control? c = ActiveControl; while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl; return c; }

        /// <summary>Ctrl + roda mouse di mana pun di editor = zoom.</summary>
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg != WM_MOUSEWHEEL || !ContainsFocus || (ModifierKeys & Keys.Control) == 0) return false;
            int delta = (short)((long)m.WParam >> 16);
            SetZoom(_zoom + (delta > 0 ? 0.1f : -0.1f));
            return true;
        }

        // ================================================================ akses untuk kontrol anak
        internal DocxDocument Doc => _doc;
        internal bool LoadingUi => _loadingUi;
        internal float Zoom => _zoom;
    }

    // =================================================================================================
    // Paragraf: wadah (label daftar + kotak teks + gambar)
    // =================================================================================================
    internal sealed class ParaHost : Panel
    {
        private readonly DocEditorForm _ed;
        public readonly ParaBox Box;
        private readonly Label _label;
        private readonly FlowLayoutPanel _imgs;
        private readonly int _width;
        private readonly System.Collections.IList _container;
        private readonly ETable? _table; private readonly ECell? _cell;

        public ParaHost(DocEditorForm ed, EPara p, System.Collections.IList container, ETable? table, ECell? cell, int width)
        {
            _ed = ed; _width = width; _container = container; _table = table; _cell = cell;
            Width = width; Margin = Padding.Empty; Padding = Padding.Empty; BackColor = Color.Transparent;
            Box = new ParaBox(ed, p, container, table, cell) { Width = width, Location = Point.Empty };
            _label = new Label { AutoSize = true, BackColor = Color.Transparent, Visible = false, UseMnemonic = false };
            _imgs = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(width, 0), BackColor = Color.Transparent, Margin = Padding.Empty };
            Controls.Add(Box); Controls.Add(_label); Controls.Add(_imgs);
            Box.SizeChanged += (s, e) => Relayout();
            _imgs.SizeChanged += (s, e) => Relayout();
            BuildImages();
            UpdateLabel();
            Box.RenderAll();
            Relayout();
        }

        public ParaHost CloneFor(EPara p) => new(_ed, p, _container, _table, _cell, _width);

        public void Rebuild() { BuildImages(); Box.RenderAll(); UpdateLabel(); Relayout(); }

        private void BuildImages()
        {
            foreach (Control c in _imgs.Controls.Cast<Control>().ToList()) { _imgs.Controls.Remove(c); c.Dispose(); }
            var p = Box.Para;
            foreach (var im in p.Images)
            {
                var bytes = _ed.Doc.ImageBytes(im.RId);
                Image? img = null;
                if (bytes != null) { try { using var ms = new MemoryStream(bytes); using var raw = Image.FromStream(ms); img = new Bitmap(raw); } catch { } }
                int w = im.Cx > 0 ? (int)(im.Cx / 12700.0 * 96 / 72 * _ed.PxScale) : _ed.PxScale > 0 ? (int)(120 * _ed.PxScale) : 120;
                int h = im.Cy > 0 ? (int)(im.Cy / 12700.0 * 96 / 72 * _ed.PxScale) : w;
                if (w > _width) { h = (int)(h * (double)_width / w); w = _width; }
                var pb = new PictureBox { Size = new Size(Math.Max(8, w), Math.Max(8, h)), SizeMode = PictureBoxSizeMode.Zoom, Image = img, BackColor = img == null ? Theme.PAPER_SOFT : Color.Transparent, Margin = new Padding(0, 2, 6, 2), Cursor = Cursors.Hand };
                var m = new ContextMenuStrip();
                var imRef = im;
                m.Items.Add("Hapus gambar", null, (s, e) => { _ed.Snapshot(); Box.Para.Images.Remove(imRef); _ed.MarkDirty(); BuildImages(); Relayout(); });
                pb.ContextMenuStrip = m;
                _imgs.Controls.Add(pb);
            }
            if (p.HasObject)
                _imgs.Controls.Add(new Label { Text = "▣ kotak teks / bentuk dipertahankan", AutoSize = true, ForeColor = Theme.PAPER_MUTED, Font = Theme.FontBody(8f), Margin = new Padding(0, 2, 0, 2) });
            _imgs.Visible = _imgs.Controls.Count > 0;
        }

        public void UpdateLabel()
        {
            var lbl = _ed.LabelFor(Box.Para);
            _label.Visible = !string.IsNullOrEmpty(lbl);
            if (_label.Visible)
            {
                var eff = _ed.Doc.EffPara(Box.Para.Pf);
                var bf = _ed.Doc.RunBase(Box.Para.Pf).Over(Box.Para.Fmts.FirstOrDefault());
                _label.Text = lbl;
                _label.Font = ParaBox.MakeFont(bf, _ed.Zoom);
                _label.ForeColor = ParaBox.ColorOf(bf.Color, Color.Black);
                int first = (eff.Left ?? 0) + (eff.FirstLine ?? 0);
                _label.Left = Math.Max(0, _ed.Px(first));
                _label.Top = _ed.Px(eff.Before ?? 0) + 1;
            }
            Box.HasLabel = _label.Visible;
            Box.ApplyParaFormat();
        }

        private void Relayout()
        {
            int y = Box.Height;
            if (Box.Para.PageBreak && Box.Para.Origin >= 0) { }
            _imgs.Location = new Point(_ed.Px(_ed.Doc.EffPara(Box.Para.Pf).Left ?? 0), y);
            Height = y + (_imgs.Visible ? _imgs.Height : 0);
            _label.BringToFront();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Box.Para.PageBreak)
            {
                using var pen = new Pen(Theme.BLUE_LINE) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
            }
        }
    }

    // =================================================================================================
    // Kotak teks satu paragraf (RichTextBox) yang terikat ke EPara
    // =================================================================================================
    internal sealed class ParaBox : RichTextBox
    {
        private readonly DocEditorForm _ed;
        public readonly EPara Para;
        public readonly System.Collections.IList OwnerList;
        public readonly ETable? Table; public readonly ECell? Cell;
        private bool _suppress;
        /// <summary>Format untuk teks yang akan diketik (tombol B/I/warna ditekan tanpa seleksi).</summary>
        public CharFmt? TypingFormat;
        public bool HasLabel;

        public ParaBox(DocEditorForm ed, EPara para, System.Collections.IList container, ETable? table, ECell? cell)
        {
            _ed = ed; Para = para; OwnerList = container; Table = table; Cell = cell;
            BorderStyle = BorderStyle.None; ScrollBars = RichTextBoxScrollBars.None; WordWrap = true; Multiline = true;
            DetectUrls = false; ShortcutsEnabled = true; AcceptsTab = true; BackColor = Color.White;
            Margin = Padding.Empty;
            Height = 20;
            ContentsResized += (s, e) => { int h = Math.Max(e.NewRectangle.Height + 2, (int)(Font.Height * 1.1)); if (Height != h) Height = h; };
            ContextMenuStrip = BuildMenu();
        }

        private ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Potong", null, (s, e) => { _ed.TypingSnapshot(); Cut(); });
            m.Items.Add("Salin", null, (s, e) => Copy());
            m.Items.Add("Tempel", null, (s, e) => _ed.PasteText(this));
            if (Table != null && Cell != null)
            {
                var row = Table.Rows.First(r => r.Cells.Contains(Cell));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Sisipkan baris di atas", null, (s, e) => _ed.TableOp(Table, row, Cell, "rowAbove"));
                m.Items.Add("Sisipkan baris di bawah", null, (s, e) => _ed.TableOp(Table, row, Cell, "rowBelow"));
                if (Table.Simple)
                {
                    m.Items.Add("Sisipkan kolom di kanan", null, (s, e) => _ed.TableOp(Table, row, Cell, "colRight"));
                    m.Items.Add("Hapus kolom", null, (s, e) => _ed.TableOp(Table, row, Cell, "colDel"));
                }
                m.Items.Add("Hapus baris", null, (s, e) => _ed.TableOp(Table, row, Cell, "rowDel"));
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Hapus tabel", null, (s, e) => _ed.TableOp(Table, row, Cell, "tableDel"));
            }
            return m;
        }

        // ---------------------------------------------------------------- tampilan dari model
        internal static Font MakeFont(CharFmt f, float zoom)
        {
            var style = FontStyle.Regular;
            if (f.B == true) style |= FontStyle.Bold;
            if (f.I == true) style |= FontStyle.Italic;
            if (f.U == true) style |= FontStyle.Underline;
            if (f.S == true) style |= FontStyle.Strikeout;
            // Ukuran titik dirender RichTextBox pada DPI layar; cukup dikalikan zoom editor
            float pt = (f.Sz ?? 22) / 2f * (f.Va is 1 or 2 ? 0.65f : 1f) * zoom;
            try { return new Font(string.IsNullOrWhiteSpace(f.Font) ? "Calibri" : f.Font!, Math.Max(1f, pt), style, GraphicsUnit.Point); }
            catch { return new Font("Calibri", Math.Max(1f, pt), style, GraphicsUnit.Point); }
        }

        internal static Color ColorOf(int? argb, Color auto) => argb is null or CharFmt.AUTO ? auto : Color.FromArgb(argb.Value | unchecked((int)0xFF000000));

        private CharFmt Base => _ed.Doc.RunBase(Para.Pf);

        public void RenderAll()
        {
            _suppress = true;
            int ss = SelectionStart, sl = SelectionLength;
            try
            {
                Text = Para.Text;
                var b = Base;
                int k = 0, n = Para.Text.Length;
                if (n == 0)
                {
                    var f0 = b.Over(TypingFormat ?? Para.Fmts.FirstOrDefault());
                    Font = MakeFont(f0, _ed.Zoom);
                    SelectAll(); SelectionFont = Font; SelectionColor = ColorOf(f0.Color, Color.Black);
                }
                while (k < n)
                {
                    int j = k;
                    var fk = k < Para.Fmts.Length ? Para.Fmts[k] : CharFmt.EMPTY;
                    while (j < n && (j < Para.Fmts.Length ? Para.Fmts[j] : CharFmt.EMPTY) == fk) j++;
                    ApplyRun(k, j - k, b.Over(fk));
                    k = j;
                }
                ApplyParaFormat();
                Select(Math.Min(ss, TextLength), Math.Min(sl, Math.Max(0, TextLength - ss)));
            }
            finally { _suppress = false; }
        }

        private void ApplyRun(int start, int len, CharFmt f)
        {
            Select(start, len);
            SelectionFont = MakeFont(f, _ed.Zoom);
            SelectionColor = ColorOf(f.Color, Color.Black);
            SelectionBackColor = f.Bg is null or CharFmt.NONE ? BackColor : Color.FromArgb(f.Bg.Value | unchecked((int)0xFF000000));
            SelectionCharOffset = f.Va switch { 1 => (int)(4 * _ed.PxScale), 2 => (int)(-3 * _ed.PxScale), _ => 0 };
        }

        // PARAFORMAT2 (perataan rata kiri-kanan, indentasi, spasi) — tidak semua tersedia lewat properti RichTextBox
        [StructLayout(LayoutKind.Sequential)]
        private struct PARAFORMAT2
        {
            public int cbSize; public uint dwMask; public short wNumbering; public short wReserved; public int dxStartIndent; public int dxRightIndent; public int dxOffset;
            public short wAlignment; public short cTabCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public int[] rgxTabs;
            public int dySpaceBefore; public int dySpaceAfter; public int dyLineSpacing; public short sStyle; public byte bLineSpacingRule; public byte bOutlineLevel;
            public short wShadingWeight; public short wShadingStyle; public short wNumberingStart; public short wNumberingStyle; public short wNumberingTab; public short wBorderSpace; public short wBorderWidth; public short wBorders;
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref PARAFORMAT2 lParam);

        public void ApplyParaFormat()
        {
            if (!IsHandleCreated) { HandleCreated -= OnHandleCreatedApply; HandleCreated += OnHandleCreatedApply; return; }
            var e = _ed.Doc.EffPara(Para.Pf);
            float z = _ed.Zoom;   // twip RichEdit sudah mengikuti DPI layar; kalikan zoom saja
            int T(int tw) => (int)Math.Round(tw * z);
            int left = e.Left ?? 0, first = e.FirstLine ?? 0;
            var pf = new PARAFORMAT2
            {
                cbSize = Marshal.SizeOf<PARAFORMAT2>(), rgxTabs = new int[32],
                dwMask = 0x1 | 0x2 | 0x4 | 0x8 | 0x40 | 0x80 | 0x100,
                dxStartIndent = T(HasLabel ? left : Math.Max(0, left + first)),
                dxOffset = HasLabel ? 0 : T(-first),
                dxRightIndent = T(e.Right ?? 0),
                wAlignment = (short)(e.Align switch { 1 => 3, 2 => 2, 3 => 4, _ => 1 }),
                dySpaceBefore = T(e.Before ?? 0),
                dySpaceAfter = T(e.After ?? 0),
            };
            int line = e.Line ?? 240;
            if (e.LineRule == "exact") { pf.bLineSpacingRule = 4; pf.dyLineSpacing = T(line); }
            else if (e.LineRule == "atLeast") { pf.bLineSpacingRule = 3; pf.dyLineSpacing = T(line); }
            else { pf.bLineSpacingRule = 5; pf.dyLineSpacing = (int)Math.Round(line / 240.0 * 20); }
            bool sup = _suppress; _suppress = true;
            int ss = SelectionStart, sl = SelectionLength;
            SelectAll();
            SendMessage(Handle, 0x447, IntPtr.Zero, ref pf);
            Select(ss, sl);
            _suppress = sup;
        }

        private void OnHandleCreatedApply(object? s, EventArgs e) { HandleCreated -= OnHandleCreatedApply; RenderAll(); }

        // ---------------------------------------------------------------- model dari ketikan
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            if (_suppress || _ed.LoadingUi) return;
            var now = Text.Replace("\r", "");
            var old = Para.Text;
            if (now == old) return;
            _ed.TypingSnapshot();
            var d = TextDiff.Align(old, now);
            var nf = new CharFmt[now.Length];
            for (int k = 0; k < old.Length; k++) { int j = d.OldToNew[k]; if (j >= 0 && j < nf.Length) nf[j] = k < Para.Fmts.Length ? Para.Fmts[k] : CharFmt.EMPTY; }
            var typed = new List<(int, int)>();
            foreach (var ins in d.Inserts)
            {
                CharFmt inherit = TypingFormat ?? (ins.At > 0 && ins.At - 1 < Para.Fmts.Length ? Para.Fmts[ins.At - 1] : Para.Fmts.Length > 0 ? Para.Fmts[0] : CharFmt.EMPTY);
                for (int j = ins.From; j < ins.To && j < nf.Length; j++) nf[j] = inherit;
                if (TypingFormat != null) typed.Add((ins.From, ins.To - ins.From));
            }
            for (int j = 0; j < nf.Length; j++) nf[j] ??= CharFmt.EMPTY;
            Para.Text = now; Para.Fmts = nf;
            if (typed.Count > 0)
            {
                _suppress = true;
                int ss = SelectionStart;
                foreach (var (s, l) in typed) ApplyRun(s, l, Base.Over(TypingFormat));
                Select(ss, 0);
                _suppress = false;
            }
            _ed.MarkDirty();
        }

        // ---------------------------------------------------------------- format seleksi
        public CharFmt FormatAtCaret()
        {
            int i = SelectionLength > 0 ? SelectionStart : SelectionStart - 1;
            var f = TypingFormat ?? (i >= 0 && i < Para.Fmts.Length ? Para.Fmts[i] : Para.Fmts.FirstOrDefault() ?? CharFmt.EMPTY);
            return Base.Over(f);
        }

        public CharFmt EffectiveAtSelection() => FormatAtCaret();

        public void ApplyCharFormat(Func<CharFmt, CharFmt> change)
        {
            int s = SelectionStart, l = SelectionLength;
            if (l == 0)
            {
                // tanpa seleksi: berlaku untuk ketikan berikutnya; bila paragraf kosong, juga tanda paragraf
                var cur = TypingFormat ?? (s > 0 && s - 1 < Para.Fmts.Length ? Para.Fmts[s - 1] : Para.Fmts.FirstOrDefault() ?? CharFmt.EMPTY);
                TypingFormat = change(cur);
                if (Para.Text.Length == 0) RenderAll();
                return;
            }
            var nf = (CharFmt[])Para.Fmts.Clone();
            if (nf.Length < Para.Text.Length) nf = nf.Concat(Enumerable.Repeat(CharFmt.EMPTY, Para.Text.Length - nf.Length)).ToArray();
            for (int k = s; k < s + l && k < nf.Length; k++) nf[k] = change(nf[k]);
            Para.Fmts = nf;
            RenderAll();
            Select(s, l);
            (Parent as ParaHost)?.UpdateLabel();
        }

        // ---------------------------------------------------------------- keyboard
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _ed.OnBoxFocused(this); }

        protected override void OnMouseDown(MouseEventArgs e) { TypingFormat = null; base.OnMouseDown(e); }

        protected override void OnSelectionChanged(EventArgs e)
        {
            base.OnSelectionChanged(e);
            if (!_suppress && Focused) _ed.SyncToolbar();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift && !e.Control)
            {
                e.SuppressKeyPress = true; e.Handled = true;
                if (Cell != null && Cell.Locked) return;
                _ed.SplitParagraph(this);
                return;
            }
            if (e.KeyCode == Keys.Enter && e.Shift) { e.SuppressKeyPress = true; SelectedText = "\n"; return; }
            if (e.KeyCode == Keys.Back && SelectionLength == 0 && SelectionStart == 0 && !(Cell?.Locked ?? false)) { e.SuppressKeyPress = true; _ed.MergeWithPrevious(this); return; }
            if (e.KeyCode == Keys.Delete && SelectionLength == 0 && SelectionStart == TextLength && !(Cell?.Locked ?? false)) { e.SuppressKeyPress = true; _ed.MergeNext(this); return; }
            if (e.KeyCode == Keys.Up && !e.Shift && GetLineFromCharIndex(SelectionStart) == 0) { e.Handled = true; _ed.MoveFocus(this, -1); return; }
            if (e.KeyCode == Keys.Down && !e.Shift && GetLineFromCharIndex(SelectionStart) == GetLineFromCharIndex(TextLength)) { e.Handled = true; _ed.MoveFocus(this, 1); return; }
            if (e.KeyCode == Keys.Left && !e.Shift && SelectionStart == 0 && SelectionLength == 0) { e.Handled = true; _ed.MoveFocus(this, -1); return; }
            if (e.KeyCode == Keys.Right && !e.Shift && SelectionStart == TextLength && SelectionLength == 0) { e.Handled = true; _ed.MoveFocus(this, 1); return; }
            if (e.Control && e.KeyCode == Keys.V) { e.SuppressKeyPress = true; _ed.PasteText(this); return; }
            if (e.Shift && e.KeyCode == Keys.Insert) { e.SuppressKeyPress = true; _ed.PasteText(this); return; }
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End) TypingFormat = null;
            base.OnKeyDown(e);
        }
    }

    // =================================================================================================
    // Tabel: sel berisi paragraf yang bisa diedit, garis tepi digambar sendiri
    // =================================================================================================
    internal sealed class TableHost : Panel
    {
        private readonly DocEditorForm _ed;
        private readonly ETable _t;
        private readonly List<(ERow Row, ECell Cell, FlowLayoutPanel Panel, int Col, int Span)> _cells = new();
        private bool _layoutPending;
        private int[] _colX = Array.Empty<int>();
        private const int PAD = 5;

        public TableHost(DocEditorForm ed, ETable t)
        {
            _ed = ed; _t = t;
            DoubleBuffered = true; BackColor = Color.White;
            Margin = new Padding(0, 2, 0, 4);
            Width = ed.TextWidthPx;
            Build();
            DoLayout();
        }

        public IEnumerable<ParaBox> Boxes()
        {
            foreach (var c in _cells) foreach (Control k in c.Panel.Controls) if (k is ParaHost ph) yield return ph.Box;
        }

        private void Build()
        {
            var grid = _t.Grid.Count > 0 ? _t.Grid : Enumerable.Repeat(2000, Math.Max(1, _t.Rows.Max(r => r.Cells.Sum(c => c.Span)))).ToList();
            int total = Math.Max(1, grid.Sum());
            double k = Math.Min(1.0, _ed.TextWidthPx / (double)_ed.Px(total));
            _colX = new int[grid.Count + 1];
            for (int i = 0; i < grid.Count; i++) _colX[i + 1] = _colX[i] + (int)(_ed.Px(grid[i]) * k);
            Width = Math.Max(_colX[^1] + 1, 40);
            foreach (var row in _t.Rows)
            {
                int col = 0;
                foreach (var cell in row.Cells)
                {
                    int span = Math.Max(1, cell.Span);
                    int x0 = _colX[Math.Min(col, _colX.Length - 1)], x1 = _colX[Math.Min(col + span, _colX.Length - 1)];
                    int w = Math.Max(20, x1 - x0 - PAD * 2);
                    var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
                    if (!cell.VCont)
                    {
                        if (cell.Locked)
                            panel.Controls.Add(new Label
                            {
                                Text = "⊞ berisi tabel di dalam sel — teks bisa diedit, susunan tabel dalamnya tetap utuh saat disimpan",
                                AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = Theme.PAPER_MUTED, Font = Theme.FontBody(8f), Margin = new Padding(0, 0, 0, 4)
                            });
                        if (cell.Paras.Count == 0) cell.Paras.Add(EPara.Blank(-1, new ParaFmt()));
                        foreach (var p in cell.Paras)
                        {
                            var host = new ParaHost(_ed, p, cell.Paras, _t, cell, w);
                            if (cell.Fill is { } fill) { host.Box.BackColor = ParaBox.ColorOf(fill, Color.White); host.Box.RenderAll(); }
                            panel.Controls.Add(host);
                        }
                    }
                    panel.SizeChanged += (s, e) => RequestLayout();
                    Controls.Add(panel);
                    _cells.Add((row, cell, panel, col, span));
                    col += span;
                }
            }
        }

        private void RequestLayout()
        {
            if (_layoutPending || !IsHandleCreated) { if (!IsHandleCreated) DoLayout(); return; }
            _layoutPending = true;
            BeginInvoke(new Action(() => { _layoutPending = false; DoLayout(); }));
        }

        private readonly List<int> _rowY = new();

        private void DoLayout()
        {
            _rowY.Clear();
            int y = 0;
            foreach (var row in _t.Rows)
            {
                _rowY.Add(y);
                int h = 22;
                foreach (var c in _cells.Where(x => ReferenceEquals(x.Row, row) && !x.Cell.VCont)) h = Math.Max(h, c.Panel.Height + PAD * 2);
                foreach (var c in _cells.Where(x => ReferenceEquals(x.Row, row)))
                    c.Panel.Location = new Point(_colX[Math.Min(c.Col, _colX.Length - 1)] + PAD, y + PAD);
                y += h;
            }
            _rowY.Add(y);
            Height = y + 1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            using var pen = new Pen(_t.Bordered ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 205, 215)) { DashStyle = _t.Bordered ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dot };
            for (int ri = 0; ri < _t.Rows.Count && ri + 1 < _rowY.Count; ri++)
            {
                var row = _t.Rows[ri];
                foreach (var c in _cells.Where(x => ReferenceEquals(x.Row, row)))
                {
                    int x0 = _colX[Math.Min(c.Col, _colX.Length - 1)], x1 = _colX[Math.Min(c.Col + c.Span, _colX.Length - 1)];
                    int y0 = _rowY[ri], y1 = _rowY[ri + 1];
                    if (c.Cell.Fill is { } f) { using var b = new SolidBrush(ParaBox.ColorOf(f, Color.White)); g.FillRectangle(b, x0, y0, x1 - x0, y1 - y0); }
                    // sel gabungan vertikal: tanpa garis atas agar tampak menyatu dengan sel di atasnya
                    if (!c.Cell.VCont) g.DrawLine(pen, x0, y0, x1, y0);
                    g.DrawLine(pen, x0, y0, x0, y1);
                    g.DrawLine(pen, x1, y0, x1, y1);
                    bool nextCont = ri + 1 < _t.Rows.Count && _cells.Any(x => ReferenceEquals(x.Row, _t.Rows[ri + 1]) && x.Col == c.Col && x.Cell.VCont);
                    if (!nextCont) g.DrawLine(pen, x0, y1, x1, y1);
                }
            }
        }
    }
}
