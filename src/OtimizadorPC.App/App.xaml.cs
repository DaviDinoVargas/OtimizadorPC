using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OtimizadorPC.App.Services;
using OtimizadorPC.App.ViewModels;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Infrastructure.Cleanup;
using OtimizadorPC.Infrastructure.Hardware;
using OtimizadorPC.Infrastructure.Optimization;
using OtimizadorPC.Infrastructure.Settings;
using Serilog;

namespace OtimizadorPC.App;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(JsonSettingsService.DataFolder, "logs", "otimizador-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += OnUnhandledException;

        _host = Host.CreateDefaultBuilder(e.Args)
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        _host.Services.GetRequiredService<ThemeService>().Initialize(window);
        MainWindow = window;
        window.Show();
        Log.Information("OtimizadorPC iniciado");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Infraestrutura
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IHardwareInfoService, WmiHardwareInfoService>();
        services.AddSingleton<ISensorService, LhmSensorService>();
        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IServiceTuner, ServiceTuner>();
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<ITweakService, TweakService>();
        services.AddSingleton<IPowerService, PowerService>();
        services.AddSingleton<IPageFileService, PageFileService>();
        services.AddSingleton<IAppxService, AppxService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<IProcessService, OtimizadorPC.Infrastructure.Processes.ProcessService>();

        // Serviços da interface
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SensorMonitor>();
        services.AddSingleton<SnapshotProvider>();

        // Janela e páginas
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MachineInfoViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<DinosizeViewModel>();
        services.AddSingleton<OverclockViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Erro não tratado na interface");
        System.Windows.MessageBox.Show(
            "Ocorreu um erro inesperado:\n\n" + e.Exception.Message + "\n\nOs detalhes foram salvos no log.",
            "OtimizadorPC", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}
