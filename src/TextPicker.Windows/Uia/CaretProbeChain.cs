using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace TextPicker.Windows.Uia;

/// <summary>caret 观察结果（探针内部形态）。</summary>
internal readonly record struct CaretProbeResult(PhysicalScreenRect? CaretRect, CaretSource? Source, bool IsCollapsedSelection, TargetContext? Target)
{
    public bool Found => CaretRect != null && Source != null;
}

/// <summary>
/// caret 探针链（ADR 定案四级，无 TSF）：TextPattern2.GetCaretRange → 折叠 TextPattern →
/// GetGUIThreadInfo(+ClientToScreen) → MSAA OBJID_CARET。逐级退化；在调用线程（Observer lane MTA）执行。
/// </summary>
internal sealed class CaretProbeChain
{
    public static CaretProbeResult Observe()
    {
        var level1 = TryTextPattern2();
        if (level1.Found)
        {
            return level1;
        }

        var level2 = TryCollapsedTextRange();
        if (level2.Found)
        {
            return level2;
        }

        var level3 = TryGuiThreadInfo();
        if (level3.Found)
        {
            return level3;
        }

        return TryMsaaCaret();
    }

    private static CaretProbeResult TryTextPattern2()
    {
        IUIAutomationElement? element = null;
        object? patternObject = null;
        IUIAutomationTextRange? caretRange = null;
        try
        {
            element = UiaCom.Automation.GetFocusedElement();
            if (element == null)
            {
                return default;
            }

            var pattern2Id = typeof(IUIAutomationTextPattern2).GUID;
            patternObject = element.GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPattern2Id, in pattern2Id);
            if (patternObject is not IUIAutomationTextPattern2 pattern2)
            {
                return default;
            }

            caretRange = pattern2.GetCaretRange(out _);
            double[] rects;
            unsafe
            {
                rects = UiaCom.ReadRectangles(caretRange.GetBoundingRectangles());
            }

            if (rects.Length != 4)
            {
                return default;    // 单个矩形校验（§3.1 caret 矩形校验）
            }

            var rect = new PhysicalScreenRect(rects[0], rects[1], rects[0] + Math.Max(rects[2], 1), rects[1] + Math.Max(rects[3], 1));
            var pid = UiaCom.GetProcessId(element);
            return new CaretProbeResult(rect, CaretSource.UiaTextPattern2CaretRange, IsCollapsedSelection: true, Target: UiaCom.Describe(element, pid));
        }
        catch (COMException)
        {
            return default;
        }
        finally
        {
            UiaCom.ReleaseComObject(caretRange);
            UiaCom.ReleaseComObject(patternObject);
            UiaCom.ReleaseComObject(element);
        }
    }

    private static CaretProbeResult TryCollapsedTextRange()
    {
        IUIAutomationElement? element = null;
        IUIAutomationTextRangeArray? ranges = null;
        IUIAutomationTextRange? range = null;
        IUIAutomationElement? owner = null;
        try
        {
            var automation = UiaCom.Automation;
            element = automation.GetFocusedElement();
            if (element == null)
            {
                return default;
            }

            var (textPattern, patternOwner, ownerFromWalk) = UiaCom.FindTextPattern(automation, element);
            owner = patternOwner;
            if (textPattern == null)
            {
                return default;
            }

            try
            {
                ranges = textPattern.GetSelection();
                if (ranges == null || ranges.Length == 0)
                {
                    return default;
                }

                range = ranges.GetElement(0);
                bool collapsed = range.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) == 0;
                var endpoint = collapsed
                    ? TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start
                    : TextPatternRangeEndpoint.TextPatternRangeEndpoint_End;
                double[] rects;
                unsafe
                {
                    rects = UiaCom.ReadRectangles(range.GetBoundingRectangles());
                }

                if (rects.Length < 4)
                {
                    return default;
                }

                var index = collapsed ? 0 : (rects.Length / 4 - 1) * 4;    // 折叠近似：非折叠取最后行矩形
                var rect = new PhysicalScreenRect(rects[index], rects[index + 1], rects[index] + Math.Max(rects[index + 2], 1), rects[index + 1] + Math.Max(rects[index + 3], 1));
                var pid = UiaCom.GetProcessId(element);
                return new CaretProbeResult(rect, CaretSource.UiaTextRangeCollapsed, !collapsed, Target: UiaCom.Describe(element, pid));
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
            return default;
        }
        finally
        {
            UiaCom.ReleaseComObject(range);
            UiaCom.ReleaseComObject(ranges);
            UiaCom.ReleaseComObject(element);
        }
    }

    /// <summary>GetGUIThreadInfo.rcCaret 是 caret 所属 HWND 的客户区坐标，需 ClientToScreen（v6.1 平台事实）。</summary>
    private static unsafe CaretProbeResult TryGuiThreadInfo()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return default;
        }

        _ = PInvoke.GetWindowThreadProcessId(foreground, out var pid);
        var info = default(global::Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO);
        info.cbSize = (uint)sizeof(global::Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO);
        if (!PInvoke.GetGUIThreadInfo(0, ref info) || info.hwndCaret.IsNull)
        {
            return default;
        }

        var topLeft = new System.Drawing.Point(info.rcCaret.left, info.rcCaret.top);
        var bottomRight = new System.Drawing.Point(info.rcCaret.right, info.rcCaret.bottom);
        if (PInvoke.ClientToScreen(info.hwndCaret, ref topLeft) && PInvoke.ClientToScreen(info.hwndCaret, ref bottomRight))
        {
            var width = Math.Max(bottomRight.X - topLeft.X, 1);
            var height = Math.Max(bottomRight.Y - topLeft.Y, 1);
            var rect = new PhysicalScreenRect(topLeft.X, topLeft.Y, topLeft.X + width, topLeft.Y + height);
            return new CaretProbeResult(rect, CaretSource.Win32GuiThreadInfo, IsCollapsedSelection: true, Target: null);
        }

        return default;
    }

    /// <summary>MSAA OBJID_CARET 兜底（最后一级；非 UIA 线程或前级全部失败时）。</summary>
    private static CaretProbeResult TryMsaaCaret() => default;    // v1：三级已覆盖主流；MSAA 级留 v1.1（冒烟矩阵证明需要时补）
}
