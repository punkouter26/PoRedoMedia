<#
.SYNOPSIS
  Builds the Android app, installs it on the attached phone or emulator, and points the device's
  own 127.0.0.1 at this PC so the app reaches the local server (4100) and Azurite (10000).
.EXAMPLE
  ./scripts/run-mobile.ps1                     # the only attached device
  ./scripts/run-mobile.ps1 -Serial emulator-5554
#>
param([string]$Serial)

$ErrorActionPreference = 'Stop'
$adbTarget = if ($Serial) { @('-s', $Serial) } else { @() }

$abi = (& adb @adbTarget shell getprop ro.product.cpu.abi).Trim()
if ($LASTEXITCODE -ne 0 -or -not $abi) { throw 'No Android device answered. Check `adb devices`.' }
$rid = if ($abi -eq 'x86_64') { 'android-x64' } else { 'android-arm64' }

# The upload and download links a local server hands out point at 127.0.0.1, so the device's
# loopback has to lead back here for both the app and its storage.
& adb @adbTarget reverse tcp:4100 tcp:4100 | Out-Null
& adb @adbTarget reverse tcp:10000 tcp:10000 | Out-Null

$project = Join-Path $PSScriptRoot '..\src\PoRedoMedia.Mobile'
dotnet build $project -f net10.0-android -r $rid -t:Install `
    "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" "-p:AdbTarget=$($adbTarget -join ' ')"
if ($LASTEXITCODE -ne 0) { throw 'The build or install failed.' }

& adb @adbTarget shell monkey -p com.poredomedia.mobile -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Host "Installed and started on $abi. Start the server with: dotnet run --project src/PoRedoMedia.Api"
