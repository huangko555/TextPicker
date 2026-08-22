namespace TextPicker.Core.Tests;

/// <summary>手势状态机全路径（ADR-0007）：五手势、连击合成、打断、过期、无效序列。全部走消息时间钟域。</summary>
public sealed class GestureStateMachineTests
{
    private const ushort VK_PRIOR = 0x21;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_HOME = 0x24;

    private readonly List<DetectedGesture> _gestures = new();
    private readonly List<InputInterrupt> _interrupts = new();
    private readonly List<GestureDropReason> _drops = new();
    private readonly GestureStateMachine _machine = new();

    public GestureStateMachineTests()
    {
        _machine.GestureDetected += g => _gestures.Add(g);
        _machine.InterruptDetected += i => _interrupts.Add(i);
        _machine.GestureDropped += d => _drops.Add(d);
    }

    private static ForegroundTargetSnapshot Fg => new(0x1234, 4321);

    private static InputRecord.PointerDown Down(long t, double x, double y, bool shift = false)
        => new InputRecord.PointerDown(t, Fg, PointerButton.Left, new PhysicalScreenPoint(x, y), new ModifierSnapshot(false, shift, false, false));

    private static InputRecord.PointerUp Up(long t, double x, double y, bool shift = false)
        => new InputRecord.PointerUp(t, Fg, PointerButton.Left, new PhysicalScreenPoint(x, y), new ModifierSnapshot(false, shift, false, false));

    private static InputRecord.Key Key(long t, ushort vk, InputKeyAction action, bool ctrl = false, bool shift = false)
        => new InputRecord.Key(t, Fg, action, vk, new ModifierSnapshot(ctrl, shift, false, false));

    private static InputRecord.PointerWheel Wheel(long t)
        => new InputRecord.PointerWheel(t, Fg, new PhysicalScreenPoint(400, 400), ModifierSnapshot.None, -120);

