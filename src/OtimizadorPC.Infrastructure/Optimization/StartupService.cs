using System.Runtime.Versioning;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Optimization;

namespace OtimizadorPC.Infrastructure.Optimization;

/// <summary>
/// Programas que iniciam com o Windows. Ativar/desativar usa as mesmas chaves
/// "StartupApproved" do Gerenciador de Tarefas, então a mudança aparece lá também e é reversível.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupService : IStartupService
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public IReadOnlyList<StartupItem> List()
    {
        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, Run, Approved + "Run", StartupSource.RegistroUsuario, admin: false);
        ReadRunKey(items, Registry.LocalMachine, Run, Approved + "Run", StartupSource.RegistroSistema, admin: true);
        ReadRunKey(items, Registry.LocalMachine, Run32, Approved + "Run32", StartupSource.RegistroSistema, admin: true);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupSource.PastaUsuario, admin: false);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, StartupSource.PastaSistema, admin: true);
        ReadStoreApps(items);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void SetEnabled(StartupItem item, bool enabled)
    {
        var (root, approvedKey, valueName) = Locate(item);
        if (item.Source == StartupSource.AppDaLoja)
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"{AppModel}\{item.Id.Split('|')[1]}", writable: true)
                          ?? throw new InvalidOperationException("Item não encontrado.");
            k.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
            return;
        }

        using var key = root.CreateSubKey(approvedKey, writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    private static (RegistryKey Root, string ApprovedKey, string ValueName) Locate(StartupItem item)
    {
        var parts = item.Id.Split('|', 3);
        var valueName = parts[^1];
        return parts[0] switch
        {
            "hkcu" => (Registry.CurrentUser, Approved + "Run", valueName),
            "hklm" => (Registry.LocalMachine, Approved + "Run", valueName),
            "hklm32" => (Registry.LocalMachine, Approved + "Run32", valueName),
            "pasta-usuario" => (Registry.CurrentUser, Approved + "StartupFolder", valueName),
            "pasta-sistema" => (Registry.LocalMachine, Approved + "StartupFolder", valueName),
            _ => (Registry.CurrentUser, string.Empty, valueName),
        };
    }

    private static void ReadRunKey(List<StartupItem> items, RegistryKey root, string runKey, string approvedKey, StartupSource source, bool admin)
    {
        using var run = root.OpenSubKey(runKey);
        if (run is null) return;
        using var approved = root.OpenSubKey(approvedKey);
        var prefix = root == Registry.CurrentUser ? "hkcu" : runKey == Run32 ? "hklm32" : "hklm";
        foreach (var name in run.GetValueNames().Where(n => !string.IsNullOrEmpty(n)))
        {
            items.Add(new StartupItem($"{prefix}|{name}", name, run.GetValue(name)?.ToString() ?? string.Empty, source,
                IsEnabled(approved, name), admin));
        }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, RegistryKey root, StartupSource source, bool admin)
    {
        if (!Directory.Exists(folder)) return;
        using var approved = root.OpenSubKey(Approved + "StartupFolder");
        var prefix = source == StartupSource.PastaUsuario ? "pasta-usuario" : "pasta-sistema";
        foreach (var file in Directory.GetFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(file);
            items.Add(new StartupItem($"{prefix}|{fileName}", Path.GetFileNameWithoutExtension(file), file, source,
                IsEnabled(approved, fileName), admin));
        }
    }

    private static void ReadStoreApps(List<StartupItem> items)
    {
        using var root = Registry.CurrentUser.OpenSubKey(AppModel);
        if (root is null) return;
        foreach (var family in root.GetSubKeyNames())
        {
            using var familyKey = root.OpenSubKey(family);
            if (familyKey is null) continue;
            foreach (var task in familyKey.GetSubKeyNames())
            {
                using var taskKey = familyKey.OpenSubKey(task);
                if (taskKey?.GetValue("State") is not int state) continue;
                var appName = family.Split('_')[0].Split('.').Last();
                items.Add(new StartupItem($"loja|{family}\\{task}", $"{appName} ({task})", family, StartupSource.AppDaLoja,
                    state is 2 or 4, false));
            }
        }
    }

    /// <summary>Sem valor em StartupApproved = ativo. Primeiro byte par (02, 06) = ativo; ímpar (03, 07) = desativado.</summary>
    public static bool IsEnabled(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
}
