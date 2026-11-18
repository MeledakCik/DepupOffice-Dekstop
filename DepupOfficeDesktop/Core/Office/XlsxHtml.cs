using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static DepupOfficeDesktop.Core.Office.OxUtil;

namespace DepupOfficeDesktop.Core.Office
{
    /// <summary>
    /// XLSX → HTML untuk PEMBACA (port XlsxHtml.kt Android v4.1): kepala kolom A, B, C &amp; nomor baris, lebar kolom &amp;
    /// tinggi baris asli, sel gabungan, font/warna/isian/garis/perataan/bungkus teks, format angka (ribuan, desimal,
    /// persen, mata uang, tanggal &amp; jam, kustom), teks meluber, gambar &amp; gambar-dalam-sel, Tabel Excel, dan tab lembar.
    /// </summary>
    public static class XlsxHtml
    {
        private const int MAX_ROWS = 3000;
        private const int MAX_COLS = 120;
        private const int CELL_CHARS = 1024;
        private static string Cap(string t) => t.Length > CELL_CHARS ? t[..CELL_CHARS] + "…" : t;

        private sealed record XFont(string? Name, double Size, bool B, bool I, bool U, bool Strike, int? Color);
        private sealed record Side(string Style, int? Color);
        private sealed record XBorder(Side? L, Side? R, Side? T, Side? B);
        private sealed record Xf(int NumFmt, int Font, int Fill, int Border, string? H, string? V, bool Wrap, int Indent, int Rot, bool Shrink);

        private sealed class Ctx
        {
            public readonly OPackage Pkg;
            public readonly List<int> Theme = new();
            public readonly List<XFont> Fonts = new(); public readonly List<int?> Fills = new(); public readonly List<XBorder> Borders = new(); public readonly List<Xf> Xfs = new();
            public readonly Dictionary<int, string> NumFmts = new();
            public readonly List<string> Shared = new(); public readonly List<string?> SharedRich = new();
            public bool Date1904;
            public readonly DocRes Res;
            public readonly Dictionary<string, string> CellCls = new();
            public readonly StringBuilder ClsCss = new();
            public int[] Indexed = (int[])DEFAULT_INDEXED.Clone();
            public readonly Dictionary<string, string> WpsImg = new();
            public readonly Dictionary<int, int> VmRv = new();
            public readonly Dictionary<int, string> RvImg = new();
            public Ctx(OPackage pkg) { Pkg = pkg; Res = new DocRes(pkg); }

            public string? Cls(string css)
            {
                if (CellCls.TryGetValue(css, out var v)) return v;
                if (CellCls.Count >= 6000) return null;
                var n = "k" + CellCls.Count;
                CellCls[css] = n;
                ClsCss.Append("table.grid td.").Append(n).Append('{').Append(css).Append('}');
                return n;
            }
        }

        public static RenderedDoc Render(string path, string title)
        {
            var pkg = new OPackage(path);
            try { return Render(pkg, title); } catch { pkg.Dispose(); throw; }
        }

        public static RenderedDoc Render(OPackage pkg, string title)
        {
            var wbPath = pkg.MainPart() ?? "xl/workbook.xml";
            var c = new Ctx(pkg);
            var wbRels = pkg.Rels(wbPath);
            var wb = pkg.Xml(wbPath) ?? throw new ArgumentException("workbook.xml tidak ada");
            c.Date1904 = wb.First("workbookPr")?.Attr("date1904") is "1" or "true";
            OPackage.Rel? R(string suffix) => wbRels.Values.FirstOrDefault(r => r.Type.EndsWith(suffix, StringComparison.Ordinal));
            if (R("/theme") is { } th) try { LoadTheme(c, th.Target); } catch { }
            if (R("/styles") is { } st) try { LoadStyles(c, st.Target); } catch { }
            if (R("/sharedStrings") is { } ss) try { LoadShared(c, ss.Target); } catch { }
            try { LoadCellImages(c, wbRels); } catch { }

            var sheets = wb.All("sheet").Select(s =>
            {
                var rid = s.Attr("id") ?? s.Attr("r:id");
                if (rid == null || !wbRels.TryGetValue(rid, out var rel)) return null;
                return new { Name = s.Attr("name") ?? "Sheet", Path = rel.Target, State = s.Attr("state") ?? "visible" };
            }).Where(x => x != null && x.State == "visible").Select(x => x!).ToList();
            int active = ToInt(wb.First("workbookView")?.Attr("activeTab")) ?? 0;
            active = sheets.Count == 0 ? 0 : Math.Clamp(active, 0, sheets.Count - 1);

            var body = new StringBuilder();
            var tabs = new StringBuilder();
            for (int idx = 0; idx < sheets.Count; idx++)
            {
                var sh = sheets[idx];
                body.Append("<div class=\"sheet-wrap\" id=\"s").Append(idx).Append("\" style=\"display:").Append(idx == active ? "block" : "none").Append("\">");
                try
                {
                    if (pkg.Has(sh.Path) && pkg.Xml(sh.Path)?.First("chartsheet") == null) Sheet(c, sh.Path, body);
                    else body.Append("<div class=\"empty\">Lembar grafik tidak dapat ditampilkan.</div>");
                }
                catch (Exception e) when (e is not OutOfMemoryException) { body.Append("<div class=\"empty\">Lembar ini tidak dapat dibaca.</div>"); }
                body.Append("</div>");
                tabs.Append("<button class=\"tab").Append(idx == active ? " on" : "").Append("\" onclick=\"show(").Append(idx).Append(")\">").Append(Esc(sh.Name)).Append("</button>");
            }
            var defFont = c.Fonts.FirstOrDefault();
            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(Esc(title)).Append("</title><style>").Append(CSS)
                .Append("table.grid{font-family:").Append(FontStack(defFont?.Name)).Append(";font-size:").Append(N(defFont?.Size ?? 11.0)).Append("pt}")
                .Append(c.ClsCss)
                .Append("</style></head><body><div id=\"main\">").Append(body).Append("</div><div id=\"tabs\">").Append(tabs).Append("</div><script>").Append(JS).Append("</script></body></html>");
            c.Res.Finish();
            return new RenderedDoc(html.ToString(), c.Res, title, "xlsx");
        }

        private static readonly Regex DISPIMG = new("DISPIMG\\(\\s*\"([^\"]+)\"", RegexOptions.Compiled);

