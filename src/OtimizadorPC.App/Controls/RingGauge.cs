using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;

namespace OtimizadorPC.App.Controls;

/// <summary>Anel de progresso (0–100) com transição animada entre valores.</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new PropertyMetadata(0d, OnValueChanged));

    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        "AnimatedValue", typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var to = Math.Clamp((double)e.NewValue, 0, 100);
        if (AnimationSettings.Enabled)
        {
            gauge.BeginAnimation(AnimatedValueProperty, new DoubleAnimation(to, AnimationSettings.Scale(600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            gauge.BeginAnimation(AnimatedValueProperty, null);
            gauge.SetValue(AnimatedValueProperty, to);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var t = Thickness;
        var radius = (size - t) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = new Pen(TrackBrush, t);
        dc.DrawEllipse(null, track, center, radius, radius);

        var value = (double)GetValue(AnimatedValueProperty);
        if (value <= 0.05) return;

        var pen = new Pen(FillBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 99.95)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = value / 100 * 360;
        var start = PointOn(center, radius, 0);
        var end = PointOn(center, radius, angle);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static Point PointOn(Point c, double r, double degrees)
    {
        var rad = (degrees - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}
