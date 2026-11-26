using OtimizadorPC.Infrastructure.Hardware;

namespace OtimizadorPC.Tests.Hardware;

/// <summary>Coleta real no Windows em que os testes rodam (local ou CI).</summary>
public sealed class HardwareInfoIntegrationTests
{
    [Fact]
    public async Task Snapshot_traz_processador_memoria_e_volumes()
    {
        var snap = await new WmiHardwareInfoService().GetSnapshotAsync();

        Assert.False(string.IsNullOrWhiteSpace(snap.Cpu.Name));
        Assert.True(snap.Cpu.Threads > 0);
        Assert.True(snap.Memory.TotalBytes > 0);
        Assert.NotEmpty(snap.Volumes);
        Assert.Single(snap.Volumes, v => v.IsSystem);
    }

    [Fact]
    public void Sensores_retornam_carga_e_memoria_sem_driver()
    {
        using var sensors = new LhmSensorService();
        var m = sensors.Sample();

        Assert.InRange(m.CpuLoad, 0, 100);
        Assert.True(m.RamTotalBytes > 0);
        Assert.InRange(m.RamUsedPercent, 0, 100);
    }
}
