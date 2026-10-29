using System.Windows;
using Depup.App.Services;
using Depup.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Depup.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Depup.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        services.AddDepupServices();
        Services = services.BuildServiceProvider();
        using (var scope = Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DepupDbContext>().Database.EnsureCreated();
        }

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        if (e.Args.Length > 0)
            _ = mainWindow.ViewModel.HandleOpenRequestAsync(e.Args[0]);
        base.OnStartup(e);
    }
}
