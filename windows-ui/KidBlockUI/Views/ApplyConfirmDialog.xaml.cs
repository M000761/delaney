using System.Windows;
using System.Windows.Media;
using KidBlockUI.Models;

namespace KidBlockUI.Views;

public partial class ApplyConfirmDialog : Window
{
    private static readonly SolidColorBrush AddBrush    = new(Color.FromRgb(0x66, 0xCC, 0x66));
    private static readonly SolidColorBrush RemoveBrush = new(Color.FromRgb(0xE0, 0x60, 0x60));
    private static readonly SolidColorBrush ModifyBrush = new(Color.FromRgb(0xFF, 0xC8, 0x33));

    public ApplyConfirmDialog()
    {
        InitializeComponent();
    }

    public static bool Show(Window owner, string title, IReadOnlyList<DiffLine> lines, string? confirmLabel = null)
    {
        var dlg = new ApplyConfirmDialog { Owner = owner };
        dlg.TitleText.Text = title;
        if (!string.IsNullOrWhiteSpace(confirmLabel)) dlg.ConfirmButton.Content = confirmLabel;
        foreach (var line in lines) dlg.DiffList.Items.Add(ToRow(line));
        return dlg.ShowDialog() == true;
    }

    private static object ToRow(DiffLine line) => new
    {
        Glyph = line.Kind switch
        {
            DiffKind.Add    => "+",
            DiffKind.Remove => "-",
            DiffKind.Modify => "~",
            _               => "?",
        },
        Color = line.Kind switch
        {
            DiffKind.Add    => AddBrush,
            DiffKind.Remove => RemoveBrush,
            DiffKind.Modify => ModifyBrush,
            _               => (Brush)Brushes.LightGray,
        },
        line.Text,
    };

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
