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
        var text = await RunAsync("show dhcp leases", ct).ConfigureAwait(false);
        return ParseDhcpLeases(text);
    }

    public Task<string> GetConfigFileAsync(string remotePath, CancellationToken ct = default)
        => RunAsync($"cat \"{remotePath}\"", ct);

    public async Task<RouterState> GetStatusAsync(CancellationToken ct = default)
    {
        var text = await RunAsync($"sudo {_config.ScriptPath} status", ct).ConfigureAwait(false);
        return ParseStatus(text);
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
