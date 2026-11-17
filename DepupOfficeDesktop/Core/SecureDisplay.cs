using System;
using System.Runtime.InteropServices;

namespace DepupOfficeDesktop.Core
{
    /// <summary>
    /// Padanan FLAG_SECURE Android untuk Windows: jendela dikecualikan dari tangkapan layar, rekaman layar,
    /// berbagi layar (Teams/Zoom/Meet) dan alat "Snipping" — yang terekam hanya bidang hitam/kosong.
    /// Windows 10 2004+ memakai WDA_EXCLUDEFROMCAPTURE; versi lebih lama jatuh ke WDA_MONITOR (hitam).
    /// </summary>
    public static class SecureDisplay
    {
        private const uint WDA_NONE = 0x0, WDA_MONITOR = 0x1, WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        /// <summary>Aktif/nonaktifkan perlindungan tangkapan layar untuk jendela tingkat-atas <paramref name="hwnd"/>.</summary>
        public static bool Protect(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
            try
            {
                if (!on) return SetWindowDisplayAffinity(hwnd, WDA_NONE);
                return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) || SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
            }
            catch { return false; }
        }
    }
}
