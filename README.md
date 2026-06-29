# KidBlock — scheduled internet control for EdgeRouter Pro 8

A small system that blocks specific devices (by MAC address) from the internet
on a schedule, with on-demand overrides from your Windows notebook — driven
either by desktop **PowerShell shortcuts** or by the **KidBlockUI** WPF control
panel (`windows-ui/`). Optionally also blocks domains (YouTube, etc.) via
router-side DNS.

## How it works

```
+---------------------------+        SSH (key auth)         +-----------------------+
|  Notebook (Windows)       |  ───────────────────────►     |  EdgeRouter Pro 8     |
|                           |                               |                       |
|  Desktop shortcuts:       |                               |  /config/scripts/     |
|   - Override Allow        |                               |    kidblock.sh        |
|   - Override Block        |                               |    kidblock-macs.conf |
|   - Block Now / Allow Now |                               |    kidblock-schedule  |
|   - Status                |                               |    kidblock-domains   |
|   - Edit Devices/Schedule |                               |                       |
|                           |                               |  task-scheduler ticks |
|  Each shortcut runs a     |                               |  every 1 min and runs |
|  PowerShell script that   |                               |  'kidblock.sh reapply'|
|  just SSHes to the router |                               |  which sets iptables  |
|  and runs kidblock.sh.    |                               |  to match the         |
|                           |                               |  schedule + override. |
+---------------------------+                               +-----------------------+
```

The schedule lives **on the router**, so blocking still works if your notebook
is asleep or offline. The notebook is only used for overrides and convenience.

State transitions use **iptables** directly — they don't rewrite the EdgeOS
config, so they don't wear out the router's flash and don't show up in
`show configuration`. A boot hook reinstates the iptables chain after reboot.

## Control surfaces — which one do I use?

Two front-ends drive the **same** router-side `kidblock.sh` over the **same** SSH
key (`~/.ssh/kidblock_ed25519`); they're interchangeable and can be used side by
side. Neither stores any state of its own — the schedule and overrides live on
the router, so a change made in one shows up immediately in the other.

| | **PowerShell shortcuts** (`windows/`) | **KidBlockUI** WPF app (`windows-ui/`) |
|---|---|---|
| Best for | quick, fire-and-forget overrides from the Desktop | sitting down to see and change everything |
| What you get | one action per shortcut: Status, Override Allow/Block, Block/Allow Now, Clear Override, Edit Devices/Schedule | a full GUI: live state, edit the schedule + device list, manage domain blocklists, tail the router log, "why is this blocked?" diagnostics, apply-with-confirm/diff |
| Setup | run the one-time scripts in *Installation* below | build the .NET 8 app — see [`windows-ui/KidBlockUI/README.md`](windows-ui/KidBlockUI/README.md) |
| Runs as | transient PowerShell windows | a persistent window with a system-tray icon |

The PowerShell scripts are the original surface and the quickest way to get
going; the **KidBlockUI** app is the richer, primary control panel.

## Files in this project

