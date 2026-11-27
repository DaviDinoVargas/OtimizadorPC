using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OtimizadorPC.App.Controls;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Hardware;
using OtimizadorPC.Core.Settings;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public sealed record DriveCard(string Path, string Title, string Subtitle, double UsedFraction, DiskMediaKind Media)
{
    public override string ToString() => $"{Title} — {Subtitle}";
}

public enum TreeSort { Tamanho, Nome, Arquivos, Modificado }

/// <summary>Linha da árvore do Dinosize (pasta ou "arquivos soltos" de uma pasta).</summary>
public sealed partial class TreeRow : ObservableObject
{
    public required DirNode? Node { get; init; }
    public required DirNode Owner { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required long Files { get; init; }
    public required DateTime LastWrite { get; init; }
    public required int Depth { get; init; }
    public required double Fraction { get; init; }
    public required Brush ColorBrush { get; init; }
    public string? PathHint { get; init; }

    [ObservableProperty] private bool _isExpanded;

    public bool IsFolder => Node is not null;
    public bool CanExpand => Node is { HasChildren: true } || Node is { FilesSize: > 0, HasChildren: true };
    public double Indent => Depth * 18;
    public System.Windows.Thickness IndentMargin => new(Indent, 0, 0, 0);
    public string SizeText => ByteSize.Format(Size);
    public string PercentText => $"{Fraction:P1}";
    public string FilesText => $"{Files:N0}";
    public string ModifiedText => LastWrite > DateTime.MinValue ? LastWrite.ToLocalTime().ToString("dd/MM/yyyy") : "—";
    public string FullPath => Node?.FullPath ?? Owner.FullPath;
}

public sealed class FileRow
{
    public required FileEntry File { get; init; }
    public string SizeText => ByteSize.Format(File.Size);
    public string Folder => Path.GetDirectoryName(File.Path) ?? string.Empty;
    public string Kind => FileKinds.Of(File.Extension);
    public ImageSource? Icon => IconCache.Get(File.Path);
}

public sealed class ExtensionRow
{
    public required ExtensionStat Stat { get; init; }
    public required double Fraction { get; init; }
    public string SizeText => ByteSize.Format(Stat.Size);
}

/// <summary>
/// Dinosize: árvore de pastas expansível sincronizada com o treemap, maiores arquivos e tipos,
/// com ações de abrir, mover (com junção) e enviar para a Lixeira.
/// </summary>
public sealed partial class DinosizeViewModel : PageViewModel
{
    private const int MaxChildrenPerExpand = 1000;

    private readonly IDiskScanner _scanner;
    private readonly IFileOperations _files;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly SnapshotProvider _snapshots;
    private readonly HashSet<DirNode> _expanded = [];
    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;
    private DirNode? _root;

    private readonly IDuplicateScanner _duplicates;

    public DinosizeViewModel(IDiskScanner scanner, IFileOperations files, IDialogService dialogs, ISettingsService settings, SnapshotProvider snapshots,
        IDuplicateScanner duplicates)
    {
        _duplicates = duplicates;
        _scanner = scanner;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _snapshots = snapshots;
    }

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<TreeRow> Rows { get; } = [];
    public ObservableCollection<DirNode> Breadcrumb { get; } = [];
    public ObservableCollection<FileRow> LargestFiles { get; } = [];
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];

    [ObservableProperty] private DriveCard? _selectedDrive;
    [ObservableProperty] private string? _scanPath;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private TreeRow? _selectedRow;
    [ObservableProperty] private DirNode? _treemapRoot;
    [ObservableProperty] private FileRow? _selectedFile;
    [ObservableProperty] private long _progressFiles;
    [ObservableProperty] private long _progressBytes;
    [ObservableProperty] private string? _progressPath;
    [ObservableProperty] private string _elapsed = "0 s";
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private string? _resultNote;
    [ObservableProperty] private string _treemapTitle = string.Empty;
    [ObservableProperty] private int _bottomTab;
    [ObservableProperty] private string? _operationMessage;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private TreeSort _sort = TreeSort.Tamanho;
    [ObservableProperty] private bool _sortDescending = true;

