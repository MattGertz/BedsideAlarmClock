# Android scheduler probe

This standalone test app exercises the production Android scheduler without
activating alarm delivery in the clock app. Use a disposable emulator, not a
phone or an emulator containing important data. It requires the Android
workload and an x86-64 emulator. It is not part of `dotnet test`.

Publish with:

```
dotnet publish tests\BedsideAlarmClock.AndroidSchedulerProbe\BedsideAlarmClock.AndroidSchedulerProbe.csproj -c Release -r android-x64 -p:AndroidPackageFormat=apk
```

Install the signed APK from the project's
`bin\Release\net10.0-android\android-x64\publish` directory on the isolated emulator and launch the
probe. Inspect logcat under the `AlarmProbe` tag. The normal scenario should
report exact access and rejection of a past time. Around 22 seconds later,
it should deliver the newer A registration, A's snooze and C; around 34
seconds it should deliver B. It must not deliver the canceled older A
registration or the original B schedule. To check process-independent
delivery, kill the probe process (not force-stop it) after `scheduled` and
before the first delivery; the receiver should run in a new process. It logs
`REGISTRATION_PROBE_PASS` only after all four expected deliveries and logs
`ERROR` for unexpected, duplicate, premature, or missing deliveries.

To exercise denied access on Android 14 or later, clean and publish again
with `-p:ProbeExactPermission=schedule`, uninstall the probe from the
disposable emulator, and install the new APK. Start its activity with the
boolean intent extra `denied=true`; the log should report
`exact_access=False` and `denied_schedule_rejected`. Permission state depends
on the emulator and must be checked, not assumed.

## Audio experiment

With the normal exact-alarm manifest installed on an isolated Android 8+
emulator, launch the audio scenario (replace the serial with the isolated
emulator's serial):

```
adb -s emulator-5562 shell am start -n com.example.bedsidealarmschedulerprobe/crc64482c9453999fce20.MainActivity --ez audio true --es focus_mode no_focus
adb -s emulator-5562 logcat -d -s AlarmAudioProbe:I
adb -s emulator-5562 shell am start -n com.example.bedsidealarmschedulerprobe/crc64482c9453999fce20.MainActivity --ez audio true --es focus_mode best_effort
adb -s emulator-5562 logcat -d -s AlarmAudioProbe:I
adb -s emulator-5562 shell am start -n com.example.bedsidealarmschedulerprobe/crc64482c9453999fce20.MainActivity --ez audio true --es focus_mode component
adb -s emulator-5562 logcat -d -s AlarmAudioProbe:I BedsideAlarmPlayer:I
```

The probe schedules an alarm about ten seconds ahead and finishes the activity.
Wait for `service_stopped` before starting the second run. To test process
restart, use `adb -s emulator-5562 shell am kill com.example.bedsidealarmschedulerprobe`
after `scheduled` and before delivery, and verify `pidof` is empty. Do not
force-stop: that changes Android's alarm delivery behavior.

Its receiver starts a `mediaPlayback` foreground service before any focus
request. Both modes use `USAGE_ALARM` and the system's actual default alarm
sound. `no_focus` does not request focus at all; `best_effort` requests focus
and proceeds even if the request fails. Focus callbacks are logged but do not
stop the player. The service samples playback position for about 2.5 seconds
and then stops solely to bound this diagnostic; it is not the eventual
dismiss/snooze behavior. Omitting `focus_mode` selects `best_effort`.
`component` uses the clock's native player component with no focus request.
It verifies that a rejected pre-start check prevents starting, that playback
position advances, and that release is idempotent and prevents another start.
The component's focus policy will be owned by the future foreground service.
The audio scenario uses a fixed registration identity so a subsequent run
replaces a pending run; do not start a new run while the previous one is
delivering. The probe times out and releases its player and
audio focus when it stops. Its timeout runs on the main looper and cannot
interrupt a blocking `SetDataSource` call; this probe is not a production
player or a model for handling potentially slow document providers.

`AUDIO_PROBE_PLAYER_PASS` means the player reported that it was playing and
its position advanced in at least one sample. It does **not** establish that the speaker produced
audible sound, that it started within any particular deadline, or that custom
files and fallback work. A silent or unavailable default sound, zero alarm
volume, routing, Do Not Disturb, and other system conditions remain possible.
The emulator can be run with audio output disabled to avoid disturbing anyone.
On a freshly created emulator, one attempt reported no default alarm sound
shortly after boot; the setting became available later and a retry succeeded.

On an isolated Android 15/API 35 emulator targeting API 36, both modes reached
`AUDIO_PROBE_PLAYER_PASS` after the app process was killed and restarted. In
`best_effort` mode Android returned `Failed` for the focus request, yet the
player reported that it was playing and its position changed. The `no_focus`
mode made no focus request and also reported changing position. Host audio
was disabled; neither result verifies audible output. Focus loss callbacks,
competing apps, calls, Do Not Disturb, and Samsung behavior have not been
tested. Ignoring a focus callback cannot prevent the operating system from
muting, interrupting, or stopping audio.

The `component` mode also passed on an isolated Android 15/API 35 emulator
targeting API 36 after process death. It exercised the actual native player
component, not the clock app's delivery path. Host audio was disabled, so it
did not establish audibility.

In earlier runs, the service diagnostics reported the alarm-clock
foreground-start exemption while denying the while-in-use capability used for
audio control. This is a result for that emulator, not all Android devices.
The normal clock build remains setup-only. An experimental build now connects
a delivery receiver, playback service and player; see the Phase 4 plan for
its validation status and limitations. Neither build should be relied on as
the only alarm.

The probe makes real alarm-clock registrations on the selected emulator.
It does not test actual audibility, recovery, UI integration, or Samsung
device behavior.
