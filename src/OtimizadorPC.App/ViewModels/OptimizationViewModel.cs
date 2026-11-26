using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Diagnostics;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Hardware;
using OtimizadorPC.Core.Optimization;
using OtimizadorPC.Infrastructure.Hardware;
using OtimizadorPC.Infrastructure.Power;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

/// <summary>Base das abas da Otimização: carga sob demanda e mensagens de status.</summary>
public abstract partial class OptimizationTab : ObservableObject
{
    protected OptimizationTab(OptimizationContext ctx) => Ctx = ctx;

    protected OptimizationContext Ctx { get; }
    public abstract string Title { get; }
    public abstract Wpf.Ui.Controls.SymbolRegular Icon { get; }
    public bool IsAdministrator => Ctx.IsAdministrator;

    [ObservableProperty] private bool _isBusy;
    private bool _loaded;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await ReloadAsync();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsBusy = true;
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao carregar aba {Aba}", Title);
            Ctx.Report($"Não foi possível carregar \"{Title}\": {ex.Message}", true);
        }
        finally { IsBusy = false; }
    }

    protected abstract Task LoadAsync();
}

/// <summary>Dependências compartilhadas pelas abas.</summary>
public sealed class OptimizationContext
{
    public required IStartupService Startup { get; init; }
    public required IServiceTuner Services { get; init; }
    public required IScheduledTaskService Tasks { get; init; }
    public required ITweakService Tweaks { get; init; }
    public required IPowerService Power { get; init; }
    public required IPageFileService PageFile { get; init; }
    public required IAppxService Apps { get; init; }
    public required IRestorePointService RestorePoints { get; init; }
    public required IDialogService Dialogs { get; init; }
    public required SnapshotProvider Snapshots { get; init; }
    public bool IsAdministrator { get; } = WmiHardwareInfoService.IsAdministrator();
    public required Action<string, bool> Report { get; init; }
}

// =====================================================================
// Página
// =====================================================================

public sealed partial class OptimizationViewModel : PageViewModel
{
    public OptimizationViewModel(IStartupService startup, IServiceTuner services, IScheduledTaskService tasks, ITweakService tweaks,
        IPowerService power, IPageFileService pageFile, IAppxService apps, IRestorePointService restorePoints,
        IDialogService dialogs, SnapshotProvider snapshots)
    {
        var ctx = new OptimizationContext
        {
            Startup = startup, Services = services, Tasks = tasks, Tweaks = tweaks, Power = power, PageFile = pageFile,
            Apps = apps, RestorePoints = restorePoints, Dialogs = dialogs, Snapshots = snapshots,
            Report = (msg, error) => { StatusMessage = msg; StatusIsError = error; },
        };
        IsAdministrator = ctx.IsAdministrator;
        Tabs =
        [
            new QuickTab(ctx),
            new StartupTab(ctx),
            new ServicesTab(ctx),
            new TasksTab(ctx),
            new TweaksTab(ctx),
            new PageFileTab(ctx),
            new AppsTab(ctx),
        ];
        _selectedTab = Tabs[0];
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatusMessage) or nameof(StatusIsError)) OnPropertyChanged(nameof(ShowSuccess));
        };
    }

    public bool ShowSuccess => !StatusIsError && !string.IsNullOrEmpty(StatusMessage);

    public IReadOnlyList<OptimizationTab> Tabs { get; }
    public bool IsAdministrator { get; }
    public bool ShowAdminHint => !IsAdministrator;

    [ObservableProperty] private OptimizationTab _selectedTab;
    [ObservableProperty] private bool _statusIsError;

    partial void OnSelectedTabChanged(OptimizationTab value)
    {
        StatusMessage = null;
        _ = value.EnsureLoadedAsync();
    }

    public override Task OnNavigatedToAsync() => SelectedTab.EnsureLoadedAsync();

    [RelayCommand]
    private void SelectTab(OptimizationTab tab) => SelectedTab = tab;

    [RelayCommand]
    private static void RestartAsAdmin() => Elevation.RestartAsAdministrator();
}

// =====================================================================
// Aba: Otimização rápida
// =====================================================================

