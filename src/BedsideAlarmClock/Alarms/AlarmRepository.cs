using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedsideAlarmClock.Alarms;

public sealed class AlarmRepository
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string path;

    public AlarmRepository(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public Task<IReadOnlyList<AlarmDefinition>> LoadAsync() => LoadAsync(allowMissing: true);

    internal Task<IReadOnlyList<AlarmDefinition>> LoadExistingAsync() => LoadAsync(allowMissing: false);

    private async Task<IReadOnlyList<AlarmDefinition>> LoadAsync(bool allowMissing)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return Array.Empty<AlarmDefinition>();
        }

        await using (stream)
        {
            var document = await JsonSerializer.DeserializeAsync<AlarmDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The alarm document is null.");
            if (document.Version != CurrentVersion)
                throw new InvalidDataException($"Unsupported alarm document version: {document.Version}.");
            ValidateAlarms(document.Alarms);
            return document.Alarms;
        }
    }

    public async Task SaveAsync(IReadOnlyList<AlarmDefinition> alarms)
    {
        ArgumentNullException.ThrowIfNull(alarms);
        ValidateAlarms(alarms);
        await AtomicJsonFile.WriteAsync(path, new AlarmDocument
        {
            Version = CurrentVersion,
            Alarms = alarms
        }, JsonOptions);
    }

    private static void ValidateAlarms(IReadOnlyList<AlarmDefinition>? alarms)
    {
        if (alarms is null)
            throw new InvalidDataException("The alarm list cannot be null.");
        var ids = new HashSet<Guid>();
        foreach (var alarm in alarms)
        {
            if (alarm is null)
                throw new InvalidDataException("An alarm cannot be null.");
            alarm.Validate();
            if (!ids.Add(alarm.Id))
                throw new InvalidDataException("Alarm IDs must be unique.");
        }
    }

    private sealed class AlarmDocument
    {
        [JsonRequired]
        public int Version { get; set; }
        [JsonRequired]
        public IReadOnlyList<AlarmDefinition> Alarms { get; set; } = null!;
    }
}
