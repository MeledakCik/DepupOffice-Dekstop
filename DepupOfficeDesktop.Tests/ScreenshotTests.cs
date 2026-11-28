using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using DepupOfficeDesktop.Controls;
using DepupOfficeDesktop.Core;
using DepupOfficeDesktop.Forms;
using Xunit;

namespace DepupOfficeDesktop.Tests;

/// <summary>
/// Potret tampilan untuk dibandingkan dengan aplikasi Android (hanya berjalan bila DEPUP_SHOTS diisi, mis. di CI).
/// Juga memastikan SEMUA ikon vektor Android bisa digambar (tidak kosong, tidak melempar).
/// </summary>
public sealed class ScreenshotTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("DEPUP_SHOTS");

    [Fact]
    public void EveryAndroidIconRenders()
    {
        var names = typeof(DepupIcons).GetField("Data", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as System.Collections.IDictionary;
        Assert.NotNull(names);
        Assert.True(names!.Count >= 80);
        const int cell = 72, cols = 10;
        int rows = (names.Count + cols - 1) / cols;
        using var sheet = new Bitmap(cols * cell, rows * (cell + 18));
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(0xF1, 0xF0, 0xEC));
        int i = 0;
        foreach (string name in names.Keys)
        {
            using var bmp = DepupIcons.Render(name, 48, Color.FromArgb(0x11, 0x11, 0x11));
            int ink = 0;
            for (int y = 0; y < 48; y += 2) for (int x = 0; x < 48; x += 2) if (bmp.GetPixel(x, y).A > 40) ink++;
            Assert.True(ink > 8, $"ikon {name} kosong");
            int cx = (i % cols) * cell, cy = (i / cols) * (cell + 18);
            g.DrawImage(bmp, cx + 12, cy + 6);
            using var f = new Font("Segoe UI", 7f);
            g.DrawString(name, f, Brushes.DimGray, cx + 2, cy + 56);
            i++;
        }
        if (Dir is { } d) { Directory.CreateDirectory(d); sheet.Save(Path.Combine(d, "icons.png"), ImageFormat.Png); }
    }

    [Fact]
    public void MainWindowTabs()
    {
        if (Dir is not { } dir) return;
        Directory.CreateDirectory(dir);
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                AppStorage.Initialize();
                Theme.Init();
                foreach (var (mode, tag) in new[] { (Theme.MODE_LIGHT, "terang"), (Theme.MODE_DARK, "gelap") })
                {
                    Theme.SetMode(mode);
                    using var f = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1280, 860) };
                    f.Show();
                    var select = typeof(MainForm).GetMethod("SelectTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    for (int tab = 0; tab < 5; tab++)
                    {
                        select.Invoke(f, new object[] { tab, true });
                        for (int k = 0; k < 30; k++) { Application.DoEvents(); Thread.Sleep(20); }
                        using var bmp = new Bitmap(f.Width, f.Height);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Path.Combine(dir, $"tab{tab}_{tag}.png"), ImageFormat.Png);
                    }
                    // jendela sempit (dibagi dua layar / laptop kecil): navigasi ringkas & tata letak bertumpuk
                    f.Size = new Size(820, 720);
                    for (int tab = 0; tab < 5; tab++)
                    {
                        select.Invoke(f, new object[] { tab, true });
                        for (int k = 0; k < 30; k++) { Application.DoEvents(); Thread.Sleep(20); }
                        using var bmp = new Bitmap(f.Width, f.Height);
                        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                        bmp.Save(Path.Combine(dir, $"sempit_tab{tab}_{tag}.png"), ImageFormat.Png);
                    }
                    f.Close();
                }
                using (var nf = new NotesForm { Size = new Size(980, 720) })
                {
                    nf.Show();
                    for (int k = 0; k < 20; k++) { Application.DoEvents(); Thread.Sleep(20); }
                    using var bmp = new Bitmap(nf.Width, nf.Height);
                    nf.DrawToBitmap(bmp, new Rectangle(0, 0, nf.Width, nf.Height));
                    bmp.Save(Path.Combine(dir, "catatan.png"), ImageFormat.Png);
                    nf.Close();
                }
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) File.WriteAllText(Path.Combine(dir, "error.txt"), err.ToString());
    }
}
