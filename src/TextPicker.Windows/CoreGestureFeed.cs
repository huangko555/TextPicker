using Windows.Win32;
using Windows.Win32.Foundation;
using TextPicker;

namespace TextPicker.Windows;

/// <summary>Core 状态机 → ISelectionGestureFeed 适配器（ADR-0007）。
/// Phase 2 的输入源（Owned/注入/Broker）把归一化输入记录灌进 <see cref="Inject"/>；
/// 手势开关过滤不在此层（门面策略层单一过滤源）。</summary>
internal sealed class CoreGestureFeed : ISelectionGestureFeed
{
    private readonly GestureStateMachine _machine;
    private long _epoch;

    public CoreGestureFeed(GestureRecognitionOptions? options = null)
    {
        _machine = new GestureStateMachine(options);
        _machine.GestureDetected += OnMachineGestureDetected;
        _machine.InterruptDetected += OnMachineInterruptDetected;
        _machine.GestureDropped += OnMachineGestureDropped;
    }

    public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

    public event EventHandler<InterruptDetectedEventArgs>? InterruptDetected;

    public event EventHandler<GestureDroppedEventArgs>? GestureDropped;

    public void Start(long epoch)
    {
        _epoch = epoch;
        _machine.Reset();
    }

    public void Stop() => _machine.Reset();

    /// <summary>注入一条归一化输入记录（调用线程即分类线程；事件在调用线程同步回调）。</summary>
    public void Inject(InputRecord record) => _machine.ProcessRecord(record);

    private void OnMachineGestureDetected(DetectedGesture gesture)
    {
        PhysicalScreenRect windowRect = default;
        if (gesture.Foreground.WindowHandle != 0)
        {
            // 阶段一允许的 Win32 浅信息：窗口矩形（非 UIA）。
            if (PInvoke.GetWindowRect(new HWND(gesture.Foreground.WindowHandle), out var rect))
            {
                windowRect = new PhysicalScreenRect(rect.left, rect.top, rect.right, rect.bottom);
            }
        }

        GestureDetected?.Invoke(this, new GestureDetectedEventArgs
        {
            Epoch = Volatile.Read(ref _epoch),
            Gesture = gesture.Gesture,
            TargetProcessId = gesture.Foreground.ProcessId,
            TargetWindowHandle = gesture.Foreground.WindowHandle,
            WindowRect = windowRect,
            DownPoint = gesture.DownPoint,
            UpPoint = gesture.UpPoint,
        });
    }

    private void OnMachineInterruptDetected(InputInterrupt interrupt)
        => InterruptDetected?.Invoke(this, new InterruptDetectedEventArgs
        {
            Epoch = Volatile.Read(ref _epoch),
            Kind = interrupt.Kind,
            MessageTimeMs = interrupt.MessageTimeMs,
            Foreground = interrupt.Foreground,
        });

    private void OnMachineGestureDropped(GestureDropReason reason)
        => GestureDropped?.Invoke(this, new GestureDroppedEventArgs { Reason = reason });
}
