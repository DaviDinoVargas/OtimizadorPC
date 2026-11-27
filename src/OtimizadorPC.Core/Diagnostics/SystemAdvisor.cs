using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Hardware;

namespace OtimizadorPC.Core.Diagnostics;

public enum Severity { Dica, Aviso, Critico }

/// <summary>Recomendação gerada a partir do diagnóstico, com o módulo que resolve o problema.</summary>
public sealed record Recommendation(
    string Id,
    Severity Severity,
    string Title,
    string Detail,
    string? TargetModule);

/// <summary>Analisa uma foto do sistema e sugere o que melhorar. Regras puras e testáveis.</summary>
public static class SystemAdvisor
{
    public static IReadOnlyList<Recommendation> Analyze(SystemSnapshot s, DateTime? now = null)
    {
        var today = now ?? DateTime.Now;
        var list = new List<Recommendation>();

        // Espaço em disco
        foreach (var v in s.Volumes.Where(v => v.TotalBytes > 0))
        {
            if (v.FreePercent < 5)
                list.Add(new($"espaco-{v.Letter}", Severity.Critico, $"Disco {v.Letter}: quase cheio",
                    $"Restam {ByteSize.Format(v.FreeBytes)} ({v.FreePercent:0}%). Com menos de 10% livre o Windows e os programas ficam lentos e atualizações podem falhar.",
                    "limpeza"));
            else if (v.FreePercent < 10)
                list.Add(new($"espaco-{v.Letter}", Severity.Aviso, $"Disco {v.Letter}: pouco espaço",
                    $"Restam {ByteSize.Format(v.FreeBytes)} ({v.FreePercent:0}%). Vale limpar ou mover pastas grandes.",
                    "dinosize"));
        }

        // Windows instalado em HD
        var system = s.Volumes.FirstOrDefault(v => v.IsSystem);
        if (system is { Media: DiskMediaKind.HDD })
            list.Add(new("sistema-hdd", Severity.Aviso, "Windows instalado em HD",
                "O Windows está em um disco rígido. Trocar por um SSD é a melhoria de desempenho mais perceptível que existe.",
                null));

        // Saúde dos discos
        foreach (var d in s.Disks.Where(d => !d.IsHealthy && d.Health != "Desconhecido"))
            list.Add(new($"saude-{d.Number}", Severity.Critico, $"Disco com problema: {d.Model}",
                $"O Windows informa o estado \"{d.Health}\". Faça backup dos arquivos importantes o quanto antes.",
                null));

        // Paginação
        if (s.PageFiles.Any(p => p.IsTemporary))
            list.Add(new("paginacao-temporaria", Severity.Aviso, "Arquivo de paginação temporário",
                "O Windows não conseguiu criar a paginação configurada e está usando uma temporária. Revise a configuração da memória virtual.",
                "otimizacao"));

        var hasSsd = s.Volumes.Any(v => v.Media is DiskMediaKind.SSD or DiskMediaKind.NVMe && IsPageFileCapableLetter(v.Letter));
        var pageOnHdd = s.PageFiles.Where(p => !p.IsTemporary)
            .Any(p => s.Volumes.FirstOrDefault(v => v.Letter.StartsWith(p.DriveLetter, StringComparison.OrdinalIgnoreCase))?.Media == DiskMediaKind.HDD);
        if (pageOnHdd && hasSsd)
            list.Add(new("paginacao-hdd", Severity.Dica, "Paginação em disco lento",
                "A memória virtual está em um HD. Colocá-la em um SSD deixa o computador mais rápido quando a RAM enche.",
                "otimizacao"));

        // Memória abaixo da velocidade nominal (XMP/DOCP/EXPO desligado)
        var slowModules = s.Memory.Modules.Where(m => m.RatedMhz > 0 && m.ConfiguredMhz > 0 && m.RatedMhz - m.ConfiguredMhz >= 200).ToList();
        if (slowModules.Count > 0)
        {
            var m = slowModules[0];
            list.Add(new("ram-xmp", Severity.Dica, "Memória abaixo da velocidade nominal",
                $"Os pentes suportam {m.RatedMhz} MHz mas estão rodando a {m.ConfiguredMhz} MHz. Ative o perfil XMP/DOCP/EXPO na BIOS (se a placa-mãe e o processador permitirem).",
                "overclock"));
        }

        // Pouca RAM
        var ramGb = ByteSize.ToGb(s.Memory.TotalBytes);
        if (ramGb < 7.5)
            list.Add(new("ram-pouca", Severity.Aviso, "Pouca memória RAM",
                $"O computador tem {ramGb:0} GB. Para uso atual (navegador, jogos, comunicação) o recomendado é pelo menos 16 GB.",
                "processos"));
        else if (s.Memory.UsedPercent > 85)
            list.Add(new("ram-cheia", Severity.Aviso, "Memória quase cheia",
                $"{s.Memory.UsedPercent:0}% da RAM em uso agora. Feche programas que não está usando.",
                "processos"));

        // Plano de energia
        if (s.PowerPlan.Contains("Economia", StringComparison.OrdinalIgnoreCase) || s.PowerPlan.Contains("saver", StringComparison.OrdinalIgnoreCase))
            list.Add(new("energia", Severity.Dica, "Plano de energia econômico",
                $"O plano ativo é \"{s.PowerPlan}\", que limita o processador. Em um desktop, prefira Equilibrado ou Alto desempenho.",
                "otimizacao"));

        // Tempo ligado
        if (s.Os.Uptime > TimeSpan.FromDays(7))
            list.Add(new("uptime", Severity.Dica, "Computador ligado há muito tempo",
                $"Ligado há {(int)s.Os.Uptime.TotalDays} dias. Reiniciar de vez em quando libera memória e aplica atualizações.",
                null));

        // BIOS antiga
        if (s.Board.BiosDate is { } biosDate && (today - biosDate).TotalDays > 365 * 3)
            list.Add(new("bios", Severity.Dica, "BIOS desatualizada",
                $"A BIOS é de {biosDate:MM/yyyy}. Versões novas trazem correções de estabilidade e compatibilidade. Atualize pelo site do fabricante da placa ({s.Board.Manufacturer}).",
                null));

        return list.OrderByDescending(r => r.Severity).ToList();
    }

    /// <summary>Nota de saúde de 0 a 100: começa em 100 e perde pontos por recomendação conforme a gravidade.</summary>
    public static int HealthScore(IEnumerable<Recommendation> recommendations)
    {
        var score = 100 - recommendations.Sum(r => r.Severity switch
        {
            Severity.Critico => 25,
            Severity.Aviso => 12,
            _ => 4,
        });
        return Math.Clamp(score, 5, 100);
    }

    public static string HealthLabel(int score) => score switch
    {
        >= 90 => "Excelente",
        >= 75 => "Bom",
        >= 50 => "Precisa de atenção",
        _ => "Crítico",
    };

    /// <summary>O Windows não cria paginação nas letras A: e B: (reservadas a disquete).</summary>
    public static bool IsPageFileCapableLetter(string letter) =>
        !string.IsNullOrEmpty(letter) && char.ToUpperInvariant(letter[0]) is not ('A' or 'B');
}
