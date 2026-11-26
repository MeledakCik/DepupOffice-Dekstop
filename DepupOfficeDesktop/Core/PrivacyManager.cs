using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Mengelola file Privacy Policy lokal (Documents\DepupOffice\privacy.html), URL resmi,
    /// serta pembukaan tautan eksternal (Saweria, Email Developer, dan GitHub) di browser default.
    /// </summary>
    public static class PrivacyManager
    {
        public const string OfficialPrivacyUrl = "https://depupstudio.com/office/privacy-policy";
        public const string OfficialSaweriaUrl = "https://saweria.co/depupdonate";
        public const string DeveloperEmail = "depupdev@gmail.com";
        public const string DeveloperMailto = "mailto:depupdev@gmail.com";
        public const string OfficialGithubUrl = "https://github.com/MeledakCik/DepupOffice";

        public const string ComplianceSummaryText =
            "Depup Office Desktop v4.4.2 - FREE, Offline, Zero Cloud Upload. " +
            "Donasi sukarela via https://saweria.co/depupdonate (tidak membuka fitur premium, diproses di luar aplikasi)";

        /// <summary>
        /// Mengembalikan path lengkap Documents\DepupOffice\privacy.html
        /// </summary>
        public static string GetPrivacyHtmlPath()
        {
            string docsDir = !string.IsNullOrWhiteSpace(AppStorage.OutDir)
                ? AppStorage.OutDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DepupOffice");
            Directory.CreateDirectory(docsDir);
            return Path.Combine(docsDir, "privacy.html");
        }

        /// <summary>
        /// Memastikan file Documents\DepupOffice\privacy.html tersedia untuk fallback offline.
        /// Jika belum ada, menyalin dari folder aplikasi (output build/publish) atau membuat dari string resource.
        /// </summary>
        public static void EnsurePrivacyFileExists()
        {
            try
            {
                string targetPath = GetPrivacyHtmlPath();
                // salinan lama (URL kebijakan lama) diganti versi terbaru
                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 100 &&
                    File.ReadAllText(targetPath).Contains("depupstudio.com/office/privacy-policy"))
                    return;

                string bundledPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "privacy.html");
                if (File.Exists(bundledPath))
                {
                    File.Copy(bundledPath, targetPath, overwrite: true);
                    return;
                }

                File.WriteAllText(targetPath, DefaultPrivacyHtmlResource, Encoding.UTF8);
            }
            catch
            {
                // Abaikan jika gagal menulis ke disk pada lingkungan terbatas
            }
        }

        public static void OpenExternalLink(string url)
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

        public static void OpenSaweriaDonation() => OpenExternalLink(OfficialSaweriaUrl);
        public static void OpenDeveloperEmail() => OpenExternalLink(DeveloperMailto);
        public static void OpenGithubRepo() => OpenExternalLink(OfficialGithubUrl);

        public const string DefaultPrivacyHtmlResource = @"<!DOCTYPE html>
