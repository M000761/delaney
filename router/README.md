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
| `kidblock-init.sh`         | Boot hook -- re-runs `kidblock.sh init` after reboot (also restores unexpired per-MAC overrides from `kidblock-overrides.conf`). |
| `kidblock-overrides.conf`  | (DM9) Active per-MAC overrides. Managed by `kidblock.sh override-*` / `clear-override`. One row: `<MAC>  block\|allow  <minutes>  <expiry-epoch>`. Not shipped in the repo -- the script creates it on first override. |
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

## Per-MAC overrides (DM9)

Each device can be overridden independently, so locking the kid's iPad
doesn't lock the parent's laptop. State lives in
`kidblock-overrides.conf` (one row per active override; managed by
`kidblock.sh`):

```
# MAC               verb   minutes   expiry-epoch
98:59:7a:8b:a5:8d   block  1440      1759334400
7c:50:79:f7:db:13   allow  30        1759335200
```

Two verbs:

- `override-block <MAC> <minutes>` -- DROP this MAC at the top of
  `KIDBLOCK_TIME`. Preempts the schedule AND any blocklist/whitelist
  filtering (the DROP terminates FORWARD traversal).
- `override-allow <MAC> <minutes>` -- ACCEPT this MAC at the top of
  `KIDBLOCK_TIME`. Bypasses the schedule AND any blocklist/whitelist
  filtering (the ACCEPT terminates FORWARD traversal).

To clear:

- `clear-override <MAC>` -- remove the override for one device; it
  returns to the schedule + its mode-specific filter.
- `clear-override --all` -- remove every active override.

Bulk (operating on every controlled MAC in `kidblock-macs.conf` in
one call, for the bedtime / homework-start use case):

- `override-block --all <minutes>` -- block every controlled device.
- `override-allow --all <minutes>` -- allow every controlled device.
- `override-block <minutes>` (numeric first arg, no MAC) is a
  **back-compat alias** for `--all <minutes>`; same for
  `override-allow <minutes>` and bare `clear-override`. The existing
  `windows/*.ps1` shortcuts and any external scripts using the pre-DM9
  no-MAC form keep working without changes.

### Precedence invariant

`override-{block,allow} <MAC>` takes precedence over **both** the schedule
AND any blocklist/whitelist mode for the duration of the override:

- A **whitelist** device under an active **block** override is fully
  blocked (the override DROP at the top of `KIDBLOCK_TIME` fires
  before `KIDBLOCK_WHITELIST` would let any allowlisted domain
  through).
- A **whitelist** device under an active **allow** override has fully
  unrestricted internet for the override window (the override ACCEPT
  terminates FORWARD traversal, so `KIDBLOCK_WHITELIST` never sees
  the packet). This is the simple per-MAC unrestricted-grant
  pre-DM9 didn't have.
- A **blocklist** device under an active **block** override is fully
  blocked (override DROP preempts schedule + `KIDBLOCK_DOMAINS`).
- A **blocklist** device under an active **allow** override has
  unrestricted internet for the window (override ACCEPT preempts
  schedule + `KIDBLOCK_DOMAINS`).

### Lifecycle

- A new override row carries an `expiry-epoch` (UTC seconds since 1970)
  set to `now + <minutes>*60`. Subsequent `kidblock.sh reapply` ticks
  prune expired rows before rebuilding `KIDBLOCK_TIME`, so the chain
  reverts to the schedule the moment the override is gone.
- Reboot survival: `kidblock-overrides.conf` lives at
  `/config/scripts/`, which is the persistent EdgeOS config partition.
  The boot hook (`kidblock-init.sh`) runs `kidblock.sh init`, which
  prunes expired rows then rebuilds the chain.
- Pre-DM9 single-global-override migration: the legacy
  `/var/run/kidblock.override` file (if it still exists on first DM9
  boot) is one-shot promoted into per-MAC entries for every controlled
  MAC, then the legacy file is removed. Idempotent.

## Commands

```bash
# Install / refresh
sudo /config/scripts/kidblock.sh install-domains      # blocklist (dnsmasq + ipsets + iptables)
sudo /config/scripts/kidblock.sh install-allowlist    # whitelist (dnsmasq + ipsets + iptables)
sudo /config/scripts/kidblock.sh reapply              # re-evaluate chains against current confs

# Inspect
sudo /config/scripts/kidblock.sh status               # human-readable summary (incl. per-MAC overrides)
sudo /config/scripts/kidblock.sh status <MAC>         # one-line override state for a single MAC
sudo ipset list kidblock_domains_v4                   # currently-resolved blocklisted IPs
sudo ipset list kidblock_allow_v4                     # currently-resolved allowlisted IPs

# Override (per-MAC -- DM9)
sudo /config/scripts/kidblock.sh override-block 98:59:7a:8b:a5:8d 60
sudo /config/scripts/kidblock.sh override-allow 7c:50:79:f7:db:13 30
sudo /config/scripts/kidblock.sh clear-override 98:59:7a:8b:a5:8d

# Override (bulk -- every controlled MAC)
sudo /config/scripts/kidblock.sh override-block --all 1440  # bedtime kill, 24h ceiling
sudo /config/scripts/kidblock.sh override-allow --all 30
sudo /config/scripts/kidblock.sh clear-override --all

# Override (back-compat, no MAC = --all)
sudo /config/scripts/kidblock.sh override-block 30          # alias for --all 30
sudo /config/scripts/kidblock.sh override-allow 30          # alias for --all 30
sudo /config/scripts/kidblock.sh clear-override             # alias for --all

# Tear down
sudo /config/scripts/kidblock.sh uninstall-domains
sudo /config/scripts/kidblock.sh uninstall-allowlist
```

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
