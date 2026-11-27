namespace OtimizadorPC.Core.DiskAnalysis;

public sealed record DuplicateFile(string Path, long Size, DateTime LastWrite)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Arquivos com conteúdo idêntico. Manter um libera <see cref="Wasted"/> bytes.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<DuplicateFile> Files)
{
    public long Wasted => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long Done, long Total, string? CurrentPath);

/// <summary>Leitura de conteúdo usada pela busca de duplicados (implementada na infraestrutura).</summary>
public interface IFileHasher
{
    /// <summary>Resumo rápido: início e fim do arquivo. Nulo se não der para ler.</summary>
    Task<string?> PartialAsync(string path, long size, CancellationToken ct);

    /// <summary>Resumo do arquivo inteiro. Nulo se não der para ler.</summary>
    Task<string?> FullAsync(string path, CancellationToken ct);

    /// <summary>
    /// Identidade física do arquivo (volume + índice). Dois caminhos com a mesma identidade são o mesmo
    /// arquivo (link físico) e apagar um não libera espaço. Nulo se não der para descobrir.
    /// </summary>
    string? Identity(string path);

    /// <summary>Até este tamanho o resumo rápido já cobre o arquivo inteiro.</summary>
    long PartialCoversWholeFileUpTo { get; }
}

/// <summary>
/// Busca de duplicados em etapas, da mais barata para a mais cara: mesmo tamanho → mesmo início/fim →
/// mesmo conteúdo inteiro. Só lê o arquivo todo quando as etapas anteriores não descartaram.
/// </summary>
public static class DuplicateFinder
{
    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        IEnumerable<DuplicateFile> files, IFileHasher hasher, IProgress<DuplicateProgress>? progress, CancellationToken ct, int parallelism = 4)
    {
        // 1) Mesmo tamanho
        var bySize = files.Where(f => f.Size > 0)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

        // 2) Links físicos contam como um arquivo só
        var candidates = new List<List<DuplicateFile>>();
        foreach (var group in bySize)
        {
            ct.ThrowIfCancellationRequested();
            var unique = group
                .GroupBy(f => hasher.Identity(f.Path) ?? f.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (unique.Count > 1) candidates.Add(unique);
        }

        // 3) Início e fim iguais
        var partialGroups = await HashStageAsync(candidates, "Comparando início e fim", (f, c) => hasher.PartialAsync(f.Path, f.Size, c), progress, parallelism, ct);

        // 4) Conteúdo inteiro igual (pulado quando o resumo rápido já leu o arquivo todo)
        var small = partialGroups.Where(g => g.Files[0].Size <= hasher.PartialCoversWholeFileUpTo).ToList();
        var large = partialGroups.Where(g => g.Files[0].Size > hasher.PartialCoversWholeFileUpTo).Select(g => g.Files.ToList()).ToList();
        var fullGroups = await HashStageAsync(large, "Comparando conteúdo", (f, c) => hasher.FullAsync(f.Path, c), progress, Math.Max(1, parallelism / 2), ct);

        return small.Concat(fullGroups)
            .OrderByDescending(g => g.Wasted)
            .ThenBy(g => g.Files[0].Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<List<DuplicateGroup>> HashStageAsync(
        List<List<DuplicateFile>> groups, string stage, Func<DuplicateFile, CancellationToken, Task<string?>> hash,
        IProgress<DuplicateProgress>? progress, int parallelism, CancellationToken ct)
    {
        var all = groups.SelectMany(g => g).ToList();
        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<DuplicateFile, string?>();
        long done = 0;
        await Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (f, c) =>
        {
            hashes[f] = await hash(f, c).ConfigureAwait(false);
            var n = Interlocked.Increment(ref done);
            if (n % 16 == 0 || n == all.Count) progress?.Report(new DuplicateProgress(stage, n, all.Count, f.Path));
        }).ConfigureAwait(false);

        var result = new List<DuplicateGroup>();
        foreach (var group in groups)
        {
            foreach (var same in group.Where(f => hashes.GetValueOrDefault(f) is not null).GroupBy(f => hashes[f]!))
            {
                var list = same.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
                if (list.Count > 1) result.Add(new DuplicateGroup(same.Key, list[0].Size, list));
            }
        }
        return result;
    }

    /// <summary>
    /// Qual cópia manter: evita as que estão em Downloads/temporários ou com nome de cópia ("(1)", "- Cópia"),
    /// depois prefere o caminho mais curto e, por fim, o arquivo mais antigo (provável original).
    /// </summary>
    public static DuplicateFile SuggestKeep(DuplicateGroup group) => group.Files
        .OrderBy(CopyPenalty)
        .ThenBy(f => f.Path.Count(ch => ch is '\\' or '/'))
        .ThenBy(f => f.LastWrite)
        .ThenBy(f => f.Path.Length)
        .First();

    private static int CopyPenalty(DuplicateFile f)
    {
        var p = f.Path.ToLowerInvariant();
        var penalty = 0;
        if (p.Contains(@"\downloads\") || p.Contains(@"\temp\") || p.Contains(@"\tmp\") || p.Contains(@"\$recycle.bin\")) penalty += 2;

        // Nome de cópia no arquivo ou em qualquer pasta do caminho ("Projeto - Cópia\app.exe")
        var parts = p.Split('\\', '/');
        parts[^1] = Path.GetFileNameWithoutExtension(parts[^1]);
        if (parts.Any(IsCopyName)) penalty += 3;
        return penalty;

        static bool IsCopyName(string s) =>
            (s.EndsWith(')') && System.Text.RegularExpressions.Regex.IsMatch(s, @" \(\d+\)$")) ||
            s.Contains("- cópia") || s.Contains("- copia") || s.Contains("- copy") || s.StartsWith("cópia de ") || s.StartsWith("copia de ") || s.StartsWith("copy of ");
    }
}
