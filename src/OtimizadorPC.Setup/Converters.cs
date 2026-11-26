using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OtimizadorPC.Setup.Engine;
using Wpf.Ui.Controls;

namespace OtimizadorPC.Setup;

public sealed class PageToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (parameter?.ToString() ?? string.Empty).Split('|').Contains(value?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StepSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StepState.Concluido => SymbolRegular.CheckmarkCircle24,
        StepState.Ignorado => SymbolRegular.ArrowRight24,
        StepState.Erro => SymbolRegular.ErrorCircle24,
        StepState.Executando => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.Circle24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StepBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            StepState.Concluido => "SystemFillColorSuccessBrush",
            StepState.Erro => "SystemFillColorCriticalBrush",
            StepState.Executando => "AccentTextFillColorPrimaryBrush",
            _ => "TextFillColorTertiaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
