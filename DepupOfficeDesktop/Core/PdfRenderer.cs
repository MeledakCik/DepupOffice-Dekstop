using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinPdf = global::Windows.Data.Pdf;
using WinStreams = global::Windows.Storage.Streams;

namespace DepupOfficeDesktop.Core
{
    public enum PdfOpenStatus
    {
        Ok,
        PasswordRequired,
        WrongPassword,
        Failed
    }

    /// <summary>
    /// Pembaca/perender PDF asli memakai mesin PDF bawaan Windows (Windows.Data.Pdf, tanpa NuGet).
    /// Mendukung PDF terenkripsi (password) dan semua jenis PDF (teks, vektor, hasil scan).
    /// Password yang sudah benar diingat selama aplikasi berjalan agar semua alat PDF
    /// (Gabung, Pisah, Kompres, Susun, TTD, JPG, Cetak) tidak meminta ulang.
    /// </summary>
    public static class PdfRenderer
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        private static readonly Regex EncryptRx = new(@"/Encrypt\b", RegexOptions.Compiled);
        private static readonly Dictionary<string, string> Passwords = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new();

        private static string Key(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        public static string? GetCachedPassword(string path)
        {
            lock (Gate) return Passwords.TryGetValue(Key(path), out var pw) ? pw : null;
        }

        private static void Remember(string path, string password)
        {
            lock (Gate) Passwords[Key(path)] = password;
        }

        private static string ReadLatin1(FileStream fs, long offset, int count)
        {
            if (offset < 0) offset = 0;
            int len = (int)Math.Min(count, Math.Max(0, fs.Length - offset));
            if (len <= 0) return "";
            var buf = new byte[len];
            fs.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return Latin1.GetString(buf, 0, read);
        }

        /// <summary>True bila berkas PDF memiliki kamus enkripsi (/Encrypt) di trailer.</summary>
        public static bool IsEncrypted(string pdfPath)
        {
            try
            {
                using var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (EncryptRx.IsMatch(ReadLatin1(fs, fs.Length - 16384, 16384))) return true;
                return EncryptRx.IsMatch(ReadLatin1(fs, 0, 16384));
            }
            catch
            {
                return false;
            }
        }

        // ---------- Inti: buka dokumen di thread pool (hindari deadlock STA WinForms) ----------

        private static (PdfOpenStatus Status, T Value) Run<T>(string path, string? password, Func<WinPdf.PdfDocument, T> work)
        {
            try
            {
                return Task.Run(() => RunCore(path, password, work)).GetAwaiter().GetResult();
            }
            catch
            {
                return (PdfOpenStatus.Failed, default!);
            }
        }

        private static (PdfOpenStatus Status, T Value) RunCore<T>(string path, string? password, Func<WinPdf.PdfDocument, T> work)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch
            {
                return (PdfOpenStatus.Failed, default!);
            }

            using var ras = new WinStreams.InMemoryRandomAccessStream();
            WinPdf.PdfDocument? doc = TryLoad(ras, data, password);
            if (doc == null)
            {
                if (!IsEncrypted(path)) return (PdfOpenStatus.Failed, default!);

                // Mesin PDF Windows TIDAK mendukung AES-256 revisi 6 (dipakai Acrobat modern, Android Depup, dan alat
                // Kunci/Anti-Kopi/Dokumen Terlindungi di aplikasi ini). Dekripsi di MEMORI memakai parser internal,
                // lalu berikan salinan polosnya ke Windows. Tidak ada salinan polos yang ditulis ke disk.
                var (st, plain) = DecryptForWindows(path, data, password);
                if (plain == null) return (st, default!);
                using var ras2 = new WinStreams.InMemoryRandomAccessStream();
                doc = TryLoad(ras2, plain, null);
                if (doc == null) return (PdfOpenStatus.Failed, default!);
                return Work(doc, work);
            }
            return Work(doc, work);
        }

        private static (PdfOpenStatus, T) Work<T>(WinPdf.PdfDocument doc, Func<WinPdf.PdfDocument, T> work)
        {
            try { return (PdfOpenStatus.Ok, work(doc)); }
            catch { return (PdfOpenStatus.Failed, default!); }
        }

