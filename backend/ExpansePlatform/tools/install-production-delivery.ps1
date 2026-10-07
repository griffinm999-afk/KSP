[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ArchivePath,
  [Parameter(Mandatory)][string]$ArchiveSha256,
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSaveSha256
)
$ErrorActionPreference = 'Stop'
$platform = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ($Version -notmatch '^20[0-9]{6}-[0-9]{6}$') { throw 'Version must be a UTC build stamp.' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $ArchiveSha256) { throw 'Archive SHA256 mismatch.' }
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$newVersion = Join-Path $installRoot "versions\$Version"
$installationPath = Join-Path $installRoot 'INSTALLATION.json'
if (-not (Test-Path -LiteralPath $installationPath -PathType Leaf)) { throw "Missing current installation record: $installationPath" }
$installation = Get-Content -LiteralPath $installationPath -Raw | ConvertFrom-Json
if ($installation.version -notmatch '^20[0-9]{6}-[0-9]{6}$') { throw 'Current installation version is invalid.' }
$oldVersion = Join-Path $installRoot ("versions\" + $installation.version)
if (-not [IO.Path]::GetFullPath($installation.versionRoot).Equals([IO.Path]::GetFullPath($oldVersion),[StringComparison]::OrdinalIgnoreCase)) { throw 'Current installation record points outside its version directory.' }
$oldHost = Join-Path $oldVersion 'Host\Expanse.Clock.Host.exe'
$oldManager = Join-Path $oldVersion 'Manager\Expanse.Clock.Manager.exe'
$gameRoot = 'C:\Kerbal Space Program'
$gamePlugin = Join-Path $gameRoot 'GameData\ExpanseWorldBridge'
$gameSave = Join-Path $gameRoot 'saves\The Expanse'
$saveFile = Join-Path $gameSave 'persistent.sfs'
$stateRoot = Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
$launcherPath = Join-Path $installRoot 'Launch-Expanse-Foundations.ps1'
$sourceLauncher = Join-Path $platform 'Launch-Expanse-Clock.cmd'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Expanse Foundations.lnk'
if (Test-Path -LiteralPath $newVersion) { throw "Version already exists: $newVersion" }
foreach ($path in @($oldHost,$oldManager,$gamePlugin,$saveFile,$launcherPath,$sourceLauncher)) {
  if (-not (Test-Path -LiteralPath $path)) { throw "Required prior installation item missing: $path" }
}
if (-not ((Get-Content -LiteralPath $launcherPath -Raw).Contains(("versions\" + $installation.version)))) { throw 'Current launcher does not select the recorded installed version.' }
function Assert-GameClosed {
  $games = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('KSP.exe','KSP_x64.exe') })
  if ($games.Count -ne 0) { throw ('KSP must be closed: ' + (($games | ForEach-Object { "$($_.ProcessId) $($_.ExecutablePath)" }) -join '; ')) }
}
Assert-GameClosed
if ((Get-FileHash -LiteralPath $saveFile -Algorithm SHA256).Hash -ne $ExpectedSaveSha256) { throw 'Original regular persistent save hash changed.' }
$running = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe') })
foreach ($process in $running) {
  $expected = if ($process.Name -eq 'Expanse.Clock.Host.exe') { $oldHost } else { $oldManager }
  if (-not $process.ExecutablePath.Equals($expected,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected Expanse process $($process.ProcessId): $($process.ExecutablePath)"
  }
}
$backup = Join-Path $platform ('artifacts\deploy-backups\delivery-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $oldVersion -Destination (Join-Path $backup 'previous-version') -Recurse
Copy-Item -LiteralPath $gamePlugin -Destination (Join-Path $backup 'ExpanseWorldBridge') -Recurse
Copy-Item -LiteralPath $gameSave -Destination (Join-Path $backup 'The Expanse') -Recurse
Copy-Item -LiteralPath $launcherPath,$sourceLauncher -Destination $backup
if (Test-Path -LiteralPath $shortcutPath) { Copy-Item -LiteralPath $shortcutPath -Destination $backup }
if ((Get-FileHash -LiteralPath (Join-Path $backup 'The Expanse\persistent.sfs') -Algorithm SHA256).Hash -ne $ExpectedSaveSha256) {
  throw 'Backup save hash mismatch; installation stopped before process shutdown.'
}
Assert-GameClosed
foreach ($process in $running) {
  Stop-Process -Id $process.ProcessId
  Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $stateRoot) { Copy-Item -LiteralPath $stateRoot -Destination (Join-Path $backup 'AppData-ExpanseFoundations') -Recurse }
Assert-GameClosed
if ((Get-FileHash -LiteralPath $saveFile -Algorithm SHA256).Hash -ne $ExpectedSaveSha256) { throw 'Regular persistent save changed during backup.' }
New-Item -ItemType Directory -Path $newVersion | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $newVersion
$manifest = Join-Path $newVersion 'SHA256SUMS.txt'
$verified = 0
foreach ($line in Get-Content -LiteralPath $manifest) {
  if ($line -notmatch '^([A-Fa-f0-9]{64})\s+(.+)$') { throw "Invalid manifest row: $line" }
  $hash = $Matches[1]; $relative = $Matches[2]
  $file = [IO.Path]::GetFullPath((Join-Path $newVersion $relative))
  if (-not $file.StartsWith($newVersion + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes version root.' }
  if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) { throw "Manifest SHA256 mismatch: $relative" }
  $verified++
}
$pluginSource = Join-Path $newVersion 'GameData\ExpanseWorldBridge\Plugins'
foreach ($dll in @('Expanse.WorldBridge.dll','Expanse.Domain.dll')) {
  $source = Join-Path $pluginSource $dll
  $target = Join-Path $gamePlugin "Plugins\$dll"
  Copy-Item -LiteralPath $source -Destination $target -Force
  if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
    throw "Installed plugin hash mismatch: $dll"
  }
}
$launcher = @'
$ErrorActionPreference = 'Stop'
$installRoot = $PSScriptRoot
$versionRoot = Join-Path $installRoot 'versions\__VERSION__'
$taskHostExe = Join-Path $versionRoot 'Host\Expanse.Clock.Host.exe'
$taskManagerExe = Join-Path $versionRoot 'Manager\Expanse.Clock.Manager.exe'
$runDir = Join-Path $env:LOCALAPPDATA 'ExpanseFoundations\Run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
foreach ($exe in @($taskHostExe,$taskManagerExe)) { if (-not (Test-Path -LiteralPath $exe)) { throw "Missing installed app: $exe" } }
foreach ($name in @('Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe')) {
  $expected = if ($name -eq 'Expanse.Clock.Host.exe') { $taskHostExe } else { $taskManagerExe }
  $other = @(Get-CimInstance Win32_Process -Filter "Name='$name'" | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith((Join-Path $installRoot 'versions') + '\',[StringComparison]::OrdinalIgnoreCase) -and
    -not $_.ExecutablePath.Equals($expected,[StringComparison]::OrdinalIgnoreCase)
  })
  if ($other.Count -ne 0) { throw "Another installed Expanse $name version is running." }
}
$hosts = @(Get-CimInstance Win32_Process -Filter "Name='Expanse.Clock.Host.exe'" | Where-Object { $_.ExecutablePath -eq $taskHostExe })
if ($hosts.Count -eq 0) {
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $startedHost = Start-Process -FilePath $taskHostExe -WorkingDirectory (Split-Path $taskHostExe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir "host-$stamp.log") -RedirectStandardError (Join-Path $runDir "host-$stamp.err.log") -PassThru
  Start-Sleep -Milliseconds 700
  if ($startedHost.HasExited) { throw 'Expanse Host exited; inspect Run logs.' }
}
$managers = @(Get-CimInstance Win32_Process -Filter "Name='Expanse.Clock.Manager.exe'" | Where-Object { $_.ExecutablePath -eq $taskManagerExe })
if ($managers.Count -eq 0) { Start-Process -FilePath $taskManagerExe -WorkingDirectory (Split-Path $taskManagerExe) | Out-Null }
'@.Replace('__VERSION__',$Version)
Set-Content -LiteralPath $launcherPath -Value $launcher -Encoding UTF8
$cmdText = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`"`r`nif errorlevel 1 pause`r`n"
Set-Content -LiteralPath (Join-Path $installRoot 'Launch-Expanse-Foundations.cmd') -Value $cmdText -Encoding ASCII
Set-Content -LiteralPath $sourceLauncher -Value $cmdText -Encoding ASCII
if (Test-Path -LiteralPath $shortcutPath) {
  $shell = New-Object -ComObject WScript.Shell
  $shortcut = $shell.CreateShortcut($shortcutPath)
  $shortcut.IconLocation = (Join-Path $newVersion 'Manager\Expanse.Clock.Manager.exe') + ',0'
  $shortcut.Save()
}
& $launcherPath
$installedHash = (Get-FileHash -LiteralPath $saveFile -Algorithm SHA256).Hash
if ($installedHash -ne $ExpectedSaveSha256) { throw 'Regular save changed unexpectedly during installation.' }
$record = [ordered]@{
  installedAtUtc = (Get-Date).ToUniversalTime().ToString('o'); version = $Version; archiveSha256 = $ArchiveSha256
  verifiedPackageFiles = $verified; versionRoot = $newVersion; backup = $backup
  bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path $gamePlugin 'Plugins\Expanse.WorldBridge.dll') -Algorithm SHA256).Hash
  domainSha256 = (Get-FileHash -LiteralPath (Join-Path $gamePlugin 'Plugins\Expanse.Domain.dll') -Algorithm SHA256).Hash
  regularPersistentSha256 = $installedHash; physicalProvider = 'Remote BRP 0.2.7 with route-specific unloaded selected-tank preflight'
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'INSTALLATION.json') -Encoding UTF8
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $installRoot 'INSTALLATION.json') -Encoding UTF8
$record | ConvertTo-Json -Depth 4
