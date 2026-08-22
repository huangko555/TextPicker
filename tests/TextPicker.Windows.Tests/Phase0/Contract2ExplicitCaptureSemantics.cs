using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #2：强类型 ID + 显式捕获语义——显式查询不发布事件、不改 LastCapture、不干扰手势状态、不消耗 generation。</summary>
public sealed class Contract2ExplicitCaptureSemantics
{
    [Fact]
    public async Task ExplicitCapture_IsQueryOnly_AndLeavesGestureLifecycleUntouched()
    {
        var feed = new FakeGestureFeed();
        var backend = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok("explicit-query-text")));
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        var result = await picker.CaptureCurrentSelectionAsync(fallbackAnchor: null, CancellationToken.None);

        // query 式：成功通过 Task 返回。
        Assert.True(result.Success);
        Assert.Equal("explicit-query-text", result.Capture!.Content.Text);
        Assert.Equal(CaptureOrigin.Explicit, result.Capture.Origin);
        Assert.Null(result.Capture.Generation);
        Assert.NotNull(result.Capture.RequestId);

        // 不发布任何手势生命周期事件；LastCapture 不变（Idle → null）。
        Assert.Null(picker.LastCapture);
        Assert.Empty(log.Candidates);
        Assert.Empty(log.Captured);
        Assert.Empty(log.Failed);
        Assert.Empty(log.Superseded);
        Assert.Empty(log.Invalidated);

        // 手势状态不变：随后手势正常走两阶段，且 generation 从 1 开始（显式查询未消耗）。
        feed.Raise(SelectionGesture.BoxSelect);
        Assert.Single(log.Candidates);    // CandidateReady 同步到达
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Equal(1, log.Captured[0].Generation!.Value.Value);
        Assert.NotNull(picker.LastCapture);
        log.AssertLifecycleInvariants();
    }
}
