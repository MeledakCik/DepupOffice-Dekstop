using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Unduh berkas dari LINK (port <c>LinkFetch.kt</c> Android 4.4.1): link Google Drive, Google Docs, share.google, LMS.
    /// DepUp tetap offline: internet hanya dipakai saat pengguna sendiri menempelkan link dan menyetujui unduhannya.
    /// Tidak ada cookie, akun, atau data pengguna yang dikirim selain permintaan HTTPS biasa ke alamat tersebut.
    /// </summary>
    public static class LinkFetch
    {
        public const long MAX_BYTES = 100L << 20;   // 100 MB
        private const int MAX_HOPS = 8;

        public sealed class LinkException : IOException { public LinkException(string m) : base(m) { } }

        public enum Kind { Pdf, Docx, Xlsx, Pptx }

        public static string Ext(Kind k) => k switch { Kind.Pdf => "pdf", Kind.Docx => "docx", Kind.Xlsx => "xlsx", _ => "pptx" };

        public sealed record Result(byte[] Bytes, Kind Kind, string Name);

        public const string NEED_LOGIN = "Link ini tidak langsung ke berkas PDF/Office (mungkin perlu login, mis. LMS). " +
            "Buka link di browser, unduh berkasnya, lalu buka berkas itu di DepUp Office.";

        private static readonly Regex UrlRe = new(@"https://[^\s<>""'`]+");

        /// <summary>Link https pertama di teks ("Judul https://…"); null bila tidak ada.</summary>
        public static string? ExtractUrl(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = UrlRe.Match(text);
            return m.Success ? m.Value.TrimEnd('.', ',', ')', ']', '}', ';', '!', '?') : null;
        }

        /// <summary>Judul sebelum link (nama berkas yang disertakan aplikasi pembagi), mis. "DDL_DML.pdf".</summary>
        public static string TitleOf(string? text, string url) =>
            (text ?? "").Replace(url, " ").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("http")) ?? "";

        private static readonly Regex DriveFile = new(@"^https://drive\.google\.com/(?:file/d/|open\?id=|uc\?(?:.*&)?id=)([A-Za-z0-9_-]{10,})");
        private static readonly Regex DocsFile = new(@"^https://docs\.google\.com/(document|spreadsheets|presentation)/d/([A-Za-z0-9_-]{10,})");

        /// <summary>Halaman pratinjau → alamat unduhan langsung (Drive → usercontent, Docs/Sheets/Slides → ekspor PDF).</summary>
        public static string DirectUrl(string url)
        {
            var d = DriveFile.Match(url);
            if (d.Success) return $"https://drive.usercontent.google.com/download?id={d.Groups[1].Value}&export=download&confirm=t";
            var g = DocsFile.Match(url);
            if (g.Success)
            {
                var id = g.Groups[2].Value;
                return g.Groups[1].Value == "presentation"
                    ? $"https://docs.google.com/presentation/d/{id}/export/pdf"
                    : $"https://docs.google.com/{g.Groups[1].Value}/d/{id}/export?format=pdf";
            }
            return url;
        }

        /// <summary>Jenis berkas dari isinya (bukan dari header server yang bisa salah).</summary>
        public static Kind? Sniff(byte[] b, string nameHint)
        {
            if (b.Length >= 5 && Encoding.Latin1.GetString(b, 0, 5) == "%PDF-") return Kind.Pdf;
            // Sebagian server menambah byte/BOM sebelum header PDF: cari di 1 KB pertama.
            if (Encoding.Latin1.GetString(b, 0, Math.Min(b.Length, 1024)).Contains("%PDF-")) return Kind.Pdf;
            if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && b[2] == 3 && b[3] == 4)
            {
                var head = Encoding.Latin1.GetString(b, 0, Math.Min(b.Length, 64 * 1024));
                if (head.Contains("word/")) return Kind.Docx;
                if (head.Contains("xl/")) return Kind.Xlsx;
                if (head.Contains("ppt/")) return Kind.Pptx;
                return Path.GetExtension(nameHint).ToLowerInvariant() switch { ".docx" => Kind.Docx, ".xlsx" => Kind.Xlsx, ".pptx" => Kind.Pptx, _ => null };
            }
            return null;
        }

        private static readonly Regex DispStar = new(@"filename\*=(?:UTF-8'')?([^;]+)", RegexOptions.IgnoreCase);
        private static readonly Regex DispPlain = new(@"filename=""?([^"";]+)""?", RegexOptions.IgnoreCase);
        private static readonly Regex BadChars = new(@"[\\/:*?""<>|\u0000-\u001F]+");

        /// <summary>Nama berkas dari Content-Disposition, lalu dari alamat; tanpa karakter jalur.</summary>
        public static string FileName(string? disposition, string url, string title)
        {
            string? fromHeader = null;
            if (disposition != null)
            {
                var m = DispStar.Match(disposition);
                if (m.Success) { try { fromHeader = Uri.UnescapeDataString(m.Groups[1].Value.Trim('"', ' ')); } catch { } }
                if (fromHeader == null) { var p = DispPlain.Match(disposition); if (p.Success) fromHeader = p.Groups[1].Value; }
            }
            string? fromUrl = null;
            try { var seg = new Uri(url).AbsolutePath; fromUrl = Uri.UnescapeDataString(seg[(seg.LastIndexOf('/') + 1)..]); } catch { }
            var raw = new[] { fromHeader, title.Contains('.') ? title : null, fromUrl, title }
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x is not ("download" or "export" or "pdf" or "view")) ?? "dokumen";
            var clean = BadChars.Replace(raw, "_").Trim();
            if (clean.Length > 120) clean = clean[..120];
            return string.IsNullOrWhiteSpace(clean) ? "dokumen" : clean;
        }

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>Unduh <paramref name="link"/> (ikuti pengalihan https saja, maks. 100 MB). Jalankan di luar thread UI.</summary>
        public static Result Download(string link, string title = "", CancellationToken ct = default)
        {
            var url = DirectUrl(link);
            if (!url.StartsWith("https://")) throw new LinkException("Hanya link https yang bisa dibuka.");
            for (int hop = 0; hop < MAX_HOPS; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0) DepUpOffice");
                req.Headers.TryAddWithoutValidation("Accept", "application/pdf,application/octet-stream,*/*;q=0.8");
                HttpResponseMessage res;
                try { res = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (HttpRequestException ex) { throw new LinkException("Tidak bisa terhubung: " + ex.Message); }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new LinkException("Server terlalu lama menjawab."); }
                using (res)
                {
                    int code = (int)res.StatusCode;
                    if (code is >= 300 and <= 399)
                    {
                        var loc = res.Headers.Location ?? throw new LinkException("Pengalihan tanpa alamat tujuan.");
                        var next = (loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc)).ToString();
                        if (!next.StartsWith("https://")) throw new LinkException("Link mengarah ke alamat tidak aman (bukan https).");
                        url = DirectUrl(next);
                        continue;
                    }
                    if (code is 401 or 403) throw new LinkException(NEED_LOGIN);
                    if (code is < 200 or > 299) throw new LinkException($"Server menjawab {code}. Coba buka link di browser.");
                    if (res.Content.Headers.ContentLength > MAX_BYTES) throw new LinkException($"Berkas lebih dari {MAX_BYTES >> 20} MB.");
                    using var s = res.Content.ReadAsStream(ct);
                    var ms = new MemoryStream();
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > MAX_BYTES) throw new LinkException($"Berkas lebih dari {MAX_BYTES >> 20} MB.");
                        ct.ThrowIfCancellationRequested();
                    }
                    var bytes = ms.ToArray();
                    var disp = res.Content.Headers.ContentDisposition?.ToString();
                    var name = FileName(disp, url, title);
                    var kind = Sniff(bytes, name) ?? throw new LinkException(NEED_LOGIN);
                    var baseName = Path.GetFileNameWithoutExtension(name);
                    if (string.IsNullOrWhiteSpace(baseName)) baseName = "dokumen";
                    return new Result(bytes, kind, baseName + "." + Ext(kind));
                }
            }
            throw new LinkException("Terlalu banyak pengalihan.");
        }
    }
}