        private static void LoadCellImages(Ctx c, Dictionary<string, OPackage.Rel> wbRels)
        {
            string? Part(string suffix, string def) => wbRels.Values.FirstOrDefault(r => r.Type.EndsWith(suffix, StringComparison.Ordinal))?.Target ?? (c.Pkg.Has(def) ? def : null);
            var ci = wbRels.Values.FirstOrDefault(r => r.Type.Contains("cellimage", StringComparison.OrdinalIgnoreCase))?.Target ?? (c.Pkg.Has("xl/cellimages.xml") ? "xl/cellimages.xml" : null);
            if (ci != null && c.Pkg.Xml(ci) is { } x)
            {
                var rels = c.Pkg.Rels(ci);
                foreach (var pic in x.All("pic"))
                {
                    var name = pic.First("cNvPr")?.Attr("name"); if (name == null) continue;
                    var emb = pic.First("blip")?.Attr("embed");
                    if (emb != null && rels.TryGetValue(emb, out var rel) && !rel.External) c.WpsImg[name] = rel.Target;
                }
            }
            var md = Part("/sheetMetadata", "xl/metadata.xml"); if (md == null) return;
            var rvPath = Part("/rdRichValue", "xl/richData/rdrichvalue.xml"); if (rvPath == null) return;
            var relPath = Part("/richValueRel", "xl/richData/richValueRel.xml"); if (relPath == null) return;
            var mdx = c.Pkg.Xml(md); if (mdx == null) return;
            var types = mdx.First("metadataTypes")?.Children("metadataType").Select(t => t.Attr("name")).ToList(); if (types == null) return;
            var fut = mdx.All("futureMetadata").FirstOrDefault(f => f.Attr("name") == "XLRICHVALUE")?.Children("bk").Select(b => ToInt(b.First("rvb")?.Attr("i")) ?? -1).ToList();
            if (fut == null) return;
            var vmd = mdx.First("valueMetadata")?.Children("bk") ?? new();
            for (int i = 0; i < vmd.Count; i++)
            {
                var rc = vmd[i].Child("rc"); if (rc == null) continue;
                var t = ToInt(rc.Attr("t")); if (t == null) continue;
                if (t.Value - 1 < 0 || t.Value - 1 >= types.Count || types[t.Value - 1] != "XLRICHVALUE") continue;
                int fv = ToInt(rc.Attr("v")) ?? -1;
                if (fv < 0 || fv >= fut.Count) continue;
                if (fut[fv] >= 0) c.VmRv[i + 1] = fut[fv];
            }
            var structPath = Part("/rdRichValueStructure", "xl/richData/rdrichvaluestructure.xml");
            var structs = structPath != null ? c.Pkg.Xml(structPath)?.All("s") ?? new() : new List<XNode>();
            var relx = c.Pkg.Xml(relPath); if (relx == null) return;
            var relRels = c.Pkg.Rels(relPath);
            var relIds = relx.All("rel").Select(r => r.Attr("r:id") ?? r.Attr("id")).ToList();
            var rvs = c.Pkg.Xml(rvPath)?.All("rv") ?? new();
            for (int k = 0; k < rvs.Count; k++)
            {
                var rv = rvs[k];
                int si = ToInt(rv.Attr("s")) ?? -1;
                var stc = si >= 0 && si < structs.Count ? structs[si] : null;
                if (stc != null && stc.Attr("t") != "_localImage") continue;
                int keyIdx = 0;
                if (stc != null) { var ks = stc.Children("k"); int ix = ks.FindIndex(kk => kk.Attr("n") == "_rvRel:LocalImageIdentifier"); if (ix >= 0) keyIdx = ix; }
                var vs = rv.Children("v");
                if (keyIdx >= vs.Count) continue;
                var relIdx = ToInt(vs[keyIdx].Text.Trim()); if (relIdx == null || relIdx < 0 || relIdx >= relIds.Count) continue;
                var rid = relIds[relIdx.Value];
                if (rid != null && relRels.TryGetValue(rid, out var rel2) && !rel2.External) c.RvImg[k] = rel2.Target;
            }
        }

