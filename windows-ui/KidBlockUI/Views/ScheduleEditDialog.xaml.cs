using System.Text.RegularExpressions;
using System.Windows;
using KidBlockUI.Models;

namespace KidBlockUI.Views;

public partial class ScheduleEditDialog : Window
{
    private static readonly Regex HhmmRx = new(@"^(\d{1,2}):(\d{2})$", RegexOptions.Compiled);
    private static readonly Regex DaysRx = new(@"^(\*|(?:sun|mon|tue|wed|thu|fri|sat)(?:(?:[-,](?:sun|mon|tue|wed|thu|fri|sat))*))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ScheduleWindow? Result { get; private set; }

    public ScheduleEditDialog(ScheduleWindow original)
    {
        InitializeComponent();
        DaysBox.Text  = original.Days;
        StartBox.Text = ScheduleSerializerFormat(original.StartMin);
        EndBox.Text   = ScheduleSerializerFormat(original.EndMin);
    }

    private static string ScheduleSerializerFormat(int minutes) =>
        Services.ScheduleSerializer.FormatHhmm(minutes);

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var days = (DaysBox.Text ?? string.Empty).Trim().ToLowerInvariant();
        if (days.Length == 0) days = "*";
        if (!DaysRx.IsMatch(days))
        {
            ErrorText.Text = "Days must be * / a day / range / list (e.g. mon-thu, sat,sun).";
            return;
        }

        if (!TryParseHhmm(StartBox.Text, out var startMin))
        {
            ErrorText.Text = "Start must be HH:MM in 24-hour clock.";
            return;
        }
        if (!TryParseHhmm(EndBox.Text, out var endMin))
        {
            ErrorText.Text = "End must be HH:MM in 24-hour clock (use 24:00 for midnight).";
            return;
        }
        if (endMin <= startMin)
        {
            ErrorText.Text = "End must be after Start.";
            return;
        }

        var raw = days == "*"
            ? $"{ScheduleSerializerFormat(startMin)}-{ScheduleSerializerFormat(endMin)}"
            : $"{days} {ScheduleSerializerFormat(startMin)}-{ScheduleSerializerFormat(endMin)}";
        Result = new ScheduleWindow(days, startMin, endMin, raw);
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool TryParseHhmm(string? text, out int minutes)
    {
        minutes = -1;
        if (text is null) return false;
        var m = HhmmRx.Match(text.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out var h)) return false;
        if (!int.TryParse(m.Groups[2].Value, out var mm)) return false;
        if (h == 24 && mm == 0) { minutes = 1440; return true; }
        if (h < 0 || h > 23) return false;
        if (mm < 0 || mm > 59) return false;
        minutes = h * 60 + mm;
        return true;
    }
}
