namespace BedsideAlarmClock.Alarms;

public sealed class AlarmConflictException(string message) : Exception(message);

public sealed class AlarmScheduleException(Exception innerException)
    : Exception($"The alarm change was saved, but scheduling could not be confirmed: {innerException.Message}", innerException);

internal enum RegularDeliveryInspection
{
    Stale,
    Early,
    DueOrLate
}

internal enum DeliveryRecordResult
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

internal sealed class PlaybackDeadlineExceededException : Exception;

internal enum PlaybackCompletionResult
{
    Completed,
    AlreadyCompleted,
    Stale,
    NotStarted
}

internal enum SnoozeResult
{
    Scheduled,
    Rescheduled,
    Stale,
    NotStarted,
    AlreadyCompleted
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

internal sealed record AlarmRecoveryResult(
    IReadOnlyList<AlarmRegistration> Missed,
    IReadOnlyList<AlarmRegistration> Interrupted,
    IReadOnlyList<AlarmRegistration> ClockUncertain,
    IReadOnlyList<AlarmRegistration> AwaitingDelivery);

public sealed class AlarmCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AlarmRepository repository;
    private readonly AlarmRegistrationReconciler? reconciler;
    private readonly Func<TimeZoneInfo> timeZone = () => TimeZoneInfo.Local;

