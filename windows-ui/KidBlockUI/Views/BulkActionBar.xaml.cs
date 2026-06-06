using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace KidBlockUI.Views;

public partial class BulkActionBar : UserControl
{
    public BulkActionBar()
    {
        InitializeComponent();
    }

    private void AllowDurationItem_Click(object sender, RoutedEventArgs e)
    {
        var d = sender as DependencyObject;
        while (d != null)
        {
            if (d is Popup popup) { popup.IsOpen = false; return; }
            d = LogicalTreeHelper.GetParent(d)
                ?? (d is Visual v ? VisualTreeHelper.GetParent(v) : null);
        }
    }
}