    [Fact]
    public void BoxSelect_EmitsWhenDisplacementReachesThreshold()
    {
        _machine.ProcessRecord(Down(1000, 100, 100));
        _machine.ProcessRecord(Up(1100, 100, 106));    // Y 位移 6px 达阈

        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.BoxSelect, gesture.Gesture);
        Assert.Equal(new PhysicalScreenPoint(100, 100), gesture.DownPoint);
        Assert.Equal(new PhysicalScreenPoint(100, 106), gesture.UpPoint);
        Assert.Equal(0, gesture.ClickCount);
    }

    [Fact]
    public void ClickBelowThreshold_EmitsNothing()
    {
        _machine.ProcessRecord(Down(1000, 100, 100));
        _machine.ProcessRecord(Up(1100, 104, 105));    // 位移 < 6px：单击

        Assert.Empty(_gestures);
    }

    [Fact]
    public void DoubleClick_EmitsMultiClick()
    {
        Click(0, 100, 100);
        Click(100, 102, 101);

        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.MultiClick, gesture.Gesture);
        Assert.Equal(2, gesture.ClickCount);
    }

    [Fact]
    public void TripleClick_EmitsSecondMultiClick_SupersedeSemanticsAtFacade()
    {
        Click(0, 100, 100);
        Click(100, 100, 100);
        Click(200, 100, 100);

        Assert.Equal(2, _gestures.Count);
        Assert.All(_gestures, g => Assert.Equal(SelectionGesture.MultiClick, g.Gesture));
        Assert.Equal(2, _gestures[0].ClickCount);
        Assert.Equal(3, _gestures[1].ClickCount);
    }

    [Fact]
    public void DoubleClickPlusDrag_SynthesizesSingleMultiClick_NotBoxSelect()
    {
        Click(0, 100, 100);

        // 第二击按下后拖拽 30px 再抬起：单词扩展选择 → 合成单个 MultiClick。
        _machine.ProcessRecord(Down(100, 100, 100));
        _machine.ProcessRecord(Up(250, 130, 100));

        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.MultiClick, gesture.Gesture);
        Assert.Equal(2, gesture.ClickCount);
        Assert.Equal(new PhysicalScreenPoint(130, 100), gesture.UpPoint);
    }

    [Fact]
    public void ClicksBeyondDoubleClickTime_DoNotChain()
    {
        Click(0, 100, 100);
        Click(600, 100, 100);    // 超出 500ms 双击时间

        Assert.Empty(_gestures);
    }

    [Fact]
    public void ClicksBeyondTolerance_DoNotChain()
    {
        Click(0, 100, 100);
        Click(50, 200, 200);    // 超出 6px 容差

        Assert.Empty(_gestures);
    }

    [Fact]
    public void ShiftClick_UsesShiftStateAtPointerDown()
    {
        _machine.ProcessRecord(Down(1000, 100, 100, shift: true));
        _machine.ProcessRecord(Up(1100, 102, 101, shift: false));    // 抬起时 Shift 已松开：仍算

        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.ShiftClick, gesture.Gesture);

        _gestures.Clear();

        // 按下时无 Shift、抬起时才有：不算。
        _machine.ProcessRecord(Down(2000, 300, 300, shift: false));
        _machine.ProcessRecord(Up(2100, 301, 301, shift: true));
        Assert.Empty(_gestures);
    }

    [Fact]
    public void ShiftDrag_EmitsBoxSelect_DragWinsOverShiftClick()
    {
        _machine.ProcessRecord(Down(1000, 100, 100, shift: true));
        _machine.ProcessRecord(Up(1100, 120, 100));

        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.BoxSelect, gesture.Gesture);
    }

    [Fact]
    public void CtrlA_TriggersOnKeyUpWithCtrlDown()
    {
        _machine.ProcessRecord(Key(1000, 0x41, InputKeyAction.Down, ctrl: true));
        Assert.Empty(_gestures);

        _machine.ProcessRecord(Key(1010, 0x41, InputKeyAction.Up, ctrl: true));
        var gesture = Assert.Single(_gestures);
        Assert.Equal(SelectionGesture.CtrlA, gesture.Gesture);
        Assert.Null(gesture.DownPoint);
        Assert.Null(gesture.UpPoint);

        _gestures.Clear();
        _machine.ProcessRecord(Key(2000, 0x41, InputKeyAction.Up, ctrl: false));    // 无 Ctrl：不触发
        Assert.Empty(_gestures);
    }

    [Fact]
    public void ShiftKeyboard_CoversNavigationKeysWithCtrlVariants()
    {
        _machine.ProcessRecord(Key(1000, VK_LEFT, InputKeyAction.Up, shift: true));
        _machine.ProcessRecord(Key(1100, VK_HOME, InputKeyAction.Up, ctrl: true, shift: true));    // Ctrl+Shift+Home
        _machine.ProcessRecord(Key(1200, VK_PRIOR, InputKeyAction.Up, shift: true));               // Shift+PageUp

        Assert.Equal(3, _gestures.Count);
        Assert.All(_gestures, g => Assert.Equal(SelectionGesture.ShiftKeyboard, g.Gesture));

        _gestures.Clear();
        _machine.ProcessRecord(Key(2000, VK_LEFT, InputKeyAction.Up, shift: false));    // 无 Shift：导航键不触发
        Assert.Empty(_gestures);
    }

    [Fact]
    public void Interrupt_WheelResetsPendingDragAndClickRun()
    {
        // 在飞拖拽被打断：down → wheel → up 无手势。
        _machine.ProcessRecord(Down(1000, 100, 100));
        _machine.ProcessRecord(Wheel(1010));
        _machine.ProcessRecord(Up(1020, 130, 100));
        Assert.Empty(_gestures);
        Assert.Contains(_interrupts, i => i.Kind == InputInterruptKind.Wheel);

        // 连击序列被打断：click → wheel → click 不构成双击。
        _gestures.Clear();
        Click(2000, 100, 100);
        _machine.ProcessRecord(Wheel(2050));
        Click(2060, 100, 100);
        Assert.Empty(_gestures);
    }

    [Theory]
    [InlineData(PointerButton.Right, InputInterruptKind.RightButton)]
    [InlineData(PointerButton.Middle, InputInterruptKind.MiddleButton)]
    [InlineData(PointerButton.XButton, InputInterruptKind.XButton)]
    public void NonLeftButtons_Interrupt(PointerButton button, InputInterruptKind expectedKind)
    {
        _machine.ProcessRecord(new InputRecord.PointerDown(1000, Fg, button, new PhysicalScreenPoint(1, 1), ModifierSnapshot.None));

        var interrupt = Assert.Single(_interrupts);
        Assert.Equal(expectedKind, interrupt.Kind);
    }

    [Fact]
    public void EscapeKeyDown_Interrupts()
    {
        _machine.ProcessRecord(Key(1000, 0x1B, InputKeyAction.Down));

        var interrupt = Assert.Single(_interrupts);
        Assert.Equal(InputInterruptKind.Escape, interrupt.Kind);
    }

    [Fact]
    public void StaleRecord_IsDroppedWithExpiredReason()
    {
        _machine.ProcessRecord(Down(5000, 100, 100));

        _machine.ProcessRecord(Up(1000, 130, 100));    // 落后最新 4000ms：过期
        Assert.Contains(_drops, d => d == GestureDropReason.ExpiredMessage);
        Assert.Empty(_gestures);

        _machine.ProcessRecord(Up(4500, 130, 100));    // 落后 500ms：仍在窗口内，正常处理
        Assert.Single(_gestures);
    }

    [Fact]
    public void UpWithoutDown_IsInvalidSequence()
    {
        _machine.ProcessRecord(Up(1000, 100, 100));
        Assert.Contains(_drops, d => d == GestureDropReason.InvalidSequence);
        Assert.Empty(_gestures);
    }

    [Fact]
    public void Reset_ClearsPendingState()
    {
        Click(1000, 100, 100);
        _machine.Reset();
        Click(1100, 100, 100);    // Reset 后重新起算：单击非双击

        Assert.Empty(_gestures);
    }

    private void Click(long t, double x, double y)
    {
        _machine.ProcessRecord(Down(t, x, y));
        _machine.ProcessRecord(Up(t + 10, x + 1, y + 1));
    }
}
