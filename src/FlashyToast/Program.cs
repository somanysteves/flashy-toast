using System.Collections.Concurrent;
using System.Text;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FlashyToast;

internal static class Program
{
    private static readonly Flasher _flasher = new();
    private static readonly TitleChangeMonitor _titles = new();
    private static readonly AudioSessionMonitor _audio = new();
    private static StreamWriter? _logFile;

    // How recent a title change must be (relative to toast/audio observation)
    // for us to trust it as the originating window. Event delivery is
    // sub-second so a small window is fine, but tab-switch + toast-fire
    // ordering can span a couple seconds on slow systems — keep some slack.
    private static readonly TimeSpan TitleChangeWindow = TimeSpan.FromSeconds(5);

    // Audio sessions shorter than this are treated as notification dings (used
    // as fallback when the originating app is not a known title-changer).
    // Calls/music/video stay Active far longer than this.
    private static readonly TimeSpan ShortSoundMax = TimeSpan.FromMilliseconds(1500);

    private const string MutexName = @"Local\flashy-toast-singleton";

    // Poll results and audio events arrive on threadpool threads; serialize
    // access to _flasher's debounce dict and _seen.
    private static readonly object _handleLock = new();
    private static readonly HashSet<uint> _seen = new();

    // Terminal title-change trigger: tracks whether each window was last seen
    // in a "busy" state (title starts with a Claude Code spinner glyph — see
    // IsSpinner). Flashes on the busy→idle transition only. No process-name
    // filter — the spinner glyph patterns are specific enough to Claude Code's
    // TUI that any terminal hosting it is covered automatically.
    private static readonly ConcurrentDictionary<IntPtr, bool> _terminalWasBusy = new();

