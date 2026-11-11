# Bedside Alarm Clock: Assessment and Architecture

## Purpose and constraints

This project is a minimalist Android bedside clock and recurring alarm app for a Samsung Galaxy S25 Ultra. Its priorities are dependable alarm delivery, a dimmable clock display, low power use, and a straightforward interface. It is intentionally not a general-purpose lifestyle app.

The app is Android-only, written in C# with .NET MAUI, and intended for sideloading and emulator/device testing. It should not depend on cloud services, accounts, networking, or unnecessary third-party packages. Android-specific behavior should stay in the Android platform layer, behind small interfaces where that keeps app code testable and understandable.

## Architecture proposal

Use one Android-only MAUI app, one small platform-independent Core library for alarm rules, and a focused Core test project. This gives recurrence calculations a deterministic test surface without introducing a separate project for every layer.

### Proposed project layout

```text
BedsideAlarmClock.sln
src/
  BedsideAlarmClock.Core/
    Alarms/
      AlarmDefinition
      WeekdayMask
      AlarmOccurrenceCalculator
      AlarmSchedule
    Settings/
      ClockPreferences
    BedsideAlarmClock.Core.csproj

  BedsideAlarmClock/
    App.xaml
    AppShell.xaml
    MauiProgram.cs
    Features/
      Clock/
        ClockPage
        ClockViewModel
      Alarms/
        AlarmListPage
        AlarmEditPage
        AlarmRingingPage
        AlarmListViewModel
        AlarmEditViewModel
      Settings/
        SettingsPage
        SettingsViewModel
    Services/
      AlarmCoordinator
      AlarmRepository
      SettingsService
      ClockDisplayService
    Platforms/
      Android/
        AlarmBroadcastReceiver
        BootCompletedReceiver
        AlarmActionReceiver
        AlarmAudioForegroundService
        AndroidAlarmScheduler
        AndroidNotificationService
        AndroidPermissionService
        MainActivity
    Resources/
      Raw/
        Alarms/

tests/
  BedsideAlarmClock.Core.Tests/
    AlarmOccurrenceCalculatorTests
    WeekdayMaskTests
```

The exact file layout may follow MAUI conventions as implementation proceeds. The boundaries matter more than the names:

- **Core** contains alarm definitions, weekday selection, validation, and local next-occurrence calculation. It has no Android or MAUI dependencies.
- **MAUI features** provide the clock, alarm list/edit, ringing, and settings UI. Use MVVM where it meaningfully separates view state and behavior; avoid framework layers without a concrete need.
- **AlarmCoordinator** coordinates repository changes and Android scheduling so that edits, deletes, disablement, snooze, and dismissal keep stored and scheduled state consistent.
- **AndroidAlarmScheduler** owns `AlarmManager.SetAlarmClock()`, unique `PendingIntent` identities, cancellation, and scheduling the next occurrence.
- **Android receivers and audio service** handle scheduled events and notification actions. Alarm playback must not rely on the MAUI UI process being alive. The service owns playback; the UI presents the ringing controls.
- **Persistence** starts with local JSON because the data set is small. Keep alarm definitions and preferences simple, write safely, and version the persisted format for future evolution. Move to SQLite only if requirements justify it.

## Android-specific requirements and constraints

### Exact alarm access

Exact alarm scheduling is the central technical dependency. `AlarmManager.SetAlarmClock()` is appropriate for user-visible alarm-clock events, but scheduling must first verify that the app is allowed to schedule exact alarms and must surface a clear recovery path if access is absent or revoked.

Android 14 documentation states that `SCHEDULE_EXACT_ALARM` is not pre-granted to most newly installed apps targeting Android 13 or later. Alarm-clock apps may use `USE_EXACT_ALARM`, a normal permission, when they satisfy the applicable alarm/calendar use case and distribution policy. The final permission choice must be validated against the app's target SDK and intended sideload/distribution path; do not blindly declare both permissions.

The app should respond to permission changes, avoid assuming a scheduling call succeeded, and make the unscheduled state visible to the user. If exact access is not available, do not silently substitute an inexact timer for an alarm whose requirement is to fire on time.

