$ErrorActionPreference='Stop'
$workspace=$PSScriptRoot
$package=Join-Path $workspace 'ExpansePlatform-budget-contract-20261006'
$archive=$package+'.zip'
$expected='90cf8adb1acde9b7dc4b58808e7af7cb1b7d142796f8e3853fbb97c4a5ad2430'
$game='C:\Kerbal Space Program'
$install=Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$state=Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
$recordPath=Join-Path $install 'INSTALLATION.json'
$launcherPath=Join-Path $install 'Launch-Expanse-Foundations.ps1'
$record=Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$oldVersion=[IO.Path]::GetFullPath($record.versionRoot)
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$newVersion=Join-Path $install ('versions\'+$stamp)
$backup=Join-Path $workspace ('budget-install-backup-'+$stamp)
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Under([string]$p,[string]$r){$x=[IO.Path]::GetFullPath((Join-Path $p $r));if(!$x.StartsWith([IO.Path]::GetFullPath($p).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes root'};return $x}
function GameClosed {if(@(Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue).Count){throw 'KSP is running; no installation allowed'}}
GameClosed
if(Test-Path -LiteralPath (Join-Path $workspace 'budget-install-result.json')){throw 'Prior installation record exists; inspect before repeat'}
if((Hash $archive)-ne $expected){throw 'Archive hash mismatch'}
if(!$oldVersion.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected installed root'}
$manifest=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt')){
 if($line -notmatch '^([a-fA-F0-9]{64})  (.+)$'){throw 'Invalid manifest'}
 $sha=$Matches[1];$r=$Matches[2];if($manifest.ContainsKey($r)){throw 'Duplicate manifest entry'}
 if((Hash (Under $package $r))-ne $sha){throw "Package hash mismatch: $r"};$manifest[$r]=$sha
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
 foreach($r in $manifest.Keys){$entry=$zip.GetEntry($r);if(!$entry){throw "Missing archive entry: $r"};$stream=$entry.Open();try{$zipsha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($zipsha -ne $manifest[$r]){throw "Archive entry mismatch: $r"}}
 $entry=$zip.GetEntry('SHA256SUMS.txt');$stream=$entry.Open();try{$zipsha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($zipsha -ne (Hash (Join-Path $package 'SHA256SUMS.txt'))){throw 'Manifest differs from archive'}
}finally{$zip.Dispose()}
$apps=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('Expanse.Clock.Host.exe','Expanse.Clock.Manager.exe')})
if(@($apps|Where-Object {$_.ExecutablePath -and !$_.ExecutablePath.StartsWith($oldVersion+'\',[StringComparison]::OrdinalIgnoreCase)}).Count){throw 'Different Expanse app version running'}
$hosts=@($apps|Where-Object {$_.Name -eq 'Expanse.Clock.Host.exe'});$managers=@($apps|Where-Object {$_.Name -eq 'Expanse.Clock.Manager.exe'})
if($hosts.Count -gt 1 -or $managers.Count -gt 1){throw 'Duplicate apps running'}
$relayPath=Join-Path $state 'SiteRelay\relay.cjs'
$relayProcesses=@(Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object {$_.CommandLine -and $_.CommandLine.Contains($relayPath)})
if($relayProcesses.Count -ne 1 -or !$relayProcesses[0].CommandLine.Contains('--telemetry-only')){throw 'Expected one telemetry-only relay'}
$relayHashes=@{};foreach($f in @('relay.cjs','start.vbs','start-telemetry-only.vbs','credentials.dpapi')){$relayHashes[$f]=Hash (Join-Path $state ('SiteRelay\'+$f))}
$launcher=Get-Content -LiteralPath $launcherPath -Raw
$oldSelector='versions\'+$record.version
if(!$launcher.Contains($oldSelector)){throw 'Launcher selector mismatch'}
$newLauncher=$launcher.Replace($oldSelector,('versions\'+$stamp))
New-Item -ItemType Directory -Path $backup | Out-Null
$rows=[Collections.Generic.List[object]]::new()
function BackupFile([string]$p,[string]$r){$dst=Under $backup $r;New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null;$sha=Hash $p;Copy-Item -LiteralPath $p -Destination $dst;if((Hash $dst)-ne $sha -or (Hash $p)-ne $sha){throw 'Backup hash mismatch'};$rows.Add([pscustomobject]@{source=$p;backupRelative=$r;sha256=$sha})}
function BackupTree([string]$p,[string]$r){foreach($f in Get-ChildItem -LiteralPath $p -Recurse -File){BackupFile $f.FullName (Join-Path $r $f.FullName.Substring($p.Length).TrimStart('\'))}}
BackupTree $oldVersion 'previous-version'
BackupTree (Join-Path $game 'saves\The Expanse') 'The Expanse'
foreach($f in @('INSTALLATION.json','Launch-Expanse-Foundations.ps1','Launch-Expanse-Foundations.cmd')){BackupFile (Join-Path $install $f) ('Installation\'+$f)}
$plugins=@('GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll','GameData/ExpanseTrackingStation/Plugins/Expanse.TrackingStation.dll')
foreach($r in $plugins){BackupFile (Under $game $r) ('PreviousPlugins\'+$r)}
GameClosed
foreach($manager in $managers){$proc=Get-Process -Id $manager.ProcessId;if(!$proc.CloseMainWindow() -or !$proc.WaitForExit(15000)){throw 'Manager did not close gracefully'}}
foreach($hostApp in $hosts){$helper=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $workspace 'shutdown-clock-host.ps1')+'"'),'-TargetProcessId',$hostApp.ProcessId) -WindowStyle Hidden -PassThru -Wait;if($helper.ExitCode -ne 0){throw 'Host graceful stop helper failed'}}
if(@(Get-Process -Name Expanse.Clock.Host,Expanse.Clock.Manager -ErrorAction SilentlyContinue).Count){throw 'App still running after graceful request'}
foreach($folder in @('Recovery','ClockBridge')){$p=Join-Path $state $folder;if(Test-Path -LiteralPath $p){BackupTree $p ('HostData\'+$folder)}}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'BACKUP-MANIFEST.json') -Encoding utf8
foreach($row in $rows){if((Hash $row.source)-ne $row.sha256){throw 'Source changed after backup'}}
GameClosed
New-Item -ItemType Directory -Path $newVersion | Out-Null
foreach($r in $manifest.Keys | Sort-Object){if(!$r.StartsWith('Host/') -and !$r.StartsWith('Manager/')){continue};$dst=Under $newVersion $r;New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null;Copy-Item -LiteralPath (Under $package $r) -Destination $dst;if((Hash $dst)-ne $manifest[$r]){throw 'Installed runtime hash mismatch'}}
foreach($r in $plugins){Copy-Item -LiteralPath (Under $package $r) -Destination (Under $game $r) -Force;if((Hash (Under $game $r))-ne $manifest[$r]){throw 'Installed plugin hash mismatch'}}
[IO.File]::WriteAllText($launcherPath,$newLauncher,[Text.UTF8Encoding]::new($false))
$updates=@{installedAtUtc=(Get-Date).ToUniversalTime().ToString('o');version=$stamp;versionRoot=$newVersion;archiveSha256=$expected;verifiedPackageFiles=$manifest.Count;backup=$backup;managerVersion=$stamp;managerRoot=(Join-Path $newVersion 'Manager');managerArchiveSha256=$expected;managerRollback=$backup;bridgeSha256=$manifest[$plugins[0]];domainSha256=$manifest[$plugins[1]];brpSha256=$manifest[$plugins[2]];trackingStationSha256=$manifest[$plugins[3]];candidateManifestSha256=(Hash (Join-Path $package 'SHA256SUMS.txt'));launchVerifiedBy='Runtime hashes verified; live qualification pending relaunch.'}
foreach($e in $updates.GetEnumerator()){$record|Add-Member NoteProperty $e.Key $e.Value -Force}
$record|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $recordPath -Encoding utf8
& 'C:\Users\griff\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' (Join-Path $workspace 'add-career-funds.py') apply (Join-Path $backup 'The Expanse\persistent.sfs')
if($LASTEXITCODE -ne 0){throw 'Funds update failed'}
foreach($row in $rows | Where-Object {$_.backupRelative.StartsWith('HostData\') -or ($_.backupRelative.StartsWith('The Expanse\') -and $_.backupRelative -ne 'The Expanse\persistent.sfs')}){if((Hash $row.source)-ne $row.sha256){throw 'Unrelated save or Host data changed'}}
foreach($f in $relayHashes.Keys){if((Hash (Join-Path $state ('SiteRelay\'+$f)))-ne $relayHashes[$f]){throw 'Relay file changed'}}
$runDir=Join-Path $state 'Run';New-Item -ItemType Directory -Force -Path $runDir|Out-Null
$hostExe=Join-Path $newVersion 'Host\Expanse.Clock.Host.exe'
$newHost=Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir ('host-budget-'+$stamp+'.log')) -RedirectStandardError (Join-Path $runDir ('host-budget-'+$stamp+'.err.log')) -PassThru
$newManagerId=$null;if($managers.Count){$newManager=Start-Process -FilePath (Join-Path $newVersion 'Manager\Expanse.Clock.Manager.exe') -PassThru;$newManagerId=$newManager.Id}
GameClosed
$result=[ordered]@{status='installed-ready-for-relaunch';archiveSha256=$expected;manifestFilesVerified=$manifest.Count;versionRoot=$newVersion;backup=$backup;backupFilesVerified=$rows.Count;hostProcessId=$newHost.Id;managerProcessId=$newManagerId;relayProcessId=$relayProcesses[0].ProcessId;relayFilesUnchanged=$true;kspLaunched=$false;liveAtlasQualification='pending relaunch'}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $workspace 'budget-install-result.json') -Encoding utf8
$result|ConvertTo-Json -Depth 6
