using Android.Content;
using Android.Media;
using Android.OS;
using Android.Util;

namespace BedsideAlarmClock.Platforms.Android;

internal sealed class AndroidAlarmSoundPlayer
{
    private const string Tag = "BedsideAlarmPlayer";
    private readonly object sync = new();
    private readonly HandlerThread thread;
    private readonly Handler handler;
    private readonly TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<AndroidAlarmSoundPlayer, Exception> onPlaybackError;
    private MediaPlayer? player;
    private bool closing;
    private Task? releaseTask;

    public AndroidAlarmSoundPlayer(Context context, Action<Exception> onPlaybackError, string? soundUri = null, double volume = 1)
        : this(context, AdaptErrorHandler(onPlaybackError), soundUri, volume)
    {
    }

    private static Action<AndroidAlarmSoundPlayer, Exception> AdaptErrorHandler(Action<Exception> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return (_, error) => handler(error);
    }

    public AndroidAlarmSoundPlayer(Context context, Action<AndroidAlarmSoundPlayer, Exception> onPlaybackError, string? soundUri = null, double volume = 1)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(volume));
        this.onPlaybackError = onPlaybackError ?? throw new ArgumentNullException(nameof(onPlaybackError));
        var applicationContext = context.ApplicationContext ?? context;
        thread = new HandlerThread("bedside-alarm-player");
        thread.Start();
        var looper = thread.Looper;
        if (looper is null)
        {
            thread.QuitSafely();
            thread.Dispose();
            throw new InvalidOperationException("The audio thread did not start.");
        }
        handler = new Handler(looper);
        if (!handler.Post(() => Prepare(applicationContext, soundUri, (float)volume)))
        {
            thread.QuitSafely();
            handler.Dispose();
            thread.Dispose();
            throw new InvalidOperationException("Could not start alarm player preparation.");
        }
    }

    public Task Prepared => prepared.Task;

    public async Task StartAsync(Action checkBeforeStart)
    {
        ArgumentNullException.ThrowIfNull(checkBeforeStart);
        await prepared.Task.ConfigureAwait(false);
        await PostAsync(() =>
        {
            checkBeforeStart();
            var active = player ?? throw new InvalidOperationException("The alarm player is unavailable.");
            active.Start();
            Log.Info(Tag, $"playback_started is_playing={active.IsPlaying}");
        }).ConfigureAwait(false);
    }

    public async Task<int> GetPositionAsync()
    {
        await prepared.Task.ConfigureAwait(false);
        var position = 0;
        await PostAsync(() =>
        {
            position = (player ?? throw new InvalidOperationException("The alarm player is unavailable.")).CurrentPosition;
        }).ConfigureAwait(false);
        return position;
    }

    public Task ReleaseAsync()
    {
        lock (sync)
        {
            if (releaseTask is not null)
                return releaseTask;
            closing = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseTask = completion.Task;
            if (!handler.Post(() =>
            {
                try
                {
                    prepared.TrySetException(new InvalidOperationException("The alarm player was released before preparation completed."));
                    player?.Release();
                    player?.Dispose();
                    player = null;
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, $"Player release failed: {ex}");
                    completion.TrySetException(ex);
                }
                finally
                {
                    thread.QuitSafely();
                    handler.Dispose();
                    thread.Dispose();
                }
            }))
                completion.TrySetException(new InvalidOperationException("Could not queue alarm player release."));
            return releaseTask;
        }
    }

    private void Prepare(Context context, string? soundUri, float volume)
    {
        try
        {
            using var uri = soundUri is null
                ? RingtoneManager.GetActualDefaultRingtoneUri(context, RingtoneType.Alarm)
                : global::Android.Net.Uri.Parse(soundUri);
            if (uri is null)
                throw new InvalidOperationException("The alarm sound is unavailable.");
            using var builder = new AudioAttributes.Builder();
            builder.SetUsage(AudioUsageKind.Alarm);
            builder.SetContentType(AudioContentType.Sonification);
            using var attributes = builder.Build()
                ?? throw new InvalidOperationException("Alarm audio attributes are unavailable.");
            player = new MediaPlayer();
            player.Prepared += (_, _) =>
            {
                Log.Info(Tag, "player_prepared usage=alarm");
                prepared.TrySetResult();
            };
            player.Error += (_, args) =>
            {
                var error = new InvalidOperationException($"Alarm player error: {args.What}, {args.Extra}.");
                Log.Error(Tag, error.Message);
                args.Handled = true;
                if (!prepared.TrySetException(error))
                {
                    try
                    {
                        onPlaybackError(this, error);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(Tag, $"Playback error handler failed: {ex}");
                    }
                }
            };
            player.SetAudioAttributes(attributes);
            player.SetDataSource(context, uri);
            player.SetVolume(volume, volume);
            player.Looping = true;
            player.PrepareAsync();
            Log.Info(Tag, $"player_preparation_requested source={(soundUri is null ? "system_default_alarm" : "selected_sound")}");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Player preparation failed: {ex}");
            prepared.TrySetException(ex);
        }
    }

    private Task PostAsync(Action action)
    {
        lock (sync)
        {
            if (closing)
                throw new ObjectDisposedException(nameof(AndroidAlarmSoundPlayer));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!handler.Post(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
                completion.TrySetException(new InvalidOperationException("Could not queue alarm player operation."));
            return completion.Task;
        }
    }
}
