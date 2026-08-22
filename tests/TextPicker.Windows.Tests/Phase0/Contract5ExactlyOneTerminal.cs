using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #5：exactly-one terminal——
/// ∀ generation：CandidateReady==1、terminal 互斥唯一、Captured 后 Invalidated≤1、被过滤手势不产生 generation（无洞）。
/// Phase 0 为确定性场景组；Phase 1 在 Core.Tests 补 property-based 全路径。</summary>
public sealed class Contract5ExactlyOneTerminal
{
    [Fact]
    public async Task SequentialCaptures_InvalidatePrevious_AndKeepGenerationGapless()
    {
        var feed = new FakeGestureFeed();
        var backend = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok()));
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        feed.Raise(SelectionGesture.BoxSelect);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));

        feed.Raise(SelectionGesture.CtrlA);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 2));

        feed.Raise(SelectionGesture.MultiClick);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 3));

        // 已完成捕获被新选择失效：gen1/gen2 各恰一次 NewSelection 失效；gen3 仍存活。
        Assert.Contains(log.Invalidated, i => i.Generation.Value == 1 && i.Reason == SelectionInvalidationReason.NewSelection);
        Assert.Contains(log.Invalidated, i => i.Generation.Value == 2 && i.Reason == SelectionInvalidationReason.NewSelection);
        Assert.DoesNotContain(log.Invalidated, i => i.Generation.Value == 3);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task FilteredGestures_ProduceNoGeneration_AndNoEvents()
    {
        var feed = new FakeGestureFeed();
        var backend = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok()));
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        // OwnProcess（本进程 PID）与消费者豁免窗口：静默过滤，不产生候选也不产生 generation。
        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: Environment.ProcessId);
        feed.Raise(SelectionGesture.MultiClick, targetProcessId: Environment.ProcessId);
        using (picker.RegisterConsumerWindow(0x7777))
        {
            feed.Raise(SelectionGesture.CtrlA, targetWindowHandle: 0x7777);
        }

        Assert.Empty(log.Candidates);
        Assert.Empty(log.Failed);
        Assert.Equal(0, picker.Counters.CandidatesPublished);

        // 无洞：下一个个通过的手势 generation 仍是 1。
        feed.Raise(SelectionGesture.ShiftClick);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Equal(1, log.Captured[0].Generation!.Value.Value);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task NewGestureSupersedesInFlight_LateResultDropped()
    {
        var feed = new FakeGestureFeed();
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

        feed.Raise(SelectionGesture.BoxSelect);          // gen1 在飞（后端挂起）
        Assert.Single(log.Candidates);

        feed.Raise(SelectionGesture.CtrlA);              // gen2 取代 gen1
        Assert.Contains(log.Superseded, g => g.Value == 1);
        Assert.Equal(2, log.Candidates.Count);

        gate.SetResult();                                       // gen1 迟到结果到达：必须丢弃（不再产生第二个 terminal）
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Equal(2, log.Captured[0].Generation!.Value.Value);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task CancelSelection_TerminatesWithCancelled_DropsLateResult()
    {
        var feed = new FakeGestureFeed();
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

        feed.Raise(SelectionGesture.BoxSelect);          // gen1 在飞
        Assert.True(picker.CancelSelection(new SelectionGeneration(1)));

        Assert.Contains(log.Failed, f => f.Generation is { Value: 1 } && f.Reason == CaptureFailureReason.Cancelled);
        Assert.False(picker.CancelSelection(new SelectionGeneration(1)));    // 已终结：不可重复取消

        gate.SetResult();                                       // 迟到结果丢弃
        await Task.Delay(200);
        Assert.Empty(log.Captured);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task StopTerminatesInFlight_ThenRestartWorks()
    {
        var feed = new FakeGestureFeed();
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

        feed.Raise(SelectionGesture.BoxSelect);          // gen1 在飞
        picker.Stop();
        Assert.Contains(log.Failed, f => f.Generation is { Value: 1 } && f.Reason == CaptureFailureReason.Cancelled);

        gate.SetResult();                                       // 跨 Stop 的迟到结果：丢弃
        await Task.Delay(200);
        Assert.Empty(log.Captured);

        // 重启后管线正常，generation 继续单调（无洞跨会话保持）。
        picker.Start();
        feed.Raise(SelectionGesture.CtrlA);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Equal(2, log.Captured[0].Generation!.Value.Value);
        log.AssertLifecycleInvariants();
    }
}
