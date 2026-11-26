using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Overclock;
using OtimizadorPC.Infrastructure.Power;
using OtimizadorPC.Infrastructure.Settings;

namespace OtimizadorPC.Infrastructure.Overclock;

// =====================================================================
// GPU NVIDIA (NVAPI)
// =====================================================================

/// <summary>
/// Deslocamento de clock do núcleo e da memória pelo driver NVIDIA (o mesmo mecanismo de
/// ferramentas como o MSI Afterburner). Os valores voltam ao padrão ao reiniciar o PC.
/// </summary>
public sealed class NvidiaGpuTuner : IGpuTuner
{
    private readonly Lazy<PhysicalGPU?> _gpu = new(() =>
    {
        try
        {
            NVIDIA.Initialize();
            return PhysicalGPU.GetPhysicalGPUs().FirstOrDefault();
        }
        catch (Exception)
        {
            return null; // sem placa NVIDIA ou driver sem NVAPI
        }
    });

    public bool IsSupported
    {
        get
        {
            try { return _gpu.Value is not null && GPUApi.GetPerformanceStates20(_gpu.Value.Handle).IsEditable; }
            catch (Exception) { return false; }
        }
    }

    public GpuClockState Read()
    {
        var gpu = _gpu.Value ?? throw new NotSupportedException("Nenhuma placa NVIDIA encontrada.");
        var info = GPUApi.GetPerformanceStates20(gpu.Handle);
        var p0 = info.Clocks[PerformanceStateId.P0_3DPerformance];
        var core = p0.First(c => c.DomainId == PublicClockDomain.Graphics).FrequencyDeltaInkHz;
        var mem = p0.First(c => c.DomainId == PublicClockDomain.Memory).FrequencyDeltaInkHz;
        return new GpuClockState(gpu.FullName,
            core.DeltaValue / 1000, mem.DeltaValue / 1000,
            core.DeltaRange.Minimum / 1000, core.DeltaRange.Maximum / 1000,
            mem.DeltaRange.Minimum / 1000, mem.DeltaRange.Maximum / 1000);
    }

    public void Apply(int coreOffsetMhz, int memoryOffsetMhz)
    {
        var gpu = _gpu.Value ?? throw new NotSupportedException("Nenhuma placa NVIDIA encontrada.");
        var state = Read();
        var core = Math.Clamp(coreOffsetMhz, state.CoreMinMhz, state.CoreMaxMhz);
        var mem = Math.Clamp(memoryOffsetMhz, state.MemoryMinMhz, state.MemoryMaxMhz);

        var clocks = new[]
        {
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000)),
            new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(mem * 1000)),
        };
        var p0 = new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, []);
        GPUApi.SetPerformanceStates20(gpu.Handle, new PerformanceStates20InfoV1([p0], 2, 0));
    }

    public void Reset() => Apply(0, 0);
}

// =====================================================================
// Ajuste do processador pelo Windows
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class CpuPowerTuner : ICpuPowerTuner
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static string BackupPath => Path.Combine(JsonSettingsService.DataFolder, "cpu-energia-original.json");

    private sealed record Backup(int MinState, int Boost);

    public (int MinimumStatePercent, int BoostMode) Read()
    {
        var scheme = PowerPlans.GetActiveId() ?? throw new InvalidOperationException("Plano de energia não encontrado.");
        return ((int)ReadAc(scheme, MinState), (int)ReadAc(scheme, BoostMode));
    }

    public void Apply(bool performance)
    {
        var scheme = PowerPlans.GetActiveId() ?? throw new InvalidOperationException("Plano de energia não encontrado.");
        if (performance)
        {
            if (!File.Exists(BackupPath))
            {
                var (min, boost) = Read();
                Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
                File.WriteAllText(BackupPath, JsonSerializer.Serialize(new Backup(min, boost)));
            }
            WriteAc(scheme, MinState, 100);
            WriteAc(scheme, BoostMode, 2); // Agressivo
        }
        else
        {
            var backup = File.Exists(BackupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)) : null;
            WriteAc(scheme, MinState, (uint)(backup?.MinState ?? 5));
            WriteAc(scheme, BoostMode, (uint)(backup?.Boost ?? 1));
            File.Delete(BackupPath);
        }
        PowerPlans.SetActive(scheme); // reaplica para valer na hora
    }

    private static uint ReadAc(Guid scheme, Guid setting)
    {
        var sub = SubProcessor;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value) == 0 ? value : 0;
    }

    private static void WriteAc(Guid scheme, Guid setting, uint value)
    {
        var sub = SubProcessor;
        var r = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (r != 0) throw new InvalidOperationException(r == 5 ? "Sem permissão para alterar o plano de energia." : $"Falha ao alterar o plano de energia ({r}).");
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subGroup, ref Guid setting, uint value);
}

