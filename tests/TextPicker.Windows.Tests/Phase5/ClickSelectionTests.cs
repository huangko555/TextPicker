using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase5;

/// <summary>点击型选区变化（ClickSelection，v1.1）：布防 → UIA 信号 → 非折叠预检 → 合成手势。</summary>
public sealed class ClickSelectionTests
{
    private static (FakeGestureFeed Feed, FakeUaEventSource Ua, FakeObserverLane Observer, SelectionPicker Picker, PickerEventLog Log) Create(bool precheckTrue = true, SelectionPickerOptions? options = null)
    {
        var feed = new FakeGestureFeed();
        var ua = new FakeUaEventSource();
        var observer = new FakeObserverLane { ForcedBoolResult = precheckTrue };
        var picker = new SelectionPicker(
            gestureFeed: feed,
            backend: new FakeBackend((_, _) => Task.FromResult(FakeBackend.Ok())),
            focusSource: new FakeFocusSource(),
            uaEventSource: ua,
            observerLane: observer);
        if (options != null)
        {
            picker.ApplyOptions(options);
        }

        var log = new PickerEventLog();
        log.Attach(picker);
        picker.Start();
        return (feed, ua, observer, picker, log);
    }

    [Fact]
    public async Task PlainClickPlusSelectionEvent_NonCollapsed_PublishesClickSelection()
    {
        var (feed, ua, _, picker, log) = Create();

        feed.RaisePlainClick(foregroundWindowHandle: 0x5555);    // pid 9999（假件固定）
        ua.RaiseSelectionChanged();

        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Equal(SelectionGesture.ClickSelection, log.Candidates[0].Gesture);
        Assert.Equal(1, log.Captured[0].Generation!.Value.Value);
        log.AssertLifecycleInvariants();
        picker.Stop();
    }

    [Fact]
    public async Task CollapsedPrecheck_DropsSilently_NoGeneration()
    {
        var (feed, ua, _, picker, log) = Create(precheckTrue: false);

        feed.RaisePlainClick(foregroundWindowHandle: 0x5555);
        ua.RaiseSelectionChanged();

        await Task.Delay(200);
        Assert.Empty(log.Candidates);
        Assert.Equal(0, picker.Counters.CandidatesPublished);
        Assert.True(picker.Counters.GestureDropsByReason.TryGetValue(GestureDropReason.ClickSelectionNoChange, out var drops) && drops >= 1);
        picker.Stop();
    }

    [Fact]
    public async Task EventBeyondWatchWindow_IsIgnored()
    {
        var (feed, ua, _, picker, log) = Create(options: new SelectionPickerOptions { ClickSelectionWindow = TimeSpan.FromMilliseconds(50) });

        feed.RaisePlainClick(foregroundWindowHandle: 0x5555);
        await Task.Delay(200);    // 超窗
        ua.RaiseSelectionChanged();

        await Task.Delay(200);
        Assert.Empty(log.Candidates);
        picker.Stop();
    }

    [Fact]
    public async Task RealGestureClearsWatch_NoDoubleTrigger()
    {
        var (feed, ua, _, picker, log) = Create();

        feed.RaisePlainClick(foregroundWindowHandle: 0x5555);
        feed.Raise(SelectionGesture.BoxSelect, targetProcessId: 4321);    // 真手势清除布防
        ua.RaiseSelectionChanged();    // 该事件属于真手势的选区变化，不得再合成 ClickSelection

        Assert.True(await PickerEventLog.EventuallyAsync(() => log.Captured.Count == 1));
        Assert.Single(log.Candidates);
        Assert.Equal(SelectionGesture.BoxSelect, log.Candidates[0].Gesture);
        log.AssertLifecycleInvariants();
        picker.Stop();
    }

    [Fact]
    public async Task ConsumerWindowClick_DoesNotArm()
    {
        var (feed, ua, _, picker, log) = Create();

        using (picker.RegisterConsumerWindow(0x7777))
        {
            feed.RaisePlainClick(foregroundWindowHandle: 0x7777);
        }

        ua.RaiseSelectionChanged();
        await Task.Delay(200);
        Assert.Empty(log.Candidates);
        picker.Stop();
    }
}
