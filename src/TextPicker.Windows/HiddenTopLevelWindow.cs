using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextPicker.Windows;

/// <summary>隐藏顶级窗口（Phase 0 供测试与守卫用；Phase 2 的 OwnedRawInputSource 改为自持线程 + 消息泵，见 ADR-0001）。
/// WM_INPUT 的 hwndTarget 必须是顶级窗口；使用系统 "STATIC" 类避免 RegisterClass 样板。</summary>
internal sealed unsafe class HiddenTopLevelWindow : IDisposable
{
    private HWND _hwnd;

    public HiddenTopLevelWindow(int width = 160, int height = 120)
    {
        _hwnd = PInvoke.CreateWindowEx(
            0,
            "STATIC",
            null,
            WINDOW_STYLE.WS_OVERLAPPED,
            0,
            0,
            width,
            height,
            default,
            null,
            null,
            null);
        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public HWND Hwnd => _hwnd;

    public void Dispose()
    {
        if (!_hwnd.IsNull)
        {
            _ = PInvoke.DestroyWindow(_hwnd);
            _hwnd = default;
        }
    }
}
