using BedsideAlarmClock.Display;
using BedsideAlarmClock.Alarms;
using BedsideAlarmClock.Platforms.Android;
using System.Globalization;

namespace BedsideAlarmClock;

public partial class MainPage : ContentPage
{
    private readonly IDispatcherTimer _clockTimer;
    private readonly IDispatcherTimer _pixelShiftTimer;
    private readonly IDispatcherTimer _skipTimer;
    private readonly IClockBrightness _brightness;
    private readonly AlarmCoordinator _alarms;
    private bool _skipBusy;
    private bool _clockActive;
    private int _nextAlarmVersion;
    private int _touchVersion;
    private DateTimeOffset? _nextAlarm;
    private DateTimeOffset _nextAlarmRefreshAt;
    private int _pixelShiftPosition;
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
        _pixelShiftTimer.IsRepeating = true;
        _pixelShiftTimer.Tick += OnPixelShiftTimerTick;

        _skipTimer = Dispatcher.CreateTimer();
        _skipTimer.Interval = TimeSpan.FromSeconds(3);
        _skipTimer.IsRepeating = false;
        _skipTimer.Tick += (_, _) => HideSkipButton();
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
        _clockActive = true;
        AlarmPlaybackService.AlarmStateChanged -= OnAlarmStateChanged;
        AlarmPlaybackService.AlarmStateChanged += OnAlarmStateChanged;
        var font = Preferences.Default.Get(ClockSettings.ClockFontKey, ClockSettings.ClockFonts[0]);
        TimeLabel.FontFamily = ClockSettings.FontFamily(font);
        DateLabel.IsVisible = Preferences.Default.Get(ClockSettings.ShowDateKey, true);
        RefreshClock();
        RenderAlarmControls();
        _ = RefreshNextAlarmAsync();
        UpdateClockFontSize();
        _clockTimer.Start();
        _brightness.Activate();
        _pixelShiftTimer.Start();
    }

    public void PauseClock()
    {
        _clockActive = false;
        _nextAlarmVersion++;
        AlarmPlaybackService.AlarmStateChanged -= OnAlarmStateChanged;
        HideSkipButton();
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
        if (AlarmPlaybackService.IsAlarmProcessing) HideSkipButton();
        if (DateTimeOffset.UtcNow >= _nextAlarmRefreshAt ||
            _nextAlarm is { } next && next <= DateTimeOffset.UtcNow)
        {
            _nextAlarmRefreshAt = DateTimeOffset.UtcNow.AddSeconds(15);
            _ = RefreshNextAlarmAsync();
        }
    }

    internal bool IsSkipButtonVisible => SkipButton.IsVisible;

    internal async void OnClockScreenTouched()
    {
        if (_skipBusy || AlarmPlaybackService.IsAlarmProcessing) return;
        var touch = ++_touchVersion;
        if (!await RefreshNextAlarmAsync() || touch != _touchVersion ||
            !_clockActive || _skipBusy || AlarmPlaybackService.IsAlarmProcessing || _nextAlarm is null)
            return;
        SkipButton.IsVisible = true;
        _skipTimer.Stop();
        _skipTimer.Start();
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
            dismiss.Clicked += (_, _) => SendAlarmAction(registration, AlarmPlaybackService.DismissAction);
            buttons.Add(dismiss);
            if (snoozeMinutes is { } minutes)
            {
                var snooze = new Button
                {
                    Text = $"SNOOZE {minutes} MIN", FontSize = 18, MinimumHeightRequest = 56,
                    MinimumWidthRequest = 140, BackgroundColor = Color.FromArgb("#555555"), TextColor = Colors.White
                };
                snooze.Clicked += (_, _) => SendAlarmAction(registration, AlarmPlaybackService.SnoozeAction);
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
        _touchVersion++;
        _skipTimer.Stop();
        SkipButton.IsVisible = false;
    }

    private async void OnSkipClicked(object? sender, EventArgs e)
    {
        HideSkipButton();
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
        catch
        {
            shell.Navigated -= OnNavigated;
            throw;
        }
    }

}
