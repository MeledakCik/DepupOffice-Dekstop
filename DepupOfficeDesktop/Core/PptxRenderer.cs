using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Perender PowerPoint (.pptx) ke gambar, tanpa NuGet (pengganti PptxHtml.kt Android yang memakai WebView).
    /// Mendukung: ukuran slide, latar (warna/gambar/tema), bentuk dari master &amp; layout, pewarisan placeholder
    /// (posisi + gaya teks bertingkat), teks (ukuran/warna/tebal/miring/garis bawah, perataan, bullet, autofit),
    /// gambar (dengan crop), bentuk preset umum, garis/konektor, grup, dan tabel.
    /// </summary>
    public sealed class PptxRenderer : IDisposable
    {
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

        private readonly ZipArchive _zip;
        private readonly Dictionary<string, XDocument?> _xml = new();
        public readonly long Cx, Cy;
        public readonly List<string> SlidePaths = new();

        private sealed class Theme
        {
            public Dictionary<string, Color> Colors = new();
            public string Major = "Calibri", Minor = "Calibri";
        }

        private sealed class Ctx
        {
            public string SlidePath = "", LayoutPath = "", MasterPath = "";
            public XElement? Slide, Layout, Master;
            public Theme Theme = new();
            public Dictionary<string, string> ClrMap = new();
            public float Scale;                       // px per EMU
        }

        public PptxRenderer(string path) : this(File.ReadAllBytes(path)) { }

        public PptxRenderer(byte[] bytes)
        {
            _zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var pres = Xml("ppt/presentation.xml")?.Root ?? throw new InvalidDataException("Bukan berkas PowerPoint (.pptx) yang valid.");
            var sz = pres.Element(P + "sldSz");
            Cx = Long(sz?.Attribute("cx")?.Value, 9144000); Cy = Long(sz?.Attribute("cy")?.Value, 5143500);
            var rels = Rels("ppt/presentation.xml");
            foreach (var id in pres.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>())
            {
                var rid = id.Attribute(R + "id")?.Value;
                if (rid != null && rels.TryGetValue(rid, out var t)) SlidePaths.Add(t);
            }
        }

        public int SlideCount => SlidePaths.Count;
        public float Aspect => Cx / (float)Cy;
        public void Dispose() => _zip.Dispose();

        // ------------------------------------------------------------------ paket

        private static long Long(string? s, long d = 0) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
        private static double Dbl(string? s, double d = 0) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        private XDocument? Xml(string path)
        {
            if (_xml.TryGetValue(path, out var d)) return d;
            try
            {
                var e = _zip.GetEntry(path);
                d = e == null ? null : XDocument.Load(e.Open());
            }
            catch { d = null; }
            _xml[path] = d;
            return d;
        }

        private byte[]? Bytes(string path)
        {
            var e = _zip.GetEntry(path);
            if (e == null) return null;
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
        }

        private static string Resolve(string baseDir, string target)
        {
            if (target.StartsWith("/")) return target.TrimStart('/');
            var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (var seg in target.Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != ".") parts.Add(seg);
            }
            return string.Join("/", parts);
        }

        private readonly Dictionary<string, Dictionary<string, string>> _rels = new();
        private Dictionary<string, string> Rels(string part)
        {
            if (_rels.TryGetValue(part, out var r)) return r;
            r = new Dictionary<string, string>();
            string dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
            string file = part[(part.LastIndexOf('/') + 1)..];
            var doc = Xml((dir.Length > 0 ? dir + "/" : "") + "_rels/" + file + ".rels");
            foreach (var e in doc?.Root?.Elements(PR + "Relationship") ?? Enumerable.Empty<XElement>())
            {
                if (e.Attribute("TargetMode")?.Value == "External") continue;
                var id = e.Attribute("Id")?.Value; var t = e.Attribute("Target")?.Value;
                if (id != null && t != null) r[id] = Resolve(dir, t);
            }
            _rels[part] = r;
            return r;
        }

        private string? RelOfType(string part, string typeSuffix)
        {
            string dir = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
            string file = part[(part.LastIndexOf('/') + 1)..];
            var doc = Xml((dir.Length > 0 ? dir + "/" : "") + "_rels/" + file + ".rels");
            var e = doc?.Root?.Elements(PR + "Relationship").FirstOrDefault(x => (x.Attribute("Type")?.Value ?? "").EndsWith(typeSuffix));
            var t = e?.Attribute("Target")?.Value;
            return t == null ? null : Resolve(dir, t);
        }

        private Ctx CtxFor(int i, float scale)
        {
            var c = new Ctx { SlidePath = SlidePaths[i], Scale = scale };
            c.Slide = Xml(c.SlidePath)?.Root;
            c.LayoutPath = RelOfType(c.SlidePath, "/slideLayout") ?? "";
            c.Layout = c.LayoutPath.Length > 0 ? Xml(c.LayoutPath)?.Root : null;
            c.MasterPath = c.LayoutPath.Length > 0 ? RelOfType(c.LayoutPath, "/slideMaster") ?? "" : "";
            c.Master = c.MasterPath.Length > 0 ? Xml(c.MasterPath)?.Root : null;
            var themePath = c.MasterPath.Length > 0 ? RelOfType(c.MasterPath, "/theme") : null;
            c.Theme = LoadTheme(themePath);
            var map = c.Master?.Element(P + "clrMap");
            var over = c.Slide?.Element(P + "clrMapOvr")?.Element(A + "overrideClrMapping") ?? c.Layout?.Element(P + "clrMapOvr")?.Element(A + "overrideClrMapping");
            foreach (var a in (over ?? map)?.Attributes() ?? Enumerable.Empty<XAttribute>()) c.ClrMap[a.Name.LocalName] = a.Value;
            return c;
        }

        private readonly Dictionary<string, Theme> _themes = new();
        private Theme LoadTheme(string? path)
        {
            if (path == null) return new Theme();
            if (_themes.TryGetValue(path, out var t)) return t;
            t = new Theme();
            var root = Xml(path)?.Root;
            var cs = root?.Descendants(A + "clrScheme").FirstOrDefault();
            foreach (var e in cs?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var v = e.Element(A + "srgbClr")?.Attribute("val")?.Value ?? e.Element(A + "sysClr")?.Attribute("lastClr")?.Value;
                if (v != null && v.Length == 6) t.Colors[e.Name.LocalName] = Hex(v);
            }
            var fs = root?.Descendants(A + "fontScheme").FirstOrDefault();
            t.Major = fs?.Element(A + "majorFont")?.Element(A + "latin")?.Attribute("typeface")?.Value ?? "Calibri";
            t.Minor = fs?.Element(A + "minorFont")?.Element(A + "latin")?.Attribute("typeface")?.Value ?? "Calibri";
            _themes[path] = t;
            return t;
        }

        // ------------------------------------------------------------------ warna

        private static Color Hex(string v) => Color.FromArgb(255, Convert.ToInt32(v[..2], 16), Convert.ToInt32(v.Substring(2, 2), 16), Convert.ToInt32(v.Substring(4, 2), 16));

        /// <summary>Warna dari elemen pemegang (solidFill / fontRef dll.).</summary>
        private Color? ColorOf(Ctx c, XElement? holder, Color? phClr = null)
        {
            if (holder == null) return null;
            var cn = holder.Elements().FirstOrDefault(e => e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" or "prstClr" or "scrgbClr" or "hslClr");
            if (cn == null) return null;
            Color baseC;
            switch (cn.Name.LocalName)
            {
                case "srgbClr": baseC = Hex(cn.Attribute("val")?.Value ?? "000000"); break;
                case "sysClr": baseC = Hex(cn.Attribute("lastClr")?.Value ?? "000000"); break;
                case "prstClr": baseC = Color.FromName(cn.Attribute("val")?.Value ?? "Black"); break;
                case "scrgbClr":
                    baseC = Color.FromArgb((int)Math.Clamp(Dbl(cn.Attribute("r")?.Value) / 100000 * 255, 0, 255), (int)Math.Clamp(Dbl(cn.Attribute("g")?.Value) / 100000 * 255, 0, 255), (int)Math.Clamp(Dbl(cn.Attribute("b")?.Value) / 100000 * 255, 0, 255)); break;
                case "schemeClr":
                    {
                        var v = cn.Attribute("val")?.Value ?? "tx1";
                        if (v == "phClr") { baseC = phClr ?? Color.Black; break; }
                        if (c.ClrMap.TryGetValue(v, out var m)) v = m;
                        v = v switch { "bg1" => "lt1", "tx1" => "dk1", "bg2" => "lt2", "tx2" => "dk2", _ => v };
                        baseC = c.Theme.Colors.TryGetValue(v, out var tc) ? tc : (v.StartsWith("lt") ? Color.White : Color.Black);
                        break;
                    }
                default: baseC = Color.Black; break;
            }
            return Mods(baseC, cn);
        }

        private static Color Mods(Color col, XElement cn)
        {
            double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0; int alpha = 255;
            foreach (var m in cn.Elements())
            {
                double v = Dbl(m.Attribute("val")?.Value) / 100000.0;
                switch (m.Name.LocalName)
                {
                    case "alpha": alpha = (int)Math.Clamp(v * 255, 0, 255); break;
                    case "tint": r += (1 - r) * (1 - v); g += (1 - g) * (1 - v); b += (1 - b) * (1 - v); break;
                    case "shade": r *= v; g *= v; b *= v; break;
                    case "lumMod":
                    case "lumOff":
                        {
                            RgbToHsl(r, g, b, out var h, out var s, out var l);
                            if (m.Name.LocalName == "lumMod") l *= v; else l += v;
                            HslToRgb(h, s, Math.Clamp(l, 0, 1), out r, out g, out b);
                            break;
                        }
                }
            }
            return Color.FromArgb(alpha, (int)Math.Clamp(r * 255, 0, 255), (int)Math.Clamp(g * 255, 0, 255), (int)Math.Clamp(b * 255, 0, 255));
        }

        private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            if (max == min) { h = s = 0; return; }
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
        {
            if (s == 0) { r = g = b = l; return; }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            static double Hue(double p, double q, double t) { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1 / 6.0) return p + (q - p) * 6 * t; if (t < 0.5) return q; if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6; return p; }
            r = Hue(p, q, h + 1 / 3.0); g = Hue(p, q, h); b = Hue(p, q, h - 1 / 3.0);
        }

        // ------------------------------------------------------------------ render

        public Bitmap RenderSlide(int index, int widthPx)
        {
            widthPx = Math.Max(64, widthPx);
            int h = Math.Max(36, (int)Math.Round(widthPx * Cy / (double)Cx));
            var bmp = new Bitmap(widthPx, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.White);
            if (index < 0 || index >= SlidePaths.Count) return bmp;
            var c = CtxFor(index, widthPx / (float)Cx);
            try
            {
                Background(g, c, new RectangleF(0, 0, widthPx, h));
                bool showMaster = c.Slide?.Attribute("showMasterSp")?.Value != "0" && c.Layout?.Attribute("showMasterSp")?.Value != "0";
                if (showMaster && c.Master != null) Tree(g, c, c.Master.Element(P + "cSld")?.Element(P + "spTree"), c.MasterPath, Xf.Id, layer: 2);
                if (c.Layout != null && c.Slide?.Attribute("showMasterSp")?.Value != "0") Tree(g, c, c.Layout.Element(P + "cSld")?.Element(P + "spTree"), c.LayoutPath, Xf.Id, layer: 1);
                Tree(g, c, c.Slide?.Element(P + "cSld")?.Element(P + "spTree"), c.SlidePath, Xf.Id, layer: 0);
            }
            catch (Exception ex)
            {
                using var f = new Font("Segoe UI", Math.Max(9, widthPx / 60f), GraphicsUnit.Pixel);
                g.DrawString("Sebagian isi slide tidak dapat ditampilkan: " + ex.Message, f, Brushes.DarkRed, 10, 10);
            }
            return bmp;
        }

        private void Background(Graphics g, Ctx c, RectangleF r)
        {
            foreach (var (root, part) in new[] { (c.Slide, c.SlidePath), (c.Layout, c.LayoutPath), (c.Master, c.MasterPath) })
            {
                var bg = root?.Element(P + "cSld")?.Element(P + "bg");
                if (bg == null) continue;
                var pr = bg.Element(P + "bgPr");
                if (pr != null)
                {
                    if (FillShape(g, c, pr, part, r, null, null)) return;
                }
                var refE = bg.Element(P + "bgRef");
                if (refE != null)
                {
                    var col = ColorOf(c, refE);
                    if (col != null) { using var b = new SolidBrush(col.Value); g.FillRectangle(b, r); return; }
                }
            }
        }

        /// <summary>Isi area dengan fill dari <paramref name="spPr"/> (solid / gradien / gambar). False bila tidak ada fill.</summary>
        private bool FillShape(Graphics g, Ctx c, XElement spPr, string part, RectangleF r, GraphicsPath? path, XElement? styleFillRef)
        {
            if (spPr.Element(A + "noFill") != null) return true;
            var solid = spPr.Element(A + "solidFill");
            var grad = spPr.Element(A + "gradFill");
            var blip = spPr.Element(A + "blipFill");
            Brush? br = null;
            try
            {
                if (solid != null) { var col = ColorOf(c, solid); if (col != null) br = new SolidBrush(col.Value); }
                else if (grad != null)
                {
                    var stops = grad.Element(A + "gsLst")?.Elements(A + "gs").Select(s => (pos: Dbl(s.Attribute("pos")?.Value) / 100000.0, col: ColorOf(c, s) ?? Color.White)).OrderBy(s => s.pos).ToList();
                    if (stops != null && stops.Count >= 2 && r.Width > 0 && r.Height > 0)
                    {
                        float ang = (float)(Dbl(grad.Element(A + "lin")?.Attribute("ang")?.Value, 5400000) / 60000.0);
                        var lb = new LinearGradientBrush(RectangleF.Inflate(r, 1, 1), stops[0].col, stops[^1].col, ang);
                        lb.InterpolationColors = new ColorBlend { Colors = stops.Select(s => s.col).ToArray(), Positions = stops.Select(s => (float)s.pos).Select((p, i) => i == 0 ? 0f : i == stops.Count - 1 ? 1f : p).ToArray() };
                        br = lb;
                    }
                    else if (stops != null && stops.Count > 0) br = new SolidBrush(stops[0].col);
                }
                else if (blip != null)
                {
                    using var img = Image(part, blip.Element(A + "blip")?.Attribute(R + "embed")?.Value);
                    if (img != null)
                    {
                        var st = g.Save();
                        if (path != null) g.SetClip(path);
                        g.DrawImage(img, r);
                        g.Restore(st);
                        return true;
                    }
                }
                else if (styleFillRef != null && Long(styleFillRef.Attribute("idx")?.Value) > 0)
                {
                    var col = ColorOf(c, styleFillRef); if (col != null) br = new SolidBrush(col.Value);
                }
                if (br == null) return false;
                if (path != null) g.FillPath(br, path); else g.FillRectangle(br, r);
                return true;
            }
            finally { br?.Dispose(); }
        }

        private Bitmap? Image(string part, string? rid)
        {
            if (rid == null || !Rels(part).TryGetValue(rid, out var t)) return null;
            var b = Bytes(t);
            if (b == null) return null;
            try { using var ms = new MemoryStream(b); using var im = System.Drawing.Image.FromStream(ms); return new Bitmap(im); } catch { return null; }
        }

        // ------------------------------------------------------------------ transformasi grup

        private readonly struct Xf
        {
            public readonly double Ox, Oy, Sx, Sy;   // EMU anak → EMU slide
            public Xf(double ox, double oy, double sx, double sy) { Ox = ox; Oy = oy; Sx = sx; Sy = sy; }
            public static Xf Id => new(0, 0, 1, 1);
            public (double x, double y) Map(double x, double y) => (Ox + x * Sx, Oy + y * Sy);
        }

        private RectangleF? BoxOf(Ctx c, XElement? xfrm, Xf xf)
        {
            var off = xfrm?.Element(A + "off"); var ext = xfrm?.Element(A + "ext");
            if (off == null || ext == null) return null;
            var (x, y) = xf.Map(Long(off.Attribute("x")?.Value), Long(off.Attribute("y")?.Value));
            double w = Long(ext.Attribute("cx")?.Value) * xf.Sx, h = Long(ext.Attribute("cy")?.Value) * xf.Sy;
            return new RectangleF((float)(x * c.Scale), (float)(y * c.Scale), (float)(w * c.Scale), (float)(h * c.Scale));
        }

        private void Tree(Graphics g, Ctx c, XElement? tree, string part, Xf xf, int layer)
        {
            if (tree == null) return;
            foreach (var e in tree.Elements())
            {
                try
                {
                    switch (e.Name.LocalName)
                    {
                        case "sp": Shape(g, c, e, part, xf, layer); break;
                        case "pic": Picture(g, c, e, part, xf, layer); break;
                        case "cxnSp": Shape(g, c, e, part, xf, layer); break;
                        case "grpSp":
                            {
                                var gx = e.Element(P + "grpSpPr")?.Element(A + "xfrm");
                                if (gx == null) { Tree(g, c, e, part, xf, layer); break; }
                                double ox = Long(gx.Element(A + "off")?.Attribute("x")?.Value), oy = Long(gx.Element(A + "off")?.Attribute("y")?.Value);
                                double ex = Long(gx.Element(A + "ext")?.Attribute("cx")?.Value, 1), ey = Long(gx.Element(A + "ext")?.Attribute("cy")?.Value, 1);
                                double cx = Long(gx.Element(A + "chOff")?.Attribute("x")?.Value), cy = Long(gx.Element(A + "chOff")?.Attribute("y")?.Value);
                                double cex = Long(gx.Element(A + "chExt")?.Attribute("cx")?.Value, (long)ex), cey = Long(gx.Element(A + "chExt")?.Attribute("cy")?.Value, (long)ey);
                                double sx = cex == 0 ? 1 : ex / cex, sy = cey == 0 ? 1 : ey / cey;
                                var (mx, my) = xf.Map(ox - cx * sx, oy - cy * sy);
                                Tree(g, c, e, part, new Xf(mx, my, xf.Sx * sx, xf.Sy * sy), layer);
                                break;
                            }
                        case "graphicFrame": Frame(g, c, e, part, xf); break;
                        case "AlternateContent":
                            {
                                var fb = e.Elements().FirstOrDefault(x => x.Name.LocalName == "Fallback");
                                if (fb != null) Tree(g, c, fb, part, xf, layer);
                                break;
                            }
                    }
                }
                catch { /* satu bentuk rusak tidak boleh menggagalkan seluruh slide */ }
            }
        }

        // placeholder ------------------------------------------------------------

        private static XElement? PhOf(XElement sp) => sp.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("nv"))?.Element(P + "nvPr")?.Element(P + "ph");
        private static string PhType(XElement? ph) => ph?.Attribute("type")?.Value switch { null or "obj" => "body", "ctrTitle" => "title", "subTitle" => "body", var t => t! };

        private static XElement? FindPh(XElement? root, XElement ph)
        {
            if (root == null) return null;
            var tree = root.Element(P + "cSld")?.Element(P + "spTree");
            if (tree == null) return null;
            string? idx = ph.Attribute("idx")?.Value; string type = PhType(ph); string? rawType = ph.Attribute("type")?.Value;
            var cands = tree.Descendants(P + "sp").Where(s => PhOf(s) != null).ToList();
            return cands.FirstOrDefault(s => idx != null && PhOf(s)!.Attribute("idx")?.Value == idx && (rawType == null || PhType(PhOf(s)) == type))
                ?? cands.FirstOrDefault(s => rawType != null && PhOf(s)!.Attribute("type")?.Value == rawType)
                ?? cands.FirstOrDefault(s => PhType(PhOf(s)) == type)
                ?? cands.FirstOrDefault(s => idx != null && PhOf(s)!.Attribute("idx")?.Value == idx);
        }

        private void Shape(Graphics g, Ctx c, XElement sp, string part, Xf xf, int layer)
        {
            var ph = PhOf(sp);
            // placeholder di master/layout hanyalah posisi contoh, tidak digambar (kecuali teks tanggal/nomor/footer kosong)
            if (layer > 0 && ph != null) return;
            XElement? layoutPh = null, masterPh = null;
            if (ph != null && layer == 0) { layoutPh = FindPh(c.Layout, ph); masterPh = FindPh(c.Master, layoutPh != null ? PhOf(layoutPh)! : ph); }
            var spPr = sp.Element(P + "spPr");
            var box = BoxOf(c, spPr?.Element(A + "xfrm"), xf)
                      ?? BoxOf(c, layoutPh?.Element(P + "spPr")?.Element(A + "xfrm"), Xf.Id)
                      ?? BoxOf(c, masterPh?.Element(P + "spPr")?.Element(A + "xfrm"), Xf.Id);
            if (box == null) return;
            var r = box.Value;
            var xfrm = spPr?.Element(A + "xfrm");
            float rot = (float)(Dbl(xfrm?.Attribute("rot")?.Value) / 60000.0);
            bool flipH = xfrm?.Attribute("flipH")?.Value == "1", flipV = xfrm?.Attribute("flipV")?.Value == "1";
            var st = g.Save();
            if (rot != 0 || flipH || flipV)
            {
                g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
                if (rot != 0) g.RotateTransform(rot);
                if (flipH || flipV) g.ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1);
                g.TranslateTransform(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
            }
            var style = sp.Element(P + "style");
            string prst = spPr?.Element(A + "prstGeom")?.Attribute("prst")?.Value ?? (spPr?.Element(A + "custGeom") != null ? "rect" : "rect");
            bool isLine = sp.Name.LocalName == "cxnSp" || prst is "line" or "straightConnector1" or "bentConnector3" or "curvedConnector3";
            using (var path = Geometry(prst, r, spPr))
            {
                if (!isLine && spPr != null) FillShape(g, c, spPr, part, r, path, style?.Element(A + "fillRef"));
                else if (!isLine && style != null) FillShape(g, c, new XElement(P + "spPr"), part, r, path, style.Element(A + "fillRef"));
                var ln = spPr?.Element(A + "ln");
                using var pen = PenOf(c, ln, style?.Element(A + "lnRef"), isLine);
                if (pen != null)
                {
                    if (isLine) g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                    else g.DrawPath(pen, path);
                }
            }
            var tx = sp.Element(P + "txBody");
            if (tx != null) Text(g, c, tx, r, ph, layoutPh, masterPh, style?.Element(A + "fontRef"));
            g.Restore(st);
        }

        private Pen? PenOf(Ctx c, XElement? ln, XElement? lnRef, bool isLine)
        {
            if (ln?.Element(A + "noFill") != null) return null;
            Color? col = ColorOf(c, ln?.Element(A + "solidFill"));
            if (col == null && lnRef != null && Long(lnRef.Attribute("idx")?.Value) > 0) col = ColorOf(c, lnRef);
            if (col == null) return null;
            float w = Math.Max(1f, (float)(Long(ln?.Attribute("w")?.Value, 9525) * c.Scale));
            var p = new Pen(col.Value, w);
            var dash = ln?.Element(A + "prstDash")?.Attribute("val")?.Value;
            if (dash is "dash" or "lgDash" or "sysDash") p.DashStyle = DashStyle.Dash; else if (dash is "dot" or "sysDot") p.DashStyle = DashStyle.Dot;
            if (ln?.Element(A + "tailEnd")?.Attribute("type")?.Value is string te && te != "none") p.CustomEndCap = new AdjustableArrowCap(4, 4);
            return p;
        }

        private static GraphicsPath Geometry(string prst, RectangleF r, XElement? spPr)
        {
            var p = new GraphicsPath();
            double Adj(string n, double d)
            {
                var gd = spPr?.Element(A + "prstGeom")?.Element(A + "avLst")?.Elements(A + "gd").FirstOrDefault(x => x.Attribute("name")?.Value == n);
                var f = gd?.Attribute("fmla")?.Value;
                return f != null && f.StartsWith("val ") ? Dbl(f[4..], d) : d;
            }
            float x = r.X, y = r.Y, w = Math.Max(0.5f, r.Width), h = Math.Max(0.5f, r.Height);
            switch (prst)
            {
                case "ellipse": case "flowChartConnector": p.AddEllipse(x, y, w, h); break;
                case "roundRect": case "flowChartAlternateProcess":
                    {
                        float rad = (float)(Math.Min(w, h) * Adj("adj", 16667) / 100000.0) * 2;
                        rad = Math.Max(1, Math.Min(rad, Math.Min(w, h)));
                        p.AddArc(x, y, rad, rad, 180, 90); p.AddArc(x + w - rad, y, rad, rad, 270, 90);
                        p.AddArc(x + w - rad, y + h - rad, rad, rad, 0, 90); p.AddArc(x, y + h - rad, rad, rad, 90, 90); p.CloseFigure();
                        break;
                    }
                case "triangle": p.AddPolygon(new[] { new PointF(x + w * (float)(Adj("adj", 50000) / 100000), y), new PointF(x + w, y + h), new PointF(x, y + h) }); break;
                case "rtTriangle": p.AddPolygon(new[] { new PointF(x, y), new PointF(x + w, y + h), new PointF(x, y + h) }); break;
                case "diamond": case "flowChartDecision": p.AddPolygon(new[] { new PointF(x + w / 2, y), new PointF(x + w, y + h / 2), new PointF(x + w / 2, y + h), new PointF(x, y + h / 2) }); break;
                case "parallelogram": { float o = w * 0.25f; p.AddPolygon(new[] { new PointF(x + o, y), new PointF(x + w, y), new PointF(x + w - o, y + h), new PointF(x, y + h) }); break; }
                case "trapezoid": { float o = w * 0.25f; p.AddPolygon(new[] { new PointF(x + o, y), new PointF(x + w - o, y), new PointF(x + w, y + h), new PointF(x, y + h) }); break; }
                case "hexagon": { float o = w * 0.25f; p.AddPolygon(new[] { new PointF(x + o, y), new PointF(x + w - o, y), new PointF(x + w, y + h / 2), new PointF(x + w - o, y + h), new PointF(x + o, y + h), new PointF(x, y + h / 2) }); break; }
                case "pentagon": case "homePlate": { float o = w * 0.2f; p.AddPolygon(new[] { new PointF(x, y), new PointF(x + w - o, y), new PointF(x + w, y + h / 2), new PointF(x + w - o, y + h), new PointF(x, y + h) }); break; }
                case "chevron": { float o = w * 0.25f; p.AddPolygon(new[] { new PointF(x, y), new PointF(x + w - o, y), new PointF(x + w, y + h / 2), new PointF(x + w - o, y + h), new PointF(x, y + h), new PointF(x + o, y + h / 2) }); break; }
                case "rightArrow": { float hy = h * 0.25f, hx = w * 0.35f; p.AddPolygon(new[] { new PointF(x, y + hy), new PointF(x + w - hx, y + hy), new PointF(x + w - hx, y), new PointF(x + w, y + h / 2), new PointF(x + w - hx, y + h), new PointF(x + w - hx, y + h - hy), new PointF(x, y + h - hy) }); break; }
                case "leftArrow": { float hy = h * 0.25f, hx = w * 0.35f; p.AddPolygon(new[] { new PointF(x + w, y + hy), new PointF(x + hx, y + hy), new PointF(x + hx, y), new PointF(x, y + h / 2), new PointF(x + hx, y + h), new PointF(x + hx, y + h - hy), new PointF(x + w, y + h - hy) }); break; }
                case "star5":
                    {
                        var pts = new PointF[10];
                        for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float rr = i % 2 == 0 ? 1f : 0.4f; pts[i] = new PointF(x + w / 2 + (float)Math.Cos(a) * w / 2 * rr, y + h / 2 + (float)Math.Sin(a) * h / 2 * rr); }
                        p.AddPolygon(pts); break;
                    }
                default: p.AddRectangle(new RectangleF(x, y, w, h)); break;
            }
            return p;
        }

        // gambar -----------------------------------------------------------------

        private void Picture(Graphics g, Ctx c, XElement pic, string part, Xf xf, int layer)
        {
            var spPr = pic.Element(P + "spPr");
            var box = BoxOf(c, spPr?.Element(A + "xfrm"), xf);
            var ph = PhOf(pic);
            if (box == null && ph != null) box = BoxOf(c, FindPh(c.Layout, ph)?.Element(P + "spPr")?.Element(A + "xfrm"), Xf.Id) ?? BoxOf(c, FindPh(c.Master, ph)?.Element(P + "spPr")?.Element(A + "xfrm"), Xf.Id);
            if (box == null) return;
            var bf = pic.Element(P + "blipFill");
            using var img = Image(part, bf?.Element(A + "blip")?.Attribute(R + "embed")?.Value);
            if (img == null) return;
            var src = new RectangleF(0, 0, img.Width, img.Height);
            var sr = bf?.Element(A + "srcRect");
            if (sr != null)
            {
                double l = Dbl(sr.Attribute("l")?.Value) / 100000, t = Dbl(sr.Attribute("t")?.Value) / 100000, rr = Dbl(sr.Attribute("r")?.Value) / 100000, b = Dbl(sr.Attribute("b")?.Value) / 100000;
                src = new RectangleF((float)(l * img.Width), (float)(t * img.Height), (float)((1 - l - rr) * img.Width), (float)((1 - t - b) * img.Height));
            }
            var r = box.Value;
            var xfrm = spPr?.Element(A + "xfrm");
            float rot = (float)(Dbl(xfrm?.Attribute("rot")?.Value) / 60000.0);
            var st = g.Save();
            if (rot != 0) { g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2); g.RotateTransform(rot); g.TranslateTransform(-(r.X + r.Width / 2), -(r.Y + r.Height / 2)); }
            string prst = spPr?.Element(A + "prstGeom")?.Attribute("prst")?.Value ?? "rect";
            if (prst != "rect") { using var path = Geometry(prst, r, spPr); g.SetClip(path); }
            g.DrawImage(img, r, src, GraphicsUnit.Pixel);
            g.ResetClip();
            using (var pen = PenOf(c, spPr?.Element(A + "ln"), null, false)) if (pen != null) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            g.Restore(st);
        }

        // bingkai grafis (tabel / grafik) ------------------------------------------

        private void Frame(Graphics g, Ctx c, XElement gf, string part, Xf xf)
        {
            var box = BoxOf(c, gf.Element(P + "xfrm"), xf);
            if (box == null) return;
            var r = box.Value;
            var gd = gf.Element(A + "graphic")?.Element(A + "graphicData");
            var tbl = gd?.Element(A + "tbl");
            if (tbl != null) { Table(g, c, tbl, r); return; }
            // grafik / SmartArt / objek: kotak penanda
            using var pen = new Pen(Color.FromArgb(160, 160, 160)) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            string label = (gd?.Attribute("uri")?.Value ?? "").Contains("chart") ? "[Grafik]" : (gd?.Attribute("uri")?.Value ?? "").Contains("diagram") ? "[SmartArt]" : "[Objek]";
            using var f = new Font("Segoe UI", Math.Max(8, r.Height / 12), GraphicsUnit.Pixel);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, f, Brushes.Gray, r, sf);
        }

        private void Table(Graphics g, Ctx c, XElement tbl, RectangleF r)
        {
            var cols = tbl.Element(A + "tblGrid")?.Elements(A + "gridCol").Select(x => Long(x.Attribute("w")?.Value) * c.Scale).ToList() ?? new List<float>();
            float y = r.Y;
            bool firstRow = tbl.Element(A + "tblPr")?.Attribute("firstRow")?.Value == "1";
            int ri = 0;
            using var border = new Pen(Color.FromArgb(190, 190, 190), Math.Max(1, 9525 * c.Scale));
            foreach (var tr in tbl.Elements(A + "tr"))
            {
                float rh = Long(tr.Attribute("h")?.Value) * c.Scale;
                float x = r.X; int ci = 0;
                // tinggi baris minimal sesuai isi
                foreach (var tc in tr.Elements(A + "tc"))
                {
                    if (tc.Attribute("hMerge")?.Value == "1" || tc.Attribute("vMerge")?.Value == "1") { x += ci < cols.Count ? cols[ci] : 0; ci++; continue; }
                    int span = (int)Long(tc.Attribute("gridSpan")?.Value, 1);
                    float cw = 0; for (int k = 0; k < span && ci + k < cols.Count; k++) cw += cols[ci + k];
                    int rspan = (int)Long(tc.Attribute("rowSpan")?.Value, 1);
                    float ch = rh;
                    if (rspan > 1) { var next = tr.ElementsAfterSelf(A + "tr").Take(rspan - 1); ch += next.Sum(n => Long(n.Attribute("h")?.Value) * c.Scale); }
                    var cell = new RectangleF(x, y, cw, ch);
                    var tcPr = tc.Element(A + "tcPr");
                    if (tcPr == null || !FillShape(g, c, tcPr, "", cell, null, null))
                        if (firstRow && ri == 0) using (var b = new SolidBrush(c.Theme.Colors.GetValueOrDefault("accent1", Color.SteelBlue))) g.FillRectangle(b, cell);
                    g.DrawRectangle(border, cell.X, cell.Y, cell.Width, cell.Height);
                    var tx = tc.Element(A + "txBody");
                    if (tx != null) Text(g, c, tx, cell, null, null, null, null, defaultColor: firstRow && ri == 0 && tcPr?.Element(A + "solidFill") == null ? Color.White : null, cellInset: true);
                    x += cw; ci += span;
                }
                y += rh; ri++;
            }
        }

        // ------------------------------------------------------------------ teks

        private sealed class RunStyle
        {
            public float? Size; public bool? Bold, Italic, Underline; public Color? Color; public string? Font;
            public RunStyle Over(RunStyle? o)
            {
                if (o == null) return this;
                return new RunStyle { Size = o.Size ?? Size, Bold = o.Bold ?? Bold, Italic = o.Italic ?? Italic, Underline = o.Underline ?? Underline, Color = o.Color ?? Color, Font = o.Font ?? Font };
            }
        }
        private sealed class ParaStyle { public string? Algn; public bool? BulletNone; public string? BuChar; public bool? AutoNum; public long? MarL, Indent; }

        private RunStyle? ParseRun(Ctx c, XElement? rpr)
        {
            if (rpr == null) return null;
            var s = new RunStyle();
            if (rpr.Attribute("sz")?.Value is string sz) s.Size = (float)(Dbl(sz) / 100.0);
            if (rpr.Attribute("b")?.Value is string b) s.Bold = b == "1" || b == "true";
            if (rpr.Attribute("i")?.Value is string i) s.Italic = i == "1" || i == "true";
            if (rpr.Attribute("u")?.Value is string u) s.Underline = u != "none";
            var col = ColorOf(c, rpr.Element(A + "solidFill")); if (col != null) s.Color = col;
            var latin = rpr.Element(A + "latin")?.Attribute("typeface")?.Value;
            if (latin != null) s.Font = latin.StartsWith("+mj") ? c.Theme.Major : latin.StartsWith("+mn") ? c.Theme.Minor : latin;
            return s;
        }

        private static ParaStyle? ParsePara(XElement? ppr)
        {
            if (ppr == null) return null;
            return new ParaStyle
            {
                Algn = ppr.Attribute("algn")?.Value,
                BulletNone = ppr.Element(A + "buNone") != null ? true : (ppr.Element(A + "buChar") != null || ppr.Element(A + "buAutoNum") != null ? false : null),
                BuChar = ppr.Element(A + "buChar")?.Attribute("char")?.Value,
                AutoNum = ppr.Element(A + "buAutoNum") != null ? true : null,
                MarL = ppr.Attribute("marL") != null ? Long(ppr.Attribute("marL")!.Value) : null,
                Indent = ppr.Attribute("indent") != null ? Long(ppr.Attribute("indent")!.Value) : null
            };
        }

        private static XElement? LvlPr(XElement? lst, int lvl) => lst?.Element(A + $"lvl{lvl + 1}pPr");

        private void Text(Graphics g, Ctx c, XElement tx, RectangleF box, XElement? ph, XElement? layoutPh, XElement? masterPh, XElement? fontRef, Color? defaultColor = null, bool cellInset = false)
        {
            var bodyPr = tx.Element(A + "bodyPr");
            var lbp = layoutPh?.Element(P + "txBody")?.Element(A + "bodyPr");
            var mbp = masterPh?.Element(P + "txBody")?.Element(A + "bodyPr");
            string? Attr(string n) => bodyPr?.Attribute(n)?.Value ?? lbp?.Attribute(n)?.Value ?? mbp?.Attribute(n)?.Value;
            float li = Long(Attr("lIns"), cellInset ? 91440 : 91440) * c.Scale, ri = Long(Attr("rIns"), 91440) * c.Scale;
            float ti = Long(Attr("tIns"), 45720) * c.Scale, bi = Long(Attr("bIns"), 45720) * c.Scale;
            string anchor = Attr("anchor") ?? (PhType(ph) == "title" && ph?.Attribute("type")?.Value != "ctrTitle" ? "ctr" : "t");
            if (ph?.Attribute("type")?.Value == "ctrTitle") anchor = Attr("anchor") ?? "b";
            bool vert = (Attr("vert") ?? "horz") is "vert" or "vert270" or "eaVert";
            float fontScale = 1f;
            var na = bodyPr?.Element(A + "normAutofit") ?? lbp?.Element(A + "normAutofit");
            if (na?.Attribute("fontScale")?.Value is string fsv) fontScale = (float)(Dbl(fsv) / 100000.0);
            bool wrap = Attr("wrap") != "none";
            var inner = new RectangleF(box.X + li, box.Y + ti, Math.Max(4, box.Width - li - ri), Math.Max(4, box.Height - ti - bi));

            // sumber gaya per level: lstStyle bentuk → placeholder layout → placeholder master → txStyles master
            string phType = PhType(ph);
            var txStyles = c.Master?.Element(P + "txStyles");
            XElement? masterStyle = ph == null ? null : phType == "title" ? txStyles?.Element(P + "titleStyle") : phType is "body" ? txStyles?.Element(P + "bodyStyle") : txStyles?.Element(P + "otherStyle");
            // bentuk biasa memakai defaultTextStyle presentasi (bukan otherStyle master)
            if (ph == null) masterStyle = Xml("ppt/presentation.xml")?.Root?.Element(P + "defaultTextStyle") ?? txStyles?.Element(P + "otherStyle");
            Color? forceColor = defaultColor ?? (ph == null ? ColorOf(c, fontRef) : null);
            var lsts = new[] { tx.Element(A + "lstStyle"), layoutPh?.Element(P + "txBody")?.Element(A + "lstStyle"), masterPh?.Element(P + "txBody")?.Element(A + "lstStyle"), masterStyle };

            var baseRun = new RunStyle { Size = 18, Bold = false, Italic = false, Underline = false, Color = defaultColor ?? ColorOf(c, fontRef) ?? ColorOf(c, new XElement(A + "x", new XElement(A + "schemeClr", new XAttribute("val", "tx1")))) ?? Color.Black, Font = phType == "title" && ph != null ? c.Theme.Major : c.Theme.Minor };
            if (fontRef?.Attribute("idx")?.Value == "major") baseRun.Font = c.Theme.Major;

            // tata letak baris
            var lines = new List<(List<(string text, RunStyle st)> runs, float width, float height, float ascent, string algn, float indent)>();
            int autoNum = 0;
            foreach (var p in tx.Elements(A + "p"))
            {
                var ppr = p.Element(A + "pPr");
                int lvl = (int)Long(ppr?.Attribute("lvl")?.Value, 0);
                var ps = new ParaStyle(); RunStyle rs = baseRun;
                foreach (var lst in lsts.Reverse())
                {
                    var lp = LvlPr(lst, lvl);
                    var pp = ParsePara(lp);
                    if (pp != null) ps = MergeP(ps, pp);
                    rs = rs.Over(ParseRun(c, lp?.Element(A + "defRPr")));
                }
                if (forceColor != null) rs = rs.Over(new RunStyle { Color = forceColor });
                var own = ParsePara(ppr); if (own != null) ps = MergeP(ps, own);
                rs = rs.Over(ParseRun(c, ppr?.Element(A + "defRPr")));
                var runs = new List<(string, RunStyle)>();
                foreach (var e in p.Elements())
                {
                    if (e.Name == A + "r" || e.Name == A + "fld") runs.Add((e.Element(A + "t")?.Value ?? "", rs.Over(ParseRun(c, e.Element(A + "rPr")))));
                    else if (e.Name == A + "br") runs.Add(("\n", rs.Over(ParseRun(c, e.Element(A + "rPr")))));
                }
                var endSt = rs.Over(ParseRun(c, p.Element(A + "endParaRPr")));
                bool hasText = runs.Any(x => x.Item1.Trim().Length > 0);
                bool bullet = hasText && ps.BulletNone == false;
                if (ps.AutoNum == true && hasText) autoNum++; else if (!hasText) { } else autoNum = 0;
                string prefix = !bullet ? "" : ps.AutoNum == true ? autoNum + ". " : (ps.BuChar ?? "•") + " ";
                if (prefix.Length > 0) prefix = prefix.Replace("Ø", "➢").Replace("§", "▪").Replace("ü", "✓");
                if (prefix.Length > 0) runs.Insert(0, (prefix, runs.Count > 0 ? runs[0].Item2 : rs));
                float marL = (ps.MarL ?? lvl * 457200L) * c.Scale + (bullet ? 0 : 0);
                float indent = Math.Max(0, marL + (bullet ? (ps.Indent ?? 0) * c.Scale : 0));
                string algn = ps.Algn ?? "l";
                LayoutPara(g, c, runs.Count == 0 ? new List<(string, RunStyle)> { ("", endSt) } : runs, inner.Width - indent, wrap, fontScale, lines, algn, indent);
            }
            if (lines.Count == 0) return;
            float total = lines.Sum(l => l.height);
            float y = anchor switch { "ctr" => inner.Y + (inner.Height - total) / 2, "b" => inner.Bottom - total, _ => inner.Y };
            var stG = g.Save();
            if (vert) { g.TranslateTransform(box.X + box.Width / 2, box.Y + box.Height / 2); g.RotateTransform(90); g.TranslateTransform(-(box.X + box.Width / 2), -(box.Y + box.Height / 2)); }
            using var sf = StringFormat.GenericTypographic;
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            foreach (var ln in lines)
            {
                float avail = inner.Width - ln.indent;
                float x = inner.X + ln.indent + (ln.algn switch { "ctr" => (avail - ln.width) / 2, "r" => avail - ln.width, _ => 0 });
                foreach (var (text, st) in ln.runs)
                {
                    using var f = FontOf(st, fontScale, c);
                    using var b = new SolidBrush(st.Color ?? Color.Black);
                    float asc = Ascent(f);
                    g.DrawString(text, f, b, x, y + ln.ascent - asc, sf);
                    x += g.MeasureString(text, f, PointF.Empty, sf).Width;
                }
                y += ln.height;
            }
            g.Restore(stG);
        }

        private static ParaStyle MergeP(ParaStyle a, ParaStyle b) => new()
        {
            Algn = b.Algn ?? a.Algn, BulletNone = b.BulletNone ?? a.BulletNone, BuChar = b.BuChar ?? a.BuChar,
            AutoNum = b.BulletNone == true ? null : b.AutoNum ?? (b.BuChar != null ? null : a.AutoNum), MarL = b.MarL ?? a.MarL, Indent = b.Indent ?? a.Indent
        };

        private static readonly HashSet<string> Installed = new(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        private Font FontOf(RunStyle st, float fontScale, Ctx c)
        {
            float pt = (st.Size ?? 18) * fontScale;
            float px = pt * 12700f * c.Scale;                       // 1 pt = 12700 EMU
            var name = st.Font ?? "Calibri";
            if (!Installed.Contains(name)) name = Installed.Contains("Segoe UI") ? "Segoe UI" : "Arial";
            var fs = FontStyle.Regular;
            if (st.Bold == true) fs |= FontStyle.Bold; if (st.Italic == true) fs |= FontStyle.Italic; if (st.Underline == true) fs |= FontStyle.Underline;
            return new Font(name, Math.Max(1f, px), fs, GraphicsUnit.Pixel);
        }

        private static float Ascent(Font f)
        {
            var ff = f.FontFamily;
            return f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        private void LayoutPara(Graphics g, Ctx c, List<(string text, RunStyle st)> runs, float maxW, bool wrap, float fontScale,
            List<(List<(string text, RunStyle st)> runs, float width, float height, float ascent, string algn, float indent)> outLines, string algn, float indent)
        {
            using var sf = StringFormat.GenericTypographic;
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            var cur = new List<(string, RunStyle)>(); float cw = 0, asc = 0, lh = 0;
            void Flush(float minH)
            {
                if (lh == 0) lh = minH;
                outLines.Add((cur, cw, lh * 1.0f, asc == 0 ? minH * 0.8f : asc, algn, indent));
                cur = new List<(string, RunStyle)>(); cw = 0; asc = 0; lh = 0;
            }
            float lastH = 0;
            foreach (var (text, st) in runs)
            {
                using var f = FontOf(st, fontScale, c);
                float fh = f.GetHeight(g) * 1.12f, fa = Ascent(f) * 1.06f;
                lastH = fh;
                var pieces = text.Split('\n');
                for (int pi = 0; pi < pieces.Length; pi++)
                {
                    if (pi > 0) Flush(fh);
                    // pecah per kata (spasi ikut kata sebelumnya)
                    var words = System.Text.RegularExpressions.Regex.Matches(pieces[pi], @"\S*\s*").Select(m => m.Value).Where(s => s.Length > 0);
                    foreach (var w in words)
                    {
                        float ww = g.MeasureString(w, f, PointF.Empty, sf).Width;
                        if (wrap && cw > 0 && cw + g.MeasureString(w.TrimEnd(), f, PointF.Empty, sf).Width > maxW) Flush(fh);
                        cur.Add((w, st)); cw += ww;
                        lh = Math.Max(lh, fh); asc = Math.Max(asc, fa);
                    }
                    if (pieces[pi].Length == 0) { lh = Math.Max(lh, fh); asc = Math.Max(asc, fa); }
                }
            }
            Flush(lastH == 0 ? 12 : lastH);
        }

        // ------------------------------------------------------------------ teks polos (cari / ringkas / baca lantang)

        public List<string> Texts()
        {
            var outL = new List<string>();
            foreach (var p in SlidePaths)
            {
                var root = Xml(p)?.Root;
                if (root == null) { outL.Add(""); continue; }
                var sb = new StringBuilder();
                foreach (var para in root.Descendants(A + "p"))
                {
                    var t = string.Concat(para.Descendants(A + "t").Select(x => x.Value));
                    if (t.Trim().Length > 0) sb.AppendLine(t.Trim());
                }
                // catatan pembicara
                var notes = RelOfType(p, "/notesSlide");
                var nroot = notes != null ? Xml(notes)?.Root : null;
                if (nroot != null)
                {
                    var nt = string.Join(" ", nroot.Descendants(P + "sp").Where(s => PhOf(s)?.Attribute("type")?.Value == "body").SelectMany(s => s.Descendants(A + "t")).Select(x => x.Value)).Trim();
                    if (nt.Length > 0) sb.AppendLine("Catatan: " + nt);
                }
                outL.Add(sb.ToString().Trim());
            }
            return outL;
        }

        public static List<string> SlideTexts(string path) { using var r = new PptxRenderer(path); return r.Texts(); }
    }
}
