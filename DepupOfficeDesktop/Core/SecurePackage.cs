using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Paket biner Dokumen Terlindungi — port 1:1 <c>SecurePackage.kt</c> (Android) / <c>SecurePackage.swift</c> (iOS).
    ///
    /// Tiga versi bisa DIBACA; hanya v3 yang DIBUAT:
    ///  - v1 (DepUp 4.1.0): kunci = HMAC(rahasia aplikasi, prefiks + docId).
    ///  - v2 (build "khusus perangkat"): kunci di Android Keystore perangkat pembuat — tidak bisa dibuka di PC.
    ///  - v3: kunci diturunkan dari KODE AKSES guru (PBKDF2-HMAC-SHA256 600.000 → HKDF-SHA256). Tidak ada kunci di aplikasi.
    ///
    /// Body: marker(19) | len,docId(36) | len,sha256Hex(64) | len,salt(16) | len,nonce(12) | len,editHashHex
    ///       | [v3: iterations int32] | editLocked(1) | len,ciphertext+tag.   Paket = body | HMAC-SHA256(macKey, body).
    /// </summary>
    public static class SecurePackage
    {
        public const string MARKER_V1 = "DEPUP_SECURE_DOC_v1";
        public const string MARKER_V2 = "DEPUP_SECURE_DOC_v2";
        public const string MARKER_V3 = "DEPUP_SECURE_DOC_v3";
        private const int MARKER_LEN = 19;
        public const int HMAC_LEN = 32, SALT_LEN = 16, NONCE_LEN = 12, TAG_LEN = 16;
        public const int V3_ITERATIONS = 600_000, V3_MIN_ITERATIONS = 100_000, V3_MAX_ITERATIONS = 5_000_000;
        public const int EDIT_ITERATIONS = 65_536;
        public const int MIN_CODE_LENGTH = 8, MAX_CODE_LENGTH = 64;
        private static readonly byte[] V3_PBKDF2_DOMAIN = Encoding.ASCII.GetBytes("DepUp-v3-Access");
        public static readonly byte[] V3_ENC_INFO = Encoding.ASCII.GetBytes("DepUp-v3-AES-GCM-Key");
        public static readonly byte[] V3_MAC_INFO = Encoding.ASCII.GetBytes("DepUp-v3-Package-HMAC-Key");
        /// <summary>Crockford Base32 tanpa I, L, O, U — tidak tertukar saat diketik dari kertas.</summary>
        private const string CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private static readonly Regex UuidRe = new("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        private static readonly Regex Hex64 = new("^[0-9a-f]{64}$");

        public sealed class Meta
        {
            public int Version; public string DocId = ""; public string OriginalSha256 = "";
            public byte[] Salt = Array.Empty<byte>(); public byte[] Nonce = Array.Empty<byte>();
            public string EditPasswordHash = ""; public int Iterations; public bool EditLocked;
            /// <summary>Offset field panjang ciphertext di dalam body.</summary>
            public int PayloadOffset;
            public void Wipe() { Array.Clear(Salt); Array.Clear(Nonce); }
        }

        /// <summary>Kode akses salah / tidak ada: UI boleh meminta ulang, bukan "dokumen rusak".</summary>
        public sealed class AccessCodeException : ProtectedDocException
        {
            public bool Missing { get; }
            public AccessCodeException(string m, bool missing) : base(m) { Missing = missing; }
        }

        // ---------------------------------------------------------------- kode akses

        /// <summary>12 karakter acak (60 bit) dalam 3 kelompok, mis. "7KQ2-M9XD-4RTW".</summary>
        public static string GenerateAccessCode()
        {
            var sb = new StringBuilder(14);
            for (int i = 0; i < 12; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(CODE_ALPHABET[RandomNumberGenerator.GetInt32(CODE_ALPHABET.Length)]);
            }
            return sb.ToString();
        }

        /// <summary>Bentuk kanonis: NFKC, huruf besar, spasi / NBSP / tab / tanda hubung dibuang. Sama persis dengan Android &amp; iOS.</summary>
        public static string NormalizeCode(string code)
        {
            var n = code.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            var sb = new StringBuilder(n.Length);
            foreach (var ch in n) if (ch != ' ' && ch != '-' && ch != ' ' && ch != '\t') sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>Kode dikelompokkan per 4 karakter untuk ditampilkan.</summary>
        public static string Grouped(string code)
        {
            var n = NormalizeCode(code);
            var sb = new StringBuilder();
            for (int i = 0; i < n.Length; i++) { if (i > 0 && i % 4 == 0) sb.Append('-'); sb.Append(n[i]); }
            return sb.ToString();
        }

        public static string? ValidateNewCode(string code)
        {
            var n = NormalizeCode(code);
            if (n.Length < MIN_CODE_LENGTH) return $"Kode akses minimal {MIN_CODE_LENGTH} karakter.";
            if (n.Length > MAX_CODE_LENGTH) return $"Kode akses maksimal {MAX_CODE_LENGTH} karakter.";
            return null;
        }

        // ---------------------------------------------------------------- pembuatan v3

        public static byte[] BuildV3(byte[] plain, string accessCode, string editPassword, string docId,
            int iterations = V3_ITERATIONS, byte[]? salt = null, byte[]? nonce = null)
        {
            if (!UuidRe.IsMatch(docId)) throw new ArgumentException("ID dokumen tidak valid.");
            salt ??= RandomNumberGenerator.GetBytes(SALT_LEN);
            nonce ??= RandomNumberGenerator.GetBytes(NONCE_LEN);
            if (salt.Length != SALT_LEN || nonce.Length != NONCE_LEN) throw new ArgumentException("Salt/nonce tidak valid.");
            if (iterations < V3_MIN_ITERATIONS || iterations > V3_MAX_ITERATIONS) throw new ArgumentException("Iterasi tidak valid.");
            var err = ValidateNewCode(accessCode);
            if (err != null) throw new ArgumentException(err);

            string sha = Sha256Hex(plain);
            string editHash = HashEditPassword(editPassword, salt);
            var ikm = V3Ikm(accessCode, salt, iterations);
            var encKey = HkdfSha256(ikm, salt, V3_ENC_INFO);
            var macKey = HkdfSha256(ikm, salt, V3_MAC_INFO);
            byte[]? ct = null, body = null;
            try
            {
                var aad = Aad(MARKER_V3, 3, docId, iterations, true);
                ct = new byte[plain.Length + TAG_LEN];
                using (var g = new AesGcm(encKey, TAG_LEN))
                    g.Encrypt(nonce, plain, ct.AsSpan(0, plain.Length), ct.AsSpan(plain.Length), aad);   // format Java: ct || tag
                var ms = new MemoryStream(ct.Length + 256);
                ms.Write(Encoding.ASCII.GetBytes(MARKER_V3));
                Field(ms, Encoding.ASCII.GetBytes(docId));
                Field(ms, Encoding.ASCII.GetBytes(sha));
                Field(ms, salt);
                Field(ms, nonce);
                Field(ms, Encoding.ASCII.GetBytes(editHash));
                ms.Write(Int32(iterations));
                ms.WriteByte(1);
                Field(ms, ct);
                body = ms.ToArray();
                var tag = HmacSha256(macKey, body);
                var pkg = new byte[body.Length + tag.Length];
                Buffer.BlockCopy(body, 0, pkg, 0, body.Length);
                Buffer.BlockCopy(tag, 0, pkg, body.Length, tag.Length);
                return pkg;
            }
            finally
            {
                Array.Clear(ikm); Array.Clear(encKey); Array.Clear(macKey);
                if (ct != null) Array.Clear(ct);
                if (body != null) Array.Clear(body);
            }
        }

        internal static byte[] V3Ikm(string accessCode, byte[] salt, int iterations)
        {
            var pw = Encoding.UTF8.GetBytes(NormalizeCode(accessCode));
            var s = new byte[salt.Length + V3_PBKDF2_DOMAIN.Length];
            Buffer.BlockCopy(salt, 0, s, 0, salt.Length);
            Buffer.BlockCopy(V3_PBKDF2_DOMAIN, 0, s, salt.Length, V3_PBKDF2_DOMAIN.Length);
            try { return Rfc2898DeriveBytes.Pbkdf2(pw, s, iterations, HashAlgorithmName.SHA256, 32); }
            finally { Array.Clear(pw); }
        }

        // ---------------------------------------------------------------- pembacaan

        /// <summary>Pisahkan body &amp; tag HMAC (keduanya salinan).</summary>
        public static (byte[] body, byte[] tag) Split(byte[] pkg)
        {
            if (pkg.Length < MARKER_LEN + HMAC_LEN) throw new ProtectedDocException("Data paket terlindungi tidak lengkap.");
            return (pkg[..^HMAC_LEN], pkg[^HMAC_LEN..]);
        }

        public static int VersionOf(byte[] body)
        {
            if (body.Length < MARKER_LEN) throw new ProtectedDocException("Data paket terlindungi tidak lengkap.");
            return Encoding.Latin1.GetString(body, 0, MARKER_LEN) switch
            {
                MARKER_V1 => 1,
                MARKER_V2 => 2,
                MARKER_V3 => 3,
                _ => throw new ProtectedDocException("Penanda Dokumen Terlindungi tidak valid.")
            };
        }

        /// <summary>Validasi ketat: data berasal dari berkas pihak luar.</summary>
        public static Meta ParseMeta(byte[] body)
        {
            int v = VersionOf(body);
            int off = MARKER_LEN;
            byte[] Next(int min, int max)
            {
                int n = ReadInt(body, off); off += 4;
                if (n < min || n > max || n > body.Length - off) throw new ProtectedDocException("Data paket terlindungi rusak.");
                var r = body[off..(off + n)]; off += n; return r;
            }
            string docId = Encoding.ASCII.GetString(Next(36, 36));
            string sha = Encoding.ASCII.GetString(Next(64, 64));
            var salt = Next(SALT_LEN, SALT_LEN);
            var nonce = Next(NONCE_LEN, NONCE_LEN);
            string edit = v == 1 ? Encoding.ASCII.GetString(Next(0, 128)) : Encoding.ASCII.GetString(Next(64, 64));
            int iterations = 0;
            if (v == 3)
            {
                iterations = ReadInt(body, off); off += 4;
                if (iterations < V3_MIN_ITERATIONS || iterations > V3_MAX_ITERATIONS) throw new ProtectedDocException("Parameter kunci dokumen tidak valid.");
            }
            if (off >= body.Length) throw new ProtectedDocException("Data paket terlindungi rusak.");
            int flag = body[off]; off += 1;
            if (flag > 1) throw new ProtectedDocException("Flag dokumen tidak valid.");
            int payloadOffset = off;
            int len = ReadInt(body, off);
            if (len < 16 || (long)off + 4L + len != body.Length) throw new ProtectedDocException("Data paket terlindungi rusak.");
            if (!UuidRe.IsMatch(docId) || !Hex64.IsMatch(sha) || (v != 1 && !Hex64.IsMatch(edit)))
                throw new ProtectedDocException("Metadata Dokumen Terlindungi tidak valid.");
            return new Meta
            {
                Version = v, DocId = docId, OriginalSha256 = sha, Salt = salt, Nonce = nonce, EditPasswordHash = edit,
                Iterations = iterations, EditLocked = flag == 1, PayloadOffset = payloadOffset
            };
        }

        /// <summary>Verifikasi HMAC (konstan-waktu), dekripsi, cek SHA-256. null bila HMAC tidak cocok.</summary>
        public static byte[]? OpenWithKeys(byte[] body, byte[] tag, Meta meta, byte[] encKey, byte[] macKey)
        {
            var expected = HmacSha256(macKey, body);
            bool ok = CryptographicOperations.FixedTimeEquals(expected, tag);
            Array.Clear(expected);
            if (!ok) return null;
            int len = ReadInt(body, meta.PayloadOffset);
            var ct = body[(meta.PayloadOffset + 4)..(meta.PayloadOffset + 4 + len)];
            try
            {
                byte[]? aad = meta.Version switch
                {
                    2 => Aad(MARKER_V2, 2, meta.DocId, null, meta.EditLocked),
                    3 => Aad(MARKER_V3, 3, meta.DocId, meta.Iterations, meta.EditLocked),
                    _ => null
                };
                var plain = new byte[ct.Length - TAG_LEN];
                try
                {
                    using var g = new AesGcm(encKey, TAG_LEN);
                    g.Decrypt(meta.Nonce, ct.AsSpan(0, plain.Length), ct.AsSpan(plain.Length), plain, aad);
                }
                catch (CryptographicException) { Array.Clear(plain); throw new ProtectedDocException("Dekripsi dokumen gagal."); }
                if (Sha256Hex(plain) != meta.OriginalSha256)
                {
                    Array.Clear(plain);
                    throw new ProtectedDocException("Integritas dokumen gagal diverifikasi (SHA-256 tidak cocok).");
                }
                return plain;
            }
            finally { Array.Clear(ct); }
        }

        /// <summary>Buka paket v3 dengan kode akses. Melempar <see cref="AccessCodeException"/> bila kode salah/kosong.</summary>
        public static byte[] OpenV3(byte[] body, byte[] tag, Meta meta, string? accessCode)
        {
            if (string.IsNullOrWhiteSpace(accessCode)) throw new AccessCodeException("Masukkan kode akses dari pembuat dokumen.", true);
            var ikm = V3Ikm(accessCode, meta.Salt, meta.Iterations);
            var enc = HkdfSha256(ikm, meta.Salt, V3_ENC_INFO);
            var mac = HkdfSha256(ikm, meta.Salt, V3_MAC_INFO);
            try
            {
                return OpenWithKeys(body, tag, meta, enc, mac)
                    ?? throw new AccessCodeException("Kode akses salah, atau dokumen telah diubah di luar DepUp Office.", false);
            }
            finally { Array.Clear(ikm); Array.Clear(enc); Array.Clear(mac); }
        }

        /// <summary>Cocokkan password edit guru dengan hash tersimpan (konstan-waktu).</summary>
        public static bool EditPasswordMatches(Meta meta, string password)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(meta.EditPasswordHash)) return false;
            var a = Encoding.ASCII.GetBytes(HashEditPassword(password, meta.Salt));
            var b = Encoding.ASCII.GetBytes(meta.EditPasswordHash);
            try { return CryptographicOperations.FixedTimeEquals(a, b); } finally { Array.Clear(a); Array.Clear(b); }
        }

        // ---------------------------------------------------------------- primitif

        public static string HashEditPassword(string password, byte[] salt)
        {
            var pw = Encoding.UTF8.GetBytes(password);
            var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, EDIT_ITERATIONS, HashAlgorithmName.SHA256, 32);
            try { return Convert.ToHexString(h).ToLowerInvariant(); } finally { Array.Clear(pw); Array.Clear(h); }
        }

        /// <summary>AAD AES-GCM v2/v3 (v2 tanpa iterations).</summary>
        public static byte[] Aad(string marker, int version, string docId, int? iterations, bool editLocked)
        {
            var id = Encoding.UTF8.GetBytes(docId);
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes(marker));
            ms.WriteByte((byte)version);
            ms.Write(Int32(id.Length)); ms.Write(id);
            if (iterations.HasValue) ms.Write(Int32(iterations.Value));
            ms.WriteByte(editLocked ? (byte)1 : (byte)0);
            return ms.ToArray();
        }

        /// <summary>RFC 5869 HKDF-SHA256.</summary>
        public static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length = 32) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, info);

        public static byte[] HmacSha256(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

        public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        private static void Field(Stream s, byte[] b) { s.Write(Int32(b.Length)); s.Write(b); }

        public static byte[] Int32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        public static int ReadInt(byte[] a, int o)
        {
            if (o < 0 || (long)o + 4 > a.Length) throw new ProtectedDocException("Data paket terlindungi rusak.");
            return (a[o] << 24) | (a[o + 1] << 16) | (a[o + 2] << 8) | a[o + 3];
        }
    }

    /// <summary>
    /// Kode akses yang pernah dimasukkan, disimpan terenkripsi DPAPI (akun Windows ini saja) — padanan AccessCodeVault Android
    /// (Keystore) / iOS (Keychain). Kunci daftar = SHA-256("depup-code:" + docId), bukan docId mentah.
    /// </summary>
    public static class AccessCodeVault
    {
        private static readonly object Gate = new();
        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupOfficeDesktop", "access_codes.json");

        private static string Slot(string docId) => SecurePackage.Sha256Hex(Encoding.UTF8.GetBytes("depup-code:" + docId));

        private static Dictionary<string, string> Read()
        {
            try
            {
                if (File.Exists(StorePath))
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new();
            }
            catch { }
            return new();
        }

        private static void Write(Dictionary<string, string> d)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(d));
        }

        public static string? Load(string docId)
        {
            lock (Gate)
            {
                try
                {
                    if (!Read().TryGetValue(Slot(docId), out var b64)) return null;
                    var plain = Dpapi.Unprotect(Convert.FromBase64String(b64), Encoding.UTF8.GetBytes(docId));
                    if (plain == null) return null;
                    try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain); }
                }
                catch { return null; }
            }
        }

        public static void Save(string docId, string code)
        {
            lock (Gate)
            {
                try
                {
                    var plain = Encoding.UTF8.GetBytes(SecurePackage.NormalizeCode(code));
                    byte[]? enc;
                    try { enc = Dpapi.Protect(plain, Encoding.UTF8.GetBytes(docId)); } finally { Array.Clear(plain); }
                    if (enc == null) return;
                    var d = Read(); d[Slot(docId)] = Convert.ToBase64String(enc); Write(d);
                }
                catch { }
            }
        }

        public static void Forget(string docId)
        {
            lock (Gate)
            {
                try { var d = Read(); if (d.Remove(Slot(docId))) Write(d); } catch { }
            }
        }

        /// <summary>CryptProtectData / CryptUnprotectData (crypt32) tanpa paket NuGet.</summary>
        private static class Dpapi
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct Blob { public int cbData; public IntPtr pbData; }

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptProtectData(ref Blob pDataIn, string? szDataDescr, ref Blob pOptionalEntropy,
                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref Blob pDataOut);

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptUnprotectData(ref Blob pDataIn, IntPtr ppszDataDescr, ref Blob pOptionalEntropy,
                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref Blob pDataOut);

            [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

            private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

            public static byte[]? Protect(byte[] data, byte[] entropy) => Run(data, entropy, true);
            public static byte[]? Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, false);

            private static byte[]? Run(byte[] data, byte[] entropy, bool protect)
            {
                if (!OperatingSystem.IsWindows()) return null;
                var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
                var hEnt = GCHandle.Alloc(entropy, GCHandleType.Pinned);
                var input = new Blob { cbData = data.Length, pbData = hIn.AddrOfPinnedObject() };
                var ent = new Blob { cbData = entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
                var output = new Blob();
                try
                {
                    bool ok = protect
                        ? CryptProtectData(ref input, "DepUp", ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                        : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
                    if (!ok || output.pbData == IntPtr.Zero) return null;
                    var r = new byte[output.cbData];
                    Marshal.Copy(output.pbData, r, 0, r.Length);
                    return r;
                }
                finally
                {
                    if (output.pbData != IntPtr.Zero) { if (output.cbData > 0) Marshal.Copy(new byte[output.cbData], 0, output.pbData, output.cbData); LocalFree(output.pbData); }
                    hIn.Free(); hEnt.Free();
                }
            }
        }
    }
}
