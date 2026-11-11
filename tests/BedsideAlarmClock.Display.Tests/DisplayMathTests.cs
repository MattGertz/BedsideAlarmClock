namespace BedsideAlarmClock.Display.Tests;

public class DisplayMathTests
{
    [Theory]
    [InlineData(96, 720, 360, 48)]
    [InlineData(48, 360, 720, 96)]
    [InlineData(48, 360, 900, 96)]
    [InlineData(96, 720, 720, 96)]
    public void ClockFontSizeFitsReferenceAndRespectsMaximum(
        double currentSize, double referenceWidth, double availableWidth, double expected)
    {
        Assert.Equal(expected, DisplayMath.FitFontSize(currentSize, referenceWidth, availableWidth, 96), 6);
    }

    [Theory]
    [InlineData(0.5f, -250f, 1000f, 0.7475f)]
    [InlineData(0.5f, 250f, 1000f, 0.2525f)]
    [InlineData(0.5f, -1000f, 1000f, 1f)]
    [InlineData(0.5f, 1000f, 1000f, 0.01f)]
    [InlineData(0.5f, 0f, 1000f, 0.5f)]
    public void SwipeMapsToClampedWindowBrightness(
        float initial, float distance, float height, float expected)
    {
        Assert.Equal(expected, DisplayMath.BrightnessAfterSwipe(initial, distance, height), 4);
    }

    [Fact]
    public void PixelShiftCyclesThroughBoundedPhysicalOffsets()
    {
        var expected = new (int X, int Y)[]
        {
            (0, 0), (48, 0), (48, 48), (0, 48), (-48, 48),
            (-48, 0), (-48, -48), (0, -48), (48, -48)
        };

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], DisplayMath.PixelOffset(i));
            Assert.Equal(expected[i], DisplayMath.PixelOffset(i + expected.Length));
        }
    }
}
