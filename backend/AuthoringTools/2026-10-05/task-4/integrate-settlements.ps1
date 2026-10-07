$ErrorActionPreference='Stop'
$repo='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$stage=Join-Path $PSScriptRoot 'settlement-work'
$changes=Get-Content (Join-Path $PSScriptRoot 'settlement-changes.json') -Raw | ConvertFrom-Json
foreach($change in $changes) {
 $target=Join-Path $repo $change.Path
 if($change.OriginalHash) {if((Get-FileHash -LiteralPath $target).Hash -ne $change.OriginalHash){throw "Concurrent change: $($change.Path)"}}
 elseif(Test-Path -LiteralPath $target){throw "New path now exists: $($change.Path)"}
 if((Get-FileHash -LiteralPath (Join-Path $stage $change.Path)).Hash -ne $change.Hash){throw "Staged hash changed: $($change.Path)"}
}
foreach($change in $changes) {
 $target=Join-Path $repo $change.Path
 New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
 Copy-Item -LiteralPath (Join-Path $stage $change.Path) -Destination $target
}
Write-Output "Integrated $($changes.Count) journal source/test/contract files, preserving all other files."
