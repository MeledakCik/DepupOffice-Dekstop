using System.Text;
using System.Text.Json;
using DepupOfficeDesktop.Core;
using CoreRect = DepupOfficeDesktop.Core.Rect;

namespace DepupOffice.Mac.Services;

/// <summary>
/// OCR & pemindai QR/barcode offline memakai Apple Vision (bawaan macOS) — pengganti Windows.Media.Ocr.
/// Helper Swift kecil dikompilasi sekali di Mac pengguna (butuh Xcode Command Line Tools).
/// </summary>
public static class VisionService
{
    private const int HelperVersion = 1;

    private const string SwiftSource = """
        import Foundation
        import Vision
        import AppKit

        func fail(_ msg: String) -> Never {
            FileHandle.standardError.write((msg + "\n").data(using: .utf8)!)
            exit(1)
        }

        let args = CommandLine.arguments
        guard args.count >= 3 else { fail("usage: depup-vision ocr|qr <image>") }
        let mode = args[1]
        guard let image = NSImage(contentsOfFile: args[2]) else { fail("Gambar tidak bisa dibaca") }
        var rect = NSRect(origin: .zero, size: image.size)
        guard let cg = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else { fail("Gambar tidak bisa didekode") }
        let handler = VNImageRequestHandler(cgImage: cg, options: [:])
        let w = Double(cg.width)
        let h = Double(cg.height)
        var out: [[String: Any]] = []

        if mode == "qr" {
            let req = VNDetectBarcodesRequest()
            do { try handler.perform([req]) } catch { fail("Vision: \(error)") }
            for o in req.results ?? [] {
                if let s = o.payloadStringValue {
                    out.append(["t": s, "k": o.symbology.rawValue])
                }
            }
        } else {
            let req = VNRecognizeTextRequest()
            req.recognitionLevel = .accurate
            req.usesLanguageCorrection = true
            if #available(macOS 13.0, *) { req.automaticallyDetectsLanguage = true }
            if #available(macOS 12.0, *) {
                let supported = (try? req.supportedRecognitionLanguages()) ?? []
                let want = ["id-ID", "en-US"].filter { supported.contains($0) }
                if !want.isEmpty { req.recognitionLanguages = want }
            }
            do { try handler.perform([req]) } catch { fail("Vision: \(error)") }
            for o in req.results ?? [] {
                guard let c = o.topCandidates(1).first else { continue }
                let b = o.boundingBox
                out.append([
                    "t": c.string,
                    "x": Int(b.minX * w),
                    "y": Int((1.0 - b.maxY) * h),
                    "w": Int(b.width * w),
                    "h": Int(b.height * h),
                ])
            }
        }
        let data = try! JSONSerialization.data(withJSONObject: out, options: [])
        FileHandle.standardOutput.write(data)
        """;

    private static string BinDir => Path.Combine(AppStorage.AppDataDir, "bin");
    private static string BinPath => Path.Combine(BinDir, $"depup-vision-v{HelperVersion}");

    public static bool IsReady => File.Exists(BinPath);

    /// <summary>Siapkan helper. Mengembalikan null bila siap, atau pesan kesalahan untuk pengguna.</summary>
    public static string? EnsureHelper()
    {
        if (!MacSystem.IsMac) return "OCR & pemindai QR memakai Apple Vision dan hanya tersedia di macOS.";
        if (File.Exists(BinPath)) return null;
        Directory.CreateDirectory(BinDir);
        string src = Path.Combine(BinDir, "depup-vision.swift");
        File.WriteAllText(src, SwiftSource, new UTF8Encoding(false));
        try
        {
            var r = MacSystem.Exec("xcrun", new[] { "swiftc", "-O", src, "-o", BinPath }, 300_000);
            if (r.Code != 0 || !File.Exists(BinPath))
            {
                var r2 = MacSystem.Exec("swiftc", new[] { "-O", src, "-o", BinPath }, 300_000);
                if (r2.Code != 0 || !File.Exists(BinPath))
                    return "Fitur ini butuh Xcode Command Line Tools (gratis dari Apple).\n\n" +
                           "Buka Terminal, jalankan:\n    xcode-select --install\n\nlalu coba lagi.\n\nDetail: " +
                           Trim(r.Err + "\n" + r2.Err);
            }
        }
        catch (Exception ex)
        {
            return "Gagal menyiapkan Apple Vision: " + ex.Message +
                   "\n\nPastikan Xcode Command Line Tools terpasang (Terminal: xcode-select --install).";
        }
        return null;
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] + "…" : s.Trim();

    private static JsonElement Run(string mode, string imagePath)
    {
        var r = MacSystem.Exec(BinPath, new[] { mode, imagePath }, 180_000);
        if (r.Code != 0) throw new InvalidOperationException("Apple Vision gagal: " + Trim(r.Err));
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Out) ? "[]" : r.Out);
        return doc.RootElement.Clone();
    }

    public static List<OcrLine> Ocr(string imagePath)
    {
        var lines = new List<OcrLine>();
        foreach (var e in Run("ocr", imagePath).EnumerateArray())
        {
            string t = e.GetProperty("t").GetString() ?? "";
            if (t.Length == 0) continue;
            lines.Add(new OcrLine(t, new CoreRect(
                e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(),
                Math.Max(1, e.GetProperty("w").GetInt32()), Math.Max(1, e.GetProperty("h").GetInt32()))));
        }
        // Urutkan atas → bawah, kiri → kanan (toleransi setengah tinggi baris).
        lines.Sort((a, b) =>
        {
            int tol = Math.Max(4, Math.Min(a.Box.Height, b.Box.Height) / 2);
            if (Math.Abs(a.Box.Y - b.Box.Y) > tol) return a.Box.Y.CompareTo(b.Box.Y);
            return a.Box.X.CompareTo(b.Box.X);
        });
        return lines;
    }

    public static List<string> ScanCodes(string imagePath)
        => Run("qr", imagePath).EnumerateArray()
            .Select(e => e.GetProperty("t").GetString() ?? "")
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>Gabungkan baris OCR menjadi paragraf (baris kosong saat jarak vertikal besar).</summary>
    public static string LinesToText(IReadOnlyList<OcrLine> lines)
    {
        var sb = new StringBuilder();
        OcrLine? prev = null;
        foreach (var ln in lines)
        {
            if (prev != null)
            {
                int gap = ln.Box.Y - (prev.Box.Y + prev.Box.Height);
                bool sameRow = Math.Abs(ln.Box.Y - prev.Box.Y) <= Math.Max(4, prev.Box.Height / 2);
                if (sameRow) sb.Append("   ");
                else if (gap > prev.Box.Height * 0.9) sb.Append("\n\n");
                else sb.Append('\n');
            }
            sb.Append(ln.Text);
            prev = ln;
        }
        return sb.ToString().Trim();
    }
}