public sealed partial class QuickAction : ObservableObject
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required bool RequiresAdmin { get; init; }
    public required Func<Task> Run { get; init; }
    public bool CanRun { get; init; } = true;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string? _result;
    [ObservableProperty] private bool? _succeeded;
}

public sealed partial class QuickTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Otimização rápida";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Flash24;

    public ObservableCollection<QuickAction> Actions { get; } = [];
    [ObservableProperty] private bool _createRestorePoint = true;
    [ObservableProperty] private bool _hasActions;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string? _summary;

    protected override async Task LoadAsync()
    {
        var actions = await Task.Run(() =>
        {
            var list = new List<QuickAction>();
            var admin = Ctx.IsAdministrator;

            var active = PowerPlans.GetActiveId();
            if (active != PowerPlans.HighPerformance && active != PowerPlans.UltimatePerformance &&
                !PowerPlans.GetActiveName().Contains("Desempenho", StringComparison.OrdinalIgnoreCase))
                list.Add(new QuickAction
                {
                    Title = "Plano de energia Alto desempenho",
                    Detail = $"O plano atual é \"{PowerPlans.GetActiveName()}\". Em um desktop, o processador responde mais rápido no Alto desempenho.",
                    RequiresAdmin = false,
                    Run = () => Task.Run(() => Ctx.Power.Activate(PowerPlans.HighPerformance)),
                });

            foreach (var t in Ctx.Tweaks.List().Where(t => t.Info.RecommendedOn && !t.IsApplied))
                list.Add(new QuickAction
                {
                    Title = t.Info.Name,
                    Detail = t.Info.Description + (t.Info.NeedsRestart ? " (vale após reiniciar)" : string.Empty),
                    RequiresAdmin = t.Info.RequiresAdmin,
                    CanRun = !t.Info.RequiresAdmin || admin,
                    Run = () => Task.Run(() => Ctx.Tweaks.Apply(t.Info.Id, true)),
                });

            foreach (var s in Ctx.Services.ListKnown().Where(s => !s.Definition.Caution &&
                         s.StartMode is ServiceStartMode.Automatico or ServiceStartMode.AutomaticoAtrasado &&
                         s.Definition.Recommended is ServiceStartMode.Manual or ServiceStartMode.Desativado))
                list.Add(new QuickAction
                {
                    Title = $"Serviço \"{s.DisplayName}\" → {ModeText(s.Definition.Recommended)}",
                    Detail = s.Definition.Description,
                    RequiresAdmin = true,
                    CanRun = admin,
                    Run = () => Ctx.Services.SetStartModeAsync(s.Definition.Name, s.Definition.Recommended, stopNow: true),
                });
            return list;
        });

        Actions.Clear();
        foreach (var a in actions)
        {
            a.IsSelected = a.CanRun;
            Actions.Add(a);
        }
        HasActions = Actions.Count > 0;
        Summary = HasActions ? $"{Actions.Count} melhorias encontradas." : "Tudo já está otimizado por aqui.";
    }

    public static string ModeText(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Automatico => "Automático",
        ServiceStartMode.AutomaticoAtrasado => "Automático (atrasado)",
        ServiceStartMode.Manual => "Manual",
        ServiceStartMode.Desativado => "Desativado",
        _ => "Desconhecido",
    };

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var selected = Actions.Where(a => a.IsSelected && a.CanRun).ToList();
        if (selected.Count == 0) return;
        if (!await Ctx.Dialogs.ConfirmAsync("Aplicar otimizações",
                $"Serão aplicadas {selected.Count} melhorias. Todas podem ser desfeitas pelas outras abas desta tela." +
                (CreateRestorePoint && Ctx.IsAdministrator ? "\n\nUm ponto de restauração do Windows será criado antes." : string.Empty),
                "Aplicar")) return;

        IsApplying = true;
        try
        {
            if (CreateRestorePoint && Ctx.IsAdministrator)
            {
                Summary = "Criando ponto de restauração…";
                var ok = await Ctx.RestorePoints.CreateAsync("OtimizadorPC - antes da otimização rápida");
                if (!ok) Log.Warning("Ponto de restauração não criado (proteção do sistema desativada ou já existe um recente)");
            }

            int success = 0;
            foreach (var a in selected)
            {
                Summary = $"Aplicando: {a.Title}";
                try
                {
                    await a.Run();
                    a.Succeeded = true;
                    a.Result = "Aplicado";
                    success++;
                }
                catch (Exception ex)
                {
                    a.Succeeded = false;
                    a.Result = ex.Message;
                    Log.Warning(ex, "Falha na otimização {Acao}", a.Title);
                }
            }
            Summary = $"{success} de {selected.Count} melhorias aplicadas.";
            Ctx.Report(Summary, success < selected.Count);
            _ = Ctx.Snapshots.GetAsync(forceRefresh: true);
        }
        finally
        {
            IsApplying = false;
        }
    }
}

