[CmdletBinding()]
param([string]$KspRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev')

$requiredRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev'
$resolvedRoot = [System.IO.Path]::GetFullPath($KspRoot).TrimEnd('\')
if (-not [string]::Equals($resolvedRoot, $requiredRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing non-development KSP root: $resolvedRoot" }
$rootItem = Get-Item -LiteralPath $resolvedRoot -ErrorAction Stop
if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing a reparse-point KSP root: $resolvedRoot" }
$pathRoot = [IO.Path]::GetPathRoot($resolvedRoot)
$ancestor = $resolvedRoot
while ($ancestor) {
    $ancestorItem = Get-Item -LiteralPath $ancestor -ErrorAction Stop
    if (($ancestorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing a path through reparse-point ancestor: $ancestor" }
    if ([string]::Equals($ancestor, $pathRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = Split-Path -Parent $ancestor
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot 'KSP_x64_Data\Managed\Assembly-CSharp.dll') -PathType Leaf)) { throw 'KSP 1.12.5 managed references are missing.' }

$running = Get-Process -Name KSP_x64,KSP -ErrorAction SilentlyContinue
if ($running) { throw 'A KSP process is already running. Close it before creating a smoke request.' }
$settingsPath = Join-Path $resolvedRoot 'settings.cfg'
$requestPath = Join-Path $resolvedRoot 'ExpanseClockSmoke.request'
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) { throw "Missing settings file: $settingsPath" }
if ((Get-Item -LiteralPath $settingsPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a reparse-point settings.cfg.' }
if (Test-Path -LiteralPath $requestPath) { throw "A smoke request already exists: $requestPath" }

$settings = [IO.File]::ReadAllText($settingsPath)
$volumeKeys = @('MASTER_VOLUME','SHIP_VOLUME','AMBIENCE_VOLUME','MUSIC_VOLUME','UI_VOLUME','VOICE_VOLUME')
foreach ($key in $volumeKeys) {
    $pattern = '(?m)^\s*' + [Regex]::Escape($key) + '\s*=.*$'
    if (-not [Regex]::IsMatch($settings, $pattern)) { throw "Expected audio setting '$key' was not found; settings left untouched." }
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$backupPath = Join-Path $resolvedRoot "settings.clock-smoke-backup-$stamp.cfg"
Copy-Item -LiteralPath $settingsPath -Destination $backupPath -ErrorAction Stop
foreach ($key in $volumeKeys) {
    $pattern = '(?m)^\s*' + [Regex]::Escape($key) + '\s*=.*$'
    $settings = [Regex]::Replace($settings, $pattern, "$key = 0")
}
[IO.File]::WriteAllText($settingsPath, $settings, [Text.UTF8Encoding]::new($false))

$requestId = [Guid]::NewGuid().ToString('N')
$requestBytes = [Text.Encoding]::ASCII.GetBytes("RUN_EXPANSE_CLOCK_SMOKE=$requestId`r`n")
$stream = [IO.File]::Open($requestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($requestBytes, 0, $requestBytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output "Muted development audio settings (backup: $backupPath)."
Write-Output "One-shot smoke request created: $requestPath (id $requestId)."
Write-Output 'The script does not launch KSP. After the harness DLL is installed in this dev instance, launch it manually and run the observer separately.'
