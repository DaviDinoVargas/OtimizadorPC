using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OtimizadorPC.App.Navigation;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Diagnostics;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Hardware;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

/// <summary>Painel inicial: uso em tempo real, discos, resumo do sistema e recomendações.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private const int HistoryLength = 60;

    private readonly SensorMonitor _monitor;
    private readonly SnapshotProvider _snapshots;

    public DashboardViewModel(SensorMonitor monitor, SnapshotProvider snapshots)
    {
        _monitor = monitor;
        _snapshots = snapshots;
    }

    public ObservableCollection<double> CpuHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));
    public ObservableCollection<double> RamHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));
    public ObservableCollection<double> GpuHistory { get; } = new(Enumerable.Repeat(0d, HistoryLength));


    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuDetail = "—";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramDetail = "—";
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private string _gpuDetail = "—";
    [ObservableProperty] private bool _hasGpuSensor;
    [ObservableProperty] private double _systemDiskUsed;
    [ObservableProperty] private string _systemDiskDetail = "—";

    [ObservableProperty] private string _osText = string.Empty;
    [ObservableProperty] private string _cpuName = string.Empty;
    [ObservableProperty] private string _gpuName = string.Empty;
    [ObservableProperty] private string _ramText = string.Empty;
    [ObservableProperty] private string _boardText = string.Empty;
    [ObservableProperty] private string _uptimeText = string.Empty;
    [ObservableProperty] private bool _hasSnapshot;
    [ObservableProperty] private bool _sensorDriverMissing;

    public ObservableCollection<VolumeInfo> Volumes { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];

    [ObservableProperty] private bool _hasRecommendations;

    public override async Task OnNavigatedToAsync()
    {
        SensorDriverMissing = !_monitor.HasDriverAccess;
        _monitor.Subscribe(OnMetrics);
        await LoadSnapshotAsync(false);
    }

    public override void OnNavigatedFrom() => _monitor.Unsubscribe(OnMetrics);

    private bool _primed;

    private void OnMetrics(LiveMetrics m)
    {
        // Na primeira leitura preenche o histórico para o gráfico não começar com um "degrau"
        if (!_primed)
        {
            _primed = true;
            Fill(CpuHistory, m.CpuLoad);
            Fill(RamHistory, m.RamUsedPercent);
            Fill(GpuHistory, m.GpuLoad ?? 0);
        }

        CpuLoad = m.CpuLoad;
        // Sem driver alguns sensores voltam 0: tratados como indisponíveis
        CpuDetail = Join(m.CpuClockMhz is > 0 and var c ? $"{c / 1000:0.00} GHz" : null, m.CpuTemperature is > 0 and var t ? $"{t:0} °C" : null,
                         m.CpuPowerWatts is > 0.5 and var p ? $"{p:0} W" : null) ?? "Uso total do processador";
        Push(CpuHistory, m.CpuLoad);

        RamPercent = m.RamUsedPercent;
        RamDetail = $"{ByteSize.Format(m.RamUsedBytes)} de {ByteSize.Format(m.RamTotalBytes)}";
        Push(RamHistory, m.RamUsedPercent);

        HasGpuSensor = m.GpuLoad is not null;
        GpuLoad = m.GpuLoad ?? 0;
        GpuDetail = Join(m.GpuTemperature is { } gt ? $"{gt:0} °C" : null, m.GpuCoreClockMhz is { } gc ? $"{gc:0} MHz" : null,
                         m.GpuPowerWatts is { } gp ? $"{gp:0} W" : null) ?? "Sem leitura da placa de vídeo";
        Push(GpuHistory, GpuLoad);
    }

    private static void Fill(ObservableCollection<double> list, double value)
    {
        for (var i = 0; i < list.Count; i++) list[i] = Math.Round(value, 1);
    }

    private static void Push(ObservableCollection<double> list, double value)
    {
        list.RemoveAt(0);
        list.Add(Math.Round(value, 1));
    }

    private static string? Join(params string?[] parts)
    {
        var p = parts.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        return p.Length == 0 ? null : string.Join("  ·  ", p);
    }

    private async Task LoadSnapshotAsync(bool force)
    {
        try
        {
            IsBusy = !HasSnapshot;
            var s = await _snapshots.GetAsync(force);
            Apply(s);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao coletar informações do sistema");
            StatusMessage = "Não foi possível ler as informações do sistema.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(SystemSnapshot s)
    {
        OsText = $"{s.Os.Name} {s.Os.Version} (build {s.Os.Build})";
        CpuName = $"{s.Cpu.Name} · {s.Cpu.Cores} núcleos / {s.Cpu.Threads} threads";
        GpuName = string.Join(" + ", s.Gpus.Select(g => g.Name)) is { Length: > 0 } g ? g : "Não identificada";
        var speed = s.Memory.Modules.Select(m => m.ConfiguredMhz).DefaultIfEmpty(0).Max();
        RamText = $"{ByteSize.Format(s.Memory.TotalBytes)}" + (speed > 0 ? $" a {speed} MHz" : string.Empty) +
                  $" · {s.Memory.Modules.Count} de {s.Memory.SlotCount} slots";
        BoardText = $"{s.Board.Manufacturer} {s.Board.Product}".Trim();
        UptimeText = s.Os.Uptime.TotalDays >= 1
            ? $"{(int)s.Os.Uptime.TotalDays} d {s.Os.Uptime.Hours} h"
            : $"{s.Os.Uptime.Hours} h {s.Os.Uptime.Minutes} min";

        Volumes.Clear();
        foreach (var v in s.Volumes) Volumes.Add(v);

        var system = s.Volumes.FirstOrDefault(v => v.IsSystem);
        if (system is not null)
        {
            SystemDiskUsed = system.UsedFraction * 100;
            SystemDiskDetail = $"{ByteSize.Format(system.FreeBytes)} livres de {ByteSize.Format(system.TotalBytes)}";
        }

        Recommendations.Clear();
        foreach (var r in SystemAdvisor.Analyze(s)) Recommendations.Add(r);
        HasRecommendations = Recommendations.Count > 0;
        HasSnapshot = true;
    }

    [RelayCommand]
    private Task Refresh() => LoadSnapshotAsync(true);

    [RelayCommand]
    private static void Open(string? key)
    {
        if (!string.IsNullOrEmpty(key)) WeakReferenceMessenger.Default.Send(new NavigateMessage(key));
    }
}
