using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmRegistrationReconcilerTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-05T00:00:00Z");
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"reconciler-tests-{Guid.NewGuid():N}");

    public AlarmRegistrationReconcilerTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static AlarmDefinition Alarm() => new()
    {
        Id = Guid.NewGuid(), Hour = 8, Enabled = true,
        IsRepeating = true, Days = AlarmDays.Monday
    };

    private static AlarmRegistrationEntry Old(AlarmDefinition alarm) => new()
    {
        AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(),
        Kind = AlarmRegistrationKind.Regular, State = AlarmRegistrationState.Desired,
        TriggerAt = DateTimeOffset.Parse("2025-01-06T07:00:00Z")
    };

    private (AlarmRegistrationRepository repository, TestStore store, TestScheduler scheduler,
        AlarmRegistrationReconciler reconciler, List<string> operations) Create()
    {
        var operations = new List<string>();
        var repository = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var store = new TestStore(repository, operations);
        var scheduler = new TestScheduler(operations);
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider());
        return (repository, store, scheduler, reconciler, operations);
    }

    [Fact]
    public async Task PersistsBeforeSchedulingAndRetiresOnlyAfterCancellation()
    {
        var (repository, _, scheduler, reconciler, operations) = Create();
        var alarm = Alarm();
        var old = Old(alarm);
        await repository.SaveAsync(new[] { old });
        scheduler.Registered.Add(old.Registration, old.TriggerAt);

        await reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc);

        Assert.Equal(new[] { "save", "schedule", "cancel", "save" }, operations);
        var current = Assert.Single(await repository.LoadAsync());
        Assert.Equal(AlarmRegistrationState.Desired, current.State);
        Assert.NotEqual(old.Registration, current.Registration);
        Assert.Equal(DateTimeOffset.Parse("2025-01-06T08:00:00Z"), current.TriggerAt);
        Assert.Equal(current.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task FailedInitialSaveDoesNotCallScheduler()
    {
        var (repository, store, scheduler, reconciler, _) = Create();
        var alarm = Alarm();
        var old = Old(alarm);
        await repository.SaveAsync(new[] { old });
        store.FailSaveOnCall = 1;
        await Assert.ThrowsAsync<IOException>(() => reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc));
        Assert.Empty(scheduler.Registered);
        Assert.Equal(old.Registration, Assert.Single(await repository.LoadAsync()).Registration);
        Assert.Equal(0, scheduler.ScheduleCalls + scheduler.CancelCalls);
    }

    [Fact]
    public async Task FailedScheduleLeavesNewAndOldIdentitiesForRetry()
    {
        var (repository, _, scheduler, reconciler, _) = Create();
        var alarm = Alarm();
        var old = Old(alarm);
        await repository.SaveAsync(new[] { old });
        scheduler.Registered.Add(old.Registration, old.TriggerAt);
        scheduler.FailScheduleOnCall = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc));
        var journal = await repository.LoadAsync();
        var desired = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(old.Registration, Assert.Single(journal, entry => entry.State == AlarmRegistrationState.PendingCancellation).Registration);
        Assert.Equal(old.Registration, Assert.Single(scheduler.Registered).Key);
        Assert.Equal(0, scheduler.CancelCalls);

        await reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc);
        Assert.Equal(desired.Registration, Assert.Single(await repository.LoadAsync()).Registration);
        Assert.Equal(desired.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task PartialSchedulingRetryReusesAllPersistedIdentities()
    {
        var (repository, _, scheduler, reconciler, _) = Create();
        var alarms = new[] { Alarm(), Alarm() };
        scheduler.FailScheduleOnCall = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileRegularAsync(alarms, TimeZoneInfo.Utc));
        var identities = (await repository.LoadAsync()).Select(entry => entry.Registration).ToHashSet();
        Assert.Equal(2, identities.Count);
        Assert.Single(scheduler.Registered);

        await reconciler.ReconcileRegularAsync(alarms, TimeZoneInfo.Utc);
        var afterRetry = (await repository.LoadAsync()).Select(entry => entry.Registration).ToHashSet();
        Assert.True(identities.SetEquals(afterRetry));
        Assert.True(identities.SetEquals(scheduler.Registered.Keys));
    }

    [Fact]
    public async Task FailedCancellationKeepsAllTombstonesUntilRetry()
    {
        var (repository, _, scheduler, reconciler, _) = Create();
        var first = Alarm();
        var second = Alarm();
        var oldFirst = Old(first);
        var oldSecond = Old(second);
        await repository.SaveAsync(new[] { oldFirst, oldSecond });
        scheduler.Registered.Add(oldFirst.Registration, oldFirst.TriggerAt);
        scheduler.Registered.Add(oldSecond.Registration, oldSecond.TriggerAt);
        scheduler.FailCancelOnCall = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciler.ReconcileRegularAsync(new[] { first, second }, TimeZoneInfo.Utc));
        var journal = await repository.LoadAsync();
        Assert.Equal(2, journal.Count(entry => entry.State == AlarmRegistrationState.PendingCancellation));
        Assert.DoesNotContain(oldFirst.Registration, scheduler.Registered.Keys);
        Assert.Contains(oldSecond.Registration, scheduler.Registered.Keys);

        await reconciler.ReconcileRegularAsync(new[] { first, second }, TimeZoneInfo.Utc);
        journal = await repository.LoadAsync();
        Assert.Equal(2, journal.Count);
        Assert.All(journal, entry => Assert.Equal(AlarmRegistrationState.Desired, entry.State));
        Assert.Equal(2, scheduler.Registered.Count);
        Assert.All(journal, entry => Assert.Contains(entry.Registration, scheduler.Registered.Keys));
    }

    [Fact]
    public async Task FailedCleanupSaveKeepsCancellationIdentityForRetry()
    {
        var (repository, store, scheduler, reconciler, _) = Create();
        var alarm = Alarm();
        var old = Old(alarm);
        await repository.SaveAsync(new[] { old });
        scheduler.Registered.Add(old.Registration, old.TriggerAt);
        store.FailSaveOnCall = 2;

        await Assert.ThrowsAsync<IOException>(() => reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc));
        var journal = await repository.LoadAsync();
        var desired = Assert.Single(journal, entry => entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(old.Registration, Assert.Single(journal, entry => entry.State == AlarmRegistrationState.PendingCancellation).Registration);
        Assert.DoesNotContain(old.Registration, scheduler.Registered.Keys);

        await reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc);
        Assert.Equal(desired.Registration, Assert.Single(await repository.LoadAsync()).Registration);
        Assert.Equal(desired.Registration, Assert.Single(scheduler.Registered).Key);
    }

    [Fact]
    public async Task OverdueRegularStopsBeforeWritingOrSchedulingAndSnoozesAreUntouched()
    {
        var (repository, _, scheduler, reconciler, operations) = Create();
        var alarm = Alarm();
        var due = Old(alarm);
        due.TriggerAt = Now;
        var snooze = Old(alarm);
        snooze.Kind = AlarmRegistrationKind.Snooze;
        snooze.TriggerAt = Now.AddMinutes(10);
        await repository.SaveAsync(new[] { due, snooze });
        await Assert.ThrowsAsync<OverdueAlarmRegistrationException>(() =>
            reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc));
        Assert.Empty(operations);

        due.TriggerAt = Now.AddDays(1);
        await repository.SaveAsync(new[] { due, snooze });
        await reconciler.ReconcileRegularAsync(new[] { alarm }, TimeZoneInfo.Utc);
        var journal = await repository.LoadAsync();
        Assert.Equal(snooze.Registration, Assert.Single(journal, entry => entry.Kind == AlarmRegistrationKind.Snooze).Registration);
        Assert.DoesNotContain(snooze.Registration, scheduler.Registered.Keys);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestStore(AlarmRegistrationRepository repository, List<string> operations) : IAlarmRegistrationStore
    {
        private int saveCalls;
        public int FailSaveOnCall { get; set; }
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => repository.LoadAsync();
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => repository.LoadExistingAsync();

        public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
        {
            operations.Add("save");
            if (++saveCalls == FailSaveOnCall)
                throw new IOException("Simulated journal failure.");
            await repository.SaveAsync(registrations);
        }
    }

    private sealed class TestScheduler(List<string> operations) : IAlarmRegistrationScheduler
    {
        public Dictionary<AlarmRegistration, DateTimeOffset> Registered { get; } = new();
        public int ScheduleCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int FailScheduleOnCall { get; set; }
        public int FailCancelOnCall { get; set; }

        public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt)
        {
            operations.Add("schedule");
            if (++ScheduleCalls == FailScheduleOnCall)
                throw new InvalidOperationException("Simulated scheduling failure.");
            Registered[registration] = triggerAt;
        }

        public void Cancel(AlarmRegistration registration)
        {
            operations.Add("cancel");
            if (++CancelCalls == FailCancelOnCall)
                throw new InvalidOperationException("Simulated cancellation failure.");
            Registered.Remove(registration);
        }
    }
}
