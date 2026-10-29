using System;
using System.Collections.Generic;
using System.Drawing;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Porting langsung dari ScanAi.kt:
    /// Deteksi dokumen murni C# tanpa library tambahan:
    /// - DetectCrop: segmentasi kertas (ambang Otsu + komponen terbesar BFS) lalu tepi dipertajam dengan gradien Sobel.
    /// - DetectSkew: kemiringan teks lewat profil proyeksi (baris teks paling tajam = lurus).
    /// </summary>
    public static class ScanAi
    {
        private const int SIDE = 320;

        private sealed class SmallImg
        {
            public int W { get; }
            public int H { get; }
            public int[] G { get; }
            public SmallImg(int w, int h, int[] g) { W = w; H = h; G = g; }
        }

        private static SmallImg MakeSmall(Bitmap src)
        {
            float s = (float)SIDE / Math.Max(src.Width, src.Height);
            int w = Math.Max(8, (int)(src.Width * Math.Min(1f, s)));
            int h = Math.Max(8, (int)(src.Height * Math.Min(1f, s)));
            using var bm = new Bitmap(w, h);
            using (var gr = Graphics.FromImage(bm))
            {
                gr.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                gr.DrawImage(src, 0, 0, w, h);
            }
            int[] g = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color c = bm.GetPixel(x, y);
                    g[y * w + x] = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                }
            }
            return new SmallImg(w, h, Blur3x3(g, w, h));
        }

        private static int[] Blur3x3(int[] g, int w, int h)
        {
            int[] o = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w) continue;
                            sum += g[yy * w + xx];
                            n++;
                        }
                    }
                    o[y * w + x] = sum / Math.Max(1, n);
                }
            }
            return o;
        }

        private static int Otsu(int[] g)
        {
            int[] hist = new int[256];
            foreach (int v in g) hist[Math.Clamp(v, 0, 255)]++;
            int total = g.Length;
            long sumAll = 0;
            for (int i = 0; i < 256; i++) sumAll += (long)i * hist[i];
            int wB = 0;
            long sumB = 0;
            double best = 0.0;
            int thr = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;
                sumB += (long)t * hist[t];
                double mB = (double)sumB / wB;
                double mF = (double)(sumAll - sumB) / wF;
                double v = (double)wB * wF * (mB - mF) * (mB - mF);
                if (v > best) { best = v; thr = t; }
            }
            return thr;
        }

        public static RectangleF? DetectCrop(Bitmap src)
        {
            var s = MakeSmall(src);
            int w = s.W, h = s.H;
            int thr = Otsu(s.G);

            int borderBright = 0, borderTotal = 0;
            void CountBorder(int x, int y) { borderTotal++; if (s.G[y * w + x] > thr) borderBright++; }
            for (int x = 0; x < w; x++) { CountBorder(x, 0); CountBorder(x, h - 1); }
            for (int y = 1; y < h - 1; y++) { CountBorder(0, y); CountBorder(w - 1, y); }
            bool docIsBright = borderBright * 2 < borderTotal;

            bool[] mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = (s.G[i] > thr) == docIsBright;

            int[] label = new int[w * h];
            int bestId = 0, bestSize = 0, id = 0;
            int[] queue = new int[w * h];

            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i] || label[i] != 0) continue;
                id++;
                int head = 0, tail = 0;
                queue[tail++] = i;
                label[i] = id;
                int size = 0;
                while (head < tail)
                {
                    int p = queue[head++];
                    size++;
                    int x = p % w, y = p / w;
                    if (x > 0 && mask[p - 1] && label[p - 1] == 0) { label[p - 1] = id; queue[tail++] = p - 1; }
                    if (x < w - 1 && mask[p + 1] && label[p + 1] == 0) { label[p + 1] = id; queue[tail++] = p + 1; }
                    if (y > 0 && mask[p - w] && label[p - w] == 0) { label[p - w] = id; queue[tail++] = p - w; }
                    if (y < h - 1 && mask[p + w] && label[p + w] == 0) { label[p + w] = id; queue[tail++] = p + w; }
                }
                if (size > bestSize) { bestSize = size; bestId = id; }
            }
            if (bestId == 0) return null;

            int l = w, t = h, r = 0, b = 0;
            for (int i = 0; i < label.Length; i++)
            {
                if (label[i] == bestId)
                {
                    int x = i % w, y = i / w;
                    if (x < l) l = x;
                    if (x > r) r = x;
                    if (y < t) t = y;
                    if (y > b) b = y;
                }
            }
            float area = (float)(r - l + 1) * (b - t + 1) / (w * h);
            if (area < 0.15f) return null;
            if (area > 0.97f) return new RectangleF(0f, 0f, 1f, 1f);

            int[] gx = new int[w * h];
            int[] gy = new int[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    gx[i] = Math.Abs(-s.G[i - w - 1] + s.G[i - w + 1] - 2 * s.G[i - 1] + 2 * s.G[i + 1] - s.G[i + w - 1] + s.G[i + w + 1]);
                    gy[i] = Math.Abs(-s.G[i - w - 1] - 2 * s.G[i - w] - s.G[i - w + 1] + s.G[i + w - 1] + 2 * s.G[i + w] + s.G[i + w + 1]);
                }
            }

            int win = Math.Max(3, (int)(Math.Max(w, h) * 0.04f));
            int RefineX(int x0)
            {
                int best = x0;
                long bs = -1;
                int minX = Math.Max(1, x0 - win);
                int maxX = Math.Min(w - 2, x0 + win);
                for (int x = minX; x <= maxX; x++)
                {
                    long sum = 0;
                    for (int y = t; y <= b; y++) sum += gx[y * w + x];
                    if (sum > bs) { bs = sum; best = x; }
                }
                return best;
            }
            int RefineY(int y0)
            {
                int best = y0;
                long bs = -1;
                int minY = Math.Max(1, y0 - win);
                int maxY = Math.Min(h - 2, y0 + win);
                for (int y = minY; y <= maxY; y++)
                {
                    long sum = 0;
                    for (int x = l; x <= r; x++) sum += gy[y * w + x];
                    if (sum > bs) { bs = sum; best = y; }
                }
                return best;
            }

            int rl = RefineX(l), rr = RefineX(r), rt = RefineY(t), rb = RefineY(b);
            if (rr - rl < w * 0.2f || rb - rt < h * 0.2f)
                return RectangleF.FromLTRB((float)l / w, (float)t / h, (r + 1f) / w, (b + 1f) / h);

            float pad = 0.004f;
            float left = Math.Clamp((float)rl / w - pad, 0f, 1f);
            float top = Math.Clamp((float)rt / h - pad, 0f, 1f);
            float right = Math.Clamp((rr + 1f) / w + pad, 0f, 1f);
            float bottom = Math.Clamp((rb + 1f) / h + pad, 0f, 1f);
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        public static float DetectSkew(Bitmap src, RectangleF? area = null)
        {
            var s = MakeSmall(src);
            int w = s.W, h = s.H;
            RectangleF a = area ?? new RectangleF(0f, 0f, 1f, 1f);
            float insetX = a.Width * 0.04f;
            float insetY = a.Height * 0.04f;
            int x0 = Math.Clamp((int)((a.Left + insetX) * w), 0, w - 1);
            int x1 = Math.Clamp((int)((a.Right - insetX) * w), x0 + 1, w);
            int y0 = Math.Clamp((int)((a.Top + insetY) * h), 0, h - 1);
            int y1 = Math.Clamp((int)((a.Bottom - insetY) * h), y0 + 1, h);

            long sum = 0;
            int n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { sum += s.G[y * w + x]; n++; }
            if (n < 400) return 0f;

            int mean = (int)(sum / n);
            var xs = new List<int>();
            var ys = new List<int>();
            int limit = mean * 80 / 100;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (s.G[y * w + x] < limit) { xs.Add(x); ys.Add(y); }
                }
            }
            if (xs.Count < 150 || xs.Count > n * 0.5f) return 0f;
            int step = Math.Max(1, xs.Count / 20000);

            float cx = (x0 + x1) / 2f;
            float cy = (y0 + y1) / 2f;
            float diag = MathF.Sqrt(w * w + h * h);
            int bins = (int)(diag * 1.2f) + 4;
            int[] hist = new int[bins];

            double Score(float deg)
            {
                Array.Clear(hist, 0, hist.Length);
                double rad = deg * Math.PI / 180.0;
                float sn = (float)Math.Sin(rad);
                float cs = (float)Math.Cos(rad);
                for (int i = 0; i < xs.Count; i += step)
                {
                    float dx = xs[i] - cx;
                    float dy = ys[i] - cy;
                    int v = (int)(-dx * sn + dy * cs + diag * 0.6f);
                    if (v >= 0 && v < bins) hist[v]++;
                }
                double sq = 0.0;
                foreach (int c in hist) sq += (double)c * c;
                return sq;
            }

            double baseSc = Score(0f);
            float bestDeg = 0f;
            double best = baseSc;
            for (float d = -10f; d <= 10f; d += 0.5f)
            {
                double sc = Score(d);
                if (sc > best) { best = sc; bestDeg = d; }
            }
            float fEnd = bestDeg + 0.5f;
            for (float f = bestDeg - 0.5f; f <= fEnd; f += 0.1f)
            {
                double sc = Score(f);
                if (sc > best) { best = sc; bestDeg = f; }
            }
            if (best < baseSc * 1.03 || Math.Abs(bestDeg) < 0.3f) return 0f;
            return -bestDeg;
        }
    }
}