// =====================================================================
// Aba: Inicialização
// =====================================================================

public sealed partial class StartupEntry : ObservableObject
{
    private readonly StartupTab _owner;
    private bool _isEnabled;

    public StartupEntry(StartupTab owner, StartupItem item)
    {
        _owner = owner;
        Item = item;
        _isEnabled = item.IsEnabled;
    }

    public StartupItem Item { get; }
    public bool CanChange => !Item.RequiresAdmin || _owner.IsAdministrator;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            if (_owner.TryChange(this, value)) SetProperty(ref _isEnabled, value);
            else OnPropertyChanged();
        }
    }
}

public sealed partial class StartupTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Inicialização";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Power24;

    public ObservableCollection<StartupEntry> Items { get; } = [];
    [ObservableProperty] private string _summary = string.Empty;

    protected override async Task LoadAsync()
    {
        var items = await Task.Run(Ctx.Startup.List);
        Items.Clear();
        foreach (var i in items) Items.Add(new StartupEntry(this, i));
        UpdateSummary();
    }

    private void UpdateSummary() =>
        Summary = $"{Items.Count(i => i.IsEnabled)} de {Items.Count} programas iniciam com o Windows.";

    internal bool TryChange(StartupEntry entry, bool enabled)
    {
        try
        {
            Ctx.Startup.SetEnabled(entry.Item, enabled);
            Ctx.Report($"\"{entry.Item.Name}\" {(enabled ? "voltará a iniciar" : "não vai mais iniciar")} com o Windows.", false);
            Log.Information("Inicialização {Item} -> {Estado}", entry.Item.Id, enabled);
            Dispatch(UpdateSummary);
            return true;
        }
        catch (Exception ex)
        {
            Ctx.Report(ex is UnauthorizedAccessException ? "Este item precisa do app aberto como administrador." : ex.Message, true);
            return false;
        }
    }

    private static void Dispatch(Action a) => System.Windows.Application.Current.Dispatcher.BeginInvoke(a);
}

// =====================================================================
// Aba: Serviços
// =====================================================================

public sealed partial class ServiceEntry : ObservableObject
{
    private readonly ServicesTab _owner;

    public ServiceEntry(ServicesTab owner, ServiceStatus status)
    {
        _owner = owner;
        Status = status;
        _mode = status.StartMode;
        _isRunning = status.IsRunning;
    }

    public ServiceStatus Status { get; }
    public string RecommendedText => "Recomendado: " + QuickTab.ModeText(Status.Definition.Recommended);
    public bool IsRecommended => Mode == Status.Definition.Recommended ||
                                 (Mode == ServiceStartMode.Desativado && Status.Definition.Recommended == ServiceStartMode.Manual);
    public static IReadOnlyList<ServiceStartMode> Modes { get; } =
        [ServiceStartMode.Automatico, ServiceStartMode.AutomaticoAtrasado, ServiceStartMode.Manual, ServiceStartMode.Desativado];

    [ObservableProperty] private ServiceStartMode _mode;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isChanging;

    partial void OnModeChanged(ServiceStartMode oldValue, ServiceStartMode newValue)
    {
        OnPropertyChanged(nameof(IsRecommended));
        if (_suppress) return;
        _ = _owner.ChangeAsync(this, oldValue, newValue);
    }

    private bool _suppress;

    internal void Revert(ServiceStartMode previous)
    {
        _suppress = true;
        Mode = previous;
        _suppress = false;
    }
}

