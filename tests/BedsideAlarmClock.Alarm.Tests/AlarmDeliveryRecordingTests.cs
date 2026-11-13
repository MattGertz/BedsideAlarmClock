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
        Assert.Single(results, result => result == DeliveryRecordResult.Recorded);
        Assert.Single(results, result => result == DeliveryRecordResult.AlreadyRecorded);
        var recorded = Assert.Single(await CreateStore().LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, recorded.State);
        Assert.Equal(observedAt, recorded.ObservedAt);

        var restarted = CreateCoordinator();
        Assert.Equal(DeliveryRecordResult.AlreadyRecorded,
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
        Assert.Equal(DeliveryRecordResult.Early,
            await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger.AddTicks(-1), TimeZoneInfo.Utc));
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordRegularDeliveryAsync(store,
                new AlarmRegistration(alarm.Id, Guid.NewGuid(), AlarmRegistrationKind.Regular), Trigger, TimeZoneInfo.Utc));
        Assert.Equal(before, await File.ReadAllTextAsync(path));

        var late = Trigger.AddHours(1);
        Assert.Equal(DeliveryRecordResult.Recorded,
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

        Assert.Equal(failAfterWrite ? DeliveryRecordResult.AlreadyRecorded : DeliveryRecordResult.Recorded,
            await coordinator.RecordRegularDeliveryAsync(failing, registration, Trigger, TimeZoneInfo.Utc));
        Assert.Equal(DeliveryRecordResult.AlreadyRecorded,
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
        Assert.Equal(DeliveryRecordResult.AlreadyRecorded,
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
        Assert.Equal(DeliveryRecordResult.AlreadyRecorded,
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

    [Fact]
    public async Task QueuedPlayerRechecksDeadlineWhenItActuallyStarts()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var pending = coordinator.StartReservedPlaybackOnPlayerAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, async check =>
            {
                queued.SetResult();
                await proceed.Task;
                check();
                starts++;
            });
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Now = Trigger.AddSeconds(30).AddTicks(1);
        proceed.SetResult();
        Assert.Equal(PlaybackStartResult.Missed, await pending);
        Assert.Equal(0, starts);
        Assert.Equal(AlarmRegistrationState.PlaybackReserved, Assert.Single(await store.LoadExistingAsync()).State);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordUnstartedReservationMissedAsync(store, registration, clock.Now));
        var missed = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.Missed, missed.State);
        Assert.Null(missed.PlaybackReservedAt);
    }

    [Fact]
    public async Task QueuedPlayerKeepsEditsSerializedThroughActualStart()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = coordinator.StartReservedPlaybackOnPlayerAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, async check =>
            {
                queued.SetResult();
                await proceed.Task;
                check();
            });
        try
        {
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var changed = new AlarmDefinition
            {
                Id = alarm.Id, Hour = 8, Minute = alarm.Minute, Enabled = true, OnceDate = alarm.OnceDate
            };
            var edit = coordinator.ReplaceAsync(alarm, changed);
            await Assert.ThrowsAsync<TimeoutException>(() => edit.WaitAsync(TimeSpan.FromMilliseconds(200)));
            clock.Now = Trigger.AddSeconds(30);
            proceed.SetResult();
            Assert.Equal(PlaybackStartResult.Started, await pending);
            await Assert.ThrowsAsync<AlarmConflictException>(() => edit);
            Assert.Equal(clock.Now, Assert.Single(await store.LoadExistingAsync()).PlaybackStartedAt);
        }
        finally
        {
            proceed.TrySetResult();
            await pending;
        }
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

    [Fact]
    public async Task SnoozePersistsOnceAndRetriesUsingTheSameIdentity()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler();
        var results = await Task.WhenAll(
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc),
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Single(results, result => result == SnoozeResult.Scheduled);
        Assert.Single(results, result => result == SnoozeResult.Rescheduled);
        var journal = await store.LoadExistingAsync();
        var source = Assert.Single(journal, entry => entry.Kind == AlarmRegistrationKind.Regular);
        var snooze = Assert.Single(journal, entry => entry.Kind == AlarmRegistrationKind.Snooze);
        Assert.Equal(AlarmRegistrationState.PlaybackCompleted, source.State);
        Assert.True(source.CancellationPending);
        Assert.Equal(registration.RegistrationId, snooze.SnoozedFromRegistrationId);
        Assert.Equal(Trigger.AddSeconds(1).AddMinutes(10), snooze.TriggerAt);
        Assert.Equal(snooze.TriggerAt, Assert.Single(scheduler.Registered).Value);
        var plan = AlarmRegistrationPlanner.PlanRegularRegistrations(
            await coordinator.LoadAsync(), journal, clock.GetUtcNow(), TimeZoneInfo.Utc);
        Assert.Equal(registration.RegistrationId,
            Assert.Single(plan, entry => entry.Kind == AlarmRegistrationKind.Snooze).SnoozedFromRegistrationId);
        Assert.Equal(SnoozeResult.Rescheduled, await CreateCoordinator().SnoozeStartedRegularPlaybackAsync(
            CreateStore(), scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Equal(snooze.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task SnoozeUsesConfiguredIntervalAndRetryKeepsOriginalTime()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler();
        Assert.Equal(SnoozeResult.Scheduled,
            await coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc, 7));
        Assert.Equal(SnoozeResult.Rescheduled,
            await coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc, 20));
        Assert.Equal(Trigger.AddSeconds(1).AddMinutes(7), Assert.Single(scheduler.Registered).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnoozeRetriesAfterUncertainJournalWrite(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var failing = new FailingSaveStore(store, failAfterWrite);
        var scheduler = new TestScheduler();
        await Assert.ThrowsAsync<IOException>(() => coordinator.SnoozeStartedRegularPlaybackAsync(
            failing, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        Assert.Equal(failAfterWrite ? SnoozeResult.Rescheduled : SnoozeResult.Scheduled,
            await coordinator.SnoozeStartedRegularPlaybackAsync(
                failing, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Single(scheduler.Registered);
        Assert.Single(await store.LoadExistingAsync(), entry => entry.Kind == AlarmRegistrationKind.Snooze);
    }

    [Fact]
    public async Task SnoozeSchedulingFailureCanBeRetriedButOverdueSnoozeIsNotScheduled()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailSchedule = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        var snooze = Assert.Single(await store.LoadExistingAsync(), entry => entry.Kind == AlarmRegistrationKind.Snooze);
        scheduler.FailSchedule = false;
        Assert.Equal(SnoozeResult.Rescheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Equal(snooze.Registration, Assert.Single(scheduler.Registered).Key);
        clock.Now = snooze.TriggerAt;
        scheduler.Registered.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task DismissedOrUnstartedAlarmCannotCreateSnooze()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        var scheduler = new TestScheduler();
        Assert.Equal(SnoozeResult.NotStarted, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        await coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc);
        Assert.Equal(SnoozeResult.AlreadyCompleted, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        Assert.Single(await store.LoadExistingAsync());
    }

    [Fact]
    public async Task OverlappingOccurrencesOfTheSameAlarmKeepSeparateSnoozes()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var journal = (await store.LoadExistingAsync()).ToList();
        var first = Assert.Single(journal);
        var second = new AlarmRegistrationEntry
        {
            AlarmId = first.AlarmId, RegistrationId = Guid.NewGuid(), Kind = first.Kind,
            TriggerAt = first.TriggerAt, State = AlarmRegistrationState.PlaybackStarted,
            ObservedAt = first.ObservedAt, PlaybackReservedAt = first.PlaybackReservedAt,
            PlaybackStartedAt = first.PlaybackStartedAt
        };
        journal.Add(second);
        await store.SaveAsync(journal);
        var scheduler = new TestScheduler();
        Assert.Equal(SnoozeResult.Scheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Equal(SnoozeResult.Scheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, second.Registration, clock, TimeZoneInfo.Utc));
        var snoozes = (await store.LoadExistingAsync()).Where(entry => entry.Kind == AlarmRegistrationKind.Snooze).ToList();
        Assert.Equal(2, snoozes.Count);
        Assert.Contains(snoozes, entry => entry.SnoozedFromRegistrationId == registration.RegistrationId);
        Assert.Contains(snoozes, entry => entry.SnoozedFromRegistrationId == second.RegistrationId);
        Assert.Equal(2, scheduler.Registered.Count);
    }

    [Fact]
    public async Task FailedSnoozeIsCanceledAndRetryKeepsItsIdentity()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailAfterSchedule = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        Assert.Equal(AlarmRegistrationState.SnoozeUnconfirmed, snooze.State);
        scheduler.FailAfterSchedule = false;
        Assert.Equal(SnoozeResult.Rescheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Equal(snooze.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task UnconfirmedSnoozeIsNeverDeliveredOrRescheduledByReconciliation()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger.AddSeconds(1) };
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailAfterSchedule = true, FailCancel = true };
        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze.Registration, snooze.TriggerAt, TimeZoneInfo.Utc));

        scheduler.FailCancel = false;
        scheduler.FailAfterSchedule = false;
        await coordinator.ReconcileAllAsync(new AlarmRegistrationReconciler(store, scheduler, clock), TimeZoneInfo.Utc);
        Assert.DoesNotContain(snooze.Registration, scheduler.Registered.Keys);
        Assert.Equal(AlarmRegistrationState.SnoozeUnconfirmed,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration).State);

        clock.Now = snooze.TriggerAt.AddMinutes(1);
        Assert.Equal(SnoozeResult.Rescheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
            store, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Equal(clock.Now.AddMinutes(10), scheduler.Registered[snooze.Registration]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnoozeConfirmationWriteFailureUsesPersistedState(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var failing = new FailingSaveStore(store, failAfterWrite, failOnCall: 2);
        var scheduler = new TestScheduler();

        if (failAfterWrite)
        {
            Assert.Equal(SnoozeResult.Scheduled, await coordinator.SnoozeStartedRegularPlaybackAsync(
                failing, scheduler, registration, clock, TimeZoneInfo.Utc));
            Assert.Single(scheduler.Registered);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => coordinator.SnoozeStartedRegularPlaybackAsync(
                failing, scheduler, registration, clock, TimeZoneInfo.Utc));
            Assert.Empty(scheduler.Registered);
            var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
            Assert.Equal(AlarmRegistrationState.SnoozeUnconfirmed, snooze.State);
            Assert.Equal(DeliveryRecordResult.Stale,
                await coordinator.RecordSnoozeDeliveryAsync(store, snooze.Registration, snooze.TriggerAt, TimeZoneInfo.Utc));
        }
    }

    [Fact]
    public async Task UnreadableSnoozeConfirmationIsRevertedAndCanceled()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var failing = new UnreadableConfirmationStore(store);
        var scheduler = new TestScheduler();

        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(failing, scheduler, registration, clock, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        Assert.Equal(AlarmRegistrationState.SnoozeUnconfirmed, snooze.State);
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze.Registration, snooze.TriggerAt, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task DismissAfterUncertainSnoozeCancelsItsIdentityAndRejectsLateDelivery()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailAfterSchedule = true, FailCancel = true };
        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        Assert.Single(scheduler.Registered);
        scheduler.FailCancel = false;

        Assert.Equal(PlaybackCompletionResult.AlreadyCompleted,
            await coordinator.DismissStartedPlaybackAsync(store, scheduler, registration, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        Assert.Equal(AlarmRegistrationState.PendingCancellation,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration).State);
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze.Registration, snooze.TriggerAt, TimeZoneInfo.Utc));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));

        await coordinator.ReconcileAllAsync(new AlarmRegistrationReconciler(store, scheduler, clock), TimeZoneInfo.Utc);
        Assert.DoesNotContain(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration);
    }

    [Fact]
    public async Task FailedSnoozeCancellationRemainsStaleAndCanBeRetried()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailAfterSchedule = true, FailCancel = true };
        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        scheduler.FailCancel = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.DismissStartedPlaybackAsync(store, scheduler, registration, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.PendingCancellation,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration).State);
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze.Registration, snooze.TriggerAt, TimeZoneInfo.Utc));

        scheduler.FailCancel = false;
        Assert.Equal(PlaybackCompletionResult.AlreadyCompleted,
            await coordinator.DismissStartedPlaybackAsync(store, scheduler, registration, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DismissalRetriesAfterUncertainSnoozeCancellationWrite(bool failAfterWrite)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(store, registration, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler { FailAfterSchedule = true, FailCancel = true };
        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc));
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        scheduler.FailCancel = false;
        var failing = new FailingSaveStore(store, failAfterWrite);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.DismissStartedPlaybackAsync(failing, scheduler, registration, TimeZoneInfo.Utc));
        Assert.Equal(failAfterWrite ? AlarmRegistrationState.PendingCancellation : AlarmRegistrationState.SnoozeUnconfirmed,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration).State);

        Assert.Equal(PlaybackCompletionResult.AlreadyCompleted,
            await coordinator.DismissStartedPlaybackAsync(failing, scheduler, registration, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        Assert.Equal(AlarmRegistrationState.PendingCancellation,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze.Registration).State);
    }

    [Fact]
    public async Task SnoozeAndPlaybackCompletionAreSerialized()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var clock = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.ReserveRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, registration, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler();
        var snooze = coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, registration, clock, TimeZoneInfo.Utc);
        var dismiss = coordinator.CompleteStartedRegularPlaybackAsync(store, registration, TimeZoneInfo.Utc);
        await Task.WhenAll(snooze, dismiss);
        if (await snooze == SnoozeResult.Scheduled)
        {
            Assert.Equal(PlaybackCompletionResult.AlreadyCompleted, await dismiss);
            Assert.Single(scheduler.Registered);
        }
        else
        {
            Assert.Equal(SnoozeResult.AlreadyCompleted, await snooze);
            Assert.Equal(PlaybackCompletionResult.Completed, await dismiss);
            Assert.Empty(scheduler.Registered);
        }
    }

    private async Task<(AlarmCoordinator coordinator, AlarmRegistrationRepository store,
        AlarmRegistration snooze, MutableTimeProvider clock, TestScheduler scheduler)> SetupSnoozeAsync()
    {
        var (coordinator, store, _, regular) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, regular, Trigger, TimeZoneInfo.Utc);
        var clock = new MutableTimeProvider { Now = Trigger };
        await coordinator.ReserveRegularPlaybackAsync(
            store, regular, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedRegularPlaybackAsync(
            store, regular, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { });
        var scheduler = new TestScheduler();
        await coordinator.SnoozeStartedRegularPlaybackAsync(store, scheduler, regular, clock, TimeZoneInfo.Utc);
        var snooze = Assert.Single(await store.LoadExistingAsync(), item => item.Kind == AlarmRegistrationKind.Snooze);
        clock.Now = snooze.TriggerAt;
        return (coordinator, store, snooze.Registration, clock, scheduler);
    }

    [Fact]
    public async Task DismissingASnoozeCancelsOnlyItsUncertainFollowUp()
    {
        var (coordinator, store, snooze, clock, scheduler) = await SetupSnoozeAsync();
        await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc);
        await coordinator.ReserveSnoozePlaybackAsync(store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        await coordinator.StartReservedSnoozePlaybackAsync(store, snooze, clock, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false, () => { });
        scheduler.FailAfterSchedule = true;
        scheduler.FailCancel = true;
        await Assert.ThrowsAsync<AggregateException>(() =>
            coordinator.SnoozeStartedSnoozePlaybackAsync(store, scheduler, snooze, clock, TimeZoneInfo.Utc));
        scheduler.FailCancel = false;
        var next = Assert.Single(await store.LoadExistingAsync(),
            item => item.SnoozedFromRegistrationId == snooze.RegistrationId);

        Assert.Equal(PlaybackCompletionResult.AlreadyCompleted,
            await coordinator.DismissStartedPlaybackAsync(store, scheduler, snooze, TimeZoneInfo.Utc));
        Assert.False(scheduler.Registered.ContainsKey(next.Registration));
        Assert.True(scheduler.Registered.ContainsKey(snooze));
        Assert.Equal(AlarmRegistrationState.PendingCancellation,
            Assert.Single(await store.LoadExistingAsync(), item => item.Registration == next.Registration).State);
    }

    [Fact]
    public async Task SnoozeDeliveryCanStartAndBeSnoozedAgainWithoutChangingTheRegularSchedule()
    {
        var (coordinator, store, snooze, clock, scheduler) = await SetupSnoozeAsync();
        var snoozeAt = clock.Now;
        Assert.False(Assert.Single(await coordinator.LoadAsync()).Enabled);
        Assert.Equal(DeliveryRecordResult.Recorded,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, snoozeAt, TimeZoneInfo.Utc));
        Assert.Equal(PlaybackReservationResult.Reserved, await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        var starts = 0;
        Assert.Equal(PlaybackStartResult.Started, await coordinator.StartReservedSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Equal(PlaybackStartResult.AlreadyStarted, await coordinator.StartReservedSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Equal(1, starts);
        Assert.Equal(SnoozeResult.Scheduled, await coordinator.SnoozeStartedSnoozePlaybackAsync(
            store, scheduler, snooze, clock, TimeZoneInfo.Utc));
        var journal = await store.LoadExistingAsync();
        var next = Assert.Single(journal, entry =>
            entry.Kind == AlarmRegistrationKind.Snooze && entry.Registration != snooze);
        Assert.Equal(snooze.RegistrationId, next.SnoozedFromRegistrationId);
        Assert.Equal(AlarmRegistrationKind.Snooze, next.SnoozedFromKind);
        Assert.Equal(snoozeAt.AddMinutes(10), next.TriggerAt);
        Assert.Equal(AlarmRegistrationState.PlaybackCompleted,
            Assert.Single(journal, entry => entry.Registration == snooze).State);
        Assert.False(Assert.Single(await coordinator.LoadAsync()).Enabled);
        var plan = AlarmRegistrationPlanner.PlanRegularRegistrations(
            await coordinator.LoadAsync(), journal, clock.Now, TimeZoneInfo.Utc);
        Assert.Equal(AlarmRegistrationKind.Snooze,
            Assert.Single(plan, entry => entry.Registration == next.Registration).SnoozedFromKind);
        await store.SaveAsync(plan);
    }

    [Fact]
    public async Task SnoozeDeliveryRejectsEarlyStaleAndLatePlayback()
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        Assert.Equal(DeliveryRecordResult.Early,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now.AddTicks(-1), TimeZoneInfo.Utc));
        var stale = snooze with { RegistrationId = Guid.NewGuid() };
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, stale, clock.Now, TimeZoneInfo.Utc));
        Assert.Equal(DeliveryRecordResult.Recorded,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc));
        Assert.Equal(DeliveryRecordResult.AlreadyRecorded,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc));
        clock.Now = clock.Now.AddSeconds(30).AddTicks(1);
        Assert.Equal(PlaybackReservationResult.Missed, await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        Assert.Equal(AlarmRegistrationState.DeliveryObserved,
            Assert.Single(await store.LoadExistingAsync(), entry => entry.Registration == snooze).State);
    }

    [Fact]
    public async Task SnoozeStartRechecksDeadlineAndCurrentDefinition()
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc);
        Assert.Equal(PlaybackReservationResult.ClockUncertain, await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Uncertain, false));
        Assert.Equal(PlaybackReservationResult.Reserved, await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false));
        clock.Now = clock.Now.AddSeconds(30).AddTicks(1);
        var starts = 0;
        Assert.Equal(PlaybackStartResult.Missed, await coordinator.StartReservedSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        await coordinator.DeleteAsync(Assert.Single(await coordinator.LoadAsync()));
        Assert.Equal(PlaybackStartResult.Stale, await coordinator.StartReservedSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => starts++));
        Assert.Equal(0, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnoozeCallbackWriteFailureIsRetryable(bool failAfterWrite)
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        var failing = new FailingSaveStore(store, failAfterWrite);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.RecordSnoozeDeliveryAsync(failing, snooze, clock.Now, TimeZoneInfo.Utc));
        Assert.Equal(failAfterWrite ? DeliveryRecordResult.AlreadyRecorded : DeliveryRecordResult.Recorded,
            await coordinator.RecordSnoozeDeliveryAsync(failing, snooze, clock.Now, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.DeliveryObserved,
            Assert.Single(await store.LoadExistingAsync(), entry => entry.Registration == snooze).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissedSnoozeRetainsReasonAndCallbackEvidence(bool knownOutage)
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc);
        Assert.Equal(MissedAlarmRecordResult.NotMissed,
            await coordinator.RecordMissedSnoozeAsync(store, snooze, clock.Now.AddSeconds(30), false, TimeZoneInfo.Utc));
        var decision = knownOutage ? clock.Now : clock.Now.AddSeconds(30).AddTicks(1);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordMissedSnoozeAsync(store, snooze, decision, knownOutage, TimeZoneInfo.Utc));
        Assert.Equal(MissedAlarmRecordResult.AlreadyRecorded,
            await coordinator.RecordMissedSnoozeAsync(store, snooze, decision, knownOutage, TimeZoneInfo.Utc));
        var entry = Assert.Single(await store.LoadExistingAsync(), item => item.Registration == snooze);
        Assert.Equal(AlarmRegistrationState.Missed, entry.State);
        Assert.Equal(clock.Now, entry.ObservedAt);
        Assert.Equal(knownOutage ? MissedAlarmReason.KnownOutage : MissedAlarmReason.DeadlineExceeded, entry.MissedReason);
        Assert.True(entry.CancellationPending);
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, decision, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task ReservedSnoozeCannotBeMarkedMissedWithoutResolvingPlayback()
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc);
        await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        Assert.Equal(MissedAlarmRecordResult.PlaybackUnresolved,
            await coordinator.RecordMissedSnoozeAsync(store, snooze, clock.Now.AddMinutes(1), false, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task DeletingDefinitionMakesPendingSnoozeStale()
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        await coordinator.DeleteAsync(Assert.Single(await coordinator.LoadAsync()));
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.Desired,
            Assert.Single(await store.LoadExistingAsync(), entry => entry.Registration == snooze).State);
    }

    [Fact]
    public async Task SnoozePlaybackDoesNotDisableANewOneTimeOccurrence()
    {
        var (coordinator, store, snooze, clock, _) = await SetupSnoozeAsync();
        var old = Assert.Single(await coordinator.LoadAsync());
        var newAlarm = new AlarmDefinition
        {
            Id = old.Id, Hour = clock.Now.Hour, Minute = clock.Now.Minute, Enabled = true,
            OnceDate = DateOnly.FromDateTime(clock.Now.UtcDateTime)
        };
        await coordinator.ReplaceAsync(old, newAlarm);
        await coordinator.RecordSnoozeDeliveryAsync(store, snooze, clock.Now, TimeZoneInfo.Utc);
        await coordinator.ReserveSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);
        Assert.Equal(PlaybackStartResult.Started, await coordinator.StartReservedSnoozePlaybackAsync(
            store, snooze, clock, TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false, () => { }));
        Assert.Equal(PlaybackCompletionResult.Completed,
            await coordinator.CompleteStartedSnoozePlaybackAsync(store, snooze, TimeZoneInfo.Utc));
        Assert.True(Assert.Single(await coordinator.LoadAsync()).Enabled);
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

    private sealed class FailingSaveStore(IAlarmRegistrationStore inner, bool failAfterWrite, int failOnCall = 1) : IAlarmRegistrationStore
    {
        private int saveCalls;
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => inner.LoadAsync();
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => inner.LoadExistingAsync();

        public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
        {
            if (++saveCalls == failOnCall)
            {
                if (failAfterWrite)
                    await inner.SaveAsync(registrations);
                throw new IOException("Simulated journal failure.");
            }
            await inner.SaveAsync(registrations);
        }
    }

    private sealed class UnreadableConfirmationStore(IAlarmRegistrationStore inner) : IAlarmRegistrationStore
    {
        private int loads;
        private int saves;
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => inner.LoadAsync();
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync()
        {
            if (++loads == 2) throw new IOException("Simulated read failure.");
            return inner.LoadExistingAsync();
        }

        public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
        {
            await inner.SaveAsync(registrations);
            if (++saves == 2) throw new IOException("Simulated confirmation write failure.");
        }
    }

    private sealed class TestScheduler : IAlarmRegistrationScheduler
    {
        public Dictionary<AlarmRegistration, DateTimeOffset> Registered { get; } = new();
        public bool FailCancel { get; set; }
        public bool FailSchedule { get; set; }
        public bool FailAfterSchedule { get; set; }
        public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt)
        {
            if (FailSchedule)
                throw new InvalidOperationException("Simulated scheduling failure.");
            Registered[registration] = triggerAt;
            if (FailAfterSchedule)
                throw new InvalidOperationException("Simulated failure after scheduling.");
        }
        public void Cancel(AlarmRegistration registration)
        {
            if (FailCancel)
                throw new InvalidOperationException("Simulated cancellation failure.");
            Registered.Remove(registration);
        }
    }
}
