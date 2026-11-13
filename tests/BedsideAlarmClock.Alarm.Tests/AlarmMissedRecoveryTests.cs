using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmMissedRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Trigger = DateTimeOffset.Parse("2025-01-06T07:30:00Z");
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"missed-tests-{Guid.NewGuid():N}");

    public AlarmMissedRecoveryTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private async Task<(AlarmCoordinator coordinator, AlarmRegistrationRepository store,
        AlarmDefinition alarm, AlarmRegistration registration)> SetupAsync()
    {
        var coordinator = new AlarmCoordinator(new AlarmRepository(Path.Combine(directory, "alarms.json")));
        var alarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        await coordinator.AddAsync(alarm);
        var store = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var entry = new AlarmRegistrationEntry
        {
            AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger
        };
        await store.SaveAsync(new[] { entry });
        return (coordinator, store, alarm, entry.Registration);
    }

    [Fact]
    public async Task MissedRecordSurvivesReconciliationAndNextOccurrenceIsScheduled()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        Assert.Equal(MissedAlarmRecordResult.NotMissed,
            await coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddSeconds(30), false, TimeZoneInfo.Utc));
        var decisionAt = Trigger.AddSeconds(30).AddTicks(1);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordMissedRegularAsync(store, registration, decisionAt, false, TimeZoneInfo.Utc));
        Assert.Equal(MissedAlarmRecordResult.AlreadyRecorded,
            await coordinator.RecordMissedRegularAsync(store, registration, decisionAt.AddMinutes(1), false, TimeZoneInfo.Utc));
        Assert.Equal(DeliveryRecordResult.Stale,
            await coordinator.RecordRegularDeliveryAsync(store, registration, decisionAt, TimeZoneInfo.Utc));
        var missed = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(MissedAlarmReason.DeadlineExceeded, missed.MissedReason);
        Assert.Equal(decisionAt, missed.MissedAt);
        Assert.True(missed.CancellationPending);

        var scheduler = new TestScheduler();
        scheduler.Registered.Add(registration, Trigger);
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider(decisionAt));
        await coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        var journal = await store.LoadExistingAsync();
        missed = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Missed);
        var next = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(alarm.Id, missed.AlarmId);
        Assert.Equal(decisionAt, missed.MissedAt);
        Assert.False(missed.CancellationPending);
        Assert.Equal(DateTimeOffset.Parse("2025-01-13T07:30:00Z"), next.TriggerAt);
        Assert.NotEqual(registration, next.Registration);
        Assert.Equal(next.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task InactiveObservedDeliveryIsReportedInterruptedWithoutRestartingIt()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        var now = Trigger.AddMinutes(1);
        var active = await coordinator.RecoverInactiveAsync(store, now, TimeZoneInfo.Utc, item => item == registration);
        Assert.Empty(active.Interrupted);
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, Assert.Single(await store.LoadExistingAsync()).State);

        var recovered = await coordinator.RecoverInactiveAsync(store, now, TimeZoneInfo.Utc, _ => false);
        Assert.Equal(registration, Assert.Single(recovered.Interrupted));
        var interrupted = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.Interrupted, interrupted.State);
        Assert.Equal(now, interrupted.InterruptedAt);
        Assert.Null(interrupted.PlaybackStartedAt);

        var scheduler = new TestScheduler();
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider(now));
        await coordinator.ReconcileAllAsync(reconciler, TimeZoneInfo.Utc);
        var journal = await store.LoadExistingAsync();
        Assert.False(Assert.Single(journal, item => item.State == AlarmRegistrationState.Interrupted).CancellationPending);
        Assert.Equal(Trigger.AddDays(7), Assert.Single(scheduler.Registered).Value);
    }

    [Fact]
    public async Task InactiveRecoveryKeepsGraceWindowAndMarksLateUnobservedAlarmMissed()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var within = await coordinator.RecoverInactiveAsync(store, Trigger.AddSeconds(30), TimeZoneInfo.Utc, _ => false);
        Assert.Equal(registration, Assert.Single(within.AwaitingDelivery));
        Assert.Equal(AlarmRegistrationState.Desired, Assert.Single(await store.LoadExistingAsync()).State);

        var late = await coordinator.RecoverInactiveAsync(store, Trigger.AddSeconds(30).AddTicks(1), TimeZoneInfo.Utc, _ => false);
        Assert.Equal(registration, Assert.Single(late.Missed));
        Assert.Equal(AlarmRegistrationState.Missed, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryDoesNotMarkAnActiveUnobservedDeliveryMissed(bool clockTrusted)
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var result = await coordinator.RecoverInactiveAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc,
            item => item == registration, clockTrusted ? RecoveryClockStatus.Trusted : RecoveryClockStatus.Uncertain);
        Assert.Empty(result.Missed);
        Assert.Empty(result.ClockUncertain);
        Assert.Equal(AlarmRegistrationState.Desired, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ClockChangeRecoveryLeavesAlreadyPlayingAlarmAlone()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var time = new FixedTimeProvider(Trigger.AddSeconds(1));
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        await coordinator.ReserveRegularPlaybackAsync(store, registration, time, TimeZoneInfo.Utc,
            RecoveryClockStatus.Trusted, false);
        Assert.Equal(PlaybackStartResult.Started,
            await coordinator.StartReservedRegularPlaybackAsync(store, registration, time, TimeZoneInfo.Utc,
                RecoveryClockStatus.Trusted, false, () => { }));

        var result = await coordinator.RecoverInactiveAsync(store, Trigger.AddMinutes(-1), TimeZoneInfo.Utc,
            item => item == registration, RecoveryClockStatus.Uncertain);
        Assert.Empty(result.ClockUncertain);
        Assert.Empty(result.Interrupted);
        Assert.Equal(AlarmRegistrationState.PlaybackStarted, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task UncertainClockDoesNotClaimThatAlarmWasMissed()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger.AddSeconds(5), TimeZoneInfo.Utc);
        var now = Trigger.AddSeconds(1);
        var result = await coordinator.RecoverInactiveAsync(store, now, TimeZoneInfo.Utc, _ => false,
            RecoveryClockStatus.Uncertain);
        Assert.Empty(result.Missed);
        Assert.Equal(registration, Assert.Single(result.ClockUncertain));
        var uncertain = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(AlarmRegistrationState.ClockUncertain, uncertain.State);
        Assert.Equal(Trigger.AddSeconds(5), uncertain.ObservedAt);
        Assert.Null(uncertain.MissedAt);
        Assert.Equal(now, uncertain.ClockUncertainAt);
    }

    [Fact]
    public async Task ClockChangeDuringReservedDeliveryCannotBeHiddenByRecovery()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var barrier = new AlarmRecoveryBarrier();
        var clockStatus = RecoveryClockStatus.Trusted;
        var time = new FixedTimeProvider(Trigger.AddSeconds(1));
        Assert.True(await barrier.BeginDeliveryAsync(() => true));
        Assert.Equal(DeliveryRecordResult.Recorded,
            await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc));
        Assert.Equal(PlaybackReservationResult.Reserved,
            await coordinator.ReserveRegularPlaybackAsync(store, registration, time, TimeZoneInfo.Utc,
                clockStatus, false));

        clockStatus = RecoveryClockStatus.Uncertain;
        async Task Recover()
        {
            await coordinator.RecoverInactiveAsync(store, Trigger.AddSeconds(1), TimeZoneInfo.Utc,
                _ => false, clockStatus);
            clockStatus = RecoveryClockStatus.Trusted;
        }
        Assert.False(await barrier.RecoverAsync(Recover));
        var started = false;
        Assert.Equal(PlaybackStartResult.ClockUncertain,
            await coordinator.StartReservedRegularPlaybackAsync(store, registration, time, TimeZoneInfo.Utc,
                clockStatus, false, () => started = true));
        Assert.False(started);
        await barrier.FinishDeliveryAsync(Recover);

        Assert.Equal(RecoveryClockStatus.Trusted, clockStatus);
        Assert.Equal(AlarmRegistrationState.ClockUncertain, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task FailedReservedDeliveryIsRetriedAfterGraceWithoutStartingAgain()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger, TimeZoneInfo.Utc);
        await coordinator.ReserveRegularPlaybackAsync(store, registration, new FixedTimeProvider(Trigger.AddSeconds(1)),
            TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, false);

        var first = await coordinator.RecoverInactiveAsync(store, Trigger.AddSeconds(2), TimeZoneInfo.Utc, _ => false);
        Assert.Equal(registration, Assert.Single(first.AwaitingDelivery));
        var retry = await coordinator.RecoverInactiveAsync(store, Trigger.AddSeconds(31), TimeZoneInfo.Utc, _ => false);
        Assert.Equal(registration, Assert.Single(retry.Interrupted));
        Assert.Equal(AlarmRegistrationState.Interrupted, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task KnownOutageOverridesGraceAndObservedEvidenceIsRetained()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var observed = Trigger.AddSeconds(1);
        await coordinator.RecordRegularDeliveryAsync(store, registration, observed, TimeZoneInfo.Utc);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordMissedRegularAsync(store, registration, observed.AddSeconds(1), true, TimeZoneInfo.Utc));
        var missed = Assert.Single(await store.LoadExistingAsync());
        Assert.Equal(MissedAlarmReason.KnownOutage, missed.MissedReason);
        Assert.Equal(observed, missed.ObservedAt);
        Assert.True(missed.CancellationPending);
    }

    [Fact]
    public async Task MissedOneTimeAlarmDoesNotAcquireAnotherOccurrence()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        var once = new AlarmDefinition
        {
            Id = alarm.Id, Hour = alarm.Hour, Minute = alarm.Minute, Enabled = true,
            OnceDate = new DateOnly(2025, 1, 6)
        };
        await coordinator.ReplaceAsync(alarm, once);
        var now = Trigger.AddMinutes(1);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordMissedRegularAsync(store, registration, now, false, TimeZoneInfo.Utc));

        var scheduler = new TestScheduler();
        scheduler.Registered.Add(registration, Trigger);
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider(now));
        await coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        Assert.Equal(AlarmRegistrationState.Missed, Assert.Single(await store.LoadExistingAsync()).State);
        Assert.Empty(scheduler.Registered);
    }

    [Fact]
    public async Task FailedMissedRecordWriteIsReportedAndCanBeRetried()
    {
        var (coordinator, repository, _, registration) = await SetupAsync();
        var store = new TestStore(repository) { FailSaveOnCall = 1 };
        var now = Trigger.AddMinutes(1);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.RecordMissedRegularAsync(store, registration, now, false, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.Desired, Assert.Single(await repository.LoadExistingAsync()).State);
        Assert.Equal(MissedAlarmRecordResult.Recorded,
            await coordinator.RecordMissedRegularAsync(store, registration, now, false, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.Missed, Assert.Single(await repository.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ChangedOrFutureAlarmIsNotReportedMissed()
    {
        var (coordinator, store, alarm, registration) = await SetupAsync();
        Assert.Equal(MissedAlarmRecordResult.NotMissed,
            await coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddTicks(-1), true, TimeZoneInfo.Utc));
        var changed = new AlarmDefinition
        {
            Id = alarm.Id, Hour = 8, Minute = alarm.Minute, Enabled = true,
            IsRepeating = true, Days = alarm.Days
        };
        await coordinator.ReplaceAsync(alarm, changed);
        Assert.Equal(MissedAlarmRecordResult.Stale,
            await coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddMinutes(1), false, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.Desired, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrCleanupFailureKeepsReportAndRetryIdentity(bool failCleanupSave)
    {
        var (coordinator, repository, _, registration) = await SetupAsync();
        var now = Trigger.AddMinutes(1);
        await coordinator.RecordMissedRegularAsync(repository, registration, now, false, TimeZoneInfo.Utc);
        var store = new TestStore(repository) { FailSaveOnCall = failCleanupSave ? 2 : 0 };
        var scheduler = new TestScheduler { FailCancelOnCall = failCleanupSave ? 0 : 1 };
        scheduler.Registered.Add(registration, Trigger);
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider(now));
        var error = await Record.ExceptionAsync(() => coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc));
        Assert.True(error is IOException or InvalidOperationException);

        var journal = await repository.LoadExistingAsync();
        var desired = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Desired);
        var missed = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Missed);
        Assert.True(missed.CancellationPending);
        Assert.Equal(registration, missed.Registration);

        await coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        journal = await repository.LoadExistingAsync();
        Assert.Equal(desired.Registration, Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Desired).Registration);
        missed = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Missed);
        Assert.False(missed.CancellationPending);
        Assert.Equal(registration, missed.Registration);
        Assert.Equal(desired.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task ClockMovingBackBeforeObservedCallbackIsReported()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, registration, Trigger.AddSeconds(5), TimeZoneInfo.Utc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RecordMissedRegularAsync(store, registration, Trigger.AddSeconds(2), false, TimeZoneInfo.Utc));
        Assert.Equal(AlarmRegistrationState.DeliveryObserved, Assert.Single(await store.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task RecoverySeparatesKnownOutageFromGracePeriodAndCanBeRetried()
    {
        var (coordinator, store, _, first) = await SetupAsync();
        var secondAlarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        await coordinator.AddAsync(secondAlarm);
        var entries = (await store.LoadExistingAsync()).ToList();
        var second = new AlarmRegistrationEntry
        {
            AlarmId = secondAlarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger
        };
        entries.Add(second);
        await store.SaveAsync(entries);
        var knownOutages = new HashSet<AlarmRegistration> { first };

        var firstPass = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddSeconds(30), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, knownOutages);
        Assert.Equal(first, Assert.Single(firstPass.Recorded));
        Assert.Equal(new(second.Registration, MissedRecoveryUnresolvedReason.WithinWindow), Assert.Single(firstPass.Unresolved));
        var journal = await store.LoadExistingAsync();
        Assert.Equal(MissedAlarmReason.KnownOutage, journal.Single(entry => entry.Registration == first).MissedReason);
        Assert.Equal(AlarmRegistrationState.Desired, journal.Single(entry => entry.Registration == second.Registration).State);

        var secondPass = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddSeconds(30).AddTicks(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, knownOutages);
        Assert.Equal(second.Registration, Assert.Single(secondPass.Recorded));
        Assert.Empty(secondPass.Unresolved);
        journal = await store.LoadExistingAsync();
        Assert.Equal(MissedAlarmReason.DeadlineExceeded, journal.Single(entry => entry.Registration == second.Registration).MissedReason);
        var retry = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddSeconds(32), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, knownOutages);
        Assert.Empty(retry.Recorded);
        Assert.Empty(retry.Unresolved);
    }

    [Fact]
    public async Task RecoveryPersistsMultipleMissesInOneBatch()
    {
        var (coordinator, repository, _, first) = await SetupAsync();
        var secondAlarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        await coordinator.AddAsync(secondAlarm);
        var second = new AlarmRegistrationEntry
        {
            AlarmId = secondAlarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger
        };
        await repository.SaveAsync((await repository.LoadExistingAsync()).Append(second).ToArray());
        var store = new TestStore(repository);
        var result = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddSeconds(31), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, new HashSet<AlarmRegistration>());
        Assert.Equal(new[] { first, second.Registration }, result.Recorded);
        Assert.Empty(result.Unresolved);
        Assert.Equal(1, store.SaveCalls);
        Assert.All(await repository.LoadExistingAsync(), entry =>
        {
            Assert.Equal(AlarmRegistrationState.Missed, entry.State);
            Assert.True(entry.CancellationPending);
        });
    }

    [Fact]
    public async Task RecoveryLeavesObservedStaleAndUncertainCasesUnresolved()
    {
        var (coordinator, store, alarm, observedRegistration) = await SetupAsync();
        await coordinator.RecordRegularDeliveryAsync(store, observedRegistration, Trigger, TimeZoneInfo.Utc);
        var staleAlarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 8, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        await coordinator.AddAsync(staleAlarm);
        var stale = new AlarmRegistrationEntry
        {
            AlarmId = staleAlarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger
        };
        var futureAlarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 8, Minute = 0, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        await coordinator.AddAsync(futureAlarm);
        var future = new AlarmRegistrationEntry
        {
            AlarmId = futureAlarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = Trigger.AddMinutes(30)
        };
        var entries = (await store.LoadExistingAsync()).Append(stale).Append(future).ToList();
        await store.SaveAsync(entries);
        var knownOutages = new HashSet<AlarmRegistration> { observedRegistration };
        var trusted = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, knownOutages);
        Assert.Empty(trusted.Recorded);
        Assert.Equal(new[]
        {
            new MissedRecoveryUnresolved(observedRegistration, MissedRecoveryUnresolvedReason.DeliveryObserved),
            new MissedRecoveryUnresolved(stale.Registration, MissedRecoveryUnresolvedReason.Stale)
        }, trusted.Unresolved);

        var uncertain = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddDays(-1), TimeZoneInfo.Utc, RecoveryClockStatus.Uncertain, knownOutages);
        Assert.Empty(uncertain.Recorded);
        Assert.Equal(new[]
        {
            new MissedRecoveryUnresolved(observedRegistration, MissedRecoveryUnresolvedReason.DeliveryObserved),
            new MissedRecoveryUnresolved(stale.Registration, MissedRecoveryUnresolvedReason.ClockUncertain),
            new MissedRecoveryUnresolved(future.Registration, MissedRecoveryUnresolvedReason.ClockUncertain)
        }, uncertain.Unresolved);
        Assert.Equal(AlarmRegistrationState.DeliveryObserved,
            (await store.LoadExistingAsync()).Single(entry => entry.AlarmId == alarm.Id).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryWriteFailurePropagatesAndRetryUsesPersistedState(bool failAfterWrite)
    {
        var (coordinator, repository, _, registration) = await SetupAsync();
        var store = new TestStore(repository) { FailSaveOnCall = 1, FailAfterSave = failAfterWrite };
        var outages = new HashSet<AlarmRegistration>();
        await Assert.ThrowsAsync<IOException>(() => coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages));
        var state = Assert.Single(await repository.LoadExistingAsync()).State;
        Assert.Equal(failAfterWrite ? AlarmRegistrationState.Missed : AlarmRegistrationState.Desired, state);

        var retry = await coordinator.RecoverUnobservedRegularAsync(
            store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages);
        if (failAfterWrite)
            Assert.Empty(retry.Recorded);
        else
            Assert.Equal(registration, Assert.Single(retry.Recorded));
        Assert.Equal(AlarmRegistrationState.Missed, Assert.Single(await repository.LoadExistingAsync()).State);
    }

    [Fact]
    public async Task ConcurrentRecoveryRecordsOnlyOnceAndMissingJournalIsAnError()
    {
        var (coordinator, store, _, registration) = await SetupAsync();
        var outages = new HashSet<AlarmRegistration>();
        var results = await Task.WhenAll(
            coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages),
            coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages));
        Assert.Equal(registration, Assert.Single(results.SelectMany(result => result.Recorded)));
        File.Delete(Path.Combine(directory, "registrations.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages));
    }

    [Fact]
    public async Task CorruptJournalAndMissingDefinitionsAreErrors()
    {
        var (coordinator, store, _, _) = await SetupAsync();
        var outages = new HashSet<AlarmRegistration>();
        await File.WriteAllTextAsync(Path.Combine(directory, "registrations.json"), "not json");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages));
        await store.SaveAsync(Array.Empty<AlarmRegistrationEntry>());
        File.Delete(Path.Combine(directory, "alarms.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            coordinator.RecoverUnobservedRegularAsync(store, Trigger.AddMinutes(1), TimeZoneInfo.Utc, RecoveryClockStatus.Trusted, outages));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestStore(AlarmRegistrationRepository repository) : IAlarmRegistrationStore
    {
        private int saveCalls;
        public int SaveCalls => saveCalls;
        public int FailSaveOnCall { get; set; }
        public bool FailAfterSave { get; set; }
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => repository.LoadAsync();
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => repository.LoadExistingAsync();

        public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
        {
            if (++saveCalls == FailSaveOnCall)
            {
                if (FailAfterSave)
                    await repository.SaveAsync(registrations);
                throw new IOException("Simulated journal failure.");
            }
            await repository.SaveAsync(registrations);
        }
    }

    private sealed class TestScheduler : IAlarmRegistrationScheduler
    {
        private int cancelCalls;
        public Dictionary<AlarmRegistration, DateTimeOffset> Registered { get; } = new();
        public int FailCancelOnCall { get; set; }
        public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt) =>
            Registered[registration] = triggerAt;
        public void Cancel(AlarmRegistration registration)
        {
            if (++cancelCalls == FailCancelOnCall)
                throw new InvalidOperationException("Simulated cancellation failure.");
            Registered.Remove(registration);
        }
    }
}
