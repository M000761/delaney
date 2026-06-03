using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

public sealed partial class DomainsViewModel : ObservableObject
{
    private readonly DomainCategorizer _categorizer;
    private readonly HashSet<string> _editedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _routerDomains = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DomainEntry> Domains       { get; } = new();
    public ObservableCollection<CategoryViewModel> Categories  { get; } = new();
    public ObservableCollection<DomainCheckboxViewModel> OtherDomains { get; } = new();

    [ObservableProperty] private int _pendingChangeCount;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string? _applyError;
    [ObservableProperty] private string _newOtherDomain = string.Empty;
    [ObservableProperty] private string? _addOtherError;

    public DomainsViewModel() : this(DomainCategorizer.Load()) { }

    public DomainsViewModel(DomainCategorizer categorizer)
    {
        _categorizer = categorizer;
        foreach (var cat in _categorizer.CategoryOrder)
            Categories.Add(new CategoryViewModel(this, cat, _categorizer.CategoryDomains[cat]));
    }

    public bool HasPendingChanges => PendingChangeCount > 0;

    public bool IsStaged(string domain) => _editedDomains.Contains(domain);

    public void LoadFromRouter(IEnumerable<DomainEntry> entries)
    {
        _routerDomains.Clear();
        _editedDomains.Clear();
        Domains.Clear();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Domain)) continue;
            _routerDomains.Add(e.Domain);
            _editedDomains.Add(e.Domain);
            Domains.Add(e);
        }
        ApplyError = null;
        AddOtherError = null;
        RefreshUiFromStaged();
    }

    internal void StageAdd(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        _editedDomains.Add(domain);
    }

    internal void StageRemove(string domain)
    {
        _editedDomains.Remove(domain);
    }

    public bool AddOther(string raw)
    {
        AddOtherError = null;
        var domain = (raw ?? string.Empty).Trim();
        if (domain.Length == 0) { AddOtherError = "Domain cannot be empty."; return false; }
        if (domain.IndexOfAny(new[] { ' ', '\t' }) >= 0) { AddOtherError = "Domain may not contain whitespace."; return false; }
        if (!domain.Contains('.')) { AddOtherError = "Not a valid domain (needs a dot)."; return false; }
        if (_categorizer.CategoryFor(domain) is { } cat)
        {
            AddOtherError = $"`{domain}` belongs to '{cat}' -- toggle it in that section.";
            return false;
        }
        if (_editedDomains.Contains(domain)) { AddOtherError = $"`{domain}` is already in the list."; return false; }
        _editedDomains.Add(domain);
        NewOtherDomain = string.Empty;
        RefreshUiFromStaged();
        return true;
    }

    public void RemoveOther(string domain)
    {
        if (_editedDomains.Remove(domain)) RefreshUiFromStaged();
    }

    public IReadOnlyList<DiffLine> ComputeDiff()
    {
        var adds    = new List<string>();
        var removes = new List<string>();
        foreach (var d in _editedDomains)
            if (!_routerDomains.Contains(d)) adds.Add(d);
        foreach (var d in _routerDomains)
            if (!_editedDomains.Contains(d)) removes.Add(d);
        adds.Sort(StringComparer.OrdinalIgnoreCase);
        removes.Sort(StringComparer.OrdinalIgnoreCase);

        var lines = new List<DiffLine>(adds.Count + removes.Count);
        foreach (var d in adds)    lines.Add(new DiffLine(DiffKind.Add,    DescribeAdd(d)));
        foreach (var d in removes) lines.Add(new DiffLine(DiffKind.Remove, DescribeRemove(d)));
        return lines;
    }

    public string SerializeEdited()
    {
        var sorted = _editedDomains
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DomainEntry(d));
        return DomainsSerializer.Serialize(sorted);
    }

    public bool MatchesRouter(IEnumerable<DomainEntry> remote)
    {
        var remoteSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in remote)
            if (!string.IsNullOrWhiteSpace(e.Domain)) remoteSet.Add(e.Domain);
        return remoteSet.SetEquals(_editedDomains);
    }

    public void Discard()
    {
        _editedDomains.Clear();
        foreach (var d in _routerDomains) _editedDomains.Add(d);
        ApplyError = null;
        AddOtherError = null;
        RefreshUiFromStaged();
    }

    public void AcceptAsRouterState()
    {
        _routerDomains.Clear();
        foreach (var d in _editedDomains) _routerDomains.Add(d);
        Domains.Clear();
        foreach (var d in _routerDomains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Domains.Add(new DomainEntry(d));
        RecomputePending();
    }

    internal void RefreshUiFromStaged()
    {
        foreach (var cat in Categories) cat.Refresh();
        RebuildOtherList();
        RecomputePending();
    }

    private void RebuildOtherList()
    {
        var byKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Categories)
            foreach (var d in cat.Domains) byKnown.Add(d.Domain);

        OtherDomains.Clear();
        foreach (var d in _editedDomains
                     .Where(x => !byKnown.Contains(x))
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            OtherDomains.Add(new DomainCheckboxViewModel(this, d, "Other"));
        }
    }

    private void RecomputePending()
    {
        var adds = 0; var removes = 0;
        foreach (var d in _editedDomains) if (!_routerDomains.Contains(d)) adds++;
        foreach (var d in _routerDomains) if (!_editedDomains.Contains(d)) removes++;
        PendingChangeCount = adds + removes;
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private string DescribeAdd(string d)
    {
        var cat = _categorizer.CategoryFor(d);
        return cat is null ? $"Add {d}" : $"Add {d} ({cat})";
    }

    private string DescribeRemove(string d)
    {
        var cat = _categorizer.CategoryFor(d);
        return cat is null ? $"Remove {d}" : $"Remove {d} ({cat})";
    }
}
