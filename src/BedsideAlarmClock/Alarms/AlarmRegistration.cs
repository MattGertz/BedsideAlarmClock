namespace BedsideAlarmClock.Alarms;

internal enum AlarmRegistrationKind
{
    Regular,
    Snooze
}

internal readonly record struct AlarmRegistration(
    Guid AlarmId, Guid RegistrationId, AlarmRegistrationKind Kind);

internal interface IAlarmRegistrationScheduler
{
    void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt);
    void Cancel(AlarmRegistration registration);
}
