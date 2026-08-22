namespace TextPicker;

/// <summary>手势识别标定值（v6.1 冻结）。双击时间由宿主注入（GetDoubleClickTime）。</summary>
public sealed record GestureRecognitionOptions
{
    public int DragThresholdPixels { get; init; } = 6;

    public int MultiClickTolerancePixels { get; init; } = 6;

    /// <summary>系统双击时间（ms）。测试注入定值。</summary>
    public long DoubleClickTimeMs { get; init; } = 500;

    /// <summary>输入消息过期阈值（GetMessageTime 时钟域，约 1 秒）。</summary>
    public long StaleMessageAgeMs { get; init; } = 1000;
}

/// <summary>状态机输出的已分类手势。</summary>
public sealed record DetectedGesture
{
    public required SelectionGesture Gesture { get; init; }

    /// <summary>鼠标手势的按下点；键盘手势为 null。</summary>
    public PhysicalScreenPoint? DownPoint { get; init; }

    /// <summary>鼠标手势的抬起点；键盘手势为 null。</summary>
    public PhysicalScreenPoint? UpPoint { get; init; }

    public long MessageTimeMs { get; init; }

    /// <summary>按下时的前台目标快照（UIA 读取顺序以按下快照为基准、抬起复核）。</summary>
    public required ForegroundTargetSnapshot Foreground { get; init; }

    /// <summary>MultiClick 连击数（2、3…）；其余手势为 0。</summary>
    public int ClickCount { get; init; }
}

public enum InputInterruptKind
{
    Wheel = 0,
    RightButton = 1,
    MiddleButton = 2,
    XButton = 3,
    Escape = 4,
}

/// <summary>打断动作（滚轮/右键/中键/X 键/Esc）：取消当前候选并清空未完成手势状态。</summary>
public sealed record InputInterrupt(InputInterruptKind Kind, long MessageTimeMs, ForegroundTargetSnapshot Foreground);

/// <summary>
/// 手势状态机（ADR-0007，Core 纯逻辑）。消费归一化未分类输入记录，输出已分类手势/打断/丢弃信号。
/// 单消费者线程契约（由注入方保证）；全部时序走 MessageTimeMs 时钟域，无真实时间依赖。
/// 手势开关过滤不在本机内——统一由门面策略层过滤（单一过滤源）。
/// </summary>
public sealed class GestureStateMachine
{
    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_LETTER_A = 0x41;
    private const ushort VK_NAV_FIRST = 0x21;    // PageUp
    private const ushort VK_NAV_LAST = 0x28;     // Right

    private readonly GestureRecognitionOptions _options;

    private bool _seenAnyRecord;
    private long _newestMessageTime;

    private PendingLeftDown? _pending;
    private int _clickRunCount;
    private PhysicalScreenPoint _lastClickPoint;
    private long _lastClickUpTimeMs;

    public GestureStateMachine(GestureRecognitionOptions? options = null) => _options = options ?? new GestureRecognitionOptions();

    public event Action<DetectedGesture>? GestureDetected;

    public event Action<InputInterrupt>? InterruptDetected;

    /// <summary>手势层静默丢弃（不产生 generation；供计数诊断）。</summary>
    public event Action<GestureDropReason>? GestureDropped;

    public void Reset()
    {
        _pending = null;
        _clickRunCount = 0;
    }

    public void ProcessRecord(InputRecord record)
    {
        // 过期检查：相对最新已见消息超过 StaleMessageAge 的记录视为过期（v6.1 标定 ≈1s）。
        if (_seenAnyRecord && _newestMessageTime - record.MessageTimeMs > _options.StaleMessageAgeMs)
        {
            RaiseDropped(GestureDropReason.ExpiredMessage);
            return;
        }

        if (record.MessageTimeMs > _newestMessageTime || !_seenAnyRecord)
        {
            _newestMessageTime = record.MessageTimeMs;
        }

        _seenAnyRecord = true;

        switch (record)
        {
            case InputRecord.Key key:
                ProcessKey(key);
                break;
            case InputRecord.PointerDown down:
                ProcessPointerDown(down);
                break;
            case InputRecord.PointerUp up:
                ProcessPointerUp(up);
                break;
            case InputRecord.PointerWheel wheel:
                RaiseInterrupt(InputInterruptKind.Wheel, wheel.MessageTimeMs, wheel.Foreground);
                break;
        }
    }

