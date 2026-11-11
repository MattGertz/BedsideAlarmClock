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
