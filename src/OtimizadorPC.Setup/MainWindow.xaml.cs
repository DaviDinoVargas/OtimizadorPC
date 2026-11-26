using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;

namespace OtimizadorPC.Setup;

public partial class MainWindow : FluentWindow
{
    private readonly SetupViewModel _vm;

    public MainWindow(SetupViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => ((Storyboard)Resources["Brand"]).Begin(this, true);
        vm.PropertyChanged += OnVmChanged;
        Closing += OnClosing;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupViewModel.Page)) return;

        // Transição entre páginas: desliza e aparece
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        PageSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        if (_vm.Page == SetupPage.Concluido)
        {
            DoneRing.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });
            DoneCheck.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(350)) { BeginTime = TimeSpan.FromMilliseconds(400), EasingFunction = ease });
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Não fecha no meio da instalação: o botão Cancelar interrompe com segurança
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            _vm.CancelCommand.Execute(null);
        }
    }
}
