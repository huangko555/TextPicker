namespace TextPicker.Core.Tests;

public sealed class SelectionPickerOptionsValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        SelectionPickerOptionsValidator.Validate(new SelectionPickerOptions());
    }

    [Fact]
    public void NullExcludedProcesses_Throws()
    {
        var options = new SelectionPickerOptions { ExcludedProcesses = null! };
        Assert.Throws<ArgumentException>(() => SelectionPickerOptionsValidator.Validate(options));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void BadMaxTextLength_Throws(int maxTextLength)
    {
        var options = new SelectionPickerOptions { MaxTextLength = maxTextLength };
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionPickerOptionsValidator.Validate(options));
    }

    [Fact]
    public void NegativeThresholds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SelectionPickerOptionsValidator.Validate(new SelectionPickerOptions { DragThresholdPixels = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SelectionPickerOptionsValidator.Validate(new SelectionPickerOptions { MultiClickTolerancePixels = -1 }));
    }

    [Theory]
    [InlineData(nameof(SelectionPickerOptions.StaleMessageAge))]
    [InlineData(nameof(SelectionPickerOptions.IncompleteTimeout))]
    [InlineData(nameof(SelectionPickerOptions.QueryTimeout))]
    [InlineData(nameof(SelectionPickerOptions.CircuitCooldown))]
    public void NonPositiveTimespan_Throws(string propertyName)
    {
        var options = new SelectionPickerOptions();
        typeof(SelectionPickerOptions).GetProperty(propertyName)!.SetValue(options, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionPickerOptionsValidator.Validate(options));
    }

    [Fact]
    public void ZeroSettleDeadline_IsAllowed()
    {
        SelectionPickerOptionsValidator.Validate(new SelectionPickerOptions { SettleDeadline = TimeSpan.Zero });
    }
}
