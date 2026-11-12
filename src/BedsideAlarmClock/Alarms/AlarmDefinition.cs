using System.Text.Json.Serialization;

namespace BedsideAlarmClock.Alarms;

[Flags]
public enum AlarmDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64
}

public sealed class AlarmDefinition
{
    [JsonRequired]
    public Guid Id { get; set; }
    [JsonRequired]
    public int Hour { get; set; }
    [JsonRequired]
    public int Minute { get; set; }
    [JsonRequired]
    public bool Enabled { get; set; }
    [JsonRequired]
    public bool IsRepeating { get; set; }
    [JsonRequired]
    public AlarmDays Days { get; set; }
    [JsonRequired]
    public string Label { get; set; } = string.Empty;
    [JsonRequired]
    public DateOnly? OnceDate { get; set; }
    [JsonRequired]
    public DateOnly? SkipDate { get; set; }
    public string? SoundUri { get; set; }
    public double Volume { get; set; } = 1;
    public int? SnoozeMinutes { get; set; } = 10;

    public void Validate()
    {
        if (Id == Guid.Empty)
            throw new InvalidDataException("An alarm must have a nonempty ID.");
        ValidateTime(Hour, Minute);
        if (Label is null)
            throw new InvalidDataException("An alarm label cannot be null.");
        if (SoundUri is not null &&
            (!Uri.TryCreate(SoundUri, UriKind.Absolute, out var sound) ||
             sound.Scheme is not ("content" or "file")))
            throw new InvalidDataException("An alarm sound must be a content or local file URI.");
        if (!double.IsFinite(Volume) || Volume is < 0 or > 1)
            throw new InvalidDataException("Alarm volume must be between 0 and 1.");
        if (SnoozeMinutes is < 1 or > 1440)
            throw new InvalidDataException("Snooze must be between 1 and 1440 minutes, or disabled.");
        const AlarmDays allDays = (AlarmDays)127;
        if ((Days & ~allDays) != 0)
            throw new InvalidDataException("An alarm has unknown day flags.");
        if (IsRepeating)
        {
            if (Days == AlarmDays.None || OnceDate is not null)
                throw new InvalidDataException("A repeating alarm requires days and no once date.");
            if (SkipDate is { } skip && (Days & DayFlag(skip)) == 0)
                throw new InvalidDataException("A skip date must be on a selected day.");
        }
        else if (Days != AlarmDays.None || OnceDate is null || SkipDate is not null)
        {
            throw new InvalidDataException("A once alarm requires a date, no days, and no skip date.");
        }
    }

    internal static void ValidateTime(int hour, int minute)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            throw new InvalidDataException("An alarm time must be between 00:00 and 23:59.");
    }

    internal static AlarmDays DayFlag(DateOnly date) => (AlarmDays)(1 << (int)date.DayOfWeek);
}
