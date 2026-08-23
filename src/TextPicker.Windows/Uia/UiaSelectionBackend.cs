using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace TextPicker.Windows.Uia;

/// <summary>
/// UIA 读取后端（ADR-0005 冻结顺序）：PID 判序 → ElementFromPoint + 焦点元素 → 密码双查 → 父链找 TextPattern
/// → GetSelection（多 range 拒绝）→ GetText(Max+1) 截断检测 → LocalContext（ContextKind 标注退化）
/// → 几何（SAFEARRAY + 端点折叠）→ 方向（GetCaretRange 权威）→ 锚点链（ADR-0004）。
/// 同步执行于 lane 的 MTA 线程（LaneRoutedBackend.reader）；COM 对象不出线程。
/// </summary>
internal sealed class UiaSelectionBackend
{
    private readonly Func<TimeSpan, bool>? _waitForSelectionSignal;    // 新鲜度信号等待（键盘手势；事件源注入）
    private readonly TimeProvider _time;

    public UiaSelectionBackend(Func<TimeSpan, bool>? waitForSelectionSignal = null, TimeProvider? timeProvider = null)
    {
        _waitForSelectionSignal = waitForSelectionSignal;
        _time = timeProvider ?? TimeProvider.System;
    }

    public BackendReadResult Read(BackendReadRequest request, CancellationToken ct)
    {
        SelectionFreshnessEvidence? freshness = request.Gesture is SelectionGesture.CtrlA or SelectionGesture.ShiftKeyboard
            ? WaitForFreshness(request)
            : (SelectionFreshnessEvidence?)null;

        IUIAutomationElement? hitElement = null;
        IUIAutomationElement? focusedElement = null;
        IUIAutomationElement? patternOwner = null;
        bool ownerIsStart = false;
        IUIAutomationTextRangeArray? ranges = null;
        IUIAutomationTextRange? range = null;
        try
        {
            var automation = UiaCom.Automation;

            // 点查询前置：高完整性（管理员）目标在点查询上抛 E_ACCESSDENIED，先于焦点路径暴露真实原因（UIPI）。
            var point = request.UpPoint ?? request.Target.PointerPoint;
            if (point is { } screenPoint)
            {
                hitElement = automation.ElementFromPoint(new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y));
                if (hitElement != null && UiaCom.GetProcessId(hitElement) != request.Target.ProcessId)
                {
                    UiaCom.ReleaseComObject(hitElement);
                    hitElement = null;    // 点命中他进程：走焦点链
                }
            }

            focusedElement = automation.GetFocusedElement();

            // 密码双查（命中 + 焦点）。
            if ((hitElement != null && UiaCom.IsPassword(hitElement)) || (focusedElement != null && UiaCom.IsPassword(focusedElement)))
            {
                return Fail(CaptureFailureReason.PasswordField);
            }

            // PID 判序（手势路径）：读取时焦点 PID 必须等于按下快照 PID。
            int readTargetPid = request.Target.ProcessId;
            if (focusedElement != null)
            {
                readTargetPid = UiaCom.GetProcessId(focusedElement);
                if (request.Origin == CaptureOrigin.Gesture && readTargetPid != request.Target.ProcessId)
                {
                    return Fail(CaptureFailureReason.ProcessMismatch);
                }
            }

            // 焦点不可见（常见于高完整性前台）：降级用命中元素。
            var origin = hitElement ?? focusedElement;
            if (origin == null)
            {
                return Fail(CaptureFailureReason.BackendUnavailable);
            }

            var (textPattern, owner, ownerFromWalk) = UiaCom.FindTextPattern(automation, origin);
            patternOwner = owner;
            ownerIsStart = !ownerFromWalk;
            if (textPattern == null || patternOwner == null)
            {
                return Fail(CaptureFailureReason.BackendUnavailable);
            }

            ranges = textPattern.GetSelection();
            var rangeCount = ranges?.Length ?? 0;
            if (rangeCount == 0)
            {
                return Fail(CaptureFailureReason.EmptySelection);
            }

            if (rangeCount > 1)
            {
                return Fail(CaptureFailureReason.MultipleSelectionUnsupported);    // 绝不伪装部分结果
            }

            range = ranges!.GetElement(0);

            // GetText(Max+1) 截断检测。
            var max = request.Options.MaxTextLength;
            var rawText = range.GetText(max + 1).ToString();
            bool truncated = rawText.Length > max;
            if (truncated)
            {
                rawText = rawText[..max];
            }

            var normalized = TextNormalizer.Normalize(rawText, max);
            if (normalized == null)
            {
                return Fail(CaptureFailureReason.EmptyText);    // 滤全不可见后为空 → 取消发布
            }

            var localContext = ReadLocalContext(range, normalized.Text, max);
            var geometry = ReadGeometry(range);
            var caretEndpointRect = ReadCaretEndpoint(patternOwner, range, out var direction);
            var (anchorRect, anchorSource) = ResolveAnchor(caretEndpointRect, request);

            return new BackendReadResult
            {
                Success = true,
                Content = new SelectionContent
                {
                    Text = normalized.Text,
                    LocalContext = localContext,
                    ReturnedLength = normalized.Text.Length,
                    Truncated = truncated || normalized.Truncated,
                    OriginalLength = null,
                },
                Geometry = geometry,
                Target = UiaCom.Describe(patternOwner, readTargetPid),
                AnchorRect = anchorRect,
                AnchorSource = anchorSource,
                Backend = CaptureBackend.UiaTextPattern,
                Freshness = freshness,
            };
        }
        catch (COMException exception)
        {
            return Fail(exception.HResult == unchecked((int)0x80070005)
                ? CaptureFailureReason.AccessDenied
                : CaptureFailureReason.BackendUnavailable);
        }
        finally
        {
            UiaCom.ReleaseComObject(range);
            UiaCom.ReleaseComObject(ranges);
            if (!ownerIsStart)
            {
                UiaCom.ReleaseComObject(patternOwner);    // owner == origin 时不重复释放
            }

            UiaCom.ReleaseComObject(hitElement);
            UiaCom.ReleaseComObject(focusedElement);
        }
    }

    /// <summary>点查询探针（ProbeTargetAsync）：无正文或带正文的目标上下文读取。</summary>
    public static TargetProbeResult Probe(PhysicalScreenPoint point, bool includeText, CancellationToken ct)
    {
        IUIAutomationElement? hitElement = null;
        IUIAutomationElement? owner = null;
        IUIAutomationTextRangeArray? ranges = null;
        IUIAutomationTextRange? range = null;
        try
        {
            var automation = UiaCom.Automation;
            hitElement = automation.ElementFromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
            if (hitElement == null)
            {
                return new TargetProbeResult { Success = false, FailureReason = CaptureFailureReason.BackendUnavailable };
            }

            var pid = UiaCom.GetProcessId(hitElement);
            if (UiaCom.IsPassword(hitElement))
            {
                return new TargetProbeResult { Success = false, FailureReason = CaptureFailureReason.PasswordField, Target = UiaCom.Describe(hitElement, pid) };
            }

            var target = UiaCom.Describe(hitElement, pid);
            if (!includeText)
            {
                return new TargetProbeResult { Success = true, Target = target };
            }

            var (textPattern, patternOwner, ownerFromWalk) = UiaCom.FindTextPattern(automation, hitElement);
            owner = patternOwner;
            if (textPattern == null)
            {
                return new TargetProbeResult { Success = true, Target = target };    // 无 TextPattern：仅上下文
            }

            try
            {
                ranges = textPattern.GetSelection();
                if (ranges == null || ranges.Length == 0)
                {
                    return new TargetProbeResult { Success = true, Target = target };
                }

                range = ranges.GetElement(0);
                var rawText = range.GetText(4001).ToString();
                var normalized = TextNormalizer.Normalize(rawText, 4000);
                return new TargetProbeResult
                {
                    Success = true,
                    Target = target,
                    Content = normalized == null ? null : new SelectionContent { Text = normalized.Text, ReturnedLength = normalized.Text.Length, Truncated = normalized.Truncated },
                    Geometry = ReadGeometry(range),
                };
            }
            finally
            {
                if (!ownerFromWalk)
                {
                    UiaCom.ReleaseComObject(owner);    // owner==hitElement 时由外层释放
                }
            }
        }
        catch (COMException exception)
        {
            return new TargetProbeResult
            {
                Success = false,
                FailureReason = exception.HResult == unchecked((int)0x80070005) ? CaptureFailureReason.AccessDenied : CaptureFailureReason.BackendUnavailable,
            };
        }
        finally
        {
            UiaCom.ReleaseComObject(range);
            UiaCom.ReleaseComObject(ranges);
            UiaCom.ReleaseComObject(hitElement);
        }
    }

    /// <summary>选区状态观察（无正文，InputCue 合规入口）。</summary>
    public static SelectionState? ObserveSelectionState(CancellationToken ct)
    {
        IUIAutomationElement? focusedElement = null;
        IUIAutomationTextRangeArray? ranges = null;
        IUIAutomationTextRange? range = null;
        IUIAutomationElement? owner = null;
        try
        {
            var automation = UiaCom.Automation;
            focusedElement = automation.GetFocusedElement();
            if (focusedElement == null)
            {
                return null;
            }

            var pid = UiaCom.GetProcessId(focusedElement);
            var (textPattern, patternOwner, ownerFromWalk) = UiaCom.FindTextPattern(automation, focusedElement);
            owner = patternOwner;
            if (textPattern == null)
            {
                return null;
            }

            try
            {
                ranges = textPattern.GetSelection();
                if (ranges == null || ranges.Length == 0)
                {
                    return new SelectionState { HasNonCollapsedSelection = false, Target = UiaCom.Describe(patternOwner!, pid) };
                }

                range = ranges.GetElement(0);
                bool collapsed = IsCollapsed(range);
                return new SelectionState
                {
                    HasNonCollapsedSelection = !collapsed,
                    Geometry = ReadGeometry(range),
                    Target = UiaCom.Describe(patternOwner!, pid),
                };
            }
            finally
            {
                if (!ownerFromWalk)
                {
                    UiaCom.ReleaseComObject(owner);
                }
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            UiaCom.ReleaseComObject(range);
            UiaCom.ReleaseComObject(ranges);
            UiaCom.ReleaseComObject(focusedElement);
        }
    }

    private static bool IsCollapsed(IUIAutomationTextRange range)
    {
        try
        {
            var startToEnd = range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            return startToEnd == 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static BackendReadResult Fail(CaptureFailureReason reason) => new() { Success = false, Failure = reason };

    private SelectionFreshnessEvidence WaitForFreshness(BackendReadRequest request)
    {
        var deadline = request.Options.SettleDeadline;
        if (_waitForSelectionSignal == null || deadline <= TimeSpan.Zero)
        {
            return SelectionFreshnessEvidence.SettledFallback;
        }

        return _waitForSelectionSignal(deadline)
            ? SelectionFreshnessEvidence.SelectionChangedEvent
            : SelectionFreshnessEvidence.SettledFallback;
    }

    private static LocalTextContext? ReadLocalContext(IUIAutomationTextRange range, string normalizedText, int max)
    {
        IUIAutomationTextRange? clone = null;
        try
        {
            clone = range.Clone();
            clone.ExpandToEnclosingUnit(TextUnit.TextUnit_Paragraph);
            var contextRaw = clone.GetText(max + 1).ToString();
            bool contextTruncated = contextRaw.Length > max;
            if (contextTruncated)
            {
                contextRaw = contextRaw[..max];
            }

            var kind = contextTruncated ? ContextKind.BestEffort : ContextKind.Paragraph;    // 退化单位标注
            return TextNormalizer.BuildLocalContext(contextRaw, kind, normalizedText, max);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            UiaCom.ReleaseComObject(clone);
        }
    }

    private static SelectionGeometry ReadGeometry(IUIAutomationTextRange range)
    {
        double[] rects;
        PhysicalScreenRect? startRect;
        PhysicalScreenRect? endRect;
        unsafe
        {
            rects = UiaCom.ReadRectangles(range.GetBoundingRectangles());
        }

        startRect = CollapseAndMeasure(range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
        endRect = CollapseAndMeasure(range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
        return GeometryBuilder.TryBuild(rects, startRect, endRect, direction: null)
            ?? new SelectionGeometry { Completeness = GeometryCompleteness.None };
    }

    private static PhysicalScreenRect? CollapseAndMeasure(IUIAutomationTextRange range, TextPatternRangeEndpoint endpoint)
    {
        IUIAutomationTextRange? clone = null;
        try
        {
            clone = range.Clone();
            var other = endpoint == TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start
                ? TextPatternRangeEndpoint.TextPatternRangeEndpoint_End
                : TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start;
            clone.MoveEndpointByRange(other, clone, endpoint);    // 折叠到指定端点
            double[] rects;
            unsafe
            {
                rects = UiaCom.ReadRectangles(clone.GetBoundingRectangles());
            }

            if (rects.Length < 4)
            {
                return null;
            }

            return new PhysicalScreenRect(
                rects[0],
                rects[1],
                rects[0] + (rects[2] <= 0 ? 1 : rects[2]),
                rects[1] + (rects[3] <= 0 ? 1 : rects[3]));
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            UiaCom.ReleaseComObject(clone);
        }
    }

    /// <summary>方向权威源：TextPattern2.GetCaretRange + 与选区 Start/End CompareEndpoints（只用正负号）。</summary>
    private static PhysicalScreenRect? ReadCaretEndpoint(IUIAutomationElement owner, IUIAutomationTextRange selection, out SelectionDirection? direction)
    {
        direction = null;
        object? patternObject = null;
        IUIAutomationTextRange? caretRange = null;
        try
        {
            var pattern2Id = typeof(IUIAutomationTextPattern2).GUID;
            patternObject = owner.GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPattern2Id, in pattern2Id);
            if (patternObject is not IUIAutomationTextPattern2 pattern2)
            {
                return null;    // 无权威来源 → Direction=null，绝不猜
            }

            caretRange = pattern2.GetCaretRange(out _);
            double[] rects;
            unsafe
            {
                rects = UiaCom.ReadRectangles(caretRange.GetBoundingRectangles());
            }

            if (rects.Length < 4 || rects.Length > 4)
            {
                return null;    // 期望单个 caret 矩形；异常形态放弃权威判定
            }

            var rect = new PhysicalScreenRect(rects[0], rects[1], rects[0] + (rects[2] <= 0 ? 1 : rects[2]), rects[1] + (rects[3] <= 0 ? 1 : rects[3]));

            var startCompare = caretRange.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selection, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            var endCompare = caretRange.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selection, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            if (startCompare > 0 && endCompare <= 0)
            {
                direction = SelectionDirection.Forward;    // caret 在选区结束端
            }
            else if (startCompare <= 0 && endCompare < 0)
            {
                direction = SelectionDirection.Backward;
            }

            return rect;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            UiaCom.ReleaseComObject(caretRange);
            UiaCom.ReleaseComObject(patternObject);
        }
    }

    /// <summary>锚点链（ADR-0004）：caret 端点矩形（键盘跳过 50px 校验）→ mouseUp 点 → fallbackAnchor → 无锚点。</summary>
    private static (PhysicalScreenRect? Rect, AnchorSource Source) ResolveAnchor(PhysicalScreenRect? caretEndpointRect, BackendReadRequest request)
    {
        bool keyboard = request.Gesture is SelectionGesture.CtrlA or SelectionGesture.ShiftKeyboard;
        if (caretEndpointRect is { } caret)
        {
            bool sizeOk = caret.Width >= 6 && caret.Height >= 6;
            bool proximityOk = keyboard || (request.UpPoint is { } up && caret.Inflate(50).Contains(up));
            if (sizeOk && proximityOk)
            {
                return (caret, AnchorSource.CaretEndpoint);
            }
        }

        if (request.UpPoint is { } mouseUp)
        {
            return (new PhysicalScreenRect(mouseUp.X, mouseUp.Y, mouseUp.X + 1, mouseUp.Y + 1), AnchorSource.MouseReleasePoint);
        }

        if (request.Origin == CaptureOrigin.Explicit && request.FallbackAnchor is { } fallback)
        {
            return (new PhysicalScreenRect(fallback.X, fallback.Y, fallback.X + 1, fallback.Y + 1), AnchorSource.FallbackAnchor);
        }

        return (null, AnchorSource.None);
    }
}

/// <summary>ClickSelection 预检：焦点元素是否持有指定进程的非折叠选区（点击型选区变化的防噪闸）。</summary>
internal static class ClickSelectionPrecheck
{
    public static bool HasNonCollapsedSelection(int expectedProcessId)
    {
        IUIAutomationElement? element = null;
        IUIAutomationTextRangeArray? ranges = null;
        IUIAutomationTextRange? range = null;
        IUIAutomationElement? owner = null;
        try
        {
            var automation = UiaCom.Automation;
            element = automation.GetFocusedElement();
            if (element == null || UiaCom.GetProcessId(element) != expectedProcessId)
            {
                return false;
            }

            var (textPattern, patternOwner, ownerFromWalk) = UiaCom.FindTextPattern(automation, element);
            owner = patternOwner;
            if (textPattern == null)
            {
                return false;
            }

            try
            {
                ranges = textPattern.GetSelection();
                if (ranges == null || ranges.Length == 0)
                {
                    return false;
                }

                range = ranges.GetElement(0);
                return range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) != 0;
            }
            finally
            {
                if (!ownerFromWalk)
                {
                    UiaCom.ReleaseComObject(owner);
                }
            }
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            UiaCom.ReleaseComObject(range);
            UiaCom.ReleaseComObject(ranges);
            UiaCom.ReleaseComObject(element);
        }
    }
}
