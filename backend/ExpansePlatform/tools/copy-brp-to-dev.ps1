[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourceGameData = 'C:\Kerbal Space Program\GameData'
$devRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev'
$devGameData = Join-Path $devRoot 'GameData'
$platformRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$backupRoot = Join-Path $platformRoot 'artifacts\deploy-backups'
$names = @('000_KSPBurst', 'BackgroundResourceProcessing')

function Assert-PlainTree([string]$path) {
  $full = [IO.Path]::GetFullPath($path)
  $cursor = [IO.Path]::GetPathRoot($full)
  foreach ($part in $full.Substring($cursor.Length).TrimEnd('\').Split('\')) {
    if (-not $part) { continue }
    $cursor = Join-Path $cursor $part
    if (Test-Path -LiteralPath $cursor) {
      $entry = Get-Item -LiteralPath $cursor -Force
      if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing reparse-point path: $cursor" }
    }
  }
  if (Test-Path -LiteralPath $full) {
    $links = @(Get-ChildItem -LiteralPath $full -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($links.Count) { throw "Refusing reparse item within $full" }
  }
}

function Assert-Child([string]$path, [string]$parent) {
  $full = [IO.Path]::GetFullPath($path)
  $root = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
  if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes intended root: $full" }
}

if ([IO.Path]::GetFullPath($devRoot).TrimEnd('\') -ine 'C:\Users\griff\Documents\KSP-RMM-Dev') { throw 'Wrong dev root' }
foreach ($path in @($sourceGameData, $devRoot, $devGameData, $platformRoot, $backupRoot)) { Assert-PlainTree $path }
$devExe = Join-Path $devRoot 'KSP_x64.exe'
if (-not (Test-Path -LiteralPath $devExe -PathType Leaf)) { throw 'Exact dev KSP executable is missing.' }
$active = @(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'" | Where-Object {
  -not $_.ExecutablePath -or $_.ExecutablePath.Equals($devExe, [StringComparison]::OrdinalIgnoreCase)
})
if ($active.Count) { throw "Dev or unresolved KSP process is running: $(($active.ProcessId -join ', '))" }

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
foreach ($name in $names) {
  $source = Join-Path $sourceGameData $name
  $target = Join-Path $devGameData $name
  $stage = Join-Path $devGameData ('.ExpanseStage-' + $name + '-' + $stamp)
  $backup = Join-Path $backupRoot ($name + '.dev-before-' + $stamp)
  foreach ($path in @($source, $target, $stage)) { Assert-Child $path $(if ($path -eq $source) { $sourceGameData } else { $devGameData }) }
  Assert-Child $backup $backupRoot
  if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Source is missing: $source" }
  if (Test-Path -LiteralPath $stage) { throw "Stage already exists: $stage" }
  if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
  Assert-PlainTree $source
  if (Test-Path -LiteralPath $target) { Assert-PlainTree $target }

  Copy-Item -LiteralPath $source -Destination $stage -Recurse
  $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File)
  $stageFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
  if ($sourceFiles.Count -ne $stageFiles.Count) { throw "File-count mismatch for $name" }
  foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($source.Length).TrimStart('\')
    $copy = Join-Path $stage $relative
    if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) {
      throw "Copy hash mismatch: $name/$relative"
    }
  }
  if (Test-Path -LiteralPath $target) {
    Assert-PlainTree $target
    Move-Item -LiteralPath $target -Destination $backup
    Write-Output "Backed up existing dev $name to $backup"
  }
  Move-Item -LiteralPath $stage -Destination $target
  Write-Output "Copied $name to isolated dev GameData ($($sourceFiles.Count) files; hashes verified)."
}
