using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextPicker.Windows;

/// <summary>输入记录源 seam（ADR-0001）：Owned / 注入 / Broker 三模式的公共面。</summary>
internal interface IInputRecordSource : IDisposable
{
    /// <summary>归一化输入记录（泵线程同步回调）。</summary>
    event Action<InputRecord>? RecordReceived;

    void Start();
    void Stop();
}

/// <summary>
/// Owned 模式输入源（ADR-0001）：自持线程 + 隐藏顶级窗口（WM_INPUT 的 hwndTarget 必须是顶级窗口）+ 自有线程 GetMessage 泵。
/// Start fail-fast：注册前用 GetRegisteredRawInputDevices 检查键盘/鼠标归属，非自身 → RawInputRegistrationConflict，绝不覆盖。
/// RIDEV_INPUTSINK：无焦点也接收（后台观察其他应用输入的必要条件）。
/// </summary>
internal sealed unsafe class OwnedRawInputSource : IInputRecordSource
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly object _gate = new();
    private volatile uint _pumpThreadId;
    private Exception? _startupError;
    private bool _running;
    private bool _disposed;

    public OwnedRawInputSource()
    {
    }

    public event Action<InputRecord>? RecordReceived;

    /// <summary>本源注册窗口句柄（仅测试诊断用；启动前为 0）。</summary>
    internal nint WindowHandle { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _startupError = null;
            _ready.Reset();
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TextPicker.RawInput",
            };
            _thread = thread;
            thread.Start();
            _ready.Wait(StartupTimeout);
            _running = true;

            if (_startupError is { } error)
            {
                Stop();
                throw error;    // fail-fast：含 RawInputRegistrationConflict
            }
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
        RecordReceived = null;
    }

    private void Run()
    {
        HWND hwnd = default;
        bool registered = false;
        try
        {
            _pumpThreadId = PInvoke.GetCurrentThreadId();
            _ = PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);    // 强制建立线程消息队列

            hwnd = PInvoke.CreateWindowEx(
                0,
                "STATIC",
                null,
                WINDOW_STYLE.WS_OVERLAPPED,
                0,
                0,
                160,
                120,
                default,
                null,
                null,
                null);
            if (hwnd.IsNull)
            {
                throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            }

            WindowHandle = (nint)hwnd.Value;

            // fail-fast：绝不悄悄覆盖同进程既有注册（ADR-0001）。
            RawInputRegistrationGuard.EnsureOwnable(hwnd);

            var devices = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, hwndTarget = hwnd },
                new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, hwndTarget = hwnd },
            };
            unsafe
            {
                if (!PInvoke.RegisterRawInputDevices(devices, (uint)sizeof(RAWINPUTDEVICE)))
                {
                    throw new InvalidOperationException($"RegisterRawInputDevices failed: {Marshal.GetLastWin32Error()}");
                }
            }

            registered = true;
        }
        catch (Exception error)
        {
            _startupError = error;
            return;    // finally 清理 + _ready.Set 在下方统一
        }
        finally
        {
            if (_startupError != null && !hwnd.IsNull)
            {
                _ = PInvoke.DestroyWindow(hwnd);
                WindowHandle = 0;
            }

            _ready.Set();
        }

        try
        {
            while (PInvoke.GetMessage(out var message, default, 0, 0) > 0)
            {
                if (message.message == WM_INPUT)
                {
                    HandleRawInput(message.lParam, message.time);
                }

                // 其余消息丢弃：本窗口永不显示、无 WndProc 消费者；WM_QUIT 退出循环。
            }
        }
        finally
        {
            if (registered)
            {
                var removal = new RAWINPUTDEVICE[]
                {
                    new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, hwndTarget = default },
                    new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, hwndTarget = default },
                };
                unsafe
                {
                    _ = PInvoke.RegisterRawInputDevices(removal, (uint)sizeof(RAWINPUTDEVICE));
                }
            }

            _ = PInvoke.DestroyWindow(hwnd);
            WindowHandle = 0;
        }
    }

    private unsafe void HandleRawInput(nint rawInputHandle, uint messageTime)
    {
        if (RecordReceived == null)
        {
            return;
        }

        var raw = default(RAWINPUT);
        var size = (uint)sizeof(RAWINPUT);
        var buffer = new Span<byte>(&raw, (int)size);
        if (PInvoke.GetRawInputData(new HRAWINPUT((void*)rawInputHandle), global::Windows.Win32.UI.Input.RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, buffer, ref size, (uint)sizeof(RAWINPUTHEADER)) == uint.MaxValue)
        {
            return;    // 取原始数据失败：安静丢弃单条消息
        }

        var pointer = CurrentPointer();
        var modifiers = CurrentModifiers();
        var foreground = CurrentForeground();
        var record = RawInputTranslator.TryTranslate(in raw, messageTime, pointer, modifiers, foreground);
        if (record != null)
        {
            RecordReceived(record);
        }
    }

    private static PhysicalScreenPoint CurrentPointer()
    {
        if (PInvoke.GetPhysicalCursorPos(out var point))
        {
            return new PhysicalScreenPoint(point.X, point.Y);
        }

        return default;
    }

    private static ModifierSnapshot CurrentModifiers() => new(
        Ctrl: IsDown(0x11),
        Shift: IsDown(0x10),
        Alt: IsDown(0x12),
        Win: IsDown(0x5B) || IsDown(0x5C));

    private static bool IsDown(int virtualKey) => (PInvoke.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static ForegroundTargetSnapshot CurrentForeground()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull)
        {
            return ForegroundTargetSnapshot.Unknown;
        }

        _ = PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        return new ForegroundTargetSnapshot((nint)hwnd.Value, (int)pid);
    }
}
