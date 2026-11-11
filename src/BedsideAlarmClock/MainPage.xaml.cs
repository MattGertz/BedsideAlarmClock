namespace BedsideAlarmClock;

public partial class MainPage : ContentPage
{
    private readonly IDispatcherTimer _clockTimer;
    private string? _lastTimeText;
    private string? _lastDateText;
    private string? _lastTimeDescription;

    public MainPage()
    {
        InitializeComponent();

        _clockTimer = Dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnClockTimerTick;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Refresh immediately in case the app was backgrounded across a minute change.
        RefreshClock();
        _clockTimer.Start();
    }

    protected override void OnDisappearing()
    {
        _clockTimer.Stop();
        base.OnDisappearing();
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
        var timeDescription = now.ToString("dddd, MMMM d, " + timeFormat);

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

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

}
