using System.Runtime.InteropServices;

namespace FlashyToast;

internal sealed class Flasher
{
    // HWND-keyed debounce: the same window can receive both a toast-listener
    // signal and an audio-session signal for one underlying notification, and
    // we don't want to flash twice. Keying on HWND naturally dedupes regardless
    // of which trigger fires first.
    private readonly Dictionary<IntPtr, DateTime> _lastFlashByHwnd = new();
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);
    private readonly object _lock = new();

    public Flash TryFlash(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Flash.NoTarget;

        // Only flash hidden windows. Visible windows (including the foreground)
        // are already in front of the user — flashing them adds nothing and
        // is occasionally jarring. The use case is windows that bug.n / virtual
        // desktops have hidden via SW_HIDE; those return false here.
        if (IsWindowVisible(hwnd)) return Flash.SkippedVisible;

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_lastFlashByHwnd.TryGetValue(hwnd, out var last) && now - last < _debounce)
            {
                return Flash.SkippedDebounce;
            }
            _lastFlashByHwnd[hwnd] = now;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL,
            uCount = 6,
            dwTimeout = 500,
        };
        return FlashWindowEx(ref info) ? Flash.Flashed : Flash.PInvokeFailed;
    }

    public enum Flash { Flashed, SkippedVisible, SkippedDebounce, NoTarget, PInvokeFailed }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 0x00000003;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
