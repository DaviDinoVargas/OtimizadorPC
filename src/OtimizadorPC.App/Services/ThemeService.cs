using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using ThemeMode = OtimizadorPC.Core.Settings.ThemeMode;

namespace OtimizadorPC.App.Services;

/// <summary>Aplica tema, cor de destaque, fundo da janela e densidade a partir das configurações.</summary>
public sealed class ThemeService
{
    private readonly ISettingsService _settings;
    private FluentWindow? _window;

    public ThemeService(ISettingsService settings) => _settings = settings;

    public void Initialize(FluentWindow window)
    {
        _window = window;
        Apply(_settings.Current);
        _settings.Changed += (_, s) => Application.Current.Dispatcher.Invoke(() => Apply(s));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _settings.Current.Theme == ThemeMode.Sistema)
                Application.Current.Dispatcher.Invoke(() => Apply(_settings.Current));
        };
    }

    public void Apply(AppSettings s)
    {
        var theme = ResolveTheme(s.Theme);
        var backdrop = s.Backdrop switch
        {
            BackdropMode.Mica => WindowBackdropType.Mica,
            BackdropMode.Acrilico => WindowBackdropType.Acrylic,
            BackdropMode.Abas => WindowBackdropType.Tabbed,
            _ => WindowBackdropType.None
        };

        ApplicationThemeManager.Apply(theme, backdrop, false);
        if (_window is not null) _window.WindowBackdropType = backdrop;

        if (s.UseSystemAccent)
            ApplicationAccentColorManager.ApplySystemAccent();
        else if (TryParseColor(s.AccentColor, out var color))
            ApplicationAccentColorManager.Apply(color, theme, false, false);

        ApplyDensity(s.Density);
        AnimationSettings.Apply(s);
    }

    public static ApplicationTheme ResolveTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Claro => ApplicationTheme.Light,
        ThemeMode.Escuro => ApplicationTheme.Dark,
        _ => IsSystemDark() ? ApplicationTheme.Dark : ApplicationTheme.Light
    };

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    private static void ApplyDensity(LayoutDensity density)
    {
        var r = Application.Current.Resources;
        var compact = density == LayoutDensity.Compacta;
        r["PagePadding"] = compact ? new Thickness(20, 12, 20, 20) : new Thickness(32, 20, 32, 32);
        r["CardPadding"] = compact ? new Thickness(14) : new Thickness(20);
        r["CardMargin"] = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 12);
        r["CardSpacing"] = compact ? 8d : 12d;
        r["NavItemHeight"] = compact ? 34d : 42d;
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var t = text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            color = (Color)ColorConverter.ConvertFromString(t);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
