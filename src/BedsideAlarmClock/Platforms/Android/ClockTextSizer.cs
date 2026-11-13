using Android.Widget;
using BedsideAlarmClock.Display;

namespace BedsideAlarmClock.Platforms.Android;

internal static class ClockTextSizer
{
    private const float SafetyPixels = 8;

    public static void Fit(Label label, double availableWidth, double availableHeight)
    {
        if (availableWidth <= 0 || availableHeight <= 0 || label.Handler?.PlatformView is not TextView textView)
        {
            return;
        }

        var density = DeviceDisplay.MainDisplayInfo.Density;
        var referenceWidth = textView.Paint?.MeasureText("10:00:00 AM") ?? 0;
        var metrics = textView.Paint?.GetFontMetrics();
        var referenceHeight = metrics is null ? 0 : metrics.Bottom - metrics.Top;
        var availableWidthPixels = availableWidth * density - SafetyPixels
            - textView.CompoundPaddingLeft - textView.CompoundPaddingRight;
        var availableHeightPixels = availableHeight * density - SafetyPixels
            - textView.CompoundPaddingTop - textView.CompoundPaddingBottom;
        if (referenceWidth <= 0 || referenceHeight <= 0 || availableWidthPixels <= 0 || availableHeightPixels <= 0)
        {
            return;
        }

        label.FontSize = DisplayMath.FitFontSize(
            label.FontSize, referenceWidth, referenceHeight, availableWidthPixels, availableHeightPixels);
    }
}
