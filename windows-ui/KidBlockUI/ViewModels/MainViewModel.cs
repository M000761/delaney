using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KidBlockUI.Models;
using KidBlockUI.Services;

namespace KidBlockUI.ViewModels;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Stale,
    Error,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly RouterConfig _config;
    private RouterClient? _client;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();
    public ScheduleViewModel Schedule { get; } = new();
    public ObservableCollection<DomainEntry> Domains { get; } = new();

    [ObservableProperty]
    private ConnectionState _connectionStatus = ConnectionState.Disconnected;

    [ObservableProperty]
    private System.DateTimeOffset? _lastRefresh;

    [ObservableProperty]
    private string _statusLabel = "Disconnected";

    [ObservableProperty]
    private string _routerLabel = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    // Registered by the View at startup. Returns true if the user confirms.
    // (title, body) -> bool. Kept off the row VMs so they stay UI-thread-agnostic.
    public Func<string, string, bool>? KillConfirm { get; set; }

    public MainViewModel(RouterConfig config)
    {
        _config = config;
        RouterLabel = $"{config.User}@{config.Host}";
    }

    private void ApplyRouterOverrideState(RouterState state)
    {
        var mode = string.IsNullOrEmpty(state.OverrideMode) ? null : state.OverrideMode;
        foreach (var row in Devices) row.UpdateOverrideState(mode, state.OverrideExpires);
    }

    private async Task RefreshOverrideStateOnlyAsync(CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return;
        var state = await _client.GetStatusAsync(ct).ConfigureAwait(true);
        ApplyRouterOverrideState(state);
        Schedule.OverrideActive = !string.IsNullOrEmpty(state.OverrideMode);
    }

    public async Task OverrideBlockFromRowAsync(DeviceRowViewModel row, int minutes, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) { row.ShowToast("Not connected"); return; }
        var name = string.IsNullOrWhiteSpace(row.Name) ? row.Mac : row.Name;
        var confirm = KillConfirm;
        if (confirm is null) return;
        var body =
            $"Block ALL controlled devices for up to 24 hours.\n" +
            $"(Triggered from row: {name})\n" +
            $"\n" +
            $"Schedule's next tick is still honored once the override expires. " +
            $"Click Clear to release sooner.";
        if (!confirm("Kill internet for controlled devices?", body)) return;

        row.IsBusy = true;
        try
        {
            await _client.OverrideBlockAsync(minutes, ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
            var until = row.OverrideExpires?.LocalDateTime.ToString("HH:mm") ?? "--:--";
            row.ShowToast(minutes >= 1440 ? $"KILL applied (until {until})" : $"Blocked until {until}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { row.ShowToast($"KILL failed: {ex.Message}"); }
        finally { row.IsBusy = false; }
    }

    public async Task OverrideAllowFromRowAsync(DeviceRowViewModel row, int minutes, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) { row.ShowToast("Not connected"); return; }
        row.IsBusy = true;
        try
        {
            await _client.OverrideAllowAsync(minutes, ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
            var until = row.OverrideExpires?.LocalDateTime.ToString("HH:mm") ?? "--:--";
            row.ShowToast($"Allowed until {until}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { row.ShowToast($"Allow failed: {ex.Message}"); }
        finally { row.IsBusy = false; }
    }

    public async Task ClearOverrideFromRowAsync(DeviceRowViewModel row, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) { row.ShowToast("Not connected"); return; }
        row.IsBusy = true;
        try
        {
            await _client.ClearOverrideAsync(ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
            row.ShowToast("Override cleared");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { row.ShowToast($"Clear failed: {ex.Message}"); }
        finally { row.IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct)
    {
        ErrorMessage = null;
        try
        {
            if (_client is null || !_client.IsConnected)
            {
                _client?.Dispose();
                _client = new RouterClient(_config);
                ConnectionStatus = ConnectionState.Connecting;
                await _client.ConnectAsync(ct).ConfigureAwait(true);
            }
            ConnectionStatus = ConnectionState.Connected;

            var dhcpTask   = _client.GetDhcpLeasesAsync(ct);
            var macsTask   = _client.GetConfigFileAsync(_config.MacConfPath, ct);
            var schedTask  = _client.GetConfigFileAsync(_config.ScheduleConfPath, ct);
            var domTask    = _client.GetConfigFileAsync(_config.DomainsConfPath, ct);
            var statusTask = _client.GetStatusAsync(ct);

            await Task.WhenAll(dhcpTask, macsTask, schedTask, domTask, statusTask).ConfigureAwait(true);

            var leases  = await dhcpTask.ConfigureAwait(true);
            var macs    = ConfigParser.ParseMacs(await macsTask.ConfigureAwait(true));
            var windows = ConfigParser.ParseSchedule(await schedTask.ConfigureAwait(true));
            var domains = ConfigParser.ParseDomains(await domTask.ConfigureAwait(true));
            var state   = await statusTask.ConfigureAwait(true);

            var leaseByMac = new Dictionary<string, DhcpLease>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in leases) leaseByMac[l.Mac] = l;

            // Preserve any in-flight per-row state (e.g. last-action toast) when re-keying by MAC.
            var existing = Devices.ToDictionary(r => r.Mac, StringComparer.OrdinalIgnoreCase);
            Devices.Clear();
            foreach (var d in macs)
            {
                leaseByMac.TryGetValue(d.Mac, out var l);
                var enriched = d with { Ip = l?.Ip, LastDhcp = l?.Expiry };
                if (existing.TryGetValue(d.Mac, out var prior))
                {
                    prior.UpdateLease(enriched.Ip, enriched.LastDhcp);
                    Devices.Add(prior);
                }
                else
                {
                    Devices.Add(new DeviceRowViewModel(this, enriched));
                }
            }

            ApplyRouterOverrideState(state);

            Schedule.LoadFromRouter(windows);
            Schedule.OverrideActive = !string.IsNullOrEmpty(state.OverrideMode);

            Domains.Clear();
            foreach (var d in domains) Domains.Add(d);

            LastRefresh = System.DateTimeOffset.Now;
            StatusLabel = $"Last refresh {LastRefresh:HH:mm:ss} -- " +
                          $"{Devices.Count} devices / {Schedule.Schedule.Count} windows / {Domains.Count} domains";
        }
        catch (OperationCanceledException)
        {
            // user-cancelled; leave UI as-is
        }
        catch (Exception ex)
        {
            ConnectionStatus = LastRefresh.HasValue ? ConnectionState.Stale : ConnectionState.Error;
            ErrorMessage = ex.Message;
            StatusLabel = LastRefresh.HasValue
                ? $"Stale ({(System.DateTimeOffset.Now - LastRefresh.Value):hh\\:mm}) -- {ex.Message}"
                : $"Disconnected -- {ex.Message}";
            try { _client?.Dispose(); } catch { /* ignore */ }
            _client = null;
        }
    }

    public async Task ApplyScheduleAsync(Func<IReadOnlyList<DiffLine>, bool> confirm, CancellationToken ct = default)
    {
        if (!Schedule.HasPendingChanges) return;
        if (_client is null || !_client.IsConnected)
        {
            Schedule.ApplyError = "Not connected. Click Refresh first.";
            return;
        }
        var diff = Schedule.ComputeDiff();
        if (diff.Count == 0) return;
        if (!confirm(diff)) return;

        Schedule.ApplyError = null;
        Schedule.IsApplying = true;
        try
        {
            var content = Schedule.SerializeEdited();
            await _client.WriteConfigFileAsync(_config.ScheduleConfPath, content, ct).ConfigureAwait(true);
            await _client.ReapplyAsync(ct).ConfigureAwait(true);

            // Poll-verify within 5s: read remote conf, parse, compare against EditedSchedule.
            // The first read after reapply may race the on-disk move on slow routers; retry up to 5s.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var verified = false;
            string? lastReadText = null;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                lastReadText = await _client.GetConfigFileAsync(_config.ScheduleConfPath, ct).ConfigureAwait(true);
                var remoteParsed = ConfigParser.ParseSchedule(lastReadText);
                if (Schedule.MatchesRouter(remoteParsed)) { verified = true; break; }
                await Task.Delay(500, ct).ConfigureAwait(true);
            }

            if (verified)
            {
                Schedule.AcceptAsRouterState();
            }
            else
            {
                Schedule.ApplyError =
                    "Schedule pushed but post-verify mismatched router state. " +
                    "Edited view restored to last-known router state. Click Refresh to reload.";
                Schedule.Discard();
            }
        }
        catch (OperationCanceledException)
        {
            // user-cancelled
        }
        catch (Exception ex)
        {
            Schedule.ApplyError = $"Apply failed: {ex.Message}";
        }
        finally
        {
            Schedule.IsApplying = false;
        }
    }
}
