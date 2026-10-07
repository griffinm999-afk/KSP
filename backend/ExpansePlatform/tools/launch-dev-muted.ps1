[CmdletBinding()]
param([string]$DevRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev', [string]$PublisherPipeName, [string]$EffectsPipeName)
$ErrorActionPreference = 'Stop'
function Assert-NoReparsePath([string]$Path) {
  $full = [IO.Path]::GetFullPath($Path)
  $driveRoot = [IO.Path]::GetPathRoot($full)
  $cursor = $driveRoot
  foreach ($part in $full.Substring($driveRoot.Length).TrimEnd('\').Split('\')) {
    if (-not $part) { continue }
    $cursor = Join-Path $cursor $part
    $item = Get-Item -LiteralPath $cursor -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing reparse-point path component: $cursor" }
  }
}
$approved = @(
  [IO.Path]::GetFullPath('C:\Users\griff\Documents\KSP-RMM-Dev').TrimEnd('\'),
  [IO.Path]::GetFullPath('C:\Users\griff\Documents\KSP-RMM-Dev-Full').TrimEnd('\')
)
$resolved = [IO.Path]::GetFullPath($DevRoot).TrimEnd('\')
if (-not @($approved | Where-Object { $_ -ieq $resolved }).Count) { throw "Muted launch is restricted to the two approved dev installs; got $resolved" }
Assert-NoReparsePath $resolved
$production = [IO.Path]::GetFullPath('C:\Kerbal Space Program').TrimEnd('\')
if ($resolved -ieq $production -or $resolved.StartsWith($production + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to launch from the production KSP tree.' }
$exe = Join-Path $resolved 'KSP_x64.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Development KSP executable not found: $exe" }
$processes = @(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'")
$unresolved = @($processes | Where-Object { -not $_.ExecutablePath })
if ($unresolved.Count) { throw "Cannot verify KSP install path for running PID(s): $(($unresolved.ProcessId -join ', ')); refusing to launch another game." }
$runningDev = @($processes | Where-Object { $_.ExecutablePath.Equals($exe, [StringComparison]::OrdinalIgnoreCase) })
if ($runningDev.Count) { throw "Development KSP is already running (PID $($runningDev[0].ProcessId))." }
$launchArgs = @('-muteaudio')
if ($PublisherPipeName) {
  $prefix = "ExpanseFoundations.Clock.Publisher.dev.$([Environment]::UserName)."
  if (-not $PublisherPipeName.StartsWith($prefix, [StringComparison]::Ordinal) -or $PublisherPipeName.Length -gt 200 -or $PublisherPipeName -cnotmatch '^[A-Za-z0-9._-]+$' -or $PublisherPipeName.Length -eq $prefix.Length) {
    throw "Development publisher pipe must start with $prefix and contain a bounded ASCII suffix."
  }
  $launchArgs += "-expanseClockPublisherPipe=$PublisherPipeName"
} elseif ($processes.Count) {
  throw "Another KSP process is running (PID $($processes[0].ProcessId)); supply an isolated -PublisherPipeName for the dev game."
}
if ($EffectsPipeName) {
  $prefix = "ExpanseFoundations.Effects.dev.$([Environment]::UserName)."
  if (-not $EffectsPipeName.StartsWith($prefix, [StringComparison]::Ordinal) -or $EffectsPipeName.Substring($prefix.Length) -cnotmatch '^[A-Za-z0-9_-]{1,64}$') {
    throw "Development effects pipe must start with $prefix and have a 1..64 character ASCII token."
  }
  $launchArgs += "-expanseEffectsPipe=$EffectsPipeName"
}
$settings = Join-Path $resolved 'settings.cfg'
if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) { throw "Settings file not found: $settings" }
$platformRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backupRoot = Join-Path $platformRoot 'artifacts\deploy-backups'
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$backup = Join-Path $backupRoot ("settings.dev.$(Get-Date -Format 'yyyyMMdd-HHmmss-fff').cfg")
Copy-Item -LiteralPath $settings -Destination $backup
$content = Get-Content -LiteralPath $settings -Raw
foreach ($key in @('MASTER_VOLUME','SHIP_VOLUME','AMBIENCE_VOLUME','MUSIC_VOLUME','UI_VOLUME','VOICE_VOLUME')) {
  $pattern = '(?m)^\s*' + [regex]::Escape($key) + '\s*=.*$'
  if ($content -match $pattern) { $content = [regex]::Replace($content, $pattern, "$key = 0") }
  else { throw "Expected audio setting is missing: $key. Original is backed up at $backup" }
}
Set-Content -LiteralPath $settings -Value $content -NoNewline
$proc = Start-Process -FilePath $exe -ArgumentList $launchArgs -WorkingDirectory $resolved -WindowStyle Hidden -PassThru
$runDir = Join-Path $platformRoot 'run'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
Set-Content -LiteralPath (Join-Path $runDir 'dev-ksp.pid') -Value $proc.Id
Set-Content -LiteralPath (Join-Path $runDir 'dev-ksp.path') -Value $exe
Set-Content -LiteralPath (Join-Path $runDir 'dev-ksp.args') -Value ($launchArgs -join ' ')
Write-Output "Muted development KSP started hidden (PID $($proc.Id)); settings backup: $backup"

