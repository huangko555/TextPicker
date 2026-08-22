namespace TextPicker;

/// <summary>用户选区生命周期（手势）的强类型标识。手势候选被接受发布时原子分配，严格单调、无洞（被过滤的手势不产生 generation）。</summary>
public readonly record struct SelectionGeneration(long Value)
{
    public override string ToString() => $"gen:{Value}";
}

/// <summary>显式请求的强类型标识。</summary>
public readonly record struct SelectionRequestId(long Value)
{
    public override string ToString() => $"req:{Value}";
}

/// <summary>捕获来源：手势触发或显式查询。</summary>
public enum CaptureOrigin
{
    Gesture = 0,
    Explicit = 1,
}
