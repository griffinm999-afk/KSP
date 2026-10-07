$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'build.ps1')
$mod=Join-Path $sourceRoot 'GameData\ExpanseFoundations'
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination $mod -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $mod -Force
if(Test-Path -LiteralPath (Join-Path $sourceRoot 'TEST-EVIDENCE.md')){Copy-Item -LiteralPath (Join-Path $sourceRoot 'TEST-EVIDENCE.md') -Destination $mod -Force}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'licenses') -Destination $mod -Recurse -Force
$out=Join-Path $sourceRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$zip=Join-Path $out 'ExpanseFoundations-0.2.0-preview.zip'
Compress-Archive -LiteralPath (Join-Path $sourceRoot 'GameData') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip -Algorithm SHA256