    public bool ShowEmptyState => !HasResult && !IsScanning;
    partial void OnHasResultChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    public IReadOnlyList<DiskScanMode> Modes { get; } = [DiskScanMode.Automatico, DiskScanMode.Rapido, DiskScanMode.Mft];

    public DiskScanMode Mode
    {
        get => _settings.Current.DiskScanMode;
        set
        {
            _settings.Update(s => s.DiskScanMode = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public string ModeHint => Mode switch
    {
        DiskScanMode.Rapido => "Rápido: lê pasta por pasta em paralelo. Funciona em qualquer disco, sem administrador.",
        DiskScanMode.Mft => "Ultra (MFT): lê a tabela de arquivos do NTFS de uma vez — um disco inteiro em segundos. Exige administrador.",
        _ => "Automático: usa o Ultra (MFT) quando possível e o Rápido nos demais casos.",
    };

    public override async Task OnNavigatedToAsync()
    {
        if (Drives.Count > 0) return;
        var snap = await _snapshots.GetAsync();
        foreach (var v in snap.Volumes)
            Drives.Add(new DriveCard($"{v.Letter}:\\", $"{v.Letter}: {v.Label}".Trim(),
                $"{ByteSize.Format(v.FreeBytes)} livres de {ByteSize.Format(v.TotalBytes)}", v.UsedFraction, v.Media));
        SelectedDrive = Drives.FirstOrDefault(d => snap.Volumes.Any(v => v.IsSystem && d.Path.StartsWith(v.Letter))) ?? Drives.FirstOrDefault();
    }

    // =====================================================================
    // Análise
    // =====================================================================

    [RelayCommand]
    private Task ScanSelected() => SelectedDrive is null ? Task.CompletedTask : ScanAsync(SelectedDrive.Path);

    [RelayCommand]
    private Task ScanDrive(DriveCard drive)
    {
        SelectedDrive = drive;
        return ScanAsync(drive.Path);
    }

    [RelayCommand]
    private async Task PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Escolha a pasta para analisar" };
        if (dialog.ShowDialog() == true) await ScanAsync(dialog.FolderName);
    }

    [RelayCommand]
    private Task Rescan() => ScanPath is null ? Task.CompletedTask : ScanAsync(ScanPath);

    private async Task ScanAsync(string path)
    {
        if (IsScanning) return;
        ScanPath = path;
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ResultNote = null;
        OperationMessage = null;
        _clock = Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressFiles = p.Files;
            ProgressBytes = p.Bytes;
            ProgressPath = p.CurrentPath;
            Elapsed = $"{_clock.Elapsed.TotalSeconds:0.0} s";
        });

        try
        {
            var result = await _scanner.ScanAsync(path, Mode, progress, _cts.Token);
            Apply(result);
        }
        catch (OperationCanceledException)
        {
            OperationMessage = "Análise cancelada.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha na análise de {Caminho}", path);
            OperationMessage = "Não foi possível analisar: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Apply(ScanResult r)
    {
        ClearDuplicates();
        var method = r.Method == ScanMethod.Mft ? "Ultra (MFT)" : "Rápido";
        ResultSummary = $"{ByteSize.Format(r.Root.Size)} em {r.Root.TotalFiles:N0} arquivos e {r.Root.TotalDirs:N0} pastas · " +
                        $"{r.Duration.TotalSeconds:0.0} s no modo {method}" +
                        (r.InaccessibleFolders > 0 ? $" · {r.InaccessibleFolders:N0} pastas sem permissão" : string.Empty);
        ResultNote = r.Note;

        LargestFiles.Clear();
        foreach (var f in r.LargestFiles) LargestFiles.Add(new FileRow { File = f });

        Extensions.Clear();
        var total = Math.Max(1, r.Extensions.Sum(e => e.Size));
        foreach (var e in r.Extensions.Take(80)) Extensions.Add(new ExtensionRow { Stat = e, Fraction = (double)e.Size / total });

        _root = r.Root;
        _expanded.Clear();
        _expanded.Add(r.Root);
        Filter = string.Empty;
        HasResult = true;
        RebuildRows(selectNode: r.Root);
    }

    // =====================================================================
    // Árvore
    // =====================================================================

    /// <summary>Recria a lista achatada a partir da raiz, respeitando as pastas abertas e a ordenação.</summary>
    private void RebuildRows(DirNode? selectNode = null)
    {
        if (_root is null) return;
        var keep = selectNode ?? SelectedRow?.Node;
        Rows.Clear();

        if (!string.IsNullOrWhiteSpace(Filter))
        {
            foreach (var n in Search(_root, Filter.Trim()).Take(500))
                Rows.Add(MakeRow(n, n.Parent ?? n, 0, n.Parent is { Size: > 0 } p ? (double)n.Size / p.Size : 1, ColorFor(n), n.FullPath));
        }
        else
        {
            var rootRow = MakeRow(_root, _root, 0, 1, Brushes.Gray, null);
            rootRow.IsExpanded = true;
            Rows.Add(rootRow);
            AppendChildren(_root, 1, Rows.Count);
        }

        SelectedRow = (keep is null ? null : Rows.FirstOrDefault(r => ReferenceEquals(r.Node, keep))) ?? Rows.FirstOrDefault();
    }

    private static IEnumerable<DirNode> Search(DirNode node, string text)
    {
        foreach (var c in node.Children)
        {
            if (c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)) yield return c;
            foreach (var d in Search(c, text)) yield return d;
        }
    }

