using System.Runtime.InteropServices;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;
using SkiaSharp;

namespace DepupOffice.Mac.Services;

/// <summary>Utilitas gambar berbasis SkiaSharp (pengganti GDI+ System.Drawing di versi Windows).</summary>
public static class ImageUtil
{
    public static AvBitmap ToAvalonia(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream(data.ToArray());
        return new AvBitmap(ms);
    }

    public static byte[] EncodeJpeg(SKBitmap bmp, int quality = 85)
    {
        // JPEG tidak punya alfa: ratakan ke latar putih dulu.
        using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(flat))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(bmp, 0, 0);
        }
        using var image = SKImage.FromBitmap(flat);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 10, 100));
        return data.ToArray();
    }

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>BGRA mentah (stride = lebar × 4) ke SKBitmap.</summary>
    public static SKBitmap FromBgra(byte[] bgra, int width, int height)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int len = Math.Min(bgra.Length, width * height * 4);
        Marshal.Copy(bgra, 0, bmp.GetPixels(), len);
        return bmp;
    }

    /// <summary>Muat gambar dan terapkan orientasi EXIF (foto iPhone / kamera).</summary>
    public static SKBitmap Load(string path)
    {
        var codecOrNull = SKCodec.Create(path);
        if (codecOrNull == null && MacSystem.IsMac)
        {
            // HEIC/TIFF dll.: konversi dulu dengan `sips` bawaan macOS.
            string tmp = Path.Combine(Path.GetTempPath(), $"depup_img_{Guid.NewGuid():N}.png");
            try
            {
                MacSystem.Exec("sips", new[] { "-s", "format", "png", path, "--out", tmp }, 60_000);
                if (File.Exists(tmp)) return Load(tmp);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        using var codec = codecOrNull ?? throw new InvalidDataException($"Format gambar tidak didukung: {Path.GetFileName(path)}");
        var origin = codec.EncodedOrigin;
        var bmp = SKBitmap.Decode(codec) ?? throw new InvalidDataException($"Gagal membaca gambar: {Path.GetFileName(path)}");
        int deg = origin switch
        {
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (deg == 0) return bmp;
        var rotated = Rotate(bmp, deg);
        bmp.Dispose();
        return rotated;
    }

    public static SKBitmap Rotate(SKBitmap src, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        bool swap = degrees == 90 || degrees == 270;
        int w = swap ? src.Height : src.Width;
        int h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    /// <summary>Perkecil agar sisi terpanjang ≤ maxSide (tidak memperbesar).</summary>
    public static SKBitmap Fit(SKBitmap src, int maxSide)
    {
        int longest = Math.Max(src.Width, src.Height);
        if (longest <= maxSide) return src.Copy();
        double k = (double)maxSide / longest;
        int w = Math.Max(1, (int)Math.Round(src.Width * k));
        int h = Math.Max(1, (int)Math.Round(src.Height * k));
        return src.Resize(new SKImageInfo(w, h), SKFilterQuality.High) ?? src.Copy();
    }

    public static SKBitmap Scale(SKBitmap src, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * factor));
        int h = Math.Max(1, (int)Math.Round(src.Height * factor));
        return src.Resize(new SKImageInfo(w, h), SKFilterQuality.High) ?? src.Copy();
    }

    /// <summary>
    /// Tema kertas "Eye-Care" untuk pembaca: 0 Siang, 1 Malam (warna dibalik, latar #0F172A),
    /// 2 Sepia (#FBF0D9), 3 Hijau (#E2F0D9). Diolah per piksel agar hasilnya pasti.
    /// </summary>
    public static SKBitmap ApplyPaperTheme(SKBitmap src, int theme)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(src, 0, 0);
        }
        if (theme == 0) return dst;

        (int R, int G, int B) paper = theme switch
        {
            1 => (0x0F, 0x17, 0x2A),
            2 => (0xFB, 0xF0, 0xD9),
            _ => (0xE2, 0xF0, 0xD9),
        };
        (int R, int G, int B) ink = theme == 1 ? (0xE2, 0xE8, 0xF0) : (0x1F, 0x1B, 0x16);

        int len = dst.Width * dst.Height * 4;
        var px = new byte[len];
        IntPtr ptr = dst.GetPixels();
        Marshal.Copy(ptr, px, 0, len);
        for (int i = 0; i < len; i += 4)
        {
            // Luminans 0..255: 255 = kertas, 0 = tinta. Warna non-abu dipertahankan sebagian.
            int r = px[i], g = px[i + 1], b = px[i + 2];
            int lum = (r * 299 + g * 587 + b * 114) / 1000;
            int sat = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            int nr = ink.R + (paper.R - ink.R) * lum / 255;
            int ng = ink.G + (paper.G - ink.G) * lum / 255;
            int nb = ink.B + (paper.B - ink.B) * lum / 255;
            if (sat > 40 && theme != 1)
            {
                // Gambar berwarna: campur 70% warna asli agar tetap terbaca.
                nr = (r * 7 + nr * 3) / 10; ng = (g * 7 + ng * 3) / 10; nb = (b * 7 + nb * 3) / 10;
            }
            px[i] = (byte)nr; px[i + 1] = (byte)ng; px[i + 2] = (byte)nb; px[i + 3] = 255;
        }
        Marshal.Copy(px, 0, ptr, len);
        return dst;
    }
}