        private static WinPdf.PdfDocument? TryLoad(WinStreams.InMemoryRandomAccessStream ras, byte[] data, string? password)
        {
            try
            {
                ras.WriteAsync(data.AsBuffer()).AsTask().GetAwaiter().GetResult();
                ras.Seek(0);
                return string.IsNullOrEmpty(password)
                    ? WinPdf.PdfDocument.LoadFromStreamAsync(ras).AsTask().GetAwaiter().GetResult()
                    : WinPdf.PdfDocument.LoadFromStreamAsync(ras, password).AsTask().GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        // Satu entri cache (berkas terakhir) agar PageCount → Render → dst. tidak mendekripsi ulang PDF besar.
        private static (string Key, long Len, DateTime Mod, string Pw, byte[] Plain)? _plainCache;

        private static (PdfOpenStatus, byte[]?) DecryptForWindows(string path, byte[] data, string? password)
        {
            string key = Key(path), pw = password ?? "";
            DateTime mod;
            try { mod = File.GetLastWriteTimeUtc(path); } catch { mod = default; }
            lock (Gate)
            {
                if (_plainCache is { } c && c.Key == key && c.Len == data.Length && c.Mod == mod && c.Pw == pw)
                    return (PdfOpenStatus.Ok, c.Plain);
            }
            try
            {
                var f = PdfFile.Open(data, pw);
                var plain = PdfSave.WriteBytes(f);
                lock (Gate) { WipeCache(); _plainCache = (key, data.Length, mod, pw, plain); }
                return (PdfOpenStatus.Ok, plain);
            }
            catch (PdfPasswordException)
            {
                return (string.IsNullOrEmpty(password) ? PdfOpenStatus.PasswordRequired : PdfOpenStatus.WrongPassword, null);
            }
            catch { return (PdfOpenStatus.Failed, null); }
        }

        private static void WipeCache()
        {
            if (_plainCache is { } c) Array.Clear(c.Plain);
            _plainCache = null;
        }

        /// <summary>Hapus salinan terdekripsi di memori &amp; password yang diingat untuk <paramref name="path"/> (Dokumen Terlindungi ditutup).</summary>
        public static void Forget(string path)
        {
            string key = Key(path);
            lock (Gate)
            {
                Passwords.Remove(key);
                if (_plainCache is { } c && c.Key == key) WipeCache();
            }
        }

        // Windows.Data.Pdf menafsirkan DestinationWidth/Height dalam DIP: pada layar ber-skala (mis. 140%) hasilnya
        // 1,4× lebih besar dari yang diminta (≈2× memori & waktu). Faktor ini dipelajari dari render pertama.
        private static double _dipScale = 1.0;

        private static Bitmap RenderPage(WinPdf.PdfPage page, int maxDim, bool retried = false)
        {
            double pw = Math.Max(1.0, page.Size.Width);
            double ph = Math.Max(1.0, page.Size.Height);
            double scale = Math.Max(200, maxDim) / Math.Max(pw, ph);
            uint w = (uint)Math.Clamp(Math.Round(pw * scale), 1, 6000);
            uint h = (uint)Math.Clamp(Math.Round(ph * scale), 1, 6000);
            double dip = Volatile.Read(ref _dipScale);

            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, Math.Round(w / dip)),
                DestinationHeight = (uint)Math.Max(1, Math.Round(h / dip)),
                BackgroundColor = global::Microsoft.UI.Colors.White
            };

            using var ras = new WinStreams.InMemoryRandomAccessStream();
            page.RenderToStreamAsync(ras, options).AsTask().GetAwaiter().GetResult();
            ras.Seek(0);

            using var ms = new MemoryStream();
            using (var rs = ras.AsStreamForRead())
                rs.CopyTo(ms);
            ms.Position = 0;
            using var tmp = new Bitmap(ms);
            if (Math.Abs(tmp.Width - (int)w) <= 2 && Math.Abs(tmp.Height - (int)h) <= 2)
                return new Bitmap(tmp);

            // Ukuran meleset (skala DPI): pelajari faktornya lalu render ulang SEKALI dengan ukuran yang benar (tetap tajam).
            double got = tmp.Width / (double)options.DestinationWidth;
            if (!retried && got > 0.5 && got < 4)
            {
                Volatile.Write(ref _dipScale, got);
                return RenderPage(page, maxDim, true);
            }
            var dst = new Bitmap((int)w, (int)h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(tmp, 0, 0, (int)w, (int)h);
            }
            return dst;
        }

