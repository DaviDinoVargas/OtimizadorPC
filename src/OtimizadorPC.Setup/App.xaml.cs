using System.IO;
using System.Windows;
using OtimizadorPC.Setup.Engine;
using Wpf.Ui.Appearance;

namespace OtimizadorPC.Setup;

/// <summary>
/// Instalador e desinstalador. Argumentos:
///   (nenhum)               instala ou atualiza
///   /uninstall             desinstala com interface
///   /uninstall /silent     desinstala sem interface
/// Quando executado como "Desinstalar.exe" dentro da pasta do app, entra direto na desinstalação.
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        var uninstall = args.Contains("/uninstall") ||
                        string.Equals(Path.GetFileName(Environment.ProcessPath), InstallerEngine.UninstallerName, StringComparison.OrdinalIgnoreCase);

        if (uninstall && args.Contains("/silent"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await InstallerEngine.UninstallAsync(removeUserData: false, new Progress<StepUpdate>(), CancellationToken.None); }
            finally { Shutdown(); }
            return;
        }

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        // Mesma cor de destaque padrão do aplicativo (turquesa)
        ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0x14, 0xB8, 0xA6), ApplicationTheme.Dark, false, false);
        var window = new MainWindow(new SetupViewModel(uninstall));
        MainWindow = window;
        window.Show();
    }
}
