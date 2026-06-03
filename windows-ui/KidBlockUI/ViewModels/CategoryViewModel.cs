using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KidBlockUI.ViewModels;

public sealed partial class CategoryViewModel : ObservableObject
{
    private readonly DomainsViewModel _parent;

    public string Name { get; }
    public ObservableCollection<DomainCheckboxViewModel> Domains { get; } = new();

    [ObservableProperty] private string _headerText = string.Empty;
    [ObservableProperty] private bool? _headerChecked;

    public CategoryViewModel(DomainsViewModel parent, string name, IReadOnlyList<string> domains)
    {
        _parent = parent;
        Name = name;
        foreach (var d in domains)
            Domains.Add(new DomainCheckboxViewModel(parent, d, name));
        Refresh();
    }

    public void Refresh()
    {
        var total = Domains.Count;
        var on    = 0;
        foreach (var d in Domains)
        {
            d.RefreshFromStage();
            if (d.IsChecked) on++;
        }

        HeaderText = on == 0
            ? $"{Name} (0/{total} OFF)"
            : on == total
                ? $"{Name} ({total}/{total} ON)"
                : $"{Name} ({on}/{total} mixed)";

        HeaderChecked = on == 0 ? false : on == total ? true : (bool?)null;
    }

    // Spec: OFF -> set all ON; ON -> set all OFF; MIXED -> set all ON.
    public void HandleHeaderToggle()
    {
        var setOn = HeaderChecked != true;
        SetAll(setOn);
    }

    private void SetAll(bool on)
    {
        foreach (var d in Domains)
        {
            if (on) _parent.StageAdd(d.Domain);
            else     _parent.StageRemove(d.Domain);
        }
        _parent.RefreshUiFromStaged();
    }
}
