[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$GameRoot,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPluginSha256
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$game = (Resolve-Path -LiteralPath $GameRoot).Path
$gameData = Join-Path $game 'GameData'
if (-not (Test-Path -LiteralPath $gameData -PathType Container)) { throw 'GameRoot must contain GameData.' }
if (-not (Test-Path -LiteralPath (Join-Path $game 'KSP_x64.exe') -PathType Leaf)) { throw 'GameRoot must contain KSP_x64.exe.' }
$source = Join-Path $package 'GameData\ExpanseTrackingStation\Plugins\Expanse.TrackingStation.dll'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing plugin: $source" }
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $ExpectedPluginSha256) { throw 'Package plugin hash mismatch.' }
$sourceAssembly = [Reflection.AssemblyName]::GetAssemblyName($source)
if ($sourceAssembly.Name -ne 'Expanse.TrackingStation') { throw 'Package DLL has the wrong assembly identity.' }
$destination = [IO.Path]::GetFullPath((Join-Path $gameData 'ExpanseTrackingStation'))
if (-not $destination.StartsWith([IO.Path]::GetFullPath($gameData).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Plugin target escapes GameData.' }
foreach ($candidate in @($gameData,$destination,(Join-Path $destination 'Plugins'))) {
    if (Test-Path -LiteralPath $candidate) {
        if ((Get-Item -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing redirected install directory: $candidate" }
    }
}
function Assert-GameClosed {
    if (@(Get-Process -Name 'KSP','KSP_x64' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'KSP must be closed before plugin installation.' }
}
Assert-GameClosed
$target = Join-Path $destination 'Plugins\Expanse.TrackingStation.dll'
$readmeTarget = Join-Path $destination 'README.txt'
foreach ($metadataPath in @($readmeTarget,(Join-Path $destination 'INSTALLATION.json'))) {
    if ((Test-Path -LiteralPath $metadataPath) -and ((Get-Item -LiteralPath $metadataPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing redirected plugin metadata: $metadataPath" }
}
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected plugin file.' }
    $priorAssembly = [Reflection.AssemblyName]::GetAssemblyName($target)
    if ($priorAssembly.Name -ne 'Expanse.TrackingStation') { throw 'Existing target DLL has a conflicting assembly identity; installation refused.' }
}
if ($PSCmdlet.ShouldProcess($target,'Install standalone Tracking Station DLL with backup')) {
    Assert-GameClosed
    if (Test-Path -LiteralPath $target) {
        $backupDir = Join-Path $destination ('Backups\' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff'))
        if (Test-Path -LiteralPath (Join-Path $destination 'Backups')) {
            if ((Get-Item -LiteralPath (Join-Path $destination 'Backups')).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing redirected backup directory.' }
        }
        New-Item -ItemType Directory -Path $backupDir | Out-Null
        # KSP recursively loads DLLs below GameData; backups must not retain .dll extension.
        Copy-Item -LiteralPath $target -Destination (Join-Path $backupDir 'Expanse.TrackingStation.dll.bak')
        [ordered]@{ backedUpAtUtc=(Get-Date).ToUniversalTime().ToString('o'); version=$priorAssembly.Version.ToString(); sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; path=$target } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDir 'PREVIOUS-INSTALLATION.json') -Encoding UTF8
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Assert-GameClosed
    Copy-Item -LiteralPath $source -Destination $target -Force
    $readmeSource = Join-Path $package 'GameData\ExpanseTrackingStation\README.txt'
    if (Test-Path -LiteralPath $readmeSource -PathType Leaf) { Copy-Item -LiteralPath $readmeSource -Destination $readmeTarget -Force }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $ExpectedPluginSha256) { throw 'Installed plugin hash mismatch; backup retained.' }
    [ordered]@{ installedAtUtc=(Get-Date).ToUniversalTime().ToString('o'); version=$sourceAssembly.Version.ToString(); sha256=$ExpectedPluginSha256; path=$target } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'INSTALLATION.json') -Encoding UTF8
    Write-Output "Installed $target"
}
