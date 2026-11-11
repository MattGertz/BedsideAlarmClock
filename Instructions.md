# Project Specification: Bedside Alarm Clock

## 1. Project Overview

Build a minimalist Android bedside alarm clock application using .NET MAUI and C#.

The primary target device is a Samsung Galaxy S25 Ultra.

The application has two primary functions:

1. Display a continuously running, attractive, dimmable digital clock.
2. Provide reliable, recurring alarms based on user-selected days of the week.

This is intentionally a simple application. Do not implement complicated alarm-dismissal mechanisms, sleep tracking, weather, calendars, motivational features, or other unnecessary functionality.

Prioritize reliability, simplicity, low power consumption, and a clean user interface.

## 2. Technology and Architecture

- .NET MAUI, C#.
- Android-only initial deployment.
- Use native Android APIs where necessary.
- Use MVVM where it provides meaningful separation of concerns, but avoid unnecessary architectural complexity.
- Persist settings and alarm definitions locally.
- No cloud services, accounts, networking, or external dependencies unless genuinely necessary.
- Use dependency injection for platform-specific services.
- Keep Android-specific functionality isolated behind interfaces so the application UI remains platform-independent where practical.

Before implementation, inspect the installed .NET SDK, MAUI workloads, Android SDK, and project environment. Do not assume specific versions.

## 3. Main Clock Screen

The clock screen is the application's primary interface.

### Appearance

- Full-screen display, portrait orientation.
- Predominantly black background.
- Large digital time display, centered horizontally and vertically.
- Display hours and minutes. Seconds are optional and disabled by default.
- Configurable 12-hour or 24-hour format.
- Display current date and day of the week.
- Optionally display the next scheduled alarm.
- Use a clean, modern sans-serif typeface.
- Digits should be subdued white or light gray rather than maximum brightness white.
- Avoid unnecessary borders, decorations, animations, and visual clutter.

### Screen behavior

- Keep the display awake while the clock screen is active.
- Hide Android navigation and status bars when practical, using immersive sticky mode.
- Handle screen rotation by maintaining a usable layout, although portrait is the primary orientation.
- Restore the clock screen when returning to the application.
- Do not prevent the phone from sleeping when the user leaves the clock application.

### Clock updates

- Update the displayed time once per minute when seconds are disabled.
- If seconds are enabled, update once per second.
- Use a timer or appropriate lifecycle-aware mechanism.
- Correctly handle time changes and timezone changes.

## 4. Brightness Control

Brightness adjustment is a core interaction.

### Gesture

- Swipe upward anywhere on the clock screen to increase brightness.
- Swipe downward anywhere on the clock screen to decrease brightness.
- Brightness changes continuously in response to swipe distance.
- No buttons, sliders, or settings dialogs should be necessary for normal adjustment.

### Implementation

- Use the Android window brightness attribute to adjust brightness while the clock application is active.
- Do not require WRITE_SETTINGS permission or change the user's global system brightness.
- Map the swipe distance to a configurable brightness range.
- Persist the user's preferred brightness.
- Restore the preferred brightness when the application restarts.

### Feedback

- Optionally display a small, temporary brightness indicator while swiping.
- Automatically hide the indicator after the gesture completes.
- Do not leave controls permanently visible.

### Minimum brightness

Provide a very low brightness setting suitable for a dark bedroom.

Investigate whether a small translucent black overlay is useful for achieving additional dimming beyond the minimum practical hardware brightness.

Avoid requiring accessibility permissions.

## 5. OLED Burn-in Protection

The S25 Ultra uses an AMOLED display. Implement automatic pixel shifting to reduce the risk of image retention and uneven pixel wear.

### Requirements

- Move the entire clock content as a single visual group.
- Shift the group approximately every 2 minutes.
- Use small offsets, approximately 2–4 physical pixels horizontally and vertically.
- Cycle through a deterministic sequence of positions, including horizontal, vertical, and diagonal offsets.
- Keep all content within safe display boundaries.
- Use instantaneous transitions rather than animated movement.
- Avoid changing the underlying clock layout or causing visible flicker.
- Reset the position sequence appropriately when the clock screen is recreated.

The pixel-shifting mechanism should operate independently of clock updates.

Test that the movement is actually occurring in physical pixels rather than merely logical MAUI layout units.

Note: Pixel shifting reduces static pixel exposure but does not guarantee prevention of OLED burn-in.

## 6. Alarm Management

Support multiple independent alarms.

### Alarm definition

Each alarm should contain:

