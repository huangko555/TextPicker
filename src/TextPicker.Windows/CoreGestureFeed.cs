using Windows.Win32;
using Windows.Win32.Foundation;
using TextPicker;

namespace TextPicker.Windows;

/// <summary>Core 状态机 → ISelectionGestureFeed 适配器（ADR-0007）。
/// 可选组装 IInputRecordSource（Owned/注入/Broker，ADR-0001）：记录 → 状态机 → 手势/打断/单击/丢弃事件。
/// 手势开关过滤不在此层（门面策略层单一过滤源）。</summary>
internal sealed class CoreGestureFeed : ISelectionGestureFeed, IDisposable
{
    private readonly GestureStateMachine _machine;
    private readonly IInputRecordSource? _recordSource;
    private long _epoch;

    public CoreGestureFeed(IInputRecordSource? recordSource = null, GestureRecognitionOptions? options = null)
    {
        _recordSource = recordSource;
        _machine = new GestureStateMachine(options);
        _machine.GestureDetected += OnMachineGestureDetected;
        _machine.InterruptDetected += OnMachineInterruptDetected;
        _machine.GestureDropped += OnMachineGestureDropped;
        _machine.PlainClickObserved += OnMachinePlainClick;
        if (_recordSource != null)
        {
            _recordSource.RecordReceived += Inject;
        }
    }

    public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

    public event EventHandler<InterruptDetectedEventArgs>? InterruptDetected;

    public event EventHandler<GestureDroppedEventArgs>? GestureDropped;

    public event EventHandler<PlainClickEventArgs>? PlainClickObserved;

    public void Start(long epoch)
    {
        _epoch = epoch;
        _machine.Reset();
        _recordSource?.Start();
    }

    public void Stop()
    {
        _recordSource?.Stop();
        _machine.Reset();
    }

    public void Dispose()
    {
        if (_recordSource != null)
        {
            _recordSource.RecordReceived -= Inject;
            _recordSource.Dispose();
        }
    }

    /// <summary>注入一条归一化输入记录（调用线程即分类线程；事件在调用线程同步回调）。</summary>
    public void Inject(InputRecord record) => _machine.ProcessRecord(record);

    private void OnMachinePlainClick(PlainClickObservation click)
        => PlainClickObserved?.Invoke(this, new PlainClickEventArgs { Epoch = Volatile.Read(ref _epoch), Click = click });

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
