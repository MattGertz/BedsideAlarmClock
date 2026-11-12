using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using Android.Util;
using BedsideAlarmClock.Alarms;
using BedsideAlarmClock.Platforms.Android;

namespace BedsideAlarmClock.AndroidSchedulerProbe;

internal static class AudioProbe
{
    public const string Tag = "AlarmAudioProbe";
    public const string StateName = "audio-probe";
    public const string TriggerKey = "trigger";
    public const string FocusModeKey = "focus_mode";
    public const string NoFocus = "no_focus";
    public const string BestEffort = "best_effort";
    public const string Component = "component";

    public static void Schedule(Context context, string focusMode)
    {
        if (focusMode is not (NoFocus or BestEffort or Component))
            throw new ArgumentException("Expected no_focus, best_effort or component.", nameof(focusMode));
        var scheduler = new AndroidAlarmScheduler(context, typeof(AudioProbeReceiver), typeof(MainActivity));
        var registration = new AlarmRegistration(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), AlarmRegistrationKind.Regular);
        var trigger = DateTimeOffset.UtcNow.AddSeconds(10);
        using var state = context.GetSharedPreferences(StateName, FileCreationMode.Private)
            ?? throw new InvalidOperationException("Audio probe state is unavailable.");
        using var editor = state.Edit() ?? throw new InvalidOperationException("Audio probe state cannot be edited.");
        editor.PutLong(TriggerKey, trigger.ToUnixTimeMilliseconds());
        editor.PutString(FocusModeKey, focusMode);
        if (!editor.Commit())
            throw new InvalidOperationException("Audio probe state could not be saved.");
        scheduler.Schedule(registration, trigger);
        Log.Info(Tag, $"scheduled trigger_ms={trigger.ToUnixTimeMilliseconds()} focus_mode={focusMode}");
    }
}

