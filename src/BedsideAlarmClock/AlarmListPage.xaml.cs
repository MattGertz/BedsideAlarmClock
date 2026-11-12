using BedsideAlarmClock.Alarms;
using System.Text.Json;

namespace BedsideAlarmClock;

public partial class AlarmListPage : ContentPage
{
    private readonly AlarmCoordinator _coordinator;
    private readonly IDispatcherTimer _statusTimer;
    private IReadOnlyList<AlarmDefinition> _alarms = [];
    private (DateOnly Date, string Status) _displayState;
    private bool _busy;
    private bool _loaded;
#if ALARM_DELIVERY_EXPERIMENT
    private IReadOnlyList<AlarmRegistrationEntry> _reports = [];
#endif

    public AlarmListPage()
    {
        InitializeComponent();
        _coordinator = IPlatformApplication.Current!.Services.GetRequiredService<AlarmCoordinator>();
        _statusTimer = Dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(15);
        _statusTimer.IsRepeating = true;
        _statusTimer.Tick += (_, _) =>
        {
            if (_loaded && !_busy && DisplayState() != _displayState) Render();
        };
        AddButton.IsEnabled = false;
#if ALARM_DELIVERY_EXPERIMENT
        DeliveryStatus.Text = "Experimental alarm delivery: keep a backup alarm.";
        ActiveControlsButton.IsVisible = true;
#endif
    }

    private async void OnActiveControlsClicked(object? sender, EventArgs e)
    {
#if ALARM_DELIVERY_EXPERIMENT
        if (Platforms.Android.AlarmPlaybackService.Current?.GetActiveAlarms().Count > 0)
        {
            try
            {
                var context = global::Android.App.Application.Context;
                using var intent = new global::Android.Content.Intent(context, typeof(Platforms.Android.AlarmRingingActivity));
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                context.StartActivity(intent);
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Unable to open alarm controls", ex.Message, "OK");
            }
            return;
        }
#endif
        await DisplayAlertAsync("Active alarms", "There are no active alarms.", "OK");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
        _statusTimer.Start();
    }

    protected override void OnDisappearing()
    {
        _statusTimer.Stop();
        base.OnDisappearing();
    }

    private async Task LoadAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            _alarms = await _coordinator.LoadAsync();
#if ALARM_DELIVERY_EXPERIMENT
            _reports = await _coordinator.LoadRecoveryReportsAsync(
                IPlatformApplication.Current!.Services.GetRequiredService<AlarmRegistrationRepository>());
#endif
            _loaded = true;
            Render();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _loaded = false;
            AlarmRows.Children.Clear();
            AlarmRows.Children.Add(new Label
            {
                Text = "Alarms could not be loaded. Return and reopen this screen to retry.",
                TextColor = Color.FromArgb("#FFAAAA"),
                FontSize = 16
            });
            await DisplayAlertAsync("Unable to load alarms", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AddButton.IsEnabled = !busy && _loaded;
        AlarmRows.IsEnabled = !busy && _loaded;
    }

