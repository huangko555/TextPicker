using System.Text.Json;

namespace TextPicker.Core.Tests;

/// <summary>仲裁黄金迹线回放（§9：DropOldest、优先级、流不挤捕获、最新获胜、显式不 supersede 手势）。</summary>
public sealed class ArbiterGoldenTraceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> TraceNames() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "traces"), "*.json")
            .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(TraceNames))]
    public void Trace_ReplaysExactly(string traceFileName)
    {
        var tracePath = Path.Combine(AppContext.BaseDirectory, "traces", traceFileName);
        var trace = JsonSerializer.Deserialize<GoldenTrace>(File.ReadAllText(tracePath), JsonOptions);
        Assert.NotNull(trace);

        var arbiter = new Arbiter(
            trace.Arbiter.GestureQueue,
            trace.Arbiter.ExplicitQueue,
            trace.Arbiter.ObservationQueue,
            trace.Arbiter.StreamQueue);

        foreach (var step in trace.Steps)
        {
            switch (step.Op)
            {
                case "enqueue":
                    var workClass = Enum.Parse<WorkClass>(step.Work!.Class, ignoreCase: true);
                    var displaced = arbiter.Enqueue(new ArbiterWork(step.Work.Id, workClass, step.Work.At, step.Work.Coalescible));
                    Assert.Equal(step.ExpectDisplaced ?? Array.Empty<long>(), displaced);
                    break;
                case "schedule":
                    Assert.Equal(step.ExpectStarted ?? Array.Empty<long>(), arbiter.Schedule());
                    break;
                case "complete":
                    arbiter.Complete(step.Id!.Value);
                    break;
                default:
                    Assert.Fail($"unknown op {step.Op} in {traceFileName}");
                    return;
            }
        }
    }

    private sealed record GoldenTrace(string Name, TraceArbiterConfig Arbiter, TraceStep[] Steps);

    private sealed record TraceArbiterConfig(int GestureQueue, int ExplicitQueue, int ObservationQueue, int StreamQueue);

    private sealed record TraceStep(string Op, long? Id, TraceWork? Work, long[]? ExpectDisplaced, long[]? ExpectStarted);

    private sealed record TraceWork(long Id, string Class, long At, bool Coalescible);
}
