# Phase 4: Native alarm delivery plan

Phase 4 added Android scheduling, alarm audio, presentation, dismissal, and
snooze. The owner has completed field testing and enabled delivery in normal
builds. The remaining notes include historical development decisions and
known limitations.

## Agreed behavior

- Use Android's exact alarm-clock scheduling rather than a MAUI timer.
- A repeating alarm schedules its next eligible local occurrence, honoring
  skip-next and its undo. A one-time alarm disables after it fires.
- In a daylight-saving spring gap, use the first valid time on the scheduled
  date. In a fall overlap, ring only at the first occurrence of the local time.
- If an occurrence is missed while the device is unavailable, do not ring it
  late. Report it as missed and schedule the next applicable occurrence.
- Allow sound to begin at most 30 seconds after the scheduled instant,
  inclusive. A known powered-off or reboot outage covering the instant is
  missed regardless of the delay. A stopped app process alone is not an
  outage. Checking a start request cannot guarantee when audio actually
  becomes audible; the playback implementation must investigate that gap.
- Present overlapping active alarms together, with separate dismiss and
  snooze controls on the clock while it is visible; retain notification and
  full-screen controls when the clock is not visible.
- Snooze defaults to ten minutes, can be disabled, and does not change the
  underlying recurring schedule.
- The clock shows the day and time of the next alarm, including a confirmed
  snooze, or "No alarm set".
- Touching the clock reveals a red SKIP button for three seconds when an
  upcoming delivery exists. It skips
  only the next upcoming delivery: an occurrence of a repeating alarm, a
  one-time alarm, or a pending snooze. Repeated presses can skip additional
  occurrences. The button is unavailable while an alarm is being delivered.
- Offer system sounds and a user-selected WAV or MP3 file on the phone.
  If the chosen sound cannot play, promptly use a system alarm sound and
  report the problem. A system default can itself be unavailable or silent,
  so this is not an absolute audibility guarantee.
- Investigate ringing after a reboot but before the first unlock. The user
  considers this useful but rare and is willing to forgo it if it makes the
  implementation disproportionately complicated. Do not claim support until
  the complete path is verified.

## Android design and questions

- `AlarmManager.SetAlarmClock` is the primary scheduling candidate; verify
  exact-alarm permission declarations and behavior on supported Android
  versions and the sideloaded S25 Ultra. Report unavailable exact access;
  do not silently substitute an inexact alarm.
- Use stable, distinct `PendingIntent` identities for each alarm and its
  separate snooze; intent extras alone do not distinguish identities.
- A native receiver handles delivery, and a foreground service owns playback.
  Validate service type, background-start eligibility, wakefulness, audio
  focus, and immediate stop on both the emulator and phone.
- Use alarm audio attributes and request focus on a best-effort basis: a
  denial or focus-loss callback must not by itself stop the alarm. Continue
  until the user dismisses or snoozes it, subject to system interruptions and
  failures; do not change global volume. This policy is implemented only in
  the experimental delivery build.
- Request and check notification and full-screen access as applicable.
  A high-priority notification with actions is the fallback when full-screen
  presentation is unavailable, but it may not be visible if notifications or
  its channel are blocked. Clearly report unavailable controls.
- Coordinate stored definitions, Android registrations, active alarms, and
  snoozes. Handle races, stale callbacks, duplicate actions, failures between
  persistence and scheduling, and explicit reconciliation; JSON writes and
  Android registrations are not a single transaction.
- Use the Android system file picker for a user-selected sound and retain
  the necessary access. A document can be moved, deleted, unavailable,
  unsupported, or supplied by a remote provider; do not make timely playback
  depend on a network. Test actual playback and the fallback, not just file
  extensions.
- Reconstruct schedules after reboot, app update, clock and timezone changes,
  and relevant exact-alarm permission changes. A device that is powered off
  cannot ring; Android force-stop requires reopening the app.
