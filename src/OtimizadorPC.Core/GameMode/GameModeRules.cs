using OtimizadorPC.Core.Cleanup;
using OtimizadorPC.Core.Processes;

namespace OtimizadorPC.Core.GameMode;

public enum CleanupFrequency { Diaria, Semanal }

/// <summary>Processo fechado pelo Modo Jogo (para poder reabrir ao sair).</summary>
public sealed record ClosedProcess(string Name, string? Path);

/// <summary>Estado salvo em disco enquanto o Modo Jogo está ligado — permite restaurar mesmo após uma queda do programa.</summary>
public sealed class GameModeSession
{
    public DateTime StartedAt { get; set; }
    public Guid? PreviousPowerPlan { get; set; }
    public List<ClosedProcess> Closed { get; set; } = [];
    public List<int> Boosted { get; set; } = [];
    public long FreedMemoryBytes { get; set; }
}

/// <summary>Regras puras do Modo Jogo e da limpeza automática.</summary>
public static class GameModeRules
{
    /// <summary>Pastas onde as bibliotecas de jogos instalam os jogos.</summary>
    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\riot games\league of legends\game\", @"\riot games\valorant\", @"\epic games\",
        @"\hoyoplay\games\", @"\gog galaxy\games\", @"\xboxgames\", @"\ubisoft game launcher\games\", @"\ea games\",
        @"\battle.net\", @"\games\",
    ];

    /// <summary>Executáveis de jogos conhecidos (quando instalados fora das pastas padrão).</summary>
    private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "League of Legends.exe", "VALORANT-Win64-Shipping.exe", "cs2.exe", "GenshinImpact.exe", "ZenlessZoneZero.exe",
        "StarRail.exe", "FortniteClient-Win64-Shipping.exe", "r5apex.exe", "RocketLeague.exe", "Overwatch.exe",
        "GTA5.exe", "eldenring.exe", "Minecraft.Windows.exe", "javaw.exe", "dota2.exe", "RobloxPlayerBeta.exe",
        "TslGame.exe", "cod.exe", "Cyberpunk2077.exe", "witcher3.exe", "MetroExodus.exe", "metro.exe",
    };

    /// <summary>Lançadores e serviços de lojas: nunca são "o jogo".</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "RiotClientServices.exe", "RiotClientUx.exe",
        "RiotClientUxRender.exe", "LeagueClient.exe", "LeagueClientUx.exe", "LeagueClientUxRender.exe", "HYP.exe", "GalaxyClient.exe",
        "Battle.net.exe", "EADesktop.exe", "upc.exe", "UbisoftConnect.exe", "vgc.exe", "vgtray.exe", "CrashReportClient.exe",
        "UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
    };

    public static bool IsGame(ProcessSample p)
    {
        var exe = string.IsNullOrEmpty(p.Path) ? p.Name + ".exe" : System.IO.Path.GetFileName(p.Path);
        if (Launchers.Contains(exe) || p.Category == ProcessCategory.Sistema) return false;
        if (KnownGames.Contains(exe)) return true;
        if (!p.HasWindow || string.IsNullOrEmpty(p.Path)) return false;
        var path = p.Path.ToLowerInvariant();
        return GameFolders.Any(path.Contains);
    }

    /// <summary>O que o Modo Jogo fecha: só o que já é classificado como dispensável (nunca o próprio programa).</summary>
    public static IReadOnlyList<ProcessSample> ToClose(IEnumerable<ProcessSample> all, int ownPid) => all
        .Where(p => p.Category == ProcessCategory.Dispensavel && p.Pid != ownPid && !ProcessClassifier.IsCritical(p.Name) && !IsGame(p))
        .ToList();

    /// <summary>
    /// Limpeza automática: só categorias seguras e marcadas por padrão, sem itens para escolher,
    /// sem esvaziar a Lixeira (surpreenderia) e sem as demoradas (DISM).
    /// </summary>
    public static IReadOnlyList<CleanupCategory> AutomaticCleanup(IEnumerable<CleanupCategory> categories) => categories
        .Where(c => c.Risk == CleanupRisk.Seguro && c.SelectedByDefault && !c.HasEntries && !c.SizeKnownOnlyAfterCleaning && c.Id != "lixeira")
        .ToList();

    /// <summary>Próxima execução da limpeza automática a partir de agora.</summary>
    public static DateTime NextRun(DateTime now, CleanupFrequency frequency, DayOfWeek day, int hour)
    {
        var today = now.Date.AddHours(hour);
        if (frequency == CleanupFrequency.Diaria) return today > now ? today : today.AddDays(1);
        var diff = ((int)day - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(diff).AddHours(hour);
        return candidate > now ? candidate : candidate.AddDays(7);
    }
}
