using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using KidBlockUI.Models;
using Renci.SshNet;

namespace KidBlockUI.Services;

public sealed record RouterConfig(
    string Host,
    string User,
    string KeyPath,
    string ScriptPath,
    string MacConfPath,
    string ScheduleConfPath,
    string DomainsConfPath);

public sealed record DhcpLease(string Mac, string Ip, System.DateTimeOffset? Expiry, string? Hostname);

public sealed class RouterClient : IDisposable
{
    private readonly RouterConfig _config;
    private SshClient? _ssh;

    public RouterClient(RouterConfig config)
    {
        _config = config;
    }

    public string Host => _config.Host;
    public string User => _config.User;
    public string ScriptPath => _config.ScriptPath;

    public bool IsConnected => _ssh is { IsConnected: true };

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var keyPath = Environment.ExpandEnvironmentVariables(_config.KeyPath);
        var pk = new PrivateKeyFile(keyPath);
        var auth = new PrivateKeyAuthenticationMethod(_config.User, pk);
        var info = new ConnectionInfo(_config.Host, _config.User, auth)
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        var client = new SshClient(info);
        client.Connect();
        ct.ThrowIfCancellationRequested();
        _ssh = client;
    }, ct);

    public Task<string> RunAsync(string command, CancellationToken ct = default) => Task.Run(() =>
    {
        var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
        if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
        ct.ThrowIfCancellationRequested();
        using var cmd = ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        var result = cmd.Execute() ?? string.Empty;
        ct.ThrowIfCancellationRequested();
        return result;
    }, ct);

    public async Task<IReadOnlyList<DhcpLease>> GetDhcpLeasesAsync(CancellationToken ct = default)
    {
        var text = await RunAsync("/opt/vyatta/bin/vyatta-op-cmd-wrapper show dhcp leases", ct).ConfigureAwait(false);
        var leases = ParseDhcpLeases(text);
        if (leases.Count > 0) return leases;

        var isc = await RunAsync("sudo cat /var/run/dhcpd.leases", ct).ConfigureAwait(false);
        return ParseIscLeases(isc);
    }

    public Task<string> GetConfigFileAsync(string remotePath, CancellationToken ct = default)
        => RunAsync($"cat \"{remotePath}\"", ct);

    public string ScheduleConfPath => _config.ScheduleConfPath;
    public string MacConfPath      => _config.MacConfPath;
    public string DomainsConfPath  => _config.DomainsConfPath;

    public Task WriteConfigFileAsync(string remotePath, string content, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
            ct.ThrowIfCancellationRequested();

            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            // base64-encoded payload sidesteps any quoting / newline / special-char issues.
            // The shell command base64-decodes into the destination atomically via a temp file + mv.
            var command =
                $"tmp=$(mktemp \"{remotePath}.XXXXXX\") && " +
                $"echo {b64} | base64 -d > \"$tmp\" && " +
                $"mv \"$tmp\" \"{remotePath}\"";

            using var cmd = ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = cmd.Execute();
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error) ? output : cmd.Error;
                throw new InvalidOperationException(
                    $"Remote write failed (exit {cmd.ExitStatus}) on {remotePath}: {err.Trim()}");
            }
        }, ct);

    public Task ReapplyAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
            ct.ThrowIfCancellationRequested();
            using var cmd = ssh.CreateCommand($"sudo {_config.ScriptPath} reapply");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = cmd.Execute();
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error) ? output : cmd.Error;
                throw new InvalidOperationException(
                    $"kidblock.sh reapply failed (exit {cmd.ExitStatus}): {err.Trim()}");
            }
        }, ct);

    public Task InstallDomainsAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
            ct.ThrowIfCancellationRequested();
            // install-domains restarts dnsmasq, which can take a couple of seconds.
            using var cmd = ssh.CreateCommand($"sudo {_config.ScriptPath} install-domains");
            cmd.CommandTimeout = TimeSpan.FromSeconds(30);
            var output = cmd.Execute();
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error) ? output : cmd.Error;
                throw new InvalidOperationException(
                    $"kidblock.sh install-domains failed (exit {cmd.ExitStatus}): {err.Trim()}");
            }
        }, ct);

    public Task OverrideBlockAsync(int minutes, CancellationToken ct = default) =>
        RunOverrideAsync("override-block", minutes, ct);

    public Task OverrideAllowAsync(int minutes, CancellationToken ct = default) =>
        RunOverrideAsync("override-allow", minutes, ct);

    public Task ClearOverrideAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
            ct.ThrowIfCancellationRequested();
            using var cmd = ssh.CreateCommand($"sudo {_config.ScriptPath} clear-override");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = cmd.Execute();
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error) ? output : cmd.Error;
                throw new InvalidOperationException(
                    $"kidblock.sh clear-override failed (exit {cmd.ExitStatus}): {err.Trim()}");
            }
        }, ct);

    private Task RunOverrideAsync(string subcommand, int minutes, CancellationToken ct) =>
        Task.Run(() =>
        {
            if (minutes < 1 || minutes > 1440)
                throw new ArgumentOutOfRangeException(nameof(minutes), minutes,
                    "Override minutes must be between 1 and 1440 (24h ceiling matches the PS1 shortcuts).");
            var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
            if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");
            ct.ThrowIfCancellationRequested();
            using var cmd = ssh.CreateCommand($"sudo {_config.ScriptPath} {subcommand} {minutes}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var output = cmd.Execute();
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus != 0)
            {
                var err = string.IsNullOrWhiteSpace(cmd.Error) ? output : cmd.Error;
                throw new InvalidOperationException(
                    $"kidblock.sh {subcommand} {minutes} failed (exit {cmd.ExitStatus}): {err.Trim()}");
            }
        }, ct);

    public async Task<RouterState> GetStatusAsync(CancellationToken ct = default)
    {
        var text = await RunAsync($"sudo {_config.ScriptPath} status", ct).ConfigureAwait(false);
        return ParseStatus(text);
    }

    // Streams /var/log/kidblock.log line-by-line via an SSH ShellStream running
    // `tail -n 50 -f`. Yields each line as it arrives; honors the cancellation token
    // on a ~500ms boundary (the ShellStream ReadLine timeout). Caller is expected to
    // wrap this in a reconnect-with-backoff loop -- this method is single-shot:
    // on transient SSH failure (disconnect, refused, etc.) it surfaces the exception
    // and the caller decides whether to retry.
    public async IAsyncEnumerable<string> StreamLogAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ssh = _ssh ?? throw new InvalidOperationException("SSH client not connected.");
        if (!ssh.IsConnected) throw new InvalidOperationException("SSH client not connected.");

        // 200x50 terminal is wide enough that lines won't get hard-wrapped before
        // they reach the ReadLine boundary; 4096-byte buffer matches Renci's example.
        using var shell = ssh.CreateShellStream("kidblock-tail", 200, 50, 800, 600, 4096);

        // Suppress echo so the command we send doesn't come back to us as a "line".
        // The `2>/dev/null` swallows stty's warning on non-tty environments.
        shell.WriteLine("stty -echo 2>/dev/null; tail -n 50 -f /var/log/kidblock.log");

        while (!ct.IsCancellationRequested)
        {
            string? line = await Task.Run(() =>
            {
                try { return shell.ReadLine(TimeSpan.FromMilliseconds(500)); }
                catch (ObjectDisposedException) { return null; }
            }, ct).ConfigureAwait(false);

            if (line is null) continue;

            // The interactive shell echoes our own setup command on most EdgeOS sshd
            // configs even with stty -echo (the line lands before stty takes effect).
            // Drop the bootstrap line + any blank passes.
            if (line.Length == 0) continue;
            if (line.Contains("tail -n 50 -f /var/log/kidblock.log")) continue;

            yield return line;
        }
    }

    public void Dispose()
    {
        try { _ssh?.Disconnect(); } catch { /* ignore */ }
        _ssh?.Dispose();
        _ssh = null;
    }

    // === parsers (exposed internal for unit tests in later milestones) ===

    private static readonly Regex LeaseLine = new(
        @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9a-fA-F:]{17})\s+(?<exp>\S+\s+\S+|\S+)\s+(?<pool>\S+)\s+(?<host>\S+)\s*$",
        RegexOptions.Compiled);

    internal static IReadOnlyList<DhcpLease> ParseDhcpLeases(string text)
    {
        var rows = new List<DhcpLease>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("IP address", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("---")) continue;
            var m = LeaseLine.Match(line);
            if (!m.Success) continue;
            var mac = m.Groups["mac"].Value.ToLowerInvariant();
            var ip = m.Groups["ip"].Value;
            var expRaw = m.Groups["exp"].Value.Trim();
            var host = m.Groups["host"].Value;
            System.DateTimeOffset? expiry = null;
            if (System.DateTimeOffset.TryParse(expRaw, out var parsed)) expiry = parsed;
            rows.Add(new DhcpLease(mac, ip, expiry, host));
        }
        return rows;
    }

    private static readonly Regex IscLeaseBlock = new(
        @"lease\s+(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s*\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex IscEnds = new(
        @"ends\s+\d+\s+(?<ends>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s*;",
        RegexOptions.Compiled);

    private static readonly Regex IscHwEthernet = new(
        @"hardware\s+ethernet\s+(?<mac>[0-9a-fA-F:]{17})\s*;",
        RegexOptions.Compiled);

    private static readonly Regex IscHostname = new(
        @"client-hostname\s+""(?<host>[^""]*)""\s*;",
        RegexOptions.Compiled);

    internal static IReadOnlyList<DhcpLease> ParseIscLeases(string text)
    {
        var byMac = new Dictionary<string, DhcpLease>();
        if (string.IsNullOrWhiteSpace(text)) return new List<DhcpLease>();

        foreach (Match block in IscLeaseBlock.Matches(text))
        {
            var body = block.Groups["body"].Value;

            var hw = IscHwEthernet.Match(body);
            if (!hw.Success) continue;
            var mac = hw.Groups["mac"].Value.ToLowerInvariant();
            var ip = block.Groups["ip"].Value;

            System.DateTimeOffset? expiry = null;
            var ends = IscEnds.Match(body);
            if (ends.Success && System.DateTimeOffset.TryParseExact(
                ends.Groups["ends"].Value,
                "yyyy/MM/dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
            {
                expiry = parsed;
            }

            string? host = null;
            var hn = IscHostname.Match(body);
            if (hn.Success) host = hn.Groups["host"].Value;

            byMac[mac] = new DhcpLease(mac, ip, expiry, host);
        }
        return new List<DhcpLease>(byMac.Values);
    }

    private static readonly Regex RxCurrentApplied = new(
        @"^Current applied\s*:\s*(?<v>\S+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RxScheduleSays = new(
        @"^Schedule says now\s*:\s*(?<v>\S+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RxEffectiveDesired = new(
        @"^Effective desired\s*:\s*(?<v>\S+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex RxOverrideActive = new(
        @"^Override active\s*:\s*(?<mode>\S+)\s+until\s+(?<until>\S+)\s+\((?<rem>\d+)\s+min remaining\)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    internal static RouterState ParseStatus(string text)
    {
        var applied = RxCurrentApplied.Match(text).Groups["v"].Value;
        var sched = RxScheduleSays.Match(text).Groups["v"].Value;
        var desired = RxEffectiveDesired.Match(text).Groups["v"].Value;
        string? overrideMode = null;
        System.DateTimeOffset? overrideExpiry = null;
        var ov = RxOverrideActive.Match(text);
        if (ov.Success && int.TryParse(ov.Groups["rem"].Value, out var rem))
        {
            overrideMode = ov.Groups["mode"].Value;
            overrideExpiry = System.DateTimeOffset.Now.AddMinutes(rem);
        }
        return new RouterState(
            applied,
            sched,
            desired,
            System.DateTimeOffset.Now,
            overrideMode,
            overrideExpiry);
    }
}
