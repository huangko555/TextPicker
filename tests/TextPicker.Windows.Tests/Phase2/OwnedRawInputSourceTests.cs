using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase2;

/// <summary>OwnedRawInputSource 现场冒烟：注册归属 + SendInput 合成键盘输入端到端（与 Contract1 同集合串行，避免进程级注册互扰）。</summary>
[Collection("RawInputSerial")]
public sealed class OwnedRawInputSourceTests
{
    private const ushort VK_F13 = 0x7C;

    [Fact]
    public unsafe void Start_RegistersKeyboardAndMouseToOwnHiddenWindow()
    {
        using var source = new OwnedRawInputSource();
        source.Start();

        var registrations = RawInputRegistrationGuard.Query();
        Assert.Contains(registrations, r => r.Usage == RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD && (nint)r.Window.Value == source.WindowHandle);
        Assert.Contains(registrations, r => r.Usage == RawInputRegistrationGuard.HID_USAGE_GENERIC_MOUSE && (nint)r.Window.Value == source.WindowHandle);

        source.Stop();
    }

    [Fact]
    public async Task SyntheticKeyboardInput_FlowsToRawInput_EndToEnd()
    {
        using var source = new OwnedRawInputSource();
        var records = new List<InputRecord>();
        source.RecordReceived += records.Add;
        source.Start();

        // 合成 F13（无标准应用消费的虚拟键）按下+抬起。
        SendF13Press();

        var sawF13 = await PickerEventLog.EventuallyAsync(() =>
            records.OfType<InputRecord.Key>().Any(k => k.VirtualKey == VK_F13 && k.Action == InputKeyAction.Down) &&
            records.OfType<InputRecord.Key>().Any(k => k.VirtualKey == VK_F13 && k.Action == InputKeyAction.Up));

        Assert.True(sawF13, "SendInput 合成键盘输入未到达 Raw Input（WM_INPUT 泵或翻译链路断裂）");
        source.Stop();
    }

    private static unsafe void SendF13Press()
    {
        var down = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        down.ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)VK_F13, dwFlags = 0 };
        var up = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        up.ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)VK_F13, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP };
        _ = PInvoke.SendInput(new[] { down, up }, sizeof(INPUT));
    }

    [Fact]
    public unsafe void StopThenStart_RegistersAgain()
    {
        using var source = new OwnedRawInputSource();
        source.Start();
        source.Stop();
        source.Start();

        Assert.True(source.WindowHandle != 0);
        Assert.Contains(RawInputRegistrationGuard.Query(), r => r.Usage == RawInputRegistrationGuard.HID_USAGE_GENERIC_KEYBOARD && (nint)r.Window.Value == source.WindowHandle);
        source.Stop();
    }
}
