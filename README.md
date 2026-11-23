# Bedside Alarm Clock

An Android bedside clock and alarm app built with .NET MAUI and C#. It is
designed for a simple, dimmable clock display and local alarm management.

Alarm delivery is enabled in standard builds. Device behavior can vary with
Android settings and power management; check alarm permissions in Settings.

## Owner's note
The most recent versions of Android rendered my old night-time alarm clock obsolete.  In checking Google Play for a replacement, I became frustrated with the alarm clocks available.  All of the free clocks had advertisements, of course.  For the paid clocks. There were clocks without alarms.  There where alarms without clocks. Those sets didn't intersect.  Furthermore, all of the alarms catered to people who have trouble waking up, making it difficult to turn the alarm off even on the most easiest setting.  Most alarms did not work well when using a separate night clock, relying on pop-up notifications for dismissal.  I am not a heavy sleeper; I am a very light sleeper, and just wanted something simple that showed a clock, allowed one to set multiple alarms, and that presented a big button to turn the alarm off (or snooze in the rare case where I want to do that). The resulting clock also allows a person to easily skip an alarm if they are already up.  It also allows the user to easily dim or brighten the clock display with a slide of the finger.

This app is (mostly) a "vibed" app using GitHub Copilot, though all of the code has been reviewed by me and in some cases augmented by me.  There are many unit tests and static analysis has been run on it multiple times to check for failure modes.  It has been tested by me "in the wild" in a large number of situations, albeit only on a Samsung Galaxy 25 Ultra, and I use it myself daily.  Although this is a MAUI app and in theory supports multiple platforms, I have only created an Android output for it.  Creating a Windows output did not make a lot of sense to me, as there are no Windows OS phones anymore and a tablet would be overkill for this app, and I have neither facility nor need to test an Apple output of it.  Both of those iterations would need a lot of additional work, as alarms touch the core of an operating system and each of those OSs is wildly different from the others. The reader is welcome to fork the code and do those, of course.

Note that I will not be adding new features to this clock; I am not at all interested in adding alarm challenges or whatever.  I will be fixing bugs and updating it whenever new versions of Android require me to do so.  I will gratefully take bug reports and even more gratefully take bug fixes to the Android version as it stands.  Please note that standard Android emulators (the ones that come with the Android SDK) do not seem to work well for testing the project; mouse-clicks-as-finger-touches fail more often than they succeed.  This problem seems to be related to making it protected from screen locking, as it worked fine in emulation up to that point of the development.

## Features

- Full-screen clock with configurable font and color, 12- or 24-hour time,
  optional seconds, date and next alarm, and portrait and landscape layouts.
- Swipe vertically to adjust the app's brightness without changing system
  brightness. The clock stays awake while its screen is active.
- Smooth 750 ms pixel shifts follow two-minute stationary intervals and cycle
  through four positions, reducing static display exposure. Stroke widths and
  overlapping glyphs vary with the font, size, and text; neither pixel
  relaxation time nor protection against OLED burn-in is guaranteed.
- Multiple one-time and repeating alarms with selectable weekdays and labels.
- Tap the clock to briefly show alarm and settings icons, and a skip icon when
  an alarm is upcoming. Skip cancels the next occurrence, a one-time
  alarm, or a pending snooze.
- Exact alarm delivery, configurable sound and volume, optional
  snooze, and on-screen and notification controls for active alarms.
- Local storage for alarm definitions and preferences; no account or cloud
  service is required.

## Requirements

- .NET 10 SDK with the .NET MAUI Android workload.
- Android SDK and an Android device or emulator running Android 6.0 (API 23)
  or later. The primary target is the Samsung Galaxy S25 Ultra.

## Build and run

From the repository root, build the app:

```powershell
dotnet build src\BedsideAlarmClock\BedsideAlarmClock.csproj -c Debug
```

To build an APK for an ARM64 phone:

```powershell
dotnet publish src\BedsideAlarmClock\BedsideAlarmClock.csproj -c Release -r android-arm64 -p:AndroidPackageFormat=apk
```

The signed APK is written under
`src\BedsideAlarmClock\bin\Release\net10.0-android\android-arm64\publish`.
Install it on a connected device with `adb install -r <path-to-signed-apk>`.
For an x64 emulator, use `android-x64` instead of `android-arm64`.

Check the alarm permissions in **Settings**.
Android's notification and full-screen behavior can vary by device and system
settings.

## Tests

```powershell
dotnet test tests\BedsideAlarmClock.Alarm.Tests\BedsideAlarmClock.Alarm.Tests.csproj
dotnet test tests\BedsideAlarmClock.Display.Tests\BedsideAlarmClock.Display.Tests.csproj
```

## Project structure

- `src\BedsideAlarmClock` - MAUI app, alarm logic, and Android integration.
- `tests` - alarm and display tests and an Android scheduler probe.
- [Architecture.md](Architecture.md) - architecture and implementation notes.
- [Phase4Plan.md](Phase4Plan.md) - alarm-delivery progress and limitations.

## License

This project is licensed under the [MIT License](LICENSE). Bundled Noto fonts
are distributed under their [separate license](src/BedsideAlarmClock/Resources/Fonts/Noto-OFL.txt).
