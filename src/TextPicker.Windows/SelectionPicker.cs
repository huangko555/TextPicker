using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using TextPicker.Windows.Uia;

namespace TextPicker.Windows;

/// <summary>
/// 门面（Phase 0 最小实现：seam 可注入，管线路语义与不变式已冻结；Phase 2 换真实输入源/串行队列/lane 执行器，公开契约不变）。
/// <para>线程模型（Phase 0）：管线转换在单一锁内完成，事件在锁内同步引发（Monitor 同线程可重入，消费者同线程回调安全；
/// 跨线程阻塞式消费者会拖慢管线——Phase 2 串行队列 + Options.EventContext 封送解决）。</para>
/// </summary>
public sealed class SelectionPicker : ISelectionPicker, IDisposable
{
    private readonly object _gate = new();
    private readonly ISelectionGestureFeed _feed;
    private readonly ISelectionBackend _backend;
    private readonly ITargetPolicy _policy;
    private readonly IFocusTargetSource _focusSource;
    private readonly IUaEventSource _uaEvents;
    private readonly IObserverLane _observerLane;
    private readonly TimeProvider _time;

    private bool _running;
    private long _epoch;                       // RunEpoch（ADR-0002）
    private long _nextGeneration;              // 严格单调且无洞（被过滤手势不消耗）
    private long _nextRequestId;
    private SelectionPickerOptions _options = new();
    private readonly HashSet<nint> _consumerWindows = new();

    private PipelineState? _inFlight;          // 当前手势阶段二
    private readonly Dictionary<SelectionRequestId, CancellationTokenSource> _pendingRequests = new();
    private SelectionCapture? _lastCapture;
    private bool _lastCaptureInvalidated;      // Captured 之后 Invalidated ≤ 1
    private TargetContext? _focusTarget;

    // 内容流（显式订阅制；40ms 合并 + 150ms 最小观测间隔 + 在飞上限 1）
    private readonly List<Action<SelectionContentChangedEventArgs>> _contentSubscribers = new();
    private long _lastStreamReadTimestamp;
    private int _streamInFlight;

    // 点击型选区变化（ClickSelection）布防：普通单击 → UIA TextSelectionChanged 确认 → 非折叠预检 → 合成手势
    private ClickWatch? _clickWatch;

    private readonly record struct ClickWatch(
        long ArmedTimestamp,
        long Epoch,
        int ProcessId,
        nint WindowHandle,
        PhysicalScreenPoint Down,
        PhysicalScreenPoint Up,
        bool VerificationScheduled = false);

    // counters（string-free，ADR-0008）
    private long _candidatesPublished;
    private long _capturesSucceeded;
    private long _capturesFailed;
    private long _superseded;
    private long _invalidated;
    private long _cancelled;
    private long _explicitQueries;
    private long _filteredGestures;
    private readonly Dictionary<GestureDropReason, int> _gestureDrops = new();
    private readonly Dictionary<CaptureFailureReason, int> _failuresByReason = new();
    private readonly Dictionary<SelectionInvalidationReason, int> _invalidationsByReason = new();

    /// <summary>生产入口：Owned 输入源 + Lane 路由 UIA 后端 + UIA 事件源 + WinEvent 焦点源（ADR-0001/0003/0005）。</summary>
    public SelectionPicker()
        : this(gestureFeed: null, backend: null, policy: null, timeProvider: null, inputSource: new OwnedRawInputSource(), focusSource: null)
    {
    }

    /// <summary>组装入口（seam 注入；测试与 Phase 装配用）。</summary>
    internal SelectionPicker(
        ISelectionGestureFeed? gestureFeed = null,
        ISelectionBackend? backend = null,
        ITargetPolicy? policy = null,
        TimeProvider? timeProvider = null,
        IInputRecordSource? inputSource = null,
        IFocusTargetSource? focusSource = null,
        IUaEventSource? uaEventSource = null,
        IObserverLane? observerLane = null)
    {
        _uaEvents = uaEventSource ?? new UiaAutomationEventSource();
        _backend = backend ?? new LaneRoutedBackend(
            (request, ct) => new UiaSelectionBackend(waitForSelectionSignal: _uaEvents.WaitForSelectionSignal).Read(request, ct));
        _feed = gestureFeed ?? new CoreGestureFeed(inputSource ?? new OwnedRawInputSource());
        _policy = policy ?? DefaultTargetPolicy.Instance;
        _focusSource = focusSource ?? new WinEventFocusTargetSource();
        _observerLane = observerLane ?? new QueryRunnerObserverLane();
        _time = timeProvider ?? TimeProvider.System;
        _feed.GestureDetected += OnGestureDetected;
        _feed.InterruptDetected += OnInterruptDetected;
        _feed.GestureDropped += OnGestureDropped;
        _feed.PlainClickObserved += OnPlainClickObserved;
        _focusSource.ForegroundChanged += OnForegroundChanged;
    }

