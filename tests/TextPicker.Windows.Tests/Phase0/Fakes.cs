using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

internal sealed class FakeGestureFeed : ISelectionGestureFeed
{
    private long _epoch;

    public event EventHandler<GestureDetectedEventArgs>? GestureDetected;

    public void Start(long epoch) => Volatile.Write(ref _epoch, epoch);

    public void Stop()
    {
    }

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

    public static BackendReadResult Ok(string text = "captured-text", GeometryCompleteness completeness = GeometryCompleteness.RectsOnly) => new()
    {
        Success = true,
        Content = new SelectionContent { Text = text, ReturnedLength = text.Length },
        Geometry = new SelectionGeometry { Completeness = completeness },
        Target = new TargetContext { ProcessId = 1, ProcessName = "fake-target" },
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
