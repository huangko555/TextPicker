using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #1：Raw Input 所有权——同进程双注册冲突复现（后注册者抢占）；Owned fail-fast；注入模式无冲突。</summary>
[Collection("RawInputSerial")]
public sealed class Contract1RawInputOwnership
{
    [Fact]
    public void SameProcessDoubleRegistration_LastCallerWins_AndOwnedFailsFast()
    {
        using var windowA = new HiddenTopLevelWindow();
        using var windowB = new HiddenTopLevelWindow();

        try
        {
            RegisterKeyboardAndMouse(windowA.Hwnd);
            AssertOwner(windowA.Hwnd);

            // 同进程第二次注册同一设备类：最后一次调用者生效（微软契约，冲突复现）。
            RegisterKeyboardAndMouse(windowB.Hwnd);
            AssertOwner(windowB.Hwnd);

            // Owned fail-fast：目标窗口 A 检测到注册归属 B ≠ A → 冲突，绝不覆盖。
            Assert.Throws<RawInputRegistrationConflict>(() => RawInputRegistrationGuard.EnsureOwnable(windowA.Hwnd));

            // 当前持有者自身可注册（无冲突）。
            RawInputRegistrationGuard.EnsureOwnable(windowB.Hwnd);
        }
        finally
        {
            RemoveKeyboardAndMouse();
        }

        // 注入模式语义：进程内无注册时任意目标可 Owned 注册（守卫放行）。
        RawInputRegistrationGuard.EnsureOwnable(windowA.Hwnd);
    }

    private static unsafe void RegisterKeyboardAndMouse(HWND hwnd)
    {
        var devices = new RAWINPUTDEVICE[]
        {
            new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD, dwFlags = default, hwndTarget = hwnd },
            new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE, dwFlags = default, hwndTarget = hwnd },
        };
        Assert.True(PInvoke.RegisterRawInputDevices(devices, (uint)sizeof(RAWINPUTDEVICE)), "RegisterRawInputDevices failed");
    }

    private static unsafe void RemoveKeyboardAndMouse()
    {
        var devices = new RAWINPUTDEVICE[]
        {
            new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, hwndTarget = default },
            new() { usUsagePage = RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC, usUsage = RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, hwndTarget = default },
        };
        _ = PInvoke.RegisterRawInputDevices(devices, (uint)sizeof(RAWINPUTDEVICE));
    }

    private static void AssertOwner(HWND expected)
    {
        var keyboard = RawInputRegistrationGuard.Query()
            .Single(r => r.UsagePage == RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC && r.Usage == RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD);
        Assert.Equal(expected, keyboard.Window);

        var mouse = RawInputRegistrationGuard.Query()
            .Single(r => r.UsagePage == RawInputRegistrationGuard.HID_USAGE_PAGE_GENERIC && r.Usage == RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE);
        Assert.Equal(expected, mouse.Window);
    }
}
