using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Execution;
using TextPicker.Windows.Uia;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase3;

/// <summary>UIA 后端进程内端到端：WPF TextBox 程序化选中 → 真实 UIA 读取（文本/几何/锚点/方向/截断/caret/state/probe）。
/// 全部断言走 xunit 线程异步等待，绝不阻塞 WPF UI 线程。</summary>
[Collection("WpfSerial")]
public sealed class UiaBackendWpfHostTests : IDisposable
{
    private readonly WpfHost _host = new();

    public void Dispose() => _host.Dispose();

    private BackendReadRequest NewRequest(SelectionGesture gesture = SelectionGesture.BoxSelect, PhysicalScreenPoint? up = null, int? maxText = null)
        => new()
        {
            Epoch = 1,
            Generation = new SelectionGeneration(1),
            Origin = CaptureOrigin.Gesture,
            Gesture = gesture,
            Target = new CandidateTargetSnapshot { ProcessId = WpfHost.ProcessId, WindowHandle = _host.WindowHandle },
            UpPoint = up ?? _host.PointInTextBox(),
            Options = new SelectionPickerOptions { MaxTextLength = maxText ?? 4000 },
        };

    [Fact]
    public void ReadBoxSelection_ReturnsSelectedText()
    {
        _host.Activate();
        _host.Select(4, 11);    // "quick brown"

        var backend = new UiaSelectionBackend();
        var result = backend.Read(NewRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("quick brown", result.Content.Text);
        Assert.Equal(CaptureBackend.UiaTextPattern, result.Backend);
        Assert.NotNull(result.AnchorRect);
        Assert.NotEqual(AnchorSource.None, result.AnchorSource);
    }

    [Fact]
    public void Read_ReturnsParagraphLocalContext_ContainingSelection()
    {
        _host.Activate();
        _host.Select(4, 5);    // "quick"

        var backend = new UiaSelectionBackend();
        var result = backend.Read(NewRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Content.LocalContext);
        Assert.Contains("quick", result.Content.LocalContext!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TruncatesAtMaxLength()
    {
        _host.Activate();
        _host.Select(0, 20);

        var backend = new UiaSelectionBackend();
        var result = backend.Read(NewRequest(maxText: 5), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, result.Content.Text.Length);
        Assert.True(result.Content.Truncated);
    }

    [Fact]
    public void Read_ProcessMismatch_WhenTargetPidDiffers()
    {
        _host.Activate();
        _host.Select(0, 4);

        var wrongPidRequest = new BackendReadRequest
        {
            Epoch = 1,
            Generation = new SelectionGeneration(1),
            Origin = CaptureOrigin.Gesture,
            Gesture = SelectionGesture.BoxSelect,
            Target = new CandidateTargetSnapshot { ProcessId = WpfHost.ProcessId + 12345, WindowHandle = _host.WindowHandle },
            UpPoint = _host.PointInTextBox(),
        };

        var backend = new UiaSelectionBackend();
        var result = backend.Read(wrongPidRequest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CaptureFailureReason.ProcessMismatch, result.Failure);
    }

    [Fact]
    public void Read_NoSelection_EmptySelectionFailure()
    {
        _host.Activate();
        _host.Select(2, 0);    // 折叠选区（纯 caret）

        var backend = new UiaSelectionBackend();
        var result = backend.Read(NewRequest(), CancellationToken.None);

        // WPF 对折叠选区可能返回退化 range 或空：两者都必须是可诊断结果而非伪装成功。
        if (result.Success)
        {
            Assert.Equal(string.Empty, result.Content.Text);    // 退化 range 读出空文本：规范化层收口
        }
        else
        {
            Assert.True(result.Failure is CaptureFailureReason.EmptySelection or CaptureFailureReason.EmptyText);
        }
    }

    [Fact]
    public async Task ExplicitCaptureThroughFacade_EndToEnd()
    {
        _host.Activate();
        _host.Select(10, 9);    // "brown fox" 偏移验证

        using var picker = new SelectionPicker(
            gestureFeed: new CoreGestureFeed(),
            backend: new LaneRoutedBackend((request, ct) => new UiaSelectionBackend().Read(request, ct), queryTimeout: TimeSpan.FromSeconds(2)),
            focusSource: new FakeFocusSource(),
            uaEventSource: new FakeUaEventSource());
        picker.Start();

        var result = await picker.CaptureCurrentSelectionAsync(fallbackAnchor: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("brown fox", result.Capture!.Content.Text);
        Assert.Equal(CaptureOrigin.Explicit, result.Capture.Origin);
        picker.Stop();
    }

    [Fact]
    public async Task ObserveCaret_ReturnsRectFromTextPattern2()
    {
        _host.Activate();
        _host.Select(2, 0);    // 折叠到位置 2：caret

        using var picker = new SelectionPicker(
            gestureFeed: new CoreGestureFeed(),
            backend: new LaneRoutedBackend((request, ct) => new UiaSelectionBackend().Read(request, ct)),
            focusSource: new FakeFocusSource(),
            uaEventSource: new FakeUaEventSource());
        picker.Start();

        var caret = await picker.ObserveCaretAsync(CancellationToken.None);

        Assert.NotNull(caret);
        Assert.True(caret!.CaretRect.Height > 0);
        picker.Stop();
    }

    [Fact]
    public async Task ObserveSelectionState_ReportsNonCollapsedWithoutText()
    {
        _host.Activate();
        _host.Select(0, 6);

        using var picker = new SelectionPicker(
            gestureFeed: new CoreGestureFeed(),
            backend: new LaneRoutedBackend((request, ct) => new UiaSelectionBackend().Read(request, ct)),
            focusSource: new FakeFocusSource(),
            uaEventSource: new FakeUaEventSource());
        picker.Start();

        var state = await picker.ObserveSelectionStateAsync(CancellationToken.None);

        Assert.NotNull(state);
        Assert.True(state!.HasNonCollapsedSelection);
        Assert.NotNull(state.Geometry);
        Assert.NotNull(state.Target);
        picker.Stop();
    }

    [Fact]
    public async Task ProbeTarget_ReturnsContextWithText()
    {
        _host.Activate();
        _host.Select(0, 8);

        using var picker = new SelectionPicker(
            gestureFeed: new CoreGestureFeed(),
            backend: new LaneRoutedBackend((request, ct) => new UiaSelectionBackend().Read(request, ct)),
            focusSource: new FakeFocusSource(),
            uaEventSource: new FakeUaEventSource());
        picker.Start();

        var probe = await picker.ProbeTargetAsync(_host.PointInTextBox(), includeText: true, CancellationToken.None);

        Assert.True(probe.Success);
        Assert.NotNull(probe.Target);
        Assert.NotNull(probe.Content);
        Assert.Equal("The quic", probe.Content!.Text);
        picker.Stop();
    }
}
