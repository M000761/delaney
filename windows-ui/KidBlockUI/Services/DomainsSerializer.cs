using System.Text;
using KidBlockUI.Models;

namespace KidBlockUI.Services;

public static class DomainsSerializer
{
    public static string Serialize(IEnumerable<DomainEntry> domains)
        => Serialize(domains, DeviceMode.Blocklist);

    public static string Serialize(IEnumerable<DomainEntry> domains, DeviceMode mode)
    {
        var sb = new StringBuilder();
        if (mode == DeviceMode.Whitelist)
        {
            sb.Append("# kidblock -- per-device whitelist (allowlist)\n");
            sb.Append("# Edited by KidBlockUI. One domain per line; comments start with #.\n");
            sb.Append("# Apply via: sudo /config/scripts/kidblock.sh install-allowlist\n");
        }
        else
        {
            sb.Append("# kidblock -- per-device domain blocklist\n");
            sb.Append("# Edited by KidBlockUI. One domain per line; comments start with #.\n");
            sb.Append("# Apply via: sudo /config/scripts/kidblock.sh install-domains\n");
        }
        sb.Append('\n');
        foreach (var d in domains)
        {
            sb.Append(d.Domain);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
