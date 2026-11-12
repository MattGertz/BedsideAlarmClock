using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedsideAlarmClock.Alarms;

internal enum AlarmRegistrationState
{
    Desired,
    PendingCancellation,
    DeliveryObserved,
    Missed,
    PlaybackReserved,
    PlaybackStarted,
    PlaybackCompleted,
    Interrupted,
    ClockUncertain
}

internal enum MissedAlarmReason
{
    DeadlineExceeded,
    KnownOutage
}

internal sealed class AlarmRegistrationEntry
{
    [JsonRequired]
    public Guid AlarmId { get; set; }
    [JsonRequired]
    public Guid RegistrationId { get; set; }
    [JsonRequired]
    public AlarmRegistrationKind Kind { get; set; }
    [JsonRequired]
    public DateTimeOffset TriggerAt { get; set; }
    [JsonRequired]
    public AlarmRegistrationState State { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ObservedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PlaybackReservedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PlaybackStartedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? InterruptedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ClockUncertainAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SnoozedFromRegistrationId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AlarmRegistrationKind? SnoozedFromKind { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? MissedAt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MissedAlarmReason? MissedReason { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CancellationPending { get; set; }

    [JsonIgnore]
    public AlarmRegistration Registration => new(AlarmId, RegistrationId, Kind);
}

internal interface IAlarmRegistrationStore
{
    Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync();
    Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync();
    Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations);
}

internal sealed class AlarmRegistrationRepository : IAlarmRegistrationStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string path;

    public AlarmRegistrationRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync() => LoadAsync(allowMissing: true);

    public Task<IReadOnlyList<AlarmRegistrationEntry>> LoadExistingAsync() => LoadAsync(allowMissing: false);

    private async Task<IReadOnlyList<AlarmRegistrationEntry>> LoadAsync(bool allowMissing)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return Array.Empty<AlarmRegistrationEntry>();
        }

