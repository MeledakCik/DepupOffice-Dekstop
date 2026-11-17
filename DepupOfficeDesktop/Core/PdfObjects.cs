using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    // =====================================================================================================
    // Pembaca struktur PDF (port dari PdfParse.kt Android). Mendukung xref klasik & xref stream, object stream,
    // filter Flate (+prediktor PNG), ASCIIHex, ASCII85, LZW, RunLength, berkas rusak (xref dibangun ulang), dan
    // dekripsi Standard Security Handler (RC4 40/128, AES-128, AES-256 R5/R6).
    // Tanpa NuGet: hanya System.IO.Compression & System.Security.Cryptography.
    // =====================================================================================================

    public sealed class PName
    {
        public readonly string N;
        public PName(string n) { N = n; }
        public override bool Equals(object? o) => o is PName p && p.N == N;
        public override int GetHashCode() => N.GetHashCode();
        public override string ToString() => "/" + N;
    }

    public sealed class PStr
    {
        public byte[] B;
        public readonly bool Hex;
        public PStr(byte[] b, bool hex = false) { B = b; Hex = hex; }

        /// <summary>Teks: UTF-16BE (BOM FE FF), UTF-8 (BOM EF BB BF), selain itu PDFDocEncoding (≈ Latin-1).</summary>
        public string Text()
        {
            if (B.Length >= 2 && B[0] == 0xFE && B[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(B, 2, B.Length - 2);
            if (B.Length >= 3 && B[0] == 0xEF && B[1] == 0xBB && B[2] == 0xBF) return Encoding.UTF8.GetString(B, 3, B.Length - 3);
            return Pdf.Latin1.GetString(B);
        }

        /// <summary>String teks PDF: Latin-1 bila cukup, selain itu UTF-16BE ber-BOM.</summary>
        public static PStr Of(string s)
        {
            if (s.All(c => c < 128)) return new PStr(Pdf.Latin1.GetBytes(s));
            var u = Encoding.BigEndianUnicode.GetBytes(s);
            var b = new byte[u.Length + 2]; b[0] = 0xFE; b[1] = 0xFF; Buffer.BlockCopy(u, 0, b, 2, u.Length);
            return new PStr(b);
        }
        public override string ToString() => "(" + Text() + ")";
    }

    public sealed class PRef
    {
        public readonly int Num, Gen;
        public PRef(int num, int gen) { Num = num; Gen = gen; }
        public override bool Equals(object? o) => o is PRef r && r.Num == Num && r.Gen == Gen;
        public override int GetHashCode() => Num * 31 + Gen;
        public override string ToString() => $"{Num} {Gen} R";
    }

    public sealed class PDict
    {
        public readonly Dictionary<string, object?> M;
        private readonly List<string> _order;
        public PDict() { M = new Dictionary<string, object?>(); _order = new List<string>(); }
        public PDict(PDict src) { M = new Dictionary<string, object?>(src.M); _order = new List<string>(src._order); }

        public object? this[string k]
        {
            get => M.TryGetValue(k, out var v) ? v : null;
            set
            {
                if (value == null) { if (M.Remove(k)) _order.Remove(k); return; }
                if (!M.ContainsKey(k)) _order.Add(k);
                M[k] = value;
            }
        }
        public bool Has(string k) => M.ContainsKey(k);
        public IEnumerable<KeyValuePair<string, object?>> Entries() { foreach (var k in _order) if (M.TryGetValue(k, out var v)) yield return new(k, v); }
        public string? Name(string k) => (this[k] as PName)?.N;
        public override string ToString() => "<<" + string.Join(" ", Entries().Select(e => "/" + e.Key + " " + e.Value)) + ">>";
    }

    public sealed class PStream
    {
        public readonly PDict Dict;
        public byte[] Raw;
        public PStream(PDict dict, byte[] raw) { Dict = dict; Raw = raw; }
        public override string ToString() => $"stream{Dict}[{Raw.Length}]";
    }

    /// <summary>Penanda objek null PDF.</summary>
    public sealed class PNull { public static readonly PNull I = new(); private PNull() { } public override string ToString() => "null"; }

    public class PdfException : Exception { public PdfException(string m) : base(m) { } }
    public class PdfPasswordException : Exception { public PdfPasswordException(string m) : base(m) { } }

    public static class Pdf
    {
        public static readonly Encoding Latin1 = Encoding.Latin1;
        public static double Num(object? o, double def = 0) => o switch { long l => l, int i => i, double d => d, float f => f, _ => def };
        public static int Int(object? o, int def = 0) => o switch { long l => (int)l, int i => i, double d => (int)d, float f => (int)f, _ => def };
        public static string Fmt(double d)
        {
            if (d == Math.Round(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
            var s = d.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0');
            if (s.EndsWith(".")) s = s[..^1];
            return s == "-0" ? "0" : s;
        }
        public static byte[] Concat(params byte[][] parts)
        {
            var r = new byte[parts.Sum(p => p.Length)]; int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
        public static byte[] Deflate(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(data, 0, data.Length);
            return ms.ToArray();
        }
    }

    // ------------------------------------------------------------------ lexer + parser objek

    public sealed class PdfLexer
    {
        public readonly byte[] D;
        public int P;
        public readonly int End;
        public PdfLexer(byte[] d, int p = 0, int end = -1) { D = d; P = p; End = end < 0 ? d.Length : end; }

        public sealed class Op { public readonly string K; public Op(string k) { K = k; } public override string ToString() => K; }

        public static bool Ws(int c) => c == 0x20 || c == 0x0A || c == 0x0D || c == 0x09 || c == 0x0C || c == 0x00;
        public static bool Delim(int c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';

        public int At(int i) => i < End && i >= 0 ? D[i] : -1;

        public void SkipWs()
        {
            while (P < End)
            {
                int c = At(P);
                if (Ws(c)) P++;
                else if (c == '%') { while (P < End && At(P) != 0x0A && At(P) != 0x0D) P++; }
                else break;
            }
        }

        public string Token()
        {
            SkipWs();
            int s = P;
            while (P < End && !Ws(At(P)) && !Delim(At(P))) P++;
            return Pdf.Latin1.GetString(D, s, P - s);
        }

        public object? Obj(bool refs = true)
        {
            SkipWs();
            if (P >= End) return null;
            int c = At(P);
            if (c == '/') { P++; return new PName(NameBody()); }
            if (c == '(') { P++; return new PStr(Literal()); }
            if (c == '<' && At(P + 1) == '<') { P += 2; return Dict(); }
            if (c == '<') { P++; return new PStr(HexStr(), true); }
            if (c == '[')
            {
                P++;
                var a = new List<object?>();
                while (true)
                {
                    SkipWs();
                    if (P >= End) break;
                    if (At(P) == ']') { P++; break; }
                    var o = Obj(refs);
                    if (o is Op) continue;
                    a.Add(o);
                }
                return a;
            }
            if (c == ']' || c == '>' || c == ')' || c == '}' || c == '{') { P++; return new Op(((char)c).ToString()); }
            var t = Token();
            if (t.Length == 0) { P++; return new Op(""); }
            var n = NumOf(t);
            if (n != null)
            {
                if (refs && n is long ln && ln >= 0)
                {
                    int save = P;
                    var t2 = Token();
                    if (long.TryParse(t2, NumberStyles.None, CultureInfo.InvariantCulture, out var g) && g >= 0)
                    {
                        if (Token() == "R") return new PRef((int)ln, (int)g);
                    }
                    P = save;
                }
                return n;
            }
            return t switch { "true" => true, "false" => false, "null" => PNull.I, _ => new Op(t) };
        }

        private static readonly Regex MultiSign = new("^[-+]{2,}");
        private static object? NumOf(string t)
        {
            if (t.Length == 0) return null;
            char c = t[0];
            if (!(char.IsDigit(c) || c == '-' || c == '+' || c == '.')) return null;
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
            var clean = MultiSign.Replace(t, "-");
            int i1 = clean.IndexOf('.');
            if (i1 >= 0) { int i2 = clean.IndexOf('.', i1 + 1); if (i2 > 0) clean = clean[..i2]; }
            clean = clean.TrimEnd('-');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return t.All(ch => ch == '-' || ch == '+' || ch == '.') ? 0L : null;
        }

        private static int Hx(int c) => c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 55 : c >= 'a' && c <= 'f' ? c - 87 : -1;

        private string NameBody()
        {
            var sb = new MemoryStream();
            while (P < End && !Ws(At(P)) && !Delim(At(P)))
            {
                int c = At(P);
                if (c == '#' && P + 2 < End && Hx(At(P + 1)) >= 0 && Hx(At(P + 2)) >= 0) { sb.WriteByte((byte)(Hx(At(P + 1)) * 16 + Hx(At(P + 2)))); P += 3; }
                else { sb.WriteByte((byte)c); P++; }
            }
            return Encoding.UTF8.GetString(sb.ToArray());
        }

        private byte[] Literal()
        {
            var o = new MemoryStream();
            int depth = 1;
            while (P < End)
            {
                int c = At(P++);
                if (c == '\\')
                {
                    if (P >= End) break;
                    int e = At(P++);
                    switch (e)
                    {
                        case 'n': o.WriteByte(10); break;
                        case 'r': o.WriteByte(13); break;
                        case 't': o.WriteByte(9); break;
                        case 'b': o.WriteByte(8); break;
                        case 'f': o.WriteByte(12); break;
                        case 0x0D: if (At(P) == 0x0A) P++; break;
                        case 0x0A: break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2; k++) { int q = At(P); if (q >= '0' && q <= '7') { v = v * 8 + (q - '0'); P++; } else break; }
                                o.WriteByte((byte)(v & 0xFF));
                            }
                            else o.WriteByte((byte)e);
                            break;
                    }
                }
                else if (c == '(') { depth++; o.WriteByte((byte)c); }
                else if (c == ')') { depth--; if (depth == 0) return o.ToArray(); o.WriteByte((byte)c); }
                else o.WriteByte((byte)c);
            }
            return o.ToArray();
        }

        private byte[] HexStr()
        {
            var o = new MemoryStream(); int hi = -1;
            while (P < End)
            {
                int c = At(P++);
                if (c == '>') break;
                int v = Hx(c);
                if (v < 0) continue;
                if (hi < 0) hi = v; else { o.WriteByte((byte)(hi * 16 + v)); hi = -1; }
            }
            if (hi >= 0) o.WriteByte((byte)(hi * 16));
            return o.ToArray();
        }

        private PDict Dict()
        {
            var dd = new PDict();
            while (true)
            {
                SkipWs();
                if (P >= End) break;
                if (At(P) == '>' && At(P + 1) == '>') { P += 2; break; }
                var k = Obj(false);
                if (k is not PName kn) { if (k == null) break; continue; }
                SkipWs();
                if (At(P) == '>' && At(P + 1) == '>') { dd[kn.N] = PNull.I; continue; }
                var v = Obj(true);
                if (v is Op) continue;
                dd[kn.N] = v;
            }
            return dd;
        }
    }

    // ------------------------------------------------------------------ dokumen

    public sealed class PdfFile
    {
        public readonly byte[] Data;
        private readonly Dictionary<int, int> _xOff = new();
        private readonly Dictionary<int, int> _xGen = new();
        private readonly Dictionary<int, int[]> _xStm = new();
        private readonly Dictionary<int, object?> _cache = new();
        private readonly Dictionary<int, (int[] offs, byte[] body)> _stmCache = new();
        private PdfDecrypt? _crypt;

        public PDict Trailer { get; private set; } = new();
        public bool Encrypted { get; private set; }
        public PRef? EncryptRef { get; private set; }
        public int Permissions { get; private set; } = -1;
        public bool OwnerAuth { get; private set; } = true;
        public bool CanExtract => OwnerAuth || !Encrypted || ((Permissions & (1 << 4)) != 0 && (Permissions & (1 << 3)) != 0);
        public bool CanPrint => OwnerAuth || !Encrypted || (Permissions & (1 << 2)) != 0;
        public bool CanModify => OwnerAuth || !Encrypted || (Permissions & (1 << 3)) != 0;
        public readonly string Version;

        private PdfFile(byte[] data)
        {
            Data = data;
            var head = Pdf.Latin1.GetString(data, 0, Math.Min(1024, data.Length));
            var m = Regex.Match(head, @"%PDF-(\d\.\d)");
            Version = m.Success ? m.Groups[1].Value : "1.4";
        }

        public static PdfFile Open(byte[] data, string password = "")
        {
            var f = new PdfFile(data);
            f.LoadXref();
            f.SetupCrypt(password);
            return f;
        }
        public static PdfFile Open(string path, string password = "") => Open(File.ReadAllBytes(path), password);

        public static PDict? DictOf(object? o) => o as PDict ?? (o as PStream)?.Dict;

        // ---------------- xref
        private void LoadXref()
        {
            int tl = Math.Min(2048, Data.Length);
            var tail = Pdf.Latin1.GetString(Data, Data.Length - tl, tl);
            int sx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
            bool ok = false;
            if (sx >= 0)
            {
                var m = Regex.Match(tail[sx..], @"startxref\s+(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var off) && off >= 0 && off < Data.Length)
                {
                    try { ReadXrefChain(off); ok = _xOff.Count > 0 || _xStm.Count > 0; } catch { ok = false; }
                }
            }
            if (!ok || !Sane()) RebuildXref();
            if (Trailer["Root"] == null) FindRoot();
        }

        private bool Sane()
        {
            if (Trailer["Root"] is not PRef r) return false;
            if (!_xOff.TryGetValue(r.Num, out var off)) return _xStm.ContainsKey(r.Num);
            if (off < 0 || off >= Data.Length) return false;
            var s = Pdf.Latin1.GetString(Data, off, Math.Min(40, Data.Length - off));
            return Regex.IsMatch(s, $@"^\s*{r.Num}\s+\d+\s+obj");
        }

        private void ReadXrefChain(int first)
        {
            var seen = new HashSet<int>();
            int? off = first;
            PDict? firstTrailer = null;
            while (off is int o && o >= 0 && o < Data.Length && seen.Add(o))
            {
                var lx = new PdfLexer(Data, o);
                lx.SkipWs();
                PDict tr;
                if (lx.P + 4 <= Data.Length && Pdf.Latin1.GetString(Data, lx.P, 4) == "xref")
                {
                    lx.P += 4;
                    tr = ReadXrefTable(lx);
                    if (tr["XRefStm"] is long xs) { try { ReadXrefStream((int)xs); } catch { } }
                }
                else
                {
                    var t = ReadXrefStream(o);
                    if (t == null) break;
                    tr = t;
                }
                if (firstTrailer == null) firstTrailer = tr;
                else foreach (var kv in tr.Entries()) if (!firstTrailer.Has(kv.Key)) firstTrailer[kv.Key] = kv.Value;
                off = tr["Prev"] is long pv ? (int)pv : null;
            }
            Trailer = firstTrailer ?? new PDict();
        }

        private PDict ReadXrefTable(PdfLexer lx)
        {
            while (true)
            {
                lx.SkipWs();
                int save = lx.P;
                var t = lx.Token();
                if (t == "trailer") return lx.Obj() as PDict ?? new PDict();
                if (!int.TryParse(t, out var start)) { lx.P = save; return new PDict(); }
                if (!int.TryParse(lx.Token(), out var count)) return new PDict();
                for (int i = 0; i < count; i++)
                {
                    long.TryParse(lx.Token(), out var ofs);
                    int.TryParse(lx.Token(), out var g);
                    var ty = lx.Token();
                    int n = start + i;
                    if (_xOff.ContainsKey(n) || _xStm.ContainsKey(n)) continue;
                    if (ty == "n" && ofs > 0) { _xOff[n] = (int)ofs; _xGen[n] = g; }
                    else if (ty == "f") _xOff[n] = -1;
                }
            }
        }

        private PDict? ReadXrefStream(int off)
        {
            var r = ReadIndirectAt(off);
            if (r?.o is not PStream s) return null;
            if (s.Dict.Name("Type") != "XRef") return null;
            if (s.Dict["W"] is not List<object?> wl) return null;
            var w = wl.Select(x => Pdf.Int(x)).ToArray();
            int size = Pdf.Int(s.Dict["Size"]);
            var idx = (s.Dict["Index"] as List<object?>)?.Select(x => Pdf.Int(x)).ToArray() ?? new[] { 0, size };
            var b = Decode(s, null);
            int rec = w.Sum();
            int p = 0;
            for (int k = 0; k + 1 < idx.Length; k += 2)
            {
                int start = idx[k], cnt = idx[k + 1];
                for (int i = 0; i < cnt; i++)
                {
                    if (p + rec > b.Length) break;
                    long F(int n) { long v = 0; for (int j = 0; j < w[n]; j++) v = (v << 8) | b[p++]; return v; }
                    long t = w[0] == 0 ? 1 : F(0);
                    long a = F(1), c = F(2);
                    int num = start + i;
                    if (_xOff.ContainsKey(num) || _xStm.ContainsKey(num)) continue;
                    if (t == 0) _xOff[num] = -1;
                    else if (t == 1) { _xOff[num] = (int)a; _xGen[num] = (int)c; }
                    else if (t == 2) _xStm[num] = new[] { (int)a, (int)c };
                }
            }
            return s.Dict;
        }

        private void RebuildXref()
        {
            _xOff.Clear(); _xStm.Clear(); _xGen.Clear(); _cache.Clear();
            var s = Pdf.Latin1.GetString(Data);
            foreach (Match m in Regex.Matches(s, @"(?<![0-9])(\d{1,10})\s+(\d{1,5})\s+obj\b"))
            {
                if (!int.TryParse(m.Groups[1].Value, out var n)) continue;
                _xOff[n] = m.Index; _xGen[n] = int.Parse(m.Groups[2].Value);
            }
            foreach (var n in _xOff.Keys.ToList())
            {
                object? o; try { o = Obj(n); } catch { o = null; }
                if (o is not PStream st || st.Dict.Name("Type") != "ObjStm") continue;
                int cnt = Pdf.Int(st.Dict["N"]);
                byte[] body; try { body = Decode(st, null); } catch { continue; }
                var lx = new PdfLexer(body);
                for (int i = 0; i < cnt; i++)
                {
                    if (!int.TryParse(lx.Token(), out var on)) break;
                    lx.Token();
                    if (!_xOff.ContainsKey(on)) _xStm[on] = new[] { n, i };
                }
            }
            _cache.Clear();
            var tr = new PDict();
            int ti = s.LastIndexOf("trailer", StringComparison.Ordinal);
            while (ti >= 0)
            {
                if (new PdfLexer(Data, ti + 7).Obj() is PDict d && d["Root"] != null) { tr = d; break; }
                ti = ti > 0 ? s.LastIndexOf("trailer", ti - 1, StringComparison.Ordinal) : -1;
            }
            Trailer = tr;
        }

        private void FindRoot()
        {
            foreach (var n in _xOff.Keys.Concat(_xStm.Keys).Distinct().OrderBy(x => x))
            {
                PDict? d; try { d = DictOf(Obj(n)); } catch { d = null; }
                if (d?.Name("Type") == "Catalog") { Trailer["Root"] = new PRef(n, GenOf(n)); return; }
            }
            throw new PdfException("Struktur PDF tidak dikenali (Catalog tidak ditemukan)");
        }

        public List<int> ObjectNumbers() => _xOff.Where(kv => kv.Value > 0).Select(kv => kv.Key).Concat(_xStm.Keys).Distinct().OrderBy(x => x).ToList();
        public int GenOf(int n) => _xGen.TryGetValue(n, out var g) ? g : 0;
        public int MaxObjectNumber() { var l = ObjectNumbers(); return l.Count == 0 ? 0 : l[^1]; }

        // ---------------- objek
        private (object? o, int n)? ReadIndirectAt(int off)
        {
            var lx = new PdfLexer(Data, off);
            if (!int.TryParse(lx.Token(), out var n)) return null;
            lx.Token();
            if (lx.Token() != "obj") return null;
            var o = lx.Obj();
            lx.SkipWs();
            if (o is PDict od && Data.Length - lx.P >= 6 && Pdf.Latin1.GetString(Data, lx.P, 6) == "stream")
            {
                int p = lx.P + 6;
                if (p < Data.Length && Data[p] == 0x0D) p++;
                if (p < Data.Length && Data[p] == 0x0A) p++;
                int len = od["Length"] switch
                {
                    long l => (int)l,
                    PRef r => r.Num == n ? -1 : SafeLen(r.Num),
                    _ => -1
                };
                if (len < 0 || p + len > Data.Length || !EndsAt(p + len))
                {
                    int e = IndexOf(Pdf.Latin1.GetBytes("endstream"), p);
                    if (e < 0) len = Data.Length - p;
                    else
                    {
                        int q = e;
                        if (q > p && Data[q - 1] == 0x0A) q--;
                        if (q > p && Data[q - 1] == 0x0D) q--;
                        len = q - p;
                    }
                }
                var raw = new byte[len];
                Buffer.BlockCopy(Data, p, raw, 0, len);
                return (new PStream(od, raw), n);
            }
            return (o, n);
        }

        private int SafeLen(int num) { try { return Pdf.Int(Obj(num), -1); } catch { return -1; } }

        private bool EndsAt(int p)
        {
            int q = p;
            while (q < Data.Length && PdfLexer.Ws(Data[q])) q++;
            return Data.Length - q >= 9 && Pdf.Latin1.GetString(Data, q, 9) == "endstream";
        }

        private int IndexOf(byte[] pat, int from)
        {
            int end = Data.Length - pat.Length;
            for (int i = Math.Max(0, from); i <= end; i++)
            {
                int j = 0;
                while (j < pat.Length && Data[i + j] == pat[j]) j++;
                if (j == pat.Length) return i;
            }
            return -1;
        }

        public object? Obj(int n)
        {
            if (_cache.TryGetValue(n, out var c)) return c;
            _cache[n] = PNull.I;
            object? r = null;
            try
            {
                if (_xOff.TryGetValue(n, out var off) && off > 0)
                {
                    var ri = ReadIndirectAt(off);
                    var o = ri?.o;
                    r = _crypt != null && n != EncryptRef?.Num ? _crypt.DecryptObj(o, n, GenOf(n)) : o;
                }
                else if (_xStm.TryGetValue(n, out var st)) r = FromObjStm(st[0], st[1], n);
            }
            catch (PdfPasswordException) { throw; }
            catch { r = null; }
            _cache[n] = r;
            return r;
        }

        private object? FromObjStm(int sn, int idx, int want)
        {
            if (!_stmCache.TryGetValue(sn, out var e))
            {
                if (Obj(sn) is not PStream st) return null;
                var b = Decode(st, null);
                int cnt = Pdf.Int(st.Dict["N"]), first = Pdf.Int(st.Dict["First"]);
                var lx = new PdfLexer(b);
                var o = new int[cnt * 2];
                for (int i = 0; i < cnt; i++)
                {
                    o[2 * i] = int.TryParse(lx.Token(), out var on) ? on : -1;
                    o[2 * i + 1] = first + (int.TryParse(lx.Token(), out var oo) ? oo : 0);
                }
                e = (o, b);
                _stmCache[sn] = e;
            }
            int k = idx;
            if (k * 2 >= e.offs.Length || e.offs[2 * k] != want)
            {
                k = -1;
                for (int i = 0; i < e.offs.Length / 2; i++) if (e.offs[2 * i] == want) { k = i; break; }
                if (k < 0) return null;
            }
            return new PdfLexer(e.body, e.offs[2 * k + 1]).Obj();
        }

        public object? Res(object? o)
        {
            var x = o; int guard = 0;
            while (x is PRef r && guard++ < 32) x = Obj(r.Num);
            return x is PNull ? null : x;
        }
        public PDict? RDict(object? o) => DictOf(Res(o));

        public PDict Catalog => RDict(Trailer["Root"]) ?? throw new PdfException("Catalog PDF tidak ada");
        public PDict? Info => RDict(Trailer["Info"]);

        // ---------------- halaman
        public sealed class Page
        {
            public readonly PRef? Ref; public readonly PDict Dict; public readonly PDict Resources; public readonly float[] Box; public readonly int Rotate;
            public Page(PRef? r, PDict d, PDict res, float[] box, int rot) { Ref = r; Dict = d; Resources = res; Box = box; Rotate = rot; }
            public float Width => Box[2] - Box[0];
            public float Height => Box[3] - Box[1];
        }

        public List<Page> Pages()
        {
            var outL = new List<Page>();
            void Walk(object? nodeRef, PDict inh, int depth)
            {
                if (depth > 64) return;
                var node = RDict(nodeRef);
                if (node == null) return;
                var h = new PDict(inh);
                foreach (var k in new[] { "Resources", "MediaBox", "CropBox", "Rotate" }) if (node[k] != null) h[k] = node[k];
                if (node.Name("Type") == "Pages" || node["Kids"] != null)
                {
                    if (Res(node["Kids"]) is List<object?> kids) foreach (var kid in kids) Walk(kid, h, depth + 1);
                }
                else
                {
                    var mb = (Res(h["CropBox"]) as List<object?> ?? Res(h["MediaBox"]) as List<object?>)?.Select(x => (float)Pdf.Num(Res(x))).ToList();
                    var box = mb != null && mb.Count >= 4
                        ? new[] { Math.Min(mb[0], mb[2]), Math.Min(mb[1], mb[3]), Math.Max(mb[0], mb[2]), Math.Max(mb[1], mb[3]) }
                        : new[] { 0f, 0f, 612f, 792f };
                    outL.Add(new Page(nodeRef as PRef, node, RDict(h["Resources"]) ?? new PDict(), box, ((Pdf.Int(Res(h["Rotate"])) % 360) + 360) % 360));
                }
            }
            Walk(Catalog["Pages"], new PDict(), 0);
            return outL;
        }

        public byte[] Contents(Page p)
        {
            var o = new MemoryStream();
            var c = Res(p.Dict["Contents"]);
            var list = c is List<object?> l ? l : new List<object?> { p.Dict["Contents"] };
            foreach (var x in list)
            {
                if (Res(x) is not PStream s) continue;
                try { var b = Decode(s, null); o.Write(b, 0, b.Length); o.WriteByte(10); } catch { }
            }
            return o.ToArray();
        }

        // ---------------- filter
        public List<string> Filters(PStream s) => Res(s.Dict["Filter"]) switch
        {
            PName n => new List<string> { n.N },
            List<object?> l => l.Select(x => (Res(x) as PName)?.N).Where(x => x != null).Select(x => x!).ToList(),
            _ => new List<string>()
        };

        public byte[] Decode(PStream s, ISet<string>? stopAt)
        {
            var fl = Filters(s);
            var pr = Res(s.Dict["DecodeParms"]);
            var pl = pr switch { PDict d => new List<PDict?> { d }, List<object?> l => l.Select(RDict).ToList(), _ => new List<PDict?>() };
            var b = s.Raw;
            for (int i = 0; i < fl.Count; i++)
            {
                var name = fl[i];
                if (stopAt != null && stopAt.Contains(name)) return b;
                var parm = i < pl.Count ? pl[i] : null;
                b = name switch
                {
                    "FlateDecode" or "Fl" => Predictor(Inflate(b), parm),
                    "LZWDecode" or "LZW" => Predictor(Lzw(b, Pdf.Int(parm?["EarlyChange"], 1)), parm),
                    "ASCIIHexDecode" or "AHx" => Ahx(b),
                    "ASCII85Decode" or "A85" => A85(b),
                    "RunLengthDecode" or "RL" => Rl(b),
                    "Crypt" => b,
                    _ => throw new PdfException($"Filter {name} belum didukung")
                };
            }
            return b;
        }

        public static byte[] Inflate(byte[] b)
        {
            var o = new MemoryStream(b.Length * 3 + 64);
            try
            {
                using var z = new ZLibStream(new MemoryStream(b), CompressionMode.Decompress);
                var buf = new byte[16384]; int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
            }
            catch
            {
                if (o.Length == 0 && b.Length > 2)
                {
                    try
                    {
                        using var z = new DeflateStream(new MemoryStream(b, 2, b.Length - 2), CompressionMode.Decompress);
                        var buf = new byte[16384]; int n;
                        while ((n = z.Read(buf, 0, buf.Length)) > 0) o.Write(buf, 0, n);
                    }
                    catch { }
                }
            }
            return o.ToArray();
        }

        private static byte[] Predictor(byte[] b, PDict? parm)
        {
            int pred = Pdf.Int(parm?["Predictor"], 1);
            if (pred < 10) return b;
            int colors = Pdf.Int(parm?["Colors"], 1), bpc = Pdf.Int(parm?["BitsPerComponent"], 8), cols = Pdf.Int(parm?["Columns"], 1);
            int bpp = Math.Max(1, (colors * bpc + 7) / 8);
            int row = (colors * bpc * cols + 7) / 8;
            var o = new MemoryStream();
            var prev = new byte[row];
            int p = 0;
            while (p < b.Length)
            {
                int t = b[p++];
                var cur = new byte[row];
                int n = Math.Min(row, b.Length - p);
                Buffer.BlockCopy(b, p, cur, 0, n); p += n;
                for (int i = 0; i < row; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0, up = prev[i], c = i >= bpp ? prev[i - bpp] : 0, x = cur[i];
                    int v = t switch
                    {
                        1 => x + a,
                        2 => x + up,
                        3 => x + (a + up) / 2,
                        4 => x + Paeth(a, up, c),
                        _ => x
                    };
                    cur[i] = (byte)v;
                }
                o.Write(cur, 0, row); prev = cur;
            }
            return o.ToArray();
        }
        private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }

        private static byte[] Ahx(byte[] b)
        {
            var o = new MemoryStream(); int hi = -1;
            foreach (var x in b)
            {
                int c = x;
                if (c == '>') break;
                int v = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 55 : c >= 'a' && c <= 'f' ? c - 87 : -1;
                if (v < 0) continue;
                if (hi < 0) hi = v; else { o.WriteByte((byte)(hi * 16 + v)); hi = -1; }
            }
            if (hi >= 0) o.WriteByte((byte)(hi * 16));
            return o.ToArray();
        }

        private static byte[] A85(byte[] b)
        {
            var o = new MemoryStream(); var g = new int[5]; int n = 0, i = 0;
            if (b.Length >= 2 && b[0] == '<' && b[1] == '~') i = 2;
            while (i < b.Length)
            {
                int c = b[i++];
                if (c == '~') break;
                if (PdfLexer.Ws(c)) continue;
                if (c == 'z' && n == 0) { for (int k = 0; k < 4; k++) o.WriteByte(0); continue; }
                if (c < 33 || c > 117) continue;
                g[n++] = c - 33;
                if (n == 5)
                {
                    long v = 0; for (int k = 0; k < 5; k++) v = v * 85 + g[k];
                    for (int k = 3; k >= 0; k--) o.WriteByte((byte)((v >> (8 * k)) & 0xFF));
                    n = 0;
                }
            }
            if (n > 1)
            {
                for (int k = n; k < 5; k++) g[k] = 84;
                long v = 0; for (int k = 0; k < 5; k++) v = v * 85 + g[k];
                for (int k = 3; k >= 4 - (n - 1); k--) o.WriteByte((byte)((v >> (8 * k)) & 0xFF));
            }
            return o.ToArray();
        }

        private static byte[] Rl(byte[] b)
        {
            var o = new MemoryStream(); int i = 0;
            while (i < b.Length)
            {
                int l = b[i++];
                if (l == 128) break;
                if (l < 128) { int n = Math.Min(l + 1, b.Length - i); o.Write(b, i, n); i += n; }
                else { if (i >= b.Length) break; byte v = b[i++]; for (int k = 0; k < 257 - l; k++) o.WriteByte(v); }
            }
            return o.ToArray();
        }

        private static byte[] Lzw(byte[] b, int early)
        {
            var o = new MemoryStream();
            var tab = new List<byte[]>(4096);
            void Reset() { tab.Clear(); for (int i = 0; i < 256; i++) tab.Add(new[] { (byte)i }); tab.Add(Array.Empty<byte>()); tab.Add(Array.Empty<byte>()); }
            Reset();
            int bits = 9, nb = 0, ip = 0; long buf = 0; byte[]? prev = null;
            while (true)
            {
                while (nb < bits && ip < b.Length) { buf = (buf << 8) | b[ip++]; nb += 8; }
                if (nb < bits) break;
                int code = (int)((buf >> (nb - bits)) & ((1L << bits) - 1)); nb -= bits;
                if (code == 256) { Reset(); bits = 9; prev = null; continue; }
                if (code == 257) break;
                byte[] e;
                if (code < tab.Count) e = tab[code];
                else if (prev != null) e = Pdf.Concat(prev, new[] { prev[0] });
                else break;
                o.Write(e, 0, e.Length);
                if (prev != null) tab.Add(Pdf.Concat(prev, new[] { e[0] }));
                prev = e;
                int lim = tab.Count + early;
                bits = lim >= 2048 ? 12 : lim >= 1024 ? 11 : lim >= 512 ? 10 : 9;
            }
            return o.ToArray();
        }

        // ---------------- dekripsi
        private void SetupCrypt(string password)
        {
            var er = Trailer["Encrypt"];
            if (er == null) return;
            EncryptRef = er as PRef;
            var ed = RDict(er);
            if (ed == null) return;
            Encrypted = true;
            Permissions = Pdf.Int(ed["P"], -1);
            var id0 = ((Res(Trailer["ID"]) as List<object?>)?.FirstOrDefault() as PStr)?.B ?? Array.Empty<byte>();
            var c = PdfDecrypt.Open(ed, id0, password) ?? throw new PdfPasswordException("Password salah");
            _crypt = c;
            OwnerAuth = c.Owner;
            _cache.Clear(); _stmCache.Clear();
        }
    }

    /// <summary>Standard Security Handler: RC4 (V1/V2), AES-128 (V4 AESV2), AES-256 (V5, R5/R6).</summary>
    public sealed class PdfDecrypt
    {
        private readonly byte[] _key; private readonly bool _aes, _v5, _encMeta; private readonly string _strF, _stmF;
        public bool Owner { get; private set; }

        private PdfDecrypt(byte[] key, bool aes, bool v5, string strF, string stmF, bool encMeta)
        { _key = key; _aes = aes; _v5 = v5; _strF = strF; _stmF = stmF; _encMeta = encMeta; }

        private static readonly byte[] PAD = {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A };

        private static byte[] Take(byte[] b, int n) { var r = new byte[n]; Buffer.BlockCopy(b, 0, r, 0, Math.Min(n, b.Length)); return r; }
        private static byte[] Range(byte[] b, int s, int e) { var r = new byte[e - s]; Buffer.BlockCopy(b, s, r, 0, e - s); return r; }

        public static PdfDecrypt? Open(PDict ed, byte[] id0, string password)
        {
            if (ed.Name("Filter") != "Standard") throw new PdfPasswordException($"Jenis enkripsi {ed.Name("Filter")} belum didukung");
            int v = Pdf.Int(ed["V"]), r = Pdf.Int(ed["R"]);
            if ((ed["O"] as PStr)?.B is not byte[] o) return null;
            if ((ed["U"] as PStr)?.B is not byte[] u) return null;
            int p = Pdf.Int(ed["P"]);
            bool encMeta = !(ed["EncryptMetadata"] is bool em && !em);
            bool aes = false; string strF = "StdCF", stmF = "StdCF";
            if (v >= 4)
            {
                var cf = ed["CF"] as PDict;
                strF = ed.Name("StrF") ?? "Identity"; stmF = ed.Name("StmF") ?? "Identity";
                var std = cf?[stmF] as PDict ?? cf?[strF] as PDict;
                var cfm = std?.Name("CFM");
                aes = cfm == "AESV2" || cfm == "AESV3";
            }
            if (r >= 5)
            {
                var pw = Encoding.UTF8.GetBytes(password); if (pw.Length > 127) pw = Take(pw, 127);
                var ue = (ed["UE"] as PStr)?.B; var oe = (ed["OE"] as PStr)?.B;
                if (u.Length >= 48 && ue != null && HashR6(pw, Range(u, 32, 40), null, r).SequenceEqual(Take(u, 32)))
                {
                    var k = HashR6(pw, Range(u, 40, 48), null, r);
                    return new PdfDecrypt(AesCbcNoIvDecrypt(k, ue), true, true, strF, stmF, encMeta);
                }
                if (o.Length >= 48 && oe != null && HashR6(pw, Range(o, 32, 40), Take(u, 48), r).SequenceEqual(Take(o, 32)))
                {
                    var k = HashR6(pw, Range(o, 40, 48), Take(u, 48), r);
                    return new PdfDecrypt(AesCbcNoIvDecrypt(k, oe), true, true, strF, stmF, encMeta) { Owner = true };
                }
                return null;
            }
            int len = v == 1 ? 5 : Math.Clamp(Pdf.Int(ed["Length"], 40) / 8, 5, 16);
            byte[] KeyFor(byte[] pwb)
            {
                using var md = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                md.AppendData(PadPw(pwb)); md.AppendData(o);
                md.AppendData(new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) });
                md.AppendData(id0);
                if (r >= 4 && !encMeta) md.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
                var k = Take(md.GetHashAndReset(), len);
                if (r >= 3) for (int i = 0; i < 50; i++) k = Take(MD5.HashData(k), len);
                return k;
            }
            bool UserOk(byte[] k)
            {
                if (r == 2) return Rc4(k, PAD).SequenceEqual(Take(u, 32));
                var x = Rc4(k, MD5.HashData(Pdf.Concat(PAD, id0)));
                for (int i = 1; i <= 19; i++) { var kk = k.Select(b => (byte)(b ^ i)).ToArray(); x = Rc4(kk, x); }
                return x.SequenceEqual(Take(u, 16));
            }
            var pwl = Pdf.Latin1.GetBytes(password);
            var k1 = KeyFor(pwl);
            if (UserOk(k1)) return new PdfDecrypt(k1, aes, false, strF, stmF, encMeta);
            {
                var ok = MD5.HashData(PadPw(pwl));
                if (r >= 3) for (int i = 0; i < 50; i++) ok = MD5.HashData(ok);
                var ok2 = Take(ok, len);
                var up = Take(o, 32);
                if (r == 2) up = Rc4(ok2, up);
                else for (int i = 19; i >= 0; i--) { var kk = ok2.Select(b => (byte)(b ^ i)).ToArray(); up = Rc4(kk, up); }
                var k = KeyFor(up);
                if (UserOk(k)) return new PdfDecrypt(k, aes, false, strF, stmF, encMeta) { Owner = true };
            }
            return null;
        }

        public static byte[] PadPw(byte[] pw) { var r = new byte[32]; for (int i = 0; i < 32; i++) r[i] = i < pw.Length ? pw[i] : PAD[i - pw.Length]; return r; }

        public static byte[] Rc4(byte[] k, byte[] d)
        {
            var s = new int[256]; for (int i = 0; i < 256; i++) s[i] = i;
            int j = 0;
            for (int i = 0; i < 256; i++) { j = (j + s[i] + k[i % k.Length]) & 255; (s[i], s[j]) = (s[j], s[i]); }
            int a = 0; j = 0;
            var r = new byte[d.Length];
            for (int n = 0; n < d.Length; n++)
            {
                a = (a + 1) & 255; j = (j + s[a]) & 255; (s[a], s[j]) = (s[j], s[a]);
                r[n] = (byte)(d[n] ^ s[(s[a] + s[j]) & 255]);
            }
            return r;
        }

        /// <summary>ISO 32000-2 Algoritma 2.B (R6); R5 = SHA-256 sekali saja.</summary>
        public static byte[] HashR6(byte[] pw, byte[] salt, byte[]? u, int r)
        {
            var k = SHA256.HashData(Pdf.Concat(pw, salt, u ?? Array.Empty<byte>()));
            if (r == 5) return k;
            int round = 0;
            using var aes = Aes.Create();
            while (true)
            {
                var unit = Pdf.Concat(pw, k, u ?? Array.Empty<byte>());
                var k1 = new byte[unit.Length * 64];
                for (int i = 0; i < 64; i++) Buffer.BlockCopy(unit, 0, k1, i * unit.Length, unit.Length);
                aes.Key = Take(k, 16);
                var e = aes.EncryptCbc(k1, Range(k, 16, 32), PaddingMode.None);
                int sum = 0; for (int i = 0; i < 16; i++) sum += e[i];
                k = (sum % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
                round++;
                if (round >= 64 && e[^1] <= round - 32) break;
            }
            return Take(k, 32);
        }

        private static byte[] AesCbcNoIvDecrypt(byte[] k, byte[] d)
        {
            using var a = Aes.Create(); a.Key = k;
            return a.DecryptCbc(Take(d, 32), new byte[16], PaddingMode.None);
        }

        private byte[] ObjKey(int n, int g)
        {
            if (_v5) return _key;
            var buf = new List<byte>(_key) { (byte)n, (byte)(n >> 8), (byte)(n >> 16), (byte)g, (byte)(g >> 8) };
            if (_aes) buf.AddRange(new byte[] { 0x73, 0x41, 0x6C, 0x54 });
            return Take(MD5.HashData(buf.ToArray()), Math.Min(16, _key.Length + 5));
        }

        private static byte[] Dec(byte[] k, byte[] d, bool useAes)
        {
            if (!useAes) return Rc4(k, d);
            if (d.Length < 16) return Array.Empty<byte>();
            using var a = Aes.Create(); a.Key = k;
            var iv = Take(d, 16);
            var body = Range(d, 16, 16 + (d.Length - 16) / 16 * 16);
            try { return a.DecryptCbc(body, iv, PaddingMode.PKCS7); }
            catch { try { return a.DecryptCbc(body, iv, PaddingMode.None); } catch { return d; } }
        }

        public object? DecryptObj(object? o, int n, int g)
        {
            byte[]? k = null;
            byte[] K() => k ??= ObjKey(n, g);
            object? Walk(object? x)
            {
                switch (x)
                {
                    case PStr s: if (_strF != "Identity") s.B = Dec(K(), s.B, _aes); break;
                    case List<object?> l: foreach (var i in l) Walk(i); break;
                    case PDict d: foreach (var vv in d.M.Values.ToList()) Walk(vv); break;
                    case PStream st:
                        Walk(st.Dict);
                        var t = st.Dict.Name("Type");
                        bool skip = t == "XRef" || (t == "Metadata" && !_encMeta) || _stmF == "Identity" ||
                                    (st.Dict["Filter"] is List<object?> fl && fl.Any(f => (f as PName)?.N == "Crypt")) || st.Dict.Name("Filter") == "Crypt";
                        if (!skip) st.Raw = Dec(K(), st.Raw, _aes);
                        break;
                }
                return x;
            }
            return Walk(o);
        }
    }
}
