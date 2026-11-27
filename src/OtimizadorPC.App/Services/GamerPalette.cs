using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace OtimizadorPC.App.Services;

/// <summary>
/// Paleta "gamer": no tema escuro troca os tons neutros do Fluent por azul-marinho profundo e
/// publica degradês da cor de destaque (destaque → segunda cor) usados em cabeçalhos, seleção,
/// barras de progresso e brilhos. No tema claro mantém o Fluent e só publica os degradês.
/// </summary>
public static class GamerPalette
{
    private static readonly Dictionary<string, Color> Dark = new()
    {
        ["ApplicationBackgroundBrush"] = Rgb(0x0A, 0x0E, 0x1A),
        ["SolidBackgroundFillColorBaseBrush"] = Rgb(0x0A, 0x0E, 0x1A),
        ["LayerFillColorDefaultBrush"] = Argb(0xF2, 0x0D, 0x12, 0x22),
        ["CardBackgroundFillColorDefaultBrush"] = Argb(0xF0, 0x13, 0x1A, 0x2D),
        ["CardBackgroundFillColorSecondaryBrush"] = Argb(0xF0, 0x16, 0x1E, 0x34),
        ["CardStrokeColorDefaultBrush"] = Rgb(0x22, 0x2D, 0x48),
        ["ControlFillColorDefaultBrush"] = Rgb(0x19, 0x22, 0x3B),
        ["ControlFillColorSecondaryBrush"] = Rgb(0x20, 0x2B, 0x49),
        ["ControlFillColorTertiaryBrush"] = Rgb(0x15, 0x1D, 0x33),
        ["ControlStrokeColorDefaultBrush"] = Rgb(0x2A, 0x36, 0x55),
        ["SubtleFillColorSecondaryBrush"] = Argb(0x80, 0x24, 0x31, 0x55),
        ["SubtleFillColorTertiaryBrush"] = Argb(0xA0, 0x24, 0x31, 0x55),
        ["DividerStrokeColorDefaultBrush"] = Rgb(0x22, 0x2D, 0x48),
        ["TextFillColorSecondaryBrush"] = Rgb(0xA9, 0xB4, 0xD0),
        ["TextFillColorTertiaryBrush"] = Rgb(0x6E, 0x7B, 0x9C),
    };

    public static void Apply(ApplicationTheme theme)
    {
        var r = Application.Current.Resources;
        var accent = r["SystemAccentColorPrimary"] as Color? ?? r["SystemAccentColor"] as Color? ?? Rgb(0x14, 0xB8, 0xA6);
        var second = SecondColor(accent);

        r["AccentStartColor"] = accent;
        r["AccentEndColor"] = second;
        r["AccentGradientBrush"] = Freeze(new LinearGradientBrush(accent, second, new Point(0, 0), new Point(1, 0)));
        r["AccentGradientVerticalBrush"] = Freeze(new LinearGradientBrush(accent, second, new Point(0, 0), new Point(0, 1)));
        r["AccentGradientSoftBrush"] = Freeze(new LinearGradientBrush(WithAlpha(accent, 0x3A), WithAlpha(second, 0x1A), new Point(0, 0), new Point(1, 1)));
        r["AccentGlowBrush"] = Freeze(new SolidColorBrush(WithAlpha(accent, 0x55)));
        r["HoverGlowEffect"] = Freeze(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = theme == ApplicationTheme.Dark ? 0.45 : 0.25, Color = accent });
        r["AccentGlowEffect"] = Freeze(new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 16, Opacity = 0.55, Color = accent });

        var hero = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        hero.GradientStops.Add(new GradientStop(WithAlpha(accent, theme == ApplicationTheme.Dark ? (byte)0x4A : (byte)0x30), 0));
        hero.GradientStops.Add(new GradientStop(WithAlpha(second, theme == ApplicationTheme.Dark ? (byte)0x30 : (byte)0x1C), 0.55));
        hero.GradientStops.Add(new GradientStop(WithAlpha(second, 0x00), 1));
        r["HeroBrush"] = Freeze(hero);

        var glow = new RadialGradientBrush(WithAlpha(accent, theme == ApplicationTheme.Dark ? (byte)0x30 : (byte)0x18), WithAlpha(accent, 0))
        {
            Center = new Point(0.85, 0.05), GradientOrigin = new Point(0.85, 0.05), RadiusX = 0.6, RadiusY = 0.7,
        };
        r["BackgroundGlowBrush"] = Freeze(glow);
        var glow2 = new RadialGradientBrush(WithAlpha(second, theme == ApplicationTheme.Dark ? (byte)0x22 : (byte)0x10), WithAlpha(second, 0))
        {
            Center = new Point(0.1, 0.95), GradientOrigin = new Point(0.1, 0.95), RadiusX = 0.5, RadiusY = 0.6,
        };
        r["BackgroundGlow2Brush"] = Freeze(glow2);

        // Botões principais (Appearance="Primary") ganham o degradê
        r["AccentButtonBackground"] = Freeze(new LinearGradientBrush(accent, second, new Point(0, 0), new Point(1, 1)));
        r["AccentButtonBackgroundPointerOver"] = Freeze(new LinearGradientBrush(Lighten(accent, 0.12), Lighten(second, 0.12), new Point(0, 0), new Point(1, 1)));
        r["AccentButtonBackgroundPressed"] = Freeze(new LinearGradientBrush(Darken(accent, 0.12), Darken(second, 0.12), new Point(0, 0), new Point(1, 1)));
        r["AccentButtonForeground"] = Freeze(new SolidColorBrush(Colors.White));
        r["AccentButtonForegroundPointerOver"] = Freeze(new SolidColorBrush(Colors.White));
        r["AccentButtonForegroundPressed"] = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));

        // Fundo da barra lateral: um pouco mais escuro que o conteúdo, com leve degradê
        r["SidebarBackgroundBrush"] = theme == ApplicationTheme.Dark
            ? Freeze(new LinearGradientBrush(Rgb(0x08, 0x0C, 0x17), Rgb(0x0B, 0x10, 0x1F), new Point(0, 0), new Point(0, 1)))
            : Freeze(new SolidColorBrush(Colors.Transparent));
        if (theme == ApplicationTheme.Dark) r["ApplicationBackgroundColor"] = Rgb(0x0A, 0x0E, 0x1A);
        else if (r.Contains("ApplicationBackgroundColor")) r.Remove("ApplicationBackgroundColor");

        foreach (var (key, color) in Dark)
        {
            if (theme == ApplicationTheme.Dark) r[key] = Freeze(new SolidColorBrush(color));
            else if (r.Contains(key)) r.Remove(key); // claro: volta aos tons do Fluent
        }
    }

    /// <summary>Segunda cor do degradê: roxo para destaques frios/verdes, turquesa para destaques roxos/rosados.</summary>
    public static Color SecondColor(Color accent)
    {
        var hue = Hue(accent);
        return hue is >= 240 and <= 340 ? Rgb(0x06, 0xB6, 0xD4) : Rgb(0x8B, 0x5C, 0xF6);
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0) return 0;
        var h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Lighten(Color c, double f) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    private static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
