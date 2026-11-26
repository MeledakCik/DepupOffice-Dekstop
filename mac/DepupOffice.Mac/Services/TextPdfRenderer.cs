using DepupOfficeDesktop.Core;
using SkiaSharp;
using CoreRect = DepupOfficeDesktop.Core.Rect;

namespace DepupOffice.Mac.Services;

/// <summary>
/// Menata teks dokumen ke halaman (word-wrap, rata kiri/tengah/kanan/justify) dan menulis PDF yang bisa dicari
/// (gambar halaman + lapisan teks tak terlihat). Pengganti PdfEngine.TextToPdf (GDI+) di Windows.
/// </summary>
public static class TextPdfRenderer
{
    private const float Dpi = 150f;
    private const float S = Dpi / 72f; // piksel per point

    private sealed record RenderedPage(SKBitmap Bitmap, List<OcrLine> Lines);

    public static SKTypeface Face(int family, bool bold, bool italic)
    {
        string name = family == 1 ? "Times New Roman" : "Helvetica Neue";
        var style = bold && italic ? SKFontStyle.BoldItalic : bold ? SKFontStyle.Bold : italic ? SKFontStyle.Italic : SKFontStyle.Normal;
        return SKTypeface.FromFamilyName(name, style) ?? SKTypeface.Default;
    }

    public static void DocToPdf(DocModel doc, string outPath, string? password = null)
    {
        var (pw, ph) = doc.PagePoints();
        var pages = Layout(doc.Blocks(), pw, ph, doc.MarginPoints(), doc.FontSize, doc.LineSpacing, doc.FontFamily);
        WritePages(pages, outPath, pw, ph, password);
    }

    public static void TextToPdf(string title, string text, string outPath)
    {
        var doc = DocModel.FromPlainText(title, text);
        doc.Title = title;
        DocToPdf(doc, outPath);
    }

