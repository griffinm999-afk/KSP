[CmdletBinding()]
param(
    [string]$KspRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev',
    [string]$KnownProductionRoot = 'C:\Kerbal Space Program'
)

$requiredRoot = 'C:\Users\griff\Documents\KSP-RMM-Dev'
$productionRoot = [IO.Path]::GetFullPath($KnownProductionRoot).TrimEnd('\')
$resolvedRoot = [IO.Path]::GetFullPath($KspRoot).TrimEnd('\')
if (-not [string]::Equals($resolvedRoot, $requiredRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing non-development KSP root: $resolvedRoot" }
function Assert-NoReparseAncestors([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($resolved)
    $cursor = $resolved
    while ($cursor) {
        $item = Get-Item -LiteralPath $cursor -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing path through reparse point: $cursor" }
        if ([string]::Equals($cursor, $rootPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = Split-Path -Parent $cursor
    }
}
Assert-NoReparseAncestors $resolvedRoot
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot 'KSP_x64_Data\Managed\Assembly-CSharp.dll') -PathType Leaf)) { throw 'KSP managed references are missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot 'GameData\ExpanseWorldBridge\Plugins\Expanse.WorldBridge.dll') -PathType Leaf)) { throw 'The development install does not contain Expanse.WorldBridge.' }
$harnessDll = Join-Path $resolvedRoot 'GameData\ExpanseDepotM2Smoke\Plugins\Expanse.Depot.M2.SmokeHarness.dll'
if (-not (Test-Path -LiteralPath $harnessDll -PathType Leaf)) { throw "Stage the reviewed development-only harness DLL first: $harnessDll" }

# A running production game is permitted only at the explicitly known production path.
foreach ($process in (Get-Process -Name KSP_x64,KSP -ErrorAction SilentlyContinue)) {
    try { $exe = [IO.Path]::GetFullPath($process.Path) } catch { throw "Cannot verify KSP process $($process.Id); refusing to create a one-shot request." }
    if ($exe.StartsWith($resolvedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Development KSP is running: $exe" }
    if (-not $exe.StartsWith($productionRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "KSP process is outside both the dev root and explicitly allowed production root: $exe" }
}

$settingsPath = Join-Path $resolvedRoot 'settings.cfg'
$requestPath = Join-Path $resolvedRoot 'ExpanseDepotM2Smoke.request'
Assert-NoReparseAncestors $settingsPath
if (Test-Path -LiteralPath $requestPath) { throw "A depot M2 request already exists: $requestPath" }
$settings = [IO.File]::ReadAllText($settingsPath)
$keys = @('MASTER_VOLUME','SHIP_VOLUME','AMBIENCE_VOLUME','MUSIC_VOLUME','UI_VOLUME','VOICE_VOLUME')
foreach ($key in $keys) {
    if (-not [Regex]::IsMatch($settings, '(?m)^\s*' + [Regex]::Escape($key) + '\s*=.*$')) { throw "Expected audio setting '$key' is absent; settings left untouched." }
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$backupPath = Join-Path $resolvedRoot "settings.depot-m2-backup-$stamp.cfg"
Copy-Item -LiteralPath $settingsPath -Destination $backupPath -ErrorAction Stop
foreach ($key in $keys) {
    $settings = [Regex]::Replace($settings, '(?m)^\s*' + [Regex]::Escape($key) + '\s*=.*$', "$key = 0")
}
[IO.File]::WriteAllText($settingsPath, $settings, [Text.UTF8Encoding]::new($false))
$token = [Guid]::NewGuid().ToString('N')
$bytes = [Text.Encoding]::ASCII.GetBytes("RUN_EXPANSE_DEPOT_M2=$token`r`n")
$stream = [IO.File]::Open($requestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
Write-Output "Muted dev settings (backup: $backupPath)."
Write-Output "One-shot request created: $requestPath (token $token)."
Write-Output 'This script does not install files, start KSP, or modify any save. Start Host and Observer, then manually start the exact dev KSP root.'
