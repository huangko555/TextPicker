using TextPicker;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #4：几何可空——正文成功 + GeometryCompleteness.None 合法发布，不判失败。</summary>
public sealed class Contract4NullableGeometry
{
    [Fact]
    public async Task ContentSuccessWithNoGeometry_PublishesAsCaptured()
    {
        var feed = new FakeGestureFeed();
        var backend = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok("text-without-geometry", GeometryCompleteness.None)));
        using var picker = new SelectionPicker(feed, backend);
        using var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();

        feed.Raise(SelectionGesture.ShiftKeyboard);   // 键盘手势：候选 ProvisionalAnchor=null

        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        var capture = log.Captured[0];
        Assert.Equal(GeometryCompleteness.None, capture.Geometry.Completeness);
        Assert.Empty(capture.Geometry.VisibleRects);
        Assert.Null(log.Candidates[0].ProvisionalAnchor);
        Assert.Empty(log.Failed);
        log.AssertLifecycleInvariants();
    }
}
