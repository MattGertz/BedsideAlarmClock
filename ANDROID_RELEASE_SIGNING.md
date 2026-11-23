# Android release signing

Release builds of `src\BedsideAlarmClock` produce an APK signed with a dedicated release key. If the keystore is missing, the build falls back to the default debug key (fine for local testing, not for distribution).

## Keystore

- Location: `%USERPROFILE%\AndroidKeys\release-key.jks` (outside the repository; `*.jks` and `*.keystore` are git-ignored).
- Alias: `myapp`, RSA 2048, valid 10,000 days.
- Override the path with `-p:AndroidSigningKeyStore=<path>` if needed.

Generate it once (never overwrite an existing keystore; `keytool` prompts for the passwords):

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\AndroidKeys" | Out-Null
keytool -genkeypair -v `
  -keystore "$env:USERPROFILE\AndroidKeys\release-key.jks" `
  -alias myapp `
  -keyalg RSA `
  -keysize 2048 `
  -validity 10000
```

## Credentials

The project reads passwords from environment variables, so nothing secret is stored in files:

```powershell
$env:ANDROID_KEYSTORE_PASSWORD = Read-Host "Keystore password"
$env:ANDROID_KEY_PASSWORD      = Read-Host "Key password"
```

## Build

```powershell
dotnet publish src\BedsideAlarmClock\BedsideAlarmClock.csproj -f net10.0-android -c Release -p:RuntimeIdentifier=android-arm64
```

Output: `src\BedsideAlarmClock\bin\Release\net10.0-android\android-arm64\publish\com.matthew.bedsidealarmclock-Signed.apk`

For the owner's final release workflow, run `.\GenerateRelease.ps1` from the
repository root. It prompts for the signing passwords and copy destination,
removes the project's Release build outputs, publishes an ARM64 APK, and
copies it to the chosen destination. The prompts display the passwords as they
are entered, and the environment variables remain set in the PowerShell session
afterward.

## Verify

```powershell
$apksigner = Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\build-tools" -Filter apksigner.bat -Recurse | Select-Object -Last 1 -ExpandProperty FullName
& $apksigner verify --verbose --print-certs <path-to-apk>
```

The signer line must show your release certificate (not `CN=Android Debug`). Compare its SHA-256 digest with the fingerprint recorded for earlier releases; it must be identical. To print the keystore's fingerprint:

```powershell
keytool -list -v -keystore "$env:USERPROFILE\AndroidKeys\release-key.jks" -alias myapp | Select-String 'SHA256'
```

Recorded release certificate SHA-256: `c5d903f1e0544e94ac80ad9ce3d548cf9951c1f92f53b6808f19e09f2a034b66`

## Install

Copy or download the APK to the device and open it. Android asks once to allow installs from that source (browser or file manager); no Play Protect changes are needed. From a PC:

```powershell
adb install -r <path-to-apk>
```

## Keep the key safe

- Back up `release-key.jks` and both passwords in at least two secure places.
- Android only updates an installed app if the new APK has the same signing certificate. If the key is lost, users must uninstall (losing app data) before installing a build signed with a new key.
- A device with a debug-signed build must uninstall it before installing a release-signed build.
