namespace TextPicker;

public sealed class SelectionCandidateReadyEventArgs : EventArgs
{
    public SelectionCandidateReadyEventArgs(SelectionCandidateReady candidate) => Candidate = candidate;
    public SelectionCandidateReady Candidate { get; }
}

public sealed class SelectionCapturedEventArgs : EventArgs
{
    public SelectionCapturedEventArgs(SelectionCapture capture) => Capture = capture;
    public SelectionCapture Capture { get; }
}

public sealed class SelectionFailedEventArgs : EventArgs
{
    public SelectionFailedEventArgs(SelectionGeneration? generation, SelectionRequestId? requestId, SelectionGesture? gesture, CaptureFailureReason reason, TimeSpan elapsed)
    {
        Generation = generation;
        RequestId = requestId;
        Gesture = gesture;
        Reason = reason;
        Elapsed = elapsed;
    }

    public SelectionGeneration? Generation { get; }
    public SelectionRequestId? RequestId { get; }
    public SelectionGesture? Gesture { get; }
    public CaptureFailureReason Reason { get; }
    public TimeSpan Elapsed { get; }
}

public sealed class SelectionSupersededEventArgs : EventArgs
{
    public SelectionSupersededEventArgs(SelectionGeneration generation, SelectionGesture? gesture)
    {
        Generation = generation;
        Gesture = gesture;
    }

    public SelectionGeneration Generation { get; }
    public SelectionGesture? Gesture { get; }
}

public sealed class SelectionInvalidatedEventArgs : EventArgs
{
    public SelectionInvalidatedEventArgs(SelectionGeneration generation, SelectionInvalidationReason reason)
    {
        Generation = generation;
        Reason = reason;
    }

    public SelectionGeneration Generation { get; }
    public SelectionInvalidationReason Reason { get; }
}

/// <summary>诊断相位（CandidateStarted 先于正文读取）。</summary>
public sealed class GesturePhaseEventArgs : EventArgs
{
    public GesturePhaseEventArgs(SelectionPipelineStage phase, SelectionGeneration? generation)
    {
        Phase = phase;
        Generation = generation;
    }

    public SelectionPipelineStage Phase { get; }
    public SelectionGeneration? Generation { get; }
}

public sealed class CaretEventArgs : EventArgs
{
    public CaretEventArgs(CaretObservation observation) => Observation = observation;
    public CaretObservation Observation { get; }
}

/// <summary>Target 为 null 表示焦点丢失。</summary>
public sealed class FocusTargetEventArgs : EventArgs
{
    public FocusTargetEventArgs(TargetContext? target) => Target = target;
    public TargetContext? Target { get; }
}

public sealed class PointerMovedEventArgs : EventArgs
{
    public PointerMovedEventArgs(PhysicalScreenPoint position, DateTimeOffset timestamp)
    {
        Position = position;
        Timestamp = timestamp;
    }

    public PhysicalScreenPoint Position { get; }
    public DateTimeOffset Timestamp { get; }
}

public sealed class SelectionContentChangedEventArgs : EventArgs
{
    public SelectionContentChangedEventArgs(SelectionCapture capture) => Capture = capture;
    public SelectionCapture Capture { get; }
}
