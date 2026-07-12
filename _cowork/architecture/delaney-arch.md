<!-- preset-freshness: {"generated_by": "audit-and-refresh-arch.md", "last_refresh_ts": "2026-07-12T23:34:56Z", "last_refresh_sha": "655c0ac4fc4ffcb422e2c7b41c78ea3db8520978", "reads": ["C:\\CC\\delaney", "_cowork/dashboard-delaney.json", "_cowork/GLOSSARY.md", "_cowork/PROJECT-MAP.json"]} -->

# [Delaney] Architecture snapshot — delaney — 2026-07-12

> Static architecture documentation (ARCH-VOCAB v3.0). delaney's `views:` manifest declares only `boundary-map`, so this file carries exactly that one view. The boundary topology is stable at the DM1–DM23 shape: a router-enforced internet-control system driven from a Windows notebook by two interchangeable front-ends over SSH. Since the 2026-06-26 refresh (which landed the DM12–DM23 Syncfusion Ribbon-shell migration) only **DM24** has landed — a docs-only refresh of `README.md` + a new `windows-ui/KidBlockUI/README.md`, no structural or dependency change — so this run re-attests the same 13-node map and refreshes only the marker + date. Companion Phase A audit at `C:\CC\Cowork Project Prompt Enginner\_cowork\reports\2026-07-12T23-34-56Z-audit-delaney.md`.

## System overview

**KidBlock** — scheduled internet control for an EdgeRouter Pro 8. A Windows notebook drives the router over SSH (key auth) two ways: the desktop **action scripts** (`windows/*.ps1` dot-sourcing `config.ps1`, whose `Invoke-RouterCmd` helper SSHes in and runs `sudo kidblock.sh <command>`), and the **KidBlockUI** WPF app (`windows-ui/KidBlockUI/`, .NET 8 + SSH.NET + CommunityToolkit.Mvvm), a Syncfusion Ribbon shell (DM16) with six static tabs hosting the bespoke Devices grid / ScheduleTimeline / DomainsControl / LogTailPanel, a per-row Why? dialog driven by `kidblock.sh explain-mac` (DM22), and a minimise-to-tray NotifyIcon (DM18). The router enforces everything on-device, so blocking keeps working when the notebook sleeps: `kidblock.sh` reads four repo-tracked `.conf` files (controlled MACs · block-window schedule · DNS blocklist · DM6 allowlist) plus the router-resident per-MAC overrides store (DM9, created/pruned at every reapply), and programs the EdgeOS internals — **iptables** (`KIDBLOCK_TIME` + `KIDBLOCK_DOMAINS` + DM6 `KIDBLOCK_WHITELIST` chains; DM21 reversed `ensure_chains()` iteration so `KIDBLOCK_TIME`/the override ACCEPT lands FIRST in FORWARD), **dnsmasq** (resolves + populates ipsets; DM10 `log-queries` observability), and **ipset** (`kidblock_domains_v4/v6` + `kidblock_allow_v4/v6` sets). An EdgeOS task-scheduler tick re-applies desired state every minute and a boot hook (`kidblock-init.sh`) rebuilds chains after reboot — that on-device autonomy is the whole design.

Kinds in play: **process** (notebook PowerShell scripts · KidBlockUI WPF Ribbon shell · the Why?/explain-mac read-only diagnostic path · router-side `kidblock.sh` + iptables/dnsmasq/ipset internals · the EdgeOS tick/boot hook), **persistence** (the four repo-tracked `.conf` files · the router-resident overrides store · `/var/log/kidblock.log`), **device** (the EdgeRouter itself). The SSH hop is the network crossing — drawn as the `→ EdgeRouter` edges.

```boundary-map
{
  "title": "Delaney KidBlock boundary map",
  "nodes": [
    { "id": "actions",    "label": "Desktop action scripts (Block/Allow/Override/Status)", "kind": "process",     "file": "windows/Block-Now.ps1" },
    { "id": "cfg",        "label": "config.ps1 (Invoke-RouterCmd SSH helper)",             "kind": "process",     "file": "windows/config.ps1" },
    { "id": "kidblockui", "label": "KidBlockUI WPF Ribbon shell (Syncfusion FluentDark, SSH.NET)", "kind": "process", "file": "windows-ui/KidBlockUI/Views/MainWindow.xaml" },
    { "id": "whymac",     "label": "Why? dialog (per-MAC explain-mac verdict walk, DM22)", "kind": "process",     "file": "windows-ui/KidBlockUI/Views/WhyBlockedDialog.xaml" },
    { "id": "kidblock",   "label": "kidblock.sh (block/allow/override/reapply/status/explain-mac)", "kind": "process", "file": "router/kidblock.sh" },
    { "id": "tick",       "label": "EdgeOS task-scheduler tick (1-min reapply) + boot hook","kind": "process",     "file": "router/kidblock-init.sh" },
    { "id": "iptables",   "label": "iptables/ip6tables (KIDBLOCK_TIME + _DOMAINS + _WHITELIST chains; DM21 order)", "kind": "process" },
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
    { "source": "whymac",     "target": "router",    "protocol": "SSH explain-mac (read-only, 5s refresh)" },
    { "source": "router",     "target": "kidblock",  "protocol": "runs sudo /config/scripts/kidblock.sh" },
    { "source": "tick",       "target": "kidblock",  "protocol": "1-min reapply / boot init" },
    { "source": "kidblock",   "target": "confs",     "protocol": "read MACs + schedule + domains + allowlist" },
    { "source": "kidblock",   "target": "overrides", "protocol": "read + prune expired rows (DM9)" },
    { "source": "kidblock",   "target": "iptables",  "protocol": "build chains (TIME-first; MAC DROP / whitelist)" },
    { "source": "kidblock",   "target": "dnsmasq",   "protocol": "write /etc/dnsmasq.d + restart" },
    { "source": "kidblock",   "target": "ipset",     "protocol": "create/match domain + allow sets" },
    { "source": "dnsmasq",    "target": "ipset",     "protocol": "ipset= populates on resolve" },
    { "source": "kidblock",   "target": "routerlog", "protocol": "log() append" },
    { "source": "kidblockui", "target": "routerlog", "protocol": "SSH tail stream (DM5 events + DM10 DNS kinds)" }
  ]
}
```

## Diff vs last run

Prior snapshot: `last_refresh_sha=1ab61a4` (2026-06-26, delaney HEAD `28d2e0d` / DM23). This run: kernel-side regen, delaney HEAD `655c0ac` — the only delaney commit since is **DM24** (`docs: refresh root README for the KidBlockUI WPF surface + add windows-ui/KidBlockUI/README`), a documentation-only change touching `README.md` + a new `windows-ui/KidBlockUI/README.md`.

- **Added/removed System overview nodes:** +0 / −0. Topology unchanged since 2026-06-26 (13 nodes); no boundary crossing was added or removed by the DM24 docs commit.
- **Added/removed views:** none (`views:` manifest unchanged at `["boundary-map"]`; no `tech-stack` view declared).
- **Added/removed deps:** n/a (no `tech-stack` view declared). For the record `windows-ui/KidBlockUI/KidBlockUI.csproj` is unchanged since the last run — `SSH.NET 2025.1.0` + `CommunityToolkit.Mvvm 8.2.2` + six `Syncfusion.*.WPF 33.2.13` packages, all `active`.

## Orphan views

(no orphan views found)

## Deprecated / replace libs

(no deprecated or replace-status libs)

<!-- eof: arch-snapshot-v1.9 -->
