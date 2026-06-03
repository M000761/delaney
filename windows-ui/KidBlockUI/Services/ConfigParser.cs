using System.Text.RegularExpressions;
using KidBlockUI.Models;

namespace KidBlockUI.Services;

public static class ConfigParser
{
    private static readonly Regex MacLine = new(
        @"^\s*([0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5})(?:\s+(.+?))?\s*$",
        RegexOptions.Compiled);

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
            var m = MacLine.Match(line);
            if (!m.Success) continue;
            var mac = m.Groups[1].Value.ToLowerInvariant();
            var name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
            devices.Add(new Device(mac, name));
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
