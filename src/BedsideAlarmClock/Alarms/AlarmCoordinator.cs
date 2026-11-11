namespace BedsideAlarmClock.Alarms;

public sealed class AlarmConflictException(string message) : Exception(message);

internal enum RegularDeliveryInspection
{
    Stale,
    Early,
    DueOrLate
}

internal enum RegularDeliveryRecordResult
{
    Stale,
    Early,
    Recorded,
    AlreadyRecorded
}

internal enum MissedAlarmRecordResult
{
    Stale,
    NotMissed,
    Recorded,
    AlreadyRecorded,
    PlaybackUnresolved
}

internal enum PlaybackReservationResult
{
    Reserved,
    AlreadyReserved,
    Stale,
    NotObserved,
    ClockUncertain,
    Missed
}

internal enum PlaybackStartResult
{
    Started,
    AlreadyStarted,
    Stale,
    NotReserved,
    ClockUncertain,
    Missed
}

internal enum PlaybackCompletionResult
{
    Completed,
    AlreadyCompleted,
    Stale,
    NotStarted
}

internal enum RecoveryClockStatus
{
    Uncertain,
    Trusted
}

internal enum MissedRecoveryUnresolvedReason
{
    ClockUncertain,
    DeliveryObserved,
    PlaybackReserved,
    PlaybackStarted,
    WithinWindow,
    Stale
}

internal sealed record MissedRecoveryUnresolved(
    AlarmRegistration Registration,
    MissedRecoveryUnresolvedReason Reason);

internal sealed record MissedRecoveryResult(
    IReadOnlyList<AlarmRegistration> Recorded,
    IReadOnlyList<MissedRecoveryUnresolved> Unresolved);

