using Android.Content;
using Android.OS;
using Android.Provider;
using BedsideAlarmClock.Alarms;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AndroidAlarmClockAnchor
{
    private const string Name = "alarm-clock-anchor";

    public static RecoveryClockStatus ReadStatus(Context context)
    {
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        var savedWall = state.GetLong("wall", 0);
        var savedElapsed = state.GetLong("elapsed", -1);
        var savedBoot = state.GetInt("boot", -1);
        var boot = GetBootCount(context);
        var elapsed = SystemClock.ElapsedRealtime();
        if (savedWall == 0 || savedElapsed < 0 || savedBoot < 0 || boot < 0 ||
            savedBoot != boot || elapsed < savedElapsed)
            return RecoveryClockStatus.Uncertain;
        var wallDelta = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedWall;
        return Math.Abs(wallDelta - (elapsed - savedElapsed)) <= 2000
            ? RecoveryClockStatus.Trusted : RecoveryClockStatus.Uncertain;
    }

    public static bool HasScheduled(Context context)
    {
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        return state.GetBoolean("scheduled", false);
    }

    public static void SetAnchor(Context context)
    {
        var boot = GetBootCount(context);
        if (boot < 0) throw new InvalidOperationException("Android boot count is unavailable.");
        using var state = context.GetSharedPreferences(Name, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Alarm clock state is unavailable.");
        using var editor = state.Edit() ?? throw new InvalidOperationException("Alarm clock state cannot be saved.");
        editor.PutLong("wall", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        editor.PutLong("elapsed", SystemClock.ElapsedRealtime());
        editor.PutInt("boot", boot);
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
