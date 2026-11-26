using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Formatting;

namespace OtimizadorPC.App.Controls;

public enum CountUpFormat { Bytes, Inteiro, Percentual, Decimal }

/// <summary>Texto numérico que "conta" até o novo valor quando ele muda.</summary>
public sealed class CountUpText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CountUpText), new PropertyMetadata(0d, OnValueChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(CountUpFormat), typeof(CountUpText), new PropertyMetadata(CountUpFormat.Bytes, (d, _) => ((CountUpText)d).Render()));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(CountUpText), new PropertyMetadata(string.Empty, (d, _) => ((CountUpText)d).Render()));

    private static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        "Current", typeof(double), typeof(CountUpText), new PropertyMetadata(0d, (d, _) => ((CountUpText)d).Render()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public CountUpFormat Format { get => (CountUpFormat)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    public CountUpText() => Render();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var t = (CountUpText)d;
        var to = (double)e.NewValue;
        if (AnimationSettings.Enabled)
            t.BeginAnimation(CurrentProperty, new DoubleAnimation(to, AnimationSettings.Scale(700)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            t.BeginAnimation(CurrentProperty, null);
            t.SetValue(CurrentProperty, to);
        }
    }

    private void Render()
    {
        var v = (double)GetValue(CurrentProperty);
        Text = Format switch
        {
            CountUpFormat.Bytes => ByteSize.Format((long)v),
            CountUpFormat.Inteiro => $"{v:N0}",
            CountUpFormat.Percentual => $"{v:0}%",
            _ => $"{v:0.0}",
        } + Suffix;
    }
}