- Unique identifier.
- Time of day.
- Enabled/disabled state.
- Days of the week on which it repeats.
- Optional descriptive label.
- Selected alarm sound.
- Alarm volume.
- Optional snooze configuration.

### Days of the week

Provide seven independent day-of-week toggles:

- Sunday
- Monday
- Tuesday
- Wednesday
- Thursday
- Friday
- Saturday

Support arbitrary combinations, including:

- Weekdays.
- Weekends.
- Individual days.
- All seven days.
- Any custom combination.

An alarm with no selected days should be treated as disabled or invalid.

### Alarm list screen

Display all configured alarms in chronological order.

For each alarm show:

- Time.
- Label, if present.
- Selected days.
- Enabled/disabled toggle.

Allow the user to:
- Add an alarm.
- Edit an existing alarm.
- Delete an alarm.
- Enable or disable an alarm.

### Alarm editing screen

Use native Android time selection where appropriate.

Make day selection straightforward, with clearly visible selected and unselected states.

Keep the interface simple and uncluttered.

## 7. Alarm Scheduling and Delivery

This is the most important technical requirement.

Do not implement alarms using application timers or background polling.

Use native Android AlarmManager APIs.

### Scheduling

- Use AlarmManager.SetAlarmClock() or the appropriate exact-alarm API.
- Schedule the next occurrence of each enabled alarm.
- Correctly calculate the next occurrence based on local time and selected weekdays.
- Account for daylight saving time.
- Account for timezone changes.
- Use unique PendingIntent identities for individual alarms.
- Avoid duplicate alarm registrations.
- When an alarm is edited or disabled, cancel and reschedule appropriately.

### Permissions

- Implement the appropriate exact-alarm permission flow for the Android version.
- Explain why the permission is needed.
- Detect whether permission has been granted.
- Provide a route to Android settings if the user needs to grant permission.
- Investigate Android full-screen intent restrictions and implement the appropriate permission handling for an alarm application.

### Alarm delivery

Implement a native Android BroadcastReceiver to receive scheduled alarms.

When an alarm fires:

1. Determine which alarm fired.
2. Present the alarm interface.
3. Start alarm audio.
4. Keep the alarm active until dismissed or snoozed.
5. Schedule the next occurrence of a repeating alarm.

Do not rely on the MAUI application process remaining alive.

### Audio

- Support a small selection of built-in alarm sounds.
- Allow the user to select an alarm sound.
- Allow individual alarm volume settings.
- Use an appropriate audio stream and audio focus behavior.
- Continue alarm audio when the application is not in the foreground.
- Implement a graceful response to audio interruptions.

### Alarm display

When an alarm fires:

- Wake the display where Android permits.
- Show a full-screen alarm interface when permitted.
- Display the alarm time and label.
- Provide a large, obvious DISMISS control.
- Provide an optional SNOOZE control.
- Do not require puzzles, gestures, barcode scanning, or other complicated dismissal mechanisms.

The application should remain usable if Android declines to launch a full-screen intent, falling back to an appropriate high-priority notification.

## 8. Snooze

Snooze is optional and should be configurable.

- Default snooze interval: 10 minutes.
- Allow snooze to be disabled.
- A snoozed alarm should fire again after the configured interval.
- Dismissing an alarm should cancel any pending snooze.
- Snoozing must not alter the underlying recurring alarm schedule.

## 9. Persistence and Recovery

Persist alarm definitions and application preferences locally.

Use a simple, robust storage mechanism. JSON or SQLite are both acceptable; choose the simpler option unless requirements justify SQLite.

### Recovery events

Re-register alarms after:

- Device reboot.
- Application update.
- Timezone change.
- System time change.
- Relevant permission changes.

Implement a BOOT_COMPLETED receiver with the necessary manifest configuration and permissions.

Do not claim that alarms can fire after an Android force-stop: document the operating system's restrictions and require the user to reopen the application if necessary.

## 10. Settings

Keep settings minimal.

Suggested settings:

- 12-hour / 24-hour clock.
- Show/hide date.
- Show/hide next alarm.
- Show/hide seconds.
- Default alarm sound.
- Default alarm volume.
- Default snooze duration.
- Optional automatic nighttime dimming schedule.
- About/version information.

Automatic nighttime dimming should be optional and disabled by default.

## 11. Application Structure

Suggested logical organization:

**Views**
- ClockPage
- AlarmListPage
- AlarmEditPage
- AlarmRingingPage
- SettingsPage

