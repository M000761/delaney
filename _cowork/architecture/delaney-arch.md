<!-- preset-freshness: {"generated_by": "audit-and-refresh-arch.md", "last_refresh_ts": "2026-08-31T03:25:10Z", "last_refresh_sha": "0cf18bb37d448cc6acfa04a31a942867eb0de924", "reads": ["C:\\CC\\delaney", "_cowork/dashboard-delaney.json", "_cowork/GLOSSARY.md", "_cowork/PROJECT-MAP.json"]} -->

# [Delaney] Architecture snapshot — delaney — 2026-08-31

> Static architecture documentation (ARCH-VOCAB v3.0). delaney's `views:` manifest declares only `boundary-map`, so this file carries exactly that one view. **No source file, script, config or dependency has changed since the last refresh** — the only commit in the window is `c067a7c`, the 2026-07-30 arch-refresh commit that landed the previous snapshot itself, making this the second consecutive window whose sole commit is the previous refresh. The topology remains the DM1–DM24 shape: a router-enforced internet-control system driven from a Windows notebook by two interchangeable front-ends over SSH. **What did change is this map's curation**: the node set is consolidated from 13 to 9 so the `process` bucket comes inside the `_cowork/ARCH-VOCAB.md` 1–5 ceiling it had been exceeding at 9. Companion Phase A audit at `_cowork/reports/2026-08-31T02-54-27Z-audit-delaney.md`.

## System overview

**KidBlock** — scheduled internet control for an EdgeRouter Pro 8. A Windows notebook drives the router over SSH (key auth) two ways: the desktop **action scripts** (`windows/*.ps1` dot-sourcing `windows/config.ps1`, whose `Invoke-RouterCmd` helper SSHes in and runs `sudo kidblock.sh <command>`), and the **KidBlockUI** WPF app (`windows-ui/KidBlockUI/`, .NET 8 + SSH.NET + CommunityToolkit.Mvvm), a Syncfusion Ribbon shell (DM16) with six static tabs hosting the bespoke Devices grid / ScheduleTimeline / DomainsControl / LogTailPanel, a per-row Why? dialog driven by `kidblock.sh explain-mac` (DM22), and a minimise-to-tray NotifyIcon (DM18). The router enforces everything on-device, so blocking keeps working when the notebook sleeps: `kidblock.sh` reads four repo-tracked `.conf` files (controlled MACs · block-window schedule · DNS blocklist · DM6 allowlist) plus the router-resident per-MAC overrides store (DM9, created/pruned at every reapply), and programs the EdgeOS enforcement plane. An EdgeOS task-scheduler tick re-applies desired state every minute and a boot hook (`kidblock-init.sh`) rebuilds chains after reboot — that on-device autonomy is the whole design.

```boundary-map
{
  "title": "Delaney KidBlock boundary map",
  "nodes": [
    { "id": "actions",    "label": "Desktop action scripts + config.ps1 SSH helper (Block/Allow/Override/Status)", "kind": "process", "file": "windows/config.ps1" },
    { "id": "kidblockui", "label": "KidBlockUI WPF Ribbon shell incl. the DM22 Why? dialog (Syncfusion FluentDark, SSH.NET)", "kind": "process", "file": "windows-ui/KidBlockUI/Views/MainWindow.xaml" },
    { "id": "kidblock",   "label": "kidblock.sh (block/allow/override/reapply/status/explain-mac)", "kind": "process", "file": "router/kidblock.sh" },
    { "id": "tick",       "label": "EdgeOS task-scheduler tick (1-min reapply) + boot hook", "kind": "process",     "file": "router/kidblock-init.sh" },
    { "id": "enforce",    "label": "EdgeOS enforcement plane: iptables/ip6tables + dnsmasq + ipset", "kind": "process" },
    { "id": "confs",      "label": "kidblock-*.conf (MACs · schedule · domains · DM6 allowlist)", "kind": "persistence", "file": "router/kidblock-macs.conf" },
    { "id": "overrides",  "label": "kidblock-overrides.conf (per-MAC overrides, router-resident, DM9)", "kind": "persistence" },
    { "id": "routerlog",  "label": "/var/log/kidblock.log (router-resident event log)",     "kind": "persistence" },
    { "id": "router",     "label": "EdgeRouter Pro 8 (EdgeOS, 192.168.200.1)",             "kind": "device" }
  ],
  "edges": [
    { "source": "actions",    "target": "router",    "protocol": "SSH key-auth (ubnt@.200.1)" },
    { "source": "kidblockui", "target": "router",    "protocol": "SSH.NET (RouterClient.cs)" },
    { "source": "kidblockui", "target": "routerlog", "protocol": "SSH tail stream (DM5/DM10)" },
    { "source": "router",     "target": "kidblock",  "protocol": "sudo kidblock.sh" },
    { "source": "tick",       "target": "kidblock",  "protocol": "1-min reapply / boot init" },
    { "source": "kidblock",   "target": "confs",     "protocol": "read 4 .conf files" },
    { "source": "kidblock",   "target": "overrides", "protocol": "read + prune expired (DM9)" },
    { "source": "kidblock",   "target": "enforce",   "protocol": "program chains + ipsets" },
    { "source": "kidblock",   "target": "routerlog", "protocol": "log() append" }
  ]
}
```