        private static List<Bitmap> RenderAll(WinPdf.PdfDocument doc, int maxDim)
        {
            var list = new List<Bitmap>();
            try
            {
                uint count = doc.PageCount;
                for (uint i = 0; i < count; i++)
                {
                    using var page = doc.GetPage(i);
                    list.Add(RenderPage(page, maxDim));
                }
                return list;
            }
            catch
            {
                foreach (var b in list) b.Dispose();
                throw;
            }
        }

        // ---------- API publik ----------

        /// <summary>Render semua halaman. Status Ok → pages terisi; selain itu pages kosong.</summary>
        public static PdfOpenStatus TryRender(string path, int maxDim, out List<Bitmap> pages)
        {
            pages = new List<Bitmap>();
            var (status, value) = Run(path, GetCachedPassword(path), d => RenderAll(d, maxDim));
            if (status == PdfOpenStatus.Ok && value != null)
                pages = value;
            return status;
        }

        /// <summary>Render hanya halaman [first, first+count) (0-based) — jauh lebih hemat dari <see cref="TryRender"/> untuk thumbnail / OCR sebagian.</summary>
        public static PdfOpenStatus TryRenderRange(string path, int maxDim, int first, int count, out List<Bitmap> pages)
        {
            pages = new List<Bitmap>();
            var (status, value) = Run(path, GetCachedPassword(path), d =>
            {
                var list = new List<Bitmap>();
                try
                {
                    uint end = (uint)Math.Min((long)d.PageCount, (long)Math.Max(0, first) + Math.Max(0, count));
                    for (uint i = (uint)Math.Max(0, first); i < end; i++)
                    {
                        using var page = d.GetPage(i);
                        list.Add(RenderPage(page, maxDim));
                    }
                    return list;
                }
                catch { foreach (var b in list) b.Dispose(); throw; }
            });
            if (status == PdfOpenStatus.Ok && value != null) pages = value;
            return status;
        }

        /// <summary>
        /// Render halaman satu per satu dan serahkan ke <paramref name="onPage"/> (indeks, total, bitmap) — bitmap dibuang
        /// setelah callback kembali, sehingga memori tetap kecil untuk PDF ratusan halaman. Callback berjalan di thread latar
        /// dan mengembalikan false untuk berhenti lebih awal.
        /// </summary>
        public static PdfOpenStatus RenderEach(string path, int maxDim, Func<int, int, Bitmap, bool> onPage)
        {
            Exception? callbackError = null;
            var (status, _) = Run(path, GetCachedPassword(path), d =>
            {
                int total = (int)d.PageCount;
                for (uint i = 0; i < d.PageCount; i++)
                {
                    using var page = d.GetPage(i);
                    using var bmp = RenderPage(page, maxDim);
                    bool more;
                    try { more = onPage((int)i, total, bmp); }
                    catch (Exception ex) { callbackError = ex; throw; }
                    if (!more) break;
                }
                return total;
            });
            if (callbackError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackError).Throw();
            return status;
        }

        /// <summary>Jumlah halaman, atau -1 bila PDF tidak dapat dibuka.</summary>
        public static int PageCount(string path)
        {
            var (status, value) = Run(path, GetCachedPassword(path), d => (int)d.PageCount);
            return status == PdfOpenStatus.Ok ? value : -1;
        }

        /// <summary>Coba buka dengan password (atau tanpa password bila kosong); ingat password bila benar.</summary>
        public static PdfOpenStatus Unlock(string path, string? password)
        {
            var (status, _) = Run(path, password, d => (int)d.PageCount);
            if (status == PdfOpenStatus.Ok && !string.IsNullOrEmpty(password))
                Remember(path, password);
            return status;
        }

