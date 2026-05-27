# flashy-toast

Windows-only background utility that watches the Action Center and calls
`FlashWindowEx` on the source application's taskbar entry whenever a new toast
notification arrives.

The default Windows shell turns this into the orange taskbar flash. Tiling
window managers that listen for `HSHELL_FLASH` (e.g. [bug.n](https://github.com/fuhsjr00/bug.n),
which routes it to `Manager_markUrgent`) get cross-workspace urgency for free —
which is the whole point. Modern apps (Slack, Discord, Teams, Outlook, every
Chromium browser post-M121) post notifications through Action Center instead
of calling `FlashWindowEx` themselves, so without something like this they're
invisible to bug.n.

## Install

flashy-toast ships as a single self-contained `.exe`. Two-step install:

**1. Download the exe:**

```powershell
iwr https://github.com/somanysteves/flashy-toast/releases/latest/download/flashy-toast.exe -OutFile flashy-toast.exe
iwr https://github.com/somanysteves/flashy-toast/releases/latest/download/install.ps1 -OutFile install.ps1
```

**2. Run the installer (elevated PowerShell):**

```powershell
.\install.ps1 -Source .\flashy-toast.exe
```

This copies the exe to `%LOCALAPPDATA%\Programs\flashy-toast\` and registers
a Scheduled Task that runs it at every logon with highest privileges (no UAC
prompt at logon). To start it immediately without rebooting:

```powershell
Start-ScheduledTask -TaskName flashy-toast
```

The exe itself declares `requireAdministrator` in its embedded manifest, so
double-clicking it directly will also prompt UAC and run elevated — useful
if you want to restart it manually mid-session.

First run pops a Windows Settings prompt asking to allow notification access —
grant it.

## Logs

Each toast and what it flashed is written to:

```
%LOCALAPPDATA%\flashy-toast\flashy-toast.log
```

To watch it live:

```powershell
Get-Content -Wait "$env:LOCALAPPDATA\flashy-toast\flashy-toast.log"
```

## Uninstall

```powershell
.\uninstall.ps1
```

Pass `-PurgeLogs` to also delete `%LOCALAPPDATA%\flashy-toast\`.

## Build from source

Requires the .NET 8 SDK.

```powershell
git clone https://github.com/somanysteves/flashy-toast
cd flashy-toast
./build.ps1
./install.ps1
```

CI runs `build.ps1` (`.github/workflows/build.yml`, `.github/workflows/release.yml`).

## How it works

See [PLAN.md](PLAN.md) for the design and the gotchas worth knowing about
(why we don't filter on `IsWindowVisible`, how Chrome multi-window
disambiguation works, why we poll instead of subscribing to
`NotificationChanged`).
