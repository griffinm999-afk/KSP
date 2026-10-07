$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$receipt=Join-Path $taskRoot 'host-colony-diagnostic-install.json'
if(Test-Path -LiteralPath $receipt){throw 'Diagnostic installation already recorded; do not repeat'}
$install=Get-Content -LiteralPath (Join-Path $taskRoot 'clock256-install-result.json') -Raw|ConvertFrom-Json
$runtime=Join-Path $install.versionRoot 'Host'
$exe=Join-Path $runtime 'Expanse.Clock.Host.exe'
$stage=Join-Path $taskRoot 'colony-diagnostic-host'
$manifest=Get-Content -LiteralPath (Join-Path $taskRoot 'colony-diagnostic-host-hashes.json') -Raw|ConvertFrom-Json
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
$oldHashes=@{'Expanse.Clock.Core.dll'='8C5575C4695D40122F6EDFB61D31BEF726C625C50DB471D00CF589E40CECF3B9';'Expanse.Clock.Host.dll'='26D9F063CDC992D9DEA51EB7C9FC09E650921676671130213506F7038CF646D0'}
foreach($name in $oldHashes.Keys){if((Hash (Join-Path $runtime $name)) -ne $oldHashes[$name]){throw 'Installed diagnostic target changed'}; $expected=($manifest|Where-Object path -eq $name).sha256;if((Hash (Join-Path $stage $name)) -ne $expected){throw 'Staged diagnostic target changed'}}
foreach($name in @('Expanse.Clock.Host.exe','Expanse.Clock.Host.deps.json','Expanse.Clock.Host.runtimeconfig.json')){if((Hash (Join-Path $stage $name)) -ne (Hash (Join-Path $runtime $name))){throw 'Runtime launcher or dependency configuration differs'}}
$hosts=@(Get-CimInstance Win32_Process -Filter "Name='Expanse.Clock.Host.exe'")
if($hosts.Count -ne 1 -or $hosts[0].ExecutablePath -ne $exe -or $hosts[0].CommandLine.Trim().Trim('"') -ne $exe){throw 'Expected one current Host using default configured endpoints'}
$game=@(Get-Process -Name KSP_x64,KSP -ErrorAction SilentlyContinue|Select-Object -ExpandProperty Id)
if(!$game.Count){throw 'Expected KSP to remain running'}
$relay=@(Get-CimInstance Win32_Process -Filter "Name='node.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains('\SiteRelay\relay.cjs')})
if($relay.Count -ne 1 -or !$relay[0].CommandLine.Contains('--telemetry-only')){throw 'Expected one existing telemetry-only relay'}
$unchanged=@{}
foreach($f in Get-ChildItem -LiteralPath $runtime -Recurse -File){if($f.Name -notin $oldHashes.Keys){$unchanged[$f.FullName]=Hash $f.FullName}}
foreach($relative in @('SiteRelay\relay.cjs','SiteRelay\credentials.dpapi','SiteRelay\start.vbs','SiteRelay\start-telemetry-only.vbs')){$p=Join-Path 'C:\Users\griff\AppData\Local\ExpanseFoundations' $relative;$unchanged[$p]=Hash $p}
foreach($relative in @('GameData\ExpanseWorldBridge\Plugins\Expanse.WorldBridge.dll','GameData\ExpanseWorldBridge\Plugins\Expanse.Domain.dll','GameData\ExpanseWorldBridge\Plugins\Expanse.BrpColony.dll','GameData\ExpanseTrackingStation\Plugins\Expanse.TrackingStation.dll')){$p=Join-Path 'C:\Kerbal Space Program' $relative;$unchanged[$p]=Hash $p}
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$backup=Join-Path $taskRoot ('host-colony-diagnostic-backup-'+$stamp)
New-Item -ItemType Directory -Path $backup|Out-Null
foreach($name in $oldHashes.Keys){Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $backup $name);if((Hash (Join-Path $backup $name)) -ne $oldHashes[$name]){throw 'Backup verification failed'}}
[pscustomobject]@{runtime=$runtime;oldHashes=$oldHashes;unchangedHashes=$unchanged;sourceHashesFile=(Join-Path $taskRoot 'colony-diagnostic-source-hashes.json');oldHostPid=$hosts[0].ProcessId;relayPid=$relay[0].ProcessId;kspPids=$game}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $backup 'backup-manifest.json')
$stop=Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',('"'+(Join-Path $taskRoot 'shutdown-clock-host.ps1')+'"'),'-TargetProcessId',$hosts[0].ProcessId) -WindowStyle Hidden -PassThru -Wait
if($stop.ExitCode -ne 0 -or @(Get-Process -Name Expanse.Clock.Host -ErrorAction SilentlyContinue).Count){throw 'Host did not stop gracefully; no force stop or overwrite attempted'}
foreach($name in $oldHashes.Keys){if((Hash (Join-Path $runtime $name)) -ne $oldHashes[$name]){throw 'Installed file changed after backup'};Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $runtime $name) -Force;if((Hash (Join-Path $runtime $name)) -ne ($manifest|Where-Object path -eq $name).sha256){throw 'Installed diagnostic hash verification failed'}}
foreach($p in $unchanged.Keys){if((Hash $p) -ne $unchanged[$p]){throw 'Unrelated component changed during diagnostic installation'}}
$logs='C:\Users\griff\AppData\Local\ExpanseFoundations\Run'
$out=Join-Path $logs ('host-colony-diagnostic-'+$stamp+'.log')
$err=Join-Path $logs ('host-colony-diagnostic-'+$stamp+'.err.log')
$newHost=Start-Process -FilePath $exe -WorkingDirectory $runtime -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
Start-Sleep -Seconds 2
if($newHost.HasExited -or @(Get-Process -Name Expanse.Clock.Host -ErrorAction SilentlyContinue).Count -ne 1){throw 'Diagnostic Host did not remain the sole running Host'}
foreach($id in $game){if(!(Get-Process -Id $id -ErrorAction SilentlyContinue)){throw 'KSP exited during installation'}}
if(!(Get-Process -Id $relay[0].ProcessId -ErrorAction SilentlyContinue)){throw 'Existing relay exited during installation'}
$result=[pscustomobject]@{status='installed-host-only-diagnostic';runtime=$runtime;backup=$backup;oldHostPid=$hosts[0].ProcessId;newHostPid=$newHost.Id;relayPid=$relay[0].ProcessId;relayRestarted=$false;kspRestarted=$false;replacedFiles=@($oldHashes.Keys);unrelatedComponentsUnchanged=$true;errorLog=$err;outputLog=$out;clockCap=262144;effectsCap=65536;rawPayloadLogging=$false;sourceProvenance=(Join-Path $taskRoot 'colony-diagnostic-source-hashes.json')}
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $receipt
$result|ConvertTo-Json -Depth 8
