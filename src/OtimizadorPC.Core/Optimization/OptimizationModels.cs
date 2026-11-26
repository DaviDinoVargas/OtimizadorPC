namespace OtimizadorPC.Core.Optimization;

// ---------- Inicialização ----------

public enum StartupSource { RegistroUsuario, RegistroSistema, PastaUsuario, PastaSistema, AppDaLoja }

public sealed record StartupItem(
    string Id,
    string Name,
    string Command,
    StartupSource Source,
    bool IsEnabled,
    bool RequiresAdmin)
{
    public string SourceText => Source switch
    {
        StartupSource.RegistroUsuario => "Registro (usuário)",
        StartupSource.RegistroSistema => "Registro (todos os usuários)",
        StartupSource.PastaUsuario => "Pasta Inicializar (usuário)",
        StartupSource.PastaSistema => "Pasta Inicializar (todos)",
        _ => "App da Microsoft Store",
    };
}

// ---------- Serviços ----------

public enum ServiceStartMode { Automatico, AutomaticoAtrasado, Manual, Desativado, Desconhecido }

/// <summary>Serviço conhecido com a configuração recomendada e o motivo.</summary>
public sealed record KnownService(
    string Name,
    string Category,
    string Description,
    ServiceStartMode Recommended,
    bool Caution = false);

public sealed record ServiceStatus(
    KnownService Definition,
    string DisplayName,
    ServiceStartMode StartMode,
    bool IsRunning);

// ---------- Tarefas agendadas ----------

public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    string Author,
    string Action,
    bool IsEnabled,
    DateTime? LastRun,
    DateTime? NextRun);

// ---------- Ajustes ----------

public enum TweakCategory { Desempenho, Jogos, Visual, Privacidade }

public sealed record TweakInfo(
    string Id,
    string Name,
    string Description,
    TweakCategory Category,
    bool RequiresAdmin,
    bool NeedsRestart,
    bool RecommendedOn);

public sealed record TweakState(TweakInfo Info, bool IsApplied);

// ---------- Memória virtual ----------

public sealed record PageFileConfig(bool Automatic, IReadOnlyList<PageFileEntry> Files);

public sealed record PageFileEntry(string Drive, int InitialMb, int MaximumMb)
{
    public bool SystemManaged => InitialMb == 0 && MaximumMb == 0;
}

// ---------- Apps pré-instalados ----------

public sealed record KnownApp(string PackageName, string DisplayName, string Description, bool Caution = false);

public sealed record InstalledApp(KnownApp Definition, string FullName, string Version);
