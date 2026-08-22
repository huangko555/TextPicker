using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase1;

/// <summary>门面手势管线扩展：打断 → Failed(Interrupted)；候选外预算 → Failed(IncompleteTimeout)；Options 校验。</summary>
public sealed class FacadeGesturePipelineTests
{
    private static ForegroundTargetSnapshot Fg => new(0x1234, 4321);

    [Fact]
    public async Task InterruptDuringInFlight_FailsWithInterrupted_LateResultDropped()
    {
        var feed = new CoreGestureFeed();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return FakeBackend.Ok();
        });
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        InjectBoxSelect(feed, t: 1000);
        Assert.Single(log.Candidates);

        feed.Inject(new InputRecord.PointerWheel(1050, Fg, new PhysicalScreenPoint(400, 400), ModifierSnapshot.None, -120));

        Assert.Contains(log.Failed, f => f.Generation is { Value: 1 } && f.Reason == CaptureFailureReason.Interrupted);

        gate.SetResult();    // 迟到结果：丢弃
        await Task.Delay(200);
        Assert.Empty(log.Captured);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task IncompleteTimeout_FailsCandidate_WhenReadExceedsBudget()
    {
        var feed = new CoreGestureFeed();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return FakeBackend.Ok();
        });
        using var picker = new SelectionPicker(feed, backend);
        picker.ApplyOptions(new SelectionPickerOptions { IncompleteTimeout = TimeSpan.FromMilliseconds(150) });
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        InjectBoxSelect(feed, t: 1000);
        Assert.Single(log.Candidates);

        Assert.True(await PickerEventLog.EventuallyAsync(
            () => log.Failed.Any(f => f.Generation is { Value: 1 } && f.Reason == CaptureFailureReason.IncompleteTimeout),
            timeoutMs: 5000));

        gate.SetResult();    // 迟到结果：已终结，丢弃
        await Task.Delay(200);
        Assert.Empty(log.Captured);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public void ApplyOptions_InvalidOptions_ThrowAndKeepCurrent()
    {
        using var picker = new SelectionPicker();
        var before = picker.Options;

        Assert.Throws<ArgumentOutOfRangeException>(() => picker.ApplyOptions(new SelectionPickerOptions { MaxTextLength = 0 }));

        Assert.Equal(before.MaxTextLength, picker.Options.MaxTextLength);
    }

    [Fact]
    public void StaleInputRecords_CountAsExpiredGestureDrops()
    {
        var feed = new CoreGestureFeed();
        using var picker = new SelectionPicker(feed, new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok())));
        picker.Start();

        feed.Inject(new InputRecord.PointerDown(5000, Fg, PointerButton.Left, new PhysicalScreenPoint(100, 100), ModifierSnapshot.None));
        feed.Inject(new InputRecord.PointerUp(1000, Fg, PointerButton.Left, new PhysicalScreenPoint(130, 100), ModifierSnapshot.None));    // 落后 4000ms

        var counters = picker.Counters;
        Assert.True(counters.GestureDropsByReason.TryGetValue(GestureDropReason.ExpiredMessage, out var count) && count >= 1);
    }

    internal static void InjectBoxSelect(CoreGestureFeed feed, long t)
    {
        feed.Inject(new InputRecord.PointerDown(t, Fg, PointerButton.Left, new PhysicalScreenPoint(100, 100), ModifierSnapshot.None));
        feed.Inject(new InputRecord.PointerUp(t + 100, Fg, PointerButton.Left, new PhysicalScreenPoint(100, 140), ModifierSnapshot.None));
    }
}
