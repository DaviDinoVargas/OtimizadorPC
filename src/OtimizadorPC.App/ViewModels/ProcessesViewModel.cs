using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Processes;
using OtimizadorPC.Infrastructure.Hardware;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public enum ProcessFilter { Todos, Aplicativos, SegundoPlano, Dispensaveis, Sistema }

public enum ProcessSort { Memoria, Cpu, Nome }

public sealed partial class ProcessGroupViewModel : ObservableObject
{
    public ProcessGroupViewModel(ProcessGroup g) => Update(g, 1);

    public string Key { get; private set; } = string.Empty;
    public string? Path { get; private set; }
    public ImageSource? Icon => IconCache.Get(Path);

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _exeName = string.Empty;
    [ObservableProperty] private ProcessCategory _category;
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _memoryBytes;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryFraction;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private IReadOnlyList<ProcessSample> _processes = [];

    public bool IsProtected => Category == ProcessCategory.Sistema;
    public string MemoryText => ByteSize.Format(MemoryBytes);
    public string CpuText => $"{CpuPercent:0.0}%";
    public string CategoryText => Category switch
    {
        ProcessCategory.Sistema => "Sistema",
        ProcessCategory.Aplicativo => "Aberto",
        ProcessCategory.Dispensavel => "Dispensável",
        _ => "Segundo plano",
    };

    partial void OnMemoryBytesChanged(long value) => OnPropertyChanged(nameof(MemoryText));
    partial void OnCpuPercentChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnCategoryChanged(ProcessCategory value)
    {
        OnPropertyChanged(nameof(IsProtected));
        OnPropertyChanged(nameof(CategoryText));
    }

    public void Update(ProcessGroup g, long totalRam)
    {
        Key = g.Key;
        Path = g.Path;
        DisplayName = g.DisplayName;
        ExeName = g.Processes[0].Name + ".exe";
        Category = g.Category;
        Reason = g.Reason;
        Count = g.Count;
        MemoryBytes = g.MemoryBytes;
        CpuPercent = Math.Round(g.CpuPercent, 1);
        MemoryFraction = totalRam > 0 ? (double)g.MemoryBytes / totalRam : 0;
        Processes = g.Processes;
    }
}

