using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
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
    // DM10: raised from 500 -> 2000 to absorb dnsmasq query volume without evicting
    // control-plane (block / override / install) events when the DNS filter is ON.
    private const int MaxEntries = 2000;

    private static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    };

    // DM10 spike. Matches the dnsmasq log-queries=extra line shape, e.g.
    //   Jun  4 19:14:32 EdgeRouter dnsmasq[1234]: query[A] youtube.com from 192.168.200.176
    // We only emit `<ip> -> <host>` to the UI -- query-type + dnsmasq pid are noise
    // when scanning the strip visually.
    private static readonly Regex DnsQueryRx = new(
        @"dnsmasq\[\d+\]:\s+query\[\w+\]\s+(?<host>\S+)\s+from\s+(?<ip>\S+)",
        RegexOptions.Compiled);

    private readonly RouterConfig _config;
    private readonly Dispatcher _dispatcher;
    private readonly object _lifecycleLock = new();

    private RouterClient? _client;
    private RouterClient? _dnsClient;
    private CancellationTokenSource? _cts;
    private Task? _consumerTask;
    private Task? _dnsConsumerTask;

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
    // DM10: default OFF so DNS volume only surfaces when the parent opts in.
    [ObservableProperty] private bool _showDns          = false;

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

    // Start the background consumers if not already running. Idempotent.
    // DM10: a second consumer streams dnsmasq query lines; both consumers share
    // the cancellation token but reconnect independently so a DNS-stream blip
    // doesn't reset the kidblock.log stream's backoff (or vice versa).
    public void Start()
    {
        lock (_lifecycleLock)
        {
            var ctlRunning = _consumerTask    is { IsCompleted: false };
            var dnsRunning = _dnsConsumerTask is { IsCompleted: false };
            if (ctlRunning && dnsRunning) return;
            // Both dead -> fresh CTS (handles Pause->Start). Either-alive -> keep the
            // shared CTS so the survivor's token doesn't change underneath it.
            if (!ctlRunning && !dnsRunning) _cts = new CancellationTokenSource();
            var token = _cts!.Token;
            if (!ctlRunning) _consumerTask    = Task.Run(() => RunLoopAsync(token));
            if (!dnsRunning) _dnsConsumerTask = Task.Run(() => RunDnsLoopAsync(token));
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
            LogKind.Dns          => ShowDns,
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

    // DM10 spike. Independent reconnect-with-backoff loop for the dnsmasq query
    // stream -- runs on a second SSH connection so a transient DNS-stream blip
    // doesn't reset the kidblock.log stream's backoff (or vice versa).
    // Status text for this stream is suppressed (StatusText is owned by the
    // control-plane loop; chattering it on every DNS reconnect would noise the
    // primary signal). DNS-stream failures surface only via row absence + the
    // SetStatus call below on terminal disconnect.
    private async Task RunDnsLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _dnsClient?.Dispose();
                _dnsClient = new RouterClient(_config);
                await _dnsClient.ConnectAsync(ct).ConfigureAwait(false);
                attempt = 0;

                await foreach (var line in _dnsClient.StreamDnsQueriesAsync(ct).ConfigureAwait(false))
                {
                    var entry = ParseDnsLine(line);
                    if (entry is not null) AppendOnUi(entry);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                var idx = Math.Min(attempt, BackoffSchedule.Length - 1);
                var delay = BackoffSchedule[idx];
                attempt++;
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { _dnsClient?.Dispose(); } catch { /* ignore */ }
        _dnsClient = null;
    }

    private static LogEntry? ParseDnsLine(string line)
    {
        var m = DnsQueryRx.Match(line);
        if (!m.Success) return null;
        var host = m.Groups["host"].Value;
        var ip = m.Groups["ip"].Value;
        return new LogEntry(DateTimeOffset.Now, LogKind.Dns, $"{ip} -> {host}");
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
