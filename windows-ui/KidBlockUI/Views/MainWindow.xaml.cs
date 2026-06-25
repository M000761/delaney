using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Syncfusion.Windows.Tools.Controls;
using KidBlockUI.Models;
using KidBlockUI.Services;
using KidBlockUI.ViewModels;

namespace KidBlockUI.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        var config = LoadConfig();
        _vm = new MainViewModel(config);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ConnectionStatus))
                UpdateConnDot();
        };
        // Wire the destructive-confirm callback: KILL routes through ApplyConfirmDialog so
        // the dialog idiom stays consistent with DM2's schedule-apply confirm.
        _vm.KillConfirm = (title, body) =>
        {
            var lines = body
                .Split('\n')
                .Select(t => new DiffLine(DiffKind.Modify, t))
                .ToList();
            return ApplyConfirmDialog.Show(this, title, lines, "KILL");
        };
        // DM22: open the per-MAC Why? popover (its own SSH session + 5s auto-refresh,
        // torn down on Close). Modal, owner-centred, mirroring the ApplyConfirmDialog idiom.
        _vm.WhyRequested = row =>
        {
            var dlg = new WhyBlockedDialog(_vm.Config, row.Mac, row.Name) { Owner = this };
            dlg.ShowDialog();
        };
        // DM22: pin-show the DM17 Live Log dock pane when "Filter log to this MAC" fires,
        // recovering it from auto-hide / float so the filtered result is visible.
        _vm.ShowLogPaneRequested = () =>
        {
            try { DockingManager.SetState(LogPane, DockState.Dock); }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DockingManager: pin-show LogPane failed: {ex.Message}");
            }
        };
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            UpdateConnDot();
            // DM17: restore the persisted dock layout over the default XAML arrangement (first
            // run / stale state degrades to the XAML layout). Before the async Refresh so the
            // panes are arranged before data lands.
            TryLoadDockState();
            // LogTail uses a SEPARATE SSH session so it can reconnect-with-backoff
            // independently; start it before Refresh so the user sees log activity
            // even if the initial Refresh fails.
            _vm.LogTail.Start();
            await _vm.RefreshCommand.ExecuteAsync(null);
        };
    }

    private void OnWindowStateChanged(object? sender, System.EventArgs e)
    {
        if (_vm is null) return;
        if (WindowState == WindowState.Minimized)
        {
            // Cancel the tail's SSH stream so we're not holding an idle session while hidden.
            _vm.LogTail.Pause();

            // DM18: minimise-to-tray. Only hide when a tray surface is live to restore from
            // (App.RestoreMainWindow / the Show menu item); otherwise stay a normal minimised
            // taskbar window so a tray-init failure can never leave the window unreachable.
            if (Application.Current is App app && app.TrayActive)
            {
                Hide();
                app.NotifyMinimisedToTray();
            }
        }
        else
        {
            // Idempotent; Start() is a no-op if the consumer is already running.
            _vm.LogTail.Start();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // DM17: persist the current dock layout before the visual tree tears down, so the next
        // launch restores it (TryLoadDockState). Runs while the Docker + panes are still intact.
        TrySaveDockState();
        _vm?.LogTail.Dispose();
    }

    // ===================== DM17 dock-layout persistence + reset =====================
    //
    // The 4-pane body is a Syncfusion DockingManager (Views/MainWindow.xaml). The user can
    // dock / float / tear-off / resize the panes; the arrangement saves to the app
    // isolated-storage state store on close and restores on launch. PersistState=False on the
    // manager, so these explicit save/load points own the round-trip (mirrors boat's
    // DockShellView). Both paths are guarded: a first run has no saved state and a stale or
    // corrupt state must degrade to the default XAML layout, never throw.

    private void TryLoadDockState()
    {
        try
        {
            Docker.LoadDockState();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"DockingManager: LoadDockState failed/absent; using the default XAML layout: {ex.Message}");
        }
    }

    private void TrySaveDockState()
    {
        try
        {
            Docker.SaveDockState();
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DockingManager: SaveDockState failed: {ex.Message}");
        }
    }

    // View tab "Reset Layout": return the four panes to their default docked arrangement,
    // recovering from any float / auto-hide / resize. Re-asserts the same State /
    // SideInDockedMode / Desired*InDockedMode the XAML declares; the persisted layout is
    // rewritten on the next close.
    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DockingManager.SetState(DevicesPane, DockState.Dock);
            DockingManager.SetSideInDockedMode(DevicesPane, DockSide.Top);
            DockingManager.SetDesiredHeightInDockedMode(DevicesPane, 240d);

            DockingManager.SetState(SchedulePane, DockState.Document);

            DockingManager.SetState(DomainsPane, DockState.Dock);
            DockingManager.SetSideInDockedMode(DomainsPane, DockSide.Right);
            DockingManager.SetDesiredWidthInDockedMode(DomainsPane, 300d);

            DockingManager.SetState(LogPane, DockState.Dock);
            DockingManager.SetSideInDockedMode(LogPane, DockSide.Bottom);
            DockingManager.SetDesiredHeightInDockedMode(LogPane, 220d);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DockingManager: ResetLayout failed: {ex.Message}");
        }
    }

    private async void ApplySchedule_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ApplyScheduleAsync(diff =>
            ApplyConfirmDialog.Show(this, "Apply schedule changes?", diff));
    }

    private void DiscardSchedule_Click(object sender, RoutedEventArgs e)
    {
        _vm.Schedule.Discard();
    }

    // DM16: the Domains ribbon tab mirrors DomainsControl's in-pane Apply / Discard. Both
    // routes drive the same MainViewModel.ApplyDomainsAsync path through the shared
    // ApplyConfirmDialog, so the command is identical wherever it is invoked.
    private async void ApplyDomains_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ApplyDomainsAsync(diff =>
            ApplyConfirmDialog.Show(this, "Apply domain changes?", diff));
    }

    private void DiscardDomains_Click(object sender, RoutedEventArgs e)
    {
        _vm.Domains.Discard();
    }

    private void AllowDurationItem_Click(object sender, RoutedEventArgs e)
    {
        var d = sender as DependencyObject;
        while (d != null)
        {
            if (d is Popup popup) { popup.IsOpen = false; return; }
            d = LogicalTreeHelper.GetParent(d)
                ?? (d is Visual v ? VisualTreeHelper.GetParent(v) : null);
        }
    }

    private void UpdateConnDot()
    {
        ConnDot.Fill = _vm.ConnectionStatus switch
        {
            ConnectionState.Connected   => new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x66)),
            ConnectionState.Connecting  => new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x33)),
            ConnectionState.Stale       => new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x33)),
            ConnectionState.Error       => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
            _                           => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        };
    }

    private static RouterConfig LoadConfig()
    {
        var host = "192.168.200.1";
        var user = "ubnt";
        var key = "%USERPROFILE%\\.ssh\\kidblock_ed25519";
        var script = "/config/scripts/kidblock.sh";
        var macConf = "/config/scripts/kidblock-macs.conf";
        var schedConf = "/config/scripts/kidblock-schedule.conf";
        var domConf = "/config/scripts/kidblock-domains.conf";
        var allowConf = "/config/scripts/kidblock-allowlist.conf";

        var ps1 = TryFindPs1Config();
        if (ps1 is not null)
        {
            var (h, u, k, s) = ParsePs1(ps1);
            if (!string.IsNullOrWhiteSpace(h)) host = h;
            if (!string.IsNullOrWhiteSpace(u)) user = u;
            if (!string.IsNullOrWhiteSpace(k)) key = k;
            if (!string.IsNullOrWhiteSpace(s)) script = s;
        }

        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var stream = File.OpenRead(settingsPath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("Router", out var r))
                {
                    host      = StringProp(r, "Host",              host);
                    user      = StringProp(r, "User",              user);
                    key       = StringProp(r, "KeyPath",           key);
                    script    = StringProp(r, "ScriptPath",        script);
                    macConf   = StringProp(r, "MacConfPath",       macConf);
                    schedConf = StringProp(r, "ScheduleConfPath",  schedConf);
                    domConf   = StringProp(r, "DomainsConfPath",   domConf);
                    allowConf = StringProp(r, "AllowlistConfPath", allowConf);
                }
            }
            catch (JsonException)
            {
                // malformed appsettings.json -- fall through to baked-in defaults
            }
        }

        return new RouterConfig(host, user, key, script, macConf, schedConf, domConf, allowConf);
    }

    private static string StringProp(JsonElement obj, string name, string fallback)
        => obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
           && v.GetString() is { } s
            ? s
            : fallback;

    private static string? TryFindPs1Config()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "windows", "config.ps1"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "windows", "config.ps1"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "windows", "config.ps1"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "windows", "config.ps1"),
            Path.Combine(AppContext.BaseDirectory, "windows", "config.ps1"),
        };
        foreach (var p in candidates)
        {
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return File.ReadAllText(full);
        }
        return null;
    }

    private static readonly Regex RxPs1Host =
        new(@"\$script:KB_RouterHost\s*=\s*'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxPs1User =
        new(@"\$script:KB_RouterUser\s*=\s*'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxPs1Script =
        new(@"\$script:KB_ScriptPath\s*=\s*'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxPs1KeyJoin =
        new(@"\$script:KB_SSHKeyPath\s*=\s*Join-Path\s+\$HOME\s+'([^']+)'", RegexOptions.Compiled);
    private static readonly Regex RxPs1KeyLiteral =
        new(@"\$script:KB_SSHKeyPath\s*=\s*'([^']+)'", RegexOptions.Compiled);

    private static (string host, string user, string key, string script) ParsePs1(string text)
    {
        var h = RxPs1Host.Match(text).Groups[1].Value;
        var u = RxPs1User.Match(text).Groups[1].Value;
        var s = RxPs1Script.Match(text).Groups[1].Value;

        string k = string.Empty;
        var keyJoin = RxPs1KeyJoin.Match(text);
        if (keyJoin.Success)
        {
            // Join-Path $HOME '.ssh\kidblock_ed25519' -- prepend %USERPROFILE%
            var tail = keyJoin.Groups[1].Value;
            k = Path.Combine("%USERPROFILE%", tail.TrimStart('\\', '/'));
        }
        else
        {
            var literal = RxPs1KeyLiteral.Match(text);
            if (literal.Success) k = literal.Groups[1].Value;
        }
        return (h, u, k, s);
    }
}
