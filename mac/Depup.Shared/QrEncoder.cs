using System.Text;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Encoder QR Code standar ISO/IEC 18004 (mode byte/UTF-8, versi 1–40, koreksi error L/M/Q/H,
    /// pemilihan mask otomatis). Lintas platform: menghasilkan matriks bool[y, x] (true = modul hitam).
    /// Menggantikan QrManager versi Windows yang tidak sesuai standar untuk teks panjang.
    /// </summary>
    public static class QrEncoder
    {
        public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

        private static readonly int[] FormatBitsOf = { 1, 0, 3, 2 };

        private static readonly sbyte[,] EccPerBlock =
        {
            { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };

        private static readonly sbyte[,] NumBlocks =
        {
            { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };

        /// <summary>Batas aman teks (byte UTF-8) untuk ECC M di versi 40.</summary>
        public static int MaxBytes(Ecc ecc = Ecc.M) => DataCodewords(40, ecc) - 3;

        public static bool[,] Encode(string text, Ecc ecc = Ecc.M)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("Teks QR tidak boleh kosong");
            byte[] data = Encoding.UTF8.GetBytes(text);

            int ver = 1;
            for (; ver <= 40; ver++)
            {
                int ccBits = ver <= 9 ? 8 : 16;
                if (4 + ccBits + data.Length * 8 <= DataCodewords(ver, ecc) * 8) break;
            }
            if (ver > 40) throw new ArgumentException($"Teks terlalu panjang untuk QR (maks {MaxBytes(ecc)} byte).");

            // Bitstream mode byte
            var bits = new List<bool>();
            void Append(int val, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
            Append(0b0100, 4);
            Append(data.Length, ver <= 9 ? 8 : 16);
            foreach (byte b in data) Append(b, 8);

            int capacity = DataCodewords(ver, ecc) * 8;
            Append(0, Math.Min(4, capacity - bits.Count));
            Append(0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

            byte[] dataCw = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) dataCw[i >> 3] |= (byte)(1 << (7 - (i & 7)));

            var qr = new Builder(ver, ecc);
            qr.DrawFunctionPatterns();
            qr.DrawCodewords(AddEccAndInterleave(dataCw, ver, ecc));

            int bestMask = 0;
            long minPenalty = long.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                qr.ApplyMask(m);
                qr.DrawFormatBits(m);
                long p = qr.Penalty();
                if (p < minPenalty) { minPenalty = p; bestMask = m; }
                qr.ApplyMask(m); // XOR balik
            }
            qr.ApplyMask(bestMask);
            qr.DrawFormatBits(bestMask);
            return qr.Modules;
        }

        private static int RawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7) result -= 36;
            }
            return result;
        }

        private static int DataCodewords(int ver, Ecc ecc)
            => RawDataModules(ver) / 8 - EccPerBlock[(int)ecc, ver] * NumBlocks[(int)ecc, ver];

        private static byte[] AddEccAndInterleave(byte[] data, int ver, Ecc ecc)
        {
            int numBlocks = NumBlocks[(int)ecc, ver];
            int blockEccLen = EccPerBlock[(int)ecc, ver];
            int rawCodewords = RawDataModules(ver) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            byte[] divisor = RsDivisor(blockEccLen);
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                byte[] dat = data.AsSpan(k, datLen).ToArray();
                k += datLen;
                byte[] ecc2 = RsRemainder(dat, divisor);
                byte[] block = new byte[shortBlockLen + 1];
                Array.Copy(dat, 0, block, 0, datLen);
                // blok pendek: posisi datLen dibiarkan sebagai "lubang" (dilewati saat interleave)
                Array.Copy(ecc2, 0, block, block.Length - blockEccLen, blockEccLen);
                blocks.Add(block);
            }

            var result = new List<byte>(rawCodewords);
            for (int i = 0; i < blocks[0].Length; i++)
                for (int j = 0; j < blocks.Count; j++)
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                        result.Add(blocks[j][i]);
            return result.ToArray();
        }

        private static byte[] RsDivisor(int degree)
        {
            byte[] result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = GfMul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = GfMul(root, 0x02);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            byte[] result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= GfMul(divisor[i], factor);
            }
            return result;
        }

        private static byte GfMul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private sealed class Builder
        {
            private readonly int _ver;
            private readonly Ecc _ecc;
            private readonly int _size;
            public bool[,] Modules { get; }
            private readonly bool[,] _isFunc;

            public Builder(int ver, Ecc ecc)
            {
                _ver = ver;
                _ecc = ecc;
                _size = ver * 4 + 17;
                Modules = new bool[_size, _size];
                _isFunc = new bool[_size, _size];
            }

            private void Set(int x, int y, bool dark)
            {
                Modules[y, x] = dark;
                _isFunc[y, x] = true;
            }

            public void DrawFunctionPatterns()
            {
                for (int i = 0; i < _size; i++)
                {
                    Set(6, i, i % 2 == 0);
                    Set(i, 6, i % 2 == 0);
                }
                DrawFinder(3, 3);
                DrawFinder(_size - 4, 3);
                DrawFinder(3, _size - 4);

                int[] align = AlignmentPositions();
                int n = align.Length;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))
                            for (int dy = -2; dy <= 2; dy++)
                                for (int dx = -2; dx <= 2; dx++)
                                    Set(align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);

                DrawFormatBits(0);
                DrawVersion();
            }

            private void DrawFinder(int x, int y)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && xx < _size && yy >= 0 && yy < _size)
                            Set(xx, yy, dist != 2 && dist != 4);
                    }
            }

            private int[] AlignmentPositions()
            {
                if (_ver == 1) return Array.Empty<int>();
                int numAlign = _ver / 7 + 2;
                int step = (_ver * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
                int[] result = new int[numAlign];
                result[0] = 6;
                for (int i = numAlign - 1, pos = _size - 7; i >= 1; i--, pos -= step) result[i] = pos;
                return result;
            }

            public void DrawFormatBits(int mask)
            {
                int data = FormatBitsOf[(int)_ecc] << 3 | mask;
                int rem = data;
                for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                int bits = (data << 10 | rem) ^ 0x5412;
                bool Bit(int i) => ((bits >> i) & 1) != 0;

                for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
                Set(8, 7, Bit(6));
                Set(8, 8, Bit(7));
                Set(7, 8, Bit(8));
                for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

                for (int i = 0; i < 8; i++) Set(_size - 1 - i, 8, Bit(i));
                for (int i = 8; i < 15; i++) Set(8, _size - 15 + i, Bit(i));
                Set(8, _size - 8, true);
            }

            private void DrawVersion()
            {
                if (_ver < 7) return;
                int rem = _ver;
                for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                long bits = (long)_ver << 12 | (long)rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = _size - 11 + i % 3, b = i / 3;
                    Set(a, b, bit);
                    Set(b, a, bit);
                }
            }

            public void DrawCodewords(byte[] data)
            {
                int i = 0;
                for (int right = _size - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vert = 0; vert < _size; vert++)
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            bool upward = ((right + 1) & 2) == 0;
                            int y = upward ? _size - 1 - vert : vert;
                            if (!_isFunc[y, x] && i < data.Length * 8)
                            {
                                Modules[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                                i++;
                            }
                        }
                }
            }

            public void ApplyMask(int mask)
            {
                for (int y = 0; y < _size; y++)
                    for (int x = 0; x < _size; x++)
                    {
                        bool invert = mask switch
                        {
                            0 => (x + y) % 2 == 0,
                            1 => y % 2 == 0,
                            2 => x % 3 == 0,
                            3 => (x + y) % 3 == 0,
                            4 => (x / 3 + y / 2) % 2 == 0,
                            5 => x * y % 2 + x * y % 3 == 0,
                            6 => (x * y % 2 + x * y % 3) % 2 == 0,
                            _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                        };
                        if (invert && !_isFunc[y, x]) Modules[y, x] = !Modules[y, x];
                    }
            }

            /// <summary>Skor penalti (aturan 1, 2, 4 ISO) untuk memilih mask yang paling mudah dipindai.</summary>
            public long Penalty()
            {
                long result = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int a = 0; a < _size; a++)
                    {
                        bool color = false;
                        int run = 0;
                        for (int b = 0; b < _size; b++)
                        {
                            bool m = pass == 0 ? Modules[a, b] : Modules[b, a];
                            if (b == 0 || m != color) { color = m; run = 1; }
                            else
                            {
                                run++;
                                if (run == 5) result += 3;
                                else if (run > 5) result++;
                            }
                        }
                    }
                for (int y = 0; y < _size - 1; y++)
                    for (int x = 0; x < _size - 1; x++)
                    {
                        bool c = Modules[y, x];
                        if (c == Modules[y, x + 1] && c == Modules[y + 1, x] && c == Modules[y + 1, x + 1]) result += 3;
                    }
                int dark = 0;
                foreach (bool m in Modules) if (m) dark++;
                int total = _size * _size;
                int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
                result += Math.Max(0, k) * 10;
                return result;
            }
        }
    }
}
