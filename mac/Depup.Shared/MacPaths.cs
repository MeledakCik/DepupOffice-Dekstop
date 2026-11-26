using System;
using System.IO;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Lokasi folder standar di macOS (juga aman di Linux/Windows).
    /// Catatan: di .NET 8 SpecialFolder.MyDocuments di macOS mengembalikan $HOME, jadi dibentuk manual.
    /// </summary>
    public static class MacPaths
    {
        public static string Home =>
            Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
                ? h
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string Documents => Path.Combine(Home, "Documents");

        public static string AppSupport =>
            OperatingSystem.IsMacOS()
                ? Path.Combine(Home, "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }
}
