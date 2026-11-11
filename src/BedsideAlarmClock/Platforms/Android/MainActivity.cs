using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using System.Runtime.Versioning;

namespace BedsideAlarmClock;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnResume()
    {
        base.OnResume();
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        HideSystemBars();

        if (Shell.Current?.CurrentPage is MainPage clockPage)
        {
            clockPage.RefreshClock();
        }
    }

    protected override void OnPause()
    {
        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        base.OnPause();
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
