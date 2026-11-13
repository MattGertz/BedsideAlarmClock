using Android.Content;
using Android.OS;
using Android.Provider;
using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AndroidAlarmClockAnchor
{
    private const string Name = "alarm-clock-anchor";

    internal readonly record struct Sample(long Wall, long Elapsed, int Boot);

    public static Sample Capture(Context context) =>
        new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), SystemClock.ElapsedRealtime(), GetBootCount(context));

    public static RecoveryClockStatus ReadStatus(Context context) => ReadStatus(context, Capture(context));

    public static RecoveryClockStatus ReadStatus(Context context, Sample sample)
    {
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        var savedWall = state.GetLong("wall", 0);
        var savedElapsed = state.GetLong("elapsed", -1);
        var savedBoot = state.GetInt("boot", -1);
        if (savedWall == 0 || savedElapsed < 0 || savedBoot < 0 || sample.Boot < 0 ||
            savedBoot != sample.Boot || sample.Elapsed < savedElapsed)
            return RecoveryClockStatus.Uncertain;
        var wallDelta = sample.Wall - savedWall;
        return Math.Abs(wallDelta - (sample.Elapsed - savedElapsed)) <= 2000
            ? RecoveryClockStatus.Trusted : RecoveryClockStatus.Uncertain;
    }

    public static bool HasScheduled(Context context)
    {
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        return state.GetBoolean("scheduled", false);
    }

    public static void SetAnchor(Context context, Sample sample)
    {
        if (sample.Boot < 0) throw new InvalidOperationException("Android boot count is unavailable.");
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        using var editor = state.Edit() ?? throw new InvalidOperationException("Alarm clock state cannot be saved.");
        editor.PutLong("wall", sample.Wall);
        editor.PutLong("elapsed", sample.Elapsed);
        editor.PutInt("boot", sample.Boot);
        if (!editor.Commit()) throw new IOException("Alarm clock state could not be saved.");
    }

    public static void MarkScheduled(Context context)
    {
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        using var editor = state.Edit() ?? throw new InvalidOperationException("Alarm clock state cannot be saved.");
        editor.PutBoolean("scheduled", true);
        if (!editor.Commit()) throw new IOException("Alarm scheduling state could not be saved.");
    }

    private static int GetBootCount(Context context) => OperatingSystem.IsAndroidVersionAtLeast(24)
        ? Settings.Global.GetInt(context.ContentResolver, Settings.Global.BootCount, -1)
        : 0;
}
