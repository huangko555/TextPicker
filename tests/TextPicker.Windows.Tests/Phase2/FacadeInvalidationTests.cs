using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase2;

/// <summary>捕获后失效跟踪（§5 Invalidated）：Escape / OutsideClick（消费者豁免）/ ForegroundChanged / TargetGone + 全屏暂停过滤。</summary>
public sealed class FacadeInvalidationTests
{
    private static SelectionPicker StartCaptured(FakeGestureFeed feed, PickerEventLog log, nint targetHwnd = 0x1234, int targetPid = 4321)
    {
        var picker = new SelectionPicker(feed, new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok())), focusSource: new FakeFocusSource());
        log.Attach(picker);
        picker.Start();
        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: targetPid, targetWindowHandle: targetHwnd);
        return picker;
    }

    [Fact]
    public async Task EscapeAfterCapture_InvalidatesEscape()
    {
        var feed = new FakeGestureFeed();
        using var log = new PickerEventLog();
        using var picker = StartCaptured(feed, log);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));

        feed.RaiseInterrupt(InputInterruptKind.Escape);
        Assert.Contains(log.Invalidated, i => i.Generation.Value == 1 && i.Reason == SelectionInvalidationReason.Escape);

        feed.RaiseInterrupt(InputInterruptKind.Escape);    // 已失效：不重复
        Assert.Single(log.Invalidated);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task PlainClickAfterCapture_InvalidatesOutsideClick_ConsumerWindowExempt()
    {
        var feed = new FakeGestureFeed();
        using var log = new PickerEventLog();
        using var picker = StartCaptured(feed, log);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));

        using (picker.RegisterConsumerWindow(0x7777))
        {
            feed.RaisePlainClick(foregroundWindowHandle: 0x7777);    // 消费者窗口上的单击：豁免
            Assert.Empty(log.Invalidated);
        }

        feed.RaisePlainClick(foregroundWindowHandle: 0x2222);        // 消费者窗口外：失效
        Assert.Contains(log.Invalidated, i => i.Reason == SelectionInvalidationReason.OutsideClick);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task ForegroundPidChange_InvalidatesForegroundChanged_AndUpdatesFocusTarget()
    {
        var feed = new FakeGestureFeed();
        using var log = new PickerEventLog();
        var focus = new FakeFocusSource();
        var picker = (SelectionPicker)null!;
        var backendOk = new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok()));
        picker = new SelectionPicker(feed, backendOk, focusSource: focus);
        log.Attach(picker);
        picker.Start();
        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: 4321, targetWindowHandle: 0x1234);
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));

        focus.Raise(new ForegroundTargetSnapshot(0x9999, 5555));    // 前台换进程

        Assert.Contains(log.Invalidated, i => i.Reason == SelectionInvalidationReason.ForegroundChanged);
        Assert.NotNull(picker.CurrentFocusTarget);
        Assert.Equal(5555, picker.CurrentFocusTarget!.ProcessId);
        Assert.Equal(0x9999, picker.CurrentFocusTarget.WindowHandle);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task SamePidButDeadWindow_InvalidatesTargetGone()
    {
        var feed = new FakeGestureFeed();
        using var log = new PickerEventLog();
        var focus = new FakeFocusSource();
        var picker = new SelectionPicker(feed, new FakeBackend((req, _) => Task.FromResult(FakeBackend.Ok(targetProcessId: req.Target.ProcessId, targetWindowHandle: req.Target.WindowHandle))), focusSource: focus);
        log.Attach(picker);
        picker.Start();
        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: 4321, targetWindowHandle: unchecked((nint)0x7FFFFFFFDEAD));    // 必然无效句柄
        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));

        focus.Raise(new ForegroundTargetSnapshot(0x1234, 4321));    // PID 未变但原窗口已消亡

        Assert.Contains(log.Invalidated, i => i.Reason == SelectionInvalidationReason.TargetGone);
        log.AssertLifecycleInvariants();
    }

    [Fact]
    public async Task FullScreenPause_FiltersGesture_Silently()
    {
        var feed = new FakeGestureFeed();
        using var log = new PickerEventLog();
        using var picker = new SelectionPicker(
            feed,
            new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok())),
            policy: new DefaultTargetPolicy(() => true));    // 全屏探测恒真
        log.Attach(picker);
        picker.Start();

        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: 4321);
        Assert.Empty(log.Candidates);
        Assert.Equal(0, picker.Counters.CandidatesPublished);
    }
}
