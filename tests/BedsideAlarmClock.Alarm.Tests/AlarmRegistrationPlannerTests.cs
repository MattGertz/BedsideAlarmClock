using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmRegistrationPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-05T00:00:00Z");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static AlarmDefinition Repeating() => new()
    {
        Id = Guid.NewGuid(), Hour = 7, Enabled = true,
        IsRepeating = true, Days = AlarmDays.Monday
    };

    private static AlarmRegistrationEntry Entry(AlarmDefinition alarm, DateTimeOffset trigger,
        AlarmRegistrationKind kind = AlarmRegistrationKind.Regular,
        AlarmRegistrationState state = AlarmRegistrationState.Desired) => new()
    {
        AlarmId = alarm.Id, RegistrationId = Guid.NewGuid(), Kind = kind,
        TriggerAt = trigger, State = state
    };

    [Fact]
    public void AddsDistinctRegistrationsAndKeepsUnchangedIdentity()
    {
        var first = Repeating();
        var second = Repeating();
        var planned = AlarmRegistrationPlanner.PlanRegularRegistrations(
            new[] { first, second }, Array.Empty<AlarmRegistrationEntry>(), Now, Utc);
        Assert.Equal(2, planned.Count);
        Assert.All(planned, entry => Assert.Equal(DateTimeOffset.Parse("2025-01-06T07:00:00Z"), entry.TriggerAt));
        Assert.NotEqual(planned[0].RegistrationId, planned[1].RegistrationId);

        first.Label = "Changed label";
        var again = AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { first, second }, planned, Now, Utc);
        Assert.Equal(planned.Select(entry => entry.Registration), again.Select(entry => entry.Registration));
        Assert.All(again, entry => Assert.Equal(AlarmRegistrationState.Desired, entry.State));
    }

    [Fact]
    public void SkipAndUndoRetireOldIdentitiesWithoutTouchingSnooze()
    {
        var alarm = Repeating();
        var monday = DateTimeOffset.Parse("2025-01-06T07:00:00Z");
        var old = Entry(alarm, monday);
        var snooze = Entry(alarm, Now.AddMinutes(10), AlarmRegistrationKind.Snooze);
        alarm.SkipDate = new DateOnly(2025, 1, 6);

        var planned = AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, new[] { old, snooze }, Now, Utc);
        Assert.Equal(AlarmRegistrationState.PendingCancellation, planned.Single(entry => entry.Registration == old.Registration).State);
        Assert.Equal(AlarmRegistrationState.Desired, old.State);
        Assert.Equal(AlarmRegistrationState.Desired, planned.Single(entry => entry.Registration == snooze.Registration).State);
        var next = Assert.Single(planned, entry => entry.Kind == AlarmRegistrationKind.Regular && entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(DateTimeOffset.Parse("2025-01-13T07:00:00Z"), next.TriggerAt);
        Assert.NotEqual(old.RegistrationId, next.RegistrationId);

        alarm.SkipDate = null;
        var undone = AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, planned, Now, Utc);
        var restored = Assert.Single(undone, entry => entry.Kind == AlarmRegistrationKind.Regular && entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(monday, restored.TriggerAt);
        Assert.NotEqual(old.RegistrationId, restored.RegistrationId);
        Assert.NotEqual(next.RegistrationId, restored.RegistrationId);
        Assert.Equal(2, undone.Count(entry => entry.State == AlarmRegistrationState.PendingCancellation));
    }

    [Fact]
    public void DisabledAndDeletedAlarmsRetainIdentitiesForCancellation()
    {
        var disabled = Repeating();
        var deleted = Repeating();
        var first = Entry(disabled, Now.AddDays(1));
        var second = Entry(deleted, Now.AddDays(1));
        disabled.Enabled = false;

        var planned = AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { disabled }, new[] { first, second }, Now, Utc);
        Assert.Equal(2, planned.Count);
        Assert.All(planned, entry => Assert.Equal(AlarmRegistrationState.PendingCancellation, entry.State));
        Assert.Contains(planned, entry => entry.Registration == first.Registration);
        Assert.Contains(planned, entry => entry.Registration == second.Registration);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void DueOrOverdueRegistrationMustBeResolvedBeforePlanning(int minutes)
    {
        var alarm = Repeating();
        var due = Entry(alarm, Now.AddMinutes(minutes));
        alarm.Enabled = false;
        var exception = Assert.Throws<OverdueAlarmRegistrationException>(() =>
            AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, new[] { due }, Now, Utc));
        Assert.Equal(due.Registration, exception.Registration);
        Assert.Equal(AlarmRegistrationState.Desired, due.State);
    }

    [Fact]
    public void TimeZoneChangeReplacesRegistrationAndInvalidInputIsRejected()
    {
        var alarm = Repeating();
        var old = Entry(alarm, DateTimeOffset.Parse("2025-01-06T07:00:00Z"));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Offset", TimeSpan.FromHours(-8), "Offset", "Offset");
        var planned = AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, new[] { old }, Now, zone);
        var current = Assert.Single(planned, entry => entry.State == AlarmRegistrationState.Desired);
        Assert.Equal(DateTimeOffset.Parse("2025-01-06T15:00:00Z"), current.TriggerAt);
        Assert.NotEqual(old.RegistrationId, current.RegistrationId);
        Assert.Single(planned, entry => entry.State == AlarmRegistrationState.PendingCancellation);
        Assert.Throws<InvalidDataException>(() =>
            AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm, alarm }, planned, Now, zone));
        Assert.Throws<InvalidDataException>(() =>
            AlarmRegistrationPlanner.PlanRegularRegistrations(new[] { alarm }, new[] { old, old }, Now, zone));
    }
}
