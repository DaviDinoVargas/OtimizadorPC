using OtimizadorPC.Core.Optimization;

namespace OtimizadorPC.Core.Abstractions;

public interface IStartupService
{
    IReadOnlyList<StartupItem> List();

    /// <summary>Ativa ou desativa como o Gerenciador de Tarefas faz (reversível).</summary>
    void SetEnabled(StartupItem item, bool enabled);
}

public interface IServiceTuner
{
    IReadOnlyList<ServiceStatus> ListKnown();
    Task SetStartModeAsync(string serviceName, ServiceStartMode mode, bool stopNow, CancellationToken ct = default);
}

public interface IScheduledTaskService
{
    /// <summary>Tarefas de programas de terceiros (fora da pasta \Microsoft).</summary>
    IReadOnlyList<ScheduledTaskInfo> ListThirdParty();
    void SetEnabled(string path, bool enabled);
}

public interface ITweakService
{
    IReadOnlyList<TweakState> List();
    void Apply(string id, bool on);
}

public interface IPowerService
{
    IReadOnlyList<(Guid Id, string Name, bool Active)> ListPlans();
    void Activate(Guid id);

    /// <summary>Cria (se preciso) e ativa o plano oculto "Desempenho Máximo".</summary>
    Guid EnableUltimatePerformance();
}

public interface IPageFileService
{
    PageFileConfig Read();

    /// <summary>Configura a paginação. Lista vazia com automatic=true volta ao padrão do Windows.</summary>
    void Write(bool automatic, IReadOnlyList<PageFileEntry> files);
}

public interface IAppxService
{
    IReadOnlyList<InstalledApp> ListRemovable();
    Task RemoveAsync(InstalledApp app, CancellationToken ct = default);
}

public interface IRestorePointService
{
    /// <summary>Cria um ponto de restauração. Retorna false se o Windows recusar (ex.: já existe um recente).</summary>
    Task<bool> CreateAsync(string description, CancellationToken ct = default);
}
