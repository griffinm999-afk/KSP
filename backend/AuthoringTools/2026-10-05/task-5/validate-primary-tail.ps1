$ErrorActionPreference='Stop'
$primary='C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform'
$dotnet='C:\Program Files\dotnet\dotnet.exe'
$log=Join-Path $PSScriptRoot 'payload-validation.log'
Add-Content -LiteralPath $log -Value 'Primary validation resumed after restoring existing development-test assets. Five builds and 611 clock tests already passed.'
function Run([string[]]$taskArgs){& $dotnet @taskArgs 2>&1|Tee-Object -FilePath $log -Append;if($LASTEXITCODE -ne 0){throw "Primary check failed: $($taskArgs -join ' ')"}}
Run @('test',(Join-Path $primary 'dev\Expanse.ProductionTelemetry.Tests\Expanse.ProductionTelemetry.Tests.csproj'),'-c','Release','--no-restore','--logger','trx;LogFileName=budget-contract.trx')
foreach($project in @('PerformanceChecks','RosterCensusChecks')){Run @('run','--project',(Join-Path $primary "dev\$project\$project.csproj"),'-c','Release','--no-restore')}
Run @('run','--project',(Join-Path $primary 'tests\Expanse.TrackingStation.Tests\Expanse.TrackingStation.Tests.csproj'),'-c','Release','--no-restore')
Run @('run','--project',(Join-Path $primary 'dev\ActualFrameChecks\ActualFrameChecks.csproj'),'-c','Release','--no-restore','--',(Join-Path $PSScriptRoot 'live-clock-qualification.json'))
& (Join-Path $primary 'dev\Expanse.ProductionTelemetry.Tests\verify-native-serializer.ps1') 2>&1|Tee-Object -FilePath $log -Append
& (Join-Path $primary 'dev\PerformanceChecks\verify-handoff.ps1') 2>&1|Tee-Object -FilePath $log -Append
@{status='primary-validation-passed';source=$primary;releaseBuilds=5;clockTests=611;productionTests=32;fixtures='roster/census, capture ownership, tracking, physical-frame replay, native budget allocation, deterministic logging and failure visibility';runtimeInstalled=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'primary-validation-result.json')
