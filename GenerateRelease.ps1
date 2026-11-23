$ErrorActionPreference = 'Stop'
$projectDirectory = Join-Path $PSScriptRoot 'src\BedsideAlarmClock'

$env:ANDROID_KEYSTORE_PASSWORD = Read-Host "Keystore password"
$env:ANDROID_KEY_PASSWORD = Read-Host "Key password"

foreach ($directory in @('bin\Release', 'obj\Release')) {
    $path = Join-Path $projectDirectory $directory
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

dotnet publish (Join-Path $projectDirectory 'BedsideAlarmClock.csproj') -f net10.0-android -c Release -p:RuntimeIdentifier=android-arm64 --verbosity normal
if ($LASTEXITCODE -ne 0) {
    throw "Release publish failed with exit code $LASTEXITCODE."
}

$apk = Join-Path $projectDirectory 'bin\Release\net10.0-android\android-arm64\publish\com.matthew.bedsidealarmclock-Signed.apk'
Copy-Item -LiteralPath $apk -Destination "M:\OneDrive\Matt's Bedside Alarm Clock" -Force