[BroadcastReceiver(Exported = false)]
public sealed class AudioProbeReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            Log.Error(AudioProbe.Tag, "ERROR missing receiver context");
            return;
        }

        using var state = context.GetSharedPreferences(AudioProbe.StateName, FileCreationMode.Private);
        var trigger = state?.GetLong(AudioProbe.TriggerKey, 0) ?? 0;
        var focusMode = state?.GetString(AudioProbe.FocusModeKey, null);
        var callback = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Log.Info(AudioProbe.Tag, $"callback_ms={callback} scheduled_ms={trigger} focus_mode={focusMode}");
        if (trigger == 0 || focusMode is not (AudioProbe.NoFocus or AudioProbe.BestEffort or AudioProbe.Component))
        {
            Log.Error(AudioProbe.Tag, "ERROR missing or invalid audio probe state");
            return;
        }

        try
        {
            using var service = new Intent(context, typeof(AudioProbeService));
            service.PutExtra(AudioProbe.FocusModeKey, focusMode);
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(service);
            else
                context.StartService(service);
            Log.Info(AudioProbe.Tag, "service_start_requested");
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR service start: {ex}");
        }
    }
}

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class AudioProbeService : Service, AudioManager.IOnAudioFocusChangeListener
{
    private const string ChannelId = "audio-probe";
    private MediaPlayer? player;
    private AudioManager? audioManager;
    private AudioAttributes? attributes;
    private AudioFocusRequestClass? focusRequest;
    private AndroidAlarmSoundPlayer? componentPlayer;
    private Handler? handler;
    private bool focusGranted;
    private bool started;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                throw new NotSupportedException("The audio probe requires Android 8 or later.");
            if (started)
                throw new InvalidOperationException("The audio probe is already running.");
            started = true;

            var notifications = (NotificationManager?)GetSystemService(NotificationService)
                ?? throw new InvalidOperationException("Notification manager is unavailable.");
            using var channel = new NotificationChannel(ChannelId, "Audio probe", NotificationImportance.Low);
            notifications.CreateNotificationChannel(channel);
            using var builder = new Notification.Builder(this, ChannelId);
            using var notification = builder.SetContentTitle("Alarm audio probe")
                .SetContentText("Testing playback for a few seconds")
                .SetSmallIcon(Android.Resource.Drawable.IcLockIdleAlarm)
                .Build() ?? throw new InvalidOperationException("Notification could not be built.");
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(1, notification, ForegroundService.TypeMediaPlayback);
            else
                StartForeground(1, notification);
            Log.Info(AudioProbe.Tag, $"foreground_started declared_type=mediaPlayback runtime_type={(OperatingSystem.IsAndroidVersionAtLeast(29) ? "mediaPlayback" : "manifest_default")}");

            audioManager = (AudioManager?)GetSystemService(AudioService)
                ?? throw new InvalidOperationException("Audio manager is unavailable.");
            handler = new Handler(Looper.MainLooper ?? throw new InvalidOperationException("Main looper is unavailable."));
            handler.PostDelayed(() =>
            {
                Log.Error(AudioProbe.Tag, "ERROR playback probe timed out");
                StopSelf();
            }, 10000);
            using var attributesBuilder = new AudioAttributes.Builder();
            attributesBuilder.SetUsage(AudioUsageKind.Alarm);
            attributesBuilder.SetContentType(AudioContentType.Sonification);
            attributes = attributesBuilder.Build()
                ?? throw new InvalidOperationException("Audio attributes could not be built.");
            Log.Info(AudioProbe.Tag, $"attributes usage={attributes.Usage} content={attributes.ContentType} alarm_volume={audioManager.GetStreamVolume(Android.Media.Stream.Alarm)}");
            var mode = intent?.GetStringExtra(AudioProbe.FocusModeKey);
            if (mode == AudioProbe.Component)
            {
                Log.Info(AudioProbe.Tag, "focus=not_requested mode=component");
                _ = RunComponentAsync();
                return StartCommandResult.NotSticky;
            }
            if (mode == AudioProbe.NoFocus)
                Log.Info(AudioProbe.Tag, "focus=not_requested mode=no_focus");
            else if (mode == AudioProbe.BestEffort)
            {
                try
                {
                    using var focusBuilder = new AudioFocusRequestClass.Builder(AudioFocus.GainTransient);
                    focusBuilder.SetAudioAttributes(attributes);
                    focusBuilder.SetOnAudioFocusChangeListener(this, handler);
                    focusRequest = focusBuilder.Build()
                        ?? throw new InvalidOperationException("Audio focus request could not be built.");
                    var focus = audioManager.RequestAudioFocus(focusRequest);
                    focusGranted = focus == AudioFocusRequest.Granted;
                    Log.Info(AudioProbe.Tag, $"focus={focus} mode=best_effort");
                    if (!focusGranted)
                        Log.Warn(AudioProbe.Tag, "Audio focus was not granted; continuing playback.");
                }
                catch (Exception ex)
                {
                    Log.Error(AudioProbe.Tag, $"ERROR audio focus request; continuing playback: {ex}");
                }
            }
            else
                throw new InvalidOperationException("Audio probe focus mode is missing or invalid.");
            StartPlayer();
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR service setup: {ex}");
            StopSelf();
        }
        return StartCommandResult.NotSticky;
    }

    private async Task RunComponentAsync()
    {
        try
        {
            componentPlayer = new AndroidAlarmSoundPlayer(this,
                error => Log.Error(AudioProbe.Tag, $"ERROR component playback: {error}"));
            await componentPlayer.Prepared;
            try
            {
                await componentPlayer.StartAsync(() => throw new InvalidOperationException("Rejected for test."));
                throw new InvalidOperationException("The rejected component start succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "Rejected for test.")
            {
                Log.Info(AudioProbe.Tag, "component_rejected_start");
            }
            await componentPlayer.StartAsync(() => { });
            var last = await componentPlayer.GetPositionAsync();
            var advances = 0;
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(250);
                var position = await componentPlayer.GetPositionAsync();
                if (position > last)
                    advances++;
                last = position;
                Log.Info(AudioProbe.Tag, $"component_position_ms={position} advances={advances}");
            }
            await componentPlayer.ReleaseAsync();
            await componentPlayer.ReleaseAsync();
            try
            {
                await componentPlayer.StartAsync(() => { });
                throw new InvalidOperationException("The released component restarted.");
            }
            catch (ObjectDisposedException)
            {
                Log.Info(AudioProbe.Tag, "component_released_start_rejected");
            }
            Log.Info(AudioProbe.Tag, advances > 0 ? "AUDIO_COMPONENT_PASS" : "ERROR component position did not advance");
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR component player: {ex}");
        }
        finally
        {
            if (componentPlayer is not null)
            {
                try
                {
                    await componentPlayer.ReleaseAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(AudioProbe.Tag, $"ERROR component release: {ex}");
                }
            }
            StopSelf();
        }
    }

    private void StartPlayer()
    {
        try
        {
            var audioAttributes = attributes ?? throw new InvalidOperationException("Audio attributes are unavailable.");
            using var uri = RingtoneManager.GetActualDefaultRingtoneUri(this, RingtoneType.Alarm)
                ?? throw new InvalidOperationException("There is no default system alarm sound.");
            player = new MediaPlayer();
            Log.Info(AudioProbe.Tag, "player_created source=system_default_alarm");
            player.Error += OnPlayerError;
            player.Prepared += OnPlayerPrepared;
            player.SetDataSource(this, uri);
            player.SetAudioAttributes(audioAttributes);
            player.Looping = true;
            player.PrepareAsync();
            Log.Info(AudioProbe.Tag, $"prepare_requested elapsed_ms={SystemClock.ElapsedRealtime()}");
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR player setup: {ex}");
            StopSelf();
        }
    }

    private void OnPlayerPrepared(object? sender, EventArgs args)
    {
        try
        {
            var active = player ?? throw new InvalidOperationException("Player is unavailable.");
            active.Start();
            Log.Info(AudioProbe.Tag, $"start_returned elapsed_ms={SystemClock.ElapsedRealtime()} is_playing={active.IsPlaying}");
            var lastPosition = active.CurrentPosition;
            var advances = 0;
            var samples = 0;
            Log.Info(AudioProbe.Tag, $"position_initial_ms={lastPosition}");
            void Sample()
            {
                try
                {
                    var playing = active.IsPlaying;
                    var position = active.CurrentPosition;
                    if (position > lastPosition)
                        advances++;
                    lastPosition = position;
                    samples++;
                    Log.Info(AudioProbe.Tag, $"position_sample={samples} position_ms={position} is_playing={playing}");
                    if (samples < 10 && playing)
                    {
                        handler?.PostDelayed(Sample, 250);
                        return;
                    }
                    Log.Info(AudioProbe.Tag, $"position_check is_playing={playing} position_ms={position} samples={samples} advances={advances}");
                    Log.Info(AudioProbe.Tag, playing && advances > 0 ? "AUDIO_PROBE_PLAYER_PASS" : "ERROR playback did not advance");
                    StopSelf();
                }
                catch (Exception ex)
                {
                    Log.Error(AudioProbe.Tag, $"ERROR position check: {ex}");
                    StopSelf();
                }
            }
            handler?.PostDelayed(Sample, 250);
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR player start: {ex}");
            StopSelf();
        }
    }

    private void OnPlayerError(object? sender, MediaPlayer.ErrorEventArgs args)
    {
        Log.Error(AudioProbe.Tag, $"ERROR player what={args.What} extra={args.Extra}");
        args.Handled = true;
        StopSelf();
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        Log.Info(AudioProbe.Tag, $"focus_change={focusChange}");
    }

    public override void OnDestroy()
    {
        handler?.RemoveCallbacksAndMessages(null);
        if (componentPlayer is not null)
            _ = ReleaseComponentAsync(componentPlayer);
        if (player is not null)
        {
            player.Release();
            player.Dispose();
            player = null;
        }
        if (focusGranted && audioManager is not null && focusRequest is not null && OperatingSystem.IsAndroidVersionAtLeast(26))
            audioManager.AbandonAudioFocusRequest(focusRequest);
        focusRequest?.Dispose();
        attributes?.Dispose();
        handler?.Dispose();
        Log.Info(AudioProbe.Tag, "service_stopped");
        base.OnDestroy();
    }

    private static async Task ReleaseComponentAsync(AndroidAlarmSoundPlayer component)
    {
        try
        {
            await component.ReleaseAsync();
        }
        catch (Exception ex)
        {
            Log.Error(AudioProbe.Tag, $"ERROR component release during service shutdown: {ex}");
        }
    }
}
