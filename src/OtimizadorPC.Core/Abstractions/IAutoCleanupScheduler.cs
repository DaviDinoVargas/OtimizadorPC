using OtimizadorPC.Core.GameMode;

namespace OtimizadorPC.Core.Abstractions;

/// <summary>Tarefa agendada do Windows que roda a limpeza segura sem abrir a janela.</summary>
public interface IAutoCleanupScheduler
{
    /// <summary>Argumento de linha de comando que o executável recebe da tarefa.</summary>
    const string SilentArgument = "--limpeza-silenciosa";

    /// <summary>Próxima execução, ou nulo se não houver tarefa.</summary>
    DateTime? NextRun();

    /// <summary>Cria ou atualiza a tarefa. Exige administrador.</summary>
    void Schedule(CleanupFrequency frequency, DayOfWeek day, int hour, string executablePath);

    void Remove();
}
