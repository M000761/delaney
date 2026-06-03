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

    public ObservableCollection<Device> Devices { get; } = new();
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

    public MainViewModel(RouterConfig config)
    {
        _config = config;
        RouterLabel = $"{config.User}@{config.Host}";
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

            Devices.Clear();
            foreach (var d in macs)
            {
                leaseByMac.TryGetValue(d.Mac, out var l);
                Devices.Add(d with { Ip = l?.Ip, LastDhcp = l?.Expiry });
            }

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