public sealed partial class ServicesTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Serviços";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Settings24;

    public ObservableCollection<ServiceEntry> Items { get; } = [];

    protected override async Task LoadAsync()
    {
        var list = await Task.Run(Ctx.Services.ListKnown);
        Items.Clear();
        foreach (var s in list.OrderBy(s => s.Definition.Category).ThenBy(s => s.DisplayName)) Items.Add(new ServiceEntry(this, s));
    }

    internal async Task ChangeAsync(ServiceEntry entry, ServiceStartMode previous, ServiceStartMode mode)
    {
        if (!Ctx.IsAdministrator)
        {
            entry.Revert(previous);
            Ctx.Report("Alterar serviços exige o OtimizadorPC aberto como administrador.", true);
            return;
        }
        entry.IsChanging = true;
        try
        {
            await Ctx.Services.SetStartModeAsync(entry.Status.Definition.Name, mode, stopNow: true);
            entry.IsRunning = mode is ServiceStartMode.Automatico or ServiceStartMode.AutomaticoAtrasado;
            Ctx.Report($"\"{entry.Status.DisplayName}\" agora inicia em modo {QuickTab.ModeText(mode)}.", false);
            Log.Information("Serviço {Servico}: {Antes} -> {Depois}", entry.Status.Definition.Name, previous, mode);
        }
        catch (Exception ex)
        {
            entry.Revert(previous);
            Ctx.Report(ex.Message, true);
        }
        finally
        {
            entry.IsChanging = false;
        }
    }

    [RelayCommand]
    private void ApplyRecommended()
    {
        foreach (var e in Items.Where(e => !e.Status.Definition.Caution && !e.IsRecommended)) e.Mode = e.Status.Definition.Recommended;
    }
}

// =====================================================================
// Aba: Tarefas agendadas
// =====================================================================

public sealed partial class TaskEntry : ObservableObject
{
    private readonly TasksTab _owner;
    private bool _isEnabled;

    public TaskEntry(TasksTab owner, ScheduledTaskInfo info)
    {
        _owner = owner;
        Info = info;
        _isEnabled = info.IsEnabled;
    }

    public ScheduledTaskInfo Info { get; }
    public string NextRunText => Info.NextRun is { } d ? $"Próxima execução: {d:dd/MM HH:mm}" : "Sem próxima execução";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            if (_owner.TryChange(this, value)) SetProperty(ref _isEnabled, value);
            else OnPropertyChanged();
        }
    }
}

public sealed partial class TasksTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Tarefas agendadas";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Timer24;

    public ObservableCollection<TaskEntry> Items { get; } = [];

    protected override async Task LoadAsync()
    {
        var list = await Task.Run(Ctx.Tasks.ListThirdParty);
        Items.Clear();
        foreach (var t in list) Items.Add(new TaskEntry(this, t));
    }

    internal bool TryChange(TaskEntry entry, bool enabled)
    {
        try
        {
            Ctx.Tasks.SetEnabled(entry.Info.Path, enabled);
            Ctx.Report($"Tarefa \"{entry.Info.Name}\" {(enabled ? "ativada" : "desativada")}.", false);
            return true;
        }
        catch (Exception ex)
        {
            Ctx.Report(ex is UnauthorizedAccessException ? "Esta tarefa precisa do app aberto como administrador." : ex.Message, true);
            return false;
        }
    }
}

// =====================================================================
// Aba: Ajustes e energia
// =====================================================================

public sealed partial class TweakEntry : ObservableObject
{
    private readonly TweaksTab _owner;
    private bool _isOn;

    public TweakEntry(TweaksTab owner, TweakState state)
    {
        _owner = owner;
        State = state;
        _isOn = state.IsApplied;
    }

    public TweakState State { get; }
    public bool CanChange => !State.Info.RequiresAdmin || _owner.IsAdministrator;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            if (_owner.TryChange(this, value)) SetProperty(ref _isOn, value);
            else OnPropertyChanged();
        }
    }
}

public sealed record PowerPlanEntry(Guid Id, string Name, bool IsActive);

