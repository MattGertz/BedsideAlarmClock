namespace BedsideAlarmClock.Alarms;

internal sealed class AlarmRegistrationReconciler(
    IAlarmRegistrationStore store,
    IAlarmRegistrationScheduler scheduler,
    TimeProvider timeProvider)
{
    private readonly IAlarmRegistrationStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAlarmRegistrationScheduler scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task ReconcileRegularAsync(IReadOnlyList<AlarmDefinition> alarms, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        ArgumentNullException.ThrowIfNull(timeZone);
        var existing = await store.LoadAsync();
        var plan = AlarmRegistrationPlanner.PlanRegularRegistrations(
            alarms, existing, timeProvider.GetUtcNow(), timeZone);
        await store.SaveAsync(plan);

        foreach (var entry in plan)
        {
            if (entry.Kind == AlarmRegistrationKind.Regular && entry.State == AlarmRegistrationState.Desired)
                scheduler.Schedule(entry.Registration, entry.TriggerAt);
        }

        var cancellations = plan.Where(entry =>
            entry.Kind == AlarmRegistrationKind.Regular &&
            (entry.State == AlarmRegistrationState.PendingCancellation ||
                (entry.State is AlarmRegistrationState.Missed or AlarmRegistrationState.PlaybackCompleted &&
                 entry.CancellationPending))).ToList();
        foreach (var entry in cancellations)
            scheduler.Cancel(entry.Registration);
        if (cancellations.Count > 0)
        {
            foreach (var entry in cancellations.Where(entry =>
                entry.State is AlarmRegistrationState.Missed or AlarmRegistrationState.PlaybackCompleted))
                entry.CancellationPending = false;
            await store.SaveAsync(plan.Where(entry =>
                entry.Kind != AlarmRegistrationKind.Regular || entry.State != AlarmRegistrationState.PendingCancellation).ToList());
        }
    }
}
