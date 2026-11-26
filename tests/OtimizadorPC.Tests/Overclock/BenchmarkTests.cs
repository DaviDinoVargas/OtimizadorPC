using OtimizadorPC.Core.Overclock;
using OtimizadorPC.Infrastructure.Overclock;

namespace OtimizadorPC.Tests.Overclock;

/// <summary>Testes curtos e não destrutivos (nenhum clock é alterado).</summary>
public sealed class BenchmarkTests
{
    [Fact]
    public void Carga_de_cpu_e_deterministica()
    {
        Assert.Equal(CpuKernel.Run(42), CpuKernel.Run(42));
        Assert.NotEqual(CpuKernel.Run(1), CpuKernel.Run(2));
    }

    [Fact]
    public async Task Estresse_de_cpu_curto_nao_encontra_erros()
    {
        var report = await new CpuStress().RunAsync(TimeSpan.FromSeconds(2), () => null, 95, null, CancellationToken.None);
        Assert.True(report.Iterations > 0);
        Assert.Equal(0, report.Errors);
        Assert.True(report.Stable);
    }

    [Fact]
    public async Task Benchmarks_de_cpu_e_memoria_retornam_valores()
    {
        var service = new BenchmarkService();
        var single = await service.RunAsync(BenchmarkKind.CpuSingle, null, null, CancellationToken.None);
        var latency = await service.RunAsync(BenchmarkKind.MemoryLatency, null, null, CancellationToken.None);
        Assert.True(single.Value > 0);
        Assert.InRange(latency.Value, 1, 1000);
        Assert.False(latency.HigherIsBetter);
    }

    [Fact]
    public async Task Carga_de_gpu_repete_o_mesmo_resultado()
    {
        if (!GpuCompute.IsAvailable()) return; // máquina sem Direct3D 11 (ex.: servidor de CI)
        using var gpu = new GpuCompute(() => null);
        gpu.CaptureGolden();
        var outcome = await gpu.RunAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(0, outcome.Errors);
        Assert.False(outcome.DeviceLost);
        Assert.True(outcome.GflopsAverage > 0);
    }

    [Fact]
    public void Leitura_dos_clocks_nvidia_fica_dentro_da_faixa()
    {
        var tuner = new NvidiaGpuTuner();
        if (!tuner.IsSupported) return;
        var s = tuner.Read();
        Assert.InRange(s.CoreOffsetMhz, s.CoreMinMhz, s.CoreMaxMhz);
        Assert.True(s.CoreMaxMhz > 0);
    }

    [Fact]
    public void Historico_salva_e_carrega()
    {
        var path = Path.Combine(Path.GetTempPath(), "otpc-bench-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new BenchmarkHistory(path);
            var run = new BenchmarkRun(Guid.NewGuid(), DateTime.Now, "Antes", [new BenchmarkResult(BenchmarkKind.CpuMulti, 1234, "pontos", true)], 70, 60, 65, 50);
            history.Save(run);
            var loaded = Assert.Single(history.Load());
            Assert.Equal("Antes", loaded.Label);
            Assert.Equal(1234, loaded.Results[0].Value);
            history.Delete(run.Id);
            Assert.Empty(history.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