- Before-first-unlock support requires direct-boot-aware components and
  device-encrypted storage. Existing alarm data is in ordinary app storage.
  Verify startup, schedule reconstruction, sound, and controls before making
  a commitment; selected files may not be accessible before unlock.
- Test Doze, DND, alarm volume, audio routing, notifications, calls, Samsung
  power policies, process death, and multiple active alarms. Android scheduling
  cannot guarantee audible playback or control presentation under every device
  setting or failure.

## Checkpoints

1. Investigate platform constraints and agree on behavior.
2. Implement and test scheduling and state coordination, without suggesting
   that the app can ring before audio delivery is ready.
3. Implement sound, presentation, dismiss and snooze; review the interface.
4. Implement essential recovery and resolve pre-unlock support.
5. Exercise failure, interruption, reboot and long-running scenarios in an
   isolated emulator and, with permission, on the S25 Ultra.

Pause between checkpoints. Essential recovery and device validation are
prerequisites for trusting this app as a bedside alarm.

## Current progress

The delivery path is enabled in normal builds. The app connects save-time
scheduling, a receiver, a media-playback
foreground service, alarm controls, snooze, sound selection and recovery.
It requests audio focus on a best-effort basis, uses alarm audio attributes,
and does not change system volume. The owner reports successful field testing,
including recurring alarms.

On an isolated Android 15/API 35 emulator targeting API 36, regular and
snoozed deliveries, process death, locked-screen controls, reboot and
timezone reconstruction were exercised. After a forward clock change, the
occurrence was reported as uncertain, a stale callback was ignored, and
the next regular occurrence was registered. A release build showed advancing
player position and a working snooze action. Host audio was disabled, so none
of these results proves audibility.

The app checks exact-alarm, notification and full-screen access
in Settings and provides an in-app route to active controls. On the isolated
emulator, that route opened the controls and dismissed a sounding alarm with
notification permission revoked. A regular alarm and a snooze also overlapped
and were dismissed independently. It tries the
system alarm when a selected sound cannot be prepared or fails during
playback; the runtime-failure path still needs device-level testing.

Recovery now defers anchor updates while any delivery is being prepared and
retries deferred work after preparation finishes, and schedules a retry to
help recover if the process dies. Tests cover overlapping preparations, a clock change during
a reservation, an already-playing alarm, and interrupted reservation recovery.
On the isolated emulator, an alarm kept playing and could be dismissed after a
backward clock change. The precise preparation race has not been reproduced
on an Android device. A second clock change during recovery remains an accepted,
documented edge case rather than a prerequisite for phone testing.

Snooze now leaves playback and controls active until scheduling is confirmed.
On failure, the controls show an error and offer retry or dismissal. An
unconfirmed snooze is recorded separately from a confirmed snooze: its callback
is rejected, it is not automatically rescheduled, and cancellation is attempted
and retried during reconciliation. A user retry can schedule it again, using a
fresh interval if the original time has passed. Dismissal also cancels the
linked snooze; if dismissal cannot be recorded, it remains unconfirmed and must
be retried. On the isolated emulator, a backward clock
change caused snooze to fail while the player remained active; after restoring
the clock, retry registered the snooze and stopped playback. This also exercised
the error message with notification permission denied. That emulator test
predated the unconfirmed-state change; the new failure paths are unit-tested.

If the journal reports an error while confirming a snooze and also cannot be
read back, the app cannot determine whether confirmation was committed. It
tries to revert confirmation and cancel the Android registration, and reports
the uncertainty. If the journal cannot be corrected, a committed confirmation
could still be reconciled or delivered later. Failure handling has been
tested with simulated failures, not on a phone.

For practical phone testing, prioritize actual audibility and timely start;
prompt dismiss and snooze; a snooze ringing at its requested interval; locked
and background delivery; sound selection and fallback; and reboot, timezone,
Doze, DND and Samsung power behavior. Also verify multiple alarms and disabled
snooze. Keep a separate backup alarm.

