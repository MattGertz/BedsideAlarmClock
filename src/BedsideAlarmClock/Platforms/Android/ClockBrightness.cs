using Android.Views;
using BedsideAlarmClock.Display;
using Window = Android.Views.Window;

namespace BedsideAlarmClock.Platforms.Android;

internal sealed class ClockBrightness : IClockBrightness
{
    private const string BrightnessKey = "clock.brightness";
    private float _brightness = Preferences.Default.Get(BrightnessKey, DisplayMath.DefaultBrightness);
    private bool _active;
    private Window? _window;

    public float Brightness => _brightness;

    public void Activate()
    {
        _active = true;
        if (_window is { } window)
        {
            ApplyClockWindow(window);
        }
    }

    public void Deactivate()
    {
        _active = false;
        if (_window is { } window)
        {
            ResetWindow(window);
        }
    }

    public void AttachWindow(Window window)
    {
        _window = window;
        if (_active)
        {
            ApplyClockWindow(window);
        }
    }

    public void DetachWindow()
    {
        if (_window is { } window)
        {
            ResetWindow(window);
            _window = null;
        }

        _active = false;
    }

    public void SetBrightness(float brightness)
    {
        _brightness = Math.Clamp(brightness, DisplayMath.MinimumBrightness, DisplayMath.MaximumBrightness);
        if (_active && _window is { } window)
        {
            ApplyBrightness(window, _brightness);
        }
    }

    public void Save()
    {
        Preferences.Default.Set(BrightnessKey, _brightness);
    }

    private void ApplyClockWindow(Window window)
    {
        window.AddFlags(WindowManagerFlags.KeepScreenOn);
        ApplyBrightness(window, _brightness);
    }

    private static void ResetWindow(Window window)
    {
        window.ClearFlags(WindowManagerFlags.KeepScreenOn);
        ApplyBrightness(window, WindowManagerLayoutParams.BrightnessOverrideNone);
    }

    private static void ApplyBrightness(Window window, float brightness)
    {
        var attributes = window.Attributes
            ?? throw new InvalidOperationException("The clock window attributes are unavailable.");
        attributes.ScreenBrightness = brightness;
        window.Attributes = attributes;
    }
}
