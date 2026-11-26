namespace OtimizadorPC.Core.Processes;

public enum ProcessCategory
{
    /// <summary>Componente do Windows: nunca encerrado pelo app.</summary>
    Sistema,
    /// <summary>Programa com janela aberta pelo usuário.</summary>
    Aplicativo,
    /// <summary>Roda escondido; pode ser útil (drivers, sincronização, antivírus de terceiros...).</summary>
    SegundoPlano,
    /// <summary>Ajudantes conhecidos que podem ser fechados sem prejuízo (atualizadores, launchers parados...).</summary>
    Dispensavel,
}

public sealed record ProcessSample(
    int Pid,
    string Name,
    string? Path,
    string Description,
    string Company,
    long MemoryBytes,
    double CpuPercent,
    int Threads,
    bool HasWindow,
    string? WindowTitle,
    ProcessCategory Category,
    string? Reason);

/// <summary>Processos do mesmo programa agrupados (ex.: 30 processos do navegador).</summary>
public sealed record ProcessGroup(
    string Key,
    string DisplayName,
    string? Path,
    ProcessCategory Category,
    string? Reason,
    IReadOnlyList<ProcessSample> Processes)
{
    public long MemoryBytes => Processes.Sum(p => p.MemoryBytes);
    public double CpuPercent => Processes.Sum(p => p.CpuPercent);
    public int Count => Processes.Count;
    public bool IsProtected => Category == ProcessCategory.Sistema;
}

public enum PriorityLevel { Baixa, AbaixoDoNormal, Normal, AcimaDoNormal, Alta }
