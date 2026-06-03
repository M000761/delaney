using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

// DM6: dual-mode. Holds two independent (router, edited) sets -- one for the
// blocklist (kidblock-domains.conf) and one for the allowlist
// (kidblock-allowlist.conf) -- and pivots the UI surface on CurrentMode.
//
// Switching mode does NOT discard the other slot's staged edits: each Apply
// writes only the active slot, so the user can stage changes in one mode,
// switch to the other to make edits there, and switch back to find the first
// slot's pending changes still pending. (Switch-with-unsaved feels like a
// foot-gun -- the BoolToVis pending badge + the device-row Mode column make
// the divergence visible; a discard-on-switch pass can land later if Adam
// surfaces friction.)
public sealed partial class DomainsViewModel : ObservableObject
{
    private readonly DomainCategorizer _categorizer;

    private readonly HashSet<string> _editedBlock  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _routerBlock  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _editedAllow  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _routerAllow  = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DomainEntry> Domains       { get; } = new();
    public ObservableCollection<CategoryViewModel> Categories  { get; } = new();
    public ObservableCollection<DomainCheckboxViewModel> OtherDomains { get; } = new();

    [ObservableProperty] private int _pendingChangeCount;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string? _applyError;
    [ObservableProperty] private string _newOtherDomain = string.Empty;
    [ObservableProperty] private string? _addOtherError;
    [ObservableProperty] private DeviceMode _currentMode = DeviceMode.Blocklist;

    public DomainsViewModel() : this(DomainCategorizer.Load()) { }

    public DomainsViewModel(DomainCategorizer categorizer)
    {
        _categorizer = categorizer;
        foreach (var cat in _categorizer.CategoryOrder)
            Categories.Add(new CategoryViewModel(this, cat, _categorizer.CategoryDomains[cat]));
    }

    public bool HasPendingChanges => PendingChangeCount > 0;

    public string PaneHeader => CurrentMode == DeviceMode.Whitelist ? "Allowed domains" : "Blocked domains";

    public string ActionVerb => CurrentMode == DeviceMode.Whitelist ? "Allowed" : "Blocked";

    private HashSet<string> EditedSet => CurrentMode == DeviceMode.Whitelist ? _editedAllow : _editedBlock;
    private HashSet<string> RouterSet => CurrentMode == DeviceMode.Whitelist ? _routerAllow : _routerBlock;

    public bool IsStaged(string domain) => EditedSet.Contains(domain);

    public void LoadBlocklistFromRouter(IEnumerable<DomainEntry> entries)
    {
        LoadInto(_routerBlock, _editedBlock, entries);
        if (CurrentMode == DeviceMode.Blocklist) RefreshUiFromStaged();
    }

    public void LoadAllowlistFromRouter(IEnumerable<DomainEntry> entries)
    {
        LoadInto(_routerAllow, _editedAllow, entries);
        if (CurrentMode == DeviceMode.Whitelist) RefreshUiFromStaged();
    }

    private static void LoadInto(HashSet<string> router, HashSet<string> edited, IEnumerable<DomainEntry> entries)
    {
        router.Clear();
        edited.Clear();
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Domain)) continue;
            router.Add(e.Domain);
            edited.Add(e.Domain);
        }
    }

    internal void StageAdd(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return;
        EditedSet.Add(domain);
    }

    internal void StageRemove(string domain)
    {
        EditedSet.Remove(domain);
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
        if (EditedSet.Contains(domain)) { AddOtherError = $"`{domain}` is already in the list."; return false; }
        EditedSet.Add(domain);
        NewOtherDomain = string.Empty;
        RefreshUiFromStaged();
        return true;
    }

    public void RemoveOther(string domain)
    {
        if (EditedSet.Remove(domain)) RefreshUiFromStaged();
    }

    public IReadOnlyList<DiffLine> ComputeDiff()
    {
        var edited = EditedSet;
        var router = RouterSet;
        var adds    = new List<string>();
        var removes = new List<string>();
        foreach (var d in edited)
            if (!router.Contains(d)) adds.Add(d);
        foreach (var d in router)
            if (!edited.Contains(d)) removes.Add(d);
        adds.Sort(StringComparer.OrdinalIgnoreCase);
        removes.Sort(StringComparer.OrdinalIgnoreCase);

        var verb = ActionVerb;
        var lines = new List<DiffLine>(adds.Count + removes.Count);
        foreach (var d in adds)    lines.Add(new DiffLine(DiffKind.Add,    DescribeAdd(d, verb)));
        foreach (var d in removes) lines.Add(new DiffLine(DiffKind.Remove, DescribeRemove(d, verb)));
        return lines;
    }

    public string SerializeEdited()
    {
        var sorted = EditedSet
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DomainEntry(d));
        return DomainsSerializer.Serialize(sorted, CurrentMode);
    }

    public bool MatchesRouter(IEnumerable<DomainEntry> remote)
    {
        var remoteSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in remote)
            if (!string.IsNullOrWhiteSpace(e.Domain)) remoteSet.Add(e.Domain);
        return remoteSet.SetEquals(EditedSet);
    }

    public void Discard()
    {
        var edited = EditedSet;
        var router = RouterSet;
        edited.Clear();
        foreach (var d in router) edited.Add(d);
        ApplyError = null;
        AddOtherError = null;
        RefreshUiFromStaged();
    }

    public void AcceptAsRouterState()
    {
        var edited = EditedSet;
        var router = RouterSet;
        router.Clear();
        foreach (var d in edited) router.Add(d);
        Domains.Clear();
        foreach (var d in router.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Domains.Add(new DomainEntry(d));
        RecomputePending();
    }

    internal void RefreshUiFromStaged()
    {
        // Rebuild the snapshot collection (Domains) -- some consumers read .Domains
        // for the status label count. Use the EDITED set so the label reflects what
        // the user will push on Apply, matching the pending-badge semantics.
        Domains.Clear();
        foreach (var d in EditedSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            Domains.Add(new DomainEntry(d));

        foreach (var cat in Categories) cat.Refresh();
        RebuildOtherList();
        RecomputePending();
    }

    partial void OnCurrentModeChanged(DeviceMode value)
    {
        // Pivot to the other slot's staged state. Don't reset anything in either
        // slot -- the user's pending edits in the inactive slot stay pending.
        RefreshUiFromStaged();
        OnPropertyChanged(nameof(PaneHeader));
        OnPropertyChanged(nameof(ActionVerb));
    }

    private void RebuildOtherList()
    {
        var byKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Categories)
            foreach (var d in cat.Domains) byKnown.Add(d.Domain);

        OtherDomains.Clear();
        foreach (var d in EditedSet
                     .Where(x => !byKnown.Contains(x))
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            OtherDomains.Add(new DomainCheckboxViewModel(this, d, "Other"));
        }
    }

    private void RecomputePending()
    {
        var edited = EditedSet;
        var router = RouterSet;
        var adds = 0; var removes = 0;
        foreach (var d in edited) if (!router.Contains(d)) adds++;
        foreach (var d in router) if (!edited.Contains(d)) removes++;
        PendingChangeCount = adds + removes;
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private string DescribeAdd(string d, string verb)
    {
        var cat = _categorizer.CategoryFor(d);
        return cat is null ? $"{verb}: add {d}" : $"{verb}: add {d} ({cat})";
    }

    private string DescribeRemove(string d, string verb)
    {
        var cat = _categorizer.CategoryFor(d);
        return cat is null ? $"{verb}: remove {d}" : $"{verb}: remove {d} ({cat})";
    }
}
