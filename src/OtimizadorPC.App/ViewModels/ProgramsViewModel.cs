using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.Programs;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public enum ProgramFilter { Todos, Programas, Loja, Grandes, Recentes, Orfaos }

public enum ProgramSort { Tamanho, Nome, Data, Editor }

/// <summary>Linha da lista de programas.</summary>
public sealed partial class ProgramRow(InstalledProgram program) : ObservableObject
{
    public InstalledProgram Program { get; } = program;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isRemoved;
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private long _sizeBytes = program.SizeBytes;

    public string Name => Program.Name;
    public bool IsStore => Program.Source == ProgramSource.Loja;
    public bool IsBroken => Program.IsBroken;
    public string SizeText => SizeBytes > 0 ? ByteSize.Format(SizeBytes) : "—";
    public string DateText => Program.InstallDate?.ToString("dd/MM/yyyy") ?? "—";
    public bool CanOpenFolder => Program.InstallLocation is { } d && Directory.Exists(d);

    public string Details => string.Join("  ·  ", new[]
    {
        Program.Publisher,
        string.IsNullOrWhiteSpace(Program.Version) ? null : "versão " + Program.Version,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private ImageSource? _icon;
    private bool _iconLoaded;

    /// <summary>Ícone carregado só quando a linha aparece na tela.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_iconLoaded) return _icon;
            _iconLoaded = true;
            try { _icon = IsStore ? LoadStoreLogo(Program.DisplayIcon) : LoadDisplayIcon(Program.DisplayIcon); }
            catch (Exception) { _icon = null; }
            return _icon;
        }
    }

