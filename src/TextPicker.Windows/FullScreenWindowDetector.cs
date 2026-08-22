using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace TextPicker.Windows;

/// <summary>前台窗口全屏检测（改编 InputCue FullScreenWindowDetector）：窗口矩形覆盖所在显示器整面（±2px 容差）。</summary>
internal static unsafe class FullScreenWindowDetector
{
    private const int BoundsTolerancePixels = 2;

    public static bool IsForegroundWindowFullScreen()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull ||
            window == PInvoke.GetShellWindow() ||
            !PInvoke.IsWindowVisible(window) ||
            PInvoke.IsIconic(window) ||
            !PInvoke.GetWindowRect(window, out var bounds))
        {
            return false;
        }

        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (monitor == default)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        return PInvoke.GetMonitorInfo(monitor, ref info) && IsFullScreenBounds(bounds, info.rcMonitor);
    }

    internal static bool IsFullScreenBounds(RECT window, RECT monitor) =>
        HasPositiveArea(window) &&
        HasPositiveArea(monitor) &&
        IsNear(window.left, monitor.left) &&
        IsNear(window.top, monitor.top) &&
        IsNear(window.right, monitor.right) &&
        IsNear(window.bottom, monitor.bottom);

    private static bool HasPositiveArea(RECT bounds) => bounds.right > bounds.left && bounds.bottom > bounds.top;

    private static bool IsNear(int first, int second) => Math.Abs((long)first - second) <= BoundsTolerancePixels;
}
