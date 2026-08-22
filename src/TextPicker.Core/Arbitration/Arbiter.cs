namespace TextPicker;

public enum WorkClass
{
    /// <summary>手势捕获（最高优先级）。</summary>
    GestureCapture = 0,

    /// <summary>显式捕获（与手势共用 Capture lane，串行；显式不 supersede 手势）。</summary>
    ExplicitCapture = 1,

    /// <summary>caret / probe / state 观察。</summary>
    Observation = 2,

    /// <summary>内容流节拍（最低优先级；在飞上限 1，永不挤掉捕获）。</summary>
    StreamTick = 3,
}

public sealed record ArbiterWork(long Id, WorkClass Class, long EnqueueTimeMs, bool Coalescible);

/// <summary>
/// 调度仲裁模型（ADR-0003）：两条串行执行 lane——Capture（手势 &gt; 显式）与 Observer（观察 &gt; 流节拍）；
/// 同类队列有限容量 + DropOldest；可合并项（键盘连发）40ms 窗口内最新获胜。
/// 纯逻辑、单线程驱动契约；Phase 2 由三 lane 执行器组以此为核。
/// </summary>
public sealed class Arbiter
{
    public const long CoalesceWindowMs = 40;

    private readonly Dictionary<WorkClass, Queue<ArbiterWork>> _queues;
    private readonly Dictionary<WorkClass, int> _capacities;
    private readonly Dictionary<long, WorkClass> _inFlight = new();

    public Arbiter(int gestureQueueCapacity = 8, int explicitQueueCapacity = 4, int observationQueueCapacity = 16, int streamQueueCapacity = 8)
    {
        if (gestureQueueCapacity < 1 || explicitQueueCapacity < 1 || observationQueueCapacity < 1 || streamQueueCapacity < 1)
        {
            throw new ArgumentException("Queue capacities must be >= 1.");
        }

        _queues = new Dictionary<WorkClass, Queue<ArbiterWork>>
        {
            [WorkClass.GestureCapture] = new Queue<ArbiterWork>(gestureQueueCapacity),
            [WorkClass.ExplicitCapture] = new Queue<ArbiterWork>(explicitQueueCapacity),
            [WorkClass.Observation] = new Queue<ArbiterWork>(observationQueueCapacity),
            [WorkClass.StreamTick] = new Queue<ArbiterWork>(streamQueueCapacity),
        };
        _capacities = new Dictionary<WorkClass, int>
        {
            [WorkClass.GestureCapture] = gestureQueueCapacity,
            [WorkClass.ExplicitCapture] = explicitQueueCapacity,
            [WorkClass.Observation] = observationQueueCapacity,
            [WorkClass.StreamTick] = streamQueueCapacity,
        };
    }

    public IReadOnlyDictionary<long, WorkClass> InFlight => _inFlight;

    /// <summary>入队；返回被挤掉/被合并的 id 列表（同类内 DropOldest；Coalescible 40ms 窗口内最新获胜）。</summary>
    public IReadOnlyList<long> Enqueue(ArbiterWork work)
    {
        var queue = _queues[work.Class];
        var displaced = new List<long>();

        if (work.Coalescible)
        {
            var kept = new Queue<ArbiterWork>(queue.Count + 1);
            while (queue.Count > 0)
            {
                var existing = queue.Dequeue();
                if (existing.Coalescible && work.EnqueueTimeMs - existing.EnqueueTimeMs <= CoalesceWindowMs)
                {
                    displaced.Add(existing.Id);    // 键盘连发：最新获胜
                }
                else
                {
                    kept.Enqueue(existing);
                }
            }

            foreach (var item in kept)
            {
                queue.Enqueue(item);
            }
        }

        if (queue.Count >= _capacities[work.Class])
        {
            displaced.Add(queue.Dequeue().Id);    // 有限队列：同类内丢弃最旧
        }

        queue.Enqueue(work);
        return displaced;
    }

    /// <summary>完成在飞项（释放 lane 槽位）。</summary>
    public void Complete(long id) => _inFlight.Remove(id);

    /// <summary>调度：按 lane 与优先级取出此刻可启动的工作（返回按启动顺序排列的 id，最多每 lane 一个）。</summary>
    public IReadOnlyList<long> Schedule()
    {
        var started = new List<long>();

        // Capture lane：串行；手势 > 显式。
        if (!_inFlight.Values.Any(IsCapture))
        {
            foreach (var workClass in new[] { WorkClass.GestureCapture, WorkClass.ExplicitCapture })
            {
                if (_queues[workClass].Count > 0)
                {
                    Start(workClass, started);
                    break;
                }
            }
        }

        // Observer lane：串行；观察 > 流节拍（流在飞上限 1 由 lane 串行 + 优先级天然保证）。
        if (!_inFlight.Values.Any(IsObserver))
        {
            if (_queues[WorkClass.Observation].Count > 0)
            {
                Start(WorkClass.Observation, started);
            }
            else if (_queues[WorkClass.StreamTick].Count > 0)
            {
                Start(WorkClass.StreamTick, started);
            }
        }

        return started;
    }

    private void Start(WorkClass workClass, List<long> started)
    {
        var work = _queues[workClass].Dequeue();
        _inFlight[work.Id] = workClass;
        started.Add(work.Id);
    }

    private static bool IsCapture(WorkClass workClass) => workClass is WorkClass.GestureCapture or WorkClass.ExplicitCapture;

    private static bool IsObserver(WorkClass workClass) => workClass is WorkClass.Observation or WorkClass.StreamTick;
}