        /// <summary>True bila PDF sudah bisa dibuka (tidak terenkripsi, password kosong, atau password sudah diingat).</summary>
        public static bool IsOpenable(string path)
        {
            var (status, _) = Run(path, GetCachedPassword(path), d => (int)d.PageCount);
            return status == PdfOpenStatus.Ok;
        }

        /// <summary>
        /// Pastikan PDF siap dibaca. Untuk PDF ber-password, tampilkan dialog password sampai benar atau dibatalkan.
        /// Mengembalikan false bila pengguna membatalkan / PDF tidak dapat dibuka.
        /// </summary>
        public static bool EnsureUnlocked(IWin32Window? owner, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
            if (!IsEncrypted(path)) return true;
            if (IsOpenable(path)) return true;

            string? message = null;
            int attempts = 0;
            while (true)
            {
                string? pw = PdfPasswordDialog.Ask(owner, Path.GetFileName(path), message);
                if (pw == null) return false;

                var status = Unlock(path, pw);
                if (status == PdfOpenStatus.Ok) return true;

                attempts++;
                message = attempts >= 3
                    ? "Password salah. Jika Anda yakin password sudah benar, jenis enkripsi PDF ini mungkin tidak didukung Windows; buka dengan Microsoft Edge / Adobe."
                    : "Password salah. Silakan coba lagi.";
            }
        }
    }

    /// <summary>Dialog kecil untuk memasukkan password PDF.</summary>
    internal static class PdfPasswordDialog
    {
        public static string? Ask(IWin32Window? owner, string fileName, string? errorMessage)
        {
            using var f = new Form
            {
                Text = "PDF Dilindungi Password",
                ClientSize = new Size(460, string.IsNullOrEmpty(errorMessage) ? 180 : 230),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.SURFACE
            };

            var lbl = new Label
            {
                Text = $"🔒 {fileName}\nBerkas ini dilindungi password. Masukkan password untuk membukanya:",
                Font = Theme.FontLabel(9.5f),
                Location = new Point(18, 16),
                Size = new Size(424, 44)
            };

            var txt = new TextBox
            {
                Location = new Point(18, 70),
                Width = 424,
                Font = Theme.FontBody(11f),
                UseSystemPasswordChar = true
            };

            var chkShow = new CheckBox
            {
                Text = "Tampilkan password",
                Font = Theme.FontBody(9f),
                Location = new Point(18, 104),
                AutoSize = true
            };
            chkShow.CheckedChanged += (s, e) => txt.UseSystemPasswordChar = !chkShow.Checked;

            int y = 134;
            var controls = new List<Control> { lbl, txt, chkShow };
            if (!string.IsNullOrEmpty(errorMessage))
            {
                controls.Add(new Label
                {
                    Text = errorMessage,
                    Font = Theme.FontLabel(9f),
                    ForeColor = Theme.RED,
                    Location = new Point(18, 130),
                    Size = new Size(424, 48)
                });
                y = 184;
            }

            var btnOk = Theme.CreateButton("Buka", true, Theme.ACCENT, 34, 110);
            btnOk.Location = new Point(332, y);
            string? result = null;
            btnOk.Click += (s, e) =>
            {
                if (txt.Text.Length == 0) return;
                result = txt.Text;
                f.DialogResult = DialogResult.OK;
                f.Close();
            };

            var btnCancel = Theme.CreateButton("Batal", false, Theme.TEXT, 34, 100);
            btnCancel.Location = new Point(222, y);
            btnCancel.Click += (s, e) => { f.DialogResult = DialogResult.Cancel; f.Close(); };

            controls.Add(btnOk);
            controls.Add(btnCancel);
            f.Controls.AddRange(controls.ToArray());
            f.AcceptButton = btnOk;
            f.CancelButton = btnCancel;
            f.Shown += (s, e) => txt.Focus();

            Theme.ScaleRawDialog(f);
            return f.ShowDialog(owner) == DialogResult.OK ? result : null;
        }
    }
}
