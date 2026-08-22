namespace TextPicker;

public enum CaretSource
{
    UiaTextPattern2CaretRange = 0,
    UiaTextRangeCollapsed = 1,
    Win32GuiThreadInfo = 2,
    MsaaCaret = 3,
    // 无 TSF：ITfThreadMgr::GetFocus 不可跨进程（ADR-0004/0007 同源的 v6.1 平台事实）
}

/// <summary>插入光标观察（无正文）。</summary>
public sealed record CaretObservation
{
    public PhysicalScreenRect CaretRect { get; init; }
    public CaretSource Source { get; init; }

    /// <summary>false = 实为选区端点（非折叠）。</summary>
    public bool IsCollapsedSelection { get; init; }
    public TargetContext Target { get; init; } = new();
}

/// <summary>无正文选区状态（InputCue 合规入口）：非折叠标记 + 几何 + Target，永不含正文。</summary>
public sealed record SelectionState
{
    public bool HasNonCollapsedSelection { get; init; }
    public SelectionGeometry? Geometry { get; init; }
    public TargetContext? Target { get; init; }
}
