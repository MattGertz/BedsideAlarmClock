using BedsideAlarmClock.Display;
using BedsideAlarmClock.Platforms.Android;

namespace BedsideAlarmClock;

public partial class MainPage : ContentPage
{
    private readonly IDispatcherTimer _clockTimer;
    private readonly IDispatcherTimer _pixelShiftTimer;
    private readonly IClockBrightness _brightness;
    private int _pixelShiftPosition;
    private string? _lastTimeText;
    private string? _lastDateText;
    private string? _lastTimeDescription;

    public MainPage()
    {
        InitializeComponent();

        _brightness = IPlatformApplication.Current!.Services.GetRequiredService<IClockBrightness>();
        _clockTimer = Dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnClockTimerTick;

        _pixelShiftTimer = Dispatcher.CreateTimer();
        _pixelShiftTimer.Interval = TimeSpan.FromMinutes(2);
        _pixelShiftTimer.IsRepeating = true;
        _pixelShiftTimer.Tick += OnPixelShiftTimerTick;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ResumeClock();
    }

    protected override void OnDisappearing()
    {
        PauseClock();
        base.OnDisappearing();
    }

    public void ResumeClock()
    {
        var font = Preferences.Default.Get(ClockSettings.ClockFontKey, ClockSettings.ClockFonts[0]);
        TimeLabel.FontFamily = ClockSettings.FontFamily(font);
        DateLabel.IsVisible = Preferences.Default.Get(ClockSettings.ShowDateKey, true);
        RefreshClock();
        UpdateClockFontSize();
        _clockTimer.Start();
        _brightness.Activate();
        _pixelShiftTimer.Start();
    }

    public void PauseClock()
    {
        _clockTimer.Stop();
        _pixelShiftTimer.Stop();
        _brightness.Deactivate();
    }

    public void RefreshClock()
    {
        var now = DateTime.Now;
        var showSeconds = Preferences.Default.Get(ClockSettings.ShowSecondsKey, true);
        var is24Hour = Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true);
        var timeFormat = (is24Hour, showSeconds) switch
        {
            (true, true) => "H:mm:ss",
            (true, false) => "H:mm",
            (false, true) => "h:mm:ss tt",
            _ => "h:mm tt"
        };

        var timeText = now.ToString(timeFormat);
        var dateText = now.ToString("dddd, MMMM d");
        var timeDescription = DateLabel.IsVisible
            ? now.ToString("dddd, MMMM d, " + timeFormat)
            : timeText;

        if (_lastTimeText != timeText)
        {
            TimeLabel.Text = timeText;
            _lastTimeText = timeText;
        }

        if (_lastDateText != dateText)
        {
            DateLabel.Text = dateText;
            _lastDateText = dateText;
        }

        if (_lastTimeDescription != timeDescription)
        {
            SemanticProperties.SetDescription(TimeLabel, timeDescription);
            _lastTimeDescription = timeDescription;
        }
    }

    private void OnClockTimerTick(object? sender, EventArgs e)
    {
        RefreshClock();
    }

    private void OnClockLayoutSizeChanged(object? sender, EventArgs e)
    {
        var margin = DisplayMath.PixelShiftDistance / DeviceDisplay.MainDisplayInfo.Density;
        ClockSafeArea.Padding = new Thickness(margin);
        UpdateClockFontSize();
    }

    private void OnClockSafeAreaSizeChanged(object? sender, EventArgs e)
    {
        UpdateClockFontSize();
    }

    private void OnTimeLabelHandlerChanged(object? sender, EventArgs e)
    {
        UpdateClockFontSize();
    }

    private void UpdateClockFontSize()
    {
        var padding = ClockSafeArea.Padding;
        var dateHeight = DateLabel.IsVisible ? DateLabel.Height + ClockContent.Spacing : 0;
        if (DateLabel.IsVisible && DateLabel.Height <= 0)
            return;
        ClockTextSizer.Fit(TimeLabel, ClockSafeArea.Width - padding.Left - padding.Right,
            ClockSafeArea.Height - padding.Top - padding.Bottom - dateHeight);
    }

    private void OnPixelShiftTimerTick(object? sender, EventArgs e)
    {
        _pixelShiftPosition = (_pixelShiftPosition + 1) % 9;
        var (x, y) = DisplayMath.PixelOffset(_pixelShiftPosition);
        var density = DeviceDisplay.MainDisplayInfo.Density;
        ClockContent.TranslationX = x / density;
        ClockContent.TranslationY = y / density;
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    private async void OnAlarmsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new AlarmListPage());
    }

}
