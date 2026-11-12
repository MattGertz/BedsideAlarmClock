using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Alarm.Tests;

public sealed class AlarmRepositoryTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"alarm-tests-{Guid.NewGuid():N}");
    private string PathName => Path.Combine(directory, "alarms.json");
    private static AlarmDefinition Once() => new()
    {
        Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = true,
        OnceDate = new DateOnly(2025, 1, 2), Label = "Wake up"
    };

    public AlarmRepositoryTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task MissingFileAndRoundTripAndReplacement()
    {
        var repository = new AlarmRepository(PathName);
        Assert.Empty(await repository.LoadAsync());
        var once = Once();
        var repeat = new AlarmDefinition
        {
            Id = Guid.NewGuid(), Hour = 7, Minute = 30, Enabled = false,
            IsRepeating = true, Days = AlarmDays.Monday | AlarmDays.Friday,
            SkipDate = new DateOnly(2025, 1, 6), Label = ""
        };
        await repository.SaveAsync(new[] { once, repeat });
        var loaded = await repository.LoadAsync();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(once.Id, loaded[0].Id);
        Assert.Equal(once.OnceDate, loaded[0].OnceDate);
        Assert.Equal(repeat.Days, loaded[1].Days);
        Assert.Equal(repeat.SkipDate, loaded[1].SkipDate);
        Assert.Equal(repeat.Enabled, loaded[1].Enabled);
        await repository.SaveAsync(new[] { repeat });
        Assert.Single(await repository.LoadAsync());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task InvalidSaveLeavesOriginalIntact()
    {
        var repository = new AlarmRepository(PathName);
        var valid = Once();
        await repository.SaveAsync(new[] { valid });
        var before = await File.ReadAllTextAsync(PathName);
        var invalid = Once();
        invalid.Hour = 24;
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { invalid }));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.SaveAsync(new[] { valid, valid }));
        Assert.Equal(before, await File.ReadAllTextAsync(PathName));
    }

    [Fact]
    public async Task SoundVolumeAndSnoozeRoundTripAndLegacyDefaults()
    {
        var repository = new AlarmRepository(PathName);
        var alarm = Once();
        alarm.SoundUri = "content://media/internal/audio/media/42";
        alarm.Volume = 0.35;
        alarm.SnoozeMinutes = null;
        await repository.SaveAsync(new[] { alarm });
        var saved = Assert.Single(await repository.LoadAsync());
        Assert.Equal(alarm.SoundUri, saved.SoundUri);
        Assert.Equal(0.35, saved.Volume);
        Assert.Null(saved.SnoozeMinutes);

        await File.WriteAllTextAsync(PathName,
            $$"""{"Version":1,"Alarms":[{"Id":"{{alarm.Id}}","Hour":7,"Minute":30,"Enabled":true,"IsRepeating":false,"Days":0,"Label":"","OnceDate":"2025-01-02","SkipDate":null}]}""");
        var legacy = Assert.Single(await repository.LoadAsync());
        Assert.Null(legacy.SoundUri);
        Assert.Equal(1, legacy.Volume);
        Assert.Equal(10, legacy.SnoozeMinutes);
    }

    [Fact]
    public void InvalidSoundVolumeAndSnoozeAreRejected()
    {
        var alarm = Once();
        alarm.SoundUri = "https://example.com/alarm.mp3";
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm.SoundUri = "file:///data/user/0/app/sound.wav";
        alarm.Volume = double.NaN;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm.Volume = 1;
        alarm.SnoozeMinutes = 0;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm.SnoozeMinutes = 1441;
        Assert.Throws<InvalidDataException>(alarm.Validate);
    }

    [Fact]
    public async Task StorageFailuresArePropagatedAndTemporaryFilesRemoved()
    {
        var target = Path.Combine(directory, "destination");
        Directory.CreateDirectory(target);
        var repository = new AlarmRepository(target);
        var error = await Record.ExceptionAsync(() => repository.SaveAsync(new[] { Once() }));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.GetFiles(directory));
        Assert.True(Directory.Exists(target));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Alarms\":[]}")]
    [InlineData("{\"Version\":1,\"Alarms\":null}")]
    [InlineData("{\"Version\":1,\"Alarms\":[{}]}")]
    [InlineData("{\"Version\":1,\"Alarms\":[null]}")]
    public async Task DamagedOrUnsupportedDocumentsNeverBecomeEmpty(string json)
    {
        await File.WriteAllTextAsync(PathName, json);
        await Assert.ThrowsAnyAsync<Exception>(() => new AlarmRepository(PathName).LoadAsync());
    }

    [Fact]
    public async Task InvalidStoredDataIsRejected()
    {
        var repository = new AlarmRepository(PathName);
        await repository.SaveAsync(new[] { Once() });
        var json = await File.ReadAllTextAsync(PathName);
        await File.WriteAllTextAsync(PathName, json.Replace("\"Hour\": 7", "\"Hour\": 25"));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
    }

    [Fact]
    public async Task DuplicateStoredIdsAreRejectedEvenForIdenticalTimes()
    {
        var repository = new AlarmRepository(PathName);
        var first = Once();
        var second = Once();
        await repository.SaveAsync(new[] { first, second });
        var json = await File.ReadAllTextAsync(PathName);
        await File.WriteAllTextAsync(PathName, json.Replace(second.Id.ToString(), first.Id.ToString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
    }

    [Fact]
    public void RejectsInconsistentDefinitions()
    {
        var alarm = Once();
        alarm.Id = Guid.Empty;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm = Once();
        alarm.Days = (AlarmDays)128;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm = Once();
        alarm.Label = null!;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm = Once();
        alarm.IsRepeating = true;
        alarm.Days = AlarmDays.Monday;
        Assert.Throws<InvalidDataException>(alarm.Validate);
        alarm.OnceDate = null;
        alarm.SkipDate = new DateOnly(2025, 1, 7);
        Assert.Throws<InvalidDataException>(alarm.Validate);
    }
}
