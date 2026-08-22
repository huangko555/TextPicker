namespace TextPicker;

public sealed record SelectionContent
{
    /// <summary>规范化后文本（TextNormalizer：\r\n→\n、去首尾空白、滤全不可见字符）。</summary>
    public string Text { get; init; } = string.Empty;
    public LocalTextContext? LocalContext { get; init; }       // 段落/局部上下文，可为 null
    public SelectionSurrounding? Surrounding { get; init; }    // 前后文（EnrichSurrounding 开启时）
    public int ReturnedLength { get; init; }                   // 实际返回长度
    public bool Truncated { get; init; }                       // GetText(Max+1) 检测
    public int? OriginalLength { get; init; }                  // UIA 无法给出真实总长，恒为 null（保留字段防未来后端）

    /// <summary>UiaWholeValue 路径 = true：整控件值而非选中文本，消费者须知。</summary>
    public bool WholeValue { get; init; }
}

public sealed record LocalTextContext
{
    public string Text { get; init; } = string.Empty;
    public ContextKind Kind { get; init; }

    /// <summary>ExpandToEnclosingUnit(Paragraph) 在 provider 不支持时会静默退化到更大单位，必须标注实际单位。</summary>
}

public enum ContextKind
{
    Paragraph = 0,
    Line = 1,
    Page = 2,
    Document = 3,
    BestEffort = 4,
}

public sealed record SelectionSurrounding
{
    public string? LeadingText { get; init; }
    public string? TrailingText { get; init; }
}
