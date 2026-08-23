namespace TextPicker;

/// <summary>模块配置。全部带默认值；构造后可调整，经 <see cref="ISelectionPicker.ApplyOptions"/> 原子热生效。</summary>
public sealed record SelectionPickerOptions
{
    public bool BoxSelectEnabled { get; set; } = true;
    public bool MultiClickEnabled { get; set; } = true;
    public bool ShiftClickEnabled { get; set; } = true;
    public bool CtrlAEnabled { get; set; } = true;
    public bool ShiftKeyboardEnabled { get; set; } = true;   // 全部键盘手势关闭时不注册键盘 sink
    public bool ClickSelectionEnabled { get; set; } = true;  // 点击型选区变化（Word 行首选行等）

    /// <summary>点击布防窗口：普通单击后等待 UIA 选区变化事件的时长。</summary>
    public TimeSpan ClickSelectionWindow { get; set; } = TimeSpan.FromMilliseconds(400);
    public int DragThresholdPixels { get; set; } = 6;
    public int MultiClickTolerancePixels { get; set; } = 6;
    public TimeSpan StaleMessageAge { get; set; } = TimeSpan.FromSeconds(1);          // GetMessageTime 时钟
    public TimeSpan IncompleteTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan CircuitCooldown { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan SettleDeadline { get; set; } = TimeSpan.FromMilliseconds(400);   // 键盘新鲜度 settle，profile 可调
    public int MaxTextLength { get; set; } = 4000;
    public bool EnrichSurrounding { get; set; }
    public bool AllowWholeValueBackend { get; set; }   // 开启时仅限 Ctrl+A/Probe 且元素无 TextPattern，结果标 WholeValue
    public bool PauseWhenFullScreen { get; set; } = true;
    public IReadOnlyList<string> ExcludedProcesses { get; set; } = Array.Empty<string>();

    /// <summary>null = 模块线程回调；设置则封送。</summary>
    public SynchronizationContext? EventContext { get; set; }
}
