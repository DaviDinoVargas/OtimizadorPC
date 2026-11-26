using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OtimizadorPC.Setup.Engine;

namespace OtimizadorPC.Setup;

public enum SetupPage { BoasVindas, Licenca, Opcoes, Progresso, Concluido, ConfirmarRemocao, Erro }

public sealed partial class StepItem : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    [ObservableProperty] private StepState _state = StepState.Pendente;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private double _percent;
}

public sealed partial class SetupViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public SetupViewModel(bool uninstall)
    {
        IsUninstall = uninstall;
        Version = InstallerEngine.Version;
        var existing = InstallerEngine.InstalledDir();
        IsUpdate = !uninstall && existing is not null;
        _installDir = existing ?? InstallerEngine.DefaultDir;
        RuntimeNeeded = !InstallerEngine.IsRuntimeInstalled();
        SensorDriverInstalled = InstallerEngine.IsPawnIoInstalled();
        _page = uninstall ? SetupPage.ConfirmarRemocao : SetupPage.BoasVindas;
        LicenseText = InstallerEngine.License();
    }

    public bool IsUninstall { get; }
    public bool IsUpdate { get; }
    public string Version { get; }
    public string LicenseText { get; }
    public bool RuntimeNeeded { get; }
    public bool SensorDriverInstalled { get; }
    public bool HasPayload => InstallerEngine.HasPayload;

    [ObservableProperty] private SetupPage _page;
    [ObservableProperty] private string _installDir;
    [ObservableProperty] private bool _licenseAccepted;
    [ObservableProperty] private bool _desktopShortcut = true;
    [ObservableProperty] private bool _startMenuShortcut = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _installSensorDriver = true;
    [ObservableProperty] private bool _launchAfter = true;
    [ObservableProperty] private bool _removeUserData;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _overallProgress;

    public ObservableCollection<StepItem> Steps { get; } = [];

    public string WelcomeTitle => IsUpdate ? "Atualizar o OtimizadorPC" : "Bem-vindo ao OtimizadorPC";
    public string WelcomeText => IsUpdate
        ? $"Uma instalação foi encontrada em {InstallDir}. Ela será atualizada para a versão {Version}; suas configurações são mantidas."
        : "Limpeza, otimização, análise de disco (Dinosize), informações do hardware e overclock em um só lugar.";
    public string RuntimeText => RuntimeNeeded
        ? "O .NET 10 Desktop Runtime (cerca de 60 MB) será baixado e instalado automaticamente."
        : "Todos os pré-requisitos já estão instalados.";

    public bool CanGoBack => Page is SetupPage.Licenca or SetupPage.Opcoes;
    public string NextText => Page switch
    {
        SetupPage.Opcoes => IsUpdate ? "Atualizar" : "Instalar",
        SetupPage.ConfirmarRemocao => "Desinstalar",
        SetupPage.Concluido or SetupPage.Erro => "Concluir",
        _ => "Próximo",
    };
    public bool CanGoNext => Page switch
    {
        SetupPage.Licenca => LicenseAccepted,
        SetupPage.Opcoes => !string.IsNullOrWhiteSpace(InstallDir) && HasPayload,
        SetupPage.Progresso => false,
        _ => true,
    };

    partial void OnPageChanged(SetupPage value) => RefreshButtons();
    partial void OnLicenseAcceptedChanged(bool value) => RefreshButtons();
    partial void OnInstallDirChanged(string value) => RefreshButtons();

    private void RefreshButtons()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextText));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Page = Page == SetupPage.Opcoes ? SetupPage.Licenca : SetupPage.BoasVindas;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Page)
        {
            case SetupPage.BoasVindas: Page = SetupPage.Licenca; break;
            case SetupPage.Licenca: Page = SetupPage.Opcoes; break;
            case SetupPage.Opcoes: await InstallAsync(); break;
            case SetupPage.ConfirmarRemocao: await UninstallAsync(); break;
            case SetupPage.Concluido:
            case SetupPage.Erro:
                Finish();
                break;
        }
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Escolha onde instalar o OtimizadorPC" };
        if (dialog.ShowDialog() == true)
            InstallDir = dialog.FolderName.EndsWith(InstallerEngine.AppName, StringComparison.OrdinalIgnoreCase)
                ? dialog.FolderName
                : Path.Combine(dialog.FolderName, InstallerEngine.AppName);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Page == SetupPage.Progresso)
        {
            _cts?.Cancel();
            return;
        }
        Application.Current.Shutdown();
    }

    private async Task InstallAsync()
    {
        Steps.Clear();
        Steps.Add(new StepItem { Id = "runtime", Title = "Pré-requisitos (.NET 10)" });
        Steps.Add(new StepItem { Id = "arquivos", Title = "Copiar arquivos" });
        Steps.Add(new StepItem { Id = "atalhos", Title = "Criar atalhos" });
        Steps.Add(new StepItem { Id = "inicializacao", Title = "Iniciar com o Windows" });
        Steps.Add(new StepItem { Id = "registro", Title = "Registrar em \"Aplicativos instalados\"" });
        Steps.Add(new StepItem { Id = "driver", Title = "Driver de sensores (temperaturas)" });
        await RunAsync(ct => InstallerEngine.InstallAsync(
            new InstallOptions(InstallDir, DesktopShortcut, StartMenuShortcut, StartWithWindows, InstallSensorDriver), Progress(), ct));
    }

    private async Task UninstallAsync()
    {
        Steps.Clear();
        Steps.Add(new StepItem { Id = "fechar", Title = "Fechar o OtimizadorPC" });
        Steps.Add(new StepItem { Id = "atalhos", Title = "Remover atalhos e inicialização" });
        Steps.Add(new StepItem { Id = "arquivos", Title = "Remover arquivos" });
        Steps.Add(new StepItem { Id = "registro", Title = "Remover de \"Aplicativos instalados\"" });
        Steps.Add(new StepItem { Id = "dados", Title = "Configurações e histórico" });
        await RunAsync(ct => InstallerEngine.UninstallAsync(RemoveUserData, Progress(), ct));
    }

    private IProgress<StepUpdate> Progress() => new Progress<StepUpdate>(u =>
    {
        var step = Steps.FirstOrDefault(s => s.Id == u.Id);
        if (step is null) return;
        step.State = u.State;
        step.Detail = u.Detail;
        step.Percent = (u.Percent ?? (u.State is StepState.Concluido or StepState.Ignorado ? 1 : 0)) * 100;
        var done = Steps.Count(s => s.State is StepState.Concluido or StepState.Ignorado or StepState.Erro);
        OverallProgress = (done + (step.State == StepState.Executando ? (u.Percent ?? 0.3) : 0)) / Steps.Count * 100;
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        _cts = new CancellationTokenSource();
        Page = SetupPage.Progresso;
        IsBusy = true;
        try
        {
            await action(_cts.Token);
            OverallProgress = 100;
            Page = SetupPage.Concluido;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Operação cancelada. Nada foi registrado; você pode rodar o instalador de novo quando quiser.";
            Page = SetupPage.Erro;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Page = SetupPage.Erro;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Finish()
    {
        if (!IsUninstall && Page == SetupPage.Concluido && LaunchAfter)
        {
            var exe = Path.Combine(InstallDir, InstallerEngine.ExeName);
            if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        Application.Current.Shutdown();
    }
}
