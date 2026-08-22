using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;

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

    /// <summary>生产入口：默认装配 Owned 输入源 + WinEvent 焦点源（ADR-0001；Owned 启动 fail-fast）。</summary>
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
        IFocusTargetSource? focusSource = null)
    {
        _feed = gestureFeed ?? new CoreGestureFeed(inputSource ?? new OwnedRawInputSource());
        _backend = backend ?? new UnavailableBackend();
        _policy = policy ?? DefaultTargetPolicy.Instance;
        _focusSource = focusSource ?? new WinEventFocusTargetSource();
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
            foreach (var cts in _pendingRequests.Values)
            {
                cts.Cancel();
            }

            _focusSource.Stop();
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
        => throw new NotSupportedException("ProbeTarget lands in Phase 3 (UIA backend).");

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
        => throw new NotSupportedException("Selection content stream lands in Phase 2/3.");

    // —— C. 指针/光标 ——

    public PointerSnapshot GetPointerSnapshot()
    {
        // Phase 2：由 IInputEventSource 指针流填充（位置/按键/按下抬起点/拖拽中）。
        return new PointerSnapshot { ObservedAt = _time.GetUtcNow() };
    }

    public event EventHandler<GesturePhaseEventArgs>? GesturePhaseChanged;

    // —— D. 插入光标（Phase 3）——

    public Task<CaretObservation?> ObserveCaretAsync(CancellationToken ct)
        => throw new NotSupportedException("Caret observation lands in Phase 3 (CaretProbeChain).");

    public Task<SelectionState?> ObserveSelectionStateAsync(CancellationToken ct)
        => throw new NotSupportedException("Selection state observation lands in Phase 3.");

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

    // CS0067：CaretChanged 由 Phase 3 的 CaretProbeChain 驱动；FocusTargetChanged 已在 OnForegroundChanged 发布。
#pragma warning disable CS0067
    public event EventHandler<CaretEventArgs>? CaretChanged;
#pragma warning restore CS0067

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
            _options = options;

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
        lock (_gate)
        {
            if (!_running || epoch != _epoch)
            {
                return;    // 旧 epoch 迟到回调：不产生任何公开事件（ADR-0002）
            }

            // Phase 2：驱动内容流节拍与键盘新鲜度信号（settle 轮询换事件等待）。
        }
    }

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

    /// <summary>普通单击（无手势）：捕获完成后 → Invalidated(OutsideClick)；消费者豁免窗口上的单击不失效。</summary>
    private void OnPlainClickObserved(object? sender, PlainClickEventArgs args)
    {
        lock (_gate)
        {
            if (!_running || args.Epoch != _epoch)
            {
                return;
            }

            if (_lastCapture?.Generation is { } generation && !_lastCaptureInvalidated && !_consumerWindows.Contains(args.Click.Foreground.WindowHandle))
            {
                InvalidateLiveCapture(generation, SelectionInvalidationReason.OutsideClick);
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

    private sealed class UnavailableBackend : ISelectionBackend
    {
        public Task<BackendReadResult> ReadAsync(BackendReadRequest request, CancellationToken ct)
            => Task.FromResult(new BackendReadResult { Success = false, Failure = CaptureFailureReason.BackendUnavailable });
    }
}
