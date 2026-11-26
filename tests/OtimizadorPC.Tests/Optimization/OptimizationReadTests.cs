using Microsoft.Win32;
using OtimizadorPC.Core.Optimization;
using OtimizadorPC.Infrastructure.Optimization;

namespace OtimizadorPC.Tests.Optimization;

/// <summary>Leituras reais do Windows. Nenhum teste altera configurações do sistema.</summary>
public sealed class OptimizationReadTests
{
    [Fact]
    public void Inicializacao_lista_sem_erro()
    {
        var items = new StartupService().List();
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 7, 0, 0, 0 }, false)]
    public void Estado_do_StartupApproved_segue_o_bit_menos_significativo(byte[] data, bool enabled)
    {
        var keyPath = @"Software\OtimizadorPC.Tests\" + Guid.NewGuid().ToString("N");
        using (var k = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            k.SetValue("App", data, RegistryValueKind.Binary);
            Assert.Equal(enabled, StartupService.IsEnabled(k, "App"));
            Assert.True(StartupService.IsEnabled(k, "SemValor"));
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\OtimizadorPC.Tests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void Servicos_listados_existem_no_catalogo()
    {
        var known = OptimizationCatalog.Services.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(new ServiceTuner().ListKnown(), s => Assert.Contains(s.Definition.Name, known));
    }

    [Fact]
    public void Tarefas_de_terceiros_nao_incluem_as_da_microsoft()
    {
        Assert.All(new ScheduledTaskService().ListThirdParty(),
            t => Assert.False(t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Ajustes_e_planos_de_energia_sao_lidos()
    {
        Assert.NotEmpty(new TweakService().List());
        var plans = new PowerService().ListPlans();
        Assert.Single(plans, p => p.Active);
    }

    [Fact]
    public void Paginacao_e_apps_sao_lidos()
    {
        var cfg = new PageFileService().Read();
        Assert.NotNull(cfg.Files);
        Assert.All(new AppxService().ListRemovable(), a => Assert.False(string.IsNullOrEmpty(a.FullName)));
    }

    [Fact]
    public void Paginacao_na_letra_A_e_recusada_antes_de_mexer_no_sistema()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PageFileService().Write(false, [new PageFileEntry("A", 4096, 8192)]));
        Assert.Contains("A:", ex.Message);
    }
}
