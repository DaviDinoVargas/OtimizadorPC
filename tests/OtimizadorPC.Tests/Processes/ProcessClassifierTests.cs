using OtimizadorPC.Core.Processes;

namespace OtimizadorPC.Tests.Processes;

public sealed class ProcessClassifierTests
{
    [Theory]
    [InlineData("lsass")]
    [InlineData("csrss")]
    [InlineData("svchost")]
    [InlineData("explorer")]
    [InlineData("MsMpEng")]
    [InlineData("OtimizadorPC")]
    public void Processos_criticos_sao_sistema(string name)
    {
        Assert.Equal(ProcessCategory.Sistema, ProcessClassifier.Classify(name, null, hasWindow: true).Category);
    }

    [Fact]
    public void Executavel_dentro_do_Windows_e_sistema()
    {
        var (cat, _) = ProcessClassifier.Classify("qualquer", @"C:\Windows\System32\qualquer.exe", false);
        Assert.Equal(ProcessCategory.Sistema, cat);
    }

    [Fact]
    public void Programa_com_janela_e_aplicativo_mesmo_se_estiver_na_lista_de_dispensaveis()
    {
        Assert.Equal(ProcessCategory.Aplicativo, ProcessClassifier.Classify("EpicGamesLauncher", @"D:\Epic\launcher.exe", true).Category);
    }

    [Theory]
    [InlineData("GoogleUpdate")]
    [InlineData("browser_assistant")]
    [InlineData("SomeVendorUpdater")]
    [InlineData("AppCrashHandler")]
    public void Ajudantes_sem_janela_sao_dispensaveis(string name)
    {
        var (cat, reason) = ProcessClassifier.Classify(name, @"C:\Program Files\X\x.exe", false);
        Assert.Equal(ProcessCategory.Dispensavel, cat);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Desconhecido_sem_janela_fica_em_segundo_plano()
    {
        Assert.Equal(ProcessCategory.SegundoPlano, ProcessClassifier.Classify("driverhelper", @"C:\Program Files\Vendor\driverhelper.exe", false).Category);
    }

    [Fact]
    public void Agrupa_pelo_executavel_e_soma_memoria_e_cpu()
    {
        var path = @"C:\Apps\opera.exe";
        ProcessSample S(int pid, long mem, double cpu, bool window) =>
            new(pid, "opera", path, "Opera GX", "Opera", mem, cpu, 10, window, window ? "Página" : null,
                window ? ProcessCategory.Aplicativo : ProcessCategory.SegundoPlano, null);

        var groups = ProcessClassifier.Group([S(1, 100, 1.5, false), S(2, 300, 2.5, true), S(3, 50, 0, false)]);

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Count);
        Assert.Equal(450, g.MemoryBytes);
        Assert.Equal(4.0, g.CpuPercent, 3);
        Assert.Equal(ProcessCategory.Aplicativo, g.Category);
        Assert.Equal("Opera GX", g.DisplayName);
    }
}
