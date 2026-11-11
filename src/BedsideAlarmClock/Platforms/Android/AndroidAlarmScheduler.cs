using Android.App;
using Android.Content;
using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Platforms.Android;

internal sealed class AndroidAlarmScheduler : IAlarmRegistrationScheduler
{
    public const string AlarmIdExtra = "alarm_id";
    public const string RegistrationIdExtra = "registration_id";
    public const string KindExtra = "registration_kind";

    private readonly Context context;
    private readonly Type receiverType;
    private readonly Type activityType;
    private readonly AlarmManager alarmManager;

    public AndroidAlarmScheduler(Context context, Type receiverType, Type activityType)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receiverType);
        ArgumentNullException.ThrowIfNull(activityType);
        if (!typeof(BroadcastReceiver).IsAssignableFrom(receiverType))
            throw new ArgumentException("The delivery component must be a broadcast receiver.", nameof(receiverType));
        if (!typeof(Activity).IsAssignableFrom(activityType))
            throw new ArgumentException("The show component must be an activity.", nameof(activityType));

        this.context = context.ApplicationContext ?? context;
        this.receiverType = receiverType;
        this.activityType = activityType;
        alarmManager = this.context.GetSystemService(Context.AlarmService) as AlarmManager
            ?? throw new InvalidOperationException("Android's alarm service is unavailable.");
    }

    public bool CanScheduleExactAlarms() =>
        !OperatingSystem.IsAndroidVersionAtLeast(31) || alarmManager.CanScheduleExactAlarms();

    public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt)
    {
        Validate(registration);
        if (triggerAt <= DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(triggerAt), "An alarm must be scheduled in the future.");
        if (!CanScheduleExactAlarms())
            throw new InvalidOperationException("Android does not allow this app to schedule exact alarms.");

        using var operation = CreateDeliveryIntent(registration, PendingIntentFlags.UpdateCurrent)
            ?? throw new InvalidOperationException("Android could not create the alarm delivery intent.");
        using var showIntent = new Intent(context, activityType);
        showIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        using var show = PendingIntent.GetActivity(context, 0, showIntent, PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Android could not create the alarm display intent.");
        using var alarm = new AlarmManager.AlarmClockInfo(triggerAt.ToUnixTimeMilliseconds(), show);
        alarmManager.SetAlarmClock(alarm, operation);
    }

    public void Cancel(AlarmRegistration registration)
    {
        Validate(registration);
        using var operation = CreateDeliveryIntent(registration, PendingIntentFlags.NoCreate);
        if (operation is null) return;
        alarmManager.Cancel(operation);
        operation.Cancel();
    }

    private PendingIntent? CreateDeliveryIntent(AlarmRegistration registration, PendingIntentFlags flags)
    {
        var uri = global::Android.Net.Uri.Parse(
            $"bedsidealarm://delivery/{registration.Kind.ToString().ToLowerInvariant()}/{registration.AlarmId:N}/{registration.RegistrationId:N}")
            ?? throw new InvalidOperationException("Could not create an alarm identity.");
        using var intent = new Intent(context, receiverType);
        intent.SetAction($"{context.PackageName}.ALARM_DELIVERY");
        intent.SetData(uri);
        intent.PutExtra(AlarmIdExtra, registration.AlarmId.ToString("N"));
        intent.PutExtra(RegistrationIdExtra, registration.RegistrationId.ToString("N"));
        intent.PutExtra(KindExtra, (int)registration.Kind);
        return PendingIntent.GetBroadcast(context, 0, intent, flags | PendingIntentFlags.Immutable);
    }

    private static void Validate(AlarmRegistration registration)
    {
        if (registration.AlarmId == Guid.Empty || registration.RegistrationId == Guid.Empty ||
            !Enum.IsDefined(registration.Kind))
            throw new ArgumentException("An alarm registration requires valid identifiers and kind.", nameof(registration));
    }
}
