namespace TextPicker.Core.Tests;

public sealed class ArbiterTests
{
    [Fact]
    public void CaptureLane_PrefersGestureOverExplicit()
    {
        var arbiter = new Arbiter();
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.ExplicitCapture, 0, Coalescible: false));
        arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 1, Coalescible: false));

        Assert.Equal(new long[] { 2 }, arbiter.Schedule());       // 手势优先
        Assert.Empty(arbiter.Schedule());                          // Capture lane 串行：显式等待

        arbiter.Complete(2);
        Assert.Equal(new long[] { 1 }, arbiter.Schedule());        // 手势完成后显式启动
    }

    [Fact]
    public void DropOldest_AppliesWithinSameClassOnly()
    {
        var arbiter = new Arbiter(gestureQueueCapacity: 2);
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.GestureCapture, 0, Coalescible: false));
        arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 1, Coalescible: false));

        var displaced = arbiter.Enqueue(new ArbiterWork(3, WorkClass.GestureCapture, 2, Coalescible: false));

        Assert.Equal(new long[] { 1 }, displaced);    // 丢最旧，不动其他类
        Assert.Equal(new long[] { 2 }, arbiter.Schedule());
        arbiter.Complete(2);
        Assert.Equal(new long[] { 3 }, arbiter.Schedule());
    }

    [Fact]
    public void CoalescibleWork_LatestWinsWithin40Ms()
    {
        var arbiter = new Arbiter();

        arbiter.Enqueue(new ArbiterWork(1, WorkClass.GestureCapture, 0, Coalescible: true));
        var displaced = arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 35, Coalescible: true));

        Assert.Equal(new long[] { 1 }, displaced);    // 35ms ≤ 40ms 窗口：最新获胜
        Assert.Equal(new long[] { 2 }, arbiter.Schedule());
    }

    [Fact]
    public void CoalescibleWork_OutsideWindow_BothKept()
    {
        var arbiter = new Arbiter();

        arbiter.Enqueue(new ArbiterWork(1, WorkClass.GestureCapture, 0, Coalescible: true));
        var displaced = arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 41, Coalescible: true));

        Assert.Empty(displaced);    // 超出 40ms 窗口：不合并
        Assert.Equal(new long[] { 1 }, arbiter.Schedule());
        arbiter.Complete(1);
        Assert.Equal(new long[] { 2 }, arbiter.Schedule());
    }

    [Fact]
    public void ObserverLane_PrefersObservationOverStream()
    {
        var arbiter = new Arbiter();
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.StreamTick, 0, Coalescible: false));
        arbiter.Enqueue(new ArbiterWork(2, WorkClass.Observation, 1, Coalescible: false));

        Assert.Equal(new long[] { 2 }, arbiter.Schedule());    // 观察优先进入 Observer lane

        arbiter.Complete(2);
        Assert.Equal(new long[] { 1 }, arbiter.Schedule());    // 流节拍随后
    }

    [Fact]
    public void StreamNeverBlocksCaptureLane()
    {
        var arbiter = new Arbiter();
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.StreamTick, 0, Coalescible: false));

        Assert.Equal(new long[] { 1 }, arbiter.Schedule());    // 流启动

        arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 5, Coalescible: false));
        Assert.Equal(new long[] { 2 }, arbiter.Schedule());    // 捕获 lane 独立：流在飞不挤掉捕获
    }

    [Fact]
    public void StreamInFlightLimit_IsOne()
    {
        var arbiter = new Arbiter();
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.StreamTick, 0, Coalescible: false));
        Assert.Equal(new long[] { 1 }, arbiter.Schedule());

        arbiter.Enqueue(new ArbiterWork(2, WorkClass.StreamTick, 5, Coalescible: false));
        Assert.Empty(arbiter.Schedule());    // Observer lane 被流占用：第二个流不启动

        arbiter.Complete(1);
        Assert.Equal(new long[] { 2 }, arbiter.Schedule());
    }

    [Fact]
    public void ExplicitCapture_IsNeverDisplacedByGestureBurst()
    {
        var arbiter = new Arbiter(gestureQueueCapacity: 1);
        arbiter.Enqueue(new ArbiterWork(1, WorkClass.ExplicitCapture, 0, Coalescible: false));

        arbiter.Enqueue(new ArbiterWork(2, WorkClass.GestureCapture, 1, Coalescible: false));
        var displaced = arbiter.Enqueue(new ArbiterWork(3, WorkClass.GestureCapture, 2, Coalescible: false));

        Assert.Contains(2L, displaced);          // 手势队列内部挤掉手势
        Assert.DoesNotContain(1L, displaced);    // 显式不被挤（显式不 supersede 手势，反之亦然）
    }
}
