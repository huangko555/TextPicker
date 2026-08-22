namespace TextPicker;

/// <summary>Options 合法性校验（ApplyOptions 前置）。</summary>
public static class SelectionPickerOptionsValidator
{
    public static void Validate(SelectionPickerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ExcludedProcesses is null)
        {
            throw new ArgumentException("ExcludedProcesses must not be null (use empty list).", nameof(options));
        }

        if (options.DragThresholdPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DragThresholdPixels must be >= 0.");
        }

        if (options.MultiClickTolerancePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MultiClickTolerancePixels must be >= 0.");
        }

        if (options.StaleMessageAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "StaleMessageAge must be positive.");
        }

        if (options.IncompleteTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "IncompleteTimeout must be positive.");
        }

        if (options.QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueryTimeout must be positive.");
        }

        if (options.CircuitCooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "CircuitCooldown must be positive.");
        }

        if (options.SettleDeadline < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SettleDeadline must be >= 0.");
        }

        if (options.MaxTextLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTextLength must be >= 1.");
        }
    }
}
