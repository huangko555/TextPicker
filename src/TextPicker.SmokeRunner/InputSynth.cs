using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;

namespace TextPicker.SmokeRunner;

/// <summary>SendInput 合成输入（自持 P/Invoke；与面板/跑批器进程无关地产生真实输入流）。</summary>
internal static unsafe class InputSynth
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_RIGHT = 0x27;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint hwndAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    public static nint ConsoleWindow => GetConsoleWindow();

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void LeftDown()
    {
        var input = Mouse(MOUSEEVENTF_LEFTDOWN);
        Send(input);
    }

    public static void LeftUp()
    {
        var input = Mouse(MOUSEEVENTF_LEFTUP);
        Send(input);
    }

    public static void Click(int x, int y, int holdMs = 30)
    {
        MoveTo(x, y);
        Thread.Sleep(40);
        LeftDown();
        Thread.Sleep(holdMs);
        LeftUp();
    }

    public static void Drag(int x1, int y1, int x2, int y2, int steps = 12)
    {
        MoveTo(x1, y1);
        Thread.Sleep(60);
        LeftDown();
        Thread.Sleep(60);
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(10);
        }

        Thread.Sleep(40);
        LeftUp();
    }

    public static void DoubleClick(int x, int y)
    {
        Click(x, y, holdMs: 20);
        Thread.Sleep(60);
        Click(x, y, holdMs: 20);
    }

    public static void TripleClick(int x, int y)
    {
        Click(x, y, holdMs: 15);
        Thread.Sleep(55);
        Click(x, y, holdMs: 15);
        Thread.Sleep(55);
        Click(x, y, holdMs: 15);
    }

    public static void KeyTap(ushort vk, int holdMs = 30)
    {
        Key(vk, down: true);
        Thread.Sleep(holdMs);
        Key(vk, down: false);
    }

    public static void Key(ushort vk, bool down)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP };
        Send(input);
    }

    public static void CtrlA()
    {
        Key(VK_CONTROL, down: true);
        Thread.Sleep(100);
        KeyTap(0x41, holdMs: 25);
        Thread.Sleep(20);
        Key(VK_CONTROL, down: false);
    }

    public static void ShiftArrow(ushort vk, int repeats = 6)
    {
        Key(VK_SHIFT, down: true);
        Thread.Sleep(30);
        for (int i = 0; i < repeats; i++)
        {
            KeyTap(vk, holdMs: 40);
            Thread.Sleep(60);
        }

        Thread.Sleep(100);
        Key(VK_SHIFT, down: false);
    }

    public static void Escape()
    {
        KeyTap(0x1B, holdMs: 25);
    }

    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var down = new INPUT { type = INPUT_KEYBOARD };
            down.u.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE };
            var up = new INPUT { type = INPUT_KEYBOARD };
            up.u.ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP };
            Send(down, up);
            Thread.Sleep(8);
        }
    }

    /// <summary>前台切换前置的 ALT 点按（绕过前台锁定的惯用法）。</summary>
    public static void FocusWindow(nint hwnd)
    {
        KeyTap(VK_MENU, holdMs: 10);
        _ = SetForegroundWindow(hwnd);
        Thread.Sleep(150);
    }

    public static void PlaceWindow(nint hwnd, int x, int y, int w, int h)
        => SetWindowPos(hwnd, 0, x, y, w, h, 0);    // HWND_TOP，并激活测试目标，避免被既有窗口遮挡

    public static RECT WindowRect(nint hwnd) => GetWindowRect(hwnd, out var rect) ? rect : default;

    public static double WindowDpiScale(nint hwnd) => Math.Max(1, GetDpiForWindow(hwnd)) / 96d;

    public static POINT ClientOrigin(nint hwnd)
    {
        var point = new POINT();
        return ClientToScreen(hwnd, ref point) ? point : default;
    }

    public static POINT CursorPosition() => GetCursorPos(out var point) ? point : default;

    public static TextPicker.PhysicalScreenPoint ToPhysicalPoint(POINT point) => new(point.X, point.Y);

    /// <summary>返回非主屏上的稳定放置点；没有第二块屏幕时返回 null。</summary>
    public static POINT? SecondaryScreenPoint()
    {
        int primaryWidth = GetSystemMetrics(0);
        int primaryHeight = GetSystemMetrics(1);
        int virtualLeft = GetSystemMetrics(76);
        int virtualTop = GetSystemMetrics(77);
        int virtualWidth = GetSystemMetrics(78);
        int virtualHeight = GetSystemMetrics(79);

        if (virtualWidth > primaryWidth)
        {
            return new POINT { X = virtualLeft < 0 ? virtualLeft + 160 : primaryWidth + 160, Y = virtualTop + 160 };
        }

        if (virtualHeight > primaryHeight)
        {
            return new POINT { X = virtualLeft + 160, Y = virtualTop < 0 ? virtualTop + 160 : primaryHeight + 160 };
        }

        return null;
    }

    private static INPUT Mouse(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dwFlags = flags };
        return input;
    }

    private static void Send(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SendInput only accepted {sent}/{inputs.Length} records.");
        }
    }
}

/// <summary>EnumWindows 窗口定位（Win11 打包应用如记事本的 MainWindowHandle 不回填，需按进程名枚举）。</summary>
internal static class WindowFinder
{
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>等待一个「新出现」的可见顶级窗口。按 HWND 快照区分，兼容浏览器复用既有进程。</summary>
    public static (nint Hwnd, int Pid) FindNewWindow(HashSet<nint> existingWindows, string processName, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var found = FindWithPid(hwnd =>
            {
                _ = GetWindowThreadProcessId(hwnd, out var pid);
                return !existingWindows.Contains(hwnd) && BelongsToProcess((int)pid, processName);
            });
            if (found.Hwnd != 0)
            {
                return found;
            }

            Thread.Sleep(200);
        }

        return (0, 0);
    }

    private static bool BelongsToProcess(int pid, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>快照当前所有属于指定进程名的可见顶级窗口 HWND（启动目标前调用）。</summary>
    public static HashSet<nint> SnapshotWindows(string processName)
    {
        var windows = new HashSet<nint>();
        _ = EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                var threadId = GetWindowThreadProcessId(hwnd, out var pid);
                if (threadId != 0 && BelongsToProcess((int)pid, processName))
                {
                    windows.Add(hwnd);
                }
            }

            return true;
        }, 0);
        return windows;
    }

    public static nint FindWindowForProcess(int processId, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var found = FindWithPid(hwnd =>
            {
                var threadId = GetWindowThreadProcessId(hwnd, out var pid);
                return threadId != 0 && pid == processId;
            });
            if (found.Hwnd != 0)
            {
                return found.Hwnd;
            }

            Thread.Sleep(100);
        }

        return 0;
    }

    private static (nint Hwnd, int Pid) FindWithPid(Func<nint, bool> predicate)
    {
        nint found = 0;
        uint foundPid = 0;
        _ = EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && found == 0 && predicate(hwnd))
            {
                found = hwnd;
                if (GetWindowThreadProcessId(hwnd, out foundPid) == 0)
                {
                    found = 0;
                }
            }

            return true;
        }, 0);
        return (found, (int)foundPid);
    }
}
