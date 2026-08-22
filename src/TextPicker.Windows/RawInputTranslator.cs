using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;

namespace TextPicker.Windows;

/// <summary>WM_INPUT 原始结构 → 归一化输入记录的纯翻译（ADR-0001 注入 DTO）。线程无关、无状态；Win32 上下文（指针位置/修饰键/前台）由委托注入以便单测。</summary>
internal static class RawInputTranslator
{
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
    private const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const ushort RI_KEY_BREAK = 0x0001;

    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    /// <summary>尝试翻译；纯移动（无按键/滚轮位）返回 null（不上抛，减轻状态机负担）。</summary>
    public static InputRecord? TryTranslate(
        in RAWINPUT raw,
        long messageTimeMs,
        PhysicalScreenPoint pointerPosition,
        ModifierSnapshot modifiers,
        ForegroundTargetSnapshot foreground)
    {
        if (raw.header.dwType == RIM_TYPEKEYBOARD)
        {
            return TranslateKeyboard(raw.data.keyboard, messageTimeMs, modifiers, foreground);
        }

        if (raw.header.dwType == RIM_TYPEMOUSE)
        {
            return TranslateMouse(raw.data.mouse, messageTimeMs, pointerPosition, modifiers, foreground);
        }

        return null;
    }

    private static InputRecord.Key? TranslateKeyboard(in RAWKEYBOARD keyboard, long messageTimeMs, ModifierSnapshot modifiers, ForegroundTargetSnapshot foreground)
    {
        if (keyboard.VKey is 0 or 0xFF)
        {
            return null;    // VK_NONE / 非键盘事件
        }

        var action = (keyboard.Flags & RI_KEY_BREAK) != 0 ? InputKeyAction.Up : InputKeyAction.Down;
        return new InputRecord.Key(messageTimeMs, foreground, action, keyboard.VKey, modifiers);
    }

    private static InputRecord? TranslateMouse(in RAWMOUSE mouse, long messageTimeMs, PhysicalScreenPoint pointerPosition, ModifierSnapshot modifiers, ForegroundTargetSnapshot foreground)
    {
        var flags = mouse.usButtonFlags;
        if (flags == 0)
        {
            return null;    // 纯移动
        }

        if ((flags & RI_MOUSE_WHEEL) != 0)
        {
            return new InputRecord.PointerWheel(messageTimeMs, foreground, pointerPosition, modifiers, (short)mouse.usButtonData);
        }

        PointerButton? button = null;
        InputKeyAction? action = null;
        if (HasPair(flags, RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP, ref button, PointerButton.Left))
        {
            action = DownOrUp(flags, RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP);
        }
        else if (HasPair(flags, RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP, ref button, PointerButton.Right))
        {
            action = DownOrUp(flags, RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP);
        }
        else if (HasPair(flags, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP, ref button, PointerButton.Middle))
        {
            action = DownOrUp(flags, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP);
        }
        else if (HasPair(flags, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP, ref button, PointerButton.XButton))
        {
            action = DownOrUp(flags, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP);
        }

        if (button is not { } resolvedButton || action is not { } resolvedAction)
        {
            return null;
        }

        return resolvedAction == InputKeyAction.Down
            ? new InputRecord.PointerDown(messageTimeMs, foreground, resolvedButton, pointerPosition, modifiers)
            : new InputRecord.PointerUp(messageTimeMs, foreground, resolvedButton, pointerPosition, modifiers);
    }

    private static bool HasPair(ushort flags, ushort downFlag, ushort upFlag, ref PointerButton? button, PointerButton candidate)
    {
        if ((flags & downFlag) == 0 && (flags & upFlag) == 0)
        {
            return false;
        }

        button = candidate;
        return true;
    }

    private static InputKeyAction DownOrUp(ushort flags, ushort downFlag, ushort upFlag)
        => (flags & downFlag) != 0 ? InputKeyAction.Down : InputKeyAction.Up;
}
