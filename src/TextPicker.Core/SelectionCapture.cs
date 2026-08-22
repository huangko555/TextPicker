namespace TextPicker;

public enum AnchorSource
{
    CaretEndpoint = 0,
    MouseReleasePoint = 1,
    FallbackAnchor = 2,
    None = 3,
}

public enum CaptureBackend
{
    UiaTextPattern = 0,
    UiaWholeValue = 1,
}

/// <summary>键盘新鲜度 = 诚实证据模型（不假装存在因果屏障）。仅键盘手势非空；鼠标/显式为 null。</summary>
public enum SelectionFreshnessEvidence
{
    SelectionChangedEvent = 0,     // 等到了同目标 TextSelectionChanged
    ChangedFromBaseline = 1,       // 与触发前基线签名不同
    IdempotentCommandVerified = 2, // 无事件但当前选区==全文档（幂等 Ctrl+A）
    SettledFallback = 3,           // 无基线无事件，仅延迟后稳定
}

/// <summary>不可变捕获结果。</summary>
public sealed record SelectionCapture
{
    public SelectionGeneration? Generation { get; init; }    // 手势捕获非空
    public SelectionRequestId? RequestId { get; init; }      // 显式捕获非空
    public CaptureOrigin Origin { get; init; }
    public SelectionGesture? Gesture { get; init; }          // Explicit 时 null
    public SelectionContent Content { get; init; } = new();
    public SelectionGeometry Geometry { get; init; } = new();
    public TargetContext Target { get; init; } = new();      // 完整 UIA 上下文（仅阶段二）
    public PhysicalScreenPoint? MouseDownPoint { get; init; }  // 键盘手势为 null
    public PhysicalScreenPoint? MouseUpPoint { get; init; }
    public PhysicalScreenRect? AnchorRect { get; init; }       // null = 无锚点仍发布
    public AnchorSource AnchorSource { get; init; }
    public CaptureBackend Backend { get; init; }
    public SelectionFreshnessEvidence? Freshness { get; init; }  // 仅键盘手势非空
    public TimeSpan Elapsed { get; init; }
}
