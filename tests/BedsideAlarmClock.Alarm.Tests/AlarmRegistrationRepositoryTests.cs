using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmRegistrationRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"registration-tests-{Guid.NewGuid():N}");
    private string PathName => Path.Combine(directory, "registrations.json");

    public AlarmRegistrationRepositoryTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static AlarmRegistrationEntry Entry(Guid alarmId, AlarmRegistrationState state,
        AlarmRegistrationKind kind = AlarmRegistrationKind.Regular) => new()
    {
        AlarmId = alarmId,
        RegistrationId = Guid.NewGuid(),
        Kind = kind,
        TriggerAt = new DateTimeOffset(2025, 1, 2, 7, 30, 0, TimeSpan.FromHours(-8)),
        State = state
    };

    [Fact]
    public async Task RoundTripRetainsDesiredAndPendingCancellationEntries()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        Assert.Empty(await repository.LoadAsync());
        var alarmId = Guid.NewGuid();
        var old = Entry(alarmId, AlarmRegistrationState.PendingCancellation);
        var current = Entry(alarmId, AlarmRegistrationState.Desired);
        var snooze = Entry(alarmId, AlarmRegistrationState.Desired, AlarmRegistrationKind.Snooze);
        await repository.SaveAsync(new[] { old, current, snooze });

        var loaded = await new AlarmRegistrationRepository(PathName).LoadAsync();
        Assert.Equal(3, loaded.Count);
        Assert.Equal(old.Registration, loaded[0].Registration);
        Assert.Equal(AlarmRegistrationState.PendingCancellation, loaded[0].State);
        Assert.Equal(current.Registration, loaded[1].Registration);
        Assert.Equal(current.TriggerAt, loaded[1].TriggerAt);
        Assert.Equal(snooze.Registration, loaded[2].Registration);
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task PersistedPlanKeepsIdentityWhenPlannedAgainAfterReload()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var now = DateTimeOffset.Parse("2025-01-05T00:00:00Z");
        var alarm = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Enabled = true,
            IsRepeating = true, Days = AlarmDays.Monday
        };
        var planned = AlarmRegistrationPlanner.PlanRegularRegistrations(
            new[] { alarm }, await repository.LoadAsync(), now, TimeZoneInfo.Utc);
        await repository.SaveAsync(planned);

        var reloaded = await new AlarmRegistrationRepository(PathName).LoadAsync();
        var again = AlarmRegistrationPlanner.PlanRegularRegistrations(
            new[] { alarm }, reloaded, now, TimeZoneInfo.Utc);
        Assert.Equal(Assert.Single(planned).Registration, Assert.Single(again).Registration);
    }

    [Fact]
    public async Task InvalidOrConflictingEntriesDoNotReplaceSavedState()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var current = Entry(Guid.NewGuid(), AlarmRegistrationState.Desired);
        await repository.SaveAsync(new[] { current });
        var before = await File.ReadAllTextAsync(PathName);

        var conflicting = Entry(current.AlarmId, AlarmRegistrationState.Desired);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { current, conflicting }));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { current, current }));
        conflicting.Kind = (AlarmRegistrationKind)42;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { conflicting }));
        conflicting.Kind = AlarmRegistrationKind.Regular;
        conflicting.State = (AlarmRegistrationState)42;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { conflicting }));
        conflicting.State = AlarmRegistrationState.Desired;
        conflicting.RegistrationId = Guid.Empty;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { conflicting }));
        Assert.Equal(before, await File.ReadAllTextAsync(PathName));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Registrations\":[]}")]
    [InlineData("{\"Version\":1,\"Registrations\":null}")]
    [InlineData("{\"Version\":1,\"Registrations\":[null]}")]
    [InlineData("{\"Version\":1,\"Registrations\":[{}]}")]
    public async Task DamagedOrUnsupportedDocumentsAreNotTreatedAsEmpty(string json)
    {
        await File.WriteAllTextAsync(PathName, json);
        await Assert.ThrowsAnyAsync<Exception>(() => new AlarmRegistrationRepository(PathName).LoadAsync());
    }

    [Fact]
    public async Task ConflictingStoredEntriesAreRejectedOnLoad()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var first = Entry(Guid.NewGuid(), AlarmRegistrationState.Desired);
        var second = Entry(Guid.NewGuid(), AlarmRegistrationState.Desired);
        await repository.SaveAsync(new[] { first, second });
        var json = await File.ReadAllTextAsync(PathName);
        await File.WriteAllTextAsync(PathName, json.Replace(second.AlarmId.ToString(), first.AlarmId.ToString()));

        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
    }

    [Fact]
    public async Task DeliveryEvidenceMustMatchTheRegistrationState()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var entry = Entry(Guid.NewGuid(), AlarmRegistrationState.DeliveryObserved);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.ObservedAt = entry.TriggerAt.AddTicks(-1);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.ObservedAt = entry.TriggerAt;
        entry.Kind = AlarmRegistrationKind.Snooze;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.Kind = AlarmRegistrationKind.Regular;
        await repository.SaveAsync(new[] { entry });
        Assert.Equal(entry.ObservedAt, Assert.Single(await repository.LoadAsync()).ObservedAt);
        var json = await File.ReadAllTextAsync(PathName);
        await File.WriteAllTextAsync(PathName, json.Replace("\"State\": 2", "\"State\": 0"));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
    }

    [Fact]
    public async Task MissedStateRequiresConsistentReasonAndTiming()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var entry = Entry(Guid.NewGuid(), AlarmRegistrationState.Missed);
        entry.MissedAt = entry.TriggerAt.AddSeconds(30);
        entry.MissedReason = MissedAlarmReason.DeadlineExceeded;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.MissedAt = entry.TriggerAt.AddSeconds(31);
        entry.ObservedAt = entry.MissedAt.Value.AddTicks(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.ObservedAt = null;
        entry.MissedReason = (MissedAlarmReason)42;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.MissedReason = MissedAlarmReason.DeadlineExceeded;
        entry.CancellationPending = true;
        await repository.SaveAsync(new[] { entry });
        var loaded = Assert.Single(await repository.LoadAsync());
        Assert.Equal(entry.MissedAt, loaded.MissedAt);
        Assert.True(loaded.CancellationPending);
    }

    [Fact]
    public async Task PlaybackReservationRequiresObservedAndTimelyDecision()
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var entry = Entry(Guid.NewGuid(), AlarmRegistrationState.PlaybackReserved);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.ObservedAt = entry.TriggerAt.AddSeconds(1);
        entry.PlaybackReservedAt = entry.TriggerAt;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.PlaybackReservedAt = entry.TriggerAt.AddSeconds(30).AddTicks(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.PlaybackReservedAt = entry.TriggerAt.AddSeconds(30);
        entry.Kind = AlarmRegistrationKind.Snooze;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.Kind = AlarmRegistrationKind.Regular;
        await repository.SaveAsync(new[] { entry });
        Assert.Equal(entry.PlaybackReservedAt, Assert.Single(await repository.LoadExistingAsync()).PlaybackReservedAt);
        entry.State = AlarmRegistrationState.DeliveryObserved;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackEvidenceRequiresAValidStart(bool completed)
    {
        var repository = new AlarmRegistrationRepository(PathName);
        var state = completed ? AlarmRegistrationState.PlaybackCompleted : AlarmRegistrationState.PlaybackStarted;
        var entry = Entry(Guid.NewGuid(), state);
        entry.ObservedAt = entry.TriggerAt;
        entry.PlaybackReservedAt = entry.TriggerAt.AddSeconds(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.PlaybackStartedAt = entry.TriggerAt;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.PlaybackStartedAt = entry.TriggerAt.AddSeconds(30).AddTicks(1);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
        entry.PlaybackStartedAt = entry.TriggerAt.AddSeconds(30);
        entry.CancellationPending = true;
        if (state == AlarmRegistrationState.PlaybackStarted)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { entry }));
            entry.CancellationPending = false;
        }
        await repository.SaveAsync(new[] { entry });
        var loaded = Assert.Single(await repository.LoadExistingAsync());
        Assert.Equal(entry.PlaybackStartedAt, loaded.PlaybackStartedAt);
        Assert.Equal(entry.CancellationPending, loaded.CancellationPending);
    }

    [Fact]
    public async Task StorageFailureIsReportedAndTemporaryFileRemoved()
    {
        var target = Path.Combine(directory, "destination");
        Directory.CreateDirectory(target);
        var repository = new AlarmRegistrationRepository(target);
        var error = await Record.ExceptionAsync(() =>
            repository.SaveAsync(new[] { Entry(Guid.NewGuid(), AlarmRegistrationState.Desired) }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(directory));
    }
}
