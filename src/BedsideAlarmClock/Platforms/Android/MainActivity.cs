using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using BedsideAlarmClock.Display;
using BedsideAlarmClock.Platforms.Android;
using System.Runtime.Versioning;

namespace BedsideAlarmClock;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private float _gestureStartX;
    private float _gestureStartY;
    private float _gestureStartBrightness;
    private bool _gestureTracking;
    private bool _adjustingBrightness;
    private ClockBrightness Brightness => IPlatformApplication.Current!.Services.GetRequiredService<ClockBrightness>();

    protected override void OnResume()
    {
        base.OnResume();
        if (Window is { } window)
        {
            Brightness.AttachWindow(window);
        }
        HideSystemBars();

        if (Shell.Current?.CurrentPage is MainPage clockPage)
        {
            clockPage.ResumeClock();
        }
    }

    protected override void OnPause()
    {
        FinishBrightnessGesture();
        if (Shell.Current?.CurrentPage is MainPage clockPage)
        {
            clockPage.PauseClock();
        }

        Brightness.DetachWindow();
        base.OnPause();
    }

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e is null)
        {
            return base.DispatchTouchEvent(e);
        }

        if (Shell.Current?.CurrentPage is not MainPage || Window?.DecorView is not { } decor)
        {
            FinishBrightnessGesture();
            return base.DispatchTouchEvent(e);
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _gestureTracking = true;
                _adjustingBrightness = false;
                _gestureStartX = e.GetX();
                _gestureStartY = e.GetY();
                _gestureStartBrightness = Brightness.Brightness;
                break;

            case MotionEventActions.Move when _gestureTracking:
                var dx = e.GetX() - _gestureStartX;
                var dy = e.GetY() - _gestureStartY;
                var touchSlop = ViewConfiguration.Get(this)?.ScaledTouchSlop ?? 0;
                if (!_adjustingBrightness && Math.Abs(dy) > touchSlop && Math.Abs(dy) > Math.Abs(dx))
                {
                    _adjustingBrightness = true;
                    using var cancel = MotionEvent.Obtain(e)
                        ?? throw new InvalidOperationException("Could not create a touch cancellation event.");
                    cancel.Action = MotionEventActions.Cancel;
                    base.DispatchTouchEvent(cancel);
                }

                if (_adjustingBrightness && decor.Height > 0)
                {
                    Brightness.SetBrightness(DisplayMath.BrightnessAfterSwipe(
                        _gestureStartBrightness, dy, decor.Height));
                    return true;
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                var consumed = _adjustingBrightness;
                FinishBrightnessGesture();
                if (consumed)
                {
                    return true;
                }

                break;

            case MotionEventActions.PointerDown:
                if (_adjustingBrightness)
                {
                    return true;
                }

                _gestureTracking = false;
                break;

            case MotionEventActions.PointerUp when _adjustingBrightness:
                return true;
        }

        return base.DispatchTouchEvent(e);
    }

    private void FinishBrightnessGesture()
    {
        if (_adjustingBrightness)
        {
            Brightness.Save();
        }

        _gestureTracking = false;
        _adjustingBrightness = false;
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);

        if (hasFocus)
        {
            HideSystemBars();
        }
    }

    private void HideSystemBars()
    {
        if (Window is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            HideSystemBarsOnAndroid30();
            return;
        }

        HideSystemBarsBeforeAndroid30();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("android30.0")]
    private void HideSystemBarsBeforeAndroid30()
    {
        if (Window is null)
        {
            return;
        }

        Window.DecorView.SystemUiFlags =
            SystemUiFlags.Fullscreen
            | SystemUiFlags.HideNavigation
            | SystemUiFlags.ImmersiveSticky
            | SystemUiFlags.LayoutStable
            | SystemUiFlags.LayoutHideNavigation
            | SystemUiFlags.LayoutFullscreen;
    }

    [SupportedOSPlatform("android30.0")]
    private void HideSystemBarsOnAndroid30()
    {
        if (Window?.InsetsController is not { } insetsController)
        {
            return;
        }

        insetsController.Hide(WindowInsets.Type.SystemBars());
        insetsController.SystemBarsBehavior =
            (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
    }
}
