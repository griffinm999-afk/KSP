$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$baseline=Join-Path $PSScriptRoot 'ExpansePlatform-clock256-20261006\Source'
$stage=Join-Path $PSScriptRoot 'colony-diagnostic-work'
Get-Content -LiteralPath (Join-Path $primary 'AGENTS.md')
$files=@('src\Expanse.Clock.Core\ClockProtocol.cs','src\Expanse.Clock.Core\ColonyProductionTelemetryProtocol.cs','src\Expanse.Clock.Core\ColonyValidationDiagnostic.cs','src\Expanse.Clock.Host\Program.cs','src\Expanse.Clock.Host\ColonyRejectionLog.cs','tests\ColonyValidationDiagnosticTests.cs','tests\ColonyRejectionLogTests.cs')
function Hash([string]$path){(Get-FileHash -LiteralPath $path).Hash}
$rows=foreach($rel in $files){$old=Join-Path $baseline $rel;$dest=Join-Path $primary $rel;$new=Join-Path $stage $rel;if(!(Test-Path -LiteralPath $new)){throw 'Staged source missing'};if(Test-Path -LiteralPath $old){if(!(Test-Path -LiteralPath $dest) -or (Hash $old) -ne (Hash $dest)){throw ('Primary target changed concurrently: '+$rel)}}elseif(Test-Path -LiteralPath $dest){throw ('New primary target already exists: '+$rel)};[pscustomobject]@{relative=$rel;existed=(Test-Path -LiteralPath $dest);before=if(Test-Path -LiteralPath $dest){Hash $dest}else{$null};after=Hash $new}}
$stamp=(Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss');$backup=Join-Path $PSScriptRoot ('host-colony-source-backup-'+$stamp)
New-Item -ItemType Directory -Path $backup|Out-Null
foreach($row in $rows){if($row.existed){$dest=Join-Path $backup $row.relative;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $primary $row.relative) -Destination $dest;if((Hash $dest) -ne $row.before){throw 'Source backup mismatch'}}}
$rows|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $backup 'source-manifest.json')
foreach($row in $rows){$dest=Join-Path $primary $row.relative;if($row.existed -and (Hash $dest) -ne $row.before){throw 'Primary target changed after backup'};Copy-Item -LiteralPath (Join-Path $stage $row.relative) -Destination $dest;if((Hash $dest) -ne $row.after){throw 'Integrated source hash mismatch'}}
[pscustomobject]@{status='primary-source-integrated';primary=$primary;backup=$backup;changedFiles=$files;archive='AD44 archive retains original Host component; installed Host has a recorded two-DLL overlay.';installedOverlayReceipt=(Join-Path $PSScriptRoot 'host-colony-empty-recipe-fix-install.json')}|ConvertTo-Json|Tee-Object -FilePath (Join-Path $PSScriptRoot 'host-colony-primary-integration.json')