public sealed class AlarmCoordinator(AlarmRepository repository)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AlarmRepository repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<IReadOnlyList<AlarmDefinition>> LoadAsync()
    {
        await gate.WaitAsync();
        try
        {
            return await repository.LoadAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<AlarmDefinition>> AddAsync(AlarmDefinition alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return MutateAsync(alarms => alarms.Add(alarm));
    }

    public Task<IReadOnlyList<AlarmDefinition>> ReplaceAsync(AlarmDefinition expected, AlarmDefinition updated) =>
        MutateAsync(alarms =>
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(updated);
            if (updated.Id != expected.Id)
                throw new ArgumentException("An alarm's ID cannot be changed.", nameof(updated));
            alarms[FindUnchanged(alarms, expected)] = updated;
        });

    public Task<IReadOnlyList<AlarmDefinition>> DeleteAsync(AlarmDefinition expected) =>
        MutateAsync(alarms => alarms.RemoveAt(FindUnchanged(alarms, expected)));

    internal async Task ReconcileRegularAsync(AlarmRegistrationReconciler reconciler, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(timeZone);
        await gate.WaitAsync();
        try
        {
            var alarms = await repository.LoadAsync();
            await reconciler.ReconcileRegularAsync(alarms, timeZone);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RegularDeliveryInspection> InspectRegularDeliveryAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ValidateRegularRegistration(registration);

        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            return InspectRegularDelivery(alarms, registrations, registration, receivedAt, timeZone);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RegularDeliveryRecordResult> RecordRegularDeliveryAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ValidateRegularRegistration(registration);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null)
                return RegularDeliveryRecordResult.Stale;
            if (entry.State is AlarmRegistrationState.DeliveryObserved or AlarmRegistrationState.PlaybackReserved or
                AlarmRegistrationState.PlaybackStarted or AlarmRegistrationState.PlaybackCompleted)
                return RegularDeliveryRecordResult.AlreadyRecorded;
            var inspection = InspectRegularDelivery(alarms, registrations, registration, receivedAt, timeZone);
            if (inspection == RegularDeliveryInspection.Stale)
                return RegularDeliveryRecordResult.Stale;
            if (inspection == RegularDeliveryInspection.Early)
                return RegularDeliveryRecordResult.Early;

            entry.State = AlarmRegistrationState.DeliveryObserved;
            entry.ObservedAt = receivedAt;
            await registrationRepository.SaveAsync(registrations);
            return RegularDeliveryRecordResult.Recorded;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlaybackReservationResult> ReserveRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);
        ValidateRegularRegistration(registration);
        if (!Enum.IsDefined(clockStatus))
            throw new ArgumentOutOfRangeException(nameof(clockStatus));

        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null || entry.State is AlarmRegistrationState.PendingCancellation or
                AlarmRegistrationState.Missed or AlarmRegistrationState.PlaybackCompleted)
                return PlaybackReservationResult.Stale;
            if (entry.State is AlarmRegistrationState.PlaybackReserved or AlarmRegistrationState.PlaybackStarted)
                return PlaybackReservationResult.AlreadyReserved;
            if (entry.State != AlarmRegistrationState.DeliveryObserved)
                return PlaybackReservationResult.NotObserved;
            if (!IsCurrentRegularOccurrence(alarms, entry, timeZone))
                return PlaybackReservationResult.Stale;
            if (clockStatus == RecoveryClockStatus.Uncertain)
                return PlaybackReservationResult.ClockUncertain;

            var decisionAt = timeProvider.GetUtcNow();
            if (entry.ObservedAt is not { } observedAt)
                throw new InvalidDataException("An observed delivery requires a callback time.");
            var timing = AlarmDeliveryTiming.Evaluate(
                entry.TriggerAt, observedAt, decisionAt, knownUnavailableAtTrigger);
            if (timing == AlarmDeliveryTimingDecision.Missed)
                return PlaybackReservationResult.Missed;

            entry.State = AlarmRegistrationState.PlaybackReserved;
            entry.PlaybackReservedAt = decisionAt;
            await registrationRepository.SaveAsync(registrations);
            return PlaybackReservationResult.Reserved;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlaybackStartResult> StartReservedRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger,
        Action startPlayback)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(startPlayback);
        ValidateRegularRegistration(registration);
        if (!Enum.IsDefined(clockStatus))
            throw new ArgumentOutOfRangeException(nameof(clockStatus));

        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null || entry.State is AlarmRegistrationState.PendingCancellation or
                AlarmRegistrationState.Missed or AlarmRegistrationState.PlaybackCompleted)
                return PlaybackStartResult.Stale;
            if (entry.State == AlarmRegistrationState.PlaybackStarted)
            {
                await DisableDeliveredOneTimeAsync(alarms, entry, timeZone);
                return PlaybackStartResult.AlreadyStarted;
            }
            if (entry.State != AlarmRegistrationState.PlaybackReserved)
                return PlaybackStartResult.NotReserved;
            if (!IsCurrentRegularOccurrence(alarms, entry, timeZone))
                return PlaybackStartResult.Stale;
            if (clockStatus == RecoveryClockStatus.Uncertain)
                return PlaybackStartResult.ClockUncertain;

            var decisionAt = timeProvider.GetUtcNow();
            if (entry.PlaybackReservedAt is not { } reservedAt || entry.ObservedAt is not { } observedAt)
                throw new InvalidDataException("A playback reservation requires callback and reservation times.");
            if (decisionAt < reservedAt)
                throw new InvalidOperationException("The clock moved back before the playback reservation.");
            if (AlarmDeliveryTiming.Evaluate(entry.TriggerAt, observedAt, decisionAt, knownUnavailableAtTrigger)
                == AlarmDeliveryTimingDecision.Missed)
                return PlaybackStartResult.Missed;

            startPlayback();
            entry.State = AlarmRegistrationState.PlaybackStarted;
            entry.PlaybackStartedAt = decisionAt;
            await registrationRepository.SaveAsync(registrations);
            await DisableDeliveredOneTimeAsync(alarms, entry, timeZone);
            return PlaybackStartResult.Started;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlaybackCompletionResult> CompleteStartedRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ValidateRegularRegistration(registration);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null || entry.State is AlarmRegistrationState.PendingCancellation or AlarmRegistrationState.Missed)
                return PlaybackCompletionResult.Stale;
            if (entry.State is not (AlarmRegistrationState.PlaybackStarted or AlarmRegistrationState.PlaybackCompleted))
                return PlaybackCompletionResult.NotStarted;

            var wasCompleted = entry.State == AlarmRegistrationState.PlaybackCompleted;
            if (!wasCompleted)
            {
                entry.State = AlarmRegistrationState.PlaybackCompleted;
                entry.CancellationPending = true;
                await registrationRepository.SaveAsync(registrations);
            }

            await DisableDeliveredOneTimeAsync(alarms, entry, timeZone);
            return wasCompleted ? PlaybackCompletionResult.AlreadyCompleted : PlaybackCompletionResult.Completed;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<MissedAlarmRecordResult> RecordMissedRegularAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset decisionAt,
        bool knownUnavailableAtTrigger,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ValidateRegularRegistration(registration);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            var result = MarkMissed(alarms, registrations, entry, decisionAt, knownUnavailableAtTrigger, timeZone);
            if (result == MissedAlarmRecordResult.Recorded)
                await registrationRepository.SaveAsync(registrations);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<MissedRecoveryResult> RecoverUnobservedRegularAsync(
        IAlarmRegistrationStore registrationRepository,
        DateTimeOffset decisionAt,
        TimeZoneInfo timeZone,
        RecoveryClockStatus clockStatus,
        IReadOnlySet<AlarmRegistration> knownUnavailableAtTrigger)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(knownUnavailableAtTrigger);
        if (!Enum.IsDefined(clockStatus))
            throw new ArgumentOutOfRangeException(nameof(clockStatus));
        foreach (var registration in knownUnavailableAtTrigger)
            ValidateRegularRegistration(registration);

        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var recorded = new List<AlarmRegistration>();
            var unresolved = new List<MissedRecoveryUnresolved>();
            foreach (var entry in registrations)
            {
                if (entry.Kind != AlarmRegistrationKind.Regular)
                    continue;
                if (entry.State == AlarmRegistrationState.DeliveryObserved)
                {
                    unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.DeliveryObserved));
                    continue;
                }
                if (entry.State == AlarmRegistrationState.PlaybackReserved)
                {
                    unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.PlaybackReserved));
                    continue;
                }
                if (entry.State == AlarmRegistrationState.PlaybackStarted)
                {
                    unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.PlaybackStarted));
                    continue;
                }
                if (entry.State == AlarmRegistrationState.Desired && clockStatus == RecoveryClockStatus.Uncertain)
                {
                    unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.ClockUncertain));
                    continue;
                }
                if (entry.State != AlarmRegistrationState.Desired || entry.TriggerAt > decisionAt)
                    continue;

                var result = MarkMissed(alarms, registrations, entry, decisionAt,
                    knownUnavailableAtTrigger.Contains(entry.Registration), timeZone);
                switch (result)
                {
                    case MissedAlarmRecordResult.Recorded:
                        recorded.Add(entry.Registration);
                        break;
                    case MissedAlarmRecordResult.NotMissed:
                        unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.WithinWindow));
                        break;
                    case MissedAlarmRecordResult.Stale:
                        unresolved.Add(new(entry.Registration, MissedRecoveryUnresolvedReason.Stale));
                        break;
                    default:
                        throw new InvalidOperationException("Unexpected recovery transition result.");
                }
            }
            if (recorded.Count > 0)
                await registrationRepository.SaveAsync(registrations);
            return new MissedRecoveryResult(recorded.ToArray(), unresolved.ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    private static MissedAlarmRecordResult MarkMissed(
        IReadOnlyList<AlarmDefinition> alarms,
        IReadOnlyList<AlarmRegistrationEntry> registrations,
        AlarmRegistrationEntry? entry,
        DateTimeOffset decisionAt,
        bool knownUnavailableAtTrigger,
        TimeZoneInfo timeZone)
    {
        if (entry is null || entry.State is AlarmRegistrationState.PendingCancellation or AlarmRegistrationState.PlaybackCompleted)
            return MissedAlarmRecordResult.Stale;
        if (entry.State == AlarmRegistrationState.Missed)
            return MissedAlarmRecordResult.AlreadyRecorded;
        if (entry.State is AlarmRegistrationState.PlaybackReserved or AlarmRegistrationState.PlaybackStarted)
            return MissedAlarmRecordResult.PlaybackUnresolved;
        if (entry.State == AlarmRegistrationState.Desired)
        {
            var inspection = InspectRegularDelivery(alarms, registrations, entry.Registration, decisionAt, timeZone);
            if (inspection == RegularDeliveryInspection.Stale)
                return MissedAlarmRecordResult.Stale;
            if (inspection == RegularDeliveryInspection.Early)
                return MissedAlarmRecordResult.NotMissed;
        }
        else if (entry.ObservedAt > decisionAt)
            throw new InvalidOperationException("The missed decision precedes the observed callback.");

        if (!AlarmDeliveryTiming.IsMissed(entry.TriggerAt, decisionAt, knownUnavailableAtTrigger))
            return MissedAlarmRecordResult.NotMissed;

        entry.State = AlarmRegistrationState.Missed;
        entry.MissedAt = decisionAt;
        entry.MissedReason = knownUnavailableAtTrigger
            ? MissedAlarmReason.KnownOutage : MissedAlarmReason.DeadlineExceeded;
        entry.CancellationPending = true;
        return MissedAlarmRecordResult.Recorded;
    }

    private static RegularDeliveryInspection InspectRegularDelivery(
        IReadOnlyList<AlarmDefinition> alarms,
        IReadOnlyList<AlarmRegistrationEntry> registrations,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        var entry = registrations.SingleOrDefault(item =>
            item.Registration == registration && item.State == AlarmRegistrationState.Desired);
        if (entry is null || !IsCurrentRegularOccurrence(alarms, entry, timeZone))
            return RegularDeliveryInspection.Stale;

        return receivedAt < entry.TriggerAt
            ? RegularDeliveryInspection.Early : RegularDeliveryInspection.DueOrLate;
    }

    private static bool IsCurrentRegularOccurrence(
        IReadOnlyList<AlarmDefinition> alarms, AlarmRegistrationEntry entry, TimeZoneInfo timeZone)
    {
        var alarm = alarms.SingleOrDefault(item => item.Id == entry.AlarmId);
        if (alarm is null)
            return false;
        var beforeTrigger = entry.TriggerAt.ToUniversalTime().AddTicks(-1);
        var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, beforeTrigger, timeZone);
        return occurrence is { } current && current.Instant == entry.TriggerAt;
    }

    private async Task DisableDeliveredOneTimeAsync(
        IReadOnlyList<AlarmDefinition> alarms, AlarmRegistrationEntry entry, TimeZoneInfo timeZone)
    {
        var alarm = alarms.SingleOrDefault(item => item.Id == entry.AlarmId);
        if (alarm is { Enabled: true, IsRepeating: false } && IsCurrentRegularOccurrence(alarms, entry, timeZone))
        {
            alarm.Enabled = false;
            await repository.SaveAsync(alarms);
        }
    }

    private static void ValidateRegularRegistration(AlarmRegistration registration)
    {
        if (registration.AlarmId == Guid.Empty || registration.RegistrationId == Guid.Empty ||
            registration.Kind != AlarmRegistrationKind.Regular)
            throw new ArgumentException("A regular delivery requires valid identifiers and kind.", nameof(registration));
    }

    private async Task<IReadOnlyList<AlarmDefinition>> MutateAsync(Action<List<AlarmDefinition>> mutation)
    {
        await gate.WaitAsync();
        try
        {
            var alarms = (await repository.LoadAsync()).ToList();
            mutation(alarms);
            await repository.SaveAsync(alarms);
            return alarms;
        }
        finally
        {
            gate.Release();
        }
    }

    private static int FindUnchanged(List<AlarmDefinition> alarms, AlarmDefinition expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var index = alarms.FindIndex(alarm => alarm.Id == expected.Id);
        if (index < 0 || !SameDefinition(alarms[index], expected))
            throw new AlarmConflictException("The alarm changed or was deleted. Reload it before making changes.");
        return index;
    }

    private static bool SameDefinition(AlarmDefinition first, AlarmDefinition second) =>
        first.Id == second.Id && first.Hour == second.Hour && first.Minute == second.Minute &&
        first.Enabled == second.Enabled && first.IsRepeating == second.IsRepeating &&
        first.Days == second.Days && first.Label == second.Label &&
        first.OnceDate == second.OnceDate && first.SkipDate == second.SkipDate;
}
