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
using OtimizadorPC.Infrastructure.Overclock;
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

        // Os argumentos são do próprio programa (ex.: --limpeza-silenciosa), não configuração do host
        _host = Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSerilog()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        // Chamado pela tarefa agendada: limpa sem abrir janela e sai
        if (e.Args.Contains(IAutoCleanupScheduler.SilentArgument, StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Log.Information("Limpeza automática iniciada pela tarefa agendada");
            try
            {
                await SilentCleanup.RunAsync(_host.Services.GetRequiredService<ICleanupService>(), _host.Services.GetRequiredService<ISettingsService>());
            }
            catch (Exception ex) { Log.Error(ex, "Falha na limpeza automática"); }
            Shutdown();
            return;
        }

        // A janela pode ir para a bandeja; o programa só fecha por "Sair" (ver MainWindow)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Se o programa fechou com o Modo Jogo ligado, devolve o plano de energia e as prioridades
        _host.Services.GetRequiredService<GameModeService>().RecoverAfterCrash();

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
        services.AddSingleton<IDiskScanner, OtimizadorPC.Infrastructure.DiskAnalysis.DiskScanner>();
        services.AddSingleton<IFileOperations, OtimizadorPC.Infrastructure.DiskAnalysis.FileOperations>();
        services.AddSingleton<IDuplicateScanner, OtimizadorPC.Infrastructure.DiskAnalysis.DuplicateScanner>();
        services.AddSingleton<IInstalledProgramsService, OtimizadorPC.Infrastructure.Programs.InstalledProgramsService>();
        services.AddSingleton<INetworkService, OtimizadorPC.Infrastructure.Network.NetworkService>();
        services.AddSingleton<IAutoCleanupScheduler, AutoCleanupScheduler>();
        services.AddSingleton<IBenchmarkService, BenchmarkService>();
        services.AddSingleton<IBenchmarkHistory, BenchmarkHistory>();
        services.AddSingleton<ICpuStress, CpuStress>();
        services.AddSingleton<IGpuTuner, NvidiaGpuTuner>();
        services.AddSingleton<ICpuPowerTuner, CpuPowerTuner>();

        // Serviços da interface
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SensorMonitor>();
        services.AddSingleton<SnapshotProvider>();
        services.AddSingleton<GameModeService>();

        // Janela e páginas
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MachineInfoViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<DinosizeViewModel>();
        services.AddSingleton<ProgramsViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<GameModeViewModel>();
        services.AddSingleton<OverclockViewModel>();
        services.AddSingleton<SettingsViewModel>();
    }

    private bool _showingError;
    private string? _lastError;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // A caixa de mensagem processa a fila de mensagens: um erro de layout se repetiria
        // dentro dela e abriria caixas em cascata. Mostra só uma por vez e não repete a mesma.
        if (_showingError || e.Exception.Message == _lastError) return;
        Log.Error(e.Exception, "Erro não tratado na interface");
        _showingError = true;
        _lastError = e.Exception.Message;
        try
        {
            System.Windows.MessageBox.Show(
                "Ocorreu um erro inesperado:\n\n" + e.Exception.Message + "\n\nOs detalhes foram salvos no log.",
                "OtimizadorPC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _showingError = false; }
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
