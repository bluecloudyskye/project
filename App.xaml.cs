// ============================================================
// App.xaml.cs
// Entry point. Configures DI container (Microsoft.Extensions.DI),
// EF Core SQLite, and all services.
// ============================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WorkSpaceApp.Core.Services;
using WorkSpaceApp.Features.Database.ViewModels;
using WorkSpaceApp.Features.Notes.ViewModels;
using WorkSpaceApp.Features.Reminders.ViewModels;
using WorkSpaceApp.Features.Sync.ViewModels;
using WorkSpaceApp.Infrastructure.Data;

namespace WorkSpaceApp;

public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _mainWindow;

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window MainWindowInstance { get; private set; }

    public App()
    {
        this.InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        this.UnhandledException += (sender, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"=== UNHANDLED EXCEPTION ===");
            System.Diagnostics.Debug.WriteLine($"Message: {e.Message}");
            System.Diagnostics.Debug.WriteLine($"Exception: {e.Exception}");
            e.Handled = true; // предотвращает краш
        };

        _host = BuildHost();
        Services = _host.Services;

        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Если миграций нет или БД рассинхронизирована — просто создаём схему
            await db.Database.EnsureCreatedAsync();
        }

        _mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindowInstance = _mainWindow;
        _mainWindow.Activate();
    }

    private static IHost BuildHost() =>
        Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // ?? EF Core / SQLite ?????????????????????????????
                services.AddDbContext<AppDbContext>(opt =>
                    opt.UseSqlite($"Data Source={AppDbContext.GetDatabaseFilePath()}"));
                
                // ?? Core services ????????????????????????????????
                services.AddSingleton<IHardwareIdService, HardwareIdService>();
                services.AddSingleton<IStorageMonitorService, StorageMonitorService>();
                services.AddSingleton<ISignalRService, SignalRService>();

                // ?? ViewModels (transient = new instance per page) ?
                services.AddTransient<NoteEditorViewModel>();
                services.AddTransient<DatabaseViewModel>();
                services.AddTransient<CollaborationViewModel>();
                services.AddTransient<RemindersViewModel>(); // <-- ?????? ??? ??????

                // ?? Shell ????????????????????????????????????????
                services.AddSingleton<MainWindow>();

            })

            .Build();

}