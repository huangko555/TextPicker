using Windows.Win32.UI.Accessibility;
using TextPicker.Windows;

namespace TextPicker.Windows.Tests.Phase0;

/// <summary>契约 #6：三 lane COM 隔离——跨 lane DTO（ILaneTransferable）白名单不含任何 COM 接口（ADR-0003）。</summary>
public sealed class Contract6LaneIsolation
{
    [Fact]
    public void ScannerRecognizesCsWin32ComInterfaces()
    {
        // CsWin32 生成的 COM 接口（[ComImport]）必须被识别为不可跨 lane 类型。
        Assert.True(LaneDtoRules.IsComInterface(typeof(IUIAutomation)));
        Assert.True(LaneDtoRules.IsComInterface(typeof(IUIAutomationElement)));

        // 非 COM 接口不误报。
        Assert.False(LaneDtoRules.IsComInterface(typeof(IDisposable)));
        Assert.False(LaneDtoRules.IsComInterface(typeof(string)));
    }

    [Fact]
    public void WindowsAssembly_LaneTransferableTypes_CarryNoComMembers()
    {
        var violations = LaneDtoRules.FindViolations(typeof(SelectionPicker).Assembly);
        Assert.Empty(violations);
    }

    [Fact]
    public void ScannerCatchesSyntheticViolator()
    {
        var violations = LaneDtoRules.FindViolations(typeof(Contract6LaneIsolation).Assembly);
        Assert.Contains(violations, v => v.DeclaringType == typeof(SyntheticViolatingDto));
    }

    // 合成违规 DTO：仅存在于测试程序集，证明扫描规则真实有效（防「扫描器永远绿」假阳性）。
    private sealed class SyntheticViolatingDto : ILaneTransferable
    {
        public IUIAutomation? Automation { get; set; }
    }
}
