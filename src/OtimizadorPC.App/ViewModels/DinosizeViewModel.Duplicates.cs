using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Formatting;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public sealed record DuplicateMinSize(string Label, long Bytes)
{
    public override string ToString() => Label;
}

/// <summary>Uma cópia dentro de um grupo de duplicados.</summary>
public sealed partial class DuplicateFileRow(DuplicateFile file, DuplicateGroupRow group, bool suggestedKeep) : ObservableObject
{
    public DuplicateFile File { get; } = file;
    public DuplicateGroupRow Group { get; } = group;
    public bool SuggestedKeep { get; } = suggestedKeep;
    public string Folder => Path.GetDirectoryName(File.Path) ?? string.Empty;
    public string DateText => File.LastWrite.ToString("dd/MM/yyyy HH:mm");
    public ImageSource? Icon => IconCache.Get(File.Path);

    [ObservableProperty] private bool _isChecked = !suggestedKeep;

    partial void OnIsCheckedChanged(bool value) => Group.OnChildChanged(this, value);
}

/// <summary>Grupo de arquivos idênticos.</summary>
public sealed partial class DuplicateGroupRow : ObservableObject
{
    private readonly Action _changed;

    public DuplicateGroupRow(DuplicateGroup group, Action changed)
    {
        Group = group;
        _changed = changed;
        var keep = DuplicateFinder.SuggestKeep(group);
        foreach (var f in group.Files) Files.Add(new DuplicateFileRow(f, this, ReferenceEquals(f, keep)));
    }

    public DuplicateGroup Group { get; }
    public ObservableCollection<DuplicateFileRow> Files { get; } = [];
    public string Name => Group.Files[0].Name;
    public string Summary => $"{Files.Count} cópias de {ByteSize.Format(Group.Size)}";
    public string WastedText => ByteSize.Format(Group.Size * (Files.Count - 1));

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Nunca deixa todas as cópias marcadas: desmarca a que acabou de ser marcada.</summary>
    internal void OnChildChanged(DuplicateFileRow row, bool value)
    {
        if (value && Files.All(f => f.IsChecked))
        {
            row.IsChecked = false;
            AllCheckedBlocked?.Invoke();
            return;
        }
        _changed();
    }

    internal Action? AllCheckedBlocked { get; set; }

    internal void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WastedText));
    }
}

public sealed partial class DinosizeViewModel
{
    public ObservableCollection<DuplicateGroupRow> Duplicates { get; } = [];

    public IReadOnlyList<DuplicateMinSize> DuplicateMinSizes { get; } =
    [
        new("a partir de 1 MB", 1L << 20),
        new("a partir de 10 MB", 10L << 20),
        new("a partir de 100 MB", 100L << 20),
        new("a partir de 1 GB", 1L << 30),
    ];

    [ObservableProperty] private DuplicateMinSize? _duplicateMinSize;
    [ObservableProperty] private bool _isFindingDuplicates;
    [ObservableProperty] private string? _duplicateStatus;
    [ObservableProperty] private double _duplicateProgress;
    [ObservableProperty] private bool _duplicateProgressKnown;
    [ObservableProperty] private bool _hasDuplicates;
    [ObservableProperty] private bool _duplicatesSearched;
    [ObservableProperty] private long _duplicatesWasted;
    [ObservableProperty] private int _duplicatesCheckedCount;
    [ObservableProperty] private long _duplicatesCheckedSize;

    private CancellationTokenSource? _dupCts;

    /// <summary>Busca na pasta aberta no mapa (ou na análise inteira).</summary>
    public string? DuplicateSearchRoot => TreemapRoot?.FullPath ?? ScanPath;

    partial void OnTreemapRootChanged(DirNode? value) => OnPropertyChanged(nameof(DuplicateSearchRoot));

