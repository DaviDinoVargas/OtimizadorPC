namespace OtimizadorPC.Core.Programs;

public enum ProgramSource { Programa, Loja }

/// <summary>Programa instalado (entrada de desinstalação do Windows ou app da Microsoft Store).</summary>
public sealed record InstalledProgram
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>Tamanho informado pelo instalador (0 quando não informado).</summary>
    public long SizeBytes { get; init; }

    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public ProgramSource Source { get; init; }

    /// <summary>Chave do registro com a entrada (ex.: HKLM\SOFTWARE\...\Uninstall\{GUID}).</summary>
    public string? RegistryPath { get; init; }

    public bool IsMsi { get; init; }
    public string? PackageFullName { get; init; }

    /// <summary>O desinstalador indicado pela entrada não existe mais (entrada órfã).</summary>
    public bool IsBroken { get; init; }
}

public enum LeftoverKind { Pasta, Registro }

/// <summary>Sobra de um programa desinstalado: pasta ou chave do registro.</summary>
public sealed record Leftover(string ProgramName, LeftoverKind Kind, string Path, long SizeBytes, bool Recommended);

public sealed record UninstallOutcome(bool Success, bool RebootRequired, string Message);

/// <summary>Linha de comando separada em executável e argumentos.</summary>
public sealed record CommandLine(string FileName, string Arguments);
