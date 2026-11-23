$ErrorActionPreference = 'Stop'
$projectDirectory = Join-Path $PSScriptRoot 'src\BedsideAlarmClock'

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
Copy-Item -LiteralPath $apk -Destination $env:ANDROID_RELEASE_DESTINATION -Force
