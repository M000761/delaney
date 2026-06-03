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
    public ObservableCollection<ScheduleWindow> Schedule { get; } = new();
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

            var dhcpTask  = _client.GetDhcpLeasesAsync(ct);
            var macsTask  = _client.GetConfigFileAsync(_config.MacConfPath, ct);
            var schedTask = _client.GetConfigFileAsync(_config.ScheduleConfPath, ct);
            var domTask   = _client.GetConfigFileAsync(_config.DomainsConfPath, ct);

            await Task.WhenAll(dhcpTask, macsTask, schedTask, domTask).ConfigureAwait(true);

            var leases  = await dhcpTask.ConfigureAwait(true);
            var macs    = ConfigParser.ParseMacs(await macsTask.ConfigureAwait(true));
            var windows = ConfigParser.ParseSchedule(await schedTask.ConfigureAwait(true));
            var domains = ConfigParser.ParseDomains(await domTask.ConfigureAwait(true));

            var leaseByMac = new Dictionary<string, DhcpLease>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in leases) leaseByMac[l.Mac] = l;

            Devices.Clear();
            foreach (var d in macs)
            {
                leaseByMac.TryGetValue(d.Mac, out var l);
                Devices.Add(d with { Ip = l?.Ip, LastDhcp = l?.Expiry });
            }

            Schedule.Clear();
            foreach (var w in windows) Schedule.Add(w);

            Domains.Clear();
            foreach (var d in domains) Domains.Add(d);

            LastRefresh = System.DateTimeOffset.Now;
            StatusLabel = $"Last refresh {LastRefresh:HH:mm:ss} -- " +
                          $"{Devices.Count} devices / {Schedule.Count} windows / {Domains.Count} domains";
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
}
