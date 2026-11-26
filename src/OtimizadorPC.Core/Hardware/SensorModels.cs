namespace OtimizadorPC.Core.Hardware;

public enum SensorKind { Carga, Temperatura, Clock, Potencia, Ventoinha, Tensao, Dados, Outro }

public sealed record SensorReading(
    string Hardware,
    string Name,
    SensorKind Kind,
    float? Value,
    float? Min,
    float? Max)
{
    public string Unit => Kind switch
    {
        SensorKind.Carga => "%",
        SensorKind.Temperatura => "°C",
        SensorKind.Clock => "MHz",
        SensorKind.Potencia => "W",
        SensorKind.Ventoinha => "RPM",
        SensorKind.Tensao => "V",
        SensorKind.Dados => "GB",
        _ => string.Empty
    };
}

/// <summary>Leituras em tempo real usadas pelo painel e pelo overclock.</summary>
public sealed record LiveMetrics(
    DateTime Time,
    double CpuLoad,
    double? CpuTemperature,
    double? CpuClockMhz,
    double? CpuPowerWatts,
    double RamUsedPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    double? GpuLoad,
    double? GpuTemperature,
    double? GpuPowerWatts,
    double? GpuCoreClockMhz,
    double? GpuMemoryClockMhz,
    IReadOnlyList<SensorReading> Sensors);