    private void ClearDuplicates()
    {
        _dupCts?.Cancel();
        Duplicates.Clear();
        HasDuplicates = false;
        DuplicatesSearched = false;
        DuplicateStatus = null;
        UpdateDuplicateTotals();
    }

    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        if (DuplicateSearchRoot is not { } root || IsFindingDuplicates) return;
        DuplicateMinSize ??= DuplicateMinSizes[0];
        Duplicates.Clear();
        UpdateDuplicateTotals();
        HasDuplicates = false;
        IsFindingDuplicates = true;
        DuplicateProgressKnown = false;
        _dupCts = new CancellationTokenSource();
        var started = DateTime.Now;
        var progress = new Progress<DuplicateProgress>(p =>
        {
            DuplicateProgressKnown = p.Total > 0;
            DuplicateProgress = p.Total > 0 ? (double)p.Done / p.Total : 0;
            DuplicateStatus = p.Total > 0 ? $"{p.Stage}: {p.Done:N0} de {p.Total:N0}" : $"{p.Stage}: {p.Done:N0} arquivos";
        });
        try
        {
            var groups = await _duplicates.FindAsync(root, DuplicateMinSize.Bytes, progress, _dupCts.Token);
            foreach (var g in groups.Take(2000))
                Duplicates.Add(new DuplicateGroupRow(g, UpdateDuplicateTotals) { AllCheckedBlocked = () => DuplicateStatus = "Fica sempre pelo menos uma cópia de cada arquivo." });
            HasDuplicates = Duplicates.Count > 0;
            UpdateDuplicateTotals();
            var secs = (DateTime.Now - started).TotalSeconds;
            DuplicateStatus = HasDuplicates
                ? $"{Duplicates.Count:N0} grupos de arquivos idênticos · {ByteSize.Format(DuplicatesWasted)} em cópias · {secs:0.#} s. As cópias sugeridas já vêm marcadas; o original fica."
                : $"Nenhum arquivo duplicado {DuplicateMinSize.Label} em {root} ({secs:0.#} s).";
            Log.Information("Dinosize duplicados em {Root}: {Grupos} grupos, {Bytes} bytes", root, Duplicates.Count, DuplicatesWasted);
        }
        catch (OperationCanceledException)
        {
            DuplicateStatus = "Busca cancelada.";
        }
        catch (Exception ex)
        {
            DuplicateStatus = "Falha na busca: " + ex.Message;
            Log.Warning(ex, "Falha na busca de duplicados");
        }
        finally
        {
            IsFindingDuplicates = false;
            DuplicatesSearched = true;
        }
    }

    [RelayCommand]
    private void CancelDuplicates() => _dupCts?.Cancel();

    private void UpdateDuplicateTotals()
    {
        DuplicatesWasted = Duplicates.Sum(g => g.Group.Size * (g.Files.Count - 1));
        var chosen = Duplicates.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
        DuplicatesCheckedCount = chosen.Count;
        DuplicatesCheckedSize = chosen.Sum(f => f.File.Size);
    }

    /// <summary>Mantém só esta cópia do grupo e marca as outras.</summary>
    [RelayCommand]
    private static void KeepOnly(DuplicateFileRow? row)
    {
        if (row is null) return;
        row.IsChecked = false;
        foreach (var f in row.Group.Files.Where(f => !ReferenceEquals(f, row))) f.IsChecked = true;
    }

    [RelayCommand]
    private void UncheckAllDuplicates()
    {
        foreach (var f in Duplicates.SelectMany(g => g.Files)) f.IsChecked = false;
    }

    [RelayCommand]
    private void CheckSuggestedDuplicates()
    {
        foreach (var f in Duplicates.SelectMany(g => g.Files)) f.IsChecked = !f.SuggestedKeep;
    }

    [RelayCommand]
    private async Task RecycleDuplicatesAsync()
    {
        var chosen = Duplicates.SelectMany(g => g.Files).Where(f => f.IsChecked).ToList();
        if (chosen.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Enviar cópias para a Lixeira",
                $"{chosen.Count:N0} cópia(s), {ByteSize.Format(chosen.Sum(c => c.File.Size))}, vão para a Lixeira. " +
                "Pelo menos uma cópia de cada arquivo continua no lugar. Você pode restaurar pela Lixeira se precisar.", "Enviar para a Lixeira"))
            return;

        IsWorking = true;
        long freed = 0;
        var errors = 0;
        try
        {
            foreach (var f in chosen)
            {
                // Confere de novo que sobra outra cópia no disco antes de apagar
                if (!f.Group.Files.Any(o => !o.IsChecked && System.IO.File.Exists(o.File.Path))) { errors++; continue; }
                try
                {
                    await Task.Run(() => _files.SendToRecycleBin(f.File.Path));
                    freed += f.File.Size;
                    f.Group.Files.Remove(f);
                    SubtractFromTree(f.File.Path, f.File.Size);
                }
                catch (Exception ex)
                {
                    errors++;
                    Log.Warning(ex, "Falha ao enviar duplicado para a Lixeira: {Path}", f.File.Path);
                }
            }
            foreach (var g in Duplicates.Where(g => g.Files.Count < 2).ToList()) Duplicates.Remove(g);
            foreach (var g in Duplicates) g.NotifyFilesChanged();
            HasDuplicates = Duplicates.Count > 0;
            UpdateDuplicateTotals();
            RebuildRows();
            if (TreemapRoot is not null) SetTreemapRoot(TreemapRoot);
            DuplicateStatus = $"{ByteSize.Format(freed)} em cópias foram para a Lixeira." + (errors > 0 ? $" {errors} não puderam ser removida(s)." : string.Empty) +
                              " Esvazie a Lixeira para liberar o espaço.";
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Desconta um arquivo removido dos totais da árvore, sem refazer a análise.</summary>
    private void SubtractFromTree(string filePath, long size)
    {
        var dir = _root?.Find(Path.GetDirectoryName(filePath) ?? string.Empty);
        if (dir is null) return;
        dir.FilesSize -= size;
        dir.FileCount = Math.Max(0, dir.FileCount - 1);
        for (var p = dir; p is not null; p = p.Parent) { p.Size -= size; p.TotalFiles = Math.Max(0, p.TotalFiles - 1); }
    }
}
