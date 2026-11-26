using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Tata letak lembar kerja untuk dicetak — port 1:1 <c>SheetLayout.kt</c> (Android 4.3). Semua baris &amp; kolom berisi ikut:
    /// lebar kolom mengikuti isi (26–190 pt, teks panjang dibungkus maks. 8 baris); A4 tegak bila muat, sedikit lebih lebar →
    /// tegak diperkecil, lebih lebar → mendatar (diperkecil sampai 75%), masih tidak muat → kolom dibagi ke beberapa halaman;
    /// baris judul kolom (baris pertama yang tebal) diulang di tiap halaman lanjutan. Satuan pt pada skala 100%.
    /// </summary>
    public static class SheetLayout
    {
        public sealed record Cell(string Text, bool Bold = false, bool Num = false, Color Fill = default);
        public sealed record Grid(string Name, IReadOnlyList<IReadOnlyList<Cell>> Rows);

        public delegate float Measure(string text, bool bold);

        public const float FONT = 9f, LINE = 11.5f, PAD_X = 5f, PAD_Y = 4f, MIN_W = 26f, MAX_W = 190f;
        public const int MAX_LINES = 8;
        public const float MARGIN = 36f, TITLE_H = 36f, BAND_H = 16f, FOOT_H = 22f, A4_W = 595f, A4_H = 842f, MIN_SCALE = 0.75f;

        public sealed record Page(int Grid, bool Landscape, float Scale, IReadOnlyList<int> Cols, IReadOnlyList<int> Rows, bool Title, int Band, int Bands);

        public sealed record Plan(IReadOnlyList<Page> Pages, IReadOnlyList<float[]> ColW, IReadOnlyList<float[]> RowH,
            IReadOnlyList<List<List<List<string>>>> Lines, IReadOnlyList<int?> Header);

        private const string ELLIPSIS = "…";

        /// <summary>Bungkus per kata ke lebar <paramref name="w"/>; kata terlalu panjang dipotong per huruf; maks. 8 baris.</summary>
        public static List<string> Wrap(string text, float w, bool bold, Measure m)
        {
            var output = new List<string>();
            if (text.Length == 0) return output;
            foreach (var para in text.Replace("\r", "").Split('\n'))
            {
                if (para.Length == 0) { output.Add(""); continue; }
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    var cand = line.Length == 0 ? word : line + " " + word;
                    if (m(cand, bold) <= w) { line = cand; continue; }
                    if (line.Length > 0) { output.Add(line); line = ""; }
                    var rest = word;
                    while (rest.Length > 0 && m(rest, bold) > w)
                    {
                        int k = rest.Length - 1;
                        while (k > 1 && m(rest[..k], bold) > w) k--;
                        output.Add(rest[..k]); rest = rest[k..];
                    }
                    line = rest;
                }
                output.Add(line);
            }
            if (output.Count <= MAX_LINES) return output;
            var keep = output.Take(MAX_LINES).ToList();
            var last = keep[MAX_LINES - 1];
            while (last.Length > 0 && m(last + ELLIPSIS, bold) > w) last = last[..^1];
            keep[MAX_LINES - 1] = last + ELLIPSIS;
            return keep;
        }

        /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
        public static string ColName(int i)
        {
            int n = i + 1;
            var sb = new System.Text.StringBuilder();
            while (n > 0) { int r = (n - 1) % 26; sb.Insert(0, (char)('A' + r)); n = (n - 1) / 26; }
            return sb.ToString();
        }

        public static Plan MakePlan(IReadOnlyList<Grid> grids, Measure m)
        {
            var pages = new List<Page>();
            var colWs = new List<float[]>(); var rowHs = new List<float[]>();
            var linesAll = new List<List<List<List<string>>>>(); var headers = new List<int?>();
            for (int gi = 0; gi < grids.Count; gi++)
            {
                var g = grids[gi];
                int lastRow = -1, lastCol = -1, firstRow = -1;
                for (int r = 0; r < g.Rows.Count; r++)
                    for (int c = 0; c < g.Rows[r].Count; c++)
                        if (!string.IsNullOrWhiteSpace(g.Rows[r][c].Text))
                        {
                            if (firstRow < 0) firstRow = r;
                            lastRow = r; lastCol = Math.Max(lastCol, c);
                        }
                int nCols = lastCol + 1;
                Cell? At(int r, int c) => c < g.Rows[r].Count ? g.Rows[r][c] : null;
                var colW = new float[Math.Max(nCols, 0)];
                for (int c = 0; c < nCols; c++)
                {
                    float widest = 0;
                    for (int r = 0; r <= lastRow; r++)
                    {
                        var cell = At(r, c);
                        if (cell == null) continue;
                        foreach (var p in cell.Text.Split('\n')) widest = Math.Max(widest, m(p, cell.Bold));
                    }
                    colW[c] = Math.Clamp(widest + 2 * PAD_X, MIN_W, MAX_W);
                }
                var lines = new List<List<List<string>>>();
                for (int r = 0; r <= lastRow; r++)
                {
                    var row = new List<List<string>>();
                    for (int c = 0; c < nCols; c++)
                    {
                        var cell = At(r, c);
                        row.Add(cell == null || string.IsNullOrWhiteSpace(cell.Text) ? new List<string>() : Wrap(cell.Text, colW[c] - 2 * PAD_X, cell.Bold, m));
                    }
                    lines.Add(row);
                }
                var rowH = new float[Math.Max(lastRow + 1, 0)];
                for (int r = 0; r <= lastRow; r++) rowH[r] = Math.Max(1, lines[r].Count == 0 ? 1 : lines[r].Max(x => x.Count)) * LINE + 2 * PAD_Y;
                // baris judul kolom: baris berisi pertama yang semua isinya tebal (dan masih ada baris sesudahnya)
                int? header = null;
                if (firstRow >= 0 && firstRow < lastRow)
                {
                    var filled = g.Rows[firstRow].Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
                    if (filled.Count > 0 && filled.All(x => x.Bold)) header = firstRow;
                }
                colWs.Add(colW); rowHs.Add(rowH); linesAll.Add(lines); headers.Add(header);

                float total = colW.Sum();
                float availP = A4_W - 2 * MARGIN, availL = A4_H - 2 * MARGIN;
                (bool land, float scale) = total <= availP ? (false, 1f)
                    : total <= availP / 0.9f ? (false, availP / total)
                    : total <= availL ? (true, 1f)
                    : total * MIN_SCALE <= availL ? (true, availL / total)
                    : (true, MIN_SCALE);
                float width = (land ? availL : availP) / scale;
                float height = ((land ? A4_W : A4_H) - 2 * MARGIN - FOOT_H) / scale;
                var bands = new List<List<int>>();
                var cur = new List<int>(); float used = 0;
                for (int c = 0; c < nCols; c++)
                {
                    if (cur.Count > 0 && used + colW[c] > width + 0.01f) { bands.Add(cur); cur = new List<int>(); used = 0; }
                    cur.Add(c); used += colW[c];
                }
                if (cur.Count > 0 || bands.Count == 0) bands.Add(cur);

                if (firstRow < 0) { pages.Add(new Page(gi, land, scale, Array.Empty<int>(), Array.Empty<int>(), true, 0, 1)); continue; }
                for (int bi = 0; bi < bands.Count; bi++)
                {
                    var cols = bands[bi];
                    var rows = new List<int>(); float y = 0; bool first = true;
                    float Avail(bool firstOfGrid) => height - (firstOfGrid ? TITLE_H : 0) - (bands.Count > 1 ? BAND_H : 0);
                    void Close()
                    {
                        pages.Add(new Page(gi, land, scale, cols, rows, first && bi == 0, bi, bands.Count));
                        first = false; rows = new List<int>(); y = 0;
                    }
                    for (int r = firstRow; r <= lastRow; r++)
                    {
                        float h = rowH[r];
                        bool isFirstPage = first && bi == 0;
                        if (rows.Count > 0 && y + h > Avail(isFirstPage) && rows.Any(x => x != header))
                        {
                            Close();
                            if (header is int hd && r != hd) { rows.Add(hd); y += rowH[hd]; }
                        }
                        rows.Add(r); y += h;
                    }
                    if (rows.Count > 0) Close();
                }
            }
            return new Plan(pages, colWs, rowHs, linesAll, headers);
        }

        /// <summary>Angka (termasuk Rp, %, pemisah ribuan) → rata kanan saat dicetak.</summary>
        public static bool LooksNumeric(string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return false;
            t = t.Replace("Rp", "", StringComparison.OrdinalIgnoreCase).Replace("%", "").Replace(" ", "").Replace(".", "").Replace(",", "");
            if (t.StartsWith("-") || t.StartsWith("+")) t = t[1..];
            return t.Length > 0 && t.All(char.IsDigit);
        }

        /// <summary>Lembar kerja string + baris tebal → grid tata letak.</summary>
        public static Grid FromRows(string name, IReadOnlyList<IReadOnlyList<string>> rows, ISet<int>? boldRows = null)
        {
            var g = new List<IReadOnlyList<Cell>>();
            for (int r = 0; r < rows.Count; r++)
            {
                bool bold = boldRows?.Contains(r) ?? false;
                g.Add(rows[r].Select(v => new Cell(v ?? "", bold, LooksNumeric(v ?? ""), bold ? Color.FromArgb(0xED, 0xED, 0xED) : default)).ToList());
            }
            // tanpa info tebal: baris pertama dianggap judul kolom bila semuanya teks (seperti tampilan editor)
            if (boldRows == null && g.Count > 1 && g[0].Any(c => !string.IsNullOrWhiteSpace(c.Text)) && g[0].All(c => !c.Num))
                g[0] = g[0].Select(c => c with { Bold = true, Fill = Color.FromArgb(0xED, 0xED, 0xED) }).ToList();
            return new Grid(name, g);
        }

        // ------------------------------------------------------------------ menggambar (padanan SheetPrint.kt)

        private static readonly Color Ink = Color.FromArgb(0x1A, 0x1A, 0x1A), MutedC = Color.FromArgb(0x6B, 0x6B, 0x6B), GridC = Color.FromArgb(0xBD, 0xBD, 0xBD);

        /// <summary>Lembar tata letak siap gambar (font &amp; pengukur di-cache).</summary>
        public sealed class Doc : IDisposable
        {
            public readonly string Title;
            public readonly IReadOnlyList<Grid> Grids;
            public readonly Plan Plan;
            private readonly Font _regular = new("Segoe UI", FONT, GraphicsUnit.World);
            private readonly Font _bold = new("Segoe UI", FONT, FontStyle.Bold, GraphicsUnit.World);
            private readonly Font _title = new("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.World);
            private readonly Font _sub = new("Segoe UI", 8.5f, GraphicsUnit.World);
            private readonly Font _foot = new("Segoe UI", 8f, GraphicsUnit.World);
            private readonly Bitmap _mbmp = new(1, 1);
            private readonly Graphics _mg;
            private readonly StringFormat _fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            private readonly string _date = DateTime.Now.ToString("d MMM yyyy", new CultureInfo("id-ID"));

            public Doc(string title, IReadOnlyList<Grid> grids)
            {
                Title = title; Grids = grids;
                _fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                _mg = Graphics.FromImage(_mbmp);
                _mg.PageUnit = GraphicsUnit.Point;
                _mg.TextRenderingHint = TextRenderingHint.AntiAlias;
                Plan = MakePlan(grids, (t, b) => Width(t, b ? _bold : _regular));
            }

            private float Width(string t, Font f) => t.Length == 0 ? 0 : _mg.MeasureString(t, f, int.MaxValue, _fmt).Width;

            public int Count => Plan.Pages.Count;

            /// <summary>Ukuran halaman (pt).</summary>
            public SizeF PageSize(int i) => Plan.Pages[i].Landscape ? new SizeF(A4_H, A4_W) : new SizeF(A4_W, A4_H);

            private string Ellipsize(string t, Font f, float w)
            {
                if (Width(t, f) <= w) return t;
                var s = t;
                while (s.Length > 0 && Width(s + "…", f) > w) s = s[..^1];
                return s + "…";
            }

            /// <summary>Gambar halaman <paramref name="pi"/> pada <paramref name="g"/>; (0,0)-(pw,ph) dalam pt sesudah transformasi pemanggil.</summary>
            public void Draw(Graphics g, int pi, bool gridlines = true)
            {
                var pg = Plan.Pages[pi];
                var size = PageSize(pi);
                float pw = size.Width, ph = size.Height;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.SmoothingMode = SmoothingMode.None;
                using var inkB = new SolidBrush(Ink);
                using var mutedB = new SolidBrush(MutedC);
                using var gridP = new Pen(GridC, 0.5f);
                var grid = Grids[pg.Grid];
                var foot = Ellipsize($"{Title} · Halaman {pi + 1} dari {Count}", _foot, pw - 2 * MARGIN);
                float fw = Width(foot, _foot);
                g.DrawString(foot, _foot, mutedB, (pw - fw) / 2f, ph - MARGIN / 2f - 8f, _fmt);
                var state = g.Save();
                g.TranslateTransform(MARGIN, MARGIN);
                g.ScaleTransform(pg.Scale, pg.Scale);
                float avail = (pw - 2 * MARGIN) / pg.Scale;
                float y = 0;
                const float asc = FONT * 1.33f;   // tinggi baris Segoe UI
                if (pg.Title)
                {
                    g.DrawString(Ellipsize(Title, _title, avail), _title, inkB, 0, 0, _fmt);
                    var sub = string.Join(" · ", new[] { Grids.Count > 1 || grid.Name != Title ? grid.Name : "", _date }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    g.DrawString(Ellipsize(sub, _sub, avail), _sub, mutedB, 0, 20, _fmt);
                    y += TITLE_H;
                }
                if (pg.Bands > 1 && pg.Cols.Count > 0)
                {
                    string a = ColName(pg.Cols[0]), b = ColName(pg.Cols[^1]);
                    g.DrawString($"Kolom {(a == b ? a : a + "–" + b)} · bagian {pg.Band + 1} dari {pg.Bands}", _sub, mutedB, 0, y + 1, _fmt);
                    y += BAND_H;
                }
                if (pg.Rows.Count == 0) g.DrawString("Lembar ini kosong.", _sub, mutedB, 0, y + 2, _fmt);
                var colW = Plan.ColW[pg.Grid]; var rowH = Plan.RowH[pg.Grid]; var lines = Plan.Lines[pg.Grid];
                var header = Plan.Header[pg.Grid];
                foreach (int r in pg.Rows)
                {
                    float h = rowH[r];
                    float x = 0;
                    foreach (int col in pg.Cols)
                    {
                        float w = colW[col];
                        var cell = col < grid.Rows[r].Count ? grid.Rows[r][col] : null;
                        if (cell != null && cell.Fill.A > 0) { using var fb = new SolidBrush(cell.Fill); g.FillRectangle(fb, x, y, w, h); }
                        if (gridlines) g.DrawRectangle(gridP, x, y, w, h);
                        var ls = lines[r][col];
                        if (cell != null && ls.Count > 0)
                        {
                            var f = cell.Bold ? _bold : _regular;
                            for (int li = 0; li < ls.Count; li++)
                            {
                                float ty = y + PAD_Y + li * LINE + (LINE - asc) / 2f;
                                float tx = cell.Num && ls.Count == 1 ? x + w - PAD_X - Width(ls[li], f) : x + PAD_X;
                                g.DrawString(ls[li], f, inkB, tx, ty, _fmt);
                            }
                        }
                        x += w;
                    }
                    if (!gridlines && r == header) g.DrawLine(gridP, 0, y + h, x, y + h);
                    y += h;
                }
                g.Restore(state);
            }

            /// <summary>Render halaman ke bitmap putih (pratinjau / cetak raster).</summary>
            public Bitmap Render(int pi, int maxDim)
            {
                var s = PageSize(pi);
                float k = maxDim / Math.Max(s.Width, s.Height);
                var bmp = new Bitmap(Math.Max(1, (int)(s.Width * k)), Math.Max(1, (int)(s.Height * k)));
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.White);
                g.PageUnit = GraphicsUnit.Pixel;
                g.ScaleTransform(k, k);
                Draw(g, pi);
                return bmp;
            }

            public void Dispose()
            {
                _regular.Dispose(); _bold.Dispose(); _title.Dispose(); _sub.Dispose(); _foot.Dispose();
                _mg.Dispose(); _mbmp.Dispose(); _fmt.Dispose();
            }
        }
    }
}
