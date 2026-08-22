namespace TextPicker;

/// <summary>阶段一快照：Win32 浅信息，结构上不可能触发 UIA（构造只需 PID/HWND/窗口矩形/指针位置）。
/// ProcessName 惰性求值由消费者侧完成，本类型只携带 PID。</summary>
public sealed record CandidateTargetSnapshot
{
    public int ProcessId { get; init; }
    public nint WindowHandle { get; init; }
    public PhysicalScreenRect WindowRect { get; init; }
    public PhysicalScreenPoint? PointerPoint { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>阶段一事件载荷：零 UIA、无正文。</summary>
public sealed record SelectionCandidateReady
{
    public SelectionGeneration Generation { get; init; }
    public SelectionGesture Gesture { get; init; }
    public CandidateTargetSnapshot Target { get; init; } = new();
    public PhysicalScreenPoint? ProvisionalAnchor { get; init; }   // 键盘手势为 null
}