**ViewModels**
- ClockViewModel
- AlarmListViewModel
- AlarmEditViewModel
- SettingsViewModel

**Services**
- ClockService
- BrightnessService
- PixelShiftService
- AlarmService
- AlarmStorageService
- SettingsService

**Platforms/Android**
- AlarmBroadcastReceiver
- BootCompletedReceiver
- AlarmAudioService
- AndroidAlarmScheduler
- AndroidNotificationService
- AndroidPermissionService

The exact organization may be adjusted to suit MAUI conventions.

Avoid unnecessary abstractions and excessive numbers of classes.

## 12. Testing Requirements

Test on the actual Samsung Galaxy S25 Ultra.

### Clock

- Correct time display.
- Correct 12/24-hour formatting.
- Date and weekday display.
- Seconds toggle.
- Display remains awake.
- Brightness responds smoothly to vertical swipes.
- Brightness persists after application restart.
- Pixel shifting occurs at the specified interval.
- Pixel shifting does not cause layout clipping or flickering.

### Alarms

- Single-day alarm.
- Weekday alarm.
- Weekend alarm.
- All-days alarm.
- Arbitrary custom day selection.
- Multiple alarms at different times.
- Multiple alarms with the same time.
- Alarm enable/disable.
- Alarm editing and deletion.
- Snooze and dismissal.
- Times are "local" -- i.e. a 7am alarm fires at 7am in the current time zone, and is not a (for example) UTC time that needs translating.  If the user travels (for example) Seattle to Berlin, an alarm set for 7am back in Seattle will also fire appropriately at 7am in Berlin.

### Android behavior

- Application in foreground.
- Application in background.
- Screen locked.
- Phone in Doze mode.
- Device reboot.
- Timezone change.
- Daylight saving transition.
- Application update.
- Do Not Disturb enabled.
- Exact-alarm permission revoked.
- Full-screen intent unavailable.

Document any Android restrictions that cannot be overcome by an ordinary application.

## 13. Development Approach

Implement incrementally rather than generating the entire application at once.

Recommended phases:

**Phase 1: Foundation**
- Create MAUI project.
- Establish application structure.
- Implement the clock screen.
- Implement time/date formatting.
- Implement full-screen behavior.

**Phase 2: Display**
- Implement swipe brightness.
- Implement brightness persistence.
- Implement OLED pixel shifting.
- Test display behavior on the S25 Ultra.

**Phase 3: Alarm UI and storage**
- Implement alarm definition model.
- Implement alarm list.
- Implement alarm editing.
- Implement day-of-week selection.
- Implement persistence.

**Phase 4: Native Android alarms**
- Implement exact-alarm scheduling.
- Implement BroadcastReceiver.
- Implement notifications and full-screen alarm presentation.
- Implement audio playback and dismissal.
- Implement snooze.

**Phase 5: Reliability**
- Implement reboot recovery.
- Implement time and timezone change handling.
- Test Doze and background operation.
- Test permission handling.

**Phase 6: Polish**
- Refine clock typography and layout.
- Refine brightness response.
- Verify pixel shifting.
- Remove unnecessary UI elements.
- Test all acceptance criteria.

## 14. Instructions to GitHub Copilot

Before writing substantial code:

1. Review this specification.
2. Propose the architecture and project structure.
3. Identify Android API and permission requirements.
4. Identify any Android-version-specific restrictions.
5. Call out potential reliability issues.
6. Present a phased implementation plan.

Then implement Phase 1.

Do not proceed through all phases in one massive code-generation operation. Build, compile, and test incrementally.

Favor simple, maintainable code over elaborate architectural patterns.

The primary objective is a dependable, attractive bedside clock with straightforward alarms—not a feature-heavy commercial alarm application.  One critical technical point worth emphasizing: Android's exact-alarm scheduling is the critical dependency. AlarmManager.setAlarmClock() is specifically intended for user-visible alarm-clock functionality and is a good starting point. To be clear: IT IS CRITICAL THAT THE ALARM FIRE AT THE EXACT TIME AND CRITICAL THAT IT TURN OFF IMMEDIATELY ONCE DISMISSED OR SNOOZED.  Without those criteria being met, the whole project is moot.

This project will eventually be maintained in GitHub, so don't forget to create n appropriate .gitignore file once approval has been given for generating code files.

Because this app will only be used on Android and because functionality for alarms is widly different on Windows, we won't bother compiling for Windows.  Emulation will be used as needed for testing.  The app will ultimately be sideloaded onto the phone.  We'll need to make sure that installation leads to the user approving required permissions.