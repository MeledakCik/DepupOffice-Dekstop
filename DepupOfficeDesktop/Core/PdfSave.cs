using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Tulis ulang PDF dari <see cref="PdfFile"/> TANPA merender ulang halaman (port PdfSave.kt): teks, font, vektor dan
    /// foto tetap asli. Dipakai untuk kunci AES-256 (V5/R6) + izin, Dokumen Terlindungi, nomor halaman, watermark,
    /// gabung/pisah/susun lossless. Object stream & xref stream dibuka jadi objek biasa + tabel xref klasik.
    /// </summary>
    public static class PdfSave
    {
        public sealed class Perms
        {
            public bool Print = true, Copy = true, Modify = true, Annotate = true;
            public Perms() { }
            public Perms(bool print, bool copy, bool modify, bool annotate) { Print = print; Copy = copy; Modify = modify; Annotate = annotate; }
            /// <summary>Nilai /P (ISO 32000-2 tabel 22): bit cadangan 7-8 & 13-32 = 1.</summary>
            public int P()
            {
                int v = unchecked((int)0xFFFFF0C0);
                if (Print) v |= (1 << 2) | (1 << 11);
                if (Modify) v |= (1 << 3) | (1 << 10);
                if (Copy) v |= (1 << 4) | (1 << 9);
                if (Annotate) v |= (1 << 5) | (1 << 8);
                if (!Copy) v |= 1 << 9;
                return v;
            }
        }

        public sealed class Lock
        {
            public readonly string UserPw, OwnerPw; public readonly Perms Perms;
            public Lock(string userPw, string ownerPw, Perms perms) { UserPw = userPw; OwnerPw = ownerPw; Perms = perms; }
        }

        /// <summary>Wadah perubahan sebelum ditulis: objek baru/diganti (nomor → objek).</summary>
        public sealed class Ctx
        {
            public readonly PdfFile F;
            public readonly Dictionary<int, object?> Objs = new();
            public PRef? InfoRef;
            public bool DropInfo;
            private int _next;
            private PDict? _catalog;
            public Ctx(PdfFile f) { F = f; _next = f.MaxObjectNumber() + 1; }
            public PRef Add(object? o) { int n = _next++; Objs[n] = o; return new PRef(n, 0); }
            public void Put(PRef r, object? o) => Objs[r.Num] = o;
            public PDict Catalog
            {
                get
                {
                    if (_catalog != null) return _catalog;
                    var c = F.Catalog;
                    if (F.Trailer["Root"] is PRef r) Objs[r.Num] = c;
                    return _catalog = c;
                }
            }
            /// <summary>Dict halaman yang bisa diubah (perubahan ikut ditulis).</summary>
            public PDict PageDict(PdfFile.Page p)
            {
                if (p.Ref != null) Objs[p.Ref.Num] = p.Dict;
                return p.Dict;
            }
        }

        public static void Write(PdfFile f, Stream output, Lock? lk = null, Action<Ctx>? edit = null)
        {
            var ctx = new Ctx(f);
            edit?.Invoke(ctx);
            if (lk != null)
            {
                var adbe = new PDict(); adbe["BaseVersion"] = new PName("1.7"); adbe["ExtensionLevel"] = 8L;
                var ext = new PDict(); ext["ADBE"] = adbe;
                ctx.Catalog["Extensions"] = ext;
            }
            var idList = f.Res(f.Trailer["ID"]) as List<object?>;
            List<object?> id;
            if (idList != null && idList.Count == 2 && idList.All(x => x is PStr)) id = idList;
            else { var r = RandomNumberGenerator.GetBytes(16); id = new List<object?> { new PStr(r, true), new PStr((byte[])r.Clone(), true) }; }
            var enc = lk != null ? new Aes256(lk) : null;
            var skip = new HashSet<int>();
            if (f.EncryptRef != null) skip.Add(f.EncryptRef.Num);
            var nums = new List<int>();
            foreach (var n in f.ObjectNumbers())
            {
                if (skip.Contains(n)) continue;
                var o = ctx.Objs.TryGetValue(n, out var co) ? co : f.Obj(n);
                var t = (o as PStream)?.Dict.Name("Type");
                if (t == "XRef" || t == "ObjStm") continue;
                if (o == null) continue;
                nums.Add(n);
            }
            foreach (var n in ctx.Objs.Keys) if (!nums.Contains(n) && ctx.Objs[n] != null) nums.Add(n);
            nums.Sort();
            int maxN = nums.Count == 0 ? 0 : nums[^1];
            int encN = enc != null ? ++maxN : -1;

            var w = new Counting(output);
            var ver = enc != null ? "1.7" : string.CompareOrdinal(f.Version, "1.4") > 0 ? f.Version : "1.4";
            w.Ascii($"%PDF-{ver}\n%âãÏÓ\n");
            var offs = new Dictionary<int, long>();
            var ser = new Ser(enc);
            foreach (var n in nums)
            {
                bool mine = ctx.Objs.ContainsKey(n);
                var o = mine ? ctx.Objs[n] : f.Obj(n);
                int g = mine ? 0 : f.GenOf(n);
                offs[n] = w.N;
                w.Ascii($"{n} {g} obj\n");
                if (o is PStream ps)
                {
                    var body = enc?.Encrypt(ps.Raw) ?? ps.Raw;
                    var d = new PDict(ps.Dict); d["Length"] = (long)body.Length;
                    ser.Write(w, d);
                    w.Ascii("\nstream\n"); w.Write(body); w.Ascii("\nendstream");
                }
                else ser.Write(w, o);
                w.Ascii("\nendobj\n");
            }
            if (enc != null)
            {
                offs[encN] = w.N;
                w.Ascii($"{encN} 0 obj\n");
                new Ser(null).Write(w, enc.Dict());
                w.Ascii("\nendobj\n");
            }
            long xref = w.N;
            int size = maxN + 1;
            w.Ascii($"xref\n0 {size}\n");
            var sb = new StringBuilder();
            sb.Append("0000000000 65535 f\r\n");
            for (int n = 1; n < size; n++)
            {
                if (!offs.TryGetValue(n, out var off)) sb.Append("0000000000 00001 f\r\n");
                else sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                       .Append((ctx.Objs.ContainsKey(n) || n == encN ? 0 : f.GenOf(n)).ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
                if (sb.Length > 64000) { w.Ascii(sb.ToString()); sb.Clear(); }
            }
            w.Ascii(sb.ToString());
            var tr = new PDict();
            tr["Size"] = (long)size;
            tr["Root"] = f.Trailer["Root"];
            if (ctx.InfoRef != null) tr["Info"] = ctx.InfoRef;
            else if (f.Trailer["Info"] != null && !ctx.DropInfo) tr["Info"] = f.Trailer["Info"];
            if (enc != null) tr["Encrypt"] = new PRef(encN, 0);
            tr["ID"] = id;
            w.Ascii("trailer\n");
            new Ser(null).Write(w, tr);
            w.Ascii($"\nstartxref\n{xref}\n%%EOF\n");
            output.Flush();
        }

        public static byte[] WriteBytes(PdfFile f, Lock? lk = null, Action<Ctx>? edit = null)
        {
            using var ms = new MemoryStream();
            Write(f, ms, lk, edit);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ serialisasi

        internal sealed class Counting
        {
            private readonly Stream _o; public long N;
            public Counting(Stream o) { _o = o; }
            public void Write(byte[] b) { _o.Write(b, 0, b.Length); N += b.Length; }
            public void Ascii(string s) => Write(Pdf.Latin1.GetBytes(s));
        }

        private const string HEX = "0123456789ABCDEF";

        internal sealed class Ser
        {
            private readonly Aes256? _enc;
            public Ser(Aes256? enc) { _enc = enc; }
            public void Write(Counting w, object? o) { var ms = new MemoryStream(); Emit(o, ms); w.Write(ms.ToArray()); }
            public static byte[] Bytes(object? o) { var ms = new MemoryStream(); new Ser(null).Emit(o, ms); return ms.ToArray(); }

            private void Emit(object? o, MemoryStream outp)
            {
                void A(string s) { var b = Pdf.Latin1.GetBytes(s); outp.Write(b, 0, b.Length); }
                switch (o)
                {
                    case null: case PNull: A("null"); break;
                    case bool b: A(b ? "true" : "false"); break;
                    case long l: A(l.ToString(CultureInfo.InvariantCulture)); break;
                    case int i: A(i.ToString(CultureInfo.InvariantCulture)); break;
                    case double d: A(Pdf.Fmt(d)); break;
                    case float fl: A(Pdf.Fmt(fl)); break;
                    case PName n: A("/"); A(NameEsc(n.N)); break;
                    case PStr s:
                        {
                            var b = _enc?.Encrypt(s.B) ?? s.B;
                            var sb = new StringBuilder(b.Length * 2 + 2).Append('<');
                            foreach (var x in b) sb.Append(HEX[x >> 4]).Append(HEX[x & 15]);
                            A(sb.Append('>').ToString());
                            break;
                        }
                    case PRef r: A($"{r.Num} {r.Gen} R"); break;
                    case List<object?> l:
                        {
                            A("["); bool first = true;
                            foreach (var x in l) { if (!first) A(" "); first = false; Emit(x, outp); }
                            A("]"); break;
                        }
                    case PDict d:
                        A("<<");
                        foreach (var kv in d.Entries()) { if (kv.Value == null) continue; A("/"); A(NameEsc(kv.Key)); A(" "); Emit(kv.Value, outp); A("\n"); }
                        A(">>"); break;
                    case PStream st: Emit(st.Dict, outp); break;
                    default: A("null"); break;
                }
            }
        }

        private static string NameEsc(string n)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(n))
            {
                int c = b;
                if (c < 0x21 || c > 0x7E || c == '#' || PdfLexer.Delim(c)) sb.Append('#').Append(HEX[c >> 4]).Append(HEX[c & 15]);
                else sb.Append((char)c);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ AES-256 (V5 R6)

        internal sealed class Aes256
        {
            private readonly byte[] _fk = RandomNumberGenerator.GetBytes(32);
            private readonly byte[] _u, _ue, _o, _oe, _perms;
            private readonly int _p;

            public Aes256(Lock lk)
            {
                _p = lk.Perms.P();
                static byte[] Pw(string s) { var b = Encoding.UTF8.GetBytes(s); return b.Length > 127 ? b.Take(127).ToArray() : b; }
                var up = Pw(lk.UserPw);
                var op = Pw(string.IsNullOrEmpty(lk.OwnerPw) ? lk.UserPw : lk.OwnerPw);
                var uvs = Salt(); var uks = Salt();
                _u = Pdf.Concat(PdfDecrypt.HashR6(up, uvs, null, 6), uvs, uks);
                _ue = AesNoIv(PdfDecrypt.HashR6(up, uks, null, 6), _fk);
                var ovs = Salt(); var oks = Salt();
                _o = Pdf.Concat(PdfDecrypt.HashR6(op, ovs, _u, 6), ovs, oks);
                _oe = AesNoIv(PdfDecrypt.HashR6(op, oks, _u, 6), _fk);
                var pb = new byte[16];
                for (int i = 0; i < 4; i++) pb[i] = (byte)(_p >> (8 * i));
                for (int i = 4; i < 8; i++) pb[i] = 0xFF;
                pb[8] = (byte)'T'; pb[9] = (byte)'a'; pb[10] = (byte)'d'; pb[11] = (byte)'b';
                var r4 = Salt(); for (int i = 12; i < 16; i++) pb[i] = r4[i - 12];
                using var a = Aes.Create(); a.Key = _fk;
                _perms = a.EncryptEcb(pb, PaddingMode.None);
            }

            private static byte[] Salt() => RandomNumberGenerator.GetBytes(8);
            private static byte[] AesNoIv(byte[] k, byte[] d) { using var a = Aes.Create(); a.Key = k; return a.EncryptCbc(d, new byte[16], PaddingMode.None); }

            public byte[] Encrypt(byte[] d)
            {
                var iv = RandomNumberGenerator.GetBytes(16);
                using var a = Aes.Create(); a.Key = _fk;
                return Pdf.Concat(iv, a.EncryptCbc(d, iv, PaddingMode.PKCS7));
            }

            public PDict Dict()
            {
                var std = new PDict(); std["AuthEvent"] = new PName("DocOpen"); std["CFM"] = new PName("AESV3"); std["Length"] = 32L;
                var cf = new PDict(); cf["StdCF"] = std;
                var d = new PDict();
                d["Filter"] = new PName("Standard"); d["V"] = 5L; d["R"] = 6L; d["Length"] = 256L;
                d["CF"] = cf; d["StmF"] = new PName("StdCF"); d["StrF"] = new PName("StdCF");
                d["O"] = new PStr(_o, true); d["U"] = new PStr(_u, true); d["OE"] = new PStr(_oe, true); d["UE"] = new PStr(_ue, true);
                d["Perms"] = new PStr(_perms, true); d["P"] = (long)_p;
                return d;
            }
        }
    }

    /// <summary>
    /// Penyusun PDF baru dari halaman beberapa PDF sumber TANPA render ulang (gabung, pisah, susun & putar).
    /// Objek tiap sumber disalin dalam (deep copy) dengan penomoran ulang; rujukan /Parent diganti ke pohon halaman baru.
    /// </summary>
    public sealed class PdfComposer
    {
        private readonly List<object?> _objs = new() { null }; // indeks = nomor objek
        private readonly List<PRef> _kids = new();
        private readonly int _pagesNum;
        private readonly Dictionary<(PdfFile, int), int> _map = new();

        public PdfComposer() { _objs.Add(null); _pagesNum = 1; }

        private int Reserve() { _objs.Add(null); return _objs.Count - 1; }
        public PRef Add(object? o) { int n = Reserve(); _objs[n] = o; return new PRef(n, 0); }

        private object? Copy(PdfFile f, object? o, int depth = 0)
        {
            if (depth > 200) return null;
            switch (o)
            {
                case PRef r:
                    {
                        if (_map.TryGetValue((f, r.Num), out var m)) return new PRef(m, 0);
                        var target = f.Obj(r.Num);
                        if (target == null) return PNull.I;
                        // jangan ikut menyalin pohon halaman lama (Parent) — diganti
                        if (PdfFile.DictOf(target)?.Name("Type") == "Pages") return new PRef(_pagesNum, 0);
                        int n = Reserve();
                        _map[(f, r.Num)] = n;
                        _objs[n] = Copy(f, target, depth + 1);
                        return new PRef(n, 0);
                    }
                case PDict d:
                    {
                        var nd = new PDict();
                        foreach (var kv in d.Entries()) nd[kv.Key] = Copy(f, kv.Value, depth + 1);
                        return nd;
                    }
                case List<object?> l: return l.Select(x => Copy(f, x, depth + 1)).ToList();
                case PStream s: return new PStream((PDict)Copy(f, s.Dict, depth + 1)!, s.Raw);
                case PStr ps: return new PStr((byte[])ps.B.Clone(), ps.Hex);
                default: return o;
            }
        }

        /// <summary>Tambahkan halaman <paramref name="p"/> dari <paramref name="f"/>; <paramref name="extraRotate"/> kelipatan 90°.</summary>
        public void AddPage(PdfFile f, PdfFile.Page p, int extraRotate = 0)
        {
            var nd = new PDict();
            foreach (var kv in p.Dict.Entries())
            {
                if (kv.Key is "Parent" or "Annots" or "StructParents" or "B") continue;
                nd[kv.Key] = Copy(f, kv.Value);
            }
            // atribut warisan dibuat eksplisit di halaman
            nd["Type"] = new PName("Page");
            nd["Resources"] = Copy(f, p.Resources);
            nd["MediaBox"] = new List<object?> { (double)p.Box[0], (double)p.Box[1], (double)p.Box[2], (double)p.Box[3] };
            nd["CropBox"] = null;
            int rot = ((p.Rotate + extraRotate) % 360 + 360) % 360;
            nd["Rotate"] = rot == 0 ? null : (long)rot;
            // anotasi tautan/isian ikut bila ada (tanpa /P lama)
            if (f.Res(p.Dict["Annots"]) is List<object?> annots)
            {
                var na = new List<object?>();
                foreach (var a in annots)
                {
                    var ad = f.RDict(a);
                    if (ad == null) continue;
                    var c = new PDict();
                    foreach (var kv in ad.Entries()) if (kv.Key is not ("P" or "Parent" or "Popup")) c[kv.Key] = Copy(f, kv.Value);
                    if (c.Name("Subtype") == "Widget") continue; // isian formulir tidak dibawa (butuh AcroForm)
                    na.Add(Add(c));
                }
                if (na.Count > 0) nd["Annots"] = na;
            }
            nd["Parent"] = new PRef(_pagesNum, 0);
            _kids.Add(Add(nd));
        }

        public int PageCount => _kids.Count;

        public void Save(string path, PdfSave.Lock? lk = null) { using var fs = File.Create(path); Save(fs, lk); }

        public void Save(Stream output, PdfSave.Lock? lk = null)
        {
            var pages = new PDict();
            pages["Type"] = new PName("Pages");
            pages["Kids"] = _kids.Cast<object?>().ToList();
            pages["Count"] = (long)_kids.Count;
            _objs[_pagesNum] = pages;
            var cat = new PDict(); cat["Type"] = new PName("Catalog"); cat["Pages"] = new PRef(_pagesNum, 0);
            var catRef = Add(cat);
            var info = new PDict(); info["Producer"] = PStr.Of("Depup Office Desktop"); info["CreationDate"] = PStr.Of("D:" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            var infoRef = Add(info);
            // Bangun PdfFile sementara di memori lalu tulis lewat PdfSave (agar enkripsi & serialisasi sama)
            var tmp = BuildRaw(catRef, infoRef);
            var pf = PdfFile.Open(tmp);
            PdfSave.Write(pf, output, lk);
        }

        private byte[] BuildRaw(PRef catRef, PRef infoRef)
        {
            using var ms = new MemoryStream();
            var w = new PdfSave.Counting(ms);
            w.Ascii("%PDF-1.7\n%âãÏÓ\n");
            var offs = new long[_objs.Count];
            for (int n = 1; n < _objs.Count; n++)
            {
                var o = _objs[n];
                offs[n] = w.N;
                w.Ascii($"{n} 0 obj\n");
                if (o is PStream s)
                {
                    var d = new PDict(s.Dict); d["Length"] = (long)s.Raw.Length;
                    w.Write(PdfSave.Ser.Bytes(d)); w.Ascii("\nstream\n"); w.Write(s.Raw); w.Ascii("\nendstream");
                }
                else w.Write(PdfSave.Ser.Bytes(o ?? PNull.I));
                w.Ascii("\nendobj\n");
            }
            long xref = w.N;
            var sb = new StringBuilder($"xref\n0 {_objs.Count}\n0000000000 65535 f\r\n");
            for (int n = 1; n < _objs.Count; n++) sb.Append(offs[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
            w.Ascii(sb.ToString());
            var tr = new PDict(); tr["Size"] = (long)_objs.Count; tr["Root"] = catRef; tr["Info"] = infoRef;
            w.Ascii("trailer\n"); w.Write(PdfSave.Ser.Bytes(tr)); w.Ascii($"\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }
    }
}
