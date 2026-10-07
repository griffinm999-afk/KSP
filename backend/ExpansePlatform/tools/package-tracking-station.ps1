[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$platform = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $platform 'src\Expanse.TrackingStation\Expanse.TrackingStation.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Project version is missing.' }
& dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tracking Station build failed.' }
$dll = Join-Path $platform "src\Expanse.TrackingStation\bin\$Configuration\net472\Expanse.TrackingStation.dll"
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$stage = Join-Path $platform "artifacts\ExpanseTrackingStation-$version-$stamp"
if (Test-Path -LiteralPath $stage) { throw "Stage already exists: $stage" }
$plugins = Join-Path $stage 'GameData\ExpanseTrackingStation\Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $plugins
foreach ($name in @('TRACKING-STATION-USAGE.md','TRACKING-STATION-WORK-EVIDENCE.md')) {
    Copy-Item -LiteralPath (Join-Path $platform "docs\$name") -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-tracking-station.ps1') -Destination $stage
@"
Expanse Tracking Station $version — standalone KSP 1.12.5 plugin

Read TRACKING-STATION-USAGE.md and TRACKING-STATION-WORK-EVIDENCE.md.
GameData contains only this plugin. No game DLLs, other Expanse components,
save files, fixtures, or external applications are included.

Automatic default Tracking Station replacement; no toolbar click required.
Full-height sidebar, branded header/footer and selection card frame the native
live map. Stock interface restores native presentation and camera viewports.
Filters affect the custom list only; native map markers retain stock visibility.
Offline tests and a build against KSP DLLs do not verify in-game behavior.
Close KSP before installation. The guarded installer requires this extracted
package path and a matching DLL hash; it backs up the existing plugin DLL.
It writes only GameData/ExpanseTrackingStation and does not launch the game.
"@ | Set-Content -LiteralPath (Join-Path $stage 'README.txt') -Encoding UTF8
@"
Expanse Tracking Station $version
Automatic default Tracking Station replacement; no toolbar click required.
Search names and body ancestry; type/situation choices OR within each group,
AND across groups and crew. Filters apply to this list only; stock map unchanged.
Select a row to inspect. Focus/Fly and native Recover/Track/End actions are
explicit. Stock interface restores native panels and camera viewports; use the
Expanse toolbar button to reopen. Game UI scale plus plugin scale are supported.
Preferences are per save-folder in PluginData, outside KSP save files.
Runtime behavior requires supervised in-game review. See archive root docs.
"@ | Set-Content -LiteralPath (Join-Path $stage 'GameData\ExpanseTrackingStation\README.txt') -Encoding UTF8
$manifest = Join-Path $stage 'SHA256SUMS.txt'
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($stage.Length).TrimStart('\','/')
} | Set-Content -LiteralPath $manifest -Encoding UTF8
$zip = "$stage.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
[pscustomobject]@{ PackageRoot=$stage; Archive=$zip; ArchiveSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash; PluginSha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash } | ConvertTo-Json
