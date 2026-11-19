using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Penafsir isi halaman PDF (port PdfLayout.kt Android v4.1): operator gambar PDF → unsur halaman yang bisa disusun
    /// ulang jadi dokumen Word — huruf (posisi, font, ukuran, warna, tebal/miring), foto (JPEG asli / PNG), dan bentuk vektor
    /// (garis, kotak, lingkaran, poligon; juga untuk mengenali tabel). Koordinat keluaran: poin, pojok kiri-ATAS halaman.
    /// </summary>
    public static class PdfLayout
    {
        public sealed record FontInfo(string Family, bool Bold, bool Italic, bool Mono, bool Serif);

        /// <summary>Satu huruf/glif. Y = garis dasar; Size = ukuran tampak (pt). Invisible = mode render 3 (teks OCR tersembunyi).</summary>
        public sealed class Glyph
        {
            public readonly float X, Y, W, Size, Angle; public readonly string Text; public readonly FontInfo Font; public readonly int Color; public readonly bool Invisible, Upright;
            public Glyph(float x, float y, float w, float size, string text, FontInfo font, int color, bool invisible, bool upright, float angle = 0)
            { X = x; Y = y; W = w; Size = size; Text = text; Font = font; Color = color; Invisible = invisible; Upright = upright; Angle = angle; }
        }

        /// <summary>Foto di halaman. Data JPEG/PNG baru didekode saat pertama diminta (gambar latar yang dibuang tidak pernah didekode).
        /// Key = aliran gambar asal (gambar sama di beberapa halaman → satu media).</summary>
        public sealed class Image
        {
            public readonly float X0, Y0, X1, Y1; public readonly object? Key;
            private Func<(byte[]? Jpeg, byte[]? Png, int W, int H)?>? _load;
            private byte[]? _jpeg, _png; private int _w, _h;
            public Image(float x0, float y0, float x1, float y1, byte[]? jpeg, byte[]? png, int pxW, int pxH)
            { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; _jpeg = jpeg; _png = png; _w = pxW; _h = pxH; }
            public Image(float x0, float y0, float x1, float y1, object key, Func<(byte[]? Jpeg, byte[]? Png, int W, int H)?> load)
            { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Key = key; _load = load; }
            private void Load()
            {
                var l = _load; if (l == null) return;
                _load = null;
                (byte[]? Jpeg, byte[]? Png, int W, int H)? r = null;
                try { r = l(); } catch { }
                if (r is { } v) { _jpeg = v.Jpeg; _png = v.Png; _w = v.W; _h = v.H; }
            }
            public byte[]? Jpeg { get { Load(); return _jpeg; } }
            public byte[]? Png { get { Load(); return _png; } }
            public int PxW { get { Load(); return _w; } }
            public int PxH { get { Load(); return _h; } }
        }

        public const int NONE = 0;

        public sealed class Shape
        {
            public readonly List<float[]> Subs; public readonly List<bool> Closed; public readonly int Fill, Stroke; public readonly float LineW; public readonly bool IsRect;
            private float[]? _bbox;
            public Shape(List<float[]> subs, List<bool> closed, int fill, int stroke, float lineW, bool isRect) { Subs = subs; Closed = closed; Fill = fill; Stroke = stroke; LineW = lineW; IsRect = isRect; }
            public float[] BBox
            {
                get
                {
                    if (_bbox != null) return _bbox;
                    float a = float.MaxValue, b = float.MaxValue, c = -float.MaxValue, d = -float.MaxValue;
                    foreach (var s in Subs) for (int i = 0; i + 1 < s.Length; i += 2) { a = Math.Min(a, s[i]); c = Math.Max(c, s[i]); b = Math.Min(b, s[i + 1]); d = Math.Max(d, s[i + 1]); }
                    return _bbox = new[] { a, b, c, d };
                }
            }
        }

        public sealed record PageOut(float W, float H, List<Glyph> Glyphs, List<Image> Images, List<Shape> Shapes);

        private const int MAX_OPS = 2_000_000;

        public static PageOut Page(PdfFile f, PdfFile.Page p)
        {
            float bw = p.Box[2] - p.Box[0], bh = p.Box[3] - p.Box[1];
            var b = p.Rotate switch
            {
                90 => new M(0, 1, 1, 0, -p.Box[1], -p.Box[0]),
                180 => new M(-1, 0, 0, 1, p.Box[2], -p.Box[1]),
                270 => new M(0, -1, -1, 0, p.Box[3], p.Box[2]),
                _ => new M(1, 0, 0, -1, -p.Box[0], p.Box[3])
            };
            var (ow, oh) = p.Rotate is 90 or 270 ? (bh, bw) : (bw, bh);
            var r = new Run(f, ow, oh);
            r.Exec(f.Contents(p), p.Resources, b, 0);
            return new PageOut(ow, oh, r.Glyphs, r.Images, r.Shapes);
        }

        internal static string ReadText(PdfFile file, PdfFile.Page page)
        {
            var text = new StringBuilder();
            Glyph? previous = null;
            foreach (var glyph in Page(file, page).Glyphs)
            {
                if (glyph.Text.Length == 0) continue;
                if (previous != null && text.Length > 0 && !char.IsWhiteSpace(text[text.Length - 1]))
                {
                    float tolerance = Math.Max(2f, Math.Min(previous.Size, glyph.Size) * 0.45f);
                    if (Math.Abs(glyph.Y - previous.Y) > tolerance || glyph.X < previous.X - tolerance)
                        text.AppendLine();
                    else if (glyph.X - previous.X - previous.W > tolerance * 0.6f)
                        text.Append(' ');
                }
                text.Append(glyph.Text);
                previous = glyph;
            }
            return text.ToString().Trim();
        }

        // ------------------------------------------------------------------ matriks
        public sealed class M
        {
            public readonly float A, B, C, D, E, F;
            public M(float a, float b, float c, float d, float e, float f) { A = a; B = b; C = c; D = d; E = e; F = f; }
            public M Times(M o) => new(A * o.A + B * o.C, A * o.B + B * o.D, C * o.A + D * o.C, C * o.B + D * o.D, E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);
            public float X(float px, float py) => px * A + py * C + E;
            public float Y(float px, float py) => px * B + py * D + F;
            public float ScaleY() => MathF.Sqrt(C * C + D * D);
            public float ScaleX() => MathF.Sqrt(A * A + B * B);
            public static readonly M I = new(1, 0, 0, 1, 0, 0);
        }

        // ------------------------------------------------------------------ penafsir
        private sealed class GS
        {
            public M Ctm; public int Fill = unchecked((int)0xFF000000), Stroke = unchecked((int)0xFF000000); public float Lw = 1;
            public string FillCs = "DeviceGray", StrokeCs = "DeviceGray";
            public Font? Font; public float Fs = 12, Tc, Tw, Th = 1, Tl, Rise; public int Mode; public float FillA = 1;
            public List<float[]>? Clip; public float[]? ClipBox;
            public GS(M ctm) { Ctm = ctm; }
            public GS Copy() => (GS)MemberwiseClone();
        }

        private sealed class FBuf
        {
            private float[] _a = new float[16]; public int N;
            public void Add(float v) { if (N == _a.Length) Array.Resize(ref _a, N * 2); _a[N++] = v; }
            public float[] ToArray() => _a[..N];
        }

        private static float Num(object? o, double def = 0) => (float)Pdf.Num(o, def);

        private sealed class Run
        {
            private readonly PdfFile _f; private readonly float _pw, _ph;
            public readonly List<Glyph> Glyphs = new();
            public readonly List<Image> Images = new();
            public readonly List<Shape> Shapes = new();
            private readonly Dictionary<object, Font> _fonts = new();
            private int _ops;

            public Run(PdfFile f, float pw, float ph) { _f = f; _pw = pw; _ph = ph; }

            public void Exec(byte[] content, PDict res, M ctm0, int depth)
            {
                if (depth > 12) return;
                var stack = new List<GS>();
                var gs = new GS(ctm0);
                var tm = M.I; var tlm = M.I;
                var args = new List<object?>();
                var subs = new List<float[]>(); var closed = new List<bool>();
                var cur = new FBuf(); float cx = 0, cy = 0, sx = 0, sy = 0;
                bool isRect = true, pendingClip = false;
                void FlushSub(bool close) { if (cur.N >= 4) { subs.Add(cur.ToArray()); closed.Add(close); } cur = new FBuf(); }
                void Dev(float x, float y) { cur.Add(gs.Ctm.X(x, y)); cur.Add(gs.Ctm.Y(x, y)); }
                static float[] Box(float[] sb)
                {
                    float a = float.MaxValue, b = float.MaxValue, c = -float.MaxValue, d = -float.MaxValue;
                    for (int i = 0; i + 1 < sb.Length; i += 2) { a = Math.Min(a, sb[i]); c = Math.Max(c, sb[i]); b = Math.Min(b, sb[i + 1]); d = Math.Max(d, sb[i + 1]); }
                    return new[] { a, b, c, d };
                }
                void Paint(bool fill, bool stroke, bool close)
                {
                    if (close && cur.N >= 4) FlushSub(true); else FlushSub(close);
                    if (pendingClip)
                    {
                        pendingClip = false;
                        if (subs.Count == 1 && !isRect) { gs.Clip = new List<float[]> { subs[0] }; gs.ClipBox = Box(subs[0]); }
                        else if (subs.Count == 1 && isRect) { gs.Clip = null; gs.ClipBox = null; }
                    }
                    var cb = gs.ClipBox;
                    if (fill && !stroke && isRect && subs.Count == 1 && cb != null && gs.Clip != null)
                    {
                        var bb = Box(subs[0]);
                        if (bb[0] <= cb[0] + 1 && bb[1] <= cb[1] + 1 && bb[2] >= cb[2] - 1 && bb[3] >= cb[3] - 1)
                        {
                            Shapes.Add(new Shape(gs.Clip, new List<bool> { true }, WithAlpha(gs.Fill, gs.FillA), NONE, 0, false));
                            subs.Clear(); closed.Clear(); isRect = true;
                            return;
                        }
                    }
                    if ((fill || stroke) && subs.Count > 0)
                    {
                        float lw = gs.Lw * Math.Max(gs.Ctm.ScaleX(), gs.Ctm.ScaleY()); if (lw <= 0) lw = 0.5f;
                        Shapes.Add(new Shape(new List<float[]>(subs), new List<bool>(closed), fill ? WithAlpha(gs.Fill, gs.FillA) : NONE, stroke ? gs.Stroke : NONE, lw, isRect && subs.Count == 1));
                    }
                    subs.Clear(); closed.Clear(); isRect = true;
                }
                var lx = new PdfLexer(content);
                while (true)
                {
                    if (++_ops > MAX_OPS) return;
                    object? o;
                    try { o = lx.Obj(); } catch { o = null; }
                    if (o == null) break;
                    if (o is not PdfLexer.Op op) { args.Add(o); continue; }
                    float N(int i) => i < args.Count ? Num(args[i]) : 0;
                    try
                    {
                        switch (op.K)
                        {
                            case "q": stack.Add(gs.Copy()); break;
                            case "Q": if (stack.Count > 0) { gs = stack[^1]; stack.RemoveAt(stack.Count - 1); } break;
                            case "cm": if (args.Count >= 6) gs.Ctm = new M(N(0), N(1), N(2), N(3), N(4), N(5)).Times(gs.Ctm); break;
                            case "w": gs.Lw = N(0); break;
                            case "gs":
                                if (args.Count > 0 && args[0] is PName nm)
                                {
                                    var e = _f.RDict(_f.RDict(res["ExtGState"])?[nm.N]);
                                    if (e?["ca"] is long or double) gs.FillA = Num(e["ca"], 1);
                                    if (e?["LW"] is long or double) gs.Lw = Num(e["LW"], 1);
                                }
                                break;
                            case "g": gs.Fill = Gray(N(0)); gs.FillCs = "DeviceGray"; break;
                            case "G": gs.Stroke = Gray(N(0)); gs.StrokeCs = "DeviceGray"; break;
                            case "rg": gs.Fill = Rgb(N(0), N(1), N(2)); gs.FillCs = "DeviceRGB"; break;
                            case "RG": gs.Stroke = Rgb(N(0), N(1), N(2)); gs.StrokeCs = "DeviceRGB"; break;
                            case "k": gs.Fill = Cmyk(N(0), N(1), N(2), N(3)); gs.FillCs = "DeviceCMYK"; break;
                            case "K": gs.Stroke = Cmyk(N(0), N(1), N(2), N(3)); gs.StrokeCs = "DeviceCMYK"; break;
                            case "cs": gs.FillCs = CsName(res, args.Count > 0 ? args[0] : null); gs.Fill = unchecked((int)0xFF000000); break;
                            case "CS": gs.StrokeCs = CsName(res, args.Count > 0 ? args[0] : null); gs.Stroke = unchecked((int)0xFF000000); break;
                            case "sc": case "scn": if (ColorFrom(gs.FillCs, args) is { } fc) gs.Fill = fc; break;
                            case "SC": case "SCN": if (ColorFrom(gs.StrokeCs, args) is { } sc) gs.Stroke = sc; break;
                            case "m": FlushSub(false); cx = N(0); cy = N(1); sx = cx; sy = cy; Dev(cx, cy); break;
                            case "l": if (cur.N == 0) Dev(cx, cy); cx = N(0); cy = N(1); Dev(cx, cy); break;
                            case "c": case "v": case "y":
                                {
                                    isRect = false;
                                    if (cur.N == 0) Dev(cx, cy);
                                    float x1, y1, x2, y2, x3, y3;
                                    if (op.K == "c") { x1 = N(0); y1 = N(1); x2 = N(2); y2 = N(3); x3 = N(4); y3 = N(5); }
                                    else if (op.K == "v") { x1 = cx; y1 = cy; x2 = N(0); y2 = N(1); x3 = N(2); y3 = N(3); }
                                    else { x1 = N(0); y1 = N(1); x2 = N(2); y2 = N(3); x3 = N(2); y3 = N(3); }
                                    for (int k = 1; k <= 8; k++)
                                    {
                                        float t = k / 8f, u = 1 - t;
                                        Dev(u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3, u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3);
                                    }
                                    cx = x3; cy = y3;
                                    break;
                                }
                            case "h": if (cur.N >= 2) { Dev(sx, sy); FlushSub(true); } cx = sx; cy = sy; break;
                            case "re":
                                {
                                    FlushSub(false);
                                    float x = N(0), y = N(1), w = N(2), h = N(3);
                                    Dev(x, y); Dev(x + w, y); Dev(x + w, y + h); Dev(x, y + h); Dev(x, y);
                                    FlushSub(true); cx = x; cy = y;
                                    if (subs.Count > 1) isRect = false;
                                    break;
                                }
                            case "S": Paint(false, true, false); break;
                            case "s": Paint(false, true, true); break;
                            case "f": case "F": case "f*": Paint(true, false, true); break;
                            case "B": case "B*": case "b": case "b*": Paint(true, true, true); break;
                            case "n": Paint(false, false, false); break;
                            case "W": case "W*": pendingClip = true; break;
                            case "BT": tm = M.I; tlm = M.I; break;
                            case "Tf": gs.Font = LoadFont(res, args.Count > 0 ? args[0] : null); gs.Fs = N(1); break;
                            case "Tc": gs.Tc = N(0); break;
                            case "Tw": gs.Tw = N(0); break;
                            case "Tz": gs.Th = N(0) / 100f; break;
                            case "TL": gs.Tl = N(0); break;
                            case "Ts": gs.Rise = N(0); break;
                            case "Tr": gs.Mode = (int)N(0); break;
                            case "Td": tlm = new M(1, 0, 0, 1, N(0), N(1)).Times(tlm); tm = tlm; break;
                            case "TD": gs.Tl = -N(1); tlm = new M(1, 0, 0, 1, N(0), N(1)).Times(tlm); tm = tlm; break;
                            case "Tm": tlm = new M(N(0), N(1), N(2), N(3), N(4), N(5)); tm = tlm; break;
                            case "T*": tlm = new M(1, 0, 0, 1, 0, -gs.Tl).Times(tlm); tm = tlm; break;
                            case "Tj": if (args.Count > 0 && args[0] is PStr s1) tm = Show(s1.B, gs, tm); break;
                            case "'": tlm = new M(1, 0, 0, 1, 0, -gs.Tl).Times(tlm); tm = tlm; if (args.Count > 0 && args[0] is PStr s2) tm = Show(s2.B, gs, tm); break;
                            case "\"": gs.Tw = N(0); gs.Tc = N(1); tlm = new M(1, 0, 0, 1, 0, -gs.Tl).Times(tlm); tm = tlm; if (args.Count > 2 && args[2] is PStr s3) tm = Show(s3.B, gs, tm); break;
                            case "TJ":
                                if (args.Count > 0 && args[0] is List<object?> arr)
                                    foreach (var e in arr)
                                    {
                                        if (e is PStr ps) tm = Show(ps.B, gs, tm);
                                        else if (e is long or double) tm = new M(1, 0, 0, 1, -Num(e) / 1000f * gs.Fs * gs.Th, 0).Times(tm);
                                    }
                                break;
                            case "Do": if (args.Count > 0 && args[0] is PName xn) XObject(res, xn.N, gs, depth); break;
                            case "BI": InlineImage(lx, gs, res); break;
                        }
                    }
                    catch { }
                    args.Clear();
                }
            }

            private M Show(byte[] b, GS gs, M tm0)
            {
                var font = gs.Font; if (font == null) return tm0;
                var tm = tm0;
                bool inv = gs.Mode is 3 or 7;
                foreach (var cg in font.Codes(b))
                {
                    float w0 = font.Width(cg.Code) / 1000f;
                    var trm = new M(gs.Fs * gs.Th, 0, 0, gs.Fs, 0, gs.Rise).Times(tm).Times(gs.Ctm);
                    float x = trm.X(0, 0), y = trm.Y(0, 0), size = trm.ScaleY();
                    float adv = (w0 * gs.Fs + gs.Tc + (cg.Single && cg.Code == 32 ? gs.Tw : 0)) * gs.Th;
                    var endM = new M(1, 0, 0, 1, w0 * gs.Fs * gs.Th, 0).Times(tm).Times(gs.Ctm);
                    float x2 = endM.X(0, 0);
                    bool upright = MathF.Abs(trm.B) < 0.01f * size && trm.A > 0;
                    if (cg.Text.Length > 0 && size > 0.5f && x > -50 && x < _pw + 50 && y > -50 && y < _ph + 50)
                        Glyphs.Add(new Glyph(x, y, upright ? MathF.Abs(x2 - x) : MathF.Sqrt((x2 - x) * (x2 - x) + (endM.Y(0, 0) - y) * (endM.Y(0, 0) - y)),
                            size, cg.Text, font.Info, gs.Fill, inv, upright, (float)(Math.Atan2(trm.B, trm.A) * 180 / Math.PI)));
                    tm = new M(1, 0, 0, 1, adv, 0).Times(tm);
                }
                return tm;
            }

            private Font? LoadFont(PDict res, object? nm)
            {
                if (nm is not PName pn) return null;
                var r = _f.RDict(res["Font"])?[pn.N];
                if (r == null) return null;
                if (!_fonts.TryGetValue(r, out var fo)) _fonts[r] = fo = Font.Load(_f, _f.RDict(r) ?? new PDict());
                return fo;
            }

            private string CsName(PDict res, object? nm)
            {
                if (nm is not PName pn) return "DeviceGray";
                var n = pn.N;
                if (n.StartsWith("Device", StringComparison.Ordinal) || n == "Pattern") return n;
                var cs = _f.Res(_f.RDict(res["ColorSpace"])?[n]);
                return cs switch
                {
                    PName p => p.N,
                    List<object?> l => (_f.Res(l.FirstOrDefault()) as PName)?.N switch
                    {
                        "ICCBased" => Pdf.Int(_f.RDict(l.Count > 1 ? l[1] : null)?["N"], 3) switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" },
                        "CalRGB" or "Lab" => "DeviceRGB",
                        "CalGray" => "DeviceGray",
                        "Separation" or "DeviceN" => "Separation",
                        "Indexed" => "Indexed:" + n,
                        _ => "DeviceRGB"
                    },
                    _ => "DeviceGray"
                };
            }

            private static int? ColorFrom(string cs, List<object?> a)
            {
                var v = a.Where(x => x is long or double).Select(x => Num(x)).ToList();
                if (cs == "DeviceGray" && v.Count > 0) return Gray(v[0]);
                if (cs == "DeviceRGB" && v.Count >= 3) return Rgb(v[0], v[1], v[2]);
                if (cs == "DeviceCMYK" && v.Count >= 4) return Cmyk(v[0], v[1], v[2], v[3]);
                if (cs == "Separation" && v.Count > 0) return Gray(1f - v[0]);
                if (v.Count >= 3) return Rgb(v[0], v[1], v[2]);
                if (v.Count == 1) return Gray(v[0]);
                return null;
            }

            private void XObject(PDict res, string name, GS gs, int depth)
            {
                if (_f.Res(_f.RDict(res["XObject"])?[name]) is not PStream st) return;
                switch (st.Dict.Name("Subtype"))
                {
                    case "Form":
                        {
                            var mt = (_f.Res(st.Dict["Matrix"]) as List<object?>)?.Select(x => Num(_f.Res(x))).ToList();
                            var m = mt != null && mt.Count == 6 ? new M(mt[0], mt[1], mt[2], mt[3], mt[4], mt[5]) : M.I;
                            var sub = gs.Copy(); sub.Ctm = m.Times(gs.Ctm);
                            var r2 = _f.RDict(st.Dict["Resources"]) ?? res;
                            byte[] body;
                            try { body = _f.Decode(st, null); } catch { return; }
                            int c = sub.Fill;
                            var prefix = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0:0.0000} {1:0.0000} {2:0.0000} rg ", ((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f));
                            Exec(prefix.Concat(body).ToArray(), r2, sub.Ctm, depth + 1);
                            break;
                        }
                    case "Image": ImageOf(st, gs); break;
                }
            }

            private static float[] Place(GS gs)
            {
                var m = gs.Ctm;
                float[] xs = { m.X(0, 0), m.X(1, 0), m.X(0, 1), m.X(1, 1) }, ys = { m.Y(0, 0), m.Y(1, 0), m.Y(0, 1), m.Y(1, 1) };
                return new[] { xs.Min(), ys.Min(), xs.Max(), ys.Max() };
            }

            private void ImageOf(PStream st, GS gs, bool inline = false)
            {
                var r = Place(gs);
                if (r[2] - r[0] < 1 || r[3] - r[1] < 1) return;
                if (!ImgDec.Supported(_f, st)) return;
                var f = _f; int fill = gs.Fill;
                // ImageMask diwarnai isian saat ini & gambar inline tidak berbagi: kunci unik; gambar biasa → kunci = alirannya
                object key = st.Dict["ImageMask"] is true || inline ? new object() : st;
                Images.Add(new Image(r[0], r[1], r[2], r[3], key, () => ImgDec.Decode(f, st, fill)));
            }

            private void InlineImage(PdfLexer lx, GS gs, PDict res)
            {
                var d = new PDict();
                while (true)
                {
                    var k = lx.Obj(); if (k == null) return;
                    if (k is PdfLexer.Op op && op.K == "ID") break;
                    var v = lx.Obj(); if (v == null) return;
                    if (k is PName kn) d[Expand(kn.N)] = v is PName vn ? new PName(Expand(vn.N)) : v is List<object?> vl ? vl.Select(x => x is PName xn ? (object?)new PName(Expand(xn.N)) : x).ToList() : v;
                }
                int start = lx.P + 1, e = start;
                while (e + 2 < lx.End)
                {
                    if (lx.D[e] == 'E' && lx.D[e + 1] == 'I' && PdfLexer.Ws(lx.At(e - 1)) && (e + 2 >= lx.End || PdfLexer.Ws(lx.At(e + 2)))) break;
                    e++;
                }
                var raw = lx.D[start..Math.Max(start, e - 1)];
                lx.P = Math.Min(lx.End, e + 2);
                if (d["ColorSpace"] is PName csn && !csn.N.StartsWith("Device", StringComparison.Ordinal) && _f.RDict(res["ColorSpace"])?[csn.N] is { } csv) d["ColorSpace"] = csv;
                ImageOf(new PStream(d, raw), gs, true);
            }

            private static string Expand(string k) => k switch
            {
                "W" => "Width", "H" => "Height", "BPC" => "BitsPerComponent", "CS" => "ColorSpace", "F" => "Filter", "DP" => "DecodeParms",
                "IM" => "ImageMask", "D" => "Decode", "I" => "Interpolate", "G" => "DeviceGray", "RGB" => "DeviceRGB", "CMYK" => "DeviceCMYK",
                "AHx" => "ASCIIHexDecode", "A85" => "ASCII85Decode", "Fl" => "FlateDecode", "DCT" => "DCTDecode", "RL" => "RunLengthDecode", "LZW" => "LZWDecode",
                _ => k
            };
        }

        public static int Gray(float v) { int c = (int)(Math.Clamp(v, 0f, 1f) * 255); return unchecked((int)0xFF000000) | (c << 16) | (c << 8) | c; }
        public static int Rgb(float r, float g, float b) => unchecked((int)0xFF000000) | ((int)(Math.Clamp(r, 0f, 1f) * 255) << 16) | ((int)(Math.Clamp(g, 0f, 1f) * 255) << 8) | (int)(Math.Clamp(b, 0f, 1f) * 255);
        public static int Cmyk(float c, float m, float y, float k) => Rgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        private static int WithAlpha(int c, float a)
        {
            if (a >= 0.99f) return c;
            float r = ((c >> 16) & 255) * a + 255 * (1 - a), g = ((c >> 8) & 255) * a + 255 * (1 - a), b = (c & 255) * a + 255 * (1 - a);
            return unchecked((int)0xFF000000) | ((int)r << 16) | ((int)g << 8) | (int)b;
        }

        // ------------------------------------------------------------------ font
        public sealed record CodeGlyph(int Code, string Text, bool Single);

        public sealed class Font
        {
            public readonly FontInfo Info;
            private readonly Dictionary<int, float> _widths; private readonly float _dw; private readonly Dictionary<int, string> _toUni;
            private readonly string?[]? _enc; private readonly bool _cid; private readonly List<int[]> _ranges; private readonly float[]? _std; private readonly bool _stdMono; private readonly float _fm;

            private Font(FontInfo info, Dictionary<int, float> widths, float dw, Dictionary<int, string> toUni, string?[]? enc, bool cid, List<int[]> ranges, float[]? std, bool stdMono, float fm)
            { Info = info; _widths = widths; _dw = dw; _toUni = toUni; _enc = enc; _cid = cid; _ranges = ranges; _std = std; _stdMono = stdMono; _fm = fm; }

            public float Width(int code)
            {
                if (_widths.TryGetValue(code, out var w)) return w * _fm;
                if (_std != null && !_cid) return _stdMono ? 600f : code >= 32 && code <= 126 ? _std[code - 32] : 500f;
                return _dw * _fm;
            }

            public List<CodeGlyph> Codes(byte[] b)
            {
                var outL = new List<CodeGlyph>(b.Length);
                if (!_cid) { foreach (var x in b) outL.Add(new CodeGlyph(x, Uni(x, 1), true)); return outL; }
                int i = 0;
                while (i < b.Length)
                {
                    int len = 2;
                    if (_ranges.Count > 0)
                    {
                        len = 0;
                        for (int n = 1; n <= 4; n++)
                        {
                            if (i + n > b.Length) break;
                            int v0 = 0; for (int k = 0; k < n; k++) v0 = (v0 << 8) | b[i + k];
                            if (_ranges.Any(r => r[0] == n && v0 >= r[1] && v0 <= r[2])) { len = n; break; }
                        }
                        if (len == 0) len = Math.Max(1, Math.Min(2, b.Length - i));
                    }
                    len = Math.Min(len, b.Length - i);
                    int v = 0; for (int k = 0; k < len; k++) v = (v << 8) | b[i + k];
                    outL.Add(new CodeGlyph(v, Uni(v, len), len == 1));
                    i += len;
                }
                return outL;
            }

            private string Uni(int c, int len)
            {
                if (_toUni.TryGetValue(c, out var u)) return u;
                if (_cid)
                {
                    if (_toUni.Count == 0 && c >= 32 && c <= 0xFFFF && len == 2 && !(c >= 0xD800 && c <= 0xDFFF))
                    { var s = ((char)c).ToString(); return char.IsLetterOrDigit((char)c) || (c >= 32 && c <= 126) ? s : ""; }
                    return "";
                }
                if (_enc != null && c >= 0 && c < _enc.Length && _enc[c] != null) return _enc[c]!;
                return c >= 32 && c <= 126 || c >= 160 && c <= 255 ? ((char)c).ToString() : "";
            }

            private static readonly float[] HELV = new[] { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584 }.Select(x => (float)x).ToArray();
            private static readonly float[] TIMES = new[] { 250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278, 500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 278, 278, 564, 564, 564, 444, 921, 722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722, 556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611, 333, 278, 333, 469, 500, 333, 444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500, 500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444, 480, 200, 480, 541 }.Select(x => (float)x).ToArray();

            public static Font Load(PdfFile f, PDict d)
            {
                var sub = d.Name("Subtype") ?? "Type1";
                bool cid = sub == "Type0";
                var desc = cid ? f.RDict((f.Res(d["DescendantFonts"]) as List<object?>)?.FirstOrDefault()) : null;
                var baseName = d.Name("BaseFont") ?? desc?.Name("BaseFont") ?? "Arial";
                var fd = f.RDict((desc ?? d)["FontDescriptor"]);
                var info = FontInfoOf(baseName, fd);
                var widths = new Dictionary<int, float>();
                float dw = 1000, fm = 1;
                if (sub == "Type3" && f.Res(d["FontMatrix"]) is List<object?> mm) fm = Num(f.Res(mm.FirstOrDefault()), 0.001) * 1000f;
                if (cid && desc != null)
                {
                    dw = Num(f.Res(desc["DW"]), 1000);
                    if (f.Res(desc["W"]) is List<object?> w)
                    {
                        int i = 0;
                        while (i < w.Count)
                        {
                            int c0 = Pdf.Int(f.Res(w[i]));
                            var nx = i + 1 < w.Count ? f.Res(w[i + 1]) : null;
                            if (nx is List<object?> nl) { for (int k = 0; k < nl.Count; k++) widths[c0 + k] = Num(f.Res(nl[k])); i += 2; }
                            else { int c1 = Pdf.Int(nx); float v = Num(f.Res(i + 2 < w.Count ? w[i + 2] : null)); if (c1 - c0 < 70000) for (int c = c0; c <= c1; c++) widths[c] = v; i += 3; }
                        }
                    }
                }
                else
                {
                    int fc = Pdf.Int(f.Res(d["FirstChar"]));
                    if (f.Res(d["Widths"]) is List<object?> wl) for (int k = 0; k < wl.Count; k++) widths[fc + k] = Num(f.Res(wl[k]));
                    dw = Num(f.Res(fd?["MissingWidth"]), 500); if (dw <= 0) dw = 500;
                }
                var toUni = new Dictionary<int, string>();
                var ranges = new List<int[]>();
                if (f.Res(d["ToUnicode"]) is PStream tu) { try { CMap.Parse(f.Decode(tu, null), toUni, ranges); } catch { } }
                if (cid && f.Res(d["Encoding"]) is PStream es)
                {
                    try { var dummy = new Dictionary<int, string>(); var rr = new List<int[]>(); CMap.Parse(f.Decode(es, null), dummy, rr); if (rr.Count > 0) { ranges.Clear(); ranges.AddRange(rr); } } catch { }
                }
                if (cid && (d.Name("Encoding")?.StartsWith("Identity", StringComparison.Ordinal) != false)) { ranges.Clear(); ranges.Add(new[] { 2, 0, 0xFFFF }); }
                string?[]? enc = null;
                if (!cid)
                {
                    var e = f.Res(d["Encoding"]);
                    var baseEnc = (e as PName)?.N ?? (e as PDict)?.Name("BaseEncoding");
                    enc = Encodings.Base(baseEnc, info.Family == "Symbol" || baseName.Contains("Symbol") || baseName.Contains("Dingbat"));
                    if ((e as PDict)?["Differences"] is { } dobj && f.Res(dobj) is List<object?> diff)
                    {
                        int code = 0;
                        foreach (var x in diff)
                        {
                            var v = f.Res(x);
                            if (v is long or double) code = Pdf.Int(v);
                            else if (v is PName gn) { if (code >= 0 && code <= 255) enc[code] = Encodings.Glyph(gn.N); code++; }
                        }
                    }
                }
                var stdKey = (baseName.Contains('+') ? baseName[(baseName.IndexOf('+') + 1)..] : baseName).ToLowerInvariant();
                float[]? std = !cid && widths.Count == 0 ? (stdKey.StartsWith("times", StringComparison.Ordinal) || (stdKey.Contains("serif") && !stdKey.Contains("sans")) ? TIMES : HELV) : null;
                return new Font(info, widths, dw, toUni, enc, cid, ranges, std, std != null && stdKey.StartsWith("courier", StringComparison.Ordinal), fm);
            }

            private static readonly (string Key, string Name)[] KNOWN =
            {
                ("timesnewroman", "Times New Roman"), ("timesroman", "Times New Roman"), ("times", "Times New Roman"),
                ("couriernew", "Courier New"), ("courier", "Courier New"), ("arialnarrow", "Arial Narrow"), ("arialblack", "Arial Black"),
                ("arial", "Arial"), ("helvetica", "Arial"), ("bookmanoldstyle", "Bookman Old Style"), ("centurygothic", "Century Gothic"),
                ("comicsansms", "Comic Sans MS"), ("trebuchetms", "Trebuchet MS"), ("palatinolinotype", "Palatino Linotype"), ("segoeui", "Segoe UI")
            };

            public static FontInfo FontInfoOf(string base0, PDict? fd)
            {
                var b = base0.Contains('+') ? base0[(base0.IndexOf('+') + 1)..] : base0;
                var lower = b.ToLowerInvariant();
                int flags = Pdf.Int(fd?["Flags"]), weight = Pdf.Int(fd?["FontWeight"]);
                bool bold = Regex.IsMatch(lower, "bold|black|heavy|semibold|demi|,bd|-bd") || weight >= 600 || (flags & (1 << 18)) != 0;
                bool italic = Regex.IsMatch(lower, @"italic|oblique|,it|-it\b|kursiv") || (flags & (1 << 6)) != 0 || Pdf.Num(fd?["ItalicAngle"]) < -1;
                bool mono = lower.Contains("courier") || lower.Contains("mono") || (flags & 1) != 0;
                var flat = Regex.Replace(lower, "[^a-z0-9]", "");
                foreach (var (key, name) in KNOWN)
                    if (flat.StartsWith(key, StringComparison.Ordinal))
                    {
                        bool serifK = name.StartsWith("Times") || name.StartsWith("Bookman") || name.StartsWith("Palatino");
                        return new FontInfo(name, bold, italic, mono || name.StartsWith("Courier"), serifK);
                    }
                var fam = Regex.Replace(b.Split(',', '-')[0], "(PSMT|PS|MT|Std|LT|Pro)$", "");
                fam = Regex.Replace(Regex.Replace(fam, "(Bold|Italic|Oblique|Regular|Roman|Light|Medium|Semibold|Black|Heavy|Book)+$", ""), "([a-z])([A-Z])", "$1 $2").Trim();
                fam = fam.ToLowerInvariant().Replace(" ", "") switch
                {
                    "helvetica" or "arial" or "arialmt" or "helveticaneue" or "liberationsans" or "nimbussans" or "nimbussansl" => "Arial",
                    "times" or "timesnewroman" or "timesroman" or "liberationserif" or "nimbusroman" or "nimbusromno9l" => "Times New Roman",
                    "courier" or "couriernew" or "liberationmono" or "nimbusmono" => "Courier New",
                    "symbol" => "Symbol", "zapfdingbats" => "Wingdings", "" => "Arial",
                    _ => fam
                };
                bool serif = (flags & 2) != 0 || fam.Contains("Times") || (fam.Contains("Serif") && !fam.Contains("Sans")) || fam.Contains("Georgia") || fam.Contains("Cambria") || fam.Contains("Garamond") || fam.Contains("Book");
                return new FontInfo(fam, bold, italic, mono, serif);
            }
        }

        /// <summary>CMap ToUnicode / pengodean CID: bfchar, bfrange, codespacerange.</summary>
        public static class CMap
        {
            public static void Parse(byte[] b, Dictionary<int, string> map, List<int[]> ranges)
            {
                var lx = new PdfLexer(b);
                var stack = new List<object?>();
                static int Code(PStr s) { int a = 0; foreach (var x in s.B) a = (a << 8) | x; return a; }
                static string Str(PStr s) => s.B.Length >= 2 && s.B.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(s.B) : Pdf.Latin1.GetString(s.B);
                while (true)
                {
                    object? o;
                    try { o = lx.Obj(false); } catch { break; }
                    if (o == null) break;
                    if (o is not PdfLexer.Op op) { stack.Add(o); if (stack.Count > 4096) stack.Clear(); continue; }
                    switch (op.K)
                    {
                        case "begincodespacerange": case "beginbfchar": case "beginbfrange": case "begincidchar": case "begincidrange": stack.Clear(); break;
                        case "endcodespacerange":
                            for (int i = 0; i + 1 < stack.Count; i += 2) if (stack[i] is PStr a && stack[i + 1] is PStr z) ranges.Add(new[] { a.B.Length, Code(a), Code(z) });
                            stack.Clear(); break;
                        case "endbfchar":
                            for (int i = 0; i + 1 < stack.Count; i += 2)
                                if (stack[i] is PStr a) map[Code(a)] = stack[i + 1] switch { PStr v => Str(v), PName n => Encodings.Glyph(n.N) ?? "", _ => "" };
                            stack.Clear(); break;
                        case "endbfrange":
                            for (int i = 0; i + 2 < stack.Count; i += 3)
                            {
                                if (stack[i] is not PStr a || stack[i + 1] is not PStr z) continue;
                                int c0 = Code(a), c1 = Code(z);
                                if (c1 - c0 < 0 || c1 - c0 > 65535) continue;
                                if (stack[i + 2] is PStr v)
                                {
                                    var bs = Str(v);
                                    if (bs.Length > 0) { char last = bs[^1]; for (int c = c0; c <= c1; c++) map[c] = bs[..^1] + (char)(last + (c - c0)); }
                                }
                                else if (stack[i + 2] is List<object?> lst)
                                    for (int k = 0; k < lst.Count; k++) if (lst[k] is PStr s) map[c0 + k] = Str(s);
                            }
                            stack.Clear(); break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ gambar → JPEG/PNG
        public static class ImgDec
        {
            /// <summary>Cek murah (tanpa dekode) apakah gambar bisa diubah: ukuran wajar, filter dikenal.</summary>
            public static bool Supported(PdfFile f, PStream st)
            {
                int w = Pdf.Int(f.Res(st.Dict["Width"])), h = Pdf.Int(f.Res(st.Dict["Height"]));
                if (w <= 0 || h <= 0 || (long)w * h > 40_000_000L) return false;
                var fl = f.Filters(st);
                return !fl.Contains("JPXDecode") && !fl.Contains("CCITTFaxDecode") && !fl.Contains("JBIG2Decode");
            }

            public static (byte[]? Jpeg, byte[]? Png, int W, int H)? Decode(PdfFile f, PStream st, int fill)
            {
                var d = st.Dict;
                int w = Pdf.Int(f.Res(d["Width"])), h = Pdf.Int(f.Res(d["Height"]));
                if (w <= 0 || h <= 0 || (long)w * h > 40_000_000L) return null;
                var fl = f.Filters(st);
                var smask = f.Res(d["SMask"]) as PStream;
                if (fl.Contains("JPXDecode")) return null;
                if (fl.Contains("DCTDecode"))
                {
                    byte[] jpg;
                    try { jpg = f.Decode(st, new HashSet<string> { "DCTDecode" }); } catch { return null; }
                    if (smask != null && Alpha(f, smask, w, h) is { } a && JpegWithAlpha(jpg, a, w, h) is { } png) return (null, png, w, h);
                    return (jpg, null, w, h);
                }
                if (fl.Contains("CCITTFaxDecode") || fl.Contains("JBIG2Decode")) return null;
                byte[] raw;
                try { raw = f.Decode(st, null); } catch { return null; }
                bool mask = d["ImageMask"] is true;
                int bpc = mask ? 1 : Pdf.Int(f.Res(d["BitsPerComponent"]), 8);
                var decode = (f.Res(d["Decode"]) as List<object?>)?.Select(x => Num(f.Res(x))).ToList();
                var rgba = new int[w * h];
                byte RawAt(int i) => i >= 0 && i < raw.Length ? raw[i] : (byte)0;
                if (mask)
                {
                    bool inv = decode != null && decode.Count >= 2 && decode[0] > decode[1];
                    int row1 = (w + 7) / 8;
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                        {
                            int bit = (RawAt(y * row1 + x / 8) >> (7 - x % 8)) & 1;
                            bool paint = inv ? bit == 1 : bit == 0;
                            rgba[y * w + x] = paint ? fill | unchecked((int)0xFF000000) : 0;
                        }
                    return (null, Png(rgba, w, h, true), w, h);
                }
                var cs = ColorSpace(f, f.Res(d["ColorSpace"]));
                int nc = cs.N, row = (w * nc * bpc + 7) / 8;
                int Sample(int y, int i)
                {
                    int bit = i * bpc, off = y * row + bit / 8;
                    return bpc switch { 8 => RawAt(off), 16 => RawAt(off), _ => (RawAt(off) >> (8 - bpc - bit % 8)) & ((1 << bpc) - 1) };
                }
                float maxV = bpc == 16 ? 255f : (1 << bpc) - 1;
                var comps = new float[nc];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        for (int k = 0; k < nc; k++)
                        {
                            float v = Sample(y, x * nc + k) / maxV;
                            if (decode != null && decode.Count >= 2 * (k + 1) && cs.Lookup == null) v = decode[2 * k] + v * (decode[2 * k + 1] - decode[2 * k]);
                            comps[k] = v;
                        }
                        rgba[y * w + x] = cs.ToRgb(comps, Sample(y, x * nc));
                    }
                bool hasA = false;
                if (smask != null && Alpha(f, smask, w, h) is { } al) { hasA = true; for (int i = 0; i < rgba.Length; i++) rgba[i] = (rgba[i] & 0xFFFFFF) | (al[i] << 24); }
                return (null, Png(rgba, w, h, hasA), w, h);
            }

            private static byte[]? Alpha(PdfFile f, PStream sm, int w, int h)
            {
                int mw = Pdf.Int(f.Res(sm.Dict["Width"])), mh = Pdf.Int(f.Res(sm.Dict["Height"]));
                if (mw <= 0 || mh <= 0) return null;
                if (f.Filters(sm).Any(x => x is "DCTDecode" or "JPXDecode")) return null;
                byte[] b;
                try { b = f.Decode(sm, null); } catch { return null; }
                int bpc = Pdf.Int(f.Res(sm.Dict["BitsPerComponent"]), 8), row = (mw * bpc + 7) / 8;
                byte At(int i) => i >= 0 && i < b.Length ? b[i] : (byte)0;
                var outA = new byte[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int sx = x * mw / w, sy = y * mh / h;
                        int v = bpc == 8 ? (sy * row + sx < b.Length ? b[sy * row + sx] : 255)
                            : ((At(sy * row + sx * bpc / 8) >> (8 - bpc - (sx * bpc) % 8)) & ((1 << bpc) - 1)) * 255 / ((1 << bpc) - 1);
                        outA[y * w + x] = (byte)v;
                    }
                return outA;
            }

            /// <summary>JPEG + kanal alfa → PNG (padanan BitmapFactory Android, lewat GDI+).</summary>
            private static byte[]? JpegWithAlpha(byte[] jpg, byte[] alpha, int w, int h)
            {
                try
                {
                    using var ms = new MemoryStream(jpg);
                    using var src = new Bitmap(ms);
                    using var bmp = new Bitmap(src, w, h);
                    var px = new int[w * h];
                    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                    bmp.UnlockBits(data);
                    for (int i = 0; i < px.Length; i++) px[i] = (px[i] & 0xFFFFFF) | (alpha[i] << 24);
                    return Png(px, w, h, true);
                }
                catch { return null; }
            }

            public readonly struct CS
            {
                public readonly int N; public readonly string Kind; public readonly byte[]? Lookup; public readonly int BaseN;
                public CS(int n, string kind, byte[]? lookup = null, int baseN = 3) { N = n; Kind = kind; Lookup = lookup; BaseN = baseN; }
                public int ToRgb(float[] c, int idx)
                {
                    if (Lookup != null)
                    {
                        int o = idx * BaseN; var lk = Lookup;
                        float L(int i) => (i >= 0 && i < lk.Length ? lk[i] : 0) / 255f;
                        return BaseN == 4 ? Cmyk(L(o), L(o + 1), L(o + 2), L(o + 3)) : BaseN == 1 ? Gray(L(o)) : Rgb(L(o), L(o + 1), L(o + 2));
                    }
                    if (Kind == "cmyk") return Cmyk(c[0], c[1], c[2], c[3]);
                    if (Kind == "sep") return Gray(1f - c[0]);
                    if (N == 1) return Gray(c[0]);
                    return Rgb(c[0], c.Length > 1 ? c[1] : c[0], c.Length > 2 ? c[2] : c[0]);
                }
            }

            public static CS ColorSpace(PdfFile f, object? cs)
            {
                if (cs is PName n) return n.N switch { "DeviceGray" or "CalGray" or "G" => new CS(1, "gray"), "DeviceCMYK" or "CMYK" => new CS(4, "cmyk"), _ => new CS(3, "rgb") };
                if (cs is List<object?> l)
                {
                    switch ((f.Res(l.FirstOrDefault()) as PName)?.N)
                    {
                        case "ICCBased": return Pdf.Int(f.RDict(l.Count > 1 ? l[1] : null)?["N"], 3) switch { 1 => new CS(1, "gray"), 4 => new CS(4, "cmyk"), _ => new CS(3, "rgb") };
                        case "CalRGB": case "Lab": return new CS(3, "rgb");
                        case "CalGray": return new CS(1, "gray");
                        case "Separation": return new CS(1, "sep");
                        case "DeviceN": return new CS((f.Res(l.Count > 1 ? l[1] : null) as List<object?>)?.Count ?? 1, "sep");
                        case "Indexed": case "I":
                            {
                                var b = ColorSpace(f, f.Res(l.Count > 1 ? l[1] : null));
                                byte[] lk = f.Res(l.Count > 3 ? l[3] : null) switch
                                {
                                    PStr s => s.B,
                                    PStream ps => TryDecode(f, ps),
                                    _ => Array.Empty<byte>()
                                };
                                return new CS(1, "idx", lk, b.N);
                            }
                    }
                }
                return new CS(3, "rgb");
            }

            private static byte[] TryDecode(PdfFile f, PStream ps) { try { return f.Decode(ps, null); } catch { return Array.Empty<byte>(); } }

            /// <summary>Penyandi PNG kecil (RGB / RGBA 8-bit).</summary>
            public static byte[] Png(int[] px, int w, int h, bool alpha)
            {
                int ch = alpha ? 4 : 3;
                var raw = new byte[(w * ch + 1) * h];
                int p = 0;
                for (int y = 0; y < h; y++)
                {
                    raw[p++] = 0;
                    for (int x = 0; x < w; x++)
                    {
                        int c = px[y * w + x];
                        raw[p++] = (byte)(c >> 16); raw[p++] = (byte)(c >> 8); raw[p++] = (byte)c;
                        if (alpha) raw[p++] = (byte)((uint)c >> 24);
                    }
                }
                byte[] z;
                using (var ms = new MemoryStream())
                {
                    using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, true)) zs.Write(raw, 0, raw.Length);
                    z = ms.ToArray();
                }
                using var o = new MemoryStream();
                o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                void Chunk(string t, byte[] d)
                {
                    o.Write(Be(d.Length)); var tb = Encoding.ASCII.GetBytes(t); o.Write(tb); o.Write(d);
                    o.Write(Be((int)Crc32(tb, d)));
                }
                Chunk("IHDR", Be(w).Concat(Be(h)).Concat(new byte[] { 8, (byte)(alpha ? 6 : 2), 0, 0, 0 }).ToArray());
                Chunk("IDAT", z);
                Chunk("IEND", Array.Empty<byte>());
                return o.ToArray();
            }

            private static byte[] Be(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

            private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
            {
                uint c = (uint)n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                return c;
            }).ToArray();

            private static uint Crc32(byte[] a, byte[] b)
            {
                uint c = 0xFFFFFFFFu;
                foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
                foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
                return c ^ 0xFFFFFFFFu;
            }
        }

        // ------------------------------------------------------------------ pengodean & nama glif
        public static class Encodings
        {
            private static readonly Dictionary<int, string> WIN_HI = new()
            {
                [128] = "€", [130] = "‚", [131] = "ƒ", [132] = "„", [133] = "…", [134] = "†", [135] = "‡", [136] = "ˆ", [137] = "‰", [138] = "Š",
                [139] = "‹", [140] = "Œ", [142] = "Ž", [145] = "‘", [146] = "’", [147] = "“", [148] = "”", [149] = "•", [150] = "–", [151] = "—", [152] = "˜", [153] = "™",
                [154] = "š", [155] = "›", [156] = "œ", [158] = "ž", [159] = "Ÿ"
            };

            public static string?[] Base(string? name, bool symbolic)
            {
                var a = new string?[256];
                for (int c = 32; c <= 126; c++) a[c] = ((char)c).ToString();
                for (int c = 160; c <= 255; c++) a[c] = ((char)c).ToString();
                switch (name)
                {
                    case "MacRomanEncoding":
                        try
                        {
                            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                            var mac = Encoding.GetEncoding(10000);
                            for (int c = 128; c <= 255; c++) a[c] = mac.GetString(new[] { (byte)c });
                        }
                        catch { }
                        break;
                    case "StandardEncoding": case null:
                        if (!symbolic) { a[39] = "’"; a[96] = "‘"; foreach (var (k, v) in WIN_HI) a[k] = v; }
                        break;
                    default:
                        foreach (var (k, v) in WIN_HI) a[k] = v;
                        break;
                }
                if (name == "WinAnsiEncoding") { a[39] = "'"; a[96] = "`"; }
                return a;
            }

            private static readonly Lazy<Dictionary<string, string>> NAMES = new(() =>
            {
                var m = new Dictionary<string, string>();
                for (char c = 'a'; c <= 'z'; c++) m[c.ToString()] = c.ToString();
                for (char c = 'A'; c <= 'Z'; c++) m[c.ToString()] = c.ToString();
                string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
                for (int i = 0; i < digits.Length; i++) m[digits[i]] = i.ToString(CultureInfo.InvariantCulture);
                var pairs = "space: ,exclam:!,quotedbl:\",numbersign:#,dollar:$,percent:%,ampersand:&,quotesingle:',parenleft:(,parenright:),asterisk:*,plus:+,comma:\\,,hyphen:-,period:.,slash:/,colon::,semicolon:;,less:<,equal:=,greater:>,question:?,at:@,bracketleft:[,backslash:\\\\,bracketright:],asciicircum:^,underscore:_,grave:`,braceleft:{,bar:|,braceright:},asciitilde:~,quoteleft:‘,quoteright:’,quotedblleft:“,quotedblright:”,quotesinglbase:‚,quotedblbase:„,endash:–,emdash:—,bullet:•,ellipsis:…,dagger:†,daggerdbl:‡,perthousand:‰,trademark:™,copyright:©,registered:®,degree:°,plusminus:±,multiply:×,divide:÷,minus:−,section:§,paragraph:¶,periodcentered:·,cent:¢,sterling:£,yen:¥,Euro:€,euro:€,florin:ƒ,guillemotleft:«,guillemotright:»,guilsinglleft:‹,guilsinglright:›,exclamdown:¡,questiondown:¿,fi:fi,fl:fl,ff:ff,ffi:ffi,ffl:ffl,nbspace: ,uni00A0: ,dotlessi:ı,acute:´,dieresis:¨,macron:¯,cedilla:¸,circumflex:ˆ,tilde:˜,ring:˚,caron:ˇ,breve:˘,dotaccent:˙,ordfeminine:ª,ordmasculine:º,onehalf:½,onequarter:¼,threequarters:¾,mu:µ,logicalnot:¬,brokenbar:¦,currency:¤,germandbls:ß,ae:æ,AE:Æ,oe:œ,OE:Œ,oslash:ø,Oslash:Ø,eth:ð,Eth:Ð,thorn:þ,Thorn:Þ,lslash:ł,Lslash:Ł,scaron:š,Scaron:Š,zcaron:ž,Zcaron:Ž,ydieresis:ÿ,Ydieresis:Ÿ,arrowright:→,arrowleft:←,arrowup:↑,arrowdown:↓,check:✓,checkmark:✓,a20:✔";
                int p = 0;
                while (p < pairs.Length)
                {
                    int c = pairs.IndexOf(':', p);
                    int j = c + 1; var sb = new StringBuilder();
                    while (j < pairs.Length && pairs[j] != ',') { if (pairs[j] == '\\' && j + 1 < pairs.Length) { sb.Append(pairs[j + 1]); j += 2; } else sb.Append(pairs[j++]); }
                    m[pairs[p..c]] = sb.ToString();
                    p = j + 1;
                }
                var acc = new Dictionary<string, char> { ["grave"] = '̀', ["acute"] = '́', ["circumflex"] = '̂', ["tilde"] = '̃', ["dieresis"] = '̈', ["ring"] = '̊', ["cedilla"] = '̧', ["caron"] = '̌' };
                foreach (var bc in Enumerable.Range('a', 26).Concat(Enumerable.Range('A', 26)).Select(x => (char)x))
                    foreach (var (nm, mark) in acc)
                    {
                        var s = (bc.ToString() + mark).Normalize(NormalizationForm.FormC);
                        if (s.Length == 1) m[bc + nm] = s;
                    }
                return m;
            });

            public static string? Glyph(string n)
            {
                if (NAMES.Value.TryGetValue(n, out var v)) return v;
                var b = n.Split('.')[0].Split('_')[0];
                if (NAMES.Value.TryGetValue(b, out var v2)) return v2;
                try
                {
                    if (b.StartsWith("uni", StringComparison.Ordinal) && b.Length >= 7)
                        return string.Concat(Enumerable.Range(0, (b.Length - 3) / 4).Select(i => (char)Convert.ToInt32(b.Substring(3 + i * 4, 4), 16)));
                    if (b.StartsWith('u') && b.Length >= 5 && b.Length <= 7) return char.ConvertFromUtf32(Convert.ToInt32(b[1..], 16));
                }
                catch { }
                return null;
            }
        }
    }
}
