using System.Collections.ObjectModel;
using System.Windows.Threading;
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
    public DomainsViewModel Domains { get; } = new();
    public LogTailViewModel LogTail { get; }

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

    [ObservableProperty]
    private DeviceRowViewModel? _selectedDevice;

    // DM9: per-MAC override summary text bound to the BulkActionBar
    // ("Overrides active: N of M devices"). Updated alongside per-row state.
    [ObservableProperty]
    private string _overridesSummary = "Overrides active: 0 of 0 devices";

    [ObservableProperty]
    private bool _hasActiveOverrides;

    // Cache of the most-recently-read kidblock-macs.conf text -- needed to round-trip
    // a Mode toggle without losing label / formatting on rows we didn't touch.
    private string _lastMacsConfText = string.Empty;

    // Registered by the View at startup. Returns true if the user confirms.
    // (title, body) -> bool. Kept off the row VMs so they stay UI-thread-agnostic.
    public Func<string, string, bool>? KillConfirm { get; set; }

    partial void OnSelectedDeviceChanged(DeviceRowViewModel? value)
    {
        // Pivot the Domains pane to reflect the selected device's mode. Null = no
        // selection (e.g. after Refresh briefly drops it); leave the pane alone so
        // the badge / pending count don't strobe.
        if (value is null) return;
        Domains.CurrentMode = value.Mode;
    }

    public MainViewModel(RouterConfig config)
    {
        _config = config;
        RouterLabel = $"{config.User}@{config.Host}";
        // LogTail owns its own RouterClient + SSH session so reconnect-with-backoff
        // is independent of the main connection's lifecycle (Refresh, Apply, etc.).
        LogTail = new LogTailViewModel(config, Dispatcher.CurrentDispatcher);
    }

    // DM9: project the per-MAC override dict onto each row + recompute the
    // BulkActionBar summary text. Rows whose MAC isn't in the dict are cleared
    // to NONE; rows whose MAC is present pick up that entry's verb + expiry.
    private void ApplyOverrideState(IReadOnlyDictionary<string, OverrideEntry> byMac)
    {
        var active = 0;
        foreach (var row in Devices)
        {
            if (byMac.TryGetValue(row.Mac.ToLowerInvariant(), out var entry))
            {
                row.UpdateOverrideState(entry.Verb == OverrideVerb.Block ? "block" : "allow", entry.ExpiryUtc.ToLocalTime());
                active++;
            }
            else
            {
                row.UpdateOverrideState(null, null);
            }
        }
        OverridesSummary = $"Overrides active: {active} of {Devices.Count} devices";
        HasActiveOverrides = active > 0;
        Schedule.OverrideActive = active > 0;
    }

    private async Task RefreshOverrideStateOnlyAsync(CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return;
        var byMac = await _client.GetOverrideStateAsync(ct).ConfigureAwait(true);
        ApplyOverrideState(byMac);
    }

    public async Task OverrideBlockFromRowAsync(DeviceRowViewModel row, int minutes, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) { row.ShowToast("Not connected"); return; }
        var name = string.IsNullOrWhiteSpace(row.Name) ? row.Mac : row.Name;
        var confirm = KillConfirm;
        if (confirm is null) return;
        var body =
            $"Block {name} for up to 24 hours.\n" +
            $"This device only -- other controlled devices keep their current state.\n" +
            $"\n" +
            $"Schedule's next tick is still honored once the override expires. " +
            $"Click Clear to release sooner.";
        if (!confirm($"Kill internet for {name}?", body)) return;

        row.IsBusy = true;
        try
        {
            await _client.OverrideBlockAsync(row.Mac, minutes, ct).ConfigureAwait(true);
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
            await _client.OverrideAllowAsync(row.Mac, minutes, ct).ConfigureAwait(true);
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
            await _client.ClearOverrideAsync(row.Mac, ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
            row.ShowToast("Override cleared");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { row.ShowToast($"Clear failed: {ex.Message}"); }
        finally { row.IsBusy = false; }
    }

    // BulkActionBar (DM9). Wraps the router-side --all primitive so the bedtime
    // (lock-everything) use case has an honest affordance separate from per-row.
    // KILL ALL is confirm-gated (24h max ceiling); ALLOW 30m and CLEAR ALL are
    // not (they're recoverable in one click).
    [RelayCommand]
    private async Task BulkKillAsync(CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return;
        var count = Devices.Count;
        if (count == 0) return;
        var confirm = KillConfirm;
        if (confirm is null) return;
        var body =
            $"Block ALL {count} controlled devices for up to 24 hours.\n" +
            $"\n" +
            $"Per-device schedule is still honored once the override expires. " +
            $"Click CLEAR ALL to release sooner.";
        if (!confirm($"Kill internet for ALL {count} controlled devices?", body)) return;
        try
        {
            await _client.OverrideBlockAllAsync(1440, ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = $"KILL ALL failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task BulkAllowAsync(CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return;
        if (Devices.Count == 0) return;
        try
        {
            await _client.OverrideAllowAllAsync(30, ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = $"ALLOW 30m ALL failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task BulkClearAsync(CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) return;
        try
        {
            await _client.ClearAllOverridesAsync(ct).ConfigureAwait(true);
            await RefreshOverrideStateOnlyAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorMessage = $"CLEAR ALL failed: {ex.Message}"; }
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

            var dhcpTask    = _client.GetDhcpLeasesAsync(ct);
            var macsTask    = _client.GetConfigFileAsync(_config.MacConfPath, ct);
            var schedTask   = _client.GetConfigFileAsync(_config.ScheduleConfPath, ct);
            var domTask     = _client.GetConfigFileAsync(_config.DomainsConfPath, ct);
            // Allowlist read is best-effort -- the file may legitimately not exist
            // (whitelist mode never installed). Empty body parses to zero domains.
            var allowTask   = SafeGetAsync(_client, _config.AllowlistConfPath, ct);
            var overridesTask = _client.GetOverrideStateAsync(ct);

            await Task.WhenAll(dhcpTask, macsTask, schedTask, domTask, allowTask, overridesTask).ConfigureAwait(true);

            var leases       = await dhcpTask.ConfigureAwait(true);
            var macsText     = await macsTask.ConfigureAwait(true);
            _lastMacsConfText = macsText;
            var macs         = ConfigParser.ParseMacs(macsText);
            var windows      = ConfigParser.ParseSchedule(await schedTask.ConfigureAwait(true));
            var domains      = ConfigParser.ParseDomains(await domTask.ConfigureAwait(true));
            var allowDomains = ConfigParser.ParseDomains(await allowTask.ConfigureAwait(true));
            var overridesByMac = await overridesTask.ConfigureAwait(true);

            var leaseByMac = new Dictionary<string, DhcpLease>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in leases) leaseByMac[l.Mac] = l;

            // Preserve any in-flight per-row state (e.g. last-action toast) when re-keying by MAC.
            var existing = Devices.ToDictionary(r => r.Mac, StringComparer.OrdinalIgnoreCase);
            var priorSelectedMac = SelectedDevice?.Mac;
            Devices.Clear();
            foreach (var d in macs)
            {
                leaseByMac.TryGetValue(d.Mac, out var l);
                var enriched = d with { Ip = l?.Ip, LastDhcp = l?.Expiry };
                if (existing.TryGetValue(d.Mac, out var prior))
                {
                    prior.UpdateFromConfig(enriched);
                    Devices.Add(prior);
                }
                else
                {
                    Devices.Add(new DeviceRowViewModel(this, enriched));
                }
            }

            ApplyOverrideState(overridesByMac);

            Schedule.LoadFromRouter(windows);

            Domains.LoadBlocklistFromRouter(domains);
            Domains.LoadAllowlistFromRouter(allowDomains);

            // Restore selection by MAC (DataGrid drops SelectedItem when ItemsSource
            // is reset). Then either the user's prior selection holds, or default to
            // the first row so the Domains pane always reflects something coherent.
            if (priorSelectedMac is not null)
                SelectedDevice = Devices.FirstOrDefault(r => string.Equals(r.Mac, priorSelectedMac, StringComparison.OrdinalIgnoreCase))
                                 ?? Devices.FirstOrDefault();
            else
                SelectedDevice ??= Devices.FirstOrDefault();

            LastRefresh = System.DateTimeOffset.Now;
            var allowCount = allowDomains.Count;
            StatusLabel = $"Last refresh {LastRefresh:HH:mm:ss} -- " +
                          $"{Devices.Count} devices / {Schedule.Schedule.Count} windows / " +
                          $"{domains.Count} blocked / {allowCount} allowed";
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

    public async Task ApplyDomainsAsync(Func<IReadOnlyList<DiffLine>, bool> confirm, CancellationToken ct = default)
    {
        if (!Domains.HasPendingChanges) return;
        if (_client is null || !_client.IsConnected)
        {
            Domains.ApplyError = "Not connected. Click Refresh first.";
            return;
        }
        var diff = Domains.ComputeDiff();
        if (diff.Count == 0) return;
        if (!confirm(diff)) return;

        Domains.ApplyError = null;
        Domains.IsApplying = true;
        try
        {
            var content = Domains.SerializeEdited();
            var isWhitelist = Domains.CurrentMode == DeviceMode.Whitelist;
            var targetPath = isWhitelist ? _config.AllowlistConfPath : _config.DomainsConfPath;

            await _client.WriteConfigFileAsync(targetPath, content, ct).ConfigureAwait(true);
            if (isWhitelist)
                await _client.InstallAllowlistAsync(ct).ConfigureAwait(true);
            else
                await _client.InstallDomainsAsync(ct).ConfigureAwait(true);

            // Poll-verify within 5s: read back, parse, compare against EditedDomains.
            // install-* restarts dnsmasq so allow a wider retry window than schedule's reapply.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var verified = false;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                var text = await _client.GetConfigFileAsync(targetPath, ct).ConfigureAwait(true);
                var remoteParsed = ConfigParser.ParseDomains(text);
                if (Domains.MatchesRouter(remoteParsed)) { verified = true; break; }
                await Task.Delay(500, ct).ConfigureAwait(true);
            }

            if (verified)
            {
                Domains.AcceptAsRouterState();
            }
            else
            {
                var verb = isWhitelist ? "Allowlist" : "Domains";
                Domains.ApplyError =
                    $"{verb} pushed but post-verify mismatched router state. " +
                    "Edited view restored to last-known router state. Click Refresh to reload.";
                Domains.Discard();
            }
        }
        catch (OperationCanceledException)
        {
            // user-cancelled
        }
        catch (Exception ex)
        {
            Domains.ApplyError = $"Apply failed: {ex.Message}";
        }
        finally
        {
            Domains.IsApplying = false;
        }
    }

    public async Task ToggleDeviceModeAsync(DeviceRowViewModel row, CancellationToken ct)
    {
        if (_client is null || !_client.IsConnected) { row.ShowToast("Not connected"); return; }
        row.IsBusy = true;
        var oldMode = row.Mode;
        var newMode = oldMode == DeviceMode.Whitelist ? DeviceMode.Blocklist : DeviceMode.Whitelist;
        try
        {
            // Pivot just THIS device's Mode -- keep every other device's row untouched.
            var asDevices = Devices
                .Select(r => r.Mac.Equals(row.Mac, StringComparison.OrdinalIgnoreCase)
                    ? r.Device with { Mode = newMode }
                    : r.Device)
                .ToList();
            var newConf = ConfigParser.SerializeMacs(_lastMacsConfText, asDevices);
            await _client.WriteConfigFileAsync(_config.MacConfPath, newConf, ct).ConfigureAwait(true);
            _lastMacsConfText = newConf;

            // Re-apply iptables -- whitelist rules get rebuilt on the new MAC partitioning.
            await _client.ReapplyAsync(ct).ConfigureAwait(true);

            // Local round-trip: update the row's Device.Mode + repaint the pane if this
            // is the selected row.
            row.UpdateFromConfig(row.Device with { Mode = newMode });
            if (ReferenceEquals(SelectedDevice, row)) Domains.CurrentMode = newMode;

            row.ShowToast(newMode == DeviceMode.Whitelist ? "Mode -> Whitelist" : "Mode -> Blocklist");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            row.ShowToast($"Mode toggle failed: {ex.Message}");
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private static async Task<string> SafeGetAsync(RouterClient client, string path, CancellationToken ct)
    {
        try { return await client.GetConfigFileAsync(path, ct).ConfigureAwait(false); }
        catch { return string.Empty; }
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
