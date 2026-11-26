namespace OtimizadorPC.Core.DiskAnalysis;

/// <summary>
/// Pasta da árvore analisada. Arquivos individuais não são guardados (economiza memória em discos
/// com milhões de arquivos): cada pasta guarda o total dos arquivos diretos dela.
/// </summary>
public sealed class DirNode
{
    private List<DirNode>? _children;

    public DirNode(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; set; }
    public DirNode? Parent { get; set; }

    /// <summary>Tamanho total (arquivos diretos + subpastas).</summary>
    public long Size { get; set; }

    /// <summary>Soma apenas dos arquivos diretamente nesta pasta.</summary>
    public long FilesSize { get; set; }
    public int FileCount { get; set; }
    public long TotalFiles { get; set; }
    public long TotalDirs { get; set; }
    public DateTime LastWrite { get; set; }
    public bool AccessDenied { get; set; }

    public IReadOnlyList<DirNode> Children => (IReadOnlyList<DirNode>?)_children ?? [];
    public bool HasChildren => _children is { Count: > 0 };

    public DirNode AddChild(string name)
    {
        var child = new DirNode(name, this);
        lock (this) (_children ??= []).Add(child);
        return child;
    }

    public void SetChildren(List<DirNode> children) => _children = children;

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var parts = new Stack<string>();
            for (var n = this; n.Parent is not null; n = n.Parent) parts.Push(n.Name);
            var root = this;
            while (root.Parent is not null) root = root.Parent;
            return Path.Combine([root.Name, .. parts]);
        }
    }

    public int Depth
    {
        get
        {
            var d = 0;
            for (var n = Parent; n is not null; n = n.Parent) d++;
            return d;
        }
    }

    public double FractionOfParent => Parent is { Size: > 0 } p ? (double)Size / p.Size : 1;

    /// <summary>Soma os tamanhos de baixo para cima e ordena os filhos do maior para o menor.</summary>
    public void ComputeTotals()
    {
        if (_children is not null)
        {
            foreach (var c in _children) c.ComputeTotals();
            _children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
        Size = FilesSize + (_children?.Sum(c => c.Size) ?? 0);
        TotalFiles = FileCount + (_children?.Sum(c => c.TotalFiles) ?? 0);
        TotalDirs = (_children?.Count ?? 0) + (_children?.Sum(c => c.TotalDirs) ?? 0);
        if (_children is { Count: > 0 })
        {
            var latest = _children.Max(c => c.LastWrite);
            if (latest > LastWrite) LastWrite = latest;
        }
    }

    public DirNode? Find(string fullPath)
    {
        var target = Path.TrimEndingDirectorySeparator(fullPath);
        var mine = Path.TrimEndingDirectorySeparator(FullPath);
        if (string.Equals(target, mine, StringComparison.OrdinalIgnoreCase)) return this;
        if (!target.StartsWith(mine, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var c in Children)
        {
            var found = c.Find(fullPath);
            if (found is not null) return found;
        }
        return null;
    }
}

public sealed record FileEntry(string Path, long Size, DateTime LastWrite)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

public sealed record ExtensionStat(string Extension, long Size, long Count, string Kind);

public enum ScanMethod { Rapido, Mft }

public sealed record ScanResult(
    DirNode Root,
    IReadOnlyList<FileEntry> LargestFiles,
    IReadOnlyList<ExtensionStat> Extensions,
    ScanMethod Method,
    TimeSpan Duration,
    long InaccessibleFolders,
    string? Note);

public sealed record ScanProgress(long Files, long Folders, long Bytes, string? CurrentPath);
