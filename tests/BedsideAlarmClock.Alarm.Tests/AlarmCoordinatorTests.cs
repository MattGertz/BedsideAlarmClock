using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"alarm-coordinator-{Guid.NewGuid():N}");

    public AlarmCoordinatorTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private AlarmCoordinator CreateCoordinator() =>
        new(new AlarmRepository(Path.Combine(directory, "alarms.json")));

    private static AlarmDefinition Once(int hour = 7) => new()
    {
        Id = Guid.NewGuid(), Hour = hour, Minute = 30, Enabled = true,
        OnceDate = new DateOnly(2025, 1, 2), Label = "Wake up"
    };

    private static AlarmDefinition Copy(AlarmDefinition alarm) => new()
    {
        Id = alarm.Id, Hour = alarm.Hour, Minute = alarm.Minute, Enabled = alarm.Enabled,
        IsRepeating = alarm.IsRepeating, Days = alarm.Days, Label = alarm.Label,
        OnceDate = alarm.OnceDate, SkipDate = alarm.SkipDate
    };

    [Fact]
    public async Task ConcurrentAddsAndIndependentEditsAreNotLost()
    {
        var coordinator = CreateCoordinator();
        var first = Once();
        var second = Once(8);
        await Task.WhenAll(coordinator.AddAsync(first), coordinator.AddAsync(second));
        var saved = await coordinator.LoadAsync();
        Assert.Equal(2, saved.Count);

        var editedFirst = Copy(first);
        var editedSecond = Copy(second);
        editedFirst.Label = "First";
        editedSecond.Label = "Second";
        await Task.WhenAll(coordinator.ReplaceAsync(first, editedFirst),
            coordinator.ReplaceAsync(second, editedSecond));
        saved = await coordinator.LoadAsync();
        Assert.Equal("First", saved.Single(a => a.Id == first.Id).Label);
        Assert.Equal("Second", saved.Single(a => a.Id == second.Id).Label);
    }

    [Fact]
    public async Task StaleEditOrDeleteDoesNotOverwriteNewerState()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        await coordinator.AddAsync(original);
        var newer = Copy(original);
        newer.Enabled = false;
        await coordinator.ReplaceAsync(original, newer);

        var staleEdit = Copy(original);
        staleEdit.Label = "Outdated";
        await Assert.ThrowsAsync<AlarmConflictException>(() => coordinator.ReplaceAsync(original, staleEdit));
        await Assert.ThrowsAsync<AlarmConflictException>(() => coordinator.DeleteAsync(original));
        var saved = Assert.Single(await coordinator.LoadAsync());
        Assert.False(saved.Enabled);
        Assert.Equal("Wake up", saved.Label);

        await coordinator.DeleteAsync(newer);
        await Assert.ThrowsAsync<AlarmConflictException>(() => coordinator.ReplaceAsync(newer, staleEdit));
        Assert.Empty(await coordinator.LoadAsync());
    }

    [Fact]
    public async Task ConcurrentEditsOfSameAlarmHaveOneWinner()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        await coordinator.AddAsync(original);
        var first = Copy(original);
        var second = Copy(original);
        first.Label = "First";
        second.Label = "Second";

        async Task<Exception?> TryReplace(AlarmDefinition updated) =>
            await Record.ExceptionAsync(() => coordinator.ReplaceAsync(original, updated));

        var results = await Task.WhenAll(TryReplace(first), TryReplace(second));
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is AlarmConflictException);
        Assert.Contains(Assert.Single(await coordinator.LoadAsync()).Label, new[] { "First", "Second" });
    }

    [Fact]
    public async Task InvalidMutationLeavesPersistedDataUnchanged()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        await coordinator.AddAsync(original);
        var invalid = Copy(original);
        invalid.Hour = 24;
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ReplaceAsync(original, invalid));
        Assert.Equal(7, Assert.Single(await coordinator.LoadAsync()).Hour);
    }

    [Fact]
    public async Task ReconciliationAndEditUseSameGateAndNextPassSeesEdit()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        var updated = Copy(original);
        updated.Hour = 8;
        await coordinator.AddAsync(original);
        var repository = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var store = new BlockingStore(repository);
        var scheduler = new TestScheduler();
        var reconciler = new AlarmRegistrationReconciler(store, scheduler, new FixedTimeProvider());

        var reconciliation = coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        Task<IReadOnlyList<AlarmDefinition>> edit;
        try
        {
            await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            edit = coordinator.ReplaceAsync(original, updated);
            await Assert.ThrowsAsync<TimeoutException>(() => edit.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            store.Continue.TrySetResult();
        }
        await Task.WhenAll(reconciliation, edit);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T07:30:00Z"), Assert.Single(scheduler.Registered).Value);
        Assert.Equal(8, Assert.Single(await coordinator.LoadAsync()).Hour);

        await coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc);
        var scheduled = Assert.Single(scheduler.Registered);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T08:30:00Z"), scheduled.Value);
        Assert.Equal(scheduled.Key, Assert.Single(await repository.LoadAsync()).Registration);
    }

    [Fact]
    public async Task FailedReconciliationReleasesGateForLaterEdits()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        await coordinator.AddAsync(original);
        var reconciler = new AlarmRegistrationReconciler(new FailingStore(), new TestScheduler(), new FixedTimeProvider());

        await Assert.ThrowsAsync<IOException>(() => coordinator.ReconcileRegularAsync(reconciler, TimeZoneInfo.Utc));
        var updated = Copy(original);
        updated.Hour = 8;
        var result = await coordinator.ReplaceAsync(original, updated).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(8, Assert.Single(result).Hour);
    }

    [Fact]
    public async Task InspectionDistinguishesObsoleteEarlyAndCurrentRegularCallbacks()
    {
        var coordinator = CreateCoordinator();
        var alarm = Once();
        await coordinator.AddAsync(alarm);
        var store = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var trigger = DateTimeOffset.Parse("2025-01-02T07:30:00Z");
        var current = new AlarmRegistrationEntry
        {
            AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = trigger
        };
        var old = new AlarmRegistrationEntry
        {
            AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.PendingCancellation, TriggerAt = trigger
        };
        await store.SaveAsync(new[] { old, current });

        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store, old.Registration, trigger, TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store,
                new AlarmRegistration(alarm.Id, Guid.NewGuid(), AlarmRegistrationKind.Regular), trigger, TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryInspection.Early,
            await coordinator.InspectRegularDeliveryAsync(store, current.Registration, trigger.AddTicks(-1), TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryInspection.DueOrLate,
            await coordinator.InspectRegularDeliveryAsync(store, current.Registration, trigger, TimeZoneInfo.Utc));
        Assert.Equal(RegularDeliveryInspection.DueOrLate,
            await coordinator.InspectRegularDeliveryAsync(store, current.Registration, trigger.AddHours(1), TimeZoneInfo.Utc));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.InspectRegularDeliveryAsync(store,
            new AlarmRegistration(alarm.Id, current.RegistrationId, AlarmRegistrationKind.Snooze), trigger, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task InspectionUsesLatestDefinitionEvenBeforeReconciliation()
    {
        var coordinator = CreateCoordinator();
        var original = Once();
        await coordinator.AddAsync(original);
        var store = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var trigger = DateTimeOffset.Parse("2025-01-02T07:30:00Z");
        var entry = new AlarmRegistrationEntry
        {
            AlarmId = original.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = trigger
        };
        await store.SaveAsync(new[] { entry });

        var updated = Copy(original);
        updated.Label = "New label";
        await coordinator.ReplaceAsync(original, updated);
        Assert.Equal(RegularDeliveryInspection.DueOrLate,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, TimeZoneInfo.Utc));
        var changed = Copy(updated);
        changed.Hour = 8;
        await coordinator.ReplaceAsync(updated, changed);
        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, TimeZoneInfo.Utc));
        var disabled = Copy(changed);
        disabled.Hour = original.Hour;
        disabled.Enabled = false;
        await coordinator.ReplaceAsync(changed, disabled);
        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, TimeZoneInfo.Utc));
        await coordinator.DeleteAsync(disabled);
        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task InspectionHonorsSkipAndDaylightSavingResolution()
    {
        var coordinator = CreateCoordinator();
        var alarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 2, Minute = 30, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Sunday
        };
        await coordinator.AddAsync(alarm);
        var store = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var trigger = DateTimeOffset.Parse("2025-03-09T10:00:00Z");
        var entry = new AlarmRegistrationEntry
        {
            AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = AlarmRegistrationKind.Regular,
            State = AlarmRegistrationState.Desired, TriggerAt = trigger
        };
        await store.SaveAsync(new[] { entry });
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        Assert.Equal(RegularDeliveryInspection.DueOrLate,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, zone));
        var skipped = Copy(alarm);
        skipped.SkipDate = new DateOnly(2025, 3, 9);
        await coordinator.ReplaceAsync(alarm, skipped);
        Assert.Equal(RegularDeliveryInspection.Stale,
            await coordinator.InspectRegularDeliveryAsync(store, entry.Registration, trigger, zone));
    }

    [Fact]
    public async Task InspectionReportsMissingOrDamagedFilesRatherThanTreatingThemAsStale()
    {
        var coordinator = CreateCoordinator();
        var store = new AlarmRegistrationRepository(Path.Combine(directory, "registrations.json"));
        var alarm = Once();
        var registration = new AlarmRegistration(alarm.Id, Guid.NewGuid(), AlarmRegistrationKind.Regular);
        var trigger = DateTimeOffset.Parse("2025-01-02T07:30:00Z");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            coordinator.InspectRegularDeliveryAsync(store, registration, trigger, TimeZoneInfo.Utc));
        await store.SaveAsync(Array.Empty<AlarmRegistrationEntry>());
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            coordinator.InspectRegularDeliveryAsync(store, registration, trigger, TimeZoneInfo.Utc));
        await coordinator.AddAsync(alarm);
        await File.WriteAllTextAsync(Path.Combine(directory, "registrations.json"), "{");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            coordinator.InspectRegularDeliveryAsync(store, registration, trigger, TimeZoneInfo.Utc));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2025-01-01T00:00:00Z");
    }

    private sealed class BlockingStore(AlarmRegistrationRepository repository) : IAlarmRegistrationStore
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync()
        {
            LoadStarted.TrySetResult();
            await Continue.Task;
            return await repository.LoadAsync();
        }

        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => repository.LoadExistingAsync();

        public Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations) => repository.SaveAsync(registrations);
    }

    private sealed class FailingStore : IAlarmRegistrationStore
    {
        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() =>
            Task.FromException<IReadOnlyList<AlarmRegistrationEntry>>(new IOException("Simulated journal failure."));

        public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => LoadAsync();

        public Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations) =>
            throw new InvalidOperationException("The failing store should not be written.");
    }

    private sealed class TestScheduler : IAlarmRegistrationScheduler
    {
        public Dictionary<AlarmRegistration, DateTimeOffset> Registered { get; } = new();
        public void Schedule(AlarmRegistration registration, DateTimeOffset triggerAt) =>
            Registered[registration] = triggerAt;
        public void Cancel(AlarmRegistration registration) => Registered.Remove(registration);
    }
}