/// <summary>Lista de processos agrupados por programa, com atualização ao vivo e encerramento seguro.</summary>
public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly IProcessService _processes;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, ProcessGroupViewModel> _index = [];
    private CancellationTokenSource? _cts;

    public ProcessesViewModel(IProcessService processes, IDialogService dialogs)
    {
        _processes = processes;
        _dialogs = dialogs;
        View = (ListCollectionView)CollectionViewSource.GetDefaultView(Groups);
        View.Filter = Matches;
        View.IsLiveSorting = true;
        ApplySort();
    }

    public ObservableCollection<ProcessGroupViewModel> Groups { get; } = [];
    public ListCollectionView View { get; }

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private ProcessFilter _filter = ProcessFilter.Todos;
    [ObservableProperty] private ProcessSort _sort = ProcessSort.Memoria;
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private double _cpuTotal;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = string.Empty;
    [ObservableProperty] private int _dispensableCount;
    [ObservableProperty] private long _dispensableMemory;

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnFilterChanged(ProcessFilter value) => View.Refresh();
    partial void OnSortChanged(ProcessSort value) => ApplySort();

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.LiveSortingProperties.Clear();
            var (prop, dir) = Sort switch
            {
                ProcessSort.Cpu => (nameof(ProcessGroupViewModel.CpuPercent), ListSortDirection.Descending),
                ProcessSort.Nome => (nameof(ProcessGroupViewModel.DisplayName), ListSortDirection.Ascending),
                _ => (nameof(ProcessGroupViewModel.MemoryBytes), ListSortDirection.Descending),
            };
            View.SortDescriptions.Add(new SortDescription(prop, dir));
            View.LiveSortingProperties.Add(prop);
        }
    }

    private bool Matches(object o)
    {
        if (o is not ProcessGroupViewModel g) return false;
        var categoryOk = Filter switch
        {
            ProcessFilter.Aplicativos => g.Category == ProcessCategory.Aplicativo,
            ProcessFilter.SegundoPlano => g.Category == ProcessCategory.SegundoPlano,
            ProcessFilter.Dispensaveis => g.Category == ProcessCategory.Dispensavel,
            ProcessFilter.Sistema => g.Category == ProcessCategory.Sistema,
            _ => true,
        };
        return categoryOk && (string.IsNullOrWhiteSpace(Search) ||
                              g.DisplayName.Contains(Search, StringComparison.CurrentCultureIgnoreCase) ||
                              g.ExeName.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
    }

    public override async Task OnNavigatedToAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = Groups.Count == 0;
        await RefreshAsync();
        IsBusy = false;
        _ = LoopAsync(token);
    }

    public override void OnNavigatedFrom() => _cts?.Cancel();

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(token)) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var (groups, total, avail) = await Task.Run(() =>
            {
                var g = ProcessClassifier.Group(_processes.Sample());
                var (t, a) = LhmSensorService.ReadMemoryStatus();
                return (g, t, a);
            });
            Apply(groups, total, avail);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao ler processos");
        }
    }

    private void Apply(IReadOnlyList<ProcessGroup> groups, long totalRam, long availRam)
    {
        var keys = new HashSet<string>();
        foreach (var g in groups)
        {
            keys.Add(g.Key);
            if (_index.TryGetValue(g.Key, out var vm)) vm.Update(g, totalRam);
            else
            {
                vm = new ProcessGroupViewModel(g);
                vm.Update(g, totalRam);
                _index[g.Key] = vm;
                Groups.Add(vm);
            }
        }
        foreach (var gone in _index.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            Groups.Remove(_index[gone]);
            _index.Remove(gone);
        }

        ProcessCount = groups.Sum(g => g.Count);
        CpuTotal = Math.Min(100, groups.Sum(g => g.CpuPercent));
        RamPercent = totalRam > 0 ? (totalRam - availRam) * 100.0 / totalRam : 0;
        RamText = $"{ByteSize.Format(totalRam - availRam)} de {ByteSize.Format(totalRam)}";
        var dispensable = groups.Where(g => g.Category == ProcessCategory.Dispensavel).ToList();
        DispensableCount = dispensable.Count;
        DispensableMemory = dispensable.Sum(g => g.MemoryBytes);
    }

    // ---------------- Ações ----------------

    [RelayCommand]
    private async Task KillAsync(ProcessGroupViewModel? group)
    {
        if (group is null || group.IsProtected) return;
        var warning = group.Category == ProcessCategory.Aplicativo
            ? "\n\nO programa está aberto: trabalhos não salvos serão perdidos."
            : string.Empty;
        if (!await _dialogs.ConfirmAsync("Encerrar programa",
                $"Encerrar \"{group.DisplayName}\" ({group.Count} processo(s), {group.MemoryText})?{warning}", "Encerrar")) return;

        var failed = KillGroup(group);
        StatusMessage = failed == 0 ? $"\"{group.DisplayName}\" encerrado." : $"{failed} processo(s) não puderam ser encerrados (talvez exijam administrador).";
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task KillDispensableAsync()
    {
        var targets = Groups.Where(g => g.Category == ProcessCategory.Dispensavel).ToList();
        if (targets.Count == 0)
        {
            await _dialogs.InfoAsync("Nada a encerrar", "Nenhum processo dispensável está rodando agora.");
            return;
        }
        var list = string.Join("\n", targets.Select(t => $"• {t.DisplayName} — {t.Reason}"));
        if (!await _dialogs.ConfirmAsync("Encerrar processos dispensáveis",
                $"Estes programas rodam em segundo plano sem necessidade e serão fechados ({ByteSize.Format(targets.Sum(t => t.MemoryBytes))}):\n\n{list}\n\nEles podem voltar a abrir sozinhos depois; para evitar, desative-os na Otimização > Inicialização.",
                "Encerrar todos")) return;

        var failed = targets.Sum(KillGroup);
        StatusMessage = failed == 0 ? $"{targets.Count} programas dispensáveis encerrados." : $"Alguns processos ({failed}) não puderam ser encerrados.";
        await RefreshAsync();
    }

    private int KillGroup(ProcessGroupViewModel group)
    {
        var failed = 0;
        foreach (var p in group.Processes)
        {
            try { _processes.Kill(p.Pid, entireTree: true); }
            catch (ArgumentException) { /* já terminou */ }
            catch (Exception ex)
            {
                failed++;
                Log.Warning(ex, "Não foi possível encerrar {Pid} {Nome}", p.Pid, p.Name);
            }
        }
        Log.Information("Encerrado {Programa} ({Qtd} processos, {Falhas} falhas)", group.DisplayName, group.Count, failed);
        return failed;
    }

    [RelayCommand]
    private void OpenLocation(ProcessGroupViewModel? group)
    {
        if (group?.Path is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{group.Path}\"") { UseShellExecute = true });
    }

    [RelayCommand] private void PriorityHigh(ProcessGroupViewModel? g) => SetPriority(g, PriorityLevel.Alta);
    [RelayCommand] private void PriorityNormal(ProcessGroupViewModel? g) => SetPriority(g, PriorityLevel.Normal);
    [RelayCommand] private void PriorityLow(ProcessGroupViewModel? g) => SetPriority(g, PriorityLevel.Baixa);

    private void SetPriority(ProcessGroupViewModel? group, PriorityLevel level)
    {
        if (group is null) return;
        var failed = 0;
        foreach (var p in group.Processes)
        {
            try { _processes.SetPriority(p.Pid, level); }
            catch (Exception) { failed++; }
        }
        StatusMessage = failed == 0 ? $"Prioridade de \"{group.DisplayName}\" alterada." : "Alguns processos não aceitaram a mudança de prioridade.";
    }

    [RelayCommand]
    private static void ToggleExpand(ProcessGroupViewModel? group)
    {
        if (group is not null) group.IsExpanded = !group.IsExpanded;
    }
}
