$ErrorActionPreference = 'Stop'
$workspace = $PSScriptRoot
$packageName = 'ExpansePlatform-reconciled-performance-20261006'
$package = Join-Path $workspace $packageName
$archive = Join-Path $workspace ($packageName + '.zip')
$expectedArchive = '67DA39B55E024D2EF518EECC53A23E2E402574200DD8B09DE2F2AC0673A2AB5B'
$game = 'C:\Kerbal Space Program'
$install = Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$state = Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
$recordPath = Join-Path $install 'INSTALLATION.json'
$launcherPath = Join-Path $install 'Launch-Expanse-Foundations.ps1'
$record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$oldVersion = [IO.Path]::GetFullPath($record.versionRoot)
$oldManager = [IO.Path]::GetFullPath($record.managerRoot)
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$newVersion = Join-Path $install ('versions\' + $stamp)
$backup = Join-Path $workspace ('performance-install-backup-' + $stamp)
$sourceRoot = 'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'

function Assert-AppsClosed {
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('KSP.exe','KSP_x64.exe','Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe') -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'Expanse\.Clock\.(Host|Manager)')
    })
    if ($running.Count) { throw ('Installation stopped; an application is running: ' + (($running | ForEach-Object { $_.Name + ' PID ' + $_.ProcessId }) -join ', ')) }
}
function Hash([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
function Under([string]$parent,[string]$relative) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $parent $relative))
    if (!$resolved.StartsWith([IO.Path]::GetFullPath($parent).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes its expected root.' }
    return $resolved
}
Assert-AppsClosed
if ((Hash $archive) -ne $expectedArchive) { throw 'Archive hash changed.' }
foreach ($old in @($oldVersion,$oldManager)) {
    if (!$old.StartsWith($install + '\',[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $old -PathType Container)) { throw 'Invalid current runtime location.' }
}
if ((Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $newVersion)) { throw 'Destination already exists.' }
$manifest = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Invalid package manifest.' }
    $sha=$Matches[1]; $relative=$Matches[2]
    if ($manifest.ContainsKey($relative)) { throw 'Duplicate manifest entry.' }
    if ((Hash (Under $package $relative)) -ne $sha) { throw "Package file changed: $relative" }
    if ($relative.StartsWith('Source/')) {
        if ((Hash (Under $sourceRoot $relative.Substring(7))) -ne $sha) { throw "Primary source changed: $relative" }
    }
    $manifest[$relative]=$sha
}
if ($manifest.Count -ne 594) { throw 'Unexpected package file count.' }
$launcher = Get-Content -LiteralPath $launcherPath -Raw
$oldHostSelector = 'versions\' + $record.version
$oldManagerSelector = 'manager-versions\' + $record.managerVersion + '\Expanse.Clock.Manager.exe'
if (!$launcher.Contains($oldHostSelector) -or !$launcher.Contains($oldManagerSelector)) { throw 'Launcher does not match inspected installation.' }
$newLauncher = $launcher.Replace($oldHostSelector,('versions\' + $stamp)).Replace($oldManagerSelector,('versions\' + $stamp + '\Manager\Expanse.Clock.Manager.exe'))
$preflight = Get-Content -LiteralPath (Join-Path $workspace 'install-performance-preflight.json') -Raw | ConvertFrom-Json
if ((Hash (Join-Path $game 'saves\The Expanse\persistent.sfs')) -ne $preflight.persistentSha256) { throw 'Closed save changed since preflight.' }

# Backups are complete and hash-verified before any installed file is replaced.
New-Item -ItemType Directory -Path $backup | Out-Null
$backupRows = [Collections.Generic.List[object]]::new()
function Backup-File([string]$source,[string]$relative) {
    $target=Under $backup $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    $before=Hash $source
    Copy-Item -LiteralPath $source -Destination $target
    if ((Hash $target) -ne $before -or (Hash $source) -ne $before) { throw 'Backup verification failed.' }
    $backupRows.Add([pscustomobject]@{source=$source;backupRelative=$relative;sha256=$before})
}
function Backup-Tree([string]$source,[string]$relative) {
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        Backup-File $file.FullName (Join-Path $relative $file.FullName.Substring($source.Length).TrimStart('\'))
    }
}
Backup-Tree $oldVersion 'previous-version'
Backup-Tree $oldManager 'previous-manager'
Backup-Tree (Join-Path $game 'saves\The Expanse') 'The Expanse'
foreach ($folder in @('Recovery','ClockBridge')) {
    $path=Join-Path $state $folder
    if (Test-Path -LiteralPath $path) { Backup-Tree $path ('HostData\' + $folder) }
}
foreach ($file in @('INSTALLATION.json','Launch-Expanse-Foundations.ps1','Launch-Expanse-Foundations.cmd')) {
    Backup-File (Join-Path $install $file) ('Installation\' + $file)
}
$plugins=@('GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll','GameData/ExpanseTrackingStation/Plugins/Expanse.TrackingStation.dll')
foreach ($relative in $plugins) { Backup-File (Under $game $relative) ('PreviousPlugins\' + $relative) }
$backupRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'BACKUP-MANIFEST.json') -Encoding UTF8
Assert-AppsClosed
foreach ($row in $backupRows) { if ((Hash $row.source) -ne $row.sha256) { throw 'Original changed after backup; stopped before replacement.' } }

New-Item -ItemType Directory -Path $newVersion | Out-Null
# Install the complete matching Host/Manager runtimes only; keep source/evidence
# in the already verified archive rather than treating archival configs as live.
foreach ($relative in $manifest.Keys | Sort-Object) {
    if (!$relative.StartsWith('Host/') -and !$relative.StartsWith('Manager/')) { continue }
    $target=Under $newVersion $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Under $package $relative) -Destination $target
    if ((Hash $target) -ne $manifest[$relative]) { throw 'New runtime hash mismatch.' }
}
Assert-AppsClosed
foreach ($relative in $plugins) {
    Copy-Item -LiteralPath (Under $package $relative) -Destination (Under $game $relative) -Force
    if ((Hash (Under $game $relative)) -ne $manifest[$relative]) { throw 'Installed plugin hash mismatch.' }
}
Set-Content -LiteralPath $launcherPath -Value $newLauncher -Encoding UTF8
$updates=[ordered]@{
    installedAtUtc=(Get-Date).ToUniversalTime().ToString('o');version=$stamp;versionRoot=$newVersion
    archiveSha256=$expectedArchive;verifiedPackageFiles=595;backup=$backup
    managerVersion=$stamp;managerRoot=(Join-Path $newVersion 'Manager');managerArchiveSha256=$expectedArchive;managerRollback=$backup
    bridgeSha256=$manifest[$plugins[0]];domainSha256=$manifest[$plugins[1]];brpSha256=$manifest[$plugins[2]];trackingStationSha256=$manifest[$plugins[3]]
    regularPersistentSha256=$preflight.persistentSha256;candidateManifestSha256=(Hash (Join-Path $package 'SHA256SUMS.txt'))
    launchVerifiedBy='Static launcher and runtime hash checks; KSP, Host and Manager remain closed.'
}
foreach ($entry in $updates.GetEnumerator()) { $record | Add-Member -MemberType NoteProperty -Name $entry.Key -Value $entry.Value -Force }
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding UTF8
Assert-AppsClosed
foreach ($row in $backupRows | Where-Object { $_.backupRelative.StartsWith('The Expanse\') -or $_.backupRelative.StartsWith('HostData\') }) {
    if ((Hash $row.source) -ne $row.sha256) { throw 'Save or Host data changed unexpectedly.' }
}
$installedHashes=[ordered]@{}
foreach ($relative in $plugins) { $installedHashes[$relative]=Hash (Under $game $relative) }
foreach ($relative in $manifest.Keys | Sort-Object) {
    if ($relative.StartsWith('Host/') -or $relative.StartsWith('Manager/')) {
        $sha=Hash (Under $newVersion $relative)
        if ($sha -ne $manifest[$relative]) { throw 'Final runtime verification failed.' }
        $installedHashes[$relative]=$sha
    }
}
$result=[ordered]@{status='installed-applications-closed';archiveSha256=$expectedArchive;versionRoot=$newVersion;backup=$backup;backupFilesVerified=$backupRows.Count;persistentSha256=$preflight.persistentSha256;installedHashes=$installedHashes;saveAndHostDataUnchanged=$true;kspLaunched=$false;hostStarted=$false;managerStarted=$false;relayChanged=$false}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $workspace 'performance-install-result.json') -Encoding UTF8
$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'NEW-INSTALLATION.json') -Encoding UTF8
[pscustomobject]@{Status=$result.status;VersionRoot=$newVersion;Backup=$backup;BackupFiles=$backupRows.Count;InstalledFiles=$installedHashes.Count;SaveAndHostDataUnchanged=$true} | Format-List
