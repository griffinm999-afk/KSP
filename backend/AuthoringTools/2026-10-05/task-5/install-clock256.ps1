$ErrorActionPreference='Stop'
$workspace=$PSScriptRoot
$package=Join-Path $workspace 'ExpansePlatform-clock256-20261006'
$archive=$package+'.zip'
$expected='ad44fe0e42c9cfd6ec87d6963a83d7fe668acfb06895dc7e5e32dcede0874021'
$game='C:\Kerbal Space Program'
$install=Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$state=Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
$recordPath=Join-Path $install 'INSTALLATION.json'
$launcherPath=Join-Path $install 'Launch-Expanse-Foundations.ps1'
$record=Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
$oldVersion=[IO.Path]::GetFullPath($record.versionRoot)
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$newVersion=Join-Path $install ('versions\'+$stamp)
$backup=Join-Path $workspace ('clock256-install-backup-'+$stamp)
function Hash([string]$p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
function Under([string]$p,[string]$r){$x=[IO.Path]::GetFullPath((Join-Path $p $r));if(!$x.StartsWith([IO.Path]::GetFullPath($p).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes root'};return $x}
function GameClosed {if(@(Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue).Count){throw 'KSP is running; no installation allowed'}}
GameClosed
if(Test-Path -LiteralPath (Join-Path $workspace 'clock256-install-result.json')){throw 'Prior installation record exists; inspect before repeat'}
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
$relayBefore=Get-Content -LiteralPath $relayPath -Raw
$relayUpdated=$relayBefore.Replace("const telemetryOnly=process.argv.includes('--telemetry-only');","const telemetryOnly=process.argv.includes('--telemetry-only');"+[Environment]::NewLine+'const clockMaxFrameBytes=256*1024;')
$relayUpdated=[regex]::new('n>65536').Replace($relayUpdated,'n>clockMaxFrameBytes',1)
$relayUpdatedHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes($relayUpdated)))
if($relayUpdatedHash -ne $manifest['Relay/relay.cjs']){throw 'Current telemetry-only relay does not match the tested minimal cap transformation'}
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
foreach($f in @('relay.cjs','start.vbs','start-telemetry-only.vbs')){BackupFile (Join-Path $state ('SiteRelay\'+$f)) ('SiteRelay\'+$f)}
GameClosed
foreach($manager in $managers){$proc=Get-Process -Id $manager.ProcessId;if(!$proc.CloseMainWindow() -or !$proc.WaitForExit(15000)){throw 'Manager did not close gracefully'}}
foreach($hostApp in $hosts){$helper=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $workspace 'shutdown-clock-host.ps1')+'"'),'-TargetProcessId',$hostApp.ProcessId) -WindowStyle Hidden -PassThru -Wait;if($helper.ExitCode -ne 0){throw 'Host graceful stop helper failed'}}
if(@(Get-Process -Name Expanse.Clock.Host,Expanse.Clock.Manager -ErrorAction SilentlyContinue).Count){throw 'App still running after graceful request'}
foreach($folder in @('Recovery','ClockBridge')){$p=Join-Path $state $folder;if(Test-Path -LiteralPath $p){BackupTree $p ('HostData\'+$folder)}}
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'BACKUP-MANIFEST.json') -Encoding utf8
foreach($row in $rows){if((Hash $row.source)-ne $row.sha256){throw 'Source changed after backup'}}
foreach($relayApp in $relayProcesses){$helper=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $workspace 'shutdown-clock-host.ps1')+'"'),'-TargetProcessId',$relayApp.ProcessId) -WindowStyle Hidden -PassThru -Wait;if($helper.ExitCode -ne 0){throw 'Relay graceful stop helper failed'}}
if(Get-Process -Id $relayProcesses[0].ProcessId -ErrorAction SilentlyContinue){throw 'Old relay still running'}
GameClosed
New-Item -ItemType Directory -Path $newVersion | Out-Null
foreach($r in $manifest.Keys | Sort-Object){if(!$r.StartsWith('Host/') -and !$r.StartsWith('Manager/')){continue};$dst=Under $newVersion $r;New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null;Copy-Item -LiteralPath (Under $package $r) -Destination $dst;if((Hash $dst)-ne $manifest[$r]){throw 'Installed runtime hash mismatch'}}
foreach($r in $plugins){Copy-Item -LiteralPath (Under $package $r) -Destination (Under $game $r) -Force;if((Hash (Under $game $r))-ne $manifest[$r]){throw 'Installed plugin hash mismatch'}}
[IO.File]::WriteAllText($launcherPath,$newLauncher,[Text.UTF8Encoding]::new($false))
$updates=@{installedAtUtc=(Get-Date).ToUniversalTime().ToString('o');version=$stamp;versionRoot=$newVersion;archiveSha256=$expected;verifiedPackageFiles=$manifest.Count;backup=$backup;managerVersion=$stamp;managerRoot=(Join-Path $newVersion 'Manager');managerArchiveSha256=$expected;managerRollback=$backup;bridgeSha256=$manifest[$plugins[0]];domainSha256=$manifest[$plugins[1]];brpSha256=$manifest[$plugins[2]];trackingStationSha256=$manifest[$plugins[3]];candidateManifestSha256=(Hash (Join-Path $package 'SHA256SUMS.txt'));launchVerifiedBy='Runtime hashes verified; live qualification pending relaunch.'}
foreach($e in $updates.GetEnumerator()){$record|Add-Member NoteProperty $e.Key $e.Value -Force}
$record|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $recordPath -Encoding utf8
foreach($row in $rows | Where-Object {$_.backupRelative.StartsWith('HostData\') -or ($_.backupRelative.StartsWith('The Expanse\'))}){if((Hash $row.source)-ne $row.sha256){throw 'Unrelated save or Host data changed'}}
foreach($f in $relayHashes.Keys){if((Hash (Join-Path $state ('SiteRelay\'+$f)))-ne $relayHashes[$f]){throw 'Relay file changed'}}
if((Hash $relayPath)-ne $relayHashes['relay.cjs']){throw 'Relay changed before cap write'}
[IO.File]::WriteAllText($relayPath,$relayUpdated,[Text.UTF8Encoding]::new($false))
if((Hash $relayPath)-ne $manifest['Relay/relay.cjs']){throw 'Installed relay cap hash mismatch'}
$runDir=Join-Path $state 'Run';New-Item -ItemType Directory -Force -Path $runDir|Out-Null
$hostExe=Join-Path $newVersion 'Host\Expanse.Clock.Host.exe'
$newHost=Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDir ('host-clock256-'+$stamp+'.log')) -RedirectStandardError (Join-Path $runDir ('host-clock256-'+$stamp+'.err.log')) -PassThru
$newRelay=Start-Process -FilePath 'C:\Users\griff\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' -ArgumentList ('"'+$relayPath+'" --telemetry-only') -WorkingDirectory (Split-Path $relayPath) -WindowStyle Hidden -PassThru
$newManagerId=$null;if($managers.Count){$newManager=Start-Process -FilePath (Join-Path $newVersion 'Manager\Expanse.Clock.Manager.exe') -PassThru;$newManagerId=$newManager.Id}
GameClosed
$result=[ordered]@{status='installed-ready-for-relaunch';archiveSha256=$expected;manifestFilesVerified=$manifest.Count;versionRoot=$newVersion;backup=$backup;backupFilesVerified=$rows.Count;hostProcessId=$newHost.Id;managerProcessId=$newManagerId;previousRelayProcessId=$relayProcesses[0].ProcessId;relayProcessId=$newRelay.Id;relayMode='telemetry-only';relayMinimalCapTransformationVerified=$true;relayCredentialsAndLaunchersUnchanged=$true;clockCap=262144;effectsWolfCap=65536;saveFilesUnchanged=$true;kspLaunched=$false;liveAtlasQualification='pending relaunch'}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $workspace 'clock256-install-result.json') -Encoding utf8
$result|ConvertTo-Json -Depth 6
