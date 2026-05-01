using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FlashyToast;

// Subscribes to EVENT_OBJECT_NAMECHANGE via SetWinEventHook on a dedicated
// pumped thread, recording the most recent title-change timestamp per HWND.
// Used to disambiguate AUMID matches that produce multiple candidate windows
// (the Chrome multi-window case): when a toast lands, the originating tab's
// title typically just flipped (e.g. "(1) Slack | …"). Pick that one.
internal sealed class TitleChangeMonitor : IDisposable
{
    private readonly ConcurrentDictionary<IntPtr, DateTime> _lastChange = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private WinEventDelegate? _callback;
    private volatile bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _callback = OnWinEvent;
            _hook = SetWinEventHook(
                EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE,
                IntPtr.Zero, _callback, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            ready.Set();

            // OUTOFCONTEXT events are delivered to this thread's message
            // queue. Pump until WM_QUIT arrives via Stop().
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        })
        {
            IsBackground = true,
            Name = "flashy-toast title-monitor",
        };
        _thread.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    public DateTime? LastChange(IntPtr hwnd)
        => _lastChange.TryGetValue(hwnd, out var t) ? t : null;

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Skip child accessibility objects (controls, menu items). We only
        // care about the window's own title.
        if (idObject != OBJID_WINDOW || idChild != 0) return;
        if (hwnd == IntPtr.Zero) return;

        // Skip non-top-level windows: Chrome's tab title is on its top-level
        // browser window, not a child. This also keeps the dict bounded.
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        _lastChange[hwnd] = DateTime.UtcNow;
    }

    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint WM_QUIT = 0x0012;
    private const uint GA_ROOT = 2;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd,
        uint msgFilterMin, uint msgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
}
