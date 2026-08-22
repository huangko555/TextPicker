namespace TextPicker;

/// <summary>几何解析：SAFEARRAY 展平后的 double 矩形数组 → <see cref="SelectionGeometry"/>。
/// count%4 != 0 视为无效（返回 null，调用方按几何缺失处理）；Completeness 只描述本模块字段完整度，不代表屏幕可见比例。</summary>
public static class GeometryBuilder
{
    /// <summary>尝试构建；raw 为 null、空或 count%4 != 0 时对可见矩形部分返回无效（null）。</summary>
    public static SelectionGeometry? TryBuild(
        IReadOnlyList<double>? rawRects,
        PhysicalScreenRect? startRect,
        PhysicalScreenRect? endRect,
        SelectionDirection? direction)
    {
        if (rawRects == null || rawRects.Count % 4 != 0)
        {
            return null;
        }

        var rects = new List<PhysicalScreenRect>(rawRects.Count / 4);
        for (int i = 0; i + 3 < rawRects.Count; i += 4)
        {
            rects.Add(new PhysicalScreenRect(rawRects[i], rawRects[i + 1], rawRects[i + 2], rawRects[i + 3]));
        }

        PhysicalScreenRect? bounding = null;
        foreach (var rect in rects)
        {
            bounding = bounding is { } b
                ? new PhysicalScreenRect(Math.Min(b.Left, rect.Left), Math.Min(b.Top, rect.Top), Math.Max(b.Right, rect.Right), Math.Max(b.Bottom, rect.Bottom))
                : rect;
        }

        int endpoints = (startRect != null ? 1 : 0) + (endRect != null ? 1 : 0);
        var completeness = endpoints switch
        {
            2 => GeometryCompleteness.CompleteEndpoints,
            1 => GeometryCompleteness.PartialEndpoints,
            _ => rects.Count > 0 ? GeometryCompleteness.RectsOnly : GeometryCompleteness.None,
        };

        return new SelectionGeometry
        {
            VisibleRects = rects,
            BoundingRect = bounding,
            StartRect = startRect,
            EndRect = endRect,
            Direction = direction,
            RectCount = rects.Count,
            Completeness = completeness,
        };
    }
}
