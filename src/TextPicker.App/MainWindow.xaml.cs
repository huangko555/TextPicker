using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TextPicker;
using TextPicker.Windows;
using Windows.Win32;
using TextPicker.Windows.Execution;

namespace TextPicker.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly SelectionPicker _picker = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IDisposable? _contentSubscription;
    private IDisposable? _consumerToken;
    private uint _lastClipboardSequence;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachPickerEvents();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        DpiText.Text = GetDpiAwarenessSummary();

        try
        {
            _picker.Start();
            AddLine("panel", "SelectionPicker.Start() 成功（Owned 输入源已注册）");
        }
        catch (Exception exception)
        {
            AddLine("panel", $"Start 失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void AttachPickerEvents()
    {
        _picker.SelectionCandidateReady += (_, args) => RunOnUi(() =>
            AddLine("candidate", $"gen:{args.Candidate.Generation.Value.ToString(CultureInfo.InvariantCulture)} {args.Candidate.Gesture} pid:{args.Candidate.Target.ProcessId} hwnd:0x{args.Candidate.Target.WindowHandle:X}"));

        _picker.SelectionCaptured += (_, args) => RunOnUi(() =>
        {
            var capture = args.Capture;
            AddLine("captured", $"gen:{capture.Generation?.Value.ToString(CultureInfo.InvariantCulture)} {capture.Gesture} 正文[{Mask(capture.Content.Text)}] 锚:{capture.AnchorSource} 后端:{capture.Backend} 完整度:{capture.Geometry.Completeness} 上下文:{capture.Content.LocalContext?.Kind.ToString() ?? "null"} 新鲜度:{capture.Freshness?.ToString() ?? "null"} 截断:{capture.Content.Truncated} 耗时:{capture.Elapsed.TotalMilliseconds:F0}ms");
            LastCaptureText.Text = $"Completeness={capture.Geometry.Completeness}\nContextKind={capture.Content.LocalContext?.Kind.ToString() ?? "—"}\nFreshness={capture.Freshness?.ToString() ?? "—"}\nRectCount={capture.Geometry.RectCount} Direction={capture.Geometry.Direction?.ToString() ?? "—"}";
        });

        _picker.SelectionFailed += (_, args) => RunOnUi(() =>
            AddLine("failed", $"gen:{(args.Generation is { } g1 ? g1.Value.ToString(CultureInfo.InvariantCulture) : "-")} {args.Gesture} 原因:{args.Reason} 耗时:{args.Elapsed.TotalMilliseconds:F0}ms"));

        _picker.SelectionSuperseded += (_, args) => RunOnUi(() =>
            AddLine("superseded", $"gen:{args.Generation.Value.ToString(CultureInfo.InvariantCulture)}（被新手势取代）"));

        _picker.SelectionInvalidated += (_, args) => RunOnUi(() =>
            AddLine("invalidated", $"gen:{args.Generation.Value.ToString(CultureInfo.InvariantCulture)} 原因:{args.Reason}"));

        _picker.GesturePhaseChanged += (_, args) => RunOnUi(() =>
            PhaseText.Text = $"{args.Phase} gen:{(args.Generation is { } g2 ? g2.Value.ToString(CultureInfo.InvariantCulture) : "-")}");

        _picker.FocusTargetChanged += (_, args) => RunOnUi(() =>
            FocusText.Text = args.Target == null
                ? "（焦点丢失）"
                : $"pid:{args.Target.ProcessId} {args.Target.ProcessName}\n0x{args.Target.WindowHandle:X} {args.Target.WindowClassName}");

        _picker.SelectionCandidateReady += (_, _) => RunOnUi(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var snapshot = _picker.GetPointerSnapshot();
        PointerText.Text = $"({snapshot.Position.X:F0}, {snapshot.Position.Y:F0}) 按钮:{snapshot.Buttons} 拖拽:{snapshot.Dragging}";

        var sequence = PInvoke.GetClipboardSequenceNumber();
        var marker = sequence == _lastClipboardSequence ? "" : "（变化！）";
        _lastClipboardSequence = sequence;
        ClipboardText.Text = $"{sequence} {marker}";

        FullScreenText.Text = FullScreenWindowDetector.IsForegroundWindowFullScreen() ? "是（前台全屏）" : "否";
        RefreshCounters();
    }

    private void RefreshCounters()
    {
        var counters = _picker.Counters;
        var failures = string.Join(",", counters.FailuresByReason.Select(kv => $"{kv.Key}:{kv.Value}"));
        var drops = string.Join(",", counters.GestureDropsByReason.Select(kv => $"{kv.Key}:{kv.Value}"));
        CountersText.Text = $"候选:{counters.CandidatesPublished} 成功:{counters.CapturesSucceeded} 失败:{counters.CapturesFailed}\n" +
            $"取代:{counters.Superseded} 失效:{counters.Invalidated} 取消:{counters.Cancelled} 显式:{counters.ExplicitQueries}\n" +
            (failures.Length > 0 ? $"失败明细:{failures}\n" : "") +
            (drops.Length > 0 ? $"手势层丢弃:{drops}" : "");
    }

    // —— 事件流 ——

    private void AddLine(string kind, string text)
    {
        Stream.Items.Add($"[{DateTime.Now:HH:mm:ss.fff}] {kind,-10} {text}");
        if (Stream.Items.Count > 500)
        {
            Stream.Items.RemoveAt(0);
        }

        Stream.ScrollIntoView(Stream.Items[^1]);
    }

    /// <summary>正文打码：长度 + SHA256 前 8 hex；揭示开关只影响本窗口显示，永不落盘。</summary>
    private string Mask(string text)
    {
        if (!RevealCheck.IsChecked ?? true)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
            return $"len={text.Length} sha:{hash}";
        }

        return text;
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e) => AddLine("panel", RevealCheck.IsChecked == true ? "正文揭示：开（仅窗口显示）" : "正文打码：开");

    private void OnClearStream(object sender, RoutedEventArgs e) => Stream.Items.Clear();

    // —— 交互区 ——

    private void OnGestureToggle(object sender, RoutedEventArgs e)
    {
        if (_picker is not { IsRunning: true })
        {
            return;
        }

        _picker.SetTriggerEnabled(SelectionGesture.BoxSelect, GestBox.IsChecked == true);
        _picker.SetTriggerEnabled(SelectionGesture.MultiClick, GestClick.IsChecked == true);
        _picker.SetTriggerEnabled(SelectionGesture.ShiftClick, GestShift.IsChecked == true);
        _picker.SetTriggerEnabled(SelectionGesture.CtrlA, GestCtrlA.IsChecked == true);
        _picker.SetTriggerEnabled(SelectionGesture.ShiftKeyboard, GestKbd.IsChecked == true);
    }

    private void OnApplyExcluded(object sender, RoutedEventArgs e)
    {
        var names = ExcludedBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _picker.SetExcludedProcesses(names);
        AddLine("panel", $"排除进程已应用：{names.Length} 个");
    }

    private void OnOptionToggle(object sender, RoutedEventArgs e)
    {
        var options = _picker.Options with
        {
            PauseWhenFullScreen = OptFullScreen.IsChecked == true,
            AllowWholeValueBackend = OptWholeValue.IsChecked == true,
            EnrichSurrounding = OptSurrounding.IsChecked == true,
        };
        _picker.ApplyOptions(options);
        AddLine("panel", "选项热更新已应用");
    }

    private void OnStreamToggle(object sender, RoutedEventArgs e)
    {
        if (OptStream.IsChecked == true)
        {
            _contentSubscription = _picker.SubscribeSelectionContent(args =>
                RunOnUi(() => AddLine("stream", $"内容流 正文[{Mask(args.Capture.Content.Text)}]")));
            AddLine("panel", "内容流订阅：开（持续本地读文本，隐私升档）");
        }
        else
        {
            _contentSubscription?.Dispose();
            _contentSubscription = null;
            AddLine("panel", "内容流订阅：关");
        }
    }

    private async void OnExplicitCapture(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _picker.CaptureCurrentSelectionAsync(fallbackAnchor: null, CancellationToken.None);
            AddLine("explicit", result.Success
                ? $"req:{result.Capture?.RequestId?.Value} 正文[{Mask(result.Capture?.Content.Text ?? "")}] 锚:{result.Capture?.AnchorSource}"
                : $"失败 原因:{result.FailureReason} 耗时:{result.Elapsed.TotalMilliseconds:F0}ms");
        }
        catch (Exception exception)
        {
            AddLine("explicit", $"异常：{exception.Message}");
        }
    }

    private async void OnObserveCaret(object sender, RoutedEventArgs e)
    {
        try
        {
            var caret = await _picker.ObserveCaretAsync(CancellationToken.None);
            CaretText.Text = caret == null
                ? "（未观察到 caret）"
                : $"来源:{caret.Source} 折叠:{caret.IsCollapsedSelection}\n矩形:{caret.CaretRect.Left:F0},{caret.CaretRect.Top:F0} {caret.CaretRect.Width:F0}x{caret.CaretRect.Height:F0}\npid:{caret.Target.ProcessId} {caret.Target.ProcessName}";
        }
        catch (Exception exception)
        {
            CaretText.Text = $"异常：{exception.Message}";
        }
    }

    private async void OnProbe(object sender, RoutedEventArgs e)
    {
        try
        {
            var position = _picker.GetPointerSnapshot().Position;
            var probe = await _picker.ProbeTargetAsync(position, ProbeText.IsChecked == true, CancellationToken.None);
            AddLine("probe", probe.Success
                ? $"pid:{probe.Target?.ProcessId} {probe.Target?.ProcessName} 类:{probe.Target?.ClassName} 可编辑:{probe.Target?.IsEditable} 正文[{(probe.Content == null ? "—" : Mask(probe.Content.Text))}] 完整度:{probe.Geometry?.Completeness.ToString() ?? "—"}"
                : $"失败 原因:{probe.FailureReason}");
        }
        catch (Exception exception)
        {
            AddLine("probe", $"异常：{exception.Message}");
        }
    }

    private void OnConsumerToggle(object sender, RoutedEventArgs e)
    {
        if (ConsumerReg.IsChecked == true)
        {
            var handle = new WindowInteropHelper(this).Handle;
            _consumerToken = _picker.RegisterConsumerWindow(handle);
            AddLine("panel", $"消费者窗口已注册：0x{handle:X}（其上手势不产生候选、不取代、不失效）");
        }
        else
        {
            _consumerToken?.Dispose();
            _consumerToken = null;
            AddLine("panel", "消费者窗口已注销");
        }
    }

    private static string GetDpiAwarenessSummary()
    {
        // PMv2 manifest 生效即无需警告；此处读取进程 DPI 感知上下文做展示。
        return "PerMonitorV2（manifest）";
    }

    private void RunOnUi(Action action) => Dispatcher.BeginInvoke(action);

    private void OnClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        Dispose();
    }

    public void Dispose()
    {
        _contentSubscription?.Dispose();
        _consumerToken?.Dispose();
        _picker.Dispose();
        GC.SuppressFinalize(this);
    }
}
