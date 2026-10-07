[CmdletBinding()]
param([switch]$Launch, [string]$SourceDevelopmentSave, [switch]$Profile)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devRoot = 'C:\Users\griff\Documents\Codex\KSP-Colony-Demo'
$liveSave = 'C:\Kerbal Space Program\saves\The Expanse\persistent.sfs'
$exePath = Join-Path $devRoot 'KSP_x64.exe'
function Assert-PrivatePath([string]$path, [switch]$ReadOnly) {
  $resolved = [IO.Path]::GetFullPath($path)
  if ($resolved -ne $devRoot -and -not $resolved.StartsWith($devRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw "Path leaves development install: $resolved" }
  for ($part = $resolved; $part; $part = [IO.Path]::GetDirectoryName($part)) {
    if (Test-Path -LiteralPath $part) {
      if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path: $part" }
    }
  }
  if (-not $ReadOnly -and (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    $links = @(& fsutil hardlink list $resolved)
    if ($LASTEXITCODE -ne 0 -or $links.Count -ne 1) { throw "Could not establish a private mutable file: $resolved" }
  }
}
# The executable is only read/launched. Mutated saves, configs and DLLs below
# require the stronger single-hardlink check as well as the path boundary.
Assert-PrivatePath $exePath -ReadOnly
if ($SourceDevelopmentSave) {
  $candidateSource = [IO.Path]::GetFullPath($SourceDevelopmentSave)
  Assert-PrivatePath $candidateSource
  $candidateFolder = Split-Path -Leaf (Split-Path -Parent $candidateSource)
  if (-not $candidateSource.StartsWith((Join-Path $devRoot 'saves') + '\',[StringComparison]::OrdinalIgnoreCase) -or
      $candidateFolder -notmatch '^ColonyBuild-[0-9]{8}-[0-9]{6}$' -or [IO.Path]::GetExtension($candidateSource) -ne '.sfs') {
    throw 'Reload fixture must be an actual save from a prior isolated ColonyBuild directory.'
  }
  $liveSave = $candidateSource
}
$games = @(Get-CimInstance Win32_Process -Filter "Name='KSP_x64.exe'")
if (@($games | Where-Object { -not $_.ExecutablePath -or $_.ExecutablePath -ieq $exePath }).Count) { throw 'Development KSP is running or a game process cannot be identified.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$token = [Guid]::NewGuid().ToString('N')
$folder = 'ColonyBuild-' + $stamp
$saveTarget = Join-Path $devRoot "saves\$folder\persistent.sfs"
Assert-PrivatePath $saveTarget
if (Test-Path -LiteralPath (Split-Path $saveTarget)) { throw 'Refusing to reuse a disposable save directory.' }
$beforeHash = (Get-FileHash -LiteralPath $liveSave -Algorithm SHA256).Hash
New-Item -ItemType Directory -Path (Split-Path $saveTarget) | Out-Null
Copy-Item -LiteralPath $liveSave -Destination $saveTarget
$afterHash = (Get-FileHash -LiteralPath $liveSave -Algorithm SHA256).Hash
if ($afterHash -ne $beforeHash -or (Get-FileHash -LiteralPath $saveTarget).Hash -ne $beforeHash) { throw 'Source changed while copied; do not launch this fixture.' }
$backup = Join-Path $sourceRoot "artifacts\colony-dev-backups\$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$files = @{
  'src\Expanse.WorldBridge\bin\Release\net472\Expanse.WorldBridge.dll' = 'GameData\ExpanseWorldBridge\Plugins\Expanse.WorldBridge.dll'
  'src\Expanse.Domain\bin\Release\net472\Expanse.Domain.dll' = 'GameData\ExpanseWorldBridge\Plugins\Expanse.Domain.dll'
  'package\GameData\ExpanseWorldBridge\ColonyPlacement.cfg' = 'GameData\ExpanseWorldBridge\ColonyPlacement.cfg'
  'package\GameData\ExpanseWorldBridge\ColonyEconomy.cfg' = 'GameData\ExpanseWorldBridge\ColonyEconomy.cfg'
  'package\GameData\ExpanseWorldBridge\ColonyPassengers.cfg' = 'GameData\ExpanseWorldBridge\ColonyPassengers.cfg'
  'package\GameData\ExpanseWorldBridge\ColonyPlanning.cfg' = 'GameData\ExpanseWorldBridge\ColonyPlanning.cfg'
  'dev\Expanse.Colony.KspHarness\bin\Release\net472\Expanse.Colony.KspHarness.dll' = 'GameData\ExpanseColonyDev\Plugins\Expanse.Colony.KspHarness.dll'
  'dev\Expanse.ColonyPlacement.KspHarness\bin\Release\net472\Expanse.ColonyPlacement.KspHarness.dll' = 'GameData\ExpanseColonyDev\Plugins\Expanse.ColonyPlacement.KspHarness.dll'
}
$templateRoot = Join-Path $sourceRoot 'package\GameData\ExpanseWorldBridge\Templates'
foreach ($template in Get-ChildItem -LiteralPath $templateRoot -File) {
  if ($template.Extension -notin '.json', '.craft') { throw "Unexpected template artifact: $($template.Name)" }
  $files.Add('package\GameData\ExpanseWorldBridge\Templates\' + $template.Name, 'GameData\ExpanseWorldBridge\Templates\' + $template.Name)
}
$hashes = @()
foreach ($item in $files.GetEnumerator()) {
  $source = Join-Path $sourceRoot $item.Key; $target = Join-Path $devRoot $item.Value
  if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing built artifact: $source" }
  Assert-PrivatePath $target
  New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
  if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination (Join-Path $backup ([IO.Path]::GetFileName($target))) }
  Copy-Item -LiteralPath $source -Destination $target
  if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw "Install hash mismatch: $target" }
  $hashes += @{ Path=$item.Value; Sha256=(Get-FileHash -LiteralPath $target).Hash }
}
$requestPath = Join-Path $devRoot 'colony-load.cfg'; Assert-PrivatePath $requestPath
if (Test-Path -LiteralPath $requestPath) { throw 'An unconsumed development load request already exists.' }
@("authorization = verified-isolated-development-only", "saveFolder = $folder", "sha256 = $beforeHash") | Set-Content -LiteralPath $requestPath
$prefix = [Environment]::UserName
$arguments = @('-muteaudio', '-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900',
  "-expanseClockPublisherPipe=ExpanseFoundations.Clock.Publisher.dev.$prefix.$token",
  "-expanseEffectsPipe=ExpanseFoundations.Effects.dev.$prefix.$token",
  "-expanseWolfPipe=ExpanseFoundations.WOLF.Admin.dev.$prefix.$token",
  "-expanseColonyPipe=ExpanseFoundations.Colonies.dev.$prefix.$token")
if ($Profile) { $arguments += '-expanseColonyProfile' }
$manifest = @{ Token=$token; SaveFolder=$folder; SourcePath=$liveSave; SourceKind=$(if ($SourceDevelopmentSave) {'isolated-native-save'} else {'fresh-live-copy'}); SourceSha256=$beforeHash; TargetSave=$saveTarget; Executable=$exePath; Arguments=$arguments; Artifacts=$hashes; CreatedUtc=[DateTime]::UtcNow.ToString('o'); Launched=[bool]$Launch }
$report = Join-Path $sourceRoot 'run\colony-development.json'
$authorizationPath = Join-Path $devRoot 'colony-development-authorization.cfg'
Assert-PrivatePath $authorizationPath
@("root = $devRoot", "saveFolder = $folder", "colonyPipe = ExpanseFoundations.Colonies.dev.$prefix.$token") | Set-Content -LiteralPath $authorizationPath
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report
if ($Launch) {
  $process = Start-Process -FilePath $exePath -ArgumentList $arguments -WorkingDirectory $devRoot -WindowStyle Hidden -PassThru
  $manifest.ProcessId=$process.Id; $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report
}
$manifest | ConvertTo-Json -Depth 5