    private int AppendChildren(DirNode node, int depth, int insertAt)
    {
        var parentSize = Math.Max(1, node.Size);
        var items = node.Children.Select(c => (Node: (DirNode?)c, c.Name, c.Size, Files: c.TotalFiles, c.LastWrite)).ToList();
        if (node.FilesSize > 0 && node.HasChildren) items.Add((null, "[arquivos nesta pasta]", node.FilesSize, node.FileCount, node.LastWrite));
        items = Order(items).Take(MaxChildrenPerExpand).ToList();

        var index = insertAt;
        foreach (var it in items)
        {
            var color = depth == 1 ? Palette(items.IndexOf(it)) : ColorFor(node);
            var row = MakeRow(it.Node, node, depth, (double)it.Size / parentSize, it.Node is null ? Brushes.Gray : color, null);
            Rows.Insert(index++, row);
            if (it.Node is not null && _expanded.Contains(it.Node))
            {
                row.IsExpanded = true;
                index = AppendChildren(it.Node, depth + 1, index);
            }
        }
        return index;
    }

    private IEnumerable<(DirNode? Node, string Name, long Size, long Files, DateTime LastWrite)> Order(
        IEnumerable<(DirNode? Node, string Name, long Size, long Files, DateTime LastWrite)> items)
    {
        var ordered = Sort switch
        {
            TreeSort.Nome => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            TreeSort.Arquivos => items.OrderBy(i => i.Files),
            TreeSort.Modificado => items.OrderBy(i => i.LastWrite),
            _ => items.OrderBy(i => i.Size),
        };
        var list = ordered.ToList();
        if (SortDescending ^ (Sort == TreeSort.Nome)) list.Reverse();
        return list;
    }

    private TreeRow MakeRow(DirNode? node, DirNode owner, int depth, double fraction, Brush color, string? hint) => new()
    {
        Node = node,
        Owner = owner,
        Name = node?.Name ?? "[arquivos nesta pasta]",
        Size = node?.Size ?? owner.FilesSize,
        Files = node?.TotalFiles ?? owner.FileCount,
        LastWrite = node?.LastWrite ?? owner.LastWrite,
        Depth = depth,
        Fraction = fraction,
        ColorBrush = color,
        PathHint = hint,
    };

