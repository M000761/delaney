# KidBlockUI

A Windows desktop **control panel** for KidBlock — the scheduled-internet-control
system for the EdgeRouter Pro 8. It's a GUI alternative to the `windows/`
PowerShell shortcuts: same router, same SSH key, same on-router config files,
just a richer front-end.

See the [repo README](../../README.md) for what KidBlock is and how the router
side works. This file is about building and running the app.

## What it does

- **Live state** — what's currently applied (block/allow), what the schedule says now, and any active override.
- **Edit the schedule** (block windows) and the **device list** (controlled MACs) in a GUI and apply the changes back to the router.
- **Manage the domain blocklist** (YouTube, etc.) by category.
- **Tail the router log** live.
- **"Why is this blocked?"** diagnostics.
- **Apply with confirm/diff** so you see exactly what will change before it lands.
- **Minimises to a system-tray icon** (double-click, or right-click → Show / Exit).

It talks to the router over SSH (SSH.NET) and keeps no state of its own — the
schedule and overrides live on the router, so it stays in sync with the
PowerShell shortcuts.

## Stack

- **.NET 8** WPF (`net8.0-windows`), C# 12, nullable enabled.
- **Syncfusion WPF v33.2.13** (Community edition) — Ribbon shell (`Syncfusion.Tools.WPF`), `SfDataGrid`, `SfSkinManager` + the **FluentDark** theme.
- **SSH.NET** — the SSH transport to the router.
- **CommunityToolkit.Mvvm** — MVVM (`[ObservableProperty]` / `[RelayCommand]`).
- **WinForms** is enabled solely for the tray `NotifyIcon` (there is no WinForms UI otherwise).

## Prerequisites

1. **.NET 8 SDK** (the `net8.0-windows` target). `dotnet --version` should report `8.x`.

2. **A Syncfusion v33 Community licence key.** The key is **never committed**.
   Provide it one of two ways — they're checked in this order at startup:
   - the **`SYNCFUSION_LICENSE_KEY`** environment variable (user or process scope), or
   - a **`syncfusion-license.key`** file containing just the key, placed next to the
     built exe *or* anywhere from there up toward the project root (so a key dropped
     beside `KidBlockUI.csproj` is found from a `bin/...` build output too).

   `syncfusion-license.key` is **gitignored**, so it can't be committed by accident.
   With **no** key found the app still runs, but Syncfusion controls render with a
   trial banner — drop in a key to clear it. Get a free Community key from your
   Syncfusion account.

3. **SSH key auth to the router**, already set up. The app reads the **same** key the
   PowerShell scripts use — `~/.ssh/kidblock_ed25519` (see `appsettings.json` →
   `Router:KeyPath`). If you haven't set it up yet, run `windows/Setup-SSH-Key.ps1`
   once (repo README, *Installation* step 3).

## Configuration

`appsettings.json` (copied next to the exe on build) holds the router connection
and the on-router script paths:

```json
{
  "Router": {
    "Host": "192.168.200.1",
    "User": "ubnt",
    "KeyPath": "%USERPROFILE%\\.ssh\\kidblock_ed25519",
    "ScriptPath": "/config/scripts/kidblock.sh"
  }
}
```

Edit `Host` / `User` / `KeyPath` if your router differs from the defaults.

## Build & run

From this folder (`windows-ui/KidBlockUI/`):

```powershell
dotnet build           # compile
dotnet run             # build + launch
```

Or open `windows-ui/KidBlockUI.sln` in Visual Studio 2022 (17.8+ for .NET 8) and press F5.

> **Palette-lint build gate.** The build **fails** on a new or grown inline
> `Foreground` / `Background` hex colour literal in `Views/*.xaml`
> (`PaletteLint.targets`). Use the semantic colour keys in `Themes/Theme.xaml`
> instead; the sanctioned residue is frozen in `palette-lint-allowlist.txt`.
> This keeps the FluentDark palette in one place.

## Layout

| Folder | What's in it |
|---|---|
| `Views/` | windows, dialogs, and the Ribbon shell (XAML + code-behind) |
| `ViewModels/` | MVVM view-models (CommunityToolkit.Mvvm) |
| `Models/` | `Device`, `RouterState`, `ScheduleWindow`, `DomainEntry`, `LogEntry` |
| `Services/` | `RouterClient` (SSH.NET) + config / schedule / domains parsers |
| `Themes/` | `Theme.xaml` semantic colour keys |
| `Resources/` | `domain-categories.json`, etc. |
| `App.xaml` / `App.xaml.cs` | startup: Syncfusion licence registration, FluentDark default style, tray icon |
