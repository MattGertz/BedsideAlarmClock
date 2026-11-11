using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;
using BedsideAlarmClock.Alarms;
using BedsideAlarmClock.Platforms.Android;

namespace BedsideAlarmClock.AndroidSchedulerProbe;

[Activity(MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            if (Intent?.GetBooleanExtra("audio", false) == true)
            {
                AudioProbe.Schedule(this, Intent.GetStringExtra("focus_mode") ?? "best_effort");
                return;
            }
            var scheduler = new AndroidAlarmScheduler(this, typeof(ProbeReceiver), typeof(MainActivity));
            Log.Info("AlarmProbe", $"exact_access={scheduler.CanScheduleExactAlarms()}");
            if (Intent?.GetBooleanExtra("denied", false) == true)
            {
                var test = new AlarmRegistration(Guid.NewGuid(), Guid.NewGuid(), AlarmRegistrationKind.Regular);
                try
                {
                    scheduler.Schedule(test, DateTimeOffset.UtcNow.AddMinutes(2));
                    Log.Error("AlarmProbe", "ERROR denied schedule succeeded");
                }
                catch (InvalidOperationException)
                {
                    Log.Info("AlarmProbe", "denied_schedule_rejected");
                }
                return;
            }

            var alarmA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var alarmB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            var alarmC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            var registration = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var old = new AlarmRegistration(alarmA, registration, AlarmRegistrationKind.Regular);
            var next = new AlarmRegistration(alarmA, Guid.Parse("22222222-2222-2222-2222-222222222222"), AlarmRegistrationKind.Regular);
            var snooze = new AlarmRegistration(alarmA, registration, AlarmRegistrationKind.Snooze);
            var b = new AlarmRegistration(alarmB, registration, AlarmRegistrationKind.Regular);
            var c = new AlarmRegistration(alarmC, registration, AlarmRegistrationKind.Regular);
            var now = DateTimeOffset.UtcNow;
            using var state = GetSharedPreferences("probe", FileCreationMode.Private)
                ?? throw new InvalidOperationException("Probe state is unavailable.");
            using var editor = state.Edit()
                ?? throw new InvalidOperationException("Probe state cannot be edited.");
            editor.Clear();
            editor.PutLong("started", now.ToUnixTimeMilliseconds());
            if (!editor.Commit())
                throw new InvalidOperationException("Probe state could not be saved.");
            scheduler.Schedule(old, now.AddSeconds(22));
            scheduler.Schedule(next, now.AddSeconds(22));
            scheduler.Schedule(snooze, now.AddSeconds(22));
            scheduler.Schedule(b, now.AddSeconds(22));
            scheduler.Schedule(c, now.AddSeconds(22));
            scheduler.Cancel(old);
            scheduler.Cancel(old);
            scheduler.Schedule(b, now.AddSeconds(34));
            try
            {
                scheduler.Schedule(old, now.AddSeconds(-1));
                Log.Error("AlarmProbe", "ERROR past schedule succeeded");
            }
            catch (ArgumentOutOfRangeException)
            {
                Log.Info("AlarmProbe", "past_schedule_rejected");
            }
            Log.Info("AlarmProbe", "scheduled");
        }
        catch (Exception ex)
        {
            Log.Error("AlarmProbe", $"ERROR {ex}");
        }
        finally
        {
            Finish();
        }
    }
}

[BroadcastReceiver(Exported = false)]
public sealed class ProbeReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var id = intent?.GetStringExtra(AndroidAlarmScheduler.AlarmIdExtra);
        var registration = intent?.GetStringExtra(AndroidAlarmScheduler.RegistrationIdExtra);
        var kind = intent?.GetIntExtra(AndroidAlarmScheduler.KindExtra, -1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Log.Info("AlarmProbe", $"delivery id={id} registration={registration} kind={kind} time={now}");

        var bit = (id, registration, kind) switch
        {
            ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "22222222222222222222222222222222", 0) => 1,
            ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "11111111111111111111111111111111", 1) => 2,
            ("cccccccccccccccccccccccccccccccc", "11111111111111111111111111111111", 0) => 4,
            ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "11111111111111111111111111111111", 0) => 8,
            _ => 0
        };
        using var state = context?.GetSharedPreferences("probe", FileCreationMode.Private);
        if (bit == 0 || state is null)
        {
            Log.Error("AlarmProbe", "ERROR unexpected delivery or unavailable state");
            return;
        }
        var mask = state.GetInt("mask", 0);
        var started = state.GetLong("started", 0);
        if ((mask & bit) != 0 || started == 0 || (bit == 8 && now - started < 31000))
        {
            Log.Error("AlarmProbe", "ERROR duplicate or premature delivery");
            return;
        }
        mask |= bit;
        using var editor = state.Edit();
        if (editor is null)
        {
            Log.Error("AlarmProbe", "ERROR saving delivery state");
            return;
        }
        editor.PutInt("mask", mask);
        if (!editor.Commit())
        {
            Log.Error("AlarmProbe", "ERROR saving delivery state");
            return;
        }
        if (bit == 8)
            Log.Info("AlarmProbe", mask == 15 ? "REGISTRATION_PROBE_PASS" : "ERROR missing deliveries");
    }
}
