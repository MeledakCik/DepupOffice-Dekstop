using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// PDF → Word (.docx) "semirip mungkin" (port PdfToDocx.kt Android v4.1). Isi PDF dibaca langsung (<see cref="PdfLayout"/>):
    ///  - teks ASLI (bukan OCR) dengan font, ukuran, tebal/miring, warna; perataan, inden, spasi baris sesuai PDF; kolom bercelah jadi tab,
    ///  - tabel dari garis-garis PDF: ukuran kolom/baris, sel gabungan, arsiran, garis per sisi,
    ///  - foto (JPEG asli / PNG transparan) di posisinya,
    ///  - bentuk (kotak, kotak bersudut bulat, lingkaran, garis, poligon) sebagai shape Word di posisi persis halaman,
    ///  - unsur berdampingan (logo + kop surat) disusun dalam tabel tata letak tanpa garis,
    ///  - ukuran &amp; margin halaman per halaman (section).
    /// Halaman hasil pindai (tanpa teks) diserahkan ke <see cref="IScan"/> (OCR Windows).
    /// </summary>
    public sealed class PdfToDocx
    {
        /// <summary>OCR halaman pindaian → paragraf; null = tidak ada OCR. PageImage: gambar halaman (JPEG) bila isi tak terbaca.</summary>
        public interface IScan
        {
            List<string>? Ocr(int pageIndex);
            (byte[] Jpeg, int W, int H)? PageImage(int pageIndex);
        }

        private readonly string _out;
        private readonly StringBuilder _body = new();
        private readonly HashSet<PdfLayout.Glyph> _underlined = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfLayout.Glyph> _struck = new(ReferenceEqualityComparer.Instance);
        private readonly List<(string Name, byte[] Data)> _media = new();
        private int _shapeId = 1;
        private string _finalSect = "";

        /// <summary>Jumlah halaman yang teksnya terbaca langsung / yang lewat OCR / yang jadi gambar (untuk laporan &amp; uji).</summary>
        public int TextPages, OcrPages, ImagePages;

        public PdfToDocx(string outPath) { _out = outPath; }

        // ------------------------------------------------------------------ API

        public void Convert(PdfFile f, IScan? scan, Action<int, int>? progress = null)
        {
            var pages = f.Pages();
            PdfLayout.PageOut Layout(int i)
            {
                var p = pages[i];
                try { return PdfLayout.Page(f, p); }
                catch (OutOfMemoryException) { throw; }
                catch { return new PdfLayout.PageOut(p.Box[2] - p.Box[0], p.Box[3] - p.Box[1], new(), new(), new()); }
            }
            // 1) ukur batas isi tiap halaman saja (hemat memori: unsur halaman tidak disimpan)
            var bounds = new List<Plan>();
            for (int i = 0; i < pages.Count; i++)
            {
                progress?.Invoke(i + 1, pages.Count * 2);
                var pl = MakePlan(Layout(i), i, scan, false);
                bounds.Add(new Plan(pl.W, pl.H, new(), "", pl.MinX, pl.MaxX, pl.MinY, pl.MaxY, isScan: pl.IsScan));
            }
            // 2) halaman berukuran sama = satu section dengan margin bersama
            int a = 0;
            while (a < bounds.Count)
            {
                int j = a + 1;
                while (j < bounds.Count && Math.Abs(bounds[j].W - bounds[a].W) < 2f && Math.Abs(bounds[j].H - bounds[a].H) < 2f) j++;
                var grp = bounds.GetRange(a, j - a);
                float w = grp[0].W, h = grp[0].H;
                float ml = Clamp(grp.Min(x => x.MinX), 14f, w * 0.4f);
                // +2 pt kelonggaran: metrik font di Word bisa sedikit lebih lebar
                float mr = Clamp(grp.Min(x => w - x.MaxX) - 2f, 12f, w * 0.4f);
                float mt = Clamp(grp.Min(x => x.MinY), 10f, h * 0.4f);
                float mb = Clamp(grp.Min(x => (h - x.MaxY) * 0.5f), 8f, 54f);
                var sect = new Sect(w, h, ml, mr, mt, mb);
                for (int k = a; k < j; k++)
                {
                    progress?.Invoke(pages.Count + k + 1, pages.Count * 2);
                    if (k > 0) _body.Append(Spacer(1f, pageBreak: true));
                    Emit(MakePlan(Layout(k), k, scan, true), sect);
                }
                if (j < bounds.Count) _body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/></w:rPr>").Append(SectXml(sect)).Append("</w:pPr></w:p>");
                else _finalSect = SectXml(sect);
                a = j;
            }
            if (pages.Count == 0) _body.Append("<w:p/>");
            Finish();
        }

        private static float Clamp(float v, float lo, float hi) => hi < lo ? lo : Math.Clamp(v, lo, hi);

        private sealed class Plan
        {
            public readonly float W, H, MinX, MaxX, MinY, MaxY; public readonly List<Item> Items; public readonly string Anchors;
            public readonly List<string>? Scan; public readonly (byte[] Jpeg, int W, int H)? ScanImg; public readonly bool IsScan;
            public Plan(float w, float h, List<Item> items, string anchors, float minX, float maxX, float minY, float maxY,
                List<string>? scan = null, (byte[], int, int)? scanImg = null, bool isScan = false)
            { W = w; H = h; Items = items; Anchors = anchors; MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY; Scan = scan; ScanImg = scanImg; IsScan = isScan; }
        }

        // ------------------------------------------------------------------ model halaman

        private sealed class Line
        {
            public readonly List<PdfLayout.Glyph> Glyphs; public readonly float Base, Size, X0, X1;
            private List<List<PdfLayout.Glyph>>? _segs;
            public Line(List<PdfLayout.Glyph> glyphs, float b, float size)
            { Glyphs = glyphs; Base = b; Size = size; X0 = glyphs.Min(g => g.X); X1 = glyphs.Max(g => g.X + g.W); }
            public float Top => Base - Size * 0.8f;
            public float Bot => Base + Size * 0.22f;
            /// <summary>Bagian baris yang dipisah celah lebar (kolom / tab).</summary>
            public List<List<PdfLayout.Glyph>> Segs
            {
                get
                {
                    if (_segs != null) return _segs;
                    var r = new List<List<PdfLayout.Glyph>>(); var cur = new List<PdfLayout.Glyph>();
                    float lastX = float.NaN;
                    foreach (var g in Glyphs)
                    {
                        bool blank = string.IsNullOrWhiteSpace(g.Text);
                        if (!float.IsNaN(lastX) && g.X - lastX > Math.Max(Size * 1.6f, 14f) && !blank && cur.Count > 0) { r.Add(cur); cur = new(); }
                        if (!blank || cur.Count > 0) cur.Add(g);
                        lastX = blank ? (float.IsNaN(lastX) ? g.X : lastX) : g.X + g.W;
                    }
                    if (cur.Count > 0) r.Add(cur);
                    return _segs = r;
                }
            }
        }

        private abstract class Item { public abstract float X0 { get; } public abstract float X1 { get; } public abstract float Top { get; } public abstract float Bot { get; } }
        private sealed class LItem : Item { public readonly Line L; public LItem(Line l) { L = l; } public override float X0 => L.X0; public override float X1 => L.X1; public override float Top => L.Top; public override float Bot => L.Bot; }
        private sealed class IItem : Item { public readonly PdfLayout.Image Im; public IItem(PdfLayout.Image im) { Im = im; } public override float X0 => Im.X0; public override float X1 => Im.X1; public override float Top => Im.Y0; public override float Bot => Im.Y1; }
        /// <summary>Wilayah berkolom (dua kolom): kolom-kolom unsur yang dipisah celah tegak.</summary>
        private sealed class CItem : Item
        {
            public readonly List<List<Item>> Cols; private readonly float _x0, _x1, _top, _bot;
            public CItem(List<List<Item>> cols)
            {
                Cols = cols; _x0 = cols[0].Min(i => i.X0); _x1 = cols[^1].Max(i => i.X1);
                _top = cols.Min(c => c.Min(i => i.Top)); _bot = cols.Max(c => c.Max(i => i.Bot));
            }
            public override float X0 => _x0; public override float X1 => _x1; public override float Top => _top; public override float Bot => _bot;
        }
        private sealed class TItem : Item { public readonly Tbl T; public TItem(Tbl t) { T = t; } public override float X0 => T.Xs[0]; public override float X1 => T.Xs[^1]; public override float Top => T.Ys[0]; public override float Bot => T.Ys[^1]; }

        /// <summary>Garis lurus (dari garis berstroke atau kotak tipis berisi).</summary>
        private sealed class Rule
        {
            public readonly bool H; public float A, B; public readonly float Pos, W; public readonly int Color;
            public Rule(bool h, float a, float b, float pos, float w, int color) { H = h; A = a; B = b; Pos = pos; W = w; Color = color; }
        }

        private sealed class Tbl
        {
            public readonly List<float> Xs, Ys; public readonly List<Rule> Rules;
            public readonly Dictionary<long, List<PdfLayout.Glyph>> CellText = new();
            public readonly Dictionary<long, int> Shade = new();
            public Tbl(List<float> xs, List<float> ys, List<Rule> rules) { Xs = xs; Ys = ys; Rules = rules; }
            public int Rows => Ys.Count - 1;
            public int Cols => Xs.Count - 1;
            public static long Key(int r, int c) => r * 1000L + c;
            /// <summary>Ada garis di batas tegak x=xs[c] pada baris r.</summary>
            public Rule? VRule(int c, int r) => Rules.FirstOrDefault(it => !it.H && Math.Abs(it.Pos - Xs[c]) <= 2.5f && it.A <= Ys[r] + 2.5f + (Ys[r + 1] - Ys[r]) * 0.4f && it.B >= Ys[r + 1] - 2.5f - (Ys[r + 1] - Ys[r]) * 0.4f);
            public Rule? HRule(int r, int c) => Rules.FirstOrDefault(it => it.H && Math.Abs(it.Pos - Ys[r]) <= 2.5f && it.A <= Xs[c] + 2.5f + (Xs[c + 1] - Xs[c]) * 0.4f && it.B >= Xs[c + 1] - 2.5f - (Xs[c + 1] - Xs[c]) * 0.4f);
        }

        private static bool Blank(PdfLayout.Glyph g) => string.IsNullOrWhiteSpace(g.Text);

        private Plan MakePlan(PdfLayout.PageOut po, int idx, IScan? scan, bool ocr)
        {
            _underlined.Clear(); _struck.Clear();
            var visible = po.Glyphs.Where(g => g.Upright && g.Text.Length > 0).ToList();
            var rotated = po.Glyphs.Where(g => !g.Upright && !g.Invisible && g.Text.Length > 0).ToList();
            bool hasInvisible = visible.Any(g => g.Invisible);
            var images = po.Images.Where(i => i.X1 > 0 && i.Y1 > 0 && i.X0 < po.W && i.Y0 < po.H).ToList();
            float pageArea = po.W * po.H;
            // PDF hasil OCR (gambar halaman + teks tersembunyi): pakai teksnya, buang gambar latar sehalaman
            if (hasInvisible) images = images.Where(i => (i.X1 - i.X0) * (i.Y1 - i.Y0) < pageArea * 0.6f).ToList();
            var glyphs = visible;
            // halaman pindaian / tanpa teks terbaca → OCR (atau gambar halaman)
            bool noText = glyphs.Count(g => !Blank(g)) < 3;
            if (noText && scan != null && po.Images.Any(i => (i.X1 - i.X0) * (i.Y1 - i.Y0) > pageArea * 0.4f))
            {
                List<string>? blocks = null;
                if (ocr) { try { blocks = scan.Ocr(idx); } catch { } }
                (byte[], int, int)? img = null;
                if (ocr && (blocks == null || blocks.Count == 0)) { try { img = scan.PageImage(idx); } catch { } }
                return new Plan(po.W, po.H, new(), "", 54f, po.W - 54f, 54f, po.H - 54f, blocks, img, true);
            }

            // --- garis & bentuk
            var rules = new List<Rule>();
            var fills = new List<PdfLayout.Shape>();
            var graphics = new List<PdfLayout.Shape>();
            foreach (var s in po.Shapes)
            {
                var b = s.BBox; float w = b[2] - b[0], h = b[3] - b[1];
                if (b[2] < -2 || b[3] < -2 || b[0] > po.W + 2 || b[1] > po.H + 2) continue;
                if (s.Fill != PdfLayout.NONE && s.IsRect && h <= 2.6f && w > 3f) { rules.Add(new Rule(true, b[0], b[2], (b[1] + b[3]) / 2, Math.Max(h, 0.5f), s.Fill)); continue; }
                if (s.Fill != PdfLayout.NONE && s.IsRect && w <= 2.6f && h > 3f) { rules.Add(new Rule(false, b[1], b[3], (b[0] + b[2]) / 2, Math.Max(w, 0.5f), s.Fill)); continue; }
                if (s.Stroke != PdfLayout.NONE && s.Fill == PdfLayout.NONE && s.Subs.All(x => x.Length <= 64))
                {
                    bool any = false;
                    foreach (var sub in s.Subs)
                        for (int i = 0; i + 3 < sub.Length; i += 2)
                        {
                            float ax = sub[i], ay = sub[i + 1], bx = sub[i + 2], by = sub[i + 3];
                            if (Math.Abs(ay - by) < 0.8f && Math.Abs(ax - bx) > 1f) { rules.Add(new Rule(true, Math.Min(ax, bx), Math.Max(ax, bx), (ay + by) / 2, s.LineW, s.Stroke)); any = true; }
                            else if (Math.Abs(ax - bx) < 0.8f && Math.Abs(ay - by) > 1f) { rules.Add(new Rule(false, Math.Min(ay, by), Math.Max(ay, by), (ax + bx) / 2, s.LineW, s.Stroke)); any = true; }
                        }
                    if (any && s.Subs.All(AxisOnly)) continue;
                }
                if (s.Fill != PdfLayout.NONE && s.IsRect && s.Stroke == PdfLayout.NONE) { fills.Add(s); continue; }
                if (s.Fill == -1 && s.Stroke == PdfLayout.NONE && w * h > pageArea * 0.8f) continue;   // latar putih sehalaman
                graphics.Add(s);
            }
            // garis tipis tepat di bawah / di tengah teks (tidak tersambung ke garis tegak) = garis bawah / coret
            {
                var vs = rules.Where(r => !r.H).ToList();
                for (int ri = rules.Count - 1; ri >= 0; ri--)
                {
                    var r = rules[ri];
                    if (!r.H || r.W > 1.8f) continue;
                    if (vs.Any(v => Math.Abs(v.Pos - r.A) <= 2.5f || Math.Abs(v.Pos - r.B) <= 2.5f)) continue;
                    var inX = glyphs.Where(g => !Blank(g) && g.X + g.W / 2 >= r.A - 1f && g.X + g.W / 2 <= r.B + 1f).ToList();
                    var under = inX.Where(g => r.Pos - g.Y >= -0.5f && r.Pos - g.Y <= g.Size * 0.32f).ToList();
                    var strike = inX.Where(g => g.Y - r.Pos >= g.Size * 0.15f && g.Y - r.Pos <= g.Size * 0.45f).ToList();
                    bool isUnder = under.Count >= strike.Count;
                    var hit = isUnder ? under : strike;
                    if (hit.Count == 0) continue;
                    // garis harus sepanjang teksnya (garis tabel / pemisah lebih panjang dari tulisan di atasnya)
                    if (r.A < hit.Min(g => g.X) - 3f || r.B > hit.Max(g => g.X + g.W) + 3f) continue;
                    foreach (var g in hit) (isUnder ? _underlined : _struck).Add(g);
                    rules.RemoveAt(ri);
                }
            }
            var tables = FindTables(rules);
            var usedRules = new HashSet<Rule>();
            foreach (var r in rules)
                if (tables.Any(t => r.Pos >= (r.H ? t.Ys[0] : t.Xs[0]) - 2.5f && r.Pos <= (r.H ? t.Ys[^1] : t.Xs[^1]) + 2.5f &&
                                    r.A >= (r.H ? t.Xs[0] : t.Ys[0]) - 2.5f && r.B <= (r.H ? t.Xs[^1] : t.Ys[^1]) + 2.5f)) usedRules.Add(r);
            // arsiran sel & teks dalam sel
            var freeFills = new List<PdfLayout.Shape>();
            foreach (var fl in fills)
            {
                var b = fl.BBox; bool used = false;
                foreach (var t in tables)
                    for (int r = 0; r < t.Rows; r++)
                        for (int c = 0; c < t.Cols; c++)
                        {
                            float cx0 = t.Xs[c], cx1 = t.Xs[c + 1], cy0 = t.Ys[r], cy1 = t.Ys[r + 1];
                            float ix = Math.Min(b[2], cx1) - Math.Max(b[0], cx0), iy = Math.Min(b[3], cy1) - Math.Max(b[1], cy0);
                            if (ix > 0 && iy > 0 && ix * iy >= (cx1 - cx0) * (cy1 - cy0) * 0.7f) { t.Shade[Tbl.Key(r, c)] = fl.Fill; used = true; }
                        }
                if (!used) freeFills.Add(fl);
            }
            var free = new List<PdfLayout.Glyph>();
            foreach (var g in glyphs)
            {
                float cx = g.X + g.W / 2, cy = g.Y - g.Size * 0.3f;
                var t = tables.FirstOrDefault(t => cx >= t.Xs[0] && cx <= t.Xs[^1] && cy >= t.Ys[0] && cy <= t.Ys[^1]);
                if (t == null) { free.Add(g); continue; }
                int c = t.Cols - 1; for (int k = 0; k < t.Cols; k++) if (cx < t.Xs[k + 1]) { c = k; break; }
                int r = t.Rows - 1; for (int k = 0; k < t.Rows; k++) if (cy < t.Ys[k + 1]) { r = k; break; }
                var key = Tbl.Key(r, c);
                if (!t.CellText.TryGetValue(key, out var lst)) t.CellText[key] = lst = new();
                lst.Add(g);
            }
            var items = new List<Item>();
            foreach (var l in BuildLines(free)) items.Add(new LItem(l));
            foreach (var im in images) items.Add(new IItem(im));
            foreach (var t in tables) items.Add(new TItem(t));
            items = items.OrderBy(i => i.Top).ThenBy(i => i.X0).ToList();

            // bentuk bebas & isian di luar tabel → shape tertambat ke halaman
            var anchors = new StringBuilder();
            foreach (var s in freeFills) anchors.Append(ShapeXml(s, true));
            foreach (var s in graphics) anchors.Append(ShapeXml(s, s.Fill != PdfLayout.NONE));
            foreach (var r in rules) if (!usedRules.Contains(r)) anchors.Append(RuleXml(r));
            RotatedText(rotated, anchors);
            if (ocr && items.Count > 0) TextPages++;
            return new Plan(po.W, po.H, items, anchors.ToString(),
                items.Count > 0 ? items.Min(i => i.X0) : 72f, items.Count > 0 ? items.Max(i => i.X1) : po.W - 72f,
                items.Count > 0 ? items.Min(i => i.Top) : 72f, items.Count > 0 ? items.Max(i => i.Bot) : po.H - 72f);
        }

        private void Emit(Plan pl, Sect sect)
        {
            if (pl.IsScan) { ScanPage(pl, sect); return; }
            int startLen = _body.Length;
            var anchors = new StringBuilder(pl.Anchors);
            Flow(pl.Items, sect.Ml, sect.W - sect.Mr, sect.Mt, anchors, _body);
            if (_body.Length == startLen || anchors.Length > 0) _body.Append(Spacer(1f, anchors.ToString()));
        }

        private static bool AxisOnly(float[] sub)
        {
            for (int i = 0; i + 3 < sub.Length; i += 2) if (Math.Abs(sub[i] - sub[i + 2]) > 0.8f && Math.Abs(sub[i + 1] - sub[i + 3]) > 0.8f) return false;
            return true;
        }

        private static int RoundI(float v) => (int)MathF.Round(v, MidpointRounding.AwayFromZero);

        // ------------------------------------------------------------------ baris

        private sealed class Row
        {
            public readonly float Base, Size; public readonly List<PdfLayout.Glyph> G = new(); public float X0 = float.MaxValue, X1 = -float.MaxValue;
            public Row(float b, float s) { Base = b; Size = s; }
        }

        private static List<Line> BuildLines(List<PdfLayout.Glyph> gs)
        {
            // glif besar membentuk baris lebih dulu; pangkat/indeks lalu menempel ke baris terdekat
            var sorted = gs.OrderBy(g => -RoundI(g.Size * 2)).ThenBy(g => g.Y).ThenBy(g => g.X).ToList();
            var rows = new List<Row>();
            foreach (var g in sorted)
            {
                Row? best = null; float bd = float.MaxValue;
                foreach (var r in rows)
                {
                    float dy = g.Y - r.Base;
                    bool small = g.Size < r.Size * 0.88f;
                    bool ok = small ? dy >= -r.Size * 0.6f && dy <= r.Size * 0.4f : Math.Abs(dy) <= Math.Max(r.Size, g.Size) * 0.35f;
                    if (ok && small && (g.X > r.X1 + r.Size * 2f || g.X + g.W < r.X0 - r.Size * 2f)) continue;
                    if (ok && Math.Abs(dy) < bd) { bd = Math.Abs(dy); best = r; }
                }
                var row = best;
                if (row == null) { row = new Row(g.Y, g.Size); rows.Add(row); }
                row.G.Add(g); row.X0 = Math.Min(row.X0, g.X); row.X1 = Math.Max(row.X1, g.X + g.W);
            }
            var outL = new List<Line>();
            foreach (var r in rows)
            {
                var s = r.G.OrderBy(g => g.X).ToList();
                // buang glif kembar (teks tebal palsu)
                var dedup = new List<PdfLayout.Glyph>();
                foreach (var g in s)
                {
                    var p = dedup.Count > 0 ? dedup[^1] : null;
                    if (p != null && p.Text == g.Text && Math.Abs(p.X - g.X) < g.Size * 0.15f && Math.Abs(p.Y - g.Y) < 0.5f && !Blank(g)) continue;
                    dedup.Add(g);
                }
                if (dedup.Any(g => !Blank(g))) outL.Add(new Line(dedup, r.Base, r.Size));
            }
            return outL.OrderBy(l => l.Base).ToList();
        }

        // ------------------------------------------------------------------ tabel dari garis

        private static List<Rule> Merge(IEnumerable<Rule> list)
        {
            var outL = new List<Rule>();
            foreach (var r in list.OrderBy(x => RoundI(x.Pos * 2)).ThenBy(x => x.A))
            {
                var p = outL.Count > 0 ? outL[^1] : null;
                if (p != null && Math.Abs(p.Pos - r.Pos) <= 1.2f && r.A <= p.B + 2f) p.B = Math.Max(p.B, r.B);
                else outL.Add(new Rule(r.H, r.A, r.B, r.Pos, r.W, r.Color));
            }
            return outL;
        }

        private static List<Tbl> FindTables(List<Rule> rules0)
        {
            var hs = Merge(rules0.Where(r => r.H)); var vs = Merge(rules0.Where(r => !r.H));
            if (hs.Count < 2 || vs.Count < 2) return new();
            var all = hs.Concat(vs).ToList();
            var parent = Enumerable.Range(0, all.Count).ToArray();
            int Find(int i) { int x = i; while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < hs.Count; i++)
                for (int j = 0; j < vs.Count; j++)
                {
                    var h = hs[i]; var v = vs[j];
                    if (v.Pos >= h.A - 2.5f && v.Pos <= h.B + 2.5f && h.Pos >= v.A - 2.5f && h.Pos <= v.B + 2.5f)
                    { int a = Find(i), b = Find(hs.Count + j); if (a != b) parent[a] = b; }
                }
            var outL = new List<Tbl>();
            foreach (var g in Enumerable.Range(0, all.Count).GroupBy(Find))
            {
                var rs = g.Select(i => all[i]).ToList();
                var gh = rs.Where(r => r.H).ToList(); var gv = rs.Where(r => !r.H).ToList();
                if (gh.Count < 2 || gv.Count < 2) continue;
                var xs = Cluster(gv.Select(r => r.Pos)); var ys = Cluster(gh.Select(r => r.Pos));
                if (xs.Count < 2 || ys.Count < 2) continue;
                if (xs[^1] - xs[0] < 20f || ys[^1] - ys[0] < 8f) continue;
                outL.Add(new Tbl(xs, ys, rs));
            }
            return outL;
        }

        private static List<float> Cluster(IEnumerable<float> v)
        {
            var outL = new List<float>(); var acc = new List<float>();
            foreach (var x in v.OrderBy(x => x))
            {
                if (acc.Count > 0 && x - acc[^1] > 2.5f) { outL.Add(acc.Average()); acc = new(); }
                acc.Add(x);
            }
            if (acc.Count > 0) outL.Add(acc.Average());
            return outL;
        }

        // ------------------------------------------------------------------ alur (flow)

        /// <summary>Susun unsur dalam wilayah [left,right] mulai top ke sink. anchors ditempel di paragraf pertama.</summary>
        private void Flow(List<Item> items0, float left, float right, float top, StringBuilder anchors, StringBuilder sink)
        {
            float cursor = top;
            var sorted = Columnize(items0).OrderBy(i => i.Top).ThenBy(i => i.X0).ToList();
            var pending = new List<Line>();
            void FlushLines() { if (pending.Count > 0) { cursor = Paragraphs(new List<Line>(pending), left, right, cursor, anchors, sink); pending.Clear(); } }
            int i = 0;
            while (i < sorted.Count)
            {
                // pita: unsur yang saling tumpang secara tegak
                float bandBot = sorted[i].Bot;
                int j = i + 1;
                while (j < sorted.Count && sorted[j].Top < bandBot - 1.5f) { bandBot = Math.Max(bandBot, sorted[j].Bot); j++; }
                var band = sorted.GetRange(i, j - i);
                if (band.Count == 1 && band[0] is CItem ci)
                {
                    FlushLines();
                    LayoutRow(ci.Cols, left, right, cursor, sink);
                    cursor = ci.Bot; i = j; continue;
                }
                var cols = Columns(band);
                if (cols.Count >= 2)
                {
                    FlushLines();
                    LayoutRow(cols, left, right, cursor, sink);
                    cursor = band.Max(x => x.Bot);
                }
                else
                {
                    foreach (var it0 in band.OrderBy(x => x.Top).ThenBy(x => x.X0))
                        switch (it0)
                        {
                            case LItem li: pending.Add(li.L); break;
                            case IItem ii: FlushLines(); cursor = ImagePara(ii.Im, left, cursor, anchors, sink); break;
                            case TItem ti: FlushLines(); cursor = Table(ti.T, left, right, cursor, sink); break;
                            case CItem c2: FlushLines(); LayoutRow(c2.Cols, left, right, cursor, sink); cursor = c2.Bot; break;
                        }
                }
                i = j;
            }
            FlushLines();
        }

        /// <summary>
        /// Cari celah tegak (gutter) yang dipakai bersama beberapa pasang baris berdampingan; wilayah di antaranya dipecah
        /// jadi kolom kiri/kanan. Baris yang melintasi celah (judul selebar halaman) memutus wilayah.
        /// </summary>
        private static List<Item> Columnize(List<Item> items0)
        {
            var ls = items0.OfType<LItem>().Select(x => x.L).ToList();
            if (ls.Count < 4) return items0;
            var gaps = new List<float[]>();   // [kiri, kanan, garisdasarA, garisdasarB]
            foreach (var l in ls)
                for (int k = 1; k < l.Segs.Count; k++)
                {
                    float a = l.Segs[k - 1].Max(g => g.X + g.W), b = l.Segs[k].Min(g => g.X);
                    if (b - a >= 8f) gaps.Add(new[] { a, b, l.Base, l.Base });
                }
            var pieces = new List<float[]>();  // [x0, x1, garisdasar, ukuran, indeksBaris]
            for (int li = 0; li < ls.Count; li++)
                foreach (var sg in ls[li].Segs) pieces.Add(new[] { sg.Min(g => g.X), sg.Max(g => g.X + g.W), ls[li].Base, ls[li].Size, li });
            pieces = pieces.OrderBy(p => p[2]).ToList();
            for (int ai = 0; ai < pieces.Count; ai++)
            {
                var a = pieces[ai];
                for (int k = ai + 1; k < pieces.Count && pieces[k][2] - a[2] <= Math.Max(a[3], pieces[k][3]) * 1.6f; k++)
                {
                    var b = pieces[k];
                    if (b[4] != a[4])
                    {
                        if (a[1] < b[0] - 6f) gaps.Add(new[] { a[1], b[0], a[2], b[2] });
                        else if (b[1] < a[0] - 6f) gaps.Add(new[] { b[1], a[0], a[2], b[2] });
                    }
                }
            }
            if (gaps.Count < 3) return items0;
            var ev = gaps.SelectMany(g => new[] { (X: g[0], D: 1), (X: g[1], D: -1) }).OrderBy(e => e.X).ThenBy(e => -e.D).ToList();
            int c = 0, best = 0; float gx = 0;
            for (int k = 0; k < ev.Count; k++) { c += ev[k].D; if (c > best && k + 1 < ev.Count) { best = c; gx = (ev[k].X + ev[k + 1].X) / 2; } }
            var ev2 = gaps.Where(g => g[0] <= gx && g[1] >= gx).ToList();
            if (ev2.SelectMany(g => new[] { RoundI(g[2]), RoundI(g[3]) }).Distinct().Count() < 3) return items0;
            var items = new List<Item>();
            foreach (var it in items0)
            {
                var l = (it as LItem)?.L;
                if (l != null && l.X0 < gx && l.X1 > gx && !l.Glyphs.Any(g => !Blank(g) && g.X < gx - 0.5f && g.X + g.W > gx + 0.5f))
                {
                    var lg = l.Glyphs.Where(g => g.X + g.W / 2 < gx).ToList(); var rg = l.Glyphs.Where(g => g.X + g.W / 2 >= gx).ToList();
                    if (lg.Any(g => !Blank(g))) items.Add(new LItem(new Line(lg, l.Base, l.Size)));
                    if (rg.Any(g => !Blank(g))) items.Add(new LItem(new Line(rg, l.Base, l.Size)));
                }
                else items.Add(it);
            }
            var szL = ls.Where(l => ev2.Any(g => Math.Abs(g[2] - l.Base) < 0.5f || Math.Abs(g[3] - l.Base) < 0.5f)).ToList();
            float sz = szL.Count > 0 ? szL.Max(l => l.Size) : ls.Max(l => l.Size);
            float yTop = ev2.Min(g => Math.Min(g[2], g[3])) - sz;
            float yBot = ev2.Max(g => Math.Max(g[2], g[3])) + sz * 0.3f;
            var inRange = items.Where(x => x.Top >= yTop - 2f && x.Top <= yBot).ToList();
            var inSet = new HashSet<Item>(inRange);
            var outL = items.Where(x => !inSet.Contains(x)).ToList();
            var left = new List<Item>(); var right = new List<Item>();
            void Flush()
            {
                if (left.Count + right.Count >= 3 && left.Count > 0 && right.Count > 0) outL.Add(new CItem(new List<List<Item>> { left, right }));
                else { outL.AddRange(left); outL.AddRange(right); }
                left = new(); right = new();
            }
            foreach (var it in inRange.OrderBy(x => x.Top))
            {
                if (it.X1 <= gx + 1f) left.Add(it);
                else if (it.X0 >= gx - 1f) right.Add(it);
                else { Flush(); outL.Add(it); }
            }
            Flush();
            return outL;
        }

        /// <summary>Kelompokkan unsur pita jadi kolom yang tidak saling tumpang mendatar.</summary>
        private static List<List<Item>> Columns(List<Item> band)
        {
            if (band.Count < 2) return new() { band };
            var cols = new List<List<Item>>(); float end = -float.MaxValue;
            foreach (var it in band.OrderBy(x => x.X0))
            {
                if (cols.Count == 0 || it.X0 > end + 4f) { cols.Add(new() { it }); end = it.X1; }
                else { cols[^1].Add(it); end = Math.Max(end, it.X1); }
            }
            if (cols.Count >= 2 && cols.All(cc => cc.All(x => x is TItem))) return cols;
            // baris teks biasa bercelah lebar sudah ditangani tab
            if (cols.Count >= 2 && cols.All(cc => cc.Count == 1 && cc[0] is LItem)) return new() { band };
            return cols;
        }

        /// <summary>Unsur berdampingan → tabel tata letak 1 baris tanpa garis.</summary>
        private void LayoutRow(List<List<Item>> cols, float left, float right, float cursor, StringBuilder sink)
        {
            float top = cols.Min(c => c.Min(i => i.Top)), bot = cols.Max(c => c.Max(i => i.Bot));
            var bounds = new List<float> { Math.Min(left, cols[0].Min(i => i.X0)) };
            for (int k = 1; k < cols.Count; k++) bounds.Add((cols[k - 1].Max(i => i.X1) + cols[k].Min(i => i.X0)) / 2f);
            bounds.Add(Math.Max(right, cols[^1].Max(i => i.X1)));
            float before = Math.Max(0f, top - cursor);
            if (before > 0.5f) sink.Append(Spacer(before));
            sink.Append("<w:tbl><w:tblPr><w:tblW w:w=\"").Append(Tw(bounds[^1] - bounds[0])).Append("\" w:type=\"dxa\"/><w:tblInd w:w=\"").Append(Tw(bounds[0] - left)).Append("\" w:type=\"dxa\"/>");
            sink.Append("<w:tblBorders>").Append(NoBorders()).Append("</w:tblBorders><w:tblLayout w:type=\"fixed\"/>").Append(ZeroMar).Append("<w:tblLook w:val=\"0000\"/></w:tblPr><w:tblGrid>");
            for (int k = 0; k < cols.Count; k++) sink.Append("<w:gridCol w:w=\"").Append(Tw(bounds[k + 1] - bounds[k])).Append("\"/>");
            sink.Append("</w:tblGrid><w:tr><w:trPr><w:trHeight w:val=\"").Append(Tw(bot - top)).Append("\" w:hRule=\"atLeast\"/><w:cantSplit/></w:trPr>");
            for (int k = 0; k < cols.Count; k++)
            {
                sink.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(Tw(bounds[k + 1] - bounds[k])).Append("\" w:type=\"dxa\"/></w:tcPr>");
                var cell = new StringBuilder();
                Flow(cols[k], bounds[k], bounds[k + 1], top, new StringBuilder(), cell);
                if (cell.Length == 0 || !EndsWith(cell, "</w:p>")) cell.Append(Spacer(1f));
                sink.Append(cell).Append("</w:tc>");
            }
            sink.Append("</w:tr></w:tbl>");
        }

        private const string ZeroMar = "<w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"0\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar>";

        private static bool EndsWith(StringBuilder sb, string s)
        {
            if (sb.Length < s.Length) return false;
            for (int i = 0; i < s.Length; i++) if (sb[sb.Length - s.Length + i] != s[i]) return false;
            return true;
        }

        // ------------------------------------------------------------------ paragraf

        private float Paragraphs(List<Line> lines, float left, float right, float cursor, StringBuilder anchors, StringBuilder sink)
        {
            var paras = new List<List<Line>>();
            // tepi kanan teks yang sebenarnya (di kolom, batas sel ada di tengah celah)
            float tr0 = Math.Min(right, lines.Max(l => l.X1) + 0.5f);
            float textRight = right - tr0 < 40f ? tr0 : right;
            float width = right - left, cx = (left + right) / 2;
            bool Full(Line l) => l.X1 >= textRight - Math.Max(l.Size * 3.5f, width * 0.12f);
            bool Centered(Line l) => Math.Abs((l.X0 + l.X1) / 2 - cx) <= Math.Max(3f, l.Size * 0.4f) && l.X0 > left + l.Size;
            foreach (var l in lines)
            {
                var p = paras.Count > 0 ? paras[^1] : null;
                if (p != null)
                {
                    var prev = p[^1];
                    float d = l.Base - prev.Base;
                    bool okGap = d > 0 && d <= Math.Max(prev.Size, l.Size) * 1.75f;
                    bool okSize = Math.Abs(l.Size - prev.Size) <= Math.Max(l.Size, prev.Size) * 0.12f;
                    float prevD = p.Count >= 2 ? prev.Base - p[^2].Base : d;
                    bool okRhythm = Math.Abs(d - prevD) <= l.Size * 0.3f;
                    float firstX = p[0].X0, bodyX = p.Count >= 2 ? p[1].X0 : l.X0;
                    bool okLeft = Math.Abs(l.X0 - bodyX) <= 2.5f && (p.Count >= 2 || (l.X0 <= firstX + 1f && firstX - l.X0 <= l.Size * 4f));
                    // bukti pemenggalan: kata pertama baris ini tidak muat lagi di sisa baris sebelumnya
                    // baris sebelumnya minimal dua kata (baris satu kata = entri daftar/tabel, bukan paragraf terpotong)
                    bool wraps = prev.X1 + l.Size * 0.25f + FirstWordW(l) > textRight - 1f && FirstWordW(prev) < prev.X1 - prev.X0 - 1f;
                    if (okGap && okSize && okRhythm && Full(prev) && wraps && okLeft && prev.Segs.Count == 1 && l.Segs.Count == 1) { p.Add(l); continue; }
                }
                paras.Add(new List<Line> { l });
            }
            bool first = true;
            foreach (var ls in paras)
            {
                int n = ls.Count;
                float lead = n >= 2 ? (ls[n - 1].Base - ls[0].Base) / (n - 1) : ls[0].Size * 1.17f;
                float lineH = Math.Max(lead, ls.Max(l => l.Size));
                float top = ls[0].Base - lineH * 0.8f;
                float before = Math.Max(0f, top - cursor);
                string align = "left";
                float indL = ls.Min(l => l.X0) - left;
                float firstInd = ls[0].X0 - (n >= 2 ? ls[1].X0 : ls[0].X0);
                var tabs = new List<(string Kind, float Pos)>();
                if (n == 1)
                {
                    var l = ls[0];
                    if (l.Segs.Count == 1 && Centered(l)) { align = "center"; indL = 0; firstInd = 0; }
                    else if (l.Segs.Count == 1 && Math.Abs(l.X1 - right) <= Math.Max(3f, l.Size * 0.5f) && l.X0 > left + width * 0.25f) { align = "right"; indL = 0; firstInd = 0; }
                    else if (l.Segs.Count > 1)
                    {
                        indL = l.X0 - left;
                        foreach (var sg in l.Segs.Skip(1))
                        {
                            float sx0 = sg.Min(g => g.X), sx1 = sg.Max(g => g.X + g.W);
                            tabs.Add(Math.Abs(sx1 - right) <= l.Size * 0.6f ? ("right", right - left) : ("left", sx0 - left));
                        }
                    }
                }
                else
                {
                    if (ls.Take(n - 1).All(l => Math.Abs(l.X1 - textRight) <= Math.Max(2.5f, l.Size * 0.35f))) align = "both";
                    if (ls.All(Centered)) { align = "center"; indL = 0; firstInd = 0; }
                }
                var runs = new StringBuilder();
                for (int k = 0; k < n; k++)
                {
                    var l = ls[k];
                    if (k > 0)
                    {
                        // sambung baris: spasi, kecuali pemenggalan "-" di ujung
                        var prevTxt = ls[k - 1].Glyphs.LastOrDefault(g => !Blank(g))?.Text ?? "";
                        if (!prevTxt.EndsWith('-') && !prevTxt.EndsWith('­')) runs.Append(RunXml(" ", l.Glyphs[0]));
                    }
                    for (int si = 0; si < l.Segs.Count; si++)
                    {
                        if (si > 0) runs.Append("<w:r><w:tab/></w:r>");
                        runs.Append(RunsOf(l.Segs[si], l.Size, l.Base));
                    }
                }
                string anc = "";
                if (first && anchors.Length > 0) { anc = anchors.ToString(); anchors.Clear(); }
                string? tabXml = tabs.Count == 0 ? null : "<w:tabs>" + string.Concat(tabs.Select(t => $"<w:tab w:val=\"{t.Kind}\" w:pos=\"{Tw(t.Pos)}\"/>")) + "</w:tabs>";
                sink.Append("<w:p>").Append(PPr(Tw(Math.Max(0f, indL)), Tw(firstInd), align, Tw(before), Tw(lineH), 0, 0, tabXml)).Append(anc).Append(runs).Append("</w:p>");
                cursor = ls[^1].Base + lineH * 0.2f;
                first = false;
            }
            return cursor;
        }

        /// <summary>Lebar kata pertama baris (sampai spasi / celah antar-kata pertama).</summary>
        private static float FirstWordW(Line l)
        {
            float start = float.NaN, end = float.NaN;
            foreach (var g in l.Glyphs)
            {
                if (Blank(g)) { if (!float.IsNaN(start)) break; continue; }
                if (!float.IsNaN(end) && g.X - end > g.Size * 0.17f) break;
                if (float.IsNaN(start)) start = g.X;
                end = g.X + g.W;
            }
            return float.IsNaN(start) ? 0f : end - start;
        }

        /// <summary>Run per kelompok glif berformat sama; spasi disisipkan pada celah antar-kata.</summary>
        private string RunsOf(List<PdfLayout.Glyph> gs, float size, float bas)
        {
            var sb = new StringBuilder();
            PdfLayout.Glyph? cur = null;
            var text = new StringBuilder();
            float lastX = float.NaN;
            int Va(PdfLayout.Glyph g) => g.Size < size * 0.88f && g.Y < bas - size * 0.18f ? 1 : g.Size < size * 0.88f && g.Y > bas + size * 0.12f ? -1 : 0;
            void Flush() { if (cur != null && text.Length > 0) sb.Append(RunXml(text.ToString(), cur, Va(cur), _underlined.Contains(cur), _struck.Contains(cur))); text.Clear(); }
            bool Same(PdfLayout.Glyph a, PdfLayout.Glyph b) => a.Font.Family == b.Font.Family && a.Font.Bold == b.Font.Bold && a.Font.Italic == b.Font.Italic &&
                a.Color == b.Color && Math.Abs(a.Size - b.Size) < 0.6f && Va(a) == Va(b) && _underlined.Contains(a) == _underlined.Contains(b) && _struck.Contains(a) == _struck.Contains(b);
            foreach (var g in gs)
            {
                float gap = float.IsNaN(lastX) ? 0f : g.X - lastX;
                bool needSpace = !float.IsNaN(lastX) && gap > g.Size * 0.17f && !(text.Length > 0 && text[^1] == ' ') && g.Text != " ";
                if (cur != null && !Same(cur, g))
                {
                    // spasi ikut format sebelumnya, kecuali garis bawahnya beda (spasi di antara kata bergaris bawah tetap bergaris)
                    if (needSpace && _underlined.Contains(cur) == _underlined.Contains(g)) { text.Append(' '); needSpace = false; }
                    Flush();
                    cur = g;
                }
                cur ??= g;
                if (needSpace) text.Append(' ');
                if (g.Text == " " && text.Length > 0 && text[^1] == ' ') { lastX = g.X + g.W; continue; }
                text.Append(g.Text);
                lastX = g.X + g.W;
            }
            Flush();
            return sb.ToString();
        }

        private static string Hex(int c) => (c & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

        private static string RunXml(string t, PdfLayout.Glyph g, int vert = 0, bool under = false, bool strike = false)
        {
            var f = g.Font;
            // Word mengecilkan pangkat/indeks ±65%: ukuran disetel agar tampak sama dengan PDF
            float pt = vert != 0 ? g.Size / 0.65f : g.Size;
            int sz = Math.Clamp(RoundI(pt * 2), 2, 3276);
            string col = Hex(g.Color), fam = Esc(f.Family);
            var rp = new StringBuilder("<w:rPr><w:rFonts w:ascii=\"").Append(fam).Append("\" w:hAnsi=\"").Append(fam).Append("\" w:cs=\"").Append(fam).Append("\" w:eastAsia=\"").Append(fam).Append("\"/>");
            if (f.Bold) rp.Append("<w:b/><w:bCs/>");
            if (f.Italic) rp.Append("<w:i/><w:iCs/>");
            if (strike) rp.Append("<w:strike/>");
            if (col != "000000") rp.Append("<w:color w:val=\"").Append(col).Append("\"/>");
            rp.Append("<w:sz w:val=\"").Append(sz).Append("\"/><w:szCs w:val=\"").Append(sz).Append("\"/>");
            if (under) rp.Append("<w:u w:val=\"single\"/>");
            if (vert != 0) rp.Append("<w:vertAlign w:val=\"").Append(vert > 0 ? "superscript" : "subscript").Append("\"/>");
            rp.Append("</w:rPr>");
            return "<w:r>" + rp + "<w:t xml:space=\"preserve\">" + Esc(t) + "</w:t></w:r>";
        }

        /// <summary>Paragraf pemberi jarak / penampung: tinggi h pt lewat spasi-sebelum, tanda paragraf 1 pt.</summary>
        private static string Spacer(float h, string inner = "", bool pageBreak = false) =>
            "<w:p><w:pPr>" + (pageBreak ? "<w:pageBreakBefore/>" : "") + "<w:spacing w:before=\"" + Math.Max(0, Tw(h) - 20) +
            "\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/><w:rPr><w:sz w:val=\"2\"/><w:szCs w:val=\"2\"/></w:rPr></w:pPr>" + inner + "</w:p>";

        private static string PPr(int ind, int first, string? align, int before, int line, int after, int indR, string? tabs = null)
        {
            var sb = new StringBuilder("<w:pPr>");
            if (tabs != null) sb.Append(tabs);
            sb.Append("<w:spacing w:before=\"").Append(Math.Max(0, before)).Append("\" w:after=\"").Append(Math.Max(0, after)).Append('"');
            if (line != 0) sb.Append(" w:line=\"").Append(Math.Abs(line)).Append("\" w:lineRule=\"exact\"");
            sb.Append("/>");
            if (ind != 0 || first != 0 || indR != 0)
            {
                sb.Append("<w:ind w:left=\"").Append(ind).Append("\" w:right=\"").Append(indR).Append('"');
                if (first > 0) sb.Append(" w:firstLine=\"").Append(first).Append('"');
                else if (first < 0) sb.Append(" w:hanging=\"").Append(-first).Append('"');
                sb.Append("/>");
            }
            if (align != null && align != "left") sb.Append("<w:jc w:val=\"").Append(align).Append("\"/>");
            return sb.Append("</w:pPr>").ToString();
        }

        // ------------------------------------------------------------------ foto

        private float ImagePara(PdfLayout.Image im, float left, float cursor, StringBuilder anchors, StringBuilder sink)
        {
            float w = im.X1 - im.X0, h = im.Y1 - im.Y0;
            float before = Math.Max(0f, im.Y0 - cursor);
            string anc = "";
            if (anchors.Length > 0) { anc = anchors.ToString(); anchors.Clear(); }
            float ind = Math.Max(0f, im.X0 - left);
            sink.Append("<w:p>").Append(PPr(Tw(ind), 0, null, Tw(before), 0, 0, 0)).Append(anc).Append("<w:r>").Append(InlinePic(im, w, h)).Append("</w:r></w:p>");
            return im.Y1;
        }

        private readonly Dictionary<object, string> _mediaByKey = new(ReferenceEqualityComparer.Instance);

        private string? AddMedia(PdfLayout.Image im)
        {
            // gambar yang sama (logo / kop berulang di tiap halaman) cukup disimpan sekali
            if (im.Key != null && _mediaByKey.TryGetValue(im.Key, out var known)) return known;
            byte[] data; string ext;
            if (im.Jpeg != null) { data = im.Jpeg; ext = "jpeg"; }
            else if (im.Png != null) { data = im.Png; ext = "png"; }
            else return null;
            _media.Add(($"image{_media.Count + 1}.{ext}", data));
            var rid = "rIdImg" + _media.Count;
            if (im.Key != null) _mediaByKey[im.Key] = rid;
            return rid;
        }

        private string InlinePic(PdfLayout.Image im, float w, float h)
        {
            var rid = AddMedia(im);
            if (rid == null) return "";
            int id = _shapeId++;
            long cx = Emu(w), cy = Emu(h);
            return $"<w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{id}\" name=\"Gambar {id}\"/>" +
                "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                $"<pic:pic><pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"Gambar {id}\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>";
        }

        // ------------------------------------------------------------------ tabel

        private float Table(Tbl t, float left, float right, float cursor, StringBuilder sink)
        {
            float before = Math.Max(0f, t.Ys[0] - cursor);
            if (before > 0.5f) sink.Append(Spacer(before));
            // gabung sel: menyatu ke kanan bila tidak ada garis tegak di antaranya, ke bawah bila tak ada garis datar
            int rows = t.Rows, cols = t.Cols;
            var owner = new int[rows, cols];
            for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) owner[r, c] = -1;
            var spans = new List<int[]>();   // r, c, rs, cs
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    if (owner[r, c] >= 0) continue;
                    int cs = 1;
                    while (c + cs < cols && owner[r, c + cs] < 0 && t.VRule(c + cs, r) == null) cs++;
                    int rs = 1;
                    while (r + rs < rows && Enumerable.Range(c, cs).All(x => owner[r + rs, x] < 0 && t.HRule(r + rs, x) == null)) rs++;
                    int id = spans.Count;
                    spans.Add(new[] { r, c, rs, cs });
                    for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++) owner[rr, cc] = id;
                }
            float w = t.Xs[^1] - t.Xs[0];
            bool centered = Math.Abs((t.Xs[0] + t.Xs[^1]) / 2 - (left + right) / 2) <= 3f && t.Xs[0] - left > 6f;
            sink.Append("<w:tbl><w:tblPr><w:tblW w:w=\"").Append(Tw(w)).Append("\" w:type=\"dxa\"/>")
                .Append(centered ? "<w:jc w:val=\"center\"/>" : $"<w:tblInd w:w=\"{Tw(t.Xs[0] - left)}\" w:type=\"dxa\"/>");
            // semua batas bergaris sama → garis tingkat tabel; pengecualian ditangani per sel
            var any = t.Rules.FirstOrDefault();
            bool uniform = any != null && t.Rules.All(r => Math.Abs(r.W - any.W) < 0.3f && r.Color == any.Color);
            sink.Append("<w:tblBorders>").Append(uniform ? string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(s => Border(s, any))) : NoBorders())
                .Append("</w:tblBorders><w:tblLayout w:type=\"fixed\"/>").Append(ZeroMar).Append("<w:tblLook w:val=\"0000\"/></w:tblPr><w:tblGrid>");
            for (int c = 0; c < cols; c++) sink.Append("<w:gridCol w:w=\"").Append(Tw(t.Xs[c + 1] - t.Xs[c])).Append("\"/>");
            sink.Append("</w:tblGrid>");
            for (int r = 0; r < rows; r++)
            {
                sink.Append("<w:tr><w:trPr><w:trHeight w:val=\"").Append(Tw(t.Ys[r + 1] - t.Ys[r])).Append("\" w:hRule=\"atLeast\"/><w:cantSplit/></w:trPr>");
                int c = 0;
                while (c < cols)
                {
                    var sp = spans[owner[r, c]];
                    // sel gabungan yang dimulai di kolom lain (bentuk L) — tulis sel tunggal agar kisi tetap utuh
                    int cs = sp[1] == c ? sp[3] : 1;
                    float cw = t.Xs[c + cs] - t.Xs[c];
                    bool startRow = sp[0] == r && sp[1] == c;
                    sink.Append("<w:tc><w:tcPr><w:tcW w:w=\"").Append(Tw(cw)).Append("\" w:type=\"dxa\"/>");
                    if (cs > 1) sink.Append("<w:gridSpan w:val=\"").Append(cs).Append("\"/>");
                    if (sp[2] > 1 && sp[1] == c) sink.Append(sp[0] == r ? "<w:vMerge w:val=\"restart\"/>" : "<w:vMerge/>");
                    int rb = sp[0] + sp[2];
                    sink.Append("<w:tcBorders>")
                        .Append(Border("top", t.HRule(r, c)))
                        .Append(Border("left", t.VRule(c, r)))
                        .Append(Border("bottom", r + 1 == rb ? t.HRule(r + 1, c) : null))
                        .Append(Border("right", t.VRule(c + cs, r)))
                        .Append("</w:tcBorders>");
                    if (t.Shade.TryGetValue(Tbl.Key(sp[0], sp[1]), out var shd)) sink.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"").Append(Hex(shd)).Append("\"/>");
                    var gl = new List<PdfLayout.Glyph>();
                    if (startRow)
                        for (int rr = sp[0]; rr < sp[0] + sp[2]; rr++)
                            for (int cc = sp[1]; cc < sp[1] + sp[3]; cc++)
                                if (t.CellText.TryGetValue(Tbl.Key(rr, cc), out var lst)) gl.AddRange(lst);
                    var ls = startRow ? BuildLines(gl) : new List<Line>();
                    // isi di tengah / bawah sel (umum di sel gabungan) → rata tengah/bawah
                    float spanTop = t.Ys[sp[0]], spanBot = t.Ys[sp[0] + sp[2]];
                    string vAlign = "top";
                    if (ls.Count > 0)
                    {
                        float gapTop = ls[0].Top - spanTop, gapBot = spanBot - ls[^1].Bot;
                        if (Math.Abs(gapTop - gapBot) <= Math.Max(2.5f, (spanBot - spanTop) * 0.12f) && gapTop > 3f) vAlign = "center";
                        else if (gapBot < gapTop * 0.4f && gapTop > 6f) vAlign = "bottom";
                    }
                    sink.Append("<w:vAlign w:val=\"").Append(vAlign).Append("\"/></w:tcPr>");
                    if (startRow)
                    {
                        var cell = new StringBuilder();
                        if (ls.Count > 0) Paragraphs(ls, t.Xs[c] + 0.5f, t.Xs[c + cs] - 0.5f, vAlign == "top" ? t.Ys[r] : ls[0].Top, new StringBuilder(), cell);
                        if (cell.Length == 0) cell.Append(Spacer(1f));
                        sink.Append(cell);
                    }
                    else sink.Append(Spacer(1f));
                    sink.Append("</w:tc>");
                    c += cs;
                }
                sink.Append("</w:tr>");
            }
            sink.Append("</w:tbl>");
            return t.Ys[^1];
        }

        private static string Border(string side, Rule? r) => r == null
            ? $"<w:{side} w:val=\"nil\"/>"
            : $"<w:{side} w:val=\"single\" w:sz=\"{Math.Clamp(RoundI(r.W * 8), 2, 96)}\" w:space=\"0\" w:color=\"{Hex(r.Color)}\"/>";

        private static string NoBorders() => string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(s => $"<w:{s} w:val=\"nil\"/>"));

        // ------------------------------------------------------------------ bentuk tertambat

        private string ShapeXml(PdfLayout.Shape s, bool behind)
        {
            var b = s.BBox;
            float w = b[2] - b[0], h = b[3] - b[1];
            if (w < 0.3f && h < 0.3f) return "";
            float lw = s.Stroke != PdfLayout.NONE ? s.LineW : 0f;
            w = Math.Max(w, 0.5f); h = Math.Max(h, 0.5f);
            string geom;
            float rr;
            if (s.IsRect) geom = "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>";
            else if (IsEllipse(s)) geom = "<a:prstGeom prst=\"ellipse\"><a:avLst/></a:prstGeom>";
            else if ((rr = RoundRadius(s)) > 0f) geom = $"<a:prstGeom prst=\"roundRect\"><a:avLst><a:gd name=\"adj\" fmla=\"val {Math.Clamp(RoundI(rr / Math.Min(w, h) * 100000), 0, 50000)}\"/></a:avLst></a:prstGeom>";
            else
            {
                long W = Emu(w), H = Emu(h);
                var p = new StringBuilder($"<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"{W}\" h=\"{H}\"{(s.Fill == PdfLayout.NONE ? " fill=\"none\"" : "")}>");
                for (int k = 0; k < s.Subs.Count; k++)
                {
                    var sub = s.Subs[k];
                    for (int i = 0; i + 1 < sub.Length; i += 2)
                    {
                        long x = Emu(sub[i] - b[0]), y = Emu(sub[i + 1] - b[1]);
                        p.Append(i == 0 ? $"<a:moveTo><a:pt x=\"{x}\" y=\"{y}\"/></a:moveTo>" : $"<a:lnTo><a:pt x=\"{x}\" y=\"{y}\"/></a:lnTo>");
                    }
                    if (k < s.Closed.Count && s.Closed[k]) p.Append("<a:close/>");
                }
                p.Append("</a:path></a:pathLst></a:custGeom>");
                geom = p.ToString();
            }
            string fill = s.Fill == PdfLayout.NONE ? "<a:noFill/>" : $"<a:solidFill><a:srgbClr val=\"{Hex(s.Fill)}\"/></a:solidFill>";
            string ln = s.Stroke == PdfLayout.NONE ? "<a:ln><a:noFill/></a:ln>" : $"<a:ln w=\"{Emu(lw)}\"><a:solidFill><a:srgbClr val=\"{Hex(s.Stroke)}\"/></a:solidFill></a:ln>";
            return Anchor(b[0], b[1], w, h, geom + fill + ln, behind);
        }

        private static bool IsEllipse(PdfLayout.Shape s)
        {
            if (s.Subs.Count != 1) return false;
            var sub = s.Subs[0]; var b = s.BBox;
            float cx = (b[0] + b[2]) / 2, cy = (b[1] + b[3]) / 2, rx = (b[2] - b[0]) / 2, ry = (b[3] - b[1]) / 2;
            if (rx < 1 || ry < 1 || sub.Length < 24) return false;
            for (int i = 0; i + 1 < sub.Length; i += 2)
            {
                float dx = (sub[i] - cx) / rx, dy = (sub[i + 1] - cy) / ry;
                if (Math.Abs(dx * dx + dy * dy - 1f) > 0.12f) return false;
            }
            return true;
        }

        /// <summary>Kotak bersudut bulat: jari-jari sudut (pt), 0 bila bukan. Semua titik jalur harus di garis luarnya.</summary>
        private static float RoundRadius(PdfLayout.Shape s)
        {
            if (s.Subs.Count != 1 || s.IsRect) return 0f;
            var sub = s.Subs[0]; var b = s.BBox;
            if (sub.Length < 16) return 0f;
            float topMin = float.MaxValue;
            for (int i = 0; i + 1 < sub.Length; i += 2) if (Math.Abs(sub[i + 1] - b[1]) <= 0.05f) topMin = Math.Min(topMin, sub[i]);
            float r = topMin - b[0];
            if (r <= 0.8f || r > Math.Min(b[2] - b[0], b[3] - b[1]) / 2f + 0.6f) return 0f;
            for (int i = 0; i + 1 < sub.Length; i += 2)
            {
                float qx = Math.Clamp(sub[i], b[0] + r, Math.Max(b[0] + r, b[2] - r)), qy = Math.Clamp(sub[i + 1], b[1] + r, Math.Max(b[1] + r, b[3] - r));
                float d = MathF.Sqrt((sub[i] - qx) * (sub[i] - qx) + (sub[i + 1] - qy) * (sub[i + 1] - qy));
                if (Math.Abs(d - r) > Math.Max(0.8f, r * 0.08f)) return 0f;
            }
            return r;
        }

        /// <summary>Teks miring/tegak (diputar) → kotak teks Word diputar dengan sudut yang sama, di posisi persis halaman.</summary>
        private void RotatedText(List<PdfLayout.Glyph> gs, StringBuilder anchors)
        {
            if (gs.Count == 0) return;
            var groups = new List<List<PdfLayout.Glyph>>();
            foreach (var g in gs)
            {
                double th = g.Angle * Math.PI / 180; float nx = -(float)Math.Sin(th), ny = (float)Math.Cos(th);
                var grp = groups.FirstOrDefault(l => Math.Abs(l[0].Angle - g.Angle) < 2f && Math.Abs((g.X - l[0].X) * nx + (g.Y - l[0].Y) * ny) < l[0].Size * 0.4f);
                if (grp != null) grp.Add(g); else groups.Add(new() { g });
            }
            foreach (var l in groups)
            {
                var f = l[0];
                double th = f.Angle * Math.PI / 180; float dx = (float)Math.Cos(th), dy = (float)Math.Sin(th);
                var s = l.OrderBy(g => g.X * dx + g.Y * dy).ToList();
                var a = s[0]; var z = s[^1];
                float len = (z.X - a.X) * dx + (z.Y - a.Y) * dy + z.W;
                float size = s.Max(g => g.Size);
                float w = len + size * 0.6f, h = size * 1.35f;
                float ux = -(float)Math.Sin(th), uy = (float)Math.Cos(th);
                float cx = a.X + dx * len / 2 - ux * size * 0.3f, cy = a.Y + dy * len / 2 - uy * size * 0.3f;
                var sb = new StringBuilder(); float lastP = float.NaN;
                foreach (var g in s)
                {
                    float p = g.X * dx + g.Y * dy;
                    if (!float.IsNaN(lastP) && p - lastP > g.Size * 0.17f && !(sb.Length > 0 && sb[^1] == ' ')) sb.Append(' ');
                    sb.Append(g.Text); lastP = p + g.W;
                }
                var txt = sb.ToString().Trim();
                if (txt.Length == 0) continue;
                var runXml = RunXml(txt, f);
                var wsp = $"<wps:cNvSpPr txBox=\"1\"/><wps:spPr><a:xfrm rot=\"{RoundI(f.Angle * 60000)}\"><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>" +
                    "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></wps:spPr>" +
                    $"<wps:txbx><w:txbxContent><w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/></w:pPr>{runXml}</w:p></w:txbxContent></wps:txbx>" +
                    "<wps:bodyPr rot=\"0\" wrap=\"none\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"ctr\"><a:noAutofit/></wps:bodyPr>";
                anchors.Append(Anchor(cx - w / 2, cy - h / 2, w, h, "", false, wsp));
            }
        }

        private string RuleXml(Rule r)
        {
            float x, y, w, h;
            if (r.H) { x = r.A; y = r.Pos - r.W / 2; w = r.B - r.A; h = r.W; }
            else { x = r.Pos - r.W / 2; y = r.A; w = r.W; h = r.B - r.A; }
            string fill = $"<a:solidFill><a:srgbClr val=\"{Hex(r.Color)}\"/></a:solidFill>";
            return Anchor(x, y, Math.Max(w, 0.3f), Math.Max(h, 0.3f), "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>" + fill + "<a:ln><a:noFill/></a:ln>", true);
        }

        private string Anchor(float x, float y, float w, float h, string spPr, bool behind, string? wsp = null)
        {
            int id = _shapeId++;
            long relH = behind ? 1 : 251658240L + id;
            return $"<w:r><w:rPr><w:noProof/><w:sz w:val=\"2\"/></w:rPr><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{relH}\" behindDoc=\"{(behind ? 1 : 0)}\" locked=\"0\" layoutInCell=\"1\" allowOverlap=\"1\">" +
                $"<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"page\"><wp:posOffset>{Emu(x)}</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"page\"><wp:posOffset>{Emu(y)}</wp:posOffset></wp:positionV>" +
                $"<wp:extent cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"{id}\" name=\"Bentuk {id}\"/><wp:cNvGraphicFramePr/>" +
                "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp>" +
                (wsp ?? $"<wps:cNvSpPr/><wps:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{Emu(w)}\" cy=\"{Emu(h)}\"/></a:xfrm>{spPr}</wps:spPr><wps:bodyPr/>") +
                "</wps:wsp></a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice></mc:AlternateContent></w:r>";
        }

        // ------------------------------------------------------------------ halaman pindaian (OCR)

        private void ScanPage(Plan pl, Sect sect)
        {
            float textW = pl.W - sect.Ml - sect.Mr;
            if (pl.Scan == null || pl.Scan.Count == 0)
            {
                if (pl.ScanImg is { } si)
                {
                    float s = Math.Min(textW / si.W, (pl.H - sect.Mt - sect.Mb) / si.H);
                    var im = new PdfLayout.Image(0, 0, si.W * s, si.H * s, si.Jpeg, null, si.W, si.H);
                    _body.Append("<w:p>").Append(PPr(0, 0, "center", 0, 0, 0, 0)).Append("<w:r>").Append(InlinePic(im, si.W * s, si.H * s)).Append("</w:r></w:p>");
                    ImagePages++;
                }
                else _body.Append("<w:p/>");
                return;
            }
            OcrPages++;
            foreach (var p in pl.Scan)
                _body.Append("<w:p>").Append(PPr(0, 0, null, 0, 0, 120, 0)).Append("<w:r><w:t xml:space=\"preserve\">").Append(Esc(p)).Append("</w:t></w:r></w:p>");
        }

        // ------------------------------------------------------------------ section & paket

        private sealed record Sect(float W, float H, float Ml, float Mr, float Mt, float Mb);

        private static string SectXml(Sect s) =>
            $"<w:sectPr><w:pgSz w:w=\"{Tw(s.W)}\" w:h=\"{Tw(s.H)}\"{(s.W > s.H ? " w:orient=\"landscape\"" : "")}/>" +
            $"<w:pgMar w:top=\"{Tw(s.Mt)}\" w:right=\"{Tw(s.Mr)}\" w:bottom=\"{Tw(s.Mb)}\" w:left=\"{Tw(s.Ml)}\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/><w:cols w:space=\"720\"/></w:sectPr>";

        private void Finish()
        {
            const string ns = "xmlns:wpc=\"http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
                "xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\" xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
                "xmlns:wp14=\"http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
                "xmlns:w10=\"urn:schemas-microsoft-com:office:word\" xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
                "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" mc:Ignorable=\"w14 wp14\"";
            const string X = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
            var doc = new StringBuilder(X).Append("<w:document ").Append(ns).Append("><w:body>").Append(_body)
                .Append(_finalSect.Length > 0 ? _finalSect : SectXml(new Sect(595f, 842f, 72f, 72f, 72f, 72f))).Append("</w:body></w:document>");
            var rels = new StringBuilder(X).Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">")
                .Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>")
                .Append("<Relationship Id=\"rIdSettings\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
            for (int i = 0; i < _media.Count; i++) rels.Append($"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{_media[i].Name}\"/>");
            rels.Append("</Relationships>");
            string ct = X + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/settings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml\"/>" +
                "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";
            string root = X + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>";
            string styles = X + "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\" w:eastAsia=\"Arial\" w:cs=\"Arial\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"id-ID\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
                "<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/>" + ZeroMar + "</w:tblPr></w:style></w:styles>";
            string settings = X + "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:defaultTabStop w:val=\"720\"/>" +
                "<w:characterSpacingControl w:val=\"doNotCompress\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>";
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string core = X + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                $"<dc:creator>Depup Office</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created></cp:coreProperties>";
            using var fs = new FileStream(_out, FileMode.Create, FileAccess.Write);
            using var z = new ZipArchive(fs, ZipArchiveMode.Create);
            void Put(string n, byte[] b) { var e = z.CreateEntry(n, n.StartsWith("word/media/") && !n.EndsWith(".png") ? CompressionLevel.NoCompression : CompressionLevel.Optimal); using var s = e.Open(); s.Write(b, 0, b.Length); }
            var utf8 = new UTF8Encoding(false);
            Put("[Content_Types].xml", utf8.GetBytes(ct));
            Put("_rels/.rels", utf8.GetBytes(root));
            Put("docProps/core.xml", utf8.GetBytes(core));
            Put("word/document.xml", utf8.GetBytes(doc.ToString()));
            Put("word/styles.xml", utf8.GetBytes(styles));
            Put("word/settings.xml", utf8.GetBytes(settings));
            Put("word/_rels/document.xml.rels", utf8.GetBytes(rels.ToString()));
            foreach (var (n, b) in _media) Put("word/media/" + n, b);
        }

        // ------------------------------------------------------------------ util

        private static int Tw(float pt) => RoundI(pt * 20f);
        private static long Emu(float pt) => Math.Max(1L, (long)(pt * 12700f));

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (ch < 0x20 && ch != '\t') break;
                        if (ch >= 0xFFFE) break;
                        // surrogate yatim membuat XML tidak sah
                        if (char.IsHighSurrogate(ch)) { if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch).Append(s[++i]); } break; }
                        if (char.IsLowSurrogate(ch)) break;
                        sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }
}
