using System.Reflection;

namespace TextPicker.Core.Tests;

/// <summary>契约结构冻结：封闭枚举值数、手势枚举冻结数值、阶段一快照零 UIA 字段、诊断类型 string-free（ADR-0008）。</summary>
public sealed class ContractTypesFreezeTests
{
    [Fact]
    public void CandidateTargetSnapshot_StructurallyContainsNoUiaFields()
    {
        // 阶段一快照只允许 Win32 浅信息类型（防未来偷加 ControlType/IsEditable 等 UIA 派生字段 → 契约 #3）。
        var allowed = new HashSet<Type>
        {
            typeof(int),
            typeof(nint),
            typeof(PhysicalScreenPoint),
            typeof(PhysicalScreenRect),
            typeof(DateTimeOffset),
        };

        foreach (var property in typeof(CandidateTargetSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var type = property.PropertyType;
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                type = underlying;
            }

            Assert.True(allowed.Contains(type), $"CandidateTargetSnapshot.{property.Name} 引用了非允许类型 {property.PropertyType}（阶段一必须零 UIA）");
        }
    }

    [Fact]
    public void CaptureFailureReason_IsClosedAt18Values()
    {
        Assert.Equal(18, Enum.GetValues<CaptureFailureReason>().Length);
        Assert.DoesNotContain("Superseded", Enum.GetNames<CaptureFailureReason>());    // Superseded 是独立终止事件
    }

    [Fact]
    public void SelectionGesture_MatchesFrozenTriggerValues()
    {
        Assert.Equal(0, (int)SelectionGesture.Explicit);
        Assert.Equal(1, (int)SelectionGesture.BoxSelect);
        Assert.Equal(2, (int)SelectionGesture.MultiClick);
        Assert.Equal(3, (int)SelectionGesture.ShiftClick);
        Assert.Equal(4, (int)SelectionGesture.CtrlA);
        Assert.Equal(5, (int)SelectionGesture.ShiftKeyboard);
    }

    [Fact]
    public void DiagnosticsAndCounters_ExposeNoStringProperties()
    {
        AssertNoneString(typeof(SelectionDiagnosticsEventArgs));
        AssertNoneString(typeof(SelectionPickerCounters));
    }

    private static void AssertNoneString(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(property.PropertyType != typeof(string), $"{type.Name}.{property.Name} 暴露了 string（诊断结构上不可能携带正文，ADR-0008）");
        }
    }
}
