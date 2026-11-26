using System.Text.RegularExpressions;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Core.Overclock;

/// <summary>
/// Diz o que dá para fazer de overclock em cada peça, conforme processador, chipset da
/// placa-mãe e fabricante da placa de vídeo. Funciona para Intel, AMD e NVIDIA.
/// </summary>
public static partial class OverclockAdvisor
{
    [GeneratedRegex(@"\b([ZHBQXA])(\d{2,3})[A-Z]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetRegex();

    [GeneratedRegex(@"-\s?(\d{4,5})(KS|KF|K|X|XE)\b|\b(\d{3,5})(K|KF|KS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelUnlockedRegex();

    [GeneratedRegex(@"i[3579]-(\d{2})\d{2,3}", RegexOptions.IgnoreCase)]
    private static partial Regex IntelGenerationRegex();

    public static OcReport Analyze(SystemSnapshot s)
    {
        var chipset = Chipset(s.Board.Product);
        return new OcReport(Cpu(s.Cpu, chipset), Memory(s, chipset), Gpu(s.Gpus));
    }

    /// <summary>Extrai o chipset do nome da placa (ex.: "PRIME H510M-E" → "H510").</summary>
    public static string? Chipset(string boardProduct)
    {
        var m = ChipsetRegex().Match(boardProduct);
        return m.Success ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant() : null;
    }

    public static bool IsIntelUnlocked(string cpuName) => IntelUnlockedRegex().IsMatch(cpuName);

    public static int? IntelGeneration(string cpuName)
    {
        var m = IntelGenerationRegex().Match(cpuName);
        if (!m.Success) return null;
        var g = int.Parse(m.Groups[1].Value);
        return g is >= 10 and <= 19 ? g : g / 10; // "i7-10700" → 10; "i7-8700" → 8
    }

    private static OcCapability Cpu(CpuInfo cpu, string? chipset)
    {
        const string windowsTuning = "Mesmo sem overclock, a aba \"Ajuste do processador\" deixa o Windows manter os clocks altos (estado mínimo 100% e turbo agressivo).";

        if (cpu.IsIntel)
        {
            var unlocked = IsIntelUnlocked(cpu.Name);
            var zBoard = chipset?.StartsWith('Z') == true;
            if (unlocked && zBoard)
                return new("Processador", cpu.Name, OcSupport.PelaBios,
                    $"Processador desbloqueado em placa {chipset}: dá para subir o multiplicador.",
                    "Na BIOS (Ai Tweaker/OC Tweaker): aumente o multiplicador de 1 em 1 (100 MHz), rode o teste de estresse do OtimizadorPC a cada passo e volte um passo se houver erro ou temperatura acima de 90 °C. " + windowsTuning);
            if (unlocked)
                return new("Processador", cpu.Name, OcSupport.NaoSuportado,
                    $"O processador é desbloqueado, mas a placa {chipset ?? "atual"} não permite overclock de CPU (só placas Z liberam o multiplicador).",
                    windowsTuning);
            return new("Processador", cpu.Name, OcSupport.NaoSuportado,
                "Processador Intel bloqueado (sem K/KF/X no nome): o multiplicador não pode ser alterado.", windowsTuning);
        }

        if (cpu.IsAmd)
        {
            if (chipset is { } c && c.StartsWith('A'))
                return new("Processador", cpu.Name, OcSupport.NaoSuportado,
                    $"Placas {c} não oferecem overclock de processador AMD.", windowsTuning);
            return new("Processador", cpu.Name, OcSupport.PelaBios,
                "Processadores Ryzen são desbloqueados e esta placa permite ajustes.",
                "Ative o PBO (Precision Boost Overdrive) e, se quiser ir além, o Curve Optimizer na BIOS ou pelo AMD Ryzen Master. Valide com o teste de estresse do OtimizadorPC. " + windowsTuning);
        }

        return new("Processador", cpu.Name, OcSupport.NaoSuportado, "Fabricante não reconhecido.", windowsTuning);
    }

    private static OcCapability Memory(SystemSnapshot s, string? chipset)
    {
        var rated = s.Memory.Modules.Select(m => m.RatedMhz).DefaultIfEmpty(0).Max();
        var configured = s.Memory.Modules.Select(m => m.ConfiguredMhz).DefaultIfEmpty(0).Max();
        var model = $"{s.Memory.Modules.Count}× {(s.Memory.Modules.FirstOrDefault()?.PartNumber ?? "memória")} — rodando a {configured} MHz";

        if (s.Cpu.IsIntel)
        {
            var gen = IntelGeneration(s.Cpu.Name);
            var officialMax = gen switch
            {
                <= 9 => 2666,
                10 => s.Cpu.Name.Contains("i7", StringComparison.OrdinalIgnoreCase) || s.Cpu.Name.Contains("i9", StringComparison.OrdinalIgnoreCase) ? 2933 : 2666,
                11 => 3200,
                _ => 0,
            };
            var allowsOc = chipset is { } c && (c.StartsWith('Z') || (gen >= 11 && (c.StartsWith("B5") || c.StartsWith("H57") || c.StartsWith("B6") || c.StartsWith("B7") || c.StartsWith("B8"))));

            if (!allowsOc && officialMax > 0)
                return new("Memória", model, configured >= officialMax ? OcSupport.NaoSuportado : OcSupport.PelaBios,
                    configured >= officialMax
                        ? $"A placa {chipset} trava a memória na velocidade oficial do processador ({officialMax} MHz), e ela já está nesse limite."
                        : $"A placa {chipset} limita a memória a {officialMax} MHz; ela está em {configured} MHz.",
                    configured >= officialMax ? null : $"Na BIOS, ajuste a frequência da DRAM para {officialMax} MHz ou ative o XMP.");
        }

        if (rated > 0 && configured > 0 && rated - configured >= 200)
            return new("Memória", model, OcSupport.PelaBios,
                $"Os pentes suportam {rated} MHz mas rodam a {configured} MHz.",
                "Ative o perfil XMP (Intel) ou EXPO/DOCP (AMD) na BIOS. É o ganho mais fácil e seguro de memória.");

        return new("Memória", model, OcSupport.PelaBios,
            "A memória já roda na velocidade do perfil. Ir além exige ajuste manual de frequência e latências na BIOS.",
            "Suba a frequência um degrau por vez na BIOS, mantendo as latências, e valide com o teste de estresse de memória (ou o MemTest86).");
    }

    private static OcCapability Gpu(IReadOnlyList<GpuInfo> gpus)
    {
        var gpu = gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia)
                  ?? gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Amd)
                  ?? gpus.FirstOrDefault();
        if (gpu is null)
            return new("Placa de vídeo", "Não identificada", OcSupport.NaoSuportado, "Nenhuma placa de vídeo encontrada.", null);

        return gpu.Vendor switch
        {
            GpuVendor.Nvidia => new("Placa de vídeo", gpu.Name, OcSupport.PeloApp,
                "Placas NVIDIA aceitam ajuste de clock do núcleo e da memória pelo driver.",
                "Use \"Encontrar o limite automaticamente\": o OtimizadorPC sobe o clock aos poucos, testa a estabilidade e para no último valor seguro."),
            GpuVendor.Amd => new("Placa de vídeo", gpu.Name, OcSupport.PelaBios,
                "Placas AMD são ajustadas pelo AMD Software: Adrenalin Edition.",
                "No AMD Software: Desempenho > Ajuste > \"Overclock automático da GPU\". Depois valide com o teste de estresse de GPU do OtimizadorPC."),
            _ => new("Placa de vídeo", gpu.Name, OcSupport.NaoSuportado, "Vídeo integrado Intel não tem overclock pelo Windows.", null),
        };
    }
}
