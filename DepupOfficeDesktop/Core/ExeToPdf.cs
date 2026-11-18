using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// EXE → PDF (port ExeToPdf.kt Android v4.1): mengambil kembali PDF yang dibungkus jadi program Windows (.exe) oleh
    /// pengubah "PDF to EXE" (mis. VaySoft). Berkas .exe TIDAK PERNAH dijalankan — hanya dibaca sebagai data.
    /// PDF aslinya tersimpan di ujung berkas (overlay sesudah section PE terakhir), biasanya disamarkan XOR 12 byte.
    /// Urutan: (1) PDF polos di overlay, (2) kunci VaySoft yang dikenal, (3) kunci dipulihkan dari pola tabel xref.
    /// </summary>
    public static class ExeToPdf
    {
        public sealed record Result(byte[] Pdf, string Method, string? KeyHex);

        private const int K = 12;
        private static readonly byte[] VAYSOFT = { 0x00, 0x0B, 0x0C, 0x04, 0x0D, 0x03, 0x04, 0x0B, 0x0C, 0x03, 0x06, 0x02 };
        private static readonly byte[] MAGIC = Encoding.ASCII.GetBytes("%PDF-");
        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        /// <summary>Awal overlay (sesudah section PE terakhir); 0 bila bukan berkas PE.</summary>
        public static int OverlayStart(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return 0;
            int pe = U32(d, 0x3C);
            if (pe < 0 || pe + 24 > d.Length || d[pe] != 'P' || d[pe + 1] != 'E' || d[pe + 2] != 0 || d[pe + 3] != 0) return 0;
            int nSec = U16(d, pe + 6), opt = U16(d, pe + 20);
            int tab = pe + 24 + opt, end = 0;
            for (int i = 0; i < nSec; i++)
            {
                int s = tab + i * 40;
                if (s + 40 > d.Length) break;
                int size = U32(d, s + 16), raw = U32(d, s + 20);
                if (size >= 0 && raw >= 0) end = Math.Max(end, raw + size);
            }
            return Math.Clamp(end, 0, d.Length);
        }

        public static Result Extract(byte[] data)
        {
            int start = OverlayStart(data);
            var ov = start > 0 && start < data.Length ? data[start..] : data;
            int at = IndexOf(ov, MAGIC, 0);
            if (at >= 0 && Trim(ov[at..]) is { } p) return new Result(p, "PDF tanpa penyamaran", null);
            var vs = Xor(ov, VAYSOFT, 0);
            if (StartsWith(vs, MAGIC) && Trim(vs) is { } p2) return new Result(p2, "Kunci VaySoft", Hex(VAYSOFT));
            var r = Recover(ov);
            if (r != null) return r;
            throw new InvalidOperationException("PDF tidak ditemukan di berkas ini. Mungkin bukan hasil \"PDF to EXE\", atau isinya dikunci kata sandi.");
        }

        // ---------- pemulihan kunci dari tabel xref ----------
        private static readonly int[] KNOWN = { 10, 16, 18, 19 };
        private static readonly int[][] PLAINS = { new[] { 0x20, 0x20, 0x0D, 0x0A }, new[] { 0x20, 0x20, 0x20, 0x0A } };

        private static Result? Recover(byte[] d)
        {
            int n = d.Length, stride = 60, minRows = 8, o = 0, tried = 0;
            while (o < n - minRows * stride - 20 && tried < 5000)
            {
                bool ok = true;
                foreach (int p in KNOWN)
                {
                    byte rf = d[o + p];
                    for (int k = 1; k < minRows; k++) if (d[o + p + k * stride] != rf) { ok = false; break; }
                    if (!ok) break;
                }
                if (ok)
                {
                    tried++;
                    foreach (var pl in PLAINS) { var r = TryTable(d, o, pl); if (r != null) return r; }
                }
                o++;
            }
            return null;
        }

        private static Result? TryTable(byte[] d, int o, int[] plain)
        {
            var key = new int?[K];
            int rows = 0;
            while (o + rows * 20 + 20 <= d.Length && rows < 60)
            {
                int b = o + rows * 20;
                bool good = true;
                for (int i = 0; i < KNOWN.Length; i++)
                {
                    int ki = Mod(b + KNOWN[i], K), kb = d[b + KNOWN[i]] ^ plain[i];
                    if (key[ki] is { } prev && prev != kb) { good = false; break; }
                    key[ki] = kb;
                }
                if (!good) { if (rows < 3) return null; break; }
                rows++;
            }
            for (int r = 0; r < Math.Min(rows, 6); r++)
            {
                int b = o + r * 20;
                for (int pos = 0; pos < 18; pos++)
                {
                    if (key[Mod(b + pos, K)] is not { } k) continue;
                    int c = d[b + pos] ^ k;
                    bool fine = pos switch { 10 or 16 => c == 0x20, 17 => c == 'n' || c == 'f', _ => c >= 0x30 && c <= 0x39 };
                    if (!fine) return null;
                }
            }
            FillFrom(d, o, key, Encoding.ASCII.GetBytes("xref"));
            var pdfAt = FindPdfStart(d, key);
            if (pdfAt == null) return null;
            var head = Encoding.ASCII.GetBytes("%PDF-1.");
            for (int j = 0; j < head.Length; j++) { int ki = Mod(pdfAt.Value + j, K); key[ki] ??= d[pdfAt.Value + j] ^ head[j]; }
            var missing = Enumerable.Range(0, K).Where(i => key[i] == null).ToList();
            if (missing.Count > 2) return null;
            int total = 1 << (8 * missing.Count);
            for (int combo = 0; combo < total; combo++)
            {
                var kb = new byte[K];
                for (int i = 0; i < K; i++) kb[i] = (byte)(key[i] ?? 0);
                for (int idx = 0; idx < missing.Count; idx++) kb[missing[idx]] = (byte)((combo >> (8 * idx)) & 0xFF);
                int headLen = Math.Min(2048, d.Length - pdfAt.Value);
                var hd = new byte[headLen];
                for (int i = 0; i < headLen; i++) hd[i] = (byte)(d[pdfAt.Value + i] ^ kb[Mod(pdfAt.Value + i, K)]);
                if (!StartsWith(hd, MAGIC) || IndexOf(hd, Encoding.ASCII.GetBytes("obj"), 0) < 0) continue;
                var outB = new byte[d.Length - pdfAt.Value];
                for (int i = 0; i < outB.Length; i++) outB[i] = (byte)(d[pdfAt.Value + i] ^ kb[Mod(pdfAt.Value + i, K)]);
                if (Trim(outB) is not { } pdf) continue;
                var aligned = new byte[K];
                for (int i = 0; i < K; i++) aligned[i] = kb[Mod(pdfAt.Value + i, K)];
                return new Result(pdf, "Kunci dipulihkan dari tabel xref", Hex(aligned));
            }
            return null;
        }

        private static void FillFrom(byte[] d, int o, int?[] key, byte[] word)
        {
            for (int back = 5; back <= 40; back++)
            {
                int s = o - back;
                if (s < 0) break;
                bool consistent = true;
                var add = new Dictionary<int, int>();
                for (int j = 0; j < word.Length; j++)
                {
                    int ki = Mod(s + j, K), kb = d[s + j] ^ word[j];
                    int? have = key[ki] ?? (add.TryGetValue(ki, out var a) ? a : null);
                    if (have != null && have != kb) { consistent = false; break; }
                    add[ki] = kb;
                }
                if (consistent && add.Keys.Any(k => key[k] == null) && add.Keys.Count(k => key[k] != null) >= 2)
                {
                    foreach (var (k, v) in add) key[k] = v;
                    return;
                }
            }
        }

        private static int? FindPdfStart(byte[] d, int?[] key)
        {
            for (int i = 0; i < d.Length - 8; i++)
            {
                bool ok = true; int known = 0;
                for (int j = 0; j < MAGIC.Length; j++)
                {
                    if (key[Mod(i + j, K)] is not { } k) continue;
                    if ((d[i + j] ^ k) != MAGIC[j]) { ok = false; break; }
                    known++;
                }
                if (ok && known >= 3) return i;
            }
            return null;
        }

        // ---------- utilitas ----------
        /// <summary>Potong tepat sesudah "%%EOF" terakhir (+ baris barunya).</summary>
        private static byte[]? Trim(byte[] p)
        {
            int eof = LastIndexOf(p, Encoding.ASCII.GetBytes("%%EOF"));
            if (eof < 64) return null;
            int end = eof + 5;
            while (end < p.Length && (p[end] == 0x0A || p[end] == 0x0D)) end++;
            return p[..end];
        }

        private static byte[] Xor(byte[] d, byte[] k, int phase) { var r = new byte[d.Length]; for (int i = 0; i < d.Length; i++) r[i] = (byte)(d[i] ^ k[Mod(i + phase, K)]); return r; }
        private static bool StartsWith(byte[] a, byte[] p) { if (a.Length < p.Length) return false; for (int i = 0; i < p.Length; i++) if (a[i] != p[i]) return false; return true; }
        private static int IndexOf(byte[] a, byte[] p, int from) => a.AsSpan(Math.Min(from, a.Length)).IndexOf(p) is var i and >= 0 ? i + from : -1;
        private static int LastIndexOf(byte[] a, byte[] p) => a.AsSpan().LastIndexOf(p);
        private static int U16(byte[] d, int o) => o + 2 > d.Length ? 0 : d[o] | (d[o + 1] << 8);
        private static int U32(byte[] d, int o) => o < 0 || o + 4 > d.Length ? -1 : d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    }
}
