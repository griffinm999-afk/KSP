[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$FreezeManifest,
 [Parameter(Mandatory=$true)][string]$SourceSave,
 [Parameter(Mandatory=$true)][string]$SourceSha256,
 [Parameter(Mandatory=$true)][string]$CaseJson,
 [Parameter(Mandatory=$true)][guid]$ReferenceVesselId,
 [switch]$SingleColdReload,
 [switch]$SinglePlacement,
 [switch]$SingleRecovery,
 [switch]$BodyLoadedReference,
 [switch]$StreetGroup,
 [switch]$ColdPackageBatch,
 [string]$PlacementHarnessSnapshot,
 [string]$PlacementHarnessSha256,
 [string]$GeneralHarnessSnapshot,
 [string]$GeneralHarnessSha256
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$devRoot='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$exe=Join-Path $devRoot 'KSP_x64.exe'
function Hash([string]$Path) {(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Private([string]$Path,[switch]$ReadOnly) {
 $absolute=[IO.Path]::GetFullPath($Path)
 if ($absolute -ne $devRoot -and !$absolute.StartsWith($devRoot+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Native target leaves isolated root.'}
 for($a=$absolute;$a;$a=[IO.Path]::GetDirectoryName($a)) {if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Native reparse path refused.'}}
 if(!$ReadOnly -and (Test-Path -LiteralPath $absolute -PathType Leaf)) {$links=@(& fsutil hardlink list $absolute);if($LASTEXITCODE -ne 0 -or $links.Count -ne 1){throw 'Native mutable target is not a private file.'}}
}
Private $exe -ReadOnly
if(@(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'" | Where-Object {!$_.ExecutablePath -or $_.ExecutablePath -ieq $exe}).Count){throw 'An isolated/unidentified game is running.'}
$source=[IO.Path]::GetFullPath($SourceSave);Private $source
if($source -notmatch '\\saves\\ColonyBuild-[0-9]{8}-[0-9]{6}\\[^\\]+\.sfs$' -or (Hash $source) -ine $SourceSha256){throw 'Exact original native source hash failed.'}
$freezePath=[IO.Path]::GetFullPath($FreezeManifest)
if(!$freezePath.StartsWith((Join-Path $workspace 'outputs')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Freeze leaves reviewed outputs.'}
$freeze=Get-Content -LiteralPath $freezePath -Raw | ConvertFrom-Json
$freezeRoot=Split-Path -Parent $freezePath
if($freeze.Artifacts.Count -notin @(25,29) -or !$freeze.RequireProfile -or @($freeze.Artifacts.Path | Select-Object -Unique).Count -ne $freeze.Artifacts.Count){throw 'Expected coherent unique 25/29-artifact profiled freeze absent.'}
if($freeze.Artifacts.Count -eq 29){
 foreach($variant in @('cultivation-duna-v1.craft','cultivation-duna-v1.manifest.json','cultivation-feeds-v1.craft','cultivation-feeds-v1.manifest.json')){
  if(('GameData\ExpanseWorldBridge\Templates\'+$variant) -notin $freeze.Artifacts.Path){throw 'Coherent29 freeze must contain exactly the four reviewed cultivation variant files.'}
 }
}
$terms=Get-Content -LiteralPath $CaseJson -Raw | ConvertFrom-Json
if(@($SingleColdReload,$SinglePlacement,$SingleRecovery,$BodyLoadedReference,$StreetGroup,$ColdPackageBatch | Where-Object {$_}).Count -gt 1){throw 'Select one bounded native purpose.'}
$groupCold=$false
$groupExisting=$false
if($StreetGroup -or $ColdPackageBatch){
 $groupCold=$terms.WatcherFields.mode -in @('cold-street-group','cold-package-batch')
 if($StreetGroup){
  $expectedPackages=@('housing-kpbs-v1','service-kpbs-v1','storage-kpbs-v1','lamp-stock-v1','power-duna-v1')
  if($terms.Cases.Count -ne 5 -or $terms.WatcherFields.mode -notin @('native-street-group','cold-street-group') -or $terms.WatcherFields.batchAuthorization -ne 'five-exact-street-packages-no-economic-certification' -or $terms.Authority -ne 'Five exact isolated native street packages; no economy or runtime certification' -or $terms.Body -notin @('Mun','Duna')){throw 'Exact five-package Mun/Duna street review required.'}
  for($i=0;$i -lt 5;$i++){if($terms.Cases[$i].PackageId -ne $expectedPackages[$i]){throw 'Street package sequence differs.'}}
  $groupExisting=!$groupCold -and $terms.Cases[0].Fields.existingAuthorization -eq 'original-anchored-housing-no-new-enqueue'
 }elseif($terms.Cases.Count -ne 8 -or !$groupCold -or $terms.WatcherFields.mode -ne 'cold-package-batch' -or $terms.Authority -ne 'Eight original actual native cold reloads; no new enqueue or assembly'){throw 'Exact eight original cold package review required.'}
 foreach($entry in $terms.Cases){
  if($groupCold -or $entry.Fields.existingAuthorization){
   if((!$groupCold -and $entry -ne $terms.Cases[0]) -or $entry.Fields.reloadSourceSha256 -ine $SourceSha256 -or $entry.Fields.reloadSourceName -ne [IO.Path]::GetFileNameWithoutExtension($source) -or [int]$entry.Fields.reloadPriorProcessId -le 0 -or $entry.Fields.enqueueRequestName){throw 'Original native group readback must bind exact source and independent process, without enqueue.'}
  }
 }
}
if($BodyLoadedReference){
 if($terms.Cases.Count -ne 0 -or $terms.Body -notin @('Mun','Duna') -or $terms.ReferenceVesselId -ne $ReferenceVesselId.ToString('D') -or $terms.NativeSourceSha256 -ine $SourceSha256 -or $terms.Authorization -ne 'load-existing-empty-native-reference-read-only-survey'){throw 'Exact existing empty native body fixture source/reference required.'}
}elseif($SinglePlacement){
 if($terms.Cases.Count -ne 1 -or $terms.WatcherFields.mode -notin @('native-placement','native-clearance-rejection','native-terrain-rejection') -or $terms.Authority -ne 'One exact isolated product probe; no economy or runtime certification' -or $terms.WatcherFields.operationId -ne $terms.Cases[0].Fields.operationId -or $terms.WatcherFields.enqueueRequestName -ne [IO.Path]::GetFileName($terms.Cases[0].Request.RequestPath) -or $terms.WatcherFields.enqueueRequestSha256 -ine $terms.Cases[0].Request.RequestSha256){throw 'Exact one reviewed new product placement request required.'}
}elseif($SingleRecovery){
 if($terms.Cases.Count -ne 1 -or $terms.WatcherFields.mode -ne 'recover-existing-native-building' -or $terms.WatcherFields.recoveryAuthorization -ne 'existing-marked-building-native-reconcile-only-no-respawn' -or $terms.WatcherFields.enqueueRequestName -or $terms.WatcherFields.injectPhase -or $terms.WatcherFields.reloadSourceSha256 -ine $SourceSha256 -or $terms.WatcherFields.reloadSourceName -ne [IO.Path]::GetFileNameWithoutExtension($source)){throw 'Exact original held source/operation and existing-building recovery authorization required.'}
}elseif($SingleColdReload){
 if($terms.Cases.Count -ne 1 -or $terms.WatcherFields.mode -ne 'cold-native-reload' -or $terms.WatcherFields.reloadAuthorization -ne 'cold-native-reload-no-new-spawn' -or $terms.WatcherFields.enqueueRequestName -or $terms.WatcherFields.injectPhase -or $terms.WatcherFields.reloadSourceSha256 -ine $SourceSha256 -or $terms.WatcherFields.reloadSourceName -ne [IO.Path]::GetFileNameWithoutExtension($source)){throw 'Exact one existing native cold operation/source required.'}
}elseif(!$StreetGroup -and !$ColdPackageBatch -and ($terms.Cases.Count -ne 8 -or $terms.WatcherFields.mode -ne 'native-package-batch')){throw 'Exactly eight fresh candidate package probes required.'}
$install=@()
foreach($artifact in $freeze.Artifacts){
 if($artifact.Path -notmatch '^GameData\\(ExpanseWorldBridge|ExpanseColonyDev)\\' -or $artifact.Path.Contains('..')){throw 'Freeze relative target invalid.'}
 $origin=[IO.Path]::GetFullPath((Join-Path $freezeRoot $artifact.Path));if(!$origin.StartsWith($freezeRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $origin) -ine $artifact.Sha256){throw 'Frozen artifact hash failed.'}
 $install+=@{Source=$origin;Path=$artifact.Path;Sha256=$artifact.Sha256}
}
if($PlacementHarnessSnapshot){
 $harness=[IO.Path]::GetFullPath($PlacementHarnessSnapshot)
 if(!$harness.StartsWith((Join-Path $workspace 'outputs')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $harness) -ine $PlacementHarnessSha256){throw 'Reviewed derived harness hash failed.'}
 $replacement=$install | Where-Object Path -eq 'GameData\ExpanseColonyDev\Plugins\Expanse.ColonyPlacement.KspHarness.dll'
 if(!$replacement){throw 'Frozen placement harness mapping missing.'};$replacement.Source=$harness;$replacement.Sha256=$PlacementHarnessSha256
}
if($GeneralHarnessSnapshot){
 $generalHarness=[IO.Path]::GetFullPath($GeneralHarnessSnapshot)
 if(!$generalHarness.StartsWith((Join-Path $workspace 'outputs')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $generalHarness) -ine $GeneralHarnessSha256){throw 'Reviewed derived general harness hash failed.'}
 $replacement=$install | Where-Object Path -eq 'GameData\ExpanseColonyDev\Plugins\Expanse.Colony.KspHarness.dll'
 if(!$replacement){throw 'Frozen general harness mapping missing.'};$replacement.Source=$generalHarness;$replacement.Sha256=$GeneralHarnessSha256
}
if(!$SingleColdReload -and !$SingleRecovery -and !$BodyLoadedReference){
 if(!$SinglePlacement){
 $batch=[IO.Path]::GetFullPath($terms.BatchPath)
 if(!$batch.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $batch) -ine $terms.BatchSha256){throw 'Exact batch terms changed.'}
 }
 foreach($entry in $terms.Cases){if(!$groupCold -and !$entry.Fields.existingAuthorization){$request=[IO.Path]::GetFullPath($entry.Request.RequestPath);if(!$request.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or (Hash $request) -ine $entry.Request.RequestSha256){throw 'Exact request hash failed.'}}}
}
foreach($name in @('colony-load.cfg','colony-control.cfg','colony-placement-body-watch.cfg')){Private (Join-Path $devRoot $name);if(Test-Path -LiteralPath (Join-Path $devRoot $name)){throw 'Unconsumed native request exists.'}}
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss';$folder='ColonyBuild-'+$stamp
$target=Join-Path $devRoot ("saves\$folder\persistent.sfs");Private $target
if(Test-Path -LiteralPath (Split-Path -Parent $target)){throw 'Disposable native folder already exists.'}
$backup=Join-Path $workspace ('outputs\colony-runtime-tests\deployment-'+$stamp);New-Item -ItemType Directory -Path $backup | Out-Null
Copy-Item -LiteralPath $freezePath -Destination (Join-Path $backup 'freeze-manifest.json')
$installed=@()
foreach($artifact in $install){
 $destination=Join-Path $devRoot $artifact.Path;Private $destination
 if(Test-Path -LiteralPath $destination){
  $old=Join-Path $backup $artifact.Path;New-Item -ItemType Directory -Path (Split-Path -Parent $old) -Force | Out-Null;Copy-Item -LiteralPath $destination -Destination $old
  # Stock AssemblyLoader opens DLLs with implicit ReadWrite. Immutable source
  # flags belong to the review snapshot; this exact private copy must be writable.
  [IO.File]::SetAttributes($destination,([IO.FileAttributes]([IO.File]::GetAttributes($destination) -band (-bnot [IO.FileAttributes]::ReadOnly))))
 }
 New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
 Copy-Item -LiteralPath $artifact.Source -Destination $destination
 [IO.File]::SetAttributes($destination,([IO.FileAttributes]([IO.File]::GetAttributes($destination) -band (-bnot [IO.FileAttributes]::ReadOnly))))
 if((Hash $destination) -ine $artifact.Sha256){throw 'Frozen native installation hash failed.'}
 $installed+=@{Path=$artifact.Path;Sha256=$artifact.Sha256}
}
New-Item -ItemType Directory -Path (Split-Path -Parent $target) | Out-Null;Copy-Item -LiteralPath $source -Destination $target
if((Hash $source) -ine $SourceSha256 -or (Hash $target) -ine $SourceSha256){throw 'Native seed changed during copy.'}
if($SingleColdReload -or $SingleRecovery -or $groupCold -or $groupExisting){
 $sourceName=$(if($SingleColdReload -or $SingleRecovery){$terms.WatcherFields.reloadSourceName}else{$terms.Cases[0].Fields.reloadSourceName})
 $namedSource=Join-Path $devRoot ("saves\$folder\"+$sourceName+'.sfs');Private $namedSource;Copy-Item -LiteralPath $source -Destination $namedSource
 if((Hash $namedSource) -ine $SourceSha256){throw 'Named original native cold source differs.'}
}
if(!$BodyLoadedReference -and !$SingleColdReload -and !$SingleRecovery){
 foreach($entry in $terms.Cases){if(!$groupCold -and !$entry.Fields.existingAuthorization){$request=$entry.Request.RequestPath;$destination=Join-Path $devRoot ([IO.Path]::GetFileName($request));Private $destination;if(Test-Path -LiteralPath $destination){throw 'Probe request already exists.'};Copy-Item -LiteralPath $request -Destination $destination;if((Hash $destination) -ine $entry.Request.RequestSha256){throw 'Installed probe hash failed.'}}}
 if(!$SinglePlacement){
 $batchTarget=Join-Path $devRoot $terms.WatcherFields.batchRequestName;Private $batchTarget
 if(Test-Path -LiteralPath $batchTarget){throw 'Batch target already exists.'};Copy-Item -LiteralPath $batch -Destination $batchTarget
 if((Hash $batchTarget) -ine $terms.BatchSha256){throw 'Installed batch hash differs.'}
 }
}
$token=[guid]::NewGuid().ToString('N');$prefix=[Environment]::UserName
$launchArgs=@('-muteaudio','-screen-fullscreen','0','-screen-width','1600','-screen-height','900',"-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev.$prefix.$token","-expanseEffectsPipe=ExpanseFoundations.Effects.dev.$prefix.$token","-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev.$prefix.$token","-expanseColonyPipe=ExpanseFoundations.Colonies.dev.$prefix.$token",'-expanseColonyProfile')
$auth=Join-Path $devRoot 'colony-development-authorization.cfg';Private $auth
@("root = $devRoot","saveFolder = $folder","colonyPipe = ExpanseFoundations.Colonies.dev.$prefix.$token") | Set-Content -LiteralPath $auth
$load=Join-Path $devRoot 'colony-load.cfg';Private $load
@('authorization = verified-isolated-development-only',"saveFolder = $folder","sha256 = $SourceSha256",('referenceVesselId = '+$ReferenceVesselId.ToString('D'))) | Set-Content -LiteralPath $load
$watch=Join-Path $devRoot 'colony-placement-watch.cfg';Private $watch;$lines=@()
if($BodyLoadedReference){
 if(Test-Path -LiteralPath $watch){$archivedWatch=$watch+'.archived-'+$stamp;Private $archivedWatch;if(Test-Path -LiteralPath $archivedWatch){throw 'Original watcher archive exists.'};Move-Item -LiteralPath $watch -Destination $archivedWatch}
}else{
 foreach($field in $terms.WatcherFields.PSObject.Properties){$value=$field.Value;if($field.Name -eq 'saveFolder'){$value=$folder};if([string]$value -match '[\r\n{}]'){throw 'Watcher field invalid.'};$lines+=($field.Name+' = '+$value)}
 $lines | Set-Content -LiteralPath $watch
}
$manifest=@{Token=$token;SaveFolder=$folder;SourcePath=$source;SourceKind='isolated-native-save';SourceSha256=$SourceSha256;TargetSave=$target;Executable=$exe;Arguments=$launchArgs;Artifacts=$installed;FreezeManifest=$freezePath;FreezeManifestSha256=(Hash $freezePath);DerivedPlacementHarness=$PlacementHarnessSnapshot;DerivedGeneralHarness=$GeneralHarnessSnapshot;CreatedUtc=[DateTime]::UtcNow.ToString('o');Launched=$false;QualificationPhase=$(if($BodyLoadedReference){'body-loaded-reference'}elseif($SingleRecovery){'single-existing-native-recovery'}elseif($SingleColdReload){'single-cold-utility-diagnostic'}elseif($SinglePlacement){'single-exact-product-probe'}elseif($StreetGroup){'five-package-street'}elseif($ColdPackageBatch){'eight-package-cold'}else{'eight-package-loaded'});PreviousEvidence=$backup}
$manifestPath=Join-Path $workspace 'ExpansePlatform\run\colony-development.json';$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
$process=Start-Process -FilePath $exe -ArgumentList $launchArgs -WorkingDirectory $devRoot -WindowStyle Hidden -PassThru
$manifest.ProcessId=$process.Id;$manifest.Launched=$true;$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
@{ProcessId=$process.Id;SaveFolder=$folder;SeedSha256=(Hash $source);FrozenArtifacts=$install.Count;ProfileEnabled=$true;FirstOperation=$terms.WatcherFields.operationId} | ConvertTo-Json
