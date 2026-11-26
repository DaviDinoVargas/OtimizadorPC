namespace OtimizadorPC.Core.Cleanup;

public enum CleanupRisk { Seguro, Moderado, Atencao }

public enum CleanupGroup { Sistema, Navegadores, Desenvolvimento, JogosEApps }

/// <summary>Categoria de limpeza (o que é, quão seguro é e se precisa de administrador).</summary>
public sealed record CleanupCategory(
    string Id,
    string Name,
    string Description,
    CleanupGroup Group,
    CleanupRisk Risk,
    bool RequiresAdmin,
    bool SelectedByDefault,
    bool HasEntries = false,
    bool SizeKnownOnlyAfterCleaning = false);

/// <summary>Item individual que o usuário pode escolher (ex.: uma pasta de jogo órfã).</summary>
public sealed record CleanupEntry(string Path, string Name, long SizeBytes);

public sealed record CleanupScan(
    CleanupCategory Category,
    long SizeBytes,
    int FileCount,
    IReadOnlyList<CleanupEntry> Entries,
    string? Note = null);

public sealed record CleanupResult(
    string CategoryId,
    long FreedBytes,
    int DeletedFiles,
    int SkippedFiles,
    string? Error = null)
{
    public bool Success => Error is null;
}
