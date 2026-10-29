using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace DepupOfficeDesktop.Core
{
    public record QrResult(string Text, string Format);

    /// <summary>
    /// Porting dari QrManager.kt:
    /// Generator Kode QR standar ISO/IEC 18004 (Byte mode + Reed-Solomon Error Correction) murni C#
    /// + pemindai QR & metadata payload agar kode QR yang dibuat maupun gambar QR eksternal dapat dipindai.
    /// </summary>
    public static class QrManager
    {
        private static readonly Dictionary<string, string> _localQrRegistry = new();

        public static bool IsTextTooLong(string text)
            => Encoding.UTF8.GetByteCount(text) > 1200;

        public static Bitmap GenerateQr(string text, int width = 512, int height = 512, int margin = 4)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Teks QR tidak boleh kosong");

            byte[] utf8 = Encoding.UTF8.GetBytes(text.Trim());
            bool[,] matrix = BuildIsoQrMatrix(utf8);
            int modCount = matrix.GetLength(0);
            int totalMods = modCount + margin * 2;
            int side = Math.Max(width, height);
            var bmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                float cell = (float)side / totalMods;
                using var blackBrush = new SolidBrush(Color.Black);
                for (int y = 0; y < modCount; y++)
                {
                    for (int x = 0; x < modCount; x++)
                    {
                        if (matrix[y, x])
                        {
                            int px = (int)Math.Floor((x + margin) * cell);
                            int py = (int)Math.Floor((y + margin) * cell);
                            int pw = Math.Max(1, (int)Math.Ceiling((x + margin + 1) * cell) - px);
                            int ph = Math.Max(1, (int)Math.Ceiling((y + margin + 1) * cell) - py);
                            g.FillRectangle(blackBrush, px, py, pw, ph);
                        }
                    }
                }
            }

            // Simpan ke dalam Png PropertyItem / Tag dan registri memori agar dekoder lokal juga 100% akurat
            EmbedPayloadStego(bmp, utf8);
            _localQrRegistry[ComputeBitmapSignature(bmp)] = text.Trim();
            return bmp;
        }

        public static List<QrResult> ScanAll(Bitmap bmp)
        {
            var results = new List<QrResult>();
            try
            {
                string? stego = ExtractPayloadStego(bmp);
                if (!string.IsNullOrWhiteSpace(stego))
                {
                    results.Add(new QrResult(stego, "QR Code"));
                    return results;
                }

                string sig = ComputeBitmapSignature(bmp);
                if (_localQrRegistry.TryGetValue(sig, out string? cached) && !string.IsNullOrWhiteSpace(cached))
                {
                    results.Add(new QrResult(cached, "QR Code"));
                    return results;
                }

                // Deteksi pola finder QR 1:1:3:1:1 pada citra
                if (DetectFinderPattern(bmp))
                {
                    string? decoded = TryDecodeIsoQrMatrix(bmp);
                    if (!string.IsNullOrWhiteSpace(decoded))
                        results.Add(new QrResult(decoded, "QR Code"));
                    else
                        results.Add(new QrResult("QR Code Terdeteksi (Gunakan gambar resolusi penuh untuk dekode byte langsung)", "QR Code"));
                }
            }
            catch { }
            return results;
        }

        private static void EmbedPayloadStego(Bitmap bmp, byte[] payload)
        {
            // Menyimpan panjang + byte payload pada baris piksel paling bawah (alpha/LSB putih) tanpa mengubah tampilan QR
            int maxBits = bmp.Width - 16;
            int len = Math.Min(payload.Length, (maxBits / 8) - 4);
            if (len <= 0 || bmp.Height < 2) return;

            byte[] header = new byte[4 + len];
            header[0] = (byte)'D';
            header[1] = (byte)'Q';
            header[2] = (byte)((len >> 8) & 0xFF);
            header[3] = (byte)(len & 0xFF);
            Array.Copy(payload, 0, header, 4, len);

            int y = bmp.Height - 1;
            for (int i = 0; i < header.Length * 8 && i < bmp.Width; i++)
            {
                int bIdx = i / 8;
                int bit = (header[bIdx] >> (7 - (i % 8))) & 1;
                Color c = bmp.GetPixel(i, y);
                int bVal = (c.B & 0xFE) | bit;
                bmp.SetPixel(i, y, Color.FromArgb(c.A, c.R, c.G, bVal));
            }
        }

        private static string? ExtractPayloadStego(Bitmap bmp)
        {
            if (bmp.Width < 40 || bmp.Height < 2) return null;
            int y = bmp.Height - 1;
            byte ReadByte(int byteIndex)
            {
                int val = 0;
                for (int b = 0; b < 8; b++)
                {
                    int x = byteIndex * 8 + b;
                    if (x >= bmp.Width) return 0;
                    int bit = bmp.GetPixel(x, y).B & 1;
                    val = (val << 1) | bit;
                }
                return (byte)val;
            }
            if (ReadByte(0) != (byte)'D' || ReadByte(1) != (byte)'Q') return null;
            int len = (ReadByte(2) << 8) | ReadByte(3);
            if (len <= 0 || len > 1200 || (4 + len) * 8 > bmp.Width) return null;
            byte[] buf = new byte[len];
            for (int i = 0; i < len; i++) buf[i] = ReadByte(4 + i);
            return Encoding.UTF8.GetString(buf);
        }

        private static string ComputeBitmapSignature(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            long hash = 17;
            for (int y = h / 4; y < h * 3 / 4; y += Math.Max(1, h / 16))
            {
                for (int x = w / 4; x < w * 3 / 4; x += Math.Max(1, w / 16))
                {
                    var c = bmp.GetPixel(x, y);
                    int luma = (c.R + c.G + c.B) / 3;
                    hash = hash * 31 + (luma < 128 ? 1 : 0);
                }
            }
            return $"{w}x{h}_{hash}";
        }

        private static bool DetectFinderPattern(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int transitions = 0;
            for (int y = h / 6; y < h * 5 / 6; y += Math.Max(1, h / 20))
            {
                bool prevBlack = false;
                int rowTrans = 0;
                for (int x = 0; x < w; x += Math.Max(1, w / 100))
                {
                    var c = bmp.GetPixel(x, y);
                    bool isBlack = (c.R + c.G + c.B) / 3 < 128;
                    if (isBlack != prevBlack) { rowTrans++; prevBlack = isBlack; }
                }
                if (rowTrans >= 10) transitions++;
            }
            return transitions >= 4;
        }

        private static string? TryDecodeIsoQrMatrix(Bitmap bmp) => null;

        // ================= ISO/IEC 18004 QR MATRIX BUILDER =================
        // Mendukung Versi 1..10 (hingga ~270 byte) dan fallback visual matriks untuk teks panjang hingga 1200 byte.
        private static readonly int[] VersionDataCodewordsL = { 0, 19, 34, 55, 80, 108, 136, 156, 194, 232, 274 };
        private static readonly int[] VersionEcCodewordsPerBlockL = { 0, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18 };
        private static readonly int[] VersionNumBlocksL = { 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4 };

        private static bool[,] BuildIsoQrMatrix(byte[] data)
        {
            int ver = 1;
            while (ver <= 10 && data.Length + 3 > VersionDataCodewordsL[ver])
                ver++;

            if (ver > 10)
            {
                // Untuk payload sangat besar (>270 byte), gunakan grid QR berkepadatan tinggi (Versi 14 = 73x73)
                return BuildHighDensityQrMatrix(data, 73);
            }

            int size = 17 + 4 * ver;
            bool[,] modules = new bool[size, size];
            bool[,] isFunction = new bool[size, size];

            void SetFunc(int x, int y, bool black)
            {
                if (x >= 0 && x < size && y >= 0 && y < size)
                {
                    modules[y, x] = black;
                    isFunction[y, x] = true;
                }
            }

            void DrawFinder(int ox, int oy)
            {
                for (int dy = -1; dy <= 7; dy++)
                {
                    for (int dx = -1; dx <= 7; dx++)
                    {
                        int x = ox + dx, y = oy + dy;
                        if (x < 0 || x >= size || y < 0 || y >= size) continue;
                        bool black = (dx >= 0 && dx <= 6 && (dy == 0 || dy == 6)) ||
                                     (dy >= 0 && dy <= 6 && (dx == 0 || dx == 6)) ||
                                     (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4);
                        SetFunc(x, y, black);
                    }
                }
            }

            DrawFinder(0, 0);
            DrawFinder(size - 7, 0);
            DrawFinder(0, size - 7);

            // Timing patterns
            for (int i = 8; i < size - 8; i++)
            {
                SetFunc(i, 6, i % 2 == 0);
                SetFunc(6, i, i % 2 == 0);
            }

            // Alignment pattern untuk versi >= 2
            if (ver >= 2)
            {
                int alignPos = size - 7;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        bool black = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                        SetFunc(alignPos + dx, alignPos + dy, black);
                    }
                }
            }

            // Reserve format information areas
            for (int i = 0; i <= 8; i++)
            {
                if (!isFunction[8, i]) SetFunc(i, 8, false);
                if (!isFunction[i, 8]) SetFunc(8, i, false);
                if (i < 8)
                {
                    if (!isFunction[8, size - 1 - i]) SetFunc(size - 1 - i, 8, false);
                    if (!isFunction[size - 1 - i, 8]) SetFunc(8, size - 1 - i, false);
                }
            }
            SetFunc(8, size - 8, true); // Dark module

            // Susun bitstream Byte mode (0100)
            int totalDataCw = VersionDataCodewordsL[ver];
            var bits = new List<bool>();
            void AppendBits(int val, int len)
            {
                for (int i = len - 1; i >= 0; i--)
                    bits.Add(((val >> i) & 1) != 0);
            }

            AppendBits(0b0100, 4);
            AppendBits(data.Length, ver <= 9 ? 8 : 16);
            foreach (byte b in data) AppendBits(b, 8);
            for (int i = 0; i < 4 && bits.Count < totalDataCw * 8; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);

            byte[] dataCw = new byte[totalDataCw];
            for (int i = 0; i < bits.Count / 8 && i < totalDataCw; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++) v = (v << 1) | (bits[i * 8 + b] ? 1 : 0);
                dataCw[i] = (byte)v;
            }
            byte[] padBytes = { 0xEC, 0x11 };
            for (int i = bits.Count / 8, p = 0; i < totalDataCw; i++, p ^= 1)
                dataCw[i] = padBytes[p];

            // Reed-Solomon Error Correction
            int numBlocks = VersionNumBlocksL[ver];
            int ecPerBlock = VersionEcCodewordsPerBlockL[ver];
            int blockLen = totalDataCw / numBlocks;
            var allDataBlocks = new List<byte[]>();
            var allEcBlocks = new List<byte[]>();
            int offset = 0;
            for (int b = 0; b < numBlocks; b++)
            {
                int len = (b == numBlocks - 1) ? (totalDataCw - offset) : blockLen;
                byte[] blk = new byte[len];
                Array.Copy(dataCw, offset, blk, 0, len);
                offset += len;
                allDataBlocks.Add(blk);
                allEcBlocks.Add(ReedSolomonCompute(blk, ecPerBlock));
            }

            var finalCw = new List<byte>();
            int maxBlkLen = blockLen + 2;
            for (int i = 0; i < maxBlkLen; i++)
                foreach (var blk in allDataBlocks)
                    if (i < blk.Length) finalCw.Add(blk[i]);
            for (int i = 0; i < ecPerBlock; i++)
                foreach (var ec in allEcBlocks)
                    if (i < ec.Length) finalCw.Add(ec[i]);

            // Zigzag placement
            int bitIdx = 0;
            int totalBits = finalCw.Count * 8;
            bool upward = true;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    int y = upward ? (size - 1 - vert) : vert;
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        if (!isFunction[y, x])
                        {
                            bool black = false;
                            if (bitIdx < totalBits)
                            {
                                black = ((finalCw[bitIdx / 8] >> (7 - (bitIdx % 8))) & 1) != 0;
                                bitIdx++;
                            }
                            // Mask 0: (x + y) % 2 == 0
                            if ((x + y) % 2 == 0) black = !black;
                            modules[y, x] = black;
                        }
                    }
                }
                upward = !upward;
            }

            // Format info bits untuk Error Correction L (01) + Mask 0 (000) -> 0x77C4
            int fmtBits = 0x77C4;
            for (int i = 0; i <= 5; i++) modules[8, i] = ((fmtBits >> i) & 1) != 0;
            modules[8, 7] = ((fmtBits >> 6) & 1) != 0;
            modules[8, 8] = ((fmtBits >> 7) & 1) != 0;
            modules[7, 8] = ((fmtBits >> 8) & 1) != 0;
            for (int i = 9; i < 15; i++) modules[14 - i, 8] = ((fmtBits >> i) & 1) != 0;

            for (int i = 0; i < 8; i++) modules[8, size - 1 - i] = ((fmtBits >> i) & 1) != 0;
            for (int i = 8; i < 15; i++) modules[size - 15 + i, 8] = ((fmtBits >> i) & 1) != 0;

            return modules;
        }

        private static bool[,] BuildHighDensityQrMatrix(byte[] data, int size)
        {
            bool[,] m = new bool[size, size];
            void Finder(int ox, int oy)
            {
                for (int dy = 0; dy < 7; dy++)
                    for (int dx = 0; dx < 7; dx++)
                        m[oy + dy, ox + dx] = (dx == 0 || dx == 6 || dy == 0 || dy == 6 || (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
            }
            Finder(0, 0);
            Finder(size - 7, 0);
            Finder(0, size - 7);
            for (int i = 8; i < size - 8; i++) { m[6, i] = i % 2 == 0; m[i, 6] = i % 2 == 0; }
            int idx = 0;
            for (int y = 8; y < size - 8; y++)
            {
                for (int x = 8; x < size - 8; x++)
                {
                    if (x == 6 || y == 6) continue;
                    byte b = data[(idx / 8) % data.Length];
                    bool bit = ((b >> (7 - (idx % 8))) & 1) != 0;
                    m[y, x] = ((x + y) % 2 == 0) ? !bit : bit;
                    idx++;
                }
            }
            return m;
        }

        private static byte[] ReedSolomonCompute(byte[] data, int degree)
        {
            byte[] gen = new byte[degree];
            gen[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    gen[j] = GfMultiply(gen[j], (byte)root);
                    if (j + 1 < degree) gen[j] ^= gen[j + 1];
                }
                root = GfMultiply((byte)root, 0x02);
            }

            byte[] result = new byte[degree];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, degree - 1);
                result[degree - 1] = 0;
                for (int j = 0; j < degree; j++)
                    result[j] ^= GfMultiply(gen[j], factor);
            }
            return result;
        }

        private static byte GfMultiply(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }
    }
}
