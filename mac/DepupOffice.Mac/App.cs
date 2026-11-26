using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DepupOffice.Mac.Services;
using DepupOffice.Mac.Windows;
using DepupOfficeDesktop.Core;

namespace DepupOffice.Mac;

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "Depup Office";
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        // Huruf Funnel Display & latar abu batu Android di semua jendela
        var win = new Style(x => x.OfType<Avalonia.Controls.Window>());
        win.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, Ui.Font));
        win.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, Ui.B(Ui.Bg)));
        Styles.Add(win);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppStorage.Initialize();
        // Kunci brankas diikat ke Hardware UUID Mac (stabil, tidak berubah saat nama host berganti).
        VaultManager.DeviceIdProvider = MacSystem.HardwareId;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Jangan biarkan satu kesalahan tak terduga menutup aplikasi: tampilkan pesannya saja.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                var owner = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
                if (owner != null) _ = Dialogs.Error(owner, "Terjadi kesalahan tak terduga:\n" + e.Exception.Message);
            };
            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            // Berkas yang dioper lewat argumen (mis. `dotnet run -- dokumen.pdf`)
            foreach (var arg in desktop.Args ?? Array.Empty<string>())
                if (File.Exists(arg)) main.OpenDocument(arg);
            // Potret tampilan untuk pemeriksaan desain (DEPUP_SHOTS=folder): tiap halaman → PNG, lalu keluar.
            if (Environment.GetEnvironmentVariable("DEPUP_SHOTS") is { Length: > 0 } shots)
                main.Opened += async (_, _) =>
                {
                    Directory.CreateDirectory(shots);
                    foreach (var key in new[] { "baca", "tools", "buat", "simpan", "brankas" })
                    {
                        main.Navigate(key);
                        await Task.Delay(700);
                        var size = new Avalonia.PixelSize((int)main.Bounds.Width, (int)main.Bounds.Height);
                        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                        rtb.Render(main);
                        rtb.Save(Path.Combine(shots, $"mac_{key}.png"));
                    }
                    desktop.Shutdown();
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
