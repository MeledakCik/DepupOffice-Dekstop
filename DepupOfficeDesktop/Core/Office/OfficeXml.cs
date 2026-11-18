using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DepupOfficeDesktop.Core.Office
{
    /// <summary>
    /// DOM XML mini (port OfficeXml.kt Android). Nama elemen/atribut disimpan TANPA awalan namespace ("w:p" → "p"),
    /// cukup untuk DOCX/XLSX/PPTX dan jauh lebih ringan dari XmlDocument untuk dokumen besar.
    /// </summary>
    public sealed class XNode
    {
        public readonly string Name;
        public readonly Dictionary<string, string> Attrs = new(4);
        public readonly List<XNode> Kids = new(2);
        /// <summary>Teks langsung (gabungan potongan teks di antara anak).</summary>
        public string Text = "";

        public XNode(string name) { Name = name; }

        public string? Attr(string n) => Attrs.TryGetValue(n, out var v) ? v : null;
        public XNode? Child(string n) { foreach (var k in Kids) if (k.Name == n) return k; return null; }
        public List<XNode> Children(string n) { var l = new List<XNode>(); foreach (var k in Kids) if (k.Name == n) l.Add(k); return l; }
        public XNode? Path(params string[] ns) { XNode? c = this; foreach (var n in ns) { c = c?.Child(n); if (c == null) return null; } return c; }
        public List<XNode> All(string n, List<XNode>? outL = null)
        {
            outL ??= new List<XNode>();
            foreach (var k in Kids) { if (k.Name == n) outL.Add(k); k.All(n, outL); }
            return outL;
        }
        public XNode? First(string n) { foreach (var k in Kids) { if (k.Name == n) return k; var f = k.First(n); if (f != null) return f; } return null; }
        public string AllText() { var sb = new StringBuilder(); void Go(XNode x) { sb.Append(x.Text); foreach (var k in x.Kids) Go(k); } Go(this); return sb.ToString(); }
        /// <summary>Nilai atribut "val" (w:val).</summary>
        public string? V => Attr("val");
    }

    public static class XmlDom
    {
        private static string Local(string n) { int i = n.IndexOf(':'); return i >= 0 ? n[(i + 1)..] : n; }

        public static XNode Parse(byte[] bytes) => Parse(Decode(bytes));

        private static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            var s = Encoding.UTF8.GetString(b);
            return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
        }

        public static XNode Parse(string s)
        {
            var root = new XNode("#root");
            var stack = new List<XNode> { root };
            var texts = new List<StringBuilder?> { null };
            int i = 0, n = s.Length;
            void AppendText(string t)
            {
                if (t.Length == 0) return;
                int top = stack.Count - 1;
                (texts[top] ??= new StringBuilder()).Append(t);
            }
            void Close()
            {
                int top = stack.Count - 1;
                if (texts[top] is { } sb) stack[top].Text = sb.ToString();
                stack.RemoveAt(top); texts.RemoveAt(top);
            }
            while (i < n)
            {
                char c = s[i];
                if (c == '<')
                {
                    if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0) { int e = s.IndexOf("-->", i + 4, StringComparison.Ordinal); i = e < 0 ? n : e + 3; continue; }
                    if (string.CompareOrdinal(s, i, "<![CDATA[", 0, 9) == 0) { int e = s.IndexOf("]]>", i + 9, StringComparison.Ordinal); AppendText(s.Substring(i + 9, (e < 0 ? n : e) - (i + 9))); i = e < 0 ? n : e + 3; continue; }
                    if (i + 1 < n && (s[i + 1] == '?' || s[i + 1] == '!')) { int e = s.IndexOf('>', i); i = e < 0 ? n : e + 1; continue; }
                    if (i + 1 < n && s[i + 1] == '/')
                    {
                        int e = s.IndexOf('>', i); i = e < 0 ? n : e + 1;
                        if (stack.Count > 1) Close();
                        continue;
                    }
                    int j = i + 1;
                    while (j < n && !char.IsWhiteSpace(s[j]) && s[j] != '>' && s[j] != '/') j++;
                    var node = new XNode(Local(s.Substring(i + 1, j - i - 1)));
                    bool selfClose = false;
                    while (j < n)
                    {
                        while (j < n && char.IsWhiteSpace(s[j])) j++;
                        if (j >= n) break;
                        if (s[j] == '>') { j++; break; }
                        if (s[j] == '/') { selfClose = true; j++; continue; }
                        int k = j;
                        while (k < n && s[k] != '=' && !char.IsWhiteSpace(s[k]) && s[k] != '>' && s[k] != '/') k++;
                        string an = s.Substring(j, k - j);
                        while (k < n && char.IsWhiteSpace(s[k])) k++;
                        if (k < n && s[k] == '=')
                        {
                            k++;
                            while (k < n && char.IsWhiteSpace(s[k])) k++;
                            char q = k < n ? s[k] : '"';
                            if (q == '"' || q == '\'')
                            {
                                int e = s.IndexOf(q, k + 1); if (e < 0) e = n;
                                string av = Unescape(s.Substring(k + 1, e - k - 1));
                                if (!an.StartsWith("xmlns", StringComparison.Ordinal))
                                {
                                    node.Attrs[Local(an)] = av;
                                    if (an.Contains(':')) node.Attrs[an] = av;
                                }
                                j = e + 1;
                            }
                            else j = k;
                        }
                        else j = k;
                    }
                    stack[^1].Kids.Add(node);
                    if (!selfClose) { stack.Add(node); texts.Add(null); }
                    i = j;
                }
                else
                {
                    int e = s.IndexOf('<', i); if (e < 0) e = n;
                    if (stack.Count > 1) AppendText(Unescape(s.Substring(i, e - i)));
                    i = e;
                }
            }
            while (stack.Count > 1) Close();
            return ResolveAlternate(root);
        }

        /// <summary>mc:AlternateContent → pilihan pertama yang namespace-nya dipahami; selain itu Fallback (seperti Word).</summary>
        private static XNode ResolveAlternate(XNode n)
        {
            int idx = 0;
            while (idx < n.Kids.Count)
            {
                var k = n.Kids[idx];
                if (k.Name == "AlternateContent")
                {
                    var choices = k.Children("Choice"); var fb = k.Child("Fallback");
                    var ok = choices.FirstOrDefault(c => c.Attr("Requires")?.Split(' ').All(p => !UnsupportedNs(p)) != false);
                    var pick = ok ?? fb ?? choices.FirstOrDefault();
                    n.Kids.RemoveAt(idx);
                    if (pick != null) n.Kids.InsertRange(idx, pick.Kids);
                    continue;
                }
                ResolveAlternate(k); idx++;
            }
            return n;
        }

        private static bool UnsupportedNs(string p) =>
            p == "v" || p == "ma" || p.StartsWith("cx", StringComparison.Ordinal) || p == "am3d" || p == "aink" || p == "wpi" || p == "w16se";

        public static string Unescape(string t)
        {
            if (t.IndexOf('&') < 0) return t;
            var sb = new StringBuilder(t.Length);
            int i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if (c == '&')
                {
                    int e = t.IndexOf(';', i);
                    if (e > i && e - i < 12)
                    {
                        string ent = t.Substring(i + 1, e - i - 1);
                        string? r = ent switch
                        {
                            "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'",
                            _ when ent.StartsWith("#x", StringComparison.OrdinalIgnoreCase) => int.TryParse(ent[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? Cp(h) : null,
                            _ when ent.StartsWith('#') => int.TryParse(ent[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? Cp(d) : null,
                            _ => null
                        };
                        if (r != null) { sb.Append(r); i = e + 1; continue; }
                    }
                }
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        private static string? Cp(int v) { try { return char.ConvertFromUtf32(v); } catch { return null; } }
    }

    /// <summary>Paket OOXML (zip) + hubungan antar-bagian. Bagian didekompresi saat diminta; aman antar-thread.</summary>
    public sealed class OPackage : IDisposable
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new();
        private readonly Dictionary<string, string> _lower = new();
        private readonly Dictionary<string, XNode?> _dom = new();
        private readonly object _gate = new();

        public OPackage(string path) : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }

        public OPackage(Stream s)
        {
            _zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var e in _zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                string name = e.FullName.Replace('\\', '/').TrimStart('/');
                _entries[name] = e;
                _lower.TryAdd(name.ToLowerInvariant(), name);
            }
        }

        public IEnumerable<string> PartNames => _entries.Keys;

        /// <summary>Nama sebenarnya di zip: persis, ter-dekode URI ("image%201.png"), lalu tanpa beda huruf besar/kecil.</summary>
        public string? RealName(string p)
        {
            if (_entries.ContainsKey(p)) return p;
            string dec = p;
            if (p.IndexOf('%') >= 0) { try { dec = Uri.UnescapeDataString(p); } catch { } }
            if (dec != p && _entries.ContainsKey(dec)) return dec;
            return _lower.TryGetValue(p.ToLowerInvariant(), out var a) ? a : _lower.TryGetValue(dec.ToLowerInvariant(), out var b) ? b : null;
        }

        public byte[]? Read(string p)
        {
            var n = RealName(p); if (n == null) return null;
            lock (_gate)
            {
                try
                {
                    using var st = _entries[n].Open();
                    using var ms = new MemoryStream();
                    st.CopyTo(ms);
                    return ms.ToArray();
                }
                catch { return null; }
            }
        }

        public byte[]? Head(string p, int count)
        {
            var n = RealName(p); if (n == null) return null;
            lock (_gate)
            {
                try
                {
                    using var st = _entries[n].Open();
                    var buf = new byte[count]; int k = 0;
                    while (k < count) { int r = st.Read(buf, k, count - k); if (r <= 0) break; k += r; }
                    Array.Resize(ref buf, k); return buf;
                }
                catch { return null; }
            }
        }

        public bool Has(string p) => RealName(p) != null;

        public XNode? Xml(string p)
        {
            lock (_dom) if (_dom.TryGetValue(p, out var x)) return x;
            XNode? node = null;
            var b = Read(p);
            if (b != null) { try { node = XmlDom.Parse(b); } catch { node = null; } }
            lock (_dom) _dom[p] = node;
            return node;
        }

        /// <summary>Buang DOM hasil penguraian (HTML sudah jadi); paket tetap bisa dibaca untuk gambar.</summary>
        public void DropXml() { lock (_dom) _dom.Clear(); }

        public sealed record Rel(string Id, string Type, string Target, bool External);

        public Dictionary<string, Rel> Rels(string part)
        {
            int slash = part.LastIndexOf('/');
            string dir = slash >= 0 ? part[..slash] : "";
            string file = slash >= 0 ? part[(slash + 1)..] : part;
            string relPath = (dir.Length == 0 ? "" : dir + "/") + "_rels/" + file + ".rels";
            var outD = new Dictionary<string, Rel>();
            var x = Xml(relPath); if (x == null) return outD;
            foreach (var r in x.All("Relationship"))
            {
                var id = r.Attr("Id"); var t = r.Attr("Target");
                if (id == null || t == null) continue;
                bool ext = r.Attr("TargetMode") == "External";
                outD[id] = new Rel(id, r.Attr("Type") ?? "", ext ? t : Resolve(dir, t), ext);
            }
            return outD;
        }

        public static string Resolve(string dir, string target0)
        {
            string target = target0.Replace('\\', '/');
            int hash = target.IndexOf('#'); if (hash >= 0) target = target[..hash];
            if (target.StartsWith('/')) return target.TrimStart('/');
            var parts = new List<string>();
            if (dir.Length > 0) parts.AddRange(dir.Split('/'));
            foreach (var seg in target.Split('/'))
            {
                if (seg is "" or ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else parts.Add(seg);
            }
            return string.Join("/", parts);
        }

        /// <summary>Bagian utama menurut _rels/.rels (officeDocument).</summary>
        public string? MainPart() => Rels("").Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal))?.Target;

        public void Dispose() { try { _zip.Dispose(); } catch { } }
    }

    /// <summary>
    /// Gambar dokumen yang dilayani WebView2 di https://doc.local/r/&lt;nomor&gt;. Isi gambar baru dibaca dari zip
    /// saat WebView memintanya (dokumen penuh foto tetap cepat dibuka &amp; hemat RAM).
    /// </summary>
    public sealed class DocRes : IDisposable
    {
        public sealed class Item
        {
            public readonly int Id; public readonly string Part;
            public double CssW, CssH;
            public Item(int id, string part) { Id = id; Part = part; }
        }

        private OPackage? _pkg;
        private readonly List<Item> _items = new();
        private readonly Dictionary<string, Item> _byPart = new();
        private readonly object _gate = new();

        public DocRes(OPackage? pkg) { _pkg = pkg; }

        public string? Url(string part, double cssW, double cssH)
        {
            lock (_gate)
            {
                if (!_byPart.TryGetValue(part, out var it))
                {
                    var p = _pkg; if (p == null) return null;
                    var real = p.RealName(part); if (real == null) return null;
                    if (!_byPart.TryGetValue(real, out it))
                    {
                        var head = p.Head(real, 2048); if (head == null) return null;
                        if (OfficeImage.Kind(head) == null) return null;
                        it = new Item(_items.Count, real);
                        _items.Add(it); _byPart[real] = it;
                    }
                    _byPart[part] = it;
                }
                if (double.IsFinite(cssW) && cssW > it.CssW) it.CssW = cssW;
                if (double.IsFinite(cssH) && cssH > it.CssH) it.CssH = cssH;
                return "https://doc.local/r/" + it.Id;
            }
        }

        public Item? Get(int id) { lock (_gate) return id >= 0 && id < _items.Count ? _items[id] : null; }
        public byte[]? Read(Item it) => _pkg?.Read(it.Part);
        public void Finish() => _pkg?.DropXml();
        public void Dispose() { lock (_gate) { _pkg?.Dispose(); _pkg = null; } }
    }

    /// <summary>Hasil render: HTML + gambar yang dilayani WebView2.</summary>
    public sealed class RenderedDoc : IDisposable
    {
        public string Html { get; private set; }
        public readonly DocRes Res;
        public readonly string Title, Kind;
        public RenderedDoc(string html, DocRes res, string title, string kind) { Html = html; Res = res; Title = title; Kind = kind; }
        public void DropHtml() => Html = "";
        public void Dispose() => Res.Dispose();
    }

    /// <summary>Jenis gambar dari byte awal + konversi ke format yang dimengerti WebView (EMF/WMF/TIFF → PNG lewat GDI+).</summary>
    public static class OfficeImage
    {
        public static string? Kind(byte[] b)
        {
            int n = b.Length;
            if (n < 4) return null;
            int U(int i) => i < n ? b[i] : -1;
            if (U(0) == 0x89 && U(1) == 0x50 && U(2) == 0x4E && U(3) == 0x47) return "png";
            if (U(0) == 0xFF && U(1) == 0xD8 && U(2) == 0xFF) return "jpeg";
            if (U(0) == 'G' && U(1) == 'I' && U(2) == 'F' && U(3) == '8') return "gif";
            if (U(0) == 'R' && U(1) == 'I' && U(2) == 'F' && U(3) == 'F' && U(8) == 'W' && U(9) == 'E' && U(10) == 'B' && U(11) == 'P') return "webp";
            if ((U(0) == 'I' && U(1) == 'I' && U(2) == 42 && U(3) == 0) || (U(0) == 'M' && U(1) == 'M' && U(2) == 0 && U(3) == 42)) return "tiff";
            if (U(0) == 0x1F && U(1) == 0x8B) return "gzip";
            if (U(0) == 'B' && U(1) == 'M') return "bmp";
            if (U(0) == 1 && U(1) == 0 && U(2) == 0 && U(3) == 0 && n > 44 && U(40) == ' ' && U(41) == 'E' && U(42) == 'M' && U(43) == 'F') return "emf";
            if (U(0) == 0xD7 && U(1) == 0xCD && U(2) == 0xC6 && U(3) == 0x9A) return "wmf";
            if ((U(0) == 1 || U(0) == 2) && U(1) == 0 && U(2) == 9 && U(3) == 0) return "wmf";
            if (U(0) == 0 && U(1) == 0 && U(2) == 1 && U(3) == 0) return "ico";
            var head = Encoding.ASCII.GetString(b, 0, Math.Min(n, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.Ordinal) && head.Contains("<svg"))) return "svg";
            return null;
        }

        /// <summary>(mime, isi) siap dilayani ke WebView, atau null bila tak terbaca.</summary>
        public static (string Mime, byte[] Data)? Prepare(byte[] raw, double cssW, double cssH)
        {
            var b = raw;
            var k = Kind(b);
            if (k == "gzip")
            {
                try
                {
                    using var gz = new GZipStream(new MemoryStream(b), CompressionMode.Decompress);
                    using var ms = new MemoryStream(); gz.CopyTo(ms); b = ms.ToArray(); k = Kind(b);
                }
                catch { return null; }
            }
            switch (k)
            {
                case "png": return ("image/png", b);
                case "jpeg": return ("image/jpeg", b);
                case "gif": return ("image/gif", b);
                case "webp": return ("image/webp", b);
                case "bmp": return ("image/bmp", b);
                case "ico": return ("image/x-icon", b);
                case "svg": return ("image/svg+xml", b);
                case "tiff": case "emf": case "wmf": return ToPng(b, k == "tiff" ? 0 : Math.Max(cssW, cssH));
                default: return null;
            }
        }

        /// <summary>TIFF/EMF/WMF → PNG lewat GDI+ (metafile dirender vektor pada ukuran tampil × 2 agar tajam).</summary>
        private static (string, byte[])? ToPng(byte[] b, double cssMax)
        {
            try
            {
                using var ms = new MemoryStream(b);
                using var img = Image.FromStream(ms);
                int w = img.Width, h = img.Height;
                if (img is Metafile)
                {
                    double target = Math.Clamp(cssMax > 0 ? cssMax * 2 : 1600, 64, 2400);
                    double s = target / Math.Max(1, Math.Max(w, h));
                    w = Math.Max(1, (int)(w * s)); h = Math.Max(1, (int)(h * s));
                }
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, w, h);
                }
                using var outMs = new MemoryStream();
                bmp.Save(outMs, ImageFormat.Png);
                return ("image/png", outMs.ToArray());
            }
            catch { return null; }
        }
    }

    /// <summary>Utilitas warna &amp; teks untuk ketiga renderer (port OxUtil).</summary>
    public static class OxUtil
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Angka untuk CSS (titik desimal, tanpa notasi ilmiah).</summary>
        public static string N(double d)
        {
            if (!double.IsFinite(d)) return "0";
            double r = Math.Round(d, 3);
            if (r == 0) return "0";
            return r.ToString("0.###", Inv);
        }

        public static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
                switch (c) { case '<': sb.Append("&lt;"); break; case '>': sb.Append("&gt;"); break; case '&': sb.Append("&amp;"); break; case '"': sb.Append("&quot;"); break; default: sb.Append(c); break; }
            return sb.ToString();
        }

        public static string Hex(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("x6", Inv);

        public static int? ParseHex(string? s)
        {
            if (s == null) return null;
            var t = s.Trim().TrimStart('#');
            if (t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, Inv, out var v)) return v;
            if (t.Length == 8 && int.TryParse(t[2..], NumberStyles.HexNumber, Inv, out var v2)) return v2;
            return null;
        }

        public static int Mix(int a, int b, float t)
        {
            int Ch(int x, int s) => (x >> s) & 255;
            int M(int s) => Math.Clamp((int)(Ch(a, s) + (Ch(b, s) - Ch(a, s)) * t), 0, 255);
            return (M(16) << 16) | (M(8) << 8) | M(0);
        }

        /// <summary>HSL luminance modulation (DrawingML lumMod/lumOff, Excel tint).</summary>
        public static int Lum(int rgb, float mod, float off)
        {
            float r = ((rgb >> 16) & 255) / 255f, g = ((rgb >> 8) & 255) / 255f, b = (rgb & 255) / 255f;
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            float h = 0, s = 0, l = (mx + mn) / 2;
            if (mx != mn)
            {
                float d = mx - mn;
                s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
                h = (mx == r ? (g - b) / d + (g < b ? 6 : 0) : mx == g ? (b - r) / d + 2 : (r - g) / d + 4) / 6f;
            }
            l = Math.Clamp(l * mod + off, 0f, 1f);
            static float Hue(float p, float q, float t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                return t < 1f / 6 ? p + (q - p) * 6 * t : t < 0.5f ? q : t < 2f / 3 ? p + (q - p) * (2f / 3 - t) * 6 : p;
            }
            float rr, gg, bb;
            if (s == 0) { rr = gg = bb = l; }
            else
            {
                float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                rr = Hue(p, q, h + 1f / 3); gg = Hue(p, q, h); bb = Hue(p, q, h - 1f / 3);
            }
            return ((int)(rr * 255 + 0.5f) << 16) | ((int)(gg * 255 + 0.5f) << 8) | (int)(bb * 255 + 0.5f);
        }

        /// <summary>Excel tint: -1..1 (negatif = lebih gelap).</summary>
        public static int Tint(int rgb, float t) => t < 0 ? Lum(rgb, 1 + t, 0f) : Lum(rgb, 1 - t, t);

        /// <summary>Nama font Office → keluarga CSS (nama disaring agar tidak bisa "keluar" dari atribut style).</summary>
        public static string FontStack(string? name, bool serifHint = false)
        {
            var n = new string((name ?? "").Where(ch => char.IsLetterOrDigit(ch) || " -_.+()@　".IndexOf(ch) >= 0).ToArray()).Trim();
            var low = n.ToLowerInvariant();
            string fallback;
            if (low.Length == 0) fallback = serifHint ? "'Times New Roman',serif" : "Calibri,sans-serif";
            else if (new[] { "times", "cambria", "georgia", "garamond", "book antiqua", "bookman", "palatino", "serif", "minion", "century", "constantia", "baskerville" }.Any(low.Contains) && !low.Contains("sans")) fallback = "'Times New Roman',serif";
            else if (new[] { "courier", "consolas", "mono", "lucida console", "menlo" }.Any(low.Contains)) fallback = "'Courier New',monospace";
            else if (new[] { "arial", "helvetica", "verdana", "tahoma", "segoe", "trebuchet", "roboto", "open sans", "liberation sans", "noto sans", "gill", "aptos" }.Any(low.Contains)) fallback = "Arial,sans-serif";
            else fallback = "Calibri,sans-serif";
            return n.Length == 0 ? fallback : "'" + n.Replace("'", "") + "'," + fallback;
        }

        /// <summary>Karakter Symbol/Wingdings (area pribadi Unicode) → karakter Unicode biasa.</summary>
        public static string SymbolChar(char c, string? font)
        {
            int code = c;
            string f = (font ?? "").ToLowerInvariant();
            int k = code >= 0xF000 && code <= 0xF0FF ? code - 0xF000 : code;
            if (f.Contains("wingdings"))
                return k switch
                {
                    0xA7 => "▪", 0xA8 => "◻", 0x6C => "●", 0x6E => "■", 0x71 => "❑", 0x76 => "❖", 0xD8 => "➢", 0xFC => "✓", 0xFB => "✗",
                    0x9F => "•", 0x77 => "◆", 0xA1 => "○", 0xE0 => "→", 0xE8 => "➔", 0xF0 => "⇨", 0x4A => "☺", 0x43 => "👍", _ => "•"
                };
            if (f.Contains("symbol"))
                return k switch { 0xB7 => "•", 0x2D => "−", 0xAE => "→", 0xD8 => "¬", 0xA7 => "♣", 0xB0 => "°", 0xB4 => "×", _ => k >= 0x20 && k <= 0x7E ? ((char)k).ToString() : "•" };
            if (code >= 0xF000 && code <= 0xF0FF) return "•";
            return c.ToString();
        }

        public static int? ToInt(string? s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : null;
        public static long? ToLong(string? s) => long.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : null;
        public static double? ToDouble(string? s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;
        public static int? HexInt(string? s) => int.TryParse(s, NumberStyles.HexNumber, Inv, out var v) ? v : null;
    }
}
