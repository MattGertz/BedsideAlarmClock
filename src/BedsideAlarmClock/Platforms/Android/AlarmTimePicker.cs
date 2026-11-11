using Android.App;

namespace BedsideAlarmClock.Platforms.Android;

internal static class AlarmTimePicker
{
    public static async Task<TimeOnly?> ShowAsync(int hour, int minute, bool use24Hour)
    {
        var activity = Platform.CurrentActivity
            ?? throw new InvalidOperationException("The alarm time picker needs an active window.");
        var result = new TaskCompletionSource<TimeOnly?>(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeOnly? selected = null;
        using var dialog = new TimePickerDialog(activity,
            (_, args) => selected = new TimeOnly(args.HourOfDay, args.Minute),
            hour, minute, use24Hour);
        // Android finishes with the dialog on dismissal, after the time-set callback.
        dialog.DismissEvent += (_, _) => result.TrySetResult(selected);
        dialog.Show();
        return await result.Task;
    }
}
