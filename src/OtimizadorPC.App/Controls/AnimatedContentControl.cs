using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OtimizadorPC.App.Services;
using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.App.Controls;

/// <summary>ContentControl que anima a troca de página conforme a transição escolhida.</summary>
public sealed class AnimatedContentControl : ContentControl
{
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        PlayTransition();
    }

    public void PlayTransition()
    {
        BeginAnimation(OpacityProperty, null);
        if (!AnimationSettings.Enabled || AnimationSettings.Transition == TransitionKind.Nenhuma)
        {
            Opacity = 1;
            RenderTransform = Transform.Identity;
            return;
        }

        var duration = AnimationSettings.Scale(340);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        switch (AnimationSettings.Transition)
        {
            case TransitionKind.Deslizar:
                var translate = new TranslateTransform(0, 22);
                RenderTransform = translate;
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, duration) { EasingFunction = ease });
                break;

            case TransitionKind.Zoom:
                var scale = new ScaleTransform(0.95, 0.95);
                RenderTransformOrigin = new Point(0.5, 0.35);
                RenderTransform = scale;
                var zoom = new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
                break;

            default:
                RenderTransform = Transform.Identity;
                break;
        }
    }
}
