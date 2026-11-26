using System.IO.Enumeration;

namespace OtimizadorPC.Infrastructure.FileSystem;

public readonly record struct DeleteStats(long FreedBytes, int DeletedFiles, int SkippedFiles);

/// <summary>
/// Operações rápidas e seguras em pastas: nunca segue junções/links simbólicos e ignora
/// arquivos em uso ou sem permissão em vez de falhar.
/// </summary>
public static class FastFs
{
    private static EnumerationOptions Options => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Soma o tamanho dos arquivos da pasta (opcionalmente só os mais antigos que a data).</summary>
    public static (long Bytes, int Files) Measure(string directory, DateTime? olderThanUtc = null, string pattern = "*", CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return (0, 0);
        long bytes = 0;
        var files = 0;
        var limit = olderThanUtc ?? DateTime.MaxValue;
        try
        {
            var e = new FileSystemEnumerable<long>(directory, (ref FileSystemEntry entry) => entry.Length, Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && entry.LastWriteTimeUtc.UtcDateTime < limit &&
                    (pattern == "*" || FileSystemName.MatchesSimpleExpression(pattern, entry.FileName)),
            };
            foreach (var len in e)
            {
                ct.ThrowIfCancellationRequested();
                bytes += len;
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (bytes, files);
    }

    /// <summary>
    /// Apaga o conteúdo da pasta. Com <paramref name="removeRoot"/> remove também a própria pasta
    /// se ela ficar vazia. Arquivos em uso são contados como ignorados.
    /// </summary>
    public static DeleteStats DeleteContents(string directory, DateTime? olderThanUtc = null, string pattern = "*",
        bool removeRoot = false, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return default;
        if (IsReparsePoint(directory)) return default;

        long freed = 0;
        int deleted = 0, skipped = 0;
        var limit = olderThanUtc ?? DateTime.MaxValue;

        IEnumerable<(string Path, long Length)> files;
        try
        {
            files = new FileSystemEnumerable<(string, long)>(directory,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length), Options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    !entry.IsDirectory && entry.LastWriteTimeUtc.UtcDateTime < limit &&
                    (pattern == "*" || FileSystemName.MatchesSimpleExpression(pattern, entry.FileName)),
            }.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }

        foreach (var (path, length) in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                File.Delete(path);
                freed += length;
                deleted++;
                progress?.Report(freed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        RemoveEmptyDirectories(directory, removeRoot);
        return new DeleteStats(freed, deleted, skipped);
    }

    /// <summary>Remove pastas vazias de baixo para cima.</summary>
    public static void RemoveEmptyDirectories(string root, bool includeRoot)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(root, "*", Options).OrderByDescending(d => d.Length).ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    public static long SizeOfFile(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }
}
