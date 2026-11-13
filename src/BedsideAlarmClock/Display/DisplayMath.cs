namespace BedsideAlarmClock.Display;

internal static class DisplayMath
{
    public const float MinimumBrightness = 0.01f;
    public const float MaximumBrightness = 1f;
    public const float DefaultBrightness = 0.2f;
    public const int PixelShiftDistance = 48;

    public static double FitFontSize(
        double currentSize, double referenceWidth, double referenceHeight, double availableWidth, double availableHeight)
    {
        return currentSize * Math.Min(availableWidth / referenceWidth, availableHeight / referenceHeight);
    }

    public static float BrightnessAfterSwipe(float initialBrightness, float verticalDistance, float screenHeight)
    {
        return Math.Clamp(
            initialBrightness - verticalDistance / screenHeight * (MaximumBrightness - MinimumBrightness),
            MinimumBrightness,
            MaximumBrightness);
    }

    public static (int X, int Y) PixelOffset(int position)
    {
        return (position % 9) switch
        {
            0 => (0, 0),
            1 => (PixelShiftDistance, 0),
            2 => (PixelShiftDistance, PixelShiftDistance),
            3 => (0, PixelShiftDistance),
            4 => (-PixelShiftDistance, PixelShiftDistance),
            5 => (-PixelShiftDistance, 0),
            6 => (-PixelShiftDistance, -PixelShiftDistance),
            7 => (0, -PixelShiftDistance),
            _ => (PixelShiftDistance, -PixelShiftDistance)
        };
    }
}
