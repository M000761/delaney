# kidblock router-side files

This directory holds everything that lives on the EdgeRouter under
`/config/scripts/`. The top-level [README](../README.md) covers install +
day-to-day usage; this file documents the file shapes + the two operating
modes (blocklist vs whitelist).

## Files

| File | What it is |
|---|---|
| `kidblock.sh`              | Main script. Runs on the 1-minute task-scheduler tick (`reapply`) and on every shortcut from the Windows UI. |
| `kidblock-macs.conf`       | List of controlled devices. One MAC per line; optional label + optional `mode:whitelist` token. |
| `kidblock-domains.conf`    | Domains blocked for blocklist-mode devices. One domain per line. |
| `kidblock-allowlist.conf`  | Domains allowed for whitelist-mode devices. One domain per line. (DM6) |
| `kidblock-schedule.conf`   | Block windows (optionally per-day-of-week). |
| `kidblock-init.sh`         | Boot hook -- re-runs `kidblock.sh init` after reboot. |
| `router-setup-commands.txt`| Manual install steps if `Install-Router-Scripts.ps1` can't reach the router. |

## The two modes

Every device in `kidblock-macs.conf` operates in one of two modes:

### Blocklist mode (default)

The device reaches **everything except** domains in `kidblock-domains.conf`.

```
98:59:7a:8b:a5:8d   DESKTOP-3VJTN3A (Adam Desktop)
```

This is the original kidblock model -- the kid's iPad reaches the internet
freely, but YouTube / TikTok / Roblox / etc. are dropped at the router.

### Whitelist mode (DM6 -- homework hour)

Append `mode:whitelist` to the row:

```
aa:bb:cc:dd:ee:ff   Kid-iPad   mode:whitelist
```

The device reaches **nothing except** domains in `kidblock-allowlist.conf`.
Useful for a homework device where the kid should only be able to hit Khan
Academy, the school portal, and a handful of reference sites.

You can also toggle a device's mode directly from the Windows UI (Devices
grid -> click the Mode column button). The UI writes `kidblock-macs.conf`
+ runs `reapply` so the new chain takes effect immediately.

## How whitelist mode works under the hood

Three iptables chains operate independently on `FORWARD`:

```
FORWARD --+--> KIDBLOCK_TIME       (full-block per schedule, all controlled MACs)
          +--> KIDBLOCK_DOMAINS    (drop blocklisted IPs, blocklist MACs only)
          +--> KIDBLOCK_WHITELIST  (default-DROP per whitelist MAC + RETURN-if-in-allow-set)
```

The `KIDBLOCK_WHITELIST` chain, per whitelist MAC:

1. `ESTABLISHED,RELATED` -> RETURN (don't break in-flight connections during a re-apply).
2. `mac-source X -m set --match-set kidblock_allow_v4 dst` -> RETURN (allow).
3. `mac-source X` -> DROP (everything else from X).

Two ipsets (`kidblock_allow_v4` + `kidblock_allow_v6`) are populated by dnsmasq
via the `ipset=/domain/kidblock_allow_v4,kidblock_allow_v6` directive that
`install-allowlist` writes to `/etc/dnsmasq.d/kidblock-allowlist.conf`.

When any device on the network looks up an allowlisted domain (e.g.
`khanacademy.org`), dnsmasq adds the resolved IP to the allow ipsets. The
whitelist device's subsequent TCP connect to that IP matches RETURN and
flows out normally.

## Commands

```bash
# Install / refresh
sudo /config/scripts/kidblock.sh install-domains      # blocklist (dnsmasq + ipsets + iptables)
sudo /config/scripts/kidblock.sh install-allowlist    # whitelist (dnsmasq + ipsets + iptables)
sudo /config/scripts/kidblock.sh reapply              # re-evaluate chains against current confs

# Inspect
sudo /config/scripts/kidblock.sh status               # human-readable summary
sudo ipset list kidblock_domains_v4                   # currently-resolved blocklisted IPs
sudo ipset list kidblock_allow_v4                     # currently-resolved allowlisted IPs

# Override (router-wide)
sudo /config/scripts/kidblock.sh override-allow 30    # 30-min global pause of schedule's block windows
sudo /config/scripts/kidblock.sh override-block 30
sudo /config/scripts/kidblock.sh clear-override

# Tear down
sudo /config/scripts/kidblock.sh uninstall-domains
sudo /config/scripts/kidblock.sh uninstall-allowlist
```

## `override-allow` interaction with whitelist mode

`override-allow N` lifts the schedule-based full-block (`KIDBLOCK_TIME`) for
all controlled devices for N minutes. It **does not** lift the per-device
filters (`KIDBLOCK_DOMAINS` for blocklist devices, `KIDBLOCK_WHITELIST` for
whitelist devices) -- those keep enforcing throughout the override window.

For a whitelist device this means: during `override-allow`, the device can
still only reach allowlisted domains. The override only matters if the
schedule was about to clamp the device down further.

If you genuinely need to grant a whitelist device temporary unrestricted
access (e.g. the kid needs to grab a YouTube tutorial referenced in an
assignment), the simple workarounds today are: (a) flip the device's Mode
to Blocklist via the UI for the duration, then flip back; or (b)
temporarily uncomment `youtube.com` in `kidblock-allowlist.conf` + run
`install-allowlist`. A first-class per-MAC unrestricted override is a
follow-up worth considering if this comes up often.

## Caveats specific to whitelist mode

1. **CDN dependencies.** Modern sites pull assets from many domains. Khan
   Academy pulls from `kastatic.org` + `kasandbox.org`; Google Workspace
   pulls from `gstatic.com` + `googleapis.com`. The seed
   `kidblock-allowlist.conf` covers the common ones, but expect to add a
   few more as the kid surfaces "this page is broken" -- the UI's Allowlist
   pane makes this a 2-click fix.

2. **DoH bypasses dnsmasq.** Same caveat as blocklist mode. If the device
   uses DNS-over-HTTPS (Chrome / Firefox / iOS+Android Private DNS),
   dnsmasq never sees the query, never populates the allow ipsets, and
   the device can't reach the allowlisted domains. Force LAN DNS to the
   router via NAT rule 4000 (see top-level README).

3. **First-connect race.** The very first connection to an allowlisted
   domain after install relies on the DNS query happening BEFORE the
   TCP connect -- which is the normal flow. If the device has the IP
   cached from a previous resolution it might attempt the connect
   without re-querying, and the allow ipset won't have that IP yet ->
   first attempt drops. Flush DNS on the device or wait for the cache
   to expire.

4. **Not a security boundary.** Same caveat as blocklist mode. A
   determined user can use a VPN, mobile hotspot, or different MAC and
   bypass everything. Whitelist mode is a "remove temptation" tool that
   makes off-task browsing inconvenient, not impossible.
