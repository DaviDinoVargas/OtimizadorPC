using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.ServiceProcess;
using Microsoft.Win32;

namespace OtimizadorPC.Setup.Engine;

public enum StepState { Pendente, Executando, Concluido, Ignorado, Erro }

public sealed record StepUpdate(string Id, StepState State, string? Detail = null, double? Percent = null);

public sealed record InstallOptions(
    string InstallDir,
    bool DesktopShortcut,
    bool StartMenuShortcut,
    bool StartWithWindows,
    bool InstallSensorDriver);

/// <summary>
/// Instalação e remoção do OtimizadorPC: pré-requisitos (baixados só se faltarem), arquivos,
/// atalhos, inicialização com o Windows, registro em "Aplicativos instalados" e driver de sensores.
/// </summary>
public static class InstallerEngine
{
    public const string AppName = "OtimizadorPC";
    public const string ExeName = "OtimizadorPC.exe";
    public const string UninstallerName = "Desinstalar.exe";
    public const string Publisher = "DaviDinoVargas";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OtimizadorPC";
    private const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string PawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
    private const string StartupTask = "OtimizadorPC";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.zip");

    public static string License()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE");
        return s is null ? "Licença MIT" : new StreamReader(s).ReadToEnd();
    }

    /// <summary>Pasta da instalação existente, se houver.</summary>
    public static string? InstalledDir()
    {
        using var k = Registry.LocalMachine.OpenSubKey(UninstallKey);
        return k?.GetValue("InstallLocation") as string;
    }

    /// <summary>.NET 10 Desktop Runtime (x64) instalado?</summary>
    public static bool IsRuntimeInstalled()
    {
        // Instalações feitas pelo SDK nem sempre ficam no registro: confere também a pasta do .NET
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
        if (k?.GetValueNames().Any(v => v.StartsWith("10.", StringComparison.Ordinal)) == true) return true;

        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\shared\Microsoft.WindowsDesktop.App");
        return Directory.Exists(shared) && Directory.GetDirectories(shared).Any(d => Path.GetFileName(d).StartsWith("10.", StringComparison.Ordinal));
    }

    public static bool IsPawnIoInstalled()
    {
        try { return ServiceController.GetDevices().Any(s => s.ServiceName.Equals("PawnIO", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return false; }
    }

    // =====================================================================
    // Instalação
    // =====================================================================

    public static async Task InstallAsync(InstallOptions o, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "OtimizadorPC-Setup");
        Directory.CreateDirectory(temp);

        // 1. Pré-requisito: .NET 10 Desktop Runtime
        if (IsRuntimeInstalled())
        {
            progress.Report(new("runtime", StepState.Ignorado, ".NET 10 já está instalado"));
        }
        else
        {
            progress.Report(new("runtime", StepState.Executando, "Baixando o .NET 10 Desktop Runtime…", 0));
            var file = Path.Combine(temp, "windowsdesktop-runtime-win-x64.exe");
            await DownloadAsync(RuntimeUrl, file, p => progress.Report(new("runtime", StepState.Executando, $"Baixando o .NET 10… {p:P0}", p)), ct);
            progress.Report(new("runtime", StepState.Executando, "Instalando o .NET 10…"));
            var code = await RunAsync(file, "/install /quiet /norestart", ct);
            if (code is not (0 or 1641 or 3010)) throw new InvalidOperationException($"O instalador do .NET terminou com o código {code}.");
            progress.Report(new("runtime", StepState.Concluido, ".NET 10 instalado"));
        }

        // 2. Arquivos
        progress.Report(new("arquivos", StepState.Executando, "Fechando o OtimizadorPC se estiver aberto…"));
        CloseRunningApp();
        Directory.CreateDirectory(o.InstallDir);
        await using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                                   ?? throw new InvalidOperationException("Este instalador foi gerado sem o pacote do aplicativo."))
        {
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            var total = zip.Entries.Count;
            var done = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(o.InstallDir, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(o.InstallDir), StringComparison.OrdinalIgnoreCase)) continue; // proteção contra caminhos maliciosos
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(target);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
                done++;
                if (done % 10 == 0 || done == total)
                    progress.Report(new("arquivos", StepState.Executando, $"Copiando arquivos… {done}/{total}", done / (double)total));
            }
        }
        // O próprio instalador vira o desinstalador
        File.Copy(Environment.ProcessPath!, Path.Combine(o.InstallDir, UninstallerName), overwrite: true);
        progress.Report(new("arquivos", StepState.Concluido, $"Instalado em {o.InstallDir}"));

        // 3. Atalhos
        progress.Report(new("atalhos", StepState.Executando));
        var exe = Path.Combine(o.InstallDir, ExeName);
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");
        if (o.StartMenuShortcut) Shortcut.Create(startMenu, exe, "Limpeza, otimização, análise de disco e overclock");
        else TryDelete(startMenu);
        if (o.DesktopShortcut) Shortcut.Create(desktop, exe, "OtimizadorPC");
        else TryDelete(desktop);
        progress.Report(new("atalhos", o.StartMenuShortcut || o.DesktopShortcut ? StepState.Concluido : StepState.Ignorado));

        // 4. Iniciar com o Windows (tarefa agendada, pois o app roda como administrador)
        progress.Report(new("inicializacao", StepState.Executando));
        if (o.StartWithWindows)
        {
            var code = await RunAsync("schtasks.exe", $"/Create /TN \"{StartupTask}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F", ct);
            progress.Report(new("inicializacao", code == 0 ? StepState.Concluido : StepState.Erro, code == 0 ? null : "Não foi possível criar a tarefa de inicialização"));
        }
        else
        {
            await RunAsync("schtasks.exe", $"/Delete /TN \"{StartupTask}\" /F", ct);
            progress.Report(new("inicializacao", StepState.Ignorado));
        }

        // 5. Registro em "Aplicativos instalados"
        progress.Report(new("registro", StepState.Executando));
        RegisterUninstall(o.InstallDir);
        progress.Report(new("registro", StepState.Concluido));

        // 6. Driver de sensores (opcional)
        if (!o.InstallSensorDriver)
        {
            progress.Report(new("driver", StepState.Ignorado, "Não selecionado"));
        }
        else if (IsPawnIoInstalled())
        {
            progress.Report(new("driver", StepState.Ignorado, "Já instalado"));
        }
        else
        {
            try
            {
                progress.Report(new("driver", StepState.Executando, "Baixando o driver PawnIO…", 0));
                var file = Path.Combine(temp, "PawnIO_setup.exe");
                await DownloadAsync(PawnIoUrl, file, p => progress.Report(new("driver", StepState.Executando, $"Baixando o driver PawnIO… {p:P0}", p)), ct);
                progress.Report(new("driver", StepState.Executando, "Confirme a instalação do driver na janela que abriu…"));
                var code = await RunAsync(file, "-install", ct, hidden: false);
                progress.Report(new("driver", code == 0 && IsPawnIoInstalled() ? StepState.Concluido : StepState.Erro,
                    code == 0 && IsPawnIoInstalled() ? "Temperaturas e consumo disponíveis" : "Driver não instalado (o app funciona sem ele)"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.Report(new("driver", StepState.Erro, "Sem conexão para baixar o driver (o app funciona sem ele)"));
            }
        }

        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    private static void RegisterUninstall(string dir)
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        var uninstaller = Path.Combine(dir, UninstallerName);
        k.SetValue("DisplayName", AppName);
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", Publisher);
        k.SetValue("DisplayIcon", Path.Combine(dir, ExeName));
        k.SetValue("InstallLocation", dir);
        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        k.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        k.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /silent");
        k.SetValue("URLInfoAbout", "https://github.com/DaviDinoVargas/OtimizadorPC");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var size = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
    }

    // =====================================================================
    // Desinstalação
    // =====================================================================

    public static async Task UninstallAsync(bool removeUserData, IProgress<StepUpdate> progress, CancellationToken ct)
    {
        var dir = InstalledDir() ?? Path.GetDirectoryName(Environment.ProcessPath)!;

        progress.Report(new("fechar", StepState.Executando));
        CloseRunningApp();
        progress.Report(new("fechar", StepState.Concluido));

        progress.Report(new("atalhos", StepState.Executando));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"));
        await RunAsync("schtasks.exe", $"/Delete /TN \"{StartupTask}\" /F", ct);
        progress.Report(new("atalhos", StepState.Concluido));

        progress.Report(new("arquivos", StepState.Executando));
        var self = Path.GetFullPath(Environment.ProcessPath!);
        foreach (var f in Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList() : [])
        {
            if (string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase)) continue;
            TryDelete(f);
        }
        progress.Report(new("arquivos", StepState.Concluido));

        progress.Report(new("registro", StepState.Executando));
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        progress.Report(new("registro", StepState.Concluido));

        progress.Report(new("dados", StepState.Executando));
        if (removeUserData)
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            try { if (Directory.Exists(data)) Directory.Delete(data, true); } catch (IOException) { }
            progress.Report(new("dados", StepState.Concluido));
        }
        else
        {
            progress.Report(new("dados", StepState.Ignorado, "Configurações mantidas"));
        }

        // O desinstalador em execução não pode se apagar: agenda a remoção da pasta para depois de fechar
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak > nul & rmdir /s /q \"{dir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    // =====================================================================
    // Utilitários
    // =====================================================================

    private static void CloseRunningApp()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(3000)) p.Kill(true);
            }
            catch (Exception) { }
            finally { p.Dispose(); }
        }
    }

    private static async Task DownloadAsync(string url, string file, Action<double> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OtimizadorPC-Setup/" + Version);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(file);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress(read / (double)total);
        }
    }

    private static async Task<int> RunAsync(string file, string args, CancellationToken ct, bool hidden = true)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = !hidden, CreateNoWindow = hidden })
                      ?? throw new InvalidOperationException($"Não foi possível executar {file}");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}

/// <summary>Cria atalhos .lnk pelo Windows Script Host.</summary>
internal static class Shortcut
{
    public static void Create(string lnkPath, string target, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponível");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(lnkPath);
        link.TargetPath = target;
        link.WorkingDirectory = Path.GetDirectoryName(target);
        link.IconLocation = target + ",0";
        link.Description = description;
        link.Save();
    }
}
