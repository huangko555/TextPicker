namespace TextPicker;

/// <summary>显式捕获（query 式）与 Probe 的结果信封。</summary>
public sealed record SelectionCaptureResult
{
    public bool Success { get; init; }
    public SelectionCapture? Capture { get; init; }
    public CaptureFailureReason? FailureReason { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static SelectionCaptureResult Ok(SelectionCapture capture) => new() { Success = true, Capture = capture, Elapsed = capture.Elapsed };
    public static SelectionCaptureResult Fail(CaptureFailureReason reason, TimeSpan elapsed) => new() { Success = false, FailureReason = reason, Elapsed = elapsed };
}

/// <summary>ProbeTargetAsync 结果：目标上下文 + 可选正文/几何。</summary>
public sealed record TargetProbeResult
{
    public bool Success { get; init; }
    public CaptureFailureReason? FailureReason { get; init; }
    public TargetContext? Target { get; init; }
    public SelectionContent? Content { get; init; }      // includeText=true 时
    public SelectionGeometry? Geometry { get; init; }
    public TimeSpan Elapsed { get; init; }
}
