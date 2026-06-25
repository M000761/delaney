using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

// DM22: backs WhyBlockedDialog. Owns its OWN RouterClient + SSH session (like
// LogTailViewModel) so the 5s auto-refresh is independent of the main connection
// and a dialog opened-then-closed tears its session down via Stop() -> no hanging
// ssh. Calls RouterClient.ExplainMacAsync (read-only on the router) and projects
// the verdict + 3-chain walk + recent log onto bindable state.
public sealed partial class WhyBlockedViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly RouterConfig _config;
    private readonly string _mac;

    private RouterClient? _client;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private bool _refreshing;

    public string Mac => _mac;
    public string DeviceName { get; }
    public string Title { get; }

    [ObservableProperty] private bool _hasVerdict;
    [ObservableProperty] private bool _isAllowed;
    [ObservableProperty] private string _headerText = "Checking...";
    [ObservableProperty] private string _reasonText = "Querying the router for this device's effective verdict...";
    [ObservableProperty] private string _statusText = "Connecting...";
    [ObservableProperty] private string _recentLogText = string.Empty;

    public ObservableCollection<WhyChainRow> Chains { get; } = new();

    public WhyBlockedViewModel(RouterConfig config, string mac, string? deviceName)
    {
        _config = config;
        _mac = mac;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? mac : deviceName!;
        Title = $"Why? -- {DeviceName} ({mac})";
    }

    // Begin the auto-refresh lifecycle: immediate first query + a 5s repeating timer.
    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    private async void OnTick(object? sender, EventArgs e) => await RefreshAsync();

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;
        _refreshing = true;
        var ct = cts.Token;
        try
        {
            if (_client is null || !_client.IsConnected)
            {
                _client?.Dispose();
                _client = new RouterClient(_config);
                StatusText = "Connecting...";
                await _client.ConnectAsync(ct).ConfigureAwait(true);
            }

            var verdict = await _client.ExplainMacAsync(_mac, null, ct).ConfigureAwait(true);
            ApplyVerdict(verdict);
            StatusText = $"Updated {DateTimeOffset.Now:HH:mm:ss}  -  auto-refresh every 5s";
        }
        catch (OperationCanceledException)
        {
            // dialog closing; leave state as-is
        }
        catch (Exception ex)
        {
            StatusText = $"explain-mac failed: {ex.Message}";
            try { _client?.Dispose(); } catch { /* ignore */ }
            _client = null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyVerdict(MacVerdict v)
    {
        IsAllowed = v.IsAllowed;
        HeaderText = v.IsAllowed ? "ALLOWED NOW" : $"BLOCKED: {v.VerdictReason}";

        var chain = string.IsNullOrEmpty(v.VerdictChain) ? string.Empty : $" by {v.VerdictChain}";
        var detail = string.IsNullOrEmpty(v.VerdictDetail) ? string.Empty : $" ({v.VerdictDetail})";
        ReasonText = $"{v.VerdictReason}{chain}{detail}".Trim();

        Chains.Clear();
        foreach (var c in v.Chains ?? Enumerable.Empty<MacVerdictChain>())
            Chains.Add(new WhyChainRow(c));

        RecentLogText = v.RecentLog is { Count: > 0 }
            ? string.Join("\n", v.RecentLog)
            : "(no recent kidblock.log entries for this MAC)";

        HasVerdict = true;
    }

    // Tear the lifecycle down: stop the timer, cancel any in-flight ssh, dispose the
    // session. Idempotent; called from the dialog's Closed handler and Dispose().
    public void Stop()
    {
        try { if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; } } catch { /* ignore */ }
        _timer = null;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _client?.Dispose(); } catch { /* ignore */ }
        _client = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    public void Dispose() => Stop();
}

// One row of the 3-chain walk in WhyBlockedDialog. Carries display strings + a
// verdict-aware colour-coded dot resolved from the Theme.xaml semantic tokens
// (green allow / red block / muted not-present) so the dialog stays token-driven.
public sealed class WhyChainRow
{
    public string Name { get; }
    public string PresentText { get; }
    public string RuleTypeText { get; }
    public string CountersText { get; }
    public string IpsetText { get; }
    public Brush DotBrush { get; }

    public WhyChainRow(MacVerdictChain c)
    {
        Name = c.Name;
        PresentText = c.MacRulePresent ? "MAC rule present: yes" : "MAC rule present: no";
        RuleTypeText = c.MacRulePresent && !string.IsNullOrEmpty(c.RuleType) ? c.RuleType! : string.Empty;
        CountersText = c.MacRulePresent ? $"pkts={c.Pkts}  bytes={c.Bytes}" : string.Empty;
        IpsetText = string.IsNullOrEmpty(c.IpsetHint) ? string.Empty : $"ipset: {c.IpsetHint}";
        DotBrush = ResolveDot(c);
    }

    private static Brush ResolveDot(MacVerdictChain c)
    {
        if (!c.MacRulePresent) return Token("MutedText");
        return (c.RuleType ?? string.Empty) switch
        {
            "override-allow" or "domain-allow" => Token("LogKindAllow"),
            "override-block" or "schedule-block" or "domain-block" or "whitelist-default-drop"
                => Token("StatusErr"),
            _ => Token("MutedText"),
        };
    }

    private static Brush Token(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
