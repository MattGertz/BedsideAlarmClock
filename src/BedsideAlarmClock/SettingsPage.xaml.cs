namespace BedsideAlarmClock;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        Use24HourTimeSwitch.IsToggled = Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true);
        ShowSecondsSwitch.IsToggled = Preferences.Default.Get(ClockSettings.ShowSecondsKey, true);
        Use24HourTimeSwitch.Toggled += On24HourTimeToggled;
        ShowSecondsSwitch.Toggled += OnShowSecondsToggled;
    }

    private void On24HourTimeToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(ClockSettings.Use24HourTimeKey, e.Value);
    }

    private void OnShowSecondsToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(ClockSettings.ShowSecondsKey, e.Value);
    }
}
