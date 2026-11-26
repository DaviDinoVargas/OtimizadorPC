using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Core.Overclock;

public enum OcSupport
{
    /// <summary>O OtimizadorPC consegue ajustar diretamente.</summary>
    PeloApp,
    /// <summary>Possível, mas só pela BIOS ou pelo software do fabricante.</summary>
    PelaBios,
    /// <summary>O hardware não permite.</summary>
    NaoSuportado,
}

public sealed record OcCapability(string Component, string Model, OcSupport Support, string Explanation, string? HowTo);

public sealed record OcReport(OcCapability Cpu, OcCapability Memory, OcCapability Gpu);

// ---------------- Benchmarks ----------------

public enum BenchmarkKind
{
    CpuSingle,
    CpuMulti,
    MemoryBandwidth,
    MemoryLatency,
    DiskSequentialRead,
    DiskSequentialWrite,
    DiskRandom4K,
    GpuCompute,
}

public sealed record BenchmarkResult(BenchmarkKind Kind, double Value, string Unit, bool HigherIsBetter)
{
    public string Name => Kind switch
    {
        BenchmarkKind.CpuSingle => "Processador (1 núcleo)",
        BenchmarkKind.CpuMulti => "Processador (todos os núcleos)",
        BenchmarkKind.MemoryBandwidth => "Memória: velocidade de cópia",
        BenchmarkKind.MemoryLatency => "Memória: latência",
        BenchmarkKind.DiskSequentialRead => "Disco: leitura sequencial",
        BenchmarkKind.DiskSequentialWrite => "Disco: gravação sequencial",
        BenchmarkKind.DiskRandom4K => "Disco: leitura aleatória 4K",
        _ => "Placa de vídeo: computação",
    };
}

public sealed record BenchmarkRun(
    Guid Id,
    DateTime When,
    string Label,
    IReadOnlyList<BenchmarkResult> Results,
    double? PeakCpuTemperature,
    double? PeakGpuTemperature,
    double? AverageCpuPower,
    double? AverageGpuPower);

public sealed record ComparisonRow(BenchmarkKind Kind, string Name, string Unit, double? Before, double? After)
{
    /// <summary>Variação positiva = melhorou (considerando se "maior é melhor").</summary>
    public double? ChangePercent { get; init; }
}

// ---------------- Estresse ----------------

public sealed record StressStatus(TimeSpan Elapsed, long Iterations, long Errors, double? Temperature, string? Message);

public sealed record StressReport(TimeSpan Duration, long Iterations, long Errors, double? MaxTemperature, bool Aborted, string Verdict)
{
    public bool Stable => Errors == 0 && !Aborted;
}

// ---------------- GPU ----------------

public sealed record GpuClockState(
    string GpuName,
    int CoreOffsetMhz,
    int MemoryOffsetMhz,
    int CoreMinMhz,
    int CoreMaxMhz,
    int MemoryMinMhz,
    int MemoryMaxMhz);

public sealed record GpuStressOutcome(long Errors, bool DeviceLost, double? MaxTemperature, double GflopsAverage);

public sealed record AutoTuneStep(string Stage, int CoreOffsetMhz, int MemoryOffsetMhz, bool Passed, string Detail);

public sealed record AutoTuneResult(int CoreOffsetMhz, int MemoryOffsetMhz, IReadOnlyList<AutoTuneStep> Steps, string Summary);

/// <summary>Cálculo do custo do desempenho extra.</summary>
public static class EnergyCost
{
    /// <summary>Custo mensal (30 dias) de um aumento de consumo, em reais.</summary>
    public static decimal Monthly(double extraWatts, double hoursPerDay, decimal pricePerKwh)
    {
        if (extraWatts <= 0 || hoursPerDay <= 0) return 0;
        var kwh = (decimal)(extraWatts * hoursPerDay * 30 / 1000.0);
        return Math.Round(kwh * pricePerKwh, 2);
    }

    /// <summary>Diferença percentual levando em conta se valores maiores são melhores.</summary>
    public static double? Change(double? before, double? after, bool higherIsBetter)
    {
        if (before is not { } b || after is not { } a || b == 0) return null;
        var pct = (a - b) / b * 100;
        return higherIsBetter ? pct : -pct;
    }
}

public static class GpuVendorExtensions
{
    public static bool SupportsAppOverclock(this GpuVendor vendor) => vendor == GpuVendor.Nvidia;
}