    // —— A. 生命周期 ——

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new InvalidOperationException("SelectionPicker is already running.");
            }

            // feed 先行启动（Owned fail-fast 在此传播：RawInputRegistrationConflict 等启动失败时状态保持未运行）。
            var newEpoch = _epoch + 1;
            _feed.Start(newEpoch);
            _focusSource.Start();
            _uaEvents.Start(newEpoch, OnUaSignal);
            _running = true;
            _epoch = newEpoch;                            // 旧 epoch 回调自此全部作废（ADR-0002）
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;

            // 在飞 generation 在 Stop 边界终结为 Cancelled，保持 exactly-one-terminal 不变式跨 Stop/Start 成立。
            if (_inFlight is { } st && !st.TerminalPublished)
            {
                TerminateInFlight(st);
                RaiseSelectionFailed(st, CaptureFailureReason.Cancelled);
            }

            _inFlight = null;
            _lastCaptureInvalidated = true;     // 冻结最后值，Stop 后不再发失效
            _clickWatch = null;
            foreach (var cts in _pendingRequests.Values)
            {
                cts.Cancel();
            }

            _focusSource.Stop();
            _uaEvents.Stop();
            _feed.Stop();
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _feed.GestureDetected -= OnGestureDetected;
        _feed.InterruptDetected -= OnInterruptDetected;
        _feed.GestureDropped -= OnGestureDropped;
        GC.SuppressFinalize(this);
    }

    // —— B. 手势选区（两阶段）——

    public event EventHandler<SelectionCandidateReadyEventArgs>? SelectionCandidateReady;
    public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;
    public event EventHandler<SelectionFailedEventArgs>? SelectionFailed;
    public event EventHandler<SelectionSupersededEventArgs>? SelectionSuperseded;
    public event EventHandler<SelectionInvalidatedEventArgs>? SelectionInvalidated;

    public SelectionCapture? LastCapture
    {
        get
        {
            lock (_gate)
            {
                return _lastCapture;
            }
        }
    }

    public async Task<SelectionCaptureResult> CaptureCurrentSelectionAsync(PhysicalScreenPoint? fallbackAnchor, CancellationToken ct)
    {
        SelectionRequestId id;
        long epoch;
        SelectionPickerOptions optionsSnapshot;
        long startTimestamp;
        CancellationTokenSource linked;

        lock (_gate)
        {
            if (!_running)
            {
                throw new InvalidOperationException("SelectionPicker is not running.");
            }

            id = new SelectionRequestId(++_nextRequestId);
            epoch = _epoch;
            optionsSnapshot = _options with { };
            startTimestamp = _time.GetTimestamp();
            _explicitQueries++;
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pendingRequests[id] = linked;
        }

        BackendReadResult result;
        try
        {
            var request = new BackendReadRequest
            {
                Epoch = epoch,
                RequestId = id,
                Origin = CaptureOrigin.Explicit,
                Gesture = SelectionGesture.Explicit,
                FallbackAnchor = fallbackAnchor,
                Options = optionsSnapshot,
            };
            result = await _backend.ReadAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // query 式：取消 = 放弃等待而非中止 COM；结果以 Cancelled 收口，任务不悬挂。
            return SelectionCaptureResult.Fail(CaptureFailureReason.Cancelled, _time.GetElapsedTime(startTimestamp));
        }
        finally
        {
            lock (_gate)
            {
                _pendingRequests.Remove(id);
            }

            linked.Dispose();
        }

        TimeSpan elapsed;
        lock (_gate)
        {
            elapsed = _time.GetElapsedTime(startTimestamp);
            if (epoch != _epoch)
            {
                // 跨 Stop/Start 的迟到完成（ADR-0002）：不产生任何公开事件，仅以 query 结果收口。
                return SelectionCaptureResult.Fail(CaptureFailureReason.StaleResult, elapsed);
            }

            _pendingRequests.Remove(id);
        }

        if (!result.Success)
        {
            return SelectionCaptureResult.Fail(result.Failure, elapsed);
        }

        // query 式语义：不发布 SelectionCaptured、不更新 LastCapture、不改变手势状态、不 supersede 手势。
        var capture = BuildCapture(result, generation: null, requestId: id, origin: CaptureOrigin.Explicit, gesture: null, down: null, up: fallbackAnchor, elapsed);
        return SelectionCaptureResult.Ok(capture);
    }

    public Task<TargetProbeResult> ProbeTargetAsync(PhysicalScreenPoint point, bool includeText, CancellationToken ct)
        => _observerLane.RunAsync(() => UiaSelectionBackend.Probe(point, includeText, ct), targetKey: $"probe:{(int)point.X},{(int)point.Y}", ct);

    public bool CancelSelection(SelectionGeneration generation)
    {
        lock (_gate)
        {
            if (_inFlight is { } st && st.Generation == generation && !st.TerminalPublished)
            {
                MarkTerminal(st);
                _inFlight = null;
                _cancelled++;
                RaiseSelectionFailed(st, CaptureFailureReason.Cancelled);
                return true;
            }

            return false;
        }
    }

    public bool CancelRequest(SelectionRequestId requestId)
    {
        lock (_gate)
        {
            if (_pendingRequests.TryGetValue(requestId, out var cts))
            {
                cts.Cancel();    // 放弃等待而非中止 COM（在飞调用可能延迟到超时）
                return true;
            }

            return false;
        }
    }

    public IDisposable SubscribeSelectionContent(Action<SelectionContentChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _contentSubscribers.Add(handler);
        }

        return new ContentSubscription(this, handler);
    }

    // —— C. 指针/光标 ——

    public PointerSnapshot GetPointerSnapshot()
    {
        // Phase 2：由 IInputEventSource 指针流填充（位置/按键/按下抬起点/拖拽中）。
        return new PointerSnapshot { ObservedAt = _time.GetUtcNow() };
    }

    public event EventHandler<GesturePhaseEventArgs>? GesturePhaseChanged;

    // —— D. 插入光标（Phase 3）——

    public Task<CaretObservation?> ObserveCaretAsync(CancellationToken ct)
        => _observerLane.RunAsync(() =>
        {
            var probe = Uia.CaretProbeChain.Observe();
            if (!probe.Found)
            {
                return null;
            }

            var observation = new CaretObservation
            {
                CaretRect = probe.CaretRect!.Value,
                Source = probe.Source!.Value,
                IsCollapsedSelection = probe.IsCollapsedSelection,
                Target = probe.Target ?? new TargetContext(),
            };
            RaiseEvent(CaretChanged, new CaretEventArgs(observation));
            return observation;
        }, targetKey: null, ct);

    public Task<SelectionState?> ObserveSelectionStateAsync(CancellationToken ct)
        => _observerLane.RunAsync(() => UiaSelectionBackend.ObserveSelectionState(ct), targetKey: null, ct);

    // —— E. 焦点目标 ——

    /// <summary>当前前台目标（Win32 浅上下文：PID/进程名/HWND/窗口类；UIA 富化在 Phase 3）。</summary>
    public TargetContext? CurrentFocusTarget
    {
        get
        {
            lock (_gate)
            {
                return _focusTarget;
            }
        }
    }

    public event EventHandler<CaretEventArgs>? CaretChanged;

    public event EventHandler<FocusTargetEventArgs>? FocusTargetChanged;

    // —— F. 配置 ——

    public SelectionPickerOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options with { };
            }
        }
    }

    public void ApplyOptions(SelectionPickerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SelectionPickerOptionsValidator.Validate(options);

        lock (_gate)
        {
            _options = options with { };
            if (_backend is LaneRoutedBackend routedBackend)
            {
                routedBackend.ApplyTimeouts(_options.QueryTimeout, _options.CircuitCooldown);
            }

            if (_observerLane is QueryRunnerObserverLane queryObserverLane)
            {
                queryObserverLane.ApplyTimeouts(_options.QueryTimeout, _options.CircuitCooldown);
            }

            // 配置热变更使已捕获结果失效（Invalidated 原因之一：OptionsChanged）。
            if (_lastCapture?.Generation is { } gen && !_lastCaptureInvalidated)
            {
                _lastCaptureInvalidated = true;
                RaiseInvalidated(gen, SelectionInvalidationReason.OptionsChanged);
            }
        }
    }

    public void SetTriggerEnabled(SelectionGesture gesture, bool enabled)
    {
        lock (_gate)
        {
            var o = _options with { };
            switch (gesture)
            {
                case SelectionGesture.BoxSelect: o.BoxSelectEnabled = enabled; break;
                case SelectionGesture.MultiClick: o.MultiClickEnabled = enabled; break;
                case SelectionGesture.ShiftClick: o.ShiftClickEnabled = enabled; break;
                case SelectionGesture.CtrlA: o.CtrlAEnabled = enabled; break;
                case SelectionGesture.ShiftKeyboard: o.ShiftKeyboardEnabled = enabled; break;
                case SelectionGesture.ClickSelection: o.ClickSelectionEnabled = enabled; break;
                default: throw new ArgumentOutOfRangeException(nameof(gesture), gesture, null);
            }

            _options = o;
        }
    }

    public void SetExcludedProcesses(IReadOnlyList<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        lock (_gate)
        {
            _options = _options with { ExcludedProcesses = processNames };
        }
    }

    public IDisposable RegisterConsumerWindow(nint window)
    {
        lock (_gate)
        {
            _consumerWindows.Add(window);
        }

        return new ConsumerWindowToken(this, window);
    }

    // —— G. 诊断 ——

    public event EventHandler<SelectionDiagnosticsEventArgs>? Diagnostics;

    public SelectionPickerCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new SelectionPickerCounters
                {
                    CandidatesPublished = _candidatesPublished,
                    CapturesSucceeded = _capturesSucceeded,
                    CapturesFailed = _capturesFailed,
                    Superseded = _superseded,
                    Invalidated = _invalidated,
                    Cancelled = _cancelled,
                    ExplicitQueries = _explicitQueries,
                    GestureDropsByReason = new Dictionary<GestureDropReason, int>(_gestureDrops),
                    FailuresByReason = new Dictionary<CaptureFailureReason, int>(_failuresByReason),
                    InvalidationsByReason = new Dictionary<SelectionInvalidationReason, int>(_invalidationsByReason),
                };
            }
        }
    }

    // —— RunEpoch 注入口（Lane 1 UIA 回调，Phase 2 接线；契约测试 #7 直接驱动）——

    internal enum UaSignalKind
    {
        TextSelectionChanged,
        FocusChanged,
    }

    /// <summary>当前 epoch 值（测试与 Lane 接线用）。旧 epoch 的信号一律丢弃。</summary>
    internal long CurrentEpoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    internal void EnqueueUaSignal(long epoch, UaSignalKind kind)
    {
        List<Action<SelectionContentChangedEventArgs>>? subscribers = null;
        ClickWatch? watchToVerify = null;
        lock (_gate)
        {
            if (!_running || epoch != _epoch)
            {
                return;    // 旧 epoch 迟到回调：不产生任何公开事件（ADR-0002）
            }

            if (kind == UaSignalKind.TextSelectionChanged)
            {
                // 点击型选区变化：布防窗口内的选区变化事件 → 非折叠预检 → 合成 ClickSelection 手势。
                if (_clickWatch is { VerificationScheduled: false } watch
                    && _time.GetElapsedTime(watch.ArmedTimestamp, _time.GetTimestamp()) <= _options.ClickSelectionWindow)
                {
                    watchToVerify = watch with { VerificationScheduled = true };
                    _clickWatch = watchToVerify;    // 单发：同一布防只安排一次验证
                }

                if (_contentSubscribers.Count > 0)
                {
                    // 双闸：150ms 最小观测间隔 + 在飞上限 1（40ms 合并由 UIA 事件天然节流近似）。
                    var now = _time.GetTimestamp();
                    if (_streamInFlight == 0 && _time.GetElapsedTime(_lastStreamReadTimestamp, now) >= TimeSpan.FromMilliseconds(150))
                    {
                        _streamInFlight = 1;
                        _lastStreamReadTimestamp = now;
                        subscribers = new List<Action<SelectionContentChangedEventArgs>>(_contentSubscribers);
                    }
                }
            }
        }

        if (watchToVerify is { } toVerify)
        {
            _ = VerifyClickSelectionAsync(toVerify);
        }

        if (subscribers != null)
        {
            _ = PushContentStreamAsync(epoch, subscribers);
        }
    }

    /// <summary>ClickSelection 防噪闸：确认焦点元素持有该进程的非折叠选区后才合成手势；否则静默丢弃计数。</summary>
    private async Task VerifyClickSelectionAsync(ClickWatch watch)
    {
        // 第一击造成的 SelectionChanged 可能早于第二击的 MultiClick 手势。等过系统双击窗口，
        // 让真实多击先清除布防，避免先发布 ClickSelection、随后又发布 MultiClick。
        await Task.Delay(TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime()), _time).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_running || watch.Epoch != _epoch || _clickWatch != watch)
            {
                return;
            }

            _clickWatch = null;
        }

        bool nonCollapsed;
        try
        {
            nonCollapsed = await _observerLane.RunAsync(
                () => ClickSelectionPrecheck.HasNonCollapsedSelection(watch.ProcessId),
                targetKey: $"pid:{watch.ProcessId}",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            nonCollapsed = false;    // 观察者 lane 不可用：按无变化处理
        }

        lock (_gate)
        {
            if (!_running || watch.Epoch != _epoch)
            {
                return;
            }

            if (!nonCollapsed)
            {
                _gestureDrops[GestureDropReason.ClickSelectionNoChange] =
                    _gestureDrops.TryGetValue(GestureDropReason.ClickSelectionNoChange, out var n) ? n + 1 : 1;
                return;
            }

            OnGestureDetected(this, new GestureDetectedEventArgs
            {
                Epoch = watch.Epoch,
                Gesture = SelectionGesture.ClickSelection,
                TargetProcessId = watch.ProcessId,
                TargetWindowHandle = watch.WindowHandle,
                DownPoint = watch.Down,
                UpPoint = watch.Up,
            });
        }
    }

    private async Task PushContentStreamAsync(long epoch, List<Action<SelectionContentChangedEventArgs>> subscribers)
    {
        try
        {
            var result = await CaptureCurrentSelectionAsync(fallbackAnchor: null, CancellationToken.None).ConfigureAwait(false);
            if (result.Success && result.Capture != null)
            {
                var args = new SelectionContentChangedEventArgs(result.Capture);
                foreach (var subscriber in subscribers)
                {
                    subscriber(args);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _streamInFlight = 0;
            }
        }
    }

    private void OnUaSignal(long epoch, Uia.UaSignalKind kind)
        => EnqueueUaSignal(epoch, kind == Uia.UaSignalKind.TextSelectionChanged ? UaSignalKind.TextSelectionChanged : UaSignalKind.FocusChanged);

    // —— 手势管线 ——

    private void OnGestureDetected(object? sender, GestureDetectedEventArgs args)
    {
        PipelineState? toProcess;
        lock (_gate)
        {
            if (!_running || args.Epoch != _epoch)
            {
                return;    // 旧 epoch / 停机后的手势：静默丢弃
            }

            var context = new GesturePolicyContext(args.Gesture, args.TargetProcessId, args.TargetWindowHandle, _options, _consumerWindows);
            var filter = _policy.FilterGesture(context);
            if (filter != PolicyFilterReason.None)
            {
                // 静默过滤：不产生 generation（无洞不变式）、不发候选/失败事件（§4）。
                _filteredGestures++;
                return;
            }

            _clickWatch = null;    // 真手势优先：清除点击布防（防双击第二击的选区事件重复触发）

            // 新手势终结旧生命周期：在飞 → Superseded；已完成捕获 → Invalidated(NewSelection)。
            if (_inFlight is { } prev && !prev.TerminalPublished)
            {
                MarkTerminal(prev);
                _superseded++;
                RaiseEvent(SelectionSuperseded, new SelectionSupersededEventArgs(prev.Generation, prev.Gesture));
                RaiseDiagnostics(SelectionPipelineStage.Superseded, prev);
            }

            if (_lastCapture?.Generation is { } lastGen && !_lastCaptureInvalidated)
            {
                _lastCaptureInvalidated = true;
                RaiseInvalidated(lastGen, SelectionInvalidationReason.NewSelection);
            }

            var state = new PipelineState
            {
                Epoch = _epoch,
                Generation = new SelectionGeneration(++_nextGeneration),
                Gesture = args.Gesture,
                Target = new CandidateTargetSnapshot
                {
                    ProcessId = args.TargetProcessId,
                    WindowHandle = args.TargetWindowHandle,
                    WindowRect = args.WindowRect,
                    PointerPoint = args.UpPoint,
                    ObservedAt = _time.GetUtcNow(),
                },
                Down = args.DownPoint,
                Up = args.UpPoint,
                Cts = new CancellationTokenSource(),
                StartTimestamp = _time.GetTimestamp(),
                OptionsSnapshot = _options with { },    // 手势时刻快照，后续 ApplyOptions 不影响在飞请求
            };
            _inFlight = state;
            toProcess = state;

            RaiseDiagnostics(SelectionPipelineStage.CandidateStarted, state);
            RaiseDiagnostics(SelectionPipelineStage.PolicyChecked, state);

            var candidate = new SelectionCandidateReady
            {
                Generation = state.Generation,
                Gesture = args.Gesture,
                Target = state.Target,
                ProvisionalAnchor = args.UpPoint,
            };
            RaiseEvent(SelectionCandidateReady, new SelectionCandidateReadyEventArgs(candidate));
            _candidatesPublished++;
            RaisePhase(SelectionPipelineStage.CandidateStarted, state.Generation);
        }

        _ = ProcessGestureReadAsync(toProcess);
    }

    /// <summary>打断动作：在飞候选 → Failed(Interrupted)；捕获完成后 Esc → Invalidated(Escape)；其余打断不影响已完成捕获。</summary>
    private void OnInterruptDetected(object? sender, InterruptDetectedEventArgs args)
    {
        lock (_gate)
        {
            if (!_running || args.Epoch != _epoch)
            {
                return;
            }

            if (_inFlight is { } state && IsCurrentInFlight(state))
            {
                TerminateInFlight(state);
                RaiseSelectionFailed(state, CaptureFailureReason.Interrupted);
                return;
            }

            if (args.Kind == InputInterruptKind.Escape && _lastCapture?.Generation is { } generation && !_lastCaptureInvalidated)
            {
                InvalidateLiveCapture(generation, SelectionInvalidationReason.Escape);
            }
        }
    }

    /// <summary>普通单击（无手势）：捕获完成后 → Invalidated(OutsideClick)（消费者豁免窗口除外）；
    /// 同时为点击型选区变化布防（ClickSelection：Word 行首选行等，v1.1）。</summary>
    private void OnPlainClickObserved(object? sender, PlainClickEventArgs args)
    {
        lock (_gate)
        {
            if (!_running || args.Epoch != _epoch)
            {
                return;
            }

            bool consumerWindow = _consumerWindows.Contains(args.Click.Foreground.WindowHandle);
            if (_lastCapture?.Generation is { } generation && !_lastCaptureInvalidated && !consumerWindow)
            {
                InvalidateLiveCapture(generation, SelectionInvalidationReason.OutsideClick);
            }

            if (_options.ClickSelectionEnabled && !consumerWindow && args.Click.Foreground.ProcessId != Environment.ProcessId)
            {
                _clickWatch = new ClickWatch(
                    _time.GetTimestamp(),
                    args.Epoch,
                    args.Click.Foreground.ProcessId,
                    args.Click.Foreground.WindowHandle,
                    args.Click.DownPoint,
                    args.Click.UpPoint);
            }
        }
    }

    /// <summary>前台变化：更新焦点目标；捕获完成后 PID 变化 → ForegroundChanged、原窗口消亡 → TargetGone（消费者窗口豁免）。</summary>
    private void OnForegroundChanged(ForegroundTargetSnapshot foreground)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _focusTarget = BuildFocusTarget(foreground);
            RaiseEvent(FocusTargetChanged, new FocusTargetEventArgs(_focusTarget));

            if (_lastCapture?.Generation is { } generation && !_lastCaptureInvalidated)
            {
                if (_consumerWindows.Contains(foreground.WindowHandle))
                {
                    return;    // 豁免：点工具条/拖工具条不自我打断
                }

                if (foreground.ProcessId != _lastCapture.Target.ProcessId)
                {
                    InvalidateLiveCapture(generation, SelectionInvalidationReason.ForegroundChanged);
                }
                else if (_lastCapture.Target.WindowHandle != 0 && !PInvoke.IsWindow(new HWND(_lastCapture.Target.WindowHandle)))
                {
                    InvalidateLiveCapture(generation, SelectionInvalidationReason.TargetGone);
                }
            }
        }
    }

    private void InvalidateLiveCapture(SelectionGeneration generation, SelectionInvalidationReason reason)
    {
        _lastCaptureInvalidated = true;
        RaiseInvalidated(generation, reason);
    }

    private static TargetContext BuildFocusTarget(ForegroundTargetSnapshot foreground)
    {
        var className = string.Empty;
        if (foreground.WindowHandle != 0)
        {
            Span<char> buffer = stackalloc char[256];
            var length = PInvoke.GetClassName(new HWND(foreground.WindowHandle), buffer);
            if (length > 0)
            {
                className = new string(buffer[..length]);
            }
        }

        return new TargetContext
        {
            ProcessId = foreground.ProcessId,
            ProcessName = DefaultTargetPolicy.ResolveProcessName(foreground.ProcessId) ?? string.Empty,
            WindowHandle = foreground.WindowHandle,
            WindowClassName = className,
        };
    }

    private void OnGestureDropped(object? sender, GestureDroppedEventArgs args)
    {
        lock (_gate)
        {
            _gestureDrops[args.Reason] = _gestureDrops.TryGetValue(args.Reason, out var n) ? n + 1 : 1;
        }
    }

    private async Task ProcessGestureReadAsync(PipelineState state)
    {
        BackendReadResult result;
        var request = new BackendReadRequest
        {
            Epoch = state.Epoch,
            Generation = state.Generation,
            Origin = CaptureOrigin.Gesture,
            Gesture = state.Gesture,
            Target = state.Target,
            DownPoint = state.Down,
            UpPoint = state.Up,
            Options = state.OptionsSnapshot,
        };

        var budget = CancellationTokenSource.CreateLinkedTokenSource(state.Cts.Token);
        try
        {
            RaiseDiagnostics(SelectionPipelineStage.BackendStarted, state);
            var readTask = _backend.ReadAsync(request, budget.Token);

            // 候选外预算（500ms）：读+settle 未按期完成 → Failed(IncompleteTimeout)，迟到结果丢弃。
            var deadline = Task.Delay(state.OptionsSnapshot.IncompleteTimeout, _time, budget.Token);
            var winner = await Task.WhenAny(readTask, deadline).ConfigureAwait(false);
            if (winner == deadline)
            {
                lock (_gate)
                {
                    if (IsCurrentInFlight(state))
                    {
                        TerminateInFlight(state);
                        RaiseSelectionFailed(state, CaptureFailureReason.IncompleteTimeout);
                    }
                }

                budget.Cancel();    // 放弃等待而非中止 COM：在飞调用任其自然完成，结果按 epoch/generation 丢弃
                return;
            }

            result = await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;    // 终止事件（Cancelled/Superseded/Interrupted）已在锁内先行发出，迟到结果丢弃
        }
        catch (Exception)
        {
            result = new BackendReadResult { Success = false, Failure = CaptureFailureReason.BackendUnavailable };
        }
        finally
        {
            budget.Dispose();
        }

        lock (_gate)
        {
            if (!IsCurrentInFlight(state))
            {
                return;    // 已终结 / 被取代 / 跨 epoch 迟到完成（ADR-0002）
            }

            TerminateInFlight(state);
            var elapsed = _time.GetElapsedTime(state.StartTimestamp);
            RaiseDiagnostics(SelectionPipelineStage.BackendFinished, state, elapsed: elapsed);
            RaiseDiagnostics(SelectionPipelineStage.AnchorResolved, state, elapsed: elapsed);

            if (result.Success)
            {
                var capture = BuildCapture(result, state.Generation, requestId: null, CaptureOrigin.Gesture, state.Gesture, state.Down, state.Up, elapsed);
                _lastCapture = capture;
                _lastCaptureInvalidated = false;
                _capturesSucceeded++;
                RaiseEvent(SelectionCaptured, new SelectionCapturedEventArgs(capture));
                RaiseDiagnostics(SelectionPipelineStage.Published, state, elapsed: elapsed, backend: result.Backend);
            }
            else
            {
                RaiseSelectionFailed(state, result.Failure, elapsed);
            }
        }
    }

    private void RaiseSelectionFailed(PipelineState state, CaptureFailureReason reason, TimeSpan? elapsed = null)
    {
        var span = elapsed ?? _time.GetElapsedTime(state.StartTimestamp);
        _capturesFailed++;
        _failuresByReason[reason] = _failuresByReason.TryGetValue(reason, out var n) ? n + 1 : 1;
        RaiseEvent(SelectionFailed, new SelectionFailedEventArgs(state.Generation, null, state.Gesture, reason, span));
        RaiseDiagnostics(SelectionPipelineStage.Failed, state, elapsed: span, reason: reason);
    }

    private static void MarkTerminal(PipelineState state) => state.TerminalPublished = true;

    /// <summary>在飞项仍是当前管线所有者（未终结、未被取代、epoch 未翻篇）。</summary>
    private bool IsCurrentInFlight(PipelineState state) =>
        _running && state.Epoch == _epoch && !state.TerminalPublished && ReferenceEquals(_inFlight, state);

    private void TerminateInFlight(PipelineState state)
    {
        MarkTerminal(state);
        _inFlight = null;
        state.Cts.Cancel();
    }

    private static SelectionCapture BuildCapture(
        BackendReadResult result,
        SelectionGeneration? generation,
        SelectionRequestId? requestId,
        CaptureOrigin origin,
        SelectionGesture? gesture,
        PhysicalScreenPoint? down,
        PhysicalScreenPoint? up,
        TimeSpan elapsed) => new()
        {
            Generation = generation,
            RequestId = requestId,
            Origin = origin,
            Gesture = gesture,
            Content = result.Content,
            Geometry = result.Geometry,
            Target = result.Target,
            MouseDownPoint = down,
            MouseUpPoint = up,
            AnchorRect = result.AnchorRect,
            AnchorSource = result.AnchorSource,
            Backend = result.Backend,
            Freshness = result.Freshness,
            Elapsed = elapsed,
        };

    private void RaiseInvalidated(SelectionGeneration generation, SelectionInvalidationReason reason)
    {
        _invalidated++;
        _invalidationsByReason[reason] = _invalidationsByReason.TryGetValue(reason, out var n) ? n + 1 : 1;
        RaiseEvent(SelectionInvalidated, new SelectionInvalidatedEventArgs(generation, reason));
    }

    private void RaiseEvent<TArgs>(EventHandler<TArgs>? handlers, TArgs args) where TArgs : EventArgs
    {
        if (handlers == null)
        {
            return;
        }

        var context = _options.EventContext;
        if (context != null)
        {
            context.Post(static s =>
            {
                var (h, a) = ((EventHandler<TArgs> Handler, TArgs Args))s!;
                h(null, a);
            }, (handlers, args));
        }
        else
        {
            handlers(null, args);
        }
    }

    private void RaiseDiagnostics(
        SelectionPipelineStage stage,
        PipelineState? state,
        TimeSpan? elapsed = null,
        CaptureFailureReason? reason = null,
        CaptureBackend? backend = null)
    {
        if (Diagnostics == null)
        {
            return;
        }

        var args = new SelectionDiagnosticsEventArgs
        {
            Stage = stage,
            Generation = state?.Generation,
            RequestId = null,
            Gesture = state?.Gesture,
            FailureReason = reason,
            Backend = backend,
            Elapsed = elapsed ?? TimeSpan.Zero,
            Timestamp = _time.GetUtcNow(),
        };
        RaiseEvent(Diagnostics, args);
    }

    private void RaisePhase(SelectionPipelineStage phase, SelectionGeneration generation)
    {
        if (GesturePhaseChanged == null)
        {
            return;
        }

        RaiseEvent(GesturePhaseChanged, new GesturePhaseEventArgs(phase, generation));
    }

    private sealed class PipelineState
    {
        public long Epoch { get; init; }
        public SelectionGeneration Generation { get; init; }
        public SelectionGesture Gesture { get; init; }
        public CandidateTargetSnapshot Target { get; init; } = new();
        public PhysicalScreenPoint? Down { get; init; }
        public PhysicalScreenPoint? Up { get; init; }
        public CancellationTokenSource Cts { get; init; } = new();
        public long StartTimestamp { get; init; }
        public SelectionPickerOptions OptionsSnapshot { get; init; } = new();
        public bool TerminalPublished { get; set; }
    }

    private sealed class ConsumerWindowToken : IDisposable
    {
        private SelectionPicker? _owner;
        private readonly nint _window;

        public ConsumerWindowToken(SelectionPicker owner, nint window)
        {
            _owner = owner;
            _window = window;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
            {
                lock (owner._gate)
                {
                    owner._consumerWindows.Remove(_window);
                }
            }
        }
    }

    /// <summary>观察者 lane seam（caret/state/probe/内容流共用；ADR-0003 Lane 3）。</summary>
    internal interface IObserverLane : IDisposable
    {
        Task<T> RunAsync<T>(Func<T> work, string? targetKey, CancellationToken ct);
    }

    private sealed class QueryRunnerObserverLane : IObserverLane
    {
        private readonly Execution.QueryRunner _runner = new(TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(2));

        public void ApplyTimeouts(TimeSpan queryTimeout, TimeSpan circuitCooldown)
            => _runner.ApplyTimeouts(queryTimeout, circuitCooldown);

        public async Task<T> RunAsync<T>(Func<T> work, string? targetKey, CancellationToken ct)
        {
            var outcome = await Task.Run(() => _runner.Run(() => work()!, targetKey, ct), CancellationToken.None).ConfigureAwait(false);
            return outcome.Outcome == Execution.QueryOutcome.Completed
                ? (T)outcome.Value!
                : throw new InvalidOperationException($"observer lane unavailable: {outcome.Outcome}");
        }

        public void Dispose() => _runner.Dispose();
    }

    private sealed class ContentSubscription : IDisposable
    {
        private SelectionPicker? _owner;
        private readonly Action<SelectionContentChangedEventArgs> _handler;

        public ContentSubscription(SelectionPicker owner, Action<SelectionContentChangedEventArgs> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner != null)
            {
                lock (owner._gate)
                {
                    owner._contentSubscribers.Remove(_handler);
                }
            }
        }
    }

    private sealed class UnavailableBackend : ISelectionBackend
    {
        public Task<BackendReadResult> ReadAsync(BackendReadRequest request, CancellationToken ct)
            => Task.FromResult(new BackendReadResult { Success = false, Failure = CaptureFailureReason.BackendUnavailable });
    }
}
