using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static DepupOfficeDesktop.Core.Office.OxUtil;

namespace DepupOfficeDesktop.Core.Office
{
    /// <summary>
    /// DOCX → HTML untuk PEMBACA (port DocxHtml.kt Android v4.1), sedekat mungkin dengan Word: halaman (ukuran &amp; margin
    /// asli), gaya &amp; pewarisan, font &amp; warna tema, penomoran/bullet, tab, TABEL (lebar kolom, gabung sel, garis,
    /// arsiran, gaya bersyarat), gambar inline &amp; mengambang, kotak teks, header/footer + nomor halaman, catatan kaki,
    /// pemisah halaman/bagian. Pemenggalan halaman dilakukan JavaScript di WebView2 (sama persis dengan Android).
    /// </summary>
    public static class DocxHtml
    {
        private static readonly Regex CSS_BG = new("background:([^;]+);", RegexOptions.Compiled);
        private static readonly Regex CSS_BORDER = new("border:([0-9.]+)pt solid ([^;]+);", RegexOptions.Compiled);
        private static readonly Regex CSS_BG_ANY = new("background:[^;]+;", RegexOptions.Compiled);
        private static readonly Regex CSS_BORDER_ANY = new("border:[^;]+;", RegexOptions.Compiled);
        private static readonly Regex VML_HREL = new("mso-position-horizontal-relative:([a-z-]+)", RegexOptions.Compiled);
        private static readonly Regex VML_VREL = new("mso-position-vertical-relative:([a-z-]+)", RegexOptions.Compiled);

        // ------------------------------------------------------------------ model properti
        private sealed class RP
        {
            public string? Font, FontTheme; public int? Size;
            public bool? B, I, Strike, DStrike, ColorAuto, Caps, SmallCaps, Vanish, Rtl;
            public string? U, Vert;
            public int? Color, Highlight, Shd, Spacing, Position;

            public RP Over(RP? o)
            {
                if (o == null) return this;
                var r = new RP
                {
                    Font = o.Font ?? Font,
                    FontTheme = o.Font != null ? o.FontTheme : (o.FontTheme ?? FontTheme),
                    Size = o.Size ?? Size, B = o.B ?? B, I = o.I ?? I, U = o.U ?? U,
                    Strike = o.Strike ?? Strike, DStrike = o.DStrike ?? DStrike, Color = o.Color ?? Color,
                    ColorAuto = o.ColorAuto ?? ColorAuto, Highlight = o.Highlight ?? Highlight, Shd = o.Shd ?? Shd, Vert = o.Vert ?? Vert,
                    Caps = o.Caps ?? Caps, SmallCaps = o.SmallCaps ?? SmallCaps, Vanish = o.Vanish ?? Vanish,
                    Spacing = o.Spacing ?? Spacing, Position = o.Position ?? Position, Rtl = o.Rtl ?? Rtl
                };
                if (o.Font != null && o.FontTheme == null) r.FontTheme = null;
                if (o.Color != null) r.ColorAuto = false;
                return r;
            }

            public RP Copy() => new RP().Over(this);
        }

        private sealed record Bdr(string Style, int SzEighths, int? Color, int Space);
        private sealed record TabStop(int Pos, string Align, string Leader);

        private sealed class PP
        {
            public string? Align, LineRule, StyleId;
            public int? Before, After, Line, Left, Right, First, Hanging, NumId, Ilvl, Shd;
            public bool? BeforeAuto, AfterAuto, PageBreakBefore, Contextual, KeepNext;
            public Dictionary<string, Bdr>? Bdr;
            public List<TabStop>? Tabs;

            public PP Over(PP? o)
            {
                if (o == null) return this;
                var r = new PP
                {
                    Align = o.Align ?? Align, Before = o.Before ?? Before, After = o.After ?? After,
                    BeforeAuto = o.BeforeAuto ?? BeforeAuto, AfterAuto = o.AfterAuto ?? AfterAuto,
                    Line = o.Line ?? Line, LineRule = o.Line != null ? o.LineRule : LineRule,
                    Left = o.Left ?? Left, Right = o.Right ?? Right,
                    PageBreakBefore = o.PageBreakBefore ?? PageBreakBefore, NumId = o.NumId ?? NumId, Ilvl = o.Ilvl ?? Ilvl,
                    Shd = o.Shd ?? Shd,
                    Contextual = o.Contextual ?? Contextual, KeepNext = o.KeepNext ?? KeepNext, StyleId = o.StyleId ?? StyleId
                };
                if (o.First != null || o.Hanging != null) { r.First = o.First; r.Hanging = o.Hanging; } else { r.First = First; r.Hanging = Hanging; }
                if (o.Bdr != null) { r.Bdr = new Dictionary<string, Bdr>(Bdr ?? new()); foreach (var kv in o.Bdr) r.Bdr[kv.Key] = kv.Value; } else r.Bdr = Bdr;
                r.Tabs = MergeTabs(Tabs, o.Tabs);
                return r;
            }

            private static List<TabStop>? MergeTabs(List<TabStop>? a, List<TabStop>? b)
            {
                if (b == null) return a;
                var m = new SortedDictionary<int, TabStop>();
                if (a != null) foreach (var t in a) m[t.Pos] = t;
                foreach (var t in b) { if (t.Align == "clear") m.Remove(t.Pos); else m[t.Pos] = t; }
                return m.Values.ToList();
            }
        }

        private sealed record Style(string Id, string Type, string? BasedOn, XNode Node);

        private sealed record Sect(int W, int H, int Top, int Right, int Bottom, int Left, int Header, int Footer,
            bool TitlePg, Dictionary<string, string> Hdr, Dictionary<string, string> Ftr, string Type, int Cols, bool TopExact, bool BottomExact);

        private sealed record Lvl(int Start, string Fmt, string Text, string Jc, PP? PP, RP? RP, string Suff, int Restart, bool IsLgl);

        private sealed class Ctx
        {
            public readonly OPackage Pkg; public readonly string Main;
            public readonly DocRes Res;
            public readonly Dictionary<string, int> Theme = new();
            public string MajorFont = "Calibri Light", MinorFont = "Calibri";
            public readonly Dictionary<string, Style> Styles = new();
            public RP DefRP = new(); public PP DefPP = new();
            public string? DefParaStyle, DefTableStyle, DefCharStyle;
            public readonly Dictionary<int, (string Abs, Dictionary<int, int> Starts)> Nums = new();
            public readonly Dictionary<string, Dictionary<int, Lvl>> Abstracts = new();
            public readonly Dictionary<int, Dictionary<int, Lvl>> NumOverrideLvls = new();
            public readonly Dictionary<string, int[]> Counters = new();
            public readonly HashSet<int> UsedNum = new();
            public int DefaultTab = 720;
            public int TblDepth;
            public readonly Dictionary<string, string> ClsMap = new();
            public readonly StringBuilder ClsCss = new();
            public bool EvenOdd;
            public readonly Dictionary<string, XNode> Footnotes = new(), Endnotes = new();
            public string? FootPart, EndPart;
            public readonly List<(string Key, int No)> NoteOrder = new();
            public int NoteNo, EndNo;
            public Dictionary<string, OPackage.Rel> Rels = new();
            public string Part;
            public readonly List<int[]> FieldStack = new();
            public readonly StringBuilder Instr = new();
            public int? Bg;
            public readonly Dictionary<string, RP> PRPCache = new(), CRPCache = new();
            public readonly Dictionary<string, PP> PPPCache = new();
            public int ShapeNo;

            public Ctx(OPackage pkg, string main) { Pkg = pkg; Main = main; Part = main; Res = new DocRes(pkg); }
        }

        // ------------------------------------------------------------------ API
        public static RenderedDoc Render(string path, string title)
        {
            var pkg = new OPackage(path);
            try
            {
                try { return Render(pkg, title); }
                catch (OutOfMemoryException) { throw; }
                catch { return RenderSimple(pkg, title); }
            }
            catch { pkg.Dispose(); throw; }
        }

        public static RenderedDoc Render(OPackage pkg, string title)
        {
            string main = pkg.MainPart() ?? "word/document.xml";
            var c = new Ctx(pkg, main);
            foreach (var ld in new Action<Ctx>[] { LoadTheme, LoadStyles, LoadNumbering, LoadSettings, LoadNotes })
                try { ld(c); } catch { }
            var doc = pkg.Xml(main) ?? throw new ArgumentException("document.xml tidak ada");
            var body = doc.First("body") ?? throw new ArgumentException("body tidak ada");
            c.Rels = pkg.Rels(main);

            var blocks = Flatten(body.Kids);
            var sects = new List<Sect>();
            var sectOf = new int[blocks.Count];
            int cur = 0;
            for (int bi = 0; bi < blocks.Count; bi++)
            {
                sectOf[bi] = cur;
                var b = blocks[bi];
                var sp = b.Name == "p" ? b.Path("pPr", "sectPr") : null;
                if (sp != null) { sects.Add(ParseSect(sp, sects.LastOrDefault())); cur++; }
            }
            sects.Add(ParseSect(body.Child("sectPr"), sects.LastOrDefault()));

            var sb = new StringBuilder(64 * 1024);
            int lastSect = -1;
            for (int bi = 0; bi < blocks.Count; bi++)
            {
                int s = sectOf[bi];
                if (s != lastSect) { sb.Append("<div class=\"sect\" data-s=\"").Append(s).Append("\"></div>"); lastSect = s; }
                Block(c, blocks[bi], sb, s, null);
            }
            if (c.NoteOrder.Count > 0)
            {
                sb.Append("<div class=\"b notes\" data-s=\"").Append(lastSect).Append("\"><hr>");
                var saveRels = c.Rels; var savePart = c.Part;
                foreach (var (key, no) in c.NoteOrder)
                {
                    bool foot = key.StartsWith("f:", StringComparison.Ordinal);
                    var map = foot ? c.Footnotes : c.Endnotes;
                    if (!map.TryGetValue(key[2..], out var node)) continue;
                    var np = foot ? c.FootPart : c.EndPart;
                    if (np != null) { c.Rels = pkg.Rels(np); c.Part = np; }
                    sb.Append("<div class=\"note\"><sup>").Append(no).Append("</sup> ");
                    foreach (var p in Flatten(node.Kids)) if (p.Name == "p") Paragraph(c, p, sb, lastSect, null, inNote: true);
                    sb.Append("</div>");
                }
                c.Rels = saveRels; c.Part = savePart;
                sb.Append("</div>");
            }

            var hf = new StringBuilder();
            var seen = new HashSet<string>();
            foreach (var sct in sects)
                foreach (var id in sct.Hdr.Values.Concat(sct.Ftr.Values))
                {
                    if (!seen.Add(id)) continue;
                    if (!c.Rels.TryGetValue(id, out var rel)) continue;
                    var x = pkg.Xml(rel.Target); if (x == null) continue;
                    var saveRels = c.Rels; var savePart = c.Part;
                    c.Rels = pkg.Rels(rel.Target); c.Part = rel.Target;
                    hf.Append("<template id=\"hf-").Append(Esc(id)).Append("\">");
                    var rootEl = x.Kids.FirstOrDefault(k => k.Name == "hdr" || k.Name == "ftr") ?? x;
                    foreach (var p in Flatten(rootEl.Kids)) Block(c, p, hf, -1, null);
                    hf.Append("</template>");
                    c.Rels = saveRels; c.Part = savePart;
                }

            string M(Dictionary<string, string> x) => "{" + string.Join(",", x.Select(kv => "\"" + kv.Key + "\":\"" + kv.Value.Replace("\"", "") + "\"")) + "}";
            string B(bool v) => v ? "true" : "false";
            var sectJson = "[" + string.Join(",", sects.Select(s =>
                "{\"w\":" + N(s.W / 20.0) + ",\"h\":" + N(s.H / 20.0) + ",\"t\":" + N(s.Top / 20.0) + ",\"r\":" + N(s.Right / 20.0) + ",\"b\":" + N(s.Bottom / 20.0) + ",\"l\":" + N(s.Left / 20.0) + "," +
                "\"hd\":" + N(s.Header / 20.0) + ",\"ft\":" + N(s.Footer / 20.0) + ",\"tp\":" + B(s.TitlePg) + ",\"tx\":" + B(s.TopExact) + ",\"bx\":" + B(s.BottomExact) +
                ",\"type\":\"" + s.Type.Replace("\"", "") + "\",\"hdr\":" + M(s.Hdr) + ",\"ftr\":" + M(s.Ftr) + "}")) + "]";
            string? baseFont = FontOf(c, c.DefRP);
            double baseSize = (c.DefRP.Size ?? 20) / 2.0;
            var html = new StringBuilder(sb.Length + hf.Length + 16384);
            html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(Esc(title)).Append("</title><style>")
                .Append(CSS).Append(OfficeViewCss.Docx)
                .Append("@page{size:").Append(N(sects[0].W / 20.0)).Append("pt ").Append(N(sects[0].H / 20.0)).Append("pt;margin:0}")
                .Append("#flow,.content,.hdr,.ftr{font-family:").Append(FontStack(baseFont)).Append(";font-size:").Append(N(baseSize)).Append("pt}")
                .Append(c.ClsCss)
                .Append("</style></head><body><div id=\"pages\"></div><div id=\"flow\">")
                .Append(sb).Append("</div>").Append(hf)
                .Append("<script>var SECTS=").Append(sectJson).Append(";var DEFTAB=").Append(N(c.DefaultTab / 20.0)).Append(";var EVEN=").Append(B(c.EvenOdd)).Append(';').Append(JS).Append("</script></body></html>");
            c.Res.Finish();
            return new RenderedDoc(html.ToString(), c.Res, title, "docx");
        }

