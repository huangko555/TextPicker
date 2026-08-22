namespace TextPicker;

// CA1711/CA1716：ISelectionPointerStream 与 Stop() 命名为冻结契约（plan v6.1 §5/§11），消费者文档已固定，不改名。
#pragma warning disable CA1711, CA1716

/// <summary>「选区观察」共享深模块门面。深模块自持输入线程、隐藏窗口、COM 线程、串行队列、generation 生命周期、UIA 事件订阅与捕获后失效跟踪。
/// <para>模块状态表（LastCapture 与候选可见性）：</para>
/// <list type="table">
/// <item><term>Idle</term><description>LastCapture=null，无候选无捕获</description></item>
/// <item><term>CandidateReady 已发、读取中</term><description>LastCapture=null，阶段二进行</description></item>
/// <item><term>Captured</term><description>LastCapture=capture，终止事件已发</description></item>
/// <item><term>Invalidated / Superseded</term><description>按原因保留或清空</description></item>
/// <item><term>Stop 后</term><description>冻结最后值，IsRunning=false</description></item>
/// </list>
/// </summary>
public interface ISelectionPicker : IDisposable
{
    // A. 生命周期
    /// <summary>无参：模块自建输入窗口与线程（所有权规则见 ADR-0001）。</summary>
    void Start();

    /// <summary>可逆；Dispose 终结。</summary>
    void Stop();

    bool IsRunning { get; }

    // B. 手势选区（两阶段）
    /// <summary>阶段一：零 UIA、无正文。</summary>
    event EventHandler<SelectionCandidateReadyEventArgs> SelectionCandidateReady;

    /// <summary>阶段二：读后发布。</summary>
    event EventHandler<SelectionCapturedEventArgs> SelectionCaptured;

    /// <summary>18 值封闭枚举（无 Superseded——那是独立终止事件）。</summary>
    event EventHandler<SelectionFailedEventArgs> SelectionFailed;

    /// <summary>终止事件之一：被新手势替代。</summary>
    event EventHandler<SelectionSupersededEventArgs> SelectionSuperseded;

    /// <summary>Captured 之后持续跟踪，直到下一次 Captured 或 Stop；新手势对已完成捕获 = Invalidated(oldId, NewSelection) 再 Captured(new)。</summary>
    event EventHandler<SelectionInvalidatedEventArgs> SelectionInvalidated;

    /// <summary>仅手势生命周期更新（见接口注状态表）。</summary>
    SelectionCapture? LastCapture { get; }

    /// <summary>query 式显式捕获：只通过 Task 返回结果；不发布 SelectionCaptured、不更新 LastCapture、不改变手势状态；不 supersede 手势。</summary>
    Task<SelectionCaptureResult> CaptureCurrentSelectionAsync(PhysicalScreenPoint? fallbackAnchor, CancellationToken ct);

    Task<TargetProbeResult> ProbeTargetAsync(PhysicalScreenPoint point, bool includeText, CancellationToken ct);

    /// <summary>放弃等待而非中止 COM（COM 同步调用不可安全中断）。</summary>
    bool CancelSelection(SelectionGeneration generation);

    /// <summary>放弃等待而非中止 COM。</summary>
    bool CancelRequest(SelectionRequestId requestId);

    /// <summary>内容流显式订阅（订阅期间 TextSelectionChanged 驱动主动读取推送；40ms 合并 + 150ms 最小观测间隔双闸）。
    /// 持续本地读文本是隐私升档，必须显式订阅获得。</summary>
    IDisposable SubscribeSelectionContent(Action<SelectionContentChangedEventArgs> handler);

    // C. 指针/光标
    PointerSnapshot GetPointerSnapshot();

    /// <summary>诊断相位（CandidateStarted 先于正文读取）。</summary>
    event EventHandler<GesturePhaseEventArgs> GesturePhaseChanged;

    // D. 插入光标（InputCue 向，全部无正文）
    Task<CaretObservation?> ObserveCaretAsync(CancellationToken ct);
    event EventHandler<CaretEventArgs> CaretChanged;

    /// <summary>无文本选区状态：非折叠标记 + 几何 + Target(IsEditable/IsReadOnly/IsPassword)，永不含正文。InputCue 唯一的选区合规入口。</summary>
    Task<SelectionState?> ObserveSelectionStateAsync(CancellationToken ct);

    // E. 焦点目标
    TargetContext? CurrentFocusTarget { get; }
    event EventHandler<FocusTargetEventArgs> FocusTargetChanged;

    // F. 配置
    SelectionPickerOptions Options { get; }

    /// <summary>原子热生效。OptionsChanged 可能触发已捕获结果的 Invalidated。</summary>
    void ApplyOptions(SelectionPickerOptions options);

    void SetTriggerEnabled(SelectionGesture gesture, bool enabled);

    void SetExcludedProcesses(IReadOnlyList<string> processNames);

    /// <summary>消费者 UI 窗口豁免：其上手势不产生候选、不取代、不失效（点工具条/拖工具条不自我打断）；
    /// 令牌 Dispose 注销 + 内部持续校验 HWND 仍属原 PID（防复用）。</summary>
    IDisposable RegisterConsumerWindow(nint window);

    // G. 诊断（结构上不可能携带正文）
    /// <summary>阶段流水事件，见 <see cref="SelectionPipelineStage"/>。</summary>
    event EventHandler<SelectionDiagnosticsEventArgs> Diagnostics;

    SelectionPickerCounters Counters { get; }
}

/// <summary>可选子接口；获取方式 = picker is ISelectionPointerStream 强转（不向 ISelectionPicker 塞属性）。</summary>
public interface ISelectionPointerStream
{
    IDisposable SubscribePointerMoved(TimeSpan minInterval, EventHandler<PointerMovedEventArgs> handler);
}
#pragma warning restore CA1711, CA1716
