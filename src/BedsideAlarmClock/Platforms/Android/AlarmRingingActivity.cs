using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using BedsideAlarmClock.Alarms;
using System.Runtime.Versioning;

namespace BedsideAlarmClock.Platforms.Android;

[Activity(Exported = false, LaunchMode = LaunchMode.SingleTop, Theme = "@android:style/Theme.Material.NoActionBar")]
public sealed class AlarmRingingActivity : Activity
{
    private AlarmPlaybackService? service;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
        {
            SetShowWhenLocked(true);
            SetTurnScreenOn(true);
        }
        else
            ShowOnLockScreenLegacy();
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    [UnsupportedOSPlatform("android27.0")]
    private void ShowOnLockScreenLegacy() =>
        Window?.AddFlags(WindowManagerFlags.ShowWhenLocked | WindowManagerFlags.TurnScreenOn);

    protected override void OnResume()
    {
        base.OnResume();
        service = AlarmPlaybackService.Current;
        if (service is not null) service.ActiveAlarmsChanged += OnActiveAlarmsChanged;
        RenderAlarms();
    }

    protected override void OnPause()
    {
        if (service is not null) service.ActiveAlarmsChanged -= OnActiveAlarmsChanged;
        service = null;
        base.OnPause();
    }

    private void OnActiveAlarmsChanged() => RunOnUiThread(RenderAlarms);

    private void RenderAlarms()
    {
        var alarms = service?.GetActiveAlarms() ?? [];
        if (alarms.Count == 0)
        {
            Finish();
            return;
        }
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(24, 24, 24, 24);
        foreach (var (registration, label, snoozeMinutes, actionError) in alarms)
        {
            var title = new TextView(this) { Text = label, TextSize = 24 };
            content.AddView(title);
            if (actionError is not null)
                content.AddView(new TextView(this) { Text = actionError, TextSize = 16 });
            var dismiss = new global::Android.Widget.Button(this) { Text = "Dismiss" };
            dismiss.Click += (_, _) => SendAction(registration, AlarmPlaybackService.DismissAction);
            content.AddView(dismiss);
            if (snoozeMinutes is { } minutes)
            {
                var snooze = new global::Android.Widget.Button(this) { Text = $"Snooze {minutes} minutes" };
                snooze.Click += (_, _) => SendAction(registration, AlarmPlaybackService.SnoozeAction);
                content.AddView(snooze);
            }
        }
        var scroll = new global::Android.Widget.ScrollView(this);
        scroll.AddView(content);
        SetContentView(scroll);
    }

    private void SendAction(AlarmRegistration registration, string action)
    {
        using var intent = AlarmPlaybackService.CreateActionIntent(this, registration, action);
        try
        {
            StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BedsideAlarm", $"Could not send alarm action: {ex}");
            Toast.MakeText(this, "Could not control the alarm. Use its notification.", ToastLength.Long)?.Show();
        }
    }
}
