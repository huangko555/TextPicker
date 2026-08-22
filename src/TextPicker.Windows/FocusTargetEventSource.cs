using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextPicker.Windows;

/// <summary>前台目标变化事件源 seam（门面与测试可替换）。</summary>
internal interface IFocusTargetSource : IDisposable
{
    event Action<ForegroundTargetSnapshot>? ForegroundChanged;
    void Start();
    void Stop();
}

/// <summary>
/// WinEvent 前台钩（改编 InputCue NativeFocusEventHook）：自持线程拥有 out-of-context 回调所需的消息队列；
/// EVENT_SYSTEM_FOREGROUND → 前台 HWND/PID 浅快照（Win32-only，无 UIA；TargetContext 富化在 Phase 3）。
/// </summary>
internal sealed class WinEventFocusTargetSource : IFocusTargetSource
{
    private const uint WM_QUIT = 0x0012;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly WINEVENTPROC _callback;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile uint _pumpThreadId;
    private bool _running;
    private bool _disposed;

    public WinEventFocusTargetSource() => _callback = OnWinEvent;

    public event Action<ForegroundTargetSnapshot>? ForegroundChanged;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _ready.Reset();
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TextPicker.WinEvent",
            };
            _thread = thread;
            thread.Start();
            _ready.Wait(StartupTimeout);
            _running = true;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _running = false;
            if (thread == null)
            {
                return;
            }

            if (_pumpThreadId != 0)
            {
                _ = PInvoke.PostThreadMessage(_pumpThreadId, WM_QUIT, default, default);
            }
        }

        thread.Join(ShutdownTimeout);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
        ForegroundChanged = null;
    }

    private unsafe void Run()
    {
        global::Windows.Win32.UnhookWinEventSafeHandle? hook = null;
        try
        {
            _pumpThreadId = PInvoke.GetCurrentThreadId();
            _ = PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            hook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                null,
                _callback,
                0,
                0,
                PInvoke.WINEVENT_OUTOFCONTEXT);

            _ready.Set();
            if (hook == null || hook.IsInvalid)
            {
                return;
            }

            while (PInvoke.GetMessage(out var message, default, 0, 0) > 0)
            {
                _ = PInvoke.TranslateMessage(in message);
                _ = PInvoke.DispatchMessage(in message);
            }
        }
        finally
        {
            hook?.Dispose();
            _ready.Set();
        }
    }

    private unsafe void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_disposed || eventType != PInvoke.EVENT_SYSTEM_FOREGROUND || hwnd.IsNull)
        {
            return;
        }

        _ = PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        ForegroundChanged?.Invoke(new ForegroundTargetSnapshot((nint)hwnd.Value, (int)pid));
    }
}
