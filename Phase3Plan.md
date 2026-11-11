# Phase 3: Alarm setup plan

Phase 3 adds alarm configuration and local storage. Alarms cannot ring until
native scheduling and delivery are implemented in Phase 4; the alarm screens
must clearly say so.

## Alarm list

- Add an Alarms entry on the clock screen and list alarms by time of day.
- Show time in the clock's selected 12- or 24-hour format, with no seconds,
  plus the label, recurrence, enabled state, and any skipped occurrence.
- Provide add, edit, enable/disable, and confirmed deletion actions and a
  useful empty state.

## Alarm editor

- Use the native Android time picker, respecting the app's 12/24-hour setting.
- Offer explicit Once and Repeat modes. Once uses the next occurrence of the
  selected local time. Repeat provides seven independent day toggles and
  Weekdays, Weekends, and Every day shortcuts; at least one day is required.
- Allow an optional label, explicit Save, and confirmation before discarding
  unsaved changes. Selected day states must not rely on color alone.
- Add sound, volume, and snooze controls in Phase 4, when they can function.

## Skip next

For repeating alarms, allow skipping the next specific local occurrence and
undoing the skip. Show the exception in the list without disabling recurrence.
Changing the time or days clears the exception with an explanation. Phase 4
must honor the exception during native scheduling and delivery.

## Model, storage, and validation

- Store a stable identifier, local hour and minute, enabled state, alarm type,
  selected days, optional label, one-time local date, and skip exception.
- Persist versioned JSON using safe replacement. Report storage errors instead
  of treating damaged data as an empty list.
- Keep occurrence calculations independent of Android scheduling, and test
  arbitrary weekdays, duplicate times, midnight, timezones, and daylight
  saving transitions. Confirm the daylight-saving policy before delivery.
- A one-time alarm will disable after firing once delivery is implemented.

## Review and completion

1. Implement and test the model, validation, and persistence.
2. Implement the list and editor; verify portrait and landscape, large text,
   accessibility, and persistence across restarts.
3. Review on an emulator, then install on the phone only with user approval.
4. Refine the interface before beginning native alarm delivery.

Phase 3 is complete when definitions can be created, reviewed, edited,
enabled, disabled, deleted, and restored reliably, while the app clearly
communicates that alarms cannot yet ring.
