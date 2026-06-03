using CommunityToolkit.Mvvm.ComponentModel;

namespace KidBlockUI.ViewModels;

public sealed partial class DomainCheckboxViewModel : ObservableObject
{
    private readonly DomainsViewModel _parent;
    private bool _suppressNotify;

    public string Domain       { get; }
    public string CategoryName { get; }
    public string Tooltip => $"Member of: {CategoryName}";

    [ObservableProperty] private bool _isChecked;

    public DomainCheckboxViewModel(DomainsViewModel parent, string domain, string categoryName)
    {
        _parent       = parent;
        Domain        = domain;
        CategoryName  = categoryName;
        _isChecked    = parent.IsStaged(domain);
    }

    public void RefreshFromStage()
    {
        var staged = _parent.IsStaged(Domain);
        if (IsChecked == staged) return;
        _suppressNotify = true;
        try { IsChecked = staged; }
        finally { _suppressNotify = false; }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_suppressNotify) return;
        if (value) _parent.StageAdd(Domain);
        else        _parent.StageRemove(Domain);
        _parent.RefreshUiFromStaged();
    }
}
