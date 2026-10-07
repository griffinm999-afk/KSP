[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][int]$ExpectedProcessId,
 [Parameter(Mandatory=$true)][guid]$OperationId,
 [Parameter(Mandatory=$true)][string]$NativeSaveName,
 [Parameter(Mandatory=$true)][string]$NativeSaveSha256,
 [Parameter(Mandatory=$true)][string]$WitnessName,
 [string[]]$AdditionalEvidence=@(),
 [switch]$StopVerifiedProcess
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifestPath=Join-Path $workspace 'ExpansePlatform\run\colony-development.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$exe=Join-Path $root 'KSP_x64.exe'
$pipe='-expanseColonyPipe=ExpanseFoundations.Colonies.dev.'+[Environment]::UserName+'.'+$manifest.Token
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Private([string]$Path){
 $p=[IO.Path]::GetFullPath($Path);if(!$p.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Archive read leaves private installation'}
 for($a=$p;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Archive refuses reparse path'}}
}
function ProcessGuard {
 $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$ExpectedProcessId)
 if($manifest.ProcessId -ne $ExpectedProcessId -or $manifest.Executable -ine $exe -or !$p -or $p.ExecutablePath -ine $exe -or !$p.CommandLine.Contains($pipe) -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$'){throw 'Exact isolated process/save/token mismatch'}
}
ProcessGuard
if($NativeSaveName -notmatch '^colony-test-[a-zA-Z0-9-]{1,80}$' -or $WitnessName -notmatch '^colony-placement-witness-[0-9]{8}-[0-9]{6}-[0-9]{3}\.txt$'){throw 'Bounded diagnostic names required'}
$native=Join-Path $root ('saves\'+$manifest.SaveFolder+'\'+$NativeSaveName+'.sfs');Private $native
if((Hash $native) -ine $NativeSaveSha256){throw 'Actual diagnostic save bytes changed'}
$id=$OperationId.ToString('D');$text=Get-Content -LiteralPath $native -Raw
if(!$text.Contains('OperationId = '+$id) -or !$text.Contains('Stage = RecoveryHold')){throw 'Native save lacks retained operation/hold'}
$witness=Join-Path $root $WitnessName;Private $witness
$log=Get-Content -LiteralPath $witness -Raw
if(!$log.Contains('save='+$manifest.SaveFolder+' operation='+$id) -or !$log.Contains('FAIL ')){throw 'Exact native failed diagnostic witness missing'}
Private $manifest.SourcePath
if((Hash $manifest.SourcePath) -ine $manifest.SourceSha256){throw 'Original actual source changed'}
foreach($a in $manifest.Artifacts){$p=Join-Path $root $a.Path;Private $p;if((Hash $p) -ine $a.Sha256){throw 'Installed frozen artifact changed'}}
$archive=Join-Path $workspace ('outputs\colony-runtime-tests\native12-contact-held-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $archive | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $archive 'manifest.json')
$files=@($WitnessName,'KSP.log','colony-placement-watch.cfg',('colony-placement-contact-diagnostic-'+$id+'.cfg'),('colony-placement-status-'+$id+'.cfg'),('colony-placement-loaded-parts-'+$id+'.cfg'),('colony-placement-sweep-'+$id+'.cfg'))
foreach($name in $AdditionalEvidence){if($name -notmatch '^colony-placement-body-evidence-[a-f0-9-]{36}\.cfg$'){throw 'Additional diagnostic evidence name refused'};$files+=$name}
$proof=@()
foreach($name in $files){$p=Join-Path $root $name;Private $p;if(Test-Path -LiteralPath $p -PathType Leaf){$dest=Join-Path $archive $name;Copy-Item -LiteralPath $p -Destination $dest;$proof+=@{Name=$name;Sha256=(Hash $dest)}}}
Copy-Item -LiteralPath $native -Destination (Join-Path $archive ([IO.Path]::GetFileName($native)))
Copy-Item -LiteralPath $manifest.SourcePath -Destination (Join-Path $archive 'original-native-source.sfs')
@{Outcome='Failed independent exact product operation retained; no anchor/recovery/retry/repair/certification';ProcessId=$ExpectedProcessId;SaveFolder=$manifest.SaveFolder;OperationId=$id;NativeSaveSha256=(Hash $native);ImmutableSource=$manifest.SourcePath;ImmutableSourceSha256=(Hash $manifest.SourcePath);Evidence=$proof;ArtifactHashes=$manifest.Artifacts;ObserverStatusMayPrecedeLaterProductHold=$true} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $archive 'preservation.json')
if((Hash $native) -ine $NativeSaveSha256 -or (Hash $manifest.SourcePath) -ine $manifest.SourceSha256){throw 'Native evidence changed during copy'}
if($StopVerifiedProcess){ProcessGuard;Stop-Process -Id $ExpectedProcessId;Wait-Process -Id $ExpectedProcessId -Timeout 20 -ErrorAction SilentlyContinue;if(Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue){throw 'Exact isolated process did not stop'}}
Write-Output $archive
