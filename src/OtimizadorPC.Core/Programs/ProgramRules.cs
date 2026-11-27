using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OtimizadorPC.Core.Programs;

/// <summary>Regras puras (testáveis) da lista de programas: o que listar, como desinstalar e o que é sobra.</summary>
public static partial class ProgramRules
{
    // =====================================================================
    // Quais entradas do registro viram programas na lista
    // =====================================================================

    /// <summary>
    /// Esconde componentes do sistema, atualizações e patches — o mesmo critério de "Aplicativos instalados" do Windows.
    /// </summary>
    public static bool ShouldList(string? displayName, bool systemComponent, string? parentKeyName, string? releaseType, bool hasUninstaller)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        if (systemComponent || !string.IsNullOrEmpty(parentKeyName)) return false;
        if (releaseType is { } rt && (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) || rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (KbPattern().IsMatch(displayName)) return false;
        return hasUninstaller;
    }

    /// <summary>Data de instalação no formato do registro (aaaammdd).</summary>
    public static DateTime? ParseInstallDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        foreach (var f in new[] { "yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "M/d/yyyy" })
            if (DateTime.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1990 && d <= DateTime.Now.AddDays(1))
                return d;
        return null;
    }

    // =====================================================================
    // Linha de comando do desinstalador
    // =====================================================================

    /// <summary>
    /// Separa executável e argumentos, aceitando caminho entre aspas, caminho com espaços sem aspas
    /// ("C:\Program Files\App\uninstall.exe /S") e comandos simples ("MsiExec.exe /X{GUID}").
    /// </summary>
    public static CommandLine? Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var c = command.Trim();

        if (c[0] == '"')
        {
            var end = c.IndexOf('"', 1);
            if (end < 0) return new CommandLine(c.Trim('"'), string.Empty);
            return new CommandLine(c[1..end], c[(end + 1)..].Trim());
        }

        // Sem aspas: o executável termina no primeiro ".exe" (caminhos com espaços são comuns aqui)
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            var cut = exe + 4;
            return new CommandLine(c[..cut], c[cut..].Trim());
        }

        var space = c.IndexOf(' ');
        return space < 0 ? new CommandLine(c, string.Empty) : new CommandLine(c[..space], c[(space + 1)..].Trim());
    }

    /// <summary>Código de produto MSI ({GUID}) dentro de um texto, se houver.</summary>
    public static string? MsiProductCode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = GuidPattern().Match(text);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Comando de desinstalação a usar. MSI vira "msiexec /x {GUID}" (com /passive no modo silencioso, que mostra
    /// só a barra de progresso). Nos demais, o comando silencioso do próprio instalador quando existir.
    /// </summary>
    public static CommandLine? UninstallCommand(InstalledProgram p, bool quiet)
    {
        if (p.IsMsi && (MsiProductCode(p.UninstallString) ?? MsiProductCode(p.Id)) is { } code)
            return new CommandLine("msiexec.exe", quiet ? $"/x {code} /passive /norestart" : $"/x {code}");

        if (quiet && Parse(p.QuietUninstallString) is { } q) return q;
        return Parse(p.UninstallString);
    }

    /// <summary>Interpretação do código de saída do desinstalador (inclui os códigos do Windows Installer).</summary>
    public static UninstallOutcome InterpretExitCode(int code) => code switch
    {
        0 => new(true, false, "Desinstalado."),
        1605 => new(true, false, "O programa já não estava instalado."),
        1641 or 3010 => new(true, true, "Desinstalado. Reinicie o computador para concluir."),
        1602 => new(false, false, "Desinstalação cancelada."),
        1618 => new(false, false, "Outra instalação está em andamento. Tente de novo em instantes."),
        _ => new(false, false, $"O desinstalador terminou com o código {code}."),
    };

    // =====================================================================
    // Sobras
    // =====================================================================

    /// <summary>Pastas genéricas que nunca são tratadas como sobra de um programa.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "common files", "microsoft shared", "packages", "temp", "programs", "intel", "amd", "nvidia",
        "nvidia corporation", "google", "mozilla", "apple", "adobe", "steam", "epic games", "windowsapps", "microsoft corporation",
        "internet explorer", "windows defender", "windows nt", "dotnet", "reference assemblies", "msbuild", "installshield installation information",
        "package cache", "ssh", "crashdumps", "d3dscache", "connecteddevicesplatform", "comms", "virtualstore", "nuget", "npm",
    };

    /// <summary>Nome normalizado para comparação: sem versão, arquitetura, acentos e pontuação.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var s = name.ToLowerInvariant();
        s = VersionPattern().Replace(s, " ");
        s = ArchPattern().Replace(s, " ");
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Uma pasta é sobra do programa quando o nome dela, normalizado, é igual ao do programa (ex.: "Discord" →
    /// "Discord") ou ao do fabricante — este último só se nenhum outro programa instalado for do mesmo fabricante.
    /// </summary>
    public static bool IsLeftoverFolder(string folderName, InstalledProgram program, IEnumerable<InstalledProgram> stillInstalled)
    {
        if (Protected.Contains(folderName.Trim())) return false;
        var folder = Normalize(folderName);
        if (folder.Length < 3) return false;

        var name = Normalize(program.Name);
        if (folder == name) return !stillInstalled.Any(o => Normalize(o.Name) == folder);

        var publisher = Normalize(CleanPublisher(program.Publisher));
        if (publisher.Length >= 3 && folder == publisher)
            return !stillInstalled.Any(o => Normalize(CleanPublisher(o.Publisher)) == publisher || Normalize(o.Name).StartsWith(folder, StringComparison.Ordinal));

        return false;
    }

    /// <summary>Tira sufixos jurídicos do fabricante ("Valve Corporation" → "Valve").</summary>
    public static string CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        return CompanySuffix().Replace(publisher, string.Empty).Trim(' ', ',', '.');
    }

    [GeneratedRegex(@"\bKB\d{6,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbPattern();

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\bv?\d+(\.\d+)+\b|\(\s*\d+(\.\d+)*\s*\)")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\(?\b(x64|x86|64-bit|32-bit|64 bits|32 bits|amd64|arm64)\b\)?", RegexOptions.IgnoreCase)]
    private static partial Regex ArchPattern();

    [GeneratedRegex(@",?\s*\b(inc|llc|ltd|ltda|gmbh|corp|corporation|co|limited|s\.a|sa|ag|bv|srl)\b\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();
}
