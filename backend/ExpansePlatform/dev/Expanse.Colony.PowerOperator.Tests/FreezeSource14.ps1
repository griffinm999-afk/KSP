param([string]$FrozenDirectory)
$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$validationRoot = Join-Path $workspaceRoot 'outputs/colony-runtime-tests/power-operator14-validation'
$parentRoot = Join-Path $workspaceRoot 'outputs/colony-runtime-tests/native13-frozen-20261003-1230'
$parentManifest = Join-Path $parentRoot 'freeze-manifest.json'
$parentHash = (Get-FileHash -LiteralPath $parentManifest -Algorithm SHA256).Hash
$parent = Get-Content -LiteralPath $parentManifest -Raw | ConvertFrom-Json
[xml]$mainResults = Get-Content -LiteralPath (Join-Path $validationRoot 'main-source14.trx') -Raw
if ($mainResults.TestRun.ResultSummary.Counters.passed -ne '472' -or $mainResults.TestRun.ResultSummary.Counters.failed -ne '0') { throw 'Expected retained main472/472 result' }
if ((Get-Content -LiteralPath (Join-Path $validationRoot 'operator-kos-source-linked-74.txt') -Raw) -notmatch '74/74 source-linked' -or
    (Get-Content -LiteralPath (Join-Path $workspaceRoot 'ExpansePlatform/artifacts/colony-ui-existing-adoption/validation-source14.log') -Raw) -notmatch '53 targeted checks passed') { throw 'Focused74 or retained UI53 evidence missing' }
