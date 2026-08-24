using TextPicker.Windows.Execution;

namespace TextPicker.Windows;

/// <summary>
/// Lane 路由后端（ADR-0003/0005）：手势/显式捕获走 Capture lane 串行执行器；观察/内容流走 Observer lane。
/// 读取函数由 Phase 3 的 UiaSelectionBackend 提供；本层只负责 lane 路由、超时/熔断/隔离映射（ADR-0006）。
/// COM 对象不过 lane：读取函数在 lane 的 MTA 线程内执行并只返回纯数据（BackendReadResult）。
/// </summary>
internal sealed class LaneRoutedBackend : ISelectionBackend, IDisposable
{
    private readonly QueryRunner _captureRunner;
    private readonly QueryRunner _observerRunner;
    private readonly SemaphoreSlim _captureGate = new(1, 1);    // Capture lane 串行（手势 > 显式的优先级调度由 Arbiter 模型承接，v1 FIFO）
    private readonly Func<BackendReadRequest, CancellationToken, BackendReadResult> _reader;

    public LaneRoutedBackend(
        Func<BackendReadRequest, CancellationToken, BackendReadResult> reader,
        TimeSpan? queryTimeout = null,
        TimeSpan? circuitCooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        var timeout = queryTimeout ?? TimeSpan.FromMilliseconds(1000);
        var cooldown = circuitCooldown ?? TimeSpan.FromSeconds(2);
        _captureRunner = new QueryRunner(timeout, cooldown, maxOrphanWorkers: 1, timeProvider);
        _observerRunner = new QueryRunner(timeout, cooldown, maxOrphanWorkers: 1, timeProvider);
    }

    internal int CaptureWorkerCreations => _captureRunner.WorkerCreationCount;

    internal void ApplyTimeouts(TimeSpan queryTimeout, TimeSpan circuitCooldown)
    {
        _captureRunner.ApplyTimeouts(queryTimeout, circuitCooldown);
        _observerRunner.ApplyTimeouts(queryTimeout, circuitCooldown);
    }

    public async Task<BackendReadResult> ReadAsync(BackendReadRequest request, CancellationToken ct)
    {
        var isCapture = request.Origin is CaptureOrigin.Gesture or CaptureOrigin.Explicit;
        if (isCapture)
        {
            await _captureGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        try
        {
            var runner = isCapture ? _captureRunner : _observerRunner;
            var targetKey = $"pid:{request.Target.ProcessId};hwnd:{request.Target.WindowHandle}";
            var outcome = await Task.Run(() => runner.Run(() => _reader(request, ct), targetKey, ct), CancellationToken.None).ConfigureAwait(false);

            return outcome.Outcome switch
            {
                QueryOutcome.Completed => (BackendReadResult)outcome.Value!,
                QueryOutcome.TimedOut => new BackendReadResult { Success = false, Failure = CaptureFailureReason.BackendTimeout },
                QueryOutcome.CircuitOpen => new BackendReadResult { Success = false, Failure = CaptureFailureReason.CircuitOpen },
                QueryOutcome.QuarantinedTarget => new BackendReadResult { Success = false, Failure = CaptureFailureReason.CircuitOpen },
                QueryOutcome.WorkerBusy => new BackendReadResult { Success = false, Failure = CaptureFailureReason.CircuitOpen },
                QueryOutcome.SourceFailed when outcome.Error is UnauthorizedAccessException => new BackendReadResult { Success = false, Failure = CaptureFailureReason.AccessDenied },
                _ => new BackendReadResult { Success = false, Failure = CaptureFailureReason.BackendUnavailable },
            };
        }
        finally
        {
            if (isCapture)
            {
                _captureGate.Release();
            }
        }
    }

    public void Dispose()
    {
        _captureRunner.Dispose();
        _observerRunner.Dispose();
    }
}
