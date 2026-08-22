namespace TextPicker;

/// <summary>焦点 / 命中目标的 UIA 上下文（仅阶段二填充）。ProcessName 非选区正文，允许出现在诊断语境之外的数据契约中。</summary>
public sealed record TargetContext
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public nint WindowHandle { get; init; }
    public string WindowClassName { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string FrameworkId { get; init; } = string.Empty;
    public bool IsEditable { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsPassword { get; init; }
    public bool HasTextPattern { get; init; }
    public bool HasValuePattern { get; init; }
    public bool HasTextPattern2 { get; init; }
}
