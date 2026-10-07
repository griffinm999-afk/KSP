[CmdletBinding()]
param([ValidateSet('Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$bridge = Join-Path $root 'src\Expanse.WorldBridge\bin\Release\net472'
$hostDir = Join-Path $root 'src\Expanse.Clock.Host\bin\Release\net8.0'
$managerDir = Join-Path $root 'src\Expanse.Clock.Manager\bin\Release\net8.0-windows'
foreach ($file in @(
  (Join-Path $bridge 'Expanse.WorldBridge.dll'), (Join-Path $bridge 'Expanse.Domain.dll'),
  (Join-Path $hostDir 'Expanse.Clock.Host.exe'), (Join-Path $managerDir 'Expanse.Clock.Manager.exe')
)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release build: $file" } }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$name = "ExpanseFoundations-colony-inspector-$stamp"
$stage = Join-Path $root "artifacts\$name"
if (Test-Path -LiteralPath $stage) { throw "Release already exists: $stage" }
$plugin = Join-Path $stage 'GameData\ExpanseWorldBridge\Plugins'
$stageHost = Join-Path $stage 'Host'
$stageManager = Join-Path $stage 'Manager'
New-Item -ItemType Directory -Force -Path $plugin,$stageHost,$stageManager | Out-Null
Copy-Item -LiteralPath (Join-Path $bridge 'Expanse.WorldBridge.dll'),(Join-Path $bridge 'Expanse.Domain.dll') -Destination $plugin
function Copy-Runtime([string]$source,[string]$destination) {
  $source = (Resolve-Path -LiteralPath $source).Path
  foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    if ($file.Extension -eq '.pdb') { continue }
    $relative = $file.FullName.Substring($source.Length).TrimStart('\','/')
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
  }
}
Copy-Runtime $hostDir $stageHost
Copy-Runtime $managerDir $stageManager
Copy-Item -LiteralPath (Join-Path $root 'docs\COLONY-INSPECTOR.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $root 'docs\ORE-EXPORT-DESIGN.md'),(Join-Path $root 'docs\RECOVERY-DELIVERY-USAGE.md') -Destination $stage
@'
Expanse Foundations delivery + colony inspector release

The Manager enables Send once only when the current game's exact saved route,
selected source stock, destination capacity and BRP provider capability pass
the Host's fresh preflight. Remote effects apply only to registered selected
tanks on unloaded BRP 0.2.7 vessels. Loaded or unsupported endpoints remain
held. The game must be running for delivery commands and arrival settlement.

This archive contains only the production bridge, Host and Manager runtime.
No test fixture, test save or one-shot request is included. Keep the matching
Host and Bridge versions together.

The Manager's Colony tab reads landed MKS/WOLF vessel tanks and configured
converters. Read COLONY-INSPECTOR.md for observation limits and scope. WOLF
virtual biome allocations are not part of the live physical tank readings.
Close KSP and the running Expanse Host/Manager before installing this package.

Modeled Ore exports use explicitly registered physical source tanks. Each
configured whole-Ore batch travels for 64,800 game seconds by default, then
simulated Kerbin recovery credits exactly 100 Career funds per Ore unit.
The default quantity is 1,000 Ore (100,000 funds); quantity is configurable
from 1 to 1,000,000 whole units. Departed historical cargo keeps its old price.
No vessel is spawned.
Source vessels must be unloaded and the Career recovery capability available.
Use the matching Host, Manager and Bridge together; economic capsules use EXS4.
Read ORE-EXPORT-DESIGN.md and RECOVERY-DELIVERY-USAGE.md before configuring.
This package has synthetic verification; no in-game payment fixture is claimed.
'@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding UTF8
$manifest = Join-Path $stage 'SHA256SUMS.txt'
Get-ChildItem -LiteralPath $stage -Recurse -File |
  Where-Object { $_.FullName -ne $manifest } |
  Sort-Object FullName |
  ForEach-Object {
    $relative = $_.FullName.Substring($stage.Length).TrimStart('\','/')
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
  } | Set-Content -LiteralPath $manifest -Encoding UTF8
$zip = "$stage.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
[pscustomobject]@{Name=$name; Stage=$stage; Archive=$zip; Sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash}
