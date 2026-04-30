using System.Runtime.InteropServices;

namespace FlashyToast;

internal sealed class Flasher
{
    private readonly Dictionary<string, DateTime> _lastFlashByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce = TimeSpan.FromSeconds(2);

    public Flash TryFlash(IntPtr hwnd, string debounceKey)
    {
        if (hwnd == IntPtr.Zero) return Flash.NoTarget;
        if (GetForegroundWindow() == hwnd) return Flash.SkippedForeground;

        var now = DateTime.UtcNow;
        if (_lastFlashByKey.TryGetValue(debounceKey, out var last) && now - last < _debounce)
        {
            return Flash.SkippedDebounce;
        }
        _lastFlashByKey[debounceKey] = now;

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

    public enum Flash { Flashed, SkippedForeground, SkippedDebounce, NoTarget, PInvokeFailed }

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
    private static extern IntPtr GetForegroundWindow();
}
