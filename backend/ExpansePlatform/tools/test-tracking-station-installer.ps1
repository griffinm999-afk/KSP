[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$platform = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$dll = Join-Path $platform 'src\Expanse.TrackingStation\bin\Release\net472\Expanse.TrackingStation.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Build Tracking Station Release first.' }
$sandbox = Join-Path $platform ('artifacts\tracking-installer-test-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff'))
$package = Join-Path $sandbox 'package'
$game = Join-Path $sandbox 'fake-game'
$plugins = Join-Path $package 'GameData\ExpanseTrackingStation\Plugins'
$gameData = Join-Path $game 'GameData'
New-Item -ItemType Directory -Path $plugins,$gameData -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $plugins
Set-Content -LiteralPath (Join-Path $package 'GameData\ExpanseTrackingStation\README.txt') -Value 'Installer synthetic test'
# This is an inert path marker, never an executable fixture.
Set-Content -LiteralPath (Join-Path $game 'KSP_x64.exe') -Value 'not executable'
Set-Content -LiteralPath (Join-Path $gameData 'other-mod.txt') -Value 'untouched'
$hash = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
$installer = Join-Path $PSScriptRoot 'install-tracking-station.ps1'
function Require([bool]$condition,[string]$description) { if (-not $condition) { throw "FAILED: $description" }; Write-Output "PASS: $description" }
function MustFail([scriptblock]$action,[string]$message) {
    $failed = $false
    try { & $action | Out-Null } catch { if ($_.Exception.Message.Contains($message)) { $failed = $true } else { throw } }
    Require $failed $message
}
MustFail { & $installer -PackageRoot $package -GameRoot $game -ExpectedPluginSha256 ('0' * 64) -Confirm:$false } 'Package plugin hash mismatch'
& $installer -PackageRoot $package -GameRoot $game -ExpectedPluginSha256 $hash -WhatIf
$destination = Join-Path $gameData 'ExpanseTrackingStation'
Require (-not (Test-Path -LiteralPath $destination)) 'WhatIf makes no installation writes'
& $installer -PackageRoot $package -GameRoot $game -ExpectedPluginSha256 $hash -Confirm:$false
$target = Join-Path $destination 'Plugins\Expanse.TrackingStation.dll'
Require ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $hash) 'fresh install hash'
$preferences = Join-Path $destination 'PluginData'
New-Item -ItemType Directory -Path $preferences | Out-Null
Set-Content -LiteralPath (Join-Path $preferences 'sentinel.cfg') -Value 'keep preferences'
$before = (Get-FileHash -LiteralPath (Join-Path $preferences 'sentinel.cfg')).Hash
& $installer -PackageRoot $package -GameRoot $game -ExpectedPluginSha256 $hash -Confirm:$false
Require ((Get-FileHash -LiteralPath (Join-Path $preferences 'sentinel.cfg')).Hash -eq $before) 'update preserves PluginData'
Require ((Get-Content -LiteralPath (Join-Path $gameData 'other-mod.txt') -Raw).Trim() -eq 'untouched') 'other GameData file unchanged'
$backups = @(Get-ChildItem -LiteralPath (Join-Path $destination 'Backups') -Recurse -File)
Require (@($backups | Where-Object Extension -eq '.dll').Count -eq 0) 'backup cannot be recursively loaded as DLL'
Require (@($backups | Where-Object Name -eq 'Expanse.TrackingStation.dll.bak').Count -eq 1) 'previous DLL retained as non-DLL backup'
Require ((Get-FileHash -LiteralPath ($backups | Where-Object Name -eq 'Expanse.TrackingStation.dll.bak').FullName).Hash -eq $hash) 'backup hash matches previous DLL'
Require ((Get-Content -LiteralPath (Join-Path $destination 'INSTALLATION.json') -Raw | ConvertFrom-Json).sha256 -eq $hash) 'installed metadata recorded'
# A valid .NET assembly with the wrong identity must be rejected even with its correct hash.
$different = Join-Path $platform 'src\Expanse.Domain\bin\Release\net472\Expanse.Domain.dll'
if (Test-Path -LiteralPath $different) {
    Copy-Item -LiteralPath $different -Destination (Join-Path $plugins 'Expanse.TrackingStation.dll') -Force
    $differentHash = (Get-FileHash -LiteralPath $different).Hash
    MustFail { & $installer -PackageRoot $package -GameRoot $game -ExpectedPluginSha256 $differentHash -Confirm:$false } 'wrong assembly identity'
}
Write-Output "Synthetic filesystem evidence retained: $sandbox"
