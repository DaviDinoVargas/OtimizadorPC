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

public sealed record DriveCard(string Path, string Title, string Subtitle, double UsedFraction, DiskMediaKind Media);

/// <summary>Linha da lista ao lado do mapa: uma pasta (ou os arquivos soltos da pasta atual).</summary>
public sealed class DirRow
{
    public required DirNode? Node { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required double Fraction { get; init; }
    public required long Files { get; init; }
    public required DateTime LastWrite { get; init; }
    public required Brush ColorBrush { get; init; }
    public string SizeText => ByteSize.Format(Size);
    public string FilesText => Node is null ? $"{Files:N0} arquivos" : $"{Files:N0} arquivos · {Node.TotalDirs:N0} pastas";
    public string ModifiedText => LastWrite > DateTime.MinValue ? LastWrite.ToLocalTime().ToString("dd/MM/yyyy") : "—";
    public bool IsFolder => Node is not null;
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

/// <summary>Dinosize: análise de espaço em disco com mapa, maiores arquivos, tipos e migração de pastas.</summary>
public sealed partial class DinosizeViewModel : PageViewModel
{
    private readonly IDiskScanner _scanner;
    private readonly IFileOperations _files;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly SnapshotProvider _snapshots;
    private CancellationTokenSource? _cts;
    private Stopwatch? _clock;

    public DinosizeViewModel(IDiskScanner scanner, IFileOperations files, IDialogService dialogs, ISettingsService settings, SnapshotProvider snapshots)
    {
        _scanner = scanner;
        _files = files;
        _dialogs = dialogs;
        _settings = settings;
        _snapshots = snapshots;
    }

    public ObservableCollection<DriveCard> Drives { get; } = [];
    public ObservableCollection<DirRow> Rows { get; } = [];
    public ObservableCollection<DirNode> Breadcrumb { get; } = [];
    public ObservableCollection<FileRow> LargestFiles { get; } = [];
    public ObservableCollection<ExtensionRow> Extensions { get; } = [];

    [ObservableProperty] private string? _selectedPath;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private DirNode? _currentNode;
    [ObservableProperty] private DirRow? _selectedRow;
    [ObservableProperty] private FileRow? _selectedFile;
    [ObservableProperty] private long _progressFiles;
    [ObservableProperty] private long _progressBytes;
    [ObservableProperty] private string? _progressPath;
    [ObservableProperty] private string _elapsed = "0 s";
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private string? _resultNote;
    [ObservableProperty] private string _currentTitle = string.Empty;
    [ObservableProperty] private string _currentSize = string.Empty;
    [ObservableProperty] private int _bottomTab;
    [ObservableProperty] private string? _operationMessage;
    [ObservableProperty] private bool _isWorking;

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
        DiskScanMode.Rapido => "Rápido: lê pasta por pasta em paralelo. Funciona em qualquer disco e sem administrador.",
        DiskScanMode.Mft => "Ultra (MFT): lê a tabela de arquivos do NTFS de uma vez; um disco inteiro em segundos. Exige administrador.",
        _ => "Automático: usa o modo Ultra (MFT) quando possível e o Rápido nos demais casos.",
    };

    public override async Task OnNavigatedToAsync()
    {
        if (Drives.Count > 0) return;
        var snap = await _snapshots.GetAsync();
        foreach (var v in snap.Volumes)
            Drives.Add(new DriveCard($"{v.Letter}:\\", $"{v.Letter}: {v.Label}".Trim(),
                $"{ByteSize.Format(v.FreeBytes)} livres de {ByteSize.Format(v.TotalBytes)}", v.UsedFraction, v.Media));
    }

    // ---------------- Análise ----------------

    [RelayCommand]
    private Task ScanDrive(DriveCard drive) => ScanAsync(drive.Path);

    [RelayCommand]
    private async Task PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Escolha a pasta para analisar" };
        if (dialog.ShowDialog() == true) await ScanAsync(dialog.FolderName);
    }

    [RelayCommand]
    private Task Rescan() => SelectedPath is null ? Task.CompletedTask : ScanAsync(SelectedPath);

    private async Task ScanAsync(string path)
    {
        if (IsScanning) return;
        SelectedPath = path;
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
        var method = r.Method == ScanMethod.Mft ? "Ultra (MFT)" : "Rápido";
        ResultSummary = $"{ByteSize.Format(r.Root.Size)} em {r.Root.TotalFiles:N0} arquivos e {r.Root.TotalDirs:N0} pastas · " +
                        $"analisado em {r.Duration.TotalSeconds:0.0} s no modo {method}" +
                        (r.InaccessibleFolders > 0 ? $" · {r.InaccessibleFolders:N0} pastas sem permissão de leitura" : string.Empty);
        ResultNote = r.Note;

        LargestFiles.Clear();
        foreach (var f in r.LargestFiles) LargestFiles.Add(new FileRow { File = f });

        Extensions.Clear();
        var total = Math.Max(1, r.Extensions.Sum(e => e.Size));
        foreach (var e in r.Extensions.Take(60)) Extensions.Add(new ExtensionRow { Stat = e, Fraction = (double)e.Size / total });

        HasResult = true;
        Navigate(r.Root);
    }

