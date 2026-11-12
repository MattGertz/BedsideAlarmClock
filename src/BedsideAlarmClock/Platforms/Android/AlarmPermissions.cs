using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AlarmPermissions
{
    public static bool NotificationsEnabled(Context context)
    {
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService)
            ?? throw new InvalidOperationException("Notification manager is unavailable.");
        if (OperatingSystem.IsAndroidVersionAtLeast(24) && !manager.AreNotificationsEnabled()) return false;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var channel = manager.GetNotificationChannel("active-alarms");
            if (channel is not null && channel.Importance == NotificationImportance.None) return false;
        }
        return true;
    }

    public static bool FullScreenEnabled(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34)) return true;
        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService)
            ?? throw new InvalidOperationException("Notification manager is unavailable.");
        return manager.CanUseFullScreenIntent();
    }

    public static void OpenNotifications(Context context)
    {
        using var intent = new Intent(OperatingSystem.IsAndroidVersionAtLeast(26)
            ? Settings.ActionAppNotificationSettings : Settings.ActionApplicationDetailsSettings);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            intent.PutExtra(Settings.ExtraAppPackage, context.PackageName);
        else
            intent.SetData(global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public static void OpenExactAlarms(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(31))
            throw new InvalidOperationException("This Android version does not require exact-alarm access.");
        using var intent = new Intent(Settings.ActionRequestScheduleExactAlarm);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public static void OpenFullScreen(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(34))
            throw new InvalidOperationException("This Android version does not require full-screen alarm access.");
        using var intent = new Intent(Settings.ActionManageAppUseFullScreenIntent);
        intent.SetData(global::Android.Net.Uri.Parse($"package:{context.PackageName}"));
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    public static bool NeedsNotificationPermission(Activity activity) =>
        OperatingSystem.IsAndroidVersionAtLeast(33) &&
        activity.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted;
}
