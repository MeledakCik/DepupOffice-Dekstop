using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DepupOfficeDesktop.Core
{
    public record RecentEntry(string FilePath, string Name, string Mime, long TimestampMs);
    public record ShareEntry(string Name, string Target, long SizeBytes, long TimestampMs);
    public record LastJobEntry(string Title, string FileName, string FilePath);

    public class AppConfigData
    {
        public List<RecentEntry> RecentFiles { get; set; } = new();
        public HashSet<string> Bookmarks { get; set; } = new();
        public List<ShareEntry> Shares { get; set; } = new();
        public LastJobEntry? LastJob { get; set; }

        // DocSettings (Util.kt)
        public int DocFontFamily { get; set; } = 0; // 0 = Sans (Calibri/Segoe), 1 = Times New Roman
        public float DocFontSize { get; set; } = 12f;
        public int DocMarginMode { get; set; } = 0; // 0 = Normal (2.5cm), 1 = Sempit (1.2cm), 2 = Lebar (3.8cm)
        public float DocLineSpacing { get; set; } = 1.15f;
        public string DocPageSize { get; set; } = "A4"; // A4, Letter, F4

        // SheetSettings (Util.kt)
        public bool SheetShowGridlines { get; set; } = true;
        public string SheetCurrencySymbol { get; set; } = "Rp";
        public string SheetCsvSeparator { get; set; } = ",";
        public bool SheetAutoSum { get; set; } = true;

        // PdfSettings (Util.kt)
        public float PdfDefaultZoom { get; set; } = 1.0f;
        public int PdfReaderTheme { get; set; } = 0; // 0 = Siang, 1 = Malam, 2 = Sepia, 3 = Eye-Care
        public bool PdfSmoothScroll { get; set; } = true;

        // VaultSettings (VaultActivity.kt)
        public string? VaultSalt { get; set; }
        public string? VaultPinHash { get; set; }
        public string? VaultMasterKeyProtectedBase64 { get; set; }
    }

    /// <summary>
    /// Porting dari Util.kt (Files, Recent, Meta, DocSettings, SheetSettings, PdfSettings).
    /// Mengelola folder utama Documents\DepupOffice (termasuk subfolder Gambar & Salinan) dan konfigurasi lokal JSON.
    /// </summary>
    public static class AppStorage
    {
        public const string MIME_PDF = "application/pdf";
        public const string MIME_DOCX = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        public const string MIME_XLSX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        private static readonly HashSet<string> SupportedDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf", ".docx", ".xlsx", ".csv", ".jpg", ".jpeg", ".png", ".bmp"
        };

        public static string OutDir { get; private set; } = "";
        public static string PicturesDir { get; private set; } = "";
        public static string DownloadsDir { get; private set; } = "";
        public static string AppDataDir { get; private set; } = "";
        public static string VaultDir { get; private set; } = "";
        public static string HistoryDatabasePath => Path.Combine(AppDataDir, "depup_history.db");
        private static string ConfigFilePath => Path.Combine(AppDataDir, "depup_config.json");

        public static AppConfigData Config { get; private set; } = new();

        public static void Initialize()
        {
            // Known folders can point to an unavailable OneDrive/network location.
            // Never let that prevent the application from starting; use LocalAppData
            // (or the user temp directory as a last resort) when a folder is missing.
            string fallbackRoot = GetWritableRoot();
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            AppDataDir = EnsureDirectory(
                Path.Combine(string.IsNullOrWhiteSpace(localApp) ? fallbackRoot : localApp, "DepupOfficeDesktop"),
                Path.Combine(fallbackRoot, "DepupOfficeDesktop"));
            VaultDir = EnsureDirectory(Path.Combine(AppDataDir, "vault"), Path.Combine(fallbackRoot, "DepupOfficeDesktop", "vault"));

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            OutDir = EnsureDirectory(
                Path.Combine(string.IsNullOrWhiteSpace(docs) ? fallbackRoot : docs, "DepupOffice"),
                Path.Combine(fallbackRoot, "Documents", "DepupOffice"));

            // Semua hasil olahan disimpan di dalam Documents\DepupOffice (bukan Downloads/Pictures).
            PicturesDir = EnsureDirectory(
                Path.Combine(OutDir, "Gambar"),
                Path.Combine(fallbackRoot, "Documents", "DepupOffice", "Gambar"));

            DownloadsDir = EnsureDirectory(
                Path.Combine(OutDir, "Salinan"),
                Path.Combine(fallbackRoot, "Documents", "DepupOffice", "Salinan"));

            LoadConfig();
            LocalHistoryDatabase.Initialize(HistoryDatabasePath, Config);
            PrivacyManager.EnsurePrivacyFileExists();
        }

        public static bool IsSupportedDocument(string filePath)
            => SupportedDocumentExtensions.Contains(Path.GetExtension(filePath));

        public static string UnsupportedDocumentMessage(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".doc" => "Format Word .doc belum didukung. Simpan atau ekspor dokumen sebagai .docx, lalu buka kembali.",
                ".xls" => "Format Excel .xls belum didukung. Simpan dokumen sebagai .xlsx atau .csv, lalu buka kembali.",
                _ => $"Format { (string.IsNullOrEmpty(extension) ? "berkas ini" : extension) } belum didukung. Pilih PDF, DOCX, XLSX, CSV, JPG, atau PNG."
            };
        }

        private static string GetWritableRoot()
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localApp))
            {
                try
                {
                    Directory.CreateDirectory(localApp);
                    return localApp;
                }
                catch { }
            }

            return Path.GetTempPath();
        }

        private static string EnsureDirectory(string preferred, string fallback)
        {
            try
            {
                Directory.CreateDirectory(preferred);
                return preferred;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }

            Directory.CreateDirectory(fallback);
            return fallback;
        }

        public static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var loaded = JsonSerializer.Deserialize<AppConfigData>(json);
                    if (loaded != null) Config = loaded;
                }
            }
            catch
            {
                Config = new AppConfigData();
            }
        }

        public static void SaveConfig()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(Config, opts));
            }
            catch { }
        }

        public static string MimeByName(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant().TrimStart('.');
            return ext switch
            {
                "pdf" => MIME_PDF,
                "docx" or "doc" => MIME_DOCX,
                "xlsx" or "xls" => MIME_XLSX,
                "csv" => "text/csv",
                "txt" or "md" => "text/plain",
                "jpg" or "jpeg" => "image/jpeg",
                "png" => "image/png",
                "bmp" => "image/bmp",
                _ => "application/octet-stream"
            };
        }

        public static (string Label, System.Drawing.Color Fg, System.Drawing.Color Bg) TypeBadgeOf(string mime, string name)
        {
            string n = name.ToLowerInvariant();
            if (mime == MIME_PDF || n.EndsWith(".pdf"))
                return ("PDF", Theme.RED, Theme.RED_BG);
            if (mime.Contains("word") || n.EndsWith(".doc") || n.EndsWith(".docx"))
                return ("DOCX", Theme.ACCENT, Theme.BLUE_BG);
            if (mime.Contains("sheet") || mime.Contains("excel") || n.EndsWith(".xls") || n.EndsWith(".xlsx") || n.EndsWith(".csv"))
                return ("XLSX", Theme.TEAL, Theme.TEAL_BG);
            if (mime.StartsWith("image/") || n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".png"))
                return ("IMG", Theme.SKY, Theme.SKY_BG);
            string ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "FILE";
            if (ext.Length > 4) ext = ext.Substring(0, 4);
            return (ext, Theme.INDIGO, Theme.INDIGO_BG);
        }

        public static string BaseName(string name)
        {
            string b = Path.GetFileNameWithoutExtension(name);
            b = Regex.Replace(b, @"[^A-Za-z0-9_\-]+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(b) ? "file" : b;
        }

        public static string NewOut(string baseName, string ext)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            return Path.Combine(OutDir, $"{baseName}_{stamp}.{ext.TrimStart('.')}");
        }

        public static string NamedOut(string name, string ext)
        {
            string safe = Regex.Replace(name, @"[^\p{L}\p{N} _.\-]+", "_").Trim();
            if (string.IsNullOrWhiteSpace(safe)) safe = "dokumen";
            string cleanExt = ext.TrimStart('.');
            string candidate = Path.Combine(OutDir, $"{safe}.{cleanExt}");
            return File.Exists(candidate) ? NewOut(safe, cleanExt) : candidate;
        }

        public static string? RenameFile(string oldPath, string newName)
        {
            if (!File.Exists(oldPath)) return null;
            string clean = Regex.Replace(newName.Trim(), @"[\/:*?""<>|]+", "_");
            if (string.IsNullOrWhiteSpace(clean)) return null;
            string ext = Path.GetExtension(oldPath).TrimStart('.');
            string targetName = (string.IsNullOrEmpty(ext) || clean.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
                ? clean
                : $"{clean}.{ext}";
            string newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? OutDir, targetName);
            try
            {
                File.Move(oldPath, newPath, false);
                RemoveRecent(oldPath);
                AddRecent(newPath);
                return newPath;
            }
            catch
            {
                return null;
            }
        }

        public static List<FileInfo> GetOutFiles()
        {
            try
            {
                var dir = new DirectoryInfo(OutDir);
                if (!dir.Exists) return new List<FileInfo>();
                return dir.GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        public static long GetDirectorySize(string dirPath)
        {
            try
            {
                var d = new DirectoryInfo(dirPath);
                if (!d.Exists) return 0L;
                return d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                return 0L;
            }
        }

        public static (long AvailableBytes, long TotalBytes) GetDeviceStorage()
        {
            try
            {
                string root = Path.GetPathRoot(OutDir) ?? "C:\\";
                var drive = new DriveInfo(root);
                return (drive.AvailableFreeSpace, drive.TotalSize);
            }
            catch
            {
                return (50L * 1024 * 1024 * 1024, 256L * 1024 * 1024 * 1024);
            }
        }

        public static string FmtSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes / 1048576.0:F1} MB";
        }

        public static string FmtGb(long bytes) => $"{bytes / 1073741824.0:F1} GB";

        public static string RelTime(long timestampMs)
        {
            if (timestampMs <= 0) return "";
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long diffMin = Math.Max(0, (nowMs - timestampMs) / 60000);
            long diffHour = diffMin / 60;
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).LocalDateTime;
            var now = DateTime.Now;
            int dayDiff = (now.Date - dt.Date).Days;
            if (diffMin < 1) return "Baru saja";
            if (diffMin < 60) return $"{diffMin} menit lalu";
            if (diffHour < 24 && dayDiff == 0) return $"{diffHour} jam lalu";
            if (dayDiff == 1) return "Kemarin, " + dt.ToString("HH:mm");
            if (dayDiff >= 2 && dayDiff <= 6) return $"{dayDiff} hari lalu";
            return dt.ToString("d MMM yyyy", new CultureInfo("id-ID"));
        }

        public static void AddRecent(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
            string name = Path.GetFileName(filePath);
            string mime = MimeByName(name);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Config.RecentFiles.RemoveAll(r => string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            Config.RecentFiles.Insert(0, new RecentEntry(filePath, name, mime, now));
            if (Config.RecentFiles.Count > 20)
                Config.RecentFiles = Config.RecentFiles.Take(20).ToList();
            SaveConfig();
        }

        public static void RemoveRecent(string filePath)
        {
            Config.RecentFiles.RemoveAll(r => string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        public static bool IsBookmarked(string filePath)
            => Config.Bookmarks.Contains(filePath);

        public static bool ToggleBookmark(string filePath)
        {
            bool added;
            if (Config.Bookmarks.Contains(filePath))
            {
                Config.Bookmarks.Remove(filePath);
                added = false;
            }
            else
            {
                Config.Bookmarks.Add(filePath);
                added = true;
            }
            SaveConfig();
            return added;
        }

        public static void LogShare(string name, string target, long sizeBytes)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Config.Shares.Insert(0, new ShareEntry(name, target, sizeBytes, now));
            if (Config.Shares.Count > 10)
                Config.Shares = Config.Shares.Take(10).ToList();
            SaveConfig();
        }

        public static void SetLastJob(string title, string filePath)
        {
            Config.LastJob = new LastJobEntry(title, Path.GetFileName(filePath), filePath);
            SaveConfig();
        }

        public static string SaveToDownloads(string srcPath) => SaveCopy(srcPath);

        /// <summary>Simpan salinan berkas ke Documents\DepupOffice\Salinan.</summary>
        public static string SaveCopy(string srcPath)
        {
            string dst = Path.Combine(DownloadsDir, Path.GetFileName(srcPath));
            if (File.Exists(dst))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                dst = Path.Combine(DownloadsDir, $"{Path.GetFileNameWithoutExtension(srcPath)}_{stamp}{Path.GetExtension(srcPath)}");
            }
            File.Copy(srcPath, dst, true);
            return dst;
        }
    }
}
