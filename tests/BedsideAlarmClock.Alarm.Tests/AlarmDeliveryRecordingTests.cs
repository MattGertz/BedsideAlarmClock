using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmDeliveryRecordingTests : IDisposable
{
    private static readonly DateTimeOffset Trigger = DateTimeOffset.Parse("2025-01-02T07:30:00Z");
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"delivery-tests-{Guid.NewGuid():N}");

    public AlarmDeliveryRecordingTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private AlarmCoordinator CreateCoordinator() => new(new AlarmRepository(Path.Combine(directory, "alarms.json")));
    private AlarmRegistrationRepository CreateStore() => new(Path.Combine(directory, "registrations.json"));

    private async Task<(AlarmCoordinator coordinator, AlarmRegistrationRepository store, AlarmDefinition alarm,
        AlarmRegistration registration)> SetupAsync()
    {
        var coordinator = CreateCoordinator();
        var alarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = true,
            OnceDate = new DateOnly(2025, 1, 2)
        };
        await coordinator.AddAsync(alarm);
        var store = CreateStore();
        var entry = new AlarmRegistrationEntry
        {
            AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger
        };
        await store.SaveAsync(new[] { entry });
        return (coordinator, store, alarm, entry.Registration);
    }

    [Fact]
    public async Task ConcurrentDuplicateIsRecordedOnceAndSurvivesNewCoordinator()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        var observedAt = Trigger.AddSeconds(3);
        var results = await Task.WhenAll(
            coordinator.RecordRegularDeliveryAsync(store, registration, observedAt, TimeZoneInfo.Utc),
            coordinator.RecordRegularDeliveryAsync(store, registration, observedAt, TimeZoneInfo.Utc));
        Assert.Single(results, result => result == RegularDeliveryRecordResult.Recorded);
        Assert.Single(results, result => result == RegularDeliveryRecordResult.AlreadyRecorded);
        var recorded = Assert.Single(await CreateStore().LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, recorded.State);
        Assert.Equal(observedAt, recorded.ObservedAt);

        var restarted = CreateCoordinator();
        Assert.Equal(RegularDeliveryRecordResult.AlreadyRecorded,
            await restarted.RecordRegularDeliveryAsync(CreateStore(), registration, observedAt.AddMinutes(1), TimeZoneInfo.Utc));
        var exception = Assert.Throws<UnresolvedAlarmDeliveryException>(() =>
            AlarmRegistrationPlanner.PlanRegularRegistrations(
                new[] { alarm }, new[] { recorded }, observedAt, TimeZoneInfo.Utc));
        Assert.Equal(registration, exception.Registration);
    }

    [Fact]
    public async Task EarlyAndStaleCallbacksDoNotModifyJournal()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        var path = Path.Combine(directory, "registrations.json");
        var before = await File.ReadAllTextAsync(path);
        Assert.Equal(RegularDeliveryRecordResult.Early,
            await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger.AddTicks(-1), TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryRecordResult.Stale,
            await coordinator.RecordRegularDeliveryAsync(store,
                new AlarmRegistration(alarm.Id, Guid.NewGuid(), AlarmRegistrationKind.Regular), Trigger, TimeZoneInfo.Utc));
        Assert.Equal(before, await File.ReadAllTextAsync(path));

        var late = Trigger.AddHours(1);
        Assert.Equal(RegularDeliveryRecordResult.Recorded,
            await coordinator.RecordRegularDeliveryAsync(store, registration, late, TimeZoneInfo.Utc));
        Assert.Equal(late, Assert.Single(await store.LoadExistingAsync()).ObservedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveReportsErrorAndRetryRecognizesWhetherCommitHappened(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var failing = new FailingSaveStore(store, failAfterWrite);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.RecordRegularDeliveryAsync(failing, registration, Trigger, TimeZoneInfo.Utc));

        Assert.Equal(failAfterWrite ? RegularDeliveryRecordResult.AlreadyRecorded : RegularDeliveryRecordResult.Recorded,
            await coordinator.RecordRegularDeliveryAsync(failing, registration, Trigger, TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryRecordResult.AlreadyRecorded,
            await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ReservationIsDurableAndDoesNotProvePlaybackStarted()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(30));
        var reservations = await Task.WhenAll(
            coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false),
            coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        Assert.Single(reservations, result => result == PlaybackReservationResult.Reserved);
        Assert.Single(reservations, result => result == PlaybackReservationResult.AlreadyReserved);

        var entry = Assert.Single(await CreateStore().LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.PlaybackReserved, entry.State);
        Assert.Equal(Trigger, entry.ObservedAt);
        Assert.Equal(Trigger.AddSeconds(30), entry.PlaybackReservedAt);
        Assert.Equal(RegularDeliveryRecordResult.AlreadyRecorded,
            await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger.AddMinutes(1), TimeZoneInfo.Utc));
        Assert.Throws<UnresolvedAlarmDeliveryException>(() =>
            AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, new[] { entry }, clock.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Equal(MissedAlarmRecordResult.PlaybackUnresolved,
            await coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddMinutes(1), false, TimeZoneInfo.Utc));
        var recovery = await coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1),
            TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, new HashSet<AlarmRegistration>());
        Assert.Equal(new(registration, MissedRecoveryUnresolvedReason.PlaybackReserved), Assert.Single(recovery.Unresolved));
        Assert.Empty(recovery.Recorded);
        Assert.Equal(PlaybackReservationResult.AlreadyReserved,
            await CreateCoordinator().ReserveRegularPlaybackAsync(CreateStore(), registration,
                clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
    }

    [Fact]
    public async Task ReservationRejectsStaleUncertainLateAndUnavailableCases()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        var now = new FixedTimeProvider(Trigger);
        Assert.Equal(PlaybackReservationResult.NotObserved,
            await coordinator.ReserveRegularPlaybackAsync(store, registration, now, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        Assert.Equal(PlaybackReservationResult.ClockUncertain,
            await coordinator.ReserveRegularPlaybackAsync(store, registration, now, TimeZoneInfo.Utc, RecoveryClockStatus.Uncertain, false));
        Assert.Equal(PlaybackReservationResult.Missed,
            await coordinator.ReserveRegularPlaybackAsync(store, registration, now, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, true));
        Assert.Equal(PlaybackReservationResult.Missed,
            await coordinator.ReserveRegularPlaybackAsync(store, registration,
                new FixedTimeProvider(Trigger.AddSeconds(30).AddTicks(1)), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            coordinator.ReserveRegularPlaybackAsync(store, registration,
                new FixedTimeProvider(Trigger.AddTicks(-1)), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        var changed = new AlarmDefinition
        {
            Id = alarm.Id, Hour = 8, Minute = alarm.Minute, Enabled = true, OnceDate = alarm.OnceDate
        };
        await coordinator.ReplaceAsync(alarm, changed);
        Assert.Equal(PlaybackReservationResult.Stale,
            await coordinator.ReserveRegularPlaybackAsync(store, registration, now, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationWriteFailureIsReportedAndRetryDoesNotDuplicate(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var failing = new FailingSaveStore(store, failAfterWrite);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.ReserveRegularPlaybackAsync(failing, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        Assert.Equal(failAfterWrite ? PlaybackReservationResult.AlreadyReserved : PlaybackReservationResult.Reserved,
            await coordinator.ReserveRegularPlaybackAsync(failing, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        Assert.Equal(AlarmRegistrationState.PlaybackReserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ReservationAndMissedDecisionCannotBothWin()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var reserve = coordinator.ReserveRegularPlaybackAsync(store, registration,
            new FixedTimeProvider(Trigger.AddSeconds(1)), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        var missed = coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddMinutes(1), false, TimeZoneInfo.Utc);
        await Task.WhenAll(reserve, missed);
        var reservationResult = await reserve;
        var missedResult = await missed;
        if (reservationResult == PlaybackReservationResult.Reserved)
            Assert.Equal(MissedAlarmRecordResult.PlaybackUnresolved, missedResult);
        else
        {
            Assert.Equal(PlaybackReservationResult.Stale, reservationResult);
            Assert.Equal(MissedAlarmRecordResult.Recorded, missedResult);
        }
    }

    [Fact]
    public async Task ReservationChecksTimeAfterLoadingTheJournal()
    {
        var (coordinator, repository, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(repository, registration, Trigger, TimeZoneInfo.Utc);
        var store = new BlockingLoadStore(repository);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        var pending = coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        try
        {
            await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Now = Trigger.AddSeconds(30).AddTicks(1);
        }
        finally
        {
            store.Continue.TrySetResult();
        }
        Assert.Equal(PlaybackReservationResult.Missed, await pending);
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, Assert.Single(await repository.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ReservedPlaybackStartsOnceAndTheEvidenceSurvivesReconciliation()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(30));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);

        var starts = 0;
        var results = await Task.WhenAll(
            coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
                RecoveryClockStatus.Trusted, false, () => starts++),
            coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
                RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Single(results, result => result == PlaybackStartResult.Started);
        Assert.Single(results, result => result == PlaybackStartResult.AlreadyStarted);
        Assert.Equal(1, starts);
        Assert.False(Assert.Single(await coordinator.LoadAsync()).Enabled);

        var entry = Assert.Single(await CreateStore().LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.PlaybackStarted, entry.State);
        Assert.Equal(Trigger.AddSeconds(30), entry.PlaybackStartedAt);
        Assert.Equal(RegularDeliveryRecordResult.AlreadyRecorded,
            await coordinator.RecordRegularDeliveryAsync(store, registration, clock.GetUtcNow(), TimeZoneInfo.Utc));
        Assert.Equal(MissedAlarmRecordResult.PlaybackUnresolved,
            await coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddMinutes(1), false, TimeZoneInfo.Utc));
        var recovery = await coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1),
            TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, new HashSet<AlarmRegistration>());
        Assert.Equal(new(registration, MissedRecoveryUnresolvedReason.PlaybackStarted), Assert.Single(recovery.Unresolved));
        var plan = AlarmRegistrationPlanner.PlanRegularRegistrations(
            new[] { alarm }, new[] { entry }, clock.GetUtcNow(), TimeZoneInfo.Utc);
        var retained = Assert.Single(plan);
        Assert.Equal(entry.PlaybackReservedAt, retained.PlaybackReservedAt);
        Assert.Equal(entry.PlaybackStartedAt, retained.PlaybackStartedAt);
    }

    [Fact]
    public async Task ReservedPlaybackRechecksDefinitionClockAndDeadlineBeforeStarting()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        var starts = 0;
        Assert.Equal(PlaybackStartResult.ClockUncertain, await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Uncertain, false, () => starts++));
        Assert.Equal(PlaybackStartResult.Missed, await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, true, () => starts++));
        clock.Now = Trigger.AddSeconds(30).AddTicks(1);
        Assert.Equal(PlaybackStartResult.Missed, await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        clock.Now = Trigger;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        clock.Now = Trigger.AddSeconds(1);
        var changed = new AlarmDefinition
        {
            Id = alarm.Id, Hour = 8, Minute = alarm.Minute, Enabled = true, OnceDate = alarm.OnceDate
        };
        await coordinator.ReplaceAsync(alarm, changed);
        Assert.Equal(PlaybackStartResult.Stale, await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Equal(0, starts);
        Assert.Equal(AlarmRegistrationState.PlaybackReserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task PlaybackStartFailureLeavesReservationUnresolved()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false,
            () => throw new InvalidOperationException("Simulated player failure.")));
        Assert.Equal(AlarmRegistrationState.PlaybackReserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackJournalFailureIsReportedAfterThePlayerWasStarted(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        var failing = new FailingSaveStore(store, failAfterWrite);
        var starts = 0;
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartReservedRegularPlaybackAsync(
            failing, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Equal(1, starts);
        Assert.Equal(failAfterWrite ? AlarmRegistrationState.PlaybackStarted : AlarmRegistrationState.PlaybackReserved,
            Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task StartingAndEditingTheSameAlarmAreSerialized()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var starting = Task.Run(() => coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () =>
            {
                startEntered.Set();
                if (!allowStart.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to finish the test player start.");
            }));
        try
        {
            Assert.True(startEntered.Wait(TimeSpan.FromSeconds(5)));
            var changed = new AlarmDefinition
            {
                Id = alarm.Id, Hour = 8, Minute = alarm.Minute, Enabled = true, OnceDate = alarm.OnceDate
            };
            var edit = coordinator.ReplaceAsync(alarm, changed);
            await Assert.ThrowsAsync<TimeoutException>(() => edit.WaitAsync(TimeSpan.FromMilliseconds(200)));
            allowStart.Set();
            Assert.Equal(PlaybackStartResult.Started, await starting);
            await Assert.ThrowsAsync<AlarmConflictException>(() => edit);
        }
        finally
        {
            allowStart.Set();
            await starting;
        }
    }

    [Fact]
    public async Task StartedRepeatingAlarmCanPlanItsNextOccurrence()
    {
        var (coordinator, store, original, registration) = await SetupAsync();
        var repeating = new AlarmDefinition
        {
            Id = original.Id, Hour = 7, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Thursday
        };
        await coordinator.ReplaceAsync(original, repeating);
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        Assert.Equal(PlaybackStartResult.Started, await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { }));

        var plan = AlarmRegistrationPlanner.PlanRegularRegistrations(
            new[] { repeating }, await store.LoadExistingAsync(), clock.GetUtcNow(), TimeZoneInfo.Utc);
        Assert.Single(plan, entry => entry.State == AlarmRegistrationState.PlaybackStarted);
        Assert.Equal(Trigger.AddDays(7), Assert.Single(plan, entry => entry.State == AlarmRegistrationState.Desired).TriggerAt);
    }

    [Fact]
    public async Task CompletionIsIdempotentAndDisablesOnlyTheDeliveredOneTimeAlarm()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var results = await Task.WhenAll(
            coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc),
            coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc));
        Assert.Single(results, result => result == PlaybackCompletionResult.Completed);
        Assert.Single(results, result => result == PlaybackCompletionResult.AlreadyCompleted);
        Assert.False(Assert.Single(await coordinator.LoadAsync()).Enabled);
        var entry = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.PlaybackCompleted, entry.State);
        Assert.True(entry.CancellationPending);

        var scheduler = new TestScheduler();
        await coordinator.ReconcileRegularAsync(
            new AlarmRegistrationReconciler(store, scheduler, clock), TimeZoneInfo.Utc);
        entry = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.PlaybackCompleted, entry.State);
        Assert.False(entry.CancellationPending);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task CompletionDoesNotDisableAnAlarmEditedAfterPlaybackStarted()
    {
        var (coordinator, store, original, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var afterStart = Assert.Single(await coordinator.LoadAsync());
        var changed = new AlarmDefinition
        {
            Id = original.Id, Hour = 8, Minute = original.Minute, Enabled = true, OnceDate = original.OnceDate
        };
        await coordinator.ReplaceAsync(afterStart, changed);

        Assert.Equal(PlaybackCompletionResult.Completed,
            await coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc));
        var saved = Assert.Single(await coordinator.LoadAsync());
        Assert.True(saved.Enabled);
        Assert.Equal(8, saved.Hour);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionRetriesAfterUncertainJournalWrite(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var failing = new FailingSaveStore(store, failAfterWrite);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CompleteStartedRegularPlaybackAsync(failing, registration, TimeZoneInfo.Utc));
        Assert.Equal(failAfterWrite ? PlaybackCompletionResult.AlreadyCompleted : PlaybackCompletionResult.Completed,
            await coordinator.CompleteStartedRegularPlaybackAsync(failing, registration, TimeZoneInfo.Utc));
        Assert.False(Assert.Single(await coordinator.LoadAsync()).Enabled);
        Assert.Equal(AlarmRegistrationState.PlaybackCompleted, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task FailedCompletionCancellationRemainsRetryable()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        await coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc);
        var scheduler = new TestScheduler { FailCancel = true };
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc));
        Assert.True(Assert.Single(await store.LoadExistingAsync()).CancellationPending);
        scheduler.FailCancel = false;
        await coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        Assert.False(Assert.Single(await store.LoadExistingAsync()).CancellationPending);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BlockingLoadStore(IAlarmRegistrationStore inner) : IAlarmRegistrationStore
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => inner.LoadAsync();
        public async Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync()
        {
            LoadStarted.TrySetResult();
            await Continue.Task;
            return await inner.LoadExistingAsync();
        }
        public Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations) => inner.SaveAsync(registrations);
    }

    private sealed class FailingSaveStore(IAlarmRegistrationStore inner, bool failAfterWrite) : IAlarmRegistrationStore
    {
        private bool fail = true;
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => inner.LoadAsync();
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => inner.LoadExistingAsync();

        public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
        {
            if (fail)
            {
                fail = false;
                if (failAfterWrite)
                    await inner.SaveAsync(registrations);
                throw new IOException("Simulated journal failure.");
            }
            await inner.SaveAsync(registrations);
        }
    }

    private sealed class TestScheduler : IAlarmRegistrationScheduler
    {
        public Dictionary<AlarmRegistration, DateTimeOffset> Registered { get; } = new();
        public bool FailCancel { get; set; }
        public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt) =>
            Registered[registration] = triggerAt;
        public void Cancel(AlarmRegistration registration)
        {
            if (FailCancel)
                throw new InvalidOperationException("Simulated cancellation failure.");
            Registered.Remove(registration);
        }
    }
}
