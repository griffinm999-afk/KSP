[CmdletBinding()]
param([string]$DevRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev', [ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
function Assert-NoReparsePath([string]$Path) {
  $full = [IO.Path]::GetFullPath($Path)
  $cursor = [IO.Path]::GetPathRoot($full)
  foreach ($part in $full.Substring([IO.Path]::GetPathRoot($full).Length).TrimEnd('\').Split('\')) {
    if (-not $part) { continue }
    $cursor = Join-Path $cursor $part
    $item = Get-Item -LiteralPath $cursor -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing reparse-point path component: $cursor" }
  }
}
$platformRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$expected = [IO.Path]::GetFullPath('C:\Users\griff\Documents\KSP-RMM-Dev').TrimEnd('\')
$resolved = [IO.Path]::GetFullPath($DevRoot).TrimEnd('\')
if ($resolved -ine $expected) { throw "Deployment is restricted to the approved dev install ($expected); got $resolved" }
Assert-NoReparsePath $resolved
$production = [IO.Path]::GetFullPath('C:\Kerbal Space Program').TrimEnd('\')
if ($resolved -ieq $production -or $resolved.StartsWith($production + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to deploy into the production KSP tree.' }
$exe = Join-Path $resolved 'KSP_x64.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Development KSP executable not found: $exe" }
$processes = @(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'")
$unresolved = @($processes | Where-Object { -not $_.ExecutablePath })
if ($unresolved.Count) { throw "Cannot verify KSP install path for running PID(s): $(($unresolved.ProcessId -join ', ')); refusing deployment." }
$running = @($processes | Where-Object { $_.ExecutablePath.Equals($exe, [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count) { throw "Refusing to replace bridge while development KSP is running (PID $($running[0].ProcessId))." }
$source = Join-Path $platformRoot "src\Expanse.WorldBridge\bin\$Configuration\net472\Expanse.WorldBridge.dll"
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Build the bridge first: $source" }
$domainSource = Join-Path $platformRoot "src\Expanse.Domain\bin\$Configuration\net472\Expanse.Domain.dll"
if (-not (Test-Path -LiteralPath $domainSource -PathType Leaf)) { throw "Build the shared net472 domain first: $domainSource" }
$targetDir = Join-Path $resolved 'GameData\ExpanseWorldBridge\Plugins'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
$target = Join-Path $targetDir 'Expanse.WorldBridge.dll'
$domainTarget = Join-Path $targetDir 'Expanse.Domain.dll'
$backupRoot = Join-Path $platformRoot 'artifacts\deploy-backups'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
if (Test-Path -LiteralPath $target) {
  $backup = Join-Path $backupRoot ("Expanse.WorldBridge.$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').dll")
  Copy-Item -LiteralPath $target -Destination $backup
  Write-Output "Backed up existing dev plugin: $backup"
}
if (Test-Path -LiteralPath $domainTarget) {
  $domainBackup = Join-Path $backupRoot ("Expanse.Domain.$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').dll")
  Copy-Item -LiteralPath $domainTarget -Destination $domainBackup
  Write-Output "Backed up existing dev domain: $domainBackup"
}
Copy-Item -LiteralPath $domainSource -Destination $domainTarget -Force
if ((Get-FileHash -LiteralPath $domainTarget -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $domainSource -Algorithm SHA256).Hash) { throw 'Deployed dev domain hash mismatch; bridge not replaced.' }
Copy-Item -LiteralPath $source -Destination $target -Force
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) { throw 'Deployed dev bridge hash mismatch.' }
Write-Output "Deployed dev bridge: $target"
Write-Output "Deployed dev domain: $domainTarget"