    public AlarmCoordinator(AlarmRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    internal AlarmCoordinator(
        AlarmRepository repository, AlarmRegistrationReconciler reconciler, Func<TimeZoneInfo> timeZone)
        : this(repository)
    {
        this.reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        this.timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

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

    internal async Task ReconcileAllAsync(AlarmRegistrationReconciler reconciler, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(timeZone);
        await gate.WaitAsync();
        try
        {
            await reconciler.ReconcileAllAsync(await repository.LoadAsync(), timeZone);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IReadOnlyList<AlarmRegistrationEntry>> LoadRecoveryReportsAsync(IAlarmRegistrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        await gate.WaitAsync();
        try
        {
            return (await store.LoadAsync()).Where(entry => entry.State is AlarmRegistrationState.Missed or
                AlarmRegistrationState.Interrupted or AlarmRegistrationState.ClockUncertain).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<AlarmRecoveryResult> RecoverInactiveAsync(
        IAlarmRegistrationStore registrationRepository, DateTimeOffset now, TimeZoneInfo timeZone,
        Func<AlarmRegistration, bool> isActive, RecoveryClockStatus clockStatus = RecoveryClockStatus.Trusted)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(isActive);
        if (!Enum.IsDefined(clockStatus))
            throw new ArgumentOutOfRangeException(nameof(clockStatus));
        await gate.WaitAsync();
        try
        {
            var registrations = (await registrationRepository.LoadAsync()).ToList();
            var alarms = await repository.LoadAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var missed = new List<AlarmRegistration>();
            var interrupted = new List<AlarmRegistration>();
            var uncertain = new List<AlarmRegistration>();
            var awaiting = new List<AlarmRegistration>();
            var changed = false;
            foreach (var entry in registrations)
            {
                if (clockStatus == RecoveryClockStatus.Uncertain &&
                    ((entry.State == AlarmRegistrationState.Desired && entry.TriggerAt <= now) ||
                     (entry.State is AlarmRegistrationState.DeliveryObserved or AlarmRegistrationState.PlaybackReserved or
                         AlarmRegistrationState.PlaybackStarted)) && !isActive(entry.Registration))
                {
                    entry.State = AlarmRegistrationState.ClockUncertain;
                    entry.ClockUncertainAt = now;
                    entry.CancellationPending = true;
                    uncertain.Add(entry.Registration);
                    changed = true;
                    continue;
                }
                if (entry.State == AlarmRegistrationState.Desired && !IsCurrentDelivery(alarms, entry, timeZone))
                {
                    entry.State = AlarmRegistrationState.PendingCancellation;
                    changed = true;
                    continue;
                }
                if (entry.State == AlarmRegistrationState.Desired && entry.TriggerAt <= now)
                {
                    if (!AlarmDeliveryTiming.IsMissed(entry.TriggerAt, now, false))
                        awaiting.Add(entry.Registration);
                    else if (MarkMissed(alarms, entry, now, false, timeZone) == MissedAlarmRecordResult.Recorded)
                    {
                        missed.Add(entry.Registration);
                        changed = true;
                    }
                    continue;
                }
                if (entry.State is not (AlarmRegistrationState.DeliveryObserved or
                    AlarmRegistrationState.PlaybackReserved or AlarmRegistrationState.PlaybackStarted) ||
                    isActive(entry.Registration))
                    continue;
                var latest = entry.PlaybackStartedAt ?? entry.PlaybackReservedAt ?? entry.ObservedAt
                    ?? throw new InvalidDataException("A delivery has no recorded time.");
                if (now < latest)
                    throw new InvalidOperationException("The clock moved back before the recorded delivery.");
                if (entry.State != AlarmRegistrationState.PlaybackStarted &&
                    !AlarmDeliveryTiming.IsMissed(entry.TriggerAt, now, false))
                {
                    awaiting.Add(entry.Registration);
                    continue;
                }
                entry.State = AlarmRegistrationState.Interrupted;
                entry.InterruptedAt = now;
                entry.CancellationPending = true;
                interrupted.Add(entry.Registration);
                changed = true;
            }
            if (changed) await registrationRepository.SaveAsync(registrations);
            return new AlarmRecoveryResult(missed, interrupted, uncertain, awaiting);
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

    internal Task<DeliveryRecordResult> RecordRegularDeliveryAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        ValidateRegularRegistration(registration);
        return RecordDeliveryAsync(registrationRepository, registration, receivedAt, timeZone);
    }

    internal Task<DeliveryRecordResult> RecordSnoozeDeliveryAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        ValidateSnoozeRegistration(registration);
        return RecordDeliveryAsync(registrationRepository, registration, receivedAt, timeZone);
    }

    private async Task<DeliveryRecordResult> RecordDeliveryAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset receivedAt,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null)
                return DeliveryRecordResult.Stale;
            if (entry.State is AlarmRegistrationState.DeliveryObserved or AlarmRegistrationState.PlaybackReserved or
                AlarmRegistrationState.PlaybackStarted or AlarmRegistrationState.PlaybackCompleted)
                return DeliveryRecordResult.AlreadyRecorded;
            if (entry.State != AlarmRegistrationState.Desired || !IsCurrentDelivery(alarms, entry, timeZone))
                return DeliveryRecordResult.Stale;
            if (receivedAt < entry.TriggerAt)
                return DeliveryRecordResult.Early;

            entry.State = AlarmRegistrationState.DeliveryObserved;
            entry.ObservedAt = receivedAt;
            await registrationRepository.SaveAsync(registrations);
            return DeliveryRecordResult.Recorded;
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<PlaybackReservationResult> ReserveRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        TimeProvider timeProvider, TimeZoneInfo timeZone, RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger)
    {
        ValidateRegularRegistration(registration);
        return ReservePlaybackAsync(registrationRepository, registration, timeProvider, timeZone,
            clockStatus, knownUnavailableAtTrigger);
    }

    internal Task<PlaybackReservationResult> ReserveSnoozePlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        TimeProvider timeProvider, TimeZoneInfo timeZone, RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger)
    {
        ValidateSnoozeRegistration(registration);
        return ReservePlaybackAsync(registrationRepository, registration, timeProvider, timeZone,
            clockStatus, knownUnavailableAtTrigger);
    }

    private async Task<PlaybackReservationResult> ReservePlaybackAsync(
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
            if (!IsCurrentDelivery(alarms, entry, timeZone))
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

    internal Task<PlaybackStartResult> StartReservedRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        TimeProvider timeProvider, TimeZoneInfo timeZone, RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger, Action startPlayback)
    {
        ValidateRegularRegistration(registration);
        return StartReservedPlaybackAsync(registrationRepository, registration, timeProvider, timeZone,
            clockStatus, knownUnavailableAtTrigger, startPlayback);
    }

    internal Task<PlaybackStartResult> StartReservedSnoozePlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        TimeProvider timeProvider, TimeZoneInfo timeZone, RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger, Action startPlayback)
    {
        ValidateSnoozeRegistration(registration);
        return StartReservedPlaybackAsync(registrationRepository, registration, timeProvider, timeZone,
            clockStatus, knownUnavailableAtTrigger, startPlayback);
    }

    private async Task<PlaybackStartResult> StartReservedPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger,
        Action startPlayback)
    {
        ArgumentNullException.ThrowIfNull(startPlayback);
        return await StartReservedPlaybackOnPlayerAsync(registrationRepository, registration, timeProvider,
            timeZone, clockStatus, knownUnavailableAtTrigger, check =>
            {
                check();
                startPlayback();
                return Task.CompletedTask;
            });
    }

