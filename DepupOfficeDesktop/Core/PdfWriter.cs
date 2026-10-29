using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    public record OcrLine(string Text, Rectangle Box);
    public record OcrPage(string Text, List<string> Paras, List<OcrLine> Lines, int Width, int Height);

    /// <summary>
    /// Porting langsung dari PdfWriter.kt:
    /// Penulis PDF 1.6 mini: tiap halaman = gambar JPEG (DCTDecode) + lapisan teks tak terlihat dari OCR (BT 3 Tr)
    /// + enkripsi AES-128 (Standard V4/R4 AESV2) opsional.
    /// </summary>
    public sealed class PdfWriter : IDisposable
    {
        private static readonly byte[] PAD = new byte[]
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private readonly string _outPath;
        private readonly int _pageCount;
        private readonly FileStream _fs;
        private readonly Dictionary<int, long> _offsets = new();
        private readonly byte[] _fileId = new byte[16];
        private byte[]? _key;
        private byte[] _oEntry = Array.Empty<byte>();
        private byte[] _uEntry = Array.Empty<byte>();
        private int _added = 0;
        private bool _finished = false;

        public PdfWriter(string outPath, int pageCount, string? password = null)
        {
            _outPath = outPath;
            _pageCount = pageCount;
            RandomNumberGenerator.Fill(_fileId);
            _fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

            if (!string.IsNullOrEmpty(password))
                SetupEncryption(password);

            WriteStr("%PDF-1.6\n%âãÏÓ\n");
        }

        private static int PageObj(int i) => 5 + 3 * i;

        private void WriteBytes(byte[] b) => _fs.Write(b, 0, b.Length);
        private void WriteStr(string s) => WriteBytes(Latin1.GetBytes(s));

        private static byte[] Md5Hash(params byte[][] parts)
        {
            using var md5 = MD5.Create();
            foreach (var p in parts)
                md5.TransformBlock(p, 0, p.Length, null, 0);
            md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return md5.Hash!;
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            int[] s = Enumerable.Range(0, 256).ToArray();
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 255;
                (s[i], s[j]) = (s[j], s[i]);
            }
            byte[] o = new byte[data.Length];
            int a = 0, b = 0;
            for (int n = 0; n < data.Length; n++)
            {
                a = (a + 1) & 255;
                b = (b + s[a]) & 255;
                (s[a], s[b]) = (s[b], s[a]);
                o[n] = (byte)(data[n] ^ s[(s[a] + s[b]) & 255]);
            }
            return o;
        }

        private static byte[] PadPw(byte[] pw)
        {
            byte[] combined = new byte[32];
            int copyLen = Math.Min(pw.Length, 32);
            Array.Copy(pw, 0, combined, 0, copyLen);
            if (copyLen < 32)
                Array.Copy(PAD, 0, combined, copyLen, 32 - copyLen);
            return combined;
        }

        private void SetupEncryption(string password)
        {
            byte[] rawPw = password.All(c => c < 256) ? Latin1.GetBytes(password) : Encoding.UTF8.GetBytes(password);
            byte[] pw = PadPw(rawPw);
            int p = -4;
            byte[] pBytes = new byte[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) };

            byte[] h = Md5Hash(pw);
            for (int r = 0; r < 50; r++) h = Md5Hash(h);
            byte[] oKey = h.Take(16).ToArray();
            byte[] o = Rc4(oKey, pw);
            for (int i = 1; i <= 19; i++)
            {
                byte[] xk = oKey.Select(b => (byte)(b ^ i)).ToArray();
                o = Rc4(xk, o);
            }

            byte[] k = Md5Hash(pw, o, pBytes, _fileId);
            for (int r = 0; r < 50; r++) k = Md5Hash(k.Take(16).ToArray());
            byte[] fileKey = k.Take(16).ToArray();

            byte[] u = Rc4(fileKey, Md5Hash(PAD, _fileId));
            for (int i = 1; i <= 19; i++)
            {
                byte[] xk = fileKey.Select(b => (byte)(b ^ i)).ToArray();
                u = Rc4(xk, u);
            }

            _key = fileKey;
            _oEntry = o;
            _uEntry = u.Concat(new byte[16]).ToArray();
        }

        private byte[] Encrypt(int objNum, byte[] data)
        {
            if (_key == null) return data;
            byte[] ok = Md5Hash(
                _key,
                new byte[] { (byte)objNum, (byte)(objNum >> 8), (byte)(objNum >> 16), 0, 0 },
                Latin1.GetBytes("sAlT")
            ).Take(16).ToArray();

            byte[] iv = new byte[16];
            RandomNumberGenerator.Fill(iv);
            using var aes = Aes.Create();
            aes.Key = ok;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var enc = aes.CreateEncryptor();
            byte[] cipherBytes = enc.TransformFinalBlock(data, 0, data.Length);
            byte[] result = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, iv.Length, cipherBytes.Length);
            return result;
        }

        private void BeginObj(int n)
        {
            _offsets[n] = _fs.Position;
            WriteStr($"{n} 0 obj\n");
        }

        private void EndObj() => WriteStr("\nendobj\n");

        private void WriteStream(int n, string dict, byte[] data)
        {
            byte[] d = Encrypt(n, data);
            BeginObj(n);
            WriteStr($"<< {dict} /Length {d.Length} >>\nstream\n");
            WriteBytes(d);
            WriteStr("\nendstream");
            EndObj();
        }

        private static string Hex(byte[] b) => "<" + string.Concat(b.Select(x => x.ToString("x2"))) + ">";
        private static string Fmt(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static string WinAnsi(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                char ch = (c >= 32 && c <= 255) ? c : (c == '\t' ? ' ' : '?');
                if (ch is '\\' or '(' or ')')
                    sb.Append('\\').Append(ch);
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        public void AddPage(
            byte[] jpeg, int jpegW, int jpegH, float pageW, float pageH,
            float imgX, float imgY, float imgW, float imgH, OcrPage? ocr = null)
        {
            int i = _added++;
            int pg = PageObj(i);
            int ct = pg + 1;
            int im = pg + 2;

            WriteStream(im, $"/Type /XObject /Subtype /Image /Width {jpegW} /Height {jpegH} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", jpeg);

            var sb = new StringBuilder();
            sb.Append($"q {Fmt(imgW)} 0 0 {Fmt(imgH)} {Fmt(imgX)} {Fmt(pageH - imgY - imgH)} cm /Im0 Do Q\n");
            if (ocr != null && ocr.Lines.Count > 0 && jpegW > 0 && jpegH > 0)
            {
                float sx = imgW / jpegW;
                float sy = imgH / jpegH;
                sb.Append("BT 3 Tr\n");
                foreach (var ln in ocr.Lines)
                {
                    float fs = Math.Clamp(ln.Box.Height * sy * 0.85f, 4f, 72f);
                    float natural = Math.Max(1f, ln.Text.Length * 0.52f * fs);
                    float boxW = ln.Box.Width * sx;
                    float tz = natural > 0f ? Math.Clamp(boxW / natural * 100f, 20f, 400f) : 100f;
                    float x = imgX + ln.Box.Left * sx;
                    float y = pageH - (imgY + ln.Box.Bottom * sy) + fs * 0.2f;
                    sb.Append($"/F1 {Fmt(fs)} Tf {Fmt(tz)} Tz 1 0 0 1 {Fmt(x)} {Fmt(y)} Tm ({WinAnsi(ln.Text)}) Tj\n");
                }
                sb.Append("ET\n");
            }
            WriteStream(ct, "", Latin1.GetBytes(sb.ToString()));

            BeginObj(pg);
            WriteStr($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Fmt(pageW)} {Fmt(pageH)}] " +
                     $"/Resources << /XObject << /Im0 {im} 0 R >> /Font << /F1 3 0 R >> >> /Contents {ct} 0 R >>");
            EndObj();
        }

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_added != _pageCount)
                throw new InvalidOperationException("Jumlah halaman tidak cocok");

            BeginObj(1);
            WriteStr("<< /Type /Catalog /Pages 2 0 R >>");
            EndObj();

            BeginObj(2);
            string kids = string.Join(" ", Enumerable.Range(0, _pageCount).Select( idx => $"{PageObj(idx)} 0 R"));
            WriteStr($"<< /Type /Pages /Count {_pageCount} /Kids [{kids}] >>");
            EndObj();

            BeginObj(3);
            WriteStr("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            EndObj();

            bool enc = _key != null;
            if (enc)
            {
                BeginObj(4);
                WriteStr($"<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /AuthEvent /DocOpen /CFM /AESV2 /Length 16 >> >> " +
                         $"/StmF /StdCF /StrF /StdCF /O {Hex(_oEntry)} /U {Hex(_uEntry)} /P -4 >>");
                EndObj();
            }
            else
            {
                BeginObj(4);
                WriteStr("<< >>");
                EndObj();
            }

            int total = 5 + 3 * _pageCount;
            long xref = _fs.Position;
            WriteStr($"xref\n0 {total}\n0000000000 65535 f \n");
            for (int n = 1; n < total; n++)
            {
                long off = _offsets.TryGetValue(n, out long v) ? v : 0L;
                WriteStr(off.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            }
            WriteStr($"trailer\n<< /Size {total} /Root 1 0 R " + (enc ? "/Encrypt 4 0 R " : "") +
                     $"/ID [{Hex(_fileId)}{Hex(_fileId)}] >>\nstartxref\n{xref}\n%%EOF\n");
            _fs.Flush();
            _fs.Dispose();
        }

        public void Abort()
        {
            _finished = true;
            try { _fs.Dispose(); } catch { }
            try { if (File.Exists(_outPath)) File.Delete(_outPath); } catch { }
        }

        public void Dispose()
        {
            if (!_finished)
            {
                try { _fs.Dispose(); } catch { }
            }
        }
    }
}
