[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][int]$ExpectedProcessId,
 [Parameter(Mandatory=$true)][guid]$OperationId,
 [Parameter(Mandatory=$true)][string]$NativeSaveName,
 [Parameter(Mandatory=$true)][string]$NativeSaveSha256,
 [Parameter(Mandatory=$true)][string]$WitnessName,
 [Parameter(Mandatory=$true)][ValidateSet('latent-scatter-negative','native-anchored','native-cold')][string]$Outcome,
 [string[]]$AdditionalEvidence=@(),
 [switch]$StopVerifiedProcess
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$root='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifestPath=Join-Path $workspace 'ExpansePlatform\run\colony-development.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$exe=Join-Path $root 'KSP_x64.exe'
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function PrivateRead([string]$Path){
 $full=[IO.Path]::GetFullPath($Path)
 if(!$full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence read leaves isolated root'}
 for($p=$full;$p;$p=[IO.Path]::GetDirectoryName($p)){if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Evidence reparse path refused'}}
}
function ProcessGuard {
 $native=Get-CimInstance Win32_Process -Filter ('ProcessId='+$ExpectedProcessId)
 $pipe='-expanseColonyPipe=ExpanseFoundations.Colonies.dev.'+[Environment]::UserName+'.'+$manifest.Token
 if(!$native -or $manifest.ProcessId -ne $ExpectedProcessId -or $manifest.Executable -ine $exe -or $native.ExecutablePath -ine $exe -or !$native.CommandLine.Contains($pipe) -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$'){throw 'Exact private process/save/token guard failed'}
}
ProcessGuard
if($OperationId -eq [guid]::Empty -or $NativeSaveName.Length -gt 160 -or $NativeSaveName -notmatch '^(colony-placement|colony-test)-[a-zA-Z0-9-]+$' -or $WitnessName -notmatch '^colony-placement-witness-[0-9]{8}-[0-9]{6}-[0-9]{3}\.txt$'){throw 'Exact bounded native names required'}
$id=$OperationId.ToString('D')
$save=Join-Path $root ('saves\'+$manifest.SaveFolder+'\'+$NativeSaveName+'.sfs');PrivateRead $save
$witness=Join-Path $root $WitnessName;PrivateRead $witness
if((Hash $save) -ine $NativeSaveSha256){throw 'Actual native save hash differs'}
$log=Get-Content -LiteralPath $witness -Raw
if(!$log.Contains('save='+$manifest.SaveFolder+' operation='+$id)){throw 'Watcher does not own exact selected native operation'}
$requiredPass=switch($Outcome){'latent-scatter-negative'{'PASS native terrain latent-scatter rejection before assembly; held duplicate120frames; buildings=0'} 'native-anchored'{'PASS actual KSP save readback'} 'native-cold'{'PASS cold-process native reload retained exact vessel/part/flight/marker/Foundation identities'}}
if(!$log.Contains($requiredPass)){throw 'Expected actual bounded native watcher result missing'}
$nativeText=Get-Content -LiteralPath $save -Raw
if(!$nativeText.Contains('OperationId = '+$id)){throw 'Exact operation absent from actual native save'}
if($Outcome -eq 'latent-scatter-negative' -and (!$nativeText.Contains('Stage = RecoveryHold') -or !$nativeText.Contains('SurfaceCollisionWitness = '))){throw 'Expected native negative receipt/provider provenance missing'}
PrivateRead $manifest.SourcePath
if((Hash $manifest.SourcePath) -ine $manifest.SourceSha256){throw 'Immutable native source differs'}
foreach($artifact in $manifest.Artifacts){$path=Join-Path $root $artifact.Path;PrivateRead $path;if((Hash $path) -ine $artifact.Sha256){throw 'Installed coherent native artifact differs'}}
$checkpointName=Split-Path -Leaf (Split-Path -Parent $manifest.FreezeManifest)
if($checkpointName -notmatch '^(native[0-9]{2})-frozen-'){throw 'Reviewed native freeze identity is absent'}
$archive=Join-Path $workspace ('outputs\colony-runtime-tests\'+$Matches[1]+'-'+$Outcome+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(!$archive.StartsWith((Join-Path $workspace 'outputs')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $archive)){throw 'Review archive target invalid or exists'}
New-Item -ItemType Directory -Path $archive | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $archive 'manifest.json')
$files=@($WitnessName,'KSP.log','colony-placement-watch.cfg',('colony-placement-status-'+$id+'.cfg'),('colony-placement-loaded-parts-'+$id+'.cfg'),('colony-placement-terrain-rejection-'+$id+'.cfg'),('colony-placement-envelope-'+$id+'.cfg'),('colony-placement-sweep-'+$id+'.cfg'),('colony-placement-footing-'+$id+'.cfg'),('colony-placement-ground-positioning-'+$id+'.cfg'),('colony-placement-support-terrain-'+$id+'.cfg'))
foreach($name in $AdditionalEvidence){if($name -notmatch '^colony-(placement-[a-z-]+|profile)-[a-f0-9-]{36}\.cfg$' -and $name -notmatch '^colony-placement-support-terrain-[a-f0-9-]{36}-cold-[1-9][0-9]{0,9}\.cfg$'){throw 'Additional exact evidence name refused'};$files+=$name}
$proof=@()
foreach($name in $files){$path=Join-Path $root $name;PrivateRead $path;if(Test-Path -LiteralPath $path -PathType Leaf){$dest=Join-Path $archive $name;Copy-Item -LiteralPath $path -Destination $dest;$proof+=@{Name=$name;Sha256=(Hash $dest)}}}
Copy-Item -LiteralPath $save -Destination (Join-Path $archive ([IO.Path]::GetFileName($save)))
Copy-Item -LiteralPath $manifest.SourcePath -Destination (Join-Path $archive 'original-native-source.sfs')
@{Outcome=$Outcome;Authority='Actual isolated native physical probe only; no economy or runtime certification';ProcessId=$ExpectedProcessId;SaveFolder=$manifest.SaveFolder;OperationId=$id;NativeSaveSha256=(Hash $save);ImmutableSource=$manifest.SourcePath;ImmutableSourceSha256=(Hash $manifest.SourcePath);Evidence=$proof;ArtifactHashes=$manifest.Artifacts} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $archive 'preservation.json')
if((Hash $save) -ine $NativeSaveSha256 -or (Hash $manifest.SourcePath) -ine $manifest.SourceSha256){throw 'Native bytes changed during archive'}
if($StopVerifiedProcess){ProcessGuard;Stop-Process -Id $ExpectedProcessId;Wait-Process -Id $ExpectedProcessId -Timeout 20 -ErrorAction SilentlyContinue;if(Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue){throw 'Exact isolated process did not stop'}}
Write-Output $archive
