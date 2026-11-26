using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Cleanup;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Infrastructure.Hardware;
using Serilog;
using Wpf.Ui.Controls;

namespace OtimizadorPC.App.ViewModels;

public enum CleanupState { Pendente, Analisando, Pronto, Limpando, Concluido, Erro }

public sealed partial class CleanupEntryViewModel : ObservableObject
{
    public required CleanupEntry Entry { get; init; }
    public string SizeText => ByteSize.Format(Entry.SizeBytes);
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class CleanupItemViewModel : ObservableObject
{
    public CleanupItemViewModel(CleanupCategory category, bool isAdmin)
    {
        Category = category;
        CanRun = !category.RequiresAdmin || isAdmin;
        _isSelected = category.SelectedByDefault && CanRun;
    }

    public CleanupCategory Category { get; }
    public bool CanRun { get; }
    public string RiskText => Category.Risk switch { CleanupRisk.Seguro => "Seguro", CleanupRisk.Moderado => "Moderado", _ => "Atenção" };
    public string AdminHint => CanRun ? string.Empty : "Requer administrador";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private CleanupState _state = CleanupState.Pendente;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _message;

    public ObservableCollection<CleanupEntryViewModel> Entries { get; } = [];

    /// <summary>Quanto esta categoria liberaria considerando a seleção atual.</summary>
    public long SelectedBytes => !IsSelected ? 0
        : Category.HasEntries ? Entries.Where(e => e.IsSelected).Sum(e => e.Entry.SizeBytes)
        : SizeBytes;

    public string SizeText => Category.SizeKnownOnlyAfterCleaning ? "medido ao limpar"
        : State is CleanupState.Pendente or CleanupState.Analisando ? "…"
        : ByteSize.Format(SizeBytes);

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(SizeText));
    partial void OnStateChanged(CleanupState value) => OnPropertyChanged(nameof(SizeText));
}

public sealed class CleanupGroupViewModel
{
    public required string Title { get; init; }
    public required SymbolRegular Icon { get; init; }
    public required IReadOnlyList<CleanupItemViewModel> Items { get; init; }
}

/// <summary>Página de limpeza: analisa categorias em paralelo e apaga só o que o usuário escolher.</summary>
public sealed partial class CleanupViewModel : PageViewModel
{
    private readonly ICleanupService _cleanup;
    private readonly IDialogService _dialogs;
    private readonly SnapshotProvider _snapshots;
    private CancellationTokenSource? _cts;
    private bool _scanned;