```
delaney/                               ← repo root (C:\CC\delaney)
├── README.md                          ← you are here
├── router/                            ← files that live on the EdgeRouter
│   ├── kidblock.sh                       main script (block/allow/override/status)
│   ├── kidblock-macs.conf                list of controlled MACs
│   ├── kidblock-schedule.conf            block windows (HH:MM-HH:MM)
│   ├── kidblock-domains.conf             DNS blocklist (YouTube etc.)
│   ├── kidblock-init.sh                  boot hook → calls kidblock.sh init
│   └── router-setup-commands.txt         manual install steps (if not using Install-Router-Scripts.ps1)
├── windows/                           ← PowerShell control surface (the notebook)
│   ├── config.ps1                        central config (router IP, user, key path)
│   ├── Setup-SSH-Key.ps1                 one-time: generate SSH key + show pubkey to install
│   ├── Install-Router-Scripts.ps1        one-time: uploads router files + schedules tick
│   ├── Create-Desktop-Shortcuts.ps1      one-time: makes the .lnk shortcuts on your Desktop
│   ├── Status.ps1                        show current state
│   ├── Block-Now.ps1                     force block (reverts within 1 min)
│   ├── Allow-Now.ps1                     force allow (reverts within 1 min)
│   ├── Override-Allow.ps1                allow for N min then revert
│   ├── Override-Block.ps1                block for N min then revert
│   ├── Clear-Override.ps1                cancel an active override
│   ├── Edit-Devices.ps1                  open MAC list in Notepad, save back
│   ├── Edit-Schedule.ps1                 open schedule in Notepad, save back
│   └── Test-Blocking.ps1                 verify domain blocking from the blocked device (DNS + TCP-443 probe)
└── windows-ui/                         ← WPF control panel (the notebook; primary surface)
    ├── KidBlockUI.sln                     Visual Studio solution
    └── KidBlockUI/                        .NET 8 WPF app (Syncfusion Ribbon + FluentDark)
        ├── KidBlockUI.csproj                 net8.0-windows; SSH.NET + CommunityToolkit.Mvvm + Syncfusion v33
        ├── appsettings.json                  router host/user/key path + router script paths
        ├── App.xaml(.cs)                      startup: registers the Syncfusion licence, applies FluentDark, inits the tray
        ├── Views/                             windows, dialogs, and the Ribbon shell
        ├── ViewModels/                        MVVM view-models (CommunityToolkit.Mvvm)
        ├── Models/                            Device / RouterState / ScheduleWindow / DomainEntry / LogEntry
        ├── Services/                          RouterClient (SSH.NET) + config/schedule/domains parsers
        ├── Themes/                            Theme.xaml semantic colour keys (PaletteLint-gated)
        ├── Resources/                         domain-categories.json, etc.
        └── README.md                          build prerequisites + how to run
```

## Installation — do this once, in order

### 0. Prerequisites on the notebook

You need **OpenSSH Client** (built into Windows 10/11). Verify by running
`ssh -V` in PowerShell. If you get "command not found":
*Settings → Apps → Optional features → Add a feature → OpenSSH Client.*

### 1. Verify the MAC address

Open `router/kidblock-macs.conf` and confirm the MAC matches the device you
want to control. The current value is:

```
98:59:7a:8b:a5:8d   DESKTOP-3VJTN3A (Adam Desktop, test target)
```

To see all DHCP leases on the router (matching device names to MACs):
```powershell
ssh ubnt@192.168.200.1 'show dhcp leases'
```

### 2. Adjust the schedule (optional)

`router/kidblock-schedule.conf` is currently set to block:
- 00:00 - 09:00 (before 9am)
- 16:00 - 17:30 (4pm - 5:30pm)
- 19:00 - 24:00 (after 7pm)

So allowed = 09:00-16:00 and 17:30-19:00. Edit the file if you want different
windows. You can also do this after install via the **Edit Schedule** desktop
shortcut.

### 3. Set up SSH key auth

In PowerShell, from the project folder:
```powershell
cd C:\CC\delaney\windows
powershell -ExecutionPolicy Bypass -File .\Setup-SSH-Key.ps1
```

The script will:
1. Generate `kidblock_ed25519` in `~/.ssh/` (with no passphrase, so scripts
   can run non-interactively).
2. Print your public key and copy it to your clipboard.
3. Show you a block of EdgeOS commands. Open a separate PowerShell window,
   `ssh ubnt@192.168.200.1`, log in with your password, and paste the commands.
   They install the public key into the router's persistent config (survives
   reboots and firmware upgrades).
4. Test passwordless login when you press Enter.

Once it says "SUCCESS: passwordless SSH is working", you're done with step 3.

### 4. Install the router scripts

Still in the `windows/` folder:
```powershell
powershell -ExecutionPolicy Bypass -File .\Install-Router-Scripts.ps1
```

This uploads the four config files + the main script + the boot hook,
configures the 1-minute task-scheduler tick, initializes iptables, and asks
if you want to enable the DNS blocklist for YouTube etc.

