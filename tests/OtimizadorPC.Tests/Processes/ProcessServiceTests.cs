using System.Diagnostics;
using OtimizadorPC.Core.Processes;
using OtimizadorPC.Infrastructure.Processes;

namespace OtimizadorPC.Tests.Processes;

public sealed class ProcessServiceTests
{
    [Fact]
    public void Leitura_traz_o_proprio_processo_com_memoria_e_caminho()
    {
        var service = new ProcessService();
        service.Sample();
        var samples = service.Sample();

        var self = Assert.Single(samples, s => s.Pid == Environment.ProcessId);
        Assert.True(self.MemoryBytes > 0);
        Assert.False(string.IsNullOrEmpty(self.Path));
        Assert.All(samples, s => Assert.InRange(s.CpuPercent, 0, 100));
    }

    [Fact]
    public void Recusa_encerrar_processo_critico()
    {
        var lsass = Process.GetProcessesByName("lsass").FirstOrDefault();
        if (lsass is null) return;
        Assert.Throws<InvalidOperationException>(() => new ProcessService().Kill(lsass.Id, false));
    }

    [Fact]
    public void Encerra_um_processo_comum()
    {
        using var p = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        new ProcessService().Kill(p.Id, entireTree: true);
        Assert.True(p.HasExited);
    }

    [Fact]
    public void Sistema_e_protegido_no_agrupamento()
    {
        var groups = ProcessClassifier.Group(new ProcessService().Sample());
        Assert.Contains(groups, g => g.IsProtected);
    }
}
