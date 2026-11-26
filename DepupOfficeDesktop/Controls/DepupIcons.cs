using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Ikon DepUp Office yang SAMA PERSIS dengan aplikasi Android/iOS: path vektor <c>res/drawable/ic_*.xml</c> disalin apa
    /// adanya (<see cref="DepupIcons"/>.g.cs, dibuat oleh tools/icons/gen_icons.py) lalu digambar dengan GDI+ — tajam di
    /// semua DPI. Path berwarna #111111 diwarnai ulang (tint) seperti di Android.
    /// </summary>
    internal static partial class DepupIcons
    {
        internal sealed record P(string D, bool EvenOdd, string? Color, float[] M);
        internal sealed record Vec(float W, float H, P[] Paths);

        private static readonly Dictionary<string, GraphicsPath[]> Cache = new();
        private static readonly object Gate = new();

        public static bool Has(string name) => Data.ContainsKey(name);

        /// <summary>Gambar ikon <paramref name="name"/> (tanpa awalan ic_) pas di <paramref name="box"/>.</summary>
        public static void Draw(Graphics g, string name, RectangleF box, Color tint)
        {
            if (!Data.TryGetValue(name, out var v)) return;
            GraphicsPath[] paths;
            lock (Gate)
            {
                if (!Cache.TryGetValue(name, out paths!))
                {
                    paths = new GraphicsPath[v.Paths.Length];
                    for (int i = 0; i < paths.Length; i++)
                    {
                        var p = v.Paths[i];
                        var gp = Parse(p.D);
                        gp.FillMode = p.EvenOdd ? FillMode.Alternate : FillMode.Winding;
                        if (p.M is not [1, 0, 0, 1, 0, 0])
                            using (var mx = new Matrix(p.M[0], p.M[1], p.M[2], p.M[3], p.M[4], p.M[5])) gp.Transform(mx);
                        paths[i] = gp;
                    }
                    Cache[name] = paths;
                }
            }
            float k = Math.Min(box.Width / v.W, box.Height / v.H);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(box.X + (box.Width - v.W * k) / 2f, box.Y + (box.Height - v.H * k) / 2f);
            g.ScaleTransform(k, k);
            for (int i = 0; i < paths.Length; i++)
            {
                var c = v.Paths[i].Color is { } hex ? FromHex(hex) : tint;
                using var b = new SolidBrush(c);
                g.FillPath(b, paths[i]);
            }
            g.Restore(state);
        }

        /// <summary>Render ke bitmap transparan (ikon jendela, gambar menu).</summary>
        public static Bitmap Render(string name, int size, Color tint)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            Draw(g, name, new RectangleF(0, 0, size, size), tint);
            return bmp;
        }

        private static Color FromHex(string hex)
        {
            uint v = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber);
            return Color.FromArgb((int)v);
        }

        // ------------------------------------------------------------------ parser pathData (sintaks SVG)

        private static GraphicsPath Parse(string d)
        {
            var gp = new GraphicsPath();
            int i = 0;
            char cmd = ' ';
            float cx = 0, cy = 0, sx = 0, sy = 0, lcx = 0, lcy = 0;   // titik kini, awal sub-path, kendali terakhir
            char prev = ' ';
            bool open = false;

            void Skip() { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; }
            bool HasNum() { Skip(); return i < d.Length && (char.IsDigit(d[i]) || d[i] is '-' or '+' or '.'); }
            float Num()
            {
                Skip();
                int s = i;
                if (i < d.Length && d[i] is '-' or '+') i++;
                bool dot = false, exp = false;
                while (i < d.Length)
                {
                    char ch = d[i];
                    if (char.IsDigit(ch)) { i++; continue; }
                    if (ch == '.' && !dot && !exp) { dot = true; i++; continue; }
                    if ((ch == 'e' || ch == 'E') && !exp) { exp = true; i++; if (i < d.Length && d[i] is '-' or '+') i++; continue; }
                    break;
                }
                return float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            bool Flag() { Skip(); char ch = d[i++]; return ch == '1'; }
            void Begin() { if (!open) { gp.StartFigure(); open = true; } }

            while (true)
            {
                Skip();
                if (i >= d.Length) break;
                if (char.IsLetter(d[i]) && d[i] is not ('e' or 'E')) cmd = d[i++];
                else if (cmd == ' ') { i++; continue; }
                bool rel = char.IsLower(cmd);
                float ox = rel ? cx : 0, oy = rel ? cy : 0;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        float x = Num() + ox, y = Num() + oy;
                        if (open) gp.StartFigure();
                        open = true;
                        cx = sx = x; cy = sy = y;
                        cmd = rel ? 'l' : 'L';   // pasangan angka berikutnya = garis
                        lcx = cx; lcy = cy; prev = 'M';
                        continue;
                    }
                    case 'L':
                    {
                        float x = Num() + ox, y = Num() + oy;
                        Begin(); gp.AddLine(cx, cy, x, y); cx = x; cy = y; break;
                    }
                    case 'H': { float x = Num() + ox; Begin(); gp.AddLine(cx, cy, x, cy); cx = x; break; }
                    case 'V': { float y = Num() + oy; Begin(); gp.AddLine(cx, cy, cx, y); cy = y; break; }
                    case 'C':
                    {
                        float x1 = Num() + ox, y1 = Num() + oy, x2 = Num() + ox, y2 = Num() + oy, x = Num() + ox, y = Num() + oy;
                        Begin(); gp.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y; prev = 'C'; goto nextNoReset;
                    }
                    case 'S':
                    {
                        float x1 = prev == 'C' ? 2 * cx - lcx : cx, y1 = prev == 'C' ? 2 * cy - lcy : cy;
                        float x2 = Num() + ox, y2 = Num() + oy, x = Num() + ox, y = Num() + oy;
                        Begin(); gp.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                        lcx = x2; lcy = y2; cx = x; cy = y; prev = 'C'; goto nextNoReset;
                    }
                    case 'Q':
                    {
                        float qx = Num() + ox, qy = Num() + oy, x = Num() + ox, y = Num() + oy;
                        Quad(qx, qy, x, y); lcx = qx; lcy = qy; cx = x; cy = y; prev = 'Q'; goto nextNoReset;
                    }
                    case 'T':
                    {
                        float qx = prev == 'Q' ? 2 * cx - lcx : cx, qy = prev == 'Q' ? 2 * cy - lcy : cy;
                        float x = Num() + ox, y = Num() + oy;
                        Quad(qx, qy, x, y); lcx = qx; lcy = qy; cx = x; cy = y; prev = 'Q'; goto nextNoReset;
                    }
                    case 'A':
                    {
                        float rx = Num(), ry = Num(), rot = Num();
                        bool large = Flag(), sweep = Flag();
                        float x = Num() + ox, y = Num() + oy;
                        Begin(); Arc(cx, cy, rx, ry, rot, large, sweep, x, y); cx = x; cy = y; break;
                    }
                    case 'Z':
                        if (open) gp.CloseFigure();
                        open = false; cx = sx; cy = sy; prev = 'Z';
                        continue;
                    default: i++; continue;
                }
                prev = char.ToUpperInvariant(cmd);
            nextNoReset:
                if (!HasNum() && i < d.Length && !char.IsLetter(d[i])) i++;
            }
            return gp;

            void Quad(float qx, float qy, float x, float y)
            {
                Begin();
                gp.AddBezier(cx, cy, cx + 2f / 3f * (qx - cx), cy + 2f / 3f * (qy - cy), x + 2f / 3f * (qx - x), y + 2f / 3f * (qy - y), x, y);
            }

            // Busur elips SVG → kurva Bezier (W3C SVG implementation notes F.6)
            void Arc(float x1, float y1, float rx, float ry, float rotDeg, bool large, bool sweep, float x2, float y2)
            {
                if (rx == 0 || ry == 0) { gp.AddLine(x1, y1, x2, y2); return; }
                double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
                double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
                double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
                double rxs = Math.Abs(rx), rys = Math.Abs(ry);
                double lam = x1p * x1p / (rxs * rxs) + y1p * y1p / (rys * rys);
                if (lam > 1) { rxs *= Math.Sqrt(lam); rys *= Math.Sqrt(lam); }
                double num = rxs * rxs * rys * rys - rxs * rxs * y1p * y1p - rys * rys * x1p * x1p;
                double den = rxs * rxs * y1p * y1p + rys * rys * x1p * x1p;
                double co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
                double cxp = co * rxs * y1p / rys, cyp = -co * rys * x1p / rxs;
                double ccx = cos * cxp - sin * cyp + (x1 + x2) / 2.0, ccy = sin * cxp + cos * cyp + (y1 + y2) / 2.0;
                double Ang(double ux, double uy, double vx, double vy)
                {
                    double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                    return a;
                }
                double t1 = Ang(1, 0, (x1p - cxp) / rxs, (y1p - cyp) / rys);
                double dt = Ang((x1p - cxp) / rxs, (y1p - cyp) / rys, (-x1p - cxp) / rxs, (-y1p - cyp) / rys);
                if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
                int segs = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
                double step = dt / segs, kk = 4.0 / 3.0 * Math.Tan(step / 4);
                double px = x1, py = y1;
                for (int s = 0; s < segs; s++)
                {
                    double a1 = t1 + s * step, a2 = a1 + step;
                    double c1x = Math.Cos(a1) - kk * Math.Sin(a1), c1y = Math.Sin(a1) + kk * Math.Cos(a1);
                    double c2x = Math.Cos(a2) + kk * Math.Sin(a2), c2y = Math.Sin(a2) - kk * Math.Cos(a2);
                    double ex = Math.Cos(a2), ey = Math.Sin(a2);
                    PointF T(double ux, double uy) => new((float)(cos * rxs * ux - sin * rys * uy + ccx), (float)(sin * rxs * ux + cos * rys * uy + ccy));
                    var e = s == segs - 1 ? new PointF(x2, y2) : T(ex, ey);
                    gp.AddBezier(new PointF((float)px, (float)py), T(c1x, c1y), T(c2x, c2y), e);
                    px = e.X; py = e.Y;
                }
            }
        }
    }
}
