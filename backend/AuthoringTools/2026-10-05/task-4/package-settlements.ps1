$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$stage=Join-Path $root 'settlement-work'
$repo='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$artifact=Join-Path $root 'settlement-journal-development'
if(Test-Path -LiteralPath $artifact){throw 'Artifact already exists'}
$sourceFiles=Get-ChildItem (Join-Path $stage src) -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$sourceManifest=foreach($file in $sourceFiles){$rel=$file.FullName.Substring($stage.Length+1);$hash=(Get-FileHash -LiteralPath $file.FullName).Hash; if((Get-FileHash -LiteralPath (Join-Path $repo $rel)).Hash -ne $hash){throw "Source changed since build: $rel"};[PSCustomObject]@{Path=$rel;SHA256=$hash}}
New-Item -ItemType Directory -Force $artifact | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'package/GameData') -Destination $artifact -Recurse
$plugins=Join-Path $artifact 'GameData/ExpanseWorldBridge/Plugins'
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'src/Expanse.WorldBridge/bin/Release/net472/Expanse.WorldBridge.dll'),(Join-Path $stage 'src/Expanse.WorldBridge/bin/Release/net472/Expanse.Domain.dll') -Destination $plugins
foreach($runtime in @(@('src/Expanse.Clock.Host/bin/Release/net8.0','Host'),@('src/Expanse.Clock.Manager/bin/Release/net8.0-windows','Manager'))) {
 $from=Join-Path $stage $runtime[0];$to=Join-Path $artifact $runtime[1]
 foreach($file in Get-ChildItem -LiteralPath $from -Recurse -File | Where-Object Extension -ne '.pdb'){$rel=$file.FullName.Substring($from.Length+1);$target=Join-Path $to $rel;New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $target}
}
$changes=Get-Content (Join-Path $root 'settlement-changes.json') -Raw | ConvertFrom-Json
foreach($change in $changes){$target=Join-Path $artifact (Join-Path 'SourceChanges' $change.Path);New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null;Copy-Item -LiteralPath (Join-Path $stage $change.Path) -Destination $target}
Copy-Item -LiteralPath (Join-Path $stage 'docs/SETTLEMENT-JOURNAL-CONTRACT.md') -Destination $artifact
Copy-Item -LiteralPath (Join-Path $stage 'tests/TestResults/settlement-verification.trx') -Destination $artifact
$sourceManifest | Sort-Object Path | ConvertTo-Json | Set-Content (Join-Path $artifact 'BUILD-SOURCE-SHA256.json')
Copy-Item -LiteralPath (Join-Path $root 'settlement-changes.json') -Destination $artifact
@'
ExpansePlatform settlement journal DEVELOPMENT BUILD — NOT INSTALLED

Matched WorldBridge/net472 Domain and Host/Manager/net8 runtime outputs, built
from the current repository source including all pre-existing changes.
611/611 regression tests pass; journal and observer-failure cases are included. Bridge and Manager
build with zero warnings/errors. Native save/load and Site proxy need separately
authorized live qualification. No game/Host was started or restarted, and no
save, order, Career funds or science was changed during this work.

No installer or launcher is included. The native journal observes existing
accepted funds pipelines and does not add a funds/science setter. Read
SETTLEMENT-JOURNAL-CONTRACT.md for the exact PascalCase pipe schema, baseline,
coverage, attribution, branch persistence, fork witness and retention limits.

SourceChanges contains only the 12 integrated/restored source changes. BUILD-SOURCE-SHA256.json
fingerprints all compiled source inputs so pending census/roster work can be
merged and rebuilt without losing the accounting hooks. This package does not
claim to contain later, unmerged source changes.
'@ | Set-Content (Join-Path $artifact 'README.txt')
$manifest=Join-Path $artifact 'SHA256SUMS.txt'
Get-ChildItem -LiteralPath $artifact -Recurse -File | Sort-Object FullName | ForEach-Object {"$((Get-FileHash -LiteralPath $_.FullName).Hash)  $($_.FullName.Substring($artifact.Length+1))"} | Set-Content $manifest
Compress-Archive -Path (Join-Path $artifact '*') -DestinationPath "$artifact.zip" -CompressionLevel Optimal
Get-FileHash -LiteralPath "$artifact.zip"
