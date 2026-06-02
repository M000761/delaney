<!-- preset-freshness: {"generated_by": "audit-and-refresh-arch.md", "last_refresh_ts": "2026-06-02T05:47:24Z", "last_refresh_sha": "712c7dface68f6e35361bfdeafef6ff09a91c8dd", "reads": ["<delaney-mount>", "_cowork/dashboard-delaney.json", "_cowork/GLOSSARY.md", "_cowork/PROJECT-MAP.json"]} -->

# [Delaney] Architecture snapshot — delaney — 2026-06-02

> Static architecture documentation (ARCH-VOCAB v3.0). delaney's `views:` manifest declares only `boundary-map`, so this file carries exactly that one view. Authored, not derived; regenerated on demand under the KM359 generator.

## System overview

**KidBlock** — scheduled internet control for an EdgeRouter Pro 8. A Windows notebook drives the router over SSH (key auth); the router enforces a per-MAC block schedule entirely on-device, so blocking keeps working when the notebook is asleep. The notebook side is just convenience (overrides + status from desktop shortcuts).

The boundary crossings: desktop **action scripts** dot-source `config.ps1`, whose `Invoke-RouterCmd` helper SSHes to the EdgeRouter and runs `sudo kidblock.sh <command>`. On the router, **kidblock.sh** reads three `.conf` files (controlled MACs · block-window schedule · DNS blocklist) and programs the EdgeOS internals — **iptables** (the `KIDBLOCK_TIME` + `KIDBLOCK_DOMAINS` chains drop traffic from controlled MACs), **dnsmasq** (resolves + populates ipsets via `ipset=` directives), and **ipset** (the `kidblock_domains_v4/v6` sets that join domain → IP for per-device domain blocking). An EdgeOS task-scheduler tick re-applies the desired state every minute, and a boot hook (`kidblock-init.sh`) rebuilds the chains after reboot — that on-device autonomy is the whole design.

Kinds in play: **process** (notebook PowerShell scripts · router-side `kidblock.sh` + the iptables/dnsmasq/ipset internals · the EdgeOS tick/boot hook), **persistence** (the three `.conf` files), **device** (the EdgeRouter itself). The SSH hop is the one network crossing — drawn as the `config.ps1 → EdgeRouter` edge. Router internals are abstract (no project file); the notebook scripts, `kidblock.sh`, the boot hook, and the `.conf` files are file-backed.

```boundary-map
{
  "title": "Delaney KidBlock boundary map",
  "nodes": [
    { "id": "actions",  "label": "Desktop action scripts (Block/Allow/Override/Status)", "kind": "process",     "file": "windows/Block-Now.ps1" },
    { "id": "cfg",      "label": "config.ps1 (Invoke-RouterCmd SSH helper)",             "kind": "process",     "file": "windows/config.ps1" },
    { "id": "kidblock", "label": "kidblock.sh (block/allow/override/reapply/status)",     "kind": "process",     "file": "router/kidblock.sh" },
    { "id": "tick",     "label": "EdgeOS task-scheduler tick (1-min reapply) + boot hook","kind": "process",     "file": "router/kidblock-init.sh" },
    { "id": "iptables", "label": "iptables/ip6tables (KIDBLOCK_TIME + KIDBLOCK_DOMAINS chains)", "kind": "process" },
    { "id": "dnsmasq",  "label": "dnsmasq (DNS forwarder + ipset population)",           "kind": "process" },
    { "id": "ipset",    "label": "ipset (kidblock_domains_v4/v6 sets)",                  "kind": "process" },
    { "id": "macs",     "label": "kidblock-macs.conf (controlled MACs)",                 "kind": "persistence", "file": "router/kidblock-macs.conf" },
    { "id": "sched",    "label": "kidblock-schedule.conf (block windows)",              "kind": "persistence", "file": "router/kidblock-schedule.conf" },
    { "id": "domains",  "label": "kidblock-domains.conf (DNS blocklist)",                "kind": "persistence", "file": "router/kidblock-domains.conf" },
    { "id": "router",   "label": "EdgeRouter Pro 8 (EdgeOS, 192.168.200.1)",            "kind": "device" }
  ],
  "edges": [
    { "source": "actions",  "target": "cfg",      "protocol": "dot-source config.ps1" },
    { "source": "cfg",      "target": "router",   "protocol": "SSH key-auth (ubnt@192.168.200.1)" },
    { "source": "router",   "target": "kidblock", "protocol": "runs sudo /config/scripts/kidblock.sh" },
    { "source": "tick",     "target": "kidblock", "protocol": "1-min reapply / boot init" },
    { "source": "kidblock", "target": "macs",     "protocol": "read controlled MACs" },
    { "source": "kidblock", "target": "sched",    "protocol": "read block windows" },
    { "source": "kidblock", "target": "domains",  "protocol": "read DNS blocklist" },
    { "source": "kidblock", "target": "iptables", "protocol": "build chains (MAC DROP)" },
    { "source": "kidblock", "target": "dnsmasq",  "protocol": "write /etc/dnsmasq.d + restart" },
    { "source": "kidblock", "target": "ipset",    "protocol": "create/match domain sets" },
    { "source": "dnsmasq",  "target": "ipset",    "protocol": "ipset= populates on resolve" }
  ]
}
```

## Diff vs last run

Prior snapshot: `last_refresh_sha=49fd1a0` (2026-05-22). HEAD this run: `712c7df` (2026-06-02). Only 2 delaney-side commits since: `72a958a` (chore: AGENTS.md re-stamp from kernel template per KM411) and `641dfb6` (docs(agents): AGENTS.md re-stamp per KM389) — both AGENTS.md template re-rendering, neither touched router/ or windows/ source.

- **Added/removed System overview nodes:** none (11 nodes unchanged).
- **Added/removed views:** none (`views:` manifest unchanged at `["boundary-map"]`).
- **Added/removed deps:** none (delaney has no dependency manifest — bash + PowerShell scripts + EdgeOS-resident iptables/dnsmasq/ipset).

This refresh is the freshness-marker (ts + sha) and H1-date bump only; KidBlock's boundary shape is stable.

## Orphan views

(no orphan views found)

## Deprecated / replace libs

(no deprecated or replace-status libs)

<!-- eof: arch-snapshot-v1.9 -->
