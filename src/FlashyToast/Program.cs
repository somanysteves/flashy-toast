using System.Text;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FlashyToast;

internal static class Program
{
    // NotificationChanged event subscription requires packaged identity / a
    // registered background task; for an unpackaged Win32 console exe it
    // throws 0x80070490 ELEMENT_NOT_FOUND. Poll GetNotificationsAsync instead.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly Flasher _flasher = new();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

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

        var seen = new HashSet<uint>();
        try
        {
            var initial = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in initial)
            {
                seen.Add(n.Id);
            }
            Log($"existing toasts at startup: {initial.Count} (suppressed)");
        }
        catch (Exception ex)
        {
            Log($"initial GetNotificationsAsync threw: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log("Ctrl+C received; shutting down.");
            cts.Cancel();
        };

        Log($"polling Action Center every {PollInterval.TotalSeconds:0.#}s. Ctrl+C to exit.");
        await PollLoop(listener, seen, cts.Token);
        return 0;
    }

    private static async Task PollLoop(UserNotificationListener listener, HashSet<uint> seen, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var current = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var currentIds = new HashSet<uint>();
                foreach (var n in current)
                {
                    currentIds.Add(n.Id);
                    if (!seen.Contains(n.Id))
                    {
                        Handle(n);
                    }
                }
                seen.Clear();
                foreach (var id in currentIds) seen.Add(id);
            }
            catch (Exception ex)
            {
                Log($"poll error: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (TaskCanceledException) { break; }
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

        var resolved = WindowResolver.Resolve(aumid);
        if (resolved is null)
        {
            Log($"toast id={n.Id} aumid={aumid} pfn={pfn} display={Quote(displayName)} text=[{text}] → unresolved");
            DumpCandidates();
            return;
        }

        var debounceKey = aumid;
        var result = _flasher.TryFlash(resolved.Hwnd, debounceKey);
        Log($"toast id={n.Id} aumid={aumid} display={Quote(displayName)} → " +
            $"hwnd=0x{resolved.Hwnd.ToInt64():X} stage={resolved.Stage} " +
            $"proc={resolved.ProcessName} title={Quote(resolved.WindowTitle)} flash={result}");
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

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}
