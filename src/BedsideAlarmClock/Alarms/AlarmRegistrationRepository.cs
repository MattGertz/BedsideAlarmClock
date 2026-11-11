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
    PlaybackCompleted
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
        var desired = new HashSet<(Guid, AlarmRegistrationKind)>();
        foreach (var entry in registrations)
        {
            if (entry is null || entry.AlarmId == Guid.Empty || entry.RegistrationId == Guid.Empty ||
                !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.State) || entry.TriggerAt == default)
                throw new InvalidDataException("An alarm registration has invalid or missing data.");
            if (entry.ObservedAt is { } observed && observed < entry.TriggerAt)
                throw new InvalidDataException("An alarm registration has inconsistent delivery evidence.");
            if (entry.State == AlarmRegistrationState.DeliveryObserved)
            {
                if (entry.Kind != AlarmRegistrationKind.Regular || entry.ObservedAt is null ||
                    entry.PlaybackReservedAt is not null || entry.PlaybackStartedAt is not null || entry.MissedAt is not null ||
                    entry.MissedReason is not null || entry.CancellationPending)
                    throw new InvalidDataException("An observed delivery has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.PlaybackReserved)
            {
                if (entry.Kind != AlarmRegistrationKind.Regular || entry.ObservedAt is not { } observedAt ||
                    entry.PlaybackReservedAt is not { } reservedAt || reservedAt < observedAt ||
                    AlarmDeliveryTiming.IsMissed(entry.TriggerAt, reservedAt, false) ||
                    entry.PlaybackStartedAt is not null || entry.MissedAt is not null ||
                    entry.MissedReason is not null || entry.CancellationPending)
                    throw new InvalidDataException("A playback reservation has inconsistent state.");
            }
            else if (entry.State is AlarmRegistrationState.PlaybackStarted or AlarmRegistrationState.PlaybackCompleted)
            {
                if (entry.Kind != AlarmRegistrationKind.Regular || entry.ObservedAt is not { } observedAt ||
                    entry.PlaybackReservedAt is not { } reservedAt || reservedAt < observedAt ||
                    entry.PlaybackStartedAt is not { } startedAt || startedAt < reservedAt ||
                    AlarmDeliveryTiming.IsMissed(entry.TriggerAt, startedAt, false) ||
                    entry.MissedAt is not null || entry.MissedReason is not null ||
                    (entry.State == AlarmRegistrationState.PlaybackStarted && entry.CancellationPending))
                    throw new InvalidDataException("A playback start has inconsistent state.");
            }
            else if (entry.State == AlarmRegistrationState.Missed)
            {
                if (entry.Kind != AlarmRegistrationKind.Regular || entry.MissedAt is not { } missedAt ||
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
            if (entry.State == AlarmRegistrationState.Desired && !desired.Add((entry.AlarmId, entry.Kind)))
                throw new InvalidDataException("Only one desired registration per alarm and kind is allowed.");
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
