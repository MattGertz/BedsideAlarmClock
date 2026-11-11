# Alarm model API

`AlarmDefinition.Validate()` throws `InvalidDataException` for invalid data.
IDs must be nonempty and unique in a repository. Times are 00:00–23:59;
labels may be empty but not null. A once alarm has `OnceDate`, no days and
no skip date. A repeating alarm has at least one valid day, no `OnceDate`,
and its optional `SkipDate` must fall on a selected weekday. Changing a
repeating alarm's time or days should clear `SkipDate`.

`AlarmRepository(path)` loads an empty list for a missing file in an existing
directory, and otherwise propagates I/O, JSON and validation errors. It
creates parent directories on save, validates first, flushes a new file in
the same directory and atomically renames it over the destination. The UI
must serialize repository calls and must not mutate definitions during a
save. This is not a multi-process transaction or a power-loss guarantee for
the directory entry.

`AlarmOccurrenceService.GetNextOccurrence(alarm, after, zone)` returns the
first occurrence strictly after the supplied instant, or null if disabled,
expired or not representable. `AlarmOccurrence.Date` is the scheduled local
date, and `Instant` is the resolved local time with offset. A gap advances
to the first valid time on that date; a wholly skipped date has no occurrence.
An ambiguous local time uses the earliest instant, even if `after` lies
between the two possible instants. Repeating alarms honor `SkipDate`.
Calculations use the supplied `TimeZoneInfo` rules and inherit inaccuracies
in the OS/.NET time-zone database, including some historical date-line changes.

For a new once alarm, set `OnceDate` using `FindNextOnceDate(hour, minute,
after, zone)`. It returns the next eligible local date regardless of enabled
state. `SetSkipToNextOccurrence(alarm, after, zone)` sets and returns the
next nominal repeating occurrence's date, ignoring any existing skip and the
enabled state; clear `SkipDate` to undo. No method schedules or fires alarms.
