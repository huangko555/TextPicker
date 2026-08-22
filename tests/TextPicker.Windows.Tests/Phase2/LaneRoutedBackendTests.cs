using TextPicker;
using TextPicker.Windows;
using TextPicker.Windows.Tests.Phase0;

namespace TextPicker.Windows.Tests.Phase2;

public sealed class LaneRoutedBackendTests
{
    [Fact]
    public async Task SuccessfulRead_PassesThrough()
    {
        using var backend = new LaneRoutedBackend((_, _) => FakeBackend.Ok("lane-text"), queryTimeout: TimeSpan.FromSeconds(2));

        var result = await backend.ReadAsync(new BackendReadRequest { Origin = CaptureOrigin.Gesture, Generation = new SelectionGeneration(1) }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("lane-text", result.Content.Text);
    }

    [Fact]
    public async Task SlowRead_MapsToBackendTimeout()
    {
        using var backend = new LaneRoutedBackend((_, _) => { Thread.Sleep(3000); return FakeBackend.Ok(); }, queryTimeout: TimeSpan.FromMilliseconds(80));

        var result = await backend.ReadAsync(new BackendReadRequest { Origin = CaptureOrigin.Gesture, Generation = new SelectionGeneration(1) }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CaptureFailureReason.BackendTimeout, result.Failure);
    }

    [Fact]
    public async Task AccessDeniedException_MapsToAccessDenied()
    {
        using var backend = new LaneRoutedBackend((_, _) => throw new UnauthorizedAccessException("0x80070005"), queryTimeout: TimeSpan.FromSeconds(2));

        var result = await backend.ReadAsync(new BackendReadRequest { Origin = CaptureOrigin.Explicit, RequestId = new SelectionRequestId(1) }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CaptureFailureReason.AccessDenied, result.Failure);
    }

    [Fact]
    public async Task CaptureLane_SerializesGestureAndExplicitReads()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        using var backend = new LaneRoutedBackend((_, _) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, current);
            Thread.Sleep(150);    // 强制重叠窗口
            Interlocked.Decrement(ref inFlight);
            return FakeBackend.Ok();
        }, queryTimeout: TimeSpan.FromSeconds(2));

        var gestureTask = backend.ReadAsync(new BackendReadRequest { Origin = CaptureOrigin.Gesture, Generation = new SelectionGeneration(1) }, CancellationToken.None);
        var explicitTask = backend.ReadAsync(new BackendReadRequest { Origin = CaptureOrigin.Explicit, RequestId = new SelectionRequestId(1) }, CancellationToken.None);

        await Task.WhenAll(gestureTask, explicitTask);
        Assert.True((await gestureTask).Success);
        Assert.True((await explicitTask).Success);
        Assert.Equal(1, maxInFlight);    // Capture lane 串行：手势与显式不同时在飞
    }
}
