[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$platformRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tests = Join-Path $platformRoot 'tests\Expanse.Clock.Tests.csproj'
if (-not (Test-Path -LiteralPath $tests -PathType Leaf)) { throw "Clock test project not found: $tests" }
dotnet test $tests --configuration $Configuration --logger 'console;verbosity=normal'
if ($LASTEXITCODE -ne 0) { throw 'Clock test suite failed.' }



