using TextPicker;
using TextPicker.Windows.Uia;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

internal sealed class FakeGestureFeed : ISelectionGestureFeed
{
    private long _epoch;

    public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

    public event EventHandler<InterruptDetectedEventArgs>? InterruptDetected;

#pragma warning disable CS0067 // 由真实输入源路径触发；假件仅按需使用
    public event EventHandler<PlainClickEventArgs>? PlainClickObserved;

    public event EventHandler<GestureDroppedEventArgs>? GestureDropped;
#pragma warning restore CS0067

    public void Start(long epoch) => Volatile.Write(ref _epoch, epoch);

    public void Stop()
    {
    }

    public void RaiseInterrupt(InputInterruptKind kind, long epochOverride = default)
        => InterruptDetected?.Invoke(this, new InterruptDetectedEventArgs
        {
            Epoch = epochOverride != default ? epochOverride : Volatile.Read(ref _epoch),
            Kind = kind,
        });

    public void RaisePlainClick(nint foregroundWindowHandle = 0x2222)
        => PlainClickObserved?.Invoke(this, new PlainClickEventArgs
        {
            Epoch = Volatile.Read(ref _epoch),
            Click = new PlainClickObservation(new PhysicalScreenPoint(1, 1), new PhysicalScreenPoint(2, 2), 1000, new ForegroundTargetSnapshot(foregroundWindowHandle, 9999)),
        });

    public void Raise(
        SelectionGesture gesture,
        int targetProcessId = 4321,
        nint targetWindowHandle = 0x1234,
        PhysicalScreenPoint? down = null,
        PhysicalScreenPoint? up = null,
        long? epochOverride = null)
    {
        GestureDetected?.Invoke(this, new GestureDetectedEventArgs
        {
            Epoch = epochOverride ?? Volatile.Read(ref _epoch),
            Gesture = gesture,
            TargetProcessId = targetProcessId,
            TargetWindowHandle = targetWindowHandle,
            DownPoint = down,
            UpPoint = up,
        });
    }
}

internal sealed class FakeBackend : ISelectionBackend
{
    private readonly Func<BackendReadRequest, CancellationToken, Task<BackendReadResult>> _handler;

    public FakeBackend(Func<BackendReadRequest, CancellationToken, Task<BackendReadResult>> handler) => _handler = handler;

    public List<BackendReadRequest> Requests { get; } = new();

    public Task<BackendReadResult> ReadAsync(BackendReadRequest request, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return _handler(request, ct);
    }

    public static BackendReadResult Ok(string text = "captured-text", GeometryCompleteness completeness = GeometryCompleteness.RectsOnly, int targetProcessId = 1, nint targetWindowHandle = 0) => new()
    {
        Success = true,
        Content = new SelectionContent { Text = text, ReturnedLength = text.Length },
        Geometry = new SelectionGeometry { Completeness = completeness },
        Target = new TargetContext { ProcessId = targetProcessId, ProcessName = "fake-target", WindowHandle = targetWindowHandle },
        AnchorRect = new PhysicalScreenRect(10, 10, 20, 40),
        AnchorSource = AnchorSource.MouseReleasePoint,
        Backend = CaptureBackend.UiaTextPattern,
    };

    public static BackendReadResult Fail(CaptureFailureReason reason) => new() { Success = false, Failure = reason };
}

/// <summary>全事件记录器 + 5 号不变式检查器。</summary>
internal sealed class PickerEventLog : IDisposable
{
    private readonly object _gate = new();

    public List<SelectionCandidateReady> Candidates { get; } = new();
    public List<SelectionCapture> Captured { get; } = new();
    public List<(SelectionGeneration? Generation, CaptureFailureReason Reason)> Failed { get; } = new();
    public List<SelectionGeneration> Superseded { get; } = new();
    public List<(SelectionGeneration Generation, SelectionInvalidationReason Reason)> Invalidated { get; } = new();
    public List<SelectionDiagnosticsEventArgs> Diagnostics { get; } = new();

