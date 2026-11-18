using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Dialog formulir serbaguna (padanan "sheet" bawah di Android): judul + subjudul, isian bertumpuk yang ikut skala
    /// DPI (<see cref="Theme.S"/>), area gulir, dan baris tombol aksi di bawah.
    /// </summary>
    internal sealed class Dlg : Form
    {
        public readonly FlowLayoutPanel Body;
        private readonly FlowLayoutPanel _actions;
        private readonly int _innerW;
        private static int S(int v) => Theme.S(v);

        public Dlg(string title, string subtitle, int width = 560, int maxHeight = 680)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int w = Math.Min(S(width), wa.Width - S(40));
            _innerW = w - S(48) - SystemInformation.VerticalScrollBarWidth;

            var head = new Panel { Dock = DockStyle.Top, Height = S(70), BackColor = Theme.SURFACE, Padding = new Padding(S(20), S(10), S(20), S(6)) };
            head.Controls.Add(new Label { UseMnemonic = false, Text = subtitle, Font = Theme.FontBody(9.5f), ForeColor = Theme.MUTED, Dock = DockStyle.Top, Height = S(22), AutoEllipsis = true });
            head.Controls.Add(new Label { UseMnemonic = false, Text = title, Font = Theme.FontHead(13f), ForeColor = Theme.TEXT, Dock = DockStyle.Top, Height = S(30), AutoEllipsis = true });

            Body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
                Padding = new Padding(S(20), S(12), S(8), S(8)), BackColor = Color.White
            };
            _actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(16), S(10), S(16), S(12)), BackColor = Theme.SURFACE, WrapContents = true
            };
            Controls.Add(Body);
            Controls.Add(_actions);
            Controls.Add(head);
            ClientSize = new Size(w, Math.Min(S(maxHeight), wa.Height - S(60)));
            Shown += (s, e) => FitHeight(maxHeight);
        }

        public int InnerWidth => _innerW;

        private void FitHeight(int maxHeight)
        {
            int content = Body.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical) + Body.Padding.Vertical + S(8);
            int want = S(70) + content + _actions.Height;
            var wa = Screen.FromControl(this).WorkingArea;
            ClientSize = new Size(ClientSize.Width, Math.Clamp(want, S(220), Math.Min(S(maxHeight), wa.Height - S(60))));
            if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        }

        public T Add<T>(T c, int top = 0) where T : Control
        {
            if (c.Width < _innerW && !(c is CheckBox) && !(c is Button)) c.Width = _innerW;
            c.Margin = new Padding(0, S(top), 0, S(6));
            Body.Controls.Add(c);
            return c;
        }

        public Label Label(string text, bool bold = false, Color? color = null, int top = 4)
        {
            var l = new Label
            {
                UseMnemonic = false, Text = text, AutoSize = true, MaximumSize = new Size(_innerW, 0),
                Font = bold ? Theme.FontLabel(10f, FontStyle.Bold) : Theme.FontBody(9.5f), ForeColor = color ?? (bold ? Theme.TEXT : Theme.MUTED)
            };
            return Add(l, top);
        }

        public TextBox Field(string label, string value = "", bool password = false, bool multiline = false, int lines = 4)
        {
            if (!string.IsNullOrEmpty(label)) Label(label);
            var t = new TextBox
            {
                Text = value, Font = Theme.FontBody(10.5f), UseSystemPasswordChar = password, Multiline = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
            };
            if (multiline) t.Height = S(22) * lines;
            return Add(t);
        }

        public CheckBox Check(string text, bool value)
        {
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true, Font = Theme.FontBody(10f), ForeColor = Theme.TEXT, MaximumSize = new Size(_innerW, 0) };
            return Add(c);
        }

        public ComboBox Choice(string label, IEnumerable<string> items, int selected = 0)
        {
            if (!string.IsNullOrEmpty(label)) Label(label);
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.FontBody(10f) };
            c.Items.AddRange(items.Cast<object>().ToArray());
            if (c.Items.Count > 0) c.SelectedIndex = Math.Clamp(selected, 0, c.Items.Count - 1);
            return Add(c);
        }

        public NumericUpDown Number(string label, decimal min, decimal max, decimal value)
        {
            if (!string.IsNullOrEmpty(label)) Label(label);
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Font = Theme.FontBody(10f), Width = S(140) };
            Body.Controls.Add(n); n.Margin = new Padding(0, 0, 0, S(6));
            return n;
        }

        public Button Action(string text, bool primary, Action click, Color? color = null)
        {
            var b = Theme.CreateButton(text, primary, color ?? Theme.ACCENT, S(40));
            b.Margin = new Padding(S(6), 0, 0, 0);
            b.Click += (s, e) => click();
            _actions.Controls.Add(b);
            if (primary && AcceptButton == null) AcceptButton = b;
            return b;
        }

        /// <summary>Baris horizontal untuk beberapa kontrol kecil.</summary>
        public FlowLayoutPanel Row(params Control[] items)
        {
            var r = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(_innerW, 0) };
            foreach (var c in items) { c.Margin = new Padding(0, 0, S(8), S(4)); r.Controls.Add(c); }
            Body.Controls.Add(r); r.Margin = new Padding(0, 0, 0, S(4));
            return r;
        }

        public Button SmallButton(string text, Action click, Color? color = null)
        {
            var b = Theme.CreateButton(text, false, color ?? Theme.ACCENT, S(34));
            b.Click += (s, e) => click();
            return b;
        }
    }

    /// <summary>Utilitas UI fitur: pilih berkas, minta teks, proses latar dengan progres, pratinjau gambar.</summary>
    internal static class Ui
    {
        private static int S(int v) => Theme.S(v);

        public const string FilterPdf = "PDF (*.pdf)|*.pdf";
        public const string FilterImages = "Gambar (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp";
        public const string FilterDocs = "Dokumen (*.pdf;*.docx;*.xlsx;*.csv;*.pptx;*.txt)|*.pdf;*.docx;*.xlsx;*.csv;*.pptx;*.txt";
        public const string FilterSheets = "Excel & CSV (*.xlsx;*.csv)|*.xlsx;*.csv";

        public static string? PickFile(IWin32Window owner, string title, string filter)
        {
            using var ofd = new OpenFileDialog { Title = title, Filter = filter + "|Semua berkas (*.*)|*.*" };
            return ofd.ShowDialog(owner) == DialogResult.OK ? ofd.FileName : null;
        }

        public static string[]? PickFiles(IWin32Window owner, string title, string filter)
        {
            using var ofd = new OpenFileDialog { Title = title, Filter = filter + "|Semua berkas (*.*)|*.*", Multiselect = true };
            return ofd.ShowDialog(owner) == DialogResult.OK ? ofd.FileNames : null;
        }

        public static string? AskText(IWin32Window owner, string title, string prompt, bool password = false, string value = "")
        {
            using var d = new Dlg(title, prompt, 440);
            var t = d.Field("", value, password);
            string? result = null;
            d.Action("OK", true, () => { result = t.Text; d.Close(); });
            d.Action("Batal", false, d.Close, Theme.TEXT);
            d.Shown += (s, e) => t.Focus();
            d.ShowDialog(owner);
            return result;
        }

        /// <summary>
        /// Jalankan <paramref name="work"/> di latar dengan jendela progres modal. Pengecualian ditampilkan sebagai pesan
        /// yang ramah. Mengembalikan (berhasil, hasil).
        /// </summary>
        public static (bool ok, T? result) Busy<T>(IWin32Window owner, string message, Func<Action<int, int>, T> work)
        {
            using var f = new Form
            {
                FormBorderStyle = FormBorderStyle.FixedDialog, ControlBox = false, StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(S(420), S(118)), BackColor = Color.White, ShowInTaskbar = false, Text = "Depup Office", AutoScaleMode = AutoScaleMode.None
            };
            var lbl = new Label { UseMnemonic = false, Text = message, Font = Theme.FontLabel(10f), ForeColor = Theme.TEXT, Location = new Point(S(20), S(18)), Size = new Size(S(380), S(24)), AutoEllipsis = true };
            var bar = new ProgressBar { Location = new Point(S(20), S(52)), Size = new Size(S(380), S(16)), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            var sub = new Label { UseMnemonic = false, Text = "Diproses 100% offline di komputer ini…", Font = Theme.FontBody(8.5f), ForeColor = Theme.MUTED, Location = new Point(S(20), S(78)), Size = new Size(S(380), S(20)) };
            f.Controls.AddRange(new Control[] { lbl, bar, sub });
            T? result = default; Exception? err = null;
            void Progress(int i, int n)
            {
                if (f.IsDisposed || !f.IsHandleCreated) return;
                f.BeginInvoke(new Action(() =>
                {
                    if (n <= 0) return;
                    bar.Style = ProgressBarStyle.Continuous; bar.Maximum = n; bar.Value = Math.Clamp(i, 0, n);
                    sub.Text = $"{i} / {n}";
                }));
            }
            f.Shown += async (s, e) =>
            {
                try { result = await Task.Run(() => work(Progress)); }
                catch (Exception ex) { err = ex; }
                f.Close();
            };
            f.ShowDialog(owner);
            if (err != null) { ShowError(owner, err); return (false, default); }
            return (true, result);
        }

        public static bool Busy(IWin32Window owner, string message, Action<Action<int, int>> work) =>
            Busy<bool>(owner, message, p => { work(p); return true; }).ok;

        public static void ShowError(IWin32Window owner, Exception ex)
        {
            while (ex is AggregateException ae && ae.InnerException != null) ex = ae.InnerException;
            string msg = ex switch
            {
                PdfPasswordException => ex.Message,
                ProtectedDocException => ex.Message,
                PdfException pe => "Struktur PDF tidak bisa dibaca: " + pe.Message,
                UnauthorizedAccessException => "Akses berkas ditolak. Tutup berkas di aplikasi lain lalu coba lagi.",
                IOException => "Berkas sedang dipakai aplikasi lain atau tidak bisa ditulis: " + ex.Message,
                OutOfMemoryException => "Memori tidak cukup untuk berkas sebesar ini.",
                _ => ex.Message
            };
            MessageBox.Show(owner, msg, "Depup Office", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Pratinjau gambar dengan tombol aksi opsional.</summary>
        public static void ShowImage(IWin32Window owner, Image img, string title, string? savedPath = null)
        {
            using var d = new Dlg(title, savedPath != null ? "Tersimpan: " + savedPath : "Pratinjau", 760, 760);
            var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Height = S(460), BackColor = Theme.SURFACE, BorderStyle = BorderStyle.FixedSingle };
            d.Add(pb);
            if (savedPath != null)
            {
                d.Action("Buka Folder", false, () => { try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{savedPath}\""); } catch { } }, Theme.TEXT);
                d.Action("Buka Gambar", true, () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(savedPath) { UseShellExecute = true }); } catch { } });
            }
            d.Action("Tutup", savedPath == null, d.Close, Theme.TEXT);
            d.ShowDialog(owner);
        }

        public static string PictureOut(string baseName, string ext)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string safe = AppStorage.BaseName(baseName);
            return Path.Combine(AppStorage.PicturesDir, $"{safe}_{stamp}.{ext}");
        }

        /// <summary>
        /// Pustaka Tanda Tangan &amp; Stempel (port SignatureManager.kt): pilih TTD/stempel tersimpan (klik), atau buat baru dari
        /// coretan / foto stempel di kertas putih — opsional disimpan ke pustaka untuk dipakai lagi.
        /// <paramref name="manageOnly"/> = hanya mengelola pustaka (tanpa mengembalikan gambar).
        /// </summary>
        public static Bitmap? PickStamp(IWin32Window owner, string title = "Pustaka Tanda Tangan & Stempel", bool manageOnly = false)
        {
            using var d = new Dlg(title, manageOnly ? "TTD & stempel tersimpan di PC ini (folder privat aplikasi). Klik kanan untuk menghapus."
                : "Klik TTD/stempel tersimpan untuk memakainya, atau gambar yang baru di kotak bawah.", 600, 760);
            Bitmap? result = null;
            var lib = new FlowLayoutPanel { Width = S(552), Height = S(150), AutoScroll = true, BackColor = Theme.SOFT, Padding = new Padding(S(6)), WrapContents = true };
            var empty = new Label { UseMnemonic = false, Text = "Pustaka masih kosong. Simpan TTD atau stempel pertama Anda di bawah.", AutoSize = true, ForeColor = Theme.MUTED, Font = Theme.FontBody(9.5f), Margin = new Padding(S(8)) };
            void Refresh()
            {
                foreach (Control c in lib.Controls.Cast<Control>().ToList()) { lib.Controls.Remove(c); if (c is PictureBox pb) pb.Image?.Dispose(); if (c != empty) c.Dispose(); }
                var items = SignatureLibrary.List();
                if (items.Count == 0) { lib.Controls.Add(empty); return; }
                foreach (var path in items)
                {
                    var img = SignatureLibrary.Load(path);
                    if (img == null) continue;
                    var pb = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(S(124), S(78)), BackColor = Color.White, Margin = new Padding(S(4)), Cursor = Cursors.Hand };
                    new ToolTip().SetToolTip(pb, Path.GetFileNameWithoutExtension(path) + (manageOnly ? "" : " — klik untuk memakai"));
                    var p = path;
                    if (!manageOnly) pb.Click += (s, e) => { result = SignatureLibrary.Load(p); d.Close(); };
                    var m = new ContextMenuStrip();
                    m.Items.Add("Hapus dari pustaka", null, (s, e) =>
                    {
                        if (MessageBox.Show(d, $"Hapus \"{Path.GetFileNameWithoutExtension(p)}\" dari pustaka?", "Pustaka TTD", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        SignatureLibrary.Delete(p); Refresh();
                    });
                    pb.ContextMenuStrip = m;
                    lib.Controls.Add(pb);
                }
            }
            d.Label("Tersimpan", bold: true);
            d.Add(lib);
            d.Label("Buat baru — gambar di kotak putih", bold: true, top: S(10));
            var pad = new SignaturePadControl { Height = S(170) };
            d.Add(pad);
            var save = d.Check("Simpan ke pustaka untuk dipakai lagi", true);
            var name = d.Field("Nama (opsional)", "");
            Refresh();

            Bitmap? Keep(Bitmap bmp)
            {
                if (save.Checked || manageOnly) { SignatureLibrary.Save(name.Text, bmp); Refresh(); }
                return bmp;
            }
            d.Action(manageOnly ? "Simpan Coretan" : "Pakai Coretan", true, () =>
            {
                var bmp = pad.IsEmpty ? null : pad.ToBitmap();
                if (bmp == null) { Theme.Toast(d, "Coretan masih kosong"); return; }
                Keep(bmp);
                if (manageOnly) { bmp.Dispose(); pad.Clear(); name.Text = ""; Theme.Toast(d, "Tersimpan di pustaka"); return; }
                result = bmp; d.Close();
            });
            d.Action("Dari Foto Stempel / TTD…", false, () =>
            {
                var p = PickFile(d, "Pilih foto stempel / TTD di atas kertas putih", FilterImages);
                if (p == null) return;
                try
                {
                    using var raw = PdfEngine.Decode(p, 1200);
                    var bmp = SignatureLibrary.MakeTransparent(raw);   // latar putih → transparan
                    if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(p);
                    Keep(bmp);
                    if (manageOnly) { bmp.Dispose(); name.Text = ""; Theme.Toast(d, "Tersimpan di pustaka"); return; }
                    result = bmp; d.Close();
                }
                catch (Exception ex) { ShowError(d, ex); }
            });
            d.Action("Bersihkan", false, () => pad.Clear(), Theme.TEXT);
            d.ShowDialog(owner);
            foreach (Control c in lib.Controls) if (c is PictureBox pb) pb.Image?.Dispose();
            return result;
        }
    }
}
