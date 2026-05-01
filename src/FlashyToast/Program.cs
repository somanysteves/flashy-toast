using System.Runtime.InteropServices;
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
    private static readonly TitleChangeMonitor _titles = new();
    private static StreamWriter? _logFile;

    // How recent a title change must be (relative to toast observation) for
    // us to trust it as the originating window. Toast polling adds up to
    // PollInterval of latency on top of the actual title-flip-to-toast gap,
    // so this needs to comfortably exceed PollInterval.
    private static readonly TimeSpan TitleChangeWindow = TimeSpan.FromSeconds(5);

    private const string MutexName = @"Local\flashy-toast-singleton";

    private static async Task<int> Main(string[] args)
    {
        // We're a WinExe (no console allocated). For interactive --install /
        // --uninstall invocations from a terminal, attach to the parent's
        // console so Installer's writes show up where the user ran us from.
        bool isInstallerCommand = args.Length == 1 &&
            (args[0].Equals("--install", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        if (isInstallerCommand)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            // Setting OutputEncoding throws IOException("handle is invalid")
            // when no console is attached, which is the normal-run case for
            // a WinExe. Only safe to set when we just attached one.
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        }

        if (args.Length == 1 && args[0].Equals("--install", StringComparison.OrdinalIgnoreCase))
        {
            return Installer.Install();
        }
        if (args.Length == 1 && args[0].Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        {
            return Installer.Uninstall();
        }

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

        _titles.Start();
        Log($"polling Action Center every {PollInterval.TotalSeconds:0.#}s. Ctrl+C to exit.");
        try
        {
            await PollLoop(listener, seen, cts.Token);
        }
        finally
        {
            _titles.Stop();
        }
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
            // run, just silently. Console.WriteLine below is also a no-op
            // when no console is attached (the WinExe normal-run case).
        }
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { _logFile?.WriteLine(line); } catch { }
        Console.WriteLine(line);
    }

    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
}