    public CleanupViewModel(ICleanupService cleanup, IDialogService dialogs, SnapshotProvider snapshots)
    {
        _cleanup = cleanup;
        _dialogs = dialogs;
        _snapshots = snapshots;
        IsAdministrator = WmiHardwareInfoService.IsAdministrator();

        var items = cleanup.Categories.Select(c => new CleanupItemViewModel(c, IsAdministrator)).ToList();
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemChanged;
            item.Entries.CollectionChanged += (_, _) => UpdateTotal();
        }
        All = items;
        Groups =
        [
            new() { Title = "Sistema", Icon = SymbolRegular.Window24, Items = items.Where(i => i.Category.Group == CleanupGroup.Sistema).ToList() },
            new() { Title = "Navegadores", Icon = SymbolRegular.Earth24, Items = items.Where(i => i.Category.Group == CleanupGroup.Navegadores).ToList() },
            new() { Title = "Jogos e aplicativos", Icon = SymbolRegular.Games24, Items = items.Where(i => i.Category.Group == CleanupGroup.JogosEApps).ToList() },
            new() { Title = "Desenvolvimento", Icon = SymbolRegular.Wrench24, Items = items.Where(i => i.Category.Group == CleanupGroup.Desenvolvimento).ToList() },
        ];
        Groups = Groups.Where(g => g.Items.Count > 0).ToList();
    }

    public IReadOnlyList<CleanupItemViewModel> All { get; }
    public IReadOnlyList<CleanupGroupViewModel> Groups { get; }

    public bool IsAdministrator { get; }
    public bool ShowAdminHint => !IsAdministrator;

    [ObservableProperty] private long _totalSelectedBytes;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _phaseText = "Analisando o computador…";
    [ObservableProperty] private bool _showResult;
    [ObservableProperty] private long _lastFreedBytes;
    [ObservableProperty] private string _resultText = string.Empty;

    public bool IsIdle => !IsScanning && !IsCleaning;
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnIsCleaningChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    public override async Task OnNavigatedToAsync()
    {
        if (!_scanned) await ScanAsync();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanupItemViewModel.IsSelected) or nameof(CleanupItemViewModel.SizeBytes))
            UpdateTotal();
    }

    private void UpdateTotal()
    {
        TotalSelectedBytes = All.Sum(i => i.SelectedBytes);
        SelectedCount = All.Count(i => i.IsSelected && i.CanRun);
    }

    // ---------------- Análise ----------------

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning || IsCleaning) return;
        _cts = new CancellationTokenSource();
        IsScanning = true;
        ShowResult = false;
        PhaseText = "Analisando o computador…";
        var done = 0;
        var runnable = All.ToList();
        using var gate = new SemaphoreSlim(4);

        foreach (var i in runnable) { i.State = CleanupState.Analisando; i.Message = null; }

        var tasks = runnable.Select(async item =>
        {
            await gate.WaitAsync(_cts.Token);
            try
            {
                var scan = await _cleanup.ScanAsync(item.Category.Id, _cts.Token);
                item.SizeBytes = scan.SizeBytes;
                item.FileCount = scan.FileCount;
                item.Message = scan.Note;
                item.Entries.Clear();
                foreach (var e in scan.Entries)
                {
                    var evm = new CleanupEntryViewModel { Entry = e };
                    evm.PropertyChanged += (_, _) => UpdateTotal();
                    item.Entries.Add(evm);
                }
                item.State = CleanupState.Pronto;
            }
            catch (OperationCanceledException)
            {
                item.State = CleanupState.Pendente;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Falha ao analisar {Categoria}", item.Category.Id);
                item.State = CleanupState.Erro;
                item.Message = "Não foi possível analisar.";
            }
            finally
            {
                gate.Release();
                OverallProgress = ++done * 100.0 / runnable.Count;
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        finally
        {
            IsScanning = false;
            _scanned = true;
            UpdateTotal();
        }
    }

    // ---------------- Limpeza ----------------

    [RelayCommand]
    private async Task CleanAsync()
    {
        var targets = All.Where(i => i.IsSelected && i.CanRun && (!i.Category.HasEntries || i.Entries.Any(e => e.IsSelected))).ToList();
        if (targets.Count == 0)
        {
            await _dialogs.InfoAsync("Nada selecionado", "Marque pelo menos uma categoria para limpar.");
            return;
        }

        var risky = targets.Where(t => t.Category.Risk == CleanupRisk.Atencao).Select(t => "• " + t.Category.Name).ToList();
        var message = $"Serão limpas {targets.Count} categorias, liberando cerca de {ByteSize.Format(TotalSelectedBytes)}." +
                      "\n\nArquivos em uso por programas abertos serão ignorados." +
                      (risky.Count > 0 ? "\n\nAtenção, estes itens não podem ser recuperados:\n" + string.Join("\n", risky) : string.Empty);
        if (!await _dialogs.ConfirmAsync("Confirmar limpeza", message, "Limpar agora")) return;

        _cts = new CancellationTokenSource();
        IsCleaning = true;
        ShowResult = false;
        long freed = 0;
        int files = 0, skipped = 0, errors = 0;

        for (var n = 0; n < targets.Count; n++)
        {
            var item = targets[n];
            PhaseText = $"Limpando: {item.Category.Name}";
            item.State = CleanupState.Limpando;
            item.Progress = 0;
            var baseProgress = n * 100.0 / targets.Count;
            var progress = new Progress<double>(p =>
            {
                item.Progress = p * 100;
                OverallProgress = baseProgress + p * 100.0 / targets.Count;
            });

            try
            {
                var selected = item.Category.HasEntries ? item.Entries.Where(e => e.IsSelected).Select(e => e.Entry.Path).ToList() : null;
                var result = await _cleanup.CleanAsync(item.Category.Id, selected, progress, _cts.Token);
                freed += result.FreedBytes;
                files += result.DeletedFiles;
                skipped += result.SkippedFiles;
                if (result.Success)
                {
                    item.State = CleanupState.Concluido;
                    item.Message = result.SkippedFiles > 0
                        ? $"{ByteSize.Format(result.FreedBytes)} liberados · {result.SkippedFiles} arquivos em uso mantidos"
                        : $"{ByteSize.Format(result.FreedBytes)} liberados";
                }
                else
                {
                    errors++;
                    item.State = CleanupState.Erro;
                    item.Message = result.Error;
                }
                Log.Information("Limpeza {Categoria}: {Bytes} bytes, {Arquivos} arquivos, {Ignorados} ignorados, erro={Erro}",
                    item.Category.Id, result.FreedBytes, result.DeletedFiles, result.SkippedFiles, result.Error);
            }
            catch (OperationCanceledException)
            {
                item.State = CleanupState.Pronto;
                break;
            }
            catch (Exception ex)
            {
                errors++;
                item.State = CleanupState.Erro;
                item.Message = ex.Message;
                Log.Error(ex, "Falha ao limpar {Categoria}", item.Category.Id);
            }
        }

        IsCleaning = false;
        OverallProgress = 100;
        LastFreedBytes = freed;
        ResultText = $"{files:N0} arquivos removidos" +
                     (skipped > 0 ? $" · {skipped:N0} em uso foram mantidos" : string.Empty) +
                     (errors > 0 ? $" · {errors} categoria(s) com erro" : string.Empty);
        ShowResult = true;

        // Atualiza os tamanhos e o painel de discos
        _ = _snapshots.GetAsync(forceRefresh: true);
        _scanned = false;
        await RescanQuietlyAsync(targets);
    }

    private async Task RescanQuietlyAsync(IEnumerable<CleanupItemViewModel> items)
    {
        foreach (var item in items)
        {
            try
            {
                var scan = await _cleanup.ScanAsync(item.Category.Id);
                item.SizeBytes = scan.SizeBytes;
                item.FileCount = scan.FileCount;
                if (item.Category.HasEntries)
                {
                    var remaining = scan.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var gone in item.Entries.Where(e => !remaining.Contains(e.Entry.Path)).ToList()) item.Entries.Remove(gone);
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Reanálise falhou"); }
        }
        _scanned = true;
        UpdateTotal();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var i in All) i.IsSelected = i.CanRun && i.Category.SelectedByDefault;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in All.Where(i => i.CanRun && i.Category.Risk != CleanupRisk.Atencao)) i.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in All) i.IsSelected = false;
    }

    [RelayCommand]
    private static void RestartAsAdmin() => Elevation.RestartAsAdministrator();
}