The tests enforce the agreed 30-second cutoff: a later alarm is reported as
missed rather than rung. If the process dies after delivery was observed,
recovery reports an interrupted occurrence rather than restarting that same
occurrence; this includes a crash while preparing sound. Those are current
behavioral tradeoffs, not proof that the alarm remained audible until dismissed.
The rare clock and storage failures above and cleanup of abandoned imported
sound files should not become an open-ended pre-phone investigation.
Before-first-unlock delivery is not supported or verified.

### Historical groundwork

The entries below describe earlier stages of implementation, not the current
app. References to unused or disconnected components and
missing features refer to those earlier stages.

- Checkpoint 1: the decisions above are recorded. Direct Boot and actual
  device-specific behavior remain to be tested.
- Checkpoint 2 has started: alarm UI persistence now goes through a shared
  coordinator that serializes operations and detects stale edits.
- An Android scheduler can create and cancel exact alarm-clock registrations,
  but is not connected to the UI or a production receiver. It does not yet
  coordinate registration and persistence; the app still schedules no alarms.
  Registrations have separate identities for each alarm, kind, and registration
  ID. A changed occurrence must get a new registration ID, and the eventual
  receiver must check it against persisted state before acting.
- A separate, currently unused registration journal can persist desired
  registrations and obsolete identities pending cancellation. Desired does
  not mean Android accepted the registration. The journal and alarm
  definitions are separate files; neither is atomic with Android. Loss of
  the journal must be addressed before integration.
- An unused regular-registration planner computes a proposed journal from
  the definitions and existing registrations. It preserves unchanged
  identities, retains obsolete ones for cancellation, and creates new IDs
  for changed occurrences. Due or overdue registrations stop planning until
  delivery or missed-alarm handling can resolve them. It does not write the
  journal or call Android, and leaves snooze entries untouched.
- A disconnected reconciler persists the plan before scheduler calls, then
  schedules desired regular occurrences and cancels obsolete identities.
  Cancellation records survive failures until cleanup succeeds, and errors
  propagate for retry. This is not a transaction: integration still requires
  one gate covering definitions, journal and delivery, stale-callback checks,
  missed-alarm handling, and a policy for journal loss.
- The coordinator can now reconcile each saved add, edit or deletion under
  the same in-process gate. A scheduling failure is reported separately from
  a save failure, because the definition has already been saved and Android
  state may be uncertain. Tests exercise changes, skips, failures and retry
  with a fake scheduler. The production app does not configure this path:
  scheduling alarms without a delivery receiver would create alarm clocks
  that cannot ring. Startup retry, actual Android wiring, delivery and
  recovery integration remain outstanding.
- An unused regular-callback inspection checks identity, desired state,
  current definition and timing while holding the same in-process gate.
  Missing files produce an error. This inspection is a snapshot, not a
  durable claim; late-versus-missed decisions, snoozes and playback remain
  unimplemented.
- A regular callback can now be recorded once in the unused journal, with
  its observation time. Duplicate callbacks are recognized after reloading.
  An observed callback is not proof of playback and does not decide whether a
  late occurrence was missed. Planning stops until observed deliveries can be
  resolved; that resolution and snooze handling are not implemented.
- An unused timing policy applies the agreed 30-second limit and known-outage
  override to a proposed playback start. It does not detect outages, start
  audio, record missed alarms, or establish when sound becomes audible.
- An unused missed-record transition saves the decision, reason and any
  callback evidence. The reconciler can cancel its old identity while
  retaining the report and planning the next repeating occurrence. Automatic
  detection, reporting UI, active playback coordination and reliable outage
  evidence remain unimplemented.
- An unused playback reservation persists the intent to attempt playback
  after a callback, definition and timing checks. It is not evidence that
  sound started or permission to play. Duplicate reservations are recognized,
  and both missed detection and planning leave them unresolved. After a
  crash, whether a request happened remains unknown; resolving this state
  and coordinating the actual player are still required.
