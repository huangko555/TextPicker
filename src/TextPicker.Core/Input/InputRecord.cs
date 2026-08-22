namespace TextPicker;

/// <summary>归一化未分类输入记录（ADR-0001 注入 DTO）：分类只归 Core 状态机，宿主/模块不做双重分类。
/// MessageTimeMs = GetMessageTime() 时钟域（RAWINPUTHEADER 无时间戳）；单调由注入方保证。</summary>
public abstract record InputRecord(long MessageTimeMs, ForegroundTargetSnapshot Foreground)
{
    public sealed record Key(
        long MessageTimeMs,
        ForegroundTargetSnapshot Foreground,
        InputKeyAction Action,
        ushort VirtualKey,
        ModifierSnapshot Modifiers) : InputRecord(MessageTimeMs, Foreground);

    public sealed record PointerDown(
        long MessageTimeMs,
        ForegroundTargetSnapshot Foreground,
        PointerButton Button,
        PhysicalScreenPoint Point,
        ModifierSnapshot Modifiers) : InputRecord(MessageTimeMs, Foreground);

    public sealed record PointerUp(
        long MessageTimeMs,
        ForegroundTargetSnapshot Foreground,
        PointerButton Button,
        PhysicalScreenPoint Point,
        ModifierSnapshot Modifiers) : InputRecord(MessageTimeMs, Foreground);

    public sealed record PointerWheel(
        long MessageTimeMs,
        ForegroundTargetSnapshot Foreground,
        PhysicalScreenPoint Point,
        ModifierSnapshot Modifiers,
        int Delta) : InputRecord(MessageTimeMs, Foreground);
}

public enum InputKeyAction
{
    Down = 0,
    Up = 1,
}

public enum PointerButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    XButton = 3,
}

/// <summary>事件时刻的修饰键快照。</summary>
public sealed record ModifierSnapshot(bool Ctrl, bool Shift, bool Alt, bool Win)
{
    public static ModifierSnapshot None { get; } = new(false, false, false, false);
}

/// <summary>前台目标浅快照（Raw Input 线程捕获 HWND，PID 由注入方解析）。</summary>
public sealed record ForegroundTargetSnapshot(nint WindowHandle, int ProcessId)
{
    public static ForegroundTargetSnapshot Unknown { get; } = new(0, 0);
}

/// <summary>手势层丢弃原因（静默，不产生 generation；进 Counters 供诊断，ADR-0008 string-free）。</summary>
public enum GestureDropReason
{
    /// <summary>Hook 消息超过约 1 秒才被处理（GetMessageTime 时钟）。</summary>
    ExpiredMessage = 0,

    /// <summary>打断动作（滚轮/右键/中键/Esc）清空了未完成手势状态。</summary>
    Interrupted = 1,

    /// <summary>无效输入序列（如无按下记录的抬起）。</summary>
    InvalidSequence = 2,
}
