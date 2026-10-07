$ErrorActionPreference = 'Stop'
$platform = Split-Path $PSScriptRoot
$release = 'ExpanseRecovery-M3-dev-20260926-123146'
$archive = Join-Path $platform "artifacts\$release.zip"
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne 'F37518DCE384D756A551FDB49C223410681F1342F8E55143535C5CBDA853B5C2') { throw 'Release archive hash mismatch.' }
function Assert-GameClosed {
    if (Get-Process -Name 'KSP','KSP_x64' -ErrorAction SilentlyContinue) { throw 'KSP is running; close it before installing.' }
}
Assert-GameClosed
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$versionRoot = Join-Path $installRoot 'versions\20260926-123146'
if (Test-Path -LiteralPath $versionRoot) { throw 'Version directory already exists; inspect before retrying.' }
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $versionRoot
$verified = 0
foreach ($entry in Get-Content -LiteralPath (Join-Path $versionRoot 'SHA256SUMS.txt')) {
    if ($entry -notmatch '^([A-Fa-f0-9]{64})\s+(.+)$') { throw "Invalid manifest entry: $entry" }
    $expected = $Matches[1]; $relative = $Matches[2]
    $candidate = [IO.Path]::GetFullPath((Join-Path $versionRoot $relative))
    if (-not $candidate.StartsWith($versionRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $expected) { throw "Hash mismatch: $relative" }
    $verified++
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $platform "artifacts\deploy-backups\regular-install-$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$gamePlugin = 'C:\Kerbal Space Program\GameData\ExpanseWorldBridge'
Copy-Item -LiteralPath $gamePlugin -Destination (Join-Path $backup 'ExpanseWorldBridge') -Recurse
Copy-Item -LiteralPath 'C:\Kerbal Space Program\saves\The Expanse' -Destination (Join-Path $backup 'The Expanse') -Recurse
$sourceLauncher = Join-Path $platform 'Launch-Expanse-Clock.cmd'
Copy-Item -LiteralPath $sourceLauncher -Destination $backup
$oldHost = Join-Path $platform 'artifacts\ExpanseWorldView-M2-20260926-025349\Host\Expanse.Clock.Host.exe'
$runningApps = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe') })
foreach ($process in $runningApps) {
    if ($process.Name -ne 'Expanse.Clock.Host.exe' -or $process.ExecutablePath -ne $oldHost) { throw "Unexpected Expanse process: $($process.ProcessId) $($process.ExecutablePath)" }
}
Assert-GameClosed
foreach ($process in $runningApps) { Stop-Process -Id $process.ProcessId; Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue }
$stateRoot = Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
if (Test-Path -LiteralPath $stateRoot) { Copy-Item -LiteralPath $stateRoot -Destination (Join-Path $backup 'AppData-ExpanseFoundations') -Recurse }
Assert-GameClosed
$pluginSource = Join-Path $versionRoot 'GameData\ExpanseWorldBridge\Plugins'
foreach ($dll in @('Expanse.WorldBridge.dll','Expanse.Domain.dll')) {
    Copy-Item -LiteralPath (Join-Path $pluginSource $dll) -Destination (Join-Path $gamePlugin "Plugins\$dll") -Force
    if ((Get-FileHash (Join-Path $pluginSource $dll)).Hash -ne (Get-FileHash (Join-Path $gamePlugin "Plugins\$dll")).Hash) { throw "Installed DLL hash mismatch: $dll" }
}
$launcher = @'
$ErrorActionPreference = 'Stop'
$versionRoot = Join-Path $PSScriptRoot 'versions\20260926-123146'
$taskHostExe = Join-Path $versionRoot 'Host\Expanse.Clock.Host.exe'
$taskManagerExe = Join-Path $versionRoot 'Manager\Expanse.Clock.Manager.exe'
$runDir = Join-Path $env:LOCALAPPDATA 'ExpanseFoundations\Run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
foreach ($exe in @($taskHostExe,$taskManagerExe)) { if (-not (Test-Path -LiteralPath $exe)) { throw "Missing installed app: $exe" } }
$runningHosts = @(Get-CimInstance Win32_Process -Filter "Name='Expanse.Clock.Host.exe'")
if ($runningHosts | Where-Object { $_.ExecutablePath -ne $taskHostExe }) { throw 'Another Expanse Host version is running. Close that Host before opening this version.' }
if (-not $runningHosts) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $startedHost = Start-Process -FilePath $taskHostExe -WorkingDirectory (Split-Path $taskHostExe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir "host-$stamp.log") -RedirectStandardError (Join-Path $runDir "host-$stamp.err.log") -PassThru
    Start-Sleep -Milliseconds 700
    if ($startedHost.HasExited) { throw 'Expanse Host exited; inspect the Run logs.' }
}
$runningManager = Get-CimInstance Win32_Process -Filter "Name='Expanse.Clock.Manager.exe'" | Where-Object { $_.ExecutablePath -eq $taskManagerExe }
if (-not $runningManager) { Start-Process -FilePath $taskManagerExe -WorkingDirectory (Split-Path $taskManagerExe) | Out-Null }
'@
$launcherPath = Join-Path $installRoot 'Launch-Expanse-Foundations.ps1'
Set-Content -LiteralPath $launcherPath -Value $launcher -Encoding UTF8
$cmdText = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`"`r`nif errorlevel 1 pause`r`n"
Set-Content -LiteralPath (Join-Path $installRoot 'Launch-Expanse-Foundations.cmd') -Value $cmdText -Encoding ASCII
Set-Content -LiteralPath $sourceLauncher -Value $cmdText -Encoding ASCII
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'Expanse Foundations.lnk'
if (Test-Path -LiteralPath $shortcutPath) { Copy-Item -LiteralPath $shortcutPath -Destination $backup }
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installRoot 'Launch-Expanse-Foundations.cmd'
$shortcut.WorkingDirectory = $installRoot
$shortcut.IconLocation = (Join-Path $versionRoot 'Manager\Expanse.Clock.Manager.exe') + ',0'
$shortcut.Description = 'Expanse Foundations Windows manager and KSP bridge host'
$shortcut.WindowStyle = 7
$shortcut.Save()
$record = [ordered]@{ installedAt = (Get-Date).ToString('o'); release = $release; verifiedPackageFiles = $verified; installRoot = $installRoot; versionRoot = $versionRoot; backup = $backup; shortcut = $shortcutPath; pluginFiles = @(Get-ChildItem -LiteralPath (Join-Path $gamePlugin 'Plugins') -File | ForEach-Object { @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash } }); physicalEffects = 'Disabled in regular installation; development test gateway remains gated'; save = 'Original unchanged; full backup retained' }
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'INSTALLATION.json') -Encoding UTF8
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $installRoot 'INSTALLATION.json') -Encoding UTF8
@"
Installed by explicit user request. All physical resource transfers remain development-gated.
Normal launcher: $launcherPath
Backup: $backup
Rollback: close KSP and Expanse apps; restore the backed-up ExpanseWorldBridge directory (remove only the added Expanse.Domain.dll when reverting to M2), restore the previous launcher, and restore the AppData recovery directory as a unit if rolling back its database. The prior M2 app binaries remain in the original artifacts directory. The save backup is precautionary; installation did not modify the original save.
"@ | Set-Content -LiteralPath (Join-Path $backup 'INSTALLATION-NOTES.txt') -Encoding UTF8
& $launcherPath
$record | ConvertTo-Json -Depth 6
