using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

public sealed partial class ScheduleViewModel : ObservableObject
{
    public ObservableCollection<ScheduleWindow> Schedule       { get; } = new();
    public ObservableCollection<ScheduleWindow> EditedSchedule { get; } = new();

    [ObservableProperty]
    private int _pendingChangeCount;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private string? _applyError;

    [ObservableProperty]
    private bool _overrideActive;

    public ScheduleViewModel()
    {
        EditedSchedule.CollectionChanged += OnEditedChanged;
        Schedule.CollectionChanged       += OnEditedChanged;
    }

    public bool HasPendingChanges => PendingChangeCount > 0;

    public void LoadFromRouter(IEnumerable<ScheduleWindow> windows)
    {
        Schedule.Clear();
        EditedSchedule.Clear();
        foreach (var w in windows) { Schedule.Add(w); EditedSchedule.Add(w); }
        RecomputePending();
    }

    public IReadOnlyList<DiffLine> ComputeDiff()
    {
        var (adds, removes, modifies) = ComputeDiffSets();
        var lines = new List<DiffLine>(adds.Count + removes.Count + modifies.Count);

        foreach (var (oldW, newW) in modifies)
            lines.Add(new DiffLine(DiffKind.Modify, $"Modify {DescribeWindow(oldW)} -> {DescribeWindow(newW)}"));
        foreach (var w in adds)
            lines.Add(new DiffLine(DiffKind.Add, $"Add block window {DescribeWindow(w)}"));
        foreach (var w in removes)
            lines.Add(new DiffLine(DiffKind.Remove, $"Remove block window {DescribeWindow(w)}"));
        return lines;
    }

    public string SerializeEdited() => ScheduleSerializer.Serialize(SortedClone(EditedSchedule));

    public bool MatchesRouter(IEnumerable<ScheduleWindow> remote)
    {
        var local = SortedClone(EditedSchedule);
        var rem   = SortedClone(remote);
        if (local.Count != rem.Count) return false;
        for (var i = 0; i < local.Count; i++)
        {
            if (!SameWindow(local[i], rem[i])) return false;
        }
        return true;
    }

    public void Discard()
    {
        EditedSchedule.Clear();
        foreach (var w in Schedule) EditedSchedule.Add(w);
        ApplyError = null;
        RecomputePending();
    }

    public void AcceptAsRouterState()
    {
        Schedule.Clear();
        foreach (var w in EditedSchedule) Schedule.Add(w);
        RecomputePending();
    }

    private void OnEditedChanged(object? sender, NotifyCollectionChangedEventArgs e) => RecomputePending();

    private void RecomputePending()
    {
        var (adds, removes, modifies) = ComputeDiffSets();
        PendingChangeCount = adds.Count + removes.Count + modifies.Count;
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private (List<ScheduleWindow> adds, List<ScheduleWindow> removes,
             List<(ScheduleWindow OldW, ScheduleWindow NewW)> modifies)
        ComputeDiffSets()
    {
        var localSet  = new List<ScheduleWindow>(EditedSchedule);
        var remoteSet = new List<ScheduleWindow>(Schedule);

        // Strip mutual exact matches first.
        for (var i = remoteSet.Count - 1; i >= 0; i--)
        {
            var match = localSet.FindIndex(x => SameWindow(x, remoteSet[i]));
            if (match >= 0)
            {
                remoteSet.RemoveAt(i);
                localSet.RemoveAt(match);
            }
        }

        // Pair leftovers with same Days greedily as modifies.
        var modifies = new List<(ScheduleWindow, ScheduleWindow)>();
        for (var i = remoteSet.Count - 1; i >= 0; i--)
        {
            var oldW = remoteSet[i];
            var bestJ = -1;
            var bestScore = int.MaxValue;
            for (var j = 0; j < localSet.Count; j++)
            {
                if (localSet[j].Days != oldW.Days) continue;
                var score = Math.Abs(localSet[j].StartMin - oldW.StartMin)
                          + Math.Abs(localSet[j].EndMin   - oldW.EndMin);
                if (score < bestScore) { bestScore = score; bestJ = j; }
            }
            if (bestJ >= 0)
            {
                modifies.Add((oldW, localSet[bestJ]));
                localSet.RemoveAt(bestJ);
                remoteSet.RemoveAt(i);
            }
        }

        var adds    = new List<ScheduleWindow>(localSet);
        var removes = new List<ScheduleWindow>(remoteSet);
        return (adds, removes, modifies);
    }

    private static bool SameWindow(ScheduleWindow a, ScheduleWindow b) =>
        a.Days == b.Days && a.StartMin == b.StartMin && a.EndMin == b.EndMin;

    private static List<ScheduleWindow> SortedClone(IEnumerable<ScheduleWindow> windows) =>
        windows
            .OrderBy(w => w.Days, StringComparer.Ordinal)
            .ThenBy(w => w.StartMin)
            .ThenBy(w => w.EndMin)
            .ToList();

    private static string DescribeWindow(ScheduleWindow w)
    {
        var range = $"{ScheduleSerializer.FormatHhmm(w.StartMin)}-{ScheduleSerializer.FormatHhmm(w.EndMin)}";
        return w.Days == "*" ? range : $"{range} ({w.Days})";
    }
}