### 5. Create the desktop shortcuts

```powershell
powershell -ExecutionPolicy Bypass -File .\Create-Desktop-Shortcuts.ps1
```

A `KidBlock` folder will appear on your Desktop with 8 shortcuts.

### 6. Verify

Double-click `KidBlock — Status`. You should see something like:
```
Current applied     : block        (or allow, depending on time of day)
Schedule says now   : block
Effective desired   : block
Override            : none
Schedule (block windows):
  00:00-09:00
  16:00-17:30
  19:00-24:00
Controlled devices (MACs):
  98:59:7a:8b:a5:8d   DESKTOP-3VJTN3A (Adam Desktop, test target)
iptables KIDBLOCK rules:
  Chain KIDBLOCK (1 references)
  target     prot opt source         destination
  DROP       all  --  0.0.0.0/0      0.0.0.0/0    MAC 98:59:7A:8B:A5:8D
```

## Day-to-day usage

| Want to... | Do this |
|---|---|
| Let the device online for 45 min right now | Double-click **Override Allow**, type `45` |
| Block the device for 30 min right now | Double-click **Override Block**, type `30` |
| Cancel an active override (snap back to schedule) | Double-click **Clear Override** |
| Check what's currently happening | Double-click **Status** |
| Force-block right now (until next tick) | Double-click **Block Now** |
| Add or remove a device | Double-click **Edit Devices**, save in Notepad |
| Change the schedule | Double-click **Edit Schedule**, save in Notepad |

**About "Block Now" vs "Override Block":** *Block Now* sets the state, but the
1-minute tick on the router will revert it to whatever the schedule says next.
*Override Block* installs a timed override that the tick honors until it expires.
For most situations you want one of the **Override** actions.

## Adding more devices later

Either:
- Use the **Edit Devices** shortcut, add a line like
  `aa:bb:cc:dd:ee:ff   Kid's iPad`, save, close Notepad. Changes apply within
  a minute (or you can use Override-Allow/Block to force-reapply).
- Or SSH to the router and edit `/config/scripts/kidblock-macs.conf` directly,
  then run `sudo /config/scripts/kidblock.sh reapply`.

## Per-device domain blocking — how it works

The `kidblock-domains.conf` list (YouTube etc.) is blocked **only for the MACs
in `kidblock-macs.conf`**. Other devices on the network can still use those
sites normally. The technique:

1. dnsmasq is told `ipset=/youtube.com/kidblock_domains_v4,kidblock_domains_v6`.
   When *any* client on the network resolves `youtube.com`, dnsmasq adds the
   resolved IP(s) to those ipsets in addition to answering the query.
2. iptables has a rule: "if source MAC is in the controlled list AND
   destination IP is in `kidblock_domains_v4`, DROP."
3. The controlled device can still resolve `youtube.com` (DNS answer flows
   normally), but the moment it tries to *connect* to one of YouTube's IPs,
   the packet is dropped. Other devices have no such rule and connect fine.

This means:
- Adding/removing devices from `kidblock-macs.conf` automatically extends or
  retracts which devices are domain-blocked (next tick rebuilds the rules).
- The ipsets are populated on demand. The first connection after install
  might "leak" if the device already has an IP cached. Subsequent queries
  populate the set and block.
- ipsets don't persist across reboots; the boot hook rebuilds them and
  dnsmasq repopulates as queries come in.

### Hardening DNS blocking

Per-device blocking only works if the device queries DNS through this router.
Several things can bypass that:

1. **The device has a hardcoded DNS server** (Chromecast, smart TVs, etc. often
   query `8.8.8.8` directly). The ipset never gets populated because the
   resolution never touches your dnsmasq.
   *Fix:* on the router, transparently redirect outbound port 53 to the router:
   ```
   configure
   set service nat rule 4000 description 'Force LAN DNS to router'
   set service nat rule 4000 type destination
   set service nat rule 4000 inbound-interface eth1   # your LAN interface
   set service nat rule 4000 protocol tcp_udp
   set service nat rule 4000 destination port 53
   set service nat rule 4000 inside-address address 192.168.200.1
   commit; save; exit
   ```

