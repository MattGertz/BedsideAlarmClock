using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Util;
using BedsideAlarmClock.Alarms;
using System.Runtime.Versioning;

namespace BedsideAlarmClock.Platforms.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AlarmPlaybackService : Service, AudioManager.IOnAudioFocusChangeListener
{
    internal const string DeliverAction = "bedside.alarm.DELIVER";
    internal const string DismissAction = "bedside.alarm.DISMISS";
    internal const string SnoozeAction = "bedside.alarm.SNOOZE";
    private const string ChannelId = "active-alarms";
    private const string Tag = "BedsideAlarm";
    private readonly ConcurrentDictionary<AlarmRegistration, ActiveAlarm> active = new();
    private int nextNotificationId = 1000;
    private int latestStartId;
    private int pendingCommands;
    private AudioManager? audioManager;
    private AudioFocusRequestClass? focusRequest;
    private PowerManager.WakeLock? wakeLock;
    internal static AlarmPlaybackService? Current { get; private set; }
    internal static event Action? AlarmStateChanged;
    internal static bool IsAlarmProcessing => Current is { } service && !service.active.IsEmpty;
    internal event Action? ActiveAlarmsChanged;

    private AlarmCoordinator Coordinator => IPlatformApplication.Current!.Services.GetRequiredService<AlarmCoordinator>();
    private AlarmRegistrationRepository Store => IPlatformApplication.Current!.Services.GetRequiredService<AlarmRegistrationRepository>();
    private AndroidAlarmScheduler Scheduler => IPlatformApplication.Current!.Services.GetRequiredService<AndroidAlarmScheduler>();
    private AlarmRegistrationReconciler Reconciler => IPlatformApplication.Current!.Services.GetRequiredService<AlarmRegistrationReconciler>();

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        latestStartId = startId;
        try
        {
            StartForegroundNotification();
            if (!TryReadRegistration(intent, out var registration))
                throw new InvalidDataException("The alarm service received an invalid registration.");
            var action = intent?.Action;
            if (action is not (DeliverAction or DismissAction or SnoozeAction))
                throw new InvalidDataException("The alarm service received an invalid action.");
            Interlocked.Increment(ref pendingCommands);
            _ = HandleCommandAsync(registration, action, intent!.GetLongExtra("received_at_ms", 0));
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Alarm service startup failed: {ex}");
            ShowError("Alarm could not start", ex.Message);
            StopIfIdle();
        }
        return StartCommandResult.NotSticky;
    }

    public void OnAudioFocusChange(AudioFocus focusChange) =>
        Log.Info(Tag, $"Audio focus callback: {focusChange}; playback continues.");

    private async Task HandleCommandAsync(AlarmRegistration registration, string action, long receivedAtMilliseconds)
    {
        try
        {
            if (action == DeliverAction)
                await DeliverAsync(registration, receivedAtMilliseconds);
            else
                await CompleteAsync(registration, action == SnoozeAction);
        }
        finally
        {
            Interlocked.Decrement(ref pendingCommands);
            StopIfIdle();
        }
    }

    private async Task DeliverAsync(AlarmRegistration registration, long receivedAtMilliseconds)
    {
        var alarm = new ActiveAlarm(Interlocked.Increment(ref nextNotificationId));
        if (!await AndroidAlarmRecovery.BeginDeliveryAsync(() => active.TryAdd(registration, alarm)))
        {
            Log.Info(Tag, $"Duplicate alarm delivery ignored: {registration}");
            return;
        }

        var recoveryNeeded = false;
        try
        {
            NotifyAlarmStateChanged();
            var zone = TimeZoneInfo.Local;
            if (receivedAtMilliseconds <= 0)
                throw new InvalidDataException("The alarm receipt time is missing.");
            var receivedAt = DateTimeOffset.FromUnixTimeMilliseconds(receivedAtMilliseconds);
            var observed = registration.Kind == AlarmRegistrationKind.Regular
                ? await Coordinator.RecordRegularDeliveryAsync(Store, registration, receivedAt, zone)
                : await Coordinator.RecordSnoozeDeliveryAsync(Store, registration, receivedAt, zone);
            if (observed is DeliveryRecordResult.Stale or DeliveryRecordResult.AlreadyRecorded)
            {
                Log.Info(Tag, $"Ignoring {observed} alarm delivery: {registration}");
                active.TryRemove(registration, out _);
                NotifyAlarmStateChanged();
                return;
            }
            if (observed != DeliveryRecordResult.Recorded)
                throw new InvalidOperationException($"Alarm delivery could not be claimed: {observed}.");

            var clockStatus = AndroidAlarmClockAnchor.ReadStatus(this);
            var reserved = registration.Kind == AlarmRegistrationKind.Regular
                ? await Coordinator.ReserveRegularPlaybackAsync(Store, registration, TimeProvider.System, zone, clockStatus, false)
                : await Coordinator.ReserveSnoozePlaybackAsync(Store, registration, TimeProvider.System, zone, clockStatus, false);
            if (reserved == PlaybackReservationResult.Missed)
            {
                var missed = registration.Kind == AlarmRegistrationKind.Regular
                    ? await Coordinator.RecordMissedRegularAsync(Store, registration, DateTimeOffset.UtcNow, false, zone)
                    : await Coordinator.RecordMissedSnoozeAsync(Store, registration, DateTimeOffset.UtcNow, false, zone);
                if (missed != MissedAlarmRecordResult.Recorded)
                    throw new InvalidOperationException($"Missed alarm could not be recorded: {missed}.");
                await ReconcileAfterDeliveryAsync();
                throw new InvalidOperationException("The alarm arrived more than 30 seconds late and was recorded as missed.");
            }
            if (reserved != PlaybackReservationResult.Reserved)
                throw new InvalidOperationException($"Alarm playback could not be reserved: {reserved}.");

            var definition = (await Coordinator.LoadAsync()).SingleOrDefault(item => item.Id == registration.AlarmId);
            if (definition is null)
                throw new InvalidOperationException("The alarm definition is unavailable.");
            alarm.Label = $"{definition.Hour:D2}:{definition.Minute:D2}" +
                (string.IsNullOrWhiteSpace(definition.Label) ? "" : $"  {definition.Label}");
            alarm.SnoozeMinutes = definition.SnoozeMinutes;
            alarm.Volume = definition.Volume;
            RequestFocus();
            var player = await PreparePlayerAsync(definition, registration, alarm);
            lock (alarm.Sync) alarm.Player = player;
            var result = await Coordinator.StartReservedPlaybackOnPlayerAsync(Store, registration,
                TimeProvider.System, zone, AndroidAlarmClockAnchor.ReadStatus(this), false,
                check => player.StartAsync(() =>
                {
                    if (AndroidAlarmClockAnchor.ReadStatus(this) != RecoveryClockStatus.Trusted)
                        throw new InvalidOperationException("The device clock changed before alarm playback.");
                    check();
                }));
            if (result == PlaybackStartResult.Missed)
            {
                var missed = await Coordinator.RecordUnstartedReservationMissedAsync(Store, registration, DateTimeOffset.UtcNow);
                if (missed != MissedAlarmRecordResult.Recorded)
                    throw new InvalidOperationException($"Missed alarm could not be recorded: {missed}.");
                await ReconcileAfterDeliveryAsync();
                throw new InvalidOperationException("The alarm could not start within 30 seconds and was recorded as missed.");
            }
            if (result != PlaybackStartResult.Started)
                throw new InvalidOperationException($"Alarm playback did not start: {result}.");

            Exception? pendingError;
            lock (alarm.Sync)
            {
                alarm.Started = true;
                pendingError = alarm.PendingError;
                alarm.PendingError = null;
            }
            ShowActive(registration, alarm);
            NotifyAlarmStateChanged();
            Log.Info(Tag, $"Alarm playback started: {registration}");
            if (pendingError is not null)
                OnPlaybackError(registration, alarm, player, pendingError);
            _ = LogPlaybackPositionAsync(registration, alarm);
            await ReconcileAfterDeliveryAsync();
        }
        catch (Exception ex)
        {
            recoveryNeeded = true;
            Log.Error(Tag, $"Alarm delivery failed: {ex}");
            ShowError("Alarm could not ring", ex.Message);
            if (alarm.Player is not null)
            {
                try { await alarm.Player.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception releaseError) { Log.Error(Tag, $"Alarm player release failed: {releaseError}"); }
            }
            active.TryRemove(registration, out _);
            NotifyAlarmStateChanged();
        }
        finally
        {
            try { await AndroidAlarmRecovery.FinishDeliveryAsync(this); }
            catch (Exception ex) { AndroidAlarmRecovery.ShowFailure(this, ex); }
            if (recoveryNeeded)
            {
                try { await AndroidAlarmRecovery.RunAsync(this); }
                catch (Exception ex) { AndroidAlarmRecovery.ShowFailure(this, ex); }
            }
        }
    }

    private async Task CompleteAsync(AlarmRegistration registration, bool snooze)
    {
        if (!active.TryGetValue(registration, out var alarm))
        {
            Log.Warn(Tag, $"Alarm action ignored for inactive alarm: {registration}");
            return;
        }
        AndroidAlarmSoundPlayer player;
        string? unavailable = null;
        lock (alarm.Sync)
        {
            if (!alarm.Started || (snooze && alarm.SnoozeMinutes is null) || alarm.Completing != 0 ||
                alarm.Player is null)
            {
                Log.Warn(Tag, $"Alarm action ignored for inactive alarm: {registration}");
                return;
            }
            player = alarm.Player;
            if (snooze && alarm.AudioStopped)
                unavailable = "Alarm audio is stopped. Retry dismissal.";
            else if (snooze && alarm.Recovering)
                unavailable = "Alarm sound is recovering. Please retry snooze.";
            else
                alarm.Completing = 1;
        }
        if (unavailable is not null)
        {
            ReportActionError(registration, alarm, unavailable);
            return;
        }
        var snoozeConfirmed = !snooze;
        var playerReleased = false;
        var actionCompleted = false;
        try
        {
            if (snooze)
            {
                var result = registration.Kind == AlarmRegistrationKind.Regular
                    ? await Coordinator.SnoozeStartedRegularPlaybackAsync(Store, Scheduler, registration, TimeProvider.System, TimeZoneInfo.Local, alarm.SnoozeMinutes!.Value)
                    : await Coordinator.SnoozeStartedSnoozePlaybackAsync(Store, Scheduler, registration, TimeProvider.System, TimeZoneInfo.Local, alarm.SnoozeMinutes!.Value);
                if (result is not (SnoozeResult.Scheduled or SnoozeResult.Rescheduled))
                    throw new InvalidOperationException($"Snooze was not scheduled: {result}.");
                snoozeConfirmed = true;
            }
            await player.ReleaseAsync();
            playerReleased = true;
            lock (alarm.Sync) alarm.AudioStopped = true;
            if (!snooze)
            {
                var result = await Coordinator.DismissStartedPlaybackAsync(Store, Scheduler, registration, TimeZoneInfo.Local);
                if (result is not (PlaybackCompletionResult.Completed or PlaybackCompletionResult.AlreadyCompleted))
                    throw new InvalidOperationException($"Dismissal was not recorded: {result}.");
            }
            Log.Info(Tag, $"Alarm {(snooze ? "snoozed" : "dismissed")}: {registration}");
            actionCompleted = true;
            await ReconcileAfterDeliveryAsync();
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Alarm action failed: {ex}");
            if (snooze && !snoozeConfirmed)
                ReportActionError(registration, alarm, $"Snooze could not be confirmed. Retry or dismiss. {ex.Message}");
            else if (!playerReleased)
                ReportActionError(registration, alarm, $"Alarm audio could not be stopped. {ex.Message}");
            else
                ReportActionError(registration, alarm,
                    $"Dismissal could not be confirmed. Retry dismissal; a pending snooze may still occur. {ex.Message}");
        }
        finally
        {
            if (!actionCompleted)
            {
                Exception? pendingError;
                lock (alarm.Sync)
                {
                    alarm.Completing = 0;
                    pendingError = alarm.PendingError;
                    alarm.PendingError = null;
                }
                if (!playerReleased && pendingError is not null) OnPlaybackError(registration, alarm, player, pendingError);
            }
            else
            {
                Notifications().Cancel(alarm.NotificationId);
                active.TryRemove(registration, out _);
                NotifyAlarmStateChanged();
            }
        }
    }

    private void ReportActionError(AlarmRegistration registration, ActiveAlarm alarm, string message)
    {
        lock (alarm.Sync) alarm.ActionError = message;
        try { ShowActive(registration, alarm); }
        catch (Exception ex) { Log.Error(Tag, $"Could not update alarm controls: {ex}"); }
        ShowError("Alarm action needs attention", message);
        NotifyAlarmStateChanged();
    }

    private void NotifyAlarmStateChanged()
    {
        ActiveAlarmsChanged?.Invoke();
        AlarmStateChanged?.Invoke();
    }

    private void StartForegroundNotification()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var channel = new NotificationChannel(ChannelId, "Active alarms", NotificationImportance.High);
            channel.SetSound(null, null);
            Notifications().CreateNotificationChannel(channel);
        }
        using var notification = BuildNotification("Alarm service", "Preparing or playing an alarm");
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(100, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(100, notification);
        Log.Info(Tag, "Foreground service started; type=mediaPlayback.");
        if (wakeLock is null)
        {
            var power = (PowerManager?)GetSystemService(PowerService)
                ?? throw new InvalidOperationException("Power service is unavailable.");
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "BedsideAlarmClock:playback")
                ?? throw new InvalidOperationException("Could not create an alarm wake lock.");
            wakeLock.Acquire();
        }
    }

    private Notification.Builder CreateBuilder() => OperatingSystem.IsAndroidVersionAtLeast(26)
        ? new Notification.Builder(this, ChannelId) : new Notification.Builder(this);

    private Notification BuildNotification(string title, string text)
    {
        using var builder = CreateBuilder();
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm);
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetCategory(Notification.CategoryAlarm);
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) SetLegacyPriority(builder);
        return builder.Build() ?? throw new InvalidOperationException("Could not build an alarm notification.");
    }

    [UnsupportedOSPlatform("android26.0")]
    private static void SetLegacyPriority(Notification.Builder builder) =>
        builder.SetPriority((int)NotificationPriority.High);

    private void ShowActive(AlarmRegistration registration, ActiveAlarm alarm)
    {
        using var dismiss = ActionIntent(registration, DismissAction);
        using var displayIntent = new Intent(this, typeof(AlarmRingingActivity));
        displayIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var display = PendingIntent.GetActivity(this, 0, displayIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Could not create alarm display intent.");
        using var builder = CreateBuilder();
        using var dismissIcon = global::Android.Graphics.Drawables.Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMenuCloseClearCancel);
        using var dismissBuilder = new Notification.Action.Builder(dismissIcon, "Dismiss", dismiss);
        using var dismissAction = dismissBuilder.Build();
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm);
        builder.SetContentTitle(alarm.Label);
        builder.SetContentText(alarm.ActionError ?? "Alarm is sounding");
        builder.SetCategory(Notification.CategoryAlarm);
        builder.SetOngoing(true);
        builder.SetContentIntent(display);
        builder.SetFullScreenIntent(display, true);
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) SetLegacyPriority(builder);
        builder.AddAction(dismissAction);
        if (alarm.SnoozeMinutes is { } minutes)
        {
            using var snooze = ActionIntent(registration, SnoozeAction);
            using var icon = global::Android.Graphics.Drawables.Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcLockIdleAlarm);
            using var snoozeBuilder = new Notification.Action.Builder(icon, $"Snooze {minutes} min", snooze);
            using var snoozeAction = snoozeBuilder.Build();
            builder.AddAction(snoozeAction);
        }
        using var notification = builder.Build() ?? throw new InvalidOperationException("Could not build active alarm controls.");
        Notifications().Notify(alarm.NotificationId, notification);
    }

    private PendingIntent ActionIntent(AlarmRegistration registration, string action)
    {
        using var intent = CreateActionIntent(this, registration, action);
        intent.SetData(global::Android.Net.Uri.Parse(
            $"bedsidealarm://action/{action}/{registration.Kind}/{registration.AlarmId:N}/{registration.RegistrationId:N}"));
        return PendingIntent.GetService(this, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
            ?? throw new InvalidOperationException("Could not create an alarm action.");
    }

    internal static Intent CreateActionIntent(Context context, AlarmRegistration registration, string action)
    {
        var intent = new Intent(context, typeof(AlarmPlaybackService));
        intent.SetAction(action);
        intent.PutExtra(AndroidAlarmScheduler.AlarmIdExtra, registration.AlarmId.ToString("N"));
        intent.PutExtra(AndroidAlarmScheduler.RegistrationIdExtra, registration.RegistrationId.ToString("N"));
        intent.PutExtra(AndroidAlarmScheduler.KindExtra, (int)registration.Kind);
        return intent;
    }

    internal IReadOnlyList<(AlarmRegistration Registration, string Label, int? SnoozeMinutes, string? ActionError)> GetActiveAlarms() =>
        active.Where(item => item.Value.Started)
            .Select(item => (item.Key, item.Value.Label, item.Value.SnoozeMinutes, item.Value.ActionError)).ToArray();

    internal bool IsProcessing(AlarmRegistration registration) => active.ContainsKey(registration);

    private async Task<AndroidAlarmSoundPlayer> PreparePlayerAsync(
        AlarmDefinition definition, AlarmRegistration registration, ActiveAlarm alarm)
    {
        AndroidAlarmSoundPlayer Create(string? uri) => new(this,
            (source, error) => OnPlaybackError(registration, alarm, source, error), uri, definition.Volume);

        var player = Create(definition.SoundUri);
        if (definition.SoundUri is not null)
        {
            try
            {
                await player.Prepared.WaitAsync(TimeSpan.FromSeconds(5));
                alarm.UsingSelectedSound = true;
                return player;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"Selected sound failed; using system alarm: {ex}");
                ShowError("Selected alarm sound unavailable", "Using the default system alarm sound.");
                _ = ReleaseFailedPlayerAsync(player);
                player = Create(null);
            }
        }
        try
        {
            await player.Prepared.WaitAsync(TimeSpan.FromSeconds(10));
            return player;
        }
        catch
        {
            _ = ReleaseFailedPlayerAsync(player);
            throw;
        }
    }

    private void OnPlaybackError(AlarmRegistration registration, ActiveAlarm alarm,
        AndroidAlarmSoundPlayer source, Exception error)
    {
        Log.Error(Tag, $"Alarm playback error: {error}");
        lock (alarm.Sync)
        {
            if (!ReferenceEquals(alarm.Player, source)) return;
            if (!alarm.Started || alarm.Completing != 0)
            {
                alarm.PendingError = error;
                return;
            }
            if (!alarm.UsingSelectedSound || alarm.Recovering)
            {
                ShowError("Alarm playback error", error.Message);
                return;
            }
            alarm.Recovering = true;
        }
        _ = RecoverPlaybackAsync(registration, alarm, source);
    }

    private async Task RecoverPlaybackAsync(AlarmRegistration registration, ActiveAlarm alarm,
        AndroidAlarmSoundPlayer failed)
    {
        AndroidAlarmSoundPlayer? replacement = null;
        try
        {
            replacement = new AndroidAlarmSoundPlayer(this,
                (source, error) => OnPlaybackError(registration, alarm, source, error), null, alarm.Volume);
            await replacement.Prepared.WaitAsync(TimeSpan.FromSeconds(10));
            lock (alarm.Sync)
            {
                if (alarm.Completing != 0 || !ReferenceEquals(alarm.Player, failed)) return;
                alarm.Player = replacement;
                alarm.UsingSelectedSound = false;
            }
            try { await failed.ReleaseAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { Log.Error(Tag, $"Failed player release during recovery: {ex}"); }
            await replacement.StartAsync(() =>
            {
                lock (alarm.Sync)
                {
                    if (alarm.Completing != 0 || !ReferenceEquals(alarm.Player, replacement))
                        throw new System.OperationCanceledException("The alarm was dismissed during sound recovery.");
                }
            });
            Log.Info(Tag, "Selected sound failed during playback; system alarm started.");
            ShowError("Selected alarm sound unavailable", "Using the default system alarm sound.");
            replacement = null;
        }
        catch (Exception ex)
        {
            lock (alarm.Sync)
            {
                if (alarm.Completing != 0) return;
            }
            Log.Error(Tag, $"Could not recover alarm playback: {ex}");
            ShowError("Alarm playback error", $"The system alarm sound could not start: {ex.Message}");
        }
        finally
        {
            lock (alarm.Sync) alarm.Recovering = false;
            if (replacement is not null) _ = ReleaseFailedPlayerAsync(replacement);
        }
    }

    private static async Task ReleaseFailedPlayerAsync(AndroidAlarmSoundPlayer player)
    {
        try { await player.ReleaseAsync(); }
        catch (Exception ex) { Log.Error(Tag, $"Failed player release: {ex}"); }
    }

    private void RequestFocus()
    {
        if (audioManager is not null) return;
        try
        {
            audioManager = (AudioManager?)GetSystemService(AudioService)
                ?? throw new InvalidOperationException("Audio manager is unavailable.");
            AudioFocusRequest result;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var attributesBuilder = new AudioAttributes.Builder();
                attributesBuilder.SetUsage(AudioUsageKind.Alarm);
                attributesBuilder.SetContentType(AudioContentType.Sonification);
                using var attributes = attributesBuilder.Build()
                    ?? throw new InvalidOperationException("Could not create alarm audio attributes.");
                using var focusBuilder = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient);
                focusBuilder.SetAudioAttributes(attributes);
                focusBuilder.SetOnAudioFocusChangeListener(this);
                focusRequest = focusBuilder.Build()
                    ?? throw new InvalidOperationException("Could not create an audio focus request.");
                result = audioManager.RequestAudioFocus(focusRequest);
            }
            else
                result = audioManager.RequestAudioFocus(this, global::Android.Media.Stream.Alarm, AudioFocus.GainTransient);
            Log.Info(Tag, $"Audio focus result: {result}; playback proceeds regardless.");
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Audio focus request failed; playback proceeds: {ex}");
        }
    }

    private void ShowError(string title, string message)
    {
        try
        {
            using var notification = BuildNotification(title, message);
            Notifications().Notify(Interlocked.Increment(ref nextNotificationId), notification);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Could not show alarm error notification: {ex}");
        }
    }

    private async Task LogPlaybackPositionAsync(AlarmRegistration registration, ActiveAlarm alarm)
    {
        try
        {
            var player = alarm.Player ?? throw new InvalidOperationException("The alarm player is unavailable.");
            var first = await player.GetPositionAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));
            if (!active.ContainsKey(registration)) return;
            var second = await player.GetPositionAsync();
            Log.Info(Tag, $"Playback position: first_ms={first} second_ms={second}.");
        }
        catch (ObjectDisposedException) when (!active.ContainsKey(registration)) { }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Could not inspect alarm playback position: {ex}");
        }
    }

    private async Task ReconcileAfterDeliveryAsync()
    {
        try
        {
            await Coordinator.ReconcileAllAsync(Reconciler, TimeZoneInfo.Local);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Alarm schedule could not be updated: {ex}");
            ShowError("Alarm schedule uncertain", ex.Message);
        }
    }

    private NotificationManager Notifications() => (NotificationManager?)GetSystemService(NotificationService)
        ?? throw new InvalidOperationException("Notification manager is unavailable.");

    private void StopIfIdle()
    {
        if (active.IsEmpty && Volatile.Read(ref pendingCommands) == 0) StopSelfResult(latestStartId);
    }

    private static bool TryReadRegistration(Intent? intent, out AlarmRegistration registration)
    {
        var validAlarm = Guid.TryParseExact(intent?.GetStringExtra(AndroidAlarmScheduler.AlarmIdExtra), "N", out var alarmId);
        var validRegistration = Guid.TryParseExact(intent?.GetStringExtra(AndroidAlarmScheduler.RegistrationIdExtra), "N", out var registrationId);
        var kind = (AlarmRegistrationKind)(intent?.GetIntExtra(AndroidAlarmScheduler.KindExtra, -1) ?? -1);
        registration = new AlarmRegistration(alarmId, registrationId, kind);
        return validAlarm && validRegistration && alarmId != Guid.Empty && registrationId != Guid.Empty && Enum.IsDefined(kind);
    }

    public override void OnDestroy()
    {
        if (audioManager is not null)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                if (focusRequest is not null) audioManager.AbandonAudioFocusRequest(focusRequest);
            }
            else
                audioManager.AbandonAudioFocus(this);
        }
        focusRequest?.Dispose();
        if (wakeLock?.IsHeld == true) wakeLock.Release();
        wakeLock?.Dispose();
        Current = null;
        NotifyAlarmStateChanged();
        base.OnDestroy();
    }

    private sealed class ActiveAlarm(int notificationId)
    {
        public object Sync { get; } = new();
        public int NotificationId { get; } = notificationId;
        public string Label { get; set; } = "Alarm";
        public string? ActionError { get; set; }
        public int? SnoozeMinutes { get; set; }
        public double Volume { get; set; } = 1;
        public AndroidAlarmSoundPlayer? Player { get; set; }
        public bool Started { get; set; }
        public bool AudioStopped { get; set; }
        public bool UsingSelectedSound { get; set; }
        public bool Recovering { get; set; }
        public Exception? PendingError { get; set; }
        public int Completing;
    }
}
