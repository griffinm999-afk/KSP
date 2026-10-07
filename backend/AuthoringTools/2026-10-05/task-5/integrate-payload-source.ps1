param([switch]$Apply,[string]$ManifestName='budget-contract-changes.json')
$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$stage=Join-Path $PSScriptRoot 'performance-work'
$changes=Get-Content -LiteralPath (Join-Path $PSScriptRoot $ManifestName) -Raw|ConvertFrom-Json
function Hash($path){if(Test-Path -LiteralPath $path -PathType Leaf){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}else{$null}}
function Target($root,$relative){$path=[IO.Path]::GetFullPath((Join-Path $root $relative));if(!$path.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source path escaped target root'};return $path}
$checks=foreach($change in $changes){
  $source=Target $stage $change.path;$target=Target $primary $change.path
  if((Hash $source) -ne $change.after){throw "Staged source differs from tested package: $($change.path)"}
  $actual=Hash $target
  if($actual -eq $change.after){$status='already-applied'}elseif($actual -eq $change.before){$status='ready'}else{throw "Primary source changed; reconcile before applying: $($change.path)"}
  [pscustomobject]@{path=$change.path;before=$change.before;after=$change.after;primaryHash=$actual;status=$status}
}
$result=[ordered]@{mode=if($Apply){'apply'}else{'read-only-preflight'};primary=$primary;stage=$stage;files=$checks}
if($Apply){
  $backup=Join-Path $PSScriptRoot ('payload-source-backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
  $null=New-Item -ItemType Directory -Path $backup
  foreach($check in $checks|Where-Object status -eq 'ready'){
    $target=Target $primary $check.path
    if($null -ne $check.before){$saved=Target $backup $check.path;$null=New-Item -ItemType Directory -Path (Split-Path $saved) -Force;Copy-Item -LiteralPath $target -Destination $saved}
  }
  $checks|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $backup 'manifest.json')
  foreach($check in $checks|Where-Object status -eq 'ready'){
    $target=Target $primary $check.path
    if((Hash $target) -ne $check.before){throw "Primary changed after preflight; backed-up partial integration must be reviewed: $($check.path)"}
    $null=New-Item -ItemType Directory -Path (Split-Path $target) -Force
    Copy-Item -LiteralPath (Target $stage $check.path) -Destination $target -Force
    if((Hash $target) -ne $check.after){throw "Installed source hash mismatch: $($check.path)"}
  }
  $result.backup=$backup
}
$result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'payload-source-integration-result.json')
[pscustomobject]@{Mode=$result.mode;Ready=@($checks|Where-Object status -eq 'ready').Count;AlreadyApplied=@($checks|Where-Object status -eq 'already-applied').Count;Primary=$primary;Stage=$stage}
