using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.DiskAnalysis;
using OtimizadorPC.Core.Formatting;

namespace OtimizadorPC.App.Controls;

/// <summary>
/// Mapa de disco "squarified": cada retângulo é uma pasta com área proporcional ao tamanho.
/// Mostra dois níveis, destaca o item sob o mouse e entra na pasta ao clicar.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(DirNode), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TreemapView)d).OnRootChanged()));

    /// <summary>Comando executado com a pasta (DirNode) clicada.</summary>
    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(TreemapView));

    public DirNode? Root { get => (DirNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }

    public ICommand? ActivateCommand { get => (ICommand?)GetValue(ActivateCommandProperty); set => SetValue(ActivateCommandProperty, value); }

    /// <summary>Paleta usada pelo mapa e pela lista ao lado (mesmo índice = mesma cor).</summary>
    public static readonly Color[] Palette =
    [
        Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x63, 0x66, 0xF1), Color.FromRgb(0xF5, 0x9E, 0x0B),
        Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x22, 0xC5, 0x5E), Color.FromRgb(0x0E, 0xA5, 0xE9),
        Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x84, 0xCC, 0x16),
        Color.FromRgb(0xF9, 0x73, 0x16),
    ];

    private sealed record Cell(TreemapRect Rect, DirNode? Node, string Label, long Size, int Depth, int ColorIndex);

    private readonly List<Cell> _cells = [];
    private Size _layoutSize;
    private Cell? _hover;
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public TreemapView()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);
    }

    private void OnRootChanged()
    {
        _layoutSize = default;
        _hover = null;
        if (!AnimationSettings.Enabled) return;
        var duration = AnimationSettings.Scale(380);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var scale = (ScaleTransform)RenderTransform;
        var grow = new DoubleAnimation(0.9, 1, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _layoutSize = default;
    }

    private void EnsureLayout()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size == _layoutSize) return;
        _layoutSize = size;
        _cells.Clear();
        if (Root is null || size.Width < 4 || size.Height < 4) return;
        Layout(Root, new TreemapRect(0, 0, size.Width, size.Height), 0, -1);
    }

    private void Layout(DirNode node, TreemapRect area, int depth, int colorIndex)
    {
        // Pastas filhas + um bloco para os arquivos soltos desta pasta
        var items = node.Children.Where(c => c.Size > 0).Take(250).Select(c => (Node: (DirNode?)c, c.Name, c.Size)).ToList();
        if (node.FilesSize > 0) items.Add((null, "(arquivos nesta pasta)", node.FilesSize));
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (items.Count == 0) return;

        var rects = Treemap.Squarify(items.Select(i => (double)i.Size).ToArray(), area);
        for (var i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            if (r.Width < 1 || r.Height < 1) continue;
            var color = depth == 0 ? i % Palette.Length : colorIndex;
            _cells.Add(new Cell(r, items[i].Node, items[i].Name, items[i].Size, depth, color));

            // Segundo nível dentro dos blocos grandes, deixando espaço para o nome
            if (depth == 0 && items[i].Node is { HasChildren: true } child && r.Width > 70 && r.Height > 50)
                Layout(child, new TreemapRect(r.X + 3, r.Y + 20, r.Width - 6, r.Height - 23), 1, color);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        EnsureLayout();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var border = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
        border.Freeze();

        foreach (var c in _cells)
        {
            var baseColor = Palette[c.ColorIndex];
            var shade = c.Depth == 0 ? baseColor : Lighten(baseColor, 0.18);
            if (c.Node is null) shade = Desaturate(shade);
            var fill = new LinearGradientBrush(Lighten(shade, 0.12), Darken(shade, 0.12), new Point(0, 0), new Point(1, 1));
            fill.Freeze();
            var rect = new Rect(c.Rect.X, c.Rect.Y, c.Rect.Width, c.Rect.Height);
            dc.DrawRectangle(fill, border, rect);

            if (c.Rect.Width > 48 && c.Rect.Height > 16)
            {
                var text = new FormattedText($"{c.Label}  {ByteSize.Format(c.Size)}", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Variable Text, Segoe UI"), c.Depth == 0 ? 12 : 10.5, Brushes.White, dpi)
                {
                    MaxTextWidth = Math.Max(1, c.Rect.Width - 8),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(c.Rect.X + 4, c.Rect.Y + 2));
            }
        }

        if (_hover is not null)
        {
            var accent = TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(accent, 2),
                new Rect(_hover.Rect.X + 1, _hover.Rect.Y + 1, Math.Max(0, _hover.Rect.Width - 2), Math.Max(0, _hover.Rect.Height - 2)));
        }
    }

    private Cell? HitTest(Point p) => _cells.LastOrDefault(c => c.Rect.Contains(p.X, p.Y));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        if (ReferenceEquals(hit, _hover)) return;
        _hover = hit;
        _tip.Content = hit is null ? null
            : $"{hit.Label}\n{ByteSize.Format(hit.Size)}" + (hit.Node is { } n ? $" · {n.TotalFiles:N0} arquivos\nClique para abrir" : string.Empty);
        _tip.IsOpen = hit is not null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTest(e.GetPosition(this))?.Node is { } node)
        {
            _tip.IsOpen = false;
            if (ActivateCommand?.CanExecute(node) == true) ActivateCommand.Execute(node);
        }
    }

    private static Color Lighten(Color c, double f) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    private static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * (1 - f)), (byte)(c.G * (1 - f)), (byte)(c.B * (1 - f)));

    private static Color Desaturate(Color c)
    {
        var gray = (byte)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
        return Color.FromRgb((byte)((c.R + gray * 2) / 3), (byte)((c.G + gray * 2) / 3), (byte)((c.B + gray * 2) / 3));
    }
}
