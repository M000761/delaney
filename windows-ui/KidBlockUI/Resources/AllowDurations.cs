namespace KidBlockUI.Resources;

public sealed record AllowDuration(string Label, int Minutes);

public static class AllowDurations
{
    public static readonly IReadOnlyList<AllowDuration> Presets = new[]
    {
        new AllowDuration("15 min",  15),
        new AllowDuration("30 min",  30),
        new AllowDuration("45 min",  45),
        new AllowDuration("1 hour",  60),
        new AllowDuration("2 hours", 120),
        new AllowDuration("3 hours", 180),
        new AllowDuration("4 hours", 240),
        new AllowDuration("5 hours", 300),
        new AllowDuration("6 hours", 360),
        new AllowDuration("7 hours", 420),
        new AllowDuration("8 hours", 480),
    };

    public const int DefaultMinutes = 30;
}
