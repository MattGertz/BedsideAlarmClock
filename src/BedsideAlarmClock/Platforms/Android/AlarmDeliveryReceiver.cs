using Android.Content;
using Android.Util;

namespace BedsideAlarmClock.Platforms.Android;

[BroadcastReceiver(Exported = false)]
public sealed class AlarmDeliveryReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null)
        {
            Log.Error("BedsideAlarm", "Alarm delivery had no context or intent.");
            return;
        }

        Log.Info("BedsideAlarm", $"Alarm received: {intent.Data}");
        try
        {
            using var service = new Intent(context, typeof(AlarmPlaybackService));
            service.SetAction(AlarmPlaybackService.DeliverAction);
            service.PutExtra(AndroidAlarmScheduler.AlarmIdExtra, intent.GetStringExtra(AndroidAlarmScheduler.AlarmIdExtra));
            service.PutExtra(AndroidAlarmScheduler.RegistrationIdExtra, intent.GetStringExtra(AndroidAlarmScheduler.RegistrationIdExtra));
            service.PutExtra(AndroidAlarmScheduler.KindExtra, intent.GetIntExtra(AndroidAlarmScheduler.KindExtra, -1));
            service.PutExtra("received_at_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(service);
            else
                context.StartService(service);
        }
        catch (Exception ex)
        {
            Log.Error("BedsideAlarm", $"Could not start alarm playback service: {ex}");
        }
    }
}
