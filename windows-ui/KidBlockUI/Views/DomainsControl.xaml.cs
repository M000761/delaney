using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KidBlockUI.ViewModels;

namespace KidBlockUI.Views;

public partial class DomainsControl : UserControl
{
    public DomainsControl()
    {
        InitializeComponent();
    }

    private DomainsViewModel? Vm => DataContext as DomainsViewModel;

    private void CategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is CategoryViewModel cat)
        {
            // WPF's auto-cycle already mutated cb.IsChecked. Override to our
            // OFF/MIXED -> ON, ON -> OFF rule, then snap the binding back.
            cat.HandleHeaderToggle();
            cb.IsChecked = cat.HeaderChecked;
        }
    }

    private async void ApplyDomains_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is MainViewModel mvm)
        {
            await mvm.ApplyDomainsAsync(diff =>
                ApplyConfirmDialog.Show(window, "Apply domain changes?", diff));
        }
    }

    private void DiscardDomains_Click(object sender, RoutedEventArgs e)
    {
        Vm?.Discard();
    }

    private void AddOther_Click(object sender, RoutedEventArgs e)
    {
        TryAddOther();
    }

    private void NewOtherBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryAddOther();
            e.Handled = true;
        }
    }

    private void TryAddOther()
    {
        if (Vm is null) return;
        Vm.AddOther(Vm.NewOtherDomain);
    }

    private void RemoveOther_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string domain)
        {
            Vm?.RemoveOther(domain);
        }
    }
}
