using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    // =====================================================================
    //  MODEL OBJEK EDIT (semua koordinat dalam piksel bitmap halaman)
    // =====================================================================

    internal enum EditTool { Select, Text, EditText, Highlight, Whiteout, Box }

    internal abstract class EditObj
    {
        public RectangleF Bounds;
        public abstract void Draw(Graphics g);
        public EditObj Clone() => (EditObj)MemberwiseClone();
    }

    internal sealed class TextObj : EditObj
    {
        public string Text = "Teks";
        public string FontName = "Segoe UI";
        public float SizePx = 28f;
        public bool Bold, Italic, Underline;
        public Color Ink = Color.Black;
        public StringAlignment Align = StringAlignment.Near;

        public FontStyle Style
        {
            get
            {
                var s = FontStyle.Regular;
                if (Bold) s |= FontStyle.Bold;
                if (Italic) s |= FontStyle.Italic;
                if (Underline) s |= FontStyle.Underline;
                return s;
            }
        }

        public Font MakeFont(float scale = 1f)
        {
            try { return new Font(FontName, Math.Max(1f, SizePx * scale), Style, GraphicsUnit.Pixel); }
            catch { return new Font("Segoe UI", Math.Max(1f, SizePx * scale), FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        public override void Draw(Graphics g)
        {
            using var f = MakeFont();
            using var b = new SolidBrush(Ink);
            using var sf = new StringFormat(StringFormatFlags.NoClip) { Alignment = Align };
            g.DrawString(Text, f, b, Bounds, sf);
        }
    }

    internal sealed class RectObj : EditObj
    {
        public int Kind;                       // 0 = sorot, 1 = tutup/hapus, 2 = kotak garis
        public Color Fill = Color.White;
        public Color Line = Color.Black;
        public float LineW = 3f;

        public override void Draw(Graphics g)
        {
            if (Kind == 2)
            {
                using var p = new Pen(Line, LineW);
                g.DrawRectangle(p, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height);
            }
            else
            {
                using var b = new SolidBrush(Fill);
                g.FillRectangle(b, Bounds);
            }
        }
    }

    internal sealed class ImageObj : EditObj
    {
        public Bitmap Img;
        public ImageObj(Bitmap img) { Img = img; }

        public override void Draw(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Img, Bounds);
        }
    }

    // =====================================================================
    //  KANVAS HALAMAN
    // =====================================================================

    internal sealed class EditCanvas : Control
    {
        public Bitmap? Page;
        public List<EditObj> Objects = new();
        public float Zoom = 1f;
        public EditTool Tool = EditTool.Select;
        public EditObj? Selected;

        public event Action? SelectionChanged;
        public event Action? Changing;                 // dipanggil SEBELUM objek diubah (untuk undo)
        public event Action? Changed;                  // dipanggil SESUDAH objek berubah
        public event Action<PointF>? PointClicked;     // alat Teks / Edit Teks Asli
        public event Action<RectangleF>? RectDrawn;    // alat Sorot / Tutup / Kotak

        private bool _moving, _resizing, _drawing, _undoPushed;
        private PointF _dragStart, _dragCur;
        private RectangleF _origBounds;
        private TextBox? _editor;
        private TextObj? _editing;

        public bool IsEditing => _editor != null;

        public EditCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.White;
        }

        // ---------- util ----------
        private RectangleF Scaled(RectangleF r) => new RectangleF(r.X * Zoom, r.Y * Zoom, r.Width * Zoom, r.Height * Zoom);

        private Rectangle HandleRect(EditObj o)
        {
            var r = Scaled(o.Bounds);
            int hs = Theme.S(12);
            return new Rectangle((int)r.Right - hs / 2, (int)r.Bottom - hs / 2, hs, hs);
        }

        private EditObj? HitTest(PointF pt)
        {
            for (int i = Objects.Count - 1; i >= 0; i--)
            {
                var b = Objects[i].Bounds;
                b.Inflate(4f, 4f);
                if (b.Contains(pt)) return Objects[i];
            }
            return null;
        }

        public void AutoHeight(TextObj t)
        {
            using var g = CreateGraphics();
            using var f = t.MakeFont();
            using var sf = new StringFormat(StringFormatFlags.NoClip) { Alignment = t.Align };
            var sz = g.MeasureString(string.IsNullOrEmpty(t.Text) ? " " : t.Text, f, (int)Math.Max(20f, t.Bounds.Width), sf);
            t.Bounds = new RectangleF(t.Bounds.X, t.Bounds.Y, t.Bounds.Width, Math.Max(sz.Height + 4f, t.SizePx * 1.3f));
        }

        public void SelectObject(EditObj? o)
        {
            Selected = o;
            SelectionChanged?.Invoke();
            Invalidate();
        }

        public void DeleteSelected()
        {
            if (Selected == null || _editor != null) return;
            Changing?.Invoke();
            Objects.Remove(Selected);
            Selected = null;
            SelectionChanged?.Invoke();
            Changed?.Invoke();
            Invalidate();
        }

        // ---------- edit teks langsung di halaman ----------
        public void BeginEdit(TextObj t)
        {
            EndEdit(true);
            _editing = t;
            Selected = t;
            var r = Scaled(t.Bounds);
            var tb = new TextBox
            {
                Multiline = true,
                AcceptsReturn = true,
                BorderStyle = BorderStyle.FixedSingle,
                Text = t.Text,
                ForeColor = t.Ink,
                BackColor = Color.FromArgb(255, 255, 251, 230),
                Font = t.MakeFont(Zoom),
                Location = new Point((int)r.X, (int)r.Y),
                Size = new Size((int)Math.Max(60f, r.Width), (int)Math.Max(24f, r.Height))
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
                else if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; EndEdit(true); }
            };
            tb.Leave += (s, e) => { if (IsHandleCreated) BeginInvoke(new Action(() => EndEdit(true))); };
            Controls.Add(tb);
            _editor = tb;
            tb.Focus();
            tb.SelectAll();
            Invalidate();
        }

        public void EndEdit(bool commit)
        {
            if (_editor == null) return;
            var tb = _editor;
            var t = _editing;
            _editor = null;
            _editing = null;
            if (commit && t != null && tb.Text != t.Text)
            {
                Changing?.Invoke();
                t.Text = tb.Text;
                AutoHeight(t);
                Changed?.Invoke();
            }
            Controls.Remove(tb);
            tb.Dispose();
            Invalidate();
        }

        // ---------- gambar ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.White);
            if (Page == null) return;

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Page, 0, 0, Width, Height);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            var state = g.Save();
            g.ScaleTransform(Zoom, Zoom);
            foreach (var o in Objects)
            {
                if (ReferenceEquals(o, _editing)) continue;
                o.Draw(g);
            }
            g.Restore(state);

            if (Selected != null && Objects.Contains(Selected))
            {
                var r = Scaled(Selected.Bounds);
                using var pen = new Pen(Theme.ACCENT, 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                var h = HandleRect(Selected);
                g.FillRectangle(Brushes.White, h);
                using var hp = new Pen(Theme.ACCENT, 2f);
                g.DrawRectangle(hp, h);
            }

            if (_drawing)
            {
                var rr = Norm(_dragStart, _dragCur);
                var sr = Scaled(rr);
                using var pen = new Pen(Theme.ACCENT, 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, sr.X, sr.Y, sr.Width, sr.Height);
            }
        }

        private static RectangleF Norm(PointF a, PointF b)
            => new RectangleF(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        // ---------- mouse ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Page == null) return;
            Focus();
            var pt = new PointF(e.X / Zoom, e.Y / Zoom);

            switch (Tool)
            {
                case EditTool.Select:
                    if (Selected != null && HandleRect(Selected).Contains(e.Location))
                    {
                        _resizing = true; _undoPushed = false; _dragStart = pt; _origBounds = Selected.Bounds;
                        return;
                    }
                    var hit = HitTest(pt);
                    if (!ReferenceEquals(hit, Selected)) { Selected = hit; SelectionChanged?.Invoke(); Invalidate(); }
                    if (hit != null)
                    {
                        _moving = true; _undoPushed = false; _dragStart = pt; _origBounds = hit.Bounds;
                    }
                    break;

                case EditTool.Text:
                case EditTool.EditText:
                    PointClicked?.Invoke(pt);
                    break;

                default:
                    _drawing = true;
                    _dragStart = pt;
                    _dragCur = pt;
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Page == null) return;
            var pt = new PointF(e.X / Zoom, e.Y / Zoom);

            if ((_moving || _resizing) && Selected != null)
            {
                float dx = pt.X - _dragStart.X, dy = pt.Y - _dragStart.Y;
                if (!_undoPushed)
                {
                    if (Math.Abs(dx) < 2f && Math.Abs(dy) < 2f) return;
                    Changing?.Invoke();
                    _undoPushed = true;
                }
                if (_moving)
                {
                    Selected.Bounds = new RectangleF(_origBounds.X + dx, _origBounds.Y + dy, _origBounds.Width, _origBounds.Height);
                }
                else
                {
                    float w = Math.Max(12f, _origBounds.Width + dx);
                    float h = Math.Max(12f, _origBounds.Height + dy);
                    if (Selected is ImageObj && _origBounds.Width > 0)
                        h = w * _origBounds.Height / _origBounds.Width;
                    Selected.Bounds = new RectangleF(_origBounds.X, _origBounds.Y, w, h);
                }
                Invalidate();
                return;
            }

            if (_drawing)
            {
                _dragCur = pt;
                Invalidate();
                return;
            }

            // kursor
            if (Tool == EditTool.Select)
            {
                if (Selected != null && HandleRect(Selected).Contains(e.Location)) Cursor = Cursors.SizeNWSE;
                else if (HitTest(pt) != null) Cursor = Cursors.SizeAll;
                else Cursor = Cursors.Default;
            }
            else if (Tool == EditTool.Text || Tool == EditTool.EditText) Cursor = Cursors.IBeam;
            else Cursor = Cursors.Cross;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drawing)
            {
                _drawing = false;
                var r = Norm(_dragStart, _dragCur);
                Invalidate();
                if (r.Width > 4f && r.Height > 4f) RectDrawn?.Invoke(r);
                return;
            }
            if (_moving || _resizing)
            {
                bool changed = _undoPushed;
                _moving = false;
                _resizing = false;
                _undoPushed = false;
                if (changed)
                {
                    if (Selected is TextObj t && Math.Abs(t.Bounds.Width - _origBounds.Width) > 0.5f) AutoHeight(t);
                    Changed?.Invoke();
                    Invalidate();
                }
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Tool != EditTool.Select || Page == null) return;
            var hit = HitTest(new PointF(e.X / Zoom, e.Y / Zoom));
            if (hit is TextObj t)
            {
                _moving = false; _resizing = false;
                BeginEdit(t);
            }
        }
    }

    // =====================================================================
    //  FORM EDITOR PDF
    // =====================================================================

    /// <summary>
    /// Editor PDF bergaya Word: klik teks asli untuk mengubahnya (OCR offline), tambah kotak teks, gambar,
    /// tanda tangan, sorot, tutup bagian, kotak garis; geser/ubah ukuran objek; undo/redo; simpan sebagai PDF baru.
    /// Catatan teknis: halaman dirender menjadi gambar lalu ditulis ulang, sehingga teks asli PDF menjadi gambar;
    /// opsi OCR saat simpan menambahkan lapisan teks tak terlihat agar hasil tetap bisa dicari/disalin.
    /// </summary>
    public sealed class PdfEditorForm : Form
    {
        public string? SavedPath { get; private set; }
        public bool Ready => _pages.Count > 0;

        private readonly string _src;
        private readonly List<Bitmap> _pages = new();
        private readonly List<Bitmap> _ownedBitmaps = new();
        private List<EditObj>[] _objs = Array.Empty<List<EditObj>>();
        private OcrPage?[] _ocr = Array.Empty<OcrPage?>();
        private int _cur = -1;

        private readonly Stack<(int Page, List<EditObj> State)> _undo = new();
        private readonly Stack<(int Page, List<EditObj> State)> _redo = new();
        private bool _dirty, _exporting, _syncing;

        private EditCanvas _canvas = null!;
        private Panel _host = null!;
        private ListView _thumbs = null!;
        private ImageList _imgs = null!;
        private Label _status = null!;
        private Label _pageLabel = null!;
        private Button _btnUndo = null!, _btnRedo = null!, _btnDelete = null!;
        private readonly Dictionary<EditTool, Button> _toolButtons = new();

        private ComboBox _cmbFont = null!, _cmbAlign = null!;
        private NumericUpDown _numSize = null!;
        private CheckBox _chkB = null!, _chkI = null!, _chkU = null!;
        private Button _btnColor = null!;
        private Color _ink = Color.Black;

        private static int S(int px) => Theme.S(px);

        public PdfEditorForm(string path)
        {
            _src = path;
            Text = $"Editor PDF — {Path.GetFileName(path)}";
            Size = new Size(S(1240), S(860));
            MinimumSize = new Size(S(900), S(600));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColorTranslator.FromHtml("#E9EDF7");
            KeyPreview = true;

            LoadSource();
            if (_pages.Count == 0) return;

            _objs = new List<EditObj>[_pages.Count];
            for (int i = 0; i < _objs.Length; i++) _objs[i] = new List<EditObj>();
            _ocr = new OcrPage?[_pages.Count];

            BuildUi();
            Shown += (s, e) => { FitWidth(); GoTo(0); };
        }

        // ---------- muat berkas ----------
        private void LoadSource()
        {
            try
            {
                string ext = Path.GetExtension(_src).ToLowerInvariant();
                if (ext == ".pdf")
                {
                    if (!PdfEngine.EnsureUnlocked(null, _src) || PdfEngine.IsLocked(_src))
                    {
                        MessageBox.Show("PDF ini terkunci password dan belum bisa dibuka.", "Editor PDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    int count = PdfEngine.PageCount(_src);
                    if (count > 80 &&
                        MessageBox.Show($"PDF ini {count} halaman. Mengedit PDF sebesar ini butuh banyak memori. Lanjutkan?",
                            "Editor PDF", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                    int maxDim = count > 40 ? 1600 : 2000;
                    _pages.AddRange(PdfEngine.RenderAllPages(_src, maxDim));
                }
                else
                {
                    _pages.Add(PdfEngine.Decode(_src, 2000));
                }
                if (_pages.Count == 0)
                    MessageBox.Show("Tidak ada halaman yang bisa dimuat dari berkas ini.", "Editor PDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal membuka berkas: " + ex.Message, "Editor PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- bangun UI ----------
        private Button Btn(string text, bool filled, Color col, Action click)
        {
            var b = Theme.CreateButton(text, filled, col, S(34));
            b.Margin = new Padding(S(3), S(3), S(3), S(3));
            b.Click += (s, e) => click();
            return b;
        }

        private void BuildUi()
        {
            // --- Baris 1: berkas, undo/redo, zoom, halaman ---
            var bar1 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = Theme.SURFACE, Padding = new Padding(S(8), S(4), S(8), S(2))
            };
            _btnUndo = Btn("↶ Urungkan", false, Theme.TEXT, Undo);
            _btnRedo = Btn("↷ Ulangi", false, Theme.TEXT, Redo);
            _pageLabel = new Label { AutoSize = true, Font = Theme.FontLabel(10f), ForeColor = Theme.TEXT, Margin = new Padding(S(10), S(10), S(10), 0) };
            bar1.Controls.AddRange(new Control[]
            {
                Btn("💾 Simpan PDF", true, Theme.TEAL, ExportPdfAsync),
                _btnUndo, _btnRedo,
                Btn("−", false, Theme.TEXT, () => ZoomBy(1f / 1.15f)),
                Btn("+", false, Theme.TEXT, () => ZoomBy(1.15f)),
                Btn("Lebar Layar", false, Theme.TEXT, FitWidth),
                Btn("◀", false, Theme.TEXT, () => { if (_cur > 0) GoTo(_cur - 1); }),
                _pageLabel,
                Btn("▶", false, Theme.TEXT, () => { if (_cur < _pages.Count - 1) GoTo(_cur + 1); })
            });

            // --- Baris 2: alat ---
            var bar2 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = Theme.SURFACE2, Padding = new Padding(S(8), S(2), S(8), S(2))
            };
            void AddTool(EditTool tool, string text)
            {
                var b = Btn(text, false, Theme.ACCENT, () => SetTool(tool));
                _toolButtons[tool] = b;
                bar2.Controls.Add(b);
            }
            AddTool(EditTool.Select, "🖱 Pilih/Geser");
            AddTool(EditTool.EditText, "✏️ Edit Teks Asli");
            AddTool(EditTool.Text, "🅣 Teks Baru");
            bar2.Controls.Add(Btn("🖼 Gambar", false, Theme.ACCENT, InsertImage));
            bar2.Controls.Add(Btn("✍️ Tanda Tangan", false, Theme.TEAL, InsertSignature));
            AddTool(EditTool.Highlight, "🟨 Sorot");
            AddTool(EditTool.Whiteout, "⬜ Tutup/Hapus");
            AddTool(EditTool.Box, "▭ Kotak");
            _btnDelete = Btn("🗑 Hapus Objek", false, Theme.RED, () => _canvas.DeleteSelected());
            bar2.Controls.Add(_btnDelete);

            // --- Baris 3: properti teks ---
            var bar3 = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true, BackColor = Theme.SURFACE2, Padding = new Padding(S(8), S(2), S(8), S(6))
            };
            _cmbFont = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(170), Margin = new Padding(S(3), S(6), S(3), 0) };
            var wanted = new[] { "Segoe UI", "Arial", "Calibri", "Cambria", "Times New Roman", "Georgia", "Verdana", "Tahoma", "Trebuchet MS", "Courier New", "Consolas" };
            var installed = new HashSet<string>(FontFamily.Families.Select(f => f.Name));
            foreach (var n in wanted) if (installed.Contains(n)) _cmbFont.Items.Add(n);
            if (_cmbFont.Items.Count == 0) _cmbFont.Items.Add("Segoe UI");
            _cmbFont.SelectedIndex = 0;
            _cmbFont.SelectedIndexChanged += (s, e) => ApplyTextProps();

            _numSize = new NumericUpDown { Minimum = 6, Maximum = 200, Value = 14, Width = S(60), Margin = new Padding(S(3), S(6), S(3), 0) };
            _numSize.ValueChanged += (s, e) => ApplyTextProps();

            CheckBox Toggle(string text, FontStyle style)
            {
                var c = new CheckBox
                {
                    Appearance = Appearance.Button, Text = text, TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat, Width = S(34), Height = S(30), Font = new Font("Segoe UI", 10f, style),
                    Margin = new Padding(S(3), S(4), S(3), 0)
                };
                c.CheckedChanged += (s, e) => ApplyTextProps();
                return c;
            }
            _chkB = Toggle("B", FontStyle.Bold);
            _chkI = Toggle("I", FontStyle.Italic);
            _chkU = Toggle("U", FontStyle.Underline);

            _btnColor = new Button
            {
                Width = S(34), Height = S(30), BackColor = _ink, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(S(3), S(4), S(3), 0), Cursor = Cursors.Hand
            };
            _btnColor.Click += (s, e) =>
            {
                using var cd = new ColorDialog { Color = _ink, FullOpen = true };
                if (cd.ShowDialog(this) == DialogResult.OK)
                {
                    _ink = cd.Color;
                    _btnColor.BackColor = _ink;
                    ApplyTextProps();
                }
            };

            _cmbAlign = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(90), Margin = new Padding(S(3), S(6), S(3), 0) };
            _cmbAlign.Items.AddRange(new object[] { "Kiri", "Tengah", "Kanan" });
            _cmbAlign.SelectedIndex = 0;
            _cmbAlign.SelectedIndexChanged += (s, e) => ApplyTextProps();

            Label Cap(string t) => new Label { Text = t, AutoSize = true, Font = Theme.FontLabel(9.5f), ForeColor = Theme.MUTED, Margin = new Padding(S(8), S(10), S(2), 0) };
            bar3.Controls.AddRange(new Control[]
            {
                Cap("Font"), _cmbFont, Cap("Ukuran (pt)"), _numSize, _chkB, _chkI, _chkU, Cap("Warna"), _btnColor, Cap("Rata"), _cmbAlign
            });

            // --- Thumbnail halaman ---
            _imgs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(S(96), S(128)) };
            _thumbs = new ListView
            {
                Dock = DockStyle.Left, Width = S(150), View = View.LargeIcon, LargeImageList = _imgs,
                MultiSelect = false, HideSelection = false, BackColor = Theme.SURFACE, BorderStyle = BorderStyle.None
            };
            for (int i = 0; i < _pages.Count; i++)
            {
                _imgs.Images.Add(MakeThumb(i));
                _thumbs.Items.Add(new ListViewItem($"Hal. {i + 1}", i));
            }
            _thumbs.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing || _thumbs.SelectedIndices.Count == 0) return;
                int idx = _thumbs.SelectedIndices[0];
                if (idx != _cur) GoTo(idx);
            };

            // --- Area halaman ---
            _host = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ColorTranslator.FromHtml("#CBD5E1") };
            _canvas = new EditCanvas();
            _host.Controls.Add(_canvas);
            _host.Resize += (s, e) => CenterCanvas();
            _host.MouseWheel += OnHostWheel;
            _canvas.MouseWheel += OnHostWheel;

            _canvas.SelectionChanged += SyncFromSelection;
            _canvas.Changing += PushUndo;
            _canvas.Changed += OnObjectsChanged;
            _canvas.PointClicked += OnCanvasPointClicked;
            _canvas.RectDrawn += OnRectDrawn;

            _status = new Label
            {
                Dock = DockStyle.Bottom, Height = S(28), TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.SURFACE,
                ForeColor = Theme.MUTED, Font = Theme.FontBody(9f), Padding = new Padding(S(10), 0, 0, 0)
            };

            Controls.Add(_host);
            Controls.Add(_thumbs);
            Controls.Add(_status);
            Controls.Add(bar3);
            Controls.Add(bar2);
            Controls.Add(bar1);

            SetTool(EditTool.Select);
            UpdateUndoButtons();
        }

        private Bitmap MakeThumb(int i)
        {
            var bmp = new Bitmap(S(96), S(128), PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var pg = _pages[i];
            float sc = Math.Min((bmp.Width - 4f) / pg.Width, (bmp.Height - 4f) / pg.Height);
            float w = pg.Width * sc, h = pg.Height * sc;
            float ox = (bmp.Width - w) / 2f, oy = (bmp.Height - h) / 2f;
            g.DrawImage(pg, ox, oy, w, h);
            if (_objs.Length > i)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.TranslateTransform(ox, oy);
                g.ScaleTransform(sc, sc);
                foreach (var o in _objs[i]) o.Draw(g);
            }
            using var pen = new Pen(Theme.LINE);
            g.ResetTransform();
            g.DrawRectangle(pen, 0, 0, bmp.Width - 1, bmp.Height - 1);
            return bmp;
        }

        private void RefreshThumb(int i)
        {
            if (i < 0 || i >= _imgs.Images.Count) return;
            var old = _imgs.Images[i];
            _imgs.Images[i] = MakeThumb(i);
            old?.Dispose();
            _thumbs.Invalidate();
        }

        // ---------- navigasi & zoom ----------
        private void GoTo(int idx)
        {
            if (idx < 0 || idx >= _pages.Count) return;
            _canvas.EndEdit(true);
            _cur = idx;
            _canvas.Page = _pages[idx];
            _canvas.Objects = _objs[idx];
            _canvas.Selected = null;
            ApplyZoom();
            _pageLabel.Text = $"Halaman {idx + 1} / {_pages.Count}";
            _syncing = true;
            try
            {
                _thumbs.SelectedIndices.Clear();
                _thumbs.SelectedIndices.Add(idx);
                _thumbs.EnsureVisible(idx);
            }
            finally { _syncing = false; }
            SyncFromSelection();
            UpdateStatus();
        }

        private void FitWidth()
        {
            if (_pages.Count == 0) return;
            var pg = _pages[Math.Max(0, _cur)];
            float z = (_host.ClientSize.Width - S(40)) / (float)pg.Width;
            _canvas.Zoom = Math.Clamp(z, 0.2f, 3f);
            if (_cur >= 0) ApplyZoom();
        }

        private void ZoomBy(float f)
        {
            _canvas.Zoom = Math.Clamp(_canvas.Zoom * f, 0.2f, 4f);
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            if (_canvas.Page == null) return;
            _canvas.EndEdit(true);
            _canvas.Size = new Size((int)(_canvas.Page.Width * _canvas.Zoom), (int)(_canvas.Page.Height * _canvas.Zoom));
            _host.AutoScrollMinSize = new Size(_canvas.Width + S(24), _canvas.Height + S(24));
            CenterCanvas();
            _canvas.Invalidate();
            UpdateStatus();
        }

        private void CenterCanvas()
        {
            if (_canvas == null || _host == null) return;
            int x = Math.Max(S(12), (_host.ClientSize.Width - _canvas.Width) / 2);
            _canvas.Location = new Point(x + _host.AutoScrollPosition.X, S(12) + _host.AutoScrollPosition.Y);
        }

        private void OnHostWheel(object? sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ZoomBy(e.Delta > 0 ? 1.1f : 1f / 1.1f);
                if (e is HandledMouseEventArgs h) h.Handled = true;
            }
        }

        private void UpdateStatus()
        {
            _status.Text = $"Zoom {(int)(_canvas.Zoom * 100)}%  •  {SaveHint()}";
        }

        private string SaveHint() => _dirty
            ? "Ada perubahan yang belum disimpan. Hasil disimpan sebagai PDF baru (berkas asli tidak diubah)."
            : "Klik “Edit Teks Asli” lalu klik baris teks di halaman untuk mengubahnya. Klik dua kali teks baru untuk mengetik.";

        // ---------- alat ----------
        private void SetTool(EditTool t)
        {
            _canvas.EndEdit(true);
            _canvas.Tool = t;
            foreach (var kv in _toolButtons)
            {
                bool on = kv.Key == t;
                kv.Value.BackColor = on ? Theme.ACCENT : Theme.BLUE_BG;
                kv.Value.ForeColor = on ? Color.White : Theme.ACCENT;
            }
            _canvas.Cursor = t == EditTool.Select ? Cursors.Default : (t == EditTool.Text || t == EditTool.EditText ? Cursors.IBeam : Cursors.Cross);
        }

        private float PxPerPt()
        {
            var pg = _pages[Math.Max(0, _cur)];
            float widthPt = pg.Width >= pg.Height ? 842f : 595f;
            return pg.Width / widthPt;
        }

        private TextObj NewTextFromControls(RectangleF bounds, string text)
        {
            return new TextObj
            {
                Bounds = bounds,
                Text = text,
                FontName = _cmbFont.SelectedItem as string ?? "Segoe UI",
                SizePx = (float)_numSize.Value * PxPerPt(),
                Bold = _chkB.Checked,
                Italic = _chkI.Checked,
                Underline = _chkU.Checked,
                Ink = _ink,
                Align = _cmbAlign.SelectedIndex == 1 ? StringAlignment.Center : _cmbAlign.SelectedIndex == 2 ? StringAlignment.Far : StringAlignment.Near
            };
        }

        private void ApplyTextProps()
        {
            if (_syncing || _cur < 0) return;
            if (_canvas.Selected is TextObj t)
            {
                PushUndo();
                t.FontName = _cmbFont.SelectedItem as string ?? t.FontName;
                t.SizePx = (float)_numSize.Value * PxPerPt();
                t.Bold = _chkB.Checked;
                t.Italic = _chkI.Checked;
                t.Underline = _chkU.Checked;
                t.Ink = _ink;
                t.Align = _cmbAlign.SelectedIndex == 1 ? StringAlignment.Center : _cmbAlign.SelectedIndex == 2 ? StringAlignment.Far : StringAlignment.Near;
                _canvas.AutoHeight(t);
                OnObjectsChanged();
                _canvas.Invalidate();
            }
        }

        private void SyncFromSelection()
        {
            _btnDelete.Enabled = _canvas.Selected != null;
            if (_canvas.Selected is not TextObj t) return;
            _syncing = true;
            try
            {
                int fi = _cmbFont.Items.IndexOf(t.FontName);
                if (fi >= 0) _cmbFont.SelectedIndex = fi;
                decimal pt = (decimal)Math.Round(t.SizePx / PxPerPt());
                _numSize.Value = Math.Clamp(pt, _numSize.Minimum, _numSize.Maximum);
                _chkB.Checked = t.Bold;
                _chkI.Checked = t.Italic;
                _chkU.Checked = t.Underline;
                _ink = t.Ink;
                _btnColor.BackColor = _ink;
                _cmbAlign.SelectedIndex = t.Align == StringAlignment.Center ? 1 : t.Align == StringAlignment.Far ? 2 : 0;
            }
            finally { _syncing = false; }
        }

        // ---------- aksi di kanvas ----------
        private async void OnCanvasPointClicked(PointF pt)
        {
            if (_cur < 0) return;
            var pg = _pages[_cur];

            if (_canvas.Tool == EditTool.Text)
            {
                float w = Math.Min(pg.Width * 0.4f, Math.Max(80f, pg.Width - pt.X - 10f));
                var t = NewTextFromControls(new RectangleF(pt.X, pt.Y, w, 10f), "Ketik di sini");
                _canvas.AutoHeight(t);
                PushUndo();
                _objs[_cur].Add(t);
                SetTool(EditTool.Select);
                _canvas.SelectObject(t);
                OnObjectsChanged();
                _canvas.BeginEdit(t);
                return;
            }

            // --- Edit Teks Asli (OCR offline) ---
            int idx = _cur;
            if (_ocr[idx] == null)
            {
                Theme.Toast(this, "Membaca teks halaman ini (OCR offline)…");
                Cursor = Cursors.WaitCursor;
                var copy = new Bitmap(pg);
                try
                {
                    _ocr[idx] = await Task.Run(() => { using (copy) return PdfEngine.RunOcr(copy); });
                }
                finally { Cursor = Cursors.Default; }
                if (!string.IsNullOrEmpty(PdfEngine.LastOcrError))
                {
                    MessageBox.Show(this, PdfEngine.LastOcrError, "OCR tidak tersedia", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (idx != _cur) return;
            }

            var ocr = _ocr[idx];
            if (ocr == null || ocr.Lines.Count == 0)
            {
                Theme.Toast(this, "Tidak ada teks terbaca di halaman ini. Pakai “Teks Baru” + “Tutup/Hapus”.");
                return;
            }

            float sx = ocr.Width > 0 ? pg.Width / (float)ocr.Width : 1f;
            float sy = ocr.Height > 0 ? pg.Height / (float)ocr.Height : 1f;
            OcrLine? found = null;
            RectangleF box = RectangleF.Empty;
            foreach (var ln in ocr.Lines)
            {
                var r = new RectangleF(ln.Box.X * sx, ln.Box.Y * sy, ln.Box.Width * sx, ln.Box.Height * sy);
                var hit = r;
                hit.Inflate(6f, 6f);
                if (hit.Contains(pt)) { found = ln; box = r; break; }
            }
            if (found == null)
            {
                Theme.Toast(this, "Tidak ada baris teks di titik itu. Klik tepat pada teks.");
                return;
            }

            var bg = SampleBackground(pg, box);
            var ink = SampleInk(pg, box, bg);

            PushUndo();
            var cover = new RectObj { Kind = 1, Fill = bg, Bounds = RectangleF.Inflate(box, 3f, 2f) };
            var text = NewTextFromControls(new RectangleF(box.X, box.Y, Math.Max(box.Width * 1.15f, 60f), box.Height), found.Text);
            text.SizePx = Math.Max(8f, box.Height / 1.25f);
            text.Ink = ink;
            text.Align = StringAlignment.Near;
            _objs[_cur].Add(cover);
            _objs[_cur].Add(text);
            _canvas.AutoHeight(text);
            SetTool(EditTool.Select);
            _canvas.SelectObject(text);
            OnObjectsChanged();
            _canvas.BeginEdit(text);
        }

        private void OnRectDrawn(RectangleF r)
        {
            if (_cur < 0) return;
            PushUndo();
            var ro = new RectObj { Bounds = r };
            switch (_canvas.Tool)
            {
                case EditTool.Highlight:
                    ro.Kind = 0;
                    ro.Fill = Color.FromArgb(105, 255, 230, 0);
                    break;
                case EditTool.Whiteout:
                    ro.Kind = 1;
                    ro.Fill = SampleBackground(_pages[_cur], r);
                    break;
                default:
                    ro.Kind = 2;
                    ro.Line = _ink;
                    ro.LineW = Math.Max(2f, _pages[_cur].Width / 450f);
                    break;
            }
            _objs[_cur].Add(ro);
            _canvas.SelectObject(ro);
            OnObjectsChanged();
        }

        private static Color SampleBackground(Bitmap bmp, RectangleF r)
        {
            int x0 = Math.Max(0, (int)r.Left - 3), x1 = Math.Min(bmp.Width - 1, (int)r.Right + 3);
            int y0 = Math.Max(0, (int)r.Top - 3), y1 = Math.Min(bmp.Height - 1, (int)r.Bottom + 3);
            long sr = 0, sg = 0, sb = 0; int n = 0;
            void Take(int x, int y)
            {
                var c = bmp.GetPixel(x, y);
                sr += c.R; sg += c.G; sb += c.B; n++;
            }
            for (int x = x0; x <= x1; x += 2) { Take(x, y0); Take(x, y1); }
            for (int y = y0; y <= y1; y += 2) { Take(x0, y); Take(x1, y); }
            if (n == 0) return Color.White;
            return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }

        private static Color SampleInk(Bitmap bmp, RectangleF r, Color bg)
        {
            int x0 = Math.Max(0, (int)r.Left), x1 = Math.Min(bmp.Width - 1, (int)r.Right);
            int y0 = Math.Max(0, (int)r.Top), y1 = Math.Min(bmp.Height - 1, (int)r.Bottom);
            double bgL = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
            double minL = 999; Color best = Color.Black;
            for (int y = y0; y <= y1; y += 2)
                for (int x = x0; x <= x1; x += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    if (l < minL) { minL = l; best = c; }
                }
            return Math.Abs(bgL - minL) > 70 ? best : Color.Black;
        }

        // ---------- sisip gambar & tanda tangan ----------
        private void AddImageObject(Bitmap bmp, float widthFraction)
        {
            if (_cur < 0) { bmp.Dispose(); return; }
            _ownedBitmaps.Add(bmp);
            var pg = _pages[_cur];
            float w = Math.Min(pg.Width * widthFraction, bmp.Width);
            float h = w * bmp.Height / Math.Max(1, bmp.Width);
            if (h > pg.Height * 0.6f) { h = pg.Height * 0.6f; w = h * bmp.Width / Math.Max(1, bmp.Height); }
            PushUndo();
            var o = new ImageObj(bmp) { Bounds = new RectangleF((pg.Width - w) / 2f, (pg.Height - h) / 2f, w, h) };
            _objs[_cur].Add(o);
            SetTool(EditTool.Select);
            _canvas.SelectObject(o);
            OnObjectsChanged();
        }

        private void InsertImage()
        {
            using var ofd = new OpenFileDialog { Title = "Pilih Gambar untuk Disisipkan", Filter = "Gambar (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var fs = new FileStream(ofd.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var tmp = new Bitmap(fs);
                AddImageObject(new Bitmap(tmp), 0.4f);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Gambar tidak bisa dibuka: " + ex.Message, "Sisipkan Gambar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InsertSignature()
        {
            Bitmap? sig = null;
            using var dlg = new Form
            {
                Text = "Tanda Tangan",
                ClientSize = new Size(S(460), S(300)),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };
            dlg.Controls.Add(new Label { Text = "Goreskan tanda tangan di kotak putih:", Location = new Point(S(20), S(14)), AutoSize = true, Font = Theme.FontLabel(9.5f) });
            var pad = new SignaturePadControl { Location = new Point(S(20), S(40)), Size = new Size(S(420), S(170)) };
            var btnClear = Theme.CreateButton("Bersihkan", false, Theme.TEXT, S(34), S(110));
            btnClear.Location = new Point(S(20), S(222));
            btnClear.Click += (s, e) => pad.Clear();
            var btnOk = Theme.CreateButton("✍️ Sisipkan", true, Theme.TEAL, S(34), S(150));
            btnOk.Location = new Point(S(290), S(222));
            btnOk.Click += (s, e) =>
            {
                sig = pad.ToBitmap();
                if (sig == null) { Theme.Toast(dlg, "Tanda tangan masih kosong"); return; }
                dlg.DialogResult = DialogResult.OK;
            };
            dlg.Controls.AddRange(new Control[] { pad, btnClear, btnOk });
            if (dlg.ShowDialog(this) == DialogResult.OK && sig != null)
                AddImageObject(sig, 0.25f);
        }

        // ---------- undo / redo ----------
        private static List<EditObj> CloneList(List<EditObj> src) => src.Select(o => o.Clone()).ToList();

        private void PushUndo()
        {
            if (_cur < 0) return;
            _undo.Push((_cur, CloneList(_objs[_cur])));
            _redo.Clear();
            if (_undo.Count > 100)
            {
                var keep = _undo.ToArray().Take(100).ToArray();   // urutan: teratas dulu
                Array.Reverse(keep);                              // jadi: terbawah dulu
                _undo.Clear();
                foreach (var k in keep) _undo.Push(k);
            }
            UpdateUndoButtons();
        }

        private void Undo()
        {
            _canvas.EndEdit(true);
            if (_undo.Count == 0) return;
            var (p, state) = _undo.Pop();
            _redo.Push((p, CloneList(_objs[p])));
            _objs[p] = state;
            AfterHistoryStep(p);
        }

        private void Redo()
        {
            _canvas.EndEdit(true);
            if (_redo.Count == 0) return;
            var (p, state) = _redo.Pop();
            _undo.Push((p, CloneList(_objs[p])));
            _objs[p] = state;
            AfterHistoryStep(p);
        }

        private void AfterHistoryStep(int p)
        {
            _dirty = true;
            if (p != _cur) GoTo(p);
            _canvas.Objects = _objs[_cur];
            _canvas.Selected = null;
            SyncFromSelection();
            RefreshThumb(p);
            UpdateUndoButtons();
            UpdateStatus();
            _canvas.Invalidate();
        }

        private void UpdateUndoButtons()
        {
            if (_btnUndo != null) _btnUndo.Enabled = _undo.Count > 0;
            if (_btnRedo != null) _btnRedo.Enabled = _redo.Count > 0;
        }

        private void OnObjectsChanged()
        {
            _dirty = true;
            RefreshThumb(_cur);
            UpdateUndoButtons();
            UpdateStatus();
            _canvas.Invalidate();
        }

        // ---------- keyboard ----------
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_canvas == null || _canvas.IsEditing) return;
            if (e.Control && e.KeyCode == Keys.Z) { Undo(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { Redo(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.S) { ExportPdfAsync(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete && ActiveControl is not ComboBox && ActiveControl is not NumericUpDown)
            {
                _canvas.DeleteSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape) { SetTool(EditTool.Select); e.Handled = true; }
        }

        // ---------- simpan ----------
        private Bitmap Flatten(int i)
        {
            var src = _pages[i];
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, src.Width, src.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            foreach (var o in _objs[i]) o.Draw(g);
            return bmp;
        }

        private static (float W, float H) PageSizePt(Bitmap b)
        {
            float ratio = (float)b.Height / Math.Max(1, b.Width);
            if (Math.Abs(ratio - 1.4142f) < 0.06f) return (595f, 842f);
            if (ratio > 0f && Math.Abs(1f / ratio - 1.4142f) < 0.06f) return (842f, 595f);
            const float max = 842f;
            return b.Width >= b.Height ? (max, max * b.Height / b.Width) : (max * b.Width / b.Height, max);
        }

        private bool AskExportOptions(out bool ocr, out int quality)
        {
            ocr = true; quality = 90;
            bool ok = false;
            bool ocrVal = true; int qVal = 90;
            using var dlg = new Form
            {
                Text = "Simpan PDF Hasil Edit",
                ClientSize = new Size(S(470), S(250)),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.White
            };
            var chk = new CheckBox
            {
                Text = "Buat teks bisa dicari & disalin (OCR, lebih lama)", Checked = true,
                Location = new Point(S(22), S(20)), AutoSize = true, Font = Theme.FontLabel(10f)
            };
            var lbl = new Label { Text = "Kualitas gambar:", Location = new Point(S(22), S(62)), AutoSize = true, Font = Theme.FontLabel(10f) };
            var cmb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(S(150), S(58)), Width = S(180) };
            cmb.Items.AddRange(new object[] { "Tinggi (file lebih besar)", "Sedang", "Hemat ukuran" });
            cmb.SelectedIndex = 0;
            var info = new Label
            {
                Text = "Halaman diubah jadi gambar lalu disusun ulang. Berkas asli tidak diubah; hasil disimpan sebagai PDF baru di Documents\\DepupOffice.",
                Location = new Point(S(22), S(100)), Size = new Size(S(426), S(60)), ForeColor = Theme.MUTED, Font = Theme.FontBody(9.5f)
            };
            var btnSave = Theme.CreateButton("💾 Simpan", true, Theme.TEAL, S(38), S(160));
            btnSave.Location = new Point(S(288), S(180));
            btnSave.Click += (s, e) =>
            {
                ocrVal = chk.Checked;
                qVal = cmb.SelectedIndex == 0 ? 90 : cmb.SelectedIndex == 1 ? 80 : 65;
                ok = true;
                dlg.DialogResult = DialogResult.OK;
            };
            var btnCancel = Theme.CreateButton("Batal", false, Theme.TEXT, S(38), S(120));
            btnCancel.Location = new Point(S(150), S(180));
            btnCancel.Click += (s, e) => dlg.DialogResult = DialogResult.Cancel;
            dlg.Controls.AddRange(new Control[] { chk, lbl, cmb, info, btnSave, btnCancel });
            dlg.ShowDialog(this);
            ocr = ocrVal; quality = qVal;
            return ok;
        }

        private async void ExportPdfAsync()
        {
            if (_exporting || _pages.Count == 0) return;
            _canvas.EndEdit(true);
            if (!AskExportOptions(out bool wantOcr, out int quality)) return;

            string outPath = AppStorage.NamedOut("Edit_" + AppStorage.BaseName(_src), "pdf");
            string oldTitle = Text;
            _exporting = true;
            Enabled = false;
            bool success = false;
            try
            {
                int n = _pages.Count;
                using (var w = new PdfWriter(outPath, n))
                {
                    for (int i = 0; i < n; i++)
                    {
                        Text = $"Menyimpan halaman {i + 1} dari {n}…";
                        var flat = Flatten(i);
                        var (pw, ph) = PageSizePt(flat);
                        await Task.Run(() =>
                        {
                            using (flat)
                            {
                                byte[] jp = PdfEngine.EncodeJpeg(flat, quality);
                                OcrPage? o = wantOcr ? PdfEngine.RunOcr(flat) : null;
                                w.AddPage(jp, flat.Width, flat.Height, pw, ph, 0f, 0f, pw, ph, o);
                            }
                        });
                    }
                    w.Finish();
                }
                success = true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                MessageBox.Show(this, "Gagal menyimpan PDF: " + ex.Message, "Simpan PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _exporting = false;
                Enabled = true;
                Text = oldTitle;
            }

            if (!success) return;
            SavedPath = outPath;
            _dirty = false;
            AppStorage.SetLastJob("Edit PDF", outPath);
            UpdateStatus();
            string warn = wantOcr && !string.IsNullOrEmpty(PdfEngine.LastOcrError)
                ? "\n\nCatatan: teks tidak bisa dicari karena OCR Windows tidak tersedia:\n" + PdfEngine.LastOcrError
                : "";
            var fi = new FileInfo(outPath);
            MessageBox.Show(this, $"✓ PDF tersimpan ({AppStorage.FmtSize(fi.Length)}):\n{outPath}{warn}", "Simpan PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------- tutup ----------
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_exporting) { e.Cancel = true; return; }
            if (_dirty && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "Ada perubahan yang belum disimpan. Tutup tanpa menyimpan?", "Editor PDF",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            foreach (var b in _pages) b.Dispose();
            foreach (var b in _ownedBitmaps) b.Dispose();
            _pages.Clear();
            _ownedBitmaps.Clear();
            _imgs?.Dispose();
        }
    }
}
