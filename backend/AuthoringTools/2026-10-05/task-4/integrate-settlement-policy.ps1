$ErrorActionPreference='Stop'
$repo='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$stage=Join-Path $PSScriptRoot 'settlement-work'
$updates=Get-Content (Join-Path $PSScriptRoot 'settlement-policy-updates.json') -Raw | ConvertFrom-Json
foreach($update in $updates){if((Get-FileHash -LiteralPath (Join-Path $repo $update.Path)).Hash -ne $update.ExpectedCurrentHash){throw "Concurrent change: $($update.Path)"};if((Get-FileHash -LiteralPath (Join-Path $stage $update.Path)).Hash -ne $update.NewHash){throw 'Staged hash mismatch'}}
foreach($update in $updates){Copy-Item -LiteralPath (Join-Path $stage $update.Path) -Destination (Join-Path $repo $update.Path)}
Write-Output "Integrated $($updates.Count) observer-only policy revision files; unrelated changes preserved."
