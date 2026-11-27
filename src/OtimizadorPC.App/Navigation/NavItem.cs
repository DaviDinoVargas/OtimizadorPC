using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace OtimizadorPC.App.Navigation;

/// <summary>Item da barra lateral.</summary>
public sealed partial class NavItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required SymbolRegular Icon { get; init; }
    public required Type ViewModelType { get; init; }

    /// <summary>Seção da barra lateral (ex.: "PAINEL", "FERRAMENTAS").</summary>
    public string Group { get; init; } = string.Empty;

    /// <summary>Texto curto opcional exibido ao lado do item (ex.: "3 GB").</summary>
    [ObservableProperty]
    private string? _badge;
}
