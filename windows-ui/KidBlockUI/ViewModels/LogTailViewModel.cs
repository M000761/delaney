using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

public sealed partial class LogTailViewModel : ObservableObject, IDisposable
{
    private const int MaxEntries = 500;

    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    };

    private readonly RouterConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly object _lifecycleLock = new();

    private RouterClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ICollectionView View { get; }

    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty] private bool _showBlock        = true;
    [ObservableProperty] private bool _showAllow        = true;
    [ObservableProperty] private bool _showOverride     = true;
    [ObservableProperty] private bool _showScheduleTick = true;
    [ObservableProperty] private bool _showInstall      = true;
    [ObservableProperty] private bool _showError        = true;
    [ObservableProperty] private bool _showOther        = true;

    public LogTailViewModel(RouterConfig config, Dispatcher dispatcher)
    {
        _config = config;
        _dispatcher = dispatcher;

        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = FilterEntry;

        // The Show* checkboxes HIDE entries from the view.
        // SearchText drives a per-row highlight (acceptance (g) -- matching entries stay
        // visible, just visually marked) so it's bound at the row level via a multi-value
        // converter; the view itself doesn't refresh on search-text changes.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null) return;
            if (e.PropertyName.StartsWith("Show", StringComparison.Ordinal))
                View.Refresh();
        };
    }

    public bool IsRunning
    {
        get { lock (_lifecycleLock) return _consumerTask is { IsCompleted: false }; }
    }

    // Start the background consumer if not already running. Idempotent.
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_consumerTask is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _consumerTask = Task.Run(() => RunLoopAsync(token));
        }
    }

    // Cancel the background consumer; UI thread returns immediately. Safe to call repeatedly.
    public void Pause()
    {
        lock (_lifecycleLock)
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
        }
        // Don't await _consumerTask here -- callers may invoke from a UI handler where
        // a synchronous join would deadlock the dispatcher. RunLoopAsync exits within
        // ~500ms of cancellation (the ShellStream ReadLine timeout).
        SetStatus("Paused");
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private bool FilterEntry(object o)
    {
        if (o is not LogEntry e) return false;

        return e.Kind switch
        {
            LogKind.Block        => ShowBlock,
            LogKind.Allow        => ShowAllow,
            LogKind.Override     => ShowOverride,
            LogKind.ScheduleTick => ShowScheduleTick,
            LogKind.Install      => ShowInstall,
            LogKind.Error        => ShowError,
            LogKind.Other        => ShowOther,
            _                    => true,
        };
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus("Connecting...");
                _client?.Dispose();
                _client = new RouterClient(_config);
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                SetStatus("Streaming /var/log/kidblock.log");
                attempt = 0;

                await foreach (var line in _client.StreamLogAsync(ct).ConfigureAwait(false))
                {
                    var entry = LogEntry.Parse(line);
                    AppendOnUi(entry);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var idx = Math.Min(attempt, BackoffSchedule.Length - 1);
                var delay = BackoffSchedule[idx];
                SetStatus($"[disconnected: {ex.Message}, reconnecting in {(int)delay.TotalSeconds}s...]");
                attempt++;
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { _client?.Dispose(); } catch { /* ignore */ }
        _client = null;
    }

    private void AppendOnUi(LogEntry entry)
    {
        _dispatcher.Invoke(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        });
    }

    private void SetStatus(string text)
    {
        if (_dispatcher.CheckAccess()) StatusText = text;
        else _dispatcher.Invoke(() => StatusText = text);
    }

    public void Dispose()
    {
        Pause();
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }
}
