param([switch]$Apply)
$ErrorActionPreference='Stop'
$workspace=$PSScriptRoot
$package=Join-Path $workspace 'ExpansePlatform-native-packed-20261006'
$archive=$package+'.zip'
$expected='1c85c031cc79ec73eaa577b1ee56bbe226cc89a5c6236525a354e36e7e0810df'
$game='C:\Kerbal Space Program'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$install=Join-Path $env:LOCALAPPDATA 'Programs\ExpanseFoundations'
$state=Join-Path $env:LOCALAPPDATA 'ExpanseFoundations'
$recordPath=Join-Path $install 'INSTALLATION.json'
$launcherPath=Join-Path $install 'Launch-Expanse-Foundations.ps1'
$relayRoot=Join-Path $state 'SiteRelay'
$relayPath=Join-Path $relayRoot 'relay.cjs'
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
function Under([string]$root,[string]$relative){$absolute=[IO.Path]::GetFullPath((Join-Path $root $relative));if(!$absolute.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escaped allowed root'};return $absolute}
function GameClosed {if(@(Get-Process -Name KSP,KSP_x64 -ErrorAction SilentlyContinue).Count){throw 'KSP is running; installation stopped'}}
function RelayProcesses {@(Get-CimInstance Win32_Process -Filter "Name='node.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($relayPath)})}
GameClosed
if(Test-Path -LiteralPath (Join-Path $workspace 'native-packed-install-result.json')){throw 'Installation receipt exists; inspect before retry'}
if((Hash $archive) -ne $expected){throw 'Reviewed archive hash mismatch'}
$manifest=@{}
foreach($line in Get-Content -LiteralPath (Join-Path $package 'SHA256SUMS.txt')){
 if($line -notmatch '^([a-fA-F0-9]{64})  (.+)$'){throw 'Invalid package manifest'}
 $sha=$Matches[1];$relative=$Matches[2];if($manifest.ContainsKey($relative)){throw 'Duplicate manifest path'}
 if((Hash (Under $package $relative)) -ne $sha){throw ('Package hash mismatch: '+$relative)};$manifest[$relative]=$sha
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try {
 if($zip.Entries.Count -ne $manifest.Count+1){throw 'Unexpected archive entries'}
 foreach($relative in @($manifest.Keys)+@('SHA256SUMS.txt')){
  $entry=$zip.GetEntry($relative);if(!$entry){throw 'Missing archive entry'}
  $stream=$entry.Open();try{$actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
  $wanted=if($relative -eq 'SHA256SUMS.txt'){Hash (Join-Path $package $relative)}else{$manifest[$relative]}
  if($actual -ne $wanted){throw 'Archive entry hash mismatch'}
 }
}finally{$zip.Dispose()}
$sourceHashes=Get-Content (Join-Path $package 'Evidence\native-packed-build-source-hashes.json') -Raw|ConvertFrom-Json
foreach($p in $sourceHashes.PSObject.Properties){if((Hash (Under $primary $p.Name)) -ne $p.Value){throw ('Primary source changed since reviewed build: '+$p.Name)}}
foreach($dll in @('Expanse.Clock.Core.dll','Expanse.Domain.dll')){if($manifest['Host/'+$dll] -ne $manifest['Manager/'+$dll]){throw 'Package dependency mismatch'}}
$record=Get-Content -LiteralPath $recordPath -Raw|ConvertFrom-Json
$oldVersion=[IO.Path]::GetFullPath($record.versionRoot)
if(!$oldVersion.StartsWith($install+'\versions\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected installed root'}
$runtimeBefore=Get-Content (Join-Path $package 'Evidence\native-packed-runtime-before.json') -Raw|ConvertFrom-Json
foreach($p in $runtimeBefore.PSObject.Properties){if((Hash $p.Name) -ne $p.Value){throw ('Installed component changed since review: '+$p.Name)}}
$hosts=@(Get-Process -Name Expanse.Clock.Host -ErrorAction SilentlyContinue)
if($hosts.Count -gt 1 -or @($hosts|Where-Object {$_.Path -ne (Join-Path $oldVersion 'Host\Expanse.Clock.Host.exe')}).Count){throw 'Unexpected running Host'}
if(@(Get-Process -Name Expanse.Clock.Manager -ErrorAction SilentlyContinue).Count){throw 'Manager running; only Host restart is authorized'}
$relayBefore=@(RelayProcesses)
if($relayBefore.Count -gt 1 -or @($relayBefore|Where-Object {!$_.CommandLine.Contains('--telemetry-only')}).Count){throw 'Unexpected relay process/mode'}
$relayHashes=@{};foreach($name in @('relay.cjs','start.vbs','start-telemetry-only.vbs','credentials.dpapi')){$relayHashes[$name]=Hash (Join-Path $relayRoot $name)}
if($relayHashes['relay.cjs'] -ne $manifest['Relay/relay.cjs']){throw 'Relay differs from reviewed telemetry-only implementation'}
$plugins=@('GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.Domain.dll','GameData/ExpanseWorldBridge/Plugins/Expanse.BrpColony.dll','GameData/ExpanseTrackingStation/Plugins/Expanse.TrackingStation.dll')
foreach($relative in $plugins){if(!(Test-Path -LiteralPath (Under $game $relative))){throw 'An existing component was removed; do not restore removed mods'}}
$launcher=Get-Content -LiteralPath $launcherPath -Raw
$oldSelector='versions\'+$record.version
if(!$launcher.Contains($oldSelector)){throw 'Launcher version selector mismatch'}
$saveRoot=Join-Path $game 'saves\The Expanse'
$saveHashes=@{};foreach($file in Get-ChildItem -LiteralPath $saveRoot -Recurse -File){$saveHashes[$file.FullName]=Hash $file.FullName}
$preflight=[ordered]@{status='preflight-pass';archiveSha256=$expected;manifestFiles=$manifest.Count;primarySourceFiles=@($sourceHashes.PSObject.Properties).Count;oldVersion=$oldVersion;kspAbsent=$true;hostIds=@($hosts.Id);relayIds=@($relayBefore|ForEach-Object ProcessId);relayAlreadyStopped=($relayBefore.Count -eq 0);relayFilesWillRemainUnchanged=$true;saveFilesHashed=$saveHashes.Count}
$preflight|ConvertTo-Json -Depth 4|Set-Content (Join-Path $workspace 'native-packed-install-preflight.json')
if(!$Apply){$preflight|ConvertTo-Json -Depth 4;return}
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$newVersion=Join-Path $install ('versions\'+$stamp)
$backup=Join-Path $workspace ('native-packed-install-backup-'+$stamp)
if((Test-Path -LiteralPath $newVersion) -or (Test-Path -LiteralPath $backup)){throw 'Install or backup path already exists'}
New-Item -ItemType Directory -Path $backup|Out-Null
$rows=[Collections.Generic.List[object]]::new()
function BackupFile([string]$source,[string]$relative){$dest=Under $backup $relative;New-Item -ItemType Directory -Force -Path (Split-Path $dest)|Out-Null;$sha=Hash $source;Copy-Item -LiteralPath $source -Destination $dest;if((Hash $dest) -ne $sha -or (Hash $source) -ne $sha){throw 'Backup hash mismatch'};$rows.Add([pscustomobject]@{source=$source;relative=$relative;sha256=$sha})}
function BackupTree([string]$source,[string]$relative){foreach($file in Get-ChildItem -LiteralPath $source -Recurse -File){BackupFile $file.FullName (Join-Path $relative $file.FullName.Substring($source.Length).TrimStart('\'))}}
BackupTree $oldVersion 'previous-version'
foreach($relative in $plugins){BackupFile (Under $game $relative) ('plugins\'+$relative)}
foreach($name in @('INSTALLATION.json','Launch-Expanse-Foundations.ps1','Launch-Expanse-Foundations.cmd')){BackupFile (Join-Path $install $name) ('installation\'+$name)}
$saveHashes|ConvertTo-Json -Depth 3|Set-Content (Join-Path $backup 'UNCHANGED-SAVES.json')
$relayHashes|ConvertTo-Json|Set-Content (Join-Path $backup 'UNCHANGED-RELAY.json')
$rows|ConvertTo-Json -Depth 4|Set-Content (Join-Path $backup 'BACKUP-MANIFEST.json')
GameClosed
foreach($hostApp in $hosts){
 $helper=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $workspace 'shutdown-clock-host.ps1')+'"'),'-TargetProcessId',$hostApp.Id) -WindowStyle Hidden -PassThru -Wait
 if($helper.ExitCode -ne 0){throw 'Graceful Host shutdown failed; no forced stop'}
}
if(@(Get-Process -Name Expanse.Clock.Host -ErrorAction SilentlyContinue).Count){throw 'Host still running'}
foreach($folder in @('Recovery','ClockBridge')){$path=Join-Path $state $folder;if(Test-Path -LiteralPath $path){BackupTree $path ('HostData\'+$folder)}}
$rows|ConvertTo-Json -Depth 4|Set-Content (Join-Path $backup 'BACKUP-MANIFEST.json')
foreach($row in $rows){if((Hash $row.source) -ne $row.sha256){throw 'Source changed after backup'}}
GameClosed
New-Item -ItemType Directory -Path $newVersion|Out-Null
$installed=@{}
foreach($relative in $manifest.Keys|Sort-Object){
 if(!$relative.StartsWith('Host/') -and !$relative.StartsWith('Manager/')){continue}
 $dest=Under $newVersion $relative;New-Item -ItemType Directory -Force -Path (Split-Path $dest)|Out-Null
 Copy-Item -LiteralPath (Under $package $relative) -Destination $dest
 if((Hash $dest) -ne $manifest[$relative]){throw 'Installed app hash mismatch'};$installed[$relative]=Hash $dest
}
$pluginsWritten=[Collections.Generic.List[string]]::new()
foreach($relative in $plugins){
 GameClosed;$dest=Under $game $relative
 if((Hash $dest) -ne $manifest[$relative]){Copy-Item -LiteralPath (Under $package $relative) -Destination $dest -Force;$pluginsWritten.Add($relative)}
 if((Hash $dest) -ne $manifest[$relative]){throw 'Installed plugin hash mismatch'};$installed[$relative]=Hash $dest
}
[IO.File]::WriteAllText($launcherPath,$launcher.Replace($oldSelector,('versions\'+$stamp)),[Text.UTF8Encoding]::new($false))
$updates=@{installedAtUtc=(Get-Date).ToUniversalTime().ToString('o');version=$stamp;versionRoot=$newVersion;archiveSha256=$expected;verifiedPackageFiles=$manifest.Count;backup=$backup;managerVersion=$stamp;managerRoot=(Join-Path $newVersion 'Manager');managerArchiveSha256=$expected;managerRollback=$backup;bridgeSha256=$manifest[$plugins[0]];domainSha256=$manifest[$plugins[1]];brpSha256=$manifest[$plugins[2]];trackingStationSha256=$manifest[$plugins[3]];candidateManifestSha256=(Hash (Join-Path $package 'SHA256SUMS.txt'));launchVerifiedBy='Reviewed native packed callback package installed; live qualification pending user launch.'}
foreach($entry in $updates.GetEnumerator()){$record|Add-Member NoteProperty $entry.Key $entry.Value -Force}
$record|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $recordPath -Encoding utf8
$runDir=Join-Path $state 'Run';New-Item -ItemType Directory -Force -Path $runDir|Out-Null
$hostExe=Join-Path $newVersion 'Host\Expanse.Clock.Host.exe'
$stdout=Join-Path $runDir ('host-native-packed-'+$stamp+'.log');$stderr=Join-Path $runDir ('host-native-packed-'+$stamp+'.err.log')
$newHost=Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path $hostExe) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
foreach($path in $saveHashes.Keys){if((Hash $path) -ne $saveHashes[$path]){throw 'Save file changed'}}
foreach($name in $relayHashes.Keys){if((Hash (Join-Path $relayRoot $name)) -ne $relayHashes[$name]){throw 'Relay file or credential changed'}}
$relayAfter=@(RelayProcesses)
if((@($relayAfter.ProcessId|Sort-Object) -join ',') -ne (@($relayBefore.ProcessId|Sort-Object) -join ',')){throw 'Relay process identity changed externally'}
if(@($relayAfter|Where-Object {!$_.CommandLine.Contains('--telemetry-only')}).Count){throw 'Relay mode changed'}
GameClosed
$installed|ConvertTo-Json|Set-Content (Join-Path $workspace 'native-packed-installed-hashes.json')
$result=[ordered]@{status='installed-ready-for-user-launch';archiveSha256=$expected;versionRoot=$newVersion;backup=$backup;backupFilesVerified=$rows.Count;manifestFilesVerified=$manifest.Count;installedFilesVerified=$installed.Count;pluginsWritten=@($pluginsWritten);oldHostIds=@($hosts.Id);hostProcessId=$newHost.Id;hostStdout=$stdout;hostStderr=$stderr;managerStarted=$false;relayProcessIds=@($relayAfter|ForEach-Object ProcessId);relayAlreadyStopped=($relayBefore.Count -eq 0);relayProcessPreserved=$true;relayFilesAndCredentialsUnchanged=$true;relayStartedOrRestarted=$false;wolfCommandsProcessed=$false;clockCap=262144;effectsWolfCap=65536;saveFilesUnchanged=$true;saveFilesVerified=$saveHashes.Count;kspLaunched=$false;liveCallbackProof='pending user launch'}
$result|ConvertTo-Json -Depth 5|Set-Content (Join-Path $workspace 'native-packed-install-result.json')
$result|ConvertTo-Json -Depth 5
