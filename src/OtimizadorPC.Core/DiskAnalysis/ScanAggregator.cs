namespace OtimizadorPC.Core.DiskAnalysis;

/// <summary>
/// Junta estatísticas durante a análise (maiores arquivos e tipos) de forma segura entre threads.
/// Usado pelos dois motores de análise.
/// </summary>
public sealed class ScanAggregator
{
    private readonly int _topCount;
    private readonly PriorityQueue<FileEntry, long> _top = new();
    private readonly Dictionary<string, (long Size, long Count)> _ext = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _topLock = new();
    private readonly object _extLock = new();
    private long _minTop;

    public ScanAggregator(int topCount = 200) => _topCount = topCount;

    public long Files;
    public long Folders;
    public long Bytes;
    public long Inaccessible;

    public void AddFile(string path, long size, DateTime lastWrite) =>
        AddFile(Path.GetExtension(path), size, lastWrite, () => path);

    /// <summary>
    /// Registra um arquivo. O caminho completo só é montado se o arquivo entrar na lista dos
    /// maiores, evitando milhões de alocações em discos grandes.
    /// </summary>
    public void AddFile(string extension, long size, DateTime lastWrite, Func<string> pathFactory)
    {
        Interlocked.Increment(ref Files);
        Interlocked.Add(ref Bytes, size);

        lock (_extLock)
        {
            _ext.TryGetValue(extension, out var cur);
            _ext[extension] = (cur.Size + size, cur.Count + 1);
        }

        if (size <= Volatile.Read(ref _minTop) && Volatile.Read(ref _topFull)) return;
        lock (_topLock)
        {
            _top.Enqueue(new FileEntry(pathFactory(), size, lastWrite), size);
            if (_top.Count > _topCount) _top.Dequeue();
            if (_top.Count >= _topCount && _top.TryPeek(out _, out var min))
            {
                Volatile.Write(ref _minTop, min);
                Volatile.Write(ref _topFull, true);
            }
        }
    }

    private bool _topFull;

    /// <summary>True se um arquivo desse tamanho pode entrar na lista dos maiores.</summary>
    public bool IsTopCandidate(long size) => !Volatile.Read(ref _topFull) || size > Volatile.Read(ref _minTop);

    public IReadOnlyList<FileEntry> LargestFiles()
    {
        lock (_topLock) return _top.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Size).ToList();
    }

    public IReadOnlyList<ExtensionStat> Extensions()
    {
        lock (_extLock)
            return _ext.Select(kv => new ExtensionStat(string.IsNullOrEmpty(kv.Key) ? "(sem extensão)" : kv.Key.ToLowerInvariant(),
                    kv.Value.Size, kv.Value.Count, FileKinds.Of(kv.Key)))
                .GroupBy(e => e.Extension)
                .Select(g => new ExtensionStat(g.Key, g.Sum(x => x.Size), g.Sum(x => x.Count), g.First().Kind))
                .OrderByDescending(e => e.Size)
                .ToList();
    }
}

/// <summary>Tipo amigável de arquivo pela extensão (também usado para colorir o treemap).</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(string kind, params string[] exts) { foreach (var e in exts) Map["." + e] = kind; }
        Add("Vídeo", "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "ts");
        Add("Imagem", "jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "psd", "raw", "heic", "svg", "ico", "clip", "kra");
        Add("Áudio", "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus");
        Add("Compactado", "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "cab", "esd", "wim");
        Add("Instalador / disco", "iso", "img", "vhd", "vhdx", "msi", "msix", "appx", "vmdk");
        Add("Programa", "exe", "dll", "sys", "ocx", "so", "bin", "jar");
        Add("Documento", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "odt", "ods", "md", "epub", "csv");
        Add("Código", "cs", "js", "ts", "py", "java", "cpp", "h", "json", "xml", "html", "css", "xaml", "sql");
        Add("Jogo / pacote", "pak", "upk", "bundle", "vpk", "blk", "pck", "assets", "unity3d", "uasset", "ucas", "utoc", "ress", "arc", "wad", "client", "vdf", "dds");
        Add("Banco de dados / cache", "db", "sqlite", "mdf", "ldf", "log", "tmp", "cache", "dat", "idx");
    }

    public static string Of(string extension) =>
        Map.TryGetValue(extension.StartsWith('.') ? extension : "." + extension, out var k) ? k : "Outros";
}
