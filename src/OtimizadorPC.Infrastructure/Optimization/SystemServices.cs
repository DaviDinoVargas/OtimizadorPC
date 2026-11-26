using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Diagnostics;
using OtimizadorPC.Core.Optimization;
using OtimizadorPC.Infrastructure.Power;
using OtimizadorPC.Infrastructure.Processes;
using CoreStartMode = OtimizadorPC.Core.Optimization.ServiceStartMode;
using Task = System.Threading.Tasks.Task;

namespace OtimizadorPC.Infrastructure.Optimization;

// =====================================================================
// Serviços do Windows
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class ServiceTuner : IServiceTuner
{
    public IReadOnlyList<ServiceStatus> ListKnown()
    {
        var installed = ServiceController.GetServices().Concat(SafeDevices())
            .GroupBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var list = new List<ServiceStatus>();
        foreach (var def in OptimizationCatalog.Services)
        {
            if (!installed.TryGetValue(def.Name, out var sc)) continue;
            using (sc)
            {
                try
                {
                    list.Add(new ServiceStatus(def, sc.DisplayName, ReadMode(sc), sc.Status == ServiceControllerStatus.Running));
                }
                catch (InvalidOperationException) { /* serviço removido durante a leitura */ }
            }
        }
        return list;
    }

    private static IEnumerable<ServiceController> SafeDevices()
    {
        try { return ServiceController.GetDevices(); }
        catch (Exception) { return []; }
    }

    private static CoreStartMode ReadMode(ServiceController sc)
    {
        switch (sc.StartType)
        {
            case System.ServiceProcess.ServiceStartMode.Automatic:
                using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}"))
                    return k?.GetValue("DelayedAutostart") is int d && d == 1 ? CoreStartMode.AutomaticoAtrasado : CoreStartMode.Automatico;
            case System.ServiceProcess.ServiceStartMode.Manual:
                return CoreStartMode.Manual;
            case System.ServiceProcess.ServiceStartMode.Disabled:
                return CoreStartMode.Desativado;
            default:
                return CoreStartMode.Desconhecido;
        }
    }

    public async Task SetStartModeAsync(string serviceName, CoreStartMode mode, bool stopNow, CancellationToken ct = default)
    {
        var start = mode switch
        {
            CoreStartMode.Automatico => "auto",
            CoreStartMode.AutomaticoAtrasado => "delayed-auto",
            CoreStartMode.Manual => "demand",
            CoreStartMode.Desativado => "disabled",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var r = await ProcessRunner.RunAsync("sc.exe", $"config \"{serviceName}\" start= {start}", ct: ct);
        if (r.ExitCode != 0)
            throw new InvalidOperationException(r.ExitCode == 5
                ? "Acesso negado. Execute como administrador (alguns serviços se protegem contra alterações)."
                : $"O Windows recusou a alteração (código {r.ExitCode}).");

        if (stopNow && mode is CoreStartMode.Manual or CoreStartMode.Desativado)
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
            {
                sc.Stop();
                await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)), ct);
            }
        }
        else if (mode is CoreStartMode.Automatico or CoreStartMode.AutomaticoAtrasado)
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
        }
    }
}

