using System;
using System.Drawing;
using System.Windows.Forms;

namespace DepupOfficeDesktop.Controls
{
    /// <summary>
    /// Transisi saat tema berganti terang ↔ gelap (port ThemeFade.kt Android 4.3): isi jendela dipotret apa adanya,
    /// dipasang di atas jendela, tampilan dibangun ulang dengan tema baru di baliknya, lalu potret memudar ±0,3 detik
    /// (ease-in-out ala iOS). Mengikuti setelan Windows: efek animasi dimatikan = langsung tanpa transisi.
    /// </summary>
    internal static class ThemeFade
    {
        private const int FadeMs = 300;

        private sealed class Veil : Form
        {
            public Veil()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                ControlBox = false;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000020 /* TRANSPARENT */; return cp; }
            }
        }

        public static void Run(Form form, Action rebuild)
        {
            bool canAnimate = SystemInformation.UIEffectsEnabled && form.Visible && form.WindowState != FormWindowState.Minimized
                              && Form.ActiveForm == form && form.ClientSize.Width > 0 && form.ClientSize.Height > 0;
            if (!canAnimate) { rebuild(); return; }
            Bitmap? shot = null;
            Veil? veil = null;
            try
            {
                var screen = form.RectangleToScreen(form.ClientRectangle);
                shot = new Bitmap(screen.Width, screen.Height);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
                veil = new Veil { Bounds = screen, BackgroundImage = shot, BackgroundImageLayout = ImageLayout.None, Opacity = 1 };
                veil.Show(form);
                veil.Update();
            }
            catch
            {
                veil?.Dispose(); shot?.Dispose();
                rebuild();
                return;
            }
            try { rebuild(); }
            finally
            {
                var started = Environment.TickCount64;
                var timer = new System.Windows.Forms.Timer { Interval = 15 };
                timer.Tick += (s, e) =>
                {
                    double t = Math.Clamp((Environment.TickCount64 - started) / (double)FadeMs, 0, 1);
                    double eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;   // ease-in-out
                    if (t >= 1 || veil.IsDisposed)
                    {
                        timer.Stop(); timer.Dispose();
                        if (!veil.IsDisposed) { veil.Close(); veil.Dispose(); }
                        shot.Dispose();
                        return;
                    }
                    veil.Opacity = 1 - eased;
                };
                timer.Start();
            }
        }
    }
}
