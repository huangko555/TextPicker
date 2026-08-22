using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #7：RunEpoch——Stop → 注入迟到 UIA 回调 → Start → 旧 epoch 回调不产生任何公开事件（ADR-0002）。</summary>
public sealed class Contract7RunEpoch
{
    [Fact]
    public async Task StaleEpochCallbacksAndGestures_ProduceNoPublicEvents_AfterRestart()
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
        var epoch1 = picker.CurrentEpoch;
        Assert.Equal(1, epoch1);

        feed.Raise(SelectionGesture.BoxSelect);          // gen1 在飞
        picker.Stop();                                    // gen1 → Failed(Cancelled)

        var snapshotBefore = log.SnapshotOf();
        int capturedBefore = snapshotBefore.Captured.Count;
        int failedBefore = snapshotBefore.Failed.Count;

        // 旧 epoch 的迟到回调三连：UIA 信号注入、旧后端完成、旧 epoch 手势。
        picker.EnqueueUaSignal(epoch1, SelectionPicker.UaSignalKind.TextSelectionChanged);
        feed.Raise(SelectionGesture.BoxSelect, epochOverride: epoch1);
        gate.SetResult();
        await Task.Delay(300);
        picker.EnqueueUaSignal(epoch1, SelectionPicker.UaSignalKind.FocusChanged);

        var snapshotAfter = log.SnapshotOf();
        Assert.Equal(capturedBefore, snapshotAfter.Captured.Count);
        Assert.Equal(failedBefore, snapshotAfter.Failed.Count);
        Assert.Equal(snapshotBefore.Candidates.Count, snapshotAfter.Candidates.Count);
        Assert.Equal(snapshotBefore.Superseded.Count, snapshotAfter.Superseded.Count);

        // 重启：epoch 递增，新 epoch 管线正常。
        picker.Start();
        var epoch2 = picker.CurrentEpoch;
        Assert.True(epoch2 > epoch1);

        picker.EnqueueUaSignal(epoch1, SelectionPicker.UaSignalKind.TextSelectionChanged);    // 旧 epoch 注入仍无事件
        await Task.Delay(100);
        Assert.Equal(snapshotAfter.Candidates.Count, log.SnapshotOf().Candidates.Count);

        feed.Raise(SelectionGesture.CtrlA);              // 新 epoch 手势正常
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.SnapshotOf().Captured.Count == capturedBefore + 1));
        log.AssertLifecycleInvariants();
    }
}
