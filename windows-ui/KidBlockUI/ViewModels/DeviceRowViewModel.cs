using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KidBlockUI.Models;

namespace KidBlockUI.ViewModels;

// Per-device row backing the Devices DataGrid. Wraps a Device record + carries:
//   - the router-wide override snapshot (every row reflects the same state -- kidblock.sh
//     override is router-wide across the controlled-devices set, not per-MAC),
//   - per-row RelayCommands for KILL / Allow-30m / Clear,
//   - a transient last-action toast string with a 5-second auto-clear.
public sealed partial class DeviceRowViewModel : ObservableObject
{
    private readonly MainViewModel _parent;
    private CancellationTokenSource? _toastCts;

    public DeviceRowViewModel(MainViewModel parent, Device device)
    {
        _parent = parent;
        _device = device;
    }

    [ObservableProperty]
    private Device _device;

    public string Name => Device.Name;
    public string Mac => Device.Mac;
    public string? Ip => Device.Ip;
    public System.DateTimeOffset? LastDhcp => Device.LastDhcp;
    public DeviceMode Mode => Device.Mode;
    public bool IsWhitelist => Device.Mode == DeviceMode.Whitelist;
    public string ModeLabel => Device.Mode == DeviceMode.Whitelist ? "Whitelist" : "Blocklist";

    // Router-wide override snapshot, projected onto every row.
    [ObservableProperty]
    private string? _overrideMode;       // "block", "allow", or null

    [ObservableProperty]
    private System.DateTimeOffset? _overrideExpires;

    [ObservableProperty]
    private string? _lastActionText;

    [ObservableProperty]
    private bool _isBusy;

    public string OverrideBadgeText
    {
        get
        {
            if (string.IsNullOrEmpty(OverrideMode)) return "NONE";
            var until = OverrideExpires?.LocalDateTime.ToString("HH:mm") ?? "??:??";
            return OverrideMode switch
            {
                "block" => OverrideExpires.HasValue && (OverrideExpires.Value - System.DateTimeOffset.Now).TotalMinutes >= 1439
                    ? "INDEFINITE-BLOCKED"
                    : $"BLOCKED-until-{until}",
                "allow" => $"ALLOWED-until-{until}",
                _       => OverrideMode.ToUpperInvariant(),
            };
        }
    }

    public Brush OverrideBadgeBackground => OverrideMode switch
    {
        "block" when OverrideExpires.HasValue && (OverrideExpires.Value - System.DateTimeOffset.Now).TotalMinutes >= 1439
            => new SolidColorBrush(Color.FromRgb(0x70, 0x10, 0x10)),   // dark-red (indefinite)
        "block" => new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),  // red
        "allow" => new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0x44)),  // green
        _       => new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5A)),  // gray (NONE)
    };

    partial void OnOverrideModeChanged(string? value)
    {
        OnPropertyChanged(nameof(OverrideBadgeText));
        OnPropertyChanged(nameof(OverrideBadgeBackground));
    }

    partial void OnOverrideExpiresChanged(System.DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(OverrideBadgeText));
        OnPropertyChanged(nameof(OverrideBadgeBackground));
    }

    public void UpdateLease(string? ip, System.DateTimeOffset? lastDhcp)
    {
        Device = Device with { Ip = ip, LastDhcp = lastDhcp };
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(LastDhcp));
    }

    // Used by Refresh round-trip + ToggleMode local-update. Replaces the full
    // Device record so Name/Mac shifts also propagate (label can change in the
    // conf), and re-fires Mode-dependent property notifications.
    public void UpdateFromConfig(Device updated)
    {
        Device = updated;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Ip));
        OnPropertyChanged(nameof(LastDhcp));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsWhitelist));
        OnPropertyChanged(nameof(ModeLabel));
    }

    public void UpdateOverrideState(string? mode, System.DateTimeOffset? expires)
    {
        OverrideMode = mode;
        OverrideExpires = expires;
    }

    public void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;
        LastActionText = message;
        _ = ClearToastAfterDelay(message, token);
    }

    private async Task ClearToastAfterDelay(string captured, CancellationToken ct)
    {
        try { await Task.Delay(5000, ct).ConfigureAwait(true); }
        catch (TaskCanceledException) { return; }
        if (!ct.IsCancellationRequested && LastActionText == captured)
            LastActionText = null;
    }

    [RelayCommand]
    private Task KillAsync(CancellationToken ct) => _parent.OverrideBlockFromRowAsync(this, 1440, ct);

    [RelayCommand]
    private Task Allow30mAsync(CancellationToken ct) => _parent.OverrideAllowFromRowAsync(this, 30, ct);

    [RelayCommand]
    private Task ClearOverrideAsync(CancellationToken ct) => _parent.ClearOverrideFromRowAsync(this, ct);

    [RelayCommand]
    private Task ToggleModeAsync(CancellationToken ct) => _parent.ToggleDeviceModeAsync(this, ct);
}
