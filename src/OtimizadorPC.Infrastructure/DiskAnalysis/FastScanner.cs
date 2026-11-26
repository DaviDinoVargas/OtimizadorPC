using System.IO.Enumeration;
using OtimizadorPC.Core.DiskAnalysis;

namespace OtimizadorPC.Infrastructure.DiskAnalysis;

/// <summary>
/// Motor rápido: enumera pastas em paralelo nos primeiros níveis (cada pasta em uma thread)
/// sem seguir junções. Funciona em qualquer sistema de arquivos e sem administrador.
/// </summary>
internal static class FastScanner
{
    private const int ParallelDepth = 3;

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public static volatile string? CurrentPath;

    public static DirNode Scan(string rootPath, ScanAggregator agg, CancellationToken ct)
    {
        var root = new DirNode(rootPath, null);
        ScanDir(root, rootPath, 0, agg, ct);
        return root;
    }

    private static void ScanDir(DirNode node, string path, int depth, ScanAggregator agg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CurrentPath = path;
        Interlocked.Increment(ref agg.Folders);

        var subdirs = new List<(DirNode Node, string Path)>();
        long filesSize = 0;
        var fileCount = 0;
        var latest = DateTime.MinValue;

        try
        {
            var entries = new FileSystemEnumerable<string?>(path, (ref FileSystemEntry e) =>
            {
                if (e.IsDirectory)
                {
                    // Junções e links simbólicos apontam para outro lugar: não entram na conta
                    return (e.Attributes & FileAttributes.ReparsePoint) != 0 ? null : e.FileName.ToString();
                }

                var len = e.Length;
                var written = e.LastWriteTimeUtc.UtcDateTime;
                filesSize += len;
                fileCount++;
                if (written > latest) latest = written;
                // O caminho completo só é montado para arquivos que podem entrar na lista dos maiores
                var fullName = agg.IsTopCandidate(len) ? Path.Join(path.AsSpan(), e.FileName) : null;
                agg.AddFile(Path.GetExtension(e.FileName).ToString(), len, written, () => fullName ?? path);
                return null;
            }, Options);

            foreach (var dirName in entries)
            {
                if (dirName is null) continue;
                subdirs.Add((node.AddChild(dirName), Path.Combine(path, dirName)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            node.AccessDenied = true;
            Interlocked.Increment(ref agg.Inaccessible);
        }

        node.FilesSize = filesSize;
        node.FileCount = fileCount;
        node.LastWrite = latest;

        if (depth < ParallelDepth && subdirs.Count > 1)
        {
            Parallel.ForEach(subdirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                s => ScanDir(s.Node, s.Path, depth + 1, agg, ct));
        }
        else
        {
            foreach (var s in subdirs) ScanDir(s.Node, s.Path, depth + 1, agg, ct);
        }
    }
}
