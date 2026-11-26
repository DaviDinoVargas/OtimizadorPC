using System.Text.RegularExpressions;

namespace OtimizadorPC.Core.Cleanup;

/// <summary>
/// Leitura dos arquivos de texto da Steam (formato VDF/KeyValues) para encontrar
/// bibliotecas e jogos instalados — usado para achar pastas de jogos órfãs.
/// </summary>
public static partial class SteamLibrary
{
    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"installdir\"\\s+\"(?<d>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    [GeneratedRegex("\"name\"\\s+\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    /// <summary>Bibliotecas listadas em steamapps\libraryfolders.vdf (caminhos com "\\" desescapados).</summary>
    public static IReadOnlyList<string> ParseLibraryFolders(string vdf) =>
        PathRegex().Matches(vdf)
            .Select(m => m.Groups["p"].Value.Replace(@"\\", @"\"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Pasta de instalação (dentro de steamapps\common) declarada em um appmanifest_*.acf.</summary>
    public static string? ParseInstallDir(string acf)
    {
        var m = InstallDirRegex().Match(acf);
        return m.Success ? m.Groups["d"].Value : null;
    }

    public static string? ParseName(string acf)
    {
        var m = NameRegex().Match(acf);
        return m.Success ? m.Groups["n"].Value : null;
    }

    /// <summary>
    /// Pastas de steamapps\common que nenhum appmanifest reconhece. São restos de jogos
    /// desinstalados ou cópias manuais que a Steam não gerencia.
    /// </summary>
    public static IReadOnlyList<string> FindOrphanFolders(IEnumerable<string> commonFolderNames, IEnumerable<string> manifestInstallDirs)
    {
        var known = new HashSet<string>(manifestInstallDirs, StringComparer.OrdinalIgnoreCase);
        // "Steamworks Shared" é mantida pela própria Steam mesmo sem manifesto próprio em algumas bibliotecas
        known.Add("Steamworks Shared");
        return commonFolderNames.Where(n => !known.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
