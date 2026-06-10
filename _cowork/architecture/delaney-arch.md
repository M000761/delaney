<!-- preset-freshness: {"generated_by": "audit-and-refresh-arch.md", "last_refresh_ts": "2026-06-10T03:11:29Z", "last_refresh_sha": "2c12bbc1894b2da646e009429af92f230bfdb325", "reads": ["C:/CC/delaney", "_cowork/dashboard-delaney.json", "_cowork/GLOSSARY.md", "_cowork/PROJECT-MAP.json"]} -->

# [Delaney] Architecture snapshot — delaney — 2026-06-10

> Static architecture documentation (ARCH-VOCAB v3.0). delaney's `views:` manifest declares only `boundary-map`, so this file carries exactly that one view. This refresh lands the **DM1–DM11 KidBlock UI program** (2026-06-03 → 06-06): a .NET 8 WPF control panel (`windows-ui/KidBlockUI/`, SSH.NET + CommunityToolkit.Mvvm) that joins the PowerShell action scripts as a second notebook-side SSH path, plus the DM6 whitelist mode (allowlist conf + dedicated chain/ipsets), the DM9 router-resident per-MAC overrides store, and the DM5/DM10 log-tail observability stream. The prior snapshot (2026-06-02) predates all of it. Companion Phase A audit at `C:\CC\delaney\_cowork\reports\2026-06-10T03-11-29Z-audit-delaney.md`.

## System overview

**KidBlock** — scheduled internet control for an EdgeRouter Pro 8. A Windows notebook drives the router over SSH (key auth) two ways: the desktop **action scripts** (dot-sourcing `config.ps1`, whose `Invoke-RouterCmd` helper SSHes in and runs `sudo kidblock.sh <command>`), and since DM1–DM11 the **KidBlockUI** WPF app (`RouterClient.cs` over SSH.NET), which adds device rows with per-row overrides + bulk actions (DM3/DM9/DM11), a schedule-timeline editor with apply-diff (DM2), categorized domain toggles (DM4), an Allowlist pane for whitelist-mode devices (DM6), DHCP-lease fetch (DM7), and a live tail of the router log with parsed event + DNS-query colorization (DM5/DM10). The router enforces everything on-device, so blocking keeps working when the notebook sleeps: `kidblock.sh` reads four repo-tracked `.conf` files (controlled MACs · block-window schedule · DNS blocklist · DM6 allowlist) plus the router-resident per-MAC overrides store (DM9, created/pruned at every reapply), and programs the EdgeOS internals — **iptables** (`KIDBLOCK_TIME` + `KIDBLOCK_DOMAINS` + DM6 `KIDBLOCK_WHITELIST` chains), **dnsmasq** (resolves + populates ipsets; DM10 `log-queries` observability), and **ipset** (`kidblock_domains_v4/v6` + `kidblock_allow_v4/v6` sets). An EdgeOS task-scheduler tick re-applies desired state every minute and a boot hook (`kidblock-init.sh`) rebuilds chains after reboot — that on-device autonomy is the whole design.

Kinds in play: **process** (notebook PowerShell scripts · KidBlockUI WPF app · router-side `kidblock.sh` + iptables/dnsmasq/ipset internals · the EdgeOS tick/boot hook), **persistence** (the four repo-tracked `.conf` files · the router-resident overrides store · `/var/log/kidblock.log`), **device** (the EdgeRouter itself). The SSH hop is the network crossing — drawn as the two `→ EdgeRouter` edges.

