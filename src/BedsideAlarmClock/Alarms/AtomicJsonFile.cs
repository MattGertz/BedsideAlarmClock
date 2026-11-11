using System.Text.Json;

namespace BedsideAlarmClock.Alarms;

internal static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(string path, T document, JsonSerializerOptions options)
    {
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
                await JsonSerializer.SerializeAsync(stream, document, options);
                await stream.FlushAsync();
                await Task.Run(() => stream.Flush(flushToDisk: true));
            }
            File.Move(temporaryPath, path, overwrite: true);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
                File.Delete(temporaryPath);
        }
    }
}
