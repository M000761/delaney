using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KidBlockUI.Models;

public enum LogKind
{
    Block,
    Allow,
    Override,
    ScheduleTick,
    Install,
    Error,
    Other,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogKind Kind, string Message)
{
    private static readonly Regex RxLine = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2})\s+(?<msg>.*)$",
        RegexOptions.Compiled);

    // kidblock.sh log() emits lines of shape: "YYYY-MM-DD HH:MM:SS <message>".
    // The known message families are the six log() call-sites in router/kidblock.sh:
    //   "applied block (N MACs, with ESTABLISHED bypass)"           -> Block
    //   "applied allow"                                              -> Allow
    //   "tick: <cur> -> <want>"                                      -> ScheduleTick
    //   "override <mode> for <N>m"                                   -> Override
    //   "installed per-device domain blocklist (N domains)"          -> Install
    //   "removed per-device domain blocklist"                        -> Install
    // Anything else buckets to Other so the UI never silently drops a row.
    // Lines that fail the timestamp shape are surfaced as Error so router-side
    // misbehaviour (rare but possible) reaches the screen.
    public static LogEntry Parse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return new LogEntry(DateTimeOffset.Now, LogKind.Other, string.Empty);

        var m = RxLine.Match(line);
        if (!m.Success)
            return new LogEntry(DateTimeOffset.Now, LogKind.Error, line);

        DateTimeOffset ts;
        if (!DateTimeOffset.TryParseExact(
                m.Groups["ts"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out ts))
        {
            ts = DateTimeOffset.Now;
        }

        var msg = m.Groups["msg"].Value;
        var kind = ClassifyMessage(msg);
        return new LogEntry(ts, kind, msg);
    }

    private static LogKind ClassifyMessage(string msg)
    {
        // Order matters: "tick:" is checked before the bare "applied" patterns because
        // a future log() refactor could in principle prefix tick lines, and we want the
        // most specific marker to win.
        if (msg.StartsWith("tick:", StringComparison.Ordinal))            return LogKind.ScheduleTick;
        if (msg.StartsWith("override ", StringComparison.Ordinal))        return LogKind.Override;
        if (msg.StartsWith("applied block", StringComparison.Ordinal))    return LogKind.Block;
        if (msg.StartsWith("applied allow", StringComparison.Ordinal))    return LogKind.Allow;
        if (msg.StartsWith("installed per-device", StringComparison.Ordinal) ||
            msg.StartsWith("removed per-device", StringComparison.Ordinal))
            return LogKind.Install;
        return LogKind.Other;
    }
}
