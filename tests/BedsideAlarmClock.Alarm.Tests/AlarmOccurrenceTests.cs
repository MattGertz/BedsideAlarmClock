using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public class AlarmOccurrenceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static AlarmDefinition Repeating(AlarmDays days, int hour = 7, int minute = 0) => new()
    {
        Id = Guid.NewGuid(), Hour = hour, Minute = minute, Enabled = true,
        IsRepeating = true, Days = days
    };

    [Fact]
    public void OnceUsesStrictlyFutureLocalDateAndExpires()
    {
        var after = DateTimeOffset.Parse("2025-01-01T07:00:00+00:00");
        var date = AlarmOccurrenceService.FindNextOnceDate(7, 0, after, Utc);
        Assert.Equal(new DateOnly(2025, 1, 2), date);
        var alarm = new AlarmDefinition { Id = Guid.NewGuid(), Hour = 7, Enabled = true, OnceDate = date };
        var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, after, Utc);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T07:00:00+00:00"), occurrence!.Value.Instant);
        Assert.Null(AlarmOccurrenceService.GetNextOccurrence(alarm, occurrence.Value.Instant, Utc));
        alarm.Enabled = false;
        Assert.Null(AlarmOccurrenceService.GetNextOccurrence(alarm, after, Utc));
    }

    [Fact]
    public void ArbitraryDaysMidnightAndSkipUseLocalDate()
    {
        var alarm = Repeating(AlarmDays.Monday | AlarmDays.Thursday, 0);
        var after = DateTimeOffset.Parse("2025-01-05T23:59:00Z");
        Assert.Equal(new DateOnly(2025, 1, 6), AlarmOccurrenceService.SetSkipToNextOccurrence(alarm, after, Utc));
        Assert.Equal(new DateOnly(2025, 1, 6), AlarmOccurrenceService.SetSkipToNextOccurrence(alarm, after, Utc));
        Assert.Equal(new DateOnly(2025, 1, 9), AlarmOccurrenceService.GetNextOccurrence(alarm, after, Utc)!.Value.Date);
        alarm.SkipDate = null;
        Assert.Equal(new DateOnly(2025, 1, 6), AlarmOccurrenceService.GetNextOccurrence(alarm, after, Utc)!.Value.Date);
        alarm.Enabled = false;
        Assert.Equal(new DateOnly(2025, 1, 6), AlarmOccurrenceService.SetSkipToNextOccurrence(alarm, after, Utc));
    }

    [Fact]
    public void DistinctAlarmsCanShareTimeAndHaveIndependentExceptions()
    {
        var first = Repeating(AlarmDays.Monday);
        var second = Repeating(AlarmDays.Monday);
        var after = DateTimeOffset.Parse("2025-01-05T00:00:00Z");
        first.SkipDate = new DateOnly(2025, 1, 6);
        Assert.Equal(new DateOnly(2025, 1, 13), AlarmOccurrenceService.GetNextOccurrence(first, after, Utc)!.Value.Date);
        Assert.Equal(new DateOnly(2025, 1, 6), AlarmOccurrenceService.GetNextOccurrence(second, after, Utc)!.Value.Date);
    }

    [Fact]
    public void ZoneControlsLocalDateAndOffset()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var after = DateTimeOffset.Parse("2025-01-06T06:00:00Z"); // Sunday in Los Angeles.
        var alarm = Repeating(AlarmDays.Sunday, 23);
        var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, after, zone)!.Value;
        Assert.Equal(new DateOnly(2025, 1, 5), occurrence.Date);
        Assert.Equal(DateTimeOffset.Parse("2025-01-05T23:00:00-08:00"), occurrence.Instant);
        Assert.Equal(TimeSpan.FromHours(-8), occurrence.Instant.Offset);
        Assert.Equal(new DateOnly(2025, 1, 5), AlarmOccurrenceService.FindNextOnceDate(23, 0, after, zone));
    }

    [Fact]
    public void SpringGapMovesToFirstValidTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var alarm = Repeating(AlarmDays.Sunday, 2, 30);
        var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, DateTimeOffset.Parse("2025-03-09T08:00:00Z"), zone)!.Value;
        Assert.Equal(new DateOnly(2025, 3, 9), occurrence.Date);
        Assert.Equal(DateTimeOffset.Parse("2025-03-09T03:00:00-07:00"), occurrence.Instant);
        Assert.Equal(TimeSpan.FromHours(-7), occurrence.Instant.Offset);
    }

    [Fact]
    public void FallOverlapUsesEarliestInstantOnly()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var alarm = Repeating(AlarmDays.Sunday, 1, 30);
        var occurrence = AlarmOccurrenceService.GetNextOccurrence(alarm, DateTimeOffset.Parse("2025-11-02T07:00:00Z"), zone)!.Value;
        Assert.Equal(DateTimeOffset.Parse("2025-11-02T01:30:00-07:00"), occurrence.Instant);
        Assert.Equal(TimeSpan.FromHours(-7), occurrence.Instant.Offset);
        var next = AlarmOccurrenceService.GetNextOccurrence(alarm, DateTimeOffset.Parse("2025-11-02T09:00:00Z"), zone)!.Value;
        Assert.Equal(new DateOnly(2025, 11, 9), next.Date);
    }

    [Fact]
    public void GapExtendingPastMidnightDoesNotMoveOccurrenceToAnotherDate()
    {
        var transitionStart = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 3, 1);
        var transitionEnd = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1), 11, 1);
        var before = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2010, 1, 1), new DateTime(2011, 12, 29), TimeSpan.Zero,
            transitionStart, transitionEnd, TimeSpan.FromHours(-10));
        var afterRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2011, 12, 30), new DateTime(2013, 1, 1), TimeSpan.Zero,
            transitionStart, transitionEnd, TimeSpan.FromHours(14));
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "SkippedDate", TimeSpan.Zero, "SkippedDate", "SkippedDate", "SkippedDate",
            new[] { before, afterRule });
        var after = DateTimeOffset.Parse("2011-12-28T18:00:00-10:00");
        var alarm = Repeating(AlarmDays.Thursday, 16);
        var next = AlarmOccurrenceService.GetNextOccurrence(alarm, after, zone)!.Value;
        Assert.Equal(new DateOnly(2012, 1, 5), next.Date);
        Assert.Equal(new DateOnly(2011, 12, 30), AlarmOccurrenceService.FindNextOnceDate(16, 0, after, zone));
        var once = new AlarmDefinition { Id = Guid.NewGuid(), Hour = 16, Enabled = true, OnceDate = new DateOnly(2011, 12, 29) };
        Assert.Null(AlarmOccurrenceService.GetNextOccurrence(once, after, zone));
    }
}
