using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Hardware;
using OtimizadorPC.Core.Overclock;

namespace OtimizadorPC.Tests.Overclock;

public sealed class OverclockTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static SystemSnapshot Pc(string cpu, string vendor, string board, int rated, int configured, GpuVendor gpu = GpuVendor.Nvidia) =>
        new(new OsInfo("Windows 11", "25H2", "1", "64", "PC", null, TimeSpan.Zero),
            new CpuInfo(cpu, vendor, 8, 16, 3800, 3800, 0, 0, ""),
            new BoardInfo("ASUS", board, "AMI", "1", DateTime.Today),
            new MemoryInfo(16 * Gb, 8 * Gb, 2, [new MemoryModule("A", "X", "P", 8 * Gb, rated, configured)]),
            [new GpuInfo(gpu == GpuVendor.Nvidia ? "NVIDIA GeForce GTX 1650" : "AMD Radeon RX 6600", gpu, "1", null, 4 * Gb)],
            [], [], [], "Equilibrado", true, DateTime.Now);

    [Theory]
    [InlineData("PRIME H510M-E", "H510")]
    [InlineData("ROG STRIX Z790-E GAMING", "Z790")]
    [InlineData("B550 AORUS ELITE", "B550")]
    [InlineData("TUF GAMING X570-PLUS", "X570")]
    public void Extrai_o_chipset_da_placa(string board, string chipset) => Assert.Equal(chipset, OverclockAdvisor.Chipset(board));

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-10700KF CPU @ 3.80GHz", true)]
    [InlineData("Intel(R) Core(TM) i9-13900K", true)]
    [InlineData("Intel(R) Core(TM) i5-12400F", false)]
    [InlineData("Intel(R) Core(TM) i7-10700 CPU", false)]
    public void Detecta_intel_desbloqueado(string name, bool unlocked) => Assert.Equal(unlocked, OverclockAdvisor.IsIntelUnlocked(name));

    [Fact]
    public void Caso_real_i7_10700KF_em_H510_sem_overclock_de_cpu_e_ram_no_limite()
    {
        var r = OverclockAdvisor.Analyze(Pc("Intel(R) Core(TM) i7-10700KF CPU @ 3.80GHz", "GenuineIntel", "PRIME H510M-E", 2133, 2933));
        Assert.Equal(OcSupport.NaoSuportado, r.Cpu.Support);
        Assert.Contains("H510", r.Cpu.Explanation);
        Assert.Equal(OcSupport.NaoSuportado, r.Memory.Support);
        Assert.Contains("2933", r.Memory.Explanation);
        Assert.Equal(OcSupport.PeloApp, r.Gpu.Support);
    }

    [Fact]
    public void Intel_K_em_placa_Z_permite_pela_bios()
    {
        var r = OverclockAdvisor.Analyze(Pc("Intel(R) Core(TM) i9-12900K", "GenuineIntel", "ROG STRIX Z690-A", 3200, 2133));
        Assert.Equal(OcSupport.PelaBios, r.Cpu.Support);
        Assert.Equal(OcSupport.PelaBios, r.Memory.Support);
        Assert.Contains("XMP", r.Memory.HowTo);
    }

    [Fact]
    public void Ryzen_em_B550_permite_e_radeon_vai_pelo_adrenalin()
    {
        var r = OverclockAdvisor.Analyze(Pc("AMD Ryzen 5 5600X 6-Core Processor", "AuthenticAMD", "B550M PRO-VDH", 3600, 3600, GpuVendor.Amd));
        Assert.Equal(OcSupport.PelaBios, r.Cpu.Support);
        Assert.Contains("PBO", r.Cpu.HowTo);
        Assert.Equal(OcSupport.PelaBios, r.Gpu.Support);
        Assert.Contains("AMD Software", r.Gpu.HowTo);
    }

    [Fact]
    public void Custo_mensal_de_energia()
    {
        // 20 W a mais, 3 h por dia, R$ 0,85/kWh → 20*3*30/1000 = 1,8 kWh → R$ 1,53
        Assert.Equal(1.53m, EnergyCost.Monthly(20, 3, 0.85m));
        Assert.Equal(0m, EnergyCost.Monthly(-5, 3, 0.85m));
    }

    [Fact]
    public void Variacao_percentual_respeita_o_sentido_da_metrica()
    {
        Assert.Equal(10, EnergyCost.Change(100, 110, higherIsBetter: true)!.Value, 6);
        Assert.Equal(10, EnergyCost.Change(100, 90, higherIsBetter: false)!.Value, 6); // latência menor = melhor
        Assert.Null(EnergyCost.Change(null, 1, true));
    }

    // ---------------- Busca automática (com GPU simulada) ----------------

    private sealed class FakeGpu(int coreLimit, int memLimit, double tempPerMhz = 0) : IGpuTuner, IGpuStress
    {
        public int Core;
        public int Mem;
        public bool IsSupported => true;
        public GpuClockState Read() => new("Fake", Core, Mem, -200, 500, -500, 1500);
        public void Apply(int core, int mem) { Core = core; Mem = mem; }
        public void Reset() { Core = 0; Mem = 0; }

        public Task<GpuStressOutcome> RunAsync(TimeSpan duration, CancellationToken ct)
        {
            var errors = Core > coreLimit || Mem > memLimit ? 3 : 0;
            return Task.FromResult(new GpuStressOutcome(errors, false, 60 + Core * tempPerMhz, 1000 + Core));
        }
    }

    [Fact]
    public async Task Busca_automatica_para_antes_do_erro_com_margem()
    {
        var gpu = new FakeGpu(coreLimit: 100, memLimit: 400);
        var tuner = new GpuAutoTuner(gpu, gpu);
        var result = await tuner.RunAsync(new AutoTuneOptions { TestDuration = TimeSpan.Zero }, null, CancellationToken.None);

        // Núcleo: passos de 15 → último estável 90, margem 15 → 75. Memória: passos de 50 → 400, margem 50 → 350.
        Assert.Equal(75, result.CoreOffsetMhz);
        Assert.Equal(350, result.MemoryOffsetMhz);
        Assert.Equal((75, 350), (gpu.Core, gpu.Mem));
        Assert.Contains(result.Steps, s => !s.Passed);
    }

    [Fact]
    public async Task Busca_automatica_para_por_temperatura()
    {
        var gpu = new FakeGpu(coreLimit: 1000, memLimit: 1000, tempPerMhz: 0.2); // 60 °C + 0,2 °C por MHz
        var result = await new GpuAutoTuner(gpu, gpu).RunAsync(
            new AutoTuneOptions { TestDuration = TimeSpan.Zero, TemperatureLimit = 80 }, null, CancellationToken.None);

        // 80 °C em +100 MHz: +105 passa do limite → último bom +90, margem → +75
        Assert.Equal(75, result.CoreOffsetMhz);
    }

    [Fact]
    public async Task Cancelar_volta_aos_clocks_originais()
    {
        var gpu = new FakeGpu(1000, 1000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GpuAutoTuner(gpu, gpu).RunAsync(new AutoTuneOptions { TestDuration = TimeSpan.Zero }, null, cts.Token));
        Assert.Equal((0, 0), (gpu.Core, gpu.Mem));
    }
}
