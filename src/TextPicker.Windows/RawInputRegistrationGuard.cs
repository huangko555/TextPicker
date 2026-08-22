using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;

namespace TextPicker.Windows;

// CA1710：RawInputRegistrationConflict 命名为冻结契约（ADR-0001），不改名。
#pragma warning disable CA1710

/// <summary>Raw Input 注册权冲突（Owned 模式 fail-fast，ADR-0001）：目标设备类已被同进程其他窗口注册。</summary>
public sealed class RawInputRegistrationConflict : Exception
{
    public RawInputRegistrationConflict(string message) : base(message)
    {
    }
}

#pragma warning restore CA1710

internal readonly record struct RawInputDeviceRegistration(HWND Window, ushort UsagePage, ushort Usage);

/// <summary>Raw Input 注册归属查询与 Owned fail-fast 守卫（ADR-0001）。
/// 微软契约：每设备类进程内只能有一个注册窗口，最后一次调用者生效；库不得悄悄覆盖既有注册。</summary>
internal static class RawInputRegistrationGuard
{
    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    public const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    public static IReadOnlyList<RawInputDeviceRegistration> Query()
    {
        uint size = (uint)Unsafe.SizeOf<RAWINPUTDEVICE>();

        // 第一次调用：空缓冲只取数量。
        uint count = 0;
        _ = PInvoke.GetRegisteredRawInputDevices(default, ref count, size);
        if (count == 0)
        {
            return Array.Empty<RawInputDeviceRegistration>();
        }

        // 第二次调用：并发变化时按需扩容重试。
        RAWINPUTDEVICE[] devices;
        uint returned;
        while (true)
        {
            devices = new RAWINPUTDEVICE[count];
            uint capacity = count;
            returned = PInvoke.GetRegisteredRawInputDevices(devices, ref capacity, size);
            if (returned <= capacity)
            {
                break;
            }

            count = returned;
        }

        var list = new List<RawInputDeviceRegistration>((int)returned);
        for (uint i = 0; i < returned && i < (uint)devices.Length; i++)
        {
            list.Add(new RawInputDeviceRegistration(devices[i].hwndTarget, devices[i].usUsagePage, devices[i].usUsage));
        }

        return list;
    }

    /// <summary>Owned 模式注册前置检查：键盘/鼠标任一注册归属非目标窗口 → 抛 <see cref="RawInputRegistrationConflict"/>，绝不覆盖。</summary>
    public static void EnsureOwnable(HWND target)
    {
        foreach (var reg in Query())
        {
            bool keyboardOrMouse = reg.UsagePage == HID_USAGE_PAGE_GENERIC
                && (reg.Usage == HID_USAGE_GENERIC_MOUSE || reg.Usage == HID_USAGE_GENERIC_KEYBOARD);
            if (keyboardOrMouse && reg.Window != target)
            {
                throw new RawInputRegistrationConflict(
                    $"Raw Input device (usage {reg.Usage}) already registered to another window in this process; use broker/injected mode instead.");
            }
        }
    }
}