**Curation note — what the consolidation merged, and why.** The prior map carried 13 nodes with 9 in the `process` bucket, against ARCH-VOCAB's 1–5-per-kind ceiling and the generator's 8–12 total. Three merges bring it to 9 nodes with `process` at 5, and each is a correction rather than a compression:

- **`actions` + `cfg` → `actions`.** The former `actions -> cfg` edge was `"dot-source config.ps1"` — a PowerShell include, which is not a boundary crossing. `config.ps1` is where `Invoke-RouterCmd` lives, so the SSH hop it performs is now the `actions -> router` edge directly.
- **`whymac` → folded into `kidblockui`.** The DM22 Why? dialog is a window inside the same WPF process, not a separate one. Its behaviour is unchanged and worth stating: it walks the per-MAC verdict by calling `kidblock.sh explain-mac` **read-only on a 5-second refresh**, over the same SSH.NET client as the rest of the app.
- **`iptables` + `dnsmasq` + `ipset` → `enforce`.** These are three subsystems of one on-device enforcement plane that `kidblock.sh` programs in a single pass, not three boundaries the project crosses. The mechanism detail they carried is preserved here: `kidblock.sh` builds the `KIDBLOCK_TIME`, `KIDBLOCK_DOMAINS` and DM6 `KIDBLOCK_WHITELIST` chains — with DM21 having reversed `ensure_chains()` iteration so `KIDBLOCK_TIME` and the override ACCEPT land **first** in FORWARD — writes `/etc/dnsmasq.d` and restarts dnsmasq (DM10 adds `log-queries` observability), and creates the `kidblock_domains_v4/v6` + `kidblock_allow_v4/v6` sets. The internal `dnsmasq -> ipset` relationship the prior map drew as an edge is the `ipset=` directive: dnsmasq populates the sets **as it resolves**, which is what makes domain blocking work without a proxy.

Kinds in play: **process** (notebook scripts · KidBlockUI · `kidblock.sh` · the EdgeOS tick/boot hook · the enforcement plane), **persistence** (the four repo-tracked `.conf` files · the router-resident overrides store · `/var/log/kidblock.log`), **device** (the EdgeRouter itself). The SSH hop is the network crossing — drawn as the `→ router` edges.

## Diff vs last run

Prior snapshot: `last_refresh_sha=2000af2c` (kernel, 2026-07-30; delaney HEAD then `a937b7f`). This run: kernel HEAD `0cf18bb3`, delaney HEAD `c067a7c` — **1 commit between, and that commit is the prior refresh landing itself**. No source file, script, config or dependency changed.

- **Added/removed System overview nodes:** **+1 / −5**, 13 → **9**. Added `enforce`; removed `cfg`, `whymac`, `iptables`, `dnsmasq`, `ipset`. Every removal is a curation correction, not a topology change — see the curation note above. Edges 14 → 9 for the same reason.
- **Curation-limit compliance:** the map is now inside both stated limits for the first time — 9 nodes (band 8–12; was 13) and `process` at 5 (ceiling 5; was 9). Nine of the prior fourteen `protocol` strings exceeded the ≤30-character limit; none does now.
- **Added/removed views:** none (`views:` manifest unchanged at `["boundary-map"]`; no `tech-stack` view declared).
- **Added/removed deps:** n/a (no `tech-stack` view declared). For the record `windows-ui/KidBlockUI/KidBlockUI.csproj` is unchanged at 8 direct refs — `SSH.NET`, `CommunityToolkit.Mvvm 8.2.2`, and six `Syncfusion.*.WPF 33.2.13` packages.
- **Dependency claim corrected.** The prior snapshot recorded that two Syncfusion packages had "zero source references". That test is invalid for this project: Syncfusion controls resolve through `xmlns:syncfusion="http://schemas.syncfusion.com/wpf"`, so no source line ever names an assembly and a package-name grep returns zero even for packages plainly in use — running it this run also marked `Syncfusion.Tools.WPF` unused, while the shell uses `<syncfusion:Ribbon>`, `<syncfusion:RibbonTab>`, `<syncfusion:RibbonBar>`, `<syncfusion:RibbonButton>` and `<syncfusion:DockingManager>` from exactly that package. By the correct element-name test, **`Syncfusion.SfGrid.WPF` is genuinely unused** (no `SfDataGrid` or `SfTreeGrid` anywhere; the Devices grid is the WPF in-box `<DataGrid>`), and **`Syncfusion.Shared.WPF` is a Tools.WPF base dependency that no source-level test can show unused**. Both dispositions are carried in the Phase A audit, since delaney declares no `tech-stack` view.

## Orphan views

(no orphan views found) — delaney declares no `sitemap` view, so there is no view set to orphan-tag.

## Deprecated / replace libs

(no deprecated or replace-status libs) — no `tech-stack` view is declared for delaney. The one live dependency disposition this run (`Syncfusion.SfGrid.WPF`, unused by element-name test) and the one retraction (`Syncfusion.Shared.WPF`, previously mis-flagged) are carried in the Phase A audit report rather than promoted into a view this project does not declare.

<!-- eof: arch-snapshot-v1.9 -->
