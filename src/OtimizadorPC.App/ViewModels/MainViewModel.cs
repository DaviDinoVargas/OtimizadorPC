using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using OtimizadorPC.App.Navigation;
using OtimizadorPC.Core.Abstractions;
using OtimizadorPC.Core.Settings;
using Wpf.Ui.Controls;

namespace OtimizadorPC.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private NavItem? _selectedNav;

    public MainViewModel(IServiceProvider services, ISettingsService settings)
    {
        _services = services;
        _settings = settings;
        _isCompact = settings.Current.SidebarMode == SidebarMode.Compacta;
        settings.Changed += (_, s) => IsCompact = s.SidebarMode == SidebarMode.Compacta;

        MainItems =
        [
            new NavItem { Key = "inicio", Title = "Início", Icon = SymbolRegular.Home24, ViewModelType = typeof(DashboardViewModel) },
            new NavItem { Key = "info", Title = "Informações", Icon = SymbolRegular.DeveloperBoard24, ViewModelType = typeof(MachineInfoViewModel) },
            new NavItem { Key = "limpeza", Title = "Limpeza", Icon = SymbolRegular.Broom24, ViewModelType = typeof(CleanupViewModel) },
            new NavItem { Key = "otimizacao", Title = "Otimização", Icon = SymbolRegular.Gauge24, ViewModelType = typeof(OptimizationViewModel) },
            new NavItem { Key = "processos", Title = "Processos", Icon = SymbolRegular.AppsList24, ViewModelType = typeof(ProcessesViewModel) },
            new NavItem { Key = "dinosize", Title = "Dinosize", Icon = SymbolRegular.DataTreemap24, ViewModelType = typeof(DinosizeViewModel) },
            new NavItem { Key = "overclock", Title = "Overclock", Icon = SymbolRegular.Rocket24, ViewModelType = typeof(OverclockViewModel) },
        ];

        FooterItems =
        [
            new NavItem { Key = "personalizacao", Title = "Personalização", Icon = SymbolRegular.PaintBrush24, ViewModelType = typeof(SettingsViewModel) },
        ];

        // Qualquer tela pode pedir navegação (ex.: botão "Resolver" de uma recomendação)
        WeakReferenceMessenger.Default.Register<NavigateMessage>(this, (_, m) => NavigateTo(m.Key));

        SelectedNav = MainItems[0];
    }

    public ObservableCollection<NavItem> MainItems { get; }
    public ObservableCollection<NavItem> FooterItems { get; }

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private bool _isCompact;

    /// <summary>Item atualmente aberto, venha ele da lista principal ou do rodapé.</summary>
    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (value is null) return;
            if (MainItems.Contains(value)) SelectedMainItem = value;
            else SelectedFooterItem = value;
        }
    }

    // Cada lista tem a própria seleção; escolher em uma limpa a outra.
    private NavItem? _selectedMainItem;
    private NavItem? _selectedFooterItem;

    public NavItem? SelectedMainItem
    {
        get => _selectedMainItem;
        set
        {
            if (!SetProperty(ref _selectedMainItem, value) || value is null) return;
            SelectedFooterItem = null;
            Open(value);
        }
    }

    public NavItem? SelectedFooterItem
    {
        get => _selectedFooterItem;
        set
        {
            if (!SetProperty(ref _selectedFooterItem, value) || value is null) return;
            SelectedMainItem = null;
            Open(value);
        }
    }

    public void NavigateTo(string key)
    {
        var item = MainItems.Concat(FooterItems).FirstOrDefault(i => i.Key == key);
        if (item is not null) SelectedNav = item;
    }

    private void Open(NavItem item)
    {
        if (ReferenceEquals(item, _selectedNav)) return;
        _selectedNav = item;
        OnPropertyChanged(nameof(SelectedNav));
        Navigate(item);
    }

    private void Navigate(NavItem item)
    {
        CurrentPage?.OnNavigatedFrom();
        var page = (PageViewModel)_services.GetRequiredService(item.ViewModelType);
        CurrentPage = page;
        _ = page.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void ToggleSidebar() =>
        _settings.Update(s => s.SidebarMode = s.SidebarMode == SidebarMode.Expandida ? SidebarMode.Compacta : SidebarMode.Expandida);
}
