using System.Runtime.Versioning;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Optimization;
using Windows.Management.Deployment;

namespace OtimizadorPC.Infrastructure.Optimization;

/// <summary>Apps pré-instalados removíveis, via API de implantação do Windows (sem PowerShell).</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AppxService : IAppxService
{
    public IReadOnlyList<InstalledApp> ListRemovable()
    {
        var known = OptimizationCatalog.Apps.ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);
        var pm = new PackageManager();
        var result = new List<InstalledApp>();
        foreach (var p in pm.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (p.IsFramework || p.IsResourcePackage) continue;
                if (!known.TryGetValue(p.Id.Name, out var def)) continue;
                var v = p.Id.Version;
                result.Add(new InstalledApp(def, p.Id.FullName, $"{v.Major}.{v.Minor}.{v.Build}"));
            }
            catch (Exception) { /* pacote inacessível */ }
        }
        return result.OrderBy(a => a.Definition.DisplayName).ToList();
    }

    public async Task RemoveAsync(InstalledApp app, CancellationToken ct = default)
    {
        var pm = new PackageManager();
        var op = pm.RemovePackageAsync(app.FullName);
        var result = await op.AsTask(ct);
        if (result.ExtendedErrorCode is { } err)
            throw new InvalidOperationException($"O Windows não removeu {app.Definition.DisplayName}: {result.ErrorText}", err);
    }
}
