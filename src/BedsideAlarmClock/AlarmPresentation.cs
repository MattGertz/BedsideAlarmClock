using System.Globalization;
using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock;

internal static class AlarmPresentation
{
    public static readonly AlarmDays[] Days =
    [
        AlarmDays.Sunday, AlarmDays.Monday, AlarmDays.Tuesday, AlarmDays.Wednesday,
        AlarmDays.Thursday, AlarmDays.Friday, AlarmDays.Saturday
    ];

    public const AlarmDays Weekdays = AlarmDays.Monday | AlarmDays.Tuesday | AlarmDays.Wednesday |
                                      AlarmDays.Thursday | AlarmDays.Friday;
    public const AlarmDays Weekends = AlarmDays.Saturday | AlarmDays.Sunday;
    public const AlarmDays EveryDay = Weekdays | Weekends;

    public static string Time(int hour, int minute)
    {
        var format = Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true) ? "H:mm" : "h:mm tt";
        return DateTime.Today.AddHours(hour).AddMinutes(minute).ToString(format, CultureInfo.CurrentCulture);
    }

    public static string DaysText(AlarmDays days)
    {
        if (days == EveryDay) return "Every day";
        if (days == Weekdays) return "Weekdays";
        if (days == Weekends) return "Weekends";

        return string.Join(", ", Days.Select((day, index) => (day, index))
            .Where(item => (days & item.day) != 0)
            .Select(item => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)item.index)));
    }

    public static DateOnly NextOnceDate(int hour, int minute) =>
        AlarmOccurrenceService.FindNextOnceDate(hour, minute, DateTimeOffset.Now, TimeZoneInfo.Local);

    public static bool HasFutureOnceOccurrence(AlarmDefinition alarm)
    {
        var candidate = Copy(alarm);
        candidate.Enabled = true;
        return AlarmOccurrenceService.GetNextOccurrence(candidate, DateTimeOffset.Now, TimeZoneInfo.Local) is not null;
    }

    public static string DateText(DateOnly date)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (date == today) return "Today";
        if (date == today.AddDays(1)) return "Tomorrow";
        return date.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
    }

    public static AlarmDefinition Copy(AlarmDefinition alarm) => new()
    {
        Id = alarm.Id,
        Hour = alarm.Hour,
        Minute = alarm.Minute,
        Enabled = alarm.Enabled,
        IsRepeating = alarm.IsRepeating,
        Days = alarm.Days,
        Label = alarm.Label,
        OnceDate = alarm.OnceDate,
        SkipDate = alarm.SkipDate
    };
}
