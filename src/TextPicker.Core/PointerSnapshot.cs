namespace TextPicker;

[Flags]
public enum PointerButtonState
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}

/// <summary>指针快照：位置 / 按键 / 按下抬起点 / 拖拽中。</summary>
public sealed record PointerSnapshot
{
    public PhysicalScreenPoint Position { get; init; }
    public PointerButtonState Buttons { get; init; }
    public PhysicalScreenPoint? DownPoint { get; init; }
    public PhysicalScreenPoint? UpPoint { get; init; }
    public bool Dragging { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
}
