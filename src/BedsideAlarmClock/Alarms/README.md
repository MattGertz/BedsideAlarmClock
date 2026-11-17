# Alarm model API

**Current integration:** normal builds connect the coordinator, scheduler,
receiver, foreground player, controls, snooze and recovery. See
[Phase 4 progress](../../../Phase4Plan.md#current-progress) for tested
scenarios and known limitations.

The descriptions below document the original groundwork and its contracts.
References to unused or disconnected components describe that earlier stage,
not the current integration.

`AlarmDefinition.Validate()` throws `InvalidDataException` for invalid data.
IDs must be nonempty and unique in a repository. Times are 00:00–23:59;
labels may be empty but not null. A once alarm has `OnceDate`, no days and
no skip dates. A repeating alarm has at least one valid day, no `OnceDate`,
and its skipped dates must fall on selected weekdays. `SkipDate` remains
compatible with older data; `AdditionalSkipDates` holds any further skips.
Changing a repeating alarm's time or days clears all skipped dates.

`AlarmRepository(path)` loads an empty list for a missing file in an existing
directory, and otherwise propagates I/O, JSON and validation errors. It
creates parent directories on save, validates first, flushes a new file in
the same directory and atomically renames it over the destination. Callers
must serialize repository calls and must not mutate definitions during a
save. This is not a multi-process transaction or a power-loss guarantee for
the directory entry.

`AlarmCoordinator` serializes repository operations through a shared instance
and reloads the latest document before each mutation. Replacement and deletion
reject an out-of-date original with `AlarmConflictException`, rather than
overwriting a newer change. Its internal regular-reconciliation entry point
also loads definitions and runs the reconciler under that same gate. All
callers must use the same coordinator; this does not coordinate separate
processes or code that accesses the repositories or reconciler directly.
When constructed with a reconciler, the coordinator saves each add, replace,
or delete and reconciles the saved definitions before releasing the gate.
If reconciliation fails, `AlarmScheduleException` explicitly reports that the
change was saved but scheduling is uncertain; a retry must reconcile the
latest definitions, not repeat the mutation. The UI recognizes this outcome.
The app does not yet configure a reconciler, so edits in the app still do not
schedule alarms. Startup retry and delivery/recovery integration remain to
be implemented before enabling Android scheduling.

The unused `InspectRegularDeliveryAsync` entry point reads both existing files
under the same gate and checks the callback identity, desired state, and
whether the latest enabled definition still includes that exact occurrence.
It distinguishes stale, early, and due-or-late callbacks. Missing or damaged
files propagate an error instead of being treated as an obsolete callback.
This is only a snapshot: it does not claim delivery, reject duplicates, decide
whether a late occurrence was missed, or authorize playback after the gate is
released. Snooze delivery is not inspected.

The unused `RecordRegularDeliveryAsync` rechecks a regular callback and
durably records its first observed time under the same gate. A repeated
callback for that recorded identity reports `AlreadyRecorded`; stale or
early callbacks do not modify the journal. Save failures propagate, including
an uncertain failure after a possible write; retry checks the saved state.
The observation is evidence of a callback, not proof of playback or
authorization to ring. In particular, a late observation is not automatically
an eligible alarm. A future receiver must distinguish a missed occurrence,
handle interruptions after recording, and coordinate edits and playback.

The unused `ReserveRegularPlaybackAsync` records a reservation only after an
observed callback, a check against the latest definition, a trusted clock, and
an eligible timing decision. It obtains the decision time after acquiring the
coordinator gate and loading the files. A known outage or a decision more than
30 seconds after the trigger is reported as missed but is not recorded by
this method. A duplicate reservation reports `AlreadyReserved`. If a write
fails after it may have committed, a retry reads the journal and must treat an
existing reservation as uncertain, not as permission to start again.

A reservation says only that the app intended to attempt playback. It does
not say the audio request happened, succeeded, or became audible. It is not
permission to play: the deadline may pass while saving, the definition may
change after releasing the gate, and the player still needs coordination.
Both the missed-record transition and the planner leave a reservation
unresolved; batch recovery reports it separately. There is deliberately no
resolution or active-player implementation yet. This conservative state can
block future scheduling until a recovery strategy is implemented.

The unused `StartReservedRegularPlaybackAsync` rechecks the definition,
clock status, and 30-second limit while holding the same gate as edits, then
calls a supplied synchronous start action and records `PlaybackStarted` if
the action returns. The caller must prepare the player before invoking it;
the action must not perform slow preparation. Concurrent attempts cannot both
invoke the action. The recorded time is the decision immediately before the
call, not proof of audibility. It disables a matching one-time definition
after recording the start; a repeat invocation retries that update if needed.
If the action throws or either subsequent write fails, playback may still
have started; the exception propagates and the caller must retain and handle
the player rather than assume silence. A failed write may or may not have
committed.

The planner preserves started records and can plan later occurrences; missed
recovery reports them separately instead of calling them missed. There is no
live player, snooze, or crash-recovery integration yet. A durable started
record alone does not establish that playback is still running.

The Android project contains an unused native sound player. It is not yet
connected to this coordinator or a production foreground service. Its
asynchronous player-thread start cannot directly replace the synchronous start
action above: integration must preserve the shared-gate and deadline checks
through the actual player start, without blocking the main thread.

The unused `CompleteStartedRegularPlaybackAsync` records completion and
retains the old identity for cancellation. It retries the one-time disable
if a previous definition write failed, without disabling a definition edited
to a different occurrence. Completion does not stop a player by itself; the
eventual service must coordinate the user action, player and journal, and
handle errors. Completed records are retained; cleanup policy is not yet
implemented.

The unused `SnoozeStartedRegularPlaybackAsync` records a completed regular
playback and a linked snooze due ten minutes later in the same journal write,
then asks the scheduler to register it. The shared gate serializes snooze
with edits, completion and other coordinator operations. A duplicate request
or retry uses the same snooze identity and trigger; the scheduler must make
repeated registration idempotent. Separate active occurrences of one alarm
can have separate snoozes. A failed journal write or scheduler call propagates
an error; the journal may already have changed and a scheduler failure may
have occurred after Android accepted the registration. Retry reads the
existing identity. An overdue snooze is not silently rescheduled into the
past.

An equivalent transition can snooze a started snooze; the source kind and
identity are retained so repeated snoozes and overlapping occurrences remain
distinct. Snooze delivery has disconnected record, reservation, start and
completion methods that share the regular playback checks. A separate
transition can record a snooze as missed while retaining callback evidence.
These methods accept a
linked snooze while its definition exists, including a disabled one-time alarm;
deletion makes a pending snooze stale. This is not yet a complete policy for
edits or disabling alarms, and cancellation of stale snoozes is not wired up.

These transitions do not stop or control a player. A completed source record
is a durable user-action intent, not proof that the snooze was scheduled or
that playback stopped; callers must retain playback and report failures until
the schedule call succeeds. Automatic missed snooze recovery, clock changes, process
death, reboot, player coordination, configurable duration, disabling snooze,
and user-facing controls remain unimplemented. The regular reconciler does
not register, cancel, or recover snoozes automatically.

`AlarmDeliveryTiming.Evaluate` applies the agreed 30-second inclusive limit
to a proposed start decision and treats a known outage at the scheduled time
as missed. Early callbacks or a start decision before the callback are
rejected. The policy does not detect outages: absence of a known outage is
not proof that the device was available. It must be evaluated immediately
before playback and again as necessary after delays. A start decision alone
cannot guarantee when audio actually becomes audible. Wall-clock changes can
also make comparisons unreliable; detecting those changes remains integration
work. The policy does not record missed alarms or start sound.

The unused `RecordMissedRegularAsync` records a missed occurrence under the
coordinator gate after the deadline, or earlier when an outage at the trigger
is known. It retains any observed callback time, the decision time and
reason, and an identity that still needs cancellation. Early, obsolete and
repeated requests are distinguished; file errors propagate. The caller must
provide reliable outage and clock-change evidence. This method is not safe
to connect to playback until the active-alarm state is coordinated as well.

The unused `RecoverUnobservedRegularAsync` scans the existing journal under
the coordinator gate and records eligible, unobserved regular misses in one
write. It returns newly recorded identities and unresolved identities with
reasons: these include observed deliveries, playback reservations, due entries
inside the grace period, and stale due entries. With an uncertain clock it
returns every desired regular entry as unresolved, including future-looking
ones, without recording misses. A
trusted clock requires the caller to establish that wall-clock changes do not
invalidate the comparison with each trigger; merely reading the current clock
does not establish this. Known-outage evidence is supplied per registration;
an absent process is not evidence. Observed entries remain unresolved even
when an outage is supplied because playback state is unknown. A failed write
propagates; after an ambiguous failure retry reads the journal, where a
previously saved miss remains available for reporting. This pass does not
cancel Android registrations, resolve snoozes, or run automatically.

The Android scheduler is not wired into the coordinator or alarm UI yet.
Its registration IDs distinguish callbacks for changed occurrences, but the
future coordinator must persist and validate those IDs and reconcile
registrations and stored state. Canceling a registration is not proof that
an already-delivered callback cannot arrive.

`AlarmRegistrationRepository` is unused groundwork for a separate, versioned
registration journal. A desired entry describes what should be scheduled,
not proof that Android accepted it. A pending-cancellation entry retains an
obsolete identity until cancellation succeeds. At most one desired regular
entry is allowed per alarm; separate desired snoozes may coexist. A linked
snooze requires a unique completed source in the journal. Old
identities can coexist with new ones.
An observed regular or linked snooze delivery records its first callback time
until it can be resolved.
Missed regular occurrences remain in the journal for future reporting even
after their Android cancellation completes. A missed record can coexist with
the next desired occurrence.
Changed occurrences require new registration IDs. The repository validates
the whole document and uses the same replacement mechanism as alarm storage.
Callers must serialize access and not mutate entries during a save.

The journal and alarm definitions are separate files, and neither is a
transaction with Android. A missing journal loads as empty, so distinguishing
first use from unexpected loss needs consideration during integration.

`AlarmRegistrationPlanner.PlanRegularRegistrations` produces a new journal
snapshot from alarm definitions, the existing journal, a supplied instant and
time zone. It retains an unchanged occurrence's identity; otherwise it marks
the old identity pending cancellation and creates a new desired identity if
there is another occurrence. Existing pending cancellations and snoozes are
preserved. The planner makes no file or Android changes, and callers must
serialize planning with all other journal and definition operations.
It throws `OverdueAlarmRegistrationException` for a due or overdue desired
regular registration, even if its definition was changed or removed: a
future delivery/recovery path must resolve whether it fired or was missed
before planning can proceed. It also stops on a recorded but unresolved
regular delivery or playback reservation. Once a missed occurrence is
recorded, it can plan the next repeating occurrence without deleting the
missed record. It does not detect
missed occurrences for which no registration was recorded.

`AlarmRegistrationReconciler` is disconnected groundwork for applying regular
registration plans. It writes the proposed journal before scheduler calls,
registers desired occurrences, then cancels obsolete registrations. It
removes obsolete cancellation records and clears missed-record cancellation
flags only after all cancellations succeed. Missed records remain. Failures
propagate; a retry uses the recorded identities and repeats operations, so the
scheduler must support idempotent scheduling and cancellation. Snoozes are
not applied by this reconciler. The clock can advance between planning and
scheduling; an occurrence that becomes due must still be handled by the future
delivery/recovery path.

The reconciler does not serialize itself with other operations. The coordinator
entry point protects its read and reconciliation against coordinator mutations,
but future callbacks and all journal access must also participate in the same
gate. Integrated missed detection and user-facing reporting, active delivery,
journal-loss recovery, automatic startup retry, and coordinated recovery are not implemented; Android
scheduling must not be enabled in the app until the necessary delivery and
recovery paths are addressed.

`AlarmOccurrenceService.GetNextOccurrence(alarm, after, zone)` returns the
first occurrence strictly after the supplied instant, or null if disabled,
expired or not representable. `AlarmOccurrence.Date` is the scheduled local
date, and `Instant` is the resolved local time with offset. A gap advances
to the first valid time on that date; a wholly skipped date has no occurrence.
An ambiguous local time uses the earliest instant, even if `after` lies
between the two possible instants. Repeating alarms honor all skipped dates.
Calculations use the supplied `TimeZoneInfo` rules and inherit inaccuracies
in the OS/.NET time-zone database, including some historical date-line changes.

For a new once alarm, set `OnceDate` using `FindNextOnceDate(hour, minute,
after, zone)`. It returns the next eligible local date regardless of enabled
state. `SetSkipToNextOccurrence(alarm, after, zone)` sets and returns the
next nominal repeating occurrence's date, ignoring existing skips and the
enabled state. The alarm list can undo the earliest pending skip. No method
in the occurrence service schedules or fires alarms.
