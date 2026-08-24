using System.Collections.Concurrent;

namespace TextPicker.Windows.Execution;

internal enum QueryOutcome
{
    Completed = 0,
    TimedOut = 1,
    CircuitOpen = 2,
    WorkerBusy = 3,
    QuarantinedTarget = 4,
    SourceFailed = 5,
}

internal readonly record struct QueryResult(QueryOutcome Outcome, object? Value, Exception? Error)
{
    public static QueryResult OK(object value) => new(QueryOutcome.Completed, value, null);
}

/// <summary>
/// MTA 请求-应答执行器（ADR-0006，改编 InputCue ObservationQueryRunner 并补齐卡死置换）：
/// 串行执行；超时 → 调用方收 TimedOut、熔断冷却；worker 卡死过冷却期 → 遗弃换新（结果按 id 丢弃）；
/// 孤儿 worker 上限 1；达限或目标卡死 → 目标 quarantine（冷却 + 快速失败），不再造线程。
/// </summary>
internal sealed class QueryRunner : IDisposable
{
    private TimeSpan _timeout;
    private TimeSpan _circuitCooldown;
    private readonly int _maxOrphanWorkers;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private Worker? _worker;
    private readonly List<Worker> _orphans = new();
    private long? _lastTimeoutTimestamp;
    private readonly Dictionary<string, long> _quarantinedTargets = new();    // key → 解禁时间戳
    private long _requestCounter;
    private long? _activeRequestId;
    private bool _activeRequestTimedOut;
    private bool _disposed;

