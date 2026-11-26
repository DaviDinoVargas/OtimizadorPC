using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Cleanup;
using OtimizadorPC.Infrastructure.FileSystem;
using OtimizadorPC.Infrastructure.Hardware;
using OtimizadorPC.Infrastructure.Processes;

namespace OtimizadorPC.Infrastructure.Cleanup;

/// <summary>
/// Categorias de limpeza e a lógica para medir e apagar cada uma. Só apaga caches,
/// temporários e restos — nunca documentos, histórico ou senhas dos navegadores.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CleanupService : ICleanupService
{
    private const string RecycleBinId = "lixeira";
    private const string ComponentStoreId = "componentes-windows";
    private const string WindowsOldId = "windows-old";
    private const string SteamOrphansId = "jogos-orfaos-steam";

    private sealed record Target(string Path, string Pattern = "*", TimeSpan? MinAge = null, bool IsFile = false);

    private sealed record Def(CleanupCategory Info, Func<IReadOnlyList<Target>> Targets);

    private readonly List<Def> _defs = [];

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string SystemRoot = Path.GetPathRoot(WinDir) ?? @"C:\";

    public CleanupService()
    {
        RegisterSystem();
        RegisterBrowsers();
        RegisterDevelopment();
        RegisterGamesAndApps();
    }

    public IReadOnlyList<CleanupCategory> Categories => _defs.Select(d => d.Info).ToList();

    // =====================================================================
    // Definição das categorias
    // =====================================================================

    private void Add(string id, string name, string description, CleanupGroup group, CleanupRisk risk, bool admin, bool selected,
        Func<IReadOnlyList<Target>> targets, bool onlyIfExists = false, bool hasEntries = false, bool sizeAfter = false)
    {
        if (onlyIfExists && !targets().Any(t => t.IsFile ? File.Exists(t.Path) : Directory.Exists(t.Path))) return;
        _defs.Add(new Def(new CleanupCategory(id, name, description, group, risk, admin, selected, hasEntries, sizeAfter), targets));
    }

    private void RegisterSystem()
    {
        var day = TimeSpan.FromDays(1);
        Add("temp-usuario", "Arquivos temporários do usuário",
            "Sobras de instaladores e programas na pasta Temp. Arquivos das últimas 24 h são mantidos.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, false, true, () => [new(Path.GetTempPath(), MinAge: day)]);

        Add("temp-windows", "Arquivos temporários do Windows",
            "Pasta Temp do sistema. Arquivos das últimas 24 h são mantidos.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, true, true, () => [new(Path.Combine(WinDir, "Temp"), MinAge: day)]);

        Add("cache-windows-update", "Downloads do Windows Update",
            "Atualizações já instaladas que ficaram guardadas.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, true, true, () => [new(Path.Combine(WinDir, "SoftwareDistribution", "Download"))]);

        Add("otimizacao-entrega", "Cache de Otimização de Entrega",
            "Cópias de atualizações compartilhadas com outros PCs da rede.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, true, true,
            () => [new(Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))]);

        Add("relatorios-erro", "Relatórios de erro e despejos de memória",
            "Relatórios de travamentos já enviados e arquivos de dump.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, false, true, () =>
            [
                new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive")),
                new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                new(Path.Combine(Local, @"Microsoft\Windows\WER")),
                new(Path.Combine(Local, "CrashDumps")),
                new(Path.Combine(WinDir, "Minidump")),
                new(Path.Combine(WinDir, "MEMORY.DMP"), IsFile: true),
            ]);

        Add("logs-windows", "Logs de instalação do Windows",
            "Registros antigos do CBS e DISM gerados por atualizações.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, true, false, () =>
            [
                new(Path.Combine(WinDir, @"Logs\CBS"), MinAge: day),
                new(Path.Combine(WinDir, @"Logs\DISM"), MinAge: day),
            ]);

        Add("miniaturas", "Cache de miniaturas",
            "Miniaturas de imagens e vídeos do Explorador. São recriadas quando você abrir as pastas.",
            CleanupGroup.Sistema, CleanupRisk.Moderado, false, false,
            () => [new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")]);

        Add("shaders", "Cache de shaders (DirectX, NVIDIA, AMD)",
            "Os jogos recriam os shaders; o primeiro carregamento depois pode demorar um pouco mais.",
            CleanupGroup.Sistema, CleanupRisk.Moderado, false, false, () =>
            [
                new(Path.Combine(Local, "D3DSCache")),
                new(Path.Combine(Local, @"NVIDIA\DXCache")),
                new(Path.Combine(Local, @"NVIDIA\GLCache")),
                new(Path.Combine(Local, @"NVIDIA Corporation\NV_Cache")),
                new(Path.Combine(ProgramData, @"NVIDIA Corporation\NV_Cache")),
                new(Path.Combine(Local, @"AMD\DxCache")),
                new(Path.Combine(Local, @"AMD\DxcCache")),
                new(Path.Combine(Local, @"AMD\GLCache")),
                new(Path.Combine(Local, @"AMD\VkCache")),
            ]);

        Add(RecycleBinId, "Lixeira", "Esvazia a lixeira de todos os discos.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, false, true, () => []);

        Add(ComponentStoreId, "Componentes antigos do Windows (WinSxS)",
            "Remove versões substituídas de componentes de atualizações (DISM). Pode levar de 5 a 20 minutos.",
            CleanupGroup.Sistema, CleanupRisk.Seguro, true, false, () => [], sizeAfter: true);

        Add(WindowsOldId, "Instalação anterior do Windows",
            "Pastas Windows.old e restos de atualização de versão. Depois disso não será possível voltar à versão anterior.",
            CleanupGroup.Sistema, CleanupRisk.Atencao, true, false, () =>
            [
                new(Path.Combine(SystemRoot, "Windows.old")),
                new(Path.Combine(SystemRoot, "$Windows.~BT")),
                new(Path.Combine(SystemRoot, "$Windows.~WS")),
            ], onlyIfExists: true);
    }

    private void RegisterBrowsers()
    {
        void Chromium(string id, string name, string userData)
        {
            Add(id, $"Cache do {name}",
                $"Arquivos temporários de páginas do {name}. Histórico, senhas e logins não são apagados. Feche o navegador antes.",
                CleanupGroup.Navegadores, CleanupRisk.Seguro, false, true, () => ChromiumTargets(userData), onlyIfExists: true);
        }

        Chromium("cache-chrome", "Google Chrome", Path.Combine(Local, @"Google\Chrome\User Data"));
        Chromium("cache-edge", "Microsoft Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"));
        Chromium("cache-brave", "Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"));
        Chromium("cache-vivaldi", "Vivaldi", Path.Combine(Local, @"Vivaldi\User Data"));

        foreach (var (id, name, folder) in new[] { ("cache-opera", "Opera", "Opera Stable"), ("cache-operagx", "Opera GX", "Opera GX Stable") })
        {
            var roaming = Path.Combine(Roaming, "Opera Software", folder);
            var local = Path.Combine(Local, "Opera Software", folder);
            Add(id, $"Cache do {name}",
                $"Arquivos temporários de páginas do {name}. Histórico, senhas e logins não são apagados. Feche o navegador antes.",
                CleanupGroup.Navegadores, CleanupRisk.Seguro, false, true,
                () => [.. ChromiumTargets(roaming, profileIsRoot: true), .. ChromiumTargets(local, profileIsRoot: true)], onlyIfExists: true);
        }

        var ffProfiles = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        Add("cache-firefox", "Cache do Firefox",
            "Arquivos temporários de páginas do Firefox. Histórico, senhas e logins não são apagados. Feche o navegador antes.",
            CleanupGroup.Navegadores, CleanupRisk.Seguro, false, true, () =>
                SafeDirs(ffProfiles).SelectMany(p => new Target[]
                {
                    new(Path.Combine(p, "cache2")),
                    new(Path.Combine(p, "startupCache")),
                    new(Path.Combine(p, "thumbnails")),
                }).ToList(), onlyIfExists: true);
    }

    private static readonly string[] ChromiumProfileCaches = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];
    private static readonly string[] ChromiumRootCaches = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache"];

    private static List<Target> ChromiumTargets(string userData, bool profileIsRoot = false)
    {
        var list = new List<Target>();
        if (!Directory.Exists(userData)) return list;

        var profiles = SafeDirs(userData)
            .Where(d => Path.GetFileName(d) is "Default" or "Guest Profile" || Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal))
            .ToList();
        if (profileIsRoot) profiles.Add(userData);

        foreach (var p in profiles)
            list.AddRange(ChromiumProfileCaches.Select(c => new Target(Path.Combine(p, c))));
        list.AddRange(ChromiumRootCaches.Select(c => new Target(Path.Combine(userData, c))));
        return list;
    }

    private void RegisterDevelopment()
    {
        var npm = Environment.GetEnvironmentVariable("npm_config_cache") is { Length: > 0 } n ? n : Path.Combine(Local, "npm-cache");
        Add("cache-npm", "Cache do npm", "Pacotes baixados pelo npm. Serão baixados de novo quando necessário.",
            CleanupGroup.Desenvolvimento, CleanupRisk.Seguro, false, false, () => [new(Path.Combine(npm, "_cacache"))], onlyIfExists: true);

        var pip = Environment.GetEnvironmentVariable("PIP_CACHE_DIR") is { Length: > 0 } p ? p : Path.Combine(Local, @"pip\cache");
        Add("cache-pip", "Cache do pip (Python)", "Pacotes Python baixados pelo pip.",
            CleanupGroup.Desenvolvimento, CleanupRisk.Seguro, false, false, () => [new(pip)], onlyIfExists: true);

        Add("cache-nuget", "Cache HTTP do NuGet", "Respostas e pacotes baixados pelo NuGet (a pasta de pacotes instalados não é tocada).",
            CleanupGroup.Desenvolvimento, CleanupRisk.Seguro, false, false,
            () => [new(Path.Combine(Local, @"NuGet\v3-cache")), new(Path.Combine(Local, @"NuGet\plugins-cache"))], onlyIfExists: true);

        Add("cache-yarn", "Cache do Yarn", "Pacotes baixados pelo Yarn.",
            CleanupGroup.Desenvolvimento, CleanupRisk.Seguro, false, false, () => [new(Path.Combine(Local, @"Yarn\Cache"))], onlyIfExists: true);

        Add("cache-gradle", "Cache do Gradle", "Dependências baixadas pelo Gradle. O próximo build fica mais demorado.",
            CleanupGroup.Desenvolvimento, CleanupRisk.Moderado, false, false, () => [new(Path.Combine(Profile, @".gradle\caches"))], onlyIfExists: true);
    }

    private void RegisterGamesAndApps()
    {
        Add("cache-discord", "Cache do Discord", "Imagens e arquivos temporários do Discord. Feche o Discord antes.",
            CleanupGroup.JogosEApps, CleanupRisk.Seguro, false, true, () =>
            [
                new(Path.Combine(Roaming, @"discord\Cache")),
                new(Path.Combine(Roaming, @"discord\Code Cache")),
                new(Path.Combine(Roaming, @"discord\GPUCache")),
            ], onlyIfExists: true);

        var steam = SteamPath();
        Add("cache-steam", "Cache web da Steam", "Páginas da loja e da comunidade guardadas pela Steam.",
            CleanupGroup.JogosEApps, CleanupRisk.Seguro, false, true, () =>
            [
                new(Path.Combine(Local, @"Steam\htmlcache")),
                .. steam is null ? Array.Empty<Target>() : [new Target(Path.Combine(steam, @"appcache\httpcache"))],
            ], onlyIfExists: true);

        Add("cache-epic", "Cache web da Epic Games", "Páginas guardadas pelo Epic Games Launcher.",
            CleanupGroup.JogosEApps, CleanupRisk.Seguro, false, true,
            () => SafeDirs(Path.Combine(Local, @"EpicGamesLauncher\Saved")).Where(d => Path.GetFileName(d).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))
                .Select(d => new Target(d)).ToList(), onlyIfExists: true);

        Add("cache-spotify", "Cache do Spotify", "Músicas em cache do Spotify. Músicas baixadas para ouvir offline também são removidas.",
            CleanupGroup.JogosEApps, CleanupRisk.Moderado, false, false, () =>
            [
                new(Path.Combine(Local, @"Spotify\Storage")),
                new(Path.Combine(Local, @"Spotify\Data")),
                .. SafeDirs(Path.Combine(Local, "Packages")).Where(d => Path.GetFileName(d).StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase))
                    .Select(d => new Target(Path.Combine(d, @"LocalCache\Spotify\Data"))),
            ], onlyIfExists: true);

        if (steam is not null)
            Add(SteamOrphansId, "Jogos órfãos da Steam",
                "Pastas em steamapps\\common que a Steam não reconhece como instaladas (restos de jogos desinstalados ou cópias duplicadas). Confira a lista antes.",
                CleanupGroup.JogosEApps, CleanupRisk.Atencao, false, false, () => [], hasEntries: true);
    }

    // =====================================================================
    // Análise
    // =====================================================================

    public Task<CleanupScan> ScanAsync(string categoryId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var def = Find(categoryId);
            switch (categoryId)
            {
                case RecycleBinId:
                    var (size, items) = QueryRecycleBin();
                    return new CleanupScan(def.Info, size, (int)items, []);

                case ComponentStoreId:
                    return new CleanupScan(def.Info, 0, 0, [], "O espaço liberado é medido ao final da limpeza.");

                case SteamOrphansId:
                    var entries = FindSteamOrphans(cancellationToken);
                    return new CleanupScan(def.Info, entries.Sum(e => e.SizeBytes), entries.Count, entries);
            }

            long total = 0;
            var files = 0;
            foreach (var t in def.Targets())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (t.IsFile)
                {
                    if (!File.Exists(t.Path)) continue;
                    total += FastFs.SizeOfFile(t.Path);
                    files++;
                    continue;
                }
                var (b, f) = FastFs.Measure(t.Path, Threshold(t), t.Pattern, cancellationToken);
                total += b;
                files += f;
            }
            return new CleanupScan(def.Info, total, files, []);
        }, cancellationToken);

    // =====================================================================
    // Limpeza
    // =====================================================================

    public async Task<CleanupResult> CleanAsync(string categoryId, IReadOnlyCollection<string>? selectedEntries,
        IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var def = Find(categoryId);
        if (def.Info.RequiresAdmin && !WmiHardwareInfoService.IsAdministrator())
            return new CleanupResult(categoryId, 0, 0, 0, "Requer executar o OtimizadorPC como administrador.");

        try
        {
            return categoryId switch
            {
                RecycleBinId => await Task.Run(EmptyRecycleBin, cancellationToken),
                ComponentStoreId => await CleanComponentStoreAsync(progress, cancellationToken),
                SteamOrphansId => await Task.Run(() => DeleteEntries(categoryId, selectedEntries ?? [], progress, cancellationToken), cancellationToken),
                WindowsOldId => await CleanWindowsOldAsync(def, progress, cancellationToken),
                _ => await Task.Run(() => DeleteTargets(categoryId, def.Targets(), progress, cancellationToken), cancellationToken),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CleanupResult(categoryId, 0, 0, 0, ex.Message);
        }
    }

    private static CleanupResult DeleteTargets(string id, IReadOnlyList<Target> targets, IProgress<double>? progress, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = targets[i];
            if (t.IsFile)
            {
                if (File.Exists(t.Path))
                {
                    var len = FastFs.SizeOfFile(t.Path);
                    try { File.Delete(t.Path); freed += len; deleted++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            else
            {
                var stats = FastFs.DeleteContents(t.Path, Threshold(t), t.Pattern, removeRoot: false, ct: ct);
                freed += stats.FreedBytes;
                deleted += stats.DeletedFiles;
                skipped += stats.SkippedFiles;
            }
            progress?.Report((i + 1.0) / targets.Count);
        }
        return new CleanupResult(id, freed, deleted, skipped);
    }

    private static CleanupResult DeleteEntries(string id, IReadOnlyCollection<string> paths, IProgress<double>? progress, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0, done = 0;
        foreach (var p in paths)
        {
            var stats = FastFs.DeleteContents(p, removeRoot: true, ct: ct);
            freed += stats.FreedBytes;
            deleted += stats.DeletedFiles;
            skipped += stats.SkippedFiles;
            progress?.Report(++done / (double)Math.Max(1, paths.Count));
        }
        return new CleanupResult(id, freed, deleted, skipped);
    }

    private static async Task<CleanupResult> CleanComponentStoreAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var before = FreeSpace(SystemRoot);
        var outcome = await ProcessRunner.RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", line =>
        {
            var m = PercentRegex().Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
                progress?.Report(pct / 100);
        }, ct);
        var freed = Math.Max(0, FreeSpace(SystemRoot) - before);
        return outcome.ExitCode == 0
            ? new CleanupResult(ComponentStoreId, freed, 0, 0)
            : new CleanupResult(ComponentStoreId, freed, 0, 0, $"O DISM terminou com o código {outcome.ExitCode}.");
    }

    [GeneratedRegex(@"(\d{1,3}[.,]\d)%")]
    private static partial Regex PercentRegex();

    private static async Task<CleanupResult> CleanWindowsOldAsync(Def def, IProgress<double>? progress, CancellationToken ct)
    {
        foreach (var t in def.Targets().Where(t => Directory.Exists(t.Path)))
        {
            // A pasta pertence ao TrustedInstaller: assume a posse antes de apagar.
            // A resposta de confirmação do takeown depende do idioma (S em português, Y em inglês).
            var r = await ProcessRunner.RunAsync("takeown.exe", $"/F \"{t.Path}\" /R /D S", ct: ct);
            if (r.ExitCode != 0) await ProcessRunner.RunAsync("takeown.exe", $"/F \"{t.Path}\" /R /D Y", ct: ct);
            await ProcessRunner.RunAsync("icacls.exe", $"\"{t.Path}\" /grant *S-1-5-32-544:F /T /C /Q", ct: ct);
        }
        var targets = def.Targets().Where(t => Directory.Exists(t.Path)).Select(t => t.Path).ToList();
        return DeleteEntries(WindowsOldId, targets, progress, ct);
    }

    // =====================================================================
    // Steam
    // =====================================================================

    private static string? SteamPath()
    {
        var p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        return string.IsNullOrWhiteSpace(p) ? null : p.Replace('/', '\\');
    }

    private static List<CleanupEntry> FindSteamOrphans(CancellationToken ct)
    {
        var steam = SteamPath();
        if (steam is null) return [];

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(SteamLibrary.ParseLibraryFolders(File.ReadAllText(vdf)));

        var result = new List<CleanupEntry>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            var common = Path.Combine(steamapps, "common");
            if (!Directory.Exists(common)) continue;

            var installDirs = SafeFiles(steamapps, "appmanifest_*.acf")
                .Select(f => { try { return SteamLibrary.ParseInstallDir(File.ReadAllText(f)); } catch (IOException) { return null; } })
                .OfType<string>();
            var folders = SafeDirs(common).Select(Path.GetFileName).OfType<string>();

            foreach (var orphan in SteamLibrary.FindOrphanFolders(folders, installDirs))
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.Combine(common, orphan);
                var (bytes, _) = FastFs.Measure(full, ct: ct);
                result.Add(new CleanupEntry(full, orphan, bytes));
            }
        }
        return result.OrderByDescending(e => e.SizeBytes).ToList();
    }

    // =====================================================================
    // Lixeira (Shell API)
    // =====================================================================

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo
    {
        public int Size;
        public long SizeBytes;
        public long NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private static (long Size, long Items) QueryRecycleBin()
    {
        var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.SizeBytes, info.NumItems) : (0, 0);
    }

    private static CleanupResult EmptyRecycleBin()
    {
        var (size, items) = QueryRecycleBin();
        if (items == 0) return new CleanupResult(RecycleBinId, 0, 0, 0);
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return hr == 0
            ? new CleanupResult(RecycleBinId, size, (int)items, 0)
            : new CleanupResult(RecycleBinId, 0, 0, (int)items, "Não foi possível esvaziar a lixeira.");
    }

    // =====================================================================
    // Utilitários
    // =====================================================================

    private Def Find(string id) => _defs.FirstOrDefault(d => d.Info.Id == id)
                                   ?? throw new ArgumentException($"Categoria de limpeza desconhecida: {id}", nameof(id));

    private static DateTime? Threshold(Target t) => t.MinAge is { } age ? DateTime.UtcNow - age : null;

    private static long FreeSpace(string root)
    {
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern)
    {
        try { return Directory.Exists(path) ? Directory.GetFiles(path, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