    private static ImageSource? LoadDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var path = displayIcon.Trim().Trim('"');
        var comma = path.LastIndexOf(',');
        if (comma > 2 && !File.Exists(path)) path = path[..comma].Trim().Trim('"');
        path = Environment.ExpandEnvironmentVariables(path);
        return File.Exists(path) ? IconCache.Get(path, large: true) : null;
    }

    /// <summary>Logo dos apps da Loja: o arquivo real costuma ter qualificador de escala (Logo.scale-100.png).</summary>
    private static ImageSource? LoadStoreLogo(string? logo)
    {
        if (string.IsNullOrWhiteSpace(logo)) return null;
        var file = logo;
        if (!File.Exists(file))
        {
            var dir = Path.GetDirectoryName(logo);
            if (dir is null || !Directory.Exists(dir)) return null;
            file = Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(logo) + ".*" + Path.GetExtension(logo))
                .OrderBy(f => f.Contains("scale-100", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
            if (file is null) return null;
        }
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(file);
        bmp.DecodePixelWidth = 40;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>Sobra encontrada depois de desinstalar.</summary>
public sealed partial class LeftoverRow(Leftover leftover) : ObservableObject
{
    public Leftover Leftover { get; } = leftover;
    [ObservableProperty] private bool _isChecked = leftover.Recommended;
    public bool IsFolder => Leftover.Kind == LeftoverKind.Pasta;
    public string SizeText => Leftover.SizeBytes > 0 ? ByteSize.Format(Leftover.SizeBytes) : IsFolder ? "vazia" : "registro";
}

/// <summary>
/// Programas instalados: busca, filtros, ordenação, desinstalação em lote (um desinstalador por vez)
/// e limpeza das sobras que cada programa deixou.
/// </summary>
public sealed partial class ProgramsViewModel : PageViewModel
{
    private readonly IInstalledProgramsService _programs;
    private readonly IDialogService _dialogs;
    private readonly IRestorePointService _restore;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ProgramRow> Rows { get; } = [];
    public ObservableCollection<LeftoverRow> Leftovers { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _search = string.Empty;
    [ObservableProperty] private ProgramFilter _filter = ProgramFilter.Todos;
    [ObservableProperty] private ProgramSort _sort = ProgramSort.Tamanho;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private long _maxSize = 1;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private long _checkedSize;
    [ObservableProperty] private int _brokenCount;
    [ObservableProperty] private bool _quiet = true;
    [ObservableProperty] private bool _createRestorePoint = true;
    [ObservableProperty] private bool _isBatchRunning;
    [ObservableProperty] private string? _batchText;
    [ObservableProperty] private double _batchProgress;
    [ObservableProperty] private bool _hasLeftovers;
    [ObservableProperty] private long _leftoversSize;

    public bool ShowActionBar => CheckedCount > 0 || IsBatchRunning;
    partial void OnCheckedCountChanged(int value) => OnPropertyChanged(nameof(ShowActionBar));
    partial void OnIsBatchRunningChanged(bool value) => OnPropertyChanged(nameof(ShowActionBar));

    public ProgramsViewModel(IInstalledProgramsService programs, IDialogService dialogs, IRestorePointService restore)
    {
        _programs = programs;
        _dialogs = dialogs;
        _restore = restore;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is ProgramRow r && Matches(r);
        ApplySort();
    }

    public override async Task OnNavigatedToAsync()
    {
        if (Rows.Count == 0 && !IsLoading) await RefreshAsync();
    }

    // =====================================================================
    // Lista
    // =====================================================================

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var list = await _programs.ListAsync();
            foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
            Rows.Clear();
            foreach (var p in list)
            {
                var row = new ProgramRow(p);
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
            }
            UpdateTotals();
            View.Refresh();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Falha ao listar programas");
            StatusMessage = "Não foi possível ler a lista de programas: " + ex.Message;
        }
        finally { IsLoading = false; }

        _ = MeasureMissingAsync();
    }

    /// <summary>Mede a pasta dos programas que não informam tamanho (e dos apps da Loja), 4 por vez.</summary>
    private async Task MeasureMissingAsync()
    {
        var measurable = ProgramRules.MeasurableIds(Rows.Select(r => r.Program));
        var pending = Rows.Where(r => r.SizeBytes == 0 && measurable.Contains(r.Program.Id)).ToList();
        if (pending.Count == 0) return;
        IsMeasuring = true;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(pending.Select(async r =>
        {
            await gate.WaitAsync();
            try { r.SizeBytes = await _programs.MeasureAsync(r.Program); }
            catch (Exception) { }
            finally { gate.Release(); }
        }));
        IsMeasuring = false;
        UpdateTotals();
        View.Refresh();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramRow.IsChecked)) UpdateChecked();
    }

    private void UpdateTotals()
    {
        var live = Rows.Where(r => !r.IsRemoved).ToList();
        TotalCount = live.Count;
        TotalSize = live.Sum(r => r.SizeBytes);
        MaxSize = Math.Max(1, live.Select(r => r.SizeBytes).DefaultIfEmpty(0).Max());
        BrokenCount = live.Count(r => r.IsBroken);
        UpdateChecked();
    }

    private void UpdateChecked()
    {
        var c = Rows.Where(r => r.IsChecked && !r.IsRemoved).ToList();
        CheckedCount = c.Count;
        CheckedSize = c.Sum(r => r.SizeBytes);
    }

    private bool Matches(ProgramRow r)
    {
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            if (!r.Name.Contains(s, StringComparison.CurrentCultureIgnoreCase)
                && !(r.Program.Publisher?.Contains(s, StringComparison.CurrentCultureIgnoreCase) ?? false))
                return false;
        }
        return Filter switch
        {
            ProgramFilter.Programas => !r.IsStore,
            ProgramFilter.Loja => r.IsStore,
            ProgramFilter.Grandes => r.SizeBytes >= 1024L * 1024 * 1024,
            ProgramFilter.Recentes => r.Program.InstallDate is { } d && d >= DateTime.Today.AddDays(-30),
            ProgramFilter.Orfaos => r.IsBroken,
            _ => true,
        };
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnFilterChanged(ProgramFilter value) => View.Refresh();
    partial void OnSortChanged(ProgramSort value) => ApplySort();

    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            switch (Sort)
            {
                case ProgramSort.Nome: View.SortDescriptions.Add(new(nameof(ProgramRow.Name), ListSortDirection.Ascending)); break;
                case ProgramSort.Data: View.SortDescriptions.Add(new("Program.InstallDate", ListSortDirection.Descending)); break;
                case ProgramSort.Editor:
                    View.SortDescriptions.Add(new("Program.Publisher", ListSortDirection.Ascending));
                    View.SortDescriptions.Add(new(nameof(ProgramRow.Name), ListSortDirection.Ascending));
                    break;
                default: View.SortDescriptions.Add(new(nameof(ProgramRow.SizeBytes), ListSortDirection.Descending)); break;
            }
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Rows) r.IsChecked = false;
    }

    [RelayCommand]
    private static void OpenFolder(ProgramRow? row)
    {
        if (row?.Program.InstallLocation is { } d && Directory.Exists(d))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void SearchWeb(ProgramRow? row)
    {
        if (row is null) return;
        var q = Uri.EscapeDataString($"{row.Name} {row.Program.Publisher}".Trim());
        Process.Start(new ProcessStartInfo($"https://duckduckgo.com/?q={q}") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RemoveEntryAsync(ProgramRow? row)
    {
        if (row is null) return;
        if (!await _dialogs.ConfirmAsync("Remover da lista",
                $"O desinstalador de \"{row.Name}\" não existe mais, então ele não pode ser desinstalado normalmente.\n\n" +
                "Remover só a entrada da lista? Uma cópia da entrada é salva em Backups\\Registro para restaurar se precisar.", "Remover entrada"))
            return;
        try
        {
            await _programs.RemoveEntryAsync(row.Program);
            row.IsRemoved = true;
            Rows.Remove(row);
            UpdateTotals();
            StatusMessage = $"Entrada de \"{row.Name}\" removida da lista.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao remover entrada {Name}", row.Name);
            StatusMessage = "Não foi possível remover a entrada: " + ex.Message;
        }
    }

    // =====================================================================
    // Desinstalação em lote
    // =====================================================================

    [RelayCommand]
    private Task UninstallOneAsync(ProgramRow? row)
    {
        if (row is null) return Task.CompletedTask;
        return RunBatchAsync([row]);
    }

    [RelayCommand]
    private Task UninstallCheckedAsync() => RunBatchAsync(Rows.Where(r => r.IsChecked && !r.IsRemoved).ToList());

    [RelayCommand]
    private void CancelBatch() => _cts?.Cancel();

    private async Task RunBatchAsync(IReadOnlyList<ProgramRow> rows)
    {
        if (rows.Count == 0 || IsBatchRunning) return;
        var size = rows.Sum(r => r.SizeBytes);
        var names = string.Join("\n", rows.Take(8).Select(r => "•  " + r.Name)) + (rows.Count > 8 ? $"\n…e mais {rows.Count - 8}" : string.Empty);
        var how = Quiet
            ? "Quando o programa permitir, a desinstalação é silenciosa; os outros abrem o próprio desinstalador, um de cada vez."
            : "O desinstalador de cada programa vai abrir, um de cada vez.";
        var title = rows.Count == 1 ? $"Desinstalar {rows[0].Name}?" : $"Desinstalar {rows.Count} programas?";
        if (!await _dialogs.ConfirmAsync(title, $"{names}\n\n{(size > 0 ? $"Libera cerca de {ByteSize.Format(size)}. " : string.Empty)}{how}", "Desinstalar"))
            return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBatchRunning = true;
        StatusMessage = null;
        var removed = new List<ProgramRow>();
        var failed = new List<string>();
        var reboot = false;
        try
        {
            if (CreateRestorePoint)
            {
                BatchText = "Criando ponto de restauração…";
                BatchProgress = 0;
                try
                {
                    if (!await _restore.CreateAsync("OtimizadorPC: antes de desinstalar programas", ct))
                        Log.Information("O Windows não criou o ponto de restauração (já existe um recente)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warning(ex, "Ponto de restauração falhou"); }
            }

            for (var i = 0; i < rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows[i];
                BatchText = $"Desinstalando {i + 1} de {rows.Count}: {row.Name}";
                BatchProgress = (double)i / rows.Count;
                row.IsWorking = true;
                row.Status = "Desinstalando…";
                try
                {
                    var r = await _programs.UninstallAsync(row.Program, Quiet, ct);
                    row.Status = r.Message;
                    reboot |= r.RebootRequired;
                    if (r.Success) { row.IsRemoved = true; row.IsChecked = false; removed.Add(row); }
                    else failed.Add(row.Name);
                }
                catch (OperationCanceledException) { row.Status = "Cancelado."; throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Falha ao desinstalar {Name}", row.Name);
                    row.Status = "Erro: " + ex.Message;
                    failed.Add(row.Name);
                }
                finally { row.IsWorking = false; }
            }
        }
        catch (OperationCanceledException) { failed.Add("(cancelado pelo usuário)"); }
        finally
        {
            BatchProgress = 1;
        }

        // Procura sobras do que saiu, sem considerar os programas que continuam instalados
        if (removed.Count > 0)
        {
            BatchText = "Procurando sobras…";
            var still = Rows.Where(r => !r.IsRemoved).Select(r => r.Program).ToList();
            foreach (var row in removed)
            {
                try
                {
                    foreach (var l in await _programs.FindLeftoversAsync(row.Program, still))
                        Leftovers.Add(new LeftoverRow(l));
                }
                catch (Exception ex) { Log.Warning(ex, "Falha ao procurar sobras de {Name}", row.Name); }
            }
            foreach (var row in removed) { row.PropertyChanged -= OnRowChanged; Rows.Remove(row); }
            UpdateLeftovers();
        }

        UpdateTotals();
        IsBatchRunning = false;
        BatchText = null;

        var parts = new List<string>();
        if (removed.Count > 0) parts.Add(removed.Count == 1 ? $"\"{removed[0].Name}\" desinstalado" : $"{removed.Count} programas desinstalados");
        if (failed.Count > 0) parts.Add($"{failed.Count} não concluído(s): {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "…" : "")}");
        if (Leftovers.Count > 0) parts.Add($"{Leftovers.Count} sobra(s) encontrada(s) — revise ao lado");
        if (reboot) parts.Add("reinicie o computador para concluir");
        StatusMessage = string.Join(". ", parts) + ".";
    }

    // =====================================================================
    // Sobras
    // =====================================================================

    private void UpdateLeftovers()
    {
        HasLeftovers = Leftovers.Count > 0;
        LeftoversSize = Leftovers.Sum(l => l.Leftover.SizeBytes);
    }

    [RelayCommand]
    private async Task RemoveLeftoversAsync()
    {
        var chosen = Leftovers.Where(l => l.IsChecked).ToList();
        if (chosen.Count == 0) return;
        IsBatchRunning = true;
        BatchText = "Removendo sobras…";
        try
        {
            await _programs.RemoveLeftoversAsync(chosen.Select(c => c.Leftover));
            foreach (var c in chosen) Leftovers.Remove(c);
            StatusMessage = $"{chosen.Count} sobra(s) removida(s): pastas na Lixeira e chaves do registro com cópia em Backups\\Registro.";
        }
        finally
        {
            IsBatchRunning = false;
            BatchText = null;
            UpdateLeftovers();
        }
    }

    [RelayCommand]
    private void DismissLeftovers()
    {
        Leftovers.Clear();
        UpdateLeftovers();
    }

    [RelayCommand]
    private static void OpenLeftover(LeftoverRow? row)
    {
        if (row is null) return;
        if (row.IsFolder && Directory.Exists(row.Leftover.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Leftover.Path}\"") { UseShellExecute = true });
        else if (!row.IsFolder)
            Clipboard.SetText(row.Leftover.Path);
    }
}
