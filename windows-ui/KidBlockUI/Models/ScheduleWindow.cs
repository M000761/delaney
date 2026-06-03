namespace KidBlockUI.Models;

// StartMin / EndMin are minutes-since-midnight; EndMin == 1440 means 24:00 (end-of-day).
public sealed record ScheduleWindow(string Days, int StartMin, int EndMin, string Raw);
