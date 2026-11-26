using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.App.ViewModels;

public sealed record AccentSwatch(string Name, string Hex);

/// <summary>Página de personalização: tema, cores, layout e animações (aplicados na hora).</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;

    public SettingsViewModel(ISettingsService settings)
    {
        _settings = settings;
        _customAccent = settings.Current.AccentColor;
        settings.Changed += (_, _) => OnPropertyChanged(string.Empty);
    }

    public IReadOnlyList<AccentSwatch> Swatches { get; } =
    [
        new("Turquesa", "#14B8A6"),
        new("Azul", "#3B82F6"),
        new("Índigo", "#6366F1"),
        new("Violeta", "#8B5CF6"),
        new("Rosa", "#EC4899"),
        new("Vermelho", "#EF4444"),
        new("Laranja", "#F97316"),
        new("Âmbar", "#F59E0B"),
        new("Verde", "#22C55E"),
        new("Ciano", "#06B6D4"),
    ];

    private AppSettings S => _settings.Current;

    private void Set(Action<AppSettings> change, [CallerMemberName] string? name = null)
    {
        _settings.Update(change);
        OnPropertyChanged(name);
    }

    public ThemeMode Theme { get => S.Theme; set => Set(s => s.Theme = value); }
    public BackdropMode Backdrop { get => S.Backdrop; set => Set(s => s.Backdrop = value); }
    public bool UseSystemAccent { get => S.UseSystemAccent; set => Set(s => s.UseSystemAccent = value); }
    public string AccentColor { get => S.AccentColor; set => Set(s => s.AccentColor = value); }
    public SidebarPosition SidebarPosition { get => S.SidebarPosition; set => Set(s => s.SidebarPosition = value); }
    public SidebarMode SidebarMode { get => S.SidebarMode; set => Set(s => s.SidebarMode = value); }
    public LayoutDensity Density { get => S.Density; set => Set(s => s.Density = value); }
    public double UiScale { get => S.UiScale; set => Set(s => s.UiScale = Math.Round(value, 2)); }
    public bool AnimationsEnabled { get => S.AnimationsEnabled; set => Set(s => s.AnimationsEnabled = value); }
    public TransitionKind Transition { get => S.Transition; set => Set(s => s.Transition = value); }
    public double AnimationSpeed { get => S.AnimationSpeed; set => Set(s => s.AnimationSpeed = Math.Round(value, 2)); }

    [ObservableProperty]
    private string _customAccent;

    [ObservableProperty]
    private string? _customAccentError;

    [RelayCommand]
    private void SelectAccent(string hex)
    {
        _settings.Update(s => { s.AccentColor = hex; s.UseSystemAccent = false; });
        CustomAccent = hex;
        CustomAccentError = null;
    }

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        if (ThemeService.TryParseColor(CustomAccent, out var c))
        {
            SelectAccent($"#{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        else
        {
            CustomAccentError = "Cor inválida. Use o formato #RRGGBB, por exemplo #14B8A6.";
        }
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _settings.Reset();
        CustomAccent = _settings.Current.AccentColor;
        CustomAccentError = null;
    }
}