<html lang=""id"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>Kebijakan Privasi - depup Office Desktop</title>
<meta name=""description"" content=""Privacy Policy for Depup Office Desktop"">
<style>
  :root{--bg:#F5F6FA;--surface:#FFFFFF;--text:#151823;--muted:#535A6C;--primary:#4338CA;--line:#D5D9E3;--ok:#15703A;--okbg:#DCF5E4}
  @media (prefers-color-scheme: dark){:root{--bg:#0E1016;--surface:#181B23;--text:#ECEEF4;--muted:#A9AFC1;--primary:#A8B1FF;--line:#373C4C;--ok:#5BD88A;--okbg:#15301F}}
  *{box-sizing:border-box}
  body{font-family:'Segoe UI',-apple-system,Roboto,sans-serif;line-height:1.65;color:var(--text);background:var(--bg);padding:16px;margin:0;font-size:16px}
  .container{max-width:760px;margin:0 auto;background:var(--surface);padding:24px 20px;border-radius:16px;border:1px solid var(--line)}
  h1{font-weight:700;font-size:26px;margin:0 0 6px;color:var(--text)}
  .meta{color:var(--muted);font-size:14px;margin-bottom:24px}
  h2{font-size:19px;margin:28px 0 10px;color:var(--text)}
  p,li{color:var(--text)}
  a{color:var(--primary);font-weight:600;text-decoration:underline;text-underline-offset:2px}
  .box{background:var(--bg);padding:14px 16px;border-radius:12px;margin:18px 0;border:1px solid var(--line)}
  .footer{margin-top:36px;padding-top:18px;font-size:14px;color:var(--muted);text-align:center;border-top:1px solid var(--line)}
  table{width:100%;border-collapse:collapse;font-size:14px;margin:10px 0}
  td{border:1px solid var(--line);padding:8px;vertical-align:top}
  td b{display:block;font-size:12px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}
  .badge{display:inline-block;background:var(--okbg);color:var(--ok);font-weight:600;padding:5px 12px;border-radius:20px;font-size:13px;margin-bottom:14px}
</style>
</head>
<body>
<div class=""container"">
  <div class=""badge"">Tanpa akun · tanpa iklan · tanpa analitik</div>
  <h1>Kebijakan Privasi depup Office</h1>
  <div class=""meta"">
    Berlaku sejak 25 November 2026 · Pengembang: Depup Studio (<a href=""mailto:depupdev@gmail.com"">depupdev@gmail.com</a>)<br>
    Versi resmi &amp; terbaru: <a href=""https://depupstudio.com/office/privacy-policy"">depupstudio.com/office/privacy-policy</a>
  </div>

  <div class=""box"">
    <strong>Ringkasnya:</strong> depup Office tidak mengumpulkan data pribadi. Dokumen, scan, dan catatan diproses dan disimpan di komputer Anda. Internet hanya dipakai untuk mengunduh link yang Anda tempelkan ke aplikasi dan setujui, serta menampilkan versi terbaru kebijakan ini.
  </div>

  <h2>1. Data yang kami kumpulkan</h2>
  <p><strong>Tidak ada.</strong> Aplikasi tidak memiliki akun atau pendaftaran, tidak menampilkan iklan, dan tidak memakai alat analitik atau pelacak. Kami tidak menerima isi dokumen, foto, catatan, atau daftar berkas Anda.</p>
  <ul>
    <li>Dokumen, PDF, dan hasil scan disimpan di folder <em>Documents\DepupOffice</em>.</li>
    <li>Catatan, riwayat berkas, berkas yang disematkan, dan pengaturan disimpan lokal di <em>%LocalAppData%\DepupOfficeDesktop</em>.</li>
    <li>Berkas di Brankas dienkripsi AES-256-GCM dan dikunci PIN.</li>
  </ul>

  <h2>2. Kapan aplikasi memakai internet</h2>
  <table>
    <tr><td><b>Buka dari link</b>Anda menempelkan atau menyeret link berkas (Google Drive/Docs, share.google, LMS) ke depup Office lalu menekan “Unduh &amp; buka”. Berkas diunduh lewat HTTPS dari server pemilik link, tanpa cookie atau data akun Anda.</td></tr>
    <tr><td><b>Kebijakan privasi</b>Jendela Kebijakan Privasi memuat versi terbaru dari depupstudio.com; bila offline, salinan lokal ini yang ditampilkan.</td></tr>
    <tr><td><b>Donasi</b>Tombol dukungan hanya membuka saweria.co di browser.</td></tr>
  </table>
  <p>OCR memakai mesin OCR bawaan Windows yang berjalan di komputer ini. Gambar atau skrip dari internet di dalam dokumen Word/Excel tidak dimuat.</p>

  <h2>3. Izin perangkat</h2>
  <ul>
    <li><strong>Kamera</strong> — memindai dokumen dan kode QR; diproses di komputer ini.</li>
    <li><strong>Internet</strong> — hanya untuk keadaan di bagian 2.</li>
    <li><strong>Printer</strong> — hanya saat Anda menekan Cetak.</li>
  </ul>

  <h2>4. Dokumen Terlindungi</h2>
  <p>Isinya dienkripsi AES-256-GCM dengan kunci dari kode akses pembuat dokumen. Kode tidak pernah dikirim ke server; bila Anda memilih “ingat kode”, kode disimpan terenkripsi dengan perlindungan data Windows (DPAPI) untuk akun Windows Anda saja. Tangkapan dan rekam layar diblokir saat dokumen terbuka.</p>

  <h2>5. Pihak ketiga</h2>
  <p>Saweria (hanya bila berdonasi) dan server pemilik link yang Anda unduh. Kami tidak menjual atau membagikan data Anda.</p>

  <h2>6. Penghapusan data</h2>
  <p>Data ada di komputer Anda: hapus berkas kapan saja. Menghapus folder <em>%LocalAppData%\DepupOfficeDesktop</em> menghapus catatan, pengaturan, kode yang diingat, dan isi Brankas; berkas di Documents\DepupOffice tetap ada sampai Anda menghapusnya.</p>

  <h2>7. Anak-anak</h2>
  <p>Aplikasi tidak mengumpulkan data pribadi dari siapa pun, termasuk anak-anak.</p>

  <h2>8. Perubahan &amp; kontak</h2>
  <p>Versi terbaru selalu ada di <a href=""https://depupstudio.com/office/privacy-policy"">depupstudio.com/office/privacy-policy</a>. Pertanyaan: <a href=""mailto:depupdev@gmail.com"">depupdev@gmail.com</a>.</p>

  <div class=""footer"">© 2026 Depup Studio · Bandung, Indonesia</div>
</div>
</body>
</html>";
    }
}
