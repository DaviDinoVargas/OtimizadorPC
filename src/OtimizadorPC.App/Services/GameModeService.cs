using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.GameMode;
using OtimizadorPC.Core.Processes;
using OtimizadorPC.Infrastructure.Settings;
using Serilog;

namespace OtimizadorPC.App.Services;

/// <summary>
/// Liga e desliga o Modo Jogo: fecha dispensáveis, ativa o plano de energia máximo e dá prioridade alta aos jogos.
/// O estado fica salvo em disco para que tudo volte ao normal mesmo se o programa fechar com o modo ligado.
/// </summary>
public sealed partial class GameModeService : ObservableObject
{
    private static readonly string SessionFile = Path.Combine(JsonSettingsService.DataFolder, "modo-jogo.json");

    private readonly IProcessService _processes;
    private readonly IPowerService _power;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(5) };
    private GameModeSession? _session;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private long _freedMemory;

    public ObservableCollection<string> ClosedNames { get; } = [];
    public ObservableCollection<string> BoostedGames { get; } = [];

    public GameModeService(IProcessService processes, IPowerService power, ISettingsService settings)
    {
        _processes = processes;
        _power = power;
        _settings = settings;
        _watch.Tick += (_, _) => BoostRunningGames();
    }

    /// <summary>Se o programa caiu com o Modo Jogo ligado, desfaz o que ficou (plano de energia e prioridades).</summary>
    public void RecoverAfterCrash()
    {
        if (!File.Exists(SessionFile)) return;
        try
        {
            var s = JsonSerializer.Deserialize<GameModeSession>(File.ReadAllText(SessionFile));
            if (s is not null) Restore(s, reopen: false);
            Log.Information("Modo Jogo restaurado após o programa ter fechado com ele ligado");
        }
        catch (Exception ex) { Log.Warning(ex, "Falha ao restaurar sessão do Modo Jogo"); }
        finally { TryDelete(); }
    }

    public Task ToggleAsync() => IsActive ? StopAsync() : StartAsync();

    public async Task StartAsync()
    {
        if (IsActive || IsBusy) return;
        IsBusy = true;
        var options = _settings.Current;
        var session = new GameModeSession { StartedAt = DateTime.Now };
        try
        {
            await Task.Run(() =>
            {
                var samples = _processes.Sample();

                // 1) Plano de energia máximo (guarda o atual para voltar depois)
                if (options.GameModeMaxPerformancePlan)
                {
                    session.PreviousPowerPlan = _power.ListPlans().FirstOrDefault(p => p.Active).Id;
                    try { _power.EnableUltimatePerformance(); }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Desempenho Máximo indisponível; tentando Alto desempenho");
                        var high = _power.ListPlans().FirstOrDefault(p => p.Name.Contains("Alto", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("High", StringComparison.OrdinalIgnoreCase));
                        if (high.Id != Guid.Empty) _power.Activate(high.Id);
                    }
                }

                // 2) Fecha dispensáveis
                if (options.GameModeCloseDispensable)
                {
                    foreach (var p in GameModeRules.ToClose(samples, Environment.ProcessId))
                    {
                        try
                        {
                            _processes.Kill(p.Pid, entireTree: false);
                            session.Closed.Add(new ClosedProcess(p.Name, p.Path));
                            session.FreedMemoryBytes += p.MemoryBytes;
                        }
                        catch (Exception ex) { Log.Debug(ex, "Não fechou {Name}", p.Name); }
                    }
                }
            });

            _session = session;
            Save();
            StartedAt = session.StartedAt;
            FreedMemory = session.FreedMemoryBytes;
            ClosedNames.Clear();
            foreach (var n in session.Closed.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)) ClosedNames.Add(n);
            BoostedGames.Clear();
            IsActive = true;
            BoostRunningGames();
            if (options.GameModeBoostGames) _watch.Start();
            Summary = $"{ClosedNames.Count} programa(s) dispensável(is) fechado(s)" +
                      (options.GameModeMaxPerformancePlan ? ", plano de energia no máximo" : string.Empty) +
                      (options.GameModeBoostGames ? " e jogos com prioridade alta assim que abrirem." : ".");
            Log.Information("Modo Jogo ligado: {Summary}", Summary);
        }
        finally { IsBusy = false; }
    }

    public async Task StopAsync()
    {
        if (!IsActive || IsBusy || _session is null) return;
        IsBusy = true;
        try
        {
            _watch.Stop();
            var session = _session;
            var reopen = _settings.Current.GameModeReopenClosed;
            await Task.Run(() => Restore(session, reopen));
            TryDelete();
            _session = null;
            IsActive = false;
            var minutes = (int)(DateTime.Now - session.StartedAt).TotalMinutes;
            Summary = $"Modo Jogo desligado depois de {minutes} min. Plano de energia e prioridades de volta ao normal" +
                      (reopen && session.Closed.Count > 0 ? "; programas fechados reabertos." : ".");
            StartedAt = null;
            Log.Information("Modo Jogo desligado");
        }
        finally { IsBusy = false; }
    }

    private void Restore(GameModeSession s, bool reopen)
    {
        if (s.PreviousPowerPlan is { } plan && plan != Guid.Empty)
        {
            try { _power.Activate(plan); } catch (Exception ex) { Log.Warning(ex, "Não voltou o plano de energia"); }
        }
        foreach (var pid in s.Boosted)
        {
            try { _processes.SetPriority(pid, PriorityLevel.Normal); } catch (Exception) { /* jogo já fechado */ }
        }
        if (!reopen) return;
        foreach (var path in s.Closed.Select(c => c.Path).Where(p => p is not null && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized }); }
            catch (Exception ex) { Log.Debug(ex, "Não reabriu {Path}", path); }
        }
    }

    /// <summary>Jogos abertos depois de ligar o modo também recebem prioridade alta.</summary>
    private bool _scanning;

    private async void BoostRunningGames()
    {
        if (_session is null || !_settings.Current.GameModeBoostGames || _scanning) return;
        _scanning = true;
        try
        {
            var games = await Task.Run(() => _processes.Sample().Where(GameModeRules.IsGame).ToList());
            if (_session is null) return;
            foreach (var p in games)
            {
                if (_session.Boosted.Contains(p.Pid)) continue;
                try
                {
                    _processes.SetPriority(p.Pid, PriorityLevel.Alta);
                    _session.Boosted.Add(p.Pid);
                    if (!BoostedGames.Contains(p.Name)) BoostedGames.Add(p.Name);
                    Log.Information("Modo Jogo: prioridade alta para {Name}", p.Name);
                }
                catch (Exception ex) { Log.Debug(ex, "Sem prioridade para {Name}", p.Name); }
            }
            Save();
        }
        catch (Exception ex) { Log.Debug(ex, "Falha ao procurar jogos"); }
        finally { _scanning = false; }
    }

    private void Save()
    {
        if (_session is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(_session));
        }
        catch (Exception ex) { Log.Debug(ex, "Falha ao salvar sessão do Modo Jogo"); }
    }

    private static void TryDelete()
    {
        try { File.Delete(SessionFile); } catch (Exception) { }
    }
}
