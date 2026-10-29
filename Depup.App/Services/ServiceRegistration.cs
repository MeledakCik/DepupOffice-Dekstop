using Depup.App.ViewModels;
using Depup.App.Views;
using Depup.Core.Interfaces;
using Depup.Core.Services;
using Depup.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace Depup.App.Services;

public static class ServiceRegistration
{
    public static IServiceCollection AddDepupServices(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentService, DocumentService>();
        services.AddSingleton<IDocumentReader, DocumentReader>();
        services.AddSingleton<IPdfOperationService, PdfOperationService>();
        services.AddSingleton<IScanExportService, ScanExportService>();
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Depup PDF");
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "depup.db");
        services.AddDbContextFactory<DepupDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<IDocumentHistoryService, DocumentHistoryService>();
        services.AddTransient<DocumentViewerViewModel>();
        services.AddTransient<DocumentViewer>();
        services.AddSingleton<DocumentPickerService>();
        services.AddSingleton<PdfToolViewModel>();
        services.AddSingleton<ScanPageViewModel>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
