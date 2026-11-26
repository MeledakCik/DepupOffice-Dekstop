using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>Sumber halaman yang bisa dicetak lewat lembar Cetak DepUp.</summary>
    internal interface IPrintSource : IDisposable
    {
        int Count { get; }
        /// <summary>Ukuran halaman (satuan bebas; hanya rasio &amp; orientasi yang dipakai, kecuali <see cref="SizeInPoints"/>).</summary>
        SizeF PageSize(int index);
        /// <summary>true = <see cref="PageSize"/> dalam pt (dicetak dengan ukuran asli, bukan diperbesar memenuhi kertas).</summary>
        bool SizeInPoints { get; }
        Bitmap Render(int index, int maxDim);
        /// <summary>Gambar vektor langsung ke printer (null = cetak hasil <see cref="Render"/>).</summary>
        Action<Graphics, int>? DrawVector { get; }
    }

    internal sealed class PagedPrintSource : IPrintSource
    {
        private readonly PagedDocument _doc;
        private readonly bool _own;
        public PagedPrintSource(PagedDocument doc, bool own) { _doc = doc; _own = own; }
        public int Count => _doc.Count;
        public SizeF PageSize(int i) => _doc.Sizes[i];
        public bool SizeInPoints => false;
        public Bitmap Render(int i, int maxDim) => _doc.Render(i, maxDim);
        public Action<Graphics, int>? DrawVector => null;
        public void Dispose() { if (_own) _doc.Dispose(); }
    }

    internal sealed class BitmapPrintSource : IPrintSource
    {
        private readonly IReadOnlyList<Bitmap> _pages;
        public BitmapPrintSource(IReadOnlyList<Bitmap> pages) => _pages = pages;
        public int Count => _pages.Count;
        public SizeF PageSize(int i) => _pages[i].Size;
        public bool SizeInPoints => false;
        public Bitmap Render(int i, int maxDim)
        {
            var src = _pages[i];
            float k = Math.Min(1f, (float)maxDim / Math.Max(src.Width, src.Height));
            var b = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var g = Graphics.FromImage(b);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, b.Width, b.Height);
            return b;
        }
        public Action<Graphics, int>? DrawVector => null;
        public void Dispose() { }
    }

    internal sealed class SheetPrintSource : IPrintSource
    {
        private readonly SheetLayout.Doc _doc;
        public SheetPrintSource(SheetLayout.Doc doc) => _doc = doc;
        public int Count => _doc.Count;
        public SizeF PageSize(int i) => _doc.PageSize(i);
        public bool SizeInPoints => true;
        public Bitmap Render(int i, int maxDim) { lock (_doc) return _doc.Render(i, maxDim); }
        public Action<Graphics, int>? DrawVector => (g, i) => { lock (_doc) _doc.Draw(g, i); };
        public void Dispose() => _doc.Dispose();
    }

    /// <summary>Teks polos (dokumen Word tanpa tata letak, .txt, .md) dipecah ke halaman A4 vektor.</summary>
    internal sealed class TextPrintSource : IPrintSource
    {
        private const float W = 595f, H = 842f, M = 56f;
        private readonly string _title;
        private readonly List<List<string>> _pages = new();
        private readonly Font _body = new("Segoe UI", 11f, GraphicsUnit.World);
        private readonly Font _head = new("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.World);
        private readonly Font _foot = new("Segoe UI", 8f, GraphicsUnit.World);
        private const float LineH = 15.5f;

        public TextPrintSource(string title, string text)
        {
            _title = title;
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            g.PageUnit = GraphicsUnit.Point;
            using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            float Wd(string s) => s.Length == 0 ? 0 : g.MeasureString(s, _body, int.MaxValue, fmt).Width;
            var lines = new List<string>();
            foreach (var para in text.Replace("\r\n", "\n").Split('\n'))
                lines.AddRange(para.Length == 0 ? new List<string> { "" } : WrapAll(para, Wd));
            float avail0 = H - 2 * M - 30f - 26f, avail = H - 2 * M - 26f;
            var cur = new List<string>(); float y = 0;
            foreach (var l in lines)
            {
                if (y + LineH > (_pages.Count == 0 ? avail0 : avail) && cur.Count > 0) { _pages.Add(cur); cur = new List<string>(); y = 0; }
                cur.Add(l); y += LineH;
            }
            _pages.Add(cur);
        }

        /// <summary>Bungkus tanpa batas 8 baris (paragraf panjang).</summary>
        private static List<string> WrapAll(string para, Func<string, float> wd)
        {
            var output = new List<string>(); string line = "";
            foreach (var word in para.Split(' '))
            {
                var cand = line.Length == 0 ? word : line + " " + word;
                if (wd(cand) <= W - 2 * M || line.Length == 0) line = cand;
                else { output.Add(line); line = word; }
            }
            output.Add(line);
            return output;
        }

        public int Count => _pages.Count;
        public SizeF PageSize(int i) => new(W, H);
        public bool SizeInPoints => true;
        public Action<Graphics, int>? DrawVector => Draw;

        private void Draw(Graphics g, int i)
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            using var ink = new SolidBrush(Color.FromArgb(0x1A, 0x1A, 0x1A));
            using var mu = new SolidBrush(Color.FromArgb(0x6B, 0x6B, 0x6B));
            float y = M;
            if (i == 0) { g.DrawString(_title, _head, ink, M, y, fmt); y += 30f; }
            foreach (var l in _pages[i]) { g.DrawString(l, _body, ink, M, y, fmt); y += LineH; }
            var foot = $"{_title} · Halaman {i + 1} dari {Count}";
            float fw = g.MeasureString(foot, _foot, int.MaxValue, fmt).Width;
            g.DrawString(foot, _foot, mu, (W - fw) / 2, H - M / 2 - 8, fmt);
        }

        public Bitmap Render(int i, int maxDim)
        {
            float k = maxDim / H;
            var bmp = new Bitmap(Math.Max(1, (int)(W * k)), Math.Max(1, (int)(H * k)));
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.ScaleTransform(k, k);
            Draw(g, i);
            return bmp;
        }

        public void Dispose() { _body.Dispose(); _head.Dispose(); _foot.Dispose(); }
    }

    /// <summary>
    /// Lembar Cetak khas DepUp (port <c>PrintSheet.kt</c> Android 4.3): pratinjau halaman, pilih halaman ("1-3, 5" atau klik
    /// gambar), salinan tersusun, ukuran kertas (A4 / F4 / Letter / Legal / A5), warna / hitam putih, satu sisi / bolak-balik.
    /// Di PC printer dipilih langsung di sini, lalu dicetak tanpa dialog Windows.
    /// </summary>
    internal sealed class PrintSheetForm : Form
    {
        private static int S(int v) => Theme.S(v);

        private sealed record Paper(string Label, string Size, PaperKind Kind, int W100, int H100);

        /// <summary>F4 = 8,5 × 13 in: ukuran "F4 / Folio" yang dikenal printer di Indonesia.</summary>
        private static readonly Paper[] Papers =
        {
            new("A4", "210 × 297 mm", PaperKind.A4, 827, 1169),
            new("F4", "215 × 330 mm", PaperKind.Custom, 850, 1300),
            new("Letter", "216 × 279 mm", PaperKind.Letter, 850, 1100),
            new("Legal", "216 × 356 mm", PaperKind.Legal, 850, 1400),
            new("A5", "148 × 210 mm", PaperKind.A5, 583, 827),
        };

        private readonly IPrintSource _src;
        private readonly string _name;
        private readonly HashSet<int> _sel = new();
        private bool _pick;
        private int _copies = 1, _paper;
        private bool _color = true, _duplex;
        private readonly CancellationTokenSource _cts = new();

        private readonly FlowLayoutPanel _thumbs;
        private readonly List<PictureBox> _cells = new();
        private readonly ComboBox _printer, _paperBox;
        private readonly Segmented _pagesSeg, _colorSeg, _sideSeg;
        private readonly TextBox _range;
        private readonly Label _rangeErr, _count, _paperNote, _summary, _duplexNote;
        private readonly Button _go;
        private bool _muteRange;

        public static void Show(IWin32Window owner, IPrintSource src, string name)
        {
            if (src.Count == 0) { Theme.Toast(owner as Control, "Tidak ada halaman yang dapat dicetak"); src.Dispose(); return; }
            if (PrinterSettings.InstalledPrinters.Count == 0)
            {
                MessageBox.Show(owner, "Belum ada printer yang terpasang di Windows. Pasang printer (atau Microsoft Print to PDF) di Pengaturan › Bluetooth & perangkat › Printer & pemindai.",
                    "Cetak", MessageBoxButtons.OK, MessageBoxIcon.Information);
                src.Dispose();
                return;
            }
            using var f = new PrintSheetForm(src, name);
            f.ShowDialog(owner);
        }

        private PrintSheetForm(IPrintSource src, string name)
        {
            _src = src; _name = name;
            int n = src.Count;
            for (int i = 0; i < n; i++) _sel.Add(i);
            var first = src.PageSize(0);
            _paper = MatchPaper(first, src.SizeInPoints);

            Text = "Cetak — " + name;
            AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(960), wa.Width - S(40)), Math.Min(S(700), wa.Height - S(40)));
            MinimumSize = new Size(Math.Min(S(720), wa.Width), Math.Min(S(520), wa.Height));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG; ForeColor = Theme.TEXT;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            // ---- kanan: pengaturan ----
            var side = new Panel { Dock = DockStyle.Right, Width = S(360), BackColor = Theme.SURFACE, Padding = new Padding(S(20), S(16), S(20), S(16)) };
            side.Paint += (s, e) => { using var p = new Pen(Theme.LINE); e.Graphics.DrawLine(p, 0, 0, 0, side.Height); };
            var col = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.SURFACE };
            Theme.DarkScrollbars(col);
            side.Controls.Add(col);
            int iw = S(360) - S(40) - SystemInformation.VerticalScrollBarWidth;

            Label H(string t, int top = 16) { var l = new Label { Text = t, Font = Theme.FontHead(11f), ForeColor = Theme.TEXT, AutoSize = true, Margin = new Padding(0, S(top), 0, S(6)), UseMnemonic = false }; col.Controls.Add(l); return l; }
            Label Note(string t) { var l = new Label { Text = t, Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, AutoSize = true, MaximumSize = new Size(iw, 0), Margin = new Padding(0, S(4), 0, 0), UseMnemonic = false }; col.Controls.Add(l); return l; }

            col.Controls.Add(new Label { Text = "Cetak", Font = Theme.FontHead(15f), ForeColor = Theme.TEXT, AutoSize = true, UseMnemonic = false });
            col.Controls.Add(new Label { Text = name, Font = Theme.FontBody(9.5f), ForeColor = Theme.MUTED, AutoSize = false, Width = iw, Height = S(20), AutoEllipsis = true, UseMnemonic = false });

            H("Printer");
            _printer = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = iw, Font = Theme.FontBody(10f), FlatStyle = FlatStyle.Flat, BackColor = Theme.FIELD, ForeColor = Theme.TEXT };
            string def = new PrinterSettings().PrinterName;
            foreach (string p in PrinterSettings.InstalledPrinters) _printer.Items.Add(p);
            _printer.SelectedItem = _printer.Items.Contains(def) ? def : _printer.Items[0];
            _printer.SelectedIndexChanged += (s, e) => Refresh1();
            col.Controls.Add(_printer);

            H("Halaman");
            _pagesSeg = new Segmented { Items = new[] { $"Semua ({n})", "Pilih halaman" }, Width = iw, Height = S(38) };
            _pagesSeg.SelectedChanged += i =>
            {
                _pick = i == 1;
                if (_pick && PrintPages.Parse(_range!.Text, n) == null) SetSel(Enumerable.Range(0, n));
                _range!.Visible = _rangeHint!.Visible = _pick;
                if (!_pick) _rangeErr!.Visible = false;
                Refresh1();
            };
            col.Controls.Add(_pagesSeg);
            _range = new TextBox { Width = iw, Font = Theme.FontBody(10.5f), BackColor = Theme.FIELD, ForeColor = Theme.TEXT, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "mis. 1-3, 5", Visible = false, Margin = new Padding(0, S(8), 0, 0), AccessibleName = "Nomor halaman" };
            _range.TextChanged += (s, e) =>
            {
                if (_muteRange) return;
                var p = PrintPages.Parse(_range.Text, n);
                _rangeErr!.Visible = p == null;
                if (p != null) { _sel.Clear(); foreach (var k in p) _sel.Add(k); PaintCells(); }
                Refresh1();
            };
            col.Controls.Add(_range);
            _rangeErr = Note($"Tulis nomor halaman 1–{n}, mis. 1-3, 5"); _rangeErr.ForeColor = Theme.RED; _rangeErr.Visible = false;
            _rangeHint = Note("Klik gambar halaman untuk memilih atau membatalkan."); _rangeHint.Visible = false;

            H("Salinan");
            var copiesRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.SURFACE, Margin = Padding.Empty };
            var minus = Theme.CreateButton("−", false, Theme.TEXT, S(38), S(44)); minus.AccessibleName = "Kurangi salinan";
            _count = new Label { Text = "1", Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, AutoSize = false, Size = new Size(S(52), S(38)), TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
            var plus = Theme.CreateButton("+", false, Theme.TEXT, S(38), S(44)); plus.AccessibleName = "Tambah salinan";
            minus.Click += (s, e) => { if (_copies > 1) { _copies--; Refresh1(); } };
            plus.Click += (s, e) => { if (_copies < 99) { _copies++; Refresh1(); } };
            copiesRow.Controls.AddRange(new Control[] { minus, _count, plus });
            col.Controls.Add(copiesRow);
            Note("Tersusun 1-2-3, 1-2-3");

            H("Ukuran kertas");
            _paperBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = iw, Font = Theme.FontBody(10f), FlatStyle = FlatStyle.Flat, BackColor = Theme.FIELD, ForeColor = Theme.TEXT };
            foreach (var p in Papers) _paperBox.Items.Add($"{p.Label}  ({p.Size})");
            _paperBox.SelectedIndex = _paper;
            _paperBox.SelectedIndexChanged += (s, e) => { _paper = _paperBox.SelectedIndex; Refresh1(); };
            col.Controls.Add(_paperBox);
            _paperNote = Note("");

            H("Warna");
            _colorSeg = new Segmented { Items = new[] { "Berwarna", "Hitam putih" }, Width = iw, Height = S(38) };
            _colorSeg.SelectedChanged += i => { _color = i == 0; Refresh1(); };
            col.Controls.Add(_colorSeg);

            H("Sisi kertas");
            _sideSeg = new Segmented { Items = new[] { "Satu sisi", "Bolak-balik" }, Width = iw, Height = S(38) };
            _sideSeg.SelectedChanged += i => { _duplex = i == 1; Refresh1(); };
            col.Controls.Add(_sideSeg);
            _duplexNote = Note("Printer yang tidak bisa bolak-balik otomatis akan mencetak satu sisi.");

            var foot = new Panel { Dock = DockStyle.Bottom, Height = S(100), BackColor = Theme.SURFACE, Padding = new Padding(S(20), S(10), S(20), S(14)) };
            foot.Paint += (s, e) => { using var p = new Pen(Theme.LINE); e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
            _summary = new Label { Dock = DockStyle.Top, Height = S(30), Font = Theme.FontLabel(10f, FontStyle.Bold), ForeColor = Theme.TEXT, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
            _go = Theme.CreateButton("🖨  Cetak", true, Theme.ACCENT, S(44));
            _go.Dock = DockStyle.Bottom;
            _go.Click += (s, e) => DoPrint();
            foot.Controls.Add(_go);
            foot.Controls.Add(_summary);
            side.Controls.Add(foot);
            AcceptButton = _go;

            // ---- kiri: pratinjau halaman ----
            _thumbs = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BG, Padding = new Padding(S(16)), WrapContents = true };
            Theme.DarkScrollbars(_thumbs);
            int cap = Math.Min(n, 200);
            for (int i = 0; i < cap; i++) _thumbs.Controls.Add(MakeCell(i));
            if (n > cap)
                _thumbs.Controls.Add(new Label { Text = $"+{n - cap}\nhalaman", Font = Theme.FontHead(12f), ForeColor = Theme.MUTED, AutoSize = false, Size = new Size(S(140), S(190)), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(S(6)), UseMnemonic = false });

            Controls.Add(_thumbs);
            Controls.Add(side);
            Shown += (s, e) => { Refresh1(); _ = LoadThumbsAsync(cap); };
        }

        private readonly Label _rangeHint;

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyWindowChrome(this); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cts.Cancel();
            foreach (var c in _cells) { c.Image?.Dispose(); }
            _src.Dispose();
            base.OnFormClosed(e);
        }

        private static int MatchPaper(SizeF size, bool inPoints)
        {
            if (!inPoints) return 0;
            float w = Math.Min(size.Width, size.Height) / 72f * 100f, h = Math.Max(size.Width, size.Height) / 72f * 100f;
            int best = 0; float bestD = float.MaxValue;
            for (int i = 0; i < Papers.Length; i++)
            {
                float d = Math.Abs(Papers[i].W100 - w) + Math.Abs(Papers[i].H100 - h);
                if (d < bestD) { bestD = d; best = i; }
            }
            return bestD < 40 ? best : 0;
        }

        private PictureBox MakeCell(int i)
        {
            var ps = _src.PageSize(i);
            float aspect = ps.Height <= 0 ? 1.414f : ps.Height / ps.Width;
            int w = S(140), h = Math.Clamp((int)(w * aspect), S(80), S(200));
            var pb = new PictureBox
            {
                Size = new Size(w, h + S(22)), Margin = new Padding(S(6)), Cursor = Cursors.Hand, BackColor = Theme.BG,
                AccessibleName = $"Halaman {i + 1}", AccessibleRole = AccessibleRole.CheckButton, Tag = i
            };
            pb.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool on = _sel.Contains(i);
                var r = new Rectangle(S(2), S(2), pb.Width - S(4), h - S(2));
                using (var wb = new SolidBrush(Color.White)) g.FillRectangle(wb, r);
                if (pb.Image != null)
                {
                    float k = Math.Min((float)r.Width / pb.Image.Width, (float)r.Height / pb.Image.Height);
                    int dw = (int)(pb.Image.Width * k), dh = (int)(pb.Image.Height * k);
                    var dst = new Rectangle(r.Left + (r.Width - dw) / 2, r.Top + (r.Height - dh) / 2, dw, dh);
                    if (!_color)
                    {
                        using var ia = new ImageAttributes();
                        ia.SetColorMatrix(Gray);
                        g.DrawImage(pb.Image, dst, 0, 0, pb.Image.Width, pb.Image.Height, GraphicsUnit.Pixel, ia);
                    }
                    else g.DrawImage(pb.Image, dst);
                }
                if (_pick && !on) { using var veil = new SolidBrush(Color.FromArgb(150, Theme.BG)); g.FillRectangle(veil, r); }
                using (var pen = new Pen(_pick && on ? Theme.ACCENT : Theme.LINE, _pick && on ? S(2) : 1)) g.DrawRectangle(pen, r);
                if (_pick)
                {
                    var box = new Rectangle(r.Right - S(26), r.Top + S(6), S(20), S(20));
                    using var bb = new SolidBrush(on ? Theme.ACCENT : Color.FromArgb(200, Color.White));
                    g.FillEllipse(bb, box);
                    using var bp = new Pen(on ? Theme.ACCENT : Color.Gray, 1.5f);
                    g.DrawEllipse(bp, box);
                    if (on) NoteCard.DrawBox(g, Rectangle.Inflate(box, -S(3), -S(3)), true, Color.White);
                }
                TextRenderer.DrawText(g, (i + 1).ToString(), Theme.FontLabel(9f, FontStyle.Bold), new Rectangle(0, h + S(2), pb.Width, S(20)),
                    Theme.MUTED, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            };
            pb.Click += (s, e) =>
            {
                if (!_pick) { _pagesSeg.SelectedIndex = 1; _pick = true; _range.Visible = _rangeHint.Visible = true; SetSel(new[] { i }); Refresh1(); return; }
                if (_sel.Contains(i)) { if (_sel.Count > 1) _sel.Remove(i); } else _sel.Add(i);
                _muteRange = true; _range.Text = PrintPages.Text(_sel); _muteRange = false;
                _rangeErr.Visible = false;
                pb.Invalidate();
                Refresh1();
            };
            _cells.Add(pb);
            return pb;
        }

        private static readonly ColorMatrix Gray = new(new[]
        {
            new[] { 0.299f, 0.299f, 0.299f, 0, 0 }, new[] { 0.587f, 0.587f, 0.587f, 0, 0 },
            new[] { 0.114f, 0.114f, 0.114f, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 }
        });

        private void SetSel(IEnumerable<int> pages)
        {
            _sel.Clear(); foreach (var p in pages) _sel.Add(p);
            _muteRange = true; _range.Text = PrintPages.Text(_sel); _muteRange = false;
            _rangeErr.Visible = false;
            PaintCells();
        }

        private void PaintCells() { foreach (var c in _cells) c.Invalidate(); }

        private async Task LoadThumbsAsync(int cap)
        {
            var token = _cts.Token;
            for (int i = 0; i < cap && !token.IsCancellationRequested; i++)
            {
                int k = i;
                Bitmap? b = null;
                try { b = await Task.Run(() => _src.Render(k, S(300)), token); } catch { }
                if (b == null) continue;
                if (IsDisposed || token.IsCancellationRequested) { b.Dispose(); return; }
                var pb = _cells[k];
                pb.Image?.Dispose();
                pb.Image = b;
                pb.Invalidate();
            }
        }

        private IReadOnlyList<int> Pages => _pick ? _sel.OrderBy(x => x).ToList() : Enumerable.Range(0, _src.Count).ToList();

        private void Refresh1()
        {
            _count.Text = _copies.ToString();
            var p = Papers[_paper];
            _paperNote.Text = p.Label == "F4" ? "F4 / Folio 8,5 × 13 in — ukuran yang dikenal printer di Indonesia." : p.Size;
            bool canDuplex = false;
            try { canDuplex = new PrinterSettings { PrinterName = (string)_printer.SelectedItem! }.CanDuplex; } catch { }
            _duplexNote.Text = _duplex && !canDuplex ? "Printer ini tidak mendukung bolak-balik otomatis: akan dicetak satu sisi." : "Printer yang tidak bisa bolak-balik otomatis akan mencetak satu sisi.";
            bool valid = !_pick || (PrintPages.Parse(_range.Text, _src.Count) != null && _sel.Count > 0);
            int count = Pages.Count;
            var parts = new List<string> { !_pick ? "Semua halaman" : $"{count} halaman" };
            if (_copies > 1) parts.Add($"{_copies} salinan");
            parts.Add(p.Label);
            parts.Add(_color ? "Berwarna" : "Hitam putih");
            if (_duplex) parts.Add("Bolak-balik");
            _summary.Text = valid ? string.Join(" · ", parts) : "Periksa nomor halaman";
            _go.Enabled = valid;
            int sheets = PrintPages.JobPages(count, _copies, _duplex && canDuplex);
            _go.Text = $"🖨  Cetak {(_duplex && canDuplex ? (sheets + 1) / 2 : sheets)} lembar";
            PaintCells();
        }

        private void DoPrint()
        {
            var pages = Pages;
            if (pages.Count == 0) return;
            string printer = (string)_printer.SelectedItem!;
            var paper = Papers[_paper];
            using var pd = new PrintDocument { DocumentName = "DepupOffice_" + _name };
            pd.PrinterSettings.PrinterName = printer;
            if (!pd.PrinterSettings.IsValid) { MessageBox.Show(this, "Printer tidak tersedia.", "Cetak", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            bool duplex = _duplex && pd.PrinterSettings.CanDuplex;
            pd.PrinterSettings.Copies = 1;   // salinan disusun sendiri (1-2-3, 1-2-3) agar sama di semua printer
            pd.PrinterSettings.Duplex = duplex ? Duplex.Vertical : Duplex.Simplex;
            var ps = pd.PrinterSettings.PaperSizes.Cast<PaperSize>().FirstOrDefault(x =>
                (paper.Kind != PaperKind.Custom && x.Kind == paper.Kind) ||
                (Math.Abs(x.Width - paper.W100) <= 6 && Math.Abs(x.Height - paper.H100) <= 6));
            pd.DefaultPageSettings.PaperSize = ps ?? new PaperSize(paper.Label, paper.W100, paper.H100);
            pd.DefaultPageSettings.Color = _color && pd.PrinterSettings.SupportsColor;
            pd.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            var order = PrintPages.JobOrder(pages, _copies, duplex);
            int at = 0;
            bool gray = !_color;
            pd.QueryPageSettings += (s, e) =>
            {
                int pi = order[Math.Min(at, order.Count - 1)];
                if (pi < 0) pi = pages[^1];
                var sz = _src.PageSize(pi);
                e.PageSettings.Landscape = sz.Width > sz.Height * 1.02f;
            };
            pd.PrintPage += (s, e) =>
            {
                var g = e.Graphics;
                int pi = order[at++];
                if (g != null && pi >= 0) DrawPage(g, e.PageSettings, e.PageBounds, pi, gray);
                e.HasMorePages = at < order.Count;
            };
            Exception? err = null;
            var (ok, _) = Ui.Busy<int>(this, "Mengirim ke printer…", _ =>
            {
                try { pd.Print(); } catch (Exception ex) { err = ex; }
                return 0;
            });
            if (err != null) { MessageBox.Show(this, "Gagal mencetak: " + err.Message, "Cetak", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (ok)
            {
                Theme.Toast(Owner, $"Dikirim ke {printer}");
                Close();
            }
        }

        /// <summary>Gambar satu halaman pas di kertas (rasio dijaga, di tengah, di dalam area cetak printer).</summary>
        private void DrawPage(Graphics g, PageSettings settings, Rectangle pageBounds, int pi, bool gray)
        {
            // area yang benar-benar bisa dicetak printer (1/100 in), relatif terhadap titik asal grafik
            var area = settings.PrintableArea;
            var bounds = settings.Landscape
                ? new RectangleF(area.Y, area.X, area.Height, area.Width)
                : new RectangleF(area.X, area.Y, area.Width, area.Height);
            float hx = settings.HardMarginX, hy = settings.HardMarginY;
            bounds.Offset(-hx, -hy);
            var size = _src.PageSize(pi);
            if (_src.DrawVector is { } draw && _src.SizeInPoints && !gray)
            {
                // vektor: 1 pt = 100/72 satuan; diperkecil bila lebih besar dari area cetak, tidak diperbesar
                float pw = size.Width * 100f / 72f, ph = size.Height * 100f / 72f;
                float k = Math.Min(1f, Math.Min(bounds.Width / pw, bounds.Height / ph));
                var st = g.Save();
                g.TranslateTransform(bounds.Left + (bounds.Width - pw * k) / 2f, bounds.Top + (bounds.Height - ph * k) / 2f);
                g.ScaleTransform(k * 100f / 72f, k * 100f / 72f);
                draw(g, pi);
                g.Restore(st);
                return;
            }
            // raster ±300 dpi pada ukuran kertas
            int dim = (int)Math.Clamp(Math.Max(bounds.Width, bounds.Height) * 3f, 1200, 3600);
            using var bmp = _src.Render(pi, dim);
            float s2 = Math.Min(bounds.Width / bmp.Width, bounds.Height / bmp.Height);
            if (_src.SizeInPoints) s2 = Math.Min(s2, size.Width * 100f / 72f / bmp.Width);
            float dw = bmp.Width * s2, dh = bmp.Height * s2;
            var dst = new RectangleF(bounds.Left + (bounds.Width - dw) / 2f, bounds.Top + (bounds.Height - dh) / 2f, dw, dh);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (gray)
            {
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(Gray);
                g.DrawImage(bmp, Rectangle.Round(dst), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
            }
            else g.DrawImage(bmp, dst);
        }
    }
}