Reference: [Schedule exact alarms](https://developer.android.com/about/versions/14/changes/schedule-exact-alarms).

### Full-screen alarm presentation and notifications

Full-screen intent access and launch behavior are controlled by Android and vary by platform version and device state. Check/request access when appropriate, but treat the full-screen activity as best effort. Provide a high-priority alarm notification with clear dismiss and snooze actions as the fallback.

Reference: [Android 14 behavior changes](https://developer.android.com/about/versions/14/behavior-changes-14#secure-fsi).

### Audio and foreground service

Alarm audio must continue when the app UI is not foregrounded. Use Android alarm audio attributes/focus behavior and an Android foreground service started for the ringing interval, with the foreground-service type and permissions appropriate for the selected target SDK. The service must stop promptly on dismiss/snooze and handle audio interruptions. Test background-start behavior on supported Android versions rather than assuming a service can always be launched.

Reference: [Foreground services](https://developer.android.com/develop/background-work/services/fgs).

### Recovery and operating-system limits

Re-register alarms after reboot, app update, local-time changes, timezone changes, and relevant exact-alarm permission changes. A boot receiver requires manifest declarations and the relevant permissions. Do not promise alarms after an Android force-stop: the app must be reopened to resume normal scheduling. OEM battery policies, Do Not Disturb, user permission revocation, and device audio settings can still affect delivery or audibility; explain relevant limitations in-app or in project documentation.

### Local wall-clock recurrence and daylight saving

Store recurring alarms as local wall-clock time plus selected weekdays, not as a fixed UTC instant. Calculate the next occurrence in the device's current timezone and recalculate after timezone or system-time changes. This preserves a 7 a.m. alarm as 7 a.m. when the user travels.

The specification does not define the edge-case policy for daylight-saving transitions. A recommended starting policy is to fire at the first valid local time after a spring-forward gap and once at the first occurrence in a repeated fall-back hour. This should be confirmed before recurrence behavior is implemented and covered by tests.

### Display brightness and OLED shifting

Adjust brightness through the current Android window rather than global system brightness, avoiding `WRITE_SETTINGS`. Keep the preferred value in local settings. If hardware minimum brightness is insufficient, investigate a translucent black overlay without accessibility permissions.

Keep all clock content inside one translated group for pixel shifting. MAUI layout coordinates are density-scaled; verify that an Android implementation moves actual physical pixels and stays within safe display bounds on the S25 Ultra. Small deterministic, instantaneous offsets should run independently of clock refreshes. Pixel shifting reduces static exposure but cannot guarantee burn-in prevention.

## Reliability risks and mitigations

| Risk | Mitigation |
|---|---|
| Exact-alarm permission missing or revoked | Check before scheduling; clearly explain/request the appropriate access; report scheduling failures instead of silently degrading. |
| Duplicate or stale alarms after edits | Stable per-alarm PendingIntent identity; cancel before replacement; have one coordinator own persistence/scheduling transitions. |
| App process absent at trigger time | Native receiver and Android audio service; do not depend on MAUI timers or a live UI process. |
| Full-screen intent unavailable | High-priority notification with actionable dismiss/snooze controls. |
| Reboot, time, or timezone change | Native recovery receivers trigger schedule reconstruction from persisted local definitions. |
| DST gap/fold ambiguity | Agree on an explicit local-time policy and test it with deterministic timezone cases. |
| Dismissal or snooze leaves audio active | Make the native service the playback owner and handle notification actions through native receivers; verify stop behavior on-device. |
| OEM power management / Doze / DND | Use exact alarm APIs and the alarm audio path; test real-device behavior and document limits the app cannot override. |
| Screen units differ from physical pixels | Convert units deliberately and measure pixel-shift behavior on target hardware. |

No ordinary app can guarantee alarm audibility or presentation against every user setting and OEM policy. The app can, however, make exact scheduling the primary path, make permission/state failures explicit, and provide robust native delivery and fallback UI.

## Development phases

1. **Foundation** — inspect the installed .NET SDK, MAUI workload, and Android SDK; create the Android-only MAUI project; implement the clock screen, date/time formatting, and full-screen behavior.
2. **Display** — implement vertical swipe brightness, local preference persistence, OLED pixel shifting, and S25 Ultra display validation.
3. **Alarm UI and storage** — implement alarm models, recurrence rule tests, alarm list/edit screens, weekday selection, and local persistence.
4. **Native alarm delivery** — implement exact-alarm permission handling and scheduling, receivers, notifications/full-screen fallback, audio, dismissal, and snooze.
5. **Reliability** — implement reboot/time/timezone/permission recovery and test Doze, background, lock-screen, DND, and failure cases.
6. **Polish** — refine typography and gesture response, verify pixel-shift movement and layout, remove unnecessary UI, and run acceptance tests on the target device.

Implement and validate each phase incrementally. Do not generate the entire app in one operation.

## Phase 1 environment assessment

Environment inspection on 2026-11-11 found:

- Windows 10, x64.
- .NET SDK 10.0.303, MSBuild 18.6.14, with .NET 6, 9, and 10 SDKs installed.
- Android workload installed (manifest 36.1.69); the `dotnet new maui` template supports `net10.0`.
- Android SDK configured at `C:\Users\Matthew\AppData\Local\Android\Sdk`.
- Android platforms 34, 35, and 36 and build-tools 34.0.0, 35.0.0, and 36.0.0 are installed.
- Android Debug Bridge 37.0.0 and Java 17 are available.
- The project folder initially contained only `Instructions.md`; it was not initialized as a Git repository.

Phase 1 will target `net10.0-android` only. Windows compilation is intentionally excluded, matching the project specification. Build/restore will verify whether the installed SDK components are sufficient for the generated MAUI Android project.

## Phase 1 implementation status

- Created `BedsideAlarmClock.slnx` and an Android-only MAUI app targeting `net10.0-android`.
- Replaced the template landing screen with a black, centered clock displaying subdued time and the localized weekday/date. Added persisted 12/24-hour, seconds, and date settings, defaulting to on, and a clock-font choice of sans serif, serif, or monospace (sans serif by default).
- The clock uses a repeating one-second UI timer while visible, reads the current local time on each tick, and only updates displayed text when it changes. It refreshes immediately when the page appears and stops when the page is no longer visible.
- The clock measures `10:00:00 AM` using the Android text rendering font and reduces the time font size as needed when the available width changes, reserving 48 physical pixels on each side for pixel shifting.
- Android keeps the display awake only while the clock page is active in the resumed activity. The activity hides system bars with transient swipe reveal on Android 11+ and uses immersive sticky flags on older supported versions.
- Removed unused template network permissions and added a root `.gitignore` for .NET, IDE, test, and Android signing artifacts.
- Verified a successful Android solution build with `dotnet build BedsideAlarmClock.slnx --no-restore`. No emulator or device was connected during this phase, so visual and lifecycle testing remains necessary.

## Phase 2 display status

- Brightness is adjusted by vertical swipe using the current Android window and a fixed range. The user verified the range, both swipe directions and orientations, and persistence across sessions on the S25 Ultra.
- The clock and date move together in a deterministic sequence every two minutes. The shift is 48 physical pixels from center in each direction, per user preference; 48 pixels of travel space are reserved around the content. The user verified shifting with a temporary 15-second interval on the S25 Ultra, and the interval was restored to two minutes.
- The clock's serif and monospace options use bundled Noto Serif and Noto Sans Mono fonts to avoid device-specific system font mappings. The fonts are distributed under the SIL Open Font License, included alongside the font files.
- Pixel shifting reduces static exposure but cannot guarantee prevention of OLED burn-in. No additional dimming overlay is implemented.
- The user verified that brightness returns to normal outside the clock and that the minimum is satisfactory in a dark room.
- Notifications and other activities may temporarily cover or pause the clock; returning refreshes the time and restores clock-only brightness and keep-awake behavior. The clock does not automatically reopen after a device reboot or update. Alarm scheduling and reboot recovery are future phases and are not yet implemented.
- On an isolated emulator, the notification shade and a separate Settings activity were opened and dismissed; after return, the clock advanced, its window brightness and keep-awake flag were restored, and a subsequent two-minute pixel shift moved 48 physical pixels. Real phone calls, overnight operation, and reboot recovery have not been verified by this test.
