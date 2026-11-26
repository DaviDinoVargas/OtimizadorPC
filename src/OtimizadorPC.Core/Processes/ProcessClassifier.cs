namespace OtimizadorPC.Core.Processes;

/// <summary>
/// Decide o que cada processo é. Regras conservadoras: na dúvida, o processo fica como
/// "segundo plano" e nunca é encerrado automaticamente.
/// </summary>
public static class ProcessClassifier
{
    /// <summary>Processos essenciais do Windows. Encerrá-los trava ou reinicia o computador.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "LsaIso", "svchost", "dwm", "fontdrvhost", "sihost", "ctfmon", "audiodg", "conhost",
        "taskhostw", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchIndexer",
        "TextInputHost", "dllhost", "WmiPrvSE", "WUDFHost", "spoolsv", "MsMpEng", "NisSrv", "SecurityHealthService",
        "SecurityHealthSystray", "SgrmBroker", "smartscreen", "MpDefenderCoreService", "explorer", "ApplicationFrameHost",
        "SystemSettings", "LockApp", "UserOOBEBroker", "backgroundTaskHost", "CompPkgSrv", "AggregatorHost", "unsecapp",
        "vmmem", "vmmemWSL", "vmcompute", "wlanext", "dasHost", "atieclxx", "atiesrxx", "NVDisplay.Container", "nvcontainer",
        "WmiApSrv", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller", "OtimizadorPC",
    };

    /// <summary>Ajudantes que só servem para atualizar, notificar ou deixar algo pré-carregado.</summary>
    private static readonly Dictionary<string, string> Dispensable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GoogleUpdate"] = "Atualizador do Google; roda de novo quando o Chrome abrir.",
        ["GoogleCrashHandler"] = "Relatório de falhas do Google.",
        ["GoogleCrashHandler64"] = "Relatório de falhas do Google.",
        ["MicrosoftEdgeUpdate"] = "Atualizador do Edge.",
        ["OneDrive"] = "Sincronização do OneDrive (sem conta, não faz nada).",
        ["OneDrive.Sync.Service"] = "Serviço de sincronização do OneDrive.",
        ["CCXProcess"] = "Processo de apoio da Adobe Creative Cloud.",
        ["AdobeIPCBroker"] = "Comunicação entre apps da Adobe.",
        ["AdobeCollabSync"] = "Sincronização do Acrobat.",
        ["jusched"] = "Atualizador do Java.",
        ["BlueStacksServices"] = "Serviços do BlueStacks com o emulador fechado.",
        ["CurseForge"] = "Launcher de mods minimizado.",
        ["EpicWebHelper"] = "Navegador interno da Epic Games.",
        ["EpicGamesLauncher"] = "Launcher da Epic minimizado.",
        ["Overwolf"] = "Plataforma de overlays.",
        ["PhoneExperienceHost"] = "Vincular ao Celular em segundo plano.",
        ["Widgets"] = "Widgets do Windows.",
        ["WidgetService"] = "Widgets do Windows.",
        ["WidgetBoard"] = "Widgets do Windows.",
        ["HxTsr"] = "Email e Calendário antigo em segundo plano.",
        ["Cortana"] = "Assistente descontinuada.",
        ["GameBarFTServer"] = "Xbox Game Bar em segundo plano.",
        ["CrossDeviceService"] = "Experiência entre Dispositivos.",
        ["CrossDeviceResume"] = "Experiência entre Dispositivos.",
        ["browser_assistant"] = "Assistente do Opera em segundo plano.",
        ["opera_autoupdate"] = "Atualizador do Opera.",
        ["Opera_crashreporter"] = "Relatório de falhas do Opera.",
        ["NVIDIA Web Helper"] = "Navegador interno do app da NVIDIA.",
        ["NVIDIA Share"] = "Overlay da NVIDIA (atalho Alt+Z).",
        ["ArmouryCrate.UserSessionHelper"] = "Ajudante do Armoury Crate.",
        ["ArmourySocketServer"] = "Ajudante do Armoury Crate.",
        ["Wacom_UpdateUtil"] = "Atualizador da Wacom.",
        ["SpotifyWebHelper"] = "Ajudante do Spotify.",
        ["Teams"] = "Teams minimizado.",
        ["ms-teams"] = "Teams minimizado.",
        ["Skype"] = "Skype em segundo plano.",
        ["DropboxUpdate"] = "Atualizador do Dropbox.",
        ["AcrobatNotificationClient"] = "Notificações do Acrobat.",
        ["CompanionApp"] = "Ajudante de fabricante.",
    };

    public static (ProcessCategory Category, string? Reason) Classify(string name, string? path, bool hasWindow)
    {
        if (Critical.Contains(name))
            return (ProcessCategory.Sistema, "Componente do Windows");

        var windows = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        if (path is not null && path.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase))
            return (ProcessCategory.Sistema, "Programa do Windows");

        if (hasWindow)
            return (ProcessCategory.Aplicativo, null);

        if (Dispensable.TryGetValue(name, out var reason))
            return (ProcessCategory.Dispensavel, reason);

        var lower = name.ToLowerInvariant();
        if (lower.Contains("updater") || lower.EndsWith("update", StringComparison.Ordinal) || lower.Contains("crashhandler") || lower.Contains("crashreport"))
            return (ProcessCategory.Dispensavel, "Atualizador ou relatório de falhas.");

        return (ProcessCategory.SegundoPlano, null);
    }

    public static bool IsCritical(string name) => Critical.Contains(name);

    /// <summary>Agrupa processos do mesmo executável (mesmo caminho, ou mesmo nome sem caminho).</summary>
    public static IReadOnlyList<ProcessGroup> Group(IEnumerable<ProcessSample> samples) =>
        samples
            .GroupBy(s => (s.Path ?? s.Name).ToLowerInvariant())
            .Select(g =>
            {
                var list = g.OrderByDescending(p => p.MemoryBytes).ToList();
                var main = list.FirstOrDefault(p => p.HasWindow) ?? list[0];
                // O grupo é "aplicativo" se qualquer processo dele tiver janela
                var category = list.Any(p => p.Category == ProcessCategory.Sistema) ? ProcessCategory.Sistema
                    : list.Any(p => p.Category == ProcessCategory.Aplicativo) ? ProcessCategory.Aplicativo
                    : main.Category;
                var display = !string.IsNullOrWhiteSpace(main.Description) ? main.Description : main.Name;
                return new ProcessGroup(g.Key, display, main.Path, category, main.Reason, list);
            })
            .ToList();
}