public sealed partial class TweaksTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Ajustes e energia";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Wrench24;

    public ObservableCollection<TweakEntry> Items { get; } = [];
    public ObservableCollection<PowerPlanEntry> Plans { get; } = [];

    protected override async Task LoadAsync()
    {
        var (tweaks, plans) = await Task.Run(() => (Ctx.Tweaks.List(), Ctx.Power.ListPlans()));
        Items.Clear();
        foreach (var t in tweaks.OrderBy(t => t.Info.Category)) Items.Add(new TweakEntry(this, t));
        Plans.Clear();
        foreach (var p in plans) Plans.Add(new PowerPlanEntry(p.Id, p.Name, p.Active));
    }

    internal bool TryChange(TweakEntry entry, bool on)
    {
        try
        {
            Ctx.Tweaks.Apply(entry.State.Info.Id, on);
            Ctx.Report($"\"{entry.State.Info.Name}\" {(on ? "ativado" : "desativado")}" +
                       (entry.State.Info.NeedsRestart ? ". Reinicie o computador para valer." : "."), false);
            Log.Information("Ajuste {Ajuste} -> {Estado}", entry.State.Info.Id, on);
            return true;
        }
        catch (Exception ex)
        {
            Ctx.Report(ex.Message, true);
            return false;
        }
    }

    [RelayCommand]
    private async Task ActivatePlanAsync(PowerPlanEntry plan)
    {
        try
        {
            await Task.Run(() => Ctx.Power.Activate(plan.Id));
            Ctx.Report($"Plano \"{plan.Name}\" ativado.", false);
            await ReloadAsync();
        }
        catch (Exception ex) { Ctx.Report(ex.Message, true); }
    }

    [RelayCommand]
    private async Task EnableUltimateAsync()
    {
        try
        {
            await Task.Run(Ctx.Power.EnableUltimatePerformance);
            Ctx.Report("Plano Desempenho Máximo criado e ativado.", false);
            await ReloadAsync();
        }
        catch (Exception ex) { Ctx.Report(ex.Message, true); }
    }
}

// =====================================================================
// Aba: Memória virtual
// =====================================================================

public sealed record PageFileDrive(string Letter, string Label, DiskMediaKind Media, long FreeBytes, bool Allowed, string Note)
{
    public string Display => $"{Letter}: {Label} — {Media}, {ByteSize.Format(FreeBytes)} livres" + (Note.Length > 0 ? $" ({Note})" : string.Empty);
}

