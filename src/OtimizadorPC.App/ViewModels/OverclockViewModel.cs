using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;
using OtimizadorPC.Core.Overclock;
using OtimizadorPC.Infrastructure.Overclock;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public sealed record CapabilityCard(OcCapability Capability, Wpf.Ui.Controls.SymbolRegular Icon)
{
    public string SupportText => Capability.Support switch
    {
        OcSupport.PeloApp => "Ajustável pelo OtimizadorPC",
        OcSupport.PelaBios => "Pela BIOS ou software do fabricante",
        _ => "Não suportado neste hardware",
    };
}

public sealed partial class BenchmarkRow : ObservableObject
{
    public required BenchmarkKind Kind { get; init; }
    public required string Name { get; init; }
    [ObservableProperty] private double? _value;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private bool _higherIsBetter = true;
    [ObservableProperty] private double? _previous;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isRunning;

    public double? Change => EnergyCost.Change(Previous, Value, HigherIsBetter);
    public string ValueText => Value is { } v ? $"{v:N0} {Unit}" : "—";
    public string PreviousText => Previous is { } p ? $"{p:N0} {Unit}" : "—";
    public string ChangeText => Change is { } c ? (c >= 0 ? $"+{c:0.0}%" : $"{c:0.0}%") : string.Empty;
    public bool Improved => Change is > 0.5;
    public bool Worse => Change is < -0.5;

    /// <summary>Barras proporcionais (0–1) para comparar visualmente atual x anterior.</summary>
    public double ValueBar => Bar(Value);
    public double PreviousBar => Bar(Previous);

    private double Bar(double? v)
    {
        var max = Math.Max(Value ?? 0, Previous ?? 0);
        if (v is null || max <= 0) return 0;
        return HigherIsBetter ? v.Value / max : Math.Min(Value ?? 0, Previous ?? 0) / v.Value;
    }

    partial void OnValueChanged(double? value) => Refresh();
    partial void OnPreviousChanged(double? value) => Refresh();

    private void Refresh()
    {
        foreach (var p in new[] { nameof(Change), nameof(ValueText), nameof(PreviousText), nameof(ChangeText), nameof(Improved), nameof(Worse), nameof(ValueBar), nameof(PreviousBar) })
            OnPropertyChanged(p);
    }
}

public sealed record RunOption(BenchmarkRun Run)
{
    public string Display => $"{Run.Label} — {Run.When:dd/MM HH:mm}";
}

/// <summary>Overclock: capacidades por plataforma, benchmarks, estresse e ajuste automático da GPU.</summary>
public sealed partial class OverclockViewModel : PageViewModel
{
    private readonly SnapshotProvider _snapshots;
    private readonly SensorMonitor _monitor;
    private readonly IBenchmarkService _bench;
    private readonly IBenchmarkHistory _history;
    private readonly ICpuStress _cpuStress;
    private readonly IGpuTuner _gpuTuner;
    private readonly ICpuPowerTuner _cpuPower;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;
    private LiveMetrics? _last;
    private readonly List<LiveMetrics> _window = [];

    public OverclockViewModel(SnapshotProvider snapshots, SensorMonitor monitor, IBenchmarkService bench, IBenchmarkHistory history,
        ICpuStress cpuStress, IGpuTuner gpuTuner, ICpuPowerTuner cpuPower, IDialogService dialogs, ISettingsService settings)
    {
        _snapshots = snapshots;
        _monitor = monitor;
        _bench = bench;
        _history = history;
        _cpuStress = cpuStress;
        _gpuTuner = gpuTuner;
        _cpuPower = cpuPower;
        _dialogs = dialogs;
        _settings = settings;

        foreach (var k in Enum.GetValues<BenchmarkKind>())
            Benchmarks.Add(new BenchmarkRow { Kind = k, Name = new BenchmarkResult(k, 0, "", true).Name });
    }

    public IReadOnlyList<string> Sections { get; } = ["Visão geral", "Benchmarks", "Teste de estresse", "Overclock da GPU"];
    [ObservableProperty] private int _selectedSection;

    // ---------------- Ciclo de vida ----------------

    public override async Task OnNavigatedToAsync()
    {
        _monitor.Subscribe(OnMetrics);
        if (Capabilities.Count == 0) await LoadOverviewAsync();
    }

    public override void OnNavigatedFrom() => _monitor.Unsubscribe(OnMetrics);