    private static async Task<int> Main(string[] args)
    {
        OpenLogFile();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"unhandled exception: {e.ExceptionObject}");

        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed without releasing; we still own it now.
            acquired = true;
        }
        if (!acquired)
        {
            Log("another flashy-toast is already running; exiting.");
            return 4;
        }

        try
        {
            return await Run();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private static async Task<int> Run()
    {
        var listener = UserNotificationListener.Current;

        Log("requesting UserNotificationListener access...");
        UserNotificationListenerAccessStatus access;
        try
        {
            access = await listener.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            Log($"RequestAccessAsync threw: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        Log($"access status: {access}");
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            Log("access not granted; exiting. Open Settings → Privacy → Notifications to grant.");
            return 1;
        }

        try
        {
            var initial = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_handleLock)
            {
                foreach (var n in initial) _seen.Add(n.Id);
            }
            Log($"existing toasts at startup: {initial.Count} (suppressed)");
        }
        catch (Exception ex)
        {
            Log($"initial GetNotificationsAsync threw: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        // Polling instead of UserNotificationListener.NotificationChanged: the
        // event subscription throws RPC_S_CALL_FAILED (0x800706BE, ~9s timeout)
        // when called from an elevated process — Windows blocks COM callbacks
        // crossing into a high-integrity process. Outbound GetNotificationsAsync
        // calls work fine, so we poll. 750ms is fast enough that the user can't
        // perceive a lag between toast and flash.
        _ = PollNotificationsAsync(listener);
        Log("notification polling started.");

        _titles.TitleChanged += (hwnd, _, newTitle) => OnTerminalTitleChanged(hwnd, newTitle);
        _titles.Start();

        // Audio path: subscribe alongside the toast listener. On machines
        // where notification access is denied, we'd have already returned
        // above; on machines where it's allowed, both signals fire and the
        // Flasher's HWND-based debounce dedupes.
        _audio.OnActivated += a => SafeRun(() => HandleAudioActive(a.Pid));
        _audio.OnDeactivated += d => SafeRun(() => HandleAudioInactive(d.Pid, d.Duration));
        try
        {
            _audio.Start();
            Log("AudioSessionMonitor started.");
        }
        catch (Exception ex)
        {
            Log($"AudioSessionMonitor start failed: {ex.GetType().Name}: {ex.Message} — audio trigger disabled, toast path still active.");
        }

        try
        {
            // Run until process kill / logoff. We're a packaged background
            // app under windows.startupTask; lifetime is OS-managed.
            await Task.Delay(Timeout.Infinite);
        }
        finally
        {
            _titles.Stop();
            _audio.Dispose();
        }
        return 0;
    }

    private static void SafeRun(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log($"audio handler threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task PollNotificationsAsync(UserNotificationListener listener)
    {
        while (true)
        {
            await Task.Delay(750);
            try
            {
                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                lock (_handleLock)
                {
                    foreach (var n in notifications)
                        if (_seen.Add(n.Id)) Handle(n);
                }
            }
            catch (Exception ex)
            {
                Log($"poll threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Handle(UserNotification n)
    {
        var appInfo = n.AppInfo;
        var aumid = appInfo?.AppUserModelId ?? "";
        var displayName = appInfo?.DisplayInfo?.DisplayName ?? "";
        var pfn = SafePackageFamilyName(appInfo);
        var text = string.Join(" | ", TryReadToastText(n).Select(Quote));

        if (string.IsNullOrEmpty(aumid))
        {
            Log($"toast id={n.Id} aumid=<empty> display={Quote(displayName)} text=[{text}] → skipped (no AUMID)");
            return;
        }

        var matches = WindowResolver.ResolveAll(aumid);
        if (matches.Count == 0)
        {
            Log($"toast id={n.Id} aumid={aumid} pfn={pfn} display={Quote(displayName)} text=[{text}] → unresolved");
            DumpCandidates();
            return;
        }

        // Pick among ALL matches, not just hidden ones. If the winner turns
        // out to be visible (the originating window is on the user's current
        // workspace), Flasher's IsWindowVisible check skips the flash. We
        // must NOT filter to hidden first — that would let a runner-up hidden
        // window get flashed when the real originating window is visible.
        var (winner, pickReason) = PickWinner(matches);
        var result = _flasher.TryFlash(winner.Hwnd);
        Log($"toast id={n.Id} aumid={aumid} display={Quote(displayName)} → " +
            $"hwnd=0x{winner.Hwnd.ToInt64():X} stage={winner.Stage} pick={pickReason} " +
            $"proc={winner.ProcessName} title={Quote(winner.WindowTitle)} flash={result} " +
            $"matches={matches.Count}");
    }

    private static (WindowResolver.Resolution Winner, string Reason) PickWinner(IReadOnlyList<WindowResolver.Resolution> matches)
    {
        if (matches.Count == 1) return (matches[0], "only-match");

        var picked = PickByTitleChange(matches);
        if (picked is { } p) return p;
        return (matches[0], "z-order");
    }

    // Titles like "(3) Mail - Outlook" or "• Slack" indicate a tab with an
    // unread count. Matches the most recently changed such window within
    // TitleChangeWindow — the audio and title-update arrive together.
    private static readonly System.Text.RegularExpressions.Regex NotificationTitleRx =
        new(@"^\(\d+\)|^• ", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (WindowResolver.Resolution Winner, string Reason)? PickByNotificationTitle(IReadOnlyList<WindowResolver.Resolution> candidates)
    {
        var cutoff = DateTime.UtcNow - TitleChangeWindow;
        WindowResolver.Resolution? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var m in candidates)
        {
            var t = _titles.LastChange(m.Hwnd);
            if (t is null || t.Value < cutoff) continue;
            if (!NotificationTitleRx.IsMatch(m.WindowTitle)) continue;
            if (t.Value > bestTime) { bestTime = t.Value; best = m; }
        }
        if (best is null) return null;
        var ageMs = (int)(DateTime.UtcNow - bestTime).TotalMilliseconds;
        return (best, $"notify-title({ageMs}ms)");
    }

    private static (WindowResolver.Resolution Winner, string Reason)? PickByTitleChange(IReadOnlyList<WindowResolver.Resolution> candidates)
    {
        var cutoff = DateTime.UtcNow - TitleChangeWindow;
        WindowResolver.Resolution? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var m in candidates)
        {
            var t = _titles.LastChange(m.Hwnd);
            if (t is null || t.Value < cutoff) continue;
            if (t.Value > bestTime)
            {
                bestTime = t.Value;
                best = m;
            }
        }
        if (best is null) return null;
        var ageMs = (int)(DateTime.UtcNow - bestTime).TotalMilliseconds;
        return (best, $"title-change({ageMs}ms)");
    }

    private static void HandleAudioActive(uint pid)
    {
        var procName = WindowResolver.ProcessNameForPid(pid);
        Log($"audio-active pid={pid} proc={Quote(procName ?? "")}");
        if (string.IsNullOrEmpty(procName)) return;

        var hwnds = WindowResolver.ResolveByPid(pid);
        if (hwnds.Count == 0) hwnds = WindowResolver.ResolveByProcessName(procName);
        if (hwnds.Count == 0) return;

        var pick = PickByNotificationTitle(hwnds);
        if (pick is null)
        {
            Log($"audio-active pid={pid} proc={procName} matches={hwnds.Count} → no notification title, wait for inactive");
            return;
        }

        var result = _flasher.TryFlash(pick.Value.Winner.Hwnd);
        Log($"audio-active pid={pid} proc={procName} → " +
            $"hwnd=0x{pick.Value.Winner.Hwnd.ToInt64():X} pick={pick.Value.Reason} " +
            $"title={Quote(pick.Value.Winner.WindowTitle)} flash={result}");
    }

    private static void HandleAudioInactive(uint pid, TimeSpan duration)
    {
        var procName = WindowResolver.ProcessNameForPid(pid);
        Log($"audio-inactive pid={pid} proc={Quote(procName ?? "")} dur={(int)duration.TotalMilliseconds}ms");
        if (string.IsNullOrEmpty(procName)) return;

        var hwnds = WindowResolver.ResolveByPid(pid);
        if (hwnds.Count == 0) hwnds = WindowResolver.ResolveByProcessName(procName);
        if (hwnds.Count == 0) return;

        // Precise path: a window whose title recently changed to a notification
        // pattern (e.g. "(3) Mail - Outlook", "• Slack"). Fires regardless of
        // sound duration — duration gating only applies to the fallback path.
        var pick = PickByNotificationTitle(hwnds);
        if (pick is not null)
        {
            var result = _flasher.TryFlash(pick.Value.Winner.Hwnd);
            Log($"audio-inactive pid={pid} proc={procName} dur={(int)duration.TotalMilliseconds}ms → " +
                $"hwnd=0x{pick.Value.Winner.Hwnd.ToInt64():X} pick={pick.Value.Reason} " +
                $"title={Quote(pick.Value.Winner.WindowTitle)} flash={result}");
            return;
        }

        // Fallback: short-sound heuristic. Anything longer than ShortSoundMax
        // is assumed to be media/call audio, not a notification ding.
        if (duration > ShortSoundMax)
        {
            Log($"audio-inactive pid={pid} proc={procName} dur={(int)duration.TotalMilliseconds}ms → too long for short-sound, skip");
            return;
        }

        var hidden = hwnds.Where(r => !WindowResolver.IsHwndVisible(r.Hwnd)).ToList();
        if (hidden.Count == 0)
        {
            Log($"audio-inactive pid={pid} proc={procName} dur={(int)duration.TotalMilliseconds}ms → short-sound but all windows visible, skip");
            return;
        }

        var winner = hidden[0];
        var flash = _flasher.TryFlash(winner.Hwnd);
        Log($"audio-inactive pid={pid} proc={procName} dur={(int)duration.TotalMilliseconds}ms → " +
            $"short-sound hwnd=0x{winner.Hwnd.ToInt64():X} hidden={hidden.Count}/{hwnds.Count} title={Quote(winner.WindowTitle)} flash={flash}");
    }

    // Spinner glyphs Claude Code's TUI puts at the start of the terminal title
    // while it's working. Older builds used the braille block (U+2800–U+28FF,
    // e.g. "⠋ Claude Code"); current builds use the half-circle spinner
    // (U+25D0–U+25D3, "◐◑◒◓"). The idle/ready state uses "✳" (U+2733), which is
    // outside both ranges, so a busy→idle transition is detected cleanly.
    private static bool IsSpinner(string title)
    {
        // Elevated terminals transiently carry an "Administrator: " prefix that
        // shoves the spinner off position 0. Strip it so a still-busy elevated
        // window ("Administrator: ◑ …") isn't misread as idle, which would flash
        // (mark urgent) while it's actually still working.
        const string adminPrefix = "Administrator: ";
        if (title.StartsWith(adminPrefix, StringComparison.Ordinal))
            title = title.Substring(adminPrefix.Length);
        if (title.Length == 0) return false;
        char c = title[0];
        return (c >= '⠀' && c <= '⣿')   // braille block  ⠀…⣿
            || (c >= '◐' && c <= '◓');   // half-circle    ◐◑◒◓
    }

    private static void OnTerminalTitleChanged(IntPtr hwnd, string newTitle)
    {
        var wasBusy = _terminalWasBusy.TryGetValue(hwnd, out var b) && b;
        var nowBusy = IsSpinner(newTitle);

        // Skip windows that have never been in a spinner state — avoids logging
        // every title change on the system.
        if (!wasBusy && !nowBusy) return;

        _terminalWasBusy[hwnd] = nowBusy;
        Log($"terminal-title hwnd=0x{hwnd.ToInt64():X} wasBusy={wasBusy} nowBusy={nowBusy} title={Quote(newTitle)}");

        if (wasBusy && !nowBusy && newTitle.Length > 0)
            SafeRun(() => FlashTerminal(hwnd));
    }

    private static void FlashTerminal(IntPtr hwnd)
    {
        var result = _flasher.TryFlashUnlessForeground(hwnd);
        Log($"terminal-idle hwnd=0x{hwnd.ToInt64():X} flash={result}");
    }

    private static void DumpCandidates()
    {
        try
        {
            var candidates = WindowResolver.Diagnose();
            Log($"  candidates: {candidates.Count}");
            foreach (var c in candidates)
            {
                var v = c.Visible ? "V" : "v";
                var k = c.Cloaked ? "C" : "c";
                Log($"    hwnd=0x{c.Hwnd.ToInt64():X} [{v}{k}] proc={c.ProcessName} aumid={c.Aumid ?? "<null>"} title={Quote(c.Title)}");
            }
        }
        catch (Exception ex)
        {
            Log($"  diagnose threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> TryReadToastText(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null) return Array.Empty<string>();
            var elements = binding.GetTextElements();
            var result = new List<string>(elements.Count);
            foreach (var e in elements)
            {
                result.Add(e.Text ?? "");
            }
            return result;
        }
        catch (Exception ex)
        {
            return new[] { $"<text-error: {ex.GetType().Name}>" };
        }
    }

    private static string SafePackageFamilyName(Windows.ApplicationModel.AppInfo? appInfo)
    {
        if (appInfo is null) return "<null>";
        try
        {
            return string.IsNullOrEmpty(appInfo.PackageFamilyName) ? "<empty>" : appInfo.PackageFamilyName;
        }
        catch
        {
            return "<unpackaged>";
        }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static string LogFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "flashy-toast", "flashy-toast.log");

    private static void OpenLogFile()
    {
        try
        {
            var path = LogFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            // Encoding.UTF8 emits a BOM on every open; in append mode that
            // sprinkles BOMs through the file. Use a no-BOM UTF-8 encoder.
            _logFile = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            _logFile.WriteLine();
            _logFile.WriteLine($"=== flashy-toast started {DateTime.Now:yyyy-MM-dd HH:mm:ss} pid={Environment.ProcessId} ===");
        }
        catch
        {
            // Logging is best-effort; if we can't open the log file we still
            // run, just silently.
        }
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { _logFile?.WriteLine(line); } catch { }
    }
}
