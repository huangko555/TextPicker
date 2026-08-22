namespace TextPicker;

/// <summary>捕获失败原因，18 值封闭枚举。
/// 注意：无 Superseded——被新手势替代发独立的 <see cref="SelectionSuperseded"/> 事件，不发 SelectionFailed(Superseded)，杜绝双发。</summary>
public enum CaptureFailureReason
{
    // 手势层
    ExpiredMessage,
    IncompleteTimeout,
    Interrupted,

    // 策略层
    OwnProcess,
    ExcludedProcess,
    PasswordField,

    // 快照复核层
    ProcessMismatch,
    WindowChanged,

    // 后端层
    BackendUnavailable,
    BackendTimeout,
    CircuitOpen,
    AccessDenied,

    // 读取层
    EmptySelection,
    EmptyText,
    MultipleSelectionUnsupported,
    SelectionNotSettled,

    // 发布层
    StaleResult,
    Cancelled,
}

/// <summary>捕获完成后的失效原因，封闭枚举（Invalidated 持续跟踪直到下一次 Captured 或 Stop）。</summary>
public enum SelectionInvalidationReason
{
    Escape,
    OutsideClick,
    ForegroundChanged,
    TargetGone,
    NewSelection,
    OptionsChanged,
}
