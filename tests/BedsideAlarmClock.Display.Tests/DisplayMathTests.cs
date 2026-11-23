namespace BedsideAlarmClock.Display.Tests;

public class DisplayMathTests
{
    [Theory]
    [InlineData(96, 720, 120, 360, 240, 48)]
    [InlineData(96, 720, 120, 1440, 360, 192)]
    [InlineData(96, 720, 120, 1440, 180, 144)]
    public void ClockFontSizeFitsReferenceWithinWidthAndHeight(
        double currentSize, double referenceWidth, double referenceHeight,
        double availableWidth, double availableHeight, double expected)
    {
        Assert.Equal(expected, DisplayMath.FitFontSize(
            currentSize, referenceWidth, referenceHeight, availableWidth, availableHeight), 6);
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
    public void PixelShiftCyclesThroughCornersWithMaximumTravel()
    {
        var expected = new (int X, int Y)[]
        {
            (48, -48), (48, 48), (-48, 48), (-48, -48)
        };

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], DisplayMath.PixelOffset(i));
            Assert.Equal(expected[i], DisplayMath.PixelOffset(i + expected.Length));
            var next = DisplayMath.PixelOffset(i + 1);
            Assert.Equal(2 * DisplayMath.PixelShiftDistance,
                Math.Abs(next.X - expected[i].X) + Math.Abs(next.Y - expected[i].Y));
        }
    }

    [Fact]
    public void ClockColorsAreDistinctAndUnknownColorsUseDefault()
    {
        var colors = ClockSettings.ClockColors.Select(ClockSettings.ColorValues).ToArray();
        Assert.Equal(colors.Length, colors.Distinct().Count());
        Assert.Equal(ClockSettings.ColorValues("Grey"), ClockSettings.ColorValues("unknown"));
        Assert.Equal(ClockSettings.ColorValues("Grey"), ClockSettings.ColorValues("Gray"));
    }
}