```boundary-map
{
  "title": "Delaney KidBlock boundary map",
  "nodes": [
    { "id": "actions",    "label": "Desktop action scripts (Block/Allow/Override/Status)", "kind": "process",     "file": "windows/Block-Now.ps1" },
    { "id": "cfg",        "label": "config.ps1 (Invoke-RouterCmd SSH helper)",             "kind": "process",     "file": "windows/config.ps1" },
    { "id": "kidblockui", "label": "KidBlockUI WPF (DM1-11 control panel, SSH.NET)",       "kind": "process",     "file": "windows-ui/KidBlockUI/Views/MainWindow.xaml" },
    { "id": "kidblock",   "label": "kidblock.sh (block/allow/override/reapply/status)",     "kind": "process",     "file": "router/kidblock.sh" },
    { "id": "tick",       "label": "EdgeOS task-scheduler tick (1-min reapply) + boot hook","kind": "process",     "file": "router/kidblock-init.sh" },
    { "id": "iptables",   "label": "iptables/ip6tables (KIDBLOCK_TIME + _DOMAINS + _WHITELIST chains)", "kind": "process" },
    { "id": "dnsmasq",    "label": "dnsmasq (DNS forwarder + ipset population + DM10 log-queries)",    "kind": "process" },
    { "id": "ipset",      "label": "ipset (kidblock_domains_v4/v6 + kidblock_allow_v4/v6)", "kind": "process" },
    { "id": "confs",      "label": "kidblock-*.conf (MACs · schedule · domains · DM6 allowlist)", "kind": "persistence", "file": "router/kidblock-macs.conf" },
    { "id": "overrides",  "label": "kidblock-overrides.conf (per-MAC overrides, router-resident, DM9)", "kind": "persistence" },
    { "id": "routerlog",  "label": "/var/log/kidblock.log (router-resident event log)",     "kind": "persistence" },
    { "id": "router",     "label": "EdgeRouter Pro 8 (EdgeOS, 192.168.200.1)",             "kind": "device" }
  ],
  "edges": [
    { "source": "actions",    "target": "cfg",       "protocol": "dot-source config.ps1" },
    { "source": "cfg",        "target": "router",    "protocol": "SSH key-auth (ubnt@192.168.200.1)" },
    { "source": "kidblockui", "target": "router",    "protocol": "SSH.NET key-auth (RouterClient.cs)" },
    { "source": "router",     "target": "kidblock",  "protocol": "runs sudo /config/scripts/kidblock.sh" },
    { "source": "tick",       "target": "kidblock",  "protocol": "1-min reapply / boot init" },
    { "source": "kidblock",   "target": "confs",     "protocol": "read MACs + schedule + domains + allowlist" },
    { "source": "kidblock",   "target": "overrides", "protocol": "read + prune expired rows (DM9)" },
    { "source": "kidblock",   "target": "iptables",  "protocol": "build chains (MAC DROP / whitelist default-DROP)" },
    { "source": "kidblock",   "target": "dnsmasq",   "protocol": "write /etc/dnsmasq.d + restart" },
    { "source": "kidblock",   "target": "ipset",     "protocol": "create/match domain + allow sets" },
    { "source": "dnsmasq",    "target": "ipset",     "protocol": "ipset= populates on resolve" },
    { "source": "kidblock",   "target": "routerlog", "protocol": "log() append" },
    { "source": "kidblockui", "target": "routerlog", "protocol": "SSH tail stream (DM5 events + DM10 DNS kinds)" }
  ]
}
```

## Diff vs last run

Prior snapshot: `last_refresh_sha=712c7df` (2026-06-02), which saw only AGENTS.md re-stamps. This run: kernel HEAD `2c12bbc`, delaney HEAD `30a0c85` (DM11) — 12 delaney commits since, the entire **DM1–DM11 KidBlock UI program**: foundation SSH client + parsers + read-only window (DM1), schedule timeline + apply-diff (DM2), per-device KILL/Allow/Clear + badges (DM3), categorized domain toggles (DM4), live log tail (DM5), whitelist mode (DM6), DHCP-lease fetch (DM7), layout rework (DM8), per-MAC override end-to-end (DM9), DNS-query observability (DM10), allow-duration SplitButtons (DM11).

- **Added/removed System overview nodes:** +4 / −3. Added `kidblockui` (the new WPF process + second SSH path), `overrides` (DM9 router-resident per-MAC store), `routerlog` (`/var/log/kidblock.log`, now load-bearing as the UI's observability stream); the three per-conf nodes (`macs`/`sched`/`domains`) merged into one `confs` node to admit the DM6 allowlist while staying inside the 8–12 hand-curation cap. Net 11 → 12 nodes.
- **Added/removed views:** none (`views:` manifest unchanged at `["boundary-map"]` — no tech-stack view declared; for the record the new app's direct deps are `SSH.NET 2025.1.0` + `CommunityToolkit.Mvvm 8.2.2` per `windows-ui/KidBlockUI/KidBlockUI.csproj`).
- **Added/removed deps:** n/a (no `tech-stack` view declared).

## Orphan views

(no orphan views found)

## Deprecated / replace libs

(no deprecated or replace-status libs)

<!-- eof: arch-snapshot-v1.9 -->
