using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KidBlockUI.Models;
using KidBlockUI.ViewModels;

namespace KidBlockUI.Views;

public partial class LogTailPanel : UserControl
{
    private INotifyCollectionChanged? _subscribedEntries;
    private LogTailViewModel? _vm;

    public LogTailPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeFromEntries();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeFromEntries();
        if (DataContext is LogTailViewModel vm)
        {
            _vm = vm;
            _subscribedEntries = vm.Entries;
            _subscribedEntries.CollectionChanged += OnEntriesChanged;
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null || !_vm.AutoScroll) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // ScrollToEnd is no-op on an empty ScrollViewer; safe to call unconditionally.
        // Dispatcher-defer so the item-container generator has a chance to lay out
        // the new row before we scroll past it.
        Dispatcher.BeginInvoke(new System.Action(() => Scroller.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void UnsubscribeFromEntries()
    {
        if (_subscribedEntries is not null)
            _subscribedEntries.CollectionChanged -= OnEntriesChanged;
        _subscribedEntries = null;
        _vm = null;
    }
}