    // ---------------- Navegação ----------------

    [RelayCommand]
    private void Navigate(DirNode? node)
    {
        if (node is null) return;
        CurrentNode = node;
        CurrentTitle = node.FullPath;
        CurrentSize = $"{ByteSize.Format(node.Size)} · {node.TotalFiles:N0} arquivos";

        Breadcrumb.Clear();
        for (var n = node; n is not null; n = n.Parent) Breadcrumb.Insert(0, n);

        Rows.Clear();
        var parentSize = Math.Max(1, node.Size);
        var items = node.Children.Select(c => (Node: (DirNode?)c, c.Name, c.Size, Files: c.TotalFiles, c.LastWrite)).ToList();
        if (node.FilesSize > 0) items.Add((null, "(arquivos nesta pasta)", node.FilesSize, node.FileCount, node.LastWrite));
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        for (var i = 0; i < items.Count && i < 500; i++)
        {
            var it = items[i];
            var color = TreemapView.Palette[i % TreemapView.Palette.Length];
            var brush = new SolidColorBrush(it.Node is null ? Color.FromRgb(0x9C, 0xA3, 0xAF) : color);
            brush.Freeze();
            Rows.Add(new DirRow
            {
                Node = it.Node, Name = it.Name, Size = it.Size, Fraction = (double)it.Size / parentSize,
                Files = it.Files, LastWrite = it.LastWrite, ColorBrush = brush,
            });
        }
    }

    [RelayCommand]
    private void GoUp()
    {
        if (CurrentNode?.Parent is { } p) Navigate(p);
    }

    [RelayCommand]
    private void OpenRow(DirRow? row)
    {
        if (row?.Node is { } n) Navigate(n);
    }

    // ---------------- Ações ----------------

    private string? SelectedTargetPath => SelectedRow?.Node?.FullPath;

    [RelayCommand]
    private void ShowInExplorer(object? item)
    {
        var path = item switch
        {
            DirRow { Node: { } n } => n.FullPath,
            DirRow => CurrentNode?.FullPath,
            FileRow f => f.File.Path,
            _ => SelectedTargetPath ?? CurrentNode?.FullPath,
        };
        if (path is null) return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task Recycle(object? item)
    {
        var (path, size, name) = item switch
        {
            DirRow { Node: { } n } => (n.FullPath, n.Size, n.Name),
            FileRow f => (f.File.Path, f.File.Size, f.File.Name),
            _ => (null, 0L, null),
        };
        if (path is null) return;
        if (!await _dialogs.ConfirmAsync("Enviar para a Lixeira",
                $"\"{name}\" ({ByteSize.Format(size)}) vai para a Lixeira. Você pode restaurá-lo de lá se precisar.\n\n{path}", "Enviar para a Lixeira"))
            return;

        IsWorking = true;
        try
        {
            await Task.Run(() => _files.SendToRecycleBin(path));
            OperationMessage = $"\"{name}\" foi para a Lixeira. Lembre de esvaziá-la para liberar o espaço.";
            Log.Information("Dinosize enviou para a Lixeira: {Caminho}", path);
            RemoveFromTree(item, size);
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
    private async Task Move(DirRow? row)
    {
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
            OperationMessage = $"\"{node.Name}\" foi movida para {destination}" + (choice == 1 ? " e uma junção foi criada no local antigo." : ".");
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
        if (item is DirRow { Node: { } node } && node.Parent is { } parent)
        {
            parent.SetChildren(parent.Children.Where(c => !ReferenceEquals(c, node)).ToList());
            for (var p = parent; p is not null; p = p.Parent) p.Size -= size;
        }
        else if (item is FileRow f)
        {
            var match = LargestFiles.FirstOrDefault(x => ReferenceEquals(x, f));
            if (match is not null) LargestFiles.Remove(match);
            var dir = Breadcrumb.FirstOrDefault()?.Find(Path.GetDirectoryName(f.File.Path) ?? string.Empty);
            if (dir is not null)
            {
                dir.FilesSize -= size;
                for (var p = dir; p is not null; p = p.Parent) p.Size -= size;
            }
        }
        if (CurrentNode is not null) Navigate(CurrentNode);
    }
}
