using System.Windows;
using KidBlockUI.Services;
using KidBlockUI.ViewModels;

namespace KidBlockUI.Views;

// DM22: per-MAC effective-verdict popover. Constructs its own WhyBlockedViewModel
// (which owns an independent SSH session) and drives the auto-refresh lifecycle off
// the window's Loaded / Closed events so a dialog opened-then-closed never leaves a
// hanging ssh session.
public partial class WhyBlockedDialog : Window
{
    private readonly WhyBlockedViewModel _vm;

    public WhyBlockedDialog(RouterConfig config, string mac, string? deviceName)
    {
        InitializeComponent();
        _vm = new WhyBlockedViewModel(config, mac, deviceName);
        DataContext = _vm;
        Loaded += (_, _) => _vm.Start();
        Closed += (_, _) => _vm.Stop();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
