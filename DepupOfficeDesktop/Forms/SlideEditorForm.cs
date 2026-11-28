using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Editor presentasi (.pptx) ala WPS Presentation: daftar slide di kiri, kanvas di tengah, pita format di atas.
    /// Klik = pilih, seret = pindah, titik sudut = ubah ukuran, klik dua kali / ketik = sunting teks.
    /// Pintasan: Ctrl+M slide baru, Ctrl+D duplikat, Ctrl+C/X/V, Ctrl+Z/Y, Delete, panah = geser (Shift = 10),
    /// F5 putar dari awal, Shift+F5 dari slide ini, Ctrl+S simpan.
    /// </summary>
    public sealed class SlideEditorForm : Form
    {
        private static int S(int v) => Theme.S(v);

        private SlideDeck _deck;
        private string? _path;
        private string _name;
        private bool _dirty;
        private int _cur, _sel = -1;
        private readonly List<Snap> _undo = new(), _redo = new();
        private List<SlideItem>? _clip;

        private readonly CanvasView _canvas = new();
        private readonly FlowLayoutPanel _strip = new();
        private Label _title = null!, _status = null!, _statusRight = null!;
        private ComboBox _font = null!, _size = null!;
        private readonly List<Control> _formatControls = new();
        private readonly Dictionary<string, Button> _toggles = new();
        private readonly ToolTip _tips = new() { InitialDelay = 300 };

        // sunting teks di tempat
        private TextBox? _editor;
        private SlideItem? _editItem;
        private Snap? _editUndo;
        private string _editOriginal = "";
        private bool _editFormatChanged;
        private Font? _editorFont;
        private float _editorFontScale;
        private RectangleF _editBoxOriginal;

        // seret
        private enum DragMode { None, Move, Resize }
        private DragMode _drag;
        private int _handle;
        private Point _downScreen;
        private PointF _downSlide;
        private RectangleF _boxStart;
        private bool _dragMoved, _snapX, _snapY;
        private Snap? _dragUndo;
        private int _hover = -1;

        private sealed record Snap(SlideDeck Deck, int Cur, int Sel);

        /// <summary>Jalur berkas setelah disimpan (null bila belum pernah disimpan).</summary>
        public string? SavedPath => _path;

        public SlideEditorForm(string? filePath = null, string template = SlideDeck.TEMPLATE_BLANK)
        {
            Theme.SetDpi(DeviceDpi);
            Size = new Size(S(1240), S(800));
            MinimumSize = new Size(S(860), S(560));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;
            KeyPreview = true;
            _name = "Presentasi Baru";
            _deck = SlideDeck.Create(template);

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    _deck = SlideDeck.Load(filePath);
                    _path = filePath;
                    _name = Path.GetFileName(filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Presentasi tidak dapat dibuka untuk disunting:\n{ex.Message}\n\nPresentasi kosong dibuka sebagai gantinya.",
                        "Editor Presentasi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            BuildUi();
            RebuildStrip();
            UpdateAll();
            UpdateTitle();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyWindowChrome(this); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _canvas.Focus();
            if (_deck.Unsupported > 0)
                Theme.Toast(this, $"{_deck.Unsupported} elemen (tabel, grafik, grup) tidak bisa disunting dan tidak ikut tersimpan. Simpan sebagai salinan agar berkas asli tetap utuh.");
        }

        private Slide Cur => _deck.Slides[_cur];
        private SlideItem? Sel => _sel >= 0 && _sel < Cur.Items.Count ? Cur.Items[_sel] : null;

        // ================================================================ UI

        private Button Tb(string text, string tip, Action act, int w = 0, bool format = false)
        {
            var b = Theme.CreateButton(text, false, Theme.TEXT, S(34), w > 0 ? S(w) : 0);
            b.BackColor = Theme.SURFACE;
            b.Font = Theme.FontLabel(9.5f);
            b.Padding = new Padding(S(4), 0, S(4), 0);
            b.Margin = new Padding(S(1), S(3), S(1), S(3));
            b.TabStop = false;
            b.AccessibleName = tip;
            b.Click += (s, e) => act();
            _tips.SetToolTip(b, tip);
            if (format) _formatControls.Add(b);
            return b;
        }

        private Button MenuTb(string text, string tip, Func<ToolStripItem[]> items, int w = 0, bool format = false)
        {
            Button? b = null;
            b = Tb(text + "  ▾", tip, () => ShowMenu(b!, items()), w, format);
            return b;
        }

        /// <summary>Tombol yang aktif untuk elemen apa pun yang dipilih (teks, gambar, bentuk).</summary>
        private Button AnyKind(Button b) { _anyKindControls.Add(b); return b; }

        private static Control Sep() => new Panel { Width = 1, Height = S(26), BackColor = Theme.LINE, Margin = new Padding(S(5), S(7), S(5), 0) };

        private void ShowMenu(Control anchor, ToolStripItem[] items)
        {
            var m = new ContextMenuStrip();
            m.Items.AddRange(items);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(anchor, new Point(0, anchor.Height));
        }

        private static ToolStripMenuItem Item(string text, Action act, bool check = false, Image? img = null)
        {
            var it = new ToolStripMenuItem(text, img) { Checked = check };
            it.Click += (s, e) => act();
            return it;
        }

        private static Bitmap Swatch(Color c)
        {
            var b = new Bitmap(16, 16);
            using var g = Graphics.FromImage(b);
            if (c.IsEmpty)
            {
                g.Clear(Color.White);
                using var red = new Pen(Color.Red, 2);
                g.DrawLine(red, 2, 14, 14, 2);
            }
            else g.Clear(c);
            g.DrawRectangle(Pens.Gray, 0, 0, 15, 15);
            return b;
        }

        /// <summary>Menu warna: warna tema dulu, lalu palet umum, lalu "Warna lain…".</summary>
        private ToolStripItem[] ColorItems(Action<Color> apply, bool allowNone)
        {
            var t = _deck.Theme;
            var list = new List<ToolStripItem>();
            if (allowNone) list.Add(Item("Tanpa", () => apply(Color.Empty), img: Swatch(Color.Empty)));
            foreach (var (name, c) in new[] { ("Judul (tema)", t.Title), ("Teks (tema)", t.Text), ("Aksen (tema)", t.Accent), ("Latar (tema)", t.Bg) })
                list.Add(Item(name, () => apply(c), img: Swatch(c)));
            list.Add(new ToolStripSeparator());
            foreach (var (name, hex) in new[]
            {
                ("Hitam", "#111111"), ("Putih", "#FFFFFF"), ("Abu", "#6B6B6B"), ("Merah", "#C62828"), ("Oranye", "#E8710A"),
                ("Kuning", "#F5A524"), ("Hijau", "#2E7D32"), ("Biru", "#1652F6"), ("Ungu", "#6A3FB5")
            })
            {
                var c = ColorTranslator.FromHtml(hex);
                list.Add(Item(name, () => apply(c), img: Swatch(c)));
            }
            list.Add(new ToolStripSeparator());
            list.Add(Item("Warna lain…", () =>
            {
                using var dlg = new ColorDialog { FullOpen = true, AnyColor = true };
                if (dlg.ShowDialog(this) == DialogResult.OK) apply(dlg.Color);
            }));
            return list.ToArray();
        }

        private static readonly (SlideLayout layout, string name)[] Layouts =
        {
            (SlideLayout.Title, "Judul"), (SlideLayout.TitleContent, "Judul & isi"), (SlideLayout.TwoContent, "Dua kolom"),
            (SlideLayout.TitleOnly, "Hanya judul"), (SlideLayout.Section, "Bagian"), (SlideLayout.Blank, "Kosong"),
        };

        private void BuildUi()
        {
            // ---- bilah atas
            var top = new Panel { Dock = DockStyle.Top, Height = S(60), BackColor = Theme.BG };
            var back = Theme.CreateButton("←  Kembali", false, Theme.TEXT, S(40), S(120));
            back.BackColor = Theme.SURFACE;
            back.Location = new Point(S(16), S(10));
            back.Click += (s, e) => Close();
            _title = new Label { AutoSize = true, Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, Location = new Point(S(150), S(18)), UseMnemonic = false };
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, Padding = new Padding(0, S(10), S(16), 0), BackColor = Theme.BG };
            Button TopBtn(string text, string tip, bool primary, Action act, int w = 0)
            {
                var b = Theme.CreateButton(text, primary, Theme.ACCENT, S(40), w > 0 ? S(w) : 0);
                if (!primary) { b.BackColor = Theme.SURFACE; b.ForeColor = Theme.TEXT; }
                if (text.Length == 1) { b.Padding = Padding.Empty; b.Font = new Font("Segoe UI Symbol", 12f); }
                b.Margin = new Padding(S(4), 0, S(4), 0);
                b.AccessibleName = tip;
                b.Click += (s, e) => act();
                _tips.SetToolTip(b, tip);
                return b;
            }
            right.Controls.AddRange(new Control[]
            {
                TopBtn("↩", "Urungkan (Ctrl+Z)", false, Undo, 44),
                TopBtn("↪", "Ulangi (Ctrl+Y)", false, Redo, 44),
                TopBtn("Putar", "Putar dari awal (F5) — Shift+F5 dari slide ini", false, () => Play(false)),
                TopBtn("Ekspor PDF", "Simpan semua slide sebagai PDF", false, ExportPdf),
                TopBtn("Simpan Sebagai…", "Simpan ke berkas lain (Ctrl+Shift+S)", false, () => SaveAs()),
                TopBtn("Simpan", "Simpan (Ctrl+S)", true, () => Save(), 110),
            });
            top.Controls.AddRange(new Control[] { back, _title, right });

            // ---- pita (membungkus ke baris kedua saat jendela sempit)
            var ribbon = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, BackColor = Theme.SURFACE, WrapContents = true, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(10), S(4), S(10), S(4))
            };
            _font = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = S(124), Font = Theme.FontBody(9.5f), Margin = new Padding(S(2), S(7), S(4), 0) };
            _font.Items.AddRange(new object[] { "Segoe UI", "Calibri", "Arial", "Georgia", "Times New Roman", "Cambria", "Verdana", "Tahoma", "Century Gothic", "Trebuchet MS", "Courier New" });
            _font.SelectionChangeCommitted += (s, e) => { if (_font.SelectedItem is string f) Format(i => i.Font = f); };
            _font.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && _font.Text.Trim().Length > 0) { e.SuppressKeyPress = true; var f = _font.Text.Trim(); Format(i => i.Font = f); _canvas.Focus(); } };
            _size = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = S(58), Font = Theme.FontBody(9.5f), Margin = new Padding(S(2), S(7), S(4), 0) };
            _size.Items.AddRange(new object[] { "10", "12", "14", "16", "18", "20", "24", "28", "32", "36", "40", "44", "48", "54", "60", "72", "96" });
            void ApplySize()
            {
                if (float.TryParse(_size.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt) && pt >= 4 && pt <= 400)
                    Format(i => i.Size = pt);
            }
            _size.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(ApplySize));
            _size.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplySize(); _canvas.Focus(); } };
            _formatControls.Add(_font); _formatControls.Add(_size);

            Button Toggle(string key, string text, string tip, Func<SlideItem, bool> get, Action<SlideItem, bool> set)
            {
                var b = Tb(text, tip, () => { if (Sel is { HasText: true } it) { bool v = !get(it); Format(i => set(i, v)); } }, 36, true);
                _toggles[key] = b;
                return b;
            }

            ribbon.Controls.AddRange(new Control[]
            {
                MenuTb("Slide baru", "Tambah slide (Ctrl+M)", () => Layouts.Select(l => (ToolStripItem)Item(l.name, () => AddSlide(l.layout))).ToArray()),
                MenuTb("Tata letak", "Ganti tata letak slide ini (teks tetap)", () => Layouts.Select(l => (ToolStripItem)Item(l.name, () => ApplyLayout(l.layout))).ToArray()),
                Tb("Duplikat", "Duplikat slide", DuplicateSlide),
                Sep(),
                Tb("Teks", "Sisipkan kotak teks", InsertText),
                Tb("Gambar", "Sisipkan gambar (atau seret / tempel Ctrl+V)", InsertImageDialog),
                MenuTb("Bentuk", "Sisipkan bentuk", () => new ToolStripItem[]
                {
                    Item("Persegi", () => InsertShape(SlideShape.Rect)), Item("Persegi bulat", () => InsertShape(SlideShape.RoundRect)),
                    Item("Lingkaran", () => InsertShape(SlideShape.Ellipse)), Item("Segitiga", () => InsertShape(SlideShape.Triangle)),
                    Item("Panah", () => InsertShape(SlideShape.Arrow)),
                }),
                Sep(),
                _font, _size,
                Toggle("b", "B", "Tebal (Ctrl+B)", i => i.Bold, (i, v) => i.Bold = v),
                Toggle("i", "I", "Miring (Ctrl+I)", i => i.Italic, (i, v) => i.Italic = v),
                Toggle("u", "U", "Garis bawah (Ctrl+U)", i => i.Underline, (i, v) => i.Underline = v),
                MenuTb("A", "Warna teks", () => ColorItems(c => Format(i => i.Color = c), false), 0, true),
                Sep(),
                Tb("⯇", "Rata kiri", () => Format(i => i.Align = 0), 36, true),
                Tb("≡", "Rata tengah", () => Format(i => i.Align = 1), 36, true),
                Tb("⯈", "Rata kanan", () => Format(i => i.Align = 2), 36, true),
                Toggle("bul", "•≡", "Daftar poin", i => i.Bullets, (i, v) => i.Bullets = v),
                Sep(),
                AnyKind(MenuTb("Isi", "Warna isi kotak / bentuk", () => ColorItems(c => Format(i => i.Fill = c, anyKind: true), true))),
                AnyKind(MenuTb("Garis", "Warna garis tepi", () => ColorItems(c => Format(i => i.Line = c, anyKind: true), true))),
                AnyKind(MenuTb("Susun", "Urutan tumpukan", () => new ToolStripItem[]
                {
                    Item("Ke paling depan", () => Arrange(int.MaxValue)), Item("Maju satu", () => Arrange(1)),
                    Item("Mundur satu", () => Arrange(-1)), Item("Ke paling belakang", () => Arrange(int.MinValue)),
                })),
                Sep(),
                MenuTb("Tema", "Tema presentasi (semua slide)", () => DeckTheme.All.Select((t, i) =>
                    (ToolStripItem)Item(t.Name, () => Commit(() => _deck.ApplyTheme(i), structural: true), i == _deck.ThemeIndex, Swatch(t.Bg))).ToArray()),
                MenuTb("Latar", "Warna latar slide", () => ColorItems(c => Commit(() => Cur.Background = c), false)
                    .Append(new ToolStripSeparator())
                    .Append(Item("Terapkan latar ini ke semua slide", () => { var c = Cur.Background; Commit(() => _deck.Slides.ForEach(s => s.Background = c), structural: true); }))
                    .ToArray()),
            });

            // ---- daftar slide
            var left = new Panel { Dock = DockStyle.Left, Width = S(StripWidth), BackColor = Theme.SURFACE };
            _strip.Dock = DockStyle.Fill;
            _strip.FlowDirection = FlowDirection.TopDown;
            _strip.WrapContents = false;
            _strip.AutoScroll = true;
            _strip.BackColor = Theme.SURFACE;
            _strip.Padding = new Padding(S(6), S(8), S(6), S(8));
            Theme.DarkScrollbars(_strip);
            var addHost = new Panel { Dock = DockStyle.Bottom, Height = S(56), BackColor = Theme.SURFACE, Padding = new Padding(S(12), S(8), S(12), S(8)) };
            var add = Theme.CreateButton("+  Slide baru", false, Theme.ACCENT, S(40));
            add.Dock = DockStyle.Fill;
            add.Click += (s, e) => AddSlide(SlideLayout.TitleContent);
            _tips.SetToolTip(add, "Slide judul & isi (Ctrl+M)");
            addHost.Controls.Add(add);
            var divider = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Theme.LINE };
            left.Controls.Add(_strip);
            left.Controls.Add(addHost);
            left.Controls.Add(divider);

            // ---- status
            var status = new Panel { Dock = DockStyle.Bottom, Height = S(30), BackColor = Theme.SURFACE };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(16), 0, 0, 0), Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, UseMnemonic = false, AutoEllipsis = true };
            _statusRight = new Label { Dock = DockStyle.Right, Width = S(260), TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(0, 0, S(16), 0), Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, UseMnemonic = false };
            status.Controls.Add(_status);
            status.Controls.Add(_statusRight);

            // ---- kanvas
            _canvas.Dock = DockStyle.Fill;
            _canvas.BackColor = Theme.CANVAS;
            _canvas.AllowDrop = true;
            _canvas.AccessibleName = "Slide";
            _canvas.Paint += PaintCanvas;
            _canvas.MouseDown += CanvasDown;
            _canvas.MouseMove += CanvasMove;
            _canvas.MouseUp += CanvasUp;
            _canvas.MouseDoubleClick += CanvasDoubleClick;
            _canvas.MouseWheel += (s, e) => { if (_editor == null) GoTo(_cur + (e.Delta < 0 ? 1 : -1)); };
            _canvas.Resize += (s, e) => { PositionEditor(); _canvas.Invalidate(); };
            _canvas.KeyPress += CanvasKeyPress;
            _canvas.DragEnter += (s, e) => e.Effect = DroppedImages(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            _canvas.DragDrop += (s, e) => { foreach (var f in DroppedImages(e.Data)) InsertImageFile(f); };

            Controls.Add(_canvas);
            Controls.Add(left);
            Controls.Add(status);
            Controls.Add(ribbon);
            Controls.Add(top);
        }

        private readonly List<Control> _anyKindControls = new();

        private static string[] DroppedImages(IDataObject? data) =>
            data?.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif").ToArray()
                : Array.Empty<string>();

        // ================================================================ STATUS

        private void UpdateTitle()
        {
            _title.Text = (_dirty ? "• " : "") + _name;
            Text = $"{_name} — Editor Presentasi — DEPUP OFFICE";
        }

        private void UpdateAll()
        {
            foreach (Control c in _strip.Controls) if (c is ThumbView t) t.SetSelected(t.Index == _cur);
            _canvas.Invalidate();
            UpdateRibbon();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            string what = Sel switch
            {
                null => _deck.Unsupported > 0
                    ? $"{_deck.Unsupported} elemen asli (tabel/grafik/grup) tidak dapat disunting — simpan sebagai salinan"
                    : "Klik elemen untuk memilih · klik dua kali untuk mengetik · seret gambar ke slide",
                { Kind: SlideItemKind.Image } => "Gambar dipilih · seret sudut untuk mengubah ukuran · Delete untuk menghapus",
                { Kind: SlideItemKind.Shape } => "Bentuk dipilih · klik dua kali untuk menulis di dalamnya",
                _ => "Kotak teks dipilih · klik dua kali atau mulai mengetik untuk menyunting",
            };
            _status.Text = what;
            _statusRight.Text = $"Tema {_deck.Theme.Name}   ·   Slide {_cur + 1} dari {_deck.Slides.Count}";
        }

        private void UpdateRibbon()
        {
            var it = _editItem ?? Sel;
            bool text = it?.HasText == true;
            foreach (var c in _formatControls) c.Enabled = text;
            foreach (var c in _anyKindControls) c.Enabled = it != null;
            if (text)
            {
                if (!_font.Focused) _font.Text = it!.Font;
                if (!_size.Focused) _size.Text = it!.Size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            }
            else { _font.Text = ""; _size.Text = ""; }
            void Mark(string key, bool on) { if (_toggles.TryGetValue(key, out var b)) b.BackColor = on ? Theme.BLUE_BG : Theme.SURFACE; }
            Mark("b", text && it!.Bold); Mark("i", text && it!.Italic); Mark("u", text && it!.Underline); Mark("bul", text && it!.Bullets);
        }

        // ================================================================ URUNGKAN

        private Snap Snapshot() => new(_deck.Clone(), _cur, _sel);

        private void PushUndo(Snap s)
        {
            _undo.Add(s);
            if (_undo.Count > 80) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>Satu perubahan yang bisa diurungkan.</summary>
        private void Commit(Action change, bool structural = false)
        {
            CommitEdit();
            PushUndo(Snapshot());
            change();
            _cur = Math.Clamp(_cur, 0, _deck.Slides.Count - 1);
            if (_sel >= Cur.Items.Count) _sel = -1;
            MarkDirty();
            if (structural) RebuildStrip(); else RefreshThumb(_cur);
            UpdateAll();
        }

        private void Restore(Snap s)
        {
            _deck = s.Deck;
            _cur = Math.Clamp(s.Cur, 0, _deck.Slides.Count - 1);
            _sel = s.Sel < Cur.Items.Count ? s.Sel : -1;
            MarkDirty();
            RebuildStrip();
            UpdateAll();
        }

        private void Undo()
        {
            CommitEdit();
            if (_undo.Count == 0) { Theme.Toast(this, "Tidak ada yang bisa diurungkan"); return; }
            _redo.Add(Snapshot());
            var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
            Restore(s);
        }

        private void Redo()
        {
            CommitEdit();
            if (_redo.Count == 0) return;
            _undo.Add(Snapshot());
            var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
            Restore(s);
        }

        private void MarkDirty() { _dirty = true; UpdateTitle(); }

        // ================================================================ DAFTAR SLIDE

        private const int StripWidth = 196;
        /// <summary>Lebar gambar thumbnail: panel − bantalan − kolom nomor − bilah gulir (tidak pernah memicu gulir mendatar).</summary>
        private static int ThumbWidth => Math.Max(S(80), S(StripWidth) - 1 - S(12) - S(34) - SystemInformation.VerticalScrollBarWidth - S(2));

        private void RebuildStrip()
        {
            _strip.SuspendLayout();
            var old = _strip.Controls.Cast<Control>().ToList();
            _strip.Controls.Clear();
            foreach (var c in old) c.Dispose();
            int tw = ThumbWidth;
            for (int i = 0; i < _deck.Slides.Count; i++)
            {
                int idx = i;
                var t = new ThumbView(idx, tw, (int)Math.Round(tw * _deck.H / _deck.W)) { Image = _deck.RenderBitmap(idx, tw) };
                t.SetSelected(idx == _cur);
                t.MouseDown += (s, e) =>
                {
                    GoTo(idx);
                    t.Focus();
                    if (e.Button == MouseButtons.Right) ShowMenu(t, SlideMenu(), e.Location);
                };
                _strip.Controls.Add(t);
            }
            _strip.ResumeLayout(true);
            if (_strip.Controls.Count > _cur) _strip.ScrollControlIntoView(_strip.Controls[_cur]);
        }

        private void ShowMenu(Control anchor, ToolStripItem[] items, Point at)
        {
            var m = new ContextMenuStrip();
            m.Items.AddRange(items);
            m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
            m.Show(anchor, at);
        }

        private ToolStripItem[] SlideMenu() => new ToolStripItem[]
        {
            Item("Slide baru  (Ctrl+M)", () => AddSlide(SlideLayout.TitleContent)),
            Item("Duplikat slide", DuplicateSlide),
            Item("Hapus slide", DeleteSlide),
            new ToolStripSeparator(),
            Item("Pindah ke atas", () => MoveSlide(-1)),
            Item("Pindah ke bawah", () => MoveSlide(1)),
            new ToolStripSeparator(),
            Item("Putar dari slide ini  (Shift+F5)", () => Play(true)),
        };

        private void RefreshThumb(int index)
        {
            if (index < 0 || index >= _strip.Controls.Count || _strip.Controls[index] is not ThumbView t) return;
            var old = t.Image;
            t.Image = _deck.RenderBitmap(index, t.ImageWidth);
            old?.Dispose();
            t.Invalidate();
        }

        private void GoTo(int index)
        {
            CommitEdit();
            index = Math.Clamp(index, 0, _deck.Slides.Count - 1);
            if (index == _cur && _sel < 0) return;
            _cur = index;
            _sel = -1;
            _hover = -1;
            if (_strip.Controls.Count > _cur) _strip.ScrollControlIntoView(_strip.Controls[_cur]);
            UpdateAll();
        }

        private void AddSlide(SlideLayout layout) => Commit(() =>
        {
            _deck.Slides.Insert(_cur + 1, _deck.NewSlide(layout));
            _cur++;
            _sel = -1;
        }, structural: true);

        private void DuplicateSlide() => Commit(() =>
        {
            _deck.Slides.Insert(_cur + 1, Cur.Clone());
            _cur++;
            _sel = -1;
        }, structural: true);

        private void DeleteSlide()
        {
            if (_deck.Slides.Count <= 1) { Theme.Toast(this, "Presentasi minimal berisi satu slide"); return; }
            Commit(() =>
            {
                _deck.Slides.RemoveAt(_cur);
                _cur = Math.Min(_cur, _deck.Slides.Count - 1);
                _sel = -1;
            }, structural: true);
        }

        private void MoveSlide(int delta)
        {
            int to = _cur + delta;
            if (to < 0 || to >= _deck.Slides.Count) return;
            Commit(() =>
            {
                var s = Cur;
                _deck.Slides.RemoveAt(_cur);
                _deck.Slides.Insert(to, s);
                _cur = to;
            }, structural: true);
        }

        /// <summary>Ganti tata letak: judul &amp; isi lama dipindah ke tempat baru; gambar, bentuk &amp; teks lain tetap.</summary>
        private void ApplyLayout(SlideLayout layout) => Commit(() =>
        {
            var old = Cur;
            var fresh = _deck.NewSlide(layout);
            fresh.Background = old.Background;
            var titles = new Queue<SlideItem>(old.Items.Where(i => i.Role == "title"));
            var bodies = new Queue<SlideItem>(old.Items.Where(i => i.Role == "body"));
            foreach (var slot in fresh.Items)
            {
                var src = slot.Role == "title" ? (titles.Count > 0 ? titles.Dequeue() : null) : slot.Role == "body" ? (bodies.Count > 0 ? bodies.Dequeue() : null) : null;
                if (src == null) continue;
                // format manual ikut pindah (seperti PowerPoint); posisi & ukuran kotak mengikuti tata letak baru
                slot.Text = src.Text; slot.Color = src.Color; slot.Font = src.Font; slot.Size = src.Size;
                slot.Bold = src.Bold; slot.Italic = src.Italic; slot.Underline = src.Underline; slot.Bullets = src.Bullets;
                GrowToFit(slot);
            }
            // judul/isi yang tidak mendapat tempat tetap dipertahankan sebagai kotak teks biasa
            foreach (var rest in titles.Concat(bodies).Where(i => i.Text.Length > 0)) { rest.Role = ""; fresh.Items.Add(rest); }
            fresh.Items.AddRange(old.Items.Where(i => i.Role is "" ));
            _deck.Slides[_cur] = fresh;
            _sel = -1;
        });

        // ================================================================ ELEMEN

        private void Select(int index)
        {
            _sel = index;
            UpdateRibbon();
            UpdateStatus();
            _canvas.Invalidate();
        }

        /// <summary>Ubah format elemen terpilih (teks; <paramref name="anyKind"/> = juga gambar/bentuk).</summary>
        private void Format(Action<SlideItem> change, bool anyKind = false)
        {
            var it = Sel;
            if (it == null || (!anyKind && !it.HasText)) return;
            int idx = _sel;
            Commit(() =>
            {
                var target = Cur.Items[idx];
                change(target);
                if (target.Kind == SlideItemKind.Text) GrowToFit(target);
            });
            _canvas.Focus();
        }

        private static void GrowToFit(SlideItem i)
        {
            float need = SlideDeck.TextHeight(i);
            if (need > i.Box.Height) i.Box = new RectangleF(i.Box.X, i.Box.Y, i.Box.Width, need);
        }

        private void InsertText()
        {
            var t = _deck.Theme;
            var box = new RectangleF(_deck.W * 0.25f, _deck.H * 0.42f, _deck.W * 0.5f, 56);
            Commit(() =>
            {
                Cur.Items.Add(new SlideItem { Kind = SlideItemKind.Text, Box = box, Size = 24, Font = t.Font, Color = t.Text });
                _sel = Cur.Items.Count - 1;
            });
            BeginEdit(_sel);
        }

        private void InsertShape(SlideShape shape)
        {
            var t = _deck.Theme;
            float w = shape == SlideShape.Arrow ? 260 : 220, h = shape == SlideShape.Arrow ? 120 : 160;
            Commit(() =>
            {
                Cur.Items.Add(new SlideItem
                {
                    Kind = SlideItemKind.Shape, Shape = shape, Box = new RectangleF((_deck.W - w) / 2, (_deck.H - h) / 2, w, h),
                    Fill = t.Accent, Color = t.Accent.GetBrightness() < 0.55f ? Color.White : Color.Black,
                    Font = t.Font, Size = 20, Align = 1, VAlign = 1
                });
                _sel = Cur.Items.Count - 1;
            });
        }

        private void InsertImageDialog()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih gambar", Filter = "Gambar (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = true };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in ofd.FileNames) InsertImageFile(f);
        }

        private void InsertImageFile(string path)
        {
            try { InsertImageBytes(File.ReadAllBytes(path)); }
            catch (Exception ex) { Theme.Toast(this, $"Gambar tidak dapat dipakai: {ex.Message}"); }
        }

        private void InsertImageBytes(byte[] raw)
        {
            var (bytes, ext, size) = SlideDeck.PrepareImage(raw);
            float k = Math.Min(Math.Min(_deck.W * 0.6f / size.Width, _deck.H * 0.6f / size.Height), 1.5f);
            float w = Math.Max(16, size.Width * k), h = Math.Max(16, size.Height * k);
            // gambar berikutnya sedikit bergeser agar tidak menumpuk persis
            float shift = Cur.Items.Count(i => i.Kind == SlideItemKind.Image) % 6 * 24;
            Commit(() =>
            {
                Cur.Items.Add(new SlideItem { Kind = SlideItemKind.Image, Box = new RectangleF((_deck.W - w) / 2 + shift, (_deck.H - h) / 2 + shift, w, h), Image = bytes, ImageExt = ext });
                _sel = Cur.Items.Count - 1;
            });
        }

        private void DeleteSelected()
        {
            if (Sel == null) return;
            int idx = _sel;
            Commit(() => { Cur.Items.RemoveAt(idx); _sel = -1; });
        }

        private void DuplicateSelected()
        {
            if (Sel is not { } it) { DuplicateSlide(); return; }
            Commit(() =>
            {
                var c = it.Clone();
                c.Box = new RectangleF(c.Box.X + 20, c.Box.Y + 20, c.Box.Width, c.Box.Height);
                Cur.Items.Add(c);
                _sel = Cur.Items.Count - 1;
            });
        }

        private void CopySelected(bool cut)
        {
            if (Sel is not { } it) return;
            _clip = new List<SlideItem> { it.Clone() };
            // isi papan klip sistem dengan teksnya (sekaligus mengosongkan gambar lama agar Ctrl+V memakai elemen ini)
            try { Clipboard.SetText(it.Text.Length > 0 ? it.Text : " "); } catch { }
            if (cut) DeleteSelected();
        }

        private void Paste()
        {
            try
            {
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                {
                    using (img)
                    using (var ms = new MemoryStream())
                    {
                        img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        InsertImageBytes(ms.ToArray());
                    }
                    return;
                }
            }
            catch { }
            if (_clip is { Count: > 0 })
            {
                Commit(() =>
                {
                    foreach (var c in _clip.Select(i => i.Clone()))
                    {
                        c.Box = new RectangleF(c.Box.X + 20, c.Box.Y + 20, c.Box.Width, c.Box.Height);
                        Cur.Items.Add(c);
                    }
                    _sel = Cur.Items.Count - 1;
                });
                // tempel berikutnya bergeser lagi
                foreach (var c in _clip) c.Box = new RectangleF(c.Box.X + 20, c.Box.Y + 20, c.Box.Width, c.Box.Height);
                return;
            }
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is { Length: > 0 } text)
                {
                    var t = _deck.Theme;
                    Commit(() =>
                    {
                        var item = new SlideItem { Kind = SlideItemKind.Text, Box = new RectangleF(_deck.W * 0.15f, _deck.H * 0.3f, _deck.W * 0.7f, 60), Text = text.Replace("\r\n", "\n"), Size = 20, Font = t.Font, Color = t.Text };
                        GrowToFit(item);
                        Cur.Items.Add(item);
                        _sel = Cur.Items.Count - 1;
                    });
                }
            }
            catch { }
        }

        private void Arrange(int delta)
        {
            if (Sel == null) return;
            int from = _sel;
            int to = delta switch
            {
                int.MaxValue => Cur.Items.Count - 1,
                int.MinValue => 0,
                _ => Math.Clamp(from + delta, 0, Cur.Items.Count - 1)
            };
            if (to == from) return;
            Commit(() =>
            {
                var it = Cur.Items[from];
                Cur.Items.RemoveAt(from);
                Cur.Items.Insert(to, it);
                _sel = to;
            });
        }

        // ================================================================ KANVAS

        private RectangleF SlideRect()
        {
            int m = S(28);
            float aw = _canvas.ClientSize.Width - 2 * m, ah = _canvas.ClientSize.Height - 2 * m;
            if (aw <= 10 || ah <= 10) return new RectangleF(0, 0, 1, 1);
            float k = Math.Min(aw / _deck.W, ah / _deck.H);
            float w = _deck.W * k, h = _deck.H * k;
            return new RectangleF((_canvas.ClientSize.Width - w) / 2f, (_canvas.ClientSize.Height - h) / 2f, w, h);
        }

        private float CanvasScale => SlideRect().Width / _deck.W;
        private PointF ToSlide(Point p) { var r = SlideRect(); float k = r.Width / _deck.W; return new PointF((p.X - r.X) / k, (p.Y - r.Y) / k); }
        private RectangleF ToScreen(RectangleF b) { var r = SlideRect(); float k = r.Width / _deck.W; return new RectangleF(r.X + b.X * k, r.Y + b.Y * k, b.Width * k, b.Height * k); }

        private static PointF[] HandlePoints(RectangleF b) => new[]
        {
            new PointF(b.Left, b.Top), new PointF(b.Left + b.Width / 2, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Top + b.Height / 2),
            new PointF(b.Right, b.Bottom), new PointF(b.Left + b.Width / 2, b.Bottom), new PointF(b.Left, b.Bottom), new PointF(b.Left, b.Top + b.Height / 2),
        };

        private int HitHandle(Point p)
        {
            if (Sel is not { } it || _editor != null) return -1;
            var pts = HandlePoints(ToScreen(it.Box));
            float r = S(7);
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(p.X - pts[i].X) <= r && Math.Abs(p.Y - pts[i].Y) <= r) return i;
            return -1;
        }

        private int HitItem(PointF sp)
        {
            float pad = 4 / Math.Max(0.01f, CanvasScale);
            for (int i = Cur.Items.Count - 1; i >= 0; i--)
            {
                var b = Cur.Items[i].Box;
                b.Inflate(pad, pad);
                if (b.Contains(sp)) return i;
            }
            return -1;
        }

        private static Cursor HandleCursor(int h) => h switch
        {
            0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, _ => Cursors.SizeWE
        };

        private void PaintCanvas(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.CANVAS);
            var r = SlideRect();
            using (var shadow = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 40, 0, 0, 0)))
                g.FillRectangle(shadow, r.X + S(2), r.Y + S(3), r.Width, r.Height);
            _deck.Render(g, Cur, r, hints: true, skipText: _editItem);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var border = new Pen(Theme.LINE)) g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);

            if (_hover >= 0 && _hover != _sel && _hover < Cur.Items.Count && _drag == DragMode.None)
            {
                var hb = ToScreen(Cur.Items[_hover].Box);
                using var hp = new Pen(Color.FromArgb(140, Theme.ACCENT), 1f);
                g.DrawRectangle(hp, hb.X, hb.Y, hb.Width, hb.Height);
            }
            if (Sel is { } it)
            {
                var b = ToScreen(it.Box);
                using var pen = new Pen(Theme.ACCENT, Math.Max(1.5f, Theme.Scale * 1.5f));
                if (_editor != null) pen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(pen, b.X, b.Y, b.Width, b.Height);
                if (_editor == null)
                {
                    float hs = S(4);
                    using var fill = new SolidBrush(Color.White);
                    using var hpen = new Pen(Theme.ACCENT, 1.5f);
                    foreach (var p in HandlePoints(b))
                    {
                        g.FillEllipse(fill, p.X - hs, p.Y - hs, hs * 2, hs * 2);
                        g.DrawEllipse(hpen, p.X - hs, p.Y - hs, hs * 2, hs * 2);
                    }
                }
            }
            if (_drag == DragMode.Move && (_snapX || _snapY))
            {
                using var guide = new Pen(Color.FromArgb(200, 0xE8, 0x3E, 0x8C), 1f) { DashStyle = DashStyle.Dash };
                if (_snapX) g.DrawLine(guide, r.X + r.Width / 2, r.Top, r.X + r.Width / 2, r.Bottom);
                if (_snapY) g.DrawLine(guide, r.Left, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
            }
        }

        private void CanvasDown(object? sender, MouseEventArgs e)
        {
            if (_editor != null)
            {
                // klik di luar kotak yang sedang disunting = selesai menyunting
                CommitEdit();
            }
            _canvas.Focus();
            var sp = ToSlide(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                Select(HitItem(sp));
                ShowMenu(_canvas, ItemMenu(), e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            int h = HitHandle(e.Location);
            if (h >= 0) { _drag = DragMode.Resize; _handle = h; }
            else
            {
                int i = HitItem(sp);
                Select(i);
                _drag = i >= 0 ? DragMode.Move : DragMode.None;
            }
            if (_drag != DragMode.None && Sel is { } it)
            {
                _dragUndo = Snapshot();
                _dragMoved = false;
                _downScreen = e.Location;
                _downSlide = sp;
                _boxStart = it.Box;
                _canvas.Capture = true;
            }
        }

        private void CanvasMove(object? sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None)
            {
                int h = HitHandle(e.Location);
                int over = h >= 0 ? _sel : HitItem(ToSlide(e.Location));
                _canvas.Cursor = h >= 0 ? HandleCursor(h) : over >= 0 ? (_editor != null && over == _sel ? Cursors.IBeam : Cursors.SizeAll) : Cursors.Default;
                if (over != _hover) { _hover = over; _canvas.Invalidate(); }
                return;
            }
            if (Sel is not { } it) return;
            if (!_dragMoved)
            {
                if (Math.Abs(e.X - _downScreen.X) < S(3) && Math.Abs(e.Y - _downScreen.Y) < S(3)) return;
                _dragMoved = true;
                if (_dragUndo != null) PushUndo(_dragUndo);
            }
            var sp = ToSlide(e.Location);
            float dx = sp.X - _downSlide.X, dy = sp.Y - _downSlide.Y;
            if (_drag == DragMode.Move)
            {
                var nb = new RectangleF(_boxStart.X + dx, _boxStart.Y + dy, _boxStart.Width, _boxStart.Height);
                float tol = S(6) / Math.Max(0.01f, CanvasScale);
                bool shift = (ModifierKeys & Keys.Shift) != 0;
                if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) nb.Y = _boxStart.Y; else nb.X = _boxStart.X; }   // gerak lurus
                _snapX = Math.Abs(nb.X + nb.Width / 2 - _deck.W / 2) < tol;
                _snapY = Math.Abs(nb.Y + nb.Height / 2 - _deck.H / 2) < tol;
                if (_snapX) nb.X = _deck.W / 2 - nb.Width / 2;
                if (_snapY) nb.Y = _deck.H / 2 - nb.Height / 2;
                it.Box = nb;
            }
            else
            {
                float l = _boxStart.Left, t = _boxStart.Top, r = _boxStart.Right, b = _boxStart.Bottom;
                if (_handle is 0 or 6 or 7) l += dx;
                if (_handle is 2 or 3 or 4) r += dx;
                if (_handle is 0 or 1 or 2) t += dy;
                if (_handle is 4 or 5 or 6) b += dy;
                const float min = 12;
                if (r - l < min) { if (_handle is 0 or 6 or 7) l = r - min; else r = l + min; }
                if (b - t < min) { if (_handle is 0 or 1 or 2) t = b - min; else b = t + min; }
                // sudut gambar menjaga proporsi (Shift = bebas); untuk elemen lain Shift = jaga proporsi
                bool corner = _handle is 0 or 2 or 4 or 6;
                bool keep = corner && ((it.Kind == SlideItemKind.Image) ^ ((ModifierKeys & Keys.Shift) != 0));
                if (keep && _boxStart.Height > 0)
                {
                    float ratio = _boxStart.Width / _boxStart.Height;
                    float w = r - l, h = w / ratio;
                    if (_handle is 0 or 2) t = b - h; else b = t + h;
                }
                it.Box = RectangleF.FromLTRB(l, t, r, b);
            }
            _canvas.Invalidate();
        }

        private void CanvasUp(object? sender, MouseEventArgs e)
        {
            if (_drag == DragMode.None) return;
            _canvas.Capture = false;
            bool moved = _dragMoved;
            _drag = DragMode.None;
            _snapX = _snapY = false;
            _dragUndo = null;
            if (moved)
            {
                if (Sel is { Kind: SlideItemKind.Text } it) GrowToFit(it);
                MarkDirty();
                RefreshThumb(_cur);
            }
            _canvas.Invalidate();
        }

        private void CanvasDoubleClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int i = HitItem(ToSlide(e.Location));
            if (i >= 0 && Cur.Items[i].HasText) BeginEdit(i);
            else if (i < 0) InsertTextAt(ToSlide(e.Location));
        }

        /// <summary>Klik dua kali di area kosong = kotak teks baru di titik itu.</summary>
        private void InsertTextAt(PointF p)
        {
            var t = _deck.Theme;
            float w = Math.Min(_deck.W * 0.4f, _deck.W - p.X);
            if (w < 60) return;
            Commit(() =>
            {
                Cur.Items.Add(new SlideItem { Kind = SlideItemKind.Text, Box = new RectangleF(p.X, p.Y - 20, w, 48), Size = 24, Font = t.Font, Color = t.Text });
                _sel = Cur.Items.Count - 1;
            });
            BeginEdit(_sel);
        }

        private void CanvasKeyPress(object? sender, KeyPressEventArgs e)
        {
            if (_editor != null || char.IsControl(e.KeyChar) || Sel is not { HasText: true }) return;
            e.Handled = true;
            BeginEdit(_sel, e.KeyChar.ToString());
        }

        private ToolStripItem[] ItemMenu()
        {
            if (Sel == null)
                return new ToolStripItem[]
                {
                    Item("Tempel  (Ctrl+V)", Paste),
                    new ToolStripSeparator(),
                    Item("Kotak teks", InsertText), Item("Gambar…", InsertImageDialog),
                    new ToolStripSeparator(),
                }.Concat(SlideMenu()).ToArray();
            var list = new List<ToolStripItem>();
            if (Sel.HasText) list.Add(Item("Sunting teks  (Enter)", () => BeginEdit(_sel)));
            list.AddRange(new ToolStripItem[]
            {
                Item("Potong  (Ctrl+X)", () => CopySelected(true)), Item("Salin  (Ctrl+C)", () => CopySelected(false)),
                Item("Tempel  (Ctrl+V)", Paste), Item("Duplikat  (Ctrl+D)", DuplicateSelected),
                new ToolStripSeparator(),
                Item("Ke paling depan", () => Arrange(int.MaxValue)), Item("Ke paling belakang", () => Arrange(int.MinValue)),
                new ToolStripSeparator(),
                Item("Hapus  (Delete)", DeleteSelected),
            });
            return list.ToArray();
        }

        // ================================================================ SUNTING TEKS

        private void BeginEdit(int index, string? replaceWith = null)
        {
            CommitEdit();
            if (index < 0 || index >= Cur.Items.Count || !Cur.Items[index].HasText) return;
            var it = Cur.Items[index];
            _sel = index;
            _editItem = it;
            _editUndo = Snapshot();
            _editOriginal = it.Text;
            _editBoxOriginal = it.Box;
            var tb = new TextBox
            {
                Multiline = true, AcceptsReturn = true, AcceptsTab = false, WordWrap = true, ScrollBars = ScrollBars.None,
                BorderStyle = BorderStyle.None, Text = (replaceWith ?? it.Text).Replace("\n", "\r\n"),
                ForeColor = it.Color, BackColor = it.Fill.IsEmpty ? Cur.Background : it.Fill,
                TextAlign = it.Align switch { 1 => HorizontalAlignment.Center, 2 => HorizontalAlignment.Right, _ => HorizontalAlignment.Left },
                AccessibleName = "Teks slide"
            };
            _editor = tb;
            _editFormatChanged = false;
            ApplyEditorFont(force: true);
            tb.TextChanged += (s, e) =>
            {
                if (_editItem == null || _editor != tb) return;
                _editItem.Text = tb.Text.Replace("\r\n", "\n");
                if (_editItem.Kind == SlideItemKind.Text) GrowToFit(_editItem);
                PositionEditor();
                _canvas.Invalidate();
            };
            tb.Leave += (s, e) => { if (_editor == tb) CommitEdit(); };
            _canvas.Controls.Add(tb);
            PositionEditor();
            tb.Focus();
            if (replaceWith == null) tb.SelectAll(); else tb.Select(tb.TextLength, 0);
            if (replaceWith != null) { it.Text = replaceWith; if (it.Kind == SlideItemKind.Text) GrowToFit(it); PositionEditor(); }
            _canvas.Invalidate();
            UpdateRibbon();
            _status.Text = "Menyunting teks · Esc atau klik di luar kotak untuk selesai · Enter = baris baru";
        }

        private void ApplyEditorFont(bool force = false)
        {
            if (_editor == null || _editItem == null) return;
            float k = CanvasScale;
            if (!force && _editorFont != null && Math.Abs(k - _editorFontScale) < 0.0005f) return;
            var old = _editorFont;
            _editorFont = SlideDeck.MakeFont(_editItem, k);
            _editorFontScale = k;
            _editor.Font = _editorFont;
            old?.Dispose();
        }

        private void PositionEditor()
        {
            if (_editor == null || _editItem == null) return;
            ApplyEditorFont();
            var area = ToScreen(SlideDeck.TextArea(_editItem.Box));
            int h = Math.Max((int)Math.Ceiling(area.Height), _editor.Font.Height + 2);
            _editor.Bounds = new Rectangle((int)Math.Floor(area.X), (int)Math.Floor(area.Y), Math.Max(20, (int)Math.Ceiling(area.Width)), h);
        }

        private void CommitEdit()
        {
            if (_editor == null || _editItem == null) return;
            var tb = _editor;
            var it = _editItem;
            _editor = null;
            _editItem = null;
            it.Text = tb.Text.Replace("\r\n", "\n");
            if (it.Kind == SlideItemKind.Text) GrowToFit(it);
            // kotak teks biasa yang ditinggalkan kosong dibuang (seperti PowerPoint); judul/isi tata letak tetap
            int idx = Cur.Items.IndexOf(it);
            bool drop = it.Kind == SlideItemKind.Text && it.Role == "" && it.Text.Trim().Length == 0 && idx >= 0;
            if (drop) { Cur.Items.RemoveAt(idx); _sel = -1; }
            if ((it.Text != _editOriginal || it.Box != _editBoxOriginal || _editFormatChanged) && _editUndo != null && !(drop && _editOriginal.Length == 0))
            {
                PushUndo(_editUndo);
                MarkDirty();
            }
            _editUndo = null;
            _canvas.Controls.Remove(tb);
            tb.Dispose();
            _editorFont?.Dispose();
            _editorFont = null;
            RefreshThumb(_cur);
            UpdateAll();
        }

        // ================================================================ PINTASAN

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
            if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { SaveAs(); return true; }
            if (key == Keys.F5) { Play(shift); return true; }

            if (_editor != null && _editor.Focused)
            {
                if (keyData == Keys.Escape) { CommitEdit(); _canvas.Focus(); return true; }
                if (ctrl && key is Keys.B or Keys.I or Keys.U && _editItem is { } ed)
                {
                    if (key == Keys.B) ed.Bold = !ed.Bold; else if (key == Keys.I) ed.Italic = !ed.Italic; else ed.Underline = !ed.Underline;
                    _editFormatChanged = true;
                    ApplyEditorFont(force: true);
                    if (ed.Kind == SlideItemKind.Text) GrowToFit(ed);
                    PositionEditor();
                    UpdateRibbon();
                    return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (ActiveControl is ComboBox || ActiveControl is TextBoxBase) return base.ProcessCmdKey(ref msg, keyData);

            bool onStrip = _strip.ContainsFocus;
            if (ctrl)
            {
                switch (key)
                {
                    case Keys.Z when shift: Redo(); return true;
                    case Keys.Z: Undo(); return true;
                    case Keys.Y: Redo(); return true;
                    case Keys.M: AddSlide(SlideLayout.TitleContent); return true;
                    case Keys.D: if (onStrip) DuplicateSlide(); else DuplicateSelected(); return true;
                    case Keys.C: CopySelected(false); return true;
                    case Keys.X: CopySelected(true); return true;
                    case Keys.V: Paste(); return true;
                    case Keys.B when Sel is { HasText: true }: Format(i => i.Bold = !i.Bold); return true;
                    case Keys.I when Sel is { HasText: true }: Format(i => i.Italic = !i.Italic); return true;
                    case Keys.U when Sel is { HasText: true }: Format(i => i.Underline = !i.Underline); return true;
                    case Keys.Up when onStrip: MoveSlide(-1); return true;
                    case Keys.Down when onStrip: MoveSlide(1); return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            switch (key)
            {
                case Keys.Delete:
                case Keys.Back:
                    if (onStrip) DeleteSlide(); else if (Sel != null) DeleteSelected(); else return base.ProcessCmdKey(ref msg, keyData);
                    return true;
                case Keys.Escape when Sel != null: Select(-1); return true;
                case Keys.Enter or Keys.F2 when Sel is { HasText: true } && !onStrip: BeginEdit(_sel); return true;
                case Keys.PageDown: GoTo(_cur + 1); return true;
                case Keys.PageUp: GoTo(_cur - 1); return true;
                case Keys.Home when Sel == null || onStrip: GoTo(0); return true;
                case Keys.End when Sel == null || onStrip: GoTo(_deck.Slides.Count - 1); return true;
                case Keys.Left or Keys.Right or Keys.Up or Keys.Down when Sel != null && !onStrip:
                {
                    float step = shift ? 10 : 1;
                    float dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0, dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
                    Commit(() => { var b = Cur.Items[_sel].Box; Cur.Items[_sel].Box = new RectangleF(b.X + dx, b.Y + dy, b.Width, b.Height); });
                    return true;
                }
                case Keys.Up or Keys.Left: GoTo(_cur - 1); return true;
                case Keys.Down or Keys.Right: GoTo(_cur + 1); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ================================================================ PUTAR, SIMPAN, EKSPOR

        private void Play(bool fromCurrent)
        {
            CommitEdit();
            var snap = _deck.Clone();
            float aspect = snap.W / snap.H;
            var sizes = Enumerable.Repeat(new SizeF(aspect, 1), snap.Slides.Count).ToArray();
            using var doc = new PagedDocument(sizes, (i, dim) => snap.RenderBitmap(i, (int)(dim * Math.Min(1f, aspect))), new NoResource());
            using var show = new SlideshowForm(doc, _name, start: fromCurrent ? _cur : 0);
            show.ShowDialog(this);
        }

        private sealed class NoResource : IDisposable { public void Dispose() { } }

        private string BaseName => Path.GetFileNameWithoutExtension(_path ?? "Presentasi");

        private bool Save()
        {
            CommitEdit();
            if (_path == null || _deck.Foreign) return SaveAs();
            return WriteTo(_path);
        }

        private bool SaveAs()
        {
            CommitEdit();
            string suggestion = _path == null ? "Presentasi" : _deck.Foreign ? BaseName + " (edit)" : BaseName;
            string dir = _path != null ? Path.GetDirectoryName(_path)! : AppStorage.OutDir;
            using var sfd = new SaveFileDialog
            {
                Title = "Simpan presentasi", Filter = "Presentasi PowerPoint (*.pptx)|*.pptx", DefaultExt = "pptx",
                InitialDirectory = Directory.Exists(dir) ? dir : AppStorage.OutDir, FileName = suggestion, OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return false;
            if (!WriteTo(sfd.FileName)) return false;
            _deck.Foreign = false;
            return true;
        }

        private bool WriteTo(string path)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                _deck.Save(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Presentasi tidak dapat disimpan:\n{ex.Message}\n\nJika berkas sedang dibuka di PowerPoint, tutup dulu lalu coba lagi.",
                    "Editor Presentasi", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { Cursor = Cursors.Default; }
            _path = path;
            _name = Path.GetFileName(path);
            _dirty = false;
            UpdateTitle();
            AppStorage.AddRecent(path);
            AppStorage.SetLastJob("Presentasi Disimpan", path);
            Theme.Toast(this, $"Tersimpan: {_name}");
            return true;
        }

        private void ExportPdf()
        {
            CommitEdit();
            using var sfd = new SaveFileDialog
            {
                Title = "Ekspor presentasi ke PDF", Filter = "PDF (*.pdf)|*.pdf", DefaultExt = "pdf",
                InitialDirectory = AppStorage.OutDir, FileName = Path.GetFileName(AppStorage.NamedOut(BaseName, "pdf")), OverwritePrompt = true
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            var snap = _deck.Clone();
            string target = sfd.FileName;
            var (ok, _) = Ui.Busy(this, "Mengekspor PDF…", progress => { snap.ExportPdf(target, progress); return true; });
            if (!ok) return;
            AppStorage.AddRecent(target);
            AppStorage.SetLastJob("Presentasi ke PDF", target);
            Theme.Toast(this, $"PDF dibuat: {Path.GetFileName(target)}");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CommitEdit();
            if (_dirty && e.CloseReason != CloseReason.WindowsShutDown)
            {
                var r = MessageBox.Show(this, $"Simpan perubahan pada \"{_name}\"?", "Editor Presentasi", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel || (r == DialogResult.Yes && !Save())) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (Control c in _strip.Controls) (c as ThumbView)?.Image?.Dispose();
            _tips.Dispose();
            base.OnFormClosed(e);
        }

        // ================================================================ KONTROL

        /// <summary>Kanvas slide: digambar sendiri, bisa fokus, menerima tombol panah.</summary>
        private sealed class CanvasView : Control
        {
            public CanvasView()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                TabStop = true;
            }
            protected override bool IsInputKey(Keys keyData) =>
                (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
        }

        /// <summary>Gambar kecil satu slide di panel kiri: nomor + pratinjau, garis biru bila terpilih.</summary>
        private sealed class ThumbView : Control
        {
            public readonly int Index;
            public readonly int ImageWidth;
            private readonly int _imageHeight;
            private bool _selected, _hot;
            public Bitmap? Image;

            public ThumbView(int index, int width, int height)
            {
                Index = index; ImageWidth = width; _imageHeight = height;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                Size = new Size(width + S(34), height + S(14));
                Margin = new Padding(0, 0, 0, S(4));
                Cursor = Cursors.Hand;
                TabStop = true;
                AccessibleName = $"Slide {index + 1}";
                AccessibleRole = AccessibleRole.ListItem;
            }

            public void SetSelected(bool on) { if (_selected == on) return; _selected = on; Invalidate(); }
            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(Theme.SURFACE);
                var img = new Rectangle(S(30), S(6), ImageWidth, _imageHeight);
                TextRenderer.DrawText(g, (Index + 1).ToString(), Theme.FontLabel(9f), new Rectangle(0, S(6), S(22), S(20)),
                    _selected ? Theme.ACCENT : Theme.MUTED, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                if (Image != null) g.DrawImage(Image, img);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (_selected)
                {
                    using var pen = new Pen(Theme.ACCENT, S(2) + 0.5f);
                    g.DrawRectangle(pen, img.X - 1, img.Y - 1, img.Width + 1, img.Height + 1);
                }
                else
                {
                    using var pen = new Pen(_hot ? Theme.LINE_STRONG : Theme.LINE);
                    g.DrawRectangle(pen, img.X - 1, img.Y - 1, img.Width + 1, img.Height + 1);
                }
                if (Focused && ShowFocusCues)
                    using (var fp = new Pen(Theme.ACCENT, 1f) { DashStyle = DashStyle.Dot }) g.DrawRectangle(fp, 1, 1, Width - 3, Height - 3);
            }
        }
    }
}
