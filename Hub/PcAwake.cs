using System.Runtime.InteropServices;

namespace ClaudeSessionManagerMCP.Hub;

/// <summary>
/// Windows sleep blocking via a power request object (PowerCreateRequest /
/// PowerSetRequest / PowerClearRequest). Unlike SetThreadExecutionState, a
/// power request is a handle owned by the process, not by the calling thread,
/// so it can be engaged and released from any threadpool thread.
/// The HUB process holds it so it survives individual session exits, and
/// Windows drops it automatically if the hub process dies.
/// </summary>
public sealed class PcAwake : IDisposable
{
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr handle, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr handle, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static readonly IntPtr InvalidHandle = new(-1);

    private readonly object _lock = new();
    private readonly bool _keepDisplay;
    private IntPtr _request = IntPtr.Zero;
    private bool _held;

    public PcAwake(bool keepDisplayOn) => _keepDisplay = keepDisplayOn;

    public bool IsHeld
    {
        get { lock (_lock) return _held; }
    }

    private bool EnsureRequest()
    {
        if (_request != IntPtr.Zero && _request != InvalidHandle) return true;
        var ctx = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = "CCSM-MCP: Claude sessions are working",
        };
        _request = PowerCreateRequest(ref ctx);
        if (_request == IntPtr.Zero || _request == InvalidHandle)
        {
            HubLog.Error($"PowerCreateRequest failed (win32 error {Marshal.GetLastWin32Error()}).");
            _request = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>Hold or release the sleep-prevention request. Returns the resulting state.</summary>
    public bool Set(bool hold)
    {
        lock (_lock)
        {
            if (hold && !_held)
            {
                if (!EnsureRequest()) return _held;
                var ok = PowerSetRequest(_request, PowerRequestSystemRequired);
                if (ok && _keepDisplay) ok = PowerSetRequest(_request, PowerRequestDisplayRequired);
                if (ok)
                {
                    _held = true;
                    HubLog.Info($"Sleep prevention ENGAGED (display={_keepDisplay}). Shows up in 'powercfg /requests' as CCSM-MCP.");
                }
                else
                {
                    HubLog.Error($"PowerSetRequest failed (win32 error {Marshal.GetLastWin32Error()}).");
                }
            }
            else if (!hold && _held)
            {
                PowerClearRequest(_request, PowerRequestSystemRequired);
                if (_keepDisplay) PowerClearRequest(_request, PowerRequestDisplayRequired);
                _held = false;
                HubLog.Info("Sleep prevention RELEASED. Windows may sleep again per its power plan.");
            }
            return _held;
        }
    }

    /// <summary>Release on shutdown so the PC is never stuck awake after the hub dies.</summary>
    public void Dispose()
    {
        Set(false);
        lock (_lock)
        {
            if (_request != IntPtr.Zero)
            {
                CloseHandle(_request);
                _request = IntPtr.Zero;
            }
        }
    }
}
