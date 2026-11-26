using OtimizadorPC.Core.Settings;

namespace OtimizadorPC.App.Services;

/// <summary>Estado global das animações, lido pelos controles animados sem precisar de injeção.</summary>
public static class AnimationSettings
{
    public static bool Enabled { get; private set; } = true;
    public static double Speed { get; private set; } = 1.0;
    public static TransitionKind Transition { get; private set; } = TransitionKind.Deslizar;

    public static event EventHandler? Changed;

    public static void Apply(AppSettings settings)
    {
        var changed = Enabled != settings.AnimationsEnabled
                      || Math.Abs(Speed - settings.AnimationSpeed) > 0.001
                      || Transition != settings.Transition;

        Enabled = settings.AnimationsEnabled;
        Speed = Math.Clamp(settings.AnimationSpeed, 0.25, 3);
        Transition = settings.Transition;

        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Converte uma duração base (velocidade 1x) para a velocidade escolhida.</summary>
    public static TimeSpan Scale(double baseMilliseconds) => TimeSpan.FromMilliseconds(baseMilliseconds / Speed);
}
