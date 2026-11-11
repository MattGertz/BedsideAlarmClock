namespace BedsideAlarmClock.Alarms;

internal sealed class OverdueAlarmRegistrationException(AlarmRegistration registration)
    : InvalidOperationException("A due or overdue alarm registration must be resolved before planning.")
{
    public AlarmRegistration Registration { get; } = registration;
}

internal sealed class UnresolvedAlarmDeliveryException(AlarmRegistration registration)
    : InvalidOperationException("An observed delivery or playback reservation must be resolved before planning.")
{
    public AlarmRegistration Registration { get; } = registration;
}

internal static class AlarmRegistrationPlanner
{
    public static IReadOnlyList<AlarmRegistrationEntry> PlanRegularRegistrations(
        IReadOnlyList<AlarmDefinition> alarms,
        IReadOnlyList<AlarmRegistrationEntry> registrations,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(timeZone);
        AlarmRegistrationRepository.Validate(registrations);

        var occurrences = new Dictionary<Guid, AlarmOccurrence?>();
        foreach (var alarm in alarms)
        {
            if (alarm is null)
                throw new InvalidDataException("An alarm cannot be null.");
            var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, now, timeZone);
            if (!occurrences.TryAdd(alarm.Id, occurrence))
                throw new InvalidDataException("Alarm IDs must be unique.");
        }

        var identities = registrations.Select(entry => entry.Registration).ToHashSet();
        var unchanged = new HashSet<Guid>();
        var result = new List<AlarmRegistrationEntry>();
        foreach (var entry in registrations)
        {
            var state = entry.State;
            if (entry.Kind == AlarmRegistrationKind.Regular &&
                state is AlarmRegistrationState.DeliveryObserved or AlarmRegistrationState.PlaybackReserved)
                throw new UnresolvedAlarmDeliveryException(entry.Registration);
            if (entry.Kind == AlarmRegistrationKind.Regular && state == AlarmRegistrationState.Desired)
            {
                if (entry.TriggerAt <= now)
                    throw new OverdueAlarmRegistrationException(entry.Registration);
                if (occurrences.TryGetValue(entry.AlarmId, out var occurrence) &&
                    occurrence is { } next && next.Instant == entry.TriggerAt)
                    unchanged.Add(entry.AlarmId);
                else
                    state = AlarmRegistrationState.PendingCancellation;
            }

            result.Add(new AlarmRegistrationEntry
            {
                AlarmId = entry.AlarmId,
                RegistrationId = entry.RegistrationId,
                Kind = entry.Kind,
                TriggerAt = entry.TriggerAt,
                State = state,
                ObservedAt = entry.ObservedAt,
                PlaybackReservedAt = entry.PlaybackReservedAt,
                PlaybackStartedAt = entry.PlaybackStartedAt,
                MissedAt = entry.MissedAt,
                MissedReason = entry.MissedReason,
                CancellationPending = entry.CancellationPending
            });
        }

        foreach (var (alarmId, occurrence) in occurrences)
        {
            if (occurrence is not { } next || unchanged.Contains(alarmId))
                continue;
            AlarmRegistration registration;
            do
            {
                registration = new AlarmRegistration(alarmId, Guid.NewGuid(), AlarmRegistrationKind.Regular);
            } while (!identities.Add(registration));
            result.Add(new AlarmRegistrationEntry
            {
                AlarmId = registration.AlarmId,
                RegistrationId = registration.RegistrationId,
                Kind = registration.Kind,
                TriggerAt = next.Instant,
                State = AlarmRegistrationState.Desired
            });
        }

        return result;
    }
}
