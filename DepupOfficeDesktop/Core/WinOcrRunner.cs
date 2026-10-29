using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using WinOcr = global::Windows.Media.Ocr;
using WinImaging = global::Windows.Graphics.Imaging;
using WinStreams = global::Windows.Storage.Streams;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// OCR offline sungguhan memakai mesin OCR bawaan Windows 10/11 (Windows.Media.Ocr, tanpa NuGet).
    /// Bahasa mengikuti bahasa profil pengguna Windows; bila tidak ada, dicoba Indonesia lalu Inggris.
    /// </summary>
    internal static class WinOcrRunner
    {
        private static WinOcr.OcrEngine? _engine;
        private static bool _engineTried;
        private static readonly object Gate = new();

        private static WinOcr.OcrEngine? GetEngine()
        {
            lock (Gate)
            {
                if (_engineTried) return _engine;
                _engineTried = true;

                try { _engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages(); } catch { }
                if (_engine != null) return _engine;

                foreach (string tag in new[] { "id", "en-US", "en" })
                {
                    try
                    {
                        var lang = new global::Windows.Globalization.Language(tag);
                        if (WinOcr.OcrEngine.IsLanguageSupported(lang))
                        {
                            _engine = WinOcr.OcrEngine.TryCreateFromLanguage(lang);
                            if (_engine != null) return _engine;
                        }
                    }
                    catch { }
                }
                return null;
            }
        }

        /// <summary>Jalankan OCR. Melempar exception berisi pesan yang ramah pengguna bila gagal.</summary>
        public static OcrPage Recognize(Bitmap src)
        {
            int origW = src.Width, origH = src.Height;
            if (origW < 40 || origH < 40)
                return new OcrPage("", new List<string>(), new List<OcrLine>(), origW, origH);

            // Mesin OCR Windows membatasi dimensi gambar; kecilkan bila perlu lalu skala balik kotaknya.
            double maxDim = 2600;
            try { maxDim = WinOcr.OcrEngine.MaxImageDimension; } catch { }
            double scale = Math.Min(1.0, maxDim / Math.Max(origW, origH));

            byte[] png;
            using (var ms = new MemoryStream())
            {
                if (scale < 1.0)
                {
                    int nw = Math.Max(1, (int)(origW * scale));
                    int nh = Math.Max(1, (int)(origH * scale));
                    using var scaled = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(scaled))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, nw, nh);
                    }
                    scaled.Save(ms, ImageFormat.Png);
                }
                else
                {
                    src.Save(ms, ImageFormat.Png);
                }
                png = ms.ToArray();
            }

            return Task.Run(() => RecognizeCore(png, scale, origW, origH)).GetAwaiter().GetResult();
        }

        private static OcrPage RecognizeCore(byte[] png, double scale, int origW, int origH)
        {
            var engine = GetEngine()
                ?? throw new InvalidOperationException(
                    "Mesin OCR Windows tidak tersedia. Tambahkan paket bahasa di Pengaturan > Waktu & bahasa > Bahasa & wilayah, lalu coba lagi.");

            using var ras = new WinStreams.InMemoryRandomAccessStream();
            ras.WriteAsync(png.AsBuffer()).AsTask().GetAwaiter().GetResult();
            ras.Seek(0);

            var decoder = WinImaging.BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            using var softBmp = decoder
                .GetSoftwareBitmapAsync(WinImaging.BitmapPixelFormat.Bgra8, WinImaging.BitmapAlphaMode.Premultiplied)
                .AsTask().GetAwaiter().GetResult();
            var result = engine.RecognizeAsync(softBmp).AsTask().GetAwaiter().GetResult();

            var lines = new List<OcrLine>();
            var texts = new List<string>();
            var paras = new List<string>();
            var cur = new List<string>();
            double prevBottom = double.NaN;
            double prevHeight = 0;

            foreach (var line in result.Lines)
            {
                string text = (line.Text ?? "").Trim();
                if (text.Length == 0 || line.Words.Count == 0) continue;

                double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    left = Math.Min(left, r.X);
                    top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width);
                    bottom = Math.Max(bottom, r.Y + r.Height);
                }

                double inv = scale > 0 ? 1.0 / scale : 1.0;
                var box = new Rectangle(
                    (int)Math.Round(left * inv),
                    (int)Math.Round(top * inv),
                    Math.Max(1, (int)Math.Round((right - left) * inv)),
                    Math.Max(1, (int)Math.Round((bottom - top) * inv)));
                lines.Add(new OcrLine(text, box));
                texts.Add(text);

                double height = bottom - top;
                if (!double.IsNaN(prevBottom) && cur.Count > 0 && (top - prevBottom) > Math.Max(height, prevHeight) * 0.9)
                {
                    paras.Add(string.Join(" ", cur));
                    cur.Clear();
                }
                cur.Add(text);
                prevBottom = bottom;
                prevHeight = height;
            }
            if (cur.Count > 0) paras.Add(string.Join(" ", cur));

            return new OcrPage(string.Join("\n", texts), paras, lines, origW, origH);
        }
    }
}
