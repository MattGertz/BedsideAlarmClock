using Android.App;
using Android.Content;
using Android.Util;
using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AndroidAlarmRecovery
{
    private const string Tag = "BedsideAlarm";
    private const string ChannelId = "alarm-recovery";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RunAsync(Context context)
    {
        await Gate.WaitAsync();
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("Application services are unavailable.");
            var coordinator = services.GetRequiredService<AlarmCoordinator>();
            var store = services.GetRequiredService<AlarmRegistrationRepository>();
            var reconciler = services.GetRequiredService<AlarmRegistrationReconciler>();
            var scheduler = services.GetRequiredService<AndroidAlarmScheduler>();
            try
            {
                await store.LoadExistingAsync();
            }
            catch (FileNotFoundException) when (!AndroidAlarmClockAnchor.HasScheduled(context))
            {
                // A new installation has no journal yet.
            }
            var clockStatus = AndroidAlarmClockAnchor.ReadStatus(context);
            var result = await coordinator.RecoverInactiveAsync(store, DateTimeOffset.UtcNow, TimeZoneInfo.Local,
                registration => AlarmPlaybackService.Current?.IsProcessing(registration) == true, clockStatus);
            AndroidAlarmClockAnchor.SetAnchor(context);
            if (result.AwaitingDelivery.Count > 0)
            {
                var entries = await store.LoadExistingAsync();
                var retryAt = entries.Where(entry => result.AwaitingDelivery.Contains(entry.Registration))
                    .Min(entry => entry.TriggerAt.AddSeconds(30).AddMilliseconds(1));
                ScheduleRetry(context, scheduler, retryAt);
                Log.Info(Tag, $"Recovery will retry after the delivery window: {retryAt:O}");
            }
            else
                await coordinator.ReconcileAllAsync(reconciler, TimeZoneInfo.Local);

            var journal = await store.LoadAsync();
            var reports = journal.Where(entry => entry.State is AlarmRegistrationState.Missed or
                AlarmRegistrationState.Interrupted or AlarmRegistrationState.ClockUncertain)
                .Where(entry => !Preferences.Default.Get($"alarm.report.{entry.RegistrationId:N}", false)).ToArray();
            if (reports.Length > 0)
                ShowReport(context, reports.Length);
            Log.Info(Tag, $"Recovery complete: missed={result.Missed.Count} interrupted={result.Interrupted.Count} uncertain={result.ClockUncertain.Count} awaiting={result.AwaitingDelivery.Count}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void ScheduleRetry(Context context, AndroidAlarmScheduler scheduler, DateTimeOffset retryAt)
    {
        if (!scheduler.CanScheduleExactAlarms())
            throw new InvalidOperationException("Android does not allow exact alarms.");
        using var intent = new Intent(context, typeof(AlarmRecoveryRetryReceiver));
        using var pending = PendingIntent.GetBroadcast(context, 0, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Could not create an alarm recovery request.");
        var manager = (AlarmManager?)context.GetSystemService(Context.AlarmService)
            ?? throw new InvalidOperationException("Android's alarm service is unavailable.");
        manager.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, Math.Max(retryAt.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000), pending);
    }

    public static void ShowFailure(Context context, Exception error)
    {
        Log.Error(Tag, $"Alarm recovery failed: {error}");
        Notify(context, "Alarm schedule needs attention", error.Message);
    }

    private static void ShowReport(Context context, int count) =>
        Notify(context, "Alarm delivery needs attention",
            $"{count} alarm occurrence(s) were missed, interrupted, or uncertain after a clock change.");

    private static void Notify(Context context, string title, string message)
    {
        try
        {
            var notifications = (NotificationManager?)context.GetSystemService(Context.NotificationService)
                ?? throw new InvalidOperationException("Notification manager is unavailable.");
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var channel = new NotificationChannel(ChannelId, "Alarm recovery", NotificationImportance.High);
                notifications.CreateNotificationChannel(channel);
            }
            using var intent = new Intent(context, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            using var display = PendingIntent.GetActivity(context, 1, intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
                ?? throw new InvalidOperationException("Could not create recovery notification intent.");
            using var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? new Notification.Builder(context, ChannelId) : new Notification.Builder(context);
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm);
            builder.SetContentTitle(title);
            builder.SetContentText(message);
            builder.SetContentIntent(display);
            builder.SetAutoCancel(true);
            using var notification = builder.Build() ?? throw new InvalidOperationException("Could not build recovery notification.");
            notifications.Notify(200, notification);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Could not show alarm recovery notification: {ex}");
        }
    }
}

[BroadcastReceiver(Exported = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, Intent.ActionTimeChanged, Intent.ActionTimezoneChanged])]
public sealed class AlarmRecoveryReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
#if ALARM_DELIVERY_EXPERIMENT
        if (context is not null) StartRecovery(context, GoAsync());
#endif
    }

    internal static async void StartRecovery(Context context, PendingResult? pending)
    {
        var app = context.ApplicationContext ?? context;
        try
        {
            await AndroidAlarmRecovery.RunAsync(app);
        }
        catch (Exception ex)
        {
            AndroidAlarmRecovery.ShowFailure(app, ex);
        }
        finally
        {
            pending?.Finish();
        }
    }
}

[BroadcastReceiver(Exported = false)]
public sealed class AlarmRecoveryRetryReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
#if ALARM_DELIVERY_EXPERIMENT
        if (context is not null) AlarmRecoveryReceiver.StartRecovery(context, GoAsync());
#endif
    }
}
