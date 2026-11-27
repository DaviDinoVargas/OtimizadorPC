using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Programs;
using OtimizadorPC.Infrastructure.FileSystem;
using OtimizadorPC.Infrastructure.Processes;
using OtimizadorPC.Infrastructure.Settings;
using Serilog;
using Windows.Management.Deployment;

namespace OtimizadorPC.Infrastructure.Programs;

/// <summary>
/// Programas instalados lidos das chaves de desinstalação do Windows (64 e 32 bits, máquina e usuário)
/// e apps da Microsoft Store via API de implantação.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class InstalledProgramsService(IFileOperations files) : IInstalledProgramsService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledProgram>> ListAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var list = new List<InstalledProgram>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", list);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32", list);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU", list);
        ct.ThrowIfCancellationRequested();
        ReadStore(list);

        // A mesma instalação pode aparecer em mais de uma chave (ex.: 32 e 64 bits)
        return (IReadOnlyList<InstalledProgram>)list
            .GroupBy(p => (p.Name.ToLowerInvariant(), p.Version))
            .Select(g => g.OrderByDescending(p => p.SizeBytes).First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }, ct);

    private static void Read(RegistryHive hive, RegistryView view, string label, List<InstalledProgram> list)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallKey);
            if (root is null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k is null) continue;
                    var name = (k.GetValue("DisplayName") as string)?.Trim();
                    var uninstall = k.GetValue("UninstallString") as string;
                    var quiet = k.GetValue("QuietUninstallString") as string;
                    var system = k.GetValue("SystemComponent") is int sc && sc == 1;
                    var isMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1;
                    if (!ProgramRules.ShouldList(name, system, k.GetValue("ParentKeyName") as string, k.GetValue("ReleaseType") as string,
                            !string.IsNullOrWhiteSpace(uninstall) || isMsi))
                        continue;

                    var location = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    var size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : 0;
                    var hivePath = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
                    var program = new InstalledProgram
                    {
                        Id = $"{label}|{sub}",
                        Name = name!,
                        Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                        Version = (k.GetValue("DisplayVersion") as string)?.Trim(),
                        InstallDate = ProgramRules.ParseInstallDate(k.GetValue("InstallDate") as string) ?? FolderDate(location),
                        SizeBytes = size,
                        InstallLocation = string.IsNullOrWhiteSpace(location) ? null : location,
                        UninstallString = uninstall,
                        QuietUninstallString = quiet,
                        DisplayIcon = k.GetValue("DisplayIcon") as string,
                        Source = ProgramSource.Programa,
                        RegistryPath = $@"{hivePath}\{UninstallKey}\{sub}|{view}",
                        IsMsi = isMsi || (uninstall?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false),
                    };
                    list.Add(program with { IsBroken = IsBroken(program) });
                }
                catch (Exception ex) { Log.Debug(ex, "Entrada de desinstalação ilegível: {Key}", sub); }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Não foi possível ler {Label}", label); }
    }

    /// <summary>Sem data no registro: usa a criação da pasta de instalação como aproximação.</summary>
    private static DateTime? FolderDate(string? location)
    {
        try { return !string.IsNullOrWhiteSpace(location) && Directory.Exists(location) ? Directory.GetCreationTime(location) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Entrada órfã: não é MSI e o executável do desinstalador sumiu.</summary>
    private static bool IsBroken(InstalledProgram p)
    {
        if (p.IsMsi) return false;
        var cmd = ProgramRules.Parse(p.UninstallString);
        if (cmd is null) return true;
        var file = Environment.ExpandEnvironmentVariables(cmd.FileName);
        if (!Path.IsPathRooted(file)) return false; // rundll32, msiexec etc. estão no PATH
        return !File.Exists(file);
    }

    private static void ReadStore(List<InstalledProgram> list)
    {
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    var name = p.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = p.Id.Version;
                    string? logo = null;
                    try { logo = p.Logo?.LocalPath; } catch (Exception) { }
                    list.Add(new InstalledProgram
                    {
                        Id = "STORE|" + p.Id.FullName,
                        Name = name,
                        Publisher = p.PublisherDisplayName,
                        Version = $"{v.Major}.{v.Minor}.{v.Build}",
                        InstallDate = p.InstalledDate.LocalDateTime,
                        InstallLocation = p.InstalledPath,
                        DisplayIcon = logo,
                        Source = ProgramSource.Loja,
                        PackageFullName = p.Id.FullName,
                    });
                }
                catch (Exception) { /* pacote inacessível */ }
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Não foi possível listar os apps da Loja"); }
    }

    public Task<long> MeasureAsync(InstalledProgram program, CancellationToken ct = default) => Task.Run(() =>
    {
        if (program.InstallLocation is not { } dir || !Directory.Exists(dir)) return 0L;
        // Pastas genéricas (ex.: "C:\Program Files") dariam um tamanho enganoso
        if (Path.GetPathRoot(dir) == dir || IsSharedFolder(dir)) return 0L;
        try { return FastFs.Measure(dir, ct: ct).Bytes; }
        catch (Exception) { return 0L; }
    }, ct);

    private static bool IsSharedFolder(string dir)
    {
        var d = dir.TrimEnd('\\');
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        }.Any(s => string.Equals(s.TrimEnd('\\'), d, StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================
    // Desinstalar
    // =====================================================================

    public async Task<UninstallOutcome> UninstallAsync(InstalledProgram program, bool quiet, CancellationToken ct = default)
    {
        if (program.Source == ProgramSource.Loja)
        {
            var result = await new PackageManager().RemovePackageAsync(program.PackageFullName!).AsTask(ct);
            return result.ExtendedErrorCode is null
                ? new UninstallOutcome(true, false, "Removido.")
                : new UninstallOutcome(false, false, result.ErrorText);
        }

        var cmd = ProgramRules.UninstallCommand(program, quiet);
        if (cmd is null) return new UninstallOutcome(false, false, "O programa não informa como ser desinstalado.");

        Log.Information("Desinstalando {Name}: {File} {Args}", program.Name, cmd.FileName, cmd.Arguments);
        var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(cmd.FileName), cmd.Arguments) { UseShellExecute = true };
        using var process = Process.Start(psi);
        if (process is null) return new UninstallOutcome(false, false, "Não foi possível abrir o desinstalador.");
        await process.WaitForExitAsync(ct);
        var outcome = ProgramRules.InterpretExitCode(process.ExitCode);

        // Muitos desinstaladores (Inno Setup, NSIS) se copiam para a pasta temporária e saem na hora:
        // espera a entrada sumir do registro por até 10 minutos.
        if (outcome.Success && IsStillInstalled(program))
        {
            var until = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < until && IsStillInstalled(program))
                await Task.Delay(1000, ct);
            if (IsStillInstalled(program))
                return new UninstallOutcome(false, false, "O desinstalador fechou, mas o programa continua na lista (talvez tenha sido cancelado).");
        }
        return outcome;
    }

    public bool IsStillInstalled(InstalledProgram program)
    {
        if (program.Source == ProgramSource.Loja)
        {
            try { return new PackageManager().FindPackageForUser(string.Empty, program.PackageFullName) is not null; }
            catch (Exception) { return false; }
        }
        return OpenEntry(program, writable: false) is { } k && Dispose(k);

        static bool Dispose(RegistryKey k) { k.Dispose(); return true; }
    }

    private static RegistryKey? OpenEntry(InstalledProgram program, bool writable)
    {
        if (program.RegistryPath is not { } full) return null;
        var parts = full.Split('|');
        var path = parts[0];
        var view = parts.Length > 1 && Enum.TryParse<RegistryView>(parts[1], out var v) ? v : RegistryView.Default;
        var hive = path.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var sub = path[(path.IndexOf('\\') + 1)..];
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(sub, writable);
        }
        catch (Exception) { return null; }
    }

    public async Task RemoveEntryAsync(InstalledProgram program, CancellationToken ct = default)
    {
        if (program.RegistryPath is not { } full) return;
        var parts = full.Split('|');
        var view = parts.Length > 1 && Enum.TryParse<RegistryView>(parts[1], out var v) ? v : RegistryView.Default;
        await BackupKeyAsync(parts[0], program.Name, view, ct);
        var hive = parts[0].StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        baseKey.DeleteSubKeyTree(parts[0][(parts[0].IndexOf('\\') + 1)..], throwOnMissingSubKey: false);
    }

    // =====================================================================
    // Sobras
    // =====================================================================

    public Task<IReadOnlyList<Leftover>> FindLeftoversAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> stillInstalled, CancellationToken ct = default) => Task.Run(() =>
    {
        var result = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFolder(string dir, bool recommended)
        {
            if (!Directory.Exists(dir) || !seen.Add(dir.TrimEnd('\\')) || IsSharedFolder(dir) || Path.GetPathRoot(dir) == dir) return;
            long size = 0;
            try { size = FastFs.Measure(dir, ct: ct).Bytes; } catch (Exception) { }
            result.Add(new Leftover(program.Name, LeftoverKind.Pasta, dir, size, recommended));
        }

        // 1) A própria pasta de instalação, se ficou para trás
        if (program.Source == ProgramSource.Programa && program.InstallLocation is { } loc)
            AddFolder(loc, recommended: true);

        // 2) Pastas com o nome do programa (ou do fabricante) nos lugares de sempre
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); } catch (Exception) { continue; }
            foreach (var d in dirs)
            {
                var folderName = Path.GetFileName(d);
                if (!ProgramRules.IsLeftoverFolder(folderName, program, stillInstalled)) continue;
                var exactName = ProgramRules.Normalize(folderName) == ProgramRules.Normalize(program.Name);
                AddFolder(d, recommended: exactName);
            }
        }

        // 3) Chaves de configuração em HKCU\Software e HKLM\SOFTWARE
        if (program.Source == ProgramSource.Programa)
        {
            var publisher = ProgramRules.CleanPublisher(program.Publisher);
            var candidates = new List<(RegistryHive Hive, string Path)>
            {
                (RegistryHive.CurrentUser, $@"Software\{program.Name}"),
                (RegistryHive.LocalMachine, $@"SOFTWARE\{program.Name}"),
            };
            if (publisher.Length > 0)
            {
                candidates.Add((RegistryHive.CurrentUser, $@"Software\{publisher}\{program.Name}"));
                candidates.Add((RegistryHive.LocalMachine, $@"SOFTWARE\{publisher}\{program.Name}"));
            }
            foreach (var (hive, path) in candidates)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    using var k = baseKey.OpenSubKey(path);
                    if (k is null) continue;
                    var full = (hive == RegistryHive.CurrentUser ? @"HKEY_CURRENT_USER\" : @"HKEY_LOCAL_MACHINE\") + path;
                    if (seen.Add(full)) result.Add(new Leftover(program.Name, LeftoverKind.Registro, full, 0, true));
                }
                catch (Exception) { }
            }
        }

        return (IReadOnlyList<Leftover>)result;
    }, ct);

    public async Task RemoveLeftoversAsync(IEnumerable<Leftover> leftovers, CancellationToken ct = default)
    {
        foreach (var l in leftovers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (l.Kind == LeftoverKind.Pasta)
                {
                    await Task.Run(() => files.SendToRecycleBin(l.Path), ct);
                }
                else
                {
                    await BackupKeyAsync(l.Path, l.ProgramName, RegistryView.Registry64, ct);
                    var hive = l.Path.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                    baseKey.DeleteSubKeyTree(l.Path[(l.Path.IndexOf('\\') + 1)..], throwOnMissingSubKey: false);
                }
                Log.Information("Sobra removida: {Path}", l.Path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Não foi possível remover a sobra {Path}", l.Path);
            }
        }
    }

    /// <summary>Exporta a chave para Backups\Registro antes de apagar, para poder restaurar com dois cliques.</summary>
    private static async Task BackupKeyAsync(string keyPath, string programName, RegistryView view, CancellationToken ct)
    {
        var folder = Path.Combine(JsonSettingsService.DataFolder, "Backups", "Registro");
        Directory.CreateDirectory(folder);
        var safe = string.Concat(programName.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.reg");
        await ProcessRunner.RunAsync("reg.exe", $"export \"{keyPath}\" \"{file}\" /y /reg:{(view == RegistryView.Registry32 ? 32 : 64)}", ct: ct);
    }
}
