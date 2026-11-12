using Android.Media;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AlarmSoundPicker
{
    public static IReadOnlyList<(string Title, string Uri)> GetSystemSounds()
    {
        using var manager = new RingtoneManager(global::Android.App.Application.Context);
        manager.SetType(RingtoneType.Alarm);
        using var cursor = manager.Cursor ?? throw new InvalidOperationException("System alarm sounds are unavailable.");
        var sounds = new List<(string, string)>();
        for (var index = 0; index < cursor.Count; index++)
        {
            if (!cursor.MoveToPosition(index))
                throw new InvalidOperationException("Could not read the system alarm sound list.");
            var title = cursor.GetString(cursor.GetColumnIndexOrThrow("title")) ?? "Alarm sound";
            using var uri = manager.GetRingtoneUri(index)
                ?? throw new InvalidOperationException("A system alarm sound has no URI.");
            sounds.Add(($"{index + 1}. {title}", uri.ToString() ?? throw new InvalidOperationException("Invalid sound URI.")));
        }
        return sounds;
    }

    public static async Task<string?> ImportFileAsync()
    {
        var types = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["audio/mpeg", "audio/wav", "audio/x-wav"]
        });
        var selected = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Choose a WAV or MP3 alarm sound",
            FileTypes = types
        });
        if (selected is null) return null;
        var extension = Path.GetExtension(selected.FileName).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
            throw new InvalidDataException("Choose a WAV or MP3 file.");

        var folder = Path.Combine(FileSystem.AppDataDirectory, "sounds");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}{extension}");
        try
        {
            await using var source = await selected.OpenReadAsync();
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(destination);
            await destination.FlushAsync();
            return new Uri(path).AbsoluteUri;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
