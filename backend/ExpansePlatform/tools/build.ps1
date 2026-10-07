[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$managed = 'C:\Kerbal Space Program\KSP_x64_Data\Managed'
$projects = @(
  'src\Expanse.Domain\Expanse.Domain.csproj',
  'src\Expanse.Clock.Core\Expanse.Clock.Core.csproj',
  'src\Expanse.Clock.Host\Expanse.Clock.Host.csproj',
  'src\Expanse.Clock.Manager\Expanse.Clock.Manager.csproj',
  'src\Expanse.Launcher\Expanse.Launcher.csproj',
  'src\Expanse.WorldBridge\Expanse.WorldBridge.csproj'
)
foreach ($project in $projects) {
  $path = Join-Path $root $project
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing project: $path" }
  if ($project -like '*WorldBridge*' -and -not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) { throw "KSP references not found: $managed" }
  dotnet clean $path --configuration $Configuration
  if ($LASTEXITCODE -ne 0) { throw "Clean failed: $project" }
}
foreach ($project in $projects) {
  $path = Join-Path $root $project
  dotnet build $path --configuration $Configuration -p:KspManagedDir=$managed
  if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
