using OtimizadorPC.Core.Processes;

namespace OtimizadorPC.Core.Abstractions;

public interface IProcessService
{
    /// <summary>Lê todos os processos. O uso de CPU é calculado em relação à leitura anterior.</summary>
    IReadOnlyList<ProcessSample> Sample();

    /// <summary>Encerra o processo. Processos do sistema são recusados.</summary>
    void Kill(int pid, bool entireTree);

    void SetPriority(int pid, PriorityLevel priority);
}
