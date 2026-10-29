using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using DepupOfficeDesktop.Core;

namespace DepupOfficeDesktop.Forms
{
    /// <summary>
    /// Form Privacy Policy untuk Depup Office Desktop v2.2.0:
    /// - Menggunakan kontrol WebBrowser bawaan .NET (Zero NuGet Dependencies).
    /// - Memuat https://depupoffice-privacy.vercel.app/ saat online.
    /// - Fallback otomatis ke file lokal Documents\DepupOffice\privacy.html jika offline atau gagal memuat.
    /// - Menangani NewWindow & Navigating agar link saweria.co/depupdonate, mailto:depupdev@gmail.com,
    ///   dan github.com/MeledakCik/DepupOffice selalu dibuka di browser/aplikasi eksternal via Process.Start.
    /// </summary>
    public class PrivacyPolicyForm : Form
    {
        private WebBrowser _browser = null!;
        private Label _statusLbl = null!;
        private bool _usingFallback = false;

        public PrivacyPolicyForm()
        {
            Text = "Privacy Policy - Depup Office";
            Size = new Size(920, 720);
            MinimumSize = new Size(680, 480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.BG;

            PrivacyManager.EnsurePrivacyFileExists();
            BuildUi();
            LoadPrivacyPolicy();
        }

        private void BuildUi()
        {
            var topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 58,
                BackColor = Theme.SURFACE,
                Padding = new Padding(14, 10, 14, 10)
            };

            var btnBack = Theme.CreateButton("← Back", false, Theme.TEXT, 36, 95);
            btnBack.Location = new Point(14, 11);
            btnBack.Click += (s, e) =>
            {
                if (_browser.CanGoBack)
                    _browser.GoBack();
                else
                    Close();
            };
            topBar.Controls.Add(btnBack);

            var lblTitle = new Label
            {
                Text = "Privacy Policy - Depup Office",
                Font = Theme.FontHead(13.5f),
                ForeColor = Theme.TEXT,
                AutoSize = true,
                Location = new Point(122, 10)
            };
            topBar.Controls.Add(lblTitle);

            _statusLbl = new Label
            {
                Text = "Memuat kebijakan privasi...",
                Font = Theme.FontBody(8.8f),
                ForeColor = Theme.MUTED,
                AutoSize = true,
                Location = new Point(122, 34)
            };
            topBar.Controls.Add(_statusLbl);

            var rightActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var btnOffline = Theme.CreateButton("📄 Mode Offline", false, Theme.TEAL, 34);
            btnOffline.Click += (s, e) => LoadLocalFallback();

            var btnDonate = Theme.CreateButton("❤️ Dukung Developer", false, Theme.RED, 34);
            btnDonate.BackColor = Theme.RED_BG;
            btnDonate.Click += (s, e) => PrivacyManager.OpenSaweriaDonation();

            var btnClose = Theme.CreateButton("Tutup", true, Theme.ACCENT, 34, 80);
            btnClose.Click += (s, e) => Close();

            rightActions.Controls.AddRange(new Control[] { btnOffline, btnDonate, btnClose });
            topBar.Controls.Add(rightActions);

            _browser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScriptErrorsSuppressed = true,
                AllowWebBrowserDrop = false,
                IsWebBrowserContextMenuEnabled = true
            };

            _browser.NewWindow += OnBrowserNewWindow;
            _browser.Navigating += OnBrowserNavigating;
            _browser.DocumentCompleted += OnBrowserDocumentCompleted;

            Controls.Add(_browser);
            Controls.Add(topBar);
        }

        private void LoadPrivacyPolicy()
        {
            _usingFallback = false;
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                LoadLocalFallback();
                return;
            }

            try
            {
                _statusLbl.Text = $"Online • {PrivacyManager.OfficialPrivacyUrl}";
                _browser.Navigate(PrivacyManager.OfficialPrivacyUrl);
            }
            catch
            {
                LoadLocalFallback();
            }
        }

        private void LoadLocalFallback()
        {
            _usingFallback = true;
            PrivacyManager.EnsurePrivacyFileExists();
            string localPath = PrivacyManager.GetPrivacyHtmlPath();
            if (File.Exists(localPath))
            {
                _statusLbl.Text = $"Offline Fallback • {localPath}";
                _browser.Navigate(new Uri(localPath).AbsoluteUri);
            }
            else
            {
                _statusLbl.Text = "Offline Fallback (Embedded)";
                _browser.DocumentText = PrivacyManager.DefaultPrivacyHtmlResource;
            }
        }

        private void OnBrowserNewWindow(object? sender, CancelEventArgs e)
        {
            e.Cancel = true;
            string? targetUrl = _browser.StatusText;
            if (!string.IsNullOrWhiteSpace(targetUrl) && IsExternalTarget(targetUrl))
            {
                OpenInDefaultBrowser(targetUrl);
            }
            else if (_browser.Document?.ActiveElement != null)
            {
                string? href = _browser.Document.ActiveElement.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                    OpenInDefaultBrowser(href);
            }
        }

        private void OnBrowserNavigating(object? sender, WebBrowserNavigatingEventArgs e)
        {
            if (e.Url == null) return;
            string url = e.Url.ToString();

            if (IsExternalTarget(url))
            {
                e.Cancel = true;
                OpenInDefaultBrowser(url);
                return;
            }

            // Jika WebBrowser mengarahkan ke halaman error IE (res://ieframe.dll/dnserror.htm), gunakan fallback lokal
            if (url.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("dnserror", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("navcancl", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                LoadLocalFallback();
            }
        }

        private void OnBrowserDocumentCompleted(object? sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (_usingFallback) return;
            try
            {
                string title = _browser.DocumentTitle ?? "";
                string bodyText = _browser.Document?.Body?.InnerText ?? "";
                if (title.Contains("cannot display", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("tidak dapat menampilkan", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Navigation Canceled", StringComparison.OrdinalIgnoreCase) ||
                    bodyText.Contains("inetcpl.cpl", StringComparison.OrdinalIgnoreCase))
                {
                    LoadLocalFallback();
                }
            }
            catch
            {
                LoadLocalFallback();
            }
        }

        private static bool IsExternalTarget(string url)
        {
            return url.Contains("saweria.co/depupdonate", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("mailto:depupdev@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("github.com/MeledakCik/DepupOffice", StringComparison.OrdinalIgnoreCase);
        }

        private static void OpenInDefaultBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
