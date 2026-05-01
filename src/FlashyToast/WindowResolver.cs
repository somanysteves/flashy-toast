using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlashyToast;

internal static class WindowResolver
{
    public sealed record Resolution(IntPtr Hwnd, string Stage, string WindowTitle, string ProcessName);

    public sealed record Candidate(IntPtr Hwnd, string Title, string ProcessName, string? Aumid, bool Visible, bool Cloaked);

    // Returns all matching HWNDs from the first pass that found anything.
    // Order is Z-order top-to-bottom, so callers can use index 0 as a
    // "most recently active" fallback when smarter disambiguation has no
    // signal.
    public static IReadOnlyList<Resolution> ResolveAll(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return Array.Empty<Resolution>();

        var candidates = EnumerateTopLevelWindows();
        var ownPid = (uint)Environment.ProcessId;

        var pass1 = new List<Resolution>();
        foreach (var hwnd in candidates)
        {
            if (!TryGetWindowPid(hwnd, out var pid) || pid == ownPid) continue;
            var windowAumid = TryGetWindowAumid(hwnd);
            if (string.IsNullOrEmpty(windowAumid)) continue;
            if (string.Equals(windowAumid, aumid, StringComparison.OrdinalIgnoreCase))
            {
                pass1.Add(new Resolution(hwnd, "aumid", GetWindowTitle(hwnd), TryGetProcessName(pid)));
            }
        }
        if (pass1.Count > 0) return pass1;

        var tokens = aumid
            .Split(new[] { '.', '!', '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();
        var pass2 = new List<Resolution>();
        foreach (var hwnd in candidates)
        {
            if (!TryGetWindowPid(hwnd, out var pid) || pid == ownPid) continue;
            var procName = TryGetProcessName(pid);
            if (string.IsNullOrEmpty(procName)) continue;
            foreach (var token in tokens)
            {
                if (string.Equals(token, procName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(StripExe(token), procName, StringComparison.OrdinalIgnoreCase))
                {
                    pass2.Add(new Resolution(hwnd, "procname", GetWindowTitle(hwnd), procName));
                    break;
                }
            }
        }
        return pass2;
    }

    public static IReadOnlyList<Candidate> Diagnose()
    {
        var list = new List<Candidate>();
        foreach (var hwnd in EnumerateTopLevelWindows())
        {
            var title = GetWindowTitle(hwnd);
            var procName = TryGetWindowPid(hwnd, out var pid) ? TryGetProcessName(pid) : "";
            var aumid = TryGetWindowAumid(hwnd);
            list.Add(new Candidate(hwnd, title, procName, aumid, IsWindowVisible(hwnd), IsCloaked(hwnd)));
        }
        return list;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int cloaked = 0;
        var hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    public static Resolution? Resolve(string aumid)
    {
        var all = ResolveAll(aumid);
        return all.Count == 0 ? null : all[0];
    }

    private static string StripExe(string token)
        => token.EndsWith("exe", StringComparison.OrdinalIgnoreCase) ? token[..^3] : token;

    private static List<IntPtr> EnumerateTopLevelWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsCandidate(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Intentionally do NOT filter on IsWindowVisible. Tiling window managers
    // (e.g. bug.n) hide windows on inactive workspaces via SW_HIDE; Windows
    // virtual desktops cloak windows on inactive desktops. Both are exactly
    // the windows we most want to find — that's the whole point of flashing.
    private static bool IsCandidate(IntPtr hwnd)
    {
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if (GetWindowTextLength(hwnd) == 0) return false;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool TryGetWindowPid(IntPtr hwnd, out uint pid)
    {
        pid = 0;
        return GetWindowThreadProcessId(hwnd, out pid) != 0;
    }

    private static string TryGetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private static string? TryGetWindowAumid(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore;
        var hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out var store);
        if (hr != 0 || store == null) return null;
        try
        {
            var key = PKEY_AppUserModel_ID;
            var pv = default(PROPVARIANT);
            hr = store.GetValue(ref key, ref pv);
            if (hr != 0) return null;
            try
            {
                if (pv.vt == VT_LPWSTR && pv.pointer != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUni(pv.pointer);
                }
                return null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private const uint GW_OWNER = 4;
    private const ushort VT_LPWSTR = 31;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const int DWMWA_CLOAKED = 14;

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static PROPERTYKEY PKEY_AppUserModel_ID => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public IntPtr pointer;
        [FieldOffset(16)] public IntPtr pointer2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