    internal async Task<PlaybackStartResult> StartReservedPlaybackOnPlayerAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        RecoveryClockStatus clockStatus,
        bool knownUnavailableAtTrigger,
        Func<Action, Task> startPlayback)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(startPlayback);
        if (registration.Kind == AlarmRegistrationKind.Regular)
            ValidateRegularRegistration(registration);
        else
            ValidateSnoozeRegistration(registration);
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
            if (!IsCurrentDelivery(alarms, entry, timeZone))
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

            DateTimeOffset? startedAt = null;
            try
            {
                await startPlayback(() =>
                {
                    if (startedAt is not null)
                        throw new InvalidOperationException("The player start check was invoked more than once.");
                    var actualStart = timeProvider.GetUtcNow();
                    if (actualStart < reservedAt)
                        throw new InvalidOperationException("The clock moved back before the playback reservation.");
                    if (AlarmDeliveryTiming.Evaluate(entry.TriggerAt, observedAt, actualStart, knownUnavailableAtTrigger)
                        == AlarmDeliveryTimingDecision.Missed)
                        throw new PlaybackDeadlineExceededException();
                    startedAt = actualStart;
                });
            }
            catch (PlaybackDeadlineExceededException)
            {
                return PlaybackStartResult.Missed;
            }
            if (startedAt is null)
                throw new InvalidOperationException("The player did not perform the required start check.");
            entry.State = AlarmRegistrationState.PlaybackStarted;
            entry.PlaybackStartedAt = startedAt;
            await registrationRepository.SaveAsync(registrations);
            await DisableDeliveredOneTimeAsync(alarms, entry, timeZone);
            return PlaybackStartResult.Started;
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<PlaybackCompletionResult> CompleteStartedRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration, TimeZoneInfo timeZone)
    {
        ValidateRegularRegistration(registration);
        return CompleteStartedPlaybackAsync(registrationRepository, registration, timeZone);
    }

    internal Task<PlaybackCompletionResult> CompleteStartedSnoozePlaybackAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration, TimeZoneInfo timeZone)
    {
        ValidateSnoozeRegistration(registration);
        return CompleteStartedPlaybackAsync(registrationRepository, registration, timeZone);
    }

    private async Task<PlaybackCompletionResult> CompleteStartedPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
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

    internal Task<SnoozeResult> SnoozeStartedRegularPlaybackAsync(
        IAlarmRegistrationStore registrationRepository, IAlarmRegistrationScheduler scheduler,
        AlarmRegistration registration, TimeProvider timeProvider, TimeZoneInfo timeZone, int snoozeMinutes = 10)
    {
        ValidateRegularRegistration(registration);
        return SnoozeStartedPlaybackAsync(registrationRepository, scheduler, registration, timeProvider, timeZone, snoozeMinutes);
    }

    internal Task<SnoozeResult> SnoozeStartedSnoozePlaybackAsync(
        IAlarmRegistrationStore registrationRepository, IAlarmRegistrationScheduler scheduler,
        AlarmRegistration registration, TimeProvider timeProvider, TimeZoneInfo timeZone, int snoozeMinutes = 10)
    {
        ValidateSnoozeRegistration(registration);
        return SnoozeStartedPlaybackAsync(registrationRepository, scheduler, registration, timeProvider, timeZone, snoozeMinutes);
    }

    private async Task<SnoozeResult> SnoozeStartedPlaybackAsync(
        IAlarmRegistrationStore registrationRepository,
        IAlarmRegistrationScheduler scheduler,
        AlarmRegistration registration,
        TimeProvider timeProvider,
        TimeZoneInfo timeZone,
        int snoozeMinutes)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (snoozeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(snoozeMinutes));
        await gate.WaitAsync();
        try
        {
            var registrations = (await registrationRepository.LoadExistingAsync()).ToList();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var source = registrations.SingleOrDefault(item => item.Registration == registration);
            if (source is null || source.State is AlarmRegistrationState.PendingCancellation or AlarmRegistrationState.Missed)
                return SnoozeResult.Stale;
            var snooze = registrations.SingleOrDefault(item =>
                item.Kind == AlarmRegistrationKind.Snooze &&
                item.AlarmId == registration.AlarmId &&
                item.SnoozedFromRegistrationId == registration.RegistrationId &&
                (item.SnoozedFromKind ?? AlarmRegistrationKind.Regular) == registration.Kind);
            var isRetry = snooze is not null;
            if (!isRetry && source.State == AlarmRegistrationState.PlaybackCompleted)
                return SnoozeResult.AlreadyCompleted;
            if (!isRetry && source.State != AlarmRegistrationState.PlaybackStarted)
                return SnoozeResult.NotStarted;

            var now = timeProvider.GetUtcNow();
            if (source.PlaybackStartedAt is not { } startedAt || now < startedAt)
                throw new InvalidOperationException("The clock moved back before playback started.");

            if (snooze is null)
            {
                var identity = new AlarmRegistration(registration.AlarmId, Guid.NewGuid(), AlarmRegistrationKind.Snooze);
                while (registrations.Any(item => item.Registration == identity))
                    identity = identity with { RegistrationId = Guid.NewGuid() };
                snooze = new AlarmRegistrationEntry
                {
                    AlarmId = identity.AlarmId,
                    RegistrationId = identity.RegistrationId,
                    Kind = identity.Kind,
                    TriggerAt = now.AddMinutes(snoozeMinutes),
                    State = AlarmRegistrationState.Desired,
                    SnoozedFromRegistrationId = registration.RegistrationId,
                    SnoozedFromKind = registration.Kind
                };
                source.State = AlarmRegistrationState.PlaybackCompleted;
                source.CancellationPending = true;
                registrations.Add(snooze);
                await registrationRepository.SaveAsync(registrations);
            }
            if (snooze.State != AlarmRegistrationState.Desired)
                throw new InvalidOperationException("The snooze has already been resolved; it cannot be rescheduled.");
            await DisableDeliveredOneTimeAsync(alarms, source, timeZone);
            if (snooze.TriggerAt <= timeProvider.GetUtcNow())
                throw new InvalidOperationException("The snooze is due or overdue and cannot be scheduled as a future alarm.");
            scheduler.Schedule(snooze.Registration, snooze.TriggerAt);
            return isRetry ? SnoozeResult.Rescheduled : SnoozeResult.Scheduled;
        }
        finally
        {
            gate.Release();
        }
    }

    internal Task<MissedAlarmRecordResult> RecordMissedRegularAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        DateTimeOffset decisionAt, bool knownUnavailableAtTrigger, TimeZoneInfo timeZone)
    {
        ValidateRegularRegistration(registration);
        return RecordMissedAsync(registrationRepository, registration, decisionAt, knownUnavailableAtTrigger, timeZone);
    }

    internal Task<MissedAlarmRecordResult> RecordMissedSnoozeAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration,
        DateTimeOffset decisionAt, bool knownUnavailableAtTrigger, TimeZoneInfo timeZone)
    {
        ValidateSnoozeRegistration(registration);
        return RecordMissedAsync(registrationRepository, registration, decisionAt, knownUnavailableAtTrigger, timeZone);
    }

    internal async Task<MissedAlarmRecordResult> RecordUnstartedReservationMissedAsync(
        IAlarmRegistrationStore registrationRepository, AlarmRegistration registration, DateTimeOffset decisionAt)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        if (registration.Kind == AlarmRegistrationKind.Regular)
            ValidateRegularRegistration(registration);
        else
            ValidateSnoozeRegistration(registration);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            if (entry is null || entry.State is AlarmRegistrationState.PendingCancellation or AlarmRegistrationState.PlaybackCompleted)
                return MissedAlarmRecordResult.Stale;
            if (entry.State == AlarmRegistrationState.Missed)
                return MissedAlarmRecordResult.AlreadyRecorded;
            if (entry.State != AlarmRegistrationState.PlaybackReserved)
                return MissedAlarmRecordResult.PlaybackUnresolved;
            if (decisionAt < entry.PlaybackReservedAt)
                throw new InvalidOperationException("The missed decision precedes the reservation.");
            if (!AlarmDeliveryTiming.IsMissed(entry.TriggerAt, decisionAt, false))
                return MissedAlarmRecordResult.NotMissed;

            entry.State = AlarmRegistrationState.Missed;
            entry.PlaybackReservedAt = null;
            entry.MissedAt = decisionAt;
            entry.MissedReason = MissedAlarmReason.DeadlineExceeded;
            entry.CancellationPending = true;
            await registrationRepository.SaveAsync(registrations);
            return MissedAlarmRecordResult.Recorded;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MissedAlarmRecordResult> RecordMissedAsync(
        IAlarmRegistrationStore registrationRepository,
        AlarmRegistration registration,
        DateTimeOffset decisionAt,
        bool knownUnavailableAtTrigger,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(registrationRepository);
        ArgumentNullException.ThrowIfNull(timeZone);
        await gate.WaitAsync();
        try
        {
            var registrations = await registrationRepository.LoadExistingAsync();
            var alarms = await repository.LoadExistingAsync();
            AlarmRegistrationRepository.Validate(registrations);
            var entry = registrations.SingleOrDefault(item => item.Registration == registration);
            var result = MarkMissed(alarms, entry, decisionAt, knownUnavailableAtTrigger, timeZone);
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

                var result = MarkMissed(alarms, entry, decisionAt,
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
            if (!IsCurrentDelivery(alarms, entry, timeZone))
                return MissedAlarmRecordResult.Stale;
            if (decisionAt < entry.TriggerAt)
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

    private static bool IsCurrentDelivery(
        IReadOnlyList<AlarmDefinition> alarms, AlarmRegistrationEntry entry, TimeZoneInfo timeZone) =>
        entry.Kind == AlarmRegistrationKind.Regular
            ? IsCurrentRegularOccurrence(alarms, entry, timeZone)
            : entry.SnoozedFromRegistrationId is not null && alarms.Any(alarm => alarm.Id == entry.AlarmId);

    private async Task DisableDeliveredOneTimeAsync(
        IReadOnlyList<AlarmDefinition> alarms, AlarmRegistrationEntry entry, TimeZoneInfo timeZone)
    {
        var alarm = alarms.SingleOrDefault(item => item.Id == entry.AlarmId);
        if (entry.Kind == AlarmRegistrationKind.Regular &&
            alarm is { Enabled: true, IsRepeating: false } && IsCurrentRegularOccurrence(alarms, entry, timeZone))
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

    private static void ValidateSnoozeRegistration(AlarmRegistration registration)
    {
        if (registration.AlarmId == Guid.Empty || registration.RegistrationId == Guid.Empty ||
            registration.Kind != AlarmRegistrationKind.Snooze)
            throw new ArgumentException("A snooze delivery requires valid identifiers and kind.", nameof(registration));
    }

    private async Task<IReadOnlyList<AlarmDefinition>> MutateAsync(Action<List<AlarmDefinition>> mutation)
    {
        await gate.WaitAsync();
        try
        {
            var alarms = (await repository.LoadAsync()).ToList();
            mutation(alarms);
            await repository.SaveAsync(alarms);
            if (reconciler is not null)
            {
                try
                {
                    await reconciler.ReconcileAllAsync(alarms, timeZone());
                }
                catch (Exception ex)
                {
                    throw new AlarmScheduleException(ex);
                }
            }
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
        first.OnceDate == second.OnceDate && first.SkipDate == second.SkipDate &&
        first.SoundUri == second.SoundUri && first.Volume == second.Volume &&
        first.SnoozeMinutes == second.SnoozeMinutes;
}
