param([string]$SourceRoot)
$ErrorActionPreference='Stop'
$stage=if($SourceRoot){[IO.Path]::GetFullPath($SourceRoot)}else{Join-Path $PSScriptRoot 'performance-work'}
$dotnet='C:\Program Files\dotnet\dotnet.exe'
$log=Join-Path $PSScriptRoot 'payload-validation.log'
Set-Content -LiteralPath $log -Value 'Staged payload and diagnostic candidate. No live installation or game calls.'
function Run-Dotnet([string[]]$taskArgs){ & $dotnet @taskArgs 2>&1 | Tee-Object -FilePath $log -Append; if($LASTEXITCODE -ne 0){throw "dotnet check failed: $($taskArgs -join ' ')"} }
foreach($project in @('Expanse.WorldBridge','Expanse.Clock.Host','Expanse.Clock.Manager','Expanse.BrpColony','Expanse.TrackingStation')){
  Run-Dotnet @('build',(Join-Path $stage "src\$project\$project.csproj"),'-c','Release','--no-restore')
}
Run-Dotnet @('test',(Join-Path $stage 'tests\Expanse.Clock.Tests.csproj'),'-c','Release','--no-restore','--logger','trx;LogFileName=payload-clock.trx')
Run-Dotnet @('test',(Join-Path $stage 'dev\Expanse.ProductionTelemetry.Tests\Expanse.ProductionTelemetry.Tests.csproj'),'-c','Release','--no-restore','--logger','trx;LogFileName=payload-production.trx')
foreach($check in @('PerformanceChecks','RosterCensusChecks')){ Run-Dotnet @('run','--project',(Join-Path $stage "dev\$check\$check.csproj"),'-c','Release','--no-restore') }
Run-Dotnet @('run','--project',(Join-Path $stage 'tests\Expanse.TrackingStation.Tests\Expanse.TrackingStation.Tests.csproj'),'-c','Release','--no-restore')
Run-Dotnet @('run','--project',(Join-Path $stage 'dev\ActualFrameChecks\ActualFrameChecks.csproj'),'-c','Release','--',(Join-Path $PSScriptRoot 'live-clock-qualification.json'))
& (Join-Path $stage 'dev\Expanse.ProductionTelemetry.Tests\verify-native-serializer.ps1') 2>&1 | Tee-Object -FilePath $log -Append
& (Join-Path $stage 'dev\PerformanceChecks\verify-handoff.ps1') 2>&1 | Tee-Object -FilePath $log -Append
