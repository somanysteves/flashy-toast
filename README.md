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

PowerShell, run as your normal user (no admin):

```powershell
$d = "$env:LOCALAPPDATA\flashy-toast"; ni $d -ItemType Directory -Force | Out-Null; iwr https://github.com/somanysteves/flashy-toast/releases/latest/download/flashy-toast.exe -OutFile "$d\flashy-toast.exe"; & "$d\flashy-toast.exe" --install; Start-Process "$d\flashy-toast.exe"
```

That downloads the exe to `%LOCALAPPDATA%\flashy-toast\`, drops a shortcut in
your Startup folder so it auto-runs at login, and starts it now.

First run pops a Windows Settings prompt asking to allow notification access —
grant it. After that flashy-toast runs silently in the background; nothing
is shown on screen.

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
& "$env:LOCALAPPDATA\flashy-toast\flashy-toast.exe" --uninstall
Get-Process flashy-toast -ErrorAction SilentlyContinue | Stop-Process
Remove-Item "$env:LOCALAPPDATA\flashy-toast" -Recurse -Force
```

## Build from source

Requires the .NET 8 SDK.

```powershell
git clone https://github.com/somanysteves/flashy-toast
cd flashy-toast
dotnet publish src/FlashyToast/FlashyToast.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
# exe lands in src\FlashyToast\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\flashy-toast.exe
```

## How it works

See [PLAN.md](PLAN.md) for the design and the gotchas worth knowing about
(why we poll instead of subscribing to `NotificationChanged`, why we don't
filter on `IsWindowVisible`, how Chrome multi-window disambiguation works).