        await using (stream)
        {
            var document = await JsonSerializer.DeserializeAsync<RegistrationDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The registration document is null.");
            if (document.Version != CurrentVersion)
                throw new InvalidDataException($"Unsupported registration document version: {document.Version}.");
            Validate(document.Registrations);
            return document.Registrations;
        }
    }

    public async Task SaveAsync(IReadOnlyList<AlarmRegistrationEntry> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        Validate(registrations);
        await AtomicJsonFile.WriteAsync(path, new RegistrationDocument
        {
            Version = CurrentVersion,
            Registrations = registrations
        }, JsonOptions);
    }

    internal static void Validate(IReadOnlyList<AlarmRegistrationEntry>? registrations)
    {
        if (registrations is null)
            throw new InvalidDataException("The registration list cannot be null.");
        var identities = new HashSet<AlarmRegistration>();
        var desiredRegular = new HashSet<Guid>();
        var snoozeSources = new HashSet<AlarmRegistration>();
        foreach (var entry in registrations)
        {
            if (entry is null || entry.AlarmId == Guid.Empty || entry.RegistrationId == Guid.Empty ||
                !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.State) || entry.TriggerAt == default)
                throw new InvalidDataException("An alarm registration has invalid or missing data.");
            if (entry.ObservedAt is { } observed && observed < entry.TriggerAt)
                throw new InvalidDataException("An alarm registration has inconsistent delivery evidence.");
            if (entry.State != AlarmRegistrationState.Interrupted && entry.InterruptedAt is not null)
                throw new InvalidDataException("Only an interrupted alarm can have interruption evidence.");
            if (entry.State != AlarmRegistrationState.ClockUncertain && entry.ClockUncertainAt is not null)
                throw new InvalidDataException("Only a clock-uncertain alarm can have clock uncertainty evidence.");
            if (entry.State == AlarmRegistrationState.DeliveryObserved)
            {
                if (entry.ObservedAt is null ||
                    entry.PlaybackReservedAt is not null || entry.PlaybackStartedAt is not null || entry.MissedAt is not null ||
                    entry.MissedReason is not null || entry.CancellationPending)
                    throw new InvalidDataException("An observed delivery has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.PlaybackReserved)
            {
                if (entry.ObservedAt is not { } observedAt ||
                    entry.PlaybackReservedAt is not { } reservedAt || reservedAt < observedAt ||
                    AlarmDeliveryTiming.IsMissed(entry.TriggerAt, reservedAt, false) ||
                    entry.PlaybackStartedAt is not null || entry.MissedAt is not null ||
                    entry.MissedReason is not null || entry.CancellationPending)
                    throw new InvalidDataException("A playback reservation has inconsistent state.");
            }
            else if (entry.State is AlarmRegistrationState.PlaybackStarted or AlarmRegistrationState.PlaybackCompleted)
            {
                if (entry.ObservedAt is not { } observedAt ||
                    entry.PlaybackReservedAt is not { } reservedAt || reservedAt < observedAt ||
                    entry.PlaybackStartedAt is not { } startedAt || startedAt < reservedAt ||
                    AlarmDeliveryTiming.IsMissed(entry.TriggerAt, startedAt, false) ||
                    entry.MissedAt is not null || entry.MissedReason is not null ||
                    (entry.State == AlarmRegistrationState.PlaybackStarted && entry.CancellationPending))
                    throw new InvalidDataException("A playback start has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.Interrupted)
            {
                if (entry.ObservedAt is not { } observedAt ||
                    entry.InterruptedAt is not { } interruptedAt || interruptedAt < observedAt ||
                    (entry.PlaybackReservedAt is { } reservedAt &&
                        (reservedAt < observedAt || AlarmDeliveryTiming.IsMissed(entry.TriggerAt, reservedAt, false))) ||
                    (entry.PlaybackStartedAt is { } startedAt &&
                        (entry.PlaybackReservedAt is not { } reservation || startedAt < reservation ||
                         startedAt > interruptedAt || AlarmDeliveryTiming.IsMissed(entry.TriggerAt, startedAt, false))) ||
                    entry.PlaybackReservedAt > interruptedAt || entry.MissedAt is not null || entry.MissedReason is not null)
                    throw new InvalidDataException("An interrupted alarm has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.ClockUncertain)
            {
                if (entry.ClockUncertainAt is null || entry.MissedAt is not null || entry.MissedReason is not null ||
                    (entry.PlaybackReservedAt is not null && entry.ObservedAt is null) ||
                    (entry.PlaybackStartedAt is not null && entry.PlaybackReservedAt is null) ||
                    entry.PlaybackReservedAt < entry.ObservedAt ||
                    entry.PlaybackStartedAt < entry.PlaybackReservedAt ||
                    (entry.PlaybackReservedAt is { } reserved && AlarmDeliveryTiming.IsMissed(entry.TriggerAt, reserved, false)) ||
                    (entry.PlaybackStartedAt is { } started && AlarmDeliveryTiming.IsMissed(entry.TriggerAt, started, false)))
                    throw new InvalidDataException("A clock-uncertain alarm has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.Missed)
            {
                if (entry.MissedAt is not { } missedAt ||
                    missedAt < entry.TriggerAt || entry.ObservedAt > missedAt ||
                    entry.PlaybackReservedAt is not null || entry.PlaybackStartedAt is not null ||
                    entry.MissedReason is not { } reason || !Enum.IsDefined(reason) ||
                    (reason == MissedAlarmReason.DeadlineExceeded &&
                        !AlarmDeliveryTiming.IsMissed(entry.TriggerAt, missedAt, false)))
                    throw new InvalidDataException("A missed alarm has inconsistent state.");
            }
            else if (entry.ObservedAt is not null || entry.PlaybackReservedAt is not null ||
                entry.PlaybackStartedAt is not null || entry.MissedAt is not null ||
                entry.MissedReason is not null || entry.CancellationPending)
                throw new InvalidDataException("An alarm registration has inconsistent state.");
            if (!identities.Add(entry.Registration))
                throw new InvalidDataException("Registration identities must be unique.");
            if (entry.Kind == AlarmRegistrationKind.Regular && entry.State == AlarmRegistrationState.Desired &&
                !desiredRegular.Add(entry.AlarmId))
                throw new InvalidDataException("Only one desired regular registration per alarm is allowed.");
            if (entry.SnoozedFromRegistrationId is { } sourceId &&
                (entry.Kind != AlarmRegistrationKind.Snooze || sourceId == Guid.Empty ||
                 !Enum.IsDefined(entry.SnoozedFromKind ?? AlarmRegistrationKind.Regular) ||
                 !snoozeSources.Add(new(entry.AlarmId, sourceId,
                     entry.SnoozedFromKind ?? AlarmRegistrationKind.Regular))))
                throw new InvalidDataException("A snooze must identify a unique source registration.");
            if (entry.SnoozedFromRegistrationId is null && entry.SnoozedFromKind is not null)
                throw new InvalidDataException("A snooze source kind requires a source registration.");
            if (entry.Kind == AlarmRegistrationKind.Snooze &&
                entry.State is AlarmRegistrationState.DeliveryObserved or AlarmRegistrationState.Missed or
                    AlarmRegistrationState.PlaybackReserved or AlarmRegistrationState.PlaybackStarted or
                    AlarmRegistrationState.PlaybackCompleted or AlarmRegistrationState.Interrupted or AlarmRegistrationState.ClockUncertain &&
                entry.SnoozedFromRegistrationId is null)
                throw new InvalidDataException("A snooze delivery requires a source registration.");
        }
        foreach (var entry in registrations.Where(item => item.SnoozedFromRegistrationId is not null))
        {
            var source = new AlarmRegistration(entry.AlarmId, entry.SnoozedFromRegistrationId!.Value,
                entry.SnoozedFromKind ?? AlarmRegistrationKind.Regular);
            if (!registrations.Any(item => item.Registration == source &&
                item.State == AlarmRegistrationState.PlaybackCompleted &&
                item.PlaybackStartedAt < entry.TriggerAt))
                throw new InvalidDataException("A snooze requires its completed source registration.");
        }
    }

    private sealed class RegistrationDocument
    {
        [JsonRequired]
        public int Version { get; set; }
        [JsonRequired]
        public IReadOnlyList<AlarmRegistrationEntry> Registrations { get; set; } = null!;
    }
}
