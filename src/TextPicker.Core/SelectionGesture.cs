namespace TextPicker;

/// <summary>触发手势分类（枚举数值为 v6.1 冻结契约，消费者按值序列化兼容）。
/// 命名为 Gesture 而非 Trigger，避免与 peeko 意图级 SelectionTrigger 冲突。</summary>
public enum SelectionGesture
{
    Explicit = 0,
    BoxSelect = 1,
    MultiClick = 2,
    ShiftClick = 3,
    CtrlA = 4,
    ShiftKeyboard = 5,

    /// <summary>点击型选区变化（v1.1 用户授权新增）：普通单击后目标应用自行改变选区（如 Word 行首选行）。
    /// 由 UIA TextSelectionChanged 事件 + 非折叠预检合成，非状态机直接识别。</summary>
    ClickSelection = 6,
}
