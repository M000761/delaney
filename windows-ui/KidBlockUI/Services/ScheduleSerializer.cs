using System.Text;
using KidBlockUI.Models;

namespace KidBlockUI.Services;

public static class ScheduleSerializer
{
    public static string Serialize(IEnumerable<ScheduleWindow> windows)
    {
        var sb = new StringBuilder();
        sb.Append("# kidblock -- block windows with optional day-of-week\n");
        sb.Append("# Edited by KidBlockUI. Format: [days] HH:MM-HH:MM\n");
        sb.Append("# Use 24:00 to mean midnight (end of day).\n");
        sb.Append('\n');
        foreach (var w in windows)
        {
            sb.Append(FormatLine(w));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static string FormatLine(ScheduleWindow w)
    {
        var range = $"{FormatHhmm(w.StartMin)}-{FormatHhmm(w.EndMin)}";
        return w.Days == "*" ? range : $"{w.Days} {range}";
    }

    public static string FormatHhmm(int minutes)
    {
        if (minutes == 1440) return "24:00";
        var h = minutes / 60;
        var m = minutes % 60;
        return $"{h:D2}:{m:D2}";
    }
}
