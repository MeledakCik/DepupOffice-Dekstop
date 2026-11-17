using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    public enum PdfAlign { Left, Center, Right }

    /// <summary>
    /// Kanvas PDF vektor sederhana (pengganti android.graphics.pdf.PdfDocument): teks Helvetica asli (bisa dicari &amp;
    /// disalin), kotak, garis, juring (grafik lingkaran), gambar JPEG / PNG transparan. Koordinat seperti Android:
    /// titik PDF, asal di KIRI ATAS, sumbu-y ke bawah. Tanpa NuGet.
    /// </summary>
    public sealed class PdfCanvas
    {
        private sealed class PageData { public float W, H; public StringBuilder Ops = new(); public HashSet<int> Images = new(); public HashSet<string> Gs = new(); }
        private sealed class ImageData { public int W, H; public byte[] Data = Array.Empty<byte>(); public string Filter = ""; public string ColorSpace = "/DeviceRGB"; public byte[]? Alpha; }

        private readonly List<PageData> _pages = new();
        private readonly List<ImageData> _images = new();
        private PageData _cur = null!;
        public float PageWidth => _cur.W;
        public float PageHeight => _cur.H;
        public int PageCount => _pages.Count;

        public const float A4W = 595f, A4H = 842f;

        public PdfCanvas(float w = A4W, float h = A4H) { NewPage(w, h); }

        public void NewPage(float w = A4W, float h = A4H)
        {
            _cur = new PageData { W = w, H = h };
            _pages.Add(_cur);
        }

        private static string F(double v) => Pdf.Fmt(Math.Round(v, 3));
        private float Y(float y) => _cur.H - y;
        private static string Rgb(Color c) => $"{F(c.R / 255.0)} {F(c.G / 255.0)} {F(c.B / 255.0)}";

        private void Alpha(int a)
        {
            if (a >= 255) return;
            string n = "Ga" + Math.Clamp(a, 0, 255);
            _cur.Gs.Add(n);
            _cur.Ops.Append('/').Append(n).Append(" gs\n");
        }

        // ------------------------------------------------------------------ teks

        /// <summary>Kode WinAnsi untuk karakter (0x80-0x9F memetakan tanda khusus cp1252), '?' bila tidak ada.</summary>
        public static int WinAnsi(char c)
        {
            if (c < 0x80 || (c >= 0xA0 && c <= 0xFF)) return c;
            return c switch
            {
                '€' => 0x80, '‚' => 0x82, 'ƒ' => 0x83, '„' => 0x84, '…' => 0x85, '†' => 0x86, '‡' => 0x87, 'ˆ' => 0x88, '‰' => 0x89,
                'Š' => 0x8A, '‹' => 0x8B, 'Œ' => 0x8C, 'Ž' => 0x8E, '‘' => 0x91, '’' => 0x92, '“' => 0x93, '”' => 0x94, '•' => 0x95,
                '–' => 0x96, '—' => 0x97, '˜' => 0x98, '™' => 0x99, 'š' => 0x9A, '›' => 0x9B, 'œ' => 0x9C, 'ž' => 0x9E, 'Ÿ' => 0x9F,
                '\t' => 0x20, _ => '?'
            };
        }

        public static float Measure(string s, float size, bool bold = false)
        {
            var w = bold ? WBold : WReg;
            double t = 0;
            foreach (var ch in s) { int c = WinAnsi(ch); t += c >= 32 && c <= 255 ? w[c - 32] : 556; }
            return (float)(t * size / 1000.0);
        }

        /// <summary>String literal PDF (dengan escape) dalam pengodean WinAnsi.</summary>
        public static string Lit(string s)
        {
            var sb = new StringBuilder("(");
            foreach (var ch in s)
            {
                int c = WinAnsi(ch);
                if (c < 32) c = 32;
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append((char)c);
                else if (c > 126) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                else sb.Append((char)c);
            }
            return sb.Append(')').ToString();
        }

        /// <summary>Gambar teks satu baris; <paramref name="y"/> = garis dasar (baseline).</summary>
        public void Text(string s, float x, float y, float size, Color color, bool bold = false, PdfAlign align = PdfAlign.Left)
        {
            if (string.IsNullOrEmpty(s)) return;
            float tw = Measure(s, size, bold);
            if (align == PdfAlign.Center) x -= tw / 2f; else if (align == PdfAlign.Right) x -= tw;
            _cur.Ops.Append("q ");
            Alpha(color.A);
            _cur.Ops.Append(Rgb(color)).Append(" rg BT /").Append(bold ? "F2" : "F1").Append(' ').Append(F(size)).Append(" Tf ")
                .Append(F(x)).Append(' ').Append(F(Y(y))).Append(" Td ").Append(Lit(s)).Append(" Tj ET Q\n");
        }

        /// <summary>Teks diputar <paramref name="deg"/> derajat (berlawanan jarum jam) terhadap titik tengahnya.</summary>
        public void TextRotated(string s, float cx, float cy, float size, Color color, float deg, bool bold = true)
        {
            float tw = Measure(s, size, bold);
            double r = deg * Math.PI / 180.0, cs = Math.Cos(r), sn = Math.Sin(r);
            // titik awal agar teks berpusat di (cx, cy)
            double dx = -tw / 2.0, dy = -size * 0.35;
            double px = cx + dx * cs - dy * sn, py = Y(cy) + dx * sn + dy * cs;
            _cur.Ops.Append("q ");
            Alpha(color.A);
            _cur.Ops.Append(Rgb(color)).Append(" rg BT /").Append(bold ? "F2" : "F1").Append(' ').Append(F(size)).Append(" Tf ")
                .Append($"{F(cs)} {F(sn)} {F(-sn)} {F(cs)} {F(px)} {F(py)} Tm ").Append(Lit(s)).Append(" Tj ET Q\n");
        }

        /// <summary>Pecah teks per kata agar muat lebar <paramref name="maxW"/>.</summary>
        public static List<string> Wrap(string text, float maxW, float size, bool bold = false)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var words = para.Split(' ');
                string line = "";
                foreach (var w0 in words)
                {
                    var w = w0;
                    // kata sangat panjang: potong paksa
                    while (Measure(w, size, bold) > maxW && w.Length > 1)
                    {
                        int k = w.Length - 1;
                        while (k > 1 && Measure(w[..k], size, bold) > maxW) k--;
                        if (line.Length > 0) { lines.Add(line); line = ""; }
                        lines.Add(w[..k]); w = w[k..];
                    }
                    string cand = line.Length == 0 ? w : line + " " + w;
                    if (Measure(cand, size, bold) > maxW && line.Length > 0) { lines.Add(line); line = w; }
                    else line = cand;
                }
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>Paragraf terbungkus; mengembalikan y garis dasar berikutnya. Pindah halaman otomatis bila <paramref name="bottom"/> &gt; 0.</summary>
        public float Paragraph(string text, float x, float y, float maxW, float size, Color color, bool bold = false, float leading = 1.35f, float bottom = 0, float topOnNewPage = 50)
        {
            foreach (var ln in Wrap(text, maxW, size, bold))
            {
                if (bottom > 0 && y > bottom) { NewPage(_cur.W, _cur.H); y = topOnNewPage; }
                Text(ln, x, y, size, color, bold);
                y += size * leading;
            }
            return y;
        }

        // ------------------------------------------------------------------ bentuk

        private void PathRect(RectangleF r, float radius)
        {
            float x = r.Left, y = Y(r.Bottom), w = r.Width, h = r.Height;
            if (radius <= 0) { _cur.Ops.Append($"{F(x)} {F(y)} {F(w)} {F(h)} re\n"); return; }
            float rr = Math.Min(radius, Math.Min(w, h) / 2f), k = rr * 0.5523f;
            var o = _cur.Ops;
            o.Append($"{F(x + rr)} {F(y)} m {F(x + w - rr)} {F(y)} l ");
            o.Append($"{F(x + w - rr + k)} {F(y)} {F(x + w)} {F(y + rr - k)} {F(x + w)} {F(y + rr)} c ");
            o.Append($"{F(x + w)} {F(y + h - rr)} l {F(x + w)} {F(y + h - rr + k)} {F(x + w - rr + k)} {F(y + h)} {F(x + w - rr)} {F(y + h)} c ");
            o.Append($"{F(x + rr)} {F(y + h)} l {F(x + rr - k)} {F(y + h)} {F(x)} {F(y + h - rr + k)} {F(x)} {F(y + h - rr)} c ");
            o.Append($"{F(x)} {F(y + rr)} l {F(x)} {F(y + rr - k)} {F(x + rr - k)} {F(y)} {F(x + rr)} {F(y)} c h\n");
        }

        public void FillRect(RectangleF r, Color c, float radius = 0)
        {
            _cur.Ops.Append("q "); Alpha(c.A);
            _cur.Ops.Append(Rgb(c)).Append(" rg ");
            PathRect(r, radius);
            _cur.Ops.Append("f Q\n");
        }

        public void StrokeRect(RectangleF r, Color c, float width = 1f, float radius = 0)
        {
            _cur.Ops.Append("q "); Alpha(c.A);
            _cur.Ops.Append(Rgb(c)).Append(" RG ").Append(F(width)).Append(" w ");
            PathRect(r, radius);
            _cur.Ops.Append("S Q\n");
        }

        public void Line(float x1, float y1, float x2, float y2, Color c, float width = 1f)
        {
            _cur.Ops.Append("q "); Alpha(c.A);
            _cur.Ops.Append($"{Rgb(c)} RG {F(width)} w 1 J {F(x1)} {F(Y(y1))} m {F(x2)} {F(Y(y2))} l S Q\n");
        }

        public void Polyline(IReadOnlyList<PointF> pts, Color c, float width = 2f)
        {
            if (pts.Count < 2) return;
            var o = _cur.Ops; o.Append("q "); Alpha(c.A);
            o.Append($"{Rgb(c)} RG {F(width)} w 1 J 1 j {F(pts[0].X)} {F(Y(pts[0].Y))} m ");
            for (int i = 1; i < pts.Count; i++) o.Append($"{F(pts[i].X)} {F(Y(pts[i].Y))} l ");
            o.Append("S Q\n");
        }

        public void FillCircle(float cx, float cy, float r, Color c) => FillEllipse(new RectangleF(cx - r, cy - r, 2 * r, 2 * r), c);

        public void FillEllipse(RectangleF b, Color c)
        {
            float cx = b.Left + b.Width / 2, cy = Y(b.Top + b.Height / 2), rx = b.Width / 2, ry = b.Height / 2, kx = rx * 0.5523f, ky = ry * 0.5523f;
            var o = _cur.Ops; o.Append("q "); Alpha(c.A); o.Append(Rgb(c)).Append(" rg ");
            o.Append($"{F(cx + rx)} {F(cy)} m {F(cx + rx)} {F(cy + ky)} {F(cx + kx)} {F(cy + ry)} {F(cx)} {F(cy + ry)} c ");
            o.Append($"{F(cx - kx)} {F(cy + ry)} {F(cx - rx)} {F(cy + ky)} {F(cx - rx)} {F(cy)} c ");
            o.Append($"{F(cx - rx)} {F(cy - ky)} {F(cx - kx)} {F(cy - ry)} {F(cx)} {F(cy - ry)} c ");
            o.Append($"{F(cx + kx)} {F(cy - ry)} {F(cx + rx)} {F(cy - ky)} {F(cx + rx)} {F(cy)} c f Q\n");
        }

        /// <summary>Juring lingkaran (grafik pie). Sudut dalam derajat searah jarum jam dari arah jam 3 (seperti Android drawArc).</summary>
        public void FillPie(float cx, float cy, float r, float startDeg, float sweepDeg, Color c)
        {
            if (sweepDeg <= 0) return;
            var o = _cur.Ops; o.Append("q "); Alpha(c.A); o.Append(Rgb(c)).Append(" rg ");
            o.Append($"{F(cx)} {F(Y(cy))} m ");
            int segs = Math.Max(1, (int)Math.Ceiling(sweepDeg / 45f));
            float step = sweepDeg / segs;
            double a0 = startDeg * Math.PI / 180;
            o.Append($"{F(cx + r * Math.Cos(a0))} {F(Y((float)(cy + r * Math.Sin(a0))))} l ");
            for (int i = 0; i < segs; i++)
            {
                double s = (startDeg + i * step) * Math.PI / 180, e = (startDeg + (i + 1) * step) * Math.PI / 180;
                double k = 4.0 / 3.0 * Math.Tan((e - s) / 4) * r;
                double x0 = cx + r * Math.Cos(s), y0 = cy + r * Math.Sin(s), x3 = cx + r * Math.Cos(e), y3 = cy + r * Math.Sin(e);
                double x1 = x0 - k * Math.Sin(s), y1 = y0 + k * Math.Cos(s), x2 = x3 + k * Math.Sin(e), y2 = y3 - k * Math.Cos(e);
                o.Append($"{F(x1)} {F(Y((float)y1))} {F(x2)} {F(Y((float)y2))} {F(x3)} {F(Y((float)y3))} c ");
            }
            o.Append("h f Q\n");
        }

        // ------------------------------------------------------------------ gambar

        /// <summary>Gambar bitmap ke kotak <paramref name="dst"/>. Bitmap ber-alfa disimpan Flate + SMask (tetap transparan).</summary>
        public void Image(Bitmap bmp, RectangleF dst, int alpha = 255, int jpegQuality = 88, int maxDim = 2000)
        {
            Bitmap src = bmp;
            bool scaled = false;
            if (Math.Max(bmp.Width, bmp.Height) > maxDim)
            {
                float f = maxDim / (float)Math.Max(bmp.Width, bmp.Height);
                src = new Bitmap(bmp, Math.Max(1, (int)(bmp.Width * f)), Math.Max(1, (int)(bmp.Height * f)));
                scaled = true;
            }
            try
            {
                var img = Encode(src, jpegQuality);
                _images.Add(img);
                int idx = _images.Count - 1;
                _cur.Images.Add(idx);
                _cur.Ops.Append("q "); Alpha(alpha);
                _cur.Ops.Append($"{F(dst.Width)} 0 0 {F(dst.Height)} {F(dst.Left)} {F(Y(dst.Bottom))} cm /Im{idx} Do Q\n");
            }
            finally { if (scaled) src.Dispose(); }
        }

        private static ImageData Encode(Bitmap bmp, int q)
        {
            int w = bmp.Width, h = bmp.Height;
            bool hasAlpha = System.Drawing.Image.IsAlphaPixelFormat(bmp.PixelFormat) && AnyTransparent(bmp);
            if (!hasAlpha) return new ImageData { W = w, H = h, Data = PdfEngine.EncodeJpeg(bmp, q), Filter = "/DCTDecode" };
            var rgb = new byte[w * h * 3]; var a = new byte[w * h];
            using (var b32 = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb))
            {
                var bd = b32.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w * 4);
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x;
                            rgb[i * 3] = row[x * 4 + 2]; rgb[i * 3 + 1] = row[x * 4 + 1]; rgb[i * 3 + 2] = row[x * 4]; a[i] = row[x * 4 + 3];
                        }
                    }
                }
                finally { b32.UnlockBits(bd); }
            }
            return new ImageData { W = w, H = h, Data = Pdf.Deflate(rgb), Filter = "/FlateDecode", Alpha = Pdf.Deflate(a) };
        }

        private static bool AnyTransparent(Bitmap bmp)
        {
            int step = Math.Max(1, Math.Max(bmp.Width, bmp.Height) / 200);
            for (int y = 0; y < bmp.Height; y += step)
                for (int x = 0; x < bmp.Width; x += step)
                    if (bmp.GetPixel(x, y).A < 250) return true;
            return false;
        }

        // ------------------------------------------------------------------ simpan

        public byte[] ToBytes()
        {
            var objs = new List<byte[]>();          // indeks 0 = objek 1
            int Reserve() { objs.Add(Array.Empty<byte>()); return objs.Count; }
            void Set(int n, string s) => objs[n - 1] = Pdf.Latin1.GetBytes(s);
            void SetStream(int n, string dict, byte[] data)
            {
                var head = Pdf.Latin1.GetBytes($"<< {dict} /Length {data.Length} >>\nstream\n");
                var tail = Pdf.Latin1.GetBytes("\nendstream");
                objs[n - 1] = Pdf.Concat(head, data, tail);
            }
            int cat = Reserve(), pagesN = Reserve(), f1 = Reserve(), f2 = Reserve();
            Set(f1, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            Set(f2, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            var imgNums = new int[_images.Count];
            for (int i = 0; i < _images.Count; i++)
            {
                var im = _images[i];
                int smask = 0;
                if (im.Alpha != null)
                {
                    smask = Reserve();
                    SetStream(smask, $"/Type /XObject /Subtype /Image /Width {im.W} /Height {im.H} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", im.Alpha);
                }
                imgNums[i] = Reserve();
                SetStream(imgNums[i], $"/Type /XObject /Subtype /Image /Width {im.W} /Height {im.H} /ColorSpace {im.ColorSpace} /BitsPerComponent 8 /Filter {im.Filter}" + (smask > 0 ? $" /SMask {smask} 0 R" : ""), im.Data);
            }
            var kids = new List<int>();
            foreach (var pg in _pages)
            {
                int cs = Reserve();
                SetStream(cs, "/Filter /FlateDecode", Pdf.Deflate(Pdf.Latin1.GetBytes(pg.Ops.ToString())));
                var res = new StringBuilder($"/Font << /F1 {f1} 0 R /F2 {f2} 0 R >>");
                if (pg.Images.Count > 0) res.Append(" /XObject << ").Append(string.Join(" ", pg.Images.Select(i => $"/Im{i} {imgNums[i]} 0 R"))).Append(" >>");
                if (pg.Gs.Count > 0) res.Append(" /ExtGState << ").Append(string.Join(" ", pg.Gs.Select(g => { var a = int.Parse(g[2..], CultureInfo.InvariantCulture) / 255.0; return $"/{g} << /ca {F(a)} /CA {F(a)} >>"; }))).Append(" >>");
                int p = Reserve();
                Set(p, $"<< /Type /Page /Parent {pagesN} 0 R /MediaBox [0 0 {F(pg.W)} {F(pg.H)}] /Resources << {res} >> /Contents {cs} 0 R >>");
                kids.Add(p);
            }
            Set(pagesN, $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(k => k + " 0 R"))}] >>");
            Set(cat, $"<< /Type /Catalog /Pages {pagesN} 0 R >>");
            int info = Reserve();
            Set(info, $"<< /Producer (Depup Office Desktop) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>");

            using var ms = new MemoryStream();
            void W(string s) { var b = Pdf.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
            W("%PDF-1.4\n%âãÏÓ\n");
            var offs = new long[objs.Count];
            for (int i = 0; i < objs.Count; i++)
            {
                offs[i] = ms.Position;
                W($"{i + 1} 0 obj\n"); ms.Write(objs[i], 0, objs[i].Length); W("\nendobj\n");
            }
            long xref = ms.Position;
            var sb = new StringBuilder($"xref\n0 {objs.Count + 1}\n0000000000 65535 f\r\n");
            foreach (var o in offs) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
            W(sb.ToString());
            W($"trailer\n<< /Size {objs.Count + 1} /Root {cat} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }

        /// <summary>Simpan ke berkas; opsional dikunci AES-256 dan/atau objek katalog diubah.</summary>
        public void Save(string path, PdfSave.Lock? lk = null, Action<PdfSave.Ctx>? edit = null)
        {
            var bytes = ToBytes();
            if (lk == null && edit == null) { File.WriteAllBytes(path, bytes); return; }
            using var fs = File.Create(path);
            PdfSave.Write(PdfFile.Open(bytes), fs, lk, edit);
        }

        // ------------------------------------------------------------------ metrik Helvetica (WinAnsi 32..255, dari AFM URW)
        private static readonly short[] WReg = { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 350, 556, 278, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 278, 611, 278, 278, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 278, 500, 667, 278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333, 400, 584, 350, 350, 333, 556, 537, 278, 333, 350, 365, 556, 834, 834, 834, 611, 667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278, 556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500 };
        private static readonly short[] WBold = { 278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 350, 556, 278, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 278, 611, 278, 278, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 278, 500, 667, 278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333, 400, 584, 350, 350, 333, 611, 556, 278, 333, 350, 365, 556, 834, 834, 834, 611, 722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278, 722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611, 556, 556, 556, 556, 556, 556, 889, 556, 556, 556, 556, 556, 278, 278, 278, 278, 611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611, 556, 611, 556 };
    }
}
