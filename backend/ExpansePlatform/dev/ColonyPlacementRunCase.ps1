[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][int]$ExpectedProcessId,
  [Parameter(Mandatory=$true)][string]$SourceSave,
  [Parameter(Mandatory=$true)][string]$SourceSha256,
  [Parameter(Mandatory=$true)][string]$CaseJson,
  [Parameter(Mandatory=$true)][int]$CaseIndex,
  [Parameter(Mandatory=$true)][string]$CompletedWitness,
  [string]$SnapshotPath,
  [switch]$ResumeVerifiedStopped,
  [switch]$PackageBatch,
  [switch]$ColdPackageBatch,
  [string]$PlacementHarnessSnapshot,
  [string]$PlacementHarnessSha256,
  [switch]$ArchiveIncompleteNativeProbe,
  [string]$DiagnosticNativeSaveName
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$devRoot = 'C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$exe = Join-Path $devRoot 'KSP_x64.exe'
$manifestPath = Join-Path $workspace 'ExpansePlatform\run\colony-development.json'
function Assert-Private([string]$Path, [switch]$ReadOnly) {
  $absolute = [IO.Path]::GetFullPath($Path)
  if ($absolute -ne $devRoot -and -not $absolute.StartsWith($devRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path leaves isolated root: $absolute" }
  for ($ancestor = $absolute; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse path: $ancestor" }
  }
  if (-not $ReadOnly -and (Test-Path -LiteralPath $absolute -PathType Leaf)) {
    $links = @(& fsutil hardlink list $absolute)
    if ($LASTEXITCODE -ne 0 -or $links.Count -ne 1) { throw "Mutable file is not private: $absolute" }
  }
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($previous.ProcessId -ne $ExpectedProcessId -or $previous.Executable -ine $exe -or $previous.SaveFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$') { throw 'Expected isolated process does not match current manifest.' }
Assert-Private $exe -ReadOnly
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedProcessId"
if ($process) {
  if ($ResumeVerifiedStopped -or $process.ExecutablePath -ine $exe -or -not $process.CommandLine.Contains("-expanseColonyPipe=ExpanseFoundations.Colonies.dev.$([Environment]::UserName).$($previous.Token)")) { throw 'Exact process executable/token verification failed.' }
} elseif (-not $ResumeVerifiedStopped) { throw 'Exact isolated process is absent; explicit stopped-case continuation is required.' }
foreach ($artifact in $previous.Artifacts) {
  $path = Join-Path $devRoot $artifact.Path; Assert-Private $path -ReadOnly
  if ((Hash $path) -ine $artifact.Sha256) { throw "Installed frozen artifact changed: $($artifact.Path)" }
}
$source = [IO.Path]::GetFullPath($SourceSave); Assert-Private $source
if ($source -notmatch '\\saves\\ColonyBuild-[0-9]{8}-[0-9]{6}\\[^\\]+\.sfs$' -or (Hash $source) -ine $SourceSha256) { throw 'Original native source identity/hash failed.' }
foreach ($unconsumed in @('colony-control.cfg', 'colony-load.cfg')) {
  $path = Join-Path $devRoot $unconsumed; Assert-Private $path
  if (Test-Path -LiteralPath $path) { throw "An unconsumed native request exists: $unconsumed" }
}
Assert-Private $CompletedWitness -ReadOnly
$witness = Get-Content -LiteralPath $CompletedWitness -Raw
if ($witness -notmatch ('START .* save=' + [regex]::Escape($previous.SaveFolder) + ' ')) { throw 'Previous native witness does not match selected native save.' }
if ($ArchiveIncompleteNativeProbe) {
  if ($previous.QualificationPhase -ne 'eight-package-loaded' -or $DiagnosticNativeSaveName -notmatch '^colony-test-[a-zA-Z0-9-]{1,70}$' -or $witness -notmatch 'STATUS Settling ' -or $witness -match 'PASS BATCH_LOADED ') { throw 'Explicit incomplete stock-window branch does not match bounded native diagnostic.' }
} elseif ($witness -notmatch 'PASS native ' -or $witness -match '(?m)^.* (FAIL|REFUSED) ') { throw 'Previous native witness is not a completed passing case.' }
$previousWatchPath = Join-Path $devRoot 'colony-placement-watch.cfg'; Assert-Private $previousWatchPath
$previousWatch = @{}
foreach ($line in Get-Content -LiteralPath $previousWatchPath) { if ($line -match '^\s*([^=]+?)\s*=\s*(.*?)\s*$') { $previousWatch[$matches[1]]=$matches[2] } }
if ($previousWatch.saveFolder -ne $previous.SaveFolder -or $witness -notmatch ('operation=' + [regex]::Escape($previousWatch.operationId))) { throw 'Watcher does not match completed native witness.' }
if (-not $ArchiveIncompleteNativeProbe -and $previousWatch.mode -in @('native-package-batch','cold-package-batch') -and $witness -notmatch 'PASS BATCH_UNLOADED ') { throw 'Package batch is not complete through actual stock unloaded native save.' }
$caseDocument = Get-Content -LiteralPath $CaseJson -Raw | ConvertFrom-Json
if ($PackageBatch -or $ColdPackageBatch) {
  $expectedMode=$(if ($ColdPackageBatch) {'cold-package-batch'} else {'native-package-batch'})
  if ($PackageBatch -and $ColdPackageBatch -or $caseDocument.Cases.Count -ne 8 -or $caseDocument.WatcherFields.mode -ne $expectedMode) { throw 'Exactly eight authored native package cases required.' }
  if ($ColdPackageBatch -and ($caseDocument.ColdSourceSha256 -ine $SourceSha256 -or $caseDocument.ColdSourceName -ne [IO.Path]::GetFileNameWithoutExtension($source) -or $caseDocument.PriorProcessId -ne $ExpectedProcessId)) { throw 'Independent original native cold source/process differs.' }
  $case = [pscustomobject]@{ Phase=$(if ($ColdPackageBatch) {'eight-package-cold'} else {'eight-package-loaded'}); Request=$caseDocument.Cases[0].Request; WatcherFields=$caseDocument.WatcherFields }
  $batchSource = [IO.Path]::GetFullPath($caseDocument.BatchPath)
  if (-not $batchSource.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) -or (Hash $batchSource) -ine $caseDocument.BatchSha256) { throw 'Exact package batch source hash failed.' }
} else {
  if ($CaseIndex -lt 0 -or $CaseIndex -ge $caseDocument.Cases.Count) { throw 'Invalid bounded case index.' }
  $case = $caseDocument.Cases[$CaseIndex]
}
$requestSource = [IO.Path]::GetFullPath($case.Request.RequestPath)
if (-not $requestSource.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) -or (Hash $requestSource) -ine $case.Request.RequestSha256) { throw 'Exact authored request SHA failed.' }
if ((-not $PackageBatch -and -not $ColdPackageBatch -and [IO.Path]::GetFileName($requestSource) -ne $case.WatcherFields.enqueueRequestName) -or $case.WatcherFields.operationId -notmatch '^[a-f0-9-]{36}$') { throw 'Request/watcher identity mismatch.' }
if ($PlacementHarnessSnapshot) {
  $harnessSource=[IO.Path]::GetFullPath($PlacementHarnessSnapshot)
  if (-not $PackageBatch -or -not $harnessSource.StartsWith($workspace + '\outputs\', [StringComparison]::OrdinalIgnoreCase) -or $PlacementHarnessSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Hash $harnessSource) -ine $PlacementHarnessSha256) { throw 'Only exact reviewed placement-harness snapshot may replace a frozen artifact.' }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$evidence = Join-Path $workspace ('outputs\colony-runtime-tests\placement-' + $previousWatch.operationId + '-' + $stamp)
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $evidence 'manifest.json')
Copy-Item -LiteralPath $CompletedWitness -Destination $evidence
Copy-Item -LiteralPath $previousWatchPath -Destination $evidence
foreach ($leaf in @($previousWatch.enqueueRequestName, ('colony-placement-status-' + $previousWatch.operationId + '.cfg'), ('colony-placement-loaded-parts-' + $previousWatch.operationId + '.cfg'), ('colony-placement-envelope-' + $previousWatch.operationId + '.cfg'), 'KSP.log')) {
  if ($leaf) { $path=Join-Path $devRoot $leaf; Assert-Private $path -ReadOnly; if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $evidence } }
}
if ($previousWatch.batchRequestName) {
  $batchOriginal=Join-Path $devRoot $previousWatch.batchRequestName; Assert-Private $batchOriginal -ReadOnly
  if ((Hash $batchOriginal) -ine $previousWatch.batchRequestSha256) { throw 'Completed original batch definition changed.' }
  Copy-Item -LiteralPath $batchOriginal -Destination $evidence
  $batchText=Get-Content -LiteralPath $batchOriginal -Raw
  $operations=@([regex]::Matches($batchText,'(?m)^\s*operationId = ([a-f0-9-]{36})\s*$') | ForEach-Object { $_.Groups[1].Value })
  if ($operations.Count -ne 8) { throw 'Original bounded batch operation inventory differs.' }
  foreach ($id in $operations) {
    foreach ($leaf in @("colony-placement-status-$id.cfg", "colony-placement-loaded-parts-$id.cfg", "colony-placement-envelope-$id.cfg", "colony-placement-package-$id.cfg", "colony-placement-request-package-$id.cfg", "colony-placement-unloaded-$id.cfg", "colony-placement-sweep-$id.cfg", "colony-placement-footing-$id.cfg")) {
      $path=Join-Path $devRoot $leaf; Assert-Private $path -ReadOnly
      if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $evidence -Force }
    }
  }
  $unloaded=Join-Path $devRoot ('saves\' + $previous.SaveFolder + '\' + $previousWatch.unloadedSaveName + '.sfs'); Assert-Private $unloaded -ReadOnly
  if (-not $ArchiveIncompleteNativeProbe) { Copy-Item -LiteralPath $unloaded -Destination $evidence }
}
$nativeName=$(if ($ArchiveIncompleteNativeProbe) {$DiagnosticNativeSaveName} else {$previousWatch.saveName})
$native = Join-Path $devRoot ('saves\' + $previous.SaveFolder + '\' + $nativeName + '.sfs'); Assert-Private $native -ReadOnly
if (-not (Test-Path -LiteralPath $native)) { throw 'Completed witness native save is absent.' }
if ($ArchiveIncompleteNativeProbe) {
  $nativeText=Get-Content -LiteralPath $native -Raw
  if ($nativeText -notmatch ([regex]::Escape($previousWatch.operationId)) -or $nativeText -notmatch 'Stage = Settling' -or $nativeText -notmatch 'packed=True') { throw 'Actual native diagnostic lacks original incomplete packed placement evidence.' }
  'Incomplete native stock-unpack window branch archived; original marked building retained in its original save. No retry, reconcile, respawn, cancellation or refund. New operations use a separate unchanged native original and legal plots.' | Set-Content -LiteralPath (Join-Path $evidence 'incomplete-branch.txt')
}
Copy-Item -LiteralPath $native -Destination $evidence
if ($SnapshotPath) { Copy-Item -LiteralPath $SnapshotPath -Destination (Join-Path $evidence 'colony-snapshot.json') }
@{ Source=$source; SourceSha256=(Hash $source); NativeWitnessSaveSha256=(Hash $native); FrozenArtifacts=$previous.Artifacts; PreviousProcessId=$ExpectedProcessId } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'preservation.json')
# Only this verified isolated process is stopped. No binaries or templates are copied.
if ($process) {
  Stop-Process -Id $ExpectedProcessId
  Wait-Process -Id $ExpectedProcessId -Timeout 20 -ErrorAction SilentlyContinue
}
if (Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue) { throw 'Verified process did not stop.' }
if (@(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'" | Where-Object { $_.ProcessId -ne $ExpectedProcessId -and (-not $_.ExecutablePath -or $_.ExecutablePath -ieq $exe) }).Count) { throw 'Another isolated or unidentified KSP is running.' }
if ($PlacementHarnessSnapshot) {
  $relative='GameData\ExpanseColonyDev\Plugins\Expanse.ColonyPlacement.KspHarness.dll'
  $installed=Join-Path $devRoot $relative; Assert-Private $installed
  Copy-Item -LiteralPath $installed -Destination (Join-Path $evidence 'previous-placement-harness.dll')
  for ($attempt=0; $attempt -lt 5; $attempt++) {
    try { Copy-Item -LiteralPath $harnessSource -Destination $installed; break }
    catch {
      if ($attempt -eq 4 -or (Get-Process -Id $ExpectedProcessId -ErrorAction SilentlyContinue)) { throw }
      # Windows may retain an image mapping briefly after the exact process exits.
      Start-Sleep -Milliseconds 500
    }
  }
  if ((Hash $installed) -ine $PlacementHarnessSha256) { throw 'Placement-only harness deployment hash failed.' }
  ($previous.Artifacts | Where-Object Path -eq $relative).Sha256=$PlacementHarnessSha256
}
$folder = 'ColonyBuild-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$target = Join-Path $devRoot "saves\$folder\persistent.sfs"; Assert-Private $target
if (Test-Path -LiteralPath (Split-Path -Parent $target)) { throw 'Refusing to reuse a native save directory.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $target) | Out-Null
Copy-Item -LiteralPath $source -Destination $target
if ((Hash $source) -ine $SourceSha256 -or (Hash $target) -ine $SourceSha256) { throw 'Native original changed during copy.' }
if ($ColdPackageBatch) {
  $namedSource=Join-Path $devRoot ("saves\$folder\" + $caseDocument.ColdSourceName + '.sfs'); Assert-Private $namedSource
  Copy-Item -LiteralPath $source -Destination $namedSource
  if ((Hash $source) -ine $SourceSha256 -or (Hash $namedSource) -ine $SourceSha256) { throw 'Named original native cold source changed during copy.' }
  $requests=@()
} elseif ($PackageBatch) { $requests=@($caseDocument.Cases | ForEach-Object Request) } else { $requests=@($case.Request) }
foreach ($request in $requests) {
  $sourceRequest=[IO.Path]::GetFullPath($request.RequestPath)
  if (-not $sourceRequest.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) -or (Hash $sourceRequest) -ine $request.RequestSha256) { throw 'Authored package request source/hash changed.' }
  $requestTarget = Join-Path $devRoot ([IO.Path]::GetFileName($sourceRequest)); Assert-Private $requestTarget
  if (Test-Path -LiteralPath $requestTarget) { throw 'Refusing to overwrite an existing qualification request.' }
  Copy-Item -LiteralPath $sourceRequest -Destination $requestTarget
  if ((Hash $requestTarget) -ine $request.RequestSha256) { throw 'Copied request hash mismatch.' }
}
if ($PackageBatch -or $ColdPackageBatch) {
  $batchTarget=Join-Path $devRoot $caseDocument.WatcherFields.batchRequestName; Assert-Private $batchTarget
  if ([IO.Path]::GetFileName($batchSource) -ne $caseDocument.WatcherFields.batchRequestName -or (Test-Path -LiteralPath $batchTarget)) { throw 'Batch file target identity mismatch or already exists.' }
  Copy-Item -LiteralPath $batchSource -Destination $batchTarget
  if ((Hash $batchTarget) -ine $caseDocument.BatchSha256) { throw 'Copied batch hash differs.' }
}
$load = Join-Path $devRoot 'colony-load.cfg'; Assert-Private $load
if (Test-Path -LiteralPath $load) { throw 'Unconsumed native load request exists.' }
@('authorization = verified-isolated-development-only', "saveFolder = $folder", "sha256 = $SourceSha256", 'referenceVesselId = 542548ee-19d5-4fab-926d-6f7831d826bf') | Set-Content -LiteralPath $load
$watchLines = @()
foreach ($property in $case.WatcherFields.PSObject.Properties) { $value=$property.Value; if ($property.Name -eq 'saveFolder') { $value=$folder }; if ($value -match '[\r\n{}]') { throw 'Invalid watcher field value.' }; $watchLines += ($property.Name + ' = ' + $value) }
$watchLines | Set-Content -LiteralPath $previousWatchPath
$token = [Guid]::NewGuid().ToString('N'); $user = [Environment]::UserName
$arguments = @('-muteaudio', '-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900', "-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev.$user.$token", "-expanseEffectsPipe=ExpanseFoundations.Effects.dev.$user.$token", "-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev.$user.$token", "-expanseColonyPipe=ExpanseFoundations.Colonies.dev.$user.$token")
if ($previous.Arguments -contains '-expanseColonyProfile') { $arguments += '-expanseColonyProfile' }
$authorization = Join-Path $devRoot 'colony-development-authorization.cfg'; Assert-Private $authorization
@("root = $devRoot", "saveFolder = $folder", "colonyPipe = ExpanseFoundations.Colonies.dev.$user.$token") | Set-Content -LiteralPath $authorization
$manifest = @{ Token=$token; SaveFolder=$folder; SourcePath=$source; SourceKind='isolated-native-save'; SourceSha256=$SourceSha256; TargetSave=$target; Executable=$exe; Arguments=$arguments; Artifacts=$previous.Artifacts; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Launched=$false; FrozenReuse=$true; QualificationPhase=$case.Phase; PreviousEvidence=$evidence }
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
$launched = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $devRoot -WindowStyle Hidden -PassThru
$manifest.ProcessId=$launched.Id; $manifest.Launched=$true
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath
@{ ProcessId=$launched.Id; SaveFolder=$folder; Phase=$case.Phase; OperationId=$case.WatcherFields.operationId; PreviousEvidence=$evidence; FrozenReuse=$true } | ConvertTo-Json
