using System.Diagnostics;
using System.Windows;
using Serilog;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace OtimizadorPC.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continuar", string cancelText = "Cancelar");
    Task InfoAsync(string title, string message);
}

/// <summary>Diálogos no estilo Fluent do WPF-UI.</summary>
public sealed class DialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Continuar", string cancelText = "Cancelar")
    {
        var box = new WpfUiMessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            Owner = Application.Current.MainWindow,
            MaxWidth = 560,
        };
        return await box.ShowDialogAsync() == WpfUiMessageBoxResult.Primary;
    }

    public async Task InfoAsync(string title, string message)
    {
        var box = new WpfUiMessageBox
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            Owner = Application.Current.MainWindow,
            MaxWidth = 560,
        };
        await box.ShowDialogAsync();
    }
}

public static class Elevation
{
    /// <summary>Reabre o OtimizadorPC como administrador (o Windows pede confirmação) e fecha esta instância.</summary>
    public static void RestartAsAdministrator()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Usuário recusou o pedido de elevação: continua como está.
            Log.Information("Elevação recusada pelo usuário");
        }
    }
}