    private void ProcessKey(InputRecord.Key key)
    {
        if (key.Action == InputKeyAction.Down)
        {
            if (key.VirtualKey == VK_ESCAPE)
            {
                RaiseInterrupt(InputInterruptKind.Escape, key.MessageTimeMs, key.Foreground);
            }

            return;
        }

        // 抬起触发（v6.1 定案：Ctrl+A 在 A 抬起且 Ctrl 按下时识别；Shift 键盘选择同取抬起沿）。
        if (key.VirtualKey == VK_LETTER_A && key.Modifiers.Ctrl)
        {
            RaiseGesture(new DetectedGesture
            {
                Gesture = SelectionGesture.CtrlA,
                MessageTimeMs = key.MessageTimeMs,
                Foreground = key.Foreground,
            });
            return;
        }

        if (key.VirtualKey is >= VK_NAV_FIRST and <= VK_NAV_LAST && key.Modifiers.Shift)
        {
            RaiseGesture(new DetectedGesture
            {
                Gesture = SelectionGesture.ShiftKeyboard,
                MessageTimeMs = key.MessageTimeMs,
                Foreground = key.Foreground,
            });
        }
    }

    private void ProcessPointerDown(InputRecord.PointerDown down)
    {
        switch (down.Button)
        {
            case PointerButton.Left:
                if (_pending != null)
                {
                    // 上一次按下未收到抬起即出现新按下：无效序列，覆盖旧状态。
                    RaiseDropped(GestureDropReason.InvalidSequence);
                }

                _pending = new PendingLeftDown(down.Point, down.MessageTimeMs, down.Modifiers.Shift, down.Foreground);
                break;
            case PointerButton.Right:
                RaiseInterrupt(InputInterruptKind.RightButton, down.MessageTimeMs, down.Foreground);
                break;
            case PointerButton.Middle:
                RaiseInterrupt(InputInterruptKind.MiddleButton, down.MessageTimeMs, down.Foreground);
                break;
            case PointerButton.XButton:
                RaiseInterrupt(InputInterruptKind.XButton, down.MessageTimeMs, down.Foreground);
                break;
        }
    }

    private void ProcessPointerUp(InputRecord.PointerUp up)
    {
        if (up.Button != PointerButton.Left)
        {
            return;    // 非左键抬起的打断语义已在按下沿处理
        }

        if (_pending is not { } pending)
        {
            RaiseDropped(GestureDropReason.InvalidSequence);
            return;
        }

        double dx = Math.Abs(up.Point.X - pending.Point.X);
        double dy = Math.Abs(up.Point.Y - pending.Point.Y);
        bool drag = Math.Max(dx, dy) >= _options.DragThresholdPixels;

        bool runContinues = _clickRunCount >= 1
            && up.MessageTimeMs - _lastClickUpTimeMs <= _options.DoubleClickTimeMs
            && Math.Max(Math.Abs(pending.Point.X - _lastClickPoint.X), Math.Abs(pending.Point.Y - _lastClickPoint.Y)) <= _options.MultiClickTolerancePixels;
        int count = runContinues ? _clickRunCount + 1 : 1;

        // 双击+拖拽 = 单词扩展选择：合成单个 MultiClick（绝不上抛 BoxSelect）。
        if (drag)
        {
            if (count >= 2)
            {
                RaiseGesture(BuildMouseGesture(SelectionGesture.MultiClick, pending, up, count));
            }
            else
            {
                RaiseGesture(BuildMouseGesture(SelectionGesture.BoxSelect, pending, up, 0));
            }
        }
        else if (count >= 2)
        {
            RaiseGesture(BuildMouseGesture(SelectionGesture.MultiClick, pending, up, count));
        }
        else if (pending.ShiftAtDown)
        {
            // Shift 状态在按下时读取（v6.1 定案）。
            RaiseGesture(BuildMouseGesture(SelectionGesture.ShiftClick, pending, up, 0));
        }

        _clickRunCount = count;
        _lastClickPoint = pending.Point;
        _lastClickUpTimeMs = up.MessageTimeMs;
        _pending = null;
    }

    private static DetectedGesture BuildMouseGesture(SelectionGesture gesture, PendingLeftDown pending, InputRecord.PointerUp up, int clickCount) => new()
    {
        Gesture = gesture,
        DownPoint = pending.Point,
        UpPoint = up.Point,
        MessageTimeMs = up.MessageTimeMs,
        Foreground = pending.Foreground,
        ClickCount = clickCount,
    };

    private void RaiseInterrupt(InputInterruptKind kind, long messageTimeMs, ForegroundTargetSnapshot foreground)
    {
        Reset();
        InterruptDetected?.Invoke(new InputInterrupt(kind, messageTimeMs, foreground));
    }

    private void RaiseGesture(DetectedGesture gesture) => GestureDetected?.Invoke(gesture);

    private void RaiseDropped(GestureDropReason reason) => GestureDropped?.Invoke(reason);

    private sealed record PendingLeftDown(PhysicalScreenPoint Point, long MessageTimeMs, bool ShiftAtDown, ForegroundTargetSnapshot Foreground);
}
