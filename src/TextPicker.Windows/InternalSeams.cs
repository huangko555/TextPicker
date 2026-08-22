using TextPicker;

namespace TextPicker.Windows;

/// <summary>手势来源 seam（ADR-0001/0007）。Phase 0 为测试假件；Phase 2 由真实输入状态机驱动（Owned/注入/Broker）。</summary>
internal interface ISelectionGestureFeed
{
    event EventHandler<GestureDetectedEventArgs> GestureDetected;
    void Start(long epoch);
    void Stop();
}

internal sealed class GestureDetectedEventArgs : EventArgs
{
    public long Epoch { get; init; }
    public SelectionGesture Gesture { get; init; }
    public int TargetProcessId { get; init; }
    public nint TargetWindowHandle { get; init; }
    public PhysicalScreenRect WindowRect { get; init; }
    public PhysicalScreenPoint? DownPoint { get; init; }
    public PhysicalScreenPoint? UpPoint { get; init; }
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>读取后端 seam（ADR-0005 BackendRouter 的 v0 形态；Phase 3 换 UiaSelectionBackend）。</summary>
internal interface ISelectionBackend
{
    Task<BackendReadResult> ReadAsync(BackendReadRequest request, CancellationToken ct);
}

internal sealed class BackendReadRequest
{
    public long Epoch { get; init; }                       // RunEpoch 携带（ADR-0002）
    public SelectionGeneration Generation { get; init; }   // 手势路径
    public SelectionRequestId RequestId { get; init; }     // 显式路径
    public CaptureOrigin Origin { get; init; }
    public SelectionGesture Gesture { get; init; }
    public CandidateTargetSnapshot Target { get; init; } = new();
    public PhysicalScreenPoint? DownPoint { get; init; }
    public PhysicalScreenPoint? UpPoint { get; init; }
    public PhysicalScreenPoint? FallbackAnchor { get; init; }   // 仅显式捕获
    public SelectionPickerOptions Options { get; init; } = new();
}

internal sealed class BackendReadResult
{
    public bool Success { get; init; }
    public CaptureFailureReason Failure { get; init; }
    public SelectionContent Content { get; init; } = new();
    public SelectionGeometry Geometry { get; init; } = new();
    public TargetContext Target { get; init; } = new();
    public PhysicalScreenRect? AnchorRect { get; init; }
    public AnchorSource AnchorSource { get; init; }
    public CaptureBackend Backend { get; init; }
    public SelectionFreshnessEvidence? Freshness { get; init; }
}

/// <summary>跨 lane DTO 标记（ADR-0003）：实现本接口的类型不得携带 COM 接口成员，由 LaneDtoRules 扫描与契约测试 #6 强制。</summary>
internal interface ILaneTransferable
{
}
