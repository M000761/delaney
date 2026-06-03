using System.Text;
using System.Text.RegularExpressions;
using KidBlockUI.Models;

namespace KidBlockUI.Services;

public static class ConfigParser
{
    private static readonly Regex MacToken = new(
        @"^[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5}$",
        RegexOptions.Compiled);

    private static readonly Regex ModeToken = new(
        @"^mode:(?<mode>blocklist|whitelist)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SchedDays = new(
        @"^([A-Za-z*,\-]+)\s+(\d{2}:\d{2}-\d{2}:\d{2})$",
        RegexOptions.Compiled);

    private static readonly Regex SchedLegacy = new(
        @"^(\d{2}:\d{2}-\d{2}:\d{2})$",
        RegexOptions.Compiled);

    public static IReadOnlyList<Device> ParseMacs(string text)
    {
        var devices = new List<Device>();
        foreach (var raw in SplitLines(text))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (!MacToken.IsMatch(tokens[0])) continue;
            var mac = tokens[0].ToLowerInvariant();

            // Trailing mode:xxx token (DM6) -- token-based so it's order-agnostic with
            // the label; backwards-compatible with existing macs.conf rows that have no
            // mode token at all (default = Blocklist).
            var mode = DeviceMode.Blocklist;
            var labelEnd = tokens.Length;
            for (var i = tokens.Length - 1; i >= 1; i--)
            {
                var m = ModeToken.Match(tokens[i]);
                if (!m.Success) continue;
                mode = m.Groups["mode"].Value.Equals("whitelist", StringComparison.OrdinalIgnoreCase)
                    ? DeviceMode.Whitelist
                    : DeviceMode.Blocklist;
                if (i == labelEnd - 1) labelEnd = i;  // strip trailing mode token from label
                break;
            }

            var name = labelEnd > 1
                ? string.Join(' ', tokens, 1, labelEnd - 1)
                : string.Empty;

            devices.Add(new Device(mac, name, Mode: mode));
        }
        return devices;
    }

    public static IReadOnlyList<ScheduleWindow> ParseSchedule(string text)
    {
        var windows = new List<ScheduleWindow>();
        foreach (var raw in SplitLines(text))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            string days;
            string range;
            var m1 = SchedDays.Match(line);
            if (m1.Success)
            {
                days = m1.Groups[1].Value.ToLowerInvariant();
                range = m1.Groups[2].Value;
            }
            else
            {
                var m2 = SchedLegacy.Match(line);
                if (!m2.Success) continue;
                days = "*";
                range = m2.Groups[1].Value;
            }

            var dash = range.IndexOf('-');
            if (dash < 0) continue;
            var startMin = ParseHhmm(range[..dash]);
            var endMin = ParseHhmm(range[(dash + 1)..]);
            if (startMin < 0 || endMin < 0) continue;
            windows.Add(new ScheduleWindow(days, startMin, endMin, line));
        }
        return windows;
    }

    public static IReadOnlyList<DomainEntry> ParseDomains(string text)
    {
        var domains = new List<DomainEntry>();
        foreach (var raw in SplitLines(text))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.Contains(' ') || line.Contains('\t')) continue;
            if (!line.Contains('.')) continue;
            domains.Add(new DomainEntry(line));
        }
        return domains;
    }

    // Round-trip serializer for kidblock-macs.conf -- preserves the row order and
    // labels of `original`, but emits each device's Mode as a trailing `mode:xxx`
    // token (or omits it for the default Blocklist). Used by the UI when the user
    // toggles a device's Mode column. New devices added to `original` without a
    // corresponding existing line are appended.
    public static string SerializeMacs(string original, IReadOnlyList<Device> devices)
    {
        var byMac = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices) byMac[d.Mac] = d;

        var sb = new StringBuilder();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in SplitLines(original))
        {
            var stripped = StripComment(raw).Trim();
            if (stripped.Length == 0) { sb.Append(raw); sb.Append('\n'); continue; }

            var tokens = stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || !MacToken.IsMatch(tokens[0]))
            {
                sb.Append(raw); sb.Append('\n'); continue;
            }
            var mac = tokens[0].ToLowerInvariant();
            if (!byMac.TryGetValue(mac, out var d)) { sb.Append(raw); sb.Append('\n'); continue; }

            // Strip any existing trailing mode token from the line tokens so we can
            // emit the canonical token (or none, if Blocklist) without duplicating it.
            var keep = new List<string>(tokens.Length);
            keep.Add(tokens[0]);
            for (var i = 1; i < tokens.Length; i++)
                if (!ModeToken.IsMatch(tokens[i])) keep.Add(tokens[i]);

            sb.Append(string.Join(' ', keep));
            if (d.Mode == DeviceMode.Whitelist)
            {
                sb.Append("   mode:whitelist");
            }
            sb.Append('\n');
            written.Add(mac);
        }

        // Devices in `devices` that weren't present in `original` get appended (no label).
        foreach (var d in devices)
        {
            if (written.Contains(d.Mac)) continue;
            sb.Append(d.Mac);
            if (!string.IsNullOrWhiteSpace(d.Name)) { sb.Append("   "); sb.Append(d.Name); }
            if (d.Mode == DeviceMode.Whitelist) sb.Append("   mode:whitelist");
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                var end = (i > 0 && text[i - 1] == '\r') ? i - 1 : i;
                yield return text[start..end];
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text[start..];
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash < 0 ? line : line[..hash];
    }

    private static int ParseHhmm(string s)
    {
        var colon = s.IndexOf(':');
        if (colon < 0) return -1;
        if (!int.TryParse(s[..colon], out var h)) return -1;
        if (!int.TryParse(s[(colon + 1)..], out var m)) return -1;
        if (h == 24 && m == 0) return 1440;
        if (h < 0 || h > 23) return -1;
        if (m < 0 || m > 59) return -1;
        return h * 60 + m;
    }
}
