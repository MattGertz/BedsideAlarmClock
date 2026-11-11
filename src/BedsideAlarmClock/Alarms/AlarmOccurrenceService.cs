namespace BedsideAlarmClock.Alarms;

public readonly record struct AlarmOccurrence(DateOnly Date, DateTimeOffset Instant);

public static class AlarmOccurrenceService
{
    public static AlarmOccurrence? GetNextOccurrence(
        AlarmDefinition alarm, DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(timeZone);
        alarm.Validate();
        return alarm.Enabled ? FindNext(alarm, after, timeZone, honorSkip: true) : null;
    }

    public static DateOnly FindNextOnceDate(int hour, int minute, DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        AlarmDefinition.ValidateTime(hour, minute);
        var date = LocalDate(after, timeZone);
        while (true)
        {
            var instant = Resolve(date, hour, minute, timeZone);
            if (instant is { } value && value > after)
                return date;
            if (date == DateOnly.MaxValue)
                throw new InvalidOperationException("No future local date is representable.");
            date = date.AddDays(1);
        }
    }

    public static DateOnly? SetSkipToNextOccurrence(
        AlarmDefinition alarm, DateTimeOffset after, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(timeZone);
        alarm.Validate();
        if (!alarm.IsRepeating)
            throw new InvalidOperationException("Only repeating alarms can skip an occurrence.");
        var occurrence = FindNext(alarm, after, timeZone, honorSkip: false);
        alarm.SkipDate = occurrence?.Date;
        return alarm.SkipDate;
    }

    private static AlarmOccurrence? FindNext(
        AlarmDefinition alarm, DateTimeOffset after, TimeZoneInfo timeZone, bool honorSkip)
    {
        if (!alarm.IsRepeating)
        {
            var date = alarm.OnceDate!.Value;
            var instant = Resolve(date, alarm.Hour, alarm.Minute, timeZone);
            return instant is { } value && value > after ? new(date, value) : null;
        }

        var current = LocalDate(after, timeZone);
        while (true)
        {
            if ((alarm.Days & AlarmDefinition.DayFlag(current)) != 0 &&
                (!honorSkip || alarm.SkipDate != current))
            {
                var instant = Resolve(current, alarm.Hour, alarm.Minute, timeZone);
                if (instant is { } value && value > after)
                    return new(current, value);
            }
            if (current == DateOnly.MaxValue)
                return null;
            current = current.AddDays(1);
        }
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateTimeOffset? Resolve(DateOnly date, int hour, int minute, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        var resolved = TryResolve(local, zone);
        if (resolved is not null)
            return resolved;

        // A gap moves forward only within the scheduled date; an entirely skipped date has no occurrence.
        var last = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified);
        var probe = local;
        while (probe < last)
        {
            var next = probe.Ticks > last.Ticks - TimeSpan.TicksPerMinute
                ? last : probe.AddMinutes(1);
            var candidate = TryResolve(next, zone);
            if (candidate is not null)
            {
                long low = probe.Ticks, high = next.Ticks;
                while (high - low > 1)
                {
                    var middle = low + (high - low) / 2;
                    if (TryResolve(new DateTime(middle, DateTimeKind.Unspecified), zone) is null)
                        low = middle;
                    else
                        high = middle;
                }
                return TryResolve(new DateTime(high, DateTimeKind.Unspecified), zone);
            }
            probe = next;
        }
        return null;
    }

    private static DateTimeOffset? TryResolve(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
            return null;
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        // Round-trip also detects local dates skipped by a date-line change.
        DateTimeOffset instant;
        try { instant = new DateTimeOffset(local, offset); }
        catch (ArgumentOutOfRangeException) { return null; }
        return TimeZoneInfo.ConvertTime(instant, zone).DateTime == local ? instant : null;
    }
}
