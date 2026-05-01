using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FlashyToast;

// Subscribes to per-process audio session state changes via WASAPI's
// IAudioSessionManager2 + IAudioSessionEvents on the default render endpoint.
// Each session ties to a process; transitions Inactive→Active and back tell us
// "process X just started/stopped emitting audio." Used as an alternative
// notification trigger on machines where UserNotificationListener is denied
// (commonly: corporate boxes with LetAppsAccessNotifications=Off via MDM).
//
// Activation-tracking semantics: only Inactive→Active transitions observed at
// runtime fire OnActivated. Sessions that were already Active when we
// registered (incumbent media) emit no Activated event and only fire
// OnDeactivated if they later transition — we don't fabricate a synthetic
// activation timestamp from registration time.
internal sealed class AudioSessionMonitor : IDisposable
{
    public sealed record Activation(uint Pid, DateTime At);
    public sealed record Deactivation(uint Pid, TimeSpan Duration);

    public event Action<Activation>? OnActivated;
    public event Action<Deactivation>? OnDeactivated;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioSessionManager2? _manager;
    private SessionNotifier? _notifier;
    private readonly List<SessionWatcher> _watchers = new();
    private readonly object _lock = new();

    // Per-PID activation timestamp; entry exists iff the PID has at least one
    // session currently in the Active state from our perspective. We don't
    // count nested activations — a PID is "active" as a single boolean.
    private readonly ConcurrentDictionary<uint, DateTime> _activeSince = new();

    public void Start()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var type = Type.GetTypeFromCLSID(clsid)
            ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID not registered");
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

        var hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out _device);
        if (hr != 0 || _device == null)
            throw new InvalidOperationException($"GetDefaultAudioEndpoint hr=0x{hr:X8}");

        var iidMgr = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        hr = _device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
        if (hr != 0 || mgrObj == null)
            throw new InvalidOperationException($"Activate IAudioSessionManager2 hr=0x{hr:X8}");
        _manager = (IAudioSessionManager2)mgrObj;

        _notifier = new SessionNotifier(this);
        hr = _manager.RegisterSessionNotification(_notifier);
        if (hr != 0)
            throw new InvalidOperationException($"RegisterSessionNotification hr=0x{hr:X8}");

        // Bind subscribers for sessions that already exist. We don't probe
        // their state — only transitions matter (see class comment).
        hr = _manager.GetSessionEnumerator(out var sessions);
        if (hr == 0 && sessions != null)
        {
            try
            {
                if (sessions.GetCount(out var count) == 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var s) == 0 && s != null)
                        {
                            BindSession(s);
                        }
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
    }

    internal void BindSession(IAudioSessionControl session)
    {
        try
        {
            var ctl2 = (IAudioSessionControl2)session;
            if (ctl2.GetProcessId(out var pid) != 0) return;
            if (pid == 0) return;
            if (pid == (uint)Environment.ProcessId) return;

            var watcher = new SessionWatcher(this, session, pid);
            if (session.RegisterAudioSessionNotification(watcher) != 0) return;

            lock (_lock) _watchers.Add(watcher);
        }
        catch
        {
            // Sessions can vanish mid-bind (process exit). Ignore.
        }
    }

    internal void OnSessionState(uint pid, AudioSessionState state)
    {
        switch (state)
        {
            case AudioSessionState.Active:
                var now = DateTime.UtcNow;
                if (_activeSince.TryAdd(pid, now))
                {
                    try { OnActivated?.Invoke(new Activation(pid, now)); } catch { }
                }
                break;
            case AudioSessionState.Inactive:
            case AudioSessionState.Expired:
                if (_activeSince.TryRemove(pid, out var since))
                {
                    var duration = DateTime.UtcNow - since;
                    try { OnDeactivated?.Invoke(new Deactivation(pid, duration)); } catch { }
                }
                break;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_manager != null && _notifier != null)
            {
                _manager.UnregisterSessionNotification(_notifier);
            }
        }
        catch { }

        lock (_lock)
        {
            foreach (var w in _watchers)
            {
                try { w.Session.UnregisterAudioSessionNotification(w); } catch { }
                try { Marshal.ReleaseComObject(w.Session); } catch { }
            }
            _watchers.Clear();
        }

        if (_manager != null) { try { Marshal.ReleaseComObject(_manager); } catch { } _manager = null; }
        if (_device != null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
        if (_enumerator != null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
    }

    private const uint CLSCTX_ALL = 0x17;

    internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    internal enum AudioSessionState { Inactive = 0, Active = 1, Expired = 2 }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice(string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr eventContext, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr eventContext, uint streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? enumerator);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl? session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        // Inherited from IAudioSessionControl, in vtable order.
        [PreserveSig] int GetState(out AudioSessionState state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
        // IAudioSessionControl2 additions.
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string newName, ref Guid eventContext);
        [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string newPath, ref Guid eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext);
        [PreserveSig] int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelArray, uint changedChannel, ref Guid eventContext);
        [PreserveSig] int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext);
        [PreserveSig] int OnStateChanged(AudioSessionState newState);
        [PreserveSig] int OnSessionDisconnected(int disconnectReason);
    }

    [ComImport]
    [Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IAudioSessionControl newSession);
    }

    private sealed class SessionNotifier : IAudioSessionNotification
    {
        private readonly AudioSessionMonitor _monitor;
        public SessionNotifier(AudioSessionMonitor monitor) => _monitor = monitor;
        public int OnSessionCreated(IAudioSessionControl newSession)
        {
            _monitor.BindSession(newSession);
            return 0;
        }
    }

    internal sealed class SessionWatcher : IAudioSessionEvents
    {
        private readonly AudioSessionMonitor _monitor;
        public IAudioSessionControl Session { get; }
        public uint Pid { get; }

        public SessionWatcher(AudioSessionMonitor monitor, IAudioSessionControl session, uint pid)
        {
            _monitor = monitor;
            Session = session;
            Pid = pid;
        }

        public int OnStateChanged(AudioSessionState newState)
        {
            _monitor.OnSessionState(Pid, newState);
            return 0;
        }

        public int OnDisplayNameChanged(string newName, ref Guid eventContext) => 0;
        public int OnIconPathChanged(string newPath, ref Guid eventContext) => 0;
        public int OnSimpleVolumeChanged(float newVolume, bool newMute, ref Guid eventContext) => 0;
        public int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelArray, uint changedChannel, ref Guid eventContext) => 0;
        public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext) => 0;
        public int OnSessionDisconnected(int disconnectReason) => 0;
    }
}
