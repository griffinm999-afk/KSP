param([string]$KspRoot='C:\Kerbal Space Program')
$ErrorActionPreference='Stop'
$sourceRoot=Split-Path $PSScriptRoot -Parent
dotnet run --project (Join-Path $sourceRoot 'tests\Foundations.Tests.csproj') -c Release
if($LASTEXITCODE -ne 0){throw 'Core tests failed.'}
dotnet build (Join-Path $sourceRoot 'src\ExpanseFoundations.csproj') -c Release --nologo "-p:KSPDIR=$KspRoot"
if($LASTEXITCODE -ne 0){throw 'Plugin build failed.'}
$plugins=Join-Path $sourceRoot 'GameData\ExpanseFoundations\Plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'src\bin\Release\net472\ExpanseFoundations.dll') -Destination $plugins -Force
Get-FileHash -LiteralPath (Join-Path $plugins 'ExpanseFoundations.dll') -Algorithm SHA256
