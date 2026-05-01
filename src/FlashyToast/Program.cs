using System.Text;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FlashyToast;

internal static class Program
{
    private static readonly Flasher _flasher = new();
    private static readonly TitleChangeMonitor _titles = new();
    private static StreamWriter? _logFile;

    // How recent a title change must be (relative to toast observation) for
    // us to trust it as the originating window. Event delivery is sub-second
    // so a small window is fine, but tab-switch + toast-fire ordering can
    // span a couple seconds on slow systems — keep some slack.
    private static readonly TimeSpan TitleChangeWindow = TimeSpan.FromSeconds(5);

    private const string MutexName = @"Local\flashy-toast-singleton";

    // NotificationChanged events fire on threadpool threads; serialize
    // access to _flasher's debounce dict and _seen.
    private static readonly object _handleLock = new();
    private static readonly HashSet<uint> _seen = new();

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

        try
        {
            listener.NotificationChanged += OnNotificationChanged;
            Log("subscribed to NotificationChanged.");
        }
        catch (Exception ex)
        {
            Log($"NotificationChanged subscription threw: {ex.GetType().Name}: {ex.Message} (HRESULT 0x{ex.HResult:X8})");
            return 5;
        }

        _titles.Start();
        try
        {
            // Run until process kill / logoff. We're a packaged background
            // app under windows.startupTask; lifetime is OS-managed.
            await Task.Delay(Timeout.Infinite);
        }
        finally
        {
            _titles.Stop();
        }
        return 0;
    }

    private static void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added) return;

        UserNotification? n = null;
        try { n = sender.GetNotification(args.UserNotificationId); }
        catch (Exception ex)
        {
            Log($"GetNotification({args.UserNotificationId}) threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        if (n is null) return;

        lock (_handleLock)
        {
            if (!_seen.Add(n.Id)) return;
            Handle(n);
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

        var (winner, pickReason) = PickWinner(matches);
        var debounceKey = aumid;
        var result = _flasher.TryFlash(winner.Hwnd, debounceKey);
        Log($"toast id={n.Id} aumid={aumid} display={Quote(displayName)} → " +
            $"hwnd=0x{winner.Hwnd.ToInt64():X} stage={winner.Stage} pick={pickReason} " +
            $"proc={winner.ProcessName} title={Quote(winner.WindowTitle)} flash={result} " +
            $"matches={matches.Count}");
    }

    private static (WindowResolver.Resolution Winner, string Reason) PickWinner(IReadOnlyList<WindowResolver.Resolution> matches)
    {
        if (matches.Count == 1) return (matches[0], "only-match");

        // Among multi-window candidates (Chrome being the canonical case),
        // the originating tab/window typically just changed its title in
        // response to the underlying notification. Pick the most-recent
        // change within TitleChangeWindow.
        var cutoff = DateTime.UtcNow - TitleChangeWindow;
        WindowResolver.Resolution? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (var m in matches)
        {
            var t = _titles.LastChange(m.Hwnd);
            if (t is null || t.Value < cutoff) continue;
            if (t.Value > bestTime)
            {
                bestTime = t.Value;
                best = m;
            }
        }
        if (best is not null)
        {
            var ageMs = (int)(DateTime.UtcNow - bestTime).TotalMilliseconds;
            return (best, $"title-change({ageMs}ms)");
        }
        return (matches[0], "z-order");
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
