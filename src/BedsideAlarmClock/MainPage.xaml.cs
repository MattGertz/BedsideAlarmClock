using BedsideAlarmClock.Display;
using BedsideAlarmClock.Alarms;
using BedsideAlarmClock.Platforms.Android;
using System.Globalization;

namespace BedsideAlarmClock;

public partial class MainPage : ContentPage
{
    private readonly IDispatcherTimer _clockTimer;
    private readonly IDispatcherTimer _pixelShiftTimer;
    private readonly IDispatcherTimer _controlsTimer;
    private readonly IClockBrightness _brightness;
    private readonly AlarmCoordinator _alarms;
    private bool _skipBusy;
    private bool _navigationBusy;
    private bool _clockActive;
    private int _nextAlarmVersion;
    private DateTimeOffset? _nextAlarm;
    private DateTimeOffset _nextAlarmRefreshAt;
    private int _pixelShiftPosition = -1;
    private int _pixelShiftVersion;
    private string? _lastTimeText;
    private string? _lastDateText;
    private string? _lastTimeDescription;

    public MainPage()
    {
        InitializeComponent();

        _brightness = IPlatformApplication.Current!.Services.GetRequiredService<IClockBrightness>();
        _alarms = IPlatformApplication.Current!.Services.GetRequiredService<AlarmCoordinator>();
        _clockTimer = Dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnClockTimerTick;

        _pixelShiftTimer = Dispatcher.CreateTimer();
        _pixelShiftTimer.Interval = TimeSpan.FromMinutes(2);
        _pixelShiftTimer.IsRepeating = false;
        _pixelShiftTimer.Tick += OnPixelShiftTimerTick;

        _controlsTimer = Dispatcher.CreateTimer();
        _controlsTimer.Interval = TimeSpan.FromSeconds(3);
        _controlsTimer.IsRepeating = false;
        _controlsTimer.Tick += (_, _) => HideClockControls();
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
        var wasActive = _clockActive;
        _clockActive = true;
        AlarmPlaybackService.AlarmStateChanged -= OnAlarmStateChanged;
        AlarmPlaybackService.AlarmStateChanged += OnAlarmStateChanged;
        var font = Preferences.Default.Get(ClockSettings.ClockFontKey, ClockSettings.ClockFonts[0]);
        TimeLabel.FontFamily = ClockSettings.FontFamily(font);
        var color = Preferences.Default.Get(ClockSettings.ClockColorKey, ClockSettings.ClockColors[0]);
        var (timeColor, dateColor) = ClockSettings.ColorValues(color);
        TimeLabel.TextColor = Color.FromArgb(timeColor);
        DateLabel.TextColor = Color.FromArgb(dateColor);
        DateLabel.IsVisible = Preferences.Default.Get(ClockSettings.ShowDateKey, true);
        NextAlarmLabel.IsVisible = Preferences.Default.Get(ClockSettings.ShowNextAlarmKey, true);
        RefreshClock();
        RenderAlarmControls();
        _ = RefreshNextAlarmAsync();
        UpdateClockFontSize();
        _clockTimer.Start();
        _brightness.Activate();
        if (!wasActive)
            _pixelShiftTimer.Start();
    }

    public void PauseClock()
    {
        _clockActive = false;
        _nextAlarmVersion++;
        AlarmPlaybackService.AlarmStateChanged -= OnAlarmStateChanged;
        HideClockControls();
        _clockTimer.Stop();
        _pixelShiftTimer.Stop();
        _pixelShiftVersion++;
        SnapPixelShiftToPosition();
        _brightness.Deactivate();
    }

    private void SnapPixelShiftToPosition()
    {
        ClockContent.CancelAnimations();
        if (_pixelShiftPosition >= 0)
        {
            var (x, y) = DisplayMath.PixelOffset(_pixelShiftPosition);
            var density = DeviceDisplay.MainDisplayInfo.Density;
            ClockContent.TranslationX = x / density;
            ClockContent.TranslationY = y / density;
        }
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
        if (AlarmPlaybackService.IsAlarmProcessing) HideSkipButton();
        if (DateTimeOffset.UtcNow >= _nextAlarmRefreshAt ||
            _nextAlarm is { } next && next <= DateTimeOffset.UtcNow)
        {
            _nextAlarmRefreshAt = DateTimeOffset.UtcNow.AddSeconds(15);
            _ = RefreshNextAlarmAsync();
        }
    }

    internal bool AreClockControlsVisible => NavigationControls.IsVisible;