    private void Render()
    {
        _displayState = DisplayState();
        AlarmRows.Children.Clear();
#if ALARM_DELIVERY_EXPERIMENT
        foreach (var report in _reports.Where(item =>
            !Preferences.Default.Get($"alarm.report.{item.RegistrationId:N}", false)))
        {
            var name = _alarms.SingleOrDefault(alarm => alarm.Id == report.AlarmId)?.Label;
            var state = report.State switch
            {
                AlarmRegistrationState.Missed => "Missed",
                AlarmRegistrationState.Interrupted => "Interrupted",
                _ => "Time uncertain"
            };
            var row = new VerticalStackLayout { Spacing = 2, Padding = 8 };
            row.Children.Add(new Label
            {
                Text = $"{state}: {report.TriggerAt.ToLocalTime():g}" +
                    (string.IsNullOrWhiteSpace(name) ? "" : $" · {name}"),
                TextColor = Color.FromArgb("#FFD180"), FontSize = 15
            });
            var acknowledge = new Button { Text = "Acknowledge", HorizontalOptions = LayoutOptions.Start };
            acknowledge.Clicked += (_, _) =>
            {
                Preferences.Default.Set($"alarm.report.{report.RegistrationId:N}", true);
                Render();
            };
            row.Children.Add(acknowledge);
            AlarmRows.Children.Add(row);
        }
#endif
        if (_alarms.Count == 0)
        {
            AlarmRows.Children.Add(new Label
            {
                Text = "No alarms yet. Select Add to create one.",
                TextColor = Color.FromArgb("#BDBDBD"),
                FontSize = 16,
                Margin = new Thickness(0, 24, 0, 0)
            });
            return;
        }

        foreach (var alarm in _alarms.OrderBy(a => a.Hour).ThenBy(a => a.Minute).ThenBy(a => a.Id))
        {
            var time = AlarmPresentation.Time(alarm.Hour, alarm.Minute);
            var details = alarm.IsRepeating
                ? AlarmPresentation.DaysText(alarm.Days)
                : $"Once · {AlarmPresentation.DateText(alarm.OnceDate!.Value)}";
            if (!alarm.IsRepeating && !AlarmPresentation.HasFutureOnceOccurrence(alarm))
            {
                details += " (past)";
            }
            if (!string.IsNullOrWhiteSpace(alarm.Label)) details += $" · {alarm.Label}";

            var info = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
            info.Children.Add(new Label { Text = time, FontSize = 27, TextColor = Colors.White });
            info.Children.Add(new Label { Text = details, FontSize = 15, TextColor = Color.FromArgb("#BDBDBD") });
            if (alarm.IsRepeating && HasPendingSkip(alarm))
            {
                info.Children.Add(new Label
                {
                    Text = $"Skipped: {AlarmPresentation.DateText(alarm.SkipDate!.Value)}",
                    FontSize = 14,
                    TextColor = Color.FromArgb("#FFD180")
                });
            }
            var editGesture = new TapGestureRecognizer();
            editGesture.Tapped += async (_, _) => await OpenEditorAsync(alarm);
            info.GestureRecognizers.Add(editGesture);

            var toggle = new Switch { IsToggled = alarm.Enabled, VerticalOptions = LayoutOptions.Center };
            SemanticProperties.SetDescription(toggle, $"Enable alarm {time}, {details}");
            toggle.Toggled += async (_, e) => await SetEnabledAsync(alarm, e.Value);
            var header = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
            header.Add(info);
            header.Add(toggle, 1);

            var edit = new Button { Text = "Edit", BackgroundColor = Colors.Transparent, TextColor = Color.FromArgb("#D8D8D8") };
            SemanticProperties.SetDescription(edit, $"Edit alarm {time}, {details}");
            edit.Clicked += async (_, _) => await OpenEditorAsync(alarm);
            var actions = new HorizontalStackLayout();
            actions.Children.Add(edit);
            if (alarm.IsRepeating && alarm.Enabled)
            {
                var undoSkip = HasPendingSkip(alarm);
                var skip = new Button
                {
                    Text = undoSkip ? "Undo skip" : "Skip next",
                    BackgroundColor = Colors.Transparent,
                    TextColor = Color.FromArgb("#D8D8D8")
                };
                SemanticProperties.SetDescription(skip, $"{skip.Text} for alarm {time}, {details}");
                skip.Clicked += async (_, _) => await SetSkipAsync(alarm, undoSkip);
                actions.Children.Add(skip);
            }

            var contents = new VerticalStackLayout { Padding = new Thickness(16, 10), Spacing = 2 };
            contents.Children.Add(header);
            contents.Children.Add(actions);
            AlarmRows.Children.Add(new Border
            {
                BackgroundColor = Color.FromArgb("#191919"),
                Stroke = Color.FromArgb("#444444"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = contents
            });
        }
    }

    private (DateOnly Date, string Status) DisplayState() =>
        (DateOnly.FromDateTime(DateTime.Now), string.Join("|", _alarms.Select(alarm =>
            alarm.IsRepeating ? HasPendingSkip(alarm) : AlarmPresentation.HasFutureOnceOccurrence(alarm))));

    private static bool HasPendingSkip(AlarmDefinition alarm)
    {
        if (alarm.SkipDate is not { } date) return false;
        var nominal = AlarmPresentation.Copy(alarm);
        return AlarmOccurrenceService.SetSkipToNextOccurrence(nominal, DateTimeOffset.Now, TimeZoneInfo.Local) == date;
    }

    private async Task SetEnabledAsync(AlarmDefinition alarm, bool enabled)
    {
        if (_busy) return;
        var updated = AlarmPresentation.Copy(alarm);
        updated.Enabled = enabled;
        if (enabled && !updated.IsRepeating && !AlarmPresentation.HasFutureOnceOccurrence(updated))
        {
            updated.OnceDate = AlarmPresentation.NextOnceDate(updated.Hour, updated.Minute);
        }
        await ReplaceAsync(alarm, updated);
    }

    private async Task SetSkipAsync(AlarmDefinition alarm, bool undoSkip)
    {
        if (_busy) return;
        var updated = AlarmPresentation.Copy(alarm);
        if (undoSkip)
        {
            updated.SkipDate = null;
        }
        else
        {
            if (AlarmOccurrenceService.SetSkipToNextOccurrence(updated, DateTimeOffset.Now, TimeZoneInfo.Local) is null)
            {
                await DisplayAlertAsync("Unable to skip", "No future occurrence could be calculated.", "OK");
                return;
            }
        }
        await ReplaceAsync(alarm, updated);
    }

    private async Task ReplaceAsync(AlarmDefinition original, AlarmDefinition updated)
    {
        SetBusy(true);
        var reload = false;
        try
        {
            _alarms = await _coordinator.ReplaceAsync(original, updated);
        }
        catch (AlarmConflictException ex)
        {
            reload = true;
            await DisplayAlertAsync("Alarm changed", ex.Message, "OK");
        }
        catch (AlarmScheduleException ex)
        {
            reload = true;
            await DisplayAlertAsync("Alarm saved; scheduling uncertain", ex.Message, "OK");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await DisplayAlertAsync("Unable to save alarm", ex.Message, "OK");
        }
        finally
        {
            Render();
            SetBusy(false);
        }
        if (reload) await LoadAsync();
    }

    private async Task OpenEditorAsync(AlarmDefinition? alarm)
    {
        if (_busy || !_loaded) return;
        SetBusy(true);
        try
        {
            await Navigation.PushAsync(new AlarmEditPage(alarm));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnAddClicked(object? sender, EventArgs e) => await OpenEditorAsync(null);

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (!_busy) await Navigation.PopAsync();
    }

    protected override bool OnBackButtonPressed() => _busy || base.OnBackButtonPressed();
}
