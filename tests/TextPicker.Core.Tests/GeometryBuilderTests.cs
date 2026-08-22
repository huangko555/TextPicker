namespace TextPicker.Core.Tests;

public sealed class GeometryBuilderTests
{
    [Fact]
    public void NullRaw_ReturnsNull()
    {
        Assert.Null(GeometryBuilder.TryBuild(null, null, null, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void RawCountNotMultipleOfFour_ReturnsNull(int doublesCount)
    {
        var raw = Enumerable.Repeat(10.0, doublesCount).ToArray();
        Assert.Null(GeometryBuilder.TryBuild(raw, null, null, null));
    }

    [Fact]
    public void EmptyRawNoEndpoints_CompletenessNone()
    {
        var geometry = GeometryBuilder.TryBuild(Array.Empty<double>(), null, null, null);

        Assert.NotNull(geometry);
        Assert.Equal(GeometryCompleteness.None, geometry.Completeness);
        Assert.Empty(geometry.VisibleRects);
        Assert.Null(geometry.BoundingRect);
        Assert.Equal(0, geometry.RectCount);
    }

    [Fact]
    public void RectsWithoutEndpoints_CompletenessRectsOnly_AndUnionsBounding()
    {
        var geometry = GeometryBuilder.TryBuild(new double[] { 10, 10, 110, 40, 50, 60, 150, 90 }, null, null, null);

        Assert.NotNull(geometry);
        Assert.Equal(GeometryCompleteness.RectsOnly, geometry.Completeness);
        Assert.Equal(2, geometry.RectCount);
        Assert.NotNull(geometry.BoundingRect);
        Assert.Equal(new PhysicalScreenRect(10, 10, 150, 90), geometry.BoundingRect!.Value);
    }

    [Fact]
    public void OneEndpoint_CompletenessPartial()
    {
        var geometry = GeometryBuilder.TryBuild(
            new double[] { 10, 10, 110, 40 },
            startRect: new PhysicalScreenRect(10, 10, 11, 28),
            endRect: null,
            direction: null);

        Assert.NotNull(geometry);
        Assert.Equal(GeometryCompleteness.PartialEndpoints, geometry.Completeness);
        Assert.NotNull(geometry.StartRect);
        Assert.Null(geometry.EndRect);
    }

    [Fact]
    public void BothEndpoints_CompletenessComplete_DirectionPassthrough()
    {
        var geometry = GeometryBuilder.TryBuild(
            Array.Empty<double>(),
            startRect: new PhysicalScreenRect(1, 1, 2, 20),
            endRect: new PhysicalScreenRect(80, 1, 81, 20),
            direction: SelectionDirection.Backward);

        Assert.NotNull(geometry);
        Assert.Equal(GeometryCompleteness.CompleteEndpoints, geometry.Completeness);
        Assert.Equal(SelectionDirection.Backward, geometry.Direction);
        Assert.Null(geometry.BoundingRect);
    }
}
