using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;

namespace OtimizadorPC.App.Controls.Animations;

/// <summary>
/// Base das animações de cabeçalho de cada página. Procura um Storyboard com a chave
/// "Loop" nos recursos e o executa em repetição, respeitando as preferências de animação.
/// </summary>
public class AnimatedIcon : UserControl
{
    private Storyboard? _loop;

    public AnimatedIcon()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Animações que aparecem/somem (ex.: "concluído") recomeçam sempre que ficam visíveis
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && IsLoaded) Restart(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationSettings.Changed += OnSettingsChanged;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AnimationSettings.Changed -= OnSettingsChanged;
        _loop?.Stop(this);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Dispatcher.Invoke(Restart);

    public void Restart()
    {
        // Recursos podem estar congelados; trabalha com uma cópia para poder ajustar a velocidade.
        _loop ??= (TryFindResource("Loop") as Storyboard)?.Clone();
        if (_loop is null) return;

        _loop.Stop(this);
        if (!AnimationSettings.Enabled) return;

        _loop.SpeedRatio = AnimationSettings.Speed;
        _loop.Begin(this, true);
    }
}