// =====================================================================
// Tarefas agendadas
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class ScheduledTaskService : IScheduledTaskService
{
    public IReadOnlyList<ScheduledTaskInfo> ListThirdParty()
    {
        var list = new List<ScheduledTaskInfo>();
        using var ts = new TaskService();
        try
        {
            foreach (var t in ts.RootFolder.EnumerateTasks(null, true))
            {
                try
                {
                    if (t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                    var action = t.Definition.Actions.OfType<ExecAction>().FirstOrDefault()?.Path ?? string.Empty;
                    list.Add(new ScheduledTaskInfo(
                        t.Path,
                        t.Name,
                        t.Definition.RegistrationInfo.Author ?? string.Empty,
                        Environment.ExpandEnvironmentVariables(action.Trim('"')),
                        t.Enabled,
                        t.LastRunTime.Year > 2000 ? t.LastRunTime : null,
                        t.NextRunTime.Year > 2000 ? t.NextRunTime : null));
                }
                catch (Exception) { /* tarefa sem permissão de leitura */ }
                finally { t.Dispose(); }
            }
        }
        catch (UnauthorizedAccessException) { }
        return list.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetEnabled(string path, bool enabled)
    {
        using var ts = new TaskService();
        using var task = ts.GetTask(path) ?? throw new InvalidOperationException("Tarefa não encontrada.");
        task.Enabled = enabled;
    }
}

// =====================================================================
// Planos de energia
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class PowerService : IPowerService
{
    public IReadOnlyList<(Guid Id, string Name, bool Active)> ListPlans() =>
        PowerPlans.List().Select(p => (p.Id, p.Name, p.IsActive)).ToList();

    public void Activate(Guid id)
    {
        if (!PowerPlans.SetActive(id)) throw new InvalidOperationException("Não foi possível ativar o plano de energia.");
    }

    public Guid EnableUltimatePerformance()
    {
        var existing = PowerPlans.List().FirstOrDefault(p =>
            p.Id == PowerPlans.UltimatePerformance ||
            p.Name.Contains("Desempenho Máximo", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase));
        var id = existing?.Id ?? PowerPlans.Duplicate(PowerPlans.UltimatePerformance)
                 ?? throw new InvalidOperationException("Este Windows não oferece o plano Desempenho Máximo.");
        Activate(id);
        return id;
    }
}

// =====================================================================
// Memória virtual (arquivo de paginação)
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class PageFileService : IPageFileService
{
    public PageFileConfig Read()
    {
        using var cs = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem").Get().Cast<ManagementObject>().First();
        var automatic = cs["AutomaticManagedPagefile"] is true;
        using var searcher = new ManagementObjectSearcher("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting");
        var files = searcher.Get().Cast<ManagementObject>()
            .Select(p => new PageFileEntry(
                (p["Name"]?.ToString() ?? "?")[..1].ToUpperInvariant(),
                Convert.ToInt32(p["InitialSize"] ?? 0),
                Convert.ToInt32(p["MaximumSize"] ?? 0)))
            .ToList();
        return new PageFileConfig(automatic, files);
    }

    public void Write(bool automatic, IReadOnlyList<PageFileEntry> files)
    {
        foreach (var f in files)
        {
            if (!SystemAdvisor.IsPageFileCapableLetter(f.Drive))
                throw new ArgumentException($"O Windows não cria arquivo de paginação na unidade {f.Drive}: (letras A: e B: são reservadas a disquete). Escolha outra unidade.");
            if (!Directory.Exists(f.Drive + @":\"))
                throw new ArgumentException($"A unidade {f.Drive}: não existe.");
            if (!f.SystemManaged && (f.InitialMb < 16 || f.MaximumMb < f.InitialMb))
                throw new ArgumentException("O tamanho máximo deve ser maior ou igual ao inicial (mínimo 16 MB).");
        }

        using var cs = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem").Get().Cast<ManagementObject>().First();
        cs["AutomaticManagedPagefile"] = automatic;
        cs.Put();
        if (automatic) return;

        using (var existing = new ManagementObjectSearcher("SELECT * FROM Win32_PageFileSetting"))
            foreach (ManagementObject o in existing.Get()) o.Delete();

        using var cls = new ManagementClass("Win32_PageFileSetting");
        foreach (var f in files)
        {
            using var inst = cls.CreateInstance();
            inst["Name"] = $@"{f.Drive}:\pagefile.sys";
            inst.Put();
            using var created = new ManagementObjectSearcher($"SELECT * FROM Win32_PageFileSetting WHERE Name = '{f.Drive}:\\\\pagefile.sys'")
                .Get().Cast<ManagementObject>().First();
            created["InitialSize"] = (uint)f.InitialMb;
            created["MaximumSize"] = (uint)f.MaximumMb;
            created.Put();
        }
    }
}

// =====================================================================
// Ponto de restauração
// =====================================================================

[SupportedOSPlatform("windows")]
public sealed class RestorePointService : IRestorePointService
{
    public Task<bool> CreateAsync(string description, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var args = cls.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = 12; // MODIFY_SETTINGS
            args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var result = cls.InvokeMethod("CreateRestorePoint", args, null);
            return Convert.ToInt32(result?["ReturnValue"] ?? -1) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }, ct);
}
