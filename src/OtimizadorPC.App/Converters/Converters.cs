using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace OtimizadorPC.App.Converters;

/// <summary>Liga RadioButtons a um enum: IsChecked = (valor == parâmetro).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is null || value is string { Length: 0 }) ^ Invert ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#000000")); }
        catch (FormatException) { return Brushes.Transparent; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formata bytes em B, KB, MB, GB ou TB.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            long l => Core.Formatting.ByteSize.Format(l),
            ulong u => Core.Formatting.ByteSize.Format((long)u),
            int i => Core.Formatting.ByteSize.Format(i),
            double d => Core.Formatting.ByteSize.Format((long)d),
            _ => string.Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multiplica um número pelo parâmetro (ex.: proporção 0-1 → largura).</summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double pct || values[1] is not double width) return 0d;
        return Math.Max(0, Math.Min(1, pct)) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