// =====================================================================
// Histórico de benchmarks
// =====================================================================

public sealed class BenchmarkHistory : IBenchmarkHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();

    public BenchmarkHistory() : this(Path.Combine(JsonSettingsService.DataFolder, "benchmarks.json")) { }

    public BenchmarkHistory(string path) => _path = path;

    public IReadOnlyList<BenchmarkRun> Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(_path), Options) ?? []
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public void Save(BenchmarkRun run)
    {
        lock (_lock)
        {
            var list = Load().Where(r => r.Id != run.Id).Append(run).OrderByDescending(r => r.When).Take(50).ToList();
            Write(list);
        }
    }

    public void Delete(Guid id)
    {
        lock (_lock) Write(Load().Where(r => r.Id != id).ToList());
    }

    private void Write(List<BenchmarkRun> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(list, Options));
    }
}

// =====================================================================
// Fachada dos benchmarks
// =====================================================================

public sealed class BenchmarkService : IBenchmarkService
{
    private readonly Lazy<bool> _gpu = new(GpuCompute.IsAvailable);
    private DiskBenchmark.DiskResult? _lastDisk;
    private string? _lastDiskRoot;

    public bool IsGpuAvailable => _gpu.Value;

    public Task<BenchmarkResult> RunAsync(BenchmarkKind kind, string? diskRoot, IProgress<double>? progress, CancellationToken ct) =>
        Task.Factory.StartNew(() => kind switch
        {
            BenchmarkKind.CpuSingle => new BenchmarkResult(kind, CpuBenchmark.Measure(1, TimeSpan.FromSeconds(4), progress, ct), "pontos", true),
            BenchmarkKind.CpuMulti => new BenchmarkResult(kind, CpuBenchmark.Measure(Environment.ProcessorCount, TimeSpan.FromSeconds(4), progress, ct), "pontos", true),
            BenchmarkKind.MemoryBandwidth => new BenchmarkResult(kind, MemoryBenchmark.Bandwidth(progress, ct), "GB/s", true),
            BenchmarkKind.MemoryLatency => new BenchmarkResult(kind, MemoryBenchmark.Latency(progress, ct), "ns", false),
            BenchmarkKind.DiskSequentialWrite => new BenchmarkResult(kind, Disk(diskRoot, progress, ct).WriteMBs, "MB/s", true),
            BenchmarkKind.DiskSequentialRead => new BenchmarkResult(kind, Disk(diskRoot, progress, ct).ReadMBs, "MB/s", true),
            BenchmarkKind.DiskRandom4K => new BenchmarkResult(kind, Disk(diskRoot, progress, ct).RandomIops, "IOPS", true),
            BenchmarkKind.GpuCompute => new BenchmarkResult(kind, GpuBench(progress, ct), "GFLOPS", true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Os três testes de disco usam a mesma execução (evita gravar 1 GB três vezes).</summary>
    private DiskBenchmark.DiskResult Disk(string? root, IProgress<double>? progress, CancellationToken ct)
    {
        root ??= Path.GetPathRoot(Environment.SystemDirectory)!;
        if (_lastDisk is null || !string.Equals(_lastDiskRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _lastDisk = DiskBenchmark.Run(root, progress, ct);
            _lastDiskRoot = root;
        }
        return _lastDisk;
    }

    /// <summary>Esquece o último teste de disco para que a próxima rodada meça de novo.</summary>
    public void ResetDiskCache() => _lastDisk = null;

    private static double GpuBench(IProgress<double>? progress, CancellationToken ct)
    {
        using var gpu = new GpuCompute(() => null);
        return gpu.Benchmark(progress, ct);
    }
}
