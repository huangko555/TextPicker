using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Accessibility;

namespace TextPicker.Windows.Uia;

/// <summary>UIA COM 基础设施（ADR-0003：每线程独立 IUIAutomation；COM 对象不出本线程；SAFEARRAY 全量解析）。</summary>
internal static class UiaCom
{
    [ThreadStatic]
    private static IUIAutomation? _threadAutomation;

    /// <summary>当前 MTA 线程专属的 IUIAutomation（懒初始化；worker 置换后自然获得新实例）。</summary>
    public static IUIAutomation Automation => _threadAutomation ??= (IUIAutomation)new CUIAutomation8();

    public static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    /// <summary>GetBoundingRectangles 的 SAFEARRAY → double[]（全量；count%4 由 GeometryBuilder 校验）。</summary>
    public static unsafe double[] ReadRectangles(global::Windows.Win32.System.Com.SAFEARRAY* rectangles)
    {
        if (rectangles is null)
        {
            return Array.Empty<double>();
        }

        var accessed = false;
        try
        {
            PInvoke.SafeArrayGetLBound(rectangles, 1, out var lower).ThrowOnFailure();
            PInvoke.SafeArrayGetUBound(rectangles, 1, out var upper).ThrowOnFailure();
            var count = upper - lower + 1;

            PInvoke.SafeArrayAccessData(rectangles, out var data).ThrowOnFailure();
            accessed = true;
            return new ReadOnlySpan<double>(data, count).ToArray();
        }
        catch (COMException)
        {
            return Array.Empty<double>();
        }
        finally
        {
            if (accessed)
            {
                _ = PInvoke.SafeArrayUnaccessData(rectangles);
            }

            _ = PInvoke.SafeArrayDestroy(rectangles);
        }
    }

    public static int GetProcessId(IUIAutomationElement element)
        => element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId) as int? ?? 0;

    public static bool IsPassword(IUIAutomationElement element)
        => element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsPasswordPropertyId) as bool? == true;

    public static string GetStringProperty(IUIAutomationElement element, UIA_PROPERTY_ID propertyId)
        => element.GetCurrentPropertyValue(propertyId) as string ?? string.Empty;

    public static TargetContext Describe(IUIAutomationElement element, int processId)
    {
        return new TargetContext
        {
            ProcessId = processId,
            ProcessName = DefaultTargetPolicy.ResolveProcessName(processId) ?? string.Empty,
            WindowHandle = 0,
            ClassName = GetStringProperty(element, UIA_PROPERTY_ID.UIA_ClassNamePropertyId),
            FrameworkId = GetStringProperty(element, UIA_PROPERTY_ID.UIA_FrameworkIdPropertyId),
            ControlType = (element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_ControlTypePropertyId) as int?)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            IsEditable = element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsValuePatternAvailablePropertyId) as bool? == true,
            IsPassword = IsPassword(element),
            HasTextPattern = element.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId) as bool? == true,
        };
    }

    /// <summary>从元素沿父链有界上溯找第一个支持 TextPattern 的元素（找到即停，非固定宽度 BFS）。
    /// 返回 (TextPattern, Owner)；Owner 的 COM 引用由返回值转移给调用方（OwnerIsStart=true 时即 start 本身，调用方不应重复释放）。</summary>
    public static (IUIAutomationTextPattern? Pattern, IUIAutomationElement? Owner, bool OwnerIsStart) FindTextPattern(
        IUIAutomation automation, IUIAutomationElement start, int maxDepth = 10)
    {
        var walker = automation.ControlViewWalker;
        IUIAutomationElement? current = start;
        bool owned = false;    // current 是否为本次遍历获得（start 归调用方）
        try
        {
            for (int depth = 0; depth < maxDepth && current != null; depth++)
            {
                object? patternObject = null;
                try
                {
                    var textPatternId = typeof(IUIAutomationTextPattern).GUID;
                    patternObject = current.GetCurrentPatternAs(UIA_PATTERN_ID.UIA_TextPatternId, in textPatternId);
                    if (patternObject is IUIAutomationTextPattern pattern)
                    {
                        return (pattern, current, !owned);    // pattern 全新引用；current 所有权转移
                    }
                }
                finally
                {
                    if (patternObject is not IUIAutomationTextPattern)
                    {
                        ReleaseComObject(patternObject);
                    }
                }

                var parent = walker.GetParentElement(current);
                if (owned)
                {
                    ReleaseComObject(current);
                }

                current = parent;
                owned = true;
            }

            if (owned && current != null)
            {
                ReleaseComObject(current);
            }
        }
        catch
        {
            if (owned)
            {
                ReleaseComObject(current);
            }

            throw;
        }

        return (null, null, false);
    }
}
