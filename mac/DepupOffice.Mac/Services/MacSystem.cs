using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupOffice.Mac.Services;

/// <summary>Integrasi macOS: Finder, Preview, browser, Trash, dan suara (perintah `say`).</summary>
public static class MacSystem
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    public static (int Code, string Out, string Err) Exec(string file, IEnumerable<string> args, int timeoutMs = 120_000, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Tidak bisa menjalankan {file}");
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, outTask.IsCompleted ? outTask.Result : "", "Waktu habis");
        }
        return (p.ExitCode, outTask.Result, errTask.Result);
    }

    private static void Fire(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { }
    }

    /// <summary>Buka berkas/URL dengan aplikasi bawaan macOS.</summary>
    public static void Open(string pathOrUrl)
    {
        if (IsMac) Fire("open", pathOrUrl);
        else
        {
            try { Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true }); } catch { }
        }
    }

    public static void OpenWithPreview(string path)
    {
        if (IsMac) Fire("open", "-a", "Preview", path);
        else Open(path);
    }

    /// <summary>Tampilkan berkas di Finder (terpilih).</summary>
    public static void Reveal(string path)
    {
        if (IsMac)
        {
            if (Directory.Exists(path)) Fire("open", path);
            else Fire("open", "-R", path);
        }
        else Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
    }

    /// <summary>Pindahkan ke Trash (bisa dikembalikan dari Finder). Mengembalikan false bila gagal.</summary>
    public static bool MoveToTrash(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return false;
        if (IsMac)
        {
            try
            {
                string escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var r = Exec("osascript", new[] { "-e", $"tell application \"Finder\" to delete POSIX file \"{escaped}\"" }, 20_000);
                if (r.Code == 0 && !File.Exists(path) && !Directory.Exists(path)) return true;
            }
            catch { }
            try
            {
                string trash = Path.Combine(DepupOfficeDesktop.Core.MacPaths.Home, ".Trash");
                string target = Path.Combine(trash, Path.GetFileName(path));
                if (File.Exists(target) || Directory.Exists(target))
                    target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(path)} {DateTime.Now:HHmmss}{Path.GetExtension(path)}");
                if (Directory.Exists(path)) Directory.Move(path, target);
                else File.Move(path, target);
                return true;
            }
            catch { }
        }
        return false;
    }

    // ================= Suara (Voice Reader) =================
    public sealed record Voice(string Name, string Locale)
    {
        public override string ToString() => $"{Name} ({Locale.Replace('_', '-')})";
    }

    private static List<Voice>? _voices;

    public static List<Voice> Voices()
    {
        if (_voices != null) return _voices;
        var list = new List<Voice>();
        if (IsMac)
        {
            try
            {
                var r = Exec("say", new[] { "-v", "?" }, 15_000);
                foreach (var line in r.Out.Split('\n'))
                {
                    var m = Regex.Match(line, @"^(.+?)\s+([a-z]{2,3}_[A-Z0-9]{2,3})\s+#");
                    if (m.Success) list.Add(new Voice(m.Groups[1].Value.Trim(), m.Groups[2].Value));
                }
            }
            catch { }
        }
        // Bahasa Indonesia dulu, lalu Inggris, lalu sisanya.
        _voices = list
            .OrderBy(v => v.Locale.StartsWith("id") ? 0 : v.Locale.StartsWith("en") ? 1 : 2)
            .ThenBy(v => v.Name)
            .ToList();
        return _voices;
    }

    public static bool CanSpeak => IsMac;

    private static string? _hardwareId;

    /// <summary>Hardware UUID Mac (IOPlatformUUID) — ID perangkat yang stabil untuk kunci brankas.</summary>
    public static string? HardwareId()
    {
        if (_hardwareId != null) return _hardwareId.Length == 0 ? null : _hardwareId;
        _hardwareId = "";
        if (!IsMac) return null;
        try
        {
            var r = Exec("ioreg", new[] { "-rd1", "-c", "IOPlatformExpertDevice" }, 10_000);
            var m = Regex.Match(r.Out, "\"IOPlatformUUID\"\\s*=\\s*\"([0-9A-Fa-f-]{20,})\"");
            if (m.Success) _hardwareId = "mac-hw:" + m.Groups[1].Value.ToUpperInvariant();
        }
        catch { }
        return _hardwareId.Length == 0 ? null : _hardwareId;
    }
}

/// <summary>Pembaca suara offline memakai suara bawaan macOS (`say`), pengganti Windows SAPI.</summary>
public sealed class Speaker : IDisposable
{
    private Process? _proc;
    private string? _tempFile;

    public bool IsSpeaking => _proc is { HasExited: false };

    public event Action? Finished;

    public void Speak(string text, MacSystem.Voice? voice, double speed)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(text) || !MacSystem.CanSpeak) return;
        _tempFile = Path.Combine(Path.GetTempPath(), $"depup_tts_{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, text, Encoding.UTF8);
        var psi = new ProcessStartInfo("say") { UseShellExecute = false, CreateNoWindow = true };
        if (voice != null) { psi.ArgumentList.Add("-v"); psi.ArgumentList.Add(voice.Name); }
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add(((int)Math.Round(180 * Math.Clamp(speed, 0.5, 2.5))).ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(_tempFile);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => Finished?.Invoke();
        p.Start();
        _proc = p;
    }

    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
        _proc = null;
        try { if (_tempFile != null && File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
        _tempFile = null;
    }

    public void Dispose() => Stop();
}
