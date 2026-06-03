namespace KidBlockUI.Models;

public sealed record Device(string Mac, string Name, string? Ip = null, System.DateTimeOffset? LastDhcp = null);