    public QueryRunner(TimeSpan timeout, TimeSpan circuitCooldown, int maxOrphanWorkers = 1, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(circuitCooldown, TimeSpan.Zero);
        _timeout = timeout;
        _circuitCooldown = circuitCooldown;
        _maxOrphanWorkers = maxOrphanWorkers;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int WorkerCreationCount { get; private set; }

    public void ApplyTimeouts(TimeSpan timeout, TimeSpan circuitCooldown)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(circuitCooldown, TimeSpan.Zero);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timeout = timeout;
            _circuitCooldown = circuitCooldown;
        }
    }

    /// <summary>执行一次查询（串行；超时/熔断/隔离按 ADR-0006）。targetKey 用于卡死目标的 quarantine 判定。</summary>
    public QueryResult Run(Func<object?> work, string? targetKey = null, CancellationToken cancellation = default)
    {
        long requestId;
        Worker? worker;
        TimeSpan timeout;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            ReapFinishedOrphans();
            PruneExpiredQuarantines();

            if (targetKey != null && _quarantinedTargets.ContainsKey(targetKey))
            {
                return new QueryResult(QueryOutcome.QuarantinedTarget, null, null);
            }

            if (IsCircuitOpen())
            {
                return new QueryResult(QueryOutcome.CircuitOpen, null, null);
            }

            if (_activeRequestId != null && !_activeRequestTimedOut)
            {
                return new QueryResult(QueryOutcome.WorkerBusy, null, null);    // 串行契约：上一请求尚在其超时窗口内
            }

            worker = EnsureUsableWorker();
            if (worker == null)
            {
                return new QueryResult(QueryOutcome.WorkerBusy, null, null);
            }

            requestId = ++_requestCounter;
            _activeRequestId = requestId;
            _activeRequestTimedOut = false;
            timeout = _timeout;
        }

        worker.StartQuery(requestId, work, targetKey);
        if (worker.WaitForResult(requestId, timeout, out var result, out var error))
        {
            lock (_gate)
            {
                if (_activeRequestId == requestId)
                {
                    _activeRequestId = null;
                    _activeRequestTimedOut = false;
                }

                _lastTimeoutTimestamp = null;    // 成功（或受控失败）重置熔断
            }

            return error != null
                ? new QueryResult(QueryOutcome.SourceFailed, null, error)
                : new QueryResult(QueryOutcome.Completed, result, null);
        }

        lock (_gate)
        {
            if (_activeRequestId == requestId)
            {
                _activeRequestTimedOut = true;    // 保持 active：worker 卡死可换新，迟到结果按 id 丢弃
            }

            _lastTimeoutTimestamp = _time.GetTimestamp();
            if (targetKey != null)
            {
                _quarantinedTargets[targetKey] = _time.GetTimestamp() + _circuitCooldown.Ticks * 2;
            }

            return new QueryResult(QueryOutcome.TimedOut, null, null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _worker?.Dispose();
            _worker = null;
            foreach (var orphan in _orphans)
            {
                orphan.Dispose();
            }

            _orphans.Clear();
        }
    }

    private Worker? EnsureUsableWorker()
    {
        // 卡死置换：上一请求已超时且 worker 仍未完成 → 遗弃（结果按 id 丢弃），换新 worker。
        if (_worker is { } current && _activeRequestTimedOut)
        {
            if (_orphans.Count >= _maxOrphanWorkers)
            {
                return null;    // 孤儿达限：不再造线程（WorkerBusy → 上层按目标 quarantine 处理）
            }

            _orphans.Add(current);
            _worker = null;
        }

        if (_worker == null)
        {
            _worker = CreateWorker();
        }

        return _worker;
    }

    private Worker CreateWorker()
    {
        WorkerCreationCount++;
        return new Worker(_time);
    }

    private bool IsCircuitOpen()
    {
        if (_lastTimeoutTimestamp is not { } lastTimeout)
        {
            return false;
        }

        if (_time.GetElapsedTime(lastTimeout, _time.GetTimestamp()) < _circuitCooldown)
        {
            return true;
        }

        _lastTimeoutTimestamp = null;
        return false;
    }

    private void ReapFinishedOrphans()
    {
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            if (_orphans[i].IsIdle)
            {
                _orphans[i].Dispose();
                _orphans.RemoveAt(i);
            }
        }
    }

    private void PruneExpiredQuarantines()
    {
        var now = _time.GetTimestamp();
        var expired = _quarantinedTargets.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _quarantinedTargets.Remove(key);
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly BlockingCollection<QueryJob> _jobs = new(boundedCapacity: 2);
        private readonly Thread _thread;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Dictionary<long, Completion> _completions = new();
        private long _activeRequestId;
        private string? _activeTargetKey;

        public Worker(TimeProvider time)
        {
            _time = time;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TextPicker.Query.MTA",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public bool IsStuck
        {
            get
            {
                lock (_gate)
                {
                    return _activeRequestId != 0 && !_completions.ContainsKey(_activeRequestId);
                }
            }
        }

        public bool IsIdle
        {
            get
            {
                lock (_gate)
                {
                    return _activeRequestId == 0;
                }
            }
        }

        public void StartQuery(long requestId, Func<object?> work, string? targetKey)
        {
            lock (_gate)
            {
                _activeRequestId = requestId;
                _activeTargetKey = targetKey;
            }

            _jobs.Add(new QueryJob(requestId, work));
        }

        public bool WaitForResult(long requestId, TimeSpan timeout, out object? result, out Exception? error)
        {
            var deadline = _time.GetTimestamp() + timeout.Ticks;
            while (true)
            {
                lock (_gate)
                {
                    if (_completions.Remove(requestId, out var completion))
                    {
                        if (_activeRequestId == requestId)
                        {
                            _activeRequestId = 0;
                            _activeTargetKey = null;
                        }

                        result = completion.Value;
                        error = completion.Error;
                        return true;
                    }
                }

                var remaining = TimeSpan.FromTicks(deadline - _time.GetTimestamp());
                if (remaining <= TimeSpan.Zero)
                {
                    result = null;
                    error = null;
                    return false;
                }

                Thread.Sleep((int)Math.Min(remaining.TotalMilliseconds, 10));
            }
        }


        public void Dispose()
        {
            _jobs.CompleteAdding();
            lock (_gate)
            {
                _completions.Clear();
            }
        }

        private void Run()
        {
            try
            {
                foreach (var job in _jobs.GetConsumingEnumerable())
                {
                    object? value = null;
                    Exception? error = null;
                    try
                    {
                        value = job.Work();
                    }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    {
                        error = exception;
                    }

                    lock (_gate)
                    {
                        _completions[job.RequestId] = new Completion(value, error);
                        if (_activeRequestId == job.RequestId)
                        {
                            _activeRequestId = 0;
                            _activeTargetKey = null;
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private readonly record struct QueryJob(long RequestId, Func<object?> Work);

        private readonly record struct Completion(object? Value, Exception? Error);
    }
}