foreach ($artifact in $parent.Artifacts) {
    if ((Get-FileHash -LiteralPath (Join-Path $parentRoot $artifact.Path) -Algorithm SHA256).Hash -ne $artifact.Sha256) { throw "Frozen13 artifact changed: $($artifact.Path)" }
}
if (-not $FrozenDirectory) { $FrozenDirectory = Join-Path $workspaceRoot ('outputs/colony-runtime-tests/native14-frozen-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$FrozenDirectory = [IO.Path]::GetFullPath($FrozenDirectory)
$outputRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'outputs/colony-runtime-tests')) + [IO.Path]::DirectorySeparatorChar
if (-not $FrozenDirectory.StartsWith($outputRoot,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $FrozenDirectory)) { throw 'Freeze must use a new directory inside workspace outputs/colony-runtime-tests' }
New-Item -ItemType Directory -Path $FrozenDirectory | Out-Null
function Copy-FrozenRecord([string]$Source,[string]$Relative) {
    $Source = (Resolve-Path -LiteralPath $Source).Path
    $before = Get-FileHash -LiteralPath $Source -Algorithm SHA256
    $target = Join-Path $FrozenDirectory $Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $target
    $copied = Get-FileHash -LiteralPath $target -Algorithm SHA256
    if ($before.Hash -ne $copied.Hash -or $before.Hash -ne (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash) { throw "Source changed during freeze: $Source" }
    [ordered]@{ Path=$Relative; Source=$Source; Sha256=$copied.Hash; Length=(Get-Item -LiteralPath $target).Length }
}
$native = @($parent.Artifacts | Sort-Object Path | ForEach-Object { Copy-FrozenRecord $_.Source $_.Path })
if ($native.Count -ne 29) { throw 'Expected exact 29-artifact native layout' }
$templatesPath = Join-Path $FrozenDirectory 'GameData/ExpanseWorldBridge/Templates'
$catalog = Get-Content -LiteralPath (Join-Path $templatesPath 'colony-template-catalog.json') -Raw | ConvertFrom-Json
$manifests = @(Get-ChildItem -LiteralPath $templatesPath -File -Filter '*.manifest.json')
if ($manifests.Count -ne 10 -or $catalog.Templates.Count -ne 10 -or @($catalog.Templates.Id | Sort-Object -Unique).Count -ne 10) { throw 'Expected exact ten-template catalog and manifests' }
$templateTerms = foreach ($manifestFile in $manifests) {
    $terms = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
    $catalogTerms = @($catalog.Templates | Where-Object Id -eq $terms.Id)
    $craft = Join-Path $templatesPath $terms.CraftRelativePath
    if ($catalogTerms.Count -ne 1 -or $catalogTerms[0].Hash -ne $terms.Hash -or $catalogTerms[0].CraftSha256 -ne $terms.CraftSha256 -or
        (Get-FileHash -LiteralPath $craft -Algorithm SHA256).Hash -ne $terms.CraftSha256) { throw "Catalog/manifest/craft lineage differs: $($terms.Id)" }
    [ordered]@{ Id=$terms.Id; ReviewedQuoteHash=$terms.Hash; CraftSha256=$terms.CraftSha256; ManifestSha256=(Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash; RuntimeCertified=$terms.RuntimeCertified }
}
$desktopRoot = Join-Path $workspaceRoot 'ExpansePlatform/src/Expanse.Clock.Manager/bin/Release/net8.0-windows'
$desktop = @(Get-ChildItem -LiteralPath $desktopRoot -File | Sort-Object Name | ForEach-Object { Copy-FrozenRecord $_.FullName ('DesktopManager/' + $_.Name) })
$evidenceSources = @(
    'outputs/colony-runtime-tests/power-operator14-validation/main-source14.trx',
    'outputs/colony-runtime-tests/power-operator14-validation/main-regression.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/operator-kos-source-linked-74.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/installed-api-11.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/power-operator-installed-provenance.json',
    'outputs/colony-runtime-tests/power-operator14-validation/source14-validation-manifest.json',
    'outputs/colony-runtime-tests/power-operator14-validation/bridge-build.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/native-ksp-harness-build.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/native-placement-harness-build.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/diagnostic-heading-offline-2.txt',
    'outputs/colony-runtime-tests/power-operator14-validation/review-boundary.md',
    'ExpansePlatform/artifacts/colony-ui-existing-adoption/validation-source14.log',
    'ExpansePlatform/docs/COLONY-OPERATOR-GUIDE.md',
    'ExpansePlatform/docs/COLONY-NATIVE-ACCEPTANCE-SEQUENCE.md',
    'ExpansePlatform/docs/COLONY-RUNTIME-PERFORMANCE-REVIEW.md',
    'ExpansePlatform/docs/COLONY-RESUME-20261003-1300.md'
)
$evidence = @($evidenceSources | ForEach-Object { Copy-FrozenRecord (Join-Path $workspaceRoot $_) ('Evidence/' + $_) })
Push-Location -LiteralPath $workspaceRoot
try {
    $sourcePaths = @(& rg --files 'ExpansePlatform/src/Expanse.WorldBridge' 'ExpansePlatform/src/Expanse.Domain' 'ExpansePlatform/src/Expanse.Clock.Manager' 'ExpansePlatform/src/Expanse.Clock.Core' 'ExpansePlatform/dev/Expanse.Colony.KspHarness' 'ExpansePlatform/dev/Expanse.ColonyPlacement.KspHarness' 'ExpansePlatform/dev/Expanse.Colony.PowerOperator.Tests' 'ExpansePlatform/dev/Expanse.Colony.Adoption.UiTests' -g '*.cs' -g '*.xaml' -g '*.csproj' -g '*.ps1' -g '*.py' -g '!**/bin/**' -g '!**/obj/**')
    if ($LASTEXITCODE -ne 0) { throw 'Source inventory failed' }
    $source = @($sourcePaths | Sort-Object | ForEach-Object { Copy-FrozenRecord (Join-Path $workspaceRoot $_) ('Source/' + $_) })
} finally { Pop-Location }
# Check all originals again after copying the whole snapshot. A shared build or
# concurrent edit cannot silently produce a mixed source/binary manifest.
foreach ($record in @($native)+@($desktop)+@($evidence)+@($source)) {
    if ((Get-FileHash -LiteralPath $record.Source -Algorithm SHA256).Hash -ne $record.Sha256) { throw "Source drifted before freeze commit: $($record.Source)" }
}
if ((Get-FileHash -LiteralPath $parentManifest -Algorithm SHA256).Hash -ne $parentHash) { throw 'Frozen13 manifest changed' }
foreach ($artifact in $parent.Artifacts) {
    if ((Get-FileHash -LiteralPath (Join-Path $parentRoot $artifact.Path) -Algorithm SHA256).Hash -ne $artifact.Sha256) { throw "Frozen13 artifact changed: $($artifact.Path)" }
}
$manifest = [ordered]@{
    CreatedUtc=[DateTime]::UtcNow.ToString('o')
    Purpose='Reviewed source14 paid power-operator duty, observation-local kOS demand lookup with nonnegative inputs, 22px maintenance detail viewport, retained exact charter review/retry UI and corrected diagnostic-only support/raw-grid heading frame; development candidate only.'
    Build='Bridge and both native harnesses clean; exact final focused74/74 and installed read-only11/11. Main472/472 preceded final Bridge-only negative-input guard; it was not rerun afterward. Existing disconnected UI53/53 retained without rerun.'
    Acceptance='Pending root acceptance; no runtime certification, release or installation'
    RequireProfile=$true
    ParentFreeze=[ordered]@{Path=$parentRoot;ManifestSha256=$parentHash;All29ArtifactHashesPreserved=$true}
    TestResults=(Join-Path $validationRoot 'main-source14.trx')
    TestResultsSha256=(Get-FileHash -LiteralPath (Join-Path $validationRoot 'main-source14.trx') -Algorithm SHA256).Hash
    NativeProof='Not executed in this phase; native crew/operator commissioning, utility/endurance/BRP continuation, full paid startup and matched performance remain open'
    MainRegressionBoundary='472 unchanged-Domain regressions ran before final nonnegative instruction-rate/byte-rate/disk guard. Source-linked74 and all three native builds cover the final source boundary.'
    DiagnosticBoundary='Optional heading0 preserves prior callers/native13 evidence. Raw grid now uses same request-heading rotation as support contacts and records frame. Offline source/direct90degree math2/2 only; no native geometry proof.'
    Artifacts=$native
    DesktopArtifacts=$desktop
    TemplateTerms=@($templateTerms | Sort-Object Id)
    Evidence=$evidence
    Source=$source
}
$manifest | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $FrozenDirectory 'freeze-manifest.json') -Encoding utf8
Get-ChildItem -LiteralPath $FrozenDirectory -Recurse -File | ForEach-Object { $_.IsReadOnly=$true }
[ordered]@{FrozenDirectory=$FrozenDirectory;ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $FrozenDirectory 'freeze-manifest.json') -Algorithm SHA256).Hash;NativeArtifacts=$native.Count;DesktopArtifacts=$desktop.Count;Evidence=$evidence.Count;Source=$source.Count;RootAcceptance='pending';Parent13Preserved=$true} | ConvertTo-Json
