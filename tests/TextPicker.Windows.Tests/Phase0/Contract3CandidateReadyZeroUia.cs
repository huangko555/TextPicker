using System.Diagnostics;
using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #3：CandidateReady 零 UIA——假后端挂起时候选必须立即到达（阶段一结构上不可能触发 UIA）。</summary>
public sealed class Contract3CandidateReadyZeroUia
{
    [Fact]
    public async Task CandidateReadyArrivesImmediately_WhileBackendHangs()
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

        var stopwatch = Stopwatch.StartNew();
        feed.Raise(SelectionGesture.BoxSelect);
        stopwatch.Stop();

        // 候选立即到达，与后端无关（防未来偷加 ControlType/IsEditable 等 UIA 查询）。
        Assert.Single(log.Candidates);
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"CandidateReady took {stopwatch.ElapsedMilliseconds}ms");

        // 阶段二在读完成后才发布（读后发布）。
        Assert.Empty(log.Captured);
        gate.SetResult();
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        log.AssertLifecycleInvariants();
    }
}
