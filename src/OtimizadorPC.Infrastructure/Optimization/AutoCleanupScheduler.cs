using Microsoft.Win32.TaskScheduler;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.GameMode;

namespace OtimizadorPC.Infrastructure.Optimization;

/// <summary>Limpeza automática via Agendador de Tarefas do Windows (\OtimizadorPC\Limpeza automática).</summary>
public sealed class AutoCleanupScheduler : IAutoCleanupScheduler
{
    private const string Folder = "OtimizadorPC";
    private const string Name = "Limpeza automática";

    public DateTime? NextRun()
    {
        using var ts = new TaskService();
        using var task = ts.GetTask($@"\{Folder}\{Name}");
        if (task is null || !task.Enabled) return null;
        return task.NextRunTime.Year > 2000 ? task.NextRunTime : null;
    }

    public void Schedule(CleanupFrequency frequency, DayOfWeek day, int hour, string executablePath)
    {
        using var ts = new TaskService();
        var folder = ts.RootFolder.SubFolders.Exists(Folder) ? ts.RootFolder.SubFolders[Folder] : ts.RootFolder.CreateFolder(Folder);

        var def = ts.NewTask();
        def.RegistrationInfo.Description = "Limpa arquivos temporários e caches seguros sem abrir a janela do OtimizadorPC.";
        def.RegistrationInfo.Author = "OtimizadorPC";
        def.Principal.RunLevel = TaskRunLevel.Highest;
        def.Principal.LogonType = TaskLogonType.InteractiveToken;
        def.Settings.StartWhenAvailable = true;          // se o PC estava desligado no horário, roda ao ligar
        def.Settings.DisallowStartIfOnBatteries = true;
        def.Settings.StopIfGoingOnBatteries = true;
        def.Settings.ExecutionTimeLimit = TimeSpan.FromHours(1);
        def.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

        var start = DateTime.Today.AddHours(hour);
        Trigger trigger = frequency == CleanupFrequency.Diaria
            ? new DailyTrigger { StartBoundary = start, DaysInterval = 1 }
            : new WeeklyTrigger { StartBoundary = start, DaysOfWeek = ToFlag(day), WeeksInterval = 1 };
        def.Triggers.Add(trigger);
        def.Actions.Add(new ExecAction(executablePath, IAutoCleanupScheduler.SilentArgument, Path.GetDirectoryName(executablePath)));

        folder.RegisterTaskDefinition(Name, def, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);
    }

    public void Remove()
    {
        using var ts = new TaskService();
        if (!ts.RootFolder.SubFolders.Exists(Folder)) return;
        var folder = ts.RootFolder.SubFolders[Folder];
        if (folder.Tasks.Exists(Name)) folder.DeleteTask(Name, exceptionOnNotExists: false);
    }

    private static DaysOfTheWeek ToFlag(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => DaysOfTheWeek.Sunday,
        DayOfWeek.Monday => DaysOfTheWeek.Monday,
        DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
        DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
        DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
        DayOfWeek.Friday => DaysOfTheWeek.Friday,
        _ => DaysOfTheWeek.Saturday,
    };
}
