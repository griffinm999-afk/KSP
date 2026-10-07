[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][ValidateSet('scout','create-reference','loaded-survey','loaded-grid-service','loaded-grid-housing','loaded-edge-service','loaded-edge-housing','loaded-operation-diagnostic')][string]$Mode,
 [Parameter(Mandatory=$true)][ValidateSet('Mun','Duna')][string]$Body,
 [Parameter(Mandatory=$true)][double]$Latitude,
 [Parameter(Mandatory=$true)][double]$Longitude,
 [Parameter(Mandatory=$true)][guid]$WorldId,
 [guid]$TargetOperationId=[guid]::Empty,
 [double]$HeadingDegrees=0
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$devRoot='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifest=Get-Content -LiteralPath (Join-Path $workspace 'ExpansePlatform\run\colony-development.json') -Raw | ConvertFrom-Json
$exe=Join-Path $devRoot 'KSP_x64.exe'
$process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$manifest.ProcessId)
$pipe="ExpanseFoundations.Colonies.dev.$([Environment]::UserName).$($manifest.Token)"
if(!$process -or $manifest.Executable -ine $exe -or $process.ExecutablePath -ine $exe -or !$process.CommandLine.Contains('-expanseColonyPipe='+$pipe) -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$'){throw 'Exact isolated process/save/IPC mismatch.'}
if([double]::IsNaN($Latitude) -or [double]::IsInfinity($Latitude) -or [Math]::Abs($Latitude) -ge 80 -or [double]::IsNaN($Longitude) -or [double]::IsInfinity($Longitude) -or $Longitude -lt -180 -or $Longitude -gt 180 -or $WorldId -eq [guid]::Empty){throw 'Body request finite coordinates/world required.'}
if([double]::IsNaN($HeadingDegrees) -or [double]::IsInfinity($HeadingDegrees) -or $HeadingDegrees -lt 0 -or $HeadingDegrees -ge 360 -or ($HeadingDegrees -ne 0 -and $Mode -notin @('loaded-edge-service','loaded-edge-housing'))){throw 'Finite heading0..360 is limited to the existing read-only native edge modes.'}
function Private([string]$Path){
 $full=[IO.Path]::GetFullPath($Path);if(!$full.StartsWith($devRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Request leaves isolated root.'}
 for($a=$full;$a;$a=[IO.Path]::GetDirectoryName($a)){if((Test-Path -LiteralPath $a) -and ((Get-Item -LiteralPath $a -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Request reparse path refused.'}}
 if(Test-Path -LiteralPath $full -PathType Leaf){$links=@(& fsutil hardlink list $full);if($LASTEXITCODE -ne 0 -or $links.Count -ne 1){throw 'Request is not private.'}}
}
$watch=Join-Path $devRoot 'colony-placement-body-watch.cfg';Private $watch
if(Test-Path -LiteralPath $watch){throw 'Unconsumed body request exists; reconcile before new operation.'}
$persistent=Join-Path $devRoot ("saves\"+$manifest.SaveFolder+'\persistent.sfs');Private $persistent
$seed=(Get-FileHash -LiteralPath $persistent -Algorithm SHA256).Hash.ToLowerInvariant()
# Stock autosave legitimately replaces this selected private persistent file.
# Bind its current actual bytes, while separately preserving the immutable native
# origin recorded by the verified run manifest. No origin save is written.
if($Mode -eq 'create-reference'){
 $origin=[IO.Path]::GetFullPath($manifest.SourcePath);Private $origin
 if($origin -notmatch '\\saves\\ColonyBuild-[0-9]{8}-[0-9]{6}\\[^\\]+\.sfs$' -or (Get-FileHash -LiteralPath $origin).Hash -ine $manifest.SourceSha256){throw 'Immutable actual native fixture origin changed.'}
}
$operation=[guid]::NewGuid().ToString('D');$saveName='colony-test-body-'+$Body.ToLowerInvariant()+'-'+$operation
$fields=[ordered]@{authorization='empty-body-reference-fixture-no-colony-buildings';operationId=$operation;saveFolder=$manifest.SaveFolder;worldId=$WorldId.ToString('D');mode=$Mode;body=$Body;latitude=$Latitude.ToString('R',[Globalization.CultureInfo]::InvariantCulture);longitude=$Longitude.ToString('R',[Globalization.CultureInfo]::InvariantCulture);saveName=$saveName;seedSha256=$seed}
if($Mode -in @('loaded-edge-service','loaded-edge-housing')){$fields.headingDegrees=$HeadingDegrees.ToString('R',[Globalization.CultureInfo]::InvariantCulture)}
if($Mode -eq 'loaded-operation-diagnostic'){if($TargetOperationId -eq [guid]::Empty){throw 'Exact existing held target operation required'};$fields.authorization='read-only-existing-held-native-geometry-no-effects';$fields.targetOperationId=$TargetOperationId.ToString('D')}
elseif($TargetOperationId -ne [guid]::Empty){throw 'Target operation is limited to read-only held geometry diagnostic'}
$evidence=Join-Path $workspace 'outputs\colony-placement-audit\body-reference-requests';New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$name='colony-placement-body-request-'+$operation+'.cfg';$payload=Join-Path $evidence $name
@($fields.GetEnumerator() | ForEach-Object {$_.Key+' = '+$_.Value}) | Set-Content -LiteralPath $payload
$hash=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
$target=Join-Path $devRoot $name;Private $target
if(Test-Path -LiteralPath $target){throw 'Native body payload already exists.'};Copy-Item -LiteralPath $payload -Destination $target
if((Get-FileHash -LiteralPath $target).Hash -ine $hash){throw 'Native body payload hash mismatch.'}
@("payloadName = $name","payloadSha256 = $hash") | Set-Content -LiteralPath $watch
@{OperationId=$operation;Mode=$Mode;Body=$Body;PayloadSha256=$hash;Payload=$payload;ExpectedEvidence=(Join-Path $devRoot ('colony-placement-body-evidence-'+$operation+'.cfg'));NativeSaveName=$saveName;NoColonyBuildings=$true} | ConvertTo-Json
