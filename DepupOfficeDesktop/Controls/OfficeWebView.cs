using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Core.Office;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Penampil dokumen Office berbasis WebView2 (padanan OfficeView.kt Android). HTML &amp; gambar dokumen dilayani dari
    /// memori di host virtual https://doc.local/ — tidak ada akses jaringan. Navigasi keluar diblokir; tautan di dokumen
    /// dibuka di browser bawaan. Zoom: Ctrl+roda mouse / Ctrl +/- (bawaan WebView2) atau <see cref="Zoom"/>.
    /// </summary>
    public sealed class OfficeWebView : UserControl
    {
        private const string Host = "https://doc.local/";
        private readonly WebView2 _wv;
        private RenderedDoc? _doc;
        private byte[]? _html;
        private bool _initStarted;
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Label _status;

        /// <summary>Dokumen selesai ditata (jumlah halaman; 0 bila tidak dihitung).</summary>
        public event Action<int>? DocumentReady;
        public event Action<double>? ZoomChanged;
        /// <summary>Pesan galat yang ramah pengguna (mis. runtime WebView2 tidak terpasang).</summary>
        public event Action<string>? Failed;

        public bool AllowCopy { get; set; } = true;

        public OfficeWebView()
        {
            BackColor = Theme.BG;
            _status = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.FontBody(11f),
                ForeColor = Theme.MUTED, Text = "Menyiapkan dokumen…", BackColor = Theme.BG
            };
            _wv = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.BG, Visible = false };
            Controls.Add(_wv);
            Controls.Add(_status);
        }

        public static string UserDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupOfficeDesktop", "WebView2");

        public static bool RuntimeAvailable()
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
            catch { return false; }
        }

        private async Task EnsureCoreAsync()
        {
            if (_initStarted) { await _ready.Task; return; }
            _initStarted = true;
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder,
                    new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --no-proxy-server"));
                await _wv.EnsureCoreWebView2Async(env);
                var core = _wv.CoreWebView2;
                var st = core.Settings;
                st.AreDevToolsEnabled = false;
                st.IsStatusBarEnabled = false;
                st.IsZoomControlEnabled = true;
                st.AreDefaultContextMenusEnabled = AllowCopy;
                st.IsGeneralAutofillEnabled = false;
                st.IsPasswordAutosaveEnabled = false;
                st.IsWebMessageEnabled = true;
                core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnResource;
                core.NavigationStarting += (s, e) =>
                {
                    if (e.Uri.StartsWith(Host, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                };
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
                core.WebMessageReceived += (s, e) =>
                {
                    string msg; try { msg = e.TryGetWebMessageAsString(); } catch { return; }
                    if (msg.StartsWith("ready:", StringComparison.Ordinal))
                    {
                        int.TryParse(msg[6..], out var n);
                        // tata letak memindahkan isi ke halaman: mulai baca dari halaman 1
                        try { _ = core.ExecuteScriptAsync("window.scrollTo(0,0)"); } catch { }
                        DocumentReady?.Invoke(n);
                    }
                };
                _wv.ZoomFactorChanged += (s, e) => ZoomChanged?.Invoke(_wv.ZoomFactor);
                _ready.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _ready.TrySetResult(false);
                ShowStatus("Komponen tampilan Microsoft Edge WebView2 tidak tersedia di PC ini.\nPasang \"WebView2 Runtime\" dari Microsoft lalu buka kembali dokumen.");
                Failed?.Invoke(ex.Message);
            }
        }

        private void ShowStatus(string text)
        {
            _status.Text = text;
            _status.Visible = true;
            _status.BringToFront();
        }

        private static void OpenExternal(string uri)
        {
            if (!(uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))) return;
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Tampilkan dokumen hasil render. Kepemilikan <paramref name="doc"/> berpindah ke kontrol ini.</summary>
        public async Task LoadAsync(RenderedDoc doc)
        {
            _doc?.Dispose();
            _doc = doc;
            _html = Encoding.UTF8.GetBytes(doc.Html);
            doc.DropHtml();
            await EnsureCoreAsync();
            if (_wv.CoreWebView2 == null) return;
            _wv.CoreWebView2.Navigate(Host + "index.html?v=" + Environment.TickCount64);
            _wv.Visible = true;
            _status.Visible = false;
        }

        public void ShowMessage(string text)
        {
            _wv.Visible = false;
            ShowStatus(text);
        }

        private void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var core = _wv.CoreWebView2;
            try
            {
                var uri = new Uri(e.Request.Uri);
                string path = uri.AbsolutePath;
                if (path == "/index.html" && _html != null)
                {
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(_html), 200, "OK",
                        "Content-Type: text/html; charset=utf-8\nContent-Security-Policy: default-src 'none'; img-src https://doc.local data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src 'none'");
                    return;
                }
                if (path.StartsWith("/r/", StringComparison.Ordinal) && _doc != null && int.TryParse(path[3..], out int id))
                {
                    var item = _doc.Res.Get(id);
                    var raw = item != null ? _doc.Res.Read(item) : null;
                    var prep = raw != null ? OfficeImage.Prepare(raw, item!.CssW, item.CssH) : null;
                    if (prep != null)
                    {
                        e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(prep.Value.Data), 200, "OK",
                            "Content-Type: " + prep.Value.Mime + "\nCache-Control: max-age=3600");
                        return;
                    }
                }
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Array.Empty<byte>()), 404, "Not Found", "Content-Type: text/plain");
            }
            catch
            {
                try { e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Array.Empty<byte>()), 404, "Not Found", ""); } catch { }
            }
        }

        /// <summary>Faktor zoom (1.0 = 100%).</summary>
        public double Zoom
        {
            get => _wv.CoreWebView2 != null ? _wv.ZoomFactor : 1.0;
            set { if (_wv.CoreWebView2 != null) _wv.ZoomFactor = Math.Clamp(value, 0.25, 5.0); }
        }

        /// <summary>Tema kertas: 0 Siang, 1 Malam, 2 Sepia, 3 Eye-Care hijau (hanya tampilan layar).</summary>
        public async void ApplyPaperTheme(int theme)
        {
            await _ready.Task;
            if (_wv.CoreWebView2 == null) return;
            string filter = theme switch
            {
                1 => "invert(0.92) hue-rotate(180deg)",
                2 => "sepia(0.35) brightness(0.97)",
                3 => "sepia(0.2) hue-rotate(50deg) saturate(1.1) brightness(0.97)",
                _ => "none"
            };
            string bg = theme switch { 1 => "#1b1b1a", 2 => "#efe3c8", 3 => "#d8e8cf", _ => "#F1F0EC" };
            try
            {
                await _wv.CoreWebView2.ExecuteScriptAsync(
                    "(function(){var s=document.getElementById('__dp_theme');if(!s){s=document.createElement('style');s.id='__dp_theme';document.head.appendChild(s);}" +
                    "s.textContent='@media screen{html,body{background:" + bg + " !important}.page,.pg,#flow,.sheet{filter:" + filter + "}}';})();");
            }
            catch { }
        }

        /// <summary>Dialog cetak Windows untuk dokumen (halaman sesuai tata letak, tanpa tema layar).</summary>
        public void Print()
        {
            try { _wv.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.System); } catch { }
        }

        /// <summary>Simpan tampilan dokumen sebagai PDF (tata letak halaman asli, teks tetap bisa dicari).</summary>
        public async Task<bool> SaveAsPdfAsync(string path)
        {
            var core = _wv.CoreWebView2;
            if (core == null) return false;
            try
            {
                var ps = core.Environment.CreatePrintSettings();
                ps.ShouldPrintBackgrounds = true;
                ps.ShouldPrintHeaderAndFooter = false;
                ps.MarginTop = ps.MarginBottom = ps.MarginLeft = ps.MarginRight = 0;
                // ukuran kertas = ukuran halaman Word (bagian pertama), bukan Letter bawaan
                var json = await core.ExecuteScriptAsync("typeof SECTS!=='undefined'&&SECTS.length?SECTS[0].w+'x'+SECTS[0].h:''");
                var size = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
                var parts = size.Split('x');
                if (parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wPt)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hPt)
                    && wPt > 36 && hPt > 36)
                {
                    ps.Orientation = CoreWebView2PrintOrientation.Portrait;
                    ps.PageWidth = wPt / 72.0;
                    ps.PageHeight = hPt / 72.0;
                }
                return await core.PrintToPdfAsync(path, ps);
            }
            catch { return false; }
        }

        /// <summary>Teks yang sedang dipilih (kosong bila tidak ada / salin dinonaktifkan).</summary>
        public async Task<string> SelectedTextAsync()
        {
            if (!AllowCopy || _wv.CoreWebView2 == null) return "";
            try
            {
                var json = await _wv.CoreWebView2.ExecuteScriptAsync("window.getSelection().toString()");
                return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Seluruh teks dokumen yang tampil (untuk baca lantang).</summary>
        public async Task<string> AllTextAsync()
        {
            if (_wv.CoreWebView2 == null) return "";
            try
            {
                var json = await _wv.CoreWebView2.ExecuteScriptAsync("(document.getElementById('pages')||document.body).innerText");
                return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Faktor zoom agar halaman terlebar muat di lebar penampil (maks. 100%), seperti Android saat dokumen dibuka.
        /// Mengembalikan faktor yang diterapkan.
        /// </summary>
        public async Task<double> FitWidthAsync()
        {
            var core = _wv.CoreWebView2;
            if (core == null) return Zoom;
            try
            {
                var json = await core.ExecuteScriptAsync(
                    "(function(){var w=0;document.querySelectorAll('.page,.sheet table.grid').forEach(function(p){w=Math.max(w,p.offsetWidth)});return w;})()");
                if (!double.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pageCss) || pageCss <= 0) return Zoom;
                // lebar penampil dalam piksel CSS pada zoom 100% (dikurangi bilah gulir & sedikit tepi)
                double scale = DeviceDpi / 96.0;
                double viewCss = (_wv.ClientSize.Width / scale) - 40;
                double fit = Math.Clamp(viewCss / pageCss, 0.3, 1.0);
                Zoom = fit;
                return fit;
            }
            catch { return Zoom; }
        }

        public async void ScrollToPage(int index)
        {
            if (_wv.CoreWebView2 == null) return;
            try { await _wv.CoreWebView2.ExecuteScriptAsync("(function(){var p=document.querySelectorAll('.page')[" + index + "];if(p)p.scrollIntoView();})()"); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _doc?.Dispose(); _doc = null; _html = null;
                try { _wv.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
