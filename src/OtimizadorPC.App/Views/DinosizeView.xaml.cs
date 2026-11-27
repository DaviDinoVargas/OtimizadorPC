using System.Windows.Controls;
using System.Windows.Input;
using OtimizadorPC.App.ViewModels;

namespace OtimizadorPC.App.Views;

public partial class DinosizeView : UserControl
{
    public DinosizeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DinosizeViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    // Mantém a linha selecionada visível (ex.: depois de clicar no treemap)
                    if (e.PropertyName == nameof(DinosizeViewModel.SelectedRow) && vm.SelectedRow is not null)
                        Dispatcher.BeginInvoke(() => Tree.ScrollIntoView(vm.SelectedRow));
                };
        };
    }

    private DinosizeViewModel? Vm => DataContext as DinosizeViewModel;

    /// <summary>Teclado na árvore: → abre, ← fecha ou sobe, Enter abre/fecha, Delete envia para a Lixeira.</summary>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm?.SelectedRow is not { } row) return;
        switch (e.Key)
        {
            case Key.Right: Vm.ExpandCommand.Execute(row); e.Handled = true; break;
            case Key.Left: Vm.CollapseOrParentCommand.Execute(row); e.Handled = true; break;
            case Key.Enter: Vm.ToggleExpandCommand.Execute(row); e.Handled = true; break;
            case Key.Delete: Vm.RecycleCommand.Execute(row); e.Handled = true; break;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { DataContext: TreeRow row }) Vm?.ToggleExpandCommand.Execute(row);
    }
}
