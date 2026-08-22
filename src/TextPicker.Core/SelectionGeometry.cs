namespace TextPicker;

/// <summary>选区几何。全部字段可空：<c>GetBoundingRectangles</c> 在退化 range / 滚出视区 / 屏幕外 / 被遮挡时可返回空数组（几何可空契约）。</summary>
public sealed record SelectionGeometry
{
    public IReadOnlyList<PhysicalScreenRect> VisibleRects { get; init; } = Array.Empty<PhysicalScreenRect>();  // 可见行矩形；可为空列表
    public PhysicalScreenRect? BoundingRect { get; init; }
    public PhysicalScreenRect? StartRect { get; init; }    // range 折叠到文档序 Start 的矩形
    public PhysicalScreenRect? EndRect { get; init; }      // 折叠到文档序 End
    public SelectionDirection? Direction { get; init; }    // Forward/Backward；无权威来源为 null（绝不猜）
    public int RectCount { get; init; }
    public GeometryCompleteness Completeness { get; init; }

    /// <summary>注意：Completeness 表示本模块几何字段的完整度，不代表选区在屏幕上的可见比例。</summary>
}

public enum GeometryCompleteness
{
    None = 0,
    RectsOnly = 1,
    PartialEndpoints = 2,
    CompleteEndpoints = 3,
}

public enum SelectionDirection
{
    Forward = 0,
    Backward = 1,
}
