using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;

namespace OtimizadorPC.App.Controls;

/// <summary>
/// Mini gráfico de linha com área em degradê. Desenhado direto no WPF (leve e fiel ao tema).
/// Ao chegar um novo ponto, a linha desliza suavemente para a esquerda.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline), new PropertyMetadata(null, OnValuesChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(
        "Shift", typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values { get => (IEnumerable?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Sparkline)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += s.OnCollectionChanged;
        s.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Só anima quando um ponto novo entra no fim
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (AnimationSettings.Enabled)
            BeginAnimation(ShiftProperty, new DoubleAnimation(1, 0, AnimationSettings.Scale(450)) { EasingFunction = new SineEase() });
        else
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.Cast<object>().Select(Convert.ToDouble).ToArray();
        if (values is null || values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;

        var w = ActualWidth;
        var h = ActualHeight;
        var max = Maximum <= 0 ? Math.Max(1, values.Max()) : Maximum;
        var step = w / (values.Length - 2);       // um ponto fica fora da tela para o deslizamento
        var offset = (double)GetValue(ShiftProperty) * step;

        var points = values.Select((v, i) => new Point(i * step - step + offset, h - Math.Clamp(v / max, 0, 1) * (h - 2) - 1)).ToArray();

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            AddSmooth(ctx, points);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, h), true, true);
            ctx.LineTo(points[0], false, false);
            AddSmooth(ctx, points);
            ctx.LineTo(new Point(points[^1].X, h), false, false);
        }
        area.Freeze();

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var fill = new LinearGradientBrush(Color.FromArgb(110, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
        fill.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, line);
        dc.Pop();
    }

    /// <summary>Curva suave (Catmull-Rom convertida em Bézier) passando por todos os pontos.</summary>
    private static void AddSmooth(StreamGeometryContext ctx, Point[] p)
    {
        for (var i = 0; i < p.Length - 1; i++)
        {
            var p0 = i > 0 ? p[i - 1] : p[i];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = i + 2 < p.Length ? p[i + 2] : p2;
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ctx.BezierTo(c1, c2, p2, true, true);
        }
    }
}
