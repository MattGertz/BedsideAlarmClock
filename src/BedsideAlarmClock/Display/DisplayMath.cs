namespace BedsideAlarmClock.Display;

internal static class DisplayMath
{
    public const float MinimumBrightness = 0.01f;
    public const float MaximumBrightness = 1f;
    public const float DefaultBrightness = 0.2f;
    public const int PixelShiftDistance = 48;
    public const int PixelShiftPositionCount = 4;

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
        return (position % PixelShiftPositionCount) switch
        {
            0 => (PixelShiftDistance, -PixelShiftDistance),
            1 => (PixelShiftDistance, PixelShiftDistance),
            2 => (-PixelShiftDistance, PixelShiftDistance),
            _ => (-PixelShiftDistance, -PixelShiftDistance)
        };
    }
}
