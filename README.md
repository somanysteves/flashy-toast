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

flashy-toast ships as a self-signed `.msix` package. Two-step install:

**1. Trust the signing cert (admin PowerShell, once per machine):**

```powershell
iwr https://github.com/somanysteves/flashy-toast/releases/latest/download/flashy-toast.cer -OutFile flashy-toast.cer
Import-Certificate -FilePath flashy-toast.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

**2. Install the package (normal-user PowerShell):**

```powershell
iwr https://github.com/somanysteves/flashy-toast/releases/latest/download/flashy-toast.msix -OutFile flashy-toast.msix
Add-AppxPackage flashy-toast.msix
```

**3. Launch flashy-toast once from the Start menu** (search "flashy-toast"). Windows
requires this one-time manual launch before auto-start kicks in. After that the
app starts on every login automatically — no shortcut, no console, no UI.

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
Get-AppxPackage -Name FlashyToast | Remove-AppxPackage
Remove-Item "$env:LOCALAPPDATA\flashy-toast" -Recurse -Force -ErrorAction SilentlyContinue
```

To also drop the trusted cert (admin):

```powershell
Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq 'CN=flashy-toast' | Remove-Item
```

## Build from source

Requires the .NET 8 SDK.

```powershell
git clone https://github.com/somanysteves/flashy-toast
cd flashy-toast
./build.ps1
```

CI runs the same script (`.github/workflows/build.yml`, `.github/workflows/release.yml`).

## How it works

See [PLAN.md](PLAN.md) for the design and the gotchas worth knowing about
(why we don't filter on `IsWindowVisible`, how Chrome multi-window
disambiguation works, why packaged identity is required for
`NotificationChanged`).
