using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
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
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            UpdateConnDot();
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
        }
        else
        {
            // Idempotent; Start() is a no-op if the consumer is already running.
            _vm.LogTail.Start();
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _vm?.LogTail.Dispose();
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
                    host      = StringProp(r, "Host",             host);
                    user      = StringProp(r, "User",             user);
                    key       = StringProp(r, "KeyPath",          key);
                    script    = StringProp(r, "ScriptPath",       script);
                    macConf   = StringProp(r, "MacConfPath",      macConf);
                    schedConf = StringProp(r, "ScheduleConfPath", schedConf);
                    domConf   = StringProp(r, "DomainsConfPath",  domConf);
                }
            }
            catch (JsonException)
            {
                // malformed appsettings.json -- fall through to baked-in defaults
            }
        }

        return new RouterConfig(host, user, key, script, macConf, schedConf, domConf);
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
