$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$dotnet='C:\Program Files\dotnet\dotnet.exe'
$paths=@((Get-Content (Join-Path $PSScriptRoot 'clock256-source-manifest.json') -Raw|ConvertFrom-Json).PSObject.Properties.Name)
$paths+=@((Get-Content (Join-Path $PSScriptRoot 'host-colony-primary-integration.json') -Raw|ConvertFrom-Json).changedFiles)
$paths+='dev/Expanse.ProductionTelemetry.Tests/ProductionTelemetryModeTests.cs'
$hashes=@{};foreach($relative in ($paths|ForEach-Object {$_.Replace('\','/')}|Sort-Object -Unique)){$hashes[$relative]=(Get-FileHash -LiteralPath (Join-Path $primary $relative)).Hash}
$hashes|ConvertTo-Json|Set-Content (Join-Path $PSScriptRoot 'native-packed-build-source-hashes.json')
foreach($project in @('Expanse.WorldBridge','Expanse.BrpColony','Expanse.TrackingStation','Expanse.Clock.Host','Expanse.Clock.Manager')){
  $log=Join-Path $PSScriptRoot ('native-packed-primary-'+$project+'.log')
  & $dotnet build (Join-Path $primary ('src\'+$project+'\'+$project+'.csproj')) -c Release -t:Rebuild *> $log
  if($LASTEXITCODE -ne 0){Get-Content $log -Tail 30;throw ('Build failed: '+$project)}
  Write-Output ($project+': rebuilt successfully')
}
foreach($entry in @(@('tests\Expanse.Clock.Tests.csproj','clock'),@('dev\Expanse.ProductionTelemetry.Tests\Expanse.ProductionTelemetry.Tests.csproj','production'))){
  $log=Join-Path $PSScriptRoot ('native-packed-primary-'+$entry[1]+'-tests.log')
  & $dotnet test (Join-Path $primary $entry[0]) -c Release --logger ('trx;LogFileName=native-packed-'+$entry[1]+'.trx') *> $log
  if($LASTEXITCODE -ne 0){Get-Content $log -Tail 30;throw ('Tests failed: '+$entry[1])}
  Get-Content $log -Tail 2
}
foreach($relative in $hashes.Keys){if((Get-FileHash -LiteralPath (Join-Path $primary $relative)).Hash -ne $hashes[$relative]){throw ('Source changed during build: '+$relative)}}
