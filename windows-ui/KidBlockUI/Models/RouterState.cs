namespace KidBlockUI.Models;

public sealed record RouterState(
    string CurrentApplied,
    string ScheduleSays,
    string EffectiveDesired,
    System.DateTimeOffset Now,
    string? OverrideMode,
    System.DateTimeOffset? OverrideExpires);