    private void OnMetrics(LiveMetrics m)
    {
        _last = m;
        lock (_window) _window.Add(m);
        CurrentCpuTemp = m.CpuTemperature;
        CurrentGpuTemp = m.GpuTemperature;
        CurrentGpuPower = m.GpuPowerWatts;
        CurrentGpuClock = m.GpuCoreClockMhz;
        if (IsStressing)
        {
            StressTemps.RemoveAt(0);
            StressTemps.Add(StressOnGpu ? m.GpuTemperature ?? 0 : m.CpuTemperature ?? m.CpuLoad);
        }
    }

    [ObservableProperty] private double? _currentCpuTemp;
    [ObservableProperty] private double? _currentGpuTemp;
    [ObservableProperty] private double? _currentGpuPower;
    [ObservableProperty] private double? _currentGpuClock;

    // ======================================================================
    // Visão geral
    // ======================================================================

    public ObservableCollection<CapabilityCard> Capabilities { get; } = [];
    public ObservableCollection<string> Drives { get; } = [];
    [ObservableProperty] private bool _cpuPerformanceMode;
    [ObservableProperty] private string _cpuPowerText = string.Empty;
    private bool _loadingPower;

    private async Task LoadOverviewAsync()
    {
        var s = await _snapshots.GetAsync();
        var r = OverclockAdvisor.Analyze(s);
        Capabilities.Clear();
        Capabilities.Add(new(r.Cpu, Wpf.Ui.Controls.SymbolRegular.DeveloperBoard24));
        Capabilities.Add(new(r.Memory, Wpf.Ui.Controls.SymbolRegular.Ram20));
        Capabilities.Add(new(r.Gpu, Wpf.Ui.Controls.SymbolRegular.Games24));
        Drives.Clear();
        foreach (var v in s.Volumes) Drives.Add($"{v.Letter}:\\");
        SelectedDrive = Drives.FirstOrDefault(d => s.Volumes.First(v => d.StartsWith(v.Letter)).IsSystem) ?? Drives.FirstOrDefault();
        ReadCpuPower();
        LoadHistory();
        await LoadGpuAsync();
    }

    private void ReadCpuPower()
    {
        try
        {
            var (min, boost) = _cpuPower.Read();
            _loadingPower = true;
            CpuPerformanceMode = min >= 100 && boost == 2;
            _loadingPower = false;
            var boostText = boost switch { 0 => "desligado", 1 => "ativado", 2 => "agressivo", 3 => "eficiente ativado", 4 => "eficiente agressivo", _ => boost.ToString() };
            CpuPowerText = $"Estado mínimo do processador: {min}% · Turbo: {boostText}";
        }
        catch (Exception ex)
        {
            CpuPowerText = "Não foi possível ler o plano de energia: " + ex.Message;
        }
    }

