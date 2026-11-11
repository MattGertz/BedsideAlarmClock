using System.Globalization;
using System.Text.Json;
using BedsideAlarmClock.Alarms;
using BedsideAlarmClock.Platforms.Android;

namespace BedsideAlarmClock;

public partial class AlarmEditPage : ContentPage
{
    private readonly AlarmCoordinator _coordinator;
    private readonly AlarmDefinition _original;
    private readonly bool _isNew;
    private readonly Button[] _dayButtons = new Button[7];
    private int _hour;
    private int _minute;
    private bool _isRepeating;
    private AlarmDays _days;
    private bool _busy;

    public AlarmEditPage(AlarmDefinition? alarm)
    {
        InitializeComponent();
        _coordinator = IPlatformApplication.Current!.Services.GetRequiredService<AlarmCoordinator>();
        _isNew = alarm is null;
        var initialTime = DateTime.Now.AddMinutes(1);
        _original = alarm is null
            ? new AlarmDefinition
            {
                Id = Guid.NewGuid(), Hour = initialTime.Hour, Minute = initialTime.Minute,
                Enabled = true, Label = ""
            }
            : AlarmPresentation.Copy(alarm);
        _hour = _original.Hour;
        _minute = _original.Minute;
        _isRepeating = _original.IsRepeating;
        _days = _original.Days;
        LabelEntry.Text = _original.Label;
        TitleLabel.Text = _isNew ? "New alarm" : "Edit alarm";
        DeleteButton.IsVisible = !_isNew;

        for (var i = 0; i < _dayButtons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Padding = 0,
                MinimumWidthRequest = 0,
                MinimumHeightRequest = 48,
                FontSize = 14
            };
            button.Clicked += (_, _) =>
            {
                _days ^= AlarmPresentation.Days[index];
                RefreshControls();
            };
            _dayButtons[i] = button;
            DayGrid.Add(button, i);
        }
        RefreshControls();
    }

    private bool IsDirty =>
        _hour != _original.Hour || _minute != _original.Minute ||
        _isRepeating != _original.IsRepeating || _days != _original.Days ||
        (LabelEntry.Text ?? "") != _original.Label;

    private void RefreshControls()
    {
        TimeButton.Text = AlarmPresentation.Time(_hour, _minute);
        SemanticProperties.SetDescription(TimeButton, $"Alarm time, {TimeButton.Text}");
        SetChoiceStyle(OnceButton, !_isRepeating);
        SetChoiceStyle(RepeatButton, _isRepeating);
        RepeatOptions.IsVisible = _isRepeating;
        OnceDescription.IsVisible = !_isRepeating;
        if (!_isNew && !_original.IsRepeating && _hour == _original.Hour &&
            _minute == _original.Minute && _original.OnceDate is { } existing)
        {
            OnceDescription.Text = AlarmPresentation.HasFutureOnceOccurrence(_original)
                ? $"Once: {AlarmPresentation.DateText(existing)}"
                : "This occurrence is past; saving selects the next occurrence.";
        }
        else
        {
            OnceDescription.Text = "Once: the next occurrence of this time";
        }

        for (var i = 0; i < _dayButtons.Length; i++)
        {
            var selected = (_days & AlarmPresentation.Days[i]) != 0;
            var day = (DayOfWeek)i;
            _dayButtons[i].Text = (selected ? "✓" : "") +
                                  CultureInfo.CurrentCulture.DateTimeFormat.GetShortestDayName(day);
            SetChoiceStyle(_dayButtons[i], selected);
            SemanticProperties.SetDescription(_dayButtons[i],
                $"{CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)}, {(selected ? "selected" : "not selected")}");
        }
    }

    private static void SetChoiceStyle(Button button, bool selected)
    {
        button.BackgroundColor = Color.FromArgb(selected ? "#546E7A" : "#262626");
        button.TextColor = Colors.White;
        button.BorderColor = Color.FromArgb(selected ? "#D5E4E9" : "#666666");
        button.BorderWidth = selected ? 2 : 1;
        if (button.Text is "Once" or "Repeat")
        {
            SemanticProperties.SetDescription(button, $"{button.Text}, {(selected ? "selected" : "not selected")}");
        }
    }

    private async void OnTimeClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var time = await AlarmTimePicker.ShowAsync(_hour, _minute,
                Preferences.Default.Get(ClockSettings.Use24HourTimeKey, true));
            if (time is { } selected)
            {
                _hour = selected.Hour;
                _minute = selected.Minute;
                RefreshControls();
            }
        }
        catch (InvalidOperationException ex)
        {
            await DisplayAlertAsync("Unable to choose time", ex.Message, "OK");
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnOnceClicked(object? sender, EventArgs e) { _isRepeating = false; RefreshControls(); }
    private void OnRepeatClicked(object? sender, EventArgs e) { _isRepeating = true; RefreshControls(); }
    private void OnWeekdaysClicked(object? sender, EventArgs e) { _days = AlarmPresentation.Weekdays; RefreshControls(); }
    private void OnWeekendsClicked(object? sender, EventArgs e) { _days = AlarmPresentation.Weekends; RefreshControls(); }
    private void OnEveryDayClicked(object? sender, EventArgs e) { _days = AlarmPresentation.EveryDay; RefreshControls(); }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_isRepeating && _days == AlarmDays.None)
        {
            await DisplayAlertAsync("Choose days", "Select at least one day for a repeating alarm.", "OK");
            return;
        }

        _busy = true;
        SetControlsEnabled(false);
        try
        {
            var changedSchedule = _hour != _original.Hour || _minute != _original.Minute ||
                                  _isRepeating != _original.IsRepeating ||
                                  (_isRepeating && _days != _original.Days);
            if (changedSchedule && _original.SkipDate is not null &&
                !await DisplayAlertAsync("Clear skipped occurrence?",
                    "Changing the time or days clears the saved skip.", "Continue", "Cancel"))
            {
                return;
            }

            var updated = AlarmPresentation.Copy(_original);
            updated.Hour = _hour;
            updated.Minute = _minute;
            updated.IsRepeating = _isRepeating;
            updated.Days = _isRepeating ? _days : AlarmDays.None;
            updated.Label = (LabelEntry.Text ?? "").Trim();
            updated.OnceDate = _isRepeating ? null
                : changedSchedule || _isNew || _original.OnceDate is null ||
                  !AlarmPresentation.HasFutureOnceOccurrence(_original)
                    ? AlarmPresentation.NextOnceDate(_hour, _minute)
                    : _original.OnceDate;
            updated.SkipDate = changedSchedule || !_isRepeating ? null : _original.SkipDate;

            if (_isNew)
            {
                await _coordinator.AddAsync(updated);
            }
            else
            {
                await _coordinator.ReplaceAsync(_original, updated);
            }
            await Navigation.PopAsync();
        }
        catch (AlarmConflictException ex)
        {
            await DisplayAlertAsync("Alarm changed", ex.Message, "OK");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await DisplayAlertAsync("Unable to save alarm", ex.Message, "OK");
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetControlsEnabled(false);
        try
        {
            if (!await DisplayAlertAsync("Delete alarm?", "This alarm will be removed.", "Delete", "Cancel")) return;
            await _coordinator.DeleteAsync(_original);
            await Navigation.PopAsync();
        }
        catch (AlarmConflictException ex)
        {
            await DisplayAlertAsync("Alarm changed", ex.Message, "OK");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await DisplayAlertAsync("Unable to delete alarm", ex.Message, "OK");
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        EditorContent.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    private async Task GoBackAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if ((!_isNew && !IsDirty) ||
                await DisplayAlertAsync("Discard changes?",
                    _isNew ? "This alarm has not been saved." : "Your changes have not been saved.",
                    "Discard", "Keep editing"))
            {
                await Navigation.PopAsync();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await GoBackAsync();

    protected override bool OnBackButtonPressed()
    {
        _ = GoBackAsync();
        return true;
    }
}