        /// <summary>Mode cadangan bila render lengkap gagal: teks + tabel sederhana, tanpa tata letak halaman.</summary>
        public static RenderedDoc RenderSimple(OPackage pkg, string title)
        {
            var doc = pkg.Xml(pkg.MainPart() ?? "word/document.xml") ?? throw new ArgumentException("document.xml tidak ada");
            var body = doc.First("body") ?? throw new ArgumentException("body tidak ada");
            var sb = new StringBuilder("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.Append("<style>body{margin:0;background:#e9eaee;font:11pt/1.35 Calibri,Arial,sans-serif;color:#000}");
            sb.Append(".pg{background:#fff;max-width:794px;margin:12px auto;padding:48px 56px;box-sizing:border-box;box-shadow:0 1px 4px rgba(0,0,0,.2)}");
            sb.Append(".p{margin:0 0 6pt;white-space:pre-wrap;word-wrap:break-word}table{border-collapse:collapse;margin:0 0 8pt}td .p{margin:0}");
            sb.Append(OfficeViewCss.Docx).Append("</style></head><body data-ready=\"1\"><div class=\"pg\">");
            foreach (var b in Flatten(body.Kids))
            {
                if (b.Name == "tbl") SimpleTable(b, sb, -1);
                else { var t = Esc(ParaText(b)); sb.Append("<div class=\"p\">").Append(t.Length == 0 ? "&#8203;" : t).Append("</div>"); }
            }
            sb.Append("</div></body></html>");
            var res = new DocRes(pkg); res.Finish();
            return new RenderedDoc(sb.ToString(), res, title, "docx");
        }

        private static void Block(Ctx c, XNode b, StringBuilder outB, int sect, (PP? pp, RP? rp)? cellDef)
        {
            int mark = outB.Length;
            try
            {
                if (b.Name == "p") Paragraph(c, b, outB, sect, cellDef);
                else if (b.Name == "tbl") Table(c, b, outB, sect);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                outB.Length = mark;
                c.Bg = null;
                if (b.Name == "tbl") SimpleTable(b, outB, sect);
                else
                {
                    var t = Esc(ParaText(b));
                    outB.Append("<div class=\"p").Append(sect >= 0 ? " b\" data-s=\"" + sect + "\"" : "\"").Append('>').Append(t.Length == 0 ? "&#8203;" : t).Append("</div>");
                }
            }
        }

        private static string ParaText(XNode p)
        {
            var sb = new StringBuilder();
            void Walk(XNode n)
            {
                foreach (var k in n.Kids)
                    switch (k.Name)
                    {
                        case "t": sb.Append(k.AllText()); break;
                        case "tab": sb.Append('\t'); break;
                        case "br": case "cr": sb.Append('\n'); break;
                        case "del": case "delText": case "instrText": break;
                        default: Walk(k); break;
                    }
            }
            Walk(p); return sb.ToString();
        }

        private static List<XNode> Through(XNode n, string name, List<XNode>? outL = null)
        {
            outL ??= new List<XNode>();
            foreach (var k in n.Kids)
            {
                if (k.Name == name) outL.Add(k);
                else if (k.Name == "sdt") { var sc = k.Child("sdtContent"); if (sc != null) Through(sc, name, outL); }
                else if (k.Name is "customXml" or "ins" or "moveTo" or "smartTag") Through(k, name, outL);
            }
            return outL;
        }

        private static void SimpleTable(XNode t, StringBuilder outB, int sect)
        {
            outB.Append("<table class=\"").Append(sect >= 0 ? "b" : "").Append('"');
            if (sect >= 0) outB.Append(" data-s=\"").Append(sect).Append('"');
            outB.Append(" style=\"border-collapse:collapse;width:100%\">");
            foreach (var tr in Through(t, "tr"))
            {
                outB.Append("<tr>");
                foreach (var tc in Through(tr, "tc"))
                {
                    var vm = tc.Path("tcPr", "vMerge");
                    if (vm != null && vm.V != "restart") continue;
                    int sp = ToInt(tc.Path("tcPr", "gridSpan")?.V) ?? 1;
                    outB.Append("<td").Append(sp > 1 ? " colspan=\"" + sp + "\"" : "").Append(" style=\"border:0.5pt solid #000;padding:0 5.4pt;vertical-align:top\">");
                    var ps = Flatten(tc.Kids);
                    if (ps.Count == 0) outB.Append("<div class=\"p\">&#8203;</div>");
                    foreach (var k in ps)
                    {
                        if (k.Name == "tbl") SimpleTable(k, outB, -1);
                        else { var tx = Esc(ParaText(k)).Replace("\n", "<br>"); outB.Append("<div class=\"p\">").Append(tx.Length == 0 ? "&#8203;" : tx).Append("</div>"); }
                    }
                    outB.Append("</td>");
                }
                outB.Append("</tr>");
            }
            outB.Append("</table>");
        }

        private static List<XNode> Flatten(List<XNode> kids, List<XNode>? outL = null)
        {
            outL ??= new List<XNode>();
            foreach (var k in kids)
            {
                if (k.Name is "p" or "tbl") outL.Add(k);
                else if (k.Name == "sdt") { var sc = k.Child("sdtContent"); if (sc != null) Flatten(sc.Kids, outL); }
                else if (k.Name is "customXml" or "smartTag" or "ins" or "moveTo") Flatten(k.Kids, outL);
            }
            return outL;
        }

        // ------------------------------------------------------------------ tema, gaya, penomoran
        private static OPackage.Rel? RelOfType(Ctx c, string suffix) => c.Pkg.Rels(c.Main).Values.FirstOrDefault(r => r.Type.EndsWith(suffix, StringComparison.Ordinal));

        private static void LoadTheme(Ctx c)
        {
            var rel = RelOfType(c, "/theme"); if (rel == null) return;
            var t = c.Pkg.Xml(rel.Target); if (t == null) return;
            var cs = t.First("clrScheme");
            if (cs != null)
                foreach (var k in cs.Kids)
                {
                    var v = k.Child("srgbClr")?.V ?? k.Child("sysClr")?.Attr("lastClr");
                    var rgb = ParseHex(v); if (rgb != null) c.Theme[k.Name] = rgb.Value;
                }
            void Alias(string a, string b) { if (c.Theme.TryGetValue(b, out var v)) c.Theme[a] = v; }
            Alias("text1", "dk1"); Alias("background1", "lt1"); Alias("text2", "dk2"); Alias("background2", "lt2");
            Alias("dark1", "dk1"); Alias("light1", "lt1"); Alias("dark2", "dk2"); Alias("light2", "lt2");
            Alias("hyperlink", "hlink"); Alias("followedHyperlink", "folHlink");
            var mj = t.First("majorFont")?.Child("latin")?.Attr("typeface"); if (!string.IsNullOrWhiteSpace(mj)) c.MajorFont = mj;
            var mn = t.First("minorFont")?.Child("latin")?.Attr("typeface"); if (!string.IsNullOrWhiteSpace(mn)) c.MinorFont = mn;
        }

        private static void LoadStyles(Ctx c)
        {
            var rel = RelOfType(c, "/styles");
            var x = (rel != null ? c.Pkg.Xml(rel.Target) : null) ?? c.Pkg.Xml("word/styles.xml");
            if (x == null) { c.DefRP.Size = 20; return; }
            var dd = x.Child("styles")?.Child("docDefaults") ?? x.First("docDefaults");
            if (dd != null) { c.DefRP = ParseRP(c, dd.Path("rPrDefault", "rPr")); c.DefPP = ParsePP(c, dd.Path("pPrDefault", "pPr")); }
            c.DefRP.Size ??= 20;
            foreach (var s in x.All("style"))
            {
                var id = s.Attr("styleId"); if (id == null) continue;
                var type = s.Attr("type") ?? "paragraph";
                c.Styles[id] = new Style(id, type, s.Child("basedOn")?.V, s);
                if (s.Attr("default") is "1" or "true")
                    switch (type) { case "paragraph": c.DefParaStyle = id; break; case "table": c.DefTableStyle = id; break; case "character": c.DefCharStyle = id; break; }
            }
        }

        private static void LoadNumbering(Ctx c)
        {
            var rel = RelOfType(c, "/numbering"); if (rel == null) return;
            var x0 = c.Pkg.Xml(rel.Target); if (x0 == null) return;
            var x = x0.Child("numbering") ?? x0;
            Dictionary<int, Lvl> Lvls(XNode n)
            {
                var m = new Dictionary<int, Lvl>();
                foreach (var l in n.Children("lvl"))
                {
                    var il = ToInt(l.Attr("ilvl")); if (il == null) continue;
                    m[il.Value] = new Lvl(
                        ToInt(l.Child("start")?.V) ?? 1, l.Child("numFmt")?.V ?? "decimal", l.Child("lvlText")?.V ?? "",
                        l.Child("lvlJc")?.V ?? "left", l.Child("pPr") is { } pp ? ParsePP(c, pp) : null, l.Child("rPr") is { } rp ? ParseRP(c, rp) : null,
                        l.Child("suff")?.V ?? "tab", ToInt(l.Child("lvlRestart")?.V) ?? -1, l.Child("isLgl") != null);
                }
                return m;
            }
            var styleLink = new Dictionary<string, string>();
            foreach (var a in x.Children("abstractNum"))
            {
                var id = a.Attr("abstractNumId"); if (id == null) continue;
                c.Abstracts[id] = Lvls(a);
                var sl = a.Child("numStyleLink")?.V; if (sl != null) styleLink[id] = sl;
            }
            foreach (var n in x.Children("num"))
            {
                var id = ToInt(n.Attr("numId")); if (id == null) continue;
                var abs = n.Child("abstractNumId")?.V; if (abs == null) continue;
                var starts = new Dictionary<int, int>();
                var ov = new Dictionary<int, Lvl>();
                foreach (var o in n.Children("lvlOverride"))
                {
                    var il = ToInt(o.Attr("ilvl")); if (il == null) continue;
                    var so = ToInt(o.Child("startOverride")?.V); if (so != null) starts[il.Value] = so.Value;
                    var l = o.Child("lvl");
                    if (l != null)
                    {
                        l.Attrs["ilvl"] = il.Value.ToString(Inv);
                        var holder = new XNode("x"); holder.Kids.Add(l);
                        if (Lvls(holder).TryGetValue(il.Value, out var lv)) ov[il.Value] = lv;
                    }
                }
                c.Nums[id.Value] = (abs, starts);
                if (ov.Count > 0) c.NumOverrideLvls[id.Value] = ov;
            }
            foreach (var (abs, styleId) in styleLink)
            {
                if (c.Abstracts.TryGetValue(abs, out var ex) && ex.Count > 0) continue;
                if (!c.Styles.TryGetValue(styleId, out var st)) continue;
                var nid = ToInt(st.Node.Path("pPr", "numPr", "numId")?.V); if (nid == null) continue;
                if (!c.Nums.TryGetValue(nid.Value, out var target)) continue;
                if (c.Abstracts.TryGetValue(target.Abs, out var lv)) c.Abstracts[abs] = lv;
            }
        }

        private static void LoadSettings(Ctx c)
        {
            var rel = RelOfType(c, "/settings"); if (rel == null) return;
            var x = c.Pkg.Xml(rel.Target); if (x == null) return;
            var dt = ToInt(x.First("defaultTabStop")?.V); if (dt is > 0) c.DefaultTab = dt.Value;
            var eo = x.First("evenAndOddHeaders"); if (eo != null) c.EvenOdd = eo.V != "0" && eo.V != "false";
        }

        private static void LoadNotes(Ctx c)
        {
            foreach (var r in c.Pkg.Rels(c.Main).Values)
            {
                if (r.Type.EndsWith("/footnotes", StringComparison.Ordinal))
                {
                    c.FootPart = r.Target;
                    foreach (var n in c.Pkg.Xml(r.Target)?.All("footnote") ?? new()) { var id = n.Attr("id"); if (id != null) c.Footnotes[id] = n; }
                }
                if (r.Type.EndsWith("/endnotes", StringComparison.Ordinal))
                {
                    c.EndPart = r.Target;
                    foreach (var n in c.Pkg.Xml(r.Target)?.All("endnote") ?? new()) { var id = n.Attr("id"); if (id != null) c.Endnotes[id] = n; }
                }
            }
        }

        // ------------------------------------------------------------------ parsing properti
        private static bool? OnOff(XNode? n)
        {
            if (n == null) return null;
            var v = n.V; if (v == null) return true;
            return !(v is "0" or "false" or "off" or "none");
        }

        private static int? ColorOf(Ctx c, XNode? n)
        {
            if (n == null) return null;
            var th = n.Attr("themeColor");
            int? rgb = th != null ? (c.Theme.TryGetValue(th, out var tv) ? tv : ParseHex(n.V)) : ParseHex(n.V);
            if (rgb != null)
            {
                var t = HexInt(n.Attr("themeTint")); if (t != null) rgb = Mix(rgb.Value, 0xFFFFFF, 1f - t.Value / 255f);
                var s = HexInt(n.Attr("themeShade")); if (s != null) rgb = Mix(0, rgb.Value, s.Value / 255f);
            }
            return rgb;
        }

        private static readonly Dictionary<string, int> HIGHLIGHT = new()
        {
            ["yellow"] = 0xFFFF00, ["green"] = 0x00FF00, ["cyan"] = 0x00FFFF, ["magenta"] = 0xFF00FF, ["blue"] = 0x0000FF, ["red"] = 0xFF0000,
            ["darkBlue"] = 0x000080, ["darkCyan"] = 0x008080, ["darkGreen"] = 0x008000, ["darkMagenta"] = 0x800080, ["darkRed"] = 0x800000,
            ["darkYellow"] = 0x808000, ["darkGray"] = 0x808080, ["lightGray"] = 0xC0C0C0, ["black"] = 0x000000, ["white"] = 0xFFFFFF
        };

        private static int? ShdOf(Ctx c, XNode? n)
        {
            if (n == null) return null;
            var fill = n.Attr("fill");
            var th = n.Attr("themeFill");
            int? baseC = th != null ? (c.Theme.TryGetValue(th, out var tv) ? tv : null) : (fill != null && fill != "auto" ? ParseHex(fill) : null);
            if (baseC != null)
            {
                var t = HexInt(n.Attr("themeFillTint")); if (t != null) baseC = Mix(baseC.Value, 0xFFFFFF, 1f - t.Value / 255f);
                var s = HexInt(n.Attr("themeFillShade")); if (s != null) baseC = Mix(0, baseC.Value, s.Value / 255f);
            }
            var v = n.V;
            if (v != null && v.StartsWith("pct", StringComparison.Ordinal))
            {
                int pct = ToInt(v[3..]) ?? 0;
                int col = ParseHex(n.Attr("color")) ?? 0;
                baseC = Mix(baseC ?? 0xFFFFFF, col, pct / 100f);
            }
            else if (v == "solid") baseC = ParseHex(n.Attr("color")) ?? 0;
            return baseC;
        }

        private static RP ParseRP(Ctx c, XNode? n)
        {
            var r = new RP();
            if (n == null) return r;
            var f = n.Child("rFonts");
            if (f != null)
            {
                var th = f.Attr("asciiTheme") ?? f.Attr("hAnsiTheme");
                var nm = f.Attr("ascii") ?? f.Attr("hAnsi");
                if (nm != null) r.Font = nm;
                if (th != null) { r.FontTheme = th; if (nm == null) r.Font = null; }
            }
            var sz = ToInt(n.Child("sz")?.V); if (sz != null) r.Size = sz;
            r.B = OnOff(n.Child("b")); r.I = OnOff(n.Child("i"));
            var u = n.Child("u"); if (u != null) r.U = u.V ?? "single";
            r.Strike = OnOff(n.Child("strike")); r.DStrike = OnOff(n.Child("dstrike"));
            var col = n.Child("color");
            if (col != null) { if (col.V == "auto" && col.Attr("themeColor") == null) r.ColorAuto = true; else r.Color = ColorOf(c, col); }
            var hl = n.Child("highlight")?.V; if (hl != null && hl != "none" && HIGHLIGHT.TryGetValue(hl, out var hv)) r.Highlight = hv;
            var shd = ShdOf(c, n.Child("shd")); if (shd != null) r.Shd = shd;
            var va = n.Child("vertAlign")?.V; if (va != null) r.Vert = va;
            r.Caps = OnOff(n.Child("caps")); r.SmallCaps = OnOff(n.Child("smallCaps")); r.Vanish = OnOff(n.Child("vanish"));
            var sp = ToInt(n.Child("spacing")?.V); if (sp != null) r.Spacing = sp;
            var pos = ToInt(n.Child("position")?.V); if (pos != null) r.Position = pos;
            return r;
        }

        private static Bdr? BdrOf(XNode? n)
        {
            if (n == null) return null;
            var v = n.V; if (v == null) return null;
            var col = n.Attr("color");
            return new Bdr(v, ToInt(n.Attr("sz")) ?? 4, col != null && col != "auto" ? ParseHex(col) : null, ToInt(n.Attr("space")) ?? 0);
        }

        private static PP ParsePP(Ctx c, XNode? n)
        {
            var p = new PP();
            if (n == null) return p;
            var ps = n.Child("pStyle")?.V; if (ps != null) p.StyleId = ps;
            var jc = n.Child("jc")?.V; if (jc != null) p.Align = jc;
            var s = n.Child("spacing");
            if (s != null)
            {
                p.Before = ToInt(s.Attr("before")) ?? p.Before; p.After = ToInt(s.Attr("after")) ?? p.After;
                var ba = s.Attr("beforeAutospacing"); if (ba != null) p.BeforeAuto = ba is "1" or "true";
                var aa = s.Attr("afterAutospacing"); if (aa != null) p.AfterAuto = aa is "1" or "true";
                var ln = ToInt(s.Attr("line")); if (ln != null) { p.Line = ln; p.LineRule = s.Attr("lineRule") ?? "auto"; }
            }
            var ind = n.Child("ind");
            if (ind != null)
            {
                var l = ToInt(ind.Attr("left") ?? ind.Attr("start")); if (l != null) p.Left = l;
                var r = ToInt(ind.Attr("right") ?? ind.Attr("end")); if (r != null) p.Right = r;
                var fl = ToInt(ind.Attr("firstLine")); if (fl != null) { p.First = fl; p.Hanging = null; }
                var hg = ToInt(ind.Attr("hanging")); if (hg != null) { p.Hanging = hg; p.First = null; }
            }
            p.PageBreakBefore = OnOff(n.Child("pageBreakBefore"));
            var np = n.Child("numPr");
            if (np != null) { var ni = ToInt(np.Child("numId")?.V); if (ni != null) p.NumId = ni; var il = ToInt(np.Child("ilvl")?.V); if (il != null) p.Ilvl = il; }
            var shd = ShdOf(c, n.Child("shd")); if (shd != null) p.Shd = shd;
            var pb = n.Child("pBdr");
            if (pb != null)
            {
                var m = new Dictionary<string, Bdr>();
                foreach (var side in new[] { "top", "bottom", "left", "right", "between" }) { var b = BdrOf(pb.Child(side)); if (b != null) m[side] = b; }
                p.Bdr = m;
            }
            var tabs = n.Child("tabs");
            if (tabs != null)
                p.Tabs = tabs.Children("tab").Select(tab => ToInt(tab.Attr("pos")) is { } pos ? new TabStop(pos, tab.V ?? "left", tab.Attr("leader") ?? "none") : null)
                    .Where(t => t != null).Select(t => t!).OrderBy(t => t.Pos).ToList();
            p.Contextual = OnOff(n.Child("contextualSpacing")); p.KeepNext = OnOff(n.Child("keepNext"));
            return p;
        }

        // ------------------------------------------------------------------ resolusi gaya
        private static List<Style> StyleChain(Ctx c, string? id)
        {
            var outL = new List<Style>();
            var cur = id; int guard = 0;
            while (cur != null && guard++ < 20) { if (!c.Styles.TryGetValue(cur, out var s)) break; outL.Insert(0, s); cur = s.BasedOn; }
            return outL;
        }

        private static RP ParaStyleRP(Ctx c, string? id)
        {
            var key = id ?? "";
            if (c.PRPCache.TryGetValue(key, out var v)) return v;
            var r = c.DefRP;
            foreach (var s in StyleChain(c, id ?? c.DefParaStyle)) r = r.Over(ParseRP(c, s.Node.Child("rPr")));
            return c.PRPCache[key] = r;
        }

        private static PP ParaStylePP(Ctx c, string? id)
        {
            var key = id ?? "";
            if (c.PPPCache.TryGetValue(key, out var v)) return v;
            var p = c.DefPP;
            foreach (var s in StyleChain(c, id ?? c.DefParaStyle)) p = p.Over(ParsePP(c, s.Node.Child("pPr")));
            return c.PPPCache[key] = p;
        }

        private static RP CharStyleRP(Ctx c, string id)
        {
            if (c.CRPCache.TryGetValue(id, out var v)) return v;
            var r = new RP();
            foreach (var s in StyleChain(c, id)) r = r.Over(ParseRP(c, s.Node.Child("rPr")));
            return c.CRPCache[id] = r;
        }

        private static string? FontOf(Ctx c, RP r)
        {
            if (r.Font != null) return r.Font;
            return r.FontTheme switch
            {
                null => null,
                "majorHAnsi" or "majorAscii" or "majorBidi" or "majorEastAsia" => c.MajorFont,
                _ => c.MinorFont
            };
        }

        // ------------------------------------------------------------------ CSS
        private static bool Dark(int rgb)
        {
            int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
            return (r * 76 + g * 151 + b * 29) / 256 <= 156;
        }

        private static void RunCss(Ctx c, RP r, StringBuilder sb, Dictionary<string, string>? fsc = null)
        {
            fsc ??= new();
            var f = FontOf(c, r);
            if (f != null)
            {
                if (!fsc.TryGetValue(f, out var stack)) fsc[f] = stack = FontStack(f);
                sb.Append("font-family:").Append(stack).Append(';');
            }
            if (r.Size != null) sb.Append("font-size:").Append(N(r.Size.Value / 2.0)).Append("pt;");
            if (r.B == true) sb.Append("font-weight:bold;"); else if (r.B == false) sb.Append("font-weight:normal;");
            if (r.I == true) sb.Append("font-style:italic;"); else if (r.I == false) sb.Append("font-style:normal;");
            var deco = new List<string>();
            var u = r.U;
            if (u != null && u != "none") deco.Add("underline");
            if (r.Strike == true || r.DStrike == true) deco.Add("line-through");
            if (deco.Count > 0)
            {
                sb.Append("text-decoration:").Append(string.Join(" ", deco));
                switch (u)
                {
                    case "double": sb.Append(" double"); break;
                    case "wave": case "wavyHeavy": case "wavyDouble": sb.Append(" wavy"); break;
                    case "dotted": case "dottedHeavy": sb.Append(" dotted"); break;
                    case "dash": case "dashLong": case "dashedHeavy": sb.Append(" dashed"); break;
                }
                if (r.DStrike == true) sb.Append(" double");
                sb.Append(';');
            }
            int? bgHere = r.Highlight ?? r.Shd ?? c.Bg;
            if (r.ColorAuto != true && r.Color != null) sb.Append("color:").Append(Hex(r.Color.Value)).Append(';');
            else if (bgHere != null && Dark(bgHere.Value)) sb.Append("color:#ffffff;");
            var bgc = r.Highlight ?? r.Shd; if (bgc != null) sb.Append("background:").Append(Hex(bgc.Value)).Append(';');
            if (r.Vert == "superscript") sb.Append("vertical-align:super;font-size:").Append(N((r.Size ?? 22) / 2.0 * 0.65)).Append("pt;");
            else if (r.Vert == "subscript") sb.Append("vertical-align:sub;font-size:").Append(N((r.Size ?? 22) / 2.0 * 0.65)).Append("pt;");
            if (r.Caps == true) sb.Append("text-transform:uppercase;");
            if (r.SmallCaps == true) sb.Append("font-variant:small-caps;");
            if (r.Spacing is int spc && spc != 0) sb.Append("letter-spacing:").Append(N(spc / 20.0)).Append("pt;");
            if (r.Position is int pos && pos != 0) sb.Append("position:relative;top:").Append(N(-pos / 2.0)).Append("pt;");
        }

        private static string BdrCss(Bdr? b)
        {
            if (b == null || b.Style is "nil" or "none") return "none";
            double w = Math.Max(0.5, b.SzEighths / 8.0);
            string st = b.Style switch
            {
                "double" or "triple" or "thinThickSmallGap" or "thickThinSmallGap" or "thinThickMediumGap" or "thickThinMediumGap" => "double",
                "dotted" or "dotDash" or "dotDotDash" => "dotted",
                "dashed" or "dashSmallGap" or "dashDotStroked" => "dashed",
                "inset" => "inset", "outset" => "outset", _ => "solid"
            };
            double ww = st == "double" ? Math.Max(w, 2.25) : w;
            return N(ww) + "pt " + st + " " + Hex(b.Color ?? 0);
        }

        /// <summary>Faktor tinggi baris "single" per font (sesuai metrik font Office).</summary>
        private static double LineFactor(string? font)
        {
            var f = (font ?? "").ToLowerInvariant();
            if (f.Contains("calibri") || f.Contains("carlito")) return 1.22;
            if (f.Contains("aptos")) return 1.2;
            if (f.Contains("cambria")) return 1.172;
            if (f.Contains("times")) return 1.15;
            if (f.Contains("arial") || f.Contains("helvetica")) return 1.15;
            if (f.Contains("courier")) return 1.133;
            if (f.Contains("georgia")) return 1.136;
            if (f.Contains("verdana")) return 1.215;
            if (f.Contains("tahoma")) return 1.207;
            if (f.Contains("segoe")) return 1.33;
            return 1.17;
        }

        // ------------------------------------------------------------------ paragraf
        private static void Paragraph(Ctx c, XNode p, StringBuilder outB, int sect, (PP? pp, RP? rp)? cellBase, bool inNote = false)
        {
            var pPr = p.Child("pPr");
            var direct = ParsePP(c, pPr);
            var styleId = direct.StyleId;
            var pp = ParaStylePP(c, styleId);
            var baseRP = ParaStyleRP(c, styleId);
            if (cellBase != null)
            {
                var (cpp, crp) = cellBase.Value;
                if (styleId == null || styleId == c.DefParaStyle)
                {
                    if (cpp != null) pp = pp.Over(cpp);
                    if (crp != null) baseRP = baseRP.Over(crp);
                }
                else
                {
                    var p2 = c.DefPP; if (cpp != null) p2 = p2.Over(cpp);
                    var r2 = c.DefRP; if (crp != null) r2 = r2.Over(crp);
                    foreach (var st in StyleChain(c, styleId)) { p2 = p2.Over(ParsePP(c, st.Node.Child("pPr"))); r2 = r2.Over(ParseRP(c, st.Node.Child("rPr"))); }
                    pp = p2; baseRP = r2;
                }
            }
            pp = pp.Over(direct);
            Lvl? lvl = null; string? label = null;
            int? numId = pp.NumId;
            if (numId is > 0)
            {
                int il = Math.Clamp(pp.Ilvl ?? 0, 0, 8);
                if (c.Nums.TryGetValue(numId.Value, out var nm))
                {
                    var absId = nm.Abs;
                    c.Abstracts.TryGetValue(absId, out var absL);
                    if (c.NumOverrideLvls.TryGetValue(numId.Value, out var ovr) && ovr.TryGetValue(il, out var lo)) lvl = lo;
                    else if (absL != null && absL.TryGetValue(il, out var la)) lvl = la;
                    if (lvl != null)
                    {
                        if (!c.Counters.TryGetValue(absId, out var cnt)) c.Counters[absId] = cnt = Enumerable.Repeat(-1, 9).ToArray();
                        if (c.UsedNum.Add(numId.Value)) foreach (var (l, st) in nm.Starts) if (l >= 0 && l < 9) cnt[l] = st - 1;
                        if (cnt[il] < 0) cnt[il] = lvl.Start - 1;
                        cnt[il]++;
                        for (int d = il + 1; d < 9; d++) cnt[d] = -1;
                        for (int d = 0; d < il; d++) if (cnt[d] < 0) cnt[d] = absL != null && absL.TryGetValue(d, out var ld) ? ld.Start : 1;
                        label = NumLabel(lvl, il, cnt, absL);
                        if (lvl.PP != null)
                        {
                            var bs = ParaStylePP(c, styleId);
                            if (cellBase?.pp != null && (styleId == null || styleId == c.DefParaStyle)) bs = bs.Over(cellBase.Value.pp);
                            pp = bs.Over(lvl.PP).Over(direct);
                        }
                    }
                }
            }
            var markRP = baseRP.Over(ParseRP(c, pPr?.Child("rPr")));
            var savedBg = c.Bg;
            if (pp.Shd != null) c.Bg = pp.Shd;
            var fcache = new Dictionary<string, string>();

            var css = new StringBuilder();
            string al = pp.Align switch
            {
                "center" => "center", "right" or "end" => "right",
                "both" or "distribute" or "thaiDistribute" or "lowKashida" or "mediumKashida" or "highKashida" => "justify",
                _ => "left"
            };
            css.Append("text-align:").Append(al).Append(';');
            int before = pp.BeforeAuto == true ? 280 : pp.Before ?? 0;
            int after = pp.AfterAuto == true ? 280 : pp.After ?? 0;
            css.Append("margin-top:").Append(N(before / 20.0)).Append("pt;margin-bottom:").Append(N(after / 20.0)).Append("pt;");
            int left = pp.Left ?? 0, right = pp.Right ?? 0;
            int firstLine = pp.Hanging != null ? -pp.Hanging.Value : pp.First ?? 0;
            if (left != 0) css.Append("margin-left:").Append(N(left / 20.0)).Append("pt;");
            if (right != 0) css.Append("margin-right:").Append(N(right / 20.0)).Append("pt;");
            if (firstLine != 0) css.Append("text-indent:").Append(N(firstLine / 20.0)).Append("pt;");
            var font = FontOf(c, markRP);
            var line = pp.Line;
            if (line == null || pp.LineRule == null || pp.LineRule == "auto")
                css.Append("line-height:").Append(((line ?? 240) / 240.0 * LineFactor(font)).ToString("0.000", Inv)).Append(';');
            else if (pp.LineRule == "exact")
                css.Append("line-height:").Append(N(line.Value / 20.0)).Append("pt;");
            else
                css.Append("line-height:max(").Append(LineFactor(font).ToString("0.000", Inv)).Append("em,").Append(N(line.Value / 20.0)).Append("pt);");
            if (pp.Shd != null) css.Append("background:").Append(Hex(pp.Shd.Value)).Append(';');
            if (pp.Bdr != null)
                foreach (var side in new[] { "top", "bottom", "left", "right" })
                    if (pp.Bdr.TryGetValue(side, out var b))
                    {
                        css.Append("border-").Append(side).Append(':').Append(BdrCss(b)).Append(';');
                        css.Append("padding-").Append(side).Append(':').Append(b.Space).Append("pt;");
                    }
            var markCss = new StringBuilder(); RunCss(c, markRP, markCss, fcache);

            var stops = new List<string>();
            if (pp.Tabs != null) foreach (var t in pp.Tabs) if (t.Align != "clear") stops.Add(N(t.Pos / 20.0) + ":" + t.Align + ":" + t.Leader);
            if (pp.Hanging is > 0) stops.Add(N(left / 20.0) + ":left:none");
            string tabsAttr = string.Join(";", stops);

            var baseCss = new StringBuilder(); RunCss(c, baseRP, baseCss, fcache);
            css.Append(baseCss);
            var pcls = Cls(c, css);
            var openTag = new StringBuilder();
            openTag.Append("<div class=\"p").Append(sect >= 0 && !inNote ? " b" : "");
            if (pcls != null) openTag.Append(' ').Append(pcls);
            openTag.Append('"');
            if (sect >= 0) openTag.Append(" data-s=\"").Append(sect).Append('"');
            openTag.Append(" data-il=\"").Append(N(left / 20.0)).Append('"');
            if (tabsAttr.Length > 0) openTag.Append(" data-tabs=\"").Append(Esc(tabsAttr)).Append('"');
            if (pp.Contextual == true) openTag.Append(" data-cs=\"").Append(Esc(styleId ?? "_")).Append('"');
            if (pp.KeepNext == true) openTag.Append(" data-kn=\"1\"");
            if (pcls == null) openTag.Append(" style=\"").Append(css).Append('"');
            openTag.Append('>');

            if (pp.PageBreakBefore == true && sect >= 0) outB.Append("<div class=\"pb\" data-s=\"").Append(sect).Append("\"></div>");
            outB.Append(openTag);
            if (label != null && lvl != null)
            {
                var lrp = markRP.Over(lvl.RP).Copy(); lrp.U = null; lrp.Highlight = null;
                var ls = new StringBuilder(); RunCss(c, lrp, ls, fcache);
                var lfont = FontOf(c, lrp);
                bool symFont = lfont?.ToLowerInvariant() is { } lf && (lf.Contains("symbol") || lf.Contains("wingding"));
                var txt = string.Concat(label.Select(ch => (ch >= 0xF000 && ch <= 0xF0FF) || symFont ? SymbolChar(ch, lfont) : ch.ToString()));
                int hang = pp.Hanging ?? 0;
                var lfl = lrp.Font?.ToLowerInvariant();
                if (txt.Length > 0 && lfl != null && (lfl.Contains("symbol") || lfl.Contains("wingding"))) ls.Append("font-family:sans-serif;");
                switch (lvl.Suff)
                {
                    case "tab": outB.Append("<span class=\"lbl\" style=\"").Append(ls).Append("min-width:").Append(N(Math.Max(hang, 0) / 20.0)).Append("pt\">").Append(Esc(txt)).Append("</span>"); break;
                    case "space": outB.Append(SpanOpen(c, ls)).Append(Esc(txt)).Append(" </span>"); break;
                    default: outB.Append(SpanOpen(c, ls)).Append(Esc(txt)).Append("</span>"); break;
                }
            }
            Inline(c, p.Kids, outB, baseRP, fcache, sect, openTag.ToString());
            outB.Append(SpanOpen(c, markCss)).Append("&#8203;</span>");
            outB.Append("</div>");
            c.Bg = savedBg;
        }

        private static string NumLabel(Lvl lvl, int il, int[] cnt, Dictionary<int, Lvl>? abs)
        {
            if (lvl.Fmt == "none") return "";
            if (lvl.Fmt == "bullet") return lvl.Text;
            var sb = new StringBuilder();
            int i = 0; var t = lvl.Text;
            while (i < t.Length)
            {
                if (t[i] == '%' && i + 1 < t.Length && char.IsDigit(t[i + 1]))
                {
                    int k = t[i + 1] - '1';
                    Lvl? l = k == il ? lvl : (abs != null && abs.TryGetValue(k, out var lk) ? lk : null);
                    int v = k >= 0 && k <= 8 ? Math.Max(cnt[k], l?.Start ?? 1) : 1;
                    sb.Append(FmtNum(v, lvl.IsLgl && k != il ? "decimal" : l?.Fmt ?? "decimal"));
                    i += 2;
                }
                else { sb.Append(t[i]); i++; }
            }
            return sb.ToString();
        }

        public static string FmtNum(int n, string fmt) => fmt switch
        {
            "lowerLetter" => Letters(n).ToLowerInvariant(), "upperLetter" => Letters(n),
            "lowerRoman" => Roman(n).ToLowerInvariant(), "upperRoman" => Roman(n),
            "decimalZero" => n < 10 ? "0" + n : n.ToString(Inv),
            "ordinal" => n + ".", "none" => "",
            "decimalEnclosedCircle" => n >= 1 && n <= 20 ? ((char)(0x2460 + n - 1)).ToString() : n.ToString(Inv),
            _ => n.ToString(Inv)
        };
        private static string Letters(int n0) { if (n0 <= 0) return ""; int n = n0 - 1; char ch = (char)('A' + n % 26); return new string(ch, n / 26 + 1); }
        private static string Roman(int n0)
        {
            int n = n0; if (n <= 0) return n0.ToString(Inv);
            int[] v = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] s = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new StringBuilder(); for (int k = 0; k < v.Length; k++) while (n >= v[k]) { sb.Append(s[k]); n -= v[k]; }
            return sb.ToString();
        }

        private static void Inline(Ctx c, List<XNode> kids, StringBuilder outB, RP baseRP, Dictionary<string, string> fc, int sect, string reopen)
        {
            foreach (var k in kids)
            {
                switch (k.Name)
                {
                    case "r": Run(c, k, outB, baseRP, fc, sect, reopen); break;
                    case "hyperlink":
                        {
                            string? href = null;
                            var rid = k.Attr("id");
                            if (rid != null && c.Rels.TryGetValue(rid, out var rel)) href = rel.Target;
                            else if (k.Attr("anchor") is { } an) href = "#" + an;
                            if (href != null && !href.StartsWith('#')) outB.Append("<a href=\"").Append(Esc(href)).Append("\">"); else outB.Append("<a>");
                            Inline(c, k.Kids, outB, baseRP, fc, sect, reopen);
                            outB.Append("</a>");
                            break;
                        }
                    case "ins": case "smartTag": case "customXml": case "moveTo": case "fldSimple": case "dir": case "bdo":
                        if (k.Name == "fldSimple")
                        {
                            var ins = (k.Attr("instr") ?? "").Trim().ToUpperInvariant();
                            if (ins.StartsWith("PAGE", StringComparison.Ordinal)) { outB.Append("<span class=\"pg\">1</span>"); break; }
                            if (ins.StartsWith("NUMPAGES", StringComparison.Ordinal) || ins.StartsWith("SECTIONPAGES", StringComparison.Ordinal)) { outB.Append("<span class=\"np\">1</span>"); break; }
                        }
                        Inline(c, k.Kids, outB, baseRP, fc, sect, reopen);
                        break;
                    case "sdt": { var sc = k.Child("sdtContent"); if (sc != null) Inline(c, sc.Kids, outB, baseRP, fc, sect, reopen); break; }
                    case "oMath": case "oMathPara":
                        if (c.FieldStack.Any(f => f[0] == 0)) break;
                        outB.Append("<span style=\"font-family:'Cambria Math','Times New Roman',serif;font-style:italic\">").Append(Esc(string.Concat(k.All("t").Select(t => t.Text)))).Append("</span>");
                        break;
                }
            }
        }

        private static void Run(Ctx c, XNode r, StringBuilder outB, RP baseRP, Dictionary<string, string> fc, int sect, string reopen)
        {
            var rPr = r.Child("rPr");
            var rp = baseRP;
            var rs = rPr?.Child("rStyle")?.V; if (rs != null) rp = rp.Over(CharStyleRP(c, rs));
            rp = rp.Over(ParseRP(c, rPr));
            bool hidden = rp.Vanish == true;
            var css = new StringBuilder(); RunCss(c, rp, css, fc);
            bool open = false;
            string spanTag = SpanOpen(c, css);
            void OpenSpan() { if (!open) { outB.Append(spanTag); open = true; } }
            void CloseSpan() { if (open) { outB.Append("</span>"); open = false; } }
            foreach (var k in r.Kids)
            {
                if (k.Name == "fldChar")
                {
                    switch (k.Attr("fldCharType"))
                    {
                        case "begin": c.FieldStack.Add(new[] { 0, 0 }); c.Instr.Clear(); break;
                        case "separate":
                            if (c.FieldStack.Count > 0)
                            {
                                var f = c.FieldStack[^1];
                                f[0] = 1;
                                var ins = c.Instr.ToString().Trim().ToUpperInvariant();
                                f[1] = ins.StartsWith("PAGE", StringComparison.Ordinal) && !ins.StartsWith("PAGEREF", StringComparison.Ordinal) ? 1
                                    : ins.StartsWith("NUMPAGES", StringComparison.Ordinal) || ins.StartsWith("SECTIONPAGES", StringComparison.Ordinal) ? 2 : 0;
                                if (f[1] != 0) { CloseSpan(); OpenSpan(); outB.Append(f[1] == 1 ? "<span class=\"pg\">1</span>" : "<span class=\"np\">1</span>"); CloseSpan(); }
                            }
                            break;
                        case "end":
                            if (c.FieldStack.Count > 0)
                            {
                                var f = c.FieldStack[^1]; c.FieldStack.RemoveAt(c.FieldStack.Count - 1);
                                if (f[0] == 0)
                                {
                                    var ins = c.Instr.ToString().Trim().ToUpperInvariant();
                                    if (ins.StartsWith("PAGE", StringComparison.Ordinal) && !ins.StartsWith("PAGEREF", StringComparison.Ordinal)) { OpenSpan(); outB.Append("<span class=\"pg\">1</span>"); CloseSpan(); }
                                    else if (ins.StartsWith("NUMPAGES", StringComparison.Ordinal)) { OpenSpan(); outB.Append("<span class=\"np\">1</span>"); CloseSpan(); }
                                }
                            }
                            break;
                    }
                    continue;
                }
                if (k.Name == "instrText") { if (c.FieldStack.Count > 0 && c.FieldStack[^1][0] == 0) c.Instr.Append(k.Text); continue; }
                var fs = c.FieldStack.Count > 0 ? c.FieldStack[^1] : null;
                if (fs != null && (fs[0] == 0 || fs[1] != 0)) continue;
                if (hidden && k.Name != "br") continue;
                switch (k.Name)
                {
                    case "t":
                        {
                            var t = k.Text;
                            if (t.Length > 0)
                            {
                                OpenSpan();
                                var fl = FontOf(c, rp)?.ToLowerInvariant();
                                if (fl != null && (fl.Contains("symbol") || fl.Contains("wingding"))) outB.Append(Esc(string.Concat(t.Select(ch => SymbolChar(ch, fl)))));
                                else outB.Append(Esc(t));
                            }
                            break;
                        }
                    case "tab": OpenSpan(); outB.Append("<span class=\"tab\"></span>"); break;
                    case "ptab": OpenSpan(); outB.Append("<span class=\"tab\" data-pa=\"").Append(Esc(k.Attr("alignment") ?? "left")).Append("\"></span>"); break;
                    case "br":
                        if (k.Attr("type") == "page" && sect >= 0) { CloseSpan(); outB.Append("</div><div class=\"pb\" data-s=\"").Append(sect).Append("\"></div>").Append(reopen.Replace("text-indent:", "x-ti:")); }
                        else { OpenSpan(); outB.Append("<br>"); }
                        break;
                    case "cr": OpenSpan(); outB.Append("<br>"); break;
                    case "noBreakHyphen": OpenSpan(); outB.Append("&#8209;"); break;
                    case "softHyphen": OpenSpan(); outB.Append("&shy;"); break;
                    case "sym":
                        {
                            var code = HexInt(k.Attr("char")); if (code == null) break;
                            OpenSpan(); outB.Append(Esc(SymbolChar((char)code.Value, k.Attr("font"))));
                            break;
                        }
                    case "footnoteReference": case "endnoteReference":
                        {
                            var id = k.Attr("id"); if (id == null) break;
                            bool isF = k.Name == "footnoteReference";
                            int no = isF ? ++c.NoteNo : ++c.EndNo;
                            c.NoteOrder.Add(((isF ? "f:" : "e:") + id, no));
                            OpenSpan(); outB.Append("<sup>").Append(no).Append("</sup>");
                            break;
                        }
                    case "drawing": CloseSpan(); Drawing(c, k, outB); break;
                    case "pict": case "object": CloseSpan(); Vml(c, k, outB); break;
                }
            }
            CloseSpan();
        }

        // ------------------------------------------------------------------ gambar & bentuk
        private static string? ImgSrc(Ctx c, string? rid, double w, double h)
        {
            if (rid == null || !c.Rels.TryGetValue(rid, out var rel) || rel.External) return null;
            return c.Res.Url(rel.Target, w * 4 / 3, h * 4 / 3);
        }

        private static double Emu(string? v) => (ToLong(v) ?? 0L) / 12700.0;

        private static void Drawing(Ctx c, XNode d, StringBuilder outB)
        {
            var anchor = d.Child("anchor");
            var box = anchor ?? d.Child("inline"); if (box == null) return;
            var ext = box.Child("extent");
            double w = Emu(ext?.Attr("cx")), h = Emu(ext?.Attr("cy"));
            var inner = new StringBuilder();
            GraphicContent(c, box.Path("graphic", "graphicData"), w, h, inner);
            if (inner.Length == 0) return;
            if (anchor == null)
            {
                outB.Append("<span class=\"ig\" style=\"display:inline-block;vertical-align:bottom;width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\">").Append(inner).Append("</span>");
                return;
            }
            bool behind = anchor.Attr("behindDoc") == "1";
            var ph = anchor.Child("positionH"); var pv = anchor.Child("positionV");
            string hRel = ph?.Attr("relativeFrom") ?? "column", vRel = pv?.Attr("relativeFrom") ?? "paragraph";
            double? hOff = ph?.Child("posOffset")?.Text is { } ho ? Emu(ho) : null; var hAl = ph?.Child("align")?.Text;
            double? vOff = pv?.Child("posOffset")?.Text is { } vo ? Emu(vo) : null; var vAl = pv?.Child("align")?.Text;
            string wrap = anchor.Child("wrapNone") != null ? "none" : anchor.Child("wrapTopAndBottom") != null ? "tb"
                : anchor.Child("wrapSquare") != null || anchor.Child("wrapTight") != null || anchor.Child("wrapThrough") != null ? "sq" : "none";
            double dl = Emu(anchor.Attr("distL")), dr = Emu(anchor.Attr("distR")), dt = Emu(anchor.Attr("distT")), db = Emu(anchor.Attr("distB"));
            if (wrap == "tb")
            {
                outB.Append("<span class=\"anc tb\" style=\"display:block;width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt;margin:")
                    .Append(N(dt)).Append("pt 0 ").Append(N(db)).Append("pt ").Append(N(hAl == null && hRel != "page" ? hOff ?? 0 : 0)).Append("pt\"");
                if (hAl != null) outB.Append(" data-ha=\"").Append(Esc(hAl)).Append('"');
                outB.Append('>').Append(inner).Append("</span>");
                return;
            }
            if (wrap == "sq")
            {
                bool right = hAl is "right" or "outside" || (hAl == null && (hOff ?? 0) > 200);
                outB.Append("<span class=\"anc sq\" style=\"float:").Append(right ? "right" : "left").Append(";width:").Append(N(w)).Append("pt;height:").Append(N(h))
                    .Append("pt;margin:").Append(N(dt)).Append("pt ").Append(N(dr)).Append("pt ").Append(N(db)).Append("pt ").Append(N(dl)).Append("pt;");
                if (!right && hAl == null && hRel != "page") outB.Append("margin-left:").Append(N(hOff ?? 0)).Append("pt;");
                outB.Append("\">").Append(inner).Append("</span>");
                return;
            }
            outB.Append("<span class=\"anc abs\" data-hr=\"").Append(Esc(hRel)).Append("\" data-vr=\"").Append(Esc(vRel)).Append('"');
            if (hOff != null) outB.Append(" data-hx=\"").Append(N(hOff.Value)).Append('"');
            if (hAl != null) outB.Append(" data-ha=\"").Append(Esc(hAl)).Append('"');
            if (vOff != null) outB.Append(" data-vy=\"").Append(N(vOff.Value)).Append('"');
            if (vAl != null) outB.Append(" data-va=\"").Append(Esc(vAl)).Append('"');
            outB.Append(" data-w=\"").Append(N(w)).Append("\" data-h=\"").Append(N(h)).Append("\" style=\"position:absolute;width:").Append(N(w)).Append("pt;height:").Append(N(h))
                .Append("pt;z-index:").Append(behind ? -1 : 3).Append("\">").Append(inner).Append("</span>");
        }

        private static void GraphicContent(Ctx c, XNode? gd, double w, double h, StringBuilder outB)
        {
            if (gd == null) return;
            if (gd.Child("pic") is { } pic) { Picture(c, pic, w, h, outB); return; }
            if (gd.Child("wsp") is { } sp) { ShapeBox(c, sp, w, h, outB); return; }
            if (gd.Child("wgp") is { } g) { Group(c, g, w, h, outB); return; }
            if (gd.Child("wpc") is { } g2) { Group(c, g2, w, h, outB); return; }
            if (gd.Child("chart") != null || gd.First("chart") != null)
            {
                outB.Append("<span class=\"ph\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\">Grafik</span>"); return;
            }
            var b = gd.First("blip");
            if (b != null && ImgSrc(c, b.Attr("embed") ?? b.Attr("link"), w, h) is { } src)
                outB.Append("<img src=\"").Append(src).Append("\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\">");
        }

        private static void Picture(Ctx c, XNode pic, double w, double h, StringBuilder outB)
        {
            var blip = pic.First("blip");
            var crop = pic.First("srcRect");
            double l = 0, t = 0, r = 0, b = 0;
            if (crop != null && crop.Attrs.Count > 0)
            {
                l = (ToDouble(crop.Attr("l")) ?? 0) / 100000; t = (ToDouble(crop.Attr("t")) ?? 0) / 100000;
                r = (ToDouble(crop.Attr("r")) ?? 0) / 100000; b = (ToDouble(crop.Attr("b")) ?? 0) / 100000;
            }
            double fw = w / Math.Max(0.01, 1 - l - r), fh = h / Math.Max(0.01, 1 - t - b);
            var src = ImgSrc(c, blip?.Attr("embed") ?? blip?.Attr("link"), fw, fh);
            if (src == null) { outB.Append("<span class=\"ph\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\"></span>"); return; }
            var xf = pic.First("xfrm");
            double rot = (ToLong(xf?.Attr("rot")) ?? 0) / 60000.0;
            var tr = new StringBuilder();
            if (rot != 0 || xf?.Attr("flipH") == "1" || xf?.Attr("flipV") == "1")
            {
                tr.Append("transform:"); if (rot != 0) tr.Append("rotate(").Append(N(rot)).Append("deg) ");
                if (xf?.Attr("flipH") == "1") tr.Append("scaleX(-1) "); if (xf?.Attr("flipV") == "1") tr.Append("scaleY(-1)");
                tr.Append(';');
            }
            switch (pic.First("prstGeom")?.Attr("prst")) { case "ellipse": tr.Append("border-radius:50%;"); break; case "roundRect": tr.Append("border-radius:12%;"); break; }
            bool clip = tr.ToString().Contains("border-radius");
            if (l != 0 || t != 0 || r != 0 || b != 0 || clip)
                outB.Append("<span style=\"display:block;overflow:hidden;width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt;").Append(tr).Append("\"><img src=\"").Append(src)
                    .Append("\" style=\"width:").Append(N(fw)).Append("pt;height:").Append(N(fh)).Append("pt;margin-left:").Append(N(-fw * l)).Append("pt;margin-top:").Append(N(-fh * t)).Append("pt;max-width:none\"></span>");
            else outB.Append("<img src=\"").Append(src).Append("\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt;").Append(tr).Append("\">");
        }

        private static int? DmlColor(Ctx c, XNode? n)
        {
            if (n == null) return null;
            var cn = n.Child("srgbClr") ?? n.Child("schemeClr") ?? n.Child("sysClr") ?? n.Child("prstClr");
            if (cn == null) return null;
            int? rgb = cn.Name switch
            {
                "srgbClr" => ParseHex(cn.V),
                "sysClr" => ParseHex(cn.Attr("lastClr")),
                "schemeClr" => c.Theme.TryGetValue(cn.V switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", _ => cn.V ?? "" }, out var tv) ? tv : null,
                _ => cn.V switch { "black" => 0, "white" => 0xFFFFFF, "red" => 0xFF0000, _ => 0x808080 }
            };
            if (rgb == null) return null;
            int col = rgb.Value;
            float mod = 1f, off = 0f;
            foreach (var m in cn.Kids)
                switch (m.Name)
                {
                    case "lumMod": mod = (float)((ToDouble(m.V) ?? 100000) / 100000); break;
                    case "lumOff": off = (float)((ToDouble(m.V) ?? 0) / 100000); break;
                    case "tint": col = Mix(0xFFFFFF, col, (float)((ToDouble(m.V) ?? 100000) / 100000)); break;
                    case "shade": col = Mix(0, col, (float)((ToDouble(m.V) ?? 100000) / 100000)); break;
                }
            if (mod != 1f || off != 0f) col = Lum(col, mod, off);
            return col;
        }

        private static (string First, string Css)? GradStops(Ctx c, XNode g)
        {
            var gs = g.First("gsLst")?.Children("gs"); if (gs == null) return null;
            var stops = gs.Select(s => DmlColor(c, s) is { } col ? ((ToDouble(s.Attr("pos")) ?? 0) / 1000, Hex(col)) : ((double, string)?)null)
                .Where(x => x != null).Select(x => x!.Value).ToList();
            if (stops.Count == 0) return null;
            double ang = (ToLong(g.Child("lin")?.Attr("ang")) ?? 0) / 60000.0 + 90;
            return (stops[0].Item2, "linear-gradient(" + N(ang) + "deg," + string.Join(",", stops.OrderBy(s => s.Item1).Select(s => s.Item2 + " " + N(s.Item1) + "%")) + ")");
        }

        /// <summary>Bentuk jalur bebas (a:custGeom): moveTo/lnTo/cubicBezTo/quadBezTo/close → path SVG berskala ukuran bentuk.</summary>
        private static string? CustPath(XNode? cg, double w, double h)
        {
            var pl = cg?.Child("pathLst");
            if (pl == null) return null;
            var d = new StringBuilder();
            foreach (var path in pl.Children("path"))
            {
                double pw = ToDouble(path.Attr("w")) ?? 0, ph = ToDouble(path.Attr("h")) ?? 0;
                double sx = pw > 0 ? w / pw : 1 / 12700.0, sy = ph > 0 ? h / ph : 1 / 12700.0;
                string P(XNode pt) => N((ToDouble(pt.Attr("x")) ?? 0) * sx) + " " + N((ToDouble(pt.Attr("y")) ?? 0) * sy);
                foreach (var cmd in path.Kids)
                {
                    var pts = cmd.Children("pt").ToList();
                    switch (cmd.Name)
                    {
                        case "moveTo" when pts.Count > 0: d.Append('M').Append(P(pts[0])).Append(' '); break;
                        case "lnTo" when pts.Count > 0: d.Append('L').Append(P(pts[0])).Append(' '); break;
                        case "cubicBezTo" when pts.Count >= 3: d.Append('C').Append(P(pts[0])).Append(' ').Append(P(pts[1])).Append(' ').Append(P(pts[2])).Append(' '); break;
                        case "quadBezTo" when pts.Count >= 2: d.Append('Q').Append(P(pts[0])).Append(' ').Append(P(pts[1])).Append(' '); break;
                        case "close": d.Append("Z "); break;
                    }
                }
            }
            return d.Length > 0 ? d.ToString() : null;
        }

        private static void ShapeBox(Ctx c, XNode sp, double w, double h, StringBuilder outB)
        {
            var spPr = sp.Child("spPr");
            var css = new StringBuilder("display:block;position:relative;width:" + N(w) + "pt;height:" + N(h) + "pt;box-sizing:border-box;overflow:hidden;");
            var x = spPr?.Child("xfrm");
            if (x != null)
            {
                double rot = (ToLong(x.Attr("rot")) ?? 0) / 60000.0;
                bool fh = x.Attr("flipH") == "1", fv = x.Attr("flipV") == "1";
                if (rot != 0 || fh || fv) css.Append("transform:").Append(rot != 0 ? "rotate(" + N(rot) + "deg) " : "").Append(fh ? "scaleX(-1) " : "").Append(fv ? "scaleY(-1)" : "").Append(';');
            }
            string? picFill = null;
            var blipFill = spPr?.Child("blipFill");
            var gradFill = spPr?.Child("gradFill");
            if (blipFill != null) picFill = ImgSrc(c, blipFill.First("blip")?.Attr("embed"), w, h);
            if (picFill != null) css.Append("background:url('").Append(picFill).Append("') center/100% 100% no-repeat;");
            else if (gradFill != null) { var g = GradStops(c, gradFill); if (g != null) css.Append("background:").Append(g.Value.Css).Append(';'); }
            else if (spPr?.Child("noFill") == null)
            {
                var sc = DmlColor(c, spPr?.Child("solidFill"));
                if (sc != null) css.Append("background:").Append(Hex(sc.Value)).Append(';');
                else if (spPr?.Child("solidFill") == null && blipFill == null)
                {
                    var fr = sp.Path("style", "fillRef");
                    if (fr != null && fr.Attr("idx") != "0" && DmlColor(c, fr) is { } fc) css.Append("background:").Append(Hex(fc)).Append(';');
                }
            }
            var ln = spPr?.Child("ln");
            if (ln != null && ln.Child("noFill") == null)
            {
                double lw0 = Emu(ln.Attr("w")); double lw = Math.Max(0.5, lw0 > 0 ? lw0 : 0.75);
                int col = DmlColor(c, ln.Child("solidFill")) ?? 0;
                css.Append("border:").Append(N(lw)).Append("pt solid ").Append(Hex(col)).Append(';');
            }
            else if (ln == null)
            {
                var lr = sp.Path("style", "lnRef");
                if (lr != null && lr.Attr("idx") != "0" && DmlColor(c, lr) is { } lc) css.Append("border:0.75pt solid ").Append(Hex(lc)).Append(';');
            }
            var prst = spPr?.Child("prstGeom")?.Attr("prst") ?? "rect";
            var custD = CustPath(spPr?.Child("custGeom"), w, h);
            if (custD != null) prst = "custom";
            if (prst != "rect" && prst != "roundRect" && prst != "ellipse")
            {
                var cssS = css.ToString();
                var bgM = picFill == null ? CSS_BG.Match(cssS) is { Success: true } mb ? mb.Groups[1].Value : null : null;
                var bdM = CSS_BORDER.Match(cssS);
                var cleaned = CSS_BORDER_ANY.Replace(CSS_BG_ANY.Replace(cssS, ""), "");
                css.Clear(); css.Append(cleaned);
                var adj = new Dictionary<string, double>();
                var av = spPr?.First("avLst");
                if (av != null) foreach (var gd in av.Children("gd"))
                    {
                        var fm = gd.Attr("fmla") ?? ""; int vi = fm.IndexOf("val ", StringComparison.Ordinal);
                        adj[gd.Attr("name") ?? ""] = vi >= 0 ? ToDouble(fm[(vi + 4)..].Trim()) ?? 0 : 0;
                    }
                bool isLine = prst == "line" || prst.Contains("Connector");
                var d = custD ?? ShapePaths.Preset(prst, w, h, adj);
                var sw = bdM.Success ? bdM.Groups[1].Value : "0.75";
                string fillSvg = isLine ? "none" : picFill != null ? "url(#pf" + c.ShapeNo + ")"
                    : bgM != null && !bgM.StartsWith("url(", StringComparison.Ordinal) && !bgM.Contains("gradient") ? bgM
                    : gradFill != null ? GradStops(c, gradFill)?.First ?? "none" : "none";
                var defs = picFill != null && !isLine
                    ? "<defs><pattern id=\"pf" + c.ShapeNo + "\" patternUnits=\"userSpaceOnUse\" width=\"" + N(w) + "\" height=\"" + N(h) + "\"><image href=\"" + picFill + "\" width=\"" + N(w) + "\" height=\"" + N(h) + "\" preserveAspectRatio=\"none\"/></pattern></defs>" : "";
                c.ShapeNo++;
                var stroke = bdM.Success ? bdM.Groups[2].Value : isLine ? "#000" : "none";
                var svg = "<svg style=\"position:absolute;left:0;top:0;overflow:visible\" width=\"" + N(w) + "pt\" height=\"" + N(h) + "pt\" viewBox=\"0 0 " + N(w) + " " + N(h) +
                    "\" preserveAspectRatio=\"none\">" + defs + "<path d=\"" + d + "\" fill=\"" + Esc(fillSvg) + "\" stroke=\"" + Esc(stroke) + "\" stroke-width=\"" + Esc(sw) + "\" vector-effect=\"non-scaling-stroke\"/></svg>";
                css.Append("overflow:visible;");
                outB.Append("<span class=\"shp\" style=\"").Append(css).Append("\">").Append(svg);
                var tc = sp.Path("txbx", "txbxContent");
                if (tc != null)
                {
                    outB.Append("<span style=\"display:block;position:relative\">");
                    foreach (var p in Flatten(tc.Kids)) Block(c, p, outB, -1, null);
                    outB.Append("</span>");
                }
                outB.Append("</span>");
                return;
            }
            if (prst == "ellipse") css.Append("border-radius:50%;"); else if (prst == "roundRect") css.Append("border-radius:10%;");
            var bp = sp.Child("bodyPr");
            double li = bp?.Attr("lIns") is { } a1 ? Emu(a1) : 7.2, ri = bp?.Attr("rIns") is { } a2 ? Emu(a2) : 7.2;
            double ti = bp?.Attr("tIns") is { } a3 ? Emu(a3) : 3.6, bi = bp?.Attr("bIns") is { } a4 ? Emu(a4) : 3.6;
            var tx = sp.Path("txbx", "txbxContent");
            // inset teks hanya untuk bentuk berisi teks: pada garis/kotak tipis tanpa teks, padding border-box melebarkan bentuk
            if (tx != null) css.Append("padding:").Append(N(ti)).Append("pt ").Append(N(ri)).Append("pt ").Append(N(bi)).Append("pt ").Append(N(li)).Append("pt;");
            switch (bp?.Attr("anchor"))
            {
                case "ctr": css.Append("display:flex;flex-direction:column;justify-content:center;"); break;
                case "b": css.Append("display:flex;flex-direction:column;justify-content:flex-end;"); break;
            }
            outB.Append("<span class=\"shp\" style=\"").Append(css).Append("\">");
            if (tx != null)
            {
                outB.Append("<span style=\"display:block\">");
                foreach (var p in Flatten(tx.Kids)) Block(c, p, outB, -1, null);
                outB.Append("</span>");
            }
            outB.Append("</span>");
        }

        private static void Group(Ctx c, XNode g, double w, double h, StringBuilder outB)
        {
            var xf = g.Path("grpSpPr", "xfrm");
            double chOffX = Emu(xf?.Child("chOff")?.Attr("x")), chOffY = Emu(xf?.Child("chOff")?.Attr("y"));
            double chW = Emu(xf?.Child("chExt")?.Attr("cx")); if (chW <= 0) chW = w;
            double chH = Emu(xf?.Child("chExt")?.Attr("cy")); if (chH <= 0) chH = h;
            double sx = w / chW, sy = h / chH;
            outB.Append("<span style=\"display:block;position:relative;width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\">");
            foreach (var k in g.Kids)
            {
                var kx = (k.Child("spPr") ?? k.Child("grpSpPr"))?.Child("xfrm") ?? k.Child("xfrm");
                if (kx == null) continue;
                double x = (Emu(kx.Child("off")?.Attr("x")) - chOffX) * sx, y = (Emu(kx.Child("off")?.Attr("y")) - chOffY) * sy;
                double kw = Emu(kx.Child("ext")?.Attr("cx")) * sx, kh = Emu(kx.Child("ext")?.Attr("cy")) * sy;
                outB.Append("<span style=\"position:absolute;left:").Append(N(x)).Append("pt;top:").Append(N(y)).Append("pt;width:").Append(N(kw)).Append("pt;height:").Append(N(kh)).Append("pt\">");
                switch (k.Name)
                {
                    case "pic": Picture(c, k, kw, kh, outB); break;
                    case "wsp": ShapeBox(c, k, kw, kh, outB); break;
                    case "grpSp": case "wgp": Group(c, k, kw, kh, outB); break;
                    case "graphicFrame": GraphicContent(c, k.Path("graphic", "graphicData"), kw, kh, outB); break;
                }
                outB.Append("</span>");
            }
            outB.Append("</span>");
        }

        /// <summary>VML lama (w:pict / w:object): gambar &amp; kotak teks.</summary>
        private static void Vml(Ctx c, XNode n, StringBuilder outB)
        {
            var shape = n.First("shape") ?? n.First("rect") ?? n.First("roundrect"); if (shape == null) return;
            var style = shape.Attr("style") ?? "";
            double? Dim(string key)
            {
                var m = Regex.Match(style, "(?:^|;)\\s*" + key + "\\s*:\\s*([-0-9.]+)(pt|in|px|mm|cm)?");
                if (!m.Success) return null;
                var v = ToDouble(m.Groups[1].Value); if (v == null) return null;
                return m.Groups[2].Value switch { "in" => v * 72, "px" => v * 0.75, "mm" => v * 72 / 25.4, "cm" => v * 72 / 2.54, _ => v };
            }
            double w = Dim("width") ?? 100.0, h = Dim("height") ?? 50.0;
            var img = shape.First("imagedata");
            string? imgRid = img != null ? img.Attr("r:id") ?? img.Attr("id") ?? img.Attr("relid") : null;
            if (imgRid == null)
            {
                var fill = shape.Child("fill");
                if (fill != null && fill.Attr("type") is "frame" or "tile") imgRid = fill.Attr("r:id") ?? fill.Attr("id") ?? fill.Attr("relid");
            }
            var inner = new StringBuilder();
            if (imgRid != null)
            {
                var src = ImgSrc(c, imgRid, w, h);
                if (src != null) inner.Append("<img src=\"").Append(src).Append("\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\">");
                else inner.Append("<span class=\"ph\" style=\"width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt\"></span>");
            }
            else if (shape.First("txbxContent") is { } tc)
            {
                var fill = ParseHex(shape.Attr("fillcolor")?.Split(' ')[0]);
                inner.Append("<span style=\"display:block;width:").Append(N(w)).Append("pt;min-height:").Append(N(h)).Append("pt;box-sizing:border-box;padding:3.6pt 7.2pt;");
                if (shape.Attr("stroked") != "f") inner.Append("border:0.75pt solid ").Append(Hex(ParseHex(shape.Attr("strokecolor")) ?? 0)).Append(';');
                if (fill != null) inner.Append("background:").Append(Hex(fill.Value)).Append(';');
                inner.Append("\">");
                foreach (var p in Flatten(tc.Kids)) Block(c, p, inner, -1, null);
                inner.Append("</span>");
            }
            if (inner.Length == 0) return;
            if (style.Contains("position:absolute"))
            {
                double ml = Dim("margin-left") ?? Dim("left") ?? 0, mt = Dim("margin-top") ?? Dim("top") ?? 0;
                var hr = VML_HREL.Match(style) is { Success: true } hm ? hm.Groups[1].Value : "column";
                var vr = VML_VREL.Match(style) is { Success: true } vm ? vm.Groups[1].Value : "paragraph";
                outB.Append("<span class=\"anc abs\" data-hr=\"").Append(hr == "page" ? "page" : hr == "margin" ? "margin" : "column")
                    .Append("\" data-vr=\"").Append(vr == "page" ? "page" : vr == "margin" ? "margin" : "paragraph")
                    .Append("\" data-hx=\"").Append(N(ml)).Append("\" data-vy=\"").Append(N(mt)).Append("\" data-w=\"").Append(N(w)).Append("\" data-h=\"").Append(N(h))
                    .Append("\" style=\"position:absolute;width:").Append(N(w)).Append("pt;height:").Append(N(h)).Append("pt;z-index:").Append(style.Contains("z-index:-") ? -1 : 3).Append("\">")
                    .Append(inner).Append("</span>");
            }
            else outB.Append("<span style=\"display:inline-block;vertical-align:bottom\">").Append(inner).Append("</span>");
        }

        // ------------------------------------------------------------------ tabel
        private sealed record Cond(PP? PP, RP? RP, int? Shd, Dictionary<string, Bdr>? Bdr, XNode? TcPr);

        /// <summary>Gaya yang berulang → kelas CSS bersama (HTML jauh lebih kecil &amp; cepat dirender).</summary>
        private static string? Cls(Ctx c, StringBuilder css)
        {
            if (css.Length == 0) return null;
            var key = css.ToString();
            if (c.ClsMap.TryGetValue(key, out var v)) return v;
            if (c.ClsMap.Count >= 8000) return null;
            var n = "z" + ToBase36(c.ClsMap.Count);
            c.ClsMap[key] = n;
            c.ClsCss.Append("#pages .").Append(n).Append(",#flow .").Append(n).Append('{').Append(key).Append('}');
            return n;
        }

        private static string ToBase36(int v)
        {
            const string D = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (v == 0) return "0";
            var sb = new StringBuilder(); while (v > 0) { sb.Insert(0, D[v % 36]); v /= 36; }
            return sb.ToString();
        }

        private static string SpanOpen(Ctx c, StringBuilder css) => Cls(c, css) is { } n ? "<span class=\"" + n + "\">" : "<span style=\"" + css + "\">";

        private const int MAX_TBL_NEST = 16;

        private static void Table(Ctx c, XNode t, StringBuilder outB, int sect)
        {
            if (c.TblDepth >= MAX_TBL_NEST) { FlatTable(c, t, outB, sect); return; }
            c.TblDepth++;
            try { TableImpl(c, t, outB, sect); } finally { c.TblDepth--; }
        }

        private static void FlatTable(Ctx c, XNode t, StringBuilder outB, int sect)
        {
            foreach (var tr in Through(t, "tr")) foreach (var tc in Through(tr, "tc")) foreach (var b in Flatten(tc.Kids))
                        if (b.Name == "tbl") FlatTable(c, b, outB, sect); else Paragraph(c, b, outB, sect, null);
        }

        private sealed class Cell
        {
            public readonly XNode Node; public readonly int Col, Span; public int Rowspan = 1; public bool Skip;
            public Cell(XNode node, int col, int span) { Node = node; Col = col; Span = span; }
        }

        private static void TableImpl(Ctx c, XNode t, StringBuilder outB, int sect)
        {
            var tblPr = t.Child("tblPr");
            var styleId = tblPr?.Child("tblStyle")?.V ?? c.DefTableStyle;
            var chain = StyleChain(c, styleId);
            PP? tsPP = null; RP? tsRP = null;
            var tsBorders = new Dictionary<string, Bdr>(); int? tsShd = null;
            var conds = new Dictionary<string, Cond>();
            int[] cellMar = { 0, 108, 0, 108 };
            foreach (var s in chain)
            {
                if (s.Node.Child("pPr") is { } spp) tsPP = (tsPP ?? new PP()).Over(ParsePP(c, spp));
                if (s.Node.Child("rPr") is { } srp) tsRP = (tsRP ?? new RP()).Over(ParseRP(c, srp));
                if (s.Node.Child("tblPr") is { } tp)
                {
                    if (tp.Child("tblBorders") is { } tb) foreach (var b in tb.Kids) if (BdrOf(b) is { } bd) tsBorders[Norm(b.Name)] = bd;
                    if (tp.Child("tblCellMar") is { } cm) cellMar = Mar(cm, cellMar);
                }
                if (s.Node.Child("tcPr") is { } tcp && ShdOf(c, tcp.Child("shd")) is { } sh) tsShd = sh;
                foreach (var cp in s.Node.Children("tblStylePr"))
                {
                    var type = cp.Attr("type"); if (type == null) continue;
                    conds.TryGetValue(type, out var prev);
                    var bd = new Dictionary<string, Bdr>(prev?.Bdr ?? new());
                    if (cp.Path("tcPr", "tcBorders") is { } tcb) foreach (var b in tcb.Kids) if (BdrOf(b) is { } bb) bd[Norm(b.Name)] = bb;
                    conds[type] = new Cond(
                        (prev?.PP ?? new PP()).Over(cp.Child("pPr") is { } cpp ? ParsePP(c, cpp) : null),
                        (prev?.RP ?? new RP()).Over(cp.Child("rPr") is { } crp ? ParseRP(c, crp) : null),
                        ShdOf(c, cp.Path("tcPr", "shd")) ?? prev?.Shd, bd, cp.Child("tcPr") ?? prev?.TcPr);
                }
            }
            var tBorders = new Dictionary<string, Bdr>(tsBorders);
            if (tblPr?.Child("tblBorders") is { } tbb) foreach (var b in tbb.Kids) if (BdrOf(b) is { } bd) tBorders[Norm(b.Name)] = bd;
            if (tblPr?.Child("tblCellMar") is { } tcm) cellMar = Mar(tcm, cellMar);
            var look = tblPr?.Child("tblLook");
            bool LookFlag(string attr, int bit, bool def)
            {
                var a = look?.Attr(attr); if (a != null) return a is "1" or "true";
                var v = HexInt(look?.V); if (v == null) return def;
                return (v.Value & bit) != 0;
            }
            bool firstRow = LookFlag("firstRow", 0x20, true), lastRow = LookFlag("lastRow", 0x40, false);
            bool firstCol = LookFlag("firstColumn", 0x80, true), lastCol = LookFlag("lastColumn", 0x100, false);
            bool noHBand = LookFlag("noHBand", 0x200, false), noVBand = LookFlag("noVBand", 0x400, true);

            var grid = t.Child("tblGrid")?.Children("gridCol").Select(g => ToInt(g.Attr("w")) ?? 0).ToList() ?? new List<int>();
            var rows = Through(t, "tr");
            var R = rows.Select(tr =>
            {
                int col = ToInt(tr.Path("trPr", "gridBefore")?.V) ?? 0;
                return Through(tr, "tc").Select(tc =>
                {
                    int sp = ToInt(tc.Path("tcPr", "gridSpan")?.V) ?? 1;
                    var cell = new Cell(tc, col, sp); col += sp; return cell;
                }).ToList();
            }).ToList();
            for (int ri = 0; ri < R.Count; ri++)
                foreach (var cell in R[ri])
                {
                    var vm = cell.Node.Path("tcPr", "vMerge");
                    if (vm == null || vm.V != "restart") continue;
                    int n = 1;
                    for (int rj = ri + 1; rj < R.Count; rj++)
                    {
                        var below = R[rj].FirstOrDefault(x => x.Col == cell.Col);
                        if (below == null) break;
                        var bvm = below.Node.Path("tcPr", "vMerge");
                        if (bvm != null && bvm.V != "restart") { below.Skip = true; n++; } else break;
                    }
                    cell.Rowspan = n;
                }
            int totalGrid = grid.Sum();
            var tblW = tblPr?.Child("tblW");
            string widthCss;
            switch (tblW?.Attr("type"))
            {
                case "pct":
                    {
                        var v = tblW.Attr("w") ?? "0";
                        widthCss = v.EndsWith('%') ? "width:" + Esc(v) + ";" : "width:" + N((ToDouble(v) ?? 0) / 50) + "%;";
                        break;
                    }
                case "dxa":
                    {
                        int wv = ToInt(tblW.Attr("w")) ?? 0;
                        widthCss = wv > 0 ? "width:" + N(wv / 20.0) + "pt;" : totalGrid > 0 ? "width:" + N(totalGrid / 20.0) + "pt;" : "";
                        break;
                    }
                default: widthCss = totalGrid > 0 ? "width:" + N(totalGrid / 20.0) + "pt;" : ""; break;
            }
            var jc = tblPr?.Child("jc")?.V;
            int ind = ToInt(tblPr?.Child("tblInd")?.Attr("w")) ?? 0;
            outB.Append("<table class=\"").Append(sect >= 0 ? "b" : "").Append('"');
            if (sect >= 0) outB.Append(" data-s=\"").Append(sect).Append('"');
            outB.Append(" style=\"").Append(widthCss);
            if (jc == "center") outB.Append("margin-left:auto;margin-right:auto;");
            else if (jc is "right" or "end") outB.Append("margin-left:auto;");
            else outB.Append("margin-left:").Append(N((ind != 0 ? ind - cellMar[1] : -cellMar[1]) / 20.0)).Append("pt;");
            if (tblPr?.Child("tblLayout")?.Attr("type") == "fixed" || grid.Count > 0) outB.Append("table-layout:fixed;");
            outB.Append("\">");
            if (grid.Count > 0) { outB.Append("<colgroup>"); foreach (var g in grid) outB.Append("<col style=\"width:").Append(N(g / 20.0)).Append("pt\">"); outB.Append("</colgroup>"); }
            int nCols = Math.Max(grid.Count, R.Count == 0 ? 0 : R.Max(r => r.Count == 0 ? 0 : r[^1].Col + r[^1].Span));
            int nRows = R.Count;
            int bandRow = 0;
            for (int ri = 0; ri < R.Count; ri++)
            {
                var tr = rows[ri];
                var trPr = tr.Child("trPr");
                bool isHeader = trPr?.Child("tblHeader") != null;
                var hgt = trPr?.Child("trHeight");
                outB.Append("<tr");
                if (isHeader) outB.Append(" class=\"th\"");
                if (hgt != null) outB.Append(" style=\"height:").Append(N((ToInt(hgt.V) ?? 0) / 20.0)).Append("pt\"");
                outB.Append('>');
                bool isFirst = firstRow && ri == 0, isLast = lastRow && ri == nRows - 1;
                if (!isFirst && !isLast) bandRow++;
                foreach (var cell in R[ri])
                {
                    if (cell.Skip) continue;
                    var tcPr = cell.Node.Child("tcPr");
                    var applied = new List<Cond>();
                    void Add(string k) { if (conds.TryGetValue(k, out var cd)) applied.Add(cd); }
                    if (!noHBand && !isFirst && !isLast) Add((bandRow - 1) % 2 == 0 ? "band1Horz" : "band2Horz");
                    if (!noVBand) Add(cell.Col % 2 == 0 ? "band1Vert" : "band2Vert");
                    if (firstCol && cell.Col == 0) Add("firstCol");
                    if (lastCol && cell.Col + cell.Span == nCols) Add("lastCol");
                    if (isFirst) Add("firstRow"); if (isLast) Add("lastRow");
                    if (isFirst && firstCol && cell.Col == 0) Add("nwCell"); if (isFirst && lastCol && cell.Col + cell.Span == nCols) Add("neCell");
                    if (isLast && firstCol && cell.Col == 0) Add("swCell"); if (isLast && lastCol && cell.Col + cell.Span == nCols) Add("seCell");
                    var cPP = tsPP; var cRP = tsRP; var shd = tsShd;
                    var bd = new Dictionary<string, Bdr?>
                    {
                        ["top"] = ri == 0 ? tBorders.GetValueOrDefault("top") : tBorders.GetValueOrDefault("insideH"),
                        ["bottom"] = ri + cell.Rowspan >= nRows ? tBorders.GetValueOrDefault("bottom") : tBorders.GetValueOrDefault("insideH"),
                        ["left"] = cell.Col == 0 ? tBorders.GetValueOrDefault("left") : tBorders.GetValueOrDefault("insideV"),
                        ["right"] = cell.Col + cell.Span >= nCols ? tBorders.GetValueOrDefault("right") : tBorders.GetValueOrDefault("insideV")
                    };
                    foreach (var a in applied)
                    {
                        if (a.PP != null) cPP = (cPP ?? new PP()).Over(a.PP);
                        if (a.RP != null) cRP = (cRP ?? new RP()).Over(a.RP);
                        if (a.Shd != null) shd = a.Shd;
                        if (a.Bdr != null)
                            foreach (var (k, v) in a.Bdr)
                            {
                                if (k == "insideH") { if (ri > 0) bd["top"] = v; if (ri + cell.Rowspan < nRows) bd["bottom"] = v; }
                                else if (k == "insideV") { if (cell.Col > 0) bd["left"] = v; if (cell.Col + cell.Span < nCols) bd["right"] = v; }
                                else bd[k] = v;
                            }
                    }
                    if (tcPr?.Child("tcBorders") is { } tcb) foreach (var b in tcb.Kids) bd[Norm(b.Name)] = BdrOf(b);
                    if (ShdOf(c, tcPr?.Child("shd")) is { } cs) shd = cs;
                    int mt = cellMar[0], ml = cellMar[1], mb = cellMar[2], mr = cellMar[3];
                    if (tcPr?.Child("tcMar") is { } tm) { var a = Mar(tm, new[] { mt, ml, mb, mr }); mt = a[0]; ml = a[1]; mb = a[2]; mr = a[3]; }
                    outB.Append("<td");
                    if (cell.Span > 1) outB.Append(" colspan=\"").Append(cell.Span).Append('"');
                    if (cell.Rowspan > 1) outB.Append(" rowspan=\"").Append(cell.Rowspan).Append('"');
                    var tcss = new StringBuilder();
                    tcss.Append("padding:").Append(N(mt / 20.0)).Append("pt ").Append(N(mr / 20.0)).Append("pt ").Append(N(mb / 20.0)).Append("pt ").Append(N(ml / 20.0)).Append("pt;");
                    foreach (var side in new[] { "top", "bottom", "left", "right" }) { var b = bd.GetValueOrDefault(side); if (b != null) tcss.Append("border-").Append(side).Append(':').Append(BdrCss(b)).Append(';'); }
                    if (shd != null) tcss.Append("background:").Append(Hex(shd.Value)).Append(';');
                    switch (tcPr?.Child("vAlign")?.V) { case "center": tcss.Append("vertical-align:middle;"); break; case "bottom": tcss.Append("vertical-align:bottom;"); break; }
                    if (tcPr?.Child("noWrap") != null) tcss.Append("white-space:nowrap;");
                    var tcls = Cls(c, tcss);
                    if (tcls != null) outB.Append(" class=\"").Append(tcls).Append("\">"); else outB.Append(" style=\"").Append(tcss).Append("\">");
                    var kids = Flatten(cell.Node.Kids);
                    if (kids.Count == 0) outB.Append("<div class=\"p\">&#8203;</div>");
                    var savedBg = c.Bg;
                    if (shd != null) c.Bg = shd;
                    foreach (var k in kids) Block(c, k, outB, -1, (cPP, cRP));
                    c.Bg = savedBg;
                    outB.Append("</td>");
                }
                outB.Append("</tr>");
            }
            outB.Append("</table>");
        }

        private static string Norm(string n) => n switch { "start" => "left", "end" => "right", _ => n };

        private static int[] Mar(XNode n, int[] baseM)
        {
            var a = (int[])baseM.Clone();
            if (ToInt(n.Child("top")?.Attr("w")) is { } t) a[0] = t;
            if (ToInt((n.Child("left") ?? n.Child("start"))?.Attr("w")) is { } l) a[1] = l;
            if (ToInt(n.Child("bottom")?.Attr("w")) is { } b) a[2] = b;
            if (ToInt((n.Child("right") ?? n.Child("end"))?.Attr("w")) is { } r) a[3] = r;
            return a;
        }

        // ------------------------------------------------------------------ bagian
        private static Sect ParseSect(XNode? sp, Sect? prev)
        {
            var pg = sp?.Child("pgSz"); var m = sp?.Child("pgMar");
            int w = ToInt(pg?.Attr("w")) ?? prev?.W ?? 11906, h = ToInt(pg?.Attr("h")) ?? prev?.H ?? 16838;
            if (pg?.Attr("orient") == "landscape" && w < h) (w, h) = (h, w);
            int Mv(string a, int d) => ToInt(m?.Attr(a)) ?? d;
            var hdr = new Dictionary<string, string>(prev?.Hdr ?? new()); var ftr = new Dictionary<string, string>(prev?.Ftr ?? new());
            if (sp != null)
            {
                foreach (var r in sp.Children("headerReference")) { var id = r.Attr("id"); if (id != null) hdr[r.Attr("type") ?? "default"] = id; }
                foreach (var r in sp.Children("footerReference")) { var id = r.Attr("id"); if (id != null) ftr[r.Attr("type") ?? "default"] = id; }
            }
            int? top = ToInt(m?.Attr("top")), bottom = ToInt(m?.Attr("bottom"));
            var tp = sp?.Child("titlePg");
            return new Sect(w, h, Math.Abs(top ?? prev?.Top ?? 1440), Mv("right", prev?.Right ?? 1440), Math.Abs(bottom ?? prev?.Bottom ?? 1440), Mv("left", prev?.Left ?? 1440),
                Mv("header", prev?.Header ?? 720), Mv("footer", prev?.Footer ?? 720), tp != null && tp.V != "0" && tp.V != "false",
                hdr, ftr, sp?.Child("type")?.V ?? "nextPage", ToInt(sp?.Child("cols")?.Attr("num")) ?? 1,
                top != null ? top < 0 : prev?.TopExact ?? false, bottom != null ? bottom < 0 : prev?.BottomExact ?? false);
        }

        // ------------------------------------------------------------------ CSS & JS halaman (sama dengan Android)
        private const string CSS = @"
html,body{margin:0;padding:0;background:#e9eaee}
#flow{position:absolute;left:-10000px;top:0;visibility:hidden}
#pages{padding:12px 0}
.page{position:relative;isolation:isolate;background:#fff;margin:0 auto 12px;box-shadow:0 1px 4px rgba(0,0,0,.25);overflow:hidden;color:#000}
.content{position:absolute;overflow:visible}
.hdr,.ftr{position:absolute;overflow:visible}
.p{margin:0;white-space:pre-wrap;overflow-wrap:break-word}
table{border-collapse:collapse;border-spacing:0}
td{vertical-align:top;overflow-wrap:break-word}
td>.p:first-child{margin-top:0}
img{display:inline-block;vertical-align:bottom;max-width:none}
a{color:inherit;text-decoration:inherit}
.tab{display:inline-block;white-space:pre}
.lbl{display:inline-block;text-indent:0;white-space:pre}
.ph{display:inline-block;background:#f1f3f6;border:1px dashed #b9bec8;box-sizing:border-box;color:#8a8f99;font:9pt sans-serif;text-align:center}
.notes{font-size:9pt}.notes hr{width:33%;margin:6pt 0 3pt 0;border:0;border-top:0.5pt solid #000}
.note .p{display:inline}
.pb,.sect{height:0}
@media print{html,body{background:#fff}#pages{padding:0}.page{margin:0;box-shadow:none;break-after:page;page-break-after:always}}
";

        private const string JS = @"
function __layout(){
var PT=4/3;
function px(pt){return pt*PT}
var flow=document.getElementById('flow'),pagesEl=document.getElementById('pages');
(function(){var ps=flow.querySelectorAll('.p[data-cs]');for(var i=0;i<ps.length;i++){var p=ps[i],n=p.nextElementSibling;if(n&&n.classList.contains('p')&&n.getAttribute('data-cs')==p.getAttribute('data-cs')){p.style.marginBottom='0';n.style.marginTop='0';}}})();
function solveTabs(root){
 var ps=root.querySelectorAll('.p');
 for(var i=0;i<ps.length;i++){
  var p=ps[i],tabs=p.querySelectorAll('.tab');if(!tabs.length)continue;
  var stops=[];var ds=p.getAttribute('data-tabs');
  if(ds)ds.split(';').forEach(function(s){var a=s.split(':');stops.push({pos:+a[0],al:a[1],ld:a[2]});});
  stops.sort(function(a,b){return a.pos-b.pos});
  var il=+(p.getAttribute('data-il')||0);
  for(var t=0;t<tabs.length;t++){
   var tb=tabs[t];tb.style.width='0px';
   var pr=p.getBoundingClientRect(),cs=getComputedStyle(p);
   var origin=pr.left+parseFloat(cs.borderLeftWidth||0)+parseFloat(cs.paddingLeft||0)-px(il);
   var x=(tb.getBoundingClientRect().left-origin)/PT;
   var st=null;for(var k=0;k<stops.length;k++){if(stops[k].pos>x+0.5){st=stops[k];break;}}
   var pa=tb.getAttribute('data-pa');
   if(pa){var cw=(pr.width-parseFloat(cs.paddingLeft||0)-parseFloat(cs.paddingRight||0))/PT+il;st={pos:pa=='right'?cw:pa=='center'?cw/2:0,al:pa,ld:'none'};}
   if(!st){var d=DEFTAB||36;st={pos:(Math.floor(x/d+1e-6)+1)*d,al:'left',ld:'none'};}
   var w=st.pos-x;
   if(st.al=='right'||st.al=='end'||st.al=='center'||st.al=='decimal'){
    var seg=0;
    var r=document.createRange();r.setStartAfter(tb);
    var nxt=tabs[t+1];if(nxt)r.setEndBefore(nxt);else r.setEnd(p,p.childNodes.length);
    var rects=r.getClientRects();var tr=tb.getBoundingClientRect();
    for(var q=0;q<rects.length;q++){var rc=rects[q];if(rc.top<=tr.bottom+1&&rc.bottom>=tr.top-1)seg+=rc.width;}
    seg/=PT;w-=st.al=='center'?seg/2:seg+0.6;
   }
   if(w<0.5)w=0.5;
   tb.style.width=w+'pt';
   if(st.ld&&st.ld!='none'){var ls=st.ld=='dot'?'dotted':st.ld=='hyphen'?'dashed':'solid';tb.style.borderBottom='1px '+ls+' currentColor';tb.style.height='0.9em';tb.style.verticalAlign='baseline';tb.style.boxSizing='border-box';tb.style.marginBottom='0.2em';}
  }
 }
}
var pages=[];
function hf(page,s,kind,idx,first){
 var map=kind=='hdr'?s.hdr:s.ftr;var id=(first&&s.tp&&map.first)?map.first:(idx%2==0&&EVEN&&map.even?map.even:map['default']);
 if(first&&s.tp&&!map.first)return null;
 if(!id)return null;var tpl=document.getElementById('hf-'+id);if(!tpl)return null;
 var d=document.createElement('div');d.className=kind;d.style.left=px(s.l)+'px';d.style.width=px(s.w-s.l-s.r)+'px';
 if(kind=='hdr')d.style.top=px(s.hd)+'px';else d.style.bottom=px(s.ft)+'px';
 d.innerHTML=tpl.innerHTML;page.appendChild(d);solveTabs(d);return d;
}
function flowH(d){var top=d.getBoundingClientRect().top,m=0;
 for(var i=0;i<d.children.length;i++){var k=d.children[i];if(k.classList.contains('anc'))continue;
  var b=k.getBoundingClientRect().bottom+(parseFloat(getComputedStyle(k).marginBottom)||0)-top;if(b>m)m=b;}
 return m/PT;}
function newPage(si,firstOfSect){
 var s=SECTS[si];var pg=document.createElement('div');pg.className='page';
 pg.style.width=px(s.w)+'px';pg.style.height=px(s.h)+'px';
 var c=document.createElement('div');c.className='content';
 pg.appendChild(c);pagesEl.appendChild(pg);
 var n=pages.length+1,t=s.t,b=s.b;
 try{var hd=hf(pg,s,'hdr',n,firstOfSect);if(hd&&!s.tx)t=Math.max(t,s.hd+flowH(hd));
  var fd=hf(pg,s,'ftr',n,firstOfSect);if(fd&&!s.bx)b=Math.max(b,s.ft+fd.getBoundingClientRect().height/PT);}catch(e){}
 var body=s.h-s.t-s.b,min=body*0.5,extra=(t-s.t)+(b-s.b);
 if(extra>0&&s.h-t-b<min){var k=Math.max(0,(body-min)/extra);t=s.t+(t-s.t)*k;b=s.b+(b-s.b)*k;}
 c.style.left=px(s.l)+'px';c.style.top=px(t)+'px';c.style.width=px(s.w-s.l-s.r)+'px';c.style.height=px(s.h-t-b)+'px';
 var rec={el:pg,c:c,s:si,first:firstOfSect,idx:pages.length};pages.push(rec);return rec;
}
function overflow(rec){var c=rec.c;var last=c.lastElementChild;if(!last)return false;
 var cb=c.getBoundingClientRect().bottom;var lb=last.getBoundingClientRect().bottom;return lb>cb+1;}
var blocks=[];for(var i=0;i<flow.children.length;i++)blocks.push(flow.children[i]);
var cur=null,curS=-1;
var CH=60;
function park(t,from){var m=[],fr=document.createDocumentFragment();while(t.rows.length>from){var r=t.rows[from];m.push(r);fr.appendChild(r);}return m;}
function feed(t){var m=t.__more;if(!m||!m.length)return null;var tb=t.tBodies[t.tBodies.length-1]||t;var fr=document.createDocumentFragment();var take=m.splice(0,CH);for(var i=0;i<take.length;i++)fr.appendChild(take[i]);tb.appendChild(fr);return take;}
function flushMore(t){if(t&&t.__more)while(feed(t)){}}
function splitTable(tbl,rec){
 var rows=tbl.rows,n=rows.length,pend=tbl.__more&&tbl.__more.length?tbl.__more:null;
 if(n<2&&!pend)return null;
 var head=0;while(head<n-1&&rows[head].classList.contains('th'))head++;
 var cb=rec.c.getBoundingClientRect().bottom+1,k=n;
 for(var i=head;i<n;i++){if(rows[i].getBoundingClientRect().bottom>cb){k=i;break;}}
 if(k==n&&n>head+1)k=n-1;
 if(k<=head){if(rec.c.children.length>1)return null;k=head+1;}
 if(k>=n&&!pend)return null;
 var rest=tbl.cloneNode(false);
 for(var c=tbl.firstElementChild;c;c=c.nextElementSibling)if(c.tagName=='COLGROUP'){rest.appendChild(c.cloneNode(true));break;}
 var tb=document.createElement('tbody');rest.appendChild(tb);
 for(var h=0;h<head;h++)tb.appendChild(rows[h].cloneNode(true));
 var mv=[],fr=document.createDocumentFragment();for(var j=k;j<n;j++)mv.push(rows[j]);
 var now=Math.min(mv.length,CH);for(var q=0;q<now;q++)fr.appendChild(mv[q]);
 var more=[],dz=document.createDocumentFragment();for(var z=now;z<mv.length;z++){more.push(mv[z]);dz.appendChild(mv[z]);}
 tb.appendChild(fr);
 tbl.__more=null;if(more.length||pend)rest.__more=pend?more.concat(pend):more;
 var g=0;while(tbl.rows.length>head+1&&overflow(rec)&&g++<40)tb.insertBefore(tbl.rows[tbl.rows.length-1],tb.children[head]||null);
 return rest;
}
for(var b=0;b<blocks.length;b++){
 var el=blocks[b];var si=+(el.getAttribute('data-s')||0);
 if(el.classList.contains('sect')){if(cur==null||si!=curS){var cont=cur&&SECTS[si].type=='continuous'&&SECTS[si].w==SECTS[curS].w;if(!cont){cur=newPage(si,true);}curS=si;}continue;}
 if(cur==null){cur=newPage(si,true);curS=si;}
 if(el.classList.contains('pb')){cur=newPage(curS,false);continue;}
 try{
 if(el.tagName=='TABLE'&&el.rows.length>CH+20){var hd0=0;while(hd0<el.rows.length-1&&el.rows[hd0].classList.contains('th'))hd0++;el.__more=park(el,hd0+CH);}
 cur.c.appendChild(el);
 if(el.classList.contains('p'))solveTabs({querySelectorAll:function(){return[el].concat(Array.prototype.slice.call(el.querySelectorAll('.p')))}});else solveTabs(el);
 var guard=0;
 while(guard++<20000){
  if(!overflow(cur)){var fed=el.tagName=='TABLE'?feed(el):null;if(fed){for(var f=0;f<fed.length;f++)solveTabs(fed[f]);continue;}break;}
  if(cur.c.children.length==1){
   if(el.tagName=='TABLE'){var rest=splitTable(el,cur);if(rest){cur=newPage(curS,false);cur.c.appendChild(rest);el=rest;continue;}}
   flushMore(el);
   break;
  }
  if(el.tagName=='TABLE'){var rest2=splitTable(el,cur);if(rest2){cur=newPage(curS,false);cur.c.appendChild(rest2);el=rest2;continue;}}
  var mv=[el];var prev=el.previousElementSibling;
  while(prev&&prev.getAttribute('data-kn')=='1'&&mv.length<4&&cur.c.children.length>mv.length+1){mv.unshift(prev);prev=prev.previousElementSibling;}
  cur=newPage(curS,false);mv.forEach(function(m){cur.c.appendChild(m)});
 }
 }catch(err){try{flushMore(el);if(el&&el.parentNode!==cur.c)cur.c.appendChild(el);}catch(e2){}if(window.console)console.log('layout: '+err);}
}
if(!pages.length)newPage(0,true);
var N=pages.length;
pages.forEach(function(rec,i){try{var s=SECTS[rec.s];
 var pg=rec.el.querySelectorAll('.pg');for(var k=0;k<pg.length;k++)pg[k].textContent=String(i+1);
 var np=rec.el.querySelectorAll('.np');for(var k2=0;k2<np.length;k2++)np[k2].textContent=String(N);
 var an=rec.el.querySelectorAll('.anc.abs');
 for(var a=0;a<an.length;a++){var e=an[a];var hr=e.getAttribute('data-hr'),vr=e.getAttribute('data-vr');
  var w=+e.getAttribute('data-w'),h=+e.getAttribute('data-h');
  var pr=rec.el.getBoundingClientRect();var par=e.parentElement;while(par&&!(par.classList&&par.classList.contains('p'))&&par!==rec.el)par=par.parentElement;
  var ppr=par?par.getBoundingClientRect():pr;
  var refL,refW;if(hr=='page'){refL=0;refW=s.w;}else if(hr=='character'){refL=(e.getBoundingClientRect().left-pr.left)/PT;refW=0;}else{refL=s.l;refW=s.w-s.l-s.r;}
  if(hr=='leftMargin'){refL=0;refW=s.l;}if(hr=='rightMargin'){refL=s.w-s.r;refW=s.r;}
  var x;var ha=e.getAttribute('data-ha');if(ha){x=ha=='center'?refL+(refW-w)/2:(ha=='right'||ha=='outside')?refL+refW-w:refL;}else x=refL+(+(e.getAttribute('data-hx')||0));
  var refT;if(vr=='page')refT=0;else if(vr=='margin'||vr=='topMargin')refT=s.t;else refT=(ppr.top-pr.top)/PT;
  var y;var va=e.getAttribute('data-va');if(va){var refH=vr=='page'?s.h:vr=='margin'?(s.h-s.t-s.b):0;y=va=='center'?refT+(refH-h)/2:va=='bottom'?refT+refH-h:refT;}else y=refT+(+(e.getAttribute('data-vy')||0));
  rec.el.appendChild(e);e.style.left=px(x)+'px';e.style.top=px(y)+'px';
 }
 }catch(err){if(window.console)console.log('page: '+err);}
});
flow.parentNode.removeChild(flow);
document.body.setAttribute('data-ready','1');
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('ready:'+N);
}
(function(){var done=false;function go(){if(done)return;done=true;
 try{__layout();}catch(err){
  var f=document.getElementById('flow');if(f){f.style.cssText='position:static;visibility:visible;background:#fff;padding:24px;margin:8px auto;max-width:900px';}
  document.body.setAttribute('data-ready','1');if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('ready:0');}}
 if(document.fonts&&document.fonts.ready){document.fonts.ready.then(go);setTimeout(go,2500);}else go();})();
";
    }

    /// <summary>CSS tampilan (layar saja) agar kanvas dokumen menyatu dengan tema aplikasi.</summary>
    internal static class OfficeViewCss
    {
        public const string Docx = "html,body{overflow-anchor:none}@media screen{html,body{background:#F1F0EC}.page{box-shadow:0 1px 3px rgba(0,0,0,.12),0 6px 24px rgba(0,0,0,.06);border-radius:2px}::selection{background:#c9d6ff}}";
    }

    /// <summary>Jalur SVG bentuk preset DrawingML (port PptxHtml.prstPath).</summary>
    internal static class ShapePaths
    {
        private static string F(double v) => OxUtil.N(v);

        public static string Preset(string p, double w, double h, Dictionary<string, double> adj)
        {
            double ss = Math.Min(w, h);
            double A(string n, double d) => (adj.TryGetValue(n, out var v) ? v : d) / 100000.0;
            string Poly(params double[] pts)
            {
                var sb = new StringBuilder("M" + F(pts[0]) + " " + F(pts[1]));
                for (int i = 2; i + 1 < pts.Length; i += 2) sb.Append('L').Append(F(pts[i])).Append(' ').Append(F(pts[i + 1]));
                return sb.Append('Z').ToString();
            }
            string Ell() => "M0 " + F(h / 2) + "A" + F(w / 2) + " " + F(h / 2) + " 0 1 0 " + F(w) + " " + F(h / 2) + "A" + F(w / 2) + " " + F(h / 2) + " 0 1 0 0 " + F(h / 2) + "Z";
            switch (p)
            {
                case "ellipse": case "flowChartConnector": case "wedgeEllipseCallout": case "cloudCallout": case "cloud": return Ell();
                case "roundRect": case "flowChartAlternateProcess":
                    {
                        double r = ss * A("adj", 16667);
                        return "M" + F(r) + " 0H" + F(w - r) + "Q" + F(w) + " 0 " + F(w) + " " + F(r) + "V" + F(h - r) + "Q" + F(w) + " " + F(h) + " " + F(w - r) + " " + F(h) + "H" + F(r) + "Q0 " + F(h) + " 0 " + F(h - r) + "V" + F(r) + "Q0 0 " + F(r) + " 0Z";
                    }
                case "round2SameRect": { double r = ss * A("adj1", 16667); return "M" + F(r) + " 0H" + F(w - r) + "Q" + F(w) + " 0 " + F(w) + " " + F(r) + "V" + F(h) + "H0V" + F(r) + "Q0 0 " + F(r) + " 0Z"; }
                case "snip1Rect": { double r = ss * A("adj", 16667); return Poly(0, 0, w - r, 0, w, r, w, h, 0, h); }
                case "triangle": case "flowChartExtract": return Poly(w * A("adj", 50000), 0, w, h, 0, h);
                case "rtTriangle": return Poly(0, 0, w, h, 0, h);
                case "diamond": case "flowChartDecision": return Poly(w / 2, 0, w, h / 2, w / 2, h, 0, h / 2);
                case "parallelogram": case "flowChartInputOutput": { double o = ss * A("adj", 25000); return Poly(o, 0, w, 0, w - o, h, 0, h); }
                case "trapezoid": { double o = ss * A("adj", 25000); return Poly(o, 0, w - o, 0, w, h, 0, h); }
                case "pentagon": case "homePlate": { double o = ss * A("adj", 50000); return Poly(0, 0, w - o, 0, w, h / 2, w - o, h, 0, h); }
                case "chevron": { double o = ss * A("adj", 50000); return Poly(0, 0, w - o, 0, w, h / 2, w - o, h, 0, h, o, h / 2); }
                case "hexagon": { double o = ss * A("adj", 25000); return Poly(o, 0, w - o, 0, w, h / 2, w - o, h, o, h, 0, h / 2); }
                case "octagon": { double o = ss * A("adj", 29289); return Poly(o, 0, w - o, 0, w, o, w, h - o, w - o, h, o, h, 0, h - o, 0, o); }
                case "rightArrow": { double t = h * A("adj1", 50000), hd = ss * A("adj2", 50000); return Poly(0, (h - t) / 2, w - hd, (h - t) / 2, w - hd, 0, w, h / 2, w - hd, h, w - hd, (h + t) / 2, 0, (h + t) / 2); }
                case "leftArrow": { double t = h * A("adj1", 50000), hd = ss * A("adj2", 50000); return Poly(w, (h - t) / 2, hd, (h - t) / 2, hd, 0, 0, h / 2, hd, h, hd, (h + t) / 2, w, (h + t) / 2); }
                case "upArrow": { double t = w * A("adj1", 50000), hd = ss * A("adj2", 50000); return Poly((w - t) / 2, h, (w - t) / 2, hd, 0, hd, w / 2, 0, w, hd, (w + t) / 2, hd, (w + t) / 2, h); }
                case "downArrow": { double t = w * A("adj1", 50000), hd = ss * A("adj2", 50000); return Poly((w - t) / 2, 0, (w + t) / 2, 0, (w + t) / 2, h - hd, w, h - hd, w / 2, h, 0, h - hd, (w - t) / 2, h - hd); }
                case "leftRightArrow": { double t = h * A("adj1", 50000), hd = ss * A("adj2", 50000); return Poly(0, h / 2, hd, 0, hd, (h - t) / 2, w - hd, (h - t) / 2, w - hd, 0, w, h / 2, w - hd, h, w - hd, (h + t) / 2, hd, (h + t) / 2, hd, h); }
                case "plus": case "mathPlus": { double t = ss * A("adj", 25000); return Poly(t, 0, w - t, 0, w - t, t, w, t, w, h - t, w - t, h - t, w - t, h, t, h, t, h - t, 0, h - t, 0, t, t, t); }
                case "line": case "straightConnector1": return "M0 0L" + F(w) + " " + F(h);
                case "bentConnector2": return "M0 0H" + F(w) + "V" + F(h);
                case "bentConnector3": { double x = w * A("adj1", 50000); return "M0 0H" + F(x) + "V" + F(h) + "H" + F(w); }
                case "curvedConnector3": { double x = w * A("adj1", 50000); return "M0 0C" + F(x) + " 0 " + F(x) + " " + F(h) + " " + F(w) + " " + F(h); }
                case "flowChartTerminator": { double r = h / 2; return "M" + F(r) + " 0H" + F(w - r) + "A" + F(r) + " " + F(r) + " 0 0 1 " + F(w - r) + " " + F(h) + "H" + F(r) + "A" + F(r) + " " + F(r) + " 0 0 1 " + F(r) + " 0Z"; }
                case "can": { double ry = ss * A("adj", 25000) / 2; return "M0 " + F(ry) + "A" + F(w / 2) + " " + F(ry) + " 0 0 1 " + F(w) + " " + F(ry) + "V" + F(h - ry) + "A" + F(w / 2) + " " + F(ry) + " 0 0 1 0 " + F(h - ry) + "Z"; }
                case "wedgeRectCallout": case "wedgeRoundRectCallout": return Poly(0, 0, w, 0, w, h * 0.8, w * 0.4, h * 0.8, w * 0.2, h, w * 0.25, h * 0.8, 0, h * 0.8);
                case "heart":
                    return "M" + F(w / 2) + " " + F(h * 0.25) + "C" + F(w * 0.5) + " 0 0 0 0 " + F(h * 0.3) + "C0 " + F(h * 0.6) + " " + F(w * 0.4) + " " + F(h * 0.8) + " " + F(w / 2) + " " + F(h) +
                           "C" + F(w * 0.6) + " " + F(h * 0.8) + " " + F(w) + " " + F(h * 0.6) + " " + F(w) + " " + F(h * 0.3) + "C" + F(w) + " 0 " + F(w * 0.5) + " 0 " + F(w / 2) + " " + F(h * 0.25) + "Z";
                default:
                    if (p.StartsWith("star", StringComparison.Ordinal))
                    {
                        int n = OxUtil.ToInt(p[4..]) ?? 5;
                        double inner = n == 5 ? 0.382 : n == 4 ? 0.38 : 0.6;
                        var sb = new StringBuilder();
                        for (int k = 0; k < n * 2; k++)
                        {
                            double ang = -Math.PI / 2 + k * Math.PI / n, r = k % 2 == 0 ? 1.0 : inner;
                            sb.Append(k == 0 ? "M" : "L").Append(F(w / 2 + Math.Cos(ang) * w / 2 * r)).Append(' ').Append(F(h / 2 + Math.Sin(ang) * h / 2 * r));
                        }
                        return sb.Append('Z').ToString();
                    }
                    return "M0 0H" + F(w) + "V" + F(h) + "H0Z";
            }
        }
    }
}
