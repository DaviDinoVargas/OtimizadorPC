using OtimizadorPC.Core.Overclock;

namespace OtimizadorPC.Core.Abstractions;

public interface IBenchmarkService
{
    /// <summary>Executa um benchmark. <paramref name="diskRoot"/> é usado só nos testes de disco.</summary>
    Task<BenchmarkResult> RunAsync(BenchmarkKind kind, string? diskRoot, IProgress<double>? progress, CancellationToken ct);

    bool IsGpuAvailable { get; }
}

public interface ICpuStress
{
    /// <summary>Carrega todos os núcleos com cálculos verificados; qualquer resultado diferente conta como erro.</summary>
    Task<StressReport> RunAsync(TimeSpan duration, Func<double?> temperature, double temperatureLimit,
        IProgress<StressStatus>? progress, CancellationToken ct);
}

public interface IGpuStress
{
    Task<GpuStressOutcome> RunAsync(TimeSpan duration, CancellationToken ct);
}

public interface IGpuTuner
{
    bool IsSupported { get; }
    GpuClockState Read();

    /// <summary>Aplica deslocamentos de clock (MHz) no estado de desempenho máximo.</summary>
    void Apply(int coreOffsetMhz, int memoryOffsetMhz);

    void Reset();
}

public interface ICpuPowerTuner
{
    /// <summary>Estado mínimo do processador (%) e modo de turbo do plano ativo.</summary>
    (int MinimumStatePercent, int BoostMode) Read();

    /// <summary>Desempenho: mínimo 100% e turbo agressivo. Padrão: valores de fábrica do Windows.</summary>
    void Apply(bool performance);
}

public interface IBenchmarkHistory
{
    IReadOnlyList<BenchmarkRun> Load();
    void Save(BenchmarkRun run);
    void Delete(Guid id);
}