    internal void OnClockScreenTouchStarted()
    {
        _controlsTimer.Stop();
    }

    internal void OnClockScreenTouchCancelled()
    {
        if (_clockActive && NavigationControls.IsVisible && !_navigationBusy)
            _controlsTimer.Start();
    }

    internal void OnClockScreenTouched()
    {
        if (!_clockActive || _navigationBusy) return;
        NavigationControls.IsVisible = true;
        SkipButton.IsVisible = false;
        _controlsTimer.Stop();
        _controlsTimer.Start();
        _ = RefreshNextAlarmAsync();
    }

    private void OnAlarmStateChanged() => Dispatcher.Dispatch(() =>
    {
        if (!_clockActive) return;
        if (AlarmPlaybackService.IsAlarmProcessing) HideSkipButton();
        RenderAlarmControls();
        _ = RefreshNextAlarmAsync();
    });

    private async Task<bool> RefreshNextAlarmAsync()
    {
        var version = ++_nextAlarmVersion;
        try
        {
            var store = IPlatformApplication.Current!.Services.GetRequiredService<AlarmRegistrationRepository>();
            var next = await _alarms.GetNextAlarmAsync(TimeProvider.System, TimeZoneInfo.Local, store);
            if (!_clockActive || version != _nextAlarmVersion) return false;
            _nextAlarm = next;
            _nextAlarmRefreshAt = DateTimeOffset.UtcNow.AddSeconds(15);
            if (next is { } instant)
            {
                var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local);
                var format = Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true)
                    ? "dddd H:mm" : "dddd h:mm tt";
                NextAlarmLabel.Text = $"Next alarm: {local.ToString(format, CultureInfo.CurrentCulture)}";
                SkipButton.IsVisible = NavigationControls.IsVisible && !_skipBusy &&
                    !AlarmPlaybackService.IsAlarmProcessing;
            }
            else
            {
                NextAlarmLabel.Text = "No alarm set";
                HideSkipButton();
            }
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BedsideAlarm", $"Could not load next alarm: {ex}");
            if (_clockActive && version == _nextAlarmVersion)
            {
                _nextAlarm = null;
                _nextAlarmRefreshAt = DateTimeOffset.UtcNow.AddSeconds(15);
                NextAlarmLabel.Text = "Alarm status unavailable";
                HideSkipButton();
            }
            return false;
        }
    }

    private void RenderAlarmControls()
    {
        ActiveAlarmList.Clear();
        var alarms = AlarmPlaybackService.Current?.GetActiveAlarms() ?? [];
        foreach (var (registration, label, snoozeMinutes, actionError) in alarms)
        {
            var group = new VerticalStackLayout { Spacing = 4, HorizontalOptions = LayoutOptions.Center };
            group.Add(new Label
            {
                Text = label, FontSize = 16, TextColor = Colors.White,
                HorizontalTextAlignment = TextAlignment.Center
            });
            if (actionError is not null)
                group.Add(new Label { Text = actionError, TextColor = Colors.White, FontSize = 14 });
            var buttons = new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center };
            var dismiss = new Button
            {
                Text = "DISMISS", FontSize = 18, MinimumHeightRequest = 56,
                MinimumWidthRequest = 140, BackgroundColor = Color.FromArgb("#A62222"), TextColor = Colors.White
            };
            dismiss.Pressed += (_, _) => SendAlarmAction(registration, AlarmPlaybackService.DismissAction);
            buttons.Add(dismiss);
            if (snoozeMinutes is { } minutes)
            {
                var snooze = new Button
                {
                    Text = $"SNOOZE {minutes} MIN", FontSize = 18, MinimumHeightRequest = 56,
                    MinimumWidthRequest = 140, BackgroundColor = Color.FromArgb("#555555"), TextColor = Colors.White
                };
                snooze.Pressed += (_, _) => SendAlarmAction(registration, AlarmPlaybackService.SnoozeAction);
                buttons.Add(snooze);
            }
            group.Add(buttons);
            ActiveAlarmList.Add(group);
        }
        AlarmControls.IsVisible = alarms.Count > 0;
    }

    private void SendAlarmAction(AlarmRegistration registration, string action)
    {
        try
        {
            using var intent = AlarmPlaybackService.CreateActionIntent(
                global::Android.App.Application.Context, registration, action);
            global::Android.App.Application.Context.StartService(intent);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BedsideAlarm", $"Could not send alarm action: {ex}");
            _ = DisplayAlertAsync("Unable to control alarm", "Please retry or use the alarm notification.", "OK");
        }
    }

    private void HideSkipButton()
    {
        SkipButton.IsVisible = false;
    }

    private void HideClockControls()
    {
        _controlsTimer.Stop();
        NavigationControls.IsVisible = false;
        HideSkipButton();
    }

    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        HideClockControls();
        if (_skipBusy || AlarmPlaybackService.IsAlarmProcessing) return;
        _skipBusy = true;
        try
        {
            var services = IPlatformApplication.Current!.Services;
            var result = await _alarms.SkipNextAsync(TimeProvider.System, TimeZoneInfo.Local,
                () => AlarmPlaybackService.IsAlarmProcessing,
                services.GetRequiredService<AlarmRegistrationRepository>(),
                services.GetRequiredService<AndroidAlarmScheduler>());
            if (result == SkipNextResult.NoUpcomingAlarm)
                await DisplayAlertAsync("No upcoming alarm", "There is no upcoming alarm to skip.", "OK");
        }
        catch (AlarmScheduleException ex)
        {
            await DisplayAlertAsync("Alarm saved; scheduling uncertain", ex.Message, "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Unable to skip alarm", ex.Message, "OK");
        }
        finally
        {
            _skipBusy = false;
            await RefreshNextAlarmAsync();
        }
    }

    private void OnClockLayoutSizeChanged(object? sender, EventArgs e)
    {
        var margin = DisplayMath.PixelShiftDistance / DeviceDisplay.MainDisplayInfo.Density;
        ClockSafeArea.Padding = new Thickness(margin);
        SnapPixelShiftToPosition();
        if (_clockActive)
        {
            _pixelShiftVersion++;
            _pixelShiftTimer.Stop();
            _pixelShiftTimer.Start();
        }
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
        double? dateHeight = DateLabel.IsVisible ? DateLabel.Height : null;
        double? alarmHeight = NextAlarmLabel.IsVisible ? NextAlarmLabel.Height : null;
        if (DateLabel.IsVisible && DateLabel.Height <= 0 ||
            NextAlarmLabel.IsVisible && NextAlarmLabel.Height <= 0)
            return;
        ClockTextSizer.Fit(TimeLabel, ClockSafeArea.Width - padding.Left - padding.Right,
            DisplayMath.AvailableTimeHeight(ClockSafeArea.Height - padding.Top - padding.Bottom,
                ClockContent.Spacing, dateHeight, alarmHeight));
    }

    private async void OnPixelShiftTimerTick(object? sender, EventArgs e)
    {
        if (!_clockActive) return;
        var version = _pixelShiftVersion;
        _pixelShiftPosition = (_pixelShiftPosition + 1) % DisplayMath.PixelShiftPositionCount;
        var (x, y) = DisplayMath.PixelOffset(_pixelShiftPosition);
        var density = DeviceDisplay.MainDisplayInfo.Density;
        var cancelled = await ClockContent.TranslateToAsync(x / density, y / density, 750, Easing.SinInOut);
        if (!cancelled && _clockActive && version == _pixelShiftVersion)
            _pixelShiftTimer.Start();
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        if (_navigationBusy) return;
        _navigationBusy = true;
        HideClockControls();
        try
        {
            await Shell.Current.GoToAsync(nameof(SettingsPage));
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("BedsideAlarm", $"Could not open settings: {ex}");
            await DisplayAlertAsync("Unable to open settings", ex.Message, "OK");
        }
        finally
        {
            _navigationBusy = false;
        }
    }

    private async void OnAlarmsClicked(object? sender, EventArgs e)
    {
        if (_navigationBusy) return;
        _navigationBusy = true;
        HideClockControls();
        var shell = Shell.Current;
        var alarmPage = new AlarmListPage();
        void OnNavigated(object? sender, ShellNavigatedEventArgs args)
        {
            if (Navigation.NavigationStack.Contains(alarmPage)) return;
            shell.Navigated -= OnNavigated;
            if (shell.CurrentPage == this)
                ResumeClock();
        }

        shell.Navigated += OnNavigated;
        try
        {
            await Navigation.PushAsync(alarmPage);
        }
        catch (Exception ex)
        {
            shell.Navigated -= OnNavigated;
            global::Android.Util.Log.Error("BedsideAlarm", $"Could not open alarms: {ex}");
            await DisplayAlertAsync("Unable to open alarms", ex.Message, "OK");
        }
        finally
        {
            _navigationBusy = false;
        }
    }

}
