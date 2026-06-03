namespace KidBlockUI.Models;

public enum DeviceMode
{
    Blocklist,
    Whitelist,
}

public sealed record Device(
    string Mac,
    string Name,
    string? Ip = null,
    System.DateTimeOffset? LastDhcp = null,
    DeviceMode Mode = DeviceMode.Blocklist);