        // ------------------------------------------------------------------ tema & gaya
        private static void LoadTheme(Ctx c, string path)
        {
            var t = c.Pkg.Xml(path); var cs = t?.First("clrScheme"); if (cs == null) return;
            string[] order = { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
            var m = new Dictionary<string, int>();
            foreach (var k in cs.Kids) { var v = ParseHex(k.Child("srgbClr")?.V ?? k.Child("sysClr")?.Attr("lastClr")); if (v != null) m[k.Name] = v.Value; }
            foreach (var o in order) c.Theme.Add(m.TryGetValue(o, out var v) ? v : 0);
        }

        private static int? Color(Ctx c, XNode? n)
        {
            if (n == null) return null;
            if (n.Attr("auto") is "1" or "true") return null;
            int? rgb = ParseHex(n.Attr("rgb"));
            if (rgb == null && ToInt(n.Attr("theme")) is { } th && th >= 0 && th < c.Theme.Count) rgb = c.Theme[th];
            if (rgb == null && ToInt(n.Attr("indexed")) is { } ix && ix != 64 && ix != 65 && ix >= 0 && ix < c.Indexed.Length) rgb = c.Indexed[ix];
            var t = ToDouble(n.Attr("tint"));
            if (rgb != null && t != null && t != 0) rgb = Tint(rgb.Value, (float)t.Value);
            return rgb;
        }

        private static bool OnOff(XNode? n) { if (n == null) return false; var v = n.V; return v == null || (v != "0" && v != "false"); }

        private static void LoadStyles(Ctx c, string path)
        {
            var x = c.Pkg.Xml(path); if (x == null) return;
            var ic = x.First("indexedColors")?.Children("rgbColor").Select(r => ParseHex(r.Attr("rgb"))).Where(v => v != null).Select(v => v!.Value).ToArray();
            if (ic is { Length: > 0 }) c.Indexed = ic.Concat(DEFAULT_INDEXED.Skip(ic.Length)).ToArray();
            foreach (var n in x.First("numFmts")?.Children("numFmt") ?? new()) if (ToInt(n.Attr("numFmtId")) is { } id) c.NumFmts[id] = n.Attr("formatCode") ?? "General";
            foreach (var f in x.First("fonts")?.Children("font") ?? new())
                c.Fonts.Add(new XFont(f.Child("name")?.V, ToDouble(f.Child("sz")?.V) ?? 11.0, OnOff(f.Child("b")), OnOff(f.Child("i")),
                    f.Child("u") is { } u && u.V != "none", OnOff(f.Child("strike")), Color(c, f.Child("color"))));
            foreach (var f in x.First("fills")?.Children("fill") ?? new())
            {
                var pf = f.Child("patternFill"); var gf = f.Child("gradientFill");
                int? fill = null;
                if (pf != null && pf.Attr("patternType") is { } pt && pt != "none")
                    fill = pt == "solid" ? Color(c, pf.Child("fgColor")) ?? Color(c, pf.Child("bgColor"))
                        : Color(c, pf.Child("fgColor")) is { } fg ? Mix(Color(c, pf.Child("bgColor")) ?? 0xFFFFFF, fg, 0.35f) : null;
                else if (gf != null) fill = Color(c, gf.First("stop")?.Child("color"));
                c.Fills.Add(fill);
            }
            foreach (var b in x.First("borders")?.Children("border") ?? new())
            {
                Side? S(XNode? n) { var st = n?.Attr("style"); if (st == null || st == "none") return null; return new Side(st, Color(c, n!.Child("color"))); }
                c.Borders.Add(new XBorder(S(b.Child("left") ?? b.Child("start")), S(b.Child("right") ?? b.Child("end")), S(b.Child("top")), S(b.Child("bottom"))));
            }
            foreach (var f in x.First("cellXfs")?.Children("xf") ?? new())
            {
                var a = f.Child("alignment");
                c.Xfs.Add(new Xf(ToInt(f.Attr("numFmtId")) ?? 0, ToInt(f.Attr("fontId")) ?? 0, ToInt(f.Attr("fillId")) ?? 0, ToInt(f.Attr("borderId")) ?? 0,
                    a?.Attr("horizontal"), a?.Attr("vertical"), a?.Attr("wrapText") is "1" or "true", ToInt(a?.Attr("indent")) ?? 0, ToInt(a?.Attr("textRotation")) ?? 0, a?.Attr("shrinkToFit") == "1"));
            }
        }

        private static void LoadShared(Ctx c, string path)
        {
            var x = c.Pkg.Xml(path); if (x == null) return;
            var sst = x.Child("sst") ?? x;
            foreach (var si in sst.Children("si"))
            {
                var runs = si.Children("r");
                if (runs.Count == 0)
                {
                    c.Shared.Add(Cap(si.Child("t")?.Text ?? string.Concat(si.All("t").Select(t => t.Text))));
                    c.SharedRich.Add(null);
                    continue;
                }
                var plain = new StringBuilder(); var rich = new StringBuilder();
                foreach (var r in runs)
                {
                    var t = r.Child("t")?.Text ?? "";
                    plain.Append(t);
                    var rp = r.Child("rPr");
                    if (rp == null) { rich.Append(Esc(t)); continue; }
                    rich.Append("<span style=\"");
                    if (rp.Child("rFont")?.V is { } rf) rich.Append("font-family:").Append(FontStack(rf)).Append(';');
                    if (ToDouble(rp.Child("sz")?.V) is { } sz) rich.Append("font-size:").Append(N(sz)).Append("pt;");
                    if (OnOff(rp.Child("b"))) rich.Append("font-weight:bold;");
                    if (OnOff(rp.Child("i"))) rich.Append("font-style:italic;");
                    if (rp.Child("u") != null) rich.Append("text-decoration:underline;");
                    if (OnOff(rp.Child("strike"))) rich.Append("text-decoration:line-through;");
                    if (Color(c, rp.Child("color")) is { } col) rich.Append("color:").Append(Hex(col)).Append(';');
                    switch (rp.Child("vertAlign")?.V) { case "superscript": rich.Append("vertical-align:super;font-size:70%;"); break; case "subscript": rich.Append("vertical-align:sub;font-size:70%;"); break; }
                    rich.Append("\">").Append(Esc(t)).Append("</span>");
                }
                if (plain.Length > CELL_CHARS) { c.Shared.Add(Cap(plain.ToString())); c.SharedRich.Add(null); }
                else { c.Shared.Add(plain.ToString()); c.SharedRich.Add(rich.ToString()); }
            }
        }

        // ------------------------------------------------------------------ lembar
        private static int ColIndex(string reference)
        {
            int n = 0;
            foreach (var ch in reference)
            {
                if (ch >= 'A' && ch <= 'Z') n = n * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') n = n * 26 + (ch - 'a' + 1);
                else if (ch == '$') continue;
                else break;
            }
            return n - 1;
        }
        private static int RowIndex(string reference) => (ToInt(new string(reference.SkipWhile(ch => char.IsLetter(ch) || ch == '$').ToArray())) ?? 1) - 1;
        public static string ColName(int i) { int n = i + 1; var sb = new StringBuilder(); while (n > 0) { int m = (n - 1) % 26; sb.Insert(0, (char)('A' + m)); n = (n - 1) / 26; } return sb.ToString(); }

        private sealed record Cell(string V, string? Rich, int Xf, string Type, double? Num, string? Img = null);
        private static long Key(int r, int c) => ((long)r << 20) | (uint)c;

        private static void Sheet(Ctx c, string path, StringBuilder outB)
        {
            var x = c.Pkg.Xml(path); if (x == null) return;
            var ws = x.Child("worksheet") ?? x;
            var rels = c.Pkg.Rels(path);
            var fmtPr = ws.Child("sheetFormatPr");
            const double maxDigit = 7.0;
            double defColW = ToDouble(fmtPr?.Attr("defaultColWidth")) ?? ((ToDouble(fmtPr?.Attr("baseColWidth")) ?? 8.0) + 0.71);
            double defRowH = ToDouble(fmtPr?.Attr("defaultRowHeight")) ?? 15.0;
            bool showGrid = ws.First("sheetView")?.Attr("showGridLines") is not ("0" or "false");
            var colW = new Dictionary<int, double>(); var colHidden = new HashSet<int>(); var colStyle = new Dictionary<int, int>();
            foreach (var col in ws.Child("cols")?.Children("col") ?? new())
            {
                int mn = (ToInt(col.Attr("min")) ?? 1) - 1, mx = Math.Min((ToInt(col.Attr("max")) ?? 1) - 1, MAX_COLS + 50);
                for (int i = Math.Max(0, mn); i <= mx; i++)
                {
                    if (ToDouble(col.Attr("width")) is { } w) colW[i] = w;
                    if (col.Attr("hidden") is "1" or "true") colHidden.Add(i);
                    if (ToInt(col.Attr("style")) is { } s) colStyle[i] = s;
                }
            }
            var cells = new Dictionary<long, Cell>();
            var rowH = new Dictionary<int, double>(); var rowHidden = new HashSet<int>();
            int maxR = -1, maxC = -1;
            foreach (var row in ws.Child("sheetData")?.Children("row") ?? new())
            {
                int r = (ToInt(row.Attr("r")) ?? (maxR + 2)) - 1;
                if (r < 0 || r >= MAX_ROWS) continue;
                if (ToDouble(row.Attr("ht")) is { } ht) rowH[r] = ht;
                if (row.Attr("hidden") is "1" or "true") rowHidden.Add(r);
                int cIdx = -1;
                foreach (var cel in row.Children("c"))
                {
                    cIdx = cel.Attr("r") is { } rf ? ColIndex(rf) : cIdx + 1;
                    if (cIdx < 0 || cIdx > MAX_COLS) continue;
                    var t = cel.Attr("t") ?? "n";
                    int s = ToInt(cel.Attr("s")) ?? 0;
                    var vNode = cel.Child("v");
                    string? rich = null; double? num = null;
                    string text;
                    switch (t)
                    {
                        case "s":
                            {
                                int i = ToInt(vNode?.Text.Trim()) ?? -1;
                                rich = i >= 0 && i < c.SharedRich.Count ? c.SharedRich[i] : null;
                                text = i >= 0 && i < c.Shared.Count ? c.Shared[i] : "";
                                break;
                            }
                        case "inlineStr": text = string.Concat(cel.Child("is")?.All("t").Select(n => n.Text) ?? Enumerable.Empty<string>()); break;
                        case "b": text = vNode?.Text.Trim() == "1" ? "TRUE" : "FALSE"; break;
                        case "e": text = vNode?.Text ?? "#ERR"; break;
                        case "str": text = vNode?.Text ?? ""; break;
                        default: num = ToDouble(vNode?.Text.Trim()); text = vNode?.Text ?? ""; break;
                    }
                    string? img = null;
                    if (ToInt(cel.Attr("vm")) is { } vm && c.VmRv.TryGetValue(vm, out var rvi) && c.RvImg.TryGetValue(rvi, out var ip)) img = ip;
                    else if (c.WpsImg.Count > 0)
                    {
                        var f = cel.Child("f")?.Text ?? text;
                        if (f.Contains("DISPIMG") && DISPIMG.Match(f) is { Success: true } dm && c.WpsImg.TryGetValue(dm.Groups[1].Value, out var wp)) img = wp;
                    }
                    if (text.Length == 0 && s == 0 && img == null) continue;
                    cells[Key(r, cIdx)] = new Cell(img != null ? "" : text, img != null ? null : rich, s, t, num, img);
                    if (img != null) { maxR = Math.Max(maxR, r); maxC = Math.Max(maxC, cIdx); }
                    var xf0 = s >= 0 && s < c.Xfs.Count ? c.Xfs[s] : null;
                    if (text.Length > 0 || (xf0 != null && (xf0.Fill != 0 || xf0.Border != 0))) { maxR = Math.Max(maxR, r); maxC = Math.Max(maxC, cIdx); }
                }
            }
            var mergeTop = new Dictionary<long, int[]>(); var covered = new HashSet<long>();
            foreach (var m in ws.Child("mergeCells")?.Children("mergeCell") ?? new())
            {
                var rf = m.Attr("ref"); if (rf == null) continue;
                var p = rf.Split(':'); if (p.Length != 2) continue;
                int r0 = RowIndex(p[0]), c0 = ColIndex(p[0]), r1 = RowIndex(p[1]), c1 = ColIndex(p[1]);
                if (r0 < 0 || c0 < 0 || r0 >= MAX_ROWS || c0 > MAX_COLS) continue;
                int r1c = Math.Min(r1, MAX_ROWS - 1), c1c = Math.Min(c1, MAX_COLS);
                mergeTop[Key(r0, c0)] = new[] { r1c - r0 + 1, c1c - c0 + 1 };
                for (int rr = r0; rr <= r1c; rr++) for (int cc = c0; cc <= c1c; cc++) if (rr != r0 || cc != c0) covered.Add(Key(rr, cc));
                maxR = Math.Max(maxR, r1c); maxC = Math.Max(maxC, c1c);
            }
            var tableFx = new Dictionary<long, string>();
            foreach (var tp in ws.Child("tableParts")?.Children("tablePart") ?? new())
            {
                var tid = tp.Attr("id"); if (tid == null || !rels.TryGetValue(tid, out var tRel)) continue;
                var tx0 = c.Pkg.Xml(tRel.Target); if (tx0 == null) continue;
                var tx = tx0.Child("table") ?? tx0;
                var rf = tx.Attr("ref"); if (rf == null) continue;
                var pr = rf.Split(':'); if (pr.Length != 2) continue;
                int r0 = RowIndex(pr[0]), c0 = ColIndex(pr[0]), r1 = Math.Min(RowIndex(pr[1]), MAX_ROWS - 1), c1 = Math.Min(ColIndex(pr[1]), MAX_COLS);
                int hdrRows = ToInt(tx.Attr("headerRowCount")) ?? 1, totRows = ToInt(tx.Attr("totalsRowCount")) ?? 0;
                var si = tx.Child("tableStyleInfo");
                var name = si?.Attr("name") ?? "TableStyleMedium2";
                bool stripes = si?.Attr("showRowStripes") is null or "1" or "true";
                var mm = Regex.Match(name, "TableStyle(Light|Medium|Dark)(\\d+)"); if (!mm.Success) continue;
                var kind = mm.Groups[1].Value; int num = int.Parse(mm.Groups[2].Value, Inv);
                int k = (num - 1) % 7;
                int accent = k == 0 ? (c.Theme.Count > 1 ? c.Theme[1] : 0) : (3 + k < c.Theme.Count ? c.Theme[3 + k] : 0x4472C4);
                int group = (num - 1) / 7;
                string Tc(float t) => Hex(Tint(accent, t));
                string A = Hex(accent);
                string hdrCss, bodyCss; string? bandCss;
                switch (kind)
                {
                    case "Light":
                        switch (group)
                        {
                            case 0: hdrCss = "font-weight:bold;border-bottom:1px solid " + A + ";"; bandCss = "background:" + Tc(0.8f) + ";"; bodyCss = ""; break;
                            case 1: hdrCss = "font-weight:bold;color:#fff;background:" + A + ";"; bandCss = null; bodyCss = "border-bottom:1px solid " + Tc(0.4f) + ";"; break;
                            default: hdrCss = "font-weight:bold;border:1px solid " + A + ";"; bandCss = "background:" + Tc(0.8f) + ";"; bodyCss = "border:1px solid " + A + ";"; break;
                        }
                        break;
                    case "Medium":
                        switch (group)
                        {
                            case 0: hdrCss = "font-weight:bold;color:#fff;background:" + A + ";"; bandCss = "background:" + Tc(0.8f) + ";"; bodyCss = "border-bottom:1px solid " + Tc(0.4f) + ";"; break;
                            case 1: hdrCss = "font-weight:bold;color:#fff;background:#000;"; bandCss = "background:" + Tc(0.6f) + ";"; bodyCss = "background:" + Tc(0.8f) + ";"; break;
                            case 2: hdrCss = "font-weight:bold;color:#fff;background:" + A + ";border:1px solid #fff;"; bandCss = "background:" + Tc(0.6f) + ";"; bodyCss = "background:" + Tc(0.8f) + ";border:1px solid #fff;"; break;
                            default: hdrCss = "font-weight:bold;"; bandCss = "background:" + Tc(0.6f) + ";"; bodyCss = "background:" + Tc(0.8f) + ";border:1px solid " + Tc(0.4f) + ";"; break;
                        }
                        break;
                    default:
                        if (group == 0) { hdrCss = "font-weight:bold;color:#fff;background:#000;"; bandCss = "background:" + Hex(Tint(accent, -0.25f)) + ";color:#fff;"; bodyCss = "background:" + A + ";color:#fff;"; }
                        else { hdrCss = "font-weight:bold;color:#fff;background:#000;"; bandCss = "background:" + Tc(0.4f) + ";"; bodyCss = "background:" + Tc(0.6f) + ";"; }
                        break;
                }
                for (int rr = Math.Max(0, r0); rr <= r1; rr++) for (int cc = Math.Max(0, c0); cc <= c1; cc++)
                    {
                        string css = rr < r0 + hdrRows ? hdrCss
                            : totRows > 0 && rr > r1 - totRows ? "font-weight:bold;border-top:2px double " + A + ";"
                            : stripes && bandCss != null && (rr - r0 - hdrRows) % 2 == 0 ? bodyCss + bandCss : bodyCss;
                        tableFx[Key(rr, cc)] = css;
                    }
                maxR = Math.Max(maxR, r1); maxC = Math.Max(maxC, c1);
            }
            var anchors = new List<Anchor>();
            var drId = ws.Child("drawing")?.Attr("id");
            if (drId != null && rels.TryGetValue(drId, out var drawingRel) && c.Pkg.Xml(drawingRel.Target) is { } dx) anchors = AnchorsOf(c, dx, drawingRel.Target);
            foreach (var a in anchors) { maxR = Math.Max(maxR, a.R1); maxC = Math.Max(maxC, a.C1); }
            if (maxR < 0 && anchors.Count == 0) { outB.Append("<div class=\"empty\">Lembar kosong.</div>"); return; }
            int nRows = Math.Min(Math.Max(maxR + 1, 1) + 3, MAX_ROWS), nCols = Math.Min(Math.Max(maxC + 1, 1) + 1, MAX_COLS + 1);
            double Cw(int i) => colHidden.Contains(i) ? 0 : Math.Floor((colW.TryGetValue(i, out var w) ? w : defColW) * maxDigit + 5);
            double Rh(int r) => rowHidden.Contains(r) ? 0 : (rowH.TryGetValue(r, out var h) ? h : defRowH) * 4 / 3;

            double tableW = 40; for (int i = 0; i < nCols; i++) tableW += Cw(i);
            outB.Append("<div class=\"sheet\"><table class=\"grid").Append(showGrid ? " gl" : "").Append("\" style=\"width:").Append(N(tableW)).Append("px\"><colgroup><col style=\"width:40px\">");
            for (int i = 0; i < nCols; i++) outB.Append("<col style=\"width:").Append(N(Cw(i))).Append("px\">");
            outB.Append("</colgroup><thead><tr><th class=\"corner\"></th>");
            for (int i = 0; i < nCols; i++) outB.Append("<th class=\"ch\"").Append(colHidden.Contains(i) ? " style=\"display:none\"" : "").Append('>').Append(ColName(i)).Append("</th>");
            outB.Append("</tr></thead><tbody>");
            var f0 = c.Fonts.FirstOrDefault();
            for (int r = 0; r < nRows; r++)
            {
                if (rowHidden.Contains(r)) continue;
                outB.Append("<tr style=\"height:").Append(N(Rh(r))).Append("px\"><th class=\"rh\">").Append(r + 1).Append("</th>");
                for (int ci = 0; ci < nCols; ci++)
                {
                    long key = Key(r, ci);
                    if (covered.Contains(key)) continue;
                    if (colHidden.Contains(ci)) { outB.Append("<td style=\"display:none\"></td>"); continue; }
                    cells.TryGetValue(key, out var cell);
                    mergeTop.TryGetValue(key, out var m);
                    int xfIdx = cell?.Xf ?? (colStyle.TryGetValue(ci, out var cs0) ? cs0 : 0);
                    var xf = xfIdx >= 0 && xfIdx < c.Xfs.Count ? c.Xfs[xfIdx] : null;
                    outB.Append("<td");
                    if (m != null) { if (m[0] > 1) outB.Append(" rowspan=\"").Append(m[0]).Append('"'); if (m[1] > 1) outB.Append(" colspan=\"").Append(m[1]).Append('"'); }
                    var css = new StringBuilder();
                    if (tableFx.TryGetValue(key, out var tfx)) css.Append(tfx);
                    string? align = null;
                    if (xf != null)
                    {
                        var f = xf.Font >= 0 && xf.Font < c.Fonts.Count ? c.Fonts[xf.Font] : null;
                        if (f != null && xf.Font != 0)
                        {
                            if (f.Name != f0?.Name) css.Append("font-family:").Append(FontStack(f.Name)).Append(';');
                            if (f.Size != f0?.Size) css.Append("font-size:").Append(N(f.Size)).Append("pt;");
                        }
                        if (f != null)
                        {
                            if (f.B) css.Append("font-weight:bold;"); if (f.I) css.Append("font-style:italic;");
                            if (f.U || f.Strike) css.Append("text-decoration:").Append(f.U ? "underline " : "").Append(f.Strike ? "line-through" : "").Append(';');
                            if (f.Color != null) css.Append("color:").Append(Hex(f.Color.Value)).Append(';');
                        }
                        if (xf.Fill >= 0 && xf.Fill < c.Fills.Count && c.Fills[xf.Fill] is { } fill) css.Append("background:").Append(Hex(fill)).Append(';');
                        if (xf.Border >= 0 && xf.Border < c.Borders.Count)
                        {
                            var b = c.Borders[xf.Border];
                            void Bs(string side, Side? p) { if (p != null) css.Append("border-").Append(side).Append(':').Append(Bcss(p)).Append(';'); }
                            Bs("left", b.L); Bs("right", b.R); Bs("top", b.T); Bs("bottom", b.B);
                        }
                        align = xf.H;
                        if (xf.V == "top") css.Append("vertical-align:top;"); else if (xf.V == "center") css.Append("vertical-align:middle;");
                        if (xf.Wrap) css.Append("white-space:pre-wrap;");
                        if (xf.Indent > 0) css.Append("padding-left:").Append(xf.Indent * 9 + 2).Append("px;");
                    }
                    string shown = ""; int? colorOverride = null; bool isNum = false;
                    if (cell != null)
                    {
                        if (cell.Num != null && cell.Type == "n")
                        {
                            var fmt = FmtCode(c, xf?.NumFmt ?? 0);
                            (shown, colorOverride) = NumFmt.Format(cell.Num.Value, fmt, c.Date1904);
                            isNum = true;
                        }
                        else shown = Cap(cell.V);
                    }
                    if (colorOverride != null) css.Append("color:").Append(Hex(colorOverride.Value)).Append(';');
                    string hAlign = align switch
                    {
                        "center" or "centerContinuous" => "center", "right" => "right", "left" => "left", "justify" or "distributed" => "justify", "fill" => "left",
                        _ => isNum || cell?.Type == "b" || cell?.Type == "e" ? "right" : "left"
                    };
                    if (hAlign != "left") css.Append("text-align:").Append(hAlign).Append(';');
                    long nextKey = hAlign == "right" ? Key(r, ci - 1) : Key(r, ci + 1);
                    bool overflow = shown.Length > 0 && xf?.Wrap != true && m == null && !isNum && (!cells.TryGetValue(nextKey, out var nc) || string.IsNullOrEmpty(nc.V)) && !covered.Contains(nextKey);
                    string? cssStr = css.Length > 0 ? css.ToString() : null;
                    var k = cssStr != null ? c.Cls(cssStr) : null;
                    if (overflow || k != null)
                    {
                        outB.Append(" class=\"");
                        if (overflow) outB.Append('o');
                        if (k != null) { if (overflow) outB.Append(' '); outB.Append(k); }
                        outB.Append('"');
                    }
                    if (cssStr != null && k == null) outB.Append(" style=\"").Append(cssStr).Append('"');
                    outB.Append('>');
                    if (cell?.Img is { } part)
                    {
                        double iw = Cw(ci), ih = Rh(r);
                        if (m != null) { iw = 0; for (int q = 0; q < m[1]; q++) iw += Cw(ci + q); ih = 0; for (int q = 0; q < m[0]; q++) ih += Rh(r + q); }
                        if (c.Res.Url(part, iw, ih) is { } src)
                            outB.Append("<img class=\"ci\" src=\"").Append(src).Append("\" style=\"width:").Append(N(Math.Max(1, iw - 3))).Append("px;height:").Append(N(Math.Max(1, ih - 3))).Append("px\">");
                    }
                    if (shown.Length > 0)
                    {
                        string content = cell?.Type == "s" && cell.Rich != null ? cell.Rich : Esc(shown);
                        if (overflow) outB.Append("<div class=\"ov ").Append(hAlign).Append("\">").Append(content).Append("</div>");
                        else if (isNum) outB.Append(Esc(shown)); else outB.Append(content);
                    }
                    outB.Append("</td>");
                }
                outB.Append("</tr>");
            }
            outB.Append("</tbody></table>");
            if (anchors.Count > 0)
            {
                var colX = new double[nCols + 2]; for (int i = 1; i <= nCols + 1; i++) colX[i] = colX[i - 1] + (i - 1 < nCols ? Cw(i - 1) : defColW * maxDigit);
                var rowY = new double[nRows + 2]; for (int i = 1; i <= nRows + 1; i++) rowY[i] = rowY[i - 1] + (i - 1 < nRows ? Rh(i - 1) : defRowH * 4 / 3);
                foreach (var a in anchors)
                {
                    double x0 = 40 + colX[Math.Clamp(a.C0, 0, nCols)] + a.Dx0, y0 = 20 + rowY[Math.Clamp(a.R0, 0, nRows)] + a.Dy0;
                    double w = a.WPx > 0 ? a.WPx : (40 + colX[Math.Clamp(a.C1, 0, nCols)] + a.Dx1) - x0;
                    double h = a.HPx > 0 ? a.HPx : (20 + rowY[Math.Clamp(a.R1, 0, nRows)] + a.Dy1) - y0;
                    if (w <= 1 || h <= 1) continue;
                    outB.Append("<div class=\"pic\" style=\"left:").Append(N(x0)).Append("px;top:").Append(N(y0)).Append("px;width:").Append(N(w)).Append("px;height:").Append(N(h)).Append("px\">");
                    var src = a.Part != null ? c.Res.Url(a.Part, w, h) : null;
                    if (src != null) outB.Append("<img src=\"").Append(src).Append("\" style=\"width:100%;height:100%\">");
                    else outB.Append("<div class=\"ph\">").Append(Esc(a.Label.Length == 0 ? "Gambar" : a.Label)).Append("</div>");
                    outB.Append("</div>");
                }
            }
            outB.Append("</div>");
            if (maxR + 1 >= MAX_ROWS) outB.Append("<div class=\"note\">Menampilkan ").Append(MAX_ROWS).Append(" baris pertama. Buka di Editor Excel untuk seluruh data.</div>");
        }

        private sealed record Anchor(int C0, int R0, double Dx0, double Dy0, int C1, int R1, double Dx1, double Dy1, double WPx, double HPx, string? Part, string Label);

        private static List<Anchor> AnchorsOf(Ctx c, XNode dx, string path)
        {
            var rels = c.Pkg.Rels(path);
            var outL = new List<Anchor>();
            var root = dx.Kids.FirstOrDefault(); if (root == null) return outL;
            foreach (var a in root.Kids)
            {
                if (a.Name is not ("twoCellAnchor" or "oneCellAnchor" or "absoluteAnchor")) continue;
                int[]? Pos(XNode? n) => n == null ? null : new[] { ToInt(n.Child("col")?.Text.Trim()) ?? 0, ToInt(n.Child("row")?.Text.Trim()) ?? 0 };
                double Off(XNode? n, string k) => (ToLong(n?.Child(k)?.Text.Trim()) ?? 0) / 9525.0;
                var f = a.Child("from"); var t = a.Child("to");
                var fp = Pos(f) ?? new[] { 0, 0 }; var tp = Pos(t);
                var ext = a.Child("ext");
                double wPx = (ToLong(ext?.Attr("cx")) ?? 0) / 9525.0, hPx = (ToLong(ext?.Attr("cy")) ?? 0) / 9525.0;
                string? src = null; string label = "";
                var blip = a.First("blip");
                if (blip != null)
                {
                    var id = blip.Attr("embed") ?? blip.Attr("link");
                    if (id != null && rels.TryGetValue(id, out var rel) && !rel.External && c.Pkg.Has(rel.Target)) src = rel.Target;
                    if (src == null) label = "Gambar";
                }
                else if (a.First("chart") != null) label = "Grafik";
                else if (a.First("sp") != null)
                {
                    label = string.Join(" ", a.First("txBody")?.All("t").Select(n => n.Text) ?? Enumerable.Empty<string>());
                    if (string.IsNullOrWhiteSpace(label)) continue;
                }
                else continue;
                outL.Add(new Anchor(fp[0], fp[1], Off(f, "colOff"), Off(f, "rowOff"), tp?[0] ?? fp[0], tp?[1] ?? fp[1], Off(t, "colOff"), Off(t, "rowOff"),
                    t == null ? wPx : 0, t == null ? hPx : 0, src, label));
            }
            return outL;
        }

        private static string Bcss(Side p)
        {
            var col = Hex(p.Color ?? 0);
            return p.Style switch
            {
                "thin" => "1px solid " + col, "hair" or "dotted" => "1px dotted " + col, "dashed" or "dashDot" or "dashDotDot" => "1px dashed " + col,
                "medium" => "2px solid " + col, "mediumDashed" or "mediumDashDot" or "mediumDashDotDot" or "slantDashDot" => "2px dashed " + col,
                "thick" => "3px solid " + col, "double" => "3px double " + col, _ => "1px solid " + col
            };
        }

        private static string FmtCode(Ctx c, int id) => c.NumFmts.TryGetValue(id, out var v) ? v : BUILTIN.TryGetValue(id, out var b) ? b : "General";

        private static readonly Dictionary<int, string> BUILTIN = new()
        {
            [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??",
            [14] = "dd/mm/yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss",
            [22] = "dd/mm/yyyy h:mm", [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;[Red](#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;[Red](#,##0.00)",
            [44] = "_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)", [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@"
        };

        private static readonly int[] DEFAULT_INDEXED =
        {
            0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF, 0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00,
            0xFF00FF, 0x00FFFF, 0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080, 0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF,
            0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF, 0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF, 0x00CCFF, 0xCCFFFF,
            0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99, 0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696,
            0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333
        };

        private const string CSS = @"
html,body{margin:0;padding:0;background:#fff;color:#000}
#main{padding-bottom:48px}
.sheet{position:relative;display:inline-block;min-width:100%}
table.grid{border-collapse:collapse;table-layout:fixed}
table.grid td{padding:0 3px;white-space:nowrap;overflow:hidden;vertical-align:bottom;line-height:1.15;box-sizing:border-box}
table.grid.gl td{border:1px solid #e1e3e6}
table.grid td.o{overflow:visible}
.ov{white-space:nowrap;position:relative;z-index:1}
.ov.right{direction:rtl;text-align:right}.ov.center{text-align:center}
th{background:#f3f2ee;color:#65625d;font:11px 'Segoe UI',sans-serif;border:1px solid #dcd9d3;padding:0 2px;position:sticky;z-index:2}
th.ch{top:0;height:20px;text-align:center}
th.rh{left:0;text-align:center;width:40px}
th.corner{top:0;left:0;z-index:3}
thead th{position:sticky;top:0}
.pic{position:absolute;z-index:4}
img.ci{display:block;object-fit:contain;max-width:none;margin:1px auto}
.pic .ph{width:100%;height:100%;box-sizing:border-box;border:1px dashed #9aa0a6;background:rgba(241,243,246,.85);display:flex;align-items:center;justify-content:center;font:12px sans-serif;color:#5f6368}
#tabs{position:fixed;left:0;right:0;bottom:0;background:#f1f0ec;border-top:1px solid #e4e2dd;white-space:nowrap;overflow-x:auto;padding:6px;z-index:10}
.tab{font:13px 'Segoe UI',sans-serif;border:0;background:#fff;color:#111;padding:7px 16px;margin-right:6px;border-radius:999px;cursor:pointer}
.tab.on{background:#1652f6;color:#fff;font-weight:600}
.empty,.note{font:13px 'Segoe UI',sans-serif;color:#65625d;padding:16px}
@media print{#tabs{display:none}th{position:static}#main{padding:0}}
";

        private const string JS = @"
function show(i){var w=document.querySelectorAll('.sheet-wrap');for(var k=0;k<w.length;k++)w[k].style.display=k==i?'block':'none';
var t=document.querySelectorAll('.tab');for(var k2=0;k2<t.length;k2++)t[k2].className='tab'+(k2==i?' on':'');window.scrollTo(0,0);}
document.body.setAttribute('data-ready','1');
if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage('ready:0');
";
    }

    /// <summary>Format angka Excel (bawaan &amp; kustom), port NumFmt Android.</summary>
    public static class NumFmt
    {
        private static readonly string[] MONTHS = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        private static readonly string[] DAYS = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        private static readonly Regex COLOR = new("\\[(Black|White|Red|Green|Blue|Yellow|Magenta|Cyan|Color\\s*\\d+)]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CONDITION = new("\\[[<>=][^]]*]", RegexOptions.Compiled);
        private static readonly Regex CURRENCY = new("\\[\\$([^-\\]]*)(-[^]]*)?]", RegexOptions.Compiled);
        private static readonly Regex DIGIT_SLOTS = new("[0#?]", RegexOptions.Compiled);
        private static readonly Regex EXP_ZEROS = new("0+E", RegexOptions.Compiled);
        private static readonly Regex FRACTION_SECONDS = new("s+\\.0", RegexOptions.Compiled);
        private static readonly Regex AM_PM = new("AM/PM|A/P", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed record Section(string Sec, int? Color, bool Date);
        private static readonly ConcurrentDictionary<string, List<string>> Sections = new();
        private static readonly ConcurrentDictionary<string, Section> Prepared = new();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static List<string> SectionsOf(string code)
        {
            if (Sections.TryGetValue(code, out var v)) return v;
            if (Sections.Count > 256) Sections.Clear();
            return Sections[code] = SplitSections(code);
        }

        private static Section Prepare(string sec0)
        {
            if (Prepared.TryGetValue(sec0, out var v)) return v;
            int? color = null;
            var m = COLOR.Match(sec0);
            if (m.Success)
                color = m.Groups[1].Value.ToLowerInvariant() switch { "red" => 0xFF0000, "blue" => 0x0000FF, "green" => 0x00B050, "yellow" => 0xFFFF00, "magenta" => 0xFF00FF, "cyan" => 0x00FFFF, "white" => 0xFFFFFF, _ => 0 };
            var sec = COLOR.Replace(sec0, "");
            sec = CONDITION.Replace(sec, "");
            sec = CURRENCY.Replace(sec, mm => mm.Groups[1].Value);
            var p = new Section(sec, color, IsDate(sec));
            if (Prepared.Count > 512) Prepared.Clear();
            return Prepared[sec0] = p;
        }

        public static (string Text, int? Color) Format(double v, string code0, bool date1904)
        {
            var code = code0.Trim();
            if (code.Length == 0 || code.Equals("General", StringComparison.OrdinalIgnoreCase) || code == "@") return (General(v), null);
            var sections = SectionsOf(code);
            string sec; double value = v;
            if (sections.Count >= 2 && v < 0) { sec = sections[1]; value = -v; }
            else if (sections.Count >= 3 && v == 0) sec = sections[2];
            else sec = sections[0];
            var p = Prepare(sec);
            sec = p.Sec;
            bool neg = v < 0 && sections.Count < 2;
            if (p.Date) { try { return (FormatDate(v, sec, date1904), p.Color); } catch { return (General(v), p.Color); } }
            var s = FormatNumber(neg ? -value : value, sec);
            return (neg ? "-" + s : s, p.Color);
        }

        private static List<string> SplitSections(string code)
        {
            var outL = new List<string>(); var sb = new StringBuilder(); bool q = false, esc = false;
            foreach (var ch in code)
            {
                if (esc) { sb.Append(ch); esc = false; continue; }
                if (ch == '\\') { sb.Append(ch); esc = true; }
                else if (ch == '"') { q = !q; sb.Append(ch); }
                else if (ch == ';' && !q) { outL.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            outL.Add(sb.ToString());
            return outL;
        }

        public static string General(double v)
        {
            if (v == 0) return "0";
            if (v == Math.Floor(v) && Math.Abs(v) < 1e11) return ((long)v).ToString(Inv);
            double a = Math.Abs(v);
            if (a >= 1e11 || a < 1e-9) return EXP_ZEROS.Replace(v.ToString("0.00000E+00", Inv), "E").Replace(".E", "E");
            int digitsBefore = a >= 1 ? (int)Math.Floor(Math.Log10(a)) + 1 : 0;
            int dec = Math.Clamp(10 - digitsBefore, 0, 10);
            var s = v.ToString("F" + dec, Inv);
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s;
        }

        private static bool IsDate(string sec)
        {
            bool q = false;
            var stripped = new StringBuilder();
            int i = 0;
            while (i < sec.Length)
            {
                char ch = sec[i];
                if (ch == '"') { q = !q; i++; continue; }
                if (!q && ch == '\\') { i += 2; continue; }
                if (!q && ch == '[')
                {
                    int e = sec.IndexOf(']', i);
                    if (e > i)
                    {
                        var inner = sec.Substring(i + 1, e - i - 1).ToLowerInvariant();
                        if (inner is "h" or "hh" or "m" or "mm" or "s" or "ss") stripped.Append('h');
                        i = e + 1; continue;
                    }
                }
                if (!q) stripped.Append(ch);
                i++;
            }
            var s = stripped.ToString().ToLowerInvariant();
            return s.Any(ch => ch is 'y' or 'd' or 'h' or 's') || (s.Contains('m') && !s.Contains('0') && !s.Contains('#'));
        }

        private static string FormatNumber(double v, string sec)
        {
            var tokens = new List<(bool Pat, string Text)>();
            var pat = new StringBuilder(); bool inPat = false;
            void FlushPat() { if (inPat) { tokens.Add((true, pat.ToString())); pat.Clear(); inPat = false; } }
            void Lit(string t) { if (t.Length > 0) tokens.Add((false, t)); }
            int i = 0;
            while (i < sec.Length)
            {
                char ch = sec[i];
                if (ch == '"') { int e = sec.IndexOf('"', i + 1); if (e < 0) e = sec.Length; FlushPat(); Lit(sec.Substring(i + 1, e - i - 1)); i = e + 1; continue; }
                if (ch == '\\' && i + 1 < sec.Length) { FlushPat(); Lit(sec[i + 1].ToString()); i += 2; continue; }
                if (ch == '_' && i + 1 < sec.Length) { FlushPat(); Lit(" "); i += 2; continue; }
                if (ch == '*' && i + 1 < sec.Length) { i += 2; continue; }
                bool isPatCh = "0#?.,%E+-".IndexOf(ch) >= 0
                    && (ch != 'E' || (i + 1 < sec.Length && (sec[i + 1] == '+' || sec[i + 1] == '-')))
                    && ((ch != '-' && ch != '+') || (inPat && pat.Length > 0 && pat[^1] == 'E'));
                if (isPatCh) { inPat = true; pat.Append(ch); }
                else if (ch == ',') pat.Append(ch);
                else { FlushPat(); Lit(ch.ToString()); }
                i++;
            }
            FlushPat();
            int pIdx = tokens.FindIndex(t => t.Pat && t.Text.Any(ch => ch is '0' or '#' or '?'));
            if (pIdx < 0) return string.Concat(tokens.Select(t => t.Text));
            var p = tokens[pIdx].Text;
            double value = v;
            int pctCount = tokens.Sum(t => t.Text.Count(ch => ch == '%'));
            for (int k = 0; k < pctCount; k++) value *= 100;
            p = p.Replace("%", "");
            while (p.EndsWith(',')) { value /= 1000; p = p[..^1]; }
            var num = p.Contains("E+") || p.Contains("E-") ? Sci(value, p) : Plain(value, p);
            var sb = new StringBuilder();
            for (int k = 0; k < tokens.Count; k++) sb.Append(k == pIdx ? num : tokens[k].Pat ? DIGIT_SLOTS.Replace(tokens[k].Text, "") : tokens[k].Text);
            if (pctCount > 0 && !sb.ToString().Contains('%')) sb.Append('%');
            return sb.ToString();
        }

        private static string Plain(double v, string p)
        {
            int dot = p.IndexOf('.');
            var intP = dot >= 0 ? p[..dot] : p;
            var decP = dot >= 0 ? new string(p[(dot + 1)..].Where(ch => ch is '0' or '#' or '?').ToArray()) : "";
            bool grouping = intP.Contains(',');
            int minInt = intP.Count(ch => ch == '0');
            int decMin = decP.Count(ch => ch == '0'), decMax = decP.Length;
            string s;
            if (Math.Abs(v) < 7.9e27) s = Math.Abs(Math.Round((decimal)v, Math.Min(decMax, 28), MidpointRounding.AwayFromZero)).ToString("F" + decMax, Inv);
            else s = Math.Abs(v).ToString("F" + Math.Min(decMax, 15), Inv);
            int sd = s.IndexOf('.');
            var ip = sd >= 0 ? s[..sd] : s; var dp = sd >= 0 ? s[(sd + 1)..] : "";
            while (dp.Length > decMin && dp.EndsWith('0')) dp = dp[..^1];
            if (ip == "0" && minInt == 0) ip = "";
            while (ip.Length < minInt) ip = "0" + ip;
            if (grouping && ip.Length > 3)
            {
                var g = new StringBuilder(); int cnt = 0;
                for (int k = ip.Length - 1; k >= 0; k--) { g.Insert(0, ip[k]); cnt++; if (cnt % 3 == 0 && k > 0) g.Insert(0, ','); }
                ip = g.ToString();
            }
            return dp.Length > 0 || (decMax > 0 && decMin > 0) ? ip + "." + dp : ip;
        }

        private static string Sci(double v, string p)
        {
            int ei = p.IndexOf('E');
            var mant = p[..ei]; var expP = p.Length > ei + 2 ? p[(ei + 2)..] : "";
            int decs = mant.Contains('.') ? mant[(mant.IndexOf('.') + 1)..].Length : 0;
            if (v == 0) return Plain(0, mant) + "E+" + new string('0', Math.Max(1, expP.Length));
            int e = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            int intDigits = Math.Max(1, (mant.Contains('.') ? mant[..mant.IndexOf('.')] : mant).Count(ch => ch is '0' or '#'));
            if (intDigits > 1) e = (int)Math.Floor((double)e / intDigits) * intDigits;
            double m = v / Math.Pow(10, e);
            return m.ToString("F" + decs, Inv) + "E" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString(Inv).PadLeft(Math.Max(1, expP.Length), '0');
        }

        private static string FormatDate(double v, string s, bool date1904)
        {
            long days = (long)Math.Floor(v);
            double frac = v - Math.Floor(v);
            DateTime epoch;
            if (date1904) epoch = new DateTime(1904, 1, 1);
            else { epoch = new DateTime(1899, 12, 31); if (days >= 60) days -= 1; }
            var date = epoch.AddDays(days);
            bool hasFrac = FRACTION_SECONDS.IsMatch(s.ToLowerInvariant());
            long totalMs = (long)Math.Round(frac * 86400000.0);
            long secs = hasFrac ? totalMs / 1000 : (long)(frac * 86400 + 0.5);
            long totalH = (long)Math.Floor(v * 24);
            int hh = (int)(secs / 3600), mi = (int)(secs % 3600 / 60), ss = (int)(secs % 60);
            bool ampm = AM_PM.IsMatch(s);
            var outB = new StringBuilder();
            int i = 0; bool lastWasHour = false;
            bool PeekSec(int from) { int j = from; while (j < s.Length && !char.IsLetter(s[j])) j++; return j < s.Length && (s[j] == 's' || s[j] == 'S'); }
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '"') { int e = s.IndexOf('"', i + 1); if (e < 0) e = s.Length; outB.Append(s, i + 1, e - i - 1); i = e + 1; continue; }
                if (ch == '\\' && i + 1 < s.Length) { outB.Append(s[i + 1]); i += 2; continue; }
                if (ch == '_' && i + 1 < s.Length) { outB.Append(' '); i += 2; continue; }
                if (ch == '*' && i + 1 < s.Length) { i += 2; continue; }
                if (ch == '[')
                {
                    int e = s.IndexOf(']', i); var inner = e > i ? s.Substring(i + 1, e - i - 1).ToLowerInvariant() : "";
                    if (inner.StartsWith('h')) outB.Append(totalH.ToString(Inv).PadLeft(inner.Length, '0'));
                    else if (inner.StartsWith('m')) outB.Append(((long)Math.Floor(v * 1440)).ToString(Inv).PadLeft(inner.Length, '0'));
                    else if (inner.StartsWith('s')) outB.Append(((long)Math.Floor(v * 86400)).ToString(Inv).PadLeft(inner.Length, '0'));
                    i = e > i ? e + 1 : i + 1; continue;
                }
                char lc = char.ToLowerInvariant(ch);
                int n = 1; while (i + n < s.Length && char.ToLowerInvariant(s[i + n]) == lc) n++;
                switch (lc)
                {
                    case 'y': outB.Append(n <= 2 ? (date.Year % 100).ToString("00", Inv) : date.Year.ToString(Inv)); lastWasHour = false; break;
                    case 'd':
                        outB.Append(n switch { 1 => date.Day.ToString(Inv), 2 => date.Day.ToString("00", Inv), 3 => DAYS[(int)date.DayOfWeek][..3], _ => DAYS[(int)date.DayOfWeek] });
                        lastWasHour = false; break;
                    case 'm':
                        if (lastWasHour || PeekSec(i + n)) outB.Append(n >= 2 ? mi.ToString("00", Inv) : mi.ToString(Inv));
                        else outB.Append(n switch { 1 => date.Month.ToString(Inv), 2 => date.Month.ToString("00", Inv), 3 => MONTHS[date.Month - 1][..3], 5 => MONTHS[date.Month - 1][..1], _ => MONTHS[date.Month - 1] });
                        lastWasHour = false; break;
                    case 'h': { int h12 = ampm ? (hh + 11) % 12 + 1 : hh; outB.Append(n >= 2 ? h12.ToString("00", Inv) : h12.ToString(Inv)); lastWasHour = true; break; }
                    case 's': outB.Append(n >= 2 ? ss.ToString("00", Inv) : ss.ToString(Inv)); lastWasHour = false; break;
                    case 'a':
                        if (string.Compare(s, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { outB.Append(hh < 12 ? "AM" : "PM"); i += 5; continue; }
                        if (string.Compare(s, i, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) { outB.Append(hh < 12 ? "A" : "P"); i += 3; continue; }
                        outB.Append(ch); break;
                    case 'e': outB.Append(date.Year); break;
                    case '0': outB.Append((totalMs % 1000).ToString("000", Inv)[..Math.Min(3, n)]); i += n; continue;
                    default: outB.Append(s, i, n); break;
                }
                i += n;
            }
            return outB.ToString();
        }
    }
}
