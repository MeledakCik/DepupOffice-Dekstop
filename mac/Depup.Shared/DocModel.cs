namespace DepupOfficeDesktop.Core
{
    public sealed class DocSection
    {
        public string Text { get; set; } = "";
        public bool IsHeading { get; set; }
        public bool IsNote { get; set; }
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        /// <summary>0 = kiri, 1 = tengah, 2 = kanan, 3 = rata kiri-kanan</summary>
        public int Align { get; set; }

        public DocSection Clone() => (DocSection)MemberwiseClone();
    }

    /// <summary>Satu blok teks siap tata letak (dipakai untuk render PDF & cetak).</summary>
    public sealed record DocBlock(string Text, bool Bold, bool Italic, int Align, float SizeFactor, bool Muted, float SpaceAfter);

    /// <summary>
    /// Model dokumen lintas platform (port dari DocEditorForm / DocEditorActivity.kt):
    /// judul, subjudul, daftar bagian, pengaturan kertas; ekspor .docx dan daftar blok untuk PDF.
    /// </summary>
    public sealed class DocModel
    {
        public const string TemplateSurat = "surat";
        public const string TemplateCv = "cv";
        public const string TemplateBlank = "blank";

        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public List<DocSection> Sections { get; } = new();

        public string PageSize { get; set; } = "A4";   // A4, Letter, F4
        public int MarginMode { get; set; }            // 0 Normal 2.5cm, 1 Sempit 1.2cm, 2 Lebar 3.8cm
        public float LineSpacing { get; set; } = 1.15f;
        public int FontFamily { get; set; }            // 0 Sans, 1 Serif
        public float FontSize { get; set; } = 12f;

        public static readonly string[] PageSizes = { "A4", "Letter", "F4" };

        public static DocModel FromSettings()
        {
            var c = AppStorage.Config;
            return new DocModel
            {
                PageSize = PageSizes.Contains(c.DocPageSize) ? c.DocPageSize : "A4",
                MarginMode = Math.Clamp(c.DocMarginMode, 0, 2),
                LineSpacing = c.DocLineSpacing is >= 1f and <= 3f ? c.DocLineSpacing : 1.15f,
                FontFamily = Math.Clamp(c.DocFontFamily, 0, 1),
                FontSize = c.DocFontSize is >= 8f and <= 24f ? c.DocFontSize : 12f,
            };
        }

        public void SaveSettings()
        {
            var c = AppStorage.Config;
            c.DocPageSize = PageSize;
            c.DocMarginMode = MarginMode;
            c.DocLineSpacing = LineSpacing;
            c.DocFontFamily = FontFamily;
            c.DocFontSize = FontSize;
            AppStorage.SaveConfig();
        }

        /// <summary>Ukuran kertas dalam point (1/72 inci).</summary>
        public (float W, float H) PagePoints() => PageSize switch
        {
            "Letter" => (612f, 792f),
            "F4" => (609.4f, 935.4f),   // 215 x 330 mm
            _ => (595.3f, 841.9f),      // A4 210 x 297 mm
        };

        public float MarginPoints() => MarginMode switch
        {
            1 => 34f,     // 1.2 cm
            2 => 107.7f,  // 3.8 cm
            _ => 70.9f,   // 2.5 cm
        };

        public static DocModel FromTemplate(string? key)
        {
            var m = FromSettings();
            m.ApplyTemplate(key);
            return m;
        }

        public static DocModel FromDocx(string path)
        {
            var m = FromSettings();
            var paragraphs = OfficeReader.ReadDocx(path);
            int start = 0;
            // Pola dokumen Depup: judul tebal di tengah, lalu subjudul miring.
            if (paragraphs.Count > 1 && paragraphs[0].Bold && paragraphs[0].Align == 1 && paragraphs[0].Text.Length <= 120)
            {
                m.Title = paragraphs[0].Text;
                start = 1;
                if (paragraphs.Count > 2 && paragraphs[1].Italic && !paragraphs[1].Bold && paragraphs[1].Text.Length <= 160)
                {
                    m.Subtitle = paragraphs[1].Text;
                    start = 2;
                }
            }
            for (int i = start; i < paragraphs.Count; i++)
            {
                var p = paragraphs[i];
                m.Sections.Add(new DocSection { Text = p.Text, IsBold = p.Bold, IsItalic = p.Italic, Align = p.Align });
            }
            if (m.Sections.Count == 0) m.Sections.Add(new DocSection());
            return m;
        }

        public static DocModel FromPlainText(string title, string text)
        {
            var m = FromSettings();
            m.Title = title;
            foreach (var para in text.Replace("\r\n", "\n").Split("\n\n"))
                if (!string.IsNullOrWhiteSpace(para)) m.Sections.Add(new DocSection { Text = para.Trim('\n') });
            if (m.Sections.Count == 0) m.Sections.Add(new DocSection());
            return m;
        }

        public void ApplyTemplate(string? key)
        {
            Sections.Clear();
            switch (key)
            {
                case TemplateSurat:
                    Title = "Surat Lamaran Kerja";
                    Subtitle = $"Nomor: 001/LAMARAN/{DateTime.Now.Year}";
                    Sections.Add(new DocSection { Text = "Kepada Yth.\nHRD Manager\nPT Nama Perusahaan\nDi Tempat", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "Dengan hormat,\n\nBerdasarkan informasi lowongan pekerjaan yang saya peroleh, saya bermaksud mengajukan lamaran kerja untuk posisi yang tersedia di perusahaan Bapak/Ibu." });
                    Sections.Add(new DocSection { Text = "Data Diri\nNama: (Nama Lengkap)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nPendidikan Terakhir: (Jenjang / Jurusan)\nAlamat: (Alamat Lengkap)\nNo. HP: (Nomor Aktif)\nEmail: (Alamat Email)" });
                    Sections.Add(new DocSection { Text = "Sebagai bahan pertimbangan, saya lampirkan Curriculum Vitae, fotokopi ijazah, transkrip nilai, dan dokumen pendukung lainnya. Besar harapan saya untuk dapat diberikan kesempatan wawancara." });
                    Sections.Add(new DocSection { Text = "Demikian surat lamaran ini saya buat dengan sebenar-benarnya. Atas perhatian dan kesempatan yang diberikan, saya ucapkan terima kasih.", IsNote = true });
                    Sections.Add(new DocSection { Text = "Hormat saya,\n\n\n(Nama Lengkap)", Align = 2 });
                    break;
                case TemplateCv:
                    Title = "Curriculum Vitae";
                    Subtitle = "Data Pribadi & Riwayat Profesional";
                    Sections.Add(new DocSection { Text = "DATA PRIBADI", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "Nama Lengkap: (Nama)\nTempat/Tanggal Lahir: (Kota, Tanggal)\nJenis Kelamin: (L/P)\nAlamat: (Alamat)\nNo. HP: (Nomor)\nEmail: (Email)" });
                    Sections.Add(new DocSection { Text = "RINGKASAN PROFIL", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "Profesional yang berorientasi pada hasil dengan pengalaman di bidang teknologi & administrasi. Terbiasa bekerja secara mandiri maupun dalam tim, teliti, dan mampu beradaptasi dengan cepat." });
                    Sections.Add(new DocSection { Text = "PENGALAMAN KERJA", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "(Tahun – Sekarang) — (Nama Perusahaan)\nPosisi: (Jabatan)\nTanggung jawab: (Uraikan pekerjaan dan pencapaian utama)" });
                    Sections.Add(new DocSection { Text = "PENDIDIKAN", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "(Tahun – Tahun) — (Nama Institusi)\nJenjang / Jurusan: (S1 / Teknik Informatika)\nIPK: (Nilai)" });
                    Sections.Add(new DocSection { Text = "KEAHLIAN", IsHeading = true, IsBold = true });
                    Sections.Add(new DocSection { Text = "• Microsoft Office & Administrasi Dokumen\n• Analisis Data & Pelaporan\n• Komunikasi & Manajemen Proyek" });
                    break;
                default:
                    Title = "";
                    Subtitle = "";
                    Sections.Add(new DocSection());
                    break;
            }
        }

        public string FullText()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title.Trim());
            if (!string.IsNullOrWhiteSpace(Subtitle)) parts.Add(Subtitle.Trim());
            parts.AddRange(Sections.Select(s => s.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            return string.Join("\n\n", parts);
        }

        /// <summary>Blok berurutan sesuai tampilan .docx (judul kapital tengah, subjudul catatan, lalu bagian).</summary>
        public List<DocBlock> Blocks()
        {
            var list = new List<DocBlock>();
            if (!string.IsNullOrWhiteSpace(Title))
                list.Add(new DocBlock(Title.Trim().ToUpperInvariant(), true, false, 1, 1.35f, false, 4f));
            if (!string.IsNullOrWhiteSpace(Subtitle))
                list.Add(new DocBlock(Subtitle.Trim(), false, true, 1, 0.9f, true, 10f));
            if (list.Count > 0) list.Add(new DocBlock(" ", false, false, 0, 1f, false, 0f));
            foreach (var s in Sections)
            {
                if (s.IsNote) list.Add(new DocBlock(s.Text, false, true, s.Align, 0.92f, true, 8f));
                else if (s.IsHeading) list.Add(new DocBlock(s.Text, true, s.IsItalic, s.Align, 1.1f, false, 6f));
                else list.Add(new DocBlock(s.Text, s.IsBold, s.IsItalic, s.Align, 1f, false, 8f));
            }
            return list;
        }

        public void SaveDocx(string path)
        {
            using var w = new DocxWriter(path, pageSize: PageSize, marginMode: MarginMode, lineSpacing: LineSpacing, fontFamily: FontFamily);
            w.BeginPage();
            if (!string.IsNullOrWhiteSpace(Title)) w.Paragraph(Title.Trim().ToUpperInvariant(), bold: true, align: 1);
            if (!string.IsNullOrWhiteSpace(Subtitle)) w.Note(Subtitle.Trim());
            if (!string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Subtitle)) w.Paragraph(" ");
            foreach (var s in Sections)
            {
                if (s.IsNote) w.Note(s.Text);
                else w.Paragraph(s.Text, bold: s.IsBold || s.IsHeading, italic: s.IsItalic, align: s.Align);
            }
            w.Finish();
        }
    }
}
