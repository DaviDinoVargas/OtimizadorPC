using OtimizadorPC.Core.Abstractions;

namespace OtimizadorPC.Core.Overclock;

public sealed record AutoTuneOptions
{
    public int CoreStepMhz { get; init; } = 15;
    public int CoreMaxMhz { get; init; } = 250;
    public int MemoryStepMhz { get; init; } = 50;
    public int MemoryMaxMhz { get; init; } = 1000;
    /// <summary>Quanto recuar do último valor estável para ter folga.</summary>
    public int SafetyMarginCoreMhz { get; init; } = 15;
    public int SafetyMarginMemoryMhz { get; init; } = 50;
    public TimeSpan TestDuration { get; init; } = TimeSpan.FromSeconds(30);
    public double TemperatureLimit { get; init; } = 85;
}

/// <summary>
/// Busca automática do overclock estável da GPU: sobe o clock em passos, testa com carga
/// verificada (os resultados precisam bater sempre) e para ao primeiro erro, perda do driver
/// ou temperatura alta — voltando ao último valor bom menos uma margem de segurança.
/// </summary>
public sealed class GpuAutoTuner
{
    private readonly IGpuTuner _tuner;
    private readonly IGpuStress _stress;

    public GpuAutoTuner(IGpuTuner tuner, IGpuStress stress)
    {
        _tuner = tuner;
        _stress = stress;
    }

    public async Task<AutoTuneResult> RunAsync(AutoTuneOptions options, IProgress<AutoTuneStep>? progress, CancellationToken ct)
    {
        var steps = new List<AutoTuneStep>();
        var start = _tuner.Read();
        var maxCore = Math.Min(options.CoreMaxMhz, start.CoreMaxMhz);
        var maxMem = Math.Min(options.MemoryMaxMhz, start.MemoryMaxMhz);

        void Log(AutoTuneStep s)
        {
            steps.Add(s);
            progress?.Report(s);
        }

        try
        {
            // Referência em clocks originais: se já falhar aqui, não há o que ajustar
            _tuner.Apply(0, 0);
            var baseline = await _stress.RunAsync(options.TestDuration, ct);
            if (baseline.Errors > 0 || baseline.DeviceLost)
            {
                Log(new("Referência", 0, 0, false, "A placa apresentou erros já nos clocks originais. Overclock não recomendado."));
                return new AutoTuneResult(0, 0, steps, "A placa não está estável nem nos clocks de fábrica; nada foi alterado.");
            }
            Log(new("Referência", 0, 0, true, $"Estável nos clocks originais ({baseline.GflopsAverage:0} GFLOPS)."));

            var core = await Climb("Núcleo", options.CoreStepMhz, maxCore, options.SafetyMarginCoreMhz,
                off => (off, 0), options, Log, ct);
            var mem = await Climb("Memória", options.MemoryStepMhz, maxMem, options.SafetyMarginMemoryMhz,
                off => (core, off), options, Log, ct);

            // Validação final com os dois ajustes juntos e o dobro do tempo
            _tuner.Apply(core, mem);
            var final = await _stress.RunAsync(options.TestDuration * 2, ct);
            if (final.Errors > 0 || final.DeviceLost || final.MaxTemperature > options.TemperatureLimit)
            {
                core = Math.Max(0, core - options.SafetyMarginCoreMhz);
                mem = Math.Max(0, mem - options.SafetyMarginMemoryMhz);
                _tuner.Apply(core, mem);
                Log(new("Validação final", core, mem, false, "Instável com os dois ajustes juntos; reduzido mais um pouco."));
            }
            else
            {
                Log(new("Validação final", core, mem, true, $"Estável: {final.GflopsAverage:0} GFLOPS (antes {baseline.GflopsAverage:0})."));
            }

            return new AutoTuneResult(core, mem, steps, $"Limite estável encontrado: núcleo +{core} MHz e memória +{mem} MHz.");
        }
        catch (OperationCanceledException)
        {
            _tuner.Apply(start.CoreOffsetMhz, start.MemoryOffsetMhz);
            throw;
        }
        catch (Exception)
        {
            // Qualquer falha inesperada devolve os clocks originais
            _tuner.Reset();
            throw;
        }
    }

    private async Task<int> Climb(string stage, int step, int max, int margin, Func<int, (int Core, int Mem)> map,
        AutoTuneOptions options, Action<AutoTuneStep> log, CancellationToken ct)
    {
        var lastGood = 0;
        for (var offset = step; offset <= max; offset += step)
        {
            ct.ThrowIfCancellationRequested();
            var (c, m) = map(offset);
            _tuner.Apply(c, m);
            var r = await _stress.RunAsync(options.TestDuration, ct);

            var hot = r.MaxTemperature is { } t && t > options.TemperatureLimit;
            if (r.Errors > 0 || r.DeviceLost || hot)
            {
                var reason = r.DeviceLost ? "o driver de vídeo reiniciou" : hot ? $"temperatura chegou a {r.MaxTemperature:0} °C" : $"{r.Errors} erros de cálculo";
                var safe = Math.Max(0, lastGood - margin);
                var (sc, sm) = map(safe);
                _tuner.Apply(sc, sm);
                log(new(stage, c, m, false, $"Falhou em +{offset} MHz ({reason}). Fica em +{safe} MHz."));
                return safe;
            }

            lastGood = offset;
            log(new(stage, c, m, true, $"+{offset} MHz estável ({r.GflopsAverage:0} GFLOPS, {r.MaxTemperature:0} °C)."));
        }

        var final = Math.Max(0, lastGood - margin);
        log(new(stage, map(final).Core, map(final).Mem, true, $"Chegou ao limite permitido; usando +{final} MHz com margem."));
        return final;
    }
}
