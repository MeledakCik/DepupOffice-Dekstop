using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>Encoder barcode 1D tanpa NuGet (pengganti ZXing di Android): Code 128 (B/C) dan EAN-13.</summary>
    public static class Barcode1D
    {
        public enum Format { Code128, Ean13, Qr }
        private static readonly string[] C128 = {"11011001100", "11001101100", "11001100110", "10010011000", "10010001100", "10001001100", "10011001000", "10011000100", "10001100100", "11001001000", "11001000100", "11000100100", "10110011100", "10011011100", "10011001110", "10111001100", "10011101100", "10011100110", "11001110010", "11001011100", "11001001110", "11011100100", "11001110100", "11101101110", "11101001100", "11100101100", "11100100110", "11101100100", "11100110100", "11100110010", "11011011000", "11011000110", "11000110110", "10100011000", "10001011000", "10001000110", "10110001000", "10001101000", "10001100010", "11010001000", "11000101000", "11000100010", "10110111000", "10110001110", "10001101110", "10111011000", "10111000110", "10001110110", "11101110110", "11010001110", "11000101110", "11011101000", "11011100010", "11011101110", "11101011000", "11101000110", "11100010110", "11101101000", "11101100010", "11100011010", "11101111010", "11001000010", "11110001010", "10100110000", "10100001100", "10010110000", "10010000110", "10000101100", "10000100110", "10110010000", "10110000100", "10011010000", "10011000010", "10000110100", "10000110010", "11000010010", "11001010000", "11110111010", "11000010100", "10001111010", "10100111100", "10010111100", "10010011110", "10111100100", "10011110100", "10011110010", "11110100100", "11110010100", "11110010010", "11011011110", "11011110110", "11110110110", "10101111000", "10100011110", "10001011110", "10111101000", "10111100010", "11110101000", "11110100010", "10111011110", "10111101110", "11101011110", "11110101110", "11010000100", "11010010000", "11010011100"};
        private const string C128_STOP = "1100011101011";

        public static string Code128Pattern(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Kode barcode tidak boleh kosong");
            var codes = new List<int>();
            bool numeric = text.Length >= 4 && text.Length % 2 == 0 && text.All(char.IsDigit);
            if (numeric)
            {
                codes.Add(105);
                for (int i = 0; i < text.Length; i += 2) codes.Add(int.Parse(text.AsSpan(i, 2), CultureInfo.InvariantCulture));
            }
            else
            {
                codes.Add(104);
                foreach (var ch in text)
                {
                    if (ch < 32 || ch > 126) throw new ArgumentException("Code 128 hanya mendukung karakter ASCII (tanpa huruf beraksen)");
                    codes.Add(ch - 32);
                }
            }
            int sum = codes[0];
            for (int i = 1; i < codes.Count; i++) sum += codes[i] * i;
            codes.Add(sum % 103);
            var sb = new StringBuilder();
            foreach (var c in codes) sb.Append(C128[c]);
            return sb.Append(C128_STOP).ToString();
        }

        private static readonly string[] EA = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
        private static readonly string[] EB = { "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111" };
        private static readonly string[] EC = { "1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100" };
        private static readonly string[] EPar = { "AAAAAA", "AABABB", "AABBAB", "AABBBA", "ABAABB", "ABBAAB", "ABBBAA", "ABABAB", "ABABBA", "ABBABA" };

        /// <summary>Lengkapi 12 digit dengan digit cek; 13 digit diperiksa.</summary>
        public static string Ean13Digits(string text)
        {
            var d = new string((text ?? "").Where(char.IsDigit).ToArray());
            if (d.Length != 12 && d.Length != 13) throw new ArgumentException("EAN-13 butuh 12 atau 13 digit angka");
            int s = 0;
            for (int i = 0; i < 12; i++) s += (d[i] - '0') * (i % 2 == 0 ? 1 : 3);
            char chk = (char)('0' + (10 - s % 10) % 10);
            if (d.Length == 13 && d[12] != chk) throw new ArgumentException($"Digit cek EAN-13 salah (seharusnya {chk})");
            return d[..12] + chk;
        }

        public static string Ean13Pattern(string text)
        {
            var d = Ean13Digits(text);
            var sb = new StringBuilder("101");
            var par = EPar[d[0] - '0'];
            for (int i = 1; i <= 6; i++) sb.Append(par[i - 1] == 'A' ? EA[d[i] - '0'] : EB[d[i] - '0']);
            sb.Append("01010");
            for (int i = 7; i <= 12; i++) sb.Append(EC[d[i] - '0']);
            return sb.Append("101").ToString();
        }

        public static string Pattern(string text, Format f) => f == Format.Ean13 ? Ean13Pattern(text) : Code128Pattern(text);

        /// <summary>Gambar barcode (latar putih, zona tenang 10 modul) atau QR.</summary>
        public static Bitmap Render(string text, Format f, int w = 400, int h = 160)
        {
            if (f == Format.Qr) return QrManager.GenerateQr(text, Math.Max(w, h), Math.Max(w, h), 2);
            var pat = Pattern(text, f);
            int quiet = 10, modules = pat.Length + quiet * 2;
            int mw = Math.Max(1, w / modules);
            var bmp = new Bitmap(modules * mw, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            for (int i = 0; i < pat.Length; i++) if (pat[i] == '1') g.FillRectangle(Brushes.Black, (quiet + i) * mw, 0, mw, h);
            return bmp;
        }

        /// <summary>Barcode vektor di PDF (tajam saat dicetak).</summary>
        public static void Draw(PdfCanvas c, string text, Format f, RectangleF r)
        {
            if (f == Format.Qr)
            {
                using var q = QrManager.GenerateQr(text, 600, 600, 1);
                float s = Math.Min(r.Width, r.Height);
                c.Image(q, new RectangleF(r.Left + (r.Width - s) / 2, r.Top, s, s), 255, 95);
                return;
            }
            var pat = Pattern(text, f);
            float mw = r.Width / pat.Length;
            int i = 0;
            while (i < pat.Length)
            {
                if (pat[i] != '1') { i++; continue; }
                int j = i; while (j < pat.Length && pat[j] == '1') j++;
                c.FillRect(new RectangleF(r.Left + i * mw, r.Top, (j - i) * mw, r.Height), Color.Black);
                i = j;
            }
        }
    }

    public sealed record InvoiceItem(string Name, int Qty, long Price) { public long Total => Qty * Price; }
    public sealed record CatalogItem(string Title, string PriceOrSub, string Description, string? ImagePath);
    public sealed record ScheduleTask(string TimeOrDay, string TaskName, string Note);
    public sealed record ChartSeries(string Label, float Value, Color Color);

    /// <summary>Generator dokumen bisnis (port Invoice/Catalog/SchedulePlanner/BarcodePriceTag/IdPhoto/ExcelChart Manager).</summary>
    public static class BizGenerators
    {
        public static readonly Color Blue = ColorTranslator.FromHtml("#1652F6");
        public static readonly Color Ink = ColorTranslator.FromHtml("#111111");
        public static readonly Color Muted = ColorTranslator.FromHtml("#65625D");
        public static readonly Color LineC = ColorTranslator.FromHtml("#E4E2DD");
        public static readonly Color HeadBg = ColorTranslator.FromHtml("#E8EEFF");
        public static readonly Color CardBg = ColorTranslator.FromHtml("#F3F2EE");
        public static readonly Color[] ChartColors = { Blue, ColorTranslator.FromHtml("#35754C"), ColorTranslator.FromHtml("#F5A524"), ColorTranslator.FromHtml("#C0262D"), ColorTranslator.FromHtml("#72539E"), ColorTranslator.FromHtml("#0284C7") };

        public static string Rupiah(long v) => "Rp " + v.ToString("#,0", CultureInfo.GetCultureInfo("id-ID"));

        private static string Clip(string s, float maxW, float size, bool bold)
        {
            if (PdfCanvas.Measure(s, size, bold) <= maxW) return s;
            while (s.Length > 1 && PdfCanvas.Measure(s + "…", size, bold) > maxW) s = s[..^1];
            return s + "…";
        }

        // ------------------------------------------------------------------ nota / kuitansi

        public static void InvoicePdf(string outPath, string shop, string invNo, string date, string customer, IReadOnlyList<InvoiceItem> items, Bitmap? stamp)
        {
            var c = new PdfCanvas();
            float pw = c.PageWidth, ph = c.PageHeight, y = 40;
            void Header()
            {
                c.FillRect(new RectangleF(40, y, pw - 80, 28), HeadBg);
                c.Text("Nama Barang / Jasa", 50, y + 18, 11, Blue, true);
                c.Text("Qty", 320, y + 18, 11, Blue, true);
                c.Text("Harga Satuan", 370, y + 18, 11, Blue, true);
                c.Text("Total", pw - 50, y + 18, 11, Blue, true, PdfAlign.Right);
                y += 46;
            }
            c.Text(shop, 40, y, 22, Blue, true); y += 24;
            c.Text("KUITANSI / NOTA PEMBAYARAN", 40, y, 12, Muted);
            c.Line(40, y + 12, pw - 40, y + 12, LineC, 1.5f); y += 32;
            c.Text("Nomor: " + invNo, 40, y, 12, Ink);
            c.Text("Tanggal: " + date, pw - 40, y, 12, Ink, false, PdfAlign.Right); y += 20;
            c.Text("Kepada: " + customer, 40, y, 12, Ink); y += 26;
            Header();
            long grand = 0;
            foreach (var it in items)
            {
                if (y > ph - 140) { c.NewPage(); y = 50; Header(); }
                grand += it.Total;
                c.Text(Clip(it.Name, 260, 11, false), 50, y, 11, Ink);
                c.Text(it.Qty.ToString(CultureInfo.InvariantCulture), 320, y, 11, Ink);
                c.Text(Rupiah(it.Price), 370, y, 11, Ink);
                c.Text(Rupiah(it.Total), pw - 50, y, 11, Ink, false, PdfAlign.Right);
                y += 22;
                c.Line(40, y - 12, pw - 40, y - 12, LineC, 1f);
            }
            if (y > ph - 170) { c.NewPage(); y = 60; }
            y += 14;
            c.Text("GRAND TOTAL: " + Rupiah(grand), pw - 50, y, 15, Blue, true, PdfAlign.Right);
            y += 50;
            c.Text("Hormat Kami,", pw - 180, y, 12, Muted);
            if (stamp != null)
            {
                float sw = 120, sh = sw * stamp.Height / Math.Max(1f, stamp.Width);
                if (sh > 90) { sh = 90; sw = sh * stamp.Width / Math.Max(1f, stamp.Height); }
                c.Image(stamp, new RectangleF(pw - 200, y + 10, sw, sh));
            }
            c.Text($"Dibuat dengan Depup Office • {DateTime.Now:dd/MM/yyyy HH:mm}", 40, ph - 30, 8, Muted);
            c.Save(outPath);
        }

        public static List<IReadOnlyList<string>> InvoiceGrid(string shop, string invNo, string date, string customer, IReadOnlyList<InvoiceItem> items)
        {
            var g = new List<IReadOnlyList<string>>
            {
                new[] { "KUITANSI / NOTA PEMBAYARAN", "", "", "" },
                new[] { "Toko:", shop, "Nomor:", invNo },
                new[] { "Tanggal:", date, "Pelanggan:", customer },
                new[] { "", "", "", "" },
                new[] { "Nama Barang", "Qty", "Harga Satuan", "Total" }
            };
            long grand = 0;
            foreach (var it in items) { grand += it.Total; g.Add(new[] { it.Name, it.Qty.ToString(CultureInfo.InvariantCulture), it.Price.ToString(CultureInfo.InvariantCulture), it.Total.ToString(CultureInfo.InvariantCulture) }); }
            g.Add(new[] { "", "", "GRAND TOTAL:", grand.ToString(CultureInfo.InvariantCulture) });
            return g;
        }

        // ------------------------------------------------------------------ katalog

        public static int CatalogPdf(string outPath, string title, string contact, IReadOnlyList<CatalogItem> items)
        {
            var c = new PdfCanvas();
            float pw = c.PageWidth, colW = (pw - 100) / 2f, cardH = 140;
            float y0 = 0;
            void Head()
            {
                float y = 40;
                c.Text(Clip(title, pw - 80, 22, true), 40, y, 22, Blue, true); y += 20;
                c.Text(Clip(contact, pw - 80, 12, false), 40, y, 12, Muted);
                c.Line(40, y + 12, pw - 40, y + 12, LineC, 1.5f);
                y0 = y + 32;
            }
            Head();
            for (int idx = 0; idx < items.Count; idx++)
            {
                int slot = idx % 8;
                if (idx > 0 && slot == 0) { c.NewPage(); Head(); }
                var it = items[idx];
                int col = slot % 2, row = slot / 2;
                float left = 40 + col * (colW + 20), top = y0 + row * (cardH + 16);
                c.FillRect(new RectangleF(left, top, colW, cardH), CardBg, 12);
                float textLeft = left + 12;
                if (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath))
                {
                    try
                    {
                        using var bmp = PdfEngine.Decode(it.ImagePath!, 900);
                        float bw = 82, bh = cardH - 16, s = Math.Min(bw / bmp.Width, bh / bmp.Height);
                        float dw = bmp.Width * s, dh = bmp.Height * s;
                        c.Image(bmp, new RectangleF(left + 8 + (bw - dw) / 2, top + 8 + (bh - dh) / 2, dw, dh), 255, 85, 900);
                        textLeft = left + 100;
                    }
                    catch { }
                }
                float tw = left + colW - textLeft - 10;
                c.Text(Clip(it.Title, tw, 13, true), textLeft, top + 28, 13, Ink, true);
                c.Text(Clip(it.PriceOrSub, tw, 12, true), textLeft, top + 48, 12, Blue, true);
                float y = top + 68;
                foreach (var ln in PdfCanvas.Wrap(it.Description, tw, 10).Take(5)) { c.Text(ln, textLeft, y, 10, Muted); y += 13; }
            }
            c.Save(outPath);
            return c.PageCount;
        }

        // ------------------------------------------------------------------ jadwal

        public static void SchedulePdf(string outPath, string title, string owner, IReadOnlyList<ScheduleTask> tasks)
        {
            var c = new PdfCanvas();
            float pw = c.PageWidth, ph = c.PageHeight, y = 40;
            c.Text(Clip(title, pw - 80, 22, true), 40, y, 22, Blue, true); y += 20;
            c.Text("Penanggung Jawab: " + owner, 40, y, 12, Muted);
            c.Line(40, y + 12, pw - 40, y + 12, LineC, 1.5f); y += 32;
            void Header()
            {
                c.FillRect(new RectangleF(40, y, pw - 80, 28), HeadBg);
                c.Text("No", 50, y + 18, 11, Blue, true);
                c.Text("Hari / Waktu", 80, y + 18, 11, Blue, true);
                c.Text("Tugas / Kegiatan", 210, y + 18, 11, Blue, true);
                c.Text("Catatan", 420, y + 18, 11, Blue, true);
                y += 46;
            }
            Header();
            int n = 0;
            foreach (var t in tasks)
            {
                var lines = PdfCanvas.Wrap(t.TaskName, 200, 11);
                float rowH = Math.Max(22, lines.Count * 14 + 8);
                if (y + rowH > ph - 50) { c.NewPage(); y = 50; Header(); }
                c.Text((++n).ToString(CultureInfo.InvariantCulture), 50, y, 11, Muted);
                c.Text(Clip(t.TimeOrDay, 120, 11, false), 80, y, 11, Ink);
                float yy = y; foreach (var l in lines) { c.Text(l, 210, yy, 11, Ink); yy += 14; }
                c.Text(Clip(t.Note, pw - 40 - 420, 11, false), 420, y, 11, Ink);
                y += rowH;
                c.Line(40, y - 12, pw - 40, y - 12, LineC, 1f);
            }
            c.Text($"Dibuat dengan Depup Office • {DateTime.Now:dd/MM/yyyy}", 40, ph - 30, 8, Muted);
            c.Save(outPath);
        }

        public static List<IReadOnlyList<string>> ScheduleGrid(string title, string owner, IReadOnlyList<ScheduleTask> tasks)
        {
            var g = new List<IReadOnlyList<string>>
            {
                new[] { "PERENCANA JADWAL & AGENDA", "", "", "" },
                new[] { "Judul:", title, "Tim / Nama:", owner },
                new[] { "", "", "", "" },
                new[] { "No", "Hari / Waktu", "Tugas / Kegiatan", "Catatan" }
            };
            int i = 0;
            foreach (var t in tasks) g.Add(new[] { (++i).ToString(CultureInfo.InvariantCulture), t.TimeOrDay, t.TaskName, t.Note });
            return g;
        }

        // ------------------------------------------------------------------ label harga

        public static Bitmap PriceTagBitmap(string product, string price, string code, Barcode1D.Format f)
        {
            int w = 720, h = 440;
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            using (var pen = new Pen(Color.LightGray, 6)) g.DrawRectangle(pen, 3, 3, w - 6, h - 6);
            using var fT = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fP = new Font("Segoe UI", 50, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fC = new Font("Segoe UI", 26, FontStyle.Regular, GraphicsUnit.Pixel);
            using var bT = new SolidBrush(Ink); using var bP = new SolidBrush(Blue); using var bC = new SolidBrush(Muted);
            g.DrawString(product, fT, bT, new RectangleF(30, 20, w - 60, 40), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            g.DrawString(price, fP, bP, 28, 64);
            using var bc = Barcode1D.Render(code, f, w - 60, 200);
            var dst = f == Barcode1D.Format.Qr ? new Rectangle((w - 200) / 2, 140, 200, 200) : new Rectangle(30, 140, w - 60, 200);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(bc, dst);
            g.DrawString(f == Barcode1D.Format.Ean13 ? Barcode1D.Ean13Digits(code) : code, fC, bC, 30, h - 70);
            return bmp;
        }

        /// <summary>Lembar A4 vektor berisi grid label (3 x 7 = 21) siap cetak.</summary>
        public static void PriceTagSheetPdf(string outPath, string product, string price, string code, Barcode1D.Format f)
        {
            var c = new PdfCanvas();
            int cols = 3, rows = 7;
            float mx = 20, my = 24, gap = 8;
            float tw = (c.PageWidth - mx * 2 - gap * (cols - 1)) / cols, th = (c.PageHeight - my * 2 - gap * (rows - 1)) / rows;
            string human = f == Barcode1D.Format.Ean13 ? Barcode1D.Ean13Digits(code) : code;
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < cols; k++)
                {
                    float x = mx + k * (tw + gap), y = my + r * (th + gap);
                    c.StrokeRect(new RectangleF(x, y, tw, th), Color.LightGray, 0.8f);
                    c.Text(Clip(product, tw - 16, 9, true), x + 8, y + 14, 9, Ink, true);
                    c.Text(Clip(price, tw - 16, 15, true), x + 8, y + 32, 15, Blue, true);
                    var br = f == Barcode1D.Format.Qr ? new RectangleF(x + 8, y + 38, tw - 16, th - 52) : new RectangleF(x + 12, y + 38, tw - 24, th - 56);
                    Barcode1D.Draw(c, code, f, br);
                    c.Text(Clip(human, tw - 16, 7, false), x + 8, y + th - 6, 7, Muted);
                }
            c.Save(outPath);
        }

        // ------------------------------------------------------------------ pasfoto

        public static readonly (string Label, float WCm, float HCm)[] PhotoSizes = { ("2 × 3 cm", 2, 3), ("3 × 4 cm", 3, 4), ("4 × 6 cm", 4, 6), ("1 × 1 (Persegi)", 3, 3) };
        public static readonly (string Label, Color Color)[] PhotoBackgrounds = { ("Merah", ColorTranslator.FromHtml("#D32F2F")), ("Biru", ColorTranslator.FromHtml("#1976D2")), ("Putih", Color.White), ("Abu", ColorTranslator.FromHtml("#E0E0E0")) };

        /// <summary>Potong sesuai rasio (berpusat, sedikit ke atas untuk posisi wajah).</summary>
        public static Bitmap CropRatio(Bitmap src, float rw, float rh)
        {
            float target = rw / rh, srcR = src.Width / (float)src.Height;
            int cw = src.Width, ch = src.Height;
            if (srcR > target) cw = (int)(src.Height * target); else ch = (int)(src.Width / target);
            int sx = Math.Max(0, (src.Width - cw) / 2), sy = Math.Max(0, (src.Height - ch) / 3);
            cw = Math.Min(cw, src.Width - sx); ch = Math.Min(ch, src.Height - sy);
            var outB = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(outB);
            g.DrawImage(src, new Rectangle(0, 0, cw, ch), new Rectangle(sx, sy, cw, ch), GraphicsUnit.Pixel);
            return outB;
        }

        /// <summary>
        /// Ganti latar: warna latar diambil dari dua pojok atas, lalu hanya area latar yang TERHUBUNG dengan tepi
        /// (flood fill) yang diganti — baju/wajah yang kebetulan mirip warna latar tidak ikut berubah (lebih baik dari versi Android).
        /// </summary>
        public static Bitmap ChangeBackground(Bitmap src, Color newColor, int tolerance = 85)
        {
            int w = src.Width, h = src.Height;
            var px = GetPixels(src);
            int s1 = px[0], s2 = px[w - 1];
            bool IsBg(int p)
            {
                int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
                int d1 = Math.Abs(r - ((s1 >> 16) & 255)) + Math.Abs(g - ((s1 >> 8) & 255)) + Math.Abs(b - (s1 & 255));
                int d2 = Math.Abs(r - ((s2 >> 16) & 255)) + Math.Abs(g - ((s2 >> 8) & 255)) + Math.Abs(b - (s2 & 255));
                return d1 < tolerance || d2 < tolerance;
            }
            var mark = new bool[w * h];
            var q = new Queue<int>();
            void Seed(int i) { if (!mark[i] && IsBg(px[i])) { mark[i] = true; q.Enqueue(i); } }
            for (int x = 0; x < w; x++) { Seed(x); Seed((h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Seed(y * w); Seed(y * w + w - 1); }
            while (q.Count > 0)
            {
                int i = q.Dequeue(), x = i % w, y = i / w;
                if (x > 0) Seed(i - 1); if (x < w - 1) Seed(i + 1); if (y > 0) Seed(i - w); if (y < h - 1) Seed(i + w);
            }
            int nc = newColor.ToArgb();
            for (int i = 0; i < px.Length; i++) if (mark[i]) px[i] = nc;
            return FromPixels(px, w, h);
        }

        /// <summary>Lembar A4 PDF dengan pasfoto UKURAN ASLI (cm) siap cetak 100%.</summary>
        public static int IdPhotoSheetPdf(string outPath, Bitmap photo, float wCm, float hCm)
        {
            const float CM = 28.3465f;
            var c = new PdfCanvas();
            float pw = wCm * CM, ph = hCm * CM, gap = 0.4f * CM, margin = 1.2f * CM;
            int cols = (int)((c.PageWidth - 2 * margin + gap) / (pw + gap)), rows = (int)((c.PageHeight - 2 * margin + gap) / (ph + gap));
            cols = Math.Max(1, cols); rows = Math.Max(1, rows);
            float ox = (c.PageWidth - (cols * pw + (cols - 1) * gap)) / 2, oy = (c.PageHeight - (rows * ph + (rows - 1) * gap)) / 2;
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < cols; k++)
                {
                    var rect = new RectangleF(ox + k * (pw + gap), oy + r * (ph + gap), pw, ph);
                    c.Image(photo, rect, 255, 92, 1400);
                    c.StrokeRect(rect, Color.LightGray, 0.5f);
                }
            c.Text($"Pasfoto {wCm:0.#} × {hCm:0.#} cm • cetak dengan skala 100% (Ukuran sebenarnya)", c.PageWidth / 2, c.PageHeight - 14, 7, Muted, false, PdfAlign.Center);
            c.Save(outPath);
            return rows * cols;
        }

        // ------------------------------------------------------------------ grafik

        public static List<ChartSeries> ExtractSeries(IReadOnlyList<IReadOnlyList<string>> rows)
        {
            var outL = new List<ChartSeries>();
            if (rows.Count == 0) return outL;
            static double? Num(string s)
            {
                s = (s ?? "").Trim().Replace("Rp", "").Replace(" ", "");
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
                if (double.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("id-ID"), out v)) return v;
                return null;
            }
            var data = rows[0].Any(c => Num(c) != null) ? rows : rows.Skip(1).ToList();
            int idx = 0;
            foreach (var row in data)
            {
                if (outL.Count >= 12) break;
                idx++;
                string label = row.Count > 0 && !string.IsNullOrWhiteSpace(row[0]) ? row[0].Trim() : $"Data {idx}";
                double? v = row.Skip(1).Select(Num).FirstOrDefault(x => x != null) ?? row.Select(Num).FirstOrDefault(x => x != null);
                if (v is double d && d > 0) outL.Add(new ChartSeries(label.Length > 15 ? label[..15] : label, (float)d, ChartColors[outL.Count % ChartColors.Length]));
            }
            return outL;
        }

        private static string NumLabel(float v) => v >= 1000 ? v.ToString("#,0", CultureInfo.GetCultureInfo("id-ID")) : v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Gambar grafik (0 batang, 1 lingkaran, 2 garis) ke Graphics di kotak <paramref name="r"/>.</summary>
        public static void DrawChart(Graphics g, RectangleF r, IReadOnlyList<ChartSeries> s, int type, float fontPx)
        {
            if (s.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", fontPx, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Ink);
            float max = Math.Max(1f, s.Max(x => x.Value));
            if (type == 1)
            {
                float total = Math.Max(1f, s.Sum(x => x.Value));
                float legendW = r.Width * 0.32f;
                float rad = Math.Min(r.Width - legendW, r.Height) / 2.2f;
                float cx = r.Left + (r.Width - legendW) / 2, cy = r.Top + r.Height / 2;
                float a = -90;
                foreach (var it in s)
                {
                    float sw = it.Value / total * 360f;
                    using var b = new SolidBrush(it.Color);
                    g.FillPie(b, cx - rad, cy - rad, 2 * rad, 2 * rad, a, sw);
                    int pct = (int)Math.Round(it.Value / total * 100);
                    if (pct > 5)
                    {
                        double m = (a + sw / 2) * Math.PI / 180;
                        var t = $"{pct}%"; var sz = g.MeasureString(t, font);
                        g.DrawString(t, font, Brushes.White, cx + (float)(rad * 0.62 * Math.Cos(m)) - sz.Width / 2, cy + (float)(rad * 0.62 * Math.Sin(m)) - sz.Height / 2);
                    }
                    a += sw;
                }
                float ly = r.Top + 10, lx = r.Right - legendW + 10;
                foreach (var it in s)
                {
                    using var b = new SolidBrush(it.Color);
                    g.FillRectangle(b, lx, ly + 3, fontPx * 0.9f, fontPx * 0.9f);
                    g.DrawString($"{it.Label} ({NumLabel(it.Value)})", font, ink, lx + fontPx * 1.3f, ly);
                    ly += fontPx * 1.6f;
                }
                return;
            }
            float left = r.Left + fontPx * 3, bottom = r.Bottom - fontPx * 2.2f, top = r.Top + fontPx * 1.6f, plotH = bottom - top, plotW = r.Right - left;
            using (var ax = new Pen(LineC, 2)) { g.DrawLine(ax, left, bottom, r.Right, bottom); for (int i = 1; i <= 4; i++) g.DrawLine(ax, left, bottom - plotH * i / 4, r.Right, bottom - plotH * i / 4); }
            for (int i = 0; i <= 4; i++) g.DrawString(NumLabel(max * i / 4), font, Brushes.Gray, r.Left, bottom - plotH * i / 4 - fontPx * 0.7f);
            if (type == 0)
            {
                float slot = plotW / s.Count, bw = slot * 0.62f;
                for (int i = 0; i < s.Count; i++)
                {
                    float bh = s[i].Value / max * plotH, x = left + i * slot + (slot - bw) / 2;
                    using var b = new SolidBrush(s[i].Color);
                    using var path = Theme.RoundedRect(Rectangle.Round(new RectangleF(x, bottom - bh, bw, bh)), (int)Math.Min(8, bw / 4));
                    g.FillPath(b, path);
                    var vt = NumLabel(s[i].Value); var vs = g.MeasureString(vt, font);
                    g.DrawString(vt, font, ink, x + bw / 2 - vs.Width / 2, bottom - bh - vs.Height);
                    var lt = s[i].Label.Length > 10 ? s[i].Label[..10] : s[i].Label; var ls = g.MeasureString(lt, font);
                    g.DrawString(lt, font, ink, x + bw / 2 - ls.Width / 2, bottom + 4);
                }
            }
            else
            {
                float step = plotW / Math.Max(1, s.Count - 1);
                var pts = s.Select((it, i) => new PointF(left + (s.Count == 1 ? plotW / 2 : i * step), bottom - it.Value / max * plotH)).ToArray();
                using (var pen = new Pen(Blue, fontPx * 0.25f) { LineJoin = LineJoin.Round }) if (pts.Length > 1) g.DrawLines(pen, pts);
                for (int i = 0; i < pts.Length; i++)
                {
                    using var b = new SolidBrush(s[i].Color);
                    float rr = fontPx * 0.45f;
                    g.FillEllipse(b, pts[i].X - rr, pts[i].Y - rr, 2 * rr, 2 * rr);
                    var vt = NumLabel(s[i].Value); var vs = g.MeasureString(vt, font);
                    g.DrawString(vt, font, ink, pts[i].X - vs.Width / 2, pts[i].Y - rr - vs.Height);
                    var lt = s[i].Label.Length > 10 ? s[i].Label[..10] : s[i].Label; var ls = g.MeasureString(lt, font);
                    g.DrawString(lt, font, ink, pts[i].X - ls.Width / 2, bottom + 4);
                }
            }
        }

        public static Bitmap ChartBitmap(IReadOnlyList<ChartSeries> s, int type, string title, int w = 1600, int h = 1000)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            using (var f = new Font("Segoe UI", 40, FontStyle.Bold, GraphicsUnit.Pixel)) using (var b = new SolidBrush(Blue)) g.DrawString("Laporan Grafik: " + title, f, b, 50, 30);
            DrawChart(g, new RectangleF(50, 120, w - 100, h - 160), s, type, 26);
            return bmp;
        }

        /// <summary>PDF laporan: grafik (gambar tajam) + tabel data vektor.</summary>
        public static void ChartPdf(string outPath, IReadOnlyList<ChartSeries> s, int type, string title)
        {
            var c = new PdfCanvas();
            float pw = c.PageWidth, y = 44;
            c.Text("LAPORAN GRAFIK", 40, y, 20, Blue, true); y += 20;
            c.Text(Clip("Sumber: " + title, pw - 80, 11, false), 40, y, 11, Muted);
            c.Line(40, y + 10, pw - 40, y + 10, LineC, 1.5f); y += 26;
            using (var bmp = ChartBitmap(s, type, title, 2000, 1250))
                c.Image(bmp, new RectangleF(40, y, pw - 80, (pw - 80) * 1250f / 2000f), 255, 92, 2000);
            y += (pw - 80) * 1250f / 2000f + 26;
            c.FillRect(new RectangleF(40, y, pw - 80, 24), HeadBg);
            c.Text("Label", 50, y + 16, 11, Blue, true); c.Text("Nilai", 300, y + 16, 11, Blue, true); c.Text("Persentase", pw - 50, y + 16, 11, Blue, true, PdfAlign.Right);
            y += 40;
            float total = Math.Max(1f, s.Sum(x => x.Value));
            foreach (var it in s)
            {
                c.FillRect(new RectangleF(50, y - 9, 9, 9), it.Color);
                c.Text(it.Label, 66, y, 11, Ink); c.Text(NumLabel(it.Value), 300, y, 11, Ink);
                c.Text($"{it.Value / total * 100:0.#}%", pw - 50, y, 11, Ink, false, PdfAlign.Right);
                y += 20; c.Line(40, y - 12, pw - 40, y - 12, LineC, 0.8f);
            }
            c.Save(outPath);
        }

        // ------------------------------------------------------------------ util piksel

        public static int[] GetPixels(Bitmap b)
        {
            using var c = b.Clone(new Rectangle(0, 0, b.Width, b.Height), PixelFormat.Format32bppArgb);
            var bd = c.LockBits(new Rectangle(0, 0, c.Width, c.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new int[c.Width * c.Height];
                for (int y = 0; y < c.Height; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * c.Width, c.Width);
                return px;
            }
            finally { c.UnlockBits(bd); }
        }

        public static Bitmap FromPixels(int[] px, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * w, bd.Scan0 + y * bd.Stride, w); }
            finally { b.UnlockBits(bd); }
            return b;
        }
    }
}
