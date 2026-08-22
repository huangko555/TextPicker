using System.Diagnostics;

namespace TextPicker.Windows;

internal enum PolicyFilterReason
{
    None = 0,

    /// <summary>手势被禁用（SetTriggerEnabled / Options 开关）。</summary>
    DisabledGesture,

    /// <summary>消费者注册豁免窗口：不产生候选、不取代、不失效。</summary>
    ConsumerWindow,

    OwnProcess,
    ExcludedProcess,
    FullScreenPaused,
}

/// <summary>手势候选发布前的静默过滤（ADR-0007：过滤不产生 generation，保证无洞不变式）。</summary>
internal interface ITargetPolicy
{
    PolicyFilterReason FilterGesture(in GesturePolicyContext context);
}

internal readonly struct GesturePolicyContext
{
    public GesturePolicyContext(SelectionGesture gesture, int targetProcessId, nint targetWindowHandle, SelectionPickerOptions options, IReadOnlyCollection<nint> consumerWindows)
    {
        Gesture = gesture;
        TargetProcessId = targetProcessId;
        TargetWindowHandle = targetWindowHandle;
        Options = options;
        ConsumerWindows = consumerWindows;
    }

    public SelectionGesture Gesture { get; }
    public int TargetProcessId { get; }
    public nint TargetWindowHandle { get; }
    public SelectionPickerOptions Options { get; }
    public IReadOnlyCollection<nint> ConsumerWindows { get; }
}

internal sealed class DefaultTargetPolicy : ITargetPolicy
{
    public static DefaultTargetPolicy Instance { get; } = new();

    public PolicyFilterReason FilterGesture(in GesturePolicyContext context)
    {
        var gesture = context.Gesture;
        var options = context.Options;
        bool enabled = gesture switch
        {
            SelectionGesture.BoxSelect => options.BoxSelectEnabled,
            SelectionGesture.MultiClick => options.MultiClickEnabled,
            SelectionGesture.ShiftClick => options.ShiftClickEnabled,
            SelectionGesture.CtrlA => options.CtrlAEnabled,
            SelectionGesture.ShiftKeyboard => options.ShiftKeyboardEnabled,
            _ => true,
        };
        if (!enabled)
        {
            return PolicyFilterReason.DisabledGesture;
        }

        if (context.ConsumerWindows.Contains(context.TargetWindowHandle))
        {
            return PolicyFilterReason.ConsumerWindow;
        }

        if (context.TargetProcessId == Environment.ProcessId)
        {
            return PolicyFilterReason.OwnProcess;
        }

        if (context.Options.ExcludedProcesses.Count > 0)
        {
            var processName = ProcessNameResolver.ResolveOrNull(context.TargetProcessId);
            if (processName != null && context.Options.ExcludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                return PolicyFilterReason.ExcludedProcess;
            }
        }

        // 全屏暂停接入点：Phase 2 FullScreenWindowDetector（ADR-0006 之前的 TargetPolicy 完整形态）。
        return PolicyFilterReason.None;
    }

    private static class ProcessNameResolver
    {
        private static readonly Dictionary<int, string?> Cache = new();

        public static string? ResolveOrNull(int processId)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(processId, out var cached))
                {
                    return cached;
                }
            }

            string? name;
            try
            {
                using var process = Process.GetProcessById(processId);
                name = process.ProcessName;
            }
            catch (ArgumentException)
            {
                name = null;    // 进程已退出：无法判定，交由快照复核层处理
            }
            catch (InvalidOperationException)
            {
                name = null;
            }

            lock (Cache)
            {
                Cache[processId] = name;
            }

            return name;
        }
    }
}
