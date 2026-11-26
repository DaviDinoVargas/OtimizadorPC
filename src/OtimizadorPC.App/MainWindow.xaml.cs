using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;
using OtimizadorPC.App.ViewModels;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Settings;
using Wpf.Ui.Controls;

namespace OtimizadorPC.App;

public partial class MainWindow : FluentWindow
{
    private const double ExpandedWidth = 248;
    private const double CompactWidth = 68;

    private readonly ISettingsService _settings;
    private SidebarPosition? _appliedPosition;

    public MainWindow(MainViewModel viewModel, ISettingsService settings)
    {
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();

        ApplyLayout(settings.Current, animate: false);
        settings.Changed += (_, s) => Dispatcher.Invoke(() => ApplyLayout(s, animate: true));
    }

    /// <summary>Aplica posição e largura da barra lateral e a escala da interface.</summary>
    private void ApplyLayout(AppSettings s, bool animate)
    {
        // Posição: troca as colunas da barra e do conteúdo
        if (_appliedPosition != s.SidebarPosition)
        {
            var right = s.SidebarPosition == SidebarPosition.Direita;

            // A coluna 0 é sempre a da esquerda: à direita, o conteúdo vai para ela e fica flexível.
            Grid.SetColumn(Sidebar, right ? 1 : 0);
            Grid.SetColumn(ContentHost, right ? 0 : 1);
            SideColumn.Width = right ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            ContentColumn.Width = right ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            ContentHost.CornerRadius = right ? new CornerRadius(0, 10, 0, 0) : new CornerRadius(10, 0, 0, 0);
            ContentHost.BorderThickness = right ? new Thickness(0, 1, 1, 0) : new Thickness(1, 1, 0, 0);
            _appliedPosition = s.SidebarPosition;
        }

        // Largura: expandida ou compacta, com animação
        var target = s.SidebarMode == SidebarMode.Compacta ? CompactWidth : ExpandedWidth;
        if (animate && AnimationSettings.Enabled)
        {
            var anim = new DoubleAnimation(target, AnimationSettings.Scale(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Sidebar.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
        }

        // Escala da interface (apenas a área de conteúdo)
        var scale = Math.Clamp(s.UiScale, 0.75, 1.5);
        UiScale.ScaleX = scale;
        UiScale.ScaleY = scale;
    }
}
