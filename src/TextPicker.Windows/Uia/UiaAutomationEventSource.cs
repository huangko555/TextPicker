using System.Windows.Automation;
using TextPicker;

namespace TextPicker.Windows.Uia;

/// <summary>UIA 事件源 seam（Lane 1；测试可注入假件）。</summary>
internal interface IUaEventSource : IDisposable
{
    /// <summary>启动订阅（epoch 传递给信号回调）。handler 在 UIA 回调线程执行：零 COM 读取、只置信号。</summary>
    void Start(long epoch, Action<long, UaSignalKind> onSignal);

    /// <summary>等待一次 TextSelectionChanged 信号（键盘新鲜度）；消费即重置。</summary>
    bool WaitForSelectionSignal(TimeSpan timeout);

    void Stop();
}

internal enum UaSignalKind
{
    TextSelectionChanged = 0,
}

/// <summary>
/// 托管 UIA 事件源（改编 InputCue WindowsInputContextEventSource）：RootElement/Subtree 订阅 TextSelectionChanged；
/// handler 内零 COM 读取（只置 AutoResetEvent）；订阅/退订集中在专属 MTA 线程（ADR-0003 Lane 1）。
/// remove 后不销毁 handler 包装对象（迟到回调可能访问已释放对象，ADR-0002）。
/// </summary>
internal sealed class UiaAutomationEventSource : IUaEventSource
{
    private readonly AutoResetEvent _selectionSignal = new(initialState: false);
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;
    private AutomationEventHandler? _selectionHandler;
    private Action<long, UaSignalKind>? _onSignal;
    private long _epoch;

    public void Start(long epoch, Action<long, UaSignalKind> onSignal)
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _epoch = epoch;
            _onSignal = onSignal;
            _running = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TextPicker.UiaEvents",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public bool WaitForSelectionSignal(TimeSpan timeout) => _selectionSignal.WaitOne(timeout);

    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            thread = _thread;
            _running = false;
            _onSignal = null;
        }

        thread?.Join(TimeSpan.FromSeconds(2));    // Run 内部先退订再退出
    }

    public void Dispose()
    {
        Stop();
        _selectionSignal.Dispose();
    }

    private void Run()
    {
        try
        {
            var handler = new AutomationEventHandler(OnSelectionChanged);
            lock (_gate)
            {
                _selectionHandler = handler;
            }

            Automation.AddAutomationEventHandler(
                TextPattern.TextSelectionChangedEvent,
                AutomationElement.RootElement,
                TreeScope.Subtree,
                handler);

            // 保持线程存活以拥有订阅上下文；Stop 时唤醒退出。
            while (_running)
            {
                Thread.Sleep(50);
            }

            try
            {
                Automation.RemoveAutomationEventHandler(
                    TextPattern.TextSelectionChangedEvent,
                    AutomationElement.RootElement,
                    handler);
            }
            catch (ArgumentException)
            {
                // 订阅已失效（关机竞态）：安静退出
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 订阅失败（UIA 服务不可用等）：事件源静默不可用，读取路径不受影响
        }
        finally
        {
            lock (_gate)
            {
                // 不置空 _selectionHandler：迟到回调持有引用，延迟到进程生命周期（ADR-0002）
            }
        }
    }

    private void OnSelectionChanged(object sender, AutomationEventArgs e)
    {
        try
        {
            _selectionSignal.Set();
            Volatile.Read(ref _onSignal)?.Invoke(Volatile.Read(ref _epoch), UaSignalKind.TextSelectionChanged);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
