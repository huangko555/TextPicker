namespace TextPicker;

/// <summary>阶段流水：CandidateStarted → PolicyChecked → BackendStarted → BackendFinished → AnchorResolved → Published / Failed / Superseded / Cancelled。</summary>
public enum SelectionPipelineStage
{
    CandidateStarted = 0,
    PolicyChecked = 1,
    BackendStarted = 2,
    BackendFinished = 3,
    AnchorResolved = 4,
    Published = 5,
    Failed = 6,
    Superseded = 7,
    Cancelled = 8,
}

/// <summary>诊断事件参数。结构上不可能携带选区正文：类型层面不含任何 string 自由文本字段（ADR-0008）。</summary>
public sealed class SelectionDiagnosticsEventArgs : EventArgs
{
    public SelectionPipelineStage Stage { get; init; }
    public SelectionGeneration? Generation { get; init; }
    public SelectionRequestId? RequestId { get; init; }
    public SelectionGesture? Gesture { get; init; }
    public CaptureFailureReason? FailureReason { get; init; }
    public CaptureBackend? Backend { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>计数器快照（string-free，ADR-0008）。</summary>
public sealed record SelectionPickerCounters
{
    public long CandidatesPublished { get; init; }
    public long CapturesSucceeded { get; init; }
    public long CapturesFailed { get; init; }
    public long Superseded { get; init; }
    public long Invalidated { get; init; }
    public long Cancelled { get; init; }
    public long ExplicitQueries { get; init; }

    /// <summary>手势层静默丢弃计数（过期消息/打断清态/无效序列；无 generation 生命周期）。</summary>
    public IReadOnlyDictionary<GestureDropReason, int> GestureDropsByReason { get; init; }
        = new Dictionary<GestureDropReason, int>();

    public IReadOnlyDictionary<CaptureFailureReason, int> FailuresByReason { get; init; }
        = new Dictionary<CaptureFailureReason, int>();
    public IReadOnlyDictionary<SelectionInvalidationReason, int> InvalidationsByReason { get; init; }
        = new Dictionary<SelectionInvalidationReason, int>();
}