    private static void WritePages(List<RenderedPage> pages, string outPath, float pw, float ph, string? password)
    {
        string tmp = outPath + ".tmp";
        var writer = new PdfWriter(tmp, pages.Count, password);
        try
        {
            foreach (var p in pages)
            {
                byte[] jpeg = ImageUtil.EncodeJpeg(p.Bitmap, 88);
                var layer = p.Lines.Count > 0 ? new OcrPage("", new List<string>(), p.Lines, p.Bitmap.Width, p.Bitmap.Height) : null;
                writer.AddPage(jpeg, p.Bitmap.Width, p.Bitmap.Height, pw, ph, 0, 0, pw, ph, layer);
            }
            writer.Finish();
            File.Move(tmp, outPath, true);
        }
        catch
        {
            writer.Abort();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        finally
        {
            foreach (var p in pages) p.Bitmap.Dispose();
        }
    }

    private static List<RenderedPage> Layout(List<DocBlock> blocks, float pwPt, float phPt, float marginPt,
        float fontSizePt, float lineSpacing, int family)
    {
        int W = (int)Math.Round(pwPt * S), H = (int)Math.Round(phPt * S);
        float margin = marginPt * S;
        float contentW = W - 2 * margin;
        var pages = new List<RenderedPage>();
        SKBitmap? bmp = null;
        SKCanvas? canvas = null;
        List<OcrLine>? lines = null;
        float y = 0;

        void NewPage()
        {
            canvas?.Dispose();
            bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            lines = new List<OcrLine>();
            pages.Add(new RenderedPage(bmp, lines));
            y = margin;
        }

        NewPage();
        foreach (var block in blocks)
        {
            var face = Face(family, block.Bold, block.Italic);
            using var paint = new SKPaint
            {
                Typeface = face,
                TextSize = fontSizePt * block.SizeFactor * S,
                IsAntialias = true,
                Color = block.Muted ? new SKColor(0x47, 0x55, 0x69) : new SKColor(0x0F, 0x17, 0x2A),
            };
            float lineH = paint.TextSize * 1.22f * lineSpacing;
            var fm = paint.FontMetrics;
            float ascent = -fm.Ascent;

            foreach (string hard in block.Text.Replace("\r\n", "\n").Split('\n'))
            {
                var wrapped = Wrap(hard, paint, contentW);
                for (int li = 0; li < wrapped.Count; li++)
                {
                    if (y + lineH > H - margin) NewPage();
                    string line = wrapped[li];
                    bool lastOfPara = li == wrapped.Count - 1;
                    float width = paint.MeasureText(line);
                    float x = block.Align switch
                    {
                        1 => margin + (contentW - width) / 2,
                        2 => margin + contentW - width,
                        _ => margin,
                    };
                    float baseline = y + ascent;
                    if (block.Align == 3 && !lastOfPara && line.Contains(' '))
                    {
                        DrawJustified(canvas!, line, margin, baseline, contentW, paint);
                        width = contentW;
                    }
                    else if (line.Length > 0)
                    {
                        canvas!.DrawText(line, x, baseline, paint);
                    }
                    if (!string.IsNullOrWhiteSpace(line))
                        lines!.Add(new OcrLine(line, new CoreRect((int)x, (int)y, (int)Math.Max(1, width), (int)Math.Max(1, lineH))));
                    y += lineH;
                }
            }
            y += block.SpaceAfter * S;
        }
        canvas?.Dispose();
        return pages;
    }

    private static void DrawJustified(SKCanvas canvas, string line, float x, float baseline, float width, SKPaint paint)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) { canvas.DrawText(line, x, baseline, paint); return; }
        float total = words.Sum(w => paint.MeasureText(w));
        float gap = (width - total) / (words.Length - 1);
        if (gap > paint.TextSize * 1.5f) gap = paint.MeasureText(" ");
        float cx = x;
        foreach (var w in words)
        {
            canvas.DrawText(w, cx, baseline, paint);
            cx += paint.MeasureText(w) + gap;
        }
    }

    public static List<string> Wrap(string text, SKPaint paint, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) { result.Add(""); return result; }
        text = text.Replace("\t", "    ");
        var words = text.Split(' ');
        string current = "";
        foreach (string word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (paint.MeasureText(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }
            if (current.Length > 0) result.Add(current);
            // Kata yang lebih panjang dari baris: pecah per karakter.
            string rest = word;
            while (paint.MeasureText(rest) > maxWidth && rest.Length > 1)
            {
                int n = rest.Length - 1;
                while (n > 1 && paint.MeasureText(rest[..n]) > maxWidth) n--;
                result.Add(rest[..n]);
                rest = rest[n..];
            }
            current = rest;
        }
        result.Add(current);
        return result;
    }

    /// <summary>Tabel (spreadsheet) → PDF lanskap A4 siap cetak, dengan judul dan garis sel.</summary>
    public static void TableToPdf(string title, List<List<string>> rows, ISet<int> boldRows, string outPath, bool gridlines = true)
    {
        const float pwPt = 841.9f, phPt = 595.3f, marginPt = 36f;
        int W = (int)(pwPt * S), H = (int)(phPt * S);
        float margin = marginPt * S;
        int cols = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Count));

        var face = Face(0, false, false);
        var bold = Face(0, true, false);
        using var paint = new SKPaint { Typeface = face, TextSize = 9.5f * S, IsAntialias = true, Color = new SKColor(0x0F, 0x17, 0x2A) };
        using var boldPaint = new SKPaint { Typeface = bold, TextSize = 9.5f * S, IsAntialias = true, Color = new SKColor(0x0F, 0x17, 0x2A) };
        using var titlePaint = new SKPaint { Typeface = bold, TextSize = 14f * S, IsAntialias = true, Color = new SKColor(0x0F, 0x17, 0x2A) };
        using var linePaint = new SKPaint { Color = new SKColor(0xCB, 0xD5, 0xE1), StrokeWidth = 1, IsAntialias = false };
        using var headBg = new SKPaint { Color = new SKColor(0xF1, 0xF5, 0xF9) };

        // Lebar kolom proporsional dengan isi (dibatasi).
        float avail = W - 2 * margin;
        var natural = new float[cols];
        for (int c = 0; c < cols; c++)
            natural[c] = Math.Clamp(rows.Select(r => c < r.Count ? paint.MeasureText(r[c] ?? "") : 0).DefaultIfEmpty(0).Max() + 14 * S, 50 * S, 260 * S);
        float sum = natural.Sum();
        var widths = natural.Select(n => n / sum * avail).ToArray();

        float rowH = paint.TextSize * 1.8f;
        var pages = new List<RenderedPage>();
        SKBitmap? bmp = null;
        SKCanvas? canvas = null;
        List<OcrLine>? lines = null;
        float y = 0;

        void NewPage(bool first)
        {
            canvas?.Dispose();
            bmp = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            lines = new List<OcrLine>();
            pages.Add(new RenderedPage(bmp, lines));
            y = margin;
            if (first && !string.IsNullOrWhiteSpace(title))
            {
                canvas.DrawText(title, margin, y + titlePaint.TextSize, titlePaint);
                lines.Add(new OcrLine(title, new CoreRect((int)margin, (int)y, (int)titlePaint.MeasureText(title), (int)(titlePaint.TextSize * 1.3f))));
                y += titlePaint.TextSize * 2f;
            }
        }

        NewPage(true);
        for (int r = 0; r < rows.Count; r++)
        {
            if (y + rowH > H - margin) NewPage(false);
            bool isBold = boldRows.Contains(r);
            var p = isBold ? boldPaint : paint;
            float x = margin;
            if (isBold) canvas!.DrawRect(margin, y, avail, rowH, headBg);
            for (int c = 0; c < cols; c++)
            {
                string val = c < rows[r].Count ? rows[r][c] ?? "" : "";
                float cw = widths[c];
                string shown = val;
                while (shown.Length > 0 && p.MeasureText(shown) > cw - 10 * S) shown = shown[..^1];
                if (shown.Length < val.Length && shown.Length > 1) shown = shown[..^1] + "…";
                bool numeric = double.TryParse(val.Replace(".", "").Replace(",", ""), out _);
                float tx = numeric ? x + cw - 5 * S - p.MeasureText(shown) : x + 5 * S;
                float baseline = y + rowH / 2 + p.TextSize * 0.35f;
                if (shown.Length > 0)
                {
                    canvas!.DrawText(shown, tx, baseline, p);
                    lines!.Add(new OcrLine(val, new CoreRect((int)tx, (int)(y + rowH * 0.15f), (int)Math.Max(1, p.MeasureText(shown)), (int)(rowH * 0.7f))));
                }
                if (gridlines) canvas!.DrawLine(x, y, x, y + rowH, linePaint);
                x += cw;
            }
            if (gridlines)
            {
                canvas!.DrawLine(margin + avail, y, margin + avail, y + rowH, linePaint);
                canvas.DrawLine(margin, y, margin + avail, y, linePaint);
                canvas.DrawLine(margin, y + rowH, margin + avail, y + rowH, linePaint);
            }
            y += rowH;
        }
        canvas?.Dispose();
        WritePages(pages, outPath, pwPt, phPt, null);
    }
}
