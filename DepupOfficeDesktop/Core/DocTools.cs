using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>Filter "Magic Color" penghapus bayangan (port DocFilter.kt, algoritma identik).</summary>
    public static class DocFilter
    {
        public static void Magic(int[] px, int w, int h)
        {
            if (w < 8 || h < 8 || px.Length < w * h) return;
            int cell = Math.Max(8, Math.Max(w, h) / 40);
            int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
            var bg = PaperMap(px, w, h, cell, gw, gh);
            var gain = new float[gw * gh];
            for (int i = 0; i < gain.Length; i++) gain[i] = 255f / (bg[i] * 0.96f);
            var lut = new int[256];
            for (int i = 0; i < 256; i++) lut[i] = Math.Clamp((int)(255.0 * Math.Pow(i / 255.0, 1.2)), 0, 255);
            var ix0 = new int[w]; var ix1 = new int[w]; var tx = new float[w];
            for (int x = 0; x < w; x++)
            {
                float f = (x + 0.5f) / cell - 0.5f;
                int i0 = f < 0 ? 0 : (int)f;
                ix0[x] = Math.Clamp(i0, 0, gw - 1); ix1[x] = Math.Clamp(i0 + 1, 0, gw - 1);
                tx[x] = f < 0 ? 0 : Math.Clamp(f - i0, 0f, 1f);
            }
            const float sat = 1.12f;
            for (int y = 0; y < h; y++)
            {
                float f = (y + 0.5f) / cell - 0.5f;
                int j0 = Math.Clamp(f < 0 ? 0 : (int)f, 0, gh - 1), j1 = Math.Clamp(j0 + 1, 0, gh - 1);
                float ty = f < 0 ? 0 : Math.Clamp(f - j0, 0f, 1f);
                int r0 = j0 * gw, r1 = j1 * gw, row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int a = ix0[x], b = ix1[x]; float t = tx[x];
                    float g0 = gain[r0 + a] * (1 - t) + gain[r0 + b] * t;
                    float g1 = gain[r1 + a] * (1 - t) + gain[r1 + b] * t;
                    float g = g0 * (1 - ty) + g1 * ty;
                    int c = px[row + x];
                    float r = Math.Min(255f, ((c >> 16) & 255) * g), gr = Math.Min(255f, ((c >> 8) & 255) * g), bl = Math.Min(255f, (c & 255) * g);
                    float lum = r * 0.299f + gr * 0.587f + bl * 0.114f;
                    int r2 = Math.Clamp((int)(lum + (r - lum) * sat), 0, 255), g2 = Math.Clamp((int)(lum + (gr - lum) * sat), 0, 255), b2 = Math.Clamp((int)(lum + (bl - lum) * sat), 0, 255);
                    px[row + x] = (int)(c & 0xFF000000) | (lut[r2] << 16) | (lut[g2] << 8) | lut[b2];
                }
            }
        }

        private static float[] PaperMap(int[] px, int w, int h, int cell, int gw, int gh)
        {
            var p90 = new float[gw * gh];
            var hist = new int[256];
            int step = Math.Max(1, cell / 16);
            for (int cy = 0; cy < gh; cy++)
                for (int cx = 0; cx < gw; cx++)
                {
                    Array.Clear(hist); int n = 0;
                    int x0 = cx * cell, x1 = Math.Min(w, x0 + cell), y0 = cy * cell, y1 = Math.Min(h, y0 + cell);
                    for (int y = y0; y < y1; y += step)
                        for (int x = x0; x < x1; x += step)
                        {
                            int c = px[y * w + x];
                            hist[(((c >> 16) & 255) * 299 + ((c >> 8) & 255) * 587 + (c & 255) * 114) / 1000]++; n++;
                        }
                    int acc = 0, v = 255; float target = n * 0.9f;
                    for (int i = 0; i <= 255; i++) { acc += hist[i]; if (acc >= target) { v = i; break; } }
                    p90[cy * gw + cx] = Math.Max(8, v);
                }
            var clo = Closing(p90, gw, gh, 4);
            var adj = new float[gw * gh];
            for (int i = 0; i < adj.Length; i++)
            {
                float dip = (clo[i] - p90[i]) / clo[i];
                float wgt = Math.Clamp((dip - 0.22f) / 0.18f, 0f, 1f);
                adj[i] = p90[i] + wgt * (clo[i] - p90[i]);
            }
            var sorted = (float[])adj.Clone(); Array.Sort(sorted);
            float floor = Math.Max(8f, sorted[sorted.Length / 2] * 0.55f);
            for (int i = 0; i < adj.Length; i++) adj[i] = Math.Max(adj[i], floor);
            return adj;
        }

        private static float[] Closing(float[] a, int gw, int gh, int r)
        {
            int pw = gw + 2 * r, ph = gh + 2 * r;
            var cur = new float[pw * ph];
            for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) cur[y * pw + x] = a[Math.Clamp(y - r, 0, gh - 1) * gw + Math.Clamp(x - r, 0, gw - 1)];
            float[] Pass(float[] src, bool horiz, bool takeMax)
            {
                var o = new float[pw * ph];
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        float v = takeMax ? -1f : 1e9f;
                        for (int d = -r; d <= r; d++)
                        {
                            int xx = horiz ? x + d : x, yy = horiz ? y : y + d;
                            if (xx < 0 || yy < 0 || xx >= pw || yy >= ph) continue;
                            float q = src[yy * pw + xx];
                            if (takeMax ? q > v : q < v) v = q;
                        }
                        o[y * pw + x] = v;
                    }
                return o;
            }
            cur = Pass(Pass(cur, true, true), false, true);
            cur = Pass(Pass(cur, true, false), false, false);
            var outA = new float[gw * gh];
            for (int y = 0; y < gh; y++) for (int x = 0; x < gw; x++) outA[y * gw + x] = cur[(y + r) * pw + x + r];
            return outA;
        }

        /// <summary>Salinan bersih dari foto dokumen (bayangan dihapus, kertas putih, tinta pekat).</summary>
        public static Bitmap Clean(Bitmap src)
        {
            var px = BizGenerators.GetPixels(src);
            Magic(px, src.Width, src.Height);
            return BizGenerators.FromPixels(px, src.Width, src.Height);
        }
    }

    public sealed class BusinessCardInfo
    {
        public string Name = "", Phone = "", Email = "", Company = "", Title = "", Website = "", Address = "";
    }

    public sealed record SearchHit(string FilePath, string Snippet, string Where);

    /// <summary>Alat dokumen: kartu nama, tabel foto→Excel, watermark foto massal, ringkasan, cari isi berkas.</summary>
    public static class DocTools
    {
        // ------------------------------------------------------------------ kartu nama → VCF

        private static readonly Regex EmailRe = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
        private static readonly Regex PhoneRe = new(@"(?:\+62|08|021|\(?0[1-9]\)?)[0-9\s-]{7,15}");
        private static readonly Regex WebRe = new(@"(?:https?://|www\.)[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");

        public static BusinessCardInfo ParseCard(string raw)
        {
            var info = new BusinessCardInfo();
            var lines = raw.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var used = new HashSet<string>();
            foreach (var line in lines)
            {
                if (info.Email == "" && EmailRe.IsMatch(line)) { info.Email = EmailRe.Match(line).Value; used.Add(line); continue; }
                if (info.Phone == "" && PhoneRe.IsMatch(line)) { info.Phone = PhoneRe.Match(line).Value.Replace(" ", "").Replace("-", ""); used.Add(line); continue; }
                if (info.Website == "" && WebRe.IsMatch(line)) { info.Website = WebRe.Match(line).Value; used.Add(line); continue; }
                var lo = line.ToLowerInvariant();
                if (info.Company == "" && (lo.Contains("pt.") || lo.Contains("pt ") || lo.Contains("cv.") || lo.Contains("inc") || lo.Contains("ltd") || lo.Contains("tbk"))) { info.Company = line; used.Add(line); continue; }
                if (info.Address == "" && (lo.Contains("jl.") || lo.Contains("jalan") || lo.Contains("gedung") || lo.Contains("street") || lo.Contains("kec."))) { info.Address = line; used.Add(line); continue; }
            }
            var rest = lines.Where(l => !used.Contains(l)).ToList();
            if (rest.Count > 0) info.Name = rest[0];
            if (rest.Count > 1) info.Title = rest[1];
            return info;
        }

        private static string VEsc(string s) => (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

        public static string Vcf(BusinessCardInfo i)
        {
            string name = string.IsNullOrWhiteSpace(i.Name) ? "Kontak" : i.Name.Trim();
            var sb = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\n");
            sb.Append($"N:;{VEsc(name)};;;\r\nFN:{VEsc(name)}\r\n");
            if (i.Company != "") sb.Append($"ORG:{VEsc(i.Company)}\r\n");
            if (i.Title != "") sb.Append($"TITLE:{VEsc(i.Title)}\r\n");
            if (i.Phone != "") sb.Append($"TEL;TYPE=CELL:{VEsc(i.Phone)}\r\n");
            if (i.Email != "") sb.Append($"EMAIL;TYPE=INTERNET:{VEsc(i.Email)}\r\n");
            if (i.Website != "") sb.Append($"URL:{VEsc(i.Website)}\r\n");
            if (i.Address != "") sb.Append($"ADR;TYPE=WORK:;;{VEsc(i.Address)};;;;\r\n");
            return sb.Append("END:VCARD\r\n").ToString();
        }

        // ------------------------------------------------------------------ tabel foto → Excel

        /// <summary>
        /// Susun kata OCR jadi grid: kata dikelompokkan ke BARIS menurut pusat-y, lalu batas KOLOM ditentukan dari celah
        /// horizontal yang konsisten di seluruh baris (proyeksi kosong). Kata di satu sel digabung dengan spasi.
        /// </summary>
        public static List<IReadOnlyList<string>> WordsToGrid(List<OcrLine> words, int imgW)
        {
            var grid = new List<IReadOnlyList<string>>();
            words = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) return grid;
            float medH = words.Select(w => (float)w.Box.Height).OrderBy(x => x).ElementAt(words.Count / 2);
            // baris
            var rows = new List<List<OcrLine>>();
            foreach (var w in words.OrderBy(w => w.Box.Top + w.Box.Height / 2f))
            {
                float cy = w.Box.Top + w.Box.Height / 2f;
                var last = rows.LastOrDefault();
                if (last != null && Math.Abs(last.Average(x => x.Box.Top + x.Box.Height / 2f) - cy) < medH * 0.6f) last.Add(w);
                else rows.Add(new List<OcrLine> { w });
            }
            // proyeksi horizontal: kolom piksel yang tertutup kata
            int W = Math.Max(imgW, words.Max(w => w.Box.Right) + 1);
            var cover = new int[W];
            foreach (var w in words) for (int x = Math.Max(0, w.Box.Left); x < Math.Min(W, w.Box.Right); x++) cover[x]++;
            int minGap = Math.Max(6, (int)(medH * 1.2f));
            var cuts = new List<int>();
            int run = 0;
            int firstX = words.Min(w => w.Box.Left), lastX = words.Max(w => w.Box.Right);
            for (int x = firstX; x <= lastX; x++)
            {
                if (x < W && cover[x] == 0) run++;
                else { if (run >= minGap) cuts.Add(x - run / 2); run = 0; }
            }
            int Col(OcrLine w) { float cx = w.Box.Left + w.Box.Width / 2f; int c = 0; while (c < cuts.Count && cx > cuts[c]) c++; return c; }
            int ncol = cuts.Count + 1;
            foreach (var r in rows)
            {
                var cells = new string[ncol];
                foreach (var g in r.OrderBy(w => w.Box.Left).GroupBy(Col))
                    cells[g.Key] = string.Join(" ", g.Select(w => w.Text.Trim()));
                grid.Add(cells.Select(c => c ?? "").ToArray());
            }
            // buang kolom yang seluruhnya kosong
            var keep = Enumerable.Range(0, ncol).Where(c => grid.Any(r => r[c].Length > 0)).ToList();
            return grid.Select(r => (IReadOnlyList<string>)keep.Select(c => r[c]).ToArray()).ToList();
        }

        public static List<IReadOnlyList<string>> TableFromImage(Bitmap bmp)
        {
            var (_, words) = WinOcrRunner.RecognizeWithWords(bmp);
            return WordsToGrid(words.Select(w => w with { Text = AiText.Clean(w.Text) }).ToList(), bmp.Width);
        }

        // ------------------------------------------------------------------ watermark foto massal

        /// <summary>Tempel teks atau stempel ke foto. <paramref name="position"/>: 0 kanan bawah, 1 tengah, 2 kanan atas, 3 kiri bawah.</summary>
        public static Bitmap WatermarkImage(Bitmap src, string text, Bitmap? stamp, int position, int opacity)
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(src, 0, 0, src.Width, src.Height);
            float w = bmp.Width, h = bmp.Height;
            if (stamp != null)
            {
                float sw = w * 0.25f, sh = sw * stamp.Height / Math.Max(1f, stamp.Width), m = w * 0.04f;
                var r = position switch
                {
                    0 => new RectangleF(w - sw - m, h - sh - m, sw, sh),
                    1 => new RectangleF((w - sw) / 2, (h - sh) / 2, sw, sh),
                    2 => new RectangleF(w - sw - m, m, sw, sh),
                    _ => new RectangleF(m, h - sh - m, sw, sh)
                };
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = opacity / 255f });
                g.DrawImage(stamp, Rectangle.Round(r), 0, 0, stamp.Width, stamp.Height, GraphicsUnit.Pixel, ia);
            }
            else if (!string.IsNullOrWhiteSpace(text))
            {
                using var f = new Font("Segoe UI", Math.Max(10f, Math.Min(w, h) * 0.045f), FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString(text, f);
                float m = w * 0.05f;
                var pt = position switch
                {
                    0 => new PointF(w - m - sz.Width, h - m - sz.Height),
                    1 => new PointF((w - sz.Width) / 2, (h - sz.Height) / 2),
                    2 => new PointF(w - m - sz.Width, m),
                    _ => new PointF(m, h - m - sz.Height)
                };
                float sh = Math.Max(2, f.Size * 0.06f);
                using (var shadow = new SolidBrush(Color.FromArgb(Math.Min(153, opacity), 0, 0, 0))) g.DrawString(text, f, shadow, pt.X + sh, pt.Y + sh);
                using var br = new SolidBrush(Color.FromArgb(opacity, 255, 255, 255));
                g.DrawString(text, f, br, pt);
            }
            return bmp;
        }

        // ------------------------------------------------------------------ baca teks dokumen

        public static string DocxPlain(string path)
        {
            using var z = ZipFile.OpenRead(path);
            var e = z.GetEntry("word/document.xml");
            if (e == null) return "";
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            var xml = r.ReadToEnd();
            xml = Regex.Replace(xml, @"</w:p>", "\n");
            xml = Regex.Replace(xml, @"<w:tab/>", "\t");
            xml = Regex.Replace(xml, "<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(xml);
        }

        public static string XlsxPlain(string path)
        {
            var s = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? OfficeReader.ReadCsv(path) : OfficeReader.ReadXlsx(path);
            return s == null ? "" : string.Join("\n", s.Rows.Select(r => string.Join("\t", r)));
        }

        /// <summary>Teks dokumen (PDF lewat OCR offline Windows, maks. <paramref name="maxPdfPages"/> halaman).</summary>
        public static string ReadText(string path, int maxPdfPages, Action<int, int>? progress = null)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // PDF: tiap halaman dibersihkan sendiri lalu dipisah \f (penanda halaman untuk hasil pencarian)
            if (ext == ".pdf") return string.Join("\f\n", PdfToolsX.OcrPages(path, maxPdfPages, progress).Select(AiText.Clean));
            string t = ext switch
            {
                ".docx" => DocxPlain(path),
                ".xlsx" or ".csv" => XlsxPlain(path),
                ".pptx" => string.Join("\n", PptxRenderer.SlideTexts(path)),
                ".txt" => File.ReadAllText(path),
                _ => ""
            };
            return AiText.Clean(t);
        }

        // ------------------------------------------------------------------ ringkasan

        private static readonly string[] KeyWords = { "tujuan", "kesimpulan", "penting", "total", "ringkasan", "rekomendasi", "hasil", "catatan", "pasal", "syarat", "wajib", "batas waktu", "jumlah" };

        /// <summary>Kalimat/paragraf kunci (bobot panjang + kata kunci + posisi awal) — algoritma Android, ditambah bobot posisi.</summary>
        public static List<string> KeyPoints(string text, int take = 5)
        {
            var paras = text.Split('\n').Select(p => Regex.Replace(p, @"\s+", " ").Trim()).Where(p => p.Length > 25).ToList();
            if (paras.Count == 0) return new List<string> { text.Length > 200 ? text[..200] : text };
            var scored = paras.Select((p, i) =>
            {
                int score = Math.Min(p.Length, 300);
                var lo = p.ToLowerInvariant();
                foreach (var k in KeyWords) if (lo.Contains(k)) score += 150;
                if (Regex.IsMatch(p, @"\d")) score += 30;
                if (i < 3) score += 60;
                if (AiText.LooksLikeInjection(p)) score -= 1000;   // jangan angkat instruksi tersembunyi ke ringkasan
                return (p, score, i);
            });
            return scored.OrderByDescending(x => x.score).Take(take).OrderBy(x => x.i).Select(x => x.p.Length > 600 ? x.p[..600] + "…" : x.p).ToList();
        }

        public static int WordCount(string t) => Regex.Matches(t, @"\S+").Count;

        public static void SummaryPdf(string outPath, string sourceName, int words, IReadOnlyList<string> points)
        {
            var c = new PdfCanvas();
            float pw = c.PageWidth, y = 44;
            c.Text("RINGKASAN DOKUMEN", 40, y, 20, BizGenerators.Blue, true); y += 20;
            c.Text($"Sumber: {sourceName} • Total {words} kata", 40, y, 11, BizGenerators.Muted);
            c.Line(40, y + 10, pw - 40, y + 10, BizGenerators.LineC, 1.5f); y += 36;
            for (int i = 0; i < points.Count; i++)
            {
                c.Text($"{i + 1}.", 40, y, 12, BizGenerators.Blue, true);
                y = c.Paragraph(points[i], 60, y, pw - 100, 11.5f, BizGenerators.Ink, false, 1.4f, c.PageHeight - 60) + 10;
            }
            c.Text("Ringkasan ekstraktif otomatis (offline) • Depup Office", 40, c.PageHeight - 28, 8, BizGenerators.Muted);
            c.Save(outPath);
        }

        // ------------------------------------------------------------------ cari isi berkas

        public static IEnumerable<string> SearchCandidates()
        {
            var set = new List<string>();
            foreach (var r in AppStorage.Config.RecentFiles) if (File.Exists(r.FilePath)) set.Add(r.FilePath);
            try
            {
                foreach (var f in Directory.EnumerateFiles(AppStorage.OutDir, "*.*", SearchOption.AllDirectories))
                    set.Add(f);
            }
            catch { }
            var ok = new[] { ".pdf", ".docx", ".xlsx", ".csv", ".txt", ".pptx" };
            return set.Where(f => ok.Contains(Path.GetExtension(f).ToLowerInvariant()))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } });
        }

        private static readonly Dictionary<string, (DateTime stamp, string text)> _textCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cari kata kunci di dalam isi berkas (Word/Excel/PPTX langsung, PDF lewat OCR; hasil OCR di-cache per sesi).</summary>
        public static List<SearchHit> Search(string q, int maxFiles, int maxPdfPages, Action<int, int, string>? progress = null, Func<bool>? cancelled = null)
        {
            var hits = new List<SearchHit>();
            var files = SearchCandidates().Take(maxFiles).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                if (cancelled?.Invoke() == true) break;
                var f = files[i];
                progress?.Invoke(i + 1, files.Count, Path.GetFileName(f));
                try
                {
                    if (ProtectedDoc.IsProtected(f)) continue;   // isi terenkripsi: tidak ikut diindeks
                    if (maxPdfPages <= 0 && f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                    var stamp = File.GetLastWriteTimeUtc(f);
                    string text;
                    if (_textCache.TryGetValue(f, out var ce) && ce.stamp == stamp) text = ce.text;
                    else
                    {
                        if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && PdfRenderer.IsEncrypted(f) && PdfRenderer.GetCachedPassword(f) == null) continue;
                        text = ReadText(f, maxPdfPages);
                        _textCache[f] = (stamp, text);
                    }
                    int idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    int s = Math.Max(0, idx - 40), e = Math.Min(text.Length, idx + q.Length + 40);
                    string where = "";
                    if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        where = "Halaman " + (text[..idx].Count(ch => ch == '\f') + 1);
                    hits.Add(new SearchHit(f, Regex.Replace(text[s..e], @"\s+", " "), where));
                }
                catch { }
            }
            return hits;
        }
    }
}