    public void Attach(SelectionPicker picker)
    {
        picker.SelectionCandidateReady += (_, e) => Add(Candidates, e.Candidate);
        picker.SelectionCaptured += (_, e) => Add(Captured, e.Capture);
        picker.SelectionFailed += (_, e) => Add(Failed, (e.Generation, e.Reason));
        picker.SelectionSuperseded += (_, e) => Add(Superseded, e.Generation);
        picker.SelectionInvalidated += (_, e) => Add(Invalidated, (e.Generation, e.Reason));
        picker.Diagnostics += (_, e) => Add(Diagnostics, e);
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_gate)
        {
            list.Add(item);
        }
    }

    public Snapshot SnapshotOf()
    {
        lock (_gate)
        {
            return new Snapshot(
                new List<SelectionCandidateReady>(Candidates),
                new List<SelectionCapture>(Captured),
                new List<(SelectionGeneration?, CaptureFailureReason)>(Failed),
                new List<SelectionGeneration>(Superseded),
                new List<(SelectionGeneration, SelectionInvalidationReason)>(Invalidated));
        }
    }

    public static async Task<bool> EventuallyAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>契约测试 #5：∀ generation —— CandidateReady==1；terminal（Captured|Failed|Superseded）互斥且唯一；Captured 之后 Invalidated ≤ 1。</summary>
    public void AssertLifecycleInvariants()
    {
        var snap = SnapshotOf();

        // 无洞：generation 值必须是 1..N 连续序列（被过滤手势不产生 generation）。
        var generations = snap.Candidates.Select(c => c.Generation.Value).OrderBy(v => v).ToArray();
        Assert.True(generations.SequenceEqual(Enumerable.Range(1, generations.Length).Select(v => (long)v)),
            $"generation sequence has holes: [{string.Join(",", generations)}]");

        foreach (var generation in generations)
        {
            var candidateCount = snap.Candidates.Count(c => c.Generation.Value == generation);
            Assert.True(candidateCount == 1, $"generation {generation}: CandidateReady x{candidateCount}");

            var captured = snap.Captured.Count(c => c.Generation!.Value.Value == generation);
            var failed = snap.Failed.Count(f => f.Generation is { } g && g.Value == generation);
            var superseded = snap.Superseded.Count(g => g.Value == generation);
            var terminals = captured + failed + superseded;
            Assert.True(terminals == 1, $"generation {generation}: terminal x{terminals} (captured={captured}, failed={failed}, superseded={superseded})");

            if (captured == 1)
            {
                var invalidated = snap.Invalidated.Count(i => i.Generation.Value == generation);
                Assert.True(invalidated <= 1, $"generation {generation}: Invalidated x{invalidated} after Captured");
            }
        }
    }

    public void Dispose()
    {
    }

    public readonly record struct Snapshot(
        List<SelectionCandidateReady> Candidates,
        List<SelectionCapture> Captured,
        List<(SelectionGeneration? Generation, CaptureFailureReason Reason)> Failed,
        List<SelectionGeneration> Superseded,
        List<(SelectionGeneration Generation, SelectionInvalidationReason Reason)> Invalidated);
}

/// <summary>假焦点源（Phase 2 失效跟踪测试）。</summary>
internal sealed class FakeFocusSource : IFocusTargetSource
{
    public event Action<ForegroundTargetSnapshot>? ForegroundChanged;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Raise(ForegroundTargetSnapshot snapshot) => ForegroundChanged?.Invoke(snapshot);

    public void Dispose()
    {
    }
}

/// <summary>假 UIA 事件源（Phase 3 测试）。</summary>
internal sealed class FakeUaEventSource : global::TextPicker.Windows.Uia.IUaEventSource
{
    private long _epoch;
    private Action<long, global::TextPicker.Windows.Uia.UaSignalKind>? _onSignal;

    public void Start(long epoch, Action<long, global::TextPicker.Windows.Uia.UaSignalKind> onSignal)
    {
        _epoch = epoch;
        _onSignal = onSignal;
    }

    public bool WaitForSelectionSignal(TimeSpan timeout) => false;

    /// <summary>注入一次 TextSelectionChanged 信号（ClickSelection 布防测试）。</summary>
    public void RaiseSelectionChanged() => _onSignal?.Invoke(_epoch, global::TextPicker.Windows.Uia.UaSignalKind.TextSelectionChanged);

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>假观察者 lane（ClickSelection 预检测试）：可配置预检结果。</summary>
internal sealed class FakeObserverLane : SelectionPicker.IObserverLane
{
    /// <summary>强制布尔结果（ClickSelection 预检桩）；null = 执行真实工作。</summary>
    public bool? ForcedBoolResult { get; set; }

    public Task<T> RunAsync<T>(Func<T> work, string? targetKey, CancellationToken ct)
        => ForcedBoolResult is { } forced && typeof(T) == typeof(bool)
            ? Task.FromResult((T)(object)forced)
            : Task.Run(work, ct);

    public void Dispose()
    {
    }
}
