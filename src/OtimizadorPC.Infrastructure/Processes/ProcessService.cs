using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Processes;

namespace OtimizadorPC.Infrastructure.Processes;

[SupportedOSPlatform("windows")]
public sealed class ProcessService : IProcessService
{
    private readonly record struct CpuMark(TimeSpan Cpu, long Ticks);

    private readonly Dictionary<int, CpuMark> _lastCpu = [];
    private readonly ConcurrentDictionary<string, (string Description, string Company)> _versionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyList<ProcessSample> Sample()
    {
        lock (_lock)
        {
            var windows = VisibleWindows();
            var now = Stopwatch.GetTimestamp();
            var cores = Environment.ProcessorCount;
            var list = new List<ProcessSample>();
            var seen = new HashSet<int>();

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == 0) continue;
                        seen.Add(p.Id);
                        var path = ImagePath(p.Id);
                        var (description, company) = path is null ? (string.Empty, string.Empty) : VersionInfo(path);

                        double cpu = 0;
                        try
                        {
                            var total = p.TotalProcessorTime;
                            if (_lastCpu.TryGetValue(p.Id, out var last))
                            {
                                var wall = Stopwatch.GetElapsedTime(last.Ticks, now).TotalMilliseconds;
                                if (wall > 0) cpu = Math.Clamp((total - last.Cpu).TotalMilliseconds / (wall * cores) * 100, 0, 100);
                            }
                            _lastCpu[p.Id] = new CpuMark(total, now);
                        }
                        catch (Exception) { /* processo protegido: sem leitura de CPU */ }

                        long mem = 0;
                        try { mem = p.WorkingSet64; } catch (Exception) { }
                        int threads = 0;
                        try { threads = p.Threads.Count; } catch (Exception) { }

                        windows.TryGetValue(p.Id, out var title);
                        var (category, reason) = ProcessClassifier.Classify(p.ProcessName, path, title is not null);

                        list.Add(new ProcessSample(p.Id, p.ProcessName, path, description, company, mem, cpu, threads,
                            title is not null, title, category, reason));
                    }
                    catch (InvalidOperationException) { /* processo terminou durante a leitura */ }
                }
            }

            foreach (var gone in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(gone);
            return list;
        }
    }

    public void Kill(int pid, bool entireTree)
    {
        using var p = Process.GetProcessById(pid);
        if (ProcessClassifier.IsCritical(p.ProcessName))
            throw new InvalidOperationException($"\"{p.ProcessName}\" é um componente do Windows e não pode ser encerrado por aqui.");
        if (pid == Environment.ProcessId)
            throw new InvalidOperationException("Para fechar o OtimizadorPC use o botão de fechar da janela.");
        p.Kill(entireTree);
        p.WaitForExit(3000);
    }

    public void SetPriority(int pid, PriorityLevel priority)
    {
        using var p = Process.GetProcessById(pid);
        p.PriorityClass = priority switch
        {
            PriorityLevel.Baixa => ProcessPriorityClass.Idle,
            PriorityLevel.AbaixoDoNormal => ProcessPriorityClass.BelowNormal,
            PriorityLevel.AcimaDoNormal => ProcessPriorityClass.AboveNormal,
            PriorityLevel.Alta => ProcessPriorityClass.High,
            _ => ProcessPriorityClass.Normal,
        };
    }

    private (string Description, string Company) VersionInfo(string path) =>
        _versionCache.GetOrAdd(path, p =>
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(p);
                return ((v.FileDescription ?? string.Empty).Trim(), (v.CompanyName ?? string.Empty).Trim());
            }
            catch (Exception)
            {
                return (string.Empty, string.Empty);
            }
        });

    // ---------------- Win32 ----------------

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Caminho do executável com o menor nível de acesso possível (funciona para quase todos os processos).</summary>
    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Janelas de nível superior visíveis (como na barra de tarefas): pid → título.</summary>
    private static Dictionary<int, string> VisibleWindows()
    {
        var result = new Dictionary<int, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            // Ignora janelas "fantasma" ocultadas pelo DWM (apps suspensos da Store)
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(256);
            if (GetWindowText(hwnd, sb, sb.Capacity) == 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            result.TryAdd(pid, sb.ToString());
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