    partial void OnCpuPerformanceModeChanged(bool value)
    {
        if (_loadingPower) return;
        try
        {
            _cpuPower.Apply(value);
            StatusMessage = value
                ? "Modo desempenho ligado: o processador fica sempre pronto no clock máximo (consome um pouco mais em repouso)."
                : "Configuração original do processador restaurada.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        ReadCpuPower();
    }

    // ======================================================================
    // Benchmarks
    // ======================================================================

    public ObservableCollection<BenchmarkRow> Benchmarks { get; } = [];
    public ObservableCollection<RunOption> History { get; } = [];
    [ObservableProperty] private RunOption? _compareWith;
    [ObservableProperty] private string? _selectedDrive;
    [ObservableProperty] private string _runLabel = "Medição";
    [ObservableProperty] private bool _isBenchmarking;
    [ObservableProperty] private string? _benchSummary;

    private void LoadHistory()
    {
        History.Clear();
        foreach (var r in _history.Load()) History.Add(new RunOption(r));
        CompareWith ??= History.FirstOrDefault();
    }

    partial void OnCompareWithChanged(RunOption? value)
    {
        foreach (var b in Benchmarks)
            b.Previous = value?.Run.Results.FirstOrDefault(r => r.Kind == b.Kind)?.Value;
    }

    [RelayCommand]
    private async Task RunAllBenchmarksAsync()
    {
        if (IsBenchmarking || IsStressing || IsTuning) return;
        _cts = new CancellationTokenSource();
        IsBenchmarking = true;
        BenchSummary = "Feche jogos e programas pesados para medir com precisão.";
        lock (_window) _window.Clear();
        (_bench as BenchmarkService)?.ResetDiskCache();
        var results = new List<BenchmarkResult>();

        try
        {
            foreach (var row in Benchmarks)
            {
                if (row.Kind == BenchmarkKind.GpuCompute && !_bench.IsGpuAvailable) continue;
                row.IsRunning = true;
                row.Progress = 0;
                var progress = new Progress<double>(p => row.Progress = p * 100);
                try
                {
                    var r = await _bench.RunAsync(row.Kind, SelectedDrive, progress, _cts.Token);
                    row.Unit = r.Unit;
                    row.HigherIsBetter = r.HigherIsBetter;
                    row.Value = r.Value;
                    results.Add(r);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Benchmark {Tipo} falhou", row.Kind);
                    BenchSummary = $"{row.Name}: {ex.Message}";
                }
                finally
                {
                    row.IsRunning = false;
                    row.Progress = 100;
                }
            }

            List<LiveMetrics> samples;
            lock (_window) samples = [.. _window];
            var run = new BenchmarkRun(Guid.NewGuid(), DateTime.Now, string.IsNullOrWhiteSpace(RunLabel) ? "Medição" : RunLabel.Trim(), results,
                samples.Max(s => s.CpuTemperature), samples.Max(s => s.GpuTemperature),
                samples.Where(s => s.CpuPowerWatts > 0).Select(s => s.CpuPowerWatts!.Value).DefaultIfEmpty().Average(),
                samples.Where(s => s.GpuPowerWatts > 0).Select(s => s.GpuPowerWatts!.Value).DefaultIfEmpty().Average());
            var previous = CompareWith;
            _history.Save(run);
            LoadHistory();
            CompareWith = previous ?? History.Skip(1).FirstOrDefault();
            BenchSummary = $"Medição \"{run.Label}\" salva. Compare com medições anteriores escolhendo na lista.";
        }
        catch (OperationCanceledException)
        {
            BenchSummary = "Benchmark cancelado.";
        }
        finally
        {
            IsBenchmarking = false;
        }
    }

    [RelayCommand]
    private void DeleteRun(RunOption? option)
    {
        if (option is null) return;
        _history.Delete(option.Run.Id);
        LoadHistory();
    }

    // ======================================================================
    // Teste de estresse
    // ======================================================================

    public IReadOnlyList<int> Durations { get; } = [1, 5, 15, 30];
    [ObservableProperty] private int _stressMinutes = 5;
    [ObservableProperty] private bool _stressOnGpu;
    [ObservableProperty] private bool _isStressing;
    [ObservableProperty] private string _stressElapsed = "0:00";
    [ObservableProperty] private long _stressIterations;
    [ObservableProperty] private long _stressErrors;
    [ObservableProperty] private string? _stressVerdict;
    [ObservableProperty] private bool? _stressStable;
    public ObservableCollection<double> StressTemps { get; } = new(Enumerable.Repeat(0d, 60));

    [RelayCommand]
    private async Task StartStressAsync()
    {
        if (IsStressing || IsBenchmarking || IsTuning) return;
        _cts = new CancellationTokenSource();
        IsStressing = true;
        StressVerdict = null;
        StressStable = null;
        StressErrors = 0;
        StressIterations = 0;
        for (var i = 0; i < StressTemps.Count; i++) StressTemps[i] = 0;
        var duration = TimeSpan.FromMinutes(StressMinutes);

        try
        {
            if (StressOnGpu)
            {
                using var gpu = new GpuCompute(() => _last?.GpuTemperature);
                gpu.CaptureGolden();
                var start = DateTime.Now;
                using var tick = new System.Threading.Timer(_ => App.Current.Dispatcher.BeginInvoke(() => StressElapsed = (DateTime.Now - start).ToString(@"m\:ss")), null, 0, 1000);
                var o = await gpu.RunAsync(duration, _cts.Token);
                StressErrors = o.Errors;
                StressStable = o.Errors == 0 && !o.DeviceLost;
                StressVerdict = o.DeviceLost ? "INSTÁVEL: o driver de vídeo reiniciou durante o teste."
                    : o.Errors > 0 ? $"INSTÁVEL: {o.Errors:N0} valores errados calculados pela GPU."
                    : $"Estável: nenhum erro. Média de {o.GflopsAverage:0} GFLOPS, temperatura máxima {o.MaxTemperature:0} °C.";
            }
            else
            {
                var progress = new Progress<StressStatus>(s =>
                {
                    StressElapsed = s.Elapsed.ToString(@"m\:ss");
                    StressIterations = s.Iterations;
                    StressErrors = s.Errors;
                });
                var r = await _cpuStress.RunAsync(duration, () => _last?.CpuTemperature, 95, progress, _cts.Token);
                StressStable = r.Stable;
                StressVerdict = r.Verdict + (r.MaxTemperature is { } t ? $" Temperatura máxima: {t:0} °C." : string.Empty);
            }
            Log.Information("Estresse {Alvo} {Min} min: {Veredito}", StressOnGpu ? "GPU" : "CPU", StressMinutes, StressVerdict);
        }
        catch (OperationCanceledException)
        {
            StressVerdict = "Teste interrompido.";
        }
        catch (Exception ex)
        {
            StressVerdict = "Não foi possível executar o teste: " + ex.Message;
            Log.Error(ex, "Falha no teste de estresse");
        }
        finally
        {
            IsStressing = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    // ======================================================================
    // Overclock da GPU
    // ======================================================================

    [ObservableProperty] private bool _gpuSupported;
    [ObservableProperty] private string _gpuName = string.Empty;
    [ObservableProperty] private int _coreOffset;
    [ObservableProperty] private int _memoryOffset;
    [ObservableProperty] private int _coreMin = -200;
    [ObservableProperty] private int _coreMax = 250;
    [ObservableProperty] private int _memoryMin;
    [ObservableProperty] private int _memoryMax = 1000;
    [ObservableProperty] private string _appliedText = string.Empty;
    [ObservableProperty] private bool _isTuning;
    [ObservableProperty] private int _stepSeconds = 30;
    public ObservableCollection<AutoTuneStep> TuneLog { get; } = [];

    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportPerformance = string.Empty;
    [ObservableProperty] private string _reportTemperature = string.Empty;
    [ObservableProperty] private string _reportPower = string.Empty;
    [ObservableProperty] private string _reportCost = string.Empty;
    [ObservableProperty] private string _reportOffsets = string.Empty;
    [ObservableProperty] private double _reportBefore;
    [ObservableProperty] private double _reportAfter;

    private async Task LoadGpuAsync()
    {
        GpuSupported = await Task.Run(() => _gpuTuner.IsSupported);
        if (!GpuSupported) return;
        var s = _gpuTuner.Read();
        GpuName = s.GpuName;
        CoreMin = Math.Max(s.CoreMinMhz, -300);
        CoreMax = Math.Min(s.CoreMaxMhz, 300);
        MemoryMin = Math.Max(s.MemoryMinMhz, -500);
        MemoryMax = Math.Min(s.MemoryMaxMhz, 1500);
        CoreOffset = s.CoreOffsetMhz;
        MemoryOffset = s.MemoryOffsetMhz;
        AppliedText = $"Aplicado agora: núcleo {s.CoreOffsetMhz:+0;-0;0} MHz · memória {s.MemoryOffsetMhz:+0;-0;0} MHz";
    }

    [RelayCommand]
    private async Task ApplyGpuAsync()
    {
        if (!await _dialogs.ConfirmAsync("Aplicar overclock",
                $"Aplicar núcleo {CoreOffset:+0;-0;0} MHz e memória {MemoryOffset:+0;-0;0} MHz na {GpuName}?\n\n" +
                "Se a tela piscar ou um jogo travar, volte aqui e clique em \"Restaurar padrão\". Os valores também voltam ao normal ao reiniciar o PC.",
                "Aplicar")) return;
        try
        {
            _gpuTuner.Apply(CoreOffset, MemoryOffset);
            Log.Information("GPU: offsets aplicados manualmente núcleo={Core} memória={Mem}", CoreOffset, MemoryOffset);
            await LoadGpuAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task ResetGpuAsync()
    {
        try
        {
            _gpuTuner.Reset();
            await LoadGpuAsync();
            StatusMessage = "Clocks da placa de vídeo voltaram ao padrão de fábrica.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task AutoTuneAsync()
    {
        if (IsTuning || IsStressing || IsBenchmarking) return;
        var minutes = Math.Ceiling(StepSeconds * 20 / 60.0);
        if (!await _dialogs.ConfirmAsync("Encontrar o limite automaticamente",
                $"O OtimizadorPC vai subir o clock da {GpuName} aos poucos e testar cada passo por {StepSeconds} s com carga verificada.\n\n" +
                $"• Pode levar até ~{minutes:0} minutos.\n• A tela pode piscar se o driver reiniciar — isso é esperado e o app volta ao último valor estável.\n" +
                "• Salve seus trabalhos e feche jogos antes.\n• Para se a GPU passar de 85 °C.\n\nContinuar?", "Começar")) return;

        _cts = new CancellationTokenSource();
        IsTuning = true;
        HasReport = false;
        TuneLog.Clear();

        try
        {
            using var gpu = new GpuCompute(() => _last?.GpuTemperature);
            _gpuTuner.Reset();
            await Task.Delay(1500, _cts.Token);
            gpu.CaptureGolden();

            // Medição antes
            var before = await MeasureGpuAsync(gpu, TimeSpan.FromSeconds(20), _cts.Token);
            TuneLog.Add(new AutoTuneStep("Medição inicial", 0, 0, true, $"{before.Gflops:0} GFLOPS · {before.Temp:0} °C · {before.Power:0} W"));

            var tuner = new GpuAutoTuner(_gpuTuner, gpu);
            var progress = new Progress<AutoTuneStep>(s => TuneLog.Add(s));
            var result = await tuner.RunAsync(new AutoTuneOptions { TestDuration = TimeSpan.FromSeconds(StepSeconds), TemperatureLimit = 85 }, progress, _cts.Token);

            // Medição depois
            var after = await MeasureGpuAsync(gpu, TimeSpan.FromSeconds(20), _cts.Token);
            TuneLog.Add(new AutoTuneStep("Medição final", result.CoreOffsetMhz, result.MemoryOffsetMhz, true, $"{after.Gflops:0} GFLOPS · {after.Temp:0} °C · {after.Power:0} W"));

            BuildReport(before, after, result);
            Log.Information("Ajuste automático da GPU: {Resumo}", result.Summary);
            await LoadGpuAsync();
        }
        catch (OperationCanceledException)
        {
            _gpuTuner.Reset();
            TuneLog.Add(new AutoTuneStep("Cancelado", 0, 0, false, "Busca cancelada. Clocks restaurados ao padrão."));
            await LoadGpuAsync();
        }
        catch (Exception ex)
        {
            try { _gpuTuner.Reset(); } catch (Exception) { }
            TuneLog.Add(new AutoTuneStep("Erro", 0, 0, false, ex.Message));
            Log.Error(ex, "Falha no ajuste automático da GPU");
            await LoadGpuAsync();
        }
        finally
        {
            IsTuning = false;
        }
    }

    private async Task<(double Gflops, double? Temp, double? Power)> MeasureGpuAsync(GpuCompute gpu, TimeSpan duration, CancellationToken ct)
    {
        lock (_window) _window.Clear();
        var outcome = await gpu.RunAsync(duration, ct);
        List<LiveMetrics> samples;
        lock (_window) samples = [.. _window];
        var power = samples.Where(s => s.GpuPowerWatts > 0).Select(s => s.GpuPowerWatts!.Value).DefaultIfEmpty().Average();
        return (outcome.GflopsAverage, outcome.MaxTemperature, power > 0 ? power : null);
    }

    private void BuildReport((double Gflops, double? Temp, double? Power) before, (double Gflops, double? Temp, double? Power) after, AutoTuneResult result)
    {
        var gain = before.Gflops > 0 ? (after.Gflops - before.Gflops) / before.Gflops * 100 : 0;
        ReportBefore = before.Gflops;
        ReportAfter = after.Gflops;
        ReportOffsets = $"Núcleo +{result.CoreOffsetMhz} MHz · Memória +{result.MemoryOffsetMhz} MHz";
        ReportPerformance = $"{before.Gflops:N0} → {after.Gflops:N0} GFLOPS ({gain:+0.0;-0.0}%)";
        ReportTemperature = before.Temp is { } tb && after.Temp is { } ta
            ? $"{tb:0} °C → {ta:0} °C ({ta - tb:+0;-0;0} °C)"
            : "Sem leitura de temperatura";
        if (before.Power is { } pb && after.Power is { } pa)
        {
            var s = _settings.Current;
            var cost = EnergyCost.Monthly(pa - pb, s.DailyGamingHours, s.EnergyPricePerKwh);
            ReportPower = $"{pb:0} W → {pa:0} W ({pa - pb:+0;-0;0} W)";
            ReportCost = cost > 0
                ? $"≈ R$ {cost:N2} por mês a mais (jogando {s.DailyGamingHours:0.#} h/dia, R$ {s.EnergyPricePerKwh:N2}/kWh)"
                : "Sem aumento de consumo perceptível";
        }
        else
        {
            ReportPower = "Sem leitura de consumo";
            ReportCost = "Não foi possível calcular sem a leitura de consumo.";
        }
        HasReport = true;
    }

    public decimal EnergyPrice
    {
        get => _settings.Current.EnergyPricePerKwh;
        set { _settings.Update(s => s.EnergyPricePerKwh = Math.Max(0, value)); OnPropertyChanged(); }
    }

    public double DailyHours
    {
        get => _settings.Current.DailyGamingHours;
        set { _settings.Update(s => s.DailyGamingHours = Math.Clamp(value, 0, 24)); OnPropertyChanged(); }
    }
}
