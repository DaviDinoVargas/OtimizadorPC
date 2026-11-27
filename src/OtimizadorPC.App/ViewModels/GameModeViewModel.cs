using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Formatting;
using OtimizadorPC.Core.GameMode;
using OtimizadorPC.Infrastructure.Hardware;
using Serilog;

namespace OtimizadorPC.App.ViewModels;

public sealed record DayOption(DayOfWeek Day, string Label)
{
    public override string ToString() => Label;
}

public sealed record HourOption(int Hour)
{
    public override string ToString() => $"{Hour:00}:00";
}

/// <summary>Modo Jogo em um clique, bandeja do sistema e limpeza automática agendada.</summary>
public sealed partial class GameModeViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IAutoCleanupScheduler _scheduler;
    private readonly ICleanupService _cleanup;

    public GameModeService GameMode { get; }

    public IReadOnlyList<DayOption> Days { get; } =
    [
        new(DayOfWeek.Sunday, "Domingo"), new(DayOfWeek.Monday, "Segunda"), new(DayOfWeek.Tuesday, "Terça"), new(DayOfWeek.Wednesday, "Quarta"),
        new(DayOfWeek.Thursday, "Quinta"), new(DayOfWeek.Friday, "Sexta"), new(DayOfWeek.Saturday, "Sábado"),
    ];

    public IReadOnlyList<HourOption> Hours { get; } = Enumerable.Range(0, 24).Select(h => new HourOption(h)).ToList();

    public bool IsAdmin { get; } = WmiHardwareInfoService.IsAdministrator();

    [ObservableProperty] private string? _scheduleMessage;
    [ObservableProperty] private string _nextRunText = "—";
    [ObservableProperty] private string _lastRunText = "Nunca";
    [ObservableProperty] private bool _isCleaning;

    public GameModeViewModel(GameModeService gameMode, ISettingsService settings, IAutoCleanupScheduler scheduler, ICleanupService cleanup)
    {
        GameMode = gameMode;
        _settings = settings;
        _scheduler = scheduler;
        _cleanup = cleanup;
    }

    public override Task OnNavigatedToAsync()
    {
        RefreshSchedule();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- opções do Modo Jogo
    public bool CloseDispensable
    {
        get => _settings.Current.GameModeCloseDispensable;
        set { _settings.Update(s => s.GameModeCloseDispensable = value); OnPropertyChanged(); }
    }

    public bool MaxPerformancePlan
    {
        get => _settings.Current.GameModeMaxPerformancePlan;
        set { _settings.Update(s => s.GameModeMaxPerformancePlan = value); OnPropertyChanged(); }
    }

    public bool BoostGames
    {
        get => _settings.Current.GameModeBoostGames;
        set { _settings.Update(s => s.GameModeBoostGames = value); OnPropertyChanged(); }
    }

    public bool ReopenClosed
    {
        get => _settings.Current.GameModeReopenClosed;
        set { _settings.Update(s => s.GameModeReopenClosed = value); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.Current.MinimizeToTray;
        set { _settings.Update(s => s.MinimizeToTray = value); OnPropertyChanged(); }
    }

    [RelayCommand]
    private Task ToggleGameModeAsync() => GameMode.ToggleAsync();

    // ---------------------------------------------------------------- limpeza automática
    public bool AutoCleanupEnabled
    {
        get => _settings.Current.AutoCleanupEnabled;
        set { _settings.Update(s => s.AutoCleanupEnabled = value); OnPropertyChanged(); ApplySchedule(); }
    }

    public CleanupFrequency Frequency
    {
        get => _settings.Current.AutoCleanupFrequency;
        set { _settings.Update(s => s.AutoCleanupFrequency = value); OnPropertyChanged(); OnPropertyChanged(nameof(IsWeekly)); ApplySchedule(); }
    }

    public bool IsWeekly => Frequency == CleanupFrequency.Semanal;

    public DayOption? Day
    {
        get => Days.FirstOrDefault(d => d.Day == _settings.Current.AutoCleanupDay);
        set { if (value is null) return; _settings.Update(s => s.AutoCleanupDay = value.Day); OnPropertyChanged(); ApplySchedule(); }
    }

    public HourOption? Hour
    {
        get => Hours.FirstOrDefault(h => h.Hour == _settings.Current.AutoCleanupHour);
        set { if (value is null) return; _settings.Update(s => s.AutoCleanupHour = value.Hour); OnPropertyChanged(); ApplySchedule(); }
    }

    public string AutoCleanupScope => string.Join(", ", GameModeRules.AutomaticCleanup(_cleanup.Categories).Select(c => c.Name));

    private void ApplySchedule()
    {
        var s = _settings.Current;
        try
        {
            if (s.AutoCleanupEnabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do programa desconhecido.");
                _scheduler.Schedule(s.AutoCleanupFrequency, s.AutoCleanupDay, s.AutoCleanupHour, exe);
                ScheduleMessage = null;
            }
            else
            {
                _scheduler.Remove();
                ScheduleMessage = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Falha ao agendar a limpeza automática");
            ScheduleMessage = IsAdmin
                ? "O Windows não aceitou o agendamento: " + ex.Message
                : "Para agendar é preciso abrir o OtimizadorPC como administrador.";
            if (s.AutoCleanupEnabled)
            {
                _settings.Update(x => x.AutoCleanupEnabled = false);
                OnPropertyChanged(nameof(AutoCleanupEnabled));
            }
        }
        RefreshSchedule();
    }

    private void RefreshSchedule()
    {
        try
        {
            NextRunText = _scheduler.NextRun() is { } next ? next.ToString("dddd, dd/MM 'às' HH:mm") : "não agendada";
        }
        catch (Exception) { NextRunText = "—"; }
        var s = _settings.Current;
        LastRunText = s.LastAutoCleanup is { } last ? $"{last:dd/MM/yyyy HH:mm} · {ByteSize.Format(s.LastAutoCleanupBytes)} liberados" : "Nunca";
    }

    [RelayCommand]
    private async Task RunCleanupNowAsync()
    {
        IsCleaning = true;
        try
        {
            var freed = await SilentCleanup.RunAsync(_cleanup, _settings);
            ScheduleMessage = $"Limpeza concluída: {ByteSize.Format(freed)} liberados.";
        }
        catch (Exception ex) { ScheduleMessage = ex.Message; }
        finally
        {
            IsCleaning = false;
            RefreshSchedule();
        }
    }
}