2. **The device uses DNS-over-HTTPS (DoH)** — Chrome/Firefox/iOS/Android Private
   DNS. The resolution happens via HTTPS to e.g. `dns.google`, completely
   invisible to dnsmasq.
   *Fix:* block known DoH endpoints. There are large public blocklists
   (e.g. *nextdns/dns-over-https-blocklist* on GitHub). Add them to
   `kidblock-domains.conf` and run `sudo /config/scripts/kidblock.sh install-domains`.
   This is a cat-and-mouse game with browser updates.

3. **The device uses DNS-over-TLS (DoT)** on port 853.
   *Fix:* add an EdgeOS firewall rule dropping outbound TCP 853 from the LAN
   (or just from the controlled MAC).

4. **The device uses a VPN.** *Fix:* run Pi-hole or NextDNS upstream; block
   outbound UDP 1194/4500/500 and common WireGuard ports for the device's MAC.

For a stricter setup, point the router's DNS forwarder at NextDNS (paid,
per-device profiles with built-in YouTube blocking) or run Pi-hole. The
kidblock scheduling part still works either way — this is just about which
resolver runs the domain blocklist.

## Troubleshooting

**"BatchMode=yes" makes ssh fail without trying the password.** That's
intentional — scripts shouldn't hang on a password prompt. If a script reports
an SSH failure, run `ssh -i ~/.ssh/kidblock_ed25519 ubnt@192.168.200.1` manually
to see the real error.

**The 1-minute tick isn't firing.** Check
```
ssh ubnt@192.168.200.1 'show system task-scheduler'
ssh ubnt@192.168.200.1 'tail -50 /var/log/kidblock.log'
```

**iptables rules disappeared.** Some EdgeOS operations flush FORWARD. The
1-minute tick will re-add them, or run `sudo /config/scripts/kidblock.sh init`
manually.

**The blocked device can still reach the internet.** Most likely it changed
MAC (modern phones/laptops randomize MACs per Wi-Fi network) or it's on a
different network interface. Verify the MAC the router actually sees with
`show dhcp leases` and update `kidblock-macs.conf`.

**I want to undo everything.**
```bash
ssh ubnt@192.168.200.1
configure
delete system task-scheduler task kidblock-tick
commit; save; exit
sudo /config/scripts/kidblock.sh uninstall-dns
sudo iptables -D FORWARD -j KIDBLOCK 2>/dev/null
sudo ip6tables -D FORWARD -j KIDBLOCK 2>/dev/null
sudo iptables -F KIDBLOCK 2>/dev/null && sudo iptables -X KIDBLOCK 2>/dev/null
sudo ip6tables -F KIDBLOCK 2>/dev/null && sudo ip6tables -X KIDBLOCK 2>/dev/null
sudo rm -f /config/scripts/kidblock* /config/scripts/post-config.d/kidblock-init.sh
```
And delete the `KidBlock` folder on your Desktop.

## Notes & limitations

- **MAC randomization.** Modern phones/laptops randomize their MAC per
  network. Once you connect the device to your network for the first time,
  the MAC it picks is stable for that network — but it differs from the
  device's "real" hardware MAC. Always copy the MAC from the router's DHCP
  lease table, not from the device's settings.
- **A determined user can bypass this.** Wired connection with a manual MAC,
  VPN, mobile hotspot from a phone, or borrowing another device all defeat
  any router-level control. This is a "remove temptation" tool, not a
  security boundary.
- **Time accuracy depends on the router's clock.** EdgeRouter uses NTP by
  default; if your router shows the wrong time (`ssh ubnt@... 'date'`) the
  schedule will fire at the wrong real-world time. Set `system ntp server`
  if needed.
- **Logs are at `/var/log/kidblock.log` on the router.** They survive reboots.
