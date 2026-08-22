using TextPicker.Windows.Execution;

namespace TextPicker.Windows.Tests.Phase2;

public sealed class QueryRunnerTests
{
    [Fact]
    public void SuccessfulQuery_ReturnsCompleted()
    {
        using var runner = new QueryRunner(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        var result = runner.Run(() => "ok");

        Assert.Equal(QueryOutcome.Completed, result.Outcome);
        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public void ThrowingQuery_ReturnsSourceFailed()
    {
        using var runner = new QueryRunner(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));

        var result = runner.Run(new Func<object?>(() => throw new InvalidOperationException("boom")));

        Assert.Equal(QueryOutcome.SourceFailed, result.Outcome);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public void SlowQuery_TimesOut_ThenCircuitOpens()
    {
        using var runner = new QueryRunner(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(400));

        var first = runner.Run(() => { Thread.Sleep(3000); return "never"; });
        Assert.Equal(QueryOutcome.TimedOut, first.Outcome);

        var second = runner.Run(() => "ok");    // 冷却期内：熔断
        Assert.Equal(QueryOutcome.CircuitOpen, second.Outcome);
    }

    [Fact]
    public void StuckWorker_IsReplacedAfterCooldown()
    {
        using var runner = new QueryRunner(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250));

        _ = runner.Run(() => { Thread.Sleep(3000); return "stuck"; });
        Thread.Sleep(400);    // 过冷却期

        var recovered = runner.Run(() => "fresh");
        Assert.Equal(QueryOutcome.Completed, recovered.Outcome);
        Assert.Equal("fresh", recovered.Value);
        Assert.Equal(2, runner.WorkerCreationCount);    // 卡死置换：换了新 worker
    }

    [Fact]
    public void OrphanCap_SecondStuckWorker_ReturnsWorkerBusy()
    {
        using var runner = new QueryRunner(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250));

        _ = runner.Run(() => { Thread.Sleep(3000); return "stuck1"; });    // worker1 卡死
        Thread.Sleep(400);
        _ = runner.Run(() => { Thread.Sleep(3000); return "stuck2"; });    // worker1 置换为孤儿，worker2 卡死
        Thread.Sleep(400);

        var third = runner.Run(() => "stuck3");    // 孤儿上限 1：不再造线程
        Assert.Equal(QueryOutcome.WorkerBusy, third.Outcome);
        Assert.Equal(2, runner.WorkerCreationCount);
    }

    [Fact]
    public void StuckTarget_IsQuarantined()
    {
        using var runner = new QueryRunner(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(250));

        _ = runner.Run(() => { Thread.Sleep(3000); return "stuck"; }, targetKey: "pid:777");

        var sameTarget = runner.Run(() => "x", targetKey: "pid:777");    // 隔离检查在熔断之前
        Assert.Equal(QueryOutcome.QuarantinedTarget, sameTarget.Outcome);

        var otherTarget = runner.Run(() => "x", targetKey: "pid:888");   // 其他目标：熔断中
        Assert.Equal(QueryOutcome.CircuitOpen, otherTarget.Outcome);
    }
}
