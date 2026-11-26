using OtimizadorPC.Core.Diagnostics;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Tests.Diagnostics;

public sealed class SystemAdvisorTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Hoje = new(2026, 11, 26);

    private static SystemSnapshot Snapshot(
        IReadOnlyList<VolumeInfo>? volumes = null,
        IReadOnlyList<PageFileInfo>? pageFiles = null,
        IReadOnlyList<MemoryModule>? modules = null,
        long ramTotal = 16 * Gb,
        long ramFree = 8 * Gb,
        string powerPlan = "Equilibrado",
        TimeSpan? uptime = null,
        DateTime? biosDate = null,
        IReadOnlyList<PhysicalDiskInfo>? disks = null) =>
        new(
            new OsInfo("Windows 11 Pro", "25H2", "26200", "64 bits", "PC", null, uptime ?? TimeSpan.FromHours(2)),
            new CpuInfo("Intel Core i7-10700KF", "GenuineIntel", 8, 16, 3800, 3801, 2048, 16384, "LGA1200"),
            new BoardInfo("ASUSTeK", "PRIME H510M-E", "AMI", "1203", biosDate ?? Hoje.AddMonths(-6)),
            new MemoryInfo(ramTotal, ramFree, 2, modules ?? []),
            [],
            disks ?? [],
            volumes ?? [new VolumeInfo("C", "Sistema", "NTFS", 100 * Gb, 50 * Gb, 1, DiskMediaKind.SSD, true)],
            pageFiles ?? [],
            powerPlan,
            true,
            Hoje);

    [Fact]
    public void Sistema_saudavel_nao_gera_recomendacoes()
    {
        Assert.Empty(SystemAdvisor.Analyze(Snapshot(), Hoje));
    }

    [Fact]
    public void Disco_com_menos_de_5_por_cento_livre_e_critico()
    {
        var r = SystemAdvisor.Analyze(Snapshot(volumes: [new("C", "", "NTFS", 100 * Gb, 4 * Gb, 1, DiskMediaKind.SSD, true)]), Hoje);
        var rec = Assert.Single(r);
        Assert.Equal(Severity.Critico, rec.Severity);
        Assert.Equal("limpeza", rec.TargetModule);
    }

    [Fact]
    public void Disco_entre_5_e_10_por_cento_e_aviso_para_o_dinosize()
    {
        var r = SystemAdvisor.Analyze(Snapshot(volumes: [new("D", "", "NTFS", 100 * Gb, 8 * Gb, 0, DiskMediaKind.HDD, false)]), Hoje);
        var rec = Assert.Single(r);
        Assert.Equal(Severity.Aviso, rec.Severity);
        Assert.Equal("dinosize", rec.TargetModule);
    }

    [Fact]
    public void Paginacao_no_hd_com_ssd_disponivel_sugere_mover()
    {
        var volumes = new List<VolumeInfo>
        {
            new("C", "", "NTFS", 100 * Gb, 50 * Gb, 1, DiskMediaKind.SSD, true),
            new("E", "", "NTFS", 400 * Gb, 200 * Gb, 0, DiskMediaKind.HDD, false),
        };
        var r = SystemAdvisor.Analyze(Snapshot(volumes: volumes, pageFiles: [new(@"E:\pagefile.sys", 8192, 0, false)]), Hoje);
        Assert.Contains(r, x => x.Id == "paginacao-hdd");
    }

    [Fact]
    public void Ssd_apenas_na_letra_A_nao_conta_para_paginacao()
    {
        var volumes = new List<VolumeInfo>
        {
            new("A", "", "NTFS", 100 * Gb, 50 * Gb, 1, DiskMediaKind.SSD, false),
            new("E", "", "NTFS", 400 * Gb, 200 * Gb, 0, DiskMediaKind.HDD, true),
        };
        var r = SystemAdvisor.Analyze(Snapshot(volumes: volumes, pageFiles: [new(@"E:\pagefile.sys", 8192, 0, false)]), Hoje);
        Assert.DoesNotContain(r, x => x.Id == "paginacao-hdd");
    }

    [Fact]
    public void Paginacao_temporaria_gera_aviso()
    {
        var r = SystemAdvisor.Analyze(Snapshot(pageFiles: [new(@"D:\pagefile.sys", 11776, 0, true)]), Hoje);
        Assert.Contains(r, x => x.Id == "paginacao-temporaria" && x.Severity == Severity.Aviso);
    }

    [Fact]
    public void Memoria_abaixo_da_nominal_sugere_xmp()
    {
        var r = SystemAdvisor.Analyze(Snapshot(modules: [new("DIMM1", "Kingston", "KF432", 8 * Gb, 3200, 2133)]), Hoje);
        Assert.Contains(r, x => x.Id == "ram-xmp");
    }

    [Fact]
    public void Memoria_ja_na_velocidade_configurada_maior_nao_sugere_xmp()
    {
        // Caso real: WMI informa Speed=2133 (JEDEC) e ConfiguredClockSpeed=2933 (já acima)
        var r = SystemAdvisor.Analyze(Snapshot(modules: [new("DIMM1", "Apacer", "GD2", 8 * Gb, 2133, 2933)]), Hoje);
        Assert.DoesNotContain(r, x => x.Id == "ram-xmp");
    }

    [Fact]
    public void Pouca_ram_gera_aviso()
    {
        var r = SystemAdvisor.Analyze(Snapshot(ramTotal: 4 * Gb, ramFree: 2 * Gb), Hoje);
        Assert.Contains(r, x => x.Id == "ram-pouca");
    }

    [Fact]
    public void Plano_economia_bios_antiga_e_uptime_longo_geram_dicas()
    {
        var r = SystemAdvisor.Analyze(Snapshot(powerPlan: "Economia de energia", uptime: TimeSpan.FromDays(10), biosDate: new DateTime(2021, 10, 26)), Hoje);
        Assert.Contains(r, x => x.Id == "energia");
        Assert.Contains(r, x => x.Id == "uptime");
        Assert.Contains(r, x => x.Id == "bios");
    }

    [Fact]
    public void Disco_com_defeito_e_critico_e_vem_primeiro()
    {
        var r = SystemAdvisor.Analyze(Snapshot(disks: [new(0, "WDC", DiskMediaKind.HDD, "SATA", 1000 * Gb, "Com defeito")], powerPlan: "Economia de energia"), Hoje);
        Assert.Equal("saude-0", r[0].Id);
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("b", false)]
    [InlineData("C", true)]
    [InlineData("E", true)]
    public void Letras_validas_para_paginacao(string letra, bool esperado)
    {
        Assert.Equal(esperado, SystemAdvisor.IsPageFileCapableLetter(letra));
    }
}
