namespace BedsideAlarmClock;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        Use24HourTimeSwitch.IsToggled = Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true);
        ShowSecondsSwitch.IsToggled = Preferences.Default.Get(ClockSettings.ShowSecondsKey, true);
        ShowDateSwitch.IsToggled = Preferences.Default.Get(ClockSettings.ShowDateKey, true);
        ClockFontPicker.ItemsSource = ClockSettings.ClockFonts;
        var savedFont = Preferences.Default.Get(ClockSettings.ClockFontKey, ClockSettings.ClockFonts[0]);
        var fontIndex = Array.IndexOf(ClockSettings.ClockFonts, savedFont);
        ClockFontPicker.SelectedIndex = fontIndex >= 0 ? fontIndex : 0;
        Use24HourTimeSwitch.Toggled += On24HourTimeToggled;
        ShowSecondsSwitch.Toggled += OnShowSecondsToggled;
        ShowDateSwitch.Toggled += OnShowDateToggled;
        ClockFontPicker.SelectedIndexChanged += OnClockFontChanged;
#if ALARM_DELIVERY_EXPERIMENT
        AlarmPermissionSection.IsVisible = true;
#endif
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ALARM_DELIVERY_EXPERIMENT
        RefreshAlarmPermissions();
#endif
    }

#if ALARM_DELIVERY_EXPERIMENT
    internal void RefreshAlarmPermissions()
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var scheduler = IPlatformApplication.Current!.Services.GetRequiredService<Platforms.Android.AndroidAlarmScheduler>();
            AlarmPermissionStatus.Text = $"Exact alarms: {(scheduler.CanScheduleExactAlarms() ? "allowed" : "not allowed")}\n" +
                $"Alarm notifications: {(Platforms.Android.AlarmPermissions.NotificationsEnabled(context) ? "allowed" : "blocked")}\n" +
                $"Full-screen alarms: {(Platforms.Android.AlarmPermissions.FullScreenEnabled(context) ? "allowed" : "not allowed; notification fallback")}";
        }
        catch (Exception ex)
        {
            AlarmPermissionStatus.Text = $"Unable to check alarm permissions: {ex.Message}";
        }
    }
#endif

    private async void OnNotificationSettingsClicked(object? sender, EventArgs e) =>
        await OpenAlarmSettingsAsync(() => Platforms.Android.AlarmPermissions.OpenNotifications(global::Android.App.Application.Context));

    private async void OnExactAlarmSettingsClicked(object? sender, EventArgs e) =>
        await OpenAlarmSettingsAsync(() => Platforms.Android.AlarmPermissions.OpenExactAlarms(global::Android.App.Application.Context));

    private async void OnFullScreenSettingsClicked(object? sender, EventArgs e) =>
        await OpenAlarmSettingsAsync(() => Platforms.Android.AlarmPermissions.OpenFullScreen(global::Android.App.Application.Context));

    private async Task OpenAlarmSettingsAsync(Action open)
    {
        try { open(); }
        catch (Exception ex) { await DisplayAlertAsync("Unable to open Android settings", ex.Message, "OK"); }
    }

    private void On24HourTimeToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(ClockSettings.Use24HourTimeKey, e.Value);
    }

    private void OnShowSecondsToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(ClockSettings.ShowSecondsKey, e.Value);
    }

    private void OnShowDateToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(ClockSettings.ShowDateKey, e.Value);
    }

    private void OnClockFontChanged(object? sender, EventArgs e)
    {
        if (ClockFontPicker.SelectedIndex >= 0)
        {
            Preferences.Default.Set(ClockSettings.ClockFontKey, ClockSettings.ClockFonts[ClockFontPicker.SelectedIndex]);
        }
    }
}
