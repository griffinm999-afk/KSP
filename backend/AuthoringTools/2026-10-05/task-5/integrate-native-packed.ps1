$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$stage=Join-Path $PSScriptRoot 'colony-diagnostic-work'
$baseline=Get-Content (Join-Path $PSScriptRoot 'native-packed-integration-baseline.json') -Raw|ConvertFrom-Json
$review=Get-Content (Join-Path $PSScriptRoot 'astra-missing-callbacks\mode-candidate-reviewed-hashes.json') -Raw|ConvertFrom-Json
# Reviewed stage hashes are additionally pinned by the root immediately after green.
$approved=Get-Content (Join-Path $PSScriptRoot 'native-packed-approved-hashes.json') -Raw|ConvertFrom-Json
$new='dev\Expanse.ProductionTelemetry.Tests\ProductionTelemetryModeTests.cs'
function Hash($p){(Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash}
foreach($row in $baseline){if((Hash (Join-Path $primary $row.path)) -ne $row.primarySha256){throw ('Primary changed concurrently: '+$row.path)}}
if(Test-Path -LiteralPath (Join-Path $primary $new)){throw 'New test path already exists in primary'}
foreach($row in $approved){if((Hash (Join-Path $stage $row.path)) -ne $row.sha256){throw 'Reviewed stage changed'}}
$backup=Join-Path $PSScriptRoot ('native-packed-source-backup-'+(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup|Out-Null
foreach($row in $baseline){$p=Join-Path $backup $row.path;New-Item -ItemType Directory -Force -Path (Split-Path $p)|Out-Null;Copy-Item -LiteralPath (Join-Path $primary $row.path) -Destination $p;if((Hash $p) -ne $row.primarySha256){throw 'Backup mismatch'}}
foreach($row in $baseline){if((Hash (Join-Path $primary $row.path)) -ne $row.primarySha256){throw 'Primary changed after backup'}}
foreach($row in $approved){Copy-Item -LiteralPath (Join-Path $stage $row.path) -Destination (Join-Path $primary $row.path);if((Hash (Join-Path $primary $row.path)) -ne $row.sha256){throw 'Integrated hash mismatch'}}
[pscustomobject]@{status='reviewed-primary-source-integrated';backup=$backup;files=$approved;runtimeInstalled=$false}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $PSScriptRoot 'native-packed-integration-result.json')
Get-Content (Join-Path $PSScriptRoot 'native-packed-integration-result.json')
