# flashy-toast — plan

A Windows-only background utility that watches the Action Center and flashes
the source application's taskbar entry every time a new toast notification
arrives. No window manager required to be useful (default Windows shows the
orange flash); window managers that listen for `HSHELL_FLASH` (e.g. bug.n,
which already routes it to `Manager_markUrgent`) get urgency for free.

## Why this exists

Modern apps — Slack, Discord, Teams, Outlook, and every Chromium browser
post-M121 (including Gmail/Google Chat/Outlook Web) — route notifications
through the Windows Action Center. They do **not** call `FlashWindowEx` on
their own window. That means tiling window managers like bug.n, which only
hear about attention-needed via the `HSHELL_FLASH` shell message, never see
those notifications.

A Chrome extension was considered (using `chrome.windows.update({drawAttention:
true})`) and rejected because it only covers webapps in Chrome — Slack desktop,
Discord, Teams, Outlook desktop, and system toasts would still go unsignalled.
A toast listener subsumes both cases.

## Approach

C# console exe (no UI, optional tray icon later) that:

1. Calls `UserNotificationListener.Current.RequestAccessAsync()` once on first
   run — this triggers a Windows Settings prompt the user grants once.
2. Polls `UserNotificationListener.Current.GetNotificationsAsync(Toast)` every
   ~2s, diffing against the previous tick's set of `UserNotification.Id`s.
   New IDs = newly-added toasts. (See "NotificationChanged vs polling" below
   for why we don't use the event.)
3. On each newly-observed toast:
   - Pull `notification.AppInfo.AppUserModelId` (and PackageFamilyName,
     DisplayName as fallbacks).
   - Resolve a top-level HWND owned by that app (mapping chain below).
   - Call `FlashWindowEx(hwnd, FLASHW_ALL, 6, 500)`.
4. Stays alive for the user session; launched at login via Startup-folder
   shortcut or Task Scheduler logon trigger (decide during dev).

### NotificationChanged: packaged identity required

Subscribing to `UserNotificationListener.NotificationChanged` from an
unpackaged Win32 exe throws `COMException 0x80070490` (`ELEMENT_NOT_FOUND`)
at `add_NotificationChanged` time. The event delivery path requires
package identity. `RequestAccessAsync` and `GetNotificationsAsync` both
work fine without packaging — only the event subscription is gated.

Confirmed empirically on 2026-04-30 (failure on unpackaged exe) and
2026-05-01 (subscription succeeds inside an installed `.msix`). On the
packaged side we use the foreground in-process event handler — no
background-task registration required for an always-running session
daemon.

`FlashWindowEx` fires `HSHELL_FLASH`, which is exactly what bug.n already
catches in `Manager_onShellMessage` and routes to `Manager_markUrgent`. No
bug.n changes required.

## AUMID → HWND mapping

Try in order, stop at first match:

1. **AUMID match.** Enumerate top-level windows; for each, query
   `SHGetPropertyStoreForWindow` → `System.AppUserModel.ID`. Compare to the
   notification's AUMID.
2. **PackageFamilyName match.** For UWP/MSIX apps, find the running process
   whose package matches; flash any top-level window of that process.
3. **Process-name match.** Some classic apps embed the exe name in the AUMID
   (e.g. `Microsoft.Office.OUTLOOK.EXE.15`). Strip and match against running
   process basenames.
4. **DisplayName match.** Last resort: `notification.AppInfo.DisplayInfo.DisplayName`
   compared against the main-window title prefix of running processes.

If nothing matches, drop the toast silently. Headless toasts (Windows Update,
antivirus, scheduled reminders) often have no source window and that's fine.

## Tech choices

- **Language: C#.** WinRT interop is native; async/await on
  `RequestAccessAsync` and `GetNotificationsAsync` is clean. PowerShell can do
  it but the awaiter dance is ugly and it's the wrong tool for a long-running
  background listener.
- **Target framework:** `.NET 8` (or whatever's current LTS at dev time),
  `net8.0-windows10.0.19041.0` so we can reference WinRT types directly via
  `Microsoft.Windows.SDK.Contracts` or the modern TFM equivalent.
- **Single-file publish** with `dotnet publish -p:PublishSingleFile=true
  -r win-x64` so distribution is one `.exe`.
- **No tray icon in v1.** Just a hidden console window. Tray + "pause flashing"
  toggle is a v2 polish item.

## Permission gate

`RequestAccessAsync` shows a Windows Settings prompt the first time. The
prompt is scoped to whatever exe registered for it, so:

- Ship a signed binary if we ever distribute. For local-only personal use,
  unsigned is fine — the prompt just looks a bit scuzzy ("Unknown publisher").
- An MSIX would solve the trust/signing problem but is overkill for v1.
  Defer.

If the user denies access, exit with a clear log line — no silent retry loop.

## Lifecycle

- Launched once at user login. Options, in order of preference:
  1. **Startup folder shortcut** — simplest, user-controllable via
     `shell:startup`.
  2. **Task Scheduler logon trigger** — survives "disable startup app" toggles
     in Task Manager and runs without a flicker. Heavier setup.
  - Decide during dev. Default to Startup folder; document Task Scheduler as
    an upgrade path.
- Single-instance enforcement via a named mutex so double-launch doesn't
  double-flash.
- No auto-restart on crash for v1. If it dies, user notices because flashes
  stop; they can relaunch.

## Edge cases to handle

- **Self-flash loop.** If flashy-toast itself shows a toast (e.g. a "could not
  find source window" debug toast), don't flash itself. Filter by our own
  AUMID/PID before mapping.
- **Window already focused.** `FlashWindowEx` is mostly a no-op on a focused
  window, but explicitly skip to avoid noise.
- **Multiple windows for one app.** Flash one (the most-recently-active by
  Z-order, probably). Flashing all of them gets obnoxious.
- **Toast updates vs new toasts.** `NotificationChanged` fires for both Added
  and Removed; only act on `UserNotificationChangedKind.Added`.
- **Toast burst.** If 10 toasts arrive in a second from the same app, debounce
  to one flash per app per ~2 seconds.

## Out of scope for v1

- GUI configuration (per-app enable/disable, schedule windows for "do not
  flash").
- Per-toast severity (urgent vs informational).
- Cross-platform anything.
- Auto-update / installer.

## Repo layout (proposed)

```
flashy-toast/
  src/
    FlashyToast/
      FlashyToast.csproj
      Program.cs              # entry, mutex, shutdown hook
      ToastListener.cs        # UserNotificationListener subscription
      WindowResolver.cs       # AUMID → HWND mapping chain
      Flasher.cs              # FlashWindowEx P/Invoke + debounce
  README.md
  PLAN.md                     # this file (delete or shrink once shipped)
  .gitignore                  # dotnet defaults
```

## Milestones

1. ✅ **Walking skeleton.** Console app prints every toast it sees with AUMID +
   DisplayName + visible toast text. Confirmed listener works; one design
   change forced (polling instead of event subscription, see above).
2. ✅ **AUMID → HWND resolution.** Implemented in `WindowResolver.cs` with two
   passes: (a) explicit AUMID match via `SHGetPropertyStoreForWindow` against
   `PKEY_AppUserModel_ID`, (b) process-name fallback against AUMID tokens.
   Z-order tiebreak (EnumWindows yields top-to-bottom) for multi-window apps.
   Verified on Chrome (`stage=aumid` succeeds; process-name fallback unused).
3. ✅ **Flash.** `FlashWindowEx(FLASHW_ALL, 6 flashes, 500ms)` with per-AUMID
   2s debounce; skips foreground windows and own PID. Verified end-to-end:
   Chrome toast on a different bug.n workspace produces `flash=Flashed` and
   bug.n's shell-hook handler marks the correct workspace urgent.
4. ✅ **Auto-start.** Replaced the Startup-folder shortcut approach with a
   packaged-desktop `windows.startupTask` extension (`Enabled="true"`, no
   consent dialog per MSIX docs). Single-instance mutex
   (`Local\flashy-toast-singleton`) preserved for safety. User must launch
   the packaged app once from the Start menu before Windows arms auto-start
   (OS-level requirement, not a code thing).
5. ✅ **MSIX + events.** Self-signed MSIX package gives the exe packaged
   identity, which unlocks the foreground `NotificationChanged` event
   (verified empirically — subscription succeeds with no `0x80070490`).
   Polling loop deleted; latency drops from ≤ 2 s to sub-second. Build
   pipeline (`build.ps1`) generates a self-signed cert if needed, publishes
   the exe, packs via `MakeAppx`, and signs with `signtool`.
6. **Polish (later).** Tray icon, pause toggle, log rotation, real
   code-signing cert (replace self-signed → CA-signed without manifest
   changes), Microsoft Store submission for free auto-update.

### Milestone 2/3 gotcha: do NOT filter on `IsWindowVisible`

The whole point of this app is to flash windows that are *not* currently
in front. Tiling WMs (bug.n) hide windows on inactive workspaces via
`SW_HIDE`, which clears `WS_VISIBLE`; Windows virtual desktops cloak
windows on inactive desktops (`DWMWA_CLOAKED`). Filtering on
`IsWindowVisible` excludes exactly the windows we want to find.

`WindowResolver` enumerates top-level windows with these filters only:
no owner (`GW_OWNER == 0`), non-empty title, not a tool window
(`WS_EX_TOOLWINDOW` clear). Visibility and cloaked state are ignored for
matching but surfaced in diagnostics. `FlashWindowEx` works on hidden
and cloaked HWNDs and `HSHELL_FLASH` fires for them, so bug.n receives
the urgency signal regardless.

## Open questions to resolve during dev

- ~~Does `UserNotificationListener.NotificationChanged` deliver to an
  unpackaged Win32 console exe?~~ **No** — `add_NotificationChanged` throws
  `0x80070490`. Initial workaround: 2s polling of `GetNotificationsAsync`.
  Replaced in milestone 5 by packaging the exe as a self-signed MSIX, which
  gives it the package identity that unlocks the event subscription.
  (Resolved 2026-05-01.)
- Does `UserNotificationListener` deliver toasts from background services
  (e.g. Slack while minimized to tray)? Expected yes, verify in milestone 1
  by leaving the listener running and triggering toasts from each target
  app.
- ~~For Chrome notifications, does `AppInfo.AppUserModelId` give us Chrome's
  AUMID or the *site's* identity?~~ **Chrome's, and it's bare `Chrome`** —
  no per-profile suffix. Verified empirically and against Chromium source
  (`chrome/browser/notifications/win/notification_template_builder.cc`):
  Chromium encodes profile_id + origin_url + notification_id into the
  toast's `launch` attribute as a serialized `NotificationLaunchId`, not
  into the AUMID. The receiver-side WinRT surface
  (`UserNotificationListener` → `UserNotification` → `Notification.Visual`)
  exposes the visible toast text via `NotificationBinding.GetTextElements()`
  but **not** the raw toast XML or the `launch` attribute. So for chat-style
  Chrome notifications (Slack, Discord, Teams web, Gmail-as-chat) the title
  is the *sender* and the body is the *message* — neither matches Chrome's
  window-title format ("Site - Profile - Google Chrome"). Disambiguation
  among multiple Chrome windows/profiles from the toast alone is not
  reliably possible. v1 plan: for `aumid=Chrome|msedge|...`, flash the
  most-recently-active top-level browser window via Z-order. Document the
  limitation. (Resolved 2026-04-30.)
- Multi-monitor / multi-DPI behavior of `FlashWindowEx` — should be fine,
  flag if not.