- A disconnected playback-start transition rechecks the definition and
  deadline under the coordinator gate, invokes a caller-supplied prepared
  player start, and records the successful return. It serializes duplicate
  attempts with edits, but cannot prove sound was audible. A start or journal
  error can leave player and journal state uncertain. Matching one-time alarms
  are disabled after the start is recorded, and started entries no longer
  prevent planning a later regular occurrence. A disconnected completion
  transition retains the old identity for cancellation; it does not stop a
  player or implement snooze or recovery.
- A disconnected snooze transition records a ten-minute follow-up alongside
  completion of its regular source, then registers it through the scheduler.
  Retries reuse the same identity, including after an uncertain write or
  scheduling error; overlapping occurrences can retain separate snoozes.
  Disconnected snooze callback, playback-start, completion and repeated-snooze
  transitions now share the regular timing checks. This does not start or stop
  a player, automatically receive callbacks, automatically recover
  overdue or rebooted snoozes, provide a snooze setting, or run in the app.
- A disconnected recovery pass can record multiple unobserved, due regular
  occurrences as missed in one journal write. It leaves observed callbacks,
  stale registrations and occurrences still within the 30-second window
  unresolved. With an uncertain clock it leaves all desired registrations
  unresolved, even future-looking ones. It requires the caller to supply
  trustworthy clock and per-registration outage evidence; neither is
  currently available automatically. It does not cancel registrations,
  report misses to users, handle snoozes, or run in the app.
- The standalone audio probe starts a `mediaPlayback` foreground service from
  an exact-alarm callback and plays the system default alarm with alarm audio
  attributes. On an Android 15/API 35 emulator targeting API 36, after the
  process was killed and restarted, a run without requesting focus and a run
  in which focus was denied both reported a playing player with changing
  position. The probe stops after a short sample and is not production
  dismiss/snooze behavior. Earlier service diagnostics showed the alarm-clock
  start exemption but denied the while-in-use audio-control capability. Host
  audio was disabled, so audibility was not established. Focus-loss callbacks,
  competing audio, calls, DND, Samsung behavior, custom files, fallback, and
  playback recovery remain untested.
- An unused Android native player component prepares the system alarm sound
  on its own handler thread and supports a pre-start check and asynchronous
  release. A separate probe exercised that component after process death;
  playback position advanced, but host audio was disabled. This component is
  not yet owned by a production foreground service or coordinated with the
  alarm journal. It does not request focus, select custom sounds, implement
  fallback, or provide controls. A blocking sound provider can still delay
  preparation and release on its worker thread; timeout and fallback handling
  require integration work.
- On an isolated Android 15/API 35 emulator targeting API 36, a standalone
  probe verified exact-alarm access with `USE_EXACT_ALARM`, rejection when
  `SCHEDULE_EXACT_ALARM` was denied, distinct registrations and snoozes,
  cancellation, replacement, and delivery after the probe process was killed.
  An attempt to enter deep Doze did not succeed in time; Doze remains
  unverified. These tests did not verify audio, recovery, app integration,
  or the S25 Ultra.

## References

- [Android alarms](https://developer.android.com/develop/background-work/services/alarms)
- [Direct Boot](https://developer.android.com/privacy-and-security/direct-boot)
- [Storage Access Framework](https://developer.android.com/training/data-storage/shared/documents-files)
- [Supported audio formats](https://developer.android.com/media/platform/supported-formats)
- [Notification permission](https://developer.android.com/develop/ui/compose/notifications/notification-permission)
- [Full-screen intent limits](https://source.android.com/docs/core/permissions/fsi-limits)
- [Audio focus](https://developer.android.com/media/optimize/audio-focus)
- [Foreground service background-start exemptions](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start)
- [Foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types)
- [Android 15 audio-focus capability check in AOSP](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android15-release/services/core/java/com/android/server/appop/AppOpsUidStateTrackerImpl.java)
- [Android 15 foreground-service capabilities in AOSP](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/android15-release/services/core/java/com/android/server/am/OomAdjuster.java)
