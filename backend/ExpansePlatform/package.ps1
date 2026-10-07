[CmdletBinding()]
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
  [string]$BridgeBuildDir,
  [string]$HostBuildDir,
  [string]$ManagerBuildDir
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$bridgeDir = Join-Path $root "src\Expanse.WorldBridge\bin\$Configuration\net472"
$hostDir = Join-Path $root "src\Expanse.Clock.Host\bin\$Configuration\net8.0"
$managerDir = Join-Path $root "src\Expanse.Clock.Manager\bin\$Configuration\net8.0-windows"
if ($BridgeBuildDir) { $bridgeDir = (Resolve-Path -LiteralPath $BridgeBuildDir).Path }
if ($HostBuildDir) { $hostDir = (Resolve-Path -LiteralPath $HostBuildDir).Path }
if ($ManagerBuildDir) { $managerDir = (Resolve-Path -LiteralPath $ManagerBuildDir).Path }

foreach ($path in @(
  (Join-Path $bridgeDir 'Expanse.WorldBridge.dll'),
  (Join-Path $bridgeDir 'Expanse.Domain.dll'),
  (Join-Path $hostDir 'Expanse.Clock.Host.exe'),
  (Join-Path $hostDir 'Expanse.Domain.dll'),
  (Join-Path $hostDir 'Microsoft.Data.Sqlite.dll'),
  (Join-Path $hostDir 'runtimes\win-x64\native\e_sqlite3.dll'),
  (Join-Path $managerDir 'Expanse.Clock.Manager.exe'),
  (Join-Path $managerDir 'Expanse.Domain.dll')
)) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Runtime build output missing: $path" }
}

$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$stage = Join-Path $artifacts "ExpanseRecovery-M3-dev-$stamp"
if (Test-Path -LiteralPath $stage) { throw "Stage already exists: $stage" }
$pluginDir = Join-Path $stage 'GameData\ExpanseWorldBridge\Plugins'
$stageHost = Join-Path $stage 'Host'
$stageManager = Join-Path $stage 'Manager'
New-Item -ItemType Directory -Force -Path $pluginDir,$stageHost,$stageManager | Out-Null
Copy-Item -LiteralPath (Join-Path $bridgeDir 'Expanse.WorldBridge.dll'),(Join-Path $bridgeDir 'Expanse.Domain.dll') -Destination $pluginDir -ErrorAction Stop

function Copy-Runtime([string]$source, [string]$destination) {
  $source = (Resolve-Path -LiteralPath $source).Path
  foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    if ($file.Extension -eq '.pdb') { continue }
    $relative = $file.FullName.Substring($source.Length).TrimStart('\','/')
    $target = Join-Path $destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -ErrorAction Stop
  }
}
Copy-Runtime $hostDir $stageHost
Copy-Runtime $managerDir $stageManager
Copy-Item -LiteralPath (Join-Path $root 'docs\RECOVERY-DELIVERY-USAGE.md'),(Join-Path $root 'docs\RECOVERY-DELIVERY-PACKAGE.md'),(Join-Path $root 'docs\RECOVERY-DELIVERY-WORK-EVIDENCE.md') -Destination $stage -ErrorAction Stop
@'
Expanse Foundations recovery/delivery DEVELOPMENT PACKAGE

This package is for reviewed development evidence only. Physical effects require
an explicit, isolated dev-only capability; they are disabled in ordinary KSP.
Loaded active-vessel transfers have bounded evidence. Distant/unloaded provider
writes remain unavailable. Do not copy this package to a production KSP install.

The GameData folder contains only the normal bridge and matching .NET Framework
domain DLL. Host and Manager contain their complete .NET 8 runtime outputs.
Fixtures, one-shot requests, save files and test logs are deliberately excluded.
No normal/default-pipe launcher is included in this development package.
Read RECOVERY-DELIVERY-USAGE.md, RECOVERY-DELIVERY-PACKAGE.md and
RECOVERY-DELIVERY-WORK-EVIDENCE.md before use.
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
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -ErrorAction Stop
Write-Output "Development runtime package: $zip"
Get-FileHash -LiteralPath $zip -Algorithm SHA256