public sealed partial class PageFileTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Memória virtual";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Memory16;

    public ObservableCollection<PageFileDrive> Drives { get; } = [];
    [ObservableProperty] private string _currentText = string.Empty;
    [ObservableProperty] private bool _automatic;
    [ObservableProperty] private PageFileDrive? _selectedDrive;
    [ObservableProperty] private int _initialMb = 4096;
    [ObservableProperty] private int _maximumMb = 8192;
    [ObservableProperty] private string _suggestion = string.Empty;

    protected override async Task LoadAsync()
    {
        var (cfg, snap) = await Task.Run(async () => (Ctx.PageFile.Read(), await Ctx.Snapshots.GetAsync()));
        Automatic = cfg.Automatic;
        CurrentText = cfg.Automatic ? "Gerenciado automaticamente pelo Windows"
            : cfg.Files.Count == 0 ? "Sem arquivo de paginação"
            : string.Join("  ·  ", cfg.Files.Select(f => f.SystemManaged ? $"{f.Drive}: tamanho gerenciado pelo sistema" : $"{f.Drive}: {f.InitialMb}–{f.MaximumMb} MB"));
        if (snap.PageFiles.Any(p => p.IsTemporary)) CurrentText += "  ·  ATENÇÃO: o Windows está usando uma paginação temporária";

        var ramMb = (int)(snap.Memory.TotalBytes / 1024 / 1024);
        InitialMb = Math.Clamp(ramMb / 2 / 1024 * 1024, 2048, 16384);
        MaximumMb = Math.Clamp((ramMb + 1023) / 1024 * 1024, 4096, 32768);

        // Precisa caber o tamanho máximo e ainda sobrar 10 GB livres no disco
        var needed = (long)MaximumMb * 1024 * 1024 + 10L * 1024 * 1024 * 1024;
        Drives.Clear();
        foreach (var v in snap.Volumes)
        {
            var allowed = SystemAdvisor.IsPageFileCapableLetter(v.Letter);
            var fast = v.Media is DiskMediaKind.SSD or DiskMediaKind.NVMe;
            var note = !allowed ? "o Windows não aceita esta letra"
                : v.FreeBytes < needed ? "pouco espaço livre"
                : fast ? "recomendado" : string.Empty;
            Drives.Add(new PageFileDrive(v.Letter, v.Label, v.Media, v.FreeBytes, allowed, note));
        }
        SelectedDrive = Drives.Where(d => d.Allowed)
            .OrderByDescending(d => d.FreeBytes >= needed)
            .ThenByDescending(d => d.Media is DiskMediaKind.SSD or DiskMediaKind.NVMe)
            .ThenByDescending(d => d.FreeBytes)
            .FirstOrDefault();
        Suggestion = $"Sugestão para {ByteSize.Format(snap.Memory.TotalBytes)} de RAM: inicial {InitialMb} MB e máximo {MaximumMb} MB, de preferência em um SSD.";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!Ctx.IsAdministrator)
        {
            Ctx.Report("Alterar a memória virtual exige o OtimizadorPC aberto como administrador.", true);
            return;
        }
        if (!Automatic && SelectedDrive is null) return;
        if (!Automatic && !SelectedDrive!.Allowed)
        {
            Ctx.Report($"O Windows não cria paginação na unidade {SelectedDrive.Letter}:. Escolha outra.", true);
            return;
        }

        var text = Automatic ? "o Windows passará a gerenciar a memória virtual"
            : $"a paginação ficará em {SelectedDrive!.Letter}: com {InitialMb}–{MaximumMb} MB";
        if (!await Ctx.Dialogs.ConfirmAsync("Memória virtual", $"Depois de reiniciar, {text}. Continuar?", "Aplicar")) return;

        try
        {
            await Task.Run(() => Ctx.PageFile.Write(Automatic, Automatic ? [] : [new PageFileEntry(SelectedDrive!.Letter, InitialMb, MaximumMb)]));
            Ctx.Report("Memória virtual configurada. Reinicie o computador para aplicar.", false);
            await ReloadAsync();
        }
        catch (Exception ex) { Ctx.Report(ex.Message, true); }
    }
}

// =====================================================================
// Aba: Apps pré-instalados
// =====================================================================

public sealed partial class AppEntry : ObservableObject
{
    public required InstalledApp App { get; init; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRemoving;
}

public sealed partial class AppsTab(OptimizationContext ctx) : OptimizationTab(ctx)
{
    public override string Title => "Apps pré-instalados";
    public override Wpf.Ui.Controls.SymbolRegular Icon => Wpf.Ui.Controls.SymbolRegular.Apps24;

    public ObservableCollection<AppEntry> Items { get; } = [];
    [ObservableProperty] private bool _isEmpty;

    protected override async Task LoadAsync()
    {
        var apps = await Task.Run(Ctx.Apps.ListRemovable);
        Items.Clear();
        foreach (var a in apps) Items.Add(new AppEntry { App = a });
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        var names = string.Join("\n", selected.Select(s => "• " + s.App.Definition.DisplayName));
        if (!await Ctx.Dialogs.ConfirmAsync("Remover apps",
                $"Estes apps serão desinstalados da sua conta:\n{names}\n\nQualquer um pode ser reinstalado depois pela Microsoft Store.", "Remover")) return;

        var ok = 0;
        foreach (var item in selected)
        {
            item.IsRemoving = true;
            try
            {
                await Ctx.Apps.RemoveAsync(item.App);
                Items.Remove(item);
                ok++;
                Log.Information("App removido: {App}", item.App.FullName);
            }
            catch (Exception ex)
            {
                item.IsRemoving = false;
                Ctx.Report(ex.Message, true);
            }
        }
        IsEmpty = Items.Count == 0;
        if (ok > 0) Ctx.Report($"{ok} app(s) removido(s).", false);
    }
}
