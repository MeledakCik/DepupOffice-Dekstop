using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Pustaka Tanda Tangan &amp; Stempel (port SignatureManager.kt Android): PNG transparan yang disimpan di folder privat
    /// aplikasi (%LocalAppData%\DepupOfficeDesktop\signatures) agar bisa dipakai ulang di PDF, nota, watermark &amp; Word.
    /// </summary>
    public static class SignatureLibrary
    {
        public static string Dir
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupOfficeDesktop", "signatures");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static List<string> List()
        {
            try { return new DirectoryInfo(Dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList(); }
            catch { return new List<string>(); }
        }

        public static string Save(string name, Bitmap bmp)
        {
            var clean = Regex.Replace(string.IsNullOrWhiteSpace(name) ? "TTD_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : name.Trim(), @"[^\w\- ]", "_");
            var path = Path.Combine(Dir, clean + ".png");
            int n = 2;
            while (File.Exists(path)) path = Path.Combine(Dir, $"{clean} ({n++}).png");
            bmp.Save(path, ImageFormat.Png);
            return path;
        }

        public static bool Delete(string path)
        {
            try { if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(Dir)) return false; File.Delete(path); return true; }
            catch { return false; }
        }

        /// <summary>Muat ke memori (berkas tidak terkunci sehingga tetap bisa dihapus).</summary>
        public static Bitmap? Load(string path)
        {
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(path));
                using var raw = Image.FromStream(ms);
                return new Bitmap(raw);
            }
            catch { return null; }
        }

        /// <summary>Foto stempel/TTD di kertas: latar terang menjadi transparan, coretan dipertahankan.</summary>
        public static Bitmap MakeTransparent(Bitmap src) =>
            Image.IsAlphaPixelFormat(src.PixelFormat) ? new Bitmap(src) : PdfEngine.ExtractTransparentSignature(src);
    }
}
