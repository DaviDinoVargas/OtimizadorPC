using CommunityToolkit.Mvvm.ComponentModel;

namespace OtimizadorPC.App.ViewModels;

/// <summary>Base das páginas navegáveis.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Chamado toda vez que a página é exibida.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>Chamado quando o usuário sai da página.</summary>
    public virtual void OnNavigatedFrom() { }
}
