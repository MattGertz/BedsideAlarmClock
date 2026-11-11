using Android.Widget;
using BedsideAlarmClock.Display;

namespace BedsideAlarmClock.Platforms.Android;

internal static class ClockTextSizer
{
    private const double MaximumFontSize = 96;
    private const float HorizontalSafetyPixels = 8;

    public static void Fit(Label label, double availableWidth)
    {
        if (availableWidth <= 0 || label.Handler?.PlatformView is not TextView textView)
        {
            return;
        }

        var density = DeviceDisplay.MainDisplayInfo.Density;
        var referenceWidth = textView.Paint?.MeasureText("10:00:00 AM") ?? 0;
        var availablePixels = availableWidth * density - HorizontalSafetyPixels
            - textView.CompoundPaddingLeft - textView.CompoundPaddingRight;
        if (referenceWidth <= 0 || availablePixels <= 0)
        {
            return;
        }

        label.FontSize = DisplayMath.FitFontSize(
            label.FontSize, referenceWidth, availablePixels, MaximumFontSize);
    }
}