    /// <summary>Cor da pasta = cor do seu "ramo" no primeiro nível (a mesma usada no treemap).</summary>
    private Brush ColorFor(DirNode node)
    {
        if (_root is null) return Brushes.Gray;
        var top = node;
        while (top.Parent is not null && !ReferenceEquals(top.Parent, _root)) top = top.Parent;
        if (top.Parent is null) return Brushes.Gray;
        var siblings = Order(_root.Children.Select(c => ((DirNode?)c, c.Name, c.Size, c.TotalFiles, c.LastWrite))).Select(i => i.Item1).ToList();
        return Palette(Math.Max(0, siblings.IndexOf(top)));
    }

    private static Brush Palette(int index)
    {
        var b = new SolidColorBrush(TreemapView.Palette[index % TreemapView.Palette.Length]);
        b.Freeze();
        return b;
    }

    [RelayCommand]
    private void ToggleExpand(TreeRow? row)
    {
        if (row?.Node is not { HasChildren: true } node || !string.IsNullOrWhiteSpace(Filter)) return;
        var index = Rows.IndexOf(row);
        if (row.IsExpanded)
        {
            _expanded.Remove(node);
            row.IsExpanded = false;
            while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth) Rows.RemoveAt(index + 1);
        }
        else
        {
            _expanded.Add(node);
            row.IsExpanded = true;
            AppendChildren(node, row.Depth + 1, index + 1);
        }
    }

    [RelayCommand]
    private void Expand(TreeRow? row)
    {
        if (row is { IsExpanded: false, CanExpand: true }) ToggleExpand(row);
    }

    [RelayCommand]
    private void CollapseOrParent(TreeRow? row)
    {
        if (row is null) return;
        if (row.IsExpanded && row.Depth > 0) { ToggleExpand(row); return; }
        var index = Rows.IndexOf(row);
        for (var i = index - 1; i >= 0; i--)
            if (Rows[i].Depth < row.Depth) { SelectedRow = Rows[i]; return; }
    }

    partial void OnSelectedRowChanged(TreeRow? value)
    {
        if (value is null) return;
        var target = value.Node is { HasChildren: true } || value.Node is { FilesSize: > 0 } ? value.Node! : value.Owner;
        if (!ReferenceEquals(target, TreemapRoot)) SetTreemapRoot(target);
    }

    private void SetTreemapRoot(DirNode node)
    {
        TreemapRoot = node;
        TreemapTitle = $"{node.Name} · {ByteSize.Format(node.Size)} · {node.TotalFiles:N0} arquivos";
        Breadcrumb.Clear();
        for (var n = node; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);
    }

    /// <summary>Clique no treemap ou na trilha: abre as pastas até o item e o seleciona na árvore.</summary>
    [RelayCommand]
    private void Reveal(DirNode? node)
    {
        if (node is null || _root is null) return;
        if (!string.IsNullOrWhiteSpace(Filter)) Filter = string.Empty;
        for (var p = node.Parent; p is not null; p = p.Parent) _expanded.Add(p);
        RebuildRows(selectNode: node);
        SetTreemapRoot(node);
    }

    [RelayCommand]
    private void GoUp()
    {
        if (TreemapRoot?.Parent is { } p) Reveal(p);
    }

    partial void OnFilterChanged(string value) => RebuildRows();

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<TreeSort>(column, out var s)) return;
        if (s == Sort) SortDescending = !SortDescending;
        else
        {
            Sort = s;
            SortDescending = s != TreeSort.Nome;
        }
        RebuildRows();
    }

    // =====================================================================
    // Ações
    // =====================================================================

    private static (string? Path, long Size, string? Name) Target(object? item) => item switch
    {
        TreeRow { Node: { } n } => (n.FullPath, n.Size, n.Name),
        FileRow f => (f.File.Path, f.File.Size, f.File.Name),
        _ => (null, 0, null),
    };

    [RelayCommand]
    private void ShowInExplorer(object? item)
    {
        var path = item switch
        {
            TreeRow r => r.FullPath,
            FileRow f => f.File.Path,
            DuplicateFileRow d => d.File.Path,
            string s => s,
            _ => SelectedRow?.FullPath,
        };
        if (path is null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Recycle(object? item)
    {
        var (path, size, name) = Target(item ?? SelectedRow);
        if (path is null) return;
        if (!await _dialogs.ConfirmAsync("Enviar para a Lixeira",
                $"\"{name}\" ({ByteSize.Format(size)}) vai para a Lixeira. Você pode restaurá-lo de lá se precisar.\n\n{path}", "Enviar para a Lixeira"))
            return;

        IsWorking = true;
        try
        {
            await Task.Run(() => _files.SendToRecycleBin(path));
            OperationMessage = $"\"{name}\" foi para a Lixeira. Esvazie a Lixeira para liberar o espaço.";
            Log.Information("Dinosize enviou para a Lixeira: {Caminho}", path);
            RemoveFromTree(item ?? SelectedRow, size);
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task Move(TreeRow? row)
    {
        row ??= SelectedRow;
        if (row?.Node is not { } node) return;
        var dialog = new OpenFolderDialog { Title = $"Para onde mover \"{node.Name}\"?" };
        if (dialog.ShowDialog() != true) return;
        var destination = Path.Combine(dialog.FolderName, node.Name);

        var choice = await _dialogs.ChoiceAsync("Mover pasta",
            $"Mover \"{node.Name}\" ({ByteSize.Format(node.Size)}) para:\n{destination}\n\n" +
            "Recomendado: deixar uma junção no lugar antigo. Assim programas e jogos continuam encontrando os arquivos como se nada tivesse mudado.\n\n" +
            "Feche os programas que usam essa pasta antes de continuar.",
            "Mover e deixar junção", "Só mover");
        if (choice == 0) return;

        IsWorking = true;
        var progress = new Progress<string>(s => OperationMessage = s);
        try
        {
            await _files.MoveFolderAsync(node.FullPath, destination, leaveJunction: choice == 1, progress, CancellationToken.None);
            OperationMessage = $"\"{node.Name}\" foi movida para {destination}" + (choice == 1 ? " e uma junção ficou no local antigo." : ".");
            Log.Information("Dinosize moveu {Origem} para {Destino} (junção={Juncao})", node.FullPath, destination, choice == 1);
            RemoveFromTree(row, node.Size);
            _ = _snapshots.GetAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            OperationMessage = ex.Message;
            Log.Warning(ex, "Falha ao mover pasta");
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Atualiza os totais sem refazer a análise depois de apagar ou mover algo.</summary>
    private void RemoveFromTree(object? item, long size)
    {
        if (item is TreeRow { Node: { Parent: { } parent } node })
        {
            parent.SetChildren(parent.Children.Where(c => !ReferenceEquals(c, node)).ToList());
            for (var p = parent; p is not null; p = p.Parent) p.Size -= size;
            _expanded.Remove(node);
            RebuildRows(selectNode: parent);
            SetTreemapRoot(parent);
        }
        else if (item is FileRow f)
        {
            LargestFiles.Remove(f);
            var dir = _root?.Find(Path.GetDirectoryName(f.File.Path) ?? string.Empty);
            if (dir is not null)
            {
                dir.FilesSize -= size;
                for (var p = dir; p is not null; p = p.Parent) p.Size -= size;
            }
            RebuildRows();
            if (TreemapRoot is not null) SetTreemapRoot(TreemapRoot);
        }
    }
}
