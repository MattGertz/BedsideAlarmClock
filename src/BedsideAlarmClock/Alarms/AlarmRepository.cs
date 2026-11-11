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

    public async Task<IReadOnlyList<AlarmDefinition>> LoadAsync()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
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
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                temporaryCreated = true;
                await JsonSerializer.SerializeAsync(stream, new AlarmDocument
                {
                    Version = CurrentVersion,
                    Alarms = alarms
                }, JsonOptions);
                await stream.FlushAsync();
                await Task.Run(() => stream.Flush(flushToDisk: true));
            }
            File.Move(temporaryPath, path, overwrite: true);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
            {
                File.Delete(temporaryPath);
            }
        }
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
