[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][int]$ExpectedProcessId,
  [Parameter(Mandatory=$true)][guid]$HeldOperationId,
  [Parameter(Mandatory=$true)][string]$CompletedWitness,
  [Parameter(Mandatory=$true)][string]$DiagnosticNativeSaveName,
  [Parameter(Mandatory=$true)][string]$DiagnosticNativeSaveSha256,
  [string]$SnapshotPath,
  [string[]]$AdditionalEvidence=@(),
  [switch]$StopVerifiedProcess
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$devRoot='C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$manifestPath=Join-Path $workspace 'ExpansePlatform\run\colony-development.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$exe=Join-Path $devRoot 'KSP_x64.exe'
function Assert-PrivateRead([string]$Path) {
  $resolved=[IO.Path]::GetFullPath($Path)
  if (-not $resolved.StartsWith($devRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Read leaves isolated root.' }
  for ($ancestor=$resolved; $ancestor; $ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse path refused.' }
  }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$process=Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedProcessId"
$pipe="ExpanseFoundations.Colonies.dev.$([Environment]::UserName).$($manifest.Token)"
if ($manifest.ProcessId -ne $ExpectedProcessId -or $manifest.Executable -ine $exe -or $manifest.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$' -or !$process -or $process.ExecutablePath -ine $exe -or !$process.CommandLine.Contains('-expanseColonyPipe='+$pipe)) { throw 'Exact isolated process/save/token mismatch.' }
Assert-PrivateRead $CompletedWitness
$witness=Get-Content -LiteralPath $CompletedWitness -Raw
$id=$HeldOperationId.ToString('D')
if ($witness -notmatch ('START .* save='+[regex]::Escape($manifest.SaveFolder)+' operation='+[regex]::Escape($id)) -or $witness -notmatch 'STATUS RecoveryHold ' -or $witness -notmatch 'FAIL System.Exception: Placement blocked:' -or $witness -match 'PASS BATCH_LOADED ') { throw 'Bounded actual failed native batch witness missing.' }
$statusPath=Join-Path $devRoot "colony-placement-status-$id.cfg"; Assert-PrivateRead $statusPath
$status=Get-Content -LiteralPath $statusPath -Raw
if ($status -notmatch ('OperationId = '+[regex]::Escape($id)) -or $status -notmatch 'Stage = RecoveryHold' -or $status -notmatch ('saveFolder = '+[regex]::Escape($manifest.SaveFolder)) -or $status -notmatch ('process = '+$ExpectedProcessId)) { throw 'Exact held native receipt differs.' }
if ($DiagnosticNativeSaveName -notmatch '^colony-test-[a-zA-Z0-9-]{1,80}$' -or $DiagnosticNativeSaveSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Bounded native diagnostic identity missing.' }
$native=Join-Path $devRoot ("saves\"+$manifest.SaveFolder+'\'+$DiagnosticNativeSaveName+'.sfs'); Assert-PrivateRead $native
if ((Hash $native) -ine $DiagnosticNativeSaveSha256) { throw 'Native diagnostic bytes changed.' }
$nativeText=Get-Content -LiteralPath $native -Raw
if ($nativeText -notmatch ([regex]::Escape($id)) -or $nativeText -notmatch 'Stage = RecoveryHold') { throw 'Actual native save lacks held queue lineage.' }
foreach ($artifact in $manifest.Artifacts) { $path=Join-Path $devRoot $artifact.Path; Assert-PrivateRead $path; if ((Hash $path) -ine $artifact.Sha256) { throw 'Installed coherent artifact changed.' } }
$source=$manifest.SourcePath; Assert-PrivateRead $source
if ((Hash $source) -ine $manifest.SourceSha256) { throw 'Original paid native source changed.' }
$watchPath=Join-Path $devRoot 'colony-placement-watch.cfg'; Assert-PrivateRead $watchPath
$watch=@{}; foreach ($line in Get-Content -LiteralPath $watchPath) { if ($line -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$') { $watch[$matches[1]]=$matches[2] } }
if ($watch.saveFolder -ne $manifest.SaveFolder -or $watch.mode -ne 'native-package-batch' -or $watch.batchRequestName -notmatch '^colony-placement-batch-[a-f0-9-]{36}\.cfg$') { throw 'Exact eight-package watcher absent.' }
$batchPath=Join-Path $devRoot $watch.batchRequestName; Assert-PrivateRead $batchPath
if ((Hash $batchPath) -ine $watch.batchRequestSha256) { throw 'Exact original batch terms changed.' }
$batchText=Get-Content -LiteralPath $batchPath -Raw
$ids=@([regex]::Matches($batchText,'(?m)^\s*operationId = ([a-f0-9-]{36})\s*$') | ForEach-Object {$_.Groups[1].Value})
if ($ids.Count -ne 8 -or $id -notin $ids) { throw 'Actual held operation outside exact eight-case batch.' }
$archive=Join-Path $workspace ('outputs\colony-runtime-tests\held-'+$id+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $archive | Out-Null
$copied=@()
function Copy-Evidence([string]$Path) {
  Assert-PrivateRead $Path
  if (Test-Path -LiteralPath $Path -PathType Leaf) {
    $destination=Join-Path $archive ([IO.Path]::GetFileName($Path)); Copy-Item -LiteralPath $Path -Destination $destination
    $script:copied+=@{File=[IO.Path]::GetFileName($destination);Sha256=(Hash $destination)}
  }
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $archive 'manifest.json')
Copy-Evidence $CompletedWitness; Copy-Evidence $watchPath; Copy-Evidence $batchPath; Copy-Evidence $native
foreach ($probe in $ids) {
  foreach ($leaf in @("colony-placement-status-$probe.cfg","colony-placement-loaded-parts-$probe.cfg","colony-placement-envelope-$probe.cfg","colony-placement-package-$probe.cfg","colony-placement-request-package-$probe.cfg","colony-placement-sweep-$probe.cfg","colony-placement-footing-$probe.cfg")) { Copy-Evidence (Join-Path $devRoot $leaf) }
}
foreach ($name in @([regex]::Matches($batchText,'(?m)^\s*saveName = ([a-zA-Z0-9-]+)\s*$') | ForEach-Object {$_.Groups[1].Value}) + @('colony-test-package-service-drift')) {
  Copy-Evidence (Join-Path $devRoot ("saves\"+$manifest.SaveFolder+'\'+$name+'.sfs'))
}
Copy-Evidence (Join-Path $devRoot 'KSP.log')
foreach ($leaf in $AdditionalEvidence) {
 if ($leaf -notmatch '^colony-placement-body-(evidence-[a-f0-9-]{36}\.cfg|reference-[0-9]{8}-[0-9]{6}-[0-9]{3}\.txt)$') { throw 'Additional evidence outside bounded body scout witnesses.' }
 Copy-Evidence (Join-Path $devRoot $leaf)
}
if ($SnapshotPath) { $snapshot=[IO.Path]::GetFullPath($SnapshotPath); if (!$snapshot.StartsWith((Join-Path $workspace 'outputs')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot leaves workspace outputs.' }; Copy-Item -LiteralPath $snapshot -Destination (Join-Path $archive 'colony-snapshot.json') }
@{ Outcome='Actual native RecoveryHold preserved; no retry/reconciliation/respawn/cancel/refund or certification'; HeldOperationId=$id; ProcessId=$ExpectedProcessId; SaveFolder=$manifest.SaveFolder; ImmutableSource=$source; ImmutableSourceSha256=(Hash $source); NativeDiagnosticSha256=(Hash $native); ArtifactHashes=$manifest.Artifacts; Evidence=$copied } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $archive 'preservation.json')
if ((Hash $native) -ine $DiagnosticNativeSaveSha256 -or (Hash $source) -ine $manifest.SourceSha256) { throw 'Immutable native evidence changed during archive.' }
if ($StopVerifiedProcess) {
  $again=Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedProcessId"
  if (!$again -or $again.ExecutablePath -ine $exe -or !$again.CommandLine.Contains('-expanseColonyPipe='+$pipe)) { throw 'Exact process changed before stop.' }
  Stop-Process -Id $ExpectedProcessId; Wait-Process -Id $ExpectedProcessId -Timeout 20 -ErrorAction SilentlyContinue
  if (Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue) { throw 'Verified isolated process did not stop.' }
}
Write-Output $archive
