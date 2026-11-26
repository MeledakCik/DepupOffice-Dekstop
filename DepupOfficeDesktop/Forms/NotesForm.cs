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
    /// Kartu catatan seperti kertas tempel (port <c>noteCard</c> NoteViews.kt): judul, cuplikan isi / item daftar centang,
    /// sematan &amp; waktu ubah. Dipakai di layar Catatan dan korsel Beranda.
    /// </summary>
    internal sealed class NoteCard : Control
    {
        private readonly Note _n;
        private readonly int _lines;
        private bool _hover;
        private static int S(int v) => Theme.S(v);

        public NoteCard(Note n, int width, int lines = 10, int minHeight = 0)
        {
            _n = n; _lines = lines;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            Width = width;
            Height = Math.Max(minHeight, Measure(width));
            var head = Notes.Headline(n);
            AccessibleName = head + ". " + Notes.Summary(n, head) + (n.Pinned ? ". Disematkan" : "");
            AccessibleRole = AccessibleRole.PushButton;
        }

        private const int Pad = 14;
        private Font TitleFont => Theme.FontHead(11f);
        private Font BodyFont => Theme.FontBody(9.5f);
        private Font MetaFont => Theme.FontBody(8.5f);

        private List<(string text, bool done)> VisibleItems(out int more)
        {
            var its = _n.Items.Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
            int cap = Math.Clamp(_lines / 2, 2, 8);
            more = Math.Max(0, its.Count - cap);
            return its.Take(cap).Select(i => (i.Text.Trim(), i.Done)).ToList();
        }

        private string BodyClip()
        {
            var lines = _n.Body.Trim().Split('\n');
            return string.Join("\n", lines.Take(_lines));
        }

        private int Measure(int width)
        {
            int inner = width - S(Pad) * 2;
            int y = S(Pad);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
            bool titled = !string.IsNullOrWhiteSpace(_n.Title);
            int line = TextRenderer.MeasureText("Ag", BodyFont, new Size(inner, 100), flags).Height;
            if (titled) y += Math.Min(TextRenderer.MeasureText(_n.Title.Trim(), TitleFont, new Size(inner, 1000), flags).Height, S(46));
            if (_n.Checklist)
            {
                var its = VisibleItems(out int more);
                foreach (var (t, _) in its)
                    y += S(6) + Math.Min(TextRenderer.MeasureText(t, BodyFont, new Size(inner - S(24), 1000), flags).Height, line * 2);
                if (more > 0) y += S(6) + line;
                if (its.Count == 0 && !titled) y += line;
            }
            else
            {
                var b = BodyClip();
                if (b.Length > 0) y += S(titled ? 4 : 0) + Math.Min(TextRenderer.MeasureText(b, BodyFont, new Size(inner, 4000), flags).Height, line * _lines);
                else if (!titled) y += line;
            }
            y += S(10) + TextRenderer.MeasureText("Ag", MetaFont).Height + S(Pad);
            return y;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) => k is Keys.Enter or Keys.Space || base.IsInputKey(k);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); return; }
            if (e.KeyCode == Keys.Apps || (e.Shift && e.KeyCode == Keys.F10)) { ContextMenuStrip?.Show(this, new Point(S(20), S(20))); return; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = NoteColors.Fill(_n.Color);
            if (_hover) fill = Theme.Dark ? ControlPaint.Light(fill, 0.08f) : ControlPaint.Dark(fill, 0.02f);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Theme.RoundedRect(r, S(20)))   // Android noteCard: squircle 20
            {
                using var b = new SolidBrush(fill);
                g.FillPath(b, path);
                using var pen = new Pen(Focused ? Theme.ACCENT : (_n.Color <= 0 ? Theme.LINE : Color.FromArgb(40, Theme.INK)), Focused ? 2 : 1);
                g.DrawPath(pen, path);
            }
            var fg = NoteColors.Text(_n.Color);
            var mu = NoteColors.Muted(_n.Color);
            int inner = Width - S(Pad) * 2;
            int x = S(Pad), y = S(Pad);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis;
            int line = TextRenderer.MeasureText("Ag", BodyFont, new Size(inner, 100), flags).Height;
            bool titled = !string.IsNullOrWhiteSpace(_n.Title);
            if (titled)
            {
                int h = Math.Min(TextRenderer.MeasureText(_n.Title.Trim(), TitleFont, new Size(inner, 1000), flags).Height, S(46));
                TextRenderer.DrawText(g, _n.Title.Trim(), TitleFont, new Rectangle(x, y, inner, h), fg, flags);
                y += h;
            }
            if (_n.Checklist)
            {
                var its = VisibleItems(out int more);
                foreach (var (t, done) in its)
                {
                    y += S(6);
                    int h = Math.Min(TextRenderer.MeasureText(t, BodyFont, new Size(inner - S(24), 1000), flags).Height, line * 2);
                    DrawBox(g, new Rectangle(x, y + (line - S(14)) / 2, S(14), S(14)), done, done ? mu : fg);
                    using var f = done ? new Font(BodyFont, FontStyle.Strikeout) : null;
                    TextRenderer.DrawText(g, t, f ?? BodyFont, new Rectangle(x + S(22), y, inner - S(22), h), done ? mu : fg, flags);
                    y += h;
                }
                if (more > 0) { y += S(6); TextRenderer.DrawText(g, $"+{more} item lagi", MetaFont, new Point(x, y), mu); y += line; }
                if (its.Count == 0 && !titled) { TextRenderer.DrawText(g, "Daftar centang kosong", BodyFont, new Point(x, y), mu); y += line; }
            }
            else
            {
                var b = BodyClip();
                if (b.Length > 0)
                {
                    y += S(titled ? 4 : 0);
                    int h = Math.Min(TextRenderer.MeasureText(b, BodyFont, new Size(inner, 4000), flags).Height, line * _lines);
                    TextRenderer.DrawText(g, b, BodyFont, new Rectangle(x, y, inner, h), fg, flags);
                }
                else if (!titled) TextRenderer.DrawText(g, "Catatan kosong", BodyFont, new Point(x, y), mu);
            }
            // kaki: sematan + waktu ubah (selalu di dasar kartu)
            int metaH = TextRenderer.MeasureText("Ag", MetaFont).Height;
            int fy = Height - S(Pad) - metaH;
            int fx = x;
            if (_n.Pinned)
            {
                int ps = S(13);
                DepupIcons.Draw(g, "pin", new RectangleF(fx, fy + (metaH - ps) / 2f, ps, ps), mu);
                fx += ps + S(5);
            }
            TextRenderer.DrawText(g, AppStorage.RelTime(_n.Updated), MetaFont, new Rectangle(fx, fy, x + inner - fx, metaH), mu, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>Kotak centang Android (ic_checkbox / ic_checkbox_on).</summary>
        internal static void DrawBox(Graphics g, Rectangle r, bool on, Color c) =>
            DepupIcons.Draw(g, on ? "checkbox_on" : "checkbox", Rectangle.Inflate(r, Math.Max(1, r.Width / 6), Math.Max(1, r.Width / 6)), c);
    }

    /// <summary>Operasi catatan bersama (menu opsi, bagikan, hapus) untuk layar Catatan, Beranda, dan editor.</summary>
    internal static class NoteUi
    {
        private static int S(int v) => Theme.S(v);

        public static void Open(IWin32Window owner, long id, bool checklist = false)
        {
            using var f = new NoteEditForm(id, checklist);
            f.ShowDialog(owner);
        }

        public static bool Copy(Control owner, Note n)
        {
            var t = n.PlainText();
            if (string.IsNullOrWhiteSpace(t)) { Theme.Toast(owner, "Catatan masih kosong"); return false; }
            try { Clipboard.SetText(t); Theme.Toast(owner, "Teks catatan disalin"); return true; }
            catch { Theme.Toast(owner, "Papan klip sedang dipakai aplikasi lain"); return false; }
        }

        /// <summary>Padanan "Bagikan" Android: simpan sebagai .txt di folder hasil lalu tampilkan di Explorer.</summary>
        public static void ExportText(Control owner, Note n)
        {
            var t = n.PlainText();
            if (string.IsNullOrWhiteSpace(t)) { Theme.Toast(owner, "Catatan masih kosong"); return; }
            var name = string.Concat(Notes.Headline(n).Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length > 60) name = name[..60];
            var path = AppStorage.NewOut(string.IsNullOrWhiteSpace(name) ? "Catatan" : name, "txt");
            File.WriteAllText(path, t.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
            AppStorage.AddRecent(path);
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
            Theme.Toast(owner, "Disimpan: " + Path.GetFileName(path));
        }

        public static bool ConfirmDelete(IWin32Window owner, Note n)
        {
            if (MessageBox.Show(owner, $"“{Notes.Headline(n)}” dihapus dari komputer ini.", "Hapus catatan?",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return false;
            Notes.Delete(n.Id);
            return true;
        }

        /// <summary>Gambar bulatan warna untuk menu pilihan warna.</summary>
        public static Image Swatch(int i, bool selected)
        {
            int d = S(18);
            var bmp = new Bitmap(d + 2, d + 2);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(NoteColors.Fill(i));
            g.FillEllipse(b, 1, 1, d - 1, d - 1);
            using var pen = new Pen(selected ? Theme.INK : Theme.LINE, selected ? 2 : 1);
            g.DrawEllipse(pen, 1, 1, d - 1, d - 1);
            return bmp;
        }

        public static void AddColorItems(ToolStripItemCollection items, int current, Action<int> pick)
        {
            for (int i = 0; i < NoteColors.All.Length; i++)
            {
                int k = i;
                var it = new ToolStripMenuItem(NoteColors.All[i].Name, Swatch(i, i == current), (_, _) => pick(k)) { Checked = i == current };
                items.Add(it);
            }
        }

        /// <summary>Menu opsi catatan (klik kanan kartu): warna, sematkan, salin, simpan teks, hapus.</summary>
        public static ContextMenuStrip Menu(Control owner, long id, Action changed)
        {
            var m = new ContextMenuStrip();
            m.Opening += (s, e) =>
            {
                m.Items.Clear();
                var n = Notes.Get(id);
                if (n == null) { e.Cancel = true; changed(); return; }
                m.Items.Add(new ToolStripMenuItem(Notes.Headline(n)) { Enabled = false });
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Buka", null, (_, _) => { Open(owner.FindForm()!, id); changed(); });
                var color = new ToolStripMenuItem("Warna");
                AddColorItems(color.DropDownItems, n.Color, c => { if (c != n.Color) { Notes.Edit(id, x => x.Color = c); changed(); } });
                m.Items.Add(color);
                m.Items.Add(n.Pinned ? "Lepas sematan" : "Sematkan", null, (_, _) => { Notes.Edit(id, x => x.Pinned = !x.Pinned); changed(); });
                m.Items.Add("Salin teks", null, (_, _) => Copy(owner, n));
                m.Items.Add("Simpan sebagai teks (.txt)", null, (_, _) => ExportText(owner, n));
                m.Items.Add(new ToolStripSeparator());
                var del = m.Items.Add("Hapus", null, (_, _) => { if (ConfirmDelete(owner.FindForm()!, n)) changed(); });
                del.ForeColor = Theme.RED;
            };
            return m;
        }
    }

    /// <summary>
    /// Semua catatan (port NotesActivity.kt): kisi "masonry" seperti kertas tempel (disematkan di atas), cari,
    /// catatan / daftar centang baru. Klik kanan kartu = opsi (warna, sematkan, salin, hapus).
    /// </summary>
    public sealed class NotesForm : Form
    {
        private static int S(int v) => Theme.S(v);
        private readonly Label _sub;
        private readonly TextBox _search;
        private readonly Panel _list;
        private readonly Panel _scroll;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 160 };
        private string _query = "";

        public NotesForm()
        {
            Text = "Catatan — " + Theme.AppName;
            AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(980), wa.Width - S(40)), Math.Min(S(720), wa.Height - S(40)));
            MinimumSize = new Size(Math.Min(S(520), wa.Width), Math.Min(S(420), wa.Height));
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG; ForeColor = Theme.TEXT;
            KeyPreview = true;

            var bar = new Panel { Dock = DockStyle.Top, Height = S(68), BackColor = Theme.BG, Padding = new Padding(S(20), S(12), S(20), S(8)) };
            var title = new Label { Text = "Catatan", Font = Theme.FontHead(16f), ForeColor = Theme.TEXT, AutoSize = true, Location = new Point(S(20), S(8)), UseMnemonic = false };
            _sub = new Label { Text = "", Font = Theme.FontBody(9f), ForeColor = Theme.MUTED, AutoSize = true, Location = new Point(S(22), S(40)), UseMnemonic = false };
            var btnNew = Theme.CreateButton("+  Baru", true, Theme.ACCENT, S(40), S(110));
            btnNew.AccessibleName = "Catatan baru (Ctrl+N)";
            btnNew.Click += (s, e) => NewNote(false);
            var btnList = Theme.CreateButton("☑  Daftar centang", false, Theme.TEXT, S(40), S(170));
            btnList.AccessibleName = "Daftar centang baru (Ctrl+Shift+N)";
            btnList.Click += (s, e) => NewNote(true);
            bar.Controls.AddRange(new Control[] { title, _sub, btnNew, btnList });
            void PlaceBar()
            {
                btnNew.Location = new Point(bar.Width - S(20) - btnNew.Width, S(14));
                btnList.Location = new Point(btnNew.Left - S(8) - btnList.Width, S(14));
            }
            bar.Resize += (s, e) => PlaceBar();

            _search = new TextBox
            {
                PlaceholderText = "Cari catatan", Font = Theme.FontBody(11f), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.SURFACE, ForeColor = Theme.TEXT, AccessibleName = "Cari catatan"
            };
            var searchHost = new Panel { Dock = DockStyle.Top, Height = S(46), Padding = new Padding(S(20), S(6), S(20), S(6)), BackColor = Theme.BG };
            _search.Dock = DockStyle.Fill;
            searchHost.Controls.Add(_search);
            _search.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _debounce.Tick += (s, e) => { _debounce.Stop(); _query = _search.Text; Fill(); };

            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.BG, Padding = new Padding(S(20), S(6), S(20), S(24)) };
            Theme.DarkScrollbars(_scroll);
            _list = new Panel { Location = new Point(S(20), S(6)), BackColor = Theme.BG };
            _scroll.Controls.Add(_list);
            _scroll.Resize += (s, e) => Fill();

            Controls.Add(_scroll);
            Controls.Add(searchHost);
            Controls.Add(bar);
            Notes.Changed += OnNotesChanged;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.Control && e.KeyCode == Keys.N) { e.SuppressKeyPress = true; NewNote(e.Shift); }
                else if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; _search.Focus(); }
            };
            Shown += (s, e) => { PlaceBar(); Fill(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyWindowChrome(this); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Notes.Changed -= OnNotesChanged;
            _debounce.Dispose();
            base.OnFormClosed(e);
        }

        private void OnNotesChanged() { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(Fill)); }

        private void NewNote(bool checklist) { NoteUi.Open(this, -1, checklist); Fill(); }

        private void Fill()
        {
            if (IsDisposed) return;
            int w = Math.Max(S(200), _scroll.ClientSize.Width - S(40));
            var all = Notes.All();
            _sub.Text = all.Count == 0 ? "Tersimpan di komputer ini" : $"{all.Count} catatan";
            _search.Parent!.Visible = !(all.Count == 0 && _query.Length == 0);

            _list.SuspendLayout();
            var old = _list.Controls.Cast<Control>().ToList();
            _list.Controls.Clear();
            foreach (var c in old) c.Dispose();
            _list.Width = w;
            int y = 0;

            if (all.Count == 0)
            {
                var empty = EmptyCard(w);
                _list.Controls.Add(empty);
                y = empty.Bottom;
            }
            else
            {
                var shown = all.Where(n => Notes.Matches(n, _query)).ToList();
                if (shown.Count == 0)
                {
                    var l = new Label
                    {
                        Text = $"Tidak ada catatan yang cocok dengan “{_query.Trim()}”.", Font = Theme.FontBody(10.5f), ForeColor = Theme.MUTED,
                        AutoSize = false, Width = w, Height = S(60), TextAlign = ContentAlignment.MiddleCenter, Location = new Point(0, S(20)), UseMnemonic = false
                    };
                    _list.Controls.Add(l);
                    y = l.Bottom;
                }
                else
                {
                    var pinned = shown.Where(n => n.Pinned).ToList();
                    var others = shown.Where(n => !n.Pinned).ToList();
                    if (pinned.Count > 0)
                    {
                        y = Section("Disematkan", y + S(6), w);
                        y = Grid(pinned, y + S(4), w);
                    }
                    if (others.Count > 0)
                    {
                        if (pinned.Count > 0) y = Section("Lainnya", y + S(18), w);
                        y = Grid(others, y + S(pinned.Count == 0 ? 8 : 4), w);
                    }
                }
            }
            _list.Height = y + S(8);
            _list.ResumeLayout();
        }

        private int Section(string text, int y, int w)
        {
            var l = new Label { Text = text, Font = Theme.FontHead(12f), ForeColor = Theme.TEXT, AutoSize = true, Location = new Point(0, y), UseMnemonic = false };
            _list.Controls.Add(l);
            return y + l.PreferredHeight;
        }

        /// <summary>Kisi "masonry": tiap kartu masuk ke kolom yang paling pendek (2 kolom di jendela sempit, sampai 4 di layar lebar).</summary>
        private int Grid(List<Note> ns, int top, int w)
        {
            int gap = S(12);
            int cols = Math.Clamp((w + gap) / (S(220) + gap), 2, 4);
            int cw = (w - gap * (cols - 1)) / cols;
            var h = Enumerable.Repeat(top, cols).ToArray();
            foreach (var n in ns)
            {
                int c = Array.IndexOf(h, h.Min());
                var card = new NoteCard(n, cw) { Location = new Point(c * (cw + gap), h[c]) };
                long id = n.Id;
                card.Click += (s, e) => { NoteUi.Open(this, id); Fill(); };
                card.ContextMenuStrip = NoteUi.Menu(card, id, Fill);
                _list.Controls.Add(card);
                h[c] = card.Bottom + gap;
            }
            return h.Max() - gap;
        }

        private Control EmptyCard(int w)
        {
            var card = new CardPanel { Width = w, Height = S(220), CornerRadius = 22, Location = new Point(0, S(16)) };
            card.Controls.Add(new IconChip { Size = new Size(S(52), S(52)), Radius = S(26), Bg = Theme.AMBER_BG, Fg = Theme.AMBER, Icon = CardIcon.Note, Location = new Point((w - S(52)) / 2, S(22)) });
            var t = new Label { Text = "Belum ada catatan", Font = Theme.FontHead(12f), ForeColor = Theme.TEXT, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(S(20), S(84), w - S(40), S(26)), UseMnemonic = false };
            var d = new Label { Text = "Tulis ide, daftar belanja, atau tugas. Semua tersimpan offline di komputer ini.", Font = Theme.FontBody(9.5f), ForeColor = Theme.MUTED, AutoSize = false, TextAlign = ContentAlignment.TopCenter, Bounds = new Rectangle(S(30), S(112), w - S(60), S(40)), UseMnemonic = false };
            var b = Theme.CreateButton("Tulis catatan", true, Theme.ACCENT, S(38), S(150));
            b.Location = new Point((w - b.Width) / 2, S(160));
            b.Click += (s, e) => NewNote(false);
            card.Controls.AddRange(new Control[] { t, d, b });
            return card;
        }
    }

    /// <summary>
    /// Editor catatan (port NoteEditActivity.kt): judul + teks, atau daftar centang (Enter = item baru, Backspace di awal
    /// item kosong = hapus). Tersimpan otomatis; catatan baru yang tetap kosong tidak disimpan.
    /// </summary>
    public sealed class NoteEditForm : Form
    {
        private static int S(int v) => Theme.S(v);
        private Note _note;
        private readonly bool _fresh;
        private bool _saved, _dirty, _deleted, _muting;
        private readonly System.Windows.Forms.Timer _autosave = new() { Interval = 700 };
        private Panel _bar = null!;
        private Panel _page = null!;
        private TextBox _title = null!;
        private Label _meta = null!;
        private TextBox? _body;
        private FlowLayoutPanel? _items;
        private Button _pin = null!, _mode = null!;

        public NoteEditForm(long id, bool checklist = false)
        {
            var existing = id >= 0 ? Notes.Get(id) : null;
            _fresh = existing == null;
            _saved = !_fresh;
            _note = existing ?? new Note(Notes.NewId()) { Checklist = checklist };
            if (_note.Checklist && _note.Items.Count == 0) _note.Items.Add(new NoteItem(""));

            Text = (_fresh ? "Catatan baru" : "Catatan") + " — " + Theme.AppName;
            AutoScaleMode = AutoScaleMode.None;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Size = new Size(Math.Min(S(760), wa.Width - S(40)), Math.Min(S(720), wa.Height - S(40)));
            MinimumSize = new Size(Math.Min(S(440), wa.Width), Math.Min(S(380), wa.Height));
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
                else if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; SaveNow(); Theme.Toast(this, "Tersimpan"); }
            };
            _autosave.Tick += (s, e) => { _autosave.Stop(); SaveNow(); };
            Build(focus: true);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.ApplyWindowChrome(this); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _autosave.Stop();
            if (!_deleted)
            {
                if (_fresh && _note.Blank && !_saved) { if (_dirty) Theme.Toast(Owner, "Catatan kosong tidak disimpan"); }
                else if (_dirty) SaveNow();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { _autosave.Dispose(); base.OnFormClosed(e); }

        private void Changed()
        {
            if (_muting) return;
            _dirty = true;
            _autosave.Stop(); _autosave.Start();
        }

        private void SaveNow()
        {
            if (_deleted || !_dirty) return;
            if (_fresh && _note.Blank && !_saved) return;   // catatan baru yang masih kosong belum disimpan
            _note.Updated = Notes.Save(_note);
            _saved = true; _dirty = false;
            PaintMeta();
        }

        private void PaintMeta()
        {
            if (_meta == null || _meta.IsDisposed) return;
            _meta.Text = !_saved ? (_fresh ? "Catatan baru" : "") : Notes.Edited(_note);
        }

        private Button BarButton(string text, string name, Action click, int width)
        {
            var b = Theme.CreateButton(text, false, Theme.TEXT, S(38), S(width));
            b.AccessibleName = name;
            b.Click += (s, e) => click();
            return b;
        }

        private void Build(bool focus)
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
            _body = null; _items = null;
            var pageColor = NoteColors.Page(_note.Color);
            var fg = NoteColors.Text(_note.Color);
            var mu = NoteColors.Muted(_note.Color);
            BackColor = pageColor; ForeColor = fg;

            _bar = new Panel { Dock = DockStyle.Top, Height = S(58), BackColor = pageColor, Padding = new Padding(S(14), S(10), S(14), S(8)) };
            var back = BarButton("←  Kembali", "Kembali", Close, 120);
            _pin = BarButton(_note.Pinned ? "📌  Disematkan" : "📌  Sematkan", _note.Pinned ? "Lepas sematan" : "Sematkan", TogglePin, 150);
            if (_note.Pinned) { _pin.BackColor = Theme.AMBER_BG; _pin.ForeColor = Theme.AMBER; }
            var color = BarButton("🎨  Warna", "Warna catatan", () => { }, 110);
            color.Click += (s, e) =>
            {
                var m = new ContextMenuStrip();
                NoteUi.AddColorItems(m.Items, _note.Color, c => { if (c != _note.Color) { _note.Color = c; Changed(); Build(false); } });
                m.Closed += (_, _) => BeginInvoke(new Action(m.Dispose));
                m.Show(color, new Point(0, color.Height));
            };
            _mode = BarButton(_note.Checklist ? "¶  Teks biasa" : "☑  Daftar centang", _note.Checklist ? "Jadikan teks biasa" : "Jadikan daftar centang", ToggleMode, 160);
            var more = BarButton("⋯", "Opsi lain", () => { }, 46);
            more.Click += (s, e) => MoreMenu(more);
            var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = pageColor };
            foreach (var b in new[] { _pin, color, _mode, more }) { b.Margin = new Padding(S(6), 0, 0, 0); right.Controls.Add(b); }
            back.Dock = DockStyle.Left;
            _bar.Controls.Add(right);
            _bar.Controls.Add(back);

            _page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = pageColor, Padding = new Padding(S(30), S(4), S(30), S(30)) };
            Theme.DarkScrollbars(_page);
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = pageColor, Location = new Point(S(30), S(4)) };
            _page.Controls.Add(col);

            _muting = true;
            _title = new TextBox
            {
                Text = _note.Title, PlaceholderText = "Judul", Font = Theme.FontHead(18f), BorderStyle = BorderStyle.None,
                BackColor = pageColor, ForeColor = fg, AccessibleName = "Judul", Margin = new Padding(0, S(8), 0, S(2))
            };
            _title.TextChanged += (s, e) => { if (!_muting) { _note.Title = _title.Text; Changed(); } };
            _title.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || (e.KeyCode == Keys.Down && !e.Shift)) { e.SuppressKeyPress = true; FocusContent(true); }
            };
            col.Controls.Add(_title);
            _meta = new Label { Font = Theme.FontBody(9f), ForeColor = mu, AutoSize = true, UseMnemonic = false, Margin = new Padding(S(2), 0, 0, S(10)), BackColor = pageColor };
            col.Controls.Add(_meta);

            if (_note.Checklist) BuildItems(col, pageColor, fg, mu);
            else BuildBody(col, pageColor, fg);
            _muting = false;

            void FitWidth()
            {
                int w = Math.Max(S(200), _page.ClientSize.Width - S(60));
                _title.Width = w;
                if (_body != null)
                {
                    _body.Width = w;
                    _body.Height = Math.Max(S(220), _page.ClientSize.Height - _body.Top - S(40));
                }
                if (_items != null) foreach (Control r in _items.Controls) r.Width = w;
                if (_items != null) _items.Width = w;
            }
            _page.Resize += (s, e) => FitWidth();

            Controls.Add(_page);
            Controls.Add(_bar);
            ResumeLayout(true);
            FitWidth();
            PaintMeta();
            if (IsHandleCreated) Theme.ApplyWindowChrome(this);
            if (focus) Shown += FocusOnShow;
        }

        private void FocusOnShow(object? s, EventArgs e)
        {
            Shown -= FocusOnShow;
            if (_fresh) _title.Focus(); else FocusContent(false);
        }

        private void BuildBody(FlowLayoutPanel col, Color bg, Color fg)
        {
            _body = new TextBox
            {
                Text = _note.Body.Replace("\r\n", "\n").Replace("\n", "\r\n"), PlaceholderText = "Tulis catatan…", Multiline = true, AcceptsReturn = true, AcceptsTab = false,
                Font = Theme.FontBody(12f), BorderStyle = BorderStyle.None, BackColor = bg, ForeColor = fg, ScrollBars = ScrollBars.Vertical,
                AccessibleName = "Isi catatan", Margin = new Padding(0, S(4), 0, 0), Height = S(320)
            };
            _body.TextChanged += (s, e) => { if (!_muting) { _note.Body = _body.Text.Replace("\r\n", "\n"); Changed(); } };
            col.Controls.Add(_body);
        }

        // ------------------------------------------------------------------ daftar centang

        private sealed class ItemRow : Panel
        {
            public readonly CheckBox Box;
            public readonly TextBox Edit;
            public readonly Button Remove;
            public NoteItem Item;
            public ItemRow(NoteItem item, Color bg, Color fg, Color mu)
            {
                Item = item;
                Height = S(36); BackColor = bg; Margin = new Padding(0, 0, 0, S(2));
                Box = new CheckBox { Checked = item.Done, AutoSize = false, Size = new Size(S(24), S(28)), Location = new Point(0, S(4)), BackColor = bg, AccessibleName = "Selesai", FlatStyle = FlatStyle.Standard };
                Edit = new TextBox { Text = item.Text, BorderStyle = BorderStyle.None, BackColor = bg, Font = Theme.FontBody(11.5f), Location = new Point(S(32), S(8)), AccessibleName = "Item" };
                Remove = Theme.CreateButton("✕", false, mu, S(28), S(32));
                Remove.AccessibleName = "Hapus item"; Remove.TabStop = false; Remove.Padding = Padding.Empty;
                Controls.AddRange(new Control[] { Box, Edit, Remove });
                Paint1(fg, mu);
                Resize += (s, e) =>
                {
                    Remove.Location = new Point(Width - Remove.Width, S(4));
                    Edit.Width = Math.Max(S(40), Remove.Left - Edit.Left - S(8));
                };
            }
            public void Paint1(Color fg, Color mu)
            {
                Edit.ForeColor = Item.Done ? mu : fg;
                Edit.Font = Item.Done ? new Font(Theme.FontBody(11.5f), FontStyle.Strikeout) : Theme.FontBody(11.5f);
            }
        }

        private void BuildItems(FlowLayoutPanel col, Color bg, Color fg, Color mu)
        {
            if (_note.Items.Count == 0) _note.Items.Add(new NoteItem(""));
            _items = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = bg, Margin = Padding.Empty };
            col.Controls.Add(_items);
            foreach (var it in _note.Items) AddRow(it, _items.Controls.Count, bg, fg, mu);
            var add = new LinkLabel
            {
                Text = "+  Tambah item", Font = Theme.FontLabel(10.5f), LinkColor = Theme.ACCENT_LIGHT, ActiveLinkColor = Theme.ACCENT_LIGHT,
                LinkBehavior = LinkBehavior.HoverUnderline, AutoSize = true, Margin = new Padding(S(30), S(8), 0, 0), BackColor = bg, UseMnemonic = false
            };
            add.Click += (s, e) =>
            {
                var it = new NoteItem("");
                _note.Items.Add(it);
                var r = AddRow(it, _items.Controls.Count, bg, fg, mu);
                Changed();
                r.Edit.Focus();
            };
            col.Controls.Add(add);
            var prog = new Label { Font = Theme.FontBody(9f), ForeColor = mu, AutoSize = true, Margin = new Padding(S(30), S(14), 0, 0), BackColor = bg, UseMnemonic = false };
            void Prog() { var (d, t) = _note.Progress(); prog.Text = t == 0 ? "" : d == t ? $"Semua selesai ({t})" : $"{d}/{t} selesai"; }
            Prog();
            _progress = Prog;
            col.Controls.Add(prog);
        }

        private Action? _progress;

        private ItemRow AddRow(NoteItem item, int at, Color bg, Color fg, Color mu)
        {
            var r = new ItemRow(item, bg, fg, mu) { Width = Math.Max(S(200), _page.ClientSize.Width - S(60)) };
            r.Edit.TextChanged += (s, e) => { if (_muting) return; r.Item.Text = r.Edit.Text; Changed(); _progress?.Invoke(); };
            r.Box.CheckedChanged += (s, e) =>
            {
                if (_muting) return;
                r.Item.Done = r.Box.Checked; r.Paint1(fg, mu); Changed(); _progress?.Invoke();
            };
            r.Remove.Click += (s, e) => RemoveRow(r, focusPrev: false);
            r.Edit.KeyDown += (s, e) =>
            {
                int idx = _items!.Controls.GetChildIndex(r);
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    // pecah di posisi kursor: sisa teks pindah ke item baru di bawahnya
                    int cur = r.Edit.SelectionStart;
                    string tail = r.Edit.Text[cur..];
                    _muting = true; r.Edit.Text = r.Edit.Text[..cur]; r.Item.Text = r.Edit.Text; _muting = false;
                    var it = new NoteItem(tail);
                    _note.Items.Insert(idx + 1, it);
                    var nr = AddRow(it, idx + 1, bg, fg, mu);
                    Changed(); _progress?.Invoke();
                    nr.Edit.Focus(); nr.Edit.SelectionStart = 0;
                }
                else if (e.KeyCode == Keys.Back && r.Edit.SelectionStart == 0 && r.Edit.SelectionLength == 0 && idx > 0)
                {
                    e.SuppressKeyPress = true;
                    // gabung dengan item di atasnya (seperti Android backAtStart)
                    var prev = (ItemRow)_items.Controls[idx - 1];
                    int join = prev.Edit.TextLength;
                    string rest = r.Edit.Text;
                    RemoveRow(r, focusPrev: false);
                    prev.Edit.Text += rest;
                    prev.Edit.Focus(); prev.Edit.SelectionStart = join;
                }
                else if (e.KeyCode == Keys.Up && idx > 0) { e.SuppressKeyPress = true; ((ItemRow)_items.Controls[idx - 1]).Edit.Focus(); }
                else if (e.KeyCode == Keys.Down && idx < _items.Controls.Count - 1) { e.SuppressKeyPress = true; ((ItemRow)_items.Controls[idx + 1]).Edit.Focus(); }
                else if (e.Control && e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; r.Box.Checked = !r.Box.Checked; }
            };
            _items!.Controls.Add(r);
            _items.Controls.SetChildIndex(r, at);
            return r;
        }

        private void RemoveRow(ItemRow r, bool focusPrev)
        {
            if (_items == null) return;
            int idx = _items.Controls.GetChildIndex(r);
            _note.Items.Remove(r.Item);
            _items.Controls.Remove(r);
            r.Dispose();
            if (_note.Items.Count == 0)
            {
                var it = new NoteItem("");
                _note.Items.Add(it);
                var nr = AddRow(it, 0, BackColor, NoteColors.Text(_note.Color), NoteColors.Muted(_note.Color));
                nr.Edit.Focus();
            }
            else if (focusPrev && idx > 0) ((ItemRow)_items.Controls[idx - 1]).Edit.Focus();
            Changed(); _progress?.Invoke();
        }

        private void FocusContent(bool start)
        {
            if (_body != null) { _body.Focus(); _body.SelectionStart = start ? 0 : _body.TextLength; return; }
            if (_items != null && _items.Controls.Count > 0)
            {
                var r = (ItemRow)_items.Controls[start ? 0 : _items.Controls.Count - 1];
                r.Edit.Focus(); r.Edit.SelectionStart = start ? 0 : r.Edit.TextLength;
            }
        }

        private void TogglePin()
        {
            _note.Pinned = !_note.Pinned;
            Changed();
            _pin.Text = _note.Pinned ? "📌  Disematkan" : "📌  Sematkan";
            _pin.AccessibleName = _note.Pinned ? "Lepas sematan" : "Sematkan";
            _pin.BackColor = _note.Pinned ? Theme.AMBER_BG : Theme.SURFACE2;
            _pin.ForeColor = _note.Pinned ? Theme.AMBER : Theme.TEXT;
            Theme.Toast(this, _note.Pinned ? "Disematkan di atas" : "Sematan dilepas");
        }

        private void ToggleMode()
        {
            if (_note.Checklist) _note.ToText(); else _note.ToChecklist();
            Changed();
            Build(focus: false);
            FocusContent(false);
        }

        private void MoreMenu(Control anchor)
        {
            var m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem(Notes.Headline(_note)) { Enabled = false });
            m.Items.Add(new ToolStripMenuItem(_saved ? Notes.Edited(_note) : "Belum tersimpan") { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Salin teks", null, (_, _) => NoteUi.Copy(this, _note));
            m.Items.Add("Simpan sebagai teks (.txt)", null, (_, _) => NoteUi.ExportText(this, _note));
            m.Items.Add(new ToolStripSeparator());
            var del = m.Items.Add("Hapus", null, (_, _) =>
            {
                if (!_saved && _fresh) { _deleted = true; Close(); return; }
                if (NoteUi.ConfirmDelete(this, _note)) { _deleted = true; _dirty = false; Theme.Toast(Owner, "Catatan dihapus"); Close(); }
            });
            del.ForeColor = Theme.RED;
            m.Closed += (_, _) => BeginInvoke(new Action(m.Dispose));
            m.Show(anchor, new Point(anchor.Width - m.Width, anchor.Height));
        }
    }
}
